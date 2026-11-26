using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml.Linq;

namespace LoveMyShelf {
    // Backup settings and what this computer remembers about its backups. Kept in %LOCALAPPDATA%\LoveMyShelf\backup.xml,
    // outside the records, so a restore never brings another computer's settings along.
    // The password is never kept: only a check value made the same slow way as the backup's key (Argon2id), so the app
    // can say "this is the same password as last time" without opening a file in the cloud.
    internal sealed class BackupSettings {
        internal string Destination;          // the folder backups go into
        internal string Keep="month";         // "1", "2", "month", "3months"
        internal int IntervalDays=7;
        internal DateTime LastBackup;         // UTC, MinValue = never
        internal DateTime RemindAfter;        // UTC: "tell me tomorrow"
        internal bool BoxOn;
        internal List<string> BoxFolders=new List<string>();
        internal string CheckSalt,CheckValue; // base64
        internal string Epoch;                // changes when the password changes; boxes made with another one are made again
        internal List<Made> Backups=new List<Made>();
        internal List<Box> Boxes=new List<Box>();
        internal bool Configured {get {return !String.IsNullOrEmpty(Destination);}}
        internal sealed class Made {internal string File,Id;internal DateTime Created;internal int Missing;}
        internal sealed class Box {
            internal string Id,Source,Epoch,Listing;
            internal int Parts,Files;internal long Size,Largest;
        }

        static string PathOf {get {return Path.Combine(Path.GetDirectoryName(Backup.StoreRoot),"backup.xml");}}
        static readonly CultureInfo Inv=CultureInfo.InvariantCulture;
        internal static BackupSettings Load() {
            var s=new BackupSettings();
            try {
                if(!File.Exists(PathOf))return s;
                var x=XElement.Load(PathOf);
                s.Destination=(string)x.Attribute("destination");
                s.Keep=(string)x.Attribute("keep")??"month";
                s.IntervalDays=Math.Max(1,(int?)x.Attribute("days")??7);
                s.LastBackup=Time((string)x.Attribute("last"));
                s.RemindAfter=Time((string)x.Attribute("remindAfter"));
                s.BoxOn=(bool?)x.Attribute("box")??false;
                s.CheckSalt=(string)x.Attribute("checkSalt");s.CheckValue=(string)x.Attribute("check");
                s.Epoch=(string)x.Attribute("epoch");
                s.BoxFolders=x.Elements("boxFolder").Select(e=>e.Value).Where(v=>v.Length>0).ToList();
                s.Backups=x.Elements("made").Select(e=>new Made {File=(string)e.Attribute("file"),Id=(string)e.Attribute("id"),
                    Created=Time((string)e.Attribute("created")),Missing=(int?)e.Attribute("missing")??0}).Where(m=>m.File!=null).ToList();
                s.Boxes=x.Elements("box").Select(e=>new Box {Id=(string)e.Attribute("id"),Source=(string)e.Attribute("source"),
                    Epoch=(string)e.Attribute("epoch"),Listing=(string)e.Attribute("listing"),Parts=(int?)e.Attribute("parts")??0,
                    Files=(int?)e.Attribute("files")??0,Size=(long?)e.Attribute("size")??0,Largest=(long?)e.Attribute("largest")??0})
                    .Where(b=>b.Id!=null && b.Source!=null).ToList();
            } catch(Exception quiet) {ErrorLog.Quiet(quiet);}
            return s;
        }
        static DateTime Time(string text) {
            DateTime t;
            return text!=null && DateTime.TryParse(text,Inv,DateTimeStyles.AdjustToUniversal|DateTimeStyles.AssumeUniversal,out t)?t:DateTime.MinValue;
        }
        static string Text(DateTime t) {return t==DateTime.MinValue?null:t.ToString("o",Inv);}
        internal void Save() {
            var x=new XElement("backup",
                Destination==null?null:new XAttribute("destination",Destination),new XAttribute("keep",Keep),new XAttribute("days",IntervalDays),
                Text(LastBackup)==null?null:new XAttribute("last",Text(LastBackup)),
                Text(RemindAfter)==null?null:new XAttribute("remindAfter",Text(RemindAfter)),
                new XAttribute("box",BoxOn),
                CheckSalt==null?null:new XAttribute("checkSalt",CheckSalt),CheckValue==null?null:new XAttribute("check",CheckValue),
                Epoch==null?null:new XAttribute("epoch",Epoch),
                BoxFolders.Select(f=>new XElement("boxFolder",f)),
                Backups.Select(m=>new XElement("made",new XAttribute("file",m.File),new XAttribute("id",m.Id??""),
                    new XAttribute("created",Text(m.Created)??""),new XAttribute("missing",m.Missing))),
                Boxes.Select(b=>new XElement("box",new XAttribute("id",b.Id),new XAttribute("source",b.Source),new XAttribute("epoch",b.Epoch??""),
                    new XAttribute("listing",b.Listing??""),new XAttribute("parts",b.Parts),new XAttribute("files",b.Files),
                    new XAttribute("size",b.Size),new XAttribute("largest",b.Largest))));
            string part=PathOf+"."+Guid.NewGuid().ToString("N")+".tmp";
            try {
                x.Save(part);
                if(File.Exists(PathOf))File.Replace(part,PathOf,null);else File.Move(part,PathOf);
            } finally {try {File.Delete(part);} catch(Exception) {}}
        }
        // Whether this is the password of the last backup. Null when no backup was made on this computer yet.
        internal bool? SamePassword(string password) {
            if(CheckSalt==null || CheckValue==null)return null;
            try {
                byte[] made=BackupCrypto.Argon2id(BackupCrypto.PasswordBytes(password),Convert.FromBase64String(CheckSalt),
                    BackupFile.MemoryKiB,BackupFile.Passes,BackupFile.Lanes,32,null,Encoding.ASCII.GetBytes("LMS password check"));
                return Convert.ToBase64String(made)==CheckValue;
            } catch(FormatException) {return null;}
        }
        internal void RememberPassword(string password) {
            byte[] salt=BackupCrypto.Random(16);
            byte[] made=BackupCrypto.Argon2id(BackupCrypto.PasswordBytes(password),salt,
                BackupFile.MemoryKiB,BackupFile.Passes,BackupFile.Lanes,32,null,Encoding.ASCII.GetBytes("LMS password check"));
            CheckSalt=Convert.ToBase64String(salt);CheckValue=Convert.ToBase64String(made);
        }
        internal bool Due {
            get {
                if(DateTime.UtcNow<RemindAfter)return false;
                return LastBackup==DateTime.MinValue || DateTime.UtcNow-LastBackup>=TimeSpan.FromDays(IntervalDays);
            }
        }
    }

    // Making a backup and putting one back.
    internal static class Backup {
        // The checks point this at a folder of made-up records.
        internal static string RootForChecks;
        internal static string StoreRoot {
            get {if(RootForChecks!=null)return RootForChecks;return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LoveMyShelf","Folders");}
        }
        internal const string BoxFolderName="Love My Shelf 사진 상자";
        static readonly string[] CoverAttributes={"coverFile","upscaleFile","motionFile"};
        // Files right in the records folder that are worth keeping (the rest are caches, queues or this PC's state).
        static readonly string[] RootFiles={"tab-order.txt","options.xml","face-likeness.txt",@"similar\not-same.txt"};

        // ---------------- Where backups can go ----------------
        internal sealed class Place {internal string Name,Path;internal bool Cloud;}
        internal static List<Place> CloudPlaces() {
            var list=new List<Place>();
            var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Action<string,string> add=(name,root)=>{
                if(String.IsNullOrEmpty(root) || !Directory.Exists(root))return;
                string path=System.IO.Path.Combine(root,"Love My Shelf 백업");
                if(seen.Add(path))list.Add(new Place {Name=name,Path=path,Cloud=true});
            };
            // Google Drive for desktop shows a drive ("Google Drive") with "My Drive" (내 드라이브) inside.
            try {
                foreach(var drive in DriveInfo.GetDrives()) {
                    try {
                        if(!drive.IsReady)continue;
                        if(drive.VolumeLabel.IndexOf("Google Drive",StringComparison.OrdinalIgnoreCase)<0)continue;
                        foreach(string inside in new[]{"내 드라이브","My Drive"})
                            add("Google Drive",System.IO.Path.Combine(drive.RootDirectory.FullName,inside));
                    } catch(IOException) {} catch(UnauthorizedAccessException) {}
                }
            } catch(IOException) {} catch(UnauthorizedAccessException) {}
            string profile=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach(string folder in new[]{"Google Drive","내 드라이브","My Drive"})add("Google Drive",System.IO.Path.Combine(profile,folder));
            add("OneDrive",Environment.GetEnvironmentVariable("OneDrive"));
            add("OneDrive",Environment.GetEnvironmentVariable("OneDriveConsumer"));
            return list;
        }
        // A folder that a cloud app keeps in step with the internet (it may still be uploading what was just saved).
        internal static bool IsCloud(string path) {
            if(String.IsNullOrEmpty(path))return false;
            foreach(string v in new[]{"OneDrive","OneDriveConsumer","OneDriveCommercial"}) {
                string root=Environment.GetEnvironmentVariable(v);
                if(!String.IsNullOrEmpty(root) && Inside(path,root))return true;
            }
            try {
                var drive=new DriveInfo(System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(path)));
                if(drive.IsReady && drive.VolumeLabel.IndexOf("Google Drive",StringComparison.OrdinalIgnoreCase)>=0)return true;
            } catch(Exception) {}
            string lower=path.ToLowerInvariant();
            return lower.Contains(@"\google drive") || lower.Contains(@"\내 드라이브") || lower.Contains(@"\my drive") || lower.Contains(@"\dropbox");
        }
        internal static bool Inside(string path,string folder) {
            try {
                string p=System.IO.Path.GetFullPath(path).TrimEnd('\\')+"\\",f=System.IO.Path.GetFullPath(folder).TrimEnd('\\')+"\\";
                return p.StartsWith(f,StringComparison.OrdinalIgnoreCase);
            } catch(Exception) {return false;}
        }
        // The part of path below folder ("2024\\Jeju"), or null when it isn't below it or would step out.
        internal static string Below(string path,string folder) {
            try {
                string p=System.IO.Path.GetFullPath(path).TrimEnd('\\'),f=System.IO.Path.GetFullPath(folder).TrimEnd('\\');
                if(!p.StartsWith(f+"\\",StringComparison.OrdinalIgnoreCase))return null;
                string rest=p.Substring(f.Length+1);
                return rest.Split('\\').Any(part=>part=="." || part=="..")?null:rest;
            } catch(Exception) {return null;}
        }
        // A cloud file that is only a placeholder here: opening it would download it first.
        internal static bool OnlyInCloud(string file) {
            try {
                var attributes=(int)File.GetAttributes(file);
                return (attributes&(0x400000|0x40000|0x1000))!=0;
            } catch(Exception) {return false;}
        }

        // ---------------- Making a backup ----------------
        internal sealed class Progress {
            internal string Step;internal double Fraction=-1;internal string Detail;
        }
        internal sealed class Result {
            internal string File;internal int Folders,Covers,Tags,Records;internal long Bytes;
            internal List<string> Missing=new List<string>();
            internal int BoxesMade,BoxesKept;internal long BoxBytes;internal List<string> BoxProblems=new List<string>();
            internal bool Cloud;internal int Removed,ProtectedOld;
        }
        sealed class Item {internal string Name,Source;internal long Length;}

        static int CountTags(string tagsFile) {
            try {
                if(!File.Exists(tagsFile))return 0;
                return XElement.Load(tagsFile).Elements("file").Sum(f=>f.Elements("tag").Count());
            } catch(Exception) {return 0;}
        }
        static string Sha(string path,out long length) {
            using(var f=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
            using(var sha=SHA256.Create()) {
                length=f.Length;
                return BitConverter.ToString(sha.ComputeHash(f)).Replace("-","").ToLowerInvariant();
            }
        }
        static bool DriveThere(string path) {
            try {return Directory.Exists(System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(path)));} catch(Exception) {return false;}
        }

        // Puts everything together, writes it locked and checked, and only then shows it under its real name.
        internal static Result Make(BackupSettings settings,string password,bool samePassword,Action<Progress> report,CancellationToken token) {
            string root=StoreRoot;
            var result=new Result();
            string id=Guid.NewGuid().ToString("N").Substring(0,12);
            Directory.CreateDirectory(settings.Destination);
            result.Cloud=IsCloud(settings.Destination);
            report(new Progress {Step=L.P("Gathering your records","기록을 모으고 있어요")});
            var items=new List<Item>();
            var folders=new XElement("folders");
            var covers=new Dictionary<string,XElement>(StringComparer.Ordinal);
            var coverRefs=new XElement("coverRefs");
            var missing=new XElement("missing");
            int number=0;
            foreach(string folder in Directory.GetDirectories(root,"folder-*").OrderBy(f=>f,StringComparer.Ordinal)) {
                token.ThrowIfCancellationRequested();
                string sourceFile=System.IO.Path.Combine(folder,"source.txt");
                if(!File.Exists(sourceFile) || !File.Exists(System.IO.Path.Combine(folder,"album.txt")))continue;
                string fid="f"+(++number).ToString(CultureInfo.InvariantCulture);
                string source;
                try {source=File.ReadAllText(sourceFile).Trim();} catch(IOException) {continue;}
                var samples=new XElement("samples");
                int records=0;
                foreach(string file in Directory.GetFiles(folder,"*",SearchOption.AllDirectories)) {
                    if(file.EndsWith(".tmp",StringComparison.OrdinalIgnoreCase))continue;
                    string rel=file.Substring(folder.Length+1);
                    // Half-copied records of a restore that stopped ("restore-….tmp") aren't records.
                    if(rel.Split('\\').Take(rel.Split('\\').Length-1).Any(d=>d.EndsWith(".tmp",StringComparison.OrdinalIgnoreCase)))continue;
                    long length;
                    try {length=new FileInfo(file).Length;} catch(IOException) {continue;}
                    items.Add(new Item {Name="r/"+fid+"/"+rel.Replace('\\','/'),Source=file,Length=length});
                    string fileName=System.IO.Path.GetFileName(file);
                    if(fileName=="photo.xml") {
                        records++;
                        try {
                            var xml=XElement.Load(file);
                            foreach(string attribute in CoverAttributes) {
                                string cover=(string)xml.Attribute(attribute);
                                if(String.IsNullOrEmpty(cover))continue;
                                string recordDir=System.IO.Path.GetDirectoryName(rel);
                                if(!File.Exists(cover)) {
                                    // A GIF cover or upscale may simply not have been made; only a missing JPG cover counts.
                                    if(attribute=="coverFile")missing.Add(new XElement("cover",new XAttribute("path",cover),
                                        new XAttribute("why",DriveThere(cover)?"gone":"drive")));
                                    continue;
                                }
                                long length2;string sha;
                                try {sha=Sha(cover,out length2);}
                                catch(IOException) {missing.Add(new XElement("cover",new XAttribute("path",cover),new XAttribute("why","unreadable")));continue;}
                                catch(UnauthorizedAccessException) {missing.Add(new XElement("cover",new XAttribute("path",cover),new XAttribute("why","unreadable")));continue;}
                                string ext=System.IO.Path.GetExtension(cover).ToLowerInvariant();
                                if(ext.Length>8 || ext.Length==0)ext=".bin";
                                if(!covers.ContainsKey(sha)) {
                                    covers[sha]=new XElement("cover",new XAttribute("sha",sha),new XAttribute("ext",ext),new XAttribute("length",length2));
                                    items.Add(new Item {Name="c/"+sha+ext,Source=cover,Length=length2});
                                }
                                coverRefs.Add(new XElement("ref",new XAttribute("folder",fid),new XAttribute("record",recordDir.Replace('\\','/')),
                                    new XAttribute("attribute",attribute),new XAttribute("path",cover),new XAttribute("sha",sha),
                                    new XAttribute("ticks",File.GetLastWriteTimeUtc(cover).Ticks)));
                            }
                        } catch(System.Xml.XmlException) {} catch(IOException) {} catch(UnauthorizedAccessException) {}
                    } else if(fileName=="origin.xml" && samples.Elements().Count()<12) {
                        try {
                            var o=XElement.Load(file);
                            string p=(string)o.Attribute("path");
                            if(p!=null)samples.Add(new XElement("file",new XAttribute("name",System.IO.Path.GetFileName(p)),
                                new XAttribute("length",(long?)o.Attribute("length")??0)));
                        } catch(Exception) {}
                    }
                }
                string album="";
                try {album=File.ReadAllText(System.IO.Path.Combine(folder,"album.txt"));} catch(IOException) {}
                folders.Add(new XElement("folder",new XAttribute("id",fid),new XAttribute("key",System.IO.Path.GetFileName(folder)),
                    new XAttribute("source",source),new XAttribute("album",album),new XAttribute("records",records),
                    new XAttribute("tags",CountTags(System.IO.Path.Combine(folder,"tags.xml"))),
                    new XAttribute("tab",!File.Exists(System.IO.Path.Combine(folder,GalleryStore.HiddenTabFile))),samples));
                result.Folders++;result.Records+=records;result.Tags+=CountTags(System.IO.Path.Combine(folder,"tags.xml"));
            }
            foreach(string name in RootFiles) {
                string file=System.IO.Path.Combine(root,name);
                if(File.Exists(file))items.Add(new Item {Name="root/"+name.Replace('\\','/'),Source=file,Length=new FileInfo(file).Length});
            }
            result.Covers=covers.Count;
            result.Missing=missing.Elements().Select(e=>(string)e.Attribute("path")).ToList();

            // The photo boxes come first: the backup's list points at boxes that were already checked.
            var boxList=new XElement("boxes");
            // Boxes are tied to the password they were locked with (its "epoch"). A new password gets a new epoch, kept
            // only once the whole backup is done, so a stopped backup never mixes boxes of two passwords.
            string epoch=samePassword && !String.IsNullOrEmpty(settings.Epoch)?settings.Epoch:Guid.NewGuid().ToString("N").Substring(0,8);
            if(settings.BoxOn && settings.BoxFolders.Count>0)
                PhotoBox.MakeAll(settings,password,epoch,folders,boxList,result,report,token);

            var manifest=new XElement("backup",new XAttribute("id",id),new XAttribute("created",DateTime.UtcNow.ToString("o",CultureInfo.InvariantCulture)),
                new XAttribute("version",AppInfo.Version),new XAttribute("folders",result.Folders),new XAttribute("records",result.Records),
                new XAttribute("covers",result.Covers),new XAttribute("tags",result.Tags),
                folders,new XElement("covers",covers.Values),coverRefs,missing,boxList);

            long total=items.Sum(i=>i.Length);
            string finalName="LMS-"+DateTime.Now.ToString("yyyy-MM-dd-HHmm",CultureInfo.InvariantCulture)+"-"+id.Substring(0,6)+BackupFile.Extension;
            string finalPath=System.IO.Path.Combine(settings.Destination,finalName);
            // Locked in a work folder first. A cloud folder would start uploading a half-made file, so the work is done
            // beside the records when that drive has room; otherwise in a hidden-named file next to the destination.
            string workDir=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(root),"backup-work");
            bool local=result.Cloud && FreeSpace(workDir)>total*1.2+256L*1024*1024;
            string work=local?System.IO.Path.Combine(workDir,id+".partial"):System.IO.Path.Combine(settings.Destination,"~"+id+".partial");
            if(!local && FreeSpace(settings.Destination)<total*1.1+64L*1024*1024)
                throw new IOException(L.P("There isn't enough space where the backup goes.","백업을 넣을 곳에 공간이 모자라요."));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(work));
            try {
                report(new Progress {Step=L.P("Locking it with your password","비밀번호로 잠그고 있어요"),Fraction=0});
                long done=0;
                using(var output=new FileStream(work,FileMode.Create,FileAccess.Write,FileShare.None,1<<20,FileOptions.WriteThrough))
                using(var writer=new BackupFile.Writer(output,password,manifest)) {
                    foreach(var item in items) {
                        token.ThrowIfCancellationRequested();
                        try {
                            using(var input=new FileStream(item.Source,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)) {
                                // A file that changed size since it was listed is written as it is now.
                                writer.AddFile(item.Name,input,input.Length,token);
                                done+=input.Length;
                            }
                        } catch(FileNotFoundException) {} catch(DirectoryNotFoundException) {}
                        if(total>0)report(new Progress {Step=L.P("Locking it with your password","비밀번호로 잠그고 있어요"),Fraction=(double)done/total});
                    }
                    writer.Finish();
                    output.Flush(true);
                    result.Bytes=output.Length;
                }
                report(new Progress {Step=L.P("Opening it again to check it","잘 열리는지 다시 확인하고 있어요"),Fraction=-1});
                int checkedFiles=Verify(work,password,token);
                if(checkedFiles<items.Count(i=>File.Exists(i.Source)))throw new IOException("check failed: files missing");
                if(local) {
                    string arriving=finalPath+".partial";
                    report(new Progress {Step=L.P("Putting it in the backup folder","백업 폴더에 넣고 있어요")});
                    File.Copy(work,arriving,true);
                    if(new FileInfo(arriving).Length!=new FileInfo(work).Length)throw new IOException("copy is short");
                    Verify(arriving,password,token);
                    File.Move(arriving,finalPath);
                } else File.Move(work,finalPath);
            } finally {
                try {if(File.Exists(work))File.Delete(work);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}
                try {if(File.Exists(finalPath+".partial"))File.Delete(finalPath+".partial");} catch(Exception quiet) {ErrorLog.Quiet(quiet);}
            }
            result.File=finalPath;
            settings.LastBackup=DateTime.UtcNow;settings.RemindAfter=DateTime.MinValue;
            settings.Backups.Add(new BackupSettings.Made {File=finalPath,Id=id,Created=DateTime.UtcNow,Missing=result.Missing.Count});
            if(!samePassword)settings.RememberPassword(password);
            settings.Epoch=epoch;
            settings.Save();
            CleanUp(settings,result);
            settings.Save();
            return result;
        }
        static long FreeSpace(string path) {
            try {
                string full=System.IO.Path.GetFullPath(path);
                return new DriveInfo(System.IO.Path.GetPathRoot(full)).AvailableFreeSpace;
            } catch(Exception) {return Int64.MaxValue;}
        }
        // Reads the whole file and checks every piece. Returns how many files are in it.
        internal static int Verify(string file,string password,CancellationToken token,Action<double> progress=null) {
            int count=0;
            using(var input=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read,1<<20))
            using(var reader=new BackupFile.Reader(input,password)) {
                var buffer=new byte[1<<20];
                reader.Entries((name,length,read)=>{
                    count++;
                    long left=length;
                    while(left>0){int n=read(buffer,0,(int)Math.Min(buffer.Length,left));left-=n;}
                    if(progress!=null && reader.Length>0)progress((double)reader.Position/reader.Length);
                },token);
            }
            return count;
        }

        // Removes backups made on this computer that are past the chosen time. Never the newest good one, never one
        // that holds covers the new one is missing, never another computer's. In a cloud folder an old backup goes only
        // when a newer one has been there for a day, so the cloud app can upload the new one before the old one is gone.
        static void CleanUp(BackupSettings settings,Result result) {
            var mine=settings.Backups.Where(m=>File.Exists(m.File)).OrderByDescending(m=>m.Created).ToList();
            settings.Backups=mine.ToList();
            if(mine.Count<=1)return;
            var newest=mine[0];
            int keepCount=settings.Keep=="1"?1:settings.Keep=="2"?2:Int32.MaxValue;
            TimeSpan keepAge=settings.Keep=="month"?TimeSpan.FromDays(31):settings.Keep=="3months"?TimeSpan.FromDays(92):TimeSpan.MaxValue;
            var now=DateTime.UtcNow;
            for(int i=1;i<mine.Count;i++) {
                var old=mine[i];
                bool past=i>=keepCount || (keepAge!=TimeSpan.MaxValue && now-old.Created>keepAge);
                if(!past)continue;
                // It goes only when one newer backup has at least its covers and, in a cloud folder, has been there a day.
                bool cloud=IsCloud(old.File);
                if(!mine.Take(i).Any(m=>m.Missing<=old.Missing && (!cloud || now-m.Created>=TimeSpan.FromDays(1)))) {
                    if(mine.Take(i).All(m=>m.Missing>old.Missing))result.ProtectedOld++;
                    continue;
                }
                try {File.Delete(old.File);settings.Backups.Remove(old);result.Removed++;}
                catch(IOException quiet) {ErrorLog.Quiet(quiet);} catch(UnauthorizedAccessException quiet) {ErrorLog.Quiet(quiet);}
            }
        }
        // Called at start: finishes clean-up that waited for a day in a cloud folder.
        internal static void CleanUpLater(BackupSettings settings) {
            try {var r=new Result();CleanUp(settings,r);if(r.Removed>0)settings.Save();}
            catch(Exception quiet) {ErrorLog.Quiet(quiet);}
        }

        // ---------------- Putting a backup back ----------------
        internal sealed class Opened {
            internal string File,Password;internal XElement Manifest;
            internal List<FolderPlan> Folders=new List<FolderPlan>();
            internal List<PhotoBox.Found> Boxes=new List<PhotoBox.Found>();
        }
        internal enum Match {Found,Maybe,Missing,FromBox}
        internal sealed class FolderPlan {
            internal string Id,Key,OldSource,Album,NewSource;internal int Records,Tags;internal bool Tab;
            internal Match State;internal List<Tuple<string,long>> Samples=new List<Tuple<string,long>>();
            internal bool Later;
            // Its original comes out of a photo box (BoxRelative: where inside that box's folder).
            internal bool FromBox;internal string BoxId,BoxRelative;
        }

        internal static Opened Open(string file,string password,CancellationToken token,Action<double> progress) {
            var opened=new Opened {File=file,Password=password};
            using(var input=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read,1<<20))
            using(var reader=new BackupFile.Reader(input,password))
                opened.Manifest=reader.Manifest;
            // The whole file is checked before anything is shown, so a cut-off download is found now, not halfway.
            Verify(file,password,token,progress);
            var idText=new System.Text.RegularExpressions.Regex("^f[0-9]{1,9}$");
            foreach(var f in opened.Manifest.Element("folders").Elements("folder")) {
                // Names from the file are only used in the shapes this program writes them.
                if(!idText.IsMatch((string)f.Attribute("id")??""))continue;
                var plan=new FolderPlan {Id=(string)f.Attribute("id"),Key=(string)f.Attribute("key"),OldSource=(string)f.Attribute("source"),
                    Album=(string)f.Attribute("album"),Records=(int?)f.Attribute("records")??0,Tags=(int?)f.Attribute("tags")??0,
                    Tab=(bool?)f.Attribute("tab")??true};
                var s=f.Element("samples");
                if(s!=null)plan.Samples=s.Elements("file").Select(e=>Tuple.Create((string)e.Attribute("name"),(long?)e.Attribute("length")??0)).ToList();
                opened.Folders.Add(plan);
            }
            Locate(opened.Folders);
            opened.Boxes=PhotoBox.FindBoxes(opened,System.IO.Path.GetDirectoryName(file));
            return opened;
        }
        // Whether a folder on this computer is the one in the backup: a file with the same name and size.
        static bool? Evidence(FolderPlan plan,string folder) {
            if(!Directory.Exists(folder))return false;
            if(plan.Samples.Count==0)return null;
            foreach(var sample in plan.Samples) {
                try {
                    string file=System.IO.Path.Combine(folder,sample.Item1);
                    if(File.Exists(file) && new FileInfo(file).Length==sample.Item2)return true;
                } catch(Exception) {}
            }
            return false;
        }
        static void Check(FolderPlan plan,string candidate) {
            var e=Evidence(plan,candidate);
            if(e==true){plan.NewSource=candidate;plan.State=Match.Found;}
            else if(e==null && plan.State==Match.Missing){plan.NewSource=candidate;plan.State=Match.Maybe;}
        }
        // The old place first, then the same place on another drive letter.
        static void Locate(List<FolderPlan> plans) {
            var letters=DriveInfo.GetDrives().Where(d=>{try {return d.IsReady;} catch(Exception) {return false;}})
                .Select(d=>d.RootDirectory.FullName).ToList();
            foreach(var plan in plans) {
                plan.State=Match.Missing;plan.NewSource=null;
                if(String.IsNullOrEmpty(plan.OldSource))continue;
                Check(plan,plan.OldSource);
                if(plan.State==Match.Found)continue;
                string rest=plan.OldSource.Length>3 && plan.OldSource[1]==':'?plan.OldSource.Substring(3):null;
                if(rest==null)continue;
                foreach(string letter in letters) {
                    string candidate=System.IO.Path.Combine(letter,rest);
                    if(String.Equals(candidate.TrimEnd('\\'),plan.OldSource.TrimEnd('\\'),StringComparison.OrdinalIgnoreCase))continue;
                    Check(plan,candidate);
                    if(plan.State==Match.Found)break;
                }
            }
        }
        // The person showed where one folder is now. The same move (the part of the path that differs) is tried for the
        // folders not found yet, longest shared ending first: D:\Media\2024\Jeju -> F:\Photos\2024\Jeju also finds
        // D:\Media\2023 at F:\Photos\2023. Returns how many more were found.
        internal static int Pick(List<FolderPlan> plans,FolderPlan chosen,string newPath) {
            chosen.NewSource=newPath;chosen.State=Match.Found;chosen.Later=false;
            var oldParts=chosen.OldSource.TrimEnd('\\').Split('\\');
            var newParts=newPath.TrimEnd('\\').Split('\\');
            var rules=new List<Tuple<string,string>>();
            for(int common=0;common<Math.Min(oldParts.Length,newParts.Length)-1;common++) {
                if(!String.Equals(oldParts[oldParts.Length-1-common],newParts[newParts.Length-1-common],StringComparison.OrdinalIgnoreCase))break;
                string from=String.Join("\\",oldParts.Take(oldParts.Length-1-common))+"\\";
                string to=String.Join("\\",newParts.Take(newParts.Length-1-common))+"\\";
                rules.Add(Tuple.Create(from,to));
            }
            if(rules.Count==0)rules.Add(Tuple.Create(chosen.OldSource.TrimEnd('\\')+"\\",newPath.TrimEnd('\\')+"\\"));
            int found=0;
            foreach(var plan in plans.Where(p=>p!=chosen && p.State!=Match.Found && !p.FromBox && p.OldSource!=null)) {
                foreach(var rule in rules) {
                    if(!plan.OldSource.StartsWith(rule.Item1,StringComparison.OrdinalIgnoreCase))continue;
                    var before=plan.State;
                    Check(plan,rule.Item2+plan.OldSource.Substring(rule.Item1.Length));
                    if(plan.State==Match.Found){if(before!=Match.Found)found++;plan.Later=false;break;}
                }
            }
            return found;
        }

        internal sealed class Restored {
            internal int Folders,NewFolders,Records,Tags,Covers,Skipped,Later;
        }
        // Puts the records of the chosen folders back, beside what this computer already has: nothing is overwritten.
        // Each record goes in whole (made in a work folder, then moved in one step), so stopping halfway leaves complete
        // records only, and doing the same backup again adds what is still missing and nothing twice.
        internal static Restored Apply(Opened opened,string coverFolder,Action<Progress> report,CancellationToken token) {
            string root=StoreRoot;
            var result=new Restored();
            var plans=opened.Folders.Where(p=>(p.State==Match.Found || p.FromBox || p.State==Match.Maybe) && !p.Later && p.NewSource!=null)
                .ToDictionary(p=>p.Id);
            result.Later=opened.Folders.Count-plans.Count;
            string stage=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(root),"restore-work",Guid.NewGuid().ToString("N").Substring(0,10));
            Directory.CreateDirectory(stage);
            try {
                report(new Progress {Step=L.P("Opening the backup","백업을 풀고 있어요"),Fraction=0});
                using(var input=new FileStream(opened.File,FileMode.Open,FileAccess.Read,FileShare.Read,1<<20))
                using(var reader=new BackupFile.Reader(input,opened.Password)) {
                    var buffer=new byte[1<<20];
                    reader.Entries((name,length,read)=>{
                        string target=null;
                        if(name.StartsWith("r/",StringComparison.Ordinal)) {
                            int slash=name.IndexOf('/',2);
                            if(slash>0 && plans.ContainsKey(name.Substring(2,slash-2)))target=SafeJoin(stage,name);
                        } else if(name.StartsWith("c/",StringComparison.Ordinal) || name.StartsWith("root/",StringComparison.Ordinal))target=SafeJoin(stage,name);
                        if(target==null)return;
                        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
                        using(var output=new FileStream(target,FileMode.Create,FileAccess.Write)) {
                            long left=length;
                            while(left>0){int n=read(buffer,0,(int)Math.Min(buffer.Length,left));output.Write(buffer,0,n);left-=n;}
                        }
                        report(new Progress {Step=L.P("Opening the backup","백업을 풀고 있어요"),Fraction=reader.Length>0?(double)reader.Position/reader.Length*0.5:-1});
                    },token);
                }
                // Covers: kept where they were when that file is still there and the same; otherwise put in the cover folder.
                var refs=opened.Manifest.Element("coverRefs").Elements("ref").ToList();
                var placed=new Dictionary<string,string>(StringComparer.Ordinal);
                var store=new GalleryStore(root);
                var newKeys=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
                int done=0;
                foreach(var plan in plans.Values) {
                    token.ThrowIfCancellationRequested();
                    report(new Progress {Step=L.P("Putting records back","기록을 넣고 있어요"),Fraction=0.5+0.5*done++/Math.Max(1,plans.Count)});
                    string staged=System.IO.Path.Combine(stage,"r",plan.Id);
                    if(!Directory.Exists(staged))continue;
                    string target=store.AlbumFolderOf(plan.NewSource);
                    newKeys[plan.Key]=System.IO.Path.GetFileName(target);
                    bool fresh=!Directory.Exists(target);
                    // Only the records that will really go in (a record already here stays as it is, with its covers).
                    var going=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach(string record in Directory.GetDirectories(staged,"p-*")) {
                        string name=System.IO.Path.GetFileName(record);
                        if(fresh || !Directory.Exists(System.IO.Path.Combine(target,name)))going.Add(name);
                        else result.Skipped++;
                    }
                    // Covers of those records, put in place before the records point at them.
                    foreach(var r in refs.Where(x=>(string)x.Attribute("folder")==plan.Id)) {
                        string record=(string)r.Attribute("record")??"";
                        string recordDir=SafeJoin(staged,record);
                        if(recordDir==null || !going.Contains(System.IO.Path.GetFileName(recordDir)))continue;
                        string attribute=(string)r.Attribute("attribute");
                        if(Array.IndexOf(CoverAttributes,attribute)<0)continue;
                        string where=PlaceCover(opened.Manifest,stage,(string)r.Attribute("sha"),(string)r.Attribute("path"),(long?)r.Attribute("ticks")??0,coverFolder,placed,result);
                        if(where==null)continue;
                        try {
                            string metadata=System.IO.Path.Combine(recordDir,"photo.xml");
                            var xml=XElement.Load(metadata);
                            xml.SetAttributeValue(attribute,where);
                            if(attribute=="coverFile")xml.SetAttributeValue("coverFileTicks",File.GetLastWriteTimeUtc(where).Ticks);
                            xml.Save(metadata);
                        } catch(Exception quiet) {ErrorLog.Quiet(quiet);}
                    }
                    // Where each original was: the new folder now.
                    string oldRoot=plan.OldSource.TrimEnd('\\'),newRoot=plan.NewSource.TrimEnd('\\');
                    foreach(string origin in Directory.GetFiles(staged,"origin.xml",SearchOption.AllDirectories)) {
                        try {
                            var o=XElement.Load(origin);
                            string p=(string)o.Attribute("path");
                            if(p!=null && p.StartsWith(oldRoot+"\\",StringComparison.OrdinalIgnoreCase)) {
                                o.SetAttributeValue("path",newRoot+p.Substring(oldRoot.Length));
                                o.Save(origin);
                            }
                        } catch(Exception quiet) {ErrorLog.Quiet(quiet);}
                    }
                    string stagedTags=System.IO.Path.Combine(staged,"tags.xml");
                    if(fresh) {
                        string temp=System.IO.Path.Combine(root,"setup-"+Guid.NewGuid().ToString("N"));
                        try {
                            CopyTree(staged,temp);
                            // Tags go in through the merge below, which leaves out the other computer's file numbers.
                            File.Delete(System.IO.Path.Combine(temp,"tags.xml"));
                            File.WriteAllText(System.IO.Path.Combine(temp,"source.txt"),plan.NewSource);
                            if(!File.Exists(System.IO.Path.Combine(temp,"album.txt")))File.WriteAllText(System.IO.Path.Combine(temp,"album.txt"),System.IO.Path.GetFileName(plan.NewSource));
                            // The drive's own number on this computer, so the folder isn't taken for one on another drive.
                            uint serial=OriginalLocator.VolumeOf(System.IO.Path.GetPathRoot(plan.NewSource));
                            string volume=System.IO.Path.Combine(temp,"volume.txt");
                            if(serial!=0)File.WriteAllText(volume,serial.ToString(CultureInfo.InvariantCulture));else File.Delete(volume);
                            Directory.Move(temp,target);
                        } finally {try {if(Directory.Exists(temp))Directory.Delete(temp,true);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}}
                        result.NewFolders++;
                        result.Records+=going.Count;
                    } else {
                        if(!File.Exists(System.IO.Path.Combine(target,"album.txt"))) {
                            File.WriteAllText(System.IO.Path.Combine(target,"source.txt"),plan.NewSource);
                            File.WriteAllText(System.IO.Path.Combine(target,"album.txt"),System.IO.Path.GetFileName(plan.NewSource));
                        }
                        foreach(string name in going) {
                            token.ThrowIfCancellationRequested();
                            string temp=System.IO.Path.Combine(target,"restore-"+Guid.NewGuid().ToString("N")+".tmp");
                            try {
                                CopyTree(System.IO.Path.Combine(staged,name),temp);
                                Directory.Move(temp,System.IO.Path.Combine(target,name));
                            } finally {try {if(Directory.Exists(temp))Directory.Delete(temp,true);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}}
                            result.Records++;
                        }
                    }
                    result.Tags+=MergeTags(stagedTags,System.IO.Path.Combine(target,"tags.xml"));
                    result.Folders++;
                }
                MergeRoot(stage,root,newKeys);
            } finally {
                try {Directory.Delete(stage,true);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}
            }
            Tags.Reload();
            return result;
        }
        static string SafeJoin(string stage,string name) {
            if(name.Contains("..") || name.Contains(":") || name.StartsWith("/"))return null;
            string path=System.IO.Path.GetFullPath(System.IO.Path.Combine(stage,name.Replace('/','\\')));
            return path.StartsWith(stage+"\\",StringComparison.OrdinalIgnoreCase)?path:null;
        }
        static void CopyTree(string from,string to) {
            Directory.CreateDirectory(to);
            foreach(string file in Directory.GetFiles(from,"*",SearchOption.AllDirectories)) {
                string target=System.IO.Path.Combine(to,file.Substring(from.Length+1));
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
                File.Copy(file,target,true);
            }
            foreach(string dir in Directory.GetDirectories(from,"*",SearchOption.AllDirectories))
                Directory.CreateDirectory(System.IO.Path.Combine(to,dir.Substring(from.Length+1)));
        }
        static readonly System.Text.RegularExpressions.Regex ShaText=new System.Text.RegularExpressions.Regex("^[0-9a-f]{64}$");
        static readonly System.Text.RegularExpressions.Regex ExtText=new System.Text.RegularExpressions.Regex(@"^\.[a-z0-9]{1,8}$");
        static string PlaceCover(XElement manifest,string stage,string sha,string oldPath,long ticks,string coverFolder,Dictionary<string,string> placed,Restored result) {
            string done;
            if(sha==null || !ShaText.IsMatch(sha))return null;
            if(placed.TryGetValue(sha,out done))return done;
            var info=manifest.Element("covers").Elements("cover").FirstOrDefault(c=>(string)c.Attribute("sha")==sha);
            if(info==null)return null;
            string ext=(string)info.Attribute("ext")??"";
            if(!ExtText.IsMatch(ext))return null;
            string staged=System.IO.Path.Combine(stage,"c",sha+ext);
            if(!File.Exists(staged))return null;
            long length;
            try {if(File.Exists(oldPath) && Sha(oldPath,out length)==sha){placed[sha]=oldPath;return oldPath;}} catch(Exception) {}
            Directory.CreateDirectory(coverFolder);
            string name="";
            try {name=System.IO.Path.GetFileName(oldPath??"");} catch(ArgumentException) {}
            foreach(char bad in System.IO.Path.GetInvalidFileNameChars())name=name.Replace(bad,'_');
            if(String.IsNullOrEmpty(name.Trim('.',' ')) || name.Length>150)name=sha.Substring(0,16)+ext;
            string target=System.IO.Path.Combine(coverFolder,name);
            for(int n=2;File.Exists(target);n++) {
                try {if(Sha(target,out length)==sha)break;} catch(Exception) {}
                target=System.IO.Path.Combine(coverFolder,System.IO.Path.GetFileNameWithoutExtension(name)+" ("+n+")"+System.IO.Path.GetExtension(name));
            }
            if(!File.Exists(target)) {
                File.Copy(staged,target+".tmp",true);
                File.Move(target+".tmp",target);
                result.Covers++;
                if(ticks>0)try {File.SetLastWriteTimeUtc(target,new DateTime(ticks,DateTimeKind.Utc));} catch(Exception) {}
            }
            placed[sha]=target;
            return target;
        }
        // Tags from the backup join the tags already here; a file's tags that are here stay as they are.
        static int MergeTags(string fromFile,string intoFile) {
            if(!File.Exists(fromFile))return 0;
            XElement from;
            try {from=XElement.Load(fromFile);} catch(Exception) {return 0;}
            XElement into=null;
            try {if(File.Exists(intoFile))into=XElement.Load(intoFile);} catch(Exception) {return 0;}
            if(into==null)into=new XElement("tags");
            int added=0;
            foreach(var file in from.Elements("file")) {
                string name=(string)file.Attribute("name");
                if(String.IsNullOrEmpty(name))continue;
                var here=into.Elements("file").FirstOrDefault(f=>String.Equals((string)f.Attribute("name"),name,StringComparison.OrdinalIgnoreCase));
                if(here==null) {
                    // The Windows file number came from the other computer; it is left out.
                    here=new XElement("file",new XAttribute("name",name));into.Add(here);
                }
                foreach(var tag in file.Elements("tag")) {
                    if(here.Elements("tag").Any(t=>String.Equals(t.Value,tag.Value,StringComparison.OrdinalIgnoreCase)))continue;
                    here.Add(new XElement("tag",tag.Value));added++;
                }
            }
            if(added==0)return 0;
            string part=intoFile+"."+Guid.NewGuid().ToString("N")+".tmp";
            try {
                into.Save(part);
                if(File.Exists(intoFile))File.Replace(part,intoFile,null);else File.Move(part,intoFile);
            } finally {try {File.Delete(part);} catch(Exception) {}}
            return added;
        }
        // The folder tab order (with the new record names) and the pairs marked "not the same".
        static void MergeRoot(string stage,string root,Dictionary<string,string> newKeys) {
            try {
                string from=System.IO.Path.Combine(stage,"root","tab-order.txt"),into=System.IO.Path.Combine(root,"tab-order.txt");
                if(File.Exists(from)) {
                    var here=File.Exists(into)?File.ReadAllLines(into).Where(l=>l.Length>0).ToList():new List<string>();
                    foreach(string line in File.ReadAllLines(from)) {
                        string key;
                        if(!newKeys.TryGetValue(line,out key))continue;
                        if(!here.Contains(key,StringComparer.OrdinalIgnoreCase))here.Add(key);
                    }
                    File.WriteAllLines(into+".tmp",here);
                    if(File.Exists(into))File.Replace(into+".tmp",into,null);else File.Move(into+".tmp",into);
                }
                from=System.IO.Path.Combine(stage,"root","similar","not-same.txt");into=System.IO.Path.Combine(root,"similar","not-same.txt");
                if(File.Exists(from)) {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(into));
                    var here=File.Exists(into)?File.ReadAllLines(into).ToList():new List<string>();
                    var add=File.ReadAllLines(from).Where(l=>l.Length>0 && !here.Contains(l)).ToList();
                    if(add.Count>0)File.AppendAllLines(into,add);
                }
                from=System.IO.Path.Combine(stage,"root","face-likeness.txt");into=System.IO.Path.Combine(root,"face-likeness.txt");
                if(File.Exists(from) && !File.Exists(into))File.Copy(from,into);
            } catch(Exception quiet) {ErrorLog.Quiet(quiet);}
        }
        // Where restored covers go: inside the cover folder the person chose, or beside the records when there is none.
        internal static string CoverFolder(Preferences prefs) {
            try {
                if(prefs!=null && !String.IsNullOrWhiteSpace(prefs.CoverDirectory) && Directory.Exists(prefs.CoverDirectory))
                    return System.IO.Path.Combine(prefs.CoverDirectory,"복원한 표지");
            } catch(Exception) {}
            return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(StoreRoot),"Restored covers");
        }
        // Leftovers of work that stopped halfway (the app was closed or the computer turned off).
        // Set while a backup, a restore or a box is being worked on in this program.
        internal static int Working;
        internal static void ClearLeftovers() {
            if(Thread.VolatileRead(ref Working)>0)return;
            var before=DateTime.UtcNow.AddHours(-2);
            foreach(string dir in new[]{"backup-work","restore-work"}) {
                try {
                    string path=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(StoreRoot),dir);
                    if(!Directory.Exists(path))continue;
                    foreach(string item in Directory.GetFileSystemEntries(path)) {
                        if(File.GetLastWriteTimeUtc(item)>before)continue;
                        if(Directory.Exists(item))Directory.Delete(item,true);else File.Delete(item);
                    }
                } catch(Exception quiet) {ErrorLog.Quiet(quiet);}
            }
            try {
                foreach(string folder in Directory.GetDirectories(StoreRoot,"folder-*"))
                    foreach(string dir in Directory.GetDirectories(folder,"restore-*.tmp"))
                        if(Directory.GetLastWriteTimeUtc(dir)<before)Directory.Delete(dir,true);
            } catch(Exception quiet) {ErrorLog.Quiet(quiet);}
            try {
                foreach(string dir in Directory.GetDirectories(StoreRoot,"setup-*"))
                    if(Directory.GetCreationTimeUtc(dir)<DateTime.UtcNow.AddHours(-1))Directory.Delete(dir,true);
            } catch(Exception quiet) {ErrorLog.Quiet(quiet);}
        }
    }
}
