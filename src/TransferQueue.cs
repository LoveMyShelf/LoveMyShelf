using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using Microsoft.Win32.SafeHandles;

namespace LoveMyShelf {
    internal enum TransferState { Waiting, Running, Paused, Done, Cancelled }

    // One file of a job: where it is, which folder it goes to, and what was answered if a same-name file was there.
    internal sealed class TransferItem {
        public string From,TargetSource,TargetAlbum;
        public long Size;
        // The file's own time when it was queued. A job brought back from the last run sends a file only if it
        // still has this size and time (it may have been replaced by another file of the same name meanwhile).
        public DateTime Stamp;
        public bool Conflict;
        public ConflictChoice? Decided;
        // The same-name file the answer was about: its size and time, so a different file that turns up there later isn't replaced.
        public long SeenLength=-1;
        public DateTime SeenStamp;
        // Undo only: the name the file had before the move it is undoing gave it another one.
        public string RestoreName;
        // Whole: From is a folder that moves in one step within its drive. MakeFolder: the folder the file goes
        // into (TargetSource) is made first if it is not there yet (a file inside a folder that is being sent).
        public bool Whole,MakeFolder;
        // What became of this file, for the queue window's file list. 0: not dealt with yet, 1: moved, 2: copied,
        // 3: skipped, 4: already there, 5: not done, 6: copied whole but the original is still in its old place.
        public volatile int Outcome;
        // Where it is now (Outcome 1, 2, 6), and why it was not done or what to look at (Outcome 3, 5, 6).
        public volatile string Arrived,Why;
    }

    // A file that was being copied when its job was paused: enough to go on from the same byte.
    internal sealed class PartialCopy {
        public string Key,Landing;
        public long Offset,Length;
        public DateTime Stamp;
        // Which file the copied part came from (NTFS only; 0 where that isn't known or in a queue saved before this).
        public uint Volume;public ulong Id;
    }

    // Thrown out of the copy when the job it belongs to was paused. The half-copied file stays for later.
    internal sealed class TransferPausedException : Exception {}

    // "Move these files to that folder", waiting in the queue or being worked on.
    internal sealed class TransferJob {
        public readonly List<TransferItem> Items=new List<TransferItem>();
        // Folders that are sent file by file: where each one is and where it goes. EmptyDirs: folders with nothing
        // in them, made at the new place when the job is done. Rejected: what couldn't be taken into the job at all.
        public readonly List<Tuple<string,string>> Roots=new List<Tuple<string,string>>();
        public readonly List<Tuple<string,string>> EmptyDirs=new List<Tuple<string,string>>(); // old place, new place
        // What the job really did, for tidying up and for an undo: the folders it took files out of, and the
        // folders it made. OnlyThese (an undo): the only folders that may be taken away again, the ones the move made.
        public readonly HashSet<string> Touched=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Created=new List<string>();
        public HashSet<string> OnlyThese;
        public readonly List<string> Rejected=new List<string>();
        public int FolderCount;
        public int FoldersFinished; // 0 until the folders have been tidied (once)
        public volatile bool Copy;
        public bool Undo;
        // Kept on disk while the job is unfinished, so it is still in the queue the next time the program starts.
        public string Id=Guid.NewGuid().ToString("N");
        public bool Saved;
        // Loaded: read back from disk at this start. InFlight: the file that was being sent when the last run
        // stopped without having written down how it ended (-1: none). Volumes: the drives the job uses, by
        // their own numbers, so a job never goes on with another drive that got the same letter.
        public bool Loaded;
        public int InFlight=-1;
        public Dictionary<string,uint> Volumes;
        // The folders of a cancelled job are still being tidied: its files on disk stay until that is done.
        public volatile bool Tidying;
        // Verify: after each copy, the copy is read back from the drive and compared with the original.
        public bool Verify;
        public volatile bool Verifying;
        public long VerifyDone;
        public string FromLabel,ToLabel,FolderName;
        public GalleryStore Store;
        public Action<string> Recycle;
        public Action<TransferJob> Finished;

        public volatile TransferState State=TransferState.Waiting;
        public volatile bool PauseRequested,CancelRequested,Started,Reported;
        public volatile int Next;
        public long TotalBytes,DoneBase,FileDone,FileBytes;
        public volatile string CurrentName,Note;
        public PartialCopy Partial;
        public readonly MoveReport Report=new MoveReport();
        // What the notice at the end has already told about (a paused job reports once, then again when it ends).
        public int ToldMoved,ToldCopied;

        public long DoneBytes {get {return Math.Min(TotalBytes,Interlocked.Read(ref DoneBase)+Interlocked.Read(ref FileDone));}}
        public bool Active {get {return State==TransferState.Waiting || State==TransferState.Running || State==TransferState.Paused;}}
        public List<string> Failures() {lock(Report)return Report.Failed.ToList();}
        public int FailedCount {get {lock(Report)return Report.Failed.Count;}}

        // For the speed shown in the queue window.
        public long SampleBytes;public DateTime SampleTime;public double Speed;
    }

    // Copies one file in pieces, so the queue can show how far it is, pause it and cancel it.
    // A paused copy lets go of both files completely (the drive can be removed safely) and goes on later from the same byte.
    internal sealed class TransferCopier {
        static readonly byte[] buffer=new byte[4<<20]; // one job runs at a time
        readonly TransferJob job;
        internal TransferCopier(TransferJob job) {this.job=job;}
        // Which file of the job is being sent, for the notes below.
        internal int Index;
        // The original got another name before it is sent ("keep both"), or got its old name back because the
        // sending failed. Written down at once, so a run that is cut short still knows what the file is called.
        internal void Renamed(string path) {TransferQueue.Log(job,"R\t"+Index+"\t"+path);}
        internal void NameBack(string path) {TransferQueue.Log(job,"U\t"+Index+"\t"+path);}

        // What the file that was last copied looked like; the original is deleted only if it still looks like this.
        internal long CopiedLength;internal DateTime CopiedStamp;
        // And which file it was (NTFS only; 0 where that isn't known).
        internal uint CopiedVolume;internal ulong CopiedId;
        // A paused copy goes on only if the last part already written is still what the original holds there
        // (the original may have been saved again with the same size and time while the job was paused).
        static bool SameTail(string source,string landing,long offset) {
            int count=(int)Math.Min(offset,1<<20);
            if(count==0)return true;
            var a=new byte[count];var b=new byte[count];
            using(var x=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read))
            using(var y=new FileStream(landing,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                x.Position=offset-count;y.Position=offset-count;
                int n=0,m=0,got;
                while(n<count && (got=x.Read(a,n,count-n))>0)n+=got;
                while(m<count && (got=y.Read(b,m,count-m))>0)m+=got;
                if(n!=count || m!=count)return false;
            }
            for(int i=0;i<count;i++)if(a[i]!=b[i])return false;
            return true;
        }
        // ---- Comparing a finished copy with its original (the "check the copy" option) ----
        // The copy is read back past Windows' own memory of what was just written (no buffering), so what is
        // compared is what the drive really holds. If the drive can't be read that way, it is read the usual way.
        [DllImport("kernel32.dll",SetLastError=true,CharSet=CharSet.Unicode)]
        static extern SafeFileHandle CreateFile(string name,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
        [DllImport("kernel32.dll",SetLastError=true)]
        static extern bool ReadFile(SafeFileHandle file,IntPtr data,uint count,out uint read,IntPtr overlapped);
        [DllImport("kernel32.dll")] static extern IntPtr VirtualAlloc(IntPtr address,UIntPtr size,uint type,uint protect);
        [DllImport("kernel32.dll")] static extern bool VirtualFree(IntPtr address,UIntPtr size,uint type);
        static readonly byte[] second=new byte[4<<20];
        void Compare(string source,string landing,long length) {
            Interlocked.Exchange(ref job.VerifyDone,0);job.Verifying=true;
            // First past Windows' memory; a drive that can't be read that way is read the usual way, from the start.
            bool? same=CompareWith(source,landing,length,true);
            if(same==null)same=CompareWith(source,landing,length,false);
            if(same!=true)throw new IOException(L.P("The copy did not come out the same as the original, so it was thrown away. The original is as it was. The drive or its cable may have a problem.",
                "복사본이 원본과 다르게 나와서 버렸어요. 원본은 그대로예요. 드라이브나 케이블에 문제가 있을 수 있어요."));
        }
        // null: the copy couldn't be read without buffering on this drive (nothing is known yet).
        bool? CompareWith(string source,string landing,long length,bool unbuffered) {
            IntPtr memory=IntPtr.Zero;SafeFileHandle raw=null;FileStream plain=null;
            try {
                if(unbuffered) {
                    // GENERIC_READ, share read, OPEN_EXISTING, NO_BUFFERING | SEQUENTIAL_SCAN; memory from VirtualAlloc starts on a page.
                    raw=CreateFile(landing,0x80000000,1,IntPtr.Zero,3,0x20000000|0x08000000,IntPtr.Zero);
                    if(raw.IsInvalid)return null;
                    memory=VirtualAlloc(IntPtr.Zero,(UIntPtr)(uint)second.Length,0x3000,0x04);
                    if(memory==IntPtr.Zero)return null;
                } else plain=new FileStream(landing,FileMode.Open,FileAccess.Read,FileShare.Read,1<<16,FileOptions.SequentialScan);
                using(var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read,1<<16,FileOptions.SequentialScan)) {
                    long done=0;
                    Interlocked.Exchange(ref job.VerifyDone,0);
                    while(done<length) {
                        if(job.CancelRequested)throw new OperationCanceledException();
                        if(job.PauseRequested)throw new TransferPausedException();
                        int want=(int)Math.Min(buffer.Length,length-done),n=0,m=0,got;
                        while(n<want && (got=input.Read(buffer,n,want-n))>0)n+=got;
                        if(raw!=null) {
                            uint read;
                            // A whole 4 MB is asked for (a multiple of any sector size); only the end of the file gives less.
                            // A drive that refuses, or hands back less in the middle, is read the usual way instead.
                            if(!ReadFile(raw,memory,(uint)second.Length,out read,IntPtr.Zero) || read<(uint)want)return null;
                            m=want;
                            Marshal.Copy(memory,second,0,m);
                        } else while(m<want && (got=plain.Read(second,m,want-m))>0)m+=got;
                        if(n!=want || m!=want)return false;
                        for(int i=0;i<want;i++)if(buffer[i]!=second[i])return false;
                        done+=want;Interlocked.Exchange(ref job.VerifyDone,done);
                    }
                }
                return true;
            } finally {
                if(raw!=null)raw.Dispose();
                if(plain!=null)plain.Dispose();
                if(memory!=IntPtr.Zero)VirtualFree(memory,UIntPtr.Zero,0x8000);
            }
        }
        // A copy that was paused goes on only into the same file: another one put there meanwhile with the same size,
        // time and ending would leave a copy that is half one file and half the other.
        static bool SameFileAs(PartialCopy partial,string source) {
            if(partial.Id==0)return true;
            uint volume;ulong id;long birth,size;
            return OriginalLocator.Identity(source,out volume,out id,out birth,out size) && volume==partial.Volume && id==partial.Id;
        }
        // Returns the hidden temporary file in targetFolder that holds the whole copy, written through to the disk.
        internal string Copy(string source,string targetFolder,string key) {
            var info=new FileInfo(source);
            long length=info.Length;DateTime stamp=info.LastWriteTimeUtc;
            long offset=0;string landing=null;bool reuse=false;
            CopiedLength=length;CopiedStamp=stamp;CopiedVolume=0;CopiedId=0;
            var partial=Interlocked.Exchange(ref job.Partial,null);
            if(partial!=null) {
                bool usable=false;
                try {
                    usable=partial.Key==key && partial.Length==length && partial.Stamp==stamp && SameFileAs(partial,source)
                        && Mover.SameFolder(Path.GetDirectoryName(partial.Landing),targetFolder)
                        && File.Exists(partial.Landing) && new FileInfo(partial.Landing).Length==partial.Offset
                        && SameTail(source,partial.Landing,partial.Offset);
                } catch(Exception quiet) {ErrorLog.Quiet(quiet);}
                if(usable){landing=partial.Landing;offset=partial.Offset;reuse=true;}
                else TransferQueue.DeleteTemp(partial.Landing);
            }
            if(landing==null)landing=Mover.Landing(targetFolder,Path.GetFileName(source));
            job.CurrentName=Path.GetFileName(key);
            Interlocked.Exchange(ref job.FileBytes,length);Interlocked.Exchange(ref job.FileDone,offset);
            long free=-1;
            try {free=new DriveInfo(Path.GetPathRoot(Path.GetFullPath(targetFolder))).AvailableFreeSpace;} catch(Exception quiet) {ErrorLog.Quiet(quiet);}
            if(free>=0 && free<length-offset+(16<<20))
                throw new IOException(L.PF("There isn't enough room on the drive it goes to. {0} more is needed.","보낼 드라이브에 남은 공간이 모자라요. {0}가 더 필요해요.",TransferQueue.Bytes(length-offset+(16<<20)-free)));
            TransferQueue.RememberTemp(landing);
            bool keep=false;
            try {
                using(var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read,1<<16,FileOptions.SequentialScan))
                using(var output=new FileStream(landing,reuse?FileMode.Open:FileMode.CreateNew,FileAccess.Write,FileShare.None,1<<16,FileOptions.SequentialScan)) {
                    if(!reuse) {try {File.SetAttributes(landing,File.GetAttributes(landing)|FileAttributes.Hidden);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}}
                    // Asked of the file as it is open for reading here: the very one whose bytes are copied.
                    OriginalLocator.IdOf(input,source,out CopiedVolume,out CopiedId);
                    input.Position=offset;output.Position=offset;
                    while(true) {
                        if(job.CancelRequested)throw new OperationCanceledException();
                        if(job.PauseRequested) {
                            output.Flush(true);
                            job.Partial=new PartialCopy {Key=key,Landing=landing,Offset=output.Position,Length=length,Stamp=stamp,Volume=CopiedVolume,Id=CopiedId};
                            keep=true;
                            throw new TransferPausedException();
                        }
                        int n=input.Read(buffer,0,buffer.Length);
                        if(n==0)break;
                        output.Write(buffer,0,n);
                        Interlocked.Add(ref job.FileDone,n);
                    }
                    // Really on the disk, not only in Windows' memory, before anything is done with the original.
                    output.Flush(true);
                }
                // Writing through to a slow drive can take seconds; a cancel that came meanwhile still counts.
                if(job.CancelRequested)throw new OperationCanceledException();
                var after=new FileInfo(source);
                if(new FileInfo(landing).Length!=length || after.Length!=length || after.LastWriteTimeUtc!=stamp)
                    throw new IOException(L.P("The file changed while it was being copied, so the copy was thrown away. The original is as it was.","복사하는 동안 파일이 바뀌어서 복사본을 버렸어요. 원본은 그대로예요."));
                if(job.Verify) {
                    try {Compare(source,landing,length);}
                    catch(TransferPausedException) {
                        // The copy itself is whole; when the job goes on, only the comparing is done again.
                        job.Partial=new PartialCopy {Key=key,Landing=landing,Offset=length,Length=length,Stamp=stamp,Volume=CopiedVolume,Id=CopiedId};
                        keep=true;throw;
                    }
                    finally {job.Verifying=false;}
                    // The comparing can take long; a cancel that came meanwhile still counts.
                    if(job.CancelRequested)throw new OperationCanceledException();
                }
                try {File.SetCreationTimeUtc(landing,info.CreationTimeUtc);File.SetLastWriteTimeUtc(landing,stamp);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}
                keep=true;
                return landing;
            } finally {
                if(!keep)TransferQueue.DeleteTemp(landing);
            }
        }
    }

    // Every move and copy between folders goes through here, one job at a time: two jobs on the same hard drive
    // at once would only make both slow. Jobs can be paused, cancelled and put in another order while they wait.
    internal static class TransferQueue {
        static readonly object gate=new object();
        static readonly List<TransferJob> jobs=new List<TransferJob>();
        static readonly AutoResetEvent wake=new AutoResetEvent(false);
        static Thread worker;
        static volatile bool working;
        static DispatcherTimer ticker;
        // Raised on the window thread: twice a second while something is active, and whenever the list changes.
        internal static event Action Tick;
        // Raised on the window thread when files have moved or been copied, so folder windows read their folders again.
        internal static event Action FilesMoved;

        internal static List<TransferJob> Jobs {get {lock(gate)return jobs.ToList();}}
        internal static bool Busy {get {lock(gate)return finishing>0 || jobs.Any(j=>j.Active);}}
        // Folders of a cancelled job still being tidied on another thread: nothing else starts until that is done.
        static int finishing;
        // Set by the folder windows: true while something else is rewriting the records (drives being linked
        // again after one was plugged in). The queue starts no job meanwhile.
        internal static Func<bool> Hold;
        internal static bool Running {get {lock(gate)return finishing>0 || jobs.Any(j=>j.State==TransferState.Running);}}
        internal static bool Undoing {get {lock(gate)return jobs.Any(j=>j.Undo && j.Active);}}
        internal static bool Working {get {lock(gate)return jobs.Any(j=>j.State==TransferState.Waiting || j.State==TransferState.Running);}}

        internal static string Bytes(long bytes) {
            if(bytes>=1024L*1024*1024)return (bytes/1073741824.0).ToString("0.##")+" GB";
            if(bytes>=1024*1024)return (bytes/1048576.0).ToString("0.#")+" MB";
            return Math.Max(bytes>0?1:0,(bytes+1023)/1024).ToString("N0")+" KB";
        }

        static void OnUi(Action action) {
            var app=Application.Current;
            if(app==null)return;
            app.Dispatcher.BeginInvoke(action);
        }
        static void RaiseTick() {
            SaveOrder(); // every change of the list or of a job's state comes through here
            OnUi(()=>{
                if(ticker==null) {
                    ticker=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(500)};
                    ticker.Tick+=delegate {
                        if(!Busy)ticker.Stop();
                        var handler=Tick;if(handler!=null)handler();
                    };
                }
                if(Busy && !ticker.IsEnabled)ticker.Start();
                var now=Tick;if(now!=null)now();
            });
        }

        internal static void Add(TransferJob job) {
            if(closing)return;
            job.TotalBytes=job.Items.Sum(i=>i.Size);
            SaveJob(job);
            lock(gate) {
                jobs.Add(job);
                StartWorker();
            }
            wake.Set();RaiseTick();
        }
        static void StartWorker() {
            if(worker!=null)return;
            worker=new Thread(Work) {IsBackground=true,Name="Love My Shelf file queue"};
            worker.Start();
        }

        // ---------- What the buttons of the queue window do ----------
        internal static void Pause(TransferJob job) {
            lock(gate) {
                if(job.State==TransferState.Waiting){job.State=TransferState.Paused;job.Note=null;}
                else if(job.State==TransferState.Running)job.PauseRequested=true;
            }
            RaiseTick();
        }
        internal static void Resume(TransferJob job) {
            if(closing)return; // the program is on its way out: nothing starts again
            lock(gate) {
                if(job.State==TransferState.Paused){job.Note=null;job.PauseRequested=false;job.State=TransferState.Waiting;}
                else if(job.State==TransferState.Running)job.PauseRequested=false;
                StartWorker(); // a job brought back from the last run may be the first thing to do
            }
            wake.Set();RaiseTick();
        }
        internal static void Cancel(TransferJob job) {
            bool ended=false;
            lock(gate) {
                if(job.State==TransferState.Running)job.CancelRequested=true;
                else if(job.State==TransferState.Waiting || job.State==TransferState.Paused) {
                    job.CancelRequested=true;job.State=TransferState.Cancelled;ended=true;
                }
            }
            if(ended) {
                DropPartial(job);
                // A paused job that had already sent some folders: they are tidied before the notice.
                if(job.Started && job.Roots.Count>0) {
                    job.Tidying=true;
                    Interlocked.Increment(ref finishing);
                    ThreadPool.QueueUserWorkItem(delegate {
                        try {FinishFolders(job,false);}
                        finally {job.Tidying=false;Interlocked.Decrement(ref finishing);Report(job,true);wake.Set();RaiseTick();}
                    });
                } else Report(job,true);
            }
            RaiseTick();
        }
        static void FinishFolders(TransferJob job,bool whole) {
            if(job.Roots.Count+job.EmptyDirs.Count==0 || Interlocked.Exchange(ref job.FoldersFinished,1)!=0)return;
            try {Mover.FinishFolders(job,whole);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}
        }
        internal static void PauseAll() {foreach(var job in Jobs)Pause(job);}
        internal static void ResumeAll() {foreach(var job in Jobs)Resume(job);}
        // Jobs that have ended leave the list.
        internal static void ClearEnded() {
            lock(gate)jobs.RemoveAll(j=>!j.Active);
            RaiseTick();
        }
        internal static void Remove(TransferJob job) {
            lock(gate)if(!job.Active)jobs.Remove(job);
            RaiseTick();
        }
        // by: -1 one place earlier, +1 one place later, 0 to the very front.
        internal static void Reorder(TransferJob job,int by) {
            lock(gate) {
                int at=jobs.IndexOf(job);
                if(at<0)return;
                int to=by==0?0:Math.Max(0,Math.Min(jobs.Count-1,at+by));
                if(to==at)return;
                jobs.RemoveAt(at);jobs.Insert(to,job);
            }
            RaiseTick();
        }
        internal static bool CanSwitch(TransferJob job) {
            // A job with folders was laid out for a copy or for a move when it was queued; it stays what it is.
            return !job.Undo && !job.Started && job.FolderCount==0 && (job.State==TransferState.Waiting || job.State==TransferState.Paused);
        }
        internal static void Switch(TransferJob job) {
            if(closing)return;
            // Written down before the worker can pick the job up, so the disk never says "move" for a job changed to "copy".
            lock(saveGate) {
                lock(gate) {
                    if(!CanSwitch(job))return;
                    job.Copy=!job.Copy;
                }
                SaveJob(job);
            }
            RaiseTick();
        }
        // The program is closing: the job being sent is paused where it is (the half-copied file is kept), nothing
        // new starts, and everything unfinished is on disk for the next start. The originals are all still whole.
        static volatile bool closing;
        // The program is ending on a problem nothing was ready for. Like Shutdown, but it never waits long and never
        // answers window messages: the thread that failed may be the worker itself, or one that still holds a lock.
        // What is unfinished is already on disk, job by job, for the next start.
        internal static void StopForCrash() {
            closing=true;
            bool held=false;
            try {
                Monitor.TryEnter(gate,500,ref held);
                if(held)foreach(var job in jobs)if(job.State==TransferState.Running)job.PauseRequested=true;
            } finally {if(held)Monitor.Exit(gate);}
            wake.Set();
            if(Thread.CurrentThread==worker)return;
            for(int i=0;i<200 && (working || finishing>0);i++)Thread.Sleep(25);
        }
        internal static void Shutdown() {
            closing=true;
            foreach(var job in Jobs) {
                lock(gate)if(job.State==TransferState.Running)job.PauseRequested=true;
            }
            wake.Set();
            // The worker may be waiting for this very thread (sending a replaced file to the Recycle Bin), so the
            // thread keeps answering while it waits. The worker stops at the next 4 MB; a file that is being put
            // in its place is finished first.
            var app=Application.Current;
            for(int i=0;i<24000 && (working || finishing>0);i++) {
                if(app!=null && app.Dispatcher.CheckAccess()) {
                    try {app.Dispatcher.Invoke(new Action(()=>{}),DispatcherPriority.Background);} catch(Exception) {}
                }
                Thread.Sleep(25);
            }
            SaveOrder();
        }

        // ---------- The worker ----------
        [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);
        static void Work() {
            bool awake=false;
            while(true) {
                TransferJob job;
                // Not while a cancelled job's folders are being tidied, or while the drives are being linked again.
                while(!closing) {
                    bool hold=finishing>0;
                    if(!hold) {try {var asked=Hold;hold=asked!=null && asked();} catch(Exception quiet) {ErrorLog.Quiet(quiet);}}
                    if(!hold)break;
                    Thread.Sleep(100);
                }
                lock(gate) {
                    job=closing?null:jobs.FirstOrDefault(j=>j.State==TransferState.Waiting);
                    if(job!=null){job.State=TransferState.Running;job.Started=true;working=true;}
                    else working=false;
                }
                if(job==null) {
                    // The computer may sleep again once nothing is left to send.
                    if(awake){try {SetThreadExecutionState(0x80000000);} catch(Exception) {}awake=false;}
                    wake.WaitOne();continue;
                }
                if(!awake){try {SetThreadExecutionState(0x80000001);} catch(Exception) {}awake=true;}
                RaiseTick();
                try {Run(job);}
                catch(Exception ex) {
                    lock(job.Report)job.Report.Failed.Add(ex.Message);
                    lock(gate)job.State=TransferState.Cancelled;
                }
                Report(job,!job.Active);
                RaiseTick();
            }
        }

        static bool DriveMissing(string path) {
            try {return !Directory.Exists(Path.GetPathRoot(Path.GetFullPath(path)));} catch(Exception) {return false;}
        }
        static void Run(TransferJob job) {
            try {TileSound.Stop();} catch(Exception quiet) {ErrorLog.Quiet(quiet);} // a cover whose sound is playing would be "in use"
            // A job from an earlier run goes on only with the very drives it was queued for.
            if(job.Loaded && job.Volumes!=null) {
                foreach(var drive in job.Volumes) {
                    uint now=0;
                    try {if(Directory.Exists(drive.Key))now=OriginalLocator.VolumeOf(drive.Key);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}
                    if(now==drive.Value)continue;
                    SetPaused(job,now==0
                        ?L.PF("Drive {0} isn't connected. Connect it again and click [Go on].","{0} 드라이브가 연결되어 있지 않아요. 다시 연결한 뒤 [이어서]를 눌러 주세요.",drive.Key)
                        :L.PF("Drive {0} is not the drive this job was queued for (the letters may have changed). Nothing was sent. Connect the drives as they were and click [Go on], or cancel this job and queue it again.",
                            "{0} 드라이브가 이 작업을 넣을 때의 그 드라이브가 아니에요(드라이브 문자가 바뀌었을 수 있어요). 아무것도 보내지 않았어요. 드라이브를 그때처럼 연결하고 [이어서]를 누르거나, 이 작업을 취소하고 다시 넣어 주세요.",drive.Key));
                    return;
                }
            }
            var copier=new TransferCopier(job);
            while(job.Next<job.Items.Count) {
                if(job.CancelRequested)break;
                if(job.PauseRequested){SetPaused(job,null);return;}
                var item=job.Items[job.Next];
                string name=Path.GetFileName(item.From);
                job.CurrentName=name;
                string renamed=null;
                int index=job.Next;
                copier.Index=index;
                // The last run stopped while this very file was being sent, without writing down how that ended.
                // (With a half-copied file kept, the copier simply goes on with it.)
                if(job.InFlight==index) {
                    job.InFlight=-1;
                    string arrived=null,lost=null;
                    if(job.Partial==null) {
                        try {
                            string there=Path.Combine(item.TargetSource,name);
                            if(item.Whole){if(!Directory.Exists(item.From) && Directory.Exists(there))arrived=there;}
                            else {
                                bool source=File.Exists(item.From);
                                var copy=new FileInfo(there);
                                // The file at the new place is the one this job put there: same size, same time.
                                bool ours=copy.Exists && copy.Length==item.Size && Math.Abs((copy.LastWriteTimeUtc-item.Stamp).TotalSeconds)<=2;
                                if(item.Conflict) {
                                    // A same-name file was involved; what was done to it can't be told apart safely.
                                    if(!source)lost=L.P("The program stopped while this file was being sent. It is not in its old place any more: please look in the folder it was going to.",
                                        "이 파일을 보내는 중에 프로그램이 꺼졌어요. 원래 자리에는 이제 없으니, 보내던 폴더에서 확인해 주세요.");
                                } else if(ours) {
                                    if(job.Copy || !source)arrived=there;
                                    else {
                                        // Copied but not finished (the original is still there): that copy goes and the file is sent again properly.
                                        File.SetAttributes(there,FileAttributes.Normal);File.Delete(there);
                                    }
                                }
                            }
                        } catch(Exception quiet) {ErrorLog.Quiet(quiet);}
                    }
                    if(lost!=null) {
                        item.Why=lost;item.Outcome=5;
                        lock(job.Report)job.Report.Failed.Add(name+" — "+lost);
                        Log(job,"I\t"+index+"\tF\t"+item.From+"\t","F\t"+Flat(name+" — "+lost));
                        Interlocked.Add(ref job.DoneBase,item.Size);Interlocked.Exchange(ref job.FileDone,0);
                        job.Next++;
                        continue;
                    }
                    if(arrived!=null) {
                        if(!job.Copy && !item.Whole) {
                            // The file had moved but its covers and tags may not have followed yet.
                            try {Mover.Recover(job.Store,item.From,arrived,item.TargetAlbum);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}
                            if(item.RestoreName!=null && !String.Equals(name,item.RestoreName,StringComparison.OrdinalIgnoreCase)
                                && !File.Exists(Path.Combine(item.TargetSource,item.RestoreName))) {
                                try {arrived=Mover.RenameInPlace(job.Store,arrived,item.RestoreName);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}
                            }
                        }
                        lock(job.Report) {
                            if(job.Copy)job.Report.Copied++;
                            else {
                                job.Report.Moved.Add(Tuple.Create(item.From,arrived));
                                if(item.MakeFolder)lock(job.Touched)job.Touched.Add(Path.GetDirectoryName(item.From));
                            }
                        }
                        Log(job,"I\t"+index+"\t"+(job.Copy?"C":"M")+"\t"+item.From+"\t"+arrived);
                        item.Arrived=arrived;item.Outcome=job.Copy?2:1;
                        Interlocked.Add(ref job.DoneBase,item.Size);Interlocked.Exchange(ref job.FileDone,0);
                        job.Next++;
                        continue;
                    }
                }
                Log(job,"B\t"+index); // this file is being sent now
                try {
                    string destination,warning;
                    Mover.TransferResult result;
                    if(item.Whole) {
                        // A whole folder within its drive: one step, and the records learn its new path.
                        if(CoverWork.HoldsUnder(item.From))throw new IOException(L.P("A cover is being made from a file in this folder right now.","지금 이 폴더 안의 파일로 표지를 만들고 있어요."));
                        string to=Path.Combine(item.TargetSource,name);
                        if(Directory.Exists(to) || File.Exists(to))throw new IOException(L.F("There is already a folder or file named “{0}” here.",name));
                        Renamer.Relocate(job.Store,item.From,to);
                        lock(job.Report)job.Report.Moved.Add(Tuple.Create(item.From,to));
                        Log(job,"I\t"+index+"\tM\t"+item.From+"\t"+to);
                        item.Arrived=to;item.Outcome=1;
                        job.Next++;
                        continue;
                    }
                    // A job from an earlier run: the file must still be the one that was queued.
                    if(job.Loaded && item.Stamp!=default(DateTime) && job.Partial==null) {
                        var still=new FileInfo(item.From);
                        if(still.Exists && (still.Length!=item.Size || Math.Abs((still.LastWriteTimeUtc-item.Stamp).TotalSeconds)>2))
                            throw new IOException(L.P("This file changed after it was queued the last time, so it was not sent.","지난번에 대기열에 넣은 뒤에 파일이 바뀌어서 보내지 않았어요."));
                    }
                    if(item.MakeFolder) {
                        Mover.MakeFolders(job,item.TargetSource);
                        // Cover records and tags that follow a file need the new folder's own record folder.
                        if(!job.Copy && !Directory.Exists(item.TargetAlbum)) {try {job.Store.OpenSubfolder(item.TargetSource);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}}
                    }
                    try {
                        result=Mover.TransferOne(job.Store,item.From,item.TargetSource,item.TargetAlbum,job.Copy,item.Decided,
                            item.SeenLength<0?null:Tuple.Create(item.SeenLength,item.SeenStamp),job.Recycle,copier,out destination,out warning,out renamed);
                    } finally {
                        // The file had been given its "keep both" name and couldn't get its old one back (the drive
                        // came loose): from now on that name is where it is.
                        if(renamed!=null){item.From=renamed;item.Conflict=false;item.Decided=null;}
                    }
                    string code;
                    lock(job.Report) {
                        // A file whose original couldn't be deleted is whole in both places: there is nothing to put back.
                        if(result==Mover.TransferResult.Moved && warning!=null){job.Report.Copied++;code="C";}
                        else if(result==Mover.TransferResult.Moved) {
                            job.Report.Moved.Add(Tuple.Create(item.From,destination));code="M";
                            if(item.MakeFolder)lock(job.Touched)job.Touched.Add(Path.GetDirectoryName(item.From));
                        }
                        else if(result==Mover.TransferResult.Copied){job.Report.Copied++;code="C";}
                        else if(result==Mover.TransferResult.Skipped){job.Report.Skipped++;code="S";}
                        else {job.Report.AlreadyThere++;code="A";}
                        if(warning!=null)job.Report.Failed.Add(name+" — "+warning);
                    }
                    Log(job,"I\t"+index+"\t"+code+"\t"+item.From+"\t"+(destination??""),warning==null?null:"F\t"+Flat(name+" — "+warning));
                    // Undo: a file that got another name to keep both gets its old name back when that name is free.
                    if(result==Mover.TransferResult.Moved && warning==null && item.RestoreName!=null
                        && !String.Equals(Path.GetFileName(destination),item.RestoreName,StringComparison.OrdinalIgnoreCase)
                        && !File.Exists(Path.Combine(item.TargetSource,item.RestoreName))) {
                        try {destination=Mover.RenameInPlace(job.Store,destination,item.RestoreName)??destination;} catch(Exception quiet) {ErrorLog.Quiet(quiet);}
                    }
                    int became=code=="M"?1:code=="S"?3:code=="A"?4:result==Mover.TransferResult.Moved?6:2;
                    if(became!=3)item.Arrived=destination;
                    item.Why=warning;item.Outcome=became;
                } catch(TransferPausedException) {SetPaused(job,null);return;}
                catch(OperationCanceledException) {break;}
                catch(Exception ex) {
                    // A drive that was unplugged: the job waits as it is instead of failing every file left in it.
                    if(DriveMissing(item.From) || DriveMissing(item.TargetSource)) {
                        // (A half-copied file kept from before is not thrown away: it may be on the drive that is missing.)
                        Interlocked.Exchange(ref job.FileDone,job.Partial==null?0:job.Partial.Offset);
                        SetPaused(job,L.P("A drive isn't connected. Connect it again and click [Go on].","드라이브가 연결되어 있지 않아요. 다시 연결한 뒤 [이어서]를 눌러 주세요."));
                        return;
                    }
                    lock(job.Report)job.Report.Failed.Add(name+" — "+ex.Message);
                    Log(job,"I\t"+index+"\tF\t"+item.From+"\t","F\t"+Flat(name+" — "+ex.Message));
                    item.Why=ex.Message;item.Outcome=5;
                }
                Interlocked.Add(ref job.DoneBase,item.Size);Interlocked.Exchange(ref job.FileDone,0);
                job.Next++;
            }
            job.CurrentName=null;
            if(job.CancelRequested)DropPartial(job);
            FinishFolders(job,!job.CancelRequested);
            lock(gate)job.State=job.CancelRequested && job.Next<job.Items.Count?TransferState.Cancelled:TransferState.Done;
        }
        static void SetPaused(TransferJob job,string note) {
            lock(gate) {
                job.Note=note;job.PauseRequested=false;
                // Cancelled in the same moment it paused: the cancel wins.
                if(job.CancelRequested)job.State=TransferState.Cancelled;
                else job.State=TransferState.Paused;
            }
            // A cancel that arrived while the job was pausing: what it has already sent is tidied like any cancel.
            if(job.State==TransferState.Cancelled){DropPartial(job);FinishFolders(job,false);}
        }
        static void DropPartial(TransferJob job) {
            var partial=Interlocked.Exchange(ref job.Partial,null);
            Interlocked.Exchange(ref job.FileDone,0);
            if(partial!=null)DeleteTemp(partial.Landing);
        }
        // Tells the folder windows what has happened so far; ended: the job will do nothing more.
        static void Report(TransferJob job,bool ended) {
            OnUi(()=>{
                int moved,copied;
                lock(job.Report){moved=job.Report.Moved.Count;copied=job.Report.Copied;}
                bool changed=moved>job.ToldMoved || copied>job.ToldCopied || (ended && job.Roots.Count+job.EmptyDirs.Count+job.FolderCount>0);
                job.ToldMoved=moved;job.ToldCopied=copied;
                if(changed){var handler=FilesMoved;if(handler!=null)handler();}
                if(ended && !job.Reported) {
                    job.Reported=true;
                    if(job.Finished!=null)job.Finished(job);
                }
            });
        }

        // ---------- The queue on disk: unfinished jobs are there again the next time the program starts ----------
        // <root>\queue\<id>.job.xml holds what a job is (written once), <id>.log gets a line for every file as it
        // is dealt with, and order.xml lists the unfinished jobs in their order with any half-copied file.
        static string queueFolder;
        static readonly object saveGate=new object();
        static string Flat(string text) {return (text??"").Replace('\t',' ').Replace('\r',' ').Replace('\n',' ');}
        static void SaveJob(TransferJob job) {
            if(queueFolder==null)return;
            try {
                if(job.Volumes==null) {
                    job.Volumes=new Dictionary<string,uint>(StringComparer.OrdinalIgnoreCase);
                    foreach(string path in job.Items.Select(i=>i.From).Concat(job.Items.Select(i=>i.TargetSource))) {
                        try {
                            string drive=Path.GetPathRoot(Path.GetFullPath(path));
                            if(String.IsNullOrEmpty(drive) || job.Volumes.ContainsKey(drive))continue;
                            uint number=OriginalLocator.VolumeOf(drive);
                            if(number!=0)job.Volumes[drive]=number;
                        } catch(Exception quiet) {ErrorLog.Quiet(quiet);}
                    }
                }
                var xml=new XElement("job",new XAttribute("id",job.Id),new XAttribute("copy",job.Copy),new XAttribute("undo",job.Undo),new XAttribute("verify",job.Verify),
                    new XAttribute("from",job.FromLabel??""),new XAttribute("to",job.ToLabel??""),new XAttribute("folder",job.FolderName??""),new XAttribute("folders",job.FolderCount),
                    job.Roots.Select(r=>new XElement("root",new XAttribute("from",r.Item1),new XAttribute("to",r.Item2))),
                    job.EmptyDirs.Select(r=>new XElement("empty",new XAttribute("from",r.Item1),new XAttribute("to",r.Item2))),
                    job.OnlyThese==null?null:new XElement("only",job.OnlyThese.Select(p=>new XElement("folder",new XAttribute("path",p)))),
                    job.Volumes.Select(v=>new XElement("drive",new XAttribute("root",v.Key),new XAttribute("number",v.Value))),
                    job.Items.Select(i=>new XElement("item",new XAttribute("from",i.From),new XAttribute("target",i.TargetSource),new XAttribute("album",i.TargetAlbum??""),new XAttribute("size",i.Size),new XAttribute("stamp",i.Stamp.Ticks),
                        i.Conflict?new XAttribute("conflict",true):null,
                        i.Decided.HasValue?new XAttribute("decided",i.Decided.Value.ToString()):null,
                        i.SeenLength<0?null:new XAttribute("seenLength",i.SeenLength),i.SeenLength<0?null:new XAttribute("seenStamp",i.SeenStamp.Ticks),
                        i.RestoreName==null?null:new XAttribute("restore",i.RestoreName),i.Whole?new XAttribute("whole",true):null,i.MakeFolder?new XAttribute("make",true):null)));
                lock(saveGate) {
                    Directory.CreateDirectory(queueFolder);
                    string file=Path.Combine(queueFolder,job.Id+".job.xml"),part=file+".tmp";
                    xml.Save(part);
                    if(File.Exists(file))File.Replace(part,file,null);else File.Move(part,file);
                    job.Saved=true;
                }
            } catch(Exception quiet) {ErrorLog.Quiet(quiet);job.Saved=false;} // it runs all the same; it just won't be there after a restart
        }
        internal static void Log(TransferJob job,params string[] lines) {
            if(queueFolder==null)return;
            lock(saveGate) {
                if(!job.Saved)return;
                try {File.AppendAllLines(Path.Combine(queueFolder,job.Id+".log"),lines.Where(l=>l!=null));}
                catch(Exception hole) {
                    ErrorLog.Quiet(hole);
                    // A record with a hole in it would be worse than none: this job is no longer kept for a restart.
                    job.Saved=false;
                    try {File.Delete(Path.Combine(queueFolder,job.Id+".job.xml"));File.Delete(Path.Combine(queueFolder,job.Id+".log"));} catch(Exception quiet) {ErrorLog.Quiet(quiet);}
                }
            }
        }
        static string lastOrder;
        static void SaveOrder() {
            if(queueFolder==null)return;
            try {
                // One at a time, and the list is read inside: an older picture of the queue never overwrites a newer one.
                lock(saveGate) {
                    List<TransferJob> all;
                    XElement xml;
                    lock(gate) {
                        all=jobs.ToList();
                        xml=new XElement("queue",all.Where(j=>(j.Active || j.Tidying) && j.Saved).Select(j=>{
                            var partial=j.Partial;
                            return new XElement("job",new XAttribute("id",j.Id),
                                partial==null?null:new XElement("partial",new XAttribute("key",partial.Key),new XAttribute("landing",partial.Landing),
                                    new XAttribute("offset",partial.Offset),new XAttribute("length",partial.Length),new XAttribute("stamp",partial.Stamp.Ticks),
                                    new XAttribute("volume",partial.Volume),new XAttribute("id",partial.Id)));
                        }));
                    }
                    string text=xml.ToString();
                    string file=Path.Combine(queueFolder,"order.xml");
                    if(text!=lastOrder) {
                        if(!xml.HasElements){if(File.Exists(file))File.Delete(file);}
                        else {
                            Directory.CreateDirectory(queueFolder);
                            string part=file+".tmp";
                            File.WriteAllText(part,text);
                            if(File.Exists(file))File.Replace(part,file,null);else File.Move(part,file);
                        }
                        lastOrder=text;
                    }
                    // Jobs that have ended (and are tidied) need their files no more.
                    foreach(var job in all.Where(j=>!j.Active && !j.Tidying && j.Saved)) {
                        job.Saved=false;
                        try {File.Delete(Path.Combine(queueFolder,job.Id+".job.xml"));File.Delete(Path.Combine(queueFolder,job.Id+".log"));} catch(Exception quiet) {ErrorLog.Quiet(quiet);}
                    }
                }
            } catch(Exception quiet) {ErrorLog.Quiet(quiet);}
        }
        // Reads the jobs the last run left unfinished. They come back paused: nothing is sent until [Go on] is clicked.
        static void LoadSaved(Action<TransferJob> bind) {
            string order=Path.Combine(queueFolder,"order.xml");
            if(!File.Exists(order))return;
            var loaded=new List<TransferJob>();
            XElement list;
            try {list=XElement.Load(order);}
            catch(Exception unreadable) {
                ErrorLog.Quiet(unreadable);
                // A list that can't be read is put aside, not written over: the jobs' own files are still there.
                try {File.Copy(order,Path.Combine(queueFolder,"order.unreadable.xml"),true);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}
                return;
            }
            foreach(var entry in list.Elements("job")) {
                try {
                    string id=(string)entry.Attribute("id");
                    var xml=XElement.Load(Path.Combine(queueFolder,id+".job.xml"));
                    var job=new TransferJob {Id=id,Copy=(bool)xml.Attribute("copy"),Undo=(bool)xml.Attribute("undo"),Verify=(bool?)xml.Attribute("verify")??false,
                        FromLabel=(string)xml.Attribute("from"),ToLabel=(string)xml.Attribute("to"),FolderName=(string)xml.Attribute("folder"),FolderCount=(int?)xml.Attribute("folders")??0};
                    foreach(var r in xml.Elements("root"))job.Roots.Add(Tuple.Create((string)r.Attribute("from"),(string)r.Attribute("to")));
                    foreach(var r in xml.Elements("empty"))job.EmptyDirs.Add(Tuple.Create((string)r.Attribute("from"),(string)r.Attribute("to")));
                    var only=xml.Element("only");
                    if(only!=null)job.OnlyThese=new HashSet<string>(only.Elements("folder").Select(f=>(string)f.Attribute("path")),StringComparer.OrdinalIgnoreCase);
                    job.Volumes=new Dictionary<string,uint>(StringComparer.OrdinalIgnoreCase);
                    foreach(var d in xml.Elements("drive"))job.Volumes[(string)d.Attribute("root")]=(uint)d.Attribute("number");
                    foreach(var i in xml.Elements("item")) {
                        var item=new TransferItem {From=(string)i.Attribute("from"),TargetSource=(string)i.Attribute("target"),TargetAlbum=(string)i.Attribute("album"),Size=(long)i.Attribute("size"),Stamp=new DateTime((long?)i.Attribute("stamp")??0,DateTimeKind.Utc),
                            Conflict=(bool?)i.Attribute("conflict")??false,RestoreName=(string)i.Attribute("restore"),Whole=(bool?)i.Attribute("whole")??false,MakeFolder=(bool?)i.Attribute("make")??false};
                        string decided=(string)i.Attribute("decided");
                        if(decided!=null)item.Decided=(ConflictChoice)Enum.Parse(typeof(ConflictChoice),decided);
                        if(i.Attribute("seenLength")!=null){item.SeenLength=(long)i.Attribute("seenLength");item.SeenStamp=new DateTime((long)i.Attribute("seenStamp"),DateTimeKind.Utc);}
                        job.Items.Add(item);
                    }
                    job.TotalBytes=job.Items.Sum(i=>i.Size);
                    // What the job had already done, line by line.
                    string log=Path.Combine(queueFolder,id+".log");
                    int begun=-1;
                    var before=new Dictionary<int,Tuple<string,bool,ConflictChoice?>>(); // a renamed file as it was before
                    if(File.Exists(log))foreach(string line in File.ReadAllLines(log)) {
                        // A line cut short by a power failure is skipped; it never costs the whole job.
                        var f=line.Split('\t');
                        int at;
                        if(f[0]=="F"){if(f.Length>1)job.Report.Failed.Add(f[1]);continue;}
                        if(f[0]=="D"){if(f.Length>1 && f[1].Length>0)job.Created.Add(f[1]);continue;}
                        if(f.Length<2 || !int.TryParse(f[1],out at) || at<0 || at>=job.Items.Count)continue;
                        var item=job.Items[at];
                        if(f[0]=="B")begun=at;
                        else if(f[0]=="R" && f.Length>2 && f[2].Length>0) {
                            if(!before.ContainsKey(at))before[at]=Tuple.Create(item.From,item.Conflict,item.Decided);
                            item.From=f[2];item.Conflict=false;item.Decided=null;
                        } else if(f[0]=="U" && before.ContainsKey(at)) {
                            item.From=before[at].Item1;item.Conflict=before[at].Item2;item.Decided=before[at].Item3;before.Remove(at);
                        } else if(f[0]=="I" && f.Length>4) {
                            if(f[2]=="M") {
                                job.Report.Moved.Add(Tuple.Create(f[3],f[4]));
                                if(item.MakeFolder)job.Touched.Add(Path.GetDirectoryName(f[3]));
                            } else if(f[2]=="C")job.Report.Copied++;
                            else if(f[2]=="S")job.Report.Skipped++;
                            else if(f[2]=="A")job.Report.AlreadyThere++;
                            item.Outcome=f[2]=="M"?1:f[2]=="C"?(job.Copy?2:6):f[2]=="S"?3:f[2]=="A"?4:5;
                            if(f[2]=="M" || f[2]=="C")item.Arrived=f[4];
                            if(at+1>job.Next)job.Next=at+1;
                            if(begun==at)begun=-1;
                        }
                    }
                    if(begun>=job.Next)job.InFlight=begun;
                    job.DoneBase=job.Items.Take(job.Next).Sum(i=>i.Size);
                    job.ToldMoved=job.Report.Moved.Count;job.ToldCopied=job.Report.Copied;
                    var partial=entry.Element("partial");
                    if(partial!=null) {
                        job.Partial=new PartialCopy {Key=(string)partial.Attribute("key"),Landing=(string)partial.Attribute("landing"),Offset=(long)partial.Attribute("offset"),
                            Length=(long)partial.Attribute("length"),Stamp=new DateTime((long)partial.Attribute("stamp"),DateTimeKind.Utc),
                            Volume=(uint?)partial.Attribute("volume")??0,Id=(ulong?)partial.Attribute("id")??0};
                        job.FileDone=job.Partial.Offset;
                    }
                    job.Started=job.Next>0 || job.Partial!=null;
                    job.Saved=true;job.Loaded=true;
                    job.State=TransferState.Paused;
                    job.Note=L.P("Left unfinished the last time. Click [Go on] and it goes on from where it stopped.","지난번에 끝내지 못한 작업이에요. [이어서]를 누르면 멈춘 곳부터 계속해요.");
                    bind(job);
                    loaded.Add(job);
                } catch(Exception quiet) {ErrorLog.Quiet(quiet);} // a job that can't be read is left out; its files stay where they are
            }
            lock(gate)jobs.AddRange(loaded);
        }
        internal static void LogCreated(TransferJob job,IEnumerable<string> folders) {
            Log(job,folders.Select(f=>"D\t"+f).ToArray());
        }

        // ---------- Half-copied files left by a program that was closed in the middle ----------
        static string tempList;
        // temps: half-copied files of this run. stale: ones an earlier run left that couldn't be removed yet
        // (their drive isn't connected); they stay on the list for a later start.
        static readonly HashSet<string> temps=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> stale=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Called once when the program starts: files this program was still copying when it closed are removed.
        // bind: gives a job read back from disk what only the running program has (its store, the Recycle Bin, the notice at the end).
        internal static void Start(string root,Action<TransferJob> bind) {
            string[] old;
            lock(temps) {
                if(tempList!=null)return;
                tempList=Path.Combine(root,"transfer-temp.txt");
                queueFolder=Path.Combine(root,"queue");
                try {LoadSaved(bind);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}
                // Read here, before any new copy can write its own name into the list.
                try {old=File.Exists(tempList)?File.ReadAllLines(tempList):new string[0];} catch(Exception) {old=new string[0];}
                foreach(string path in old)
                    if(path.Length>0 && Path.GetFileName(path).StartsWith("~LoveMyShelf-",StringComparison.Ordinal))stale.Add(path);
                // The half-copied file of a job that will go on is not a leftover.
                foreach(var job in Jobs)if(job.Partial!=null){stale.Remove(job.Partial.Landing);temps.Add(job.Partial.Landing);}
            }
            RaiseTick();
            if(old.Length==0)return;
            ThreadPool.QueueUserWorkItem(delegate {
                List<string> paths;
                lock(temps)paths=stale.ToList();
                foreach(string path in paths) {
                    bool gone=false;
                    try {
                        if(!DriveMissing(path)) {
                            if(File.Exists(path)) {
                                File.SetAttributes(path,FileAttributes.Normal);
                                File.Delete(path);
                            }
                            gone=!File.Exists(path);
                        }
                    } catch(Exception quiet) {ErrorLog.Quiet(quiet);}
                    if(gone)lock(temps)stale.Remove(path);
                }
                lock(temps)SaveTemps();
            });
        }
        static void SaveTemps() {
            if(tempList==null)return;
            try {
                if(temps.Count+stale.Count==0)File.Delete(tempList);
                else File.WriteAllLines(tempList,temps.Concat(stale).ToArray());
            } catch(Exception quiet) {ErrorLog.Quiet(quiet);}
        }
        internal static void RememberTemp(string path) {lock(temps){if(temps.Add(path))SaveTemps();}}
        internal static void ForgetTemp(string path) {lock(temps){if(temps.Remove(path))SaveTemps();}}
        // Removes a half-copied file. If it can't be removed now (its drive came loose), it stays on the list
        // and goes the next time the program starts with that drive connected.
        internal static void DeleteTemp(string path) {
            bool gone=false;
            try {
                if(File.Exists(path))File.Delete(path);
                gone=!DriveMissing(path) && !File.Exists(path);
            } catch(Exception quiet) {ErrorLog.Quiet(quiet);}
            lock(temps) {
                temps.Remove(path);
                if(!gone)stale.Add(path);
                SaveTemps();
            }
        }
    }

    // Asked when files are dropped on another drive without Ctrl or Shift: copy them or move them?
    internal sealed class TransferChoiceWindow : Window {
        // null: cancelled. true: copy. false: move.
        internal bool? CopyChosen;
        internal bool Remember;
        internal TransferChoiceWindow(Window owner,int count,string folderName) {
            UI.WindowSetup(this,L.P("Copy or move?","복사할까요, 옮길까요?"));Owner=owner;ShowInTaskbar=false;
            WindowStartupLocation=WindowStartupLocation.CenterOwner;ResizeMode=ResizeMode.NoResize;
            Width=Math.Min(460,SystemParameters.WorkArea.Width);SizeToContent=SizeToContent.Height;
            // Whole pixels: at 150 % and 225 % a button that ends on a half pixel loses its right edge.
            var root=new DockPanel {UseLayoutRounding=true};root.Children.Add(new WindowTitleBar(this));
            var page=new StackPanel {Margin=new Thickness(22,16,22,20)};root.Children.Add(page);Content=root;
            var title=UI.Text(NoticeLook.KeepWords(L.PF("{0:N0} files go to the “{1}” folder on another drive.","파일 {0:N0}개를 다른 드라이브의 ‘{1}’ 폴더로 보내요.",count,folderName)),15);
            title.TextWrapping=TextWrapping.Wrap;page.Children.Add(title);
            var lines=UI.Text(L.P("[Copy] leaves the originals where they are.\n[Move] deletes each original after it has arrived whole, and its covers and tags go with it.",
                "[복사]는 원본을 그 자리에 그대로 둬요.\n[옮기기]는 파일이 다 도착한 뒤에 원본을 지우고, 표지와 태그도 함께 따라가요."),13,true);
            lines.TextWrapping=TextWrapping.Wrap;lines.Margin=new Thickness(0,8,0,0);page.Children.Add(lines);
            var keys=UI.Text(L.P("Hold Ctrl while dropping to copy, or Shift to move, and this question is skipped.","Ctrl을 누른 채 놓으면 복사, Shift를 누른 채 놓으면 옮기기로 바로 넘어가요."),12,true);
            keys.TextWrapping=TextWrapping.Wrap;keys.Margin=new Thickness(0,8,0,0);page.Children.Add(keys);
            var remember=new CheckBox {Content=L.P("Do the same next time, without asking","다음부터 묻지 않고 이대로 해요"),Margin=new Thickness(0,12,0,0)};
            remember.SetResourceReference(Control.ForegroundProperty,"Text");page.Children.Add(remember);
            var later=UI.Text(L.P("You can change this later in Guide & Options.","나중에 설명서 겸 옵션에서 바꿀 수 있어요."),12,true);
            later.Margin=new Thickness(0,4,0,0);later.TextWrapping=TextWrapping.Wrap;page.Children.Add(later);
            var actions=new WrapPanel {HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,14,0,0)};
            Action<string,bool?,bool> add=(label,choice,primary)=>{
                var button=UI.Btn(label,()=>{CopyChosen=choice;Remember=choice.HasValue && remember.IsChecked==true;Close();},primary);
                button.FontSize=14;button.MinHeight=32;button.Margin=new Thickness(6,0,0,0);button.Padding=new Thickness(16,0,16,0);
                if(choice==null)button.IsCancel=true;
                actions.Children.Add(button);
            };
            add(L.T("Cancel"),null,false);
            add(L.P("Move","옮기기"),false,false);
            add(L.P("Copy","복사"),true,true); // what File Explorer does between drives
            page.Children.Add(actions);
            PreviewKeyDown+=(s,e)=>{if(e.Key==Key.Escape){e.Handled=true;Close();}};
        }
    }

    // The file queue: every move and copy that is waiting, running, paused or done, with how far each one is.
    internal sealed class TransferWindow : Window {
        static TransferWindow open;
        // The window was closed while files were still being sent (they go on being sent).
        internal static event Action ClosedWhileWorking;
        internal static void ShowQueue(bool activate) {
            if(open==null) {
                open=new TransferWindow();
                open.ShowActivated=activate;
                open.Show();
                return;
            }
            if(open.WindowState==WindowState.Minimized)open.WindowState=WindowState.Normal;
            if(activate)open.Activate();
        }
        internal static void CloseQueue() {if(open!=null)open.Close();}

        readonly StackPanel list=new StackPanel();
        readonly TextBlock summary=UI.Text("",13);
        readonly Button pauseAll,clear;
        readonly List<Card> cards=new List<Card>();
        readonly TextBlock empty=UI.Text("",13,true);

        TransferWindow() {
            UI.WindowSetup(this,L.P("File queue","파일 대기열"));
            var area=SystemParameters.WorkArea;
            Width=Math.Min(720,area.Width-16);Height=Math.Min(560,area.Height-16);MinWidth=Math.Min(380,Width);MinHeight=Math.Min(220,Height);
            // Whole pixels: at 150 % and 225 % a button that ends on a half pixel loses its right edge.
            var root=new DockPanel {UseLayoutRounding=true};root.Children.Add(new WindowTitleBar(this));
            var top=new DockPanel {Margin=new Thickness(14,10,14,8)};DockPanel.SetDock(top,Dock.Top);
            var buttons=new StackPanel {Orientation=Orientation.Horizontal};DockPanel.SetDock(buttons,Dock.Right);
            pauseAll=UI.Btn("",()=>{if(TransferQueue.Working)TransferQueue.PauseAll();else TransferQueue.ResumeAll();});
            clear=UI.Btn(L.P("Clear finished","끝난 작업 지우기"),TransferQueue.ClearEnded);
            clear.ToolTip=L.P("Takes the jobs that have ended off this list. No file is touched.","끝난 작업을 이 목록에서만 지워요. 파일은 건드리지 않아요.");
            foreach(var b in new[]{pauseAll,clear}){b.Margin=new Thickness(6,0,0,0);b.Padding=new Thickness(10,0,10,0);buttons.Children.Add(b);}
            top.Children.Add(buttons);
            summary.TextTrimming=TextTrimming.CharacterEllipsis;top.Children.Add(summary);
            root.Children.Add(top);
            empty.Text=L.P("Nothing is waiting. Files you move or copy to another folder show up here.","기다리는 작업이 없어요. 파일을 다른 폴더로 옮기거나 복사하면 여기에 보여요.");
            empty.TextWrapping=TextWrapping.Wrap;empty.Margin=new Thickness(4,10,4,0);
            var scroll=new ScrollViewer {Content=list,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Margin=new Thickness(10,0,6,10),Focusable=false};
            root.Children.Add(scroll);Content=root;
            TransferQueue.Tick+=Refresh;
            Closed+=delegate {
                TransferQueue.Tick-=Refresh;if(open==this)open=null;
                var handler=ClosedWhileWorking;
                if(handler!=null && TransferQueue.Working)handler();
            };
            PreviewKeyDown+=(s,e)=>{if(e.Key==Key.Escape){e.Handled=true;Close();}};
            Refresh();
        }

        void Refresh() {
            var jobs=TransferQueue.Jobs;
            if(jobs.Count!=cards.Count || jobs.Where((j,i)=>cards[i].Job!=j).Any()) {
                var old=cards.ToDictionary(c=>c.Job);
                cards.Clear();list.Children.Clear();
                foreach(var job in jobs) {
                    Card card;
                    if(!old.TryGetValue(job,out card))card=new Card(job);
                    cards.Add(card);list.Children.Add(card);
                }
                if(jobs.Count==0)list.Children.Add(empty);
            }
            for(int i=0;i<cards.Count;i++)cards[i].Update(i,cards.Count);
            int waiting=jobs.Count(j=>j.State==TransferState.Waiting),paused=jobs.Count(j=>j.State==TransferState.Paused);
            bool running=jobs.Any(j=>j.State==TransferState.Running);
            long left=jobs.Where(j=>j.Active).Sum(j=>j.TotalBytes-j.DoneBytes);
            if(!jobs.Any(j=>j.Active))summary.Text=jobs.Count==0?"":L.P("Everything is done.","모두 끝났어요.");
            else summary.Text=L.PF("{0} left","남은 용량 {0}",TransferQueue.Bytes(left))+" · "+String.Join(" · ",Summaries(running,waiting,paused));
            summary.ToolTip=summary.Text;
            bool working=TransferQueue.Working;
            pauseAll.Content=working?L.P("Pause all","모두 일시정지"):L.P("Go on with all","모두 이어서");
            pauseAll.IsEnabled=working || paused>0;
            clear.IsEnabled=jobs.Any(j=>!j.Active);
        }
        static IEnumerable<string> Summaries(bool running,int waiting,int paused) {
            if(running)yield return L.P("Sending 1","1개 보내는 중");
            if(waiting>0)yield return L.PF("{0:N0} waiting","{0:N0}개 대기",waiting);
            if(paused>0)yield return L.PF("{0:N0} paused","{0:N0}개 일시정지",paused);
        }

        static string Time(double seconds) {
            if(seconds<60)return L.PF("about {0:N0} s left","약 {0:N0}초 남음",Math.Max(1,Math.Round(seconds)));
            if(seconds<3600)return L.PF("about {0:N0} min left","약 {0:N0}분 남음",Math.Round(seconds/60));
            return L.PF("about {0:N0} h {1:N0} min left","약 {0:N0}시간 {1:N0}분 남음",Math.Floor(seconds/3600),Math.Round(seconds%3600/60));
        }

        // One job in the list.
        sealed class Card : Border {
            internal readonly TransferJob Job;
            readonly TextBlock kind=new TextBlock {FontSize=12,FontWeight=FontWeights.SemiBold};
            readonly Border pill=new Border {CornerRadius=new CornerRadius(8),Padding=new Thickness(8,1,8,2),BorderThickness=new Thickness(1),VerticalAlignment=VerticalAlignment.Center};
            readonly TextBlock title=UI.Text("",13),state=UI.Text("",12,true),path=UI.Text("",12,true),detail=UI.Text("",12,true),failed=UI.Text("",12);
            readonly ColumnDefinition done=new ColumnDefinition(),rest=new ColumnDefinition();
            readonly Border fill=new Border {CornerRadius=new CornerRadius(3)};
            readonly WrapPanel actions=new WrapPanel {Margin=new Thickness(0,6,0,0)};
            string drawn;
            bool showFailed;
            // The file list under the bar: every file of the job with what has become of it.
            readonly FileList fileList;
            readonly DockPanel listHead=new DockPanel {Margin=new Thickness(0,7,0,0)};
            readonly Button toggle,toCurrent;
            readonly TextBlock counts=UI.Text("",12,true);
            // null: open while the job is being sent, closed otherwise. Set once the person opens or closes it.
            bool? opened;
            int lastIndex,lastCount;
            static readonly Brush MoveColor=Frozen(0xE0,0x8A,0x2E),CopyColor=Frozen(0x31,0x82,0xF6);
            static readonly Brush MoveTint=Tone(0x2E,0xE0,0x8A,0x2E),CopyTint=Tone(0x2E,0x31,0x82,0xF6);
            static Brush Frozen(byte r,byte g,byte b) {var brush=new SolidColorBrush(Color.FromRgb(r,g,b));brush.Freeze();return brush;}

            internal Card(TransferJob job) {
                Job=job;
                BorderThickness=new Thickness(1);CornerRadius=new CornerRadius(8);Padding=new Thickness(12,9,12,10);Margin=new Thickness(0,0,4,8);
                SetResourceReference(BackgroundProperty,"Panel");SetResourceReference(BorderBrushProperty,"Line");
                var stack=new StackPanel();Child=stack;
                var head=new DockPanel();
                pill.Child=kind;DockPanel.SetDock(pill,Dock.Left);head.Children.Add(pill);
                state.Margin=new Thickness(8,0,0,0);DockPanel.SetDock(state,Dock.Right);head.Children.Add(state);
                title.FontWeight=FontWeights.SemiBold;title.Margin=new Thickness(8,0,0,0);title.TextTrimming=TextTrimming.CharacterEllipsis;head.Children.Add(title);
                stack.Children.Add(head);
                path.Margin=new Thickness(0,4,0,0);path.TextTrimming=TextTrimming.CharacterEllipsis;stack.Children.Add(path);
                var bar=new Grid {Height=6,Margin=new Thickness(0,7,0,0)};
                var track=new Border {CornerRadius=new CornerRadius(3)};track.SetResourceReference(BackgroundProperty,"Raised");
                Grid.SetColumnSpan(track,2);bar.ColumnDefinitions.Add(done);bar.ColumnDefinitions.Add(rest);bar.Children.Add(track);bar.Children.Add(fill);
                stack.Children.Add(bar);
                detail.Margin=new Thickness(0,5,0,0);detail.TextTrimming=TextTrimming.CharacterEllipsis;stack.Children.Add(detail);
                toggle=UI.Btn("",()=>{opened=!IsOpen(Job.State);Update(lastIndex,lastCount);});
                toCurrent=UI.Btn(L.P("To the file being sent","지금 보내는 파일로"),()=>{fileList.Follow=true;Update(lastIndex,lastCount);});
                toCurrent.ToolTip=L.P("Brings the list back to the file being sent, and it follows along again.","목록을 지금 보내는 파일 자리로 되돌리고, 다시 따라가요.");
                foreach(var b in new[]{toggle,toCurrent}){b.FontSize=12;b.MinHeight=24;b.Padding=new Thickness(9,0,9,0);}
                DockPanel.SetDock(toggle,Dock.Left);listHead.Children.Add(toggle);
                DockPanel.SetDock(toCurrent,Dock.Right);listHead.Children.Add(toCurrent);
                counts.Margin=new Thickness(9,0,9,0);counts.TextTrimming=TextTrimming.CharacterEllipsis;listHead.Children.Add(counts);
                stack.Children.Add(listHead);
                fileList=new FileList(job);stack.Children.Add(fileList);
                fileList.Unfollowed=()=>Update(lastIndex,lastCount);
                failed.TextWrapping=TextWrapping.Wrap;failed.Margin=new Thickness(0,5,0,0);failed.Visibility=Visibility.Collapsed;stack.Children.Add(failed);
                stack.Children.Add(actions);
            }
            bool IsOpen(TransferState now) {return opened??(now==TransferState.Running || (now==TransferState.Paused && Job.Started));}

            Button Small(string label,string tip,Action action) {
                var button=UI.Btn(label,action);
                button.FontSize=12;button.MinHeight=24;button.Padding=new Thickness(9,0,9,0);button.Margin=new Thickness(0,0,6,4);
                button.ToolTip=tip;System.Windows.Automation.AutomationProperties.SetName(button,tip??label);
                actions.Children.Add(button);
                return button;
            }

            internal void Update(int index,int count) {
                var job=Job;var now=job.State;bool copy=job.Copy;
                lastIndex=index;lastCount=count;
                int files=job.Items.Count,failures=job.FailedCount;
                bool open=files>0 && IsOpen(now);
                long total=job.TotalBytes,sent=job.DoneBytes;
                double part=now==TransferState.Done?1:total>0?Math.Max(0,Math.Min(1,(double)sent/total)):files>0?(double)job.Next/files:0;
                kind.Text=job.Undo?L.P("Put back","되돌리기"):copy?L.P("Copy","복사"):L.P("Move","옮기기");
                var color=copy?CopyColor:MoveColor;
                kind.Foreground=color;pill.BorderBrush=color;fill.Background=color;
                int wholes=job.Items.Count(i=>i.Whole),folders=Math.Max(job.FolderCount,wholes),single=files-wholes;
                title.Text=(folders>0?L.PF("{0:N0} folders","폴더 {0:N0}개",folders)+(single>0?" · ":""):"")
                    +(single>0 || folders==0?L.PF("{0:N0} files","파일 {0:N0}개",single):"")+(total>0?" · "+TransferQueue.Bytes(total):"");
                path.Text=job.FromLabel+"  →  "+job.ToLabel;path.ToolTip=path.Text;
                done.Width=new GridLength(part,GridUnitType.Star);rest.Width=new GridLength(1-part,GridUnitType.Star);
                fill.Opacity=now==TransferState.Paused || now==TransferState.Cancelled?0.45:1;

                if(now==TransferState.Running) {
                    // Speed over the last second or so, smoothed so the time left doesn't jump about.
                    var time=DateTime.UtcNow;
                    if(job.SampleTime==default(DateTime)){job.SampleTime=time;job.SampleBytes=sent;}
                    double seconds=(time-job.SampleTime).TotalSeconds;
                    if(seconds>=1) {
                        double fresh=Math.Max(0,sent-job.SampleBytes)/seconds;
                        job.Speed=job.Speed<=0?fresh:job.Speed*0.6+fresh*0.4;
                        job.SampleTime=time;job.SampleBytes=sent;
                    }
                } else {job.SampleTime=default(DateTime);job.Speed=0;}

                switch(now) {
                    case TransferState.Running:
                        state.Text=job.PauseRequested?L.P("Pausing…","멈추는 중…"):job.CancelRequested?L.P("Cancelling…","취소하는 중…"):job.Verifying?L.P("Checking","확인하는 중"):L.P("Sending","보내는 중");
                        var words=new List<string>();
                        if(job.Verifying) {
                            long of=Interlocked.Read(ref job.FileBytes),read=Interlocked.Read(ref job.VerifyDone);
                            words.Add(L.PF("Checking that the copy is the same as the original · {0}%","복사본이 원본과 똑같은지 확인하는 중 · {0}%",of>0?Math.Floor(100.0*read/of):0));
                            words.Add(L.PF("{0:N0} of {1:N0}","{1:N0}개 중 {0:N0}번째",Math.Min(files,job.Next+1),files));
                            if(job.CurrentName!=null && !open)words.Add(job.CurrentName);
                            detail.Text=String.Join(" · ",words);break;
                        }
                        // The numbers come first: a long file name is what gets cut off when the window is narrow.
                        words.Add(Math.Floor(part*100).ToString("0")+"%");
                        if(job.Speed>0) {
                            words.Add(TransferQueue.Bytes((long)job.Speed)+L.P("/s","/초"));
                            words.Add(Time((total-sent)/job.Speed));
                        }
                        words.Add(L.PF("{0:N0} of {1:N0}","{1:N0}개 중 {0:N0}번째",Math.Min(files,job.Next+1),files));
                        if(job.CurrentName!=null && !open)words.Add(job.CurrentName);
                        detail.Text=String.Join(" · ",words);break;
                    case TransferState.Waiting:
                        state.Text=L.P("Waiting","대기 중");
                        detail.Text=job.Started
                            ?L.PF("It goes on from {0}% when its turn comes.","차례가 오면 {0}%부터 이어서 보내요.",Math.Floor(part*100))
                            :copy?L.P("It starts when its turn comes.","차례가 오면 시작해요.")
                            :L.P("It starts when its turn comes. Each original is deleted after it has arrived whole.","차례가 오면 시작해요. 다 도착한 파일부터 원본을 지워요.");break;
                    case TransferState.Paused:
                        state.Text=L.P("Paused","일시정지됨");
                        detail.Text=job.Note??L.PF("Stopped at {0}%. Click [Go on] and it goes on from there. Nothing is held open, so the drive can be removed safely.",
                            "{0}%에서 멈춰 뒀어요. [이어서]를 누르면 거기서부터 계속해요. 파일을 잡고 있지 않아서 드라이브를 안전하게 뺄 수 있어요.",Math.Floor(part*100));break;
                    case TransferState.Done:
                    case TransferState.Cancelled:
                        int moved,copied,skipped;
                        lock(job.Report){moved=job.Report.Moved.Count;copied=job.Report.Copied;skipped=job.Report.Skipped;}
                        state.Text=now==TransferState.Done?(failures>0?L.P("Done, with some left","끝났어요 · 안 된 파일 있음"):L.P("Done","끝났어요")):L.P("Cancelled","취소했어요");
                        var told=new List<string>();
                        if(moved>0)told.Add(job.Undo?L.PF("{0:N0} put back","{0:N0}개 제자리로 돌려놓음",moved):L.PF("{0:N0} moved","{0:N0}개 옮김",moved));
                        if(copied>0)told.Add(L.PF("{0:N0} copied","{0:N0}개 복사",copied));
                        if(skipped>0)told.Add(L.PF("{0:N0} skipped","{0:N0}개 건너뜀",skipped));
                        if(failures>0)told.Add(L.PF("{0:N0} not done","{0:N0}개 안 됨",failures));
                        if(now==TransferState.Cancelled && job.Next<files)told.Add(L.PF("{0:N0} left where they were","{0:N0}개는 원래 자리에 그대로",files-job.Next));
                        detail.Text=told.Count>0?String.Join(" · ",told):L.P("Nothing was changed.","아무것도 바꾸지 않았어요.");break;
                }
                detail.ToolTip=detail.Text;
                detail.TextWrapping=now==TransferState.Paused?TextWrapping.Wrap:TextWrapping.NoWrap;

                // The file list. How many are done, being sent, still to go and not done is told beside its button.
                listHead.Visibility=files>0?Visibility.Visible:Visibility.Collapsed;
                // (The place it has got to is read before each file's outcome: the outcome is written first.)
                int next=job.Next,ended=0,bad=0,sending=0,left=0;
                for(int i=0;i<files;i++) {
                    int outcome=job.Items[i].Outcome;
                    if(outcome==5)bad++;
                    else if(outcome!=0 || i<next)ended++;
                    else if(i==next && now==TransferState.Running)sending=1;
                    else left++;
                }
                var tally=new List<string>();
                if(ended>0)tally.Add(L.PF("{0:N0} done","끝남 {0:N0}",ended));
                if(sending>0)tally.Add(L.P("1 being sent","보내는 중 1"));
                if(left>0)tally.Add(now==TransferState.Cancelled?L.PF("{0:N0} left as they were","그대로 둠 {0:N0}",left):L.PF("{0:N0} to go","남음 {0:N0}",left));
                if(bad>0)tally.Add(L.PF("{0:N0} not done","안 됨 {0:N0}",bad));
                counts.Text=String.Join(" · ",tally);
                toggle.Content=open?L.P("Hide the files  ▴","파일 목록 접기  ▴"):L.P("Show the files  ▾","파일 목록 보기  ▾");
                toggle.ToolTip=open?null:L.P("Shows every file of this job: which are done, which is being sent, which are waiting.","이 작업의 파일을 하나씩 보여 줘요. 어느 파일이 끝났고, 어느 파일을 보내는 중이고, 어느 파일이 기다리는지 볼 수 있어요.");
                fileList.Visibility=open?Visibility.Visible:Visibility.Collapsed;
                if(open)fileList.Show(color,copy?CopyTint:MoveTint);
                toCurrent.Visibility=open && !fileList.Follow && next<files && (now==TransferState.Running || now==TransferState.Paused)?Visibility.Visible:Visibility.Collapsed;
                if(showFailed && failures>0) {
                    var lines=job.Failures();
                    failed.Text=String.Join(Environment.NewLine,lines.Take(30))+(lines.Count>30?Environment.NewLine+L.F("and {0:N0} more",lines.Count-30):"");
                    failed.Visibility=Visibility.Visible;
                } else failed.Visibility=Visibility.Collapsed;

                // The buttons are made again only when what they should be has changed.
                string key=now+"|"+copy+"|"+job.Started+"|"+(failures>0)+"|"+showFailed+"|"+(index==0)+"|"+(index==count-1)+"|"+job.PauseRequested+"|"+job.CancelRequested;
                if(key==drawn)return;
                drawn=key;actions.Children.Clear();
                bool active=job.Active;
                if(now==TransferState.Running || now==TransferState.Waiting) {
                    var pause=Small(L.P("Pause","일시정지"),L.P("Stops this job where it is. The file being sent is let go of, and it goes on from the same place later.","이 작업을 그 자리에서 멈춰요. 보내던 파일을 놓아 두고, 나중에 같은 곳부터 이어서 보내요."),()=>TransferQueue.Pause(job));
                    pause.IsEnabled=!job.PauseRequested && !job.CancelRequested;
                }
                if(now==TransferState.Paused)
                    Small(L.P("Go on","이어서"),L.P("Puts this job back in line. It goes on when its turn comes.","이 작업을 다시 줄에 세워요. 차례가 오면 이어서 보내요."),()=>TransferQueue.Resume(job)).Style=(Style)Application.Current.Resources["Primary"];
                if(active) {
                    var cancel=Small(L.T("Cancel"),L.P("Stops this job for good. Files already sent stay sent; the rest stay where they are.","이 작업을 그만둬요. 이미 보낸 파일은 그대로 두고, 나머지는 원래 자리에 남아요."),()=>TransferQueue.Cancel(job));
                    cancel.IsEnabled=!job.CancelRequested;
                }
                if(now==TransferState.Waiting || now==TransferState.Paused) {
                    Small("↑",L.P("One place earlier","한 칸 앞으로"),()=>TransferQueue.Reorder(job,-1)).IsEnabled=index>0;
                    Small("↓",L.P("One place later","한 칸 뒤로"),()=>TransferQueue.Reorder(job,1)).IsEnabled=index<count-1;
                    Small(L.P("To the front","맨 앞으로"),L.P("This job goes next, right after the one being sent now.","지금 보내는 작업이 끝나면 바로 이 작업을 보내요."),()=>TransferQueue.Reorder(job,0)).IsEnabled=index>0;
                }
                if(TransferQueue.CanSwitch(job))
                    Small(copy?L.P("Change to move","옮기기로 바꾸기"):L.P("Change to copy","복사로 바꾸기"),
                        copy?L.P("Each original is deleted after it has arrived whole, and its covers and tags go with it.","다 도착한 파일부터 원본을 지우고, 표지와 태그도 함께 따라가요.")
                            :L.P("The originals stay where they are.","원본을 그 자리에 그대로 둬요."),()=>TransferQueue.Switch(job));
                if(failures>0)
                    Small(showFailed?L.P("Hide the list","목록 접기"):L.P("See what wasn't done","안 된 파일 보기"),null,()=>{showFailed=!showFailed;Update(index,count);});
                if(!active)
                    Small(L.P("Take off the list","목록에서 지우기"),L.P("Only this line goes away. No file is touched.","이 줄만 목록에서 지워요. 파일은 건드리지 않아요."),()=>TransferQueue.Remove(job));
            }
        }

        static Brush Tone(byte a,byte r,byte g,byte b) {var brush=new SolidColorBrush(Color.FromArgb(a,r,g,b));brush.Freeze();return brush;}

        // The files of one job, in the order they are sent, each with what has become of it. Only the rows in
        // view exist, so a job of thousands of files costs no more than one of five.
        sealed class FileList : Border {
            internal const double RowHeight=30;
            const int Shown=5;
            readonly TransferJob job;
            readonly ScrollViewer view=new ScrollViewer {VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,Focusable=false};
            readonly Canvas canvas=new Canvas();
            readonly List<Row> rows=new List<Row>();
            Brush color,tint;
            double wanted=-1;
            // The list keeps the file being sent in view until the person scrolls it themselves;
            // [To the file being sent] turns this on again.
            internal bool Follow=true;
            internal Action Unfollowed;

            internal FileList(TransferJob job) {
                this.job=job;
                CornerRadius=new CornerRadius(6);BorderThickness=new Thickness(1);Margin=new Thickness(0,6,0,0);ClipToBounds=true;
                SetResourceReference(BackgroundProperty,"Paper");SetResourceReference(BorderBrushProperty,"Line");
                view.Content=canvas;Child=view;
                view.ScrollChanged+=(s,e)=>{
                    if(Follow && e.VerticalChange!=0 && Math.Abs(e.VerticalOffset-wanted)>1) {
                        Follow=false;
                        if(Unfollowed!=null)Dispatcher.BeginInvoke(Unfollowed);
                    }
                    Draw();
                };
                // At its top or bottom the wheel goes on to the queue itself instead of stopping dead here.
                view.PreviewMouseWheel+=(s,e)=>{
                    bool up=e.Delta>0;
                    if(up?view.VerticalOffset>0:view.VerticalOffset<view.ScrollableHeight-0.5)return;
                    e.Handled=true;
                    var parent=VisualTreeHelper.GetParent(this) as UIElement;
                    if(parent!=null)parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice,e.Timestamp,e.Delta) {RoutedEvent=UIElement.MouseWheelEvent,Source=this});
                };
            }

            internal void Show(Brush color,Brush tint) {
                this.color=color;this.tint=tint;
                int count=job.Items.Count;
                double tall=Math.Min(count,Shown)*RowHeight,all=count*RowHeight;
                if(view.Height!=tall)view.Height=tall;
                if(canvas.Height!=all)canvas.Height=all;
                if(Follow) {
                    // One finished file stays in view above the one being sent.
                    double target=Math.Max(0,Math.Min((count-Shown)*RowHeight,(Math.Min(job.Next,count-1)-1)*RowHeight));
                    wanted=target;
                    if(Math.Abs(view.VerticalOffset-target)>0.5)view.ScrollToVerticalOffset(target);
                }
                Draw();
            }

            void Draw() {
                int count=job.Items.Count;
                double width=view.ViewportWidth,height=view.ViewportHeight;
                if(width<=0 || height<=0 || color==null)return; // not laid out yet: it is drawn when it has a size
                int first=Math.Max(0,(int)Math.Floor(view.VerticalOffset/RowHeight));
                int last=Math.Min(count-1,(int)Math.Floor((view.VerticalOffset+height-0.5)/RowHeight));
                int need=Math.Max(0,last-first+1);
                while(rows.Count<need){var row=new Row(job);rows.Add(row);canvas.Children.Add(row);}
                for(int i=0;i<rows.Count;i++) {
                    var row=rows[i];
                    if(i>=need){row.Visibility=Visibility.Collapsed;continue;}
                    row.Visibility=Visibility.Visible;
                    if(row.Width!=width)row.Width=width;
                    Canvas.SetTop(row,(first+i)*RowHeight);
                    row.Show(first+i,color,tint);
                }
            }
        }

        // One file in the list: a mark, its name, and what has become of it. The file being sent is lit and has its own bar.
        sealed class Row : Grid {
            readonly TransferJob job;
            readonly Border glow=new Border {CornerRadius=new CornerRadius(5),Margin=new Thickness(3,1,3,1),Visibility=Visibility.Collapsed};
            readonly TextBlock mark=new TextBlock {FontSize=12,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
            readonly MiddleText name=new MiddleText {Margin=new Thickness(0,0,14,0)};
            readonly TextBlock status=new TextBlock {FontSize=12,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,12,0)};
            readonly ColumnDefinition done=new ColumnDefinition(),rest=new ColumnDefinition();
            readonly Grid bar=new Grid {Height=3,VerticalAlignment=VerticalAlignment.Bottom,Margin=new Thickness(28,0,12,3),Visibility=Visibility.Collapsed};
            readonly Border fill=new Border {CornerRadius=new CornerRadius(1.5)};
            readonly TranslateTransform lift=new TranslateTransform();
            string drawn;
            int index=-1;
            static readonly Brush Good=Tone(0xFF,0x2F,0xA8,0x62),Bad=Tone(0xFF,0xE5,0x48,0x4D);

            internal Row(TransferJob job) {
                this.job=job;
                Height=FileList.RowHeight;Background=Brushes.Transparent;
                ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(28)});
                ColumnDefinitions.Add(new ColumnDefinition());
                ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
                Grid.SetColumnSpan(glow,3);Children.Add(glow);
                Children.Add(mark);
                Grid.SetColumn(name,1);Children.Add(name);
                // Numbers of one width, so the line doesn't shiver as the percent goes up.
                System.Windows.Documents.Typography.SetNumeralAlignment(status,FontNumeralAlignment.Tabular);
                Grid.SetColumn(status,2);Children.Add(status);
                foreach(var element in new UIElement[]{mark,name,status})element.RenderTransform=lift;
                var track=new Border {CornerRadius=new CornerRadius(1.5)};track.SetResourceReference(Border.BackgroundProperty,"Raised");
                Grid.SetColumnSpan(track,2);bar.ColumnDefinitions.Add(done);bar.ColumnDefinitions.Add(rest);bar.Children.Add(track);bar.Children.Add(fill);
                Grid.SetColumnSpan(bar,3);Children.Add(bar);
                MouseLeftButtonDown+=(s,e)=>{if(e.ClickCount==2){e.Handled=true;OpenPlace();}};
            }

            internal void Show(int at,Brush color,Brush tint) {
                index=at;
                var item=job.Items[at];var state=job.State;
                int next=job.Next,outcome=item.Outcome;
                string title=Path.GetFileName(item.From);
                long size=item.Size;
                string amount=item.Whole?L.P("folder","폴더"):TransferQueue.Bytes(size);
                string sign,words;Brush ink=null,told=null;bool lit=false,faint=false;double part=-1;
                if(outcome==5){sign="✕";words=L.P("Not done","안 됨");ink=Bad;told=Bad;}
                else if(outcome==6){sign="!";words=L.P("Copied · original still there","복사됨 · 원본 남음");ink=color;}
                else if(outcome==3){sign="–";words=L.P("Skipped","건너뜀");}
                else if(outcome==4){sign="–";words=L.P("Already there","이미 있음");}
                else if(outcome!=0 || at<next) {
                    sign="✓";ink=Good;
                    words=(outcome==2 || (outcome==0 && job.Copy)?L.P("Copied","복사함"):job.Undo?L.P("Put back","돌려놓음"):L.P("Moved","옮김"))+" · "+amount;
                }
                else if(at==next && (state==TransferState.Running || state==TransferState.Paused)) {
                    long sent=Math.Max(0,Math.Min(size,Interlocked.Read(ref job.FileDone)));
                    double share=size>0?Math.Max(0,Math.Min(1,(double)sent/size)):0;
                    if(state==TransferState.Running) {
                        sign="▶";ink=color;lit=true;part=item.Whole?-1:share;
                        if(job.Verifying) {
                            long of=Interlocked.Read(ref job.FileBytes),read=Interlocked.Read(ref job.VerifyDone);
                            part=of>0?Math.Max(0,Math.Min(1,(double)read/of)):0;
                            words=L.PF("Checking {0}%","확인 중 {0}%",Math.Floor(part*100));
                        }
                        else if(item.Whole || size<=0){words=L.P("Sending","보내는 중");part=-1;}
                        else words=Math.Floor(share*100).ToString("0")+"% · "+TransferQueue.Bytes(sent)+" / "+TransferQueue.Bytes(size);
                    } else if(sent>0) {
                        sign="Ⅱ";ink=color;lit=true;part=share;
                        words=L.PF("Paused · {0}%","멈춤 · {0}%",Math.Floor(share*100));
                    } else {sign="•";words=L.P("Waiting","대기")+" · "+amount;faint=true;}
                }
                else if(state==TransferState.Cancelled || state==TransferState.Done){sign="–";words=L.P("Left as it was","그대로 둠");faint=true;}
                else {sign="•";words=L.P("Waiting","대기")+" · "+amount;faint=true;}

                string why=item.Why;
                string key=at+"|"+sign+"|"+words+"|"+title+"|"+lit+"|"+Math.Round(part*400)+"|"+(color==fill.Background)+"|"+why;
                if(key==drawn)return;
                drawn=key;
                mark.Text=sign;
                if(ink!=null)mark.Foreground=ink;else mark.SetResourceReference(TextBlock.ForegroundProperty,"Subtle");
                name.Set(title,lit?FontWeights.SemiBold:FontWeights.Normal,faint?"Subtle":"Text");
                status.Text=words;
                if(told!=null)status.Foreground=told;else status.SetResourceReference(TextBlock.ForegroundProperty,lit?"Text":"Subtle");
                glow.Background=tint;glow.Visibility=lit?Visibility.Visible:Visibility.Collapsed;
                fill.Background=color;
                bar.Visibility=part>=0?Visibility.Visible:Visibility.Collapsed;
                if(part>=0){done.Width=new GridLength(part,GridUnitType.Star);rest.Width=new GridLength(1-part,GridUnitType.Star);}
                // The words sit a little higher on the line that has a bar under them.
                lift.Y=part>=0?-2:0;
                ToolTip=title+Environment.NewLine+words+(String.IsNullOrEmpty(why)?"":Environment.NewLine+why)
                    +Environment.NewLine+L.P("Double-click to open the folder this file is in.","더블클릭하면 이 파일이 있는 폴더를 열어요.");
            }

            void OpenPlace() {
                if(index<0 || index>=job.Items.Count)return;
                var item=job.Items[index];
                try {
                    string path=null;
                    foreach(string place in new[]{item.Arrived,item.From}) {
                        if(place!=null && (File.Exists(place) || Directory.Exists(place))){path=place;break;}
                    }
                    if(path!=null){System.Diagnostics.Process.Start("explorer.exe","/select,\""+path+"\"");return;}
                    int outcome=item.Outcome;
                    string folder=outcome==1 || outcome==2 || outcome==4 || outcome==6?item.TargetSource:Path.GetDirectoryName(item.From);
                    if(folder!=null && Directory.Exists(folder))System.Diagnostics.Process.Start("explorer.exe","\""+folder+"\"");
                } catch(Exception quiet) {ErrorLog.Quiet(quiet);}
            }
        }

        // A file name on one line. When it is too long, the middle is left out ("0012 - [4K] concert…cam.mp4"),
        // so both the start and the end with the file type can still be read.
        sealed class MiddleText : FrameworkElement {
            static readonly DependencyProperty InkProperty=DependencyProperty.Register("Ink",typeof(Brush),typeof(MiddleText),
                new FrameworkPropertyMetadata(null,FrameworkPropertyMetadataOptions.AffectsRender));
            string text="";
            FontWeight weight=FontWeights.Normal;
            FormattedText fitted;
            double fittedWidth=-1;

            internal void Set(string value,FontWeight heavy,string brush) {
                SetResourceReference(InkProperty,brush);
                value=value??"";
                if(value==text && heavy==weight)return;
                text=value;weight=heavy;fitted=null;
                InvalidateVisual();
            }
            internal MiddleText() {ClipToBounds=true;}
            protected override Size MeasureOverride(Size available) {return new Size(0,18);}
            // Not between the two halves of one character (emoji and rare letters take two).
            int Whole(int at) {return at>0 && at<text.Length && Char.IsLowSurrogate(text[at]) && Char.IsHighSurrogate(text[at-1])?at-1:at;}
            FormattedText Make(string s) {
                var family=(FontFamily)Application.Current.TryFindResource("AppFont")??new FontFamily("Segoe UI");
                return new FormattedText(s,System.Globalization.CultureInfo.CurrentUICulture,FlowDirection.LeftToRight,
                    new Typeface(family,FontStyles.Normal,weight,FontStretches.Normal),13,Brushes.Black,VisualTreeHelper.GetDpi(this).PixelsPerDip);
            }
            FormattedText Fit(double width) {
                var whole=Make(text);
                if(whole.WidthIncludingTrailingWhitespace<=width)return whole;
                // The end that stays: the file type and a few letters before it.
                int dot=text.LastIndexOf('.');
                int tail=dot>0 && text.Length-dot<=6?Math.Min(text.Length-1,text.Length-dot+6):Math.Min(8,text.Length/2);
                string end=text.Substring(Whole(text.Length-tail));
                var best=Make("…"+end);
                if(best.WidthIncludingTrailingWhitespace>width) {
                    // Too narrow even for that: the usual cut at the end.
                    whole.MaxTextWidth=Math.Max(1,width);whole.MaxLineCount=1;whole.Trimming=TextTrimming.CharacterEllipsis;
                    return whole;
                }
                int low=0,high=text.Length-end.Length;
                while(low<high) {
                    int middle=(low+high+1)/2;
                    var tried=Make(text.Substring(0,Whole(middle)).TrimEnd()+"…"+end);
                    if(tried.WidthIncludingTrailingWhitespace<=width){low=middle;best=tried;}
                    else high=middle-1;
                }
                return best;
            }
            protected override void OnRender(DrawingContext context) {
                double width=ActualWidth;
                if(width<=0 || text.Length==0)return;
                if(fitted==null || fittedWidth!=width){fitted=Fit(width);fittedWidth=width;}
                var ink=(Brush)GetValue(InkProperty);
                if(ink!=null)fitted.SetForegroundBrush(ink);
                context.DrawText(fitted,new Point(0,Math.Max(0,(ActualHeight-fitted.Height)/2)));
            }
        }
    }
}
