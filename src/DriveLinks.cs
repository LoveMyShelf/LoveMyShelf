using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Linq;

namespace LoveMyShelf {
    // The drives connected right now, known by the serial number each volume keeps when its letter changes.
    internal sealed class DriveLayout {
        internal readonly Dictionary<string,uint> SerialOfRoot=new Dictionary<string,uint>(StringComparer.OrdinalIgnoreCase);
        // Only serials that exactly one connected drive has: two clones can't be told apart, so neither is followed.
        internal readonly Dictionary<uint,string> RootOfSerial=new Dictionary<uint,string>();
        readonly HashSet<uint> shared=new HashSet<uint>();
        // Roots that are not connected but should still be recognized as roots.
        internal readonly List<string> OtherRoots=new List<string>();

        internal static string WithSlash(string root) {return root.EndsWith("\\")?root:root+"\\";}
        internal void Add(string root,uint serial) {
            root=WithSlash(root);
            SerialOfRoot[root]=serial;
            if(serial==0 || shared.Contains(serial))return;
            if(RootOfSerial.ContainsKey(serial)){RootOfSerial.Remove(serial);shared.Add(serial);return;}
            RootOfSerial[serial]=root;
        }
        internal static DriveLayout Current() {
            var layout=new DriveLayout();
            foreach(var drive in DriveInfo.GetDrives()) {
                try {
                    if(drive.DriveType!=DriveType.Fixed && drive.DriveType!=DriveType.Removable)continue;
                    uint serial=OriginalLocator.VolumeOf(drive.Name);
                    if(serial!=0)layout.Add(drive.Name,serial);
                } catch(IOException) {} catch(UnauthorizedAccessException) {}
            }
            return layout;
        }
        internal string RootOf(string path) {
            string best=null;
            foreach(string root in SerialOfRoot.Keys.Concat(OtherRoots)) {
                string plain=WithSlash(root);
                if((path+"\\").StartsWith(plain,StringComparison.OrdinalIgnoreCase) && (best==null || plain.Length>best.Length))best=plain;
            }
            return best??WithSlash(Path.GetPathRoot(path));
        }
        internal string Relative(string path,string root) {
            return path.Length<=root.Length?"":path.Substring(root.Length).TrimStart('\\');
        }
        internal static string Join(string root,string relative) {return relative.Length==0?root:Path.Combine(root,relative);}
    }

    internal sealed class RelinkResult {
        internal int Records,Files;
        // Old record folder → new record folder and its real folder, for folders that followed their drive.
        internal readonly Dictionary<string,Tuple<string,string>> Albums=new Dictionary<string,Tuple<string,string>>(StringComparer.OrdinalIgnoreCase);
        internal string CoverDirectory;
        internal bool Changed {get {return Records>0 || Files>0 || Albums.Count>0 || CoverDirectory!=null;}}
    }

    // Keeps covers attached to their files when external drives come and go. Records are filed under the folder's
    // full path, so a new drive letter would otherwise look like a different folder. Nothing here touches the
    // originals or the cover files themselves; only Love My Shelf's own records are moved or corrected.
    internal static class DriveLinks {
        const string HoldingFolder="records-waiting-for-drive",Ticket="relink.xml",VolumeFile="volume.txt",CoverVolumeFile="cover-volume.txt";
        const string HiddenTabFile="hide-folder-tab";
        static readonly object running=new object();
        static readonly StringComparer Paths=StringComparer.OrdinalIgnoreCase;

        // True when the drive this path is on isn't connected right now.
        internal static bool DriveAway(string path) {
            try {
                string root=Path.GetPathRoot(path);
                return !String.IsNullOrEmpty(root) && !Directory.Exists(root);
            } catch(ArgumentException) {return false;}
        }

        sealed class Plan {
            public string Record,From,Name,Relative;
            public uint Volume;
            public bool DriveMove;
        }

        internal static RelinkResult Run(GalleryStore store,DriveLayout drives,CancellationToken token) {
            lock(running) {
                var result=new RelinkResult();
                var visible=new HashSet<string>(store.VisibleAlbums().Select(a=>a.Folder),Paths);
                PlaceHeld(store,drives,result,token); // anything left from an interrupted run, or a drive that came back
                var albums=store.Albums().Where(a=>a.Source!=null && !a.Source.StartsWith(@"\\")).ToList();
                var plans=new List<Plan>();
                foreach(var album in albums) {
                    token.ThrowIfCancellationRequested();
                    try {PlanAlbum(album,drives,plans,token);}
                    catch(IOException) {} catch(UnauthorizedAccessException) {}
                }
                foreach(var plan in plans) {
                    token.ThrowIfCancellationRequested();
                    try {Hold(store,plan);}
                    catch(IOException) {} catch(UnauthorizedAccessException) {} // in use: the next run tries again
                }
                if(plans.Count>0)PlaceHeld(store,drives,result,token);
                FollowEmptyFolders(store,albums,visible,drives,result);
                RememberVolumes(albums,drives,result);
                MoveTabs(store,visible,result);
                RepointCoverFiles(store,drives,result,token);
                return result;
            }
        }

        static void PlanAlbum(Album album,DriveLayout drives,List<Plan> plans,CancellationToken token) {
            string root=drives.RootOf(album.Source),relative=drives.Relative(album.Source,root);
            uint here;
            bool rootKnown=drives.SerialOfRoot.TryGetValue(root,out here);
            foreach(string record in Directory.GetDirectories(album.Folder,"p-*")) {
                token.ThrowIfCancellationRequested();
                string name=RecordName(record);
                if(name==null)continue;
                string atHome=Path.Combine(album.Source,name),saved;
                uint volume;long length;
                if(!OriginalLocator.Facts(record,out saved,out volume,out length)) {
                    // A record from an old version: learn which drive it is on while the file is here.
                    if(rootKnown && File.Exists(atHome)) {
                        try {OriginalLocator.Remember(GalleryStore.Read(record,atHome),atHome,true);} catch(Exception) {}
                    }
                    continue;
                }
                if(volume==0)continue;
                if(rootKnown && here==volume) {
                    if(File.Exists(atHome))continue;
                    AddFound(record,album,name,volume,OriginalLocator.FoundById(record)??OriginalLocator.FoundBeside(record,album.Source),atHome,drives,plans);
                    continue;
                }
                string home;
                if(drives.RootOfSerial.TryGetValue(volume,out home)) {
                    // Same drive, new letter.
                    string folder=DriveLayout.Join(home,relative);
                    bool sameFolder=Paths.Equals(drives.Relative(Path.GetDirectoryName(saved),drives.RootOf(saved)),relative);
                    // The record was last matched to a copy in some other folder, and this folder still has its file: it stays.
                    if(!sameFolder && File.Exists(atHome))continue;
                    if(sameFolder && Directory.Exists(folder)) {
                        string savedName=Path.GetFileName(saved);
                        string fileName=File.Exists(Path.Combine(folder,savedName))?savedName:name;
                        if(File.Exists(Path.Combine(folder,fileName))) {
                            plans.Add(new Plan {Record=record,From=album.Folder,Name=fileName,Relative=relative,Volume=volume,DriveMove=true});
                            continue;
                        }
                    }
                    string found=OriginalLocator.FoundById(record);
                    if(found==null && Directory.Exists(folder))found=OriginalLocator.FoundBeside(record,folder);
                    AddFound(record,album,name,volume,found,atHome,drives,plans);
                    continue;
                }
                // Its drive is away and another drive now has this letter. A different file with the same name
                // must not wear this cover, so the record waits until its own drive is back.
                if(rootKnown && File.Exists(atHome)) {
                    long now;
                    try {now=new FileInfo(atHome).Length;} catch(IOException) {continue;} catch(UnauthorizedAccessException) {continue;}
                    if(now!=length)plans.Add(new Plan {Record=record,From=album.Folder,Name=name,Relative=relative,Volume=volume});
                }
            }
        }
        // The original was renamed or moved outside Love My Shelf and was found again on the same drive.
        static void AddFound(string record,Album album,string name,uint volume,string found,string atHome,DriveLayout drives,List<Plan> plans) {
            if(found==null || Paths.Equals(found,atHome))return;
            string folder=Path.GetDirectoryName(found),root=drives.RootOf(found);
            string oldRoot=drives.RootOf(album.Source);
            string relative=drives.Relative(folder,root);
            bool sameFolder=Paths.Equals(relative,drives.Relative(album.Source,oldRoot));
            plans.Add(new Plan {Record=record,From=album.Folder,Name=Path.GetFileName(found),Relative=relative,Volume=volume,
                DriveMove=sameFolder && !Paths.Equals(root,oldRoot)});
        }
        static string RecordName(string record) {
            try {
                string file=Path.Combine(record,"photo.xml");
                if(!File.Exists(file))return null;
                lock(SharedCoverFiles.MetadataLock)return Path.GetFileName((string)XElement.Load(file).Attribute("name"));
            } catch(IOException) {return null;} catch(UnauthorizedAccessException) {return null;}
            catch(System.Xml.XmlException) {return null;} catch(ArgumentException) {return null;}
        }

        // A record moves in two steps through a waiting folder, so two drives that swapped letters can't collide.
        // The ticket inside says where it belongs, so an interrupted run simply continues next time.
        static void Hold(GalleryStore store,Plan plan) {
            string holding=Path.Combine(store.Root,HoldingFolder);
            Directory.CreateDirectory(holding);
            string folderName=Path.GetFileName(plan.Record);
            string ticket=Path.Combine(plan.Record,Ticket);
            new XElement("relink",new XAttribute("volume",plan.Volume),new XAttribute("relative",plan.Relative),new XAttribute("name",plan.Name),
                new XAttribute("suffix",folderName.Length>34?folderName.Substring(34):""),new XAttribute("from",plan.From),
                new XAttribute("drive",plan.DriveMove?"1":"0")).Save(ticket);
            try {
                lock(SharedCoverFiles.MetadataLock)Directory.Move(plan.Record,Path.Combine(holding,Guid.NewGuid().ToString("N")));
            } catch {
                try {File.Delete(ticket);} catch(Exception) {}
                throw;
            }
        }
        static void PlaceHeld(GalleryStore store,DriveLayout drives,RelinkResult result,CancellationToken token) {
            string holding=Path.Combine(store.Root,HoldingFolder);
            if(!Directory.Exists(holding))return;
            foreach(string held in Directory.GetDirectories(holding)) {
                token.ThrowIfCancellationRequested();
                try {
                    var ticket=XElement.Load(Path.Combine(held,Ticket));
                    uint volume=(uint)ticket.Attribute("volume");
                    string home;
                    if(!drives.RootOfSerial.TryGetValue(volume,out home))continue; // its drive is still away
                    string relative=(string)ticket.Attribute("relative"),name=(string)ticket.Attribute("name");
                    string source=DriveLayout.Join(home,relative);
                    if(!Directory.Exists(source))continue;
                    var album=store.OpenSubfolder(source);
                    string basis="p-"+GalleryStore.KeyOf(name),suffix=(string)ticket.Attribute("suffix")??"";
                    string target=Path.Combine(album.Folder,basis+suffix);
                    // A cover made meanwhile keeps its place; this one joins it as another cover of the same file.
                    if(Directory.Exists(target))target=Path.Combine(album.Folder,basis+"-"+Guid.NewGuid().ToString("N").Substring(0,10));
                    SetName(held,name);
                    OriginalLocator.MovePath(held,Path.Combine(source,name));
                    lock(SharedCoverFiles.MetadataLock)Directory.Move(held,target);
                    try {File.Delete(Path.Combine(target,Ticket));} catch(Exception) {}
                    result.Records++;
                    string from=(string)ticket.Attribute("from");
                    if((string)ticket.Attribute("drive")=="1" && from!=null && !Paths.Equals(from,album.Folder))
                        result.Albums[from]=Tuple.Create(album.Folder,album.Source);
                } catch(IOException) {} catch(UnauthorizedAccessException) {}
                catch(System.Xml.XmlException) {} catch(FormatException) {} catch(ArgumentException) {}
            }
        }
        static void SetName(string record,string name) {
            string metadata=Path.Combine(record,"photo.xml");
            lock(SharedCoverFiles.MetadataLock) {
                var xml=XElement.Load(metadata);
                if((string)xml.Attribute("name")==name)return;
                xml.SetAttributeValue("name",name);xml.SetAttributeValue("original",name);
                SaveOver(xml,metadata);
            }
        }
        static void SaveOver(XElement xml,string file) {
            string part=file+"."+Guid.NewGuid().ToString("N")+".tmp";
            try {xml.Save(part);File.Replace(part,file,null);}
            finally {try {File.Delete(part);} catch(Exception) {}}
        }

        // A shelf folder without any covers has no record to follow, so its drive's number is kept beside it.
        static void FollowEmptyFolders(GalleryStore store,List<Album> albums,HashSet<string> visible,DriveLayout drives,RelinkResult result) {
            foreach(var album in albums) {
                try {
                    if(!visible.Contains(album.Folder) || result.Albums.ContainsKey(album.Folder))continue;
                    if(Directory.GetDirectories(album.Folder,"p-*").Length>0)continue;
                    string file=Path.Combine(album.Folder,VolumeFile);
                    uint volume;
                    if(!File.Exists(file) || !UInt32.TryParse(File.ReadAllText(file).Trim(),out volume) || volume==0)continue;
                    string home,root=drives.RootOf(album.Source);
                    uint here;
                    if(drives.SerialOfRoot.TryGetValue(root,out here) && here==volume)continue;
                    if(!drives.RootOfSerial.TryGetValue(volume,out home))continue;
                    string source=DriveLayout.Join(home,drives.Relative(album.Source,root));
                    if(!Directory.Exists(source))continue;
                    var moved=store.OpenSubfolder(source);
                    if(!Paths.Equals(moved.Folder,album.Folder))result.Albums[album.Folder]=Tuple.Create(moved.Folder,moved.Source);
                } catch(IOException) {} catch(UnauthorizedAccessException) {}
            }
        }
        static void RememberVolumes(List<Album> albums,DriveLayout drives,RelinkResult result) {
            foreach(var old in result.Albums.Keys)
                try {File.Delete(Path.Combine(old,VolumeFile));} catch(Exception) {}
            foreach(var moved in result.Albums.Values) {
                try {
                    uint serial;
                    if(drives.SerialOfRoot.TryGetValue(drives.RootOf(moved.Item2),out serial) && serial!=0)
                        File.WriteAllText(Path.Combine(moved.Item1,VolumeFile),serial.ToString());
                } catch(IOException) {} catch(UnauthorizedAccessException) {}
            }
            foreach(var album in albums) {
                try {
                    string file=Path.Combine(album.Folder,VolumeFile);
                    if(File.Exists(file) || result.Albums.ContainsKey(album.Folder) || !Directory.Exists(album.Source))continue;
                    uint serial;
                    if(drives.SerialOfRoot.TryGetValue(drives.RootOf(album.Source),out serial) && serial!=0)
                        File.WriteAllText(file,serial.ToString());
                } catch(IOException) {} catch(UnauthorizedAccessException) {}
            }
        }
        // The folder keeps its place among the tabs at its new address.
        static void MoveTabs(GalleryStore store,HashSet<string> visible,RelinkResult result) {
            if(result.Albums.Count==0)return;
            var shown=new HashSet<string>(Paths);
            foreach(var pair in result.Albums)
                if(visible.Contains(pair.Key))shown.Add(pair.Value.Item1);
            foreach(string folder in shown)
                try {File.Delete(Path.Combine(folder,HiddenTabFile));} catch(Exception) {}
            foreach(var pair in result.Albums) {
                if(!visible.Contains(pair.Key) || shown.Contains(pair.Key))continue;
                try {
                    if(Directory.GetDirectories(pair.Key,"p-*").Length==0)File.WriteAllText(Path.Combine(pair.Key,HiddenTabFile),"");
                } catch(IOException) {} catch(UnauthorizedAccessException) {}
            }
            var names=result.Albums.ToDictionary(p=>Path.GetFileName(p.Key),p=>Path.GetFileName(p.Value.Item1),Paths);
            try {
                string order=Path.Combine(store.Root,"tab-order.txt");
                if(File.Exists(order)) {
                    var lines=File.ReadAllLines(order).Select(l=>names.ContainsKey(l)?names[l]:l).Distinct(Paths).ToArray();
                    string part=order+".tmp";File.WriteAllLines(part,lines);File.Replace(part,order,null);
                }
                string last=Path.Combine(store.Root,"last.txt");
                if(File.Exists(last)) {
                    string now=File.ReadAllText(last).Trim();
                    if(names.ContainsKey(now))File.WriteAllText(last,names[now]);
                }
            } catch(IOException) {} catch(UnauthorizedAccessException) {}
        }

        // Cover files can live on any drive. Their names carry a unique tag, so the same path on another drive is the same file.
        static void RepointCoverFiles(GalleryStore store,DriveLayout drives,RelinkResult result,CancellationToken token) {
            var folderThere=new Dictionary<string,bool>(Paths);
            var moves=new Dictionary<string,HashSet<string>>(Paths);
            foreach(var album in store.Albums()) {
                string[] records;
                try {records=Directory.GetDirectories(album.Folder,"p-*");}
                catch(IOException) {continue;} catch(UnauthorizedAccessException) {continue;}
                foreach(string record in records) {
                    token.ThrowIfCancellationRequested();
                    try {
                        string metadata=Path.Combine(record,"photo.xml");
                        if(!File.Exists(metadata))continue;
                        lock(SharedCoverFiles.MetadataLock) {
                            var xml=XElement.Load(metadata);
                            bool changed=false;
                            foreach(string attribute in new[]{"coverFile","upscaleFile","motionFile"}) {
                                string value=(string)xml.Attribute(attribute);
                                if(String.IsNullOrEmpty(value) || File.Exists(value))continue;
                                string oldRoot=drives.RootOf(value);
                                string found=FindOnOtherDrive(value,oldRoot,record,drives,folderThere);
                                if(found==null)continue;
                                xml.SetAttributeValue(attribute,found);changed=true;
                                HashSet<string> targets;
                                if(!moves.TryGetValue(oldRoot,out targets))moves[oldRoot]=targets=new HashSet<string>(Paths);
                                targets.Add(drives.RootOf(found));
                            }
                            if(changed){SaveOver(xml,metadata);result.Files++;}
                        }
                    } catch(IOException) {} catch(UnauthorizedAccessException) {}
                    catch(System.Xml.XmlException) {} catch(ArgumentException) {}
                }
            }
            FollowCoverFolder(store,drives,moves,result);
        }
        static string FindOnOtherDrive(string file,string oldRoot,string record,DriveLayout drives,Dictionary<string,bool> folderThere) {
            string relative=drives.Relative(file,oldRoot);
            var hits=new List<string>();
            foreach(string root in drives.SerialOfRoot.Keys) {
                if(Paths.Equals(root,oldRoot))continue;
                string candidate=DriveLayout.Join(root,relative),folder=Path.GetDirectoryName(candidate);
                bool there;
                if(!folderThere.TryGetValue(folder,out there))folderThere[folder]=there=Directory.Exists(folder);
                if(there && File.Exists(candidate))hits.Add(candidate);
            }
            if(hits.Count==1)return hits[0];
            if(hits.Count==0)return null;
            // A backup drive may hold the same cover: the drive of this cover's original wins.
            string saved;uint volume;long length;
            if(!OriginalLocator.Facts(record,out saved,out volume,out length))return null;
            string home;
            if(!drives.RootOfSerial.TryGetValue(volume,out home))return null;
            return hits.FirstOrDefault(h=>h.StartsWith(home,StringComparison.OrdinalIgnoreCase));
        }
        static void FollowCoverFolder(GalleryStore store,DriveLayout drives,Dictionary<string,HashSet<string>> moves,RelinkResult result) {
            string options=Path.Combine(store.Root,"options.xml"),memory=Path.Combine(store.Root,CoverVolumeFile);
            try {
                if(!File.Exists(options))return;
                var xml=XElement.Load(options);
                string folder=(string)xml.Attribute("coverDirectory");
                if(String.IsNullOrWhiteSpace(folder))return;
                string root=drives.RootOf(folder);
                uint serial;
                if(Directory.Exists(folder)) {
                    if(drives.SerialOfRoot.TryGetValue(root,out serial) && serial!=0)File.WriteAllText(memory,serial.ToString());
                    return;
                }
                string newRoot=null;
                HashSet<string> targets;
                if(moves.TryGetValue(root,out targets) && targets.Count==1)newRoot=targets.First();
                else if(File.Exists(memory) && UInt32.TryParse(File.ReadAllText(memory).Trim(),out serial))
                    drives.RootOfSerial.TryGetValue(serial,out newRoot);
                if(newRoot==null || Paths.Equals(newRoot,root))return;
                string moved=DriveLayout.Join(newRoot,drives.Relative(folder,root));
                if(!Directory.Exists(moved))return;
                xml.SetAttributeValue("coverDirectory",moved);
                SaveOver(xml,options);
                result.CoverDirectory=moved;
            } catch(IOException) {} catch(UnauthorizedAccessException) {} catch(System.Xml.XmlException) {}
        }
    }
}
