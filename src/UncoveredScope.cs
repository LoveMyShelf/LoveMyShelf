using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace LoveMyShelf {
    // Where the "Unfinished cover collection" looks: only the folders added to LoveMyShelf, or every drive of this computer.
    // 2026-11-27 user: "표지 x도 컴퓨터 전체에서 보여주는 거랑, 내가 직접 추가한 폴더에 있는 것만 보여주는 거 선택할 수 있었으면 좋겠어"
    internal enum UncoveredRange {Added,Computer}

    // The options of the Unfinished cover collection. Kept in their own file beside the other records, so they never
    // touch the main options file; every folder window shares them.
    internal sealed class UncoveredOptions {
        const string FileName="uncovered-collection.xml";
        // null until the person chooses: then "added folders" when there are any, else the whole computer.
        public UncoveredRange? Range;
        public bool HintDone;
        public bool Removable=true,Network,Hidden,SkipSystem=true;
        public List<string> Excluded=new List<string>();
        public int MinSizeKb;          // 0 … 10,485,760 (10 GB)
        public int DrivesAtOnce=1;     // 1 … 8: hard disks fighting over one head only slow each other
        public int WorkersPerDrive=4;  // 1 … 32
        public int RecheckMinutes=30;  // 0 … 1,440; 0 = only when asked

        static UncoveredOptions current;
        static string root;
        internal static event Action Changed;
        internal static UncoveredOptions Current {get {return current??(current=new UncoveredOptions());}}

        internal static void Use(string storeRoot) {
            if(root!=null)return;
            root=storeRoot;current=Load(Path.Combine(storeRoot,FileName));
        }
        static UncoveredOptions Load(string path) {
            var o=new UncoveredOptions();
            try {
                if(!File.Exists(path))return o;
                var x=XElement.Load(path);
                string range=(string)x.Attribute("range");
                o.Range=range=="computer"?UncoveredRange.Computer:range=="added"?(UncoveredRange?)UncoveredRange.Added:null;
                o.HintDone=(string)x.Attribute("hintDone")=="true";
                o.Removable=(string)x.Attribute("removable")!="false";
                o.Network=(string)x.Attribute("network")=="true";
                o.Hidden=(string)x.Attribute("hidden")=="true";
                o.SkipSystem=(string)x.Attribute("skipSystem")!="false";
                o.MinSizeKb=Number(x,"minSizeKb",0,10485760,0);
                o.DrivesAtOnce=Number(x,"drivesAtOnce",1,8,1);
                o.WorkersPerDrive=Number(x,"workersPerDrive",1,32,4);
                o.RecheckMinutes=Number(x,"recheckMinutes",0,1440,30);
                o.Excluded=x.Elements("exclude").Select(e=>(string)e).Where(s=>!String.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            } catch(Exception quiet) {ErrorLog.Quiet(quiet);}
            return o;
        }
        static int Number(XElement x,string name,int min,int max,int def) {
            int value;
            return Int32.TryParse((string)x.Attribute(name),out value)?Math.Max(min,Math.Min(max,value)):def;
        }
        // Saves and tells every window. A change to what is looked through also forgets the last list.
        internal static void Change(Action<UncoveredOptions> change) {
            var o=Current;
            string before=o.ListKey;
            change(o);
            o.MinSizeKb=Math.Max(0,Math.Min(10485760,o.MinSizeKb));
            o.DrivesAtOnce=Math.Max(1,Math.Min(8,o.DrivesAtOnce));
            o.WorkersPerDrive=Math.Max(1,Math.Min(32,o.WorkersPerDrive));
            o.RecheckMinutes=Math.Max(0,Math.Min(1440,o.RecheckMinutes));
            if(o.ListKey!=before)UncoveredFolders.Forget();
            try {
                if(root!=null) {
                    var x=new XElement("uncovered",
                        o.Range.HasValue?new XAttribute("range",o.Range==UncoveredRange.Computer?"computer":"added"):null,
                        o.HintDone?new XAttribute("hintDone","true"):null,
                        new XAttribute("removable",o.Removable),new XAttribute("network",o.Network),new XAttribute("hidden",o.Hidden),
                        new XAttribute("skipSystem",o.SkipSystem),new XAttribute("minSizeKb",o.MinSizeKb),
                        new XAttribute("drivesAtOnce",o.DrivesAtOnce),new XAttribute("workersPerDrive",o.WorkersPerDrive),
                        new XAttribute("recheckMinutes",o.RecheckMinutes),
                        o.Excluded.Select(e=>new XElement("exclude",e)));
                    string path=Path.Combine(root,FileName),temp=path+".tmp";
                    x.Save(temp);
                    if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);
                }
            } catch(Exception quiet) {ErrorLog.Quiet(quiet);}
            var handler=Changed;if(handler!=null)handler();
        }
        // The range in use now: the person's choice, else the safe start.
        internal UncoveredRange RangeFor(int addedFolders) {
            return Range??(addedFolders>0?UncoveredRange.Added:UncoveredRange.Computer);
        }
        // What the list of folders depends on (the size limit is applied later, while reading each folder).
        string ListKey {get {return String.Join("|",Removable,Network,Hidden,SkipSystem,DrivesAtOnce,WorkersPerDrive,String.Join(";",Excluded.OrderBy(e=>e,StringComparer.OrdinalIgnoreCase)));}}

        // A copy for a reading in the background, so a change on screen never alters a list being read.
        internal UncoveredOptions Copy() {
            var c=(UncoveredOptions)MemberwiseClone();
            c.Excluded=new List<string>(Excluded);
            return c;
        }
        internal bool IsExcluded(string path) {
            foreach(string e in Excluded) {
                if(path.Length<e.Length)continue;
                if(String.Compare(path,0,e,0,e.Length,StringComparison.OrdinalIgnoreCase)!=0)continue;
                if(path.Length==e.Length || e.EndsWith("\\") || path[e.Length]=='\\')return true;
            }
            return false;
        }
    }

    // ----- Where the list of files comes from -----
    // The whole-computer list is read by an IFileListSource. Today that is the plain crawl below, which needs no
    // administrator rights and so also works in the Microsoft Store version. A faster NTFS/USN index can later stand in
    // for it by giving the same answer: the folders that have photos or videos right inside them.
    internal interface IFileListSource {
        string Name {get;}
        List<string> PictureFolders(FileListRequest request,FileListProgress progress,CancellationToken token);
    }
    internal sealed class FileListRequest {
        public List<string> Roots=new List<string>();
        public bool Hidden,SkipSystem;
        public Func<string,bool> Excluded=p=>false;
        public int RootsAtOnce=1,WorkersPerRoot=4;
    }
    // Counts while a list is read; shown under the empty page so it never looks frozen.
    internal sealed class FileListProgress {
        int folders,pictureFolders,unreadable;
        public int Folders {get {return folders;}}
        public int PictureFolders {get {return pictureFolders;}}
        public int Unreadable {get {return unreadable;}}
        internal void Folder(){Interlocked.Increment(ref folders);}
        internal void Pictures(){Interlocked.Increment(ref pictureFolders);}
        internal void CouldNotRead(){Interlocked.Increment(ref unreadable);}
    }

    // Reads folders the ordinary way, many names at once (FindFirstFileEx with the large fetch).
    // Each drive is read by a few workers; only so many drives at once, so two hard disks never fight.
    internal sealed class CrawlSource : IFileListSource {
        public string Name {get {return "crawl";}}
        // Windows' own and programs' folders: thousands of icons nobody made covers for.
        static readonly HashSet<string> TopSystem=new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "Windows","Program Files","Program Files (x86)","ProgramData","PerfLogs","$Recycle.Bin","System Volume Information",
            "Recovery","$WinREAgent","$SysReset","$Windows.~BT","$Windows.~WS","Config.Msi","MSOCache","Intel","AMD","NVIDIA","OneDriveTemp"
        };
        static readonly HashSet<string> AnywhereSystem=new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "AppData","$Recycle.Bin","System Volume Information","node_modules","__pycache__",".git"
        };
        public List<string> PictureFolders(FileListRequest request,FileListProgress progress,CancellationToken token) {
            var found=new ConcurrentBag<string>();
            var seen=new ConcurrentDictionary<string,bool>(StringComparer.OrdinalIgnoreCase);
            var roots=request.Roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            try {
                Parallel.ForEach(roots,new ParallelOptions {MaxDegreeOfParallelism=Math.Max(1,request.RootsAtOnce),CancellationToken=token},
                    root=>ReadTree(root,request,progress,found,seen,token));
            } catch(OperationCanceledException) {} // what was found so far is kept: [Stop] shows it
            catch(AggregateException e) {if(!e.InnerExceptions.All(x=>x is OperationCanceledException))throw;}
            return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        static void ReadTree(string root,FileListRequest request,FileListProgress progress,ConcurrentBag<string> found,
            ConcurrentDictionary<string,bool> seen,CancellationToken token) {
            var waiting=new ConcurrentStack<string>();
            waiting.Push(root);
            int busy=0;
            Action work=()=>{
                while(!token.IsCancellationRequested) {
                    string path;
                    // Counted as busy before taking a folder, so no worker sees "empty and nobody busy" in between.
                    Interlocked.Increment(ref busy);
                    if(!waiting.TryPop(out path)) {
                        if(Interlocked.Decrement(ref busy)==0 && waiting.IsEmpty)return;
                        Thread.Sleep(2);continue;
                    }
                    try {
                        if(!seen.TryAdd(path,true))continue;
                        progress.Folder();
                        bool pictures;
                        var inner=ReadFolder(path,request,out pictures);
                        if(inner==null){progress.CouldNotRead();continue;}
                        if(pictures){found.Add(path);progress.Pictures();}
                        foreach(string d in inner)waiting.Push(d);
                    } finally {Interlocked.Decrement(ref busy);}
                }
            };
            int workers=Math.Max(1,request.WorkersPerRoot);
            var threads=Enumerable.Range(0,workers).Select(i=>Task.Factory.StartNew(work,CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default)).ToArray();
            Task.WaitAll(threads);
        }

        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
        internal struct FindData {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Created,Accessed,Written;
            public uint SizeHigh,SizeLow,ReparseTag,Reserved;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)] public string Name;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=14)] public string ShortName;
        }
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
        static extern IntPtr FindFirstFileExW(string name,int infoLevel,out FindData data,int searchOp,IntPtr filter,int flags);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
        static extern bool FindNextFileW(IntPtr handle,out FindData data);
        [DllImport("kernel32.dll")] static extern bool FindClose(IntPtr handle);
        const uint Directory=0x10,Hidden=0x2,SystemFlag=0x4,Reparse=0x400;

        // Deep folders: the long-path form reads past 260 characters.
        static string LongForm(string pattern) {
            if(pattern.Length>=250 && !pattern.StartsWith("\\\\?\\"))
                pattern=pattern.StartsWith("\\\\")?"\\\\?\\UNC\\"+pattern.Substring(2):"\\\\?\\"+pattern;
            return pattern;
        }
        // Junctions and folder links can point back up and never end; OneDrive folders are real (other tags).
        internal static bool IsLink(uint attributes,uint tag) {return (attributes&Reparse)!=0 && (tag==0xA0000003 || tag==0xA000000C);}
        // One folder's own attributes and link tag (not a drive's root). false = it could not be read.
        internal static bool Info(string folder,out uint attributes,out uint tag) {
            FindData data;
            IntPtr handle=FindFirstFileExW(LongForm(folder.TrimEnd('\\')),1,out data,0,IntPtr.Zero,0);
            attributes=0;tag=0;
            if(handle==new IntPtr(-1))return false;
            FindClose(handle);
            attributes=data.Attributes;tag=data.ReparseTag;
            return true;
        }
        // Every entry of one folder (FindExInfoBasic, large fetch). null = the folder could not be read.
        internal static List<FindData> Entries(string folder) {
            FindData data;
            string pattern=LongForm((folder.EndsWith("\\")?folder:folder+"\\")+"*");
            IntPtr handle=FindFirstFileExW(pattern,1,out data,0,IntPtr.Zero,2);
            if(handle==new IntPtr(-1))return null;
            var list=new List<FindData>();
            try {
                do {
                    if(data.Name=="." || data.Name=="..")continue;
                    list.Add(data);
                } while(FindNextFileW(handle,out data));
            } finally {FindClose(handle);}
            return list;
        }
        // The folders inside to read next, and whether photos or videos sit right here.
        static List<string> ReadFolder(string path,FileListRequest request,out bool pictures) {
            pictures=false;
            var entries=Entries(path);
            if(entries==null)return null;
            bool top=path.Length<=3;
            string prefix=path.EndsWith("\\")?path:path+"\\";
            var inner=new List<string>();
            foreach(var e in entries) {
                if((e.Attributes&Directory)!=0) {
                    if(IsLink(e.Attributes,e.ReparseTag))continue;
                    string full=prefix+e.Name;
                    if(Skip(e.Name,e.Attributes,top,full,request))continue;
                    inner.Add(full);
                } else if(!pictures && UncoveredFolders.IsPicture(e.Name)) pictures=true;
            }
            return inner;
        }
        // Whether a folder is left out (hidden, system, LoveMyShelf's own, or excluded). Also used by EverythingSource,
        // so both ways of reading leave out the same folders. top: the folder sits right on a drive.
        internal static bool Skip(string name,uint attributes,bool top,string full,FileListRequest request) {
            if(!request.Hidden && (attributes&Hidden)!=0)return true;
            if(request.SkipSystem && ((attributes&SystemFlag)!=0 || AnywhereSystem.Contains(name) || (top && TopSystem.Contains(name))))return true;
            if(String.Equals(name,Preferences.SubfolderName,StringComparison.OrdinalIgnoreCase))return true;
            return request.Excluded(full);
        }
    }

    // The folders with photos or videos for the whole computer, read once and kept while LoveMyShelf runs.
    // Each visit reads only those folders again (cheap), so new, renamed or deleted files show at once; the whole
    // tree is read again on [Check again], after the chosen minutes, or when what is looked through changes.
    // The reading runs in the background: leaving the collection doesn't stop it, [Stop] does.
    internal static class UncoveredFolders {
        // Everything (voidtools) when it is running, else the plain crawl.
        internal static IFileListSource Source=new EverythingSource(new CrawlSource());
        static readonly object gate=new object();
        static List<string> folders;
        static DateTime readAt;
        static int unreadable,generation,runningGeneration;
        static bool partial;
        static Task<List<string>> running;
        static CancellationTokenSource stopping;
        static FileListProgress progress;

        internal static void Forget() {
            lock(gate) {
                folders=null;generation++;
                if(stopping!=null)stopping.Cancel();
            }
        }
        internal static void Stop() {lock(gate) {if(stopping!=null)stopping.Cancel();}}
        internal static bool Busy {get {lock(gate) return running!=null && !running.IsCompleted && runningGeneration==generation;}}
        internal static bool HasList {get {lock(gate) return folders!=null;}}
        internal static FileListProgress Progress {get {lock(gate) return progress;}}
        internal static int Unreadable {get {lock(gate) return unreadable;}}
        internal static bool Partial {get {lock(gate) return partial;}}
        internal static DateTime ReadAt {get {lock(gate) return readAt.ToLocalTime();}}
        static bool Fresh {
            get {
                if(folders==null)return false;
                // A stopped reading stays as it is until [Check again], so saving a cover doesn't start it all over.
                int minutes=UncoveredOptions.Current.RecheckMinutes;
                return partial || minutes==0 || DateTime.UtcNow-readAt<TimeSpan.FromMinutes(minutes);
            }
        }

        internal static bool IsPicture(string name) {
            string ext=Path.GetExtension(name);
            return (PictureTypes.Extensions.Contains(ext,StringComparer.OrdinalIgnoreCase) || VideoFrames.Supports(name)) && !SharedCoverFiles.IsBesideCover(name);
        }
        // How a path is kept: a drive keeps its "C:\", any other folder has no "\" at the end.
        internal static string Plain(string path) {return path.Length<=3?path:path.TrimEnd('\\');}
        static bool Under(string path,string top) {
            if(!path.StartsWith(top,StringComparison.OrdinalIgnoreCase))return false;
            return path.Length==top.Length || top.EndsWith("\\") || path[top.Length]=='\\';
        }
        // The drives looked through: fixed ones always, others as chosen. Folders added to LoveMyShelf always count
        // (an added network folder shows in either range). o is a copy taken on the window's thread.
        static List<string> Roots(UncoveredOptions o,List<string> added) {
            var list=new List<string>();
            foreach(var d in DriveInfo.GetDrives()) {
                try {
                    bool wanted=d.DriveType==DriveType.Fixed || (d.DriveType==DriveType.Removable && o.Removable) || (d.DriveType==DriveType.Network && o.Network);
                    if(wanted && d.IsReady && !o.IsExcluded(d.RootDirectory.FullName))list.Add(d.RootDirectory.FullName);
                } catch(IOException quiet) {ErrorLog.Quiet(quiet);} catch(UnauthorizedAccessException quiet) {ErrorLog.Quiet(quiet);}
            }
            foreach(string a in added)
                if(a!=null && !o.IsExcluded(a) && !list.Any(r=>Under(a,r)))list.Add(a);
            return list;
        }
        // The folder list: from memory when it is fresh enough, the reading already going on, or a new reading.
        // again: [Check again] — also right after [Stop], while the stopped reading is still ending.
        internal static Task<List<string>> Start(IEnumerable<string> added,string coverFolder,bool again) {
            lock(gate) {
                // A reading for an older range was told to stop and ends on its own; a new one starts.
                if(running!=null && !running.IsCompleted && runningGeneration==generation && !(again && stopping.IsCancellationRequested))return running;
                if(!again && Fresh)return Task.FromResult(folders);
                generation++; // a stopped reading still ending must not overwrite this one
                var o=UncoveredOptions.Current.Copy();
                var addedList=added.Where(a=>a!=null).Select(Plain).ToList();
                // Roots are found inside the task: asking whether a network drive is ready can take seconds.
                var request=new FileListRequest {Hidden=o.Hidden,SkipSystem=o.SkipSystem,RootsAtOnce=o.DrivesAtOnce,WorkersPerRoot=o.WorkersPerDrive};
                request.Excluded=p=>(coverFolder!=null && String.Equals(p,coverFolder,StringComparison.OrdinalIgnoreCase)) || o.IsExcluded(p);
                var stop=new CancellationTokenSource();
                var counts=new FileListProgress();
                int mine=generation;
                stopping=stop;progress=counts;runningGeneration=mine;
                running=Task.Run(()=>{
                    List<string> list;
                    try {request.Roots=Roots(o,addedList);list=Source.PictureFolders(request,counts,stop.Token);}
                    catch(Exception e) {ErrorLog.Quiet(e);list=new List<string>();}
                    lock(gate) {
                        // Something about the range changed meanwhile: this list is not kept.
                        if(mine!=generation)return list;
                        folders=list;readAt=DateTime.UtcNow;unreadable=counts.Unreadable;partial=stop.IsCancellationRequested;
                    }
                    return list;
                });
                return running;
            }
        }
    }
}
