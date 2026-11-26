using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using System.Xml.Linq;

namespace LoveMyShelf {
    // "Similar photos": one search for the whole program, shown by any folder window as a collection in the folder tree.
    // It lists the files of the chosen folders, finds exact copies and nearly the same pictures, and keeps the groups.
    internal static class SimilarSearch {
        const int Batch=200;
        internal static readonly int[] Levels={2,4,6};
        internal static readonly List<string> Folders=new List<string>();
        internal static bool Inside=true,ShowOriginals,ShowNear;
        // How exactly the same files are tidied: 0 = ask each time, 1 = one group at a time (choose the folder), 2 = automatically.
        internal static int TidyWay;
        internal static int Level=1;
        internal static List<SimilarFile> Files=new List<SimilarFile>();
        internal static List<SimilarGroup> Exact=new List<SimilarGroup>(),Near=new List<SimilarGroup>();
        internal static bool Busy,HasResult,FromLastTime;
        // Set while files go to the Recycle Bin or covers move (in any folder window): nothing else starts meanwhile.
        internal static bool Working;
        // [Stop] while every group is being tidied up: the group being done finishes, the rest wait.
        internal static bool StopWork;
        internal static void Changed() {Tell();}
        static int run;
        internal static string Phase="";
        internal static int Failed;
        internal static DateTime? LastRun;
        // Raised on the UI thread: State when the bar's words change, Groups when the groups changed.
        internal static event Action State,Groups,View;
        static GalleryStore store;
        static Dispatcher ui;
        static CancellationTokenSource scan;
        static SimilarChild child;
        static HashSet<string> notSame;
        static string SettingsFile {get {return Path.Combine(store.Root,"similar","settings.txt");}}

        internal static void Use(GalleryStore s) {
            if(store!=null)return;
            store=s;ui=Dispatcher.CurrentDispatcher;
            LoadSettings();
        }
        static string T(string english,string korean) {return L.P(english,korean);}
        // Progress from the search: a line that arrives after the search ended (or from an older one) is dropped.
        static void Say(string text) {
            int mine=run;
            ui.BeginInvoke(new Action(()=>{if(!Busy || mine!=run)return;Phase=text;if(State!=null)State();}));
        }
        // Which list or covers/originals changed: every folder window showing the collection shows it the new way.
        internal static void ViewChanged() {SaveSettings();if(View!=null)View();Tell();}
        static void Tell() {if(State!=null)State();}
        // ----- Settings: the folders, "include the folders inside", how alike, which list, covers or originals -----
        static void LoadSettings() {
            try {
                if(!File.Exists(SettingsFile))return;
                foreach(string line in File.ReadAllLines(SettingsFile,Encoding.UTF8)) {
                    int n;long ticks;
                    if(line.StartsWith("inside="))Inside=line.Substring(7)!="0";
                    else if(line.StartsWith("level=")){if(Int32.TryParse(line.Substring(6),out n) && n>=0 && n<3)Level=n;}
                    else if(line.StartsWith("originals="))ShowOriginals=line.Substring(10)=="1";
                    else if(line.StartsWith("near="))ShowNear=line.Substring(5)=="1";
                    else if(line.StartsWith("tidy=")){if(Int32.TryParse(line.Substring(5),out n) && n>=0 && n<3)TidyWay=n;}
                    else if(line.StartsWith("last=")){if(Int64.TryParse(line.Substring(5),out ticks))LastRun=new DateTime(ticks,DateTimeKind.Utc);}
                    else if(line.StartsWith("folder=")){string f=line.Substring(7);if(f.Length>0 && !Folders.Any(x=>ShellPlaces.Same(x,f)))Folders.Add(f);}
                }
            } catch(IOException) {} catch(UnauthorizedAccessException) {}
        }
        internal static void SaveSettings() {
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile));
                var lines=new List<string>{"inside="+(Inside?"1":"0"),"level="+Level,"originals="+(ShowOriginals?"1":"0"),"near="+(ShowNear?"1":"0"),"tidy="+TidyWay};
                if(LastRun.HasValue)lines.Add("last="+LastRun.Value.Ticks.ToString(CultureInfo.InvariantCulture));
                lines.AddRange(Folders.Select(f=>"folder="+f));
                File.WriteAllLines(SettingsFile,lines,new UTF8Encoding(false));
            } catch(IOException) {} catch(UnauthorizedAccessException) {}
        }
        internal static bool AddFolder(string folder) {
            if(Busy || Working || folder==null || Folders.Any(f=>ShellPlaces.Same(f,folder)))return false;
            Folders.Add(folder);SaveSettings();Tell();
            return true;
        }
        internal static void RemoveFolder(string folder) {RemoveFolders(f=>ShellPlaces.Same(f,folder));}
        // [Take out every folder]: the list empties in one click.
        internal static void RemoveAllFolders() {RemoveFolders(f=>true);}
        static void RemoveFolders(Predicate<string> which) {
            if(Busy || Working)return;
            Folders.RemoveAll(which);SaveSettings();
            // Nothing left to look through: last time's groups go too.
            if(Folders.Count==0) {
                Files=new List<SimilarFile>();Exact=new List<SimilarGroup>();Near=new List<SimilarGroup>();HasResult=false;Phase="";
                if(Groups!=null)Groups();
            }
            Tell();
        }
        internal static void SetLevel(int level) {
            if(level<0 || level>2 || level==Level)return;
            Level=level;SaveSettings();Regroup();
        }
        // ----- Looking -----
        internal static void Stop() {
            var c=scan;if(c!=null)c.Cancel();
            var reader=child;if(reader!=null)reader.Stop();
        }
        // quick: only what was worked out before (the list of files is read again, nothing new is read):
        // shows last time's groups at once when the collection is opened.
        internal static async void Start(bool quick) {
            if(Busy || Working || store==null || Folders.Count==0)return;
            var token=(scan=new CancellationTokenSource()).Token;
            Busy=true;Failed=0;run++;
            notSame=SimilarCache.NotSame(store);
            Phase=T("Listing the files…","파일 목록을 확인하는 중…");
            Tell();
            var roots=Folders.ToList();bool sub=Inside;
            string error=null;bool stopped=false;
            try {
                await Task.Run(()=>Look(roots,sub,quick,token,out error),token);
            } catch(OperationCanceledException) {stopped=true;} catch(Exception ex) {error=ex.Message;}
            if(token.IsCancellationRequested)stopped=true;
            Busy=false;scan=null;child=null;
            HasResult=true;FromLastTime=quick;
            if(!quick && !stopped && error==null){LastRun=DateTime.UtcNow;SaveSettings();}
            int pictures=Files.Count(f=>f.IsPicture),videos=Files.Count-pictures;
            Phase=error!=null?error:
                stopped?T("Stopped. What was found so far is shown. [Find] again carries on from here.","멈췄어요. 지금까지 찾은 것만 보여요. 다시 [찾기]를 누르면 이어서 해요."):
                quick?(LastRun.HasValue?L.PF("Last time's result ({0}). Files added since are not in it: click [Find] to look again.",
                    "지난번 결과예요({0}). 그 뒤에 생긴 파일은 빠져 있어요. [찾기]를 누르면 다시 찾아요.",LastRun.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm",CultureInfo.InvariantCulture))
                    :T("Last time's result. Click [Find] to look again.","지난번 결과예요. [찾기]를 누르면 다시 찾아요.")):
                L.PF("Done · {0:N0} photos and {1:N0} videos looked at{2}","끝났어요 · 사진 {0:N0}개, 영상 {1:N0}개를 살펴봤어요{2}",pictures,videos,
                    Failed>0?L.PF(" · {0:N0} couldn't be read"," · {0:N0}개는 읽지 못했어요",Failed):"");
            Tell();
            await RegroupNow();
        }
        // Runs in the background: lists the files, finds exact copies, then reads fingerprints a batch at a time.
        static void Look(List<string> roots,bool sub,bool quick,CancellationToken token,out string error) {
            error=null;
            var all=SimilarFiles.List(roots,sub,token,n=>Say(L.PF("Listing the files… {0:N0} so far","파일 목록을 확인하는 중… 지금까지 {0:N0}개",n)));
            // What was worked out before and is still true.
            var byFolder=all.GroupBy(f=>f.Folder,StringComparer.OrdinalIgnoreCase).ToDictionary(g=>g.Key,g=>g.ToList(),StringComparer.OrdinalIgnoreCase);
            foreach(var pair in byFolder) {
                token.ThrowIfCancellationRequested();
                SimilarCache.Fill(store,pair.Key,pair.Value);
                foreach(var f in pair.Value)f.HasCover=SimilarFiles.HasCover(store,f.Path);
            }
            ui.Invoke(new Action(()=>{Files=all;}));
            if(quick)return;
            var touched=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Action save=()=>{
                foreach(string folder in touched)SimilarCache.Save(store,folder,byFolder[folder]);
                touched.Clear();
            };
            // Whatever was worked out is kept, also when the search is stopped in the middle.
            try {
                // 1) Exactly the same: only files that share their size are read, and first only their start and end.
                var sameSize=all.Where(f=>f.Size>0).GroupBy(f=>f.Size).Where(g=>g.Count()>1).SelectMany(g=>g)
                    .Where(f=>f.Sha==null).OrderBy(f=>f.Path,StringComparer.OrdinalIgnoreCase).ToList();
                var peeks=new Dictionary<SimilarFile,string>();
                int done=0;
                foreach(var f in sameSize) {
                    token.ThrowIfCancellationRequested();
                    if(++done%20==0)Say(L.PF("Looking for exact copies… {0:N0} of {1:N0}","똑같은 파일 찾는 중… {1:N0}개 중 {0:N0}개",done,sameSize.Count));
                    try {peeks[f]=SimilarFiles.Peek(f.Path);} catch(IOException) {} catch(UnauthorizedAccessException) {}
                }
                var whole=all.Where(f=>f.Size>0 && (f.Sha!=null || peeks.ContainsKey(f))).GroupBy(f=>f.Size)
                    .SelectMany(g=>g.Where(f=>f.Sha==null).Where(f=>g.Count(o=>o!=f && (o.Sha!=null || (peeks.ContainsKey(o) && peeks[o]==peeks[f])))>0))
                    .OrderBy(f=>f.Path,StringComparer.OrdinalIgnoreCase).ToList();
                done=0;
                foreach(var f in whole) {
                    token.ThrowIfCancellationRequested();
                    Say(L.PF("Comparing files that look the same… {0:N0} of {1:N0}","같아 보이는 파일 비교 중… {1:N0}개 중 {0:N0}개",++done,whole.Count));
                    try {
                        string sha=SimilarFiles.WholeSha(f.Path,token);
                        lock(SimilarFiles.Gate)f.Sha=sha;
                        touched.Add(f.Folder);
                    } catch(IOException) {} catch(UnauthorizedAccessException) {}
                    if(touched.Count>0 && done%25==0)save();
                }
                save();
                ui.BeginInvoke(new Action(()=>Regroup()));
                // 2) Fingerprints of the pictures not read before.
                var todo=all.Where(f=>f.IsPicture && !f.HasHash && !f.Failed && !f.Moving).OrderBy(f=>f.Path,StringComparer.OrdinalIgnoreCase).ToList();
                int total=todo.Count;
                var reader=new SimilarChild();
                child=reader;
                for(int at=0;at<todo.Count;at+=Batch) {
                    token.ThrowIfCancellationRequested();
                    var batch=todo.Skip(at).Take(Batch).ToList();
                    var left=batch;
                    try {
                        // When the copy stops on a picture that breaks it, only that picture is marked "couldn't be read";
                        // the ones after it are read by a new copy.
                        for(int tries=0;left.Count>0 && tries<20;tries++) {
                            int before=at+batch.Count-left.Count;
                            string problem=reader.Read(left,n=>Say(L.PF("Reading the photos… {0:N0} of {1:N0}. You can keep using LoveMyShelf meanwhile.",
                                "사진 읽는 중… {1:N0}개 중 {0:N0}개. 그동안 LoveMyShelf를 계속 써도 돼요.",before+n,total)));
                            token.ThrowIfCancellationRequested();
                            var unread=left.Where(f=>!f.HasHash && !f.Failed && !f.Moving).ToList();
                            if(problem==null || unread.Count==0)break;
                            lock(SimilarFiles.Gate)unread[0].Failed=true;
                            left=unread.Skip(1).ToList();
                        }
                    } finally {
                        foreach(var f in batch)if(f.HasHash || f.Failed || f.Moving)touched.Add(f.Folder);
                    }
                    save();
                }
            } finally {
                try {save();} catch(Exception) {}
                Failed=all.Count(f=>f.Failed);
            }
        }
        // ----- Groups -----
        static bool grouping,groupAgain;
        static int version;
        internal static async void Regroup() {await RegroupNow();}
        // One grouping at a time; asking again while one runs makes it run once more afterwards with what is newest.
        static async Task RegroupNow() {
            if(grouping){groupAgain=true;return;}
            grouping=true;
            try {
                do {
                    groupAgain=false;
                    int mine=++version;
                    var snapshot=Files.ToList();var skip=notSame??SimilarCache.NotSame(store);notSame=skip;
                    int most=Levels[Level];
                    Tuple<List<SimilarGroup>,List<SimilarGroup>> made;
                    try {
                        made=await Task.Run(()=>Tuple.Create(SimilarGroups.Exact(snapshot),SimilarGroups.Near(snapshot,most,skip)));
                    } catch(Exception ex) {Phase=ex.Message;Tell();return;}
                    if(mine!=version)continue;
                    Exact=made.Item1;Near=made.Item2;
                    if(Groups!=null)Groups();
                    Tell();
                } while(groupAgain);
            } finally {grouping=false;}
        }
        // Files that went to the Recycle Bin leave the groups.
        internal static void Removed(IEnumerable<SimilarFile> gone) {
            var set=new HashSet<SimilarFile>(gone);
            if(set.Count==0)return;
            Files=Files.Where(f=>!set.Contains(f)).ToList();
            Regroup();
        }
        // The person says these pictures aren't the same: remembered as pairs, so they aren't grouped again.
        internal static void NotSame(SimilarGroup group,SimilarFile only) {
            var pairs=new List<string>();
            for(int i=0;i<group.Files.Count;i++)for(int j=i+1;j<group.Files.Count;j++) {
                var a=group.Files[i];var b=group.Files[j];
                if(only!=null && a!=only && b!=only)continue;
                pairs.Add(SimilarCache.Pair(a,b));
            }
            SimilarCache.AddNotSame(store,pairs);
            notSame=SimilarCache.NotSame(store);
            Regroup();
        }
        // The groups a file is in, exact ones first.
        internal static List<SimilarGroup> GroupsOf(SimilarFile file) {
            return Exact.Concat(Near).Where(g=>g.Files.Contains(file)).OrderByDescending(g=>g.Exact).ToList();
        }

        // ----- Right before a file goes to the Recycle Bin -----
        // It must still be what was looked at, and another file of one of its groups must stay. Until the file is in the
        // Recycle Bin, it is held so nothing can write to it (opened with "delete allowed", so the Recycle Bin can still
        // take it), and the one that stays is held so nothing can delete, move or change it.
        // For exact copies the one that stays is compared byte for byte once more; the same file reached by two paths
        // (a link) is not a second copy.
        internal sealed class Hold : IDisposable {
            internal FileStream Going,Staying;
            // Right before the Recycle Bin: the path still leads to the very file that was checked (not one put there meanwhile).
            internal bool StillAt(string path) {
                try {
                    string held=Going==null?null:SimilarFiles.IdOf(Going);
                    if(held==null)return Going!=null;
                    using(var now=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))return SimilarFiles.IdOf(now)==held;
                } catch(Exception) {return false;}
            }
            public void Dispose() {if(Going!=null)Going.Dispose();if(Staying!=null)Staying.Dispose();Going=Staying=null;}
        }
        internal static Hold Lock(SimilarFile file,List<SimilarGroup> groups,ICollection<SimilarFile> picked) {
            if(groups==null || groups.Count==0)return null;
            var hold=new Hold();
            try {
                hold.Going=Open(file,FileShare.Read|FileShare.Delete);
                if(hold.Going==null){hold.Dispose();return null;}
                string self=SimilarFiles.IdOf(hold.Going);
                foreach(var group in groups) foreach(var other in group.Files) {
                    if(other==file || picked.Contains(other))continue;
                    var staying=Open(other,FileShare.Read);
                    if(staying==null)continue;
                    string id=SimilarFiles.IdOf(staying);
                    bool same=self!=null && self==id;
                    if(same || (group.Exact && !SimilarFiles.SameBytes(hold.Going,staying,CancellationToken.None))){staying.Dispose();continue;}
                    hold.Staying=staying;
                    return hold;
                }
            } catch(Exception) {}
            hold.Dispose();
            return null;
        }
        // Opened only when it is still the same size and has the same time as when it was looked at.
        static FileStream Open(SimilarFile file,FileShare share) {
            FileStream stream=null;
            try {
                stream=new FileStream(file.Path,FileMode.Open,FileAccess.Read,share);
                if(stream.Length==file.Size && File.GetLastWriteTimeUtc(file.Path).Ticks==file.Ticks)return stream;
            } catch(IOException) {} catch(UnauthorizedAccessException) {}
            if(stream!=null)stream.Dispose();
            return null;
        }

        // ----- Exact copies: the covers of a copy go to the file that stays -----
        // How many cover records (main and extra) and tags a file has.
        internal static int CoverCount(GalleryStore s,SimilarFile file) {
            try {return Mover.RecordsOf(s.AlbumFolderOf(file.Folder),file.Name).Count(r=>File.Exists(Path.Combine(r,"photo.xml")));}
            catch(Exception) {return 0;}
        }
        // A record folder that moved, and the name its record had, so it can go back.
        internal sealed class Moved {internal string From,To,OldName,OldOriginal;}
        // Moves every cover record of the copy to the file that stays: the first becomes its main cover when it has none,
        // the rest become extra covers. The cover files themselves stay where they are (the records name them).
        // On a problem, whatever moved goes back and the problem is passed on.
        internal static List<Moved> MoveCovers(GalleryStore s,SimilarFile copy,SimilarFile stays) {
            var moved=new List<Moved>();
            var records=Mover.RecordsOf(s.AlbumFolderOf(copy.Folder),copy.Name)
                .Where(r=>File.Exists(Path.Combine(r,"photo.xml"))).OrderBy(r=>Path.GetFileName(r).Length).ToList();
            if(records.Count==0)return moved;
            string album=s.OpenSubfolder(stays.Folder).Folder;
            string basis="p-"+GalleryStore.KeyOf(stays.Name);
            try {
                lock(SharedCoverFiles.MetadataLock) {
                    foreach(string record in records) {
                        string main=Path.Combine(album,basis);
                        string target=Directory.Exists(main)?Path.Combine(album,basis+"-"+Guid.NewGuid().ToString("N").Substring(0,10)):main;
                        string metadata=Path.Combine(record,"photo.xml");
                        var xml=XElement.Load(metadata);
                        string oldName=(string)xml.Attribute("name"),oldOriginal=(string)xml.Attribute("original");
                        Directory.Move(record,target);
                        moved.Add(new Moved {From=record,To=target,OldName=oldName,OldOriginal=oldOriginal});
                        Rewrite(Path.Combine(target,"photo.xml"),stays.Name);
                    }
                }
            } catch(Exception) {
                PutBack(moved);
                throw;
            }
            return moved;
        }
        // Whether every cover record of the copy really belongs to it (not to an earlier file that had its name).
        internal static bool CoversBelongTo(GalleryStore s,SimilarFile copy) {
            foreach(string record in Mover.RecordsOf(s.AlbumFolderOf(copy.Folder),copy.Name)) {
                if(!File.Exists(Path.Combine(record,"photo.xml")))continue;
                try {
                    var cover=GalleryStore.Read(record,copy.Path);
                    if(cover.HasCrop && !OriginalLocator.MatchesOriginal(cover,copy.Path,CancellationToken.None))return false;
                } catch(Exception) {return false;}
            }
            return true;
        }
        // Before the copy goes: the moved covers remember the file that stays as their original (a problem stops it all).
        internal static void Settle(List<Moved> moved,SimilarFile stays) {
            foreach(var m in moved)OriginalLocator.Remember(GalleryStore.Read(m.To,stays.Path),stays.Path,false,true);
        }
        // After PutBack: the covers remember the copy again.
        internal static void Unsettle(List<Moved> moved,SimilarFile copy) {
            foreach(var m in moved) {
                try {OriginalLocator.Remember(GalleryStore.Read(m.From,copy.Path),copy.Path);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}
            }
        }
        static void Rewrite(string metadata,string name,string original=null) {
            var xml=XElement.Load(metadata);
            xml.SetAttributeValue("name",name);
            xml.SetAttributeValue("original",original??name);
            string part=metadata+"."+Guid.NewGuid().ToString("N")+".tmp";
            try {xml.Save(part);File.Replace(part,metadata,null);}
            finally {try {File.Delete(part);} catch(Exception) {}}
        }
        // Undoes MoveCovers (the copy could not go to the Recycle Bin after all).
        internal static void PutBack(List<Moved> moved) {
            lock(SharedCoverFiles.MetadataLock) {
                for(int i=moved.Count-1;i>=0;i--) {
                    var m=moved[i];
                    try {
                        Directory.Move(m.To,m.From);
                        Rewrite(Path.Combine(m.From,"photo.xml"),m.OldName,m.OldOriginal);
                    } catch(Exception quiet) {ErrorLog.Quiet(quiet);}
                }
            }
        }
    }
}
