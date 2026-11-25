using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using Microsoft.Win32.SafeHandles;

namespace LoveMyShelf {
    // Windows file IDs survive moves on the same volume. The content signature also
    // works with copied files, removable disks and file systems without stable IDs.
    static class OriginalLocator {
        const string Metadata="origin.xml";
        static readonly StringComparer Paths=StringComparer.OrdinalIgnoreCase;
        static readonly object writeLock=new object();

        [StructLayout(LayoutKind.Sequential)]
        struct FileInfoNative {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Created,Accessed,Written;
            public uint Volume,SizeHigh,SizeLow,Links,IdHigh,IdLow;
            public ulong Id {get{return ((ulong)IdHigh<<32)|IdLow;}}
            public long Birth {get{return ((long)Created.dwHighDateTime<<32)|(uint)Created.dwLowDateTime;}}
        }
        [StructLayout(LayoutKind.Explicit,Size=24)]
        struct FileId { [FieldOffset(0)]public uint Size; [FieldOffset(4)]public int Type; [FieldOffset(8)]public ulong Id; }
        [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetFileInformationByHandle(SafeFileHandle file,out FileInfoNative info);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr security,uint mode,uint flags,IntPtr template);
        [DllImport("kernel32.dll",SetLastError=true)] static extern SafeFileHandle OpenFileById(SafeFileHandle volume,ref FileId id,uint access,uint share,IntPtr security,uint flags);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern uint GetFinalPathNameByHandle(SafeFileHandle file,StringBuilder buffer,uint size,uint flags);

        sealed class Reference {
            public string Path,Signature;
            public long Length,Birth;
            public ulong Id;
            public uint Volume;
            public bool SameFile(FileInfoNative info){return Id!=0 && info.Id==Id && Volume==info.Volume && Birth==info.Birth;}
        }
        static Reference Read(string folder) {
            try {
                var x=XElement.Load(Path.Combine(folder,Metadata));
                return new Reference {Path=(string)x.Attribute("path"),Length=(long)x.Attribute("length"),Signature=(string)x.Attribute("signature"),
                    Id=(ulong)x.Attribute("id"),Volume=(uint)x.Attribute("volume"),Birth=(long)x.Attribute("birth")};
            } catch(Exception){return null;}
        }
        internal static string LastKnownPath(Photo photo) {
            var saved=Read(photo.Folder);return saved==null?null:saved.Path;
        }
        static FileStream Open(string path){return new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);}
        static string Signature(FileStream file) {
            using(var hash=SHA256.Create()) {
                file.Position=0;
                if(file.Length<=8*1024*1024)return Convert.ToBase64String(hash.ComputeHash(file));
                // Bound the I/O cost of saving a crop from a multi-gigabyte video.
                var block=new byte[65536];
                for(int i=0;i<8;i++){
                    file.Position=(file.Length-block.Length)/7*i;
                    int used=0;
                    while(used<block.Length) {
                        int count=file.Read(block,used,block.Length-used);
                        if(count==0)throw new EndOfStreamException();
                        used+=count;
                    }
                    hash.TransformBlock(block,0,used,block,0);
                }
                hash.TransformFinalBlock(new byte[0],0,0);return Convert.ToBase64String(hash.Hash);
            }
        }
        internal static void Remember(Photo photo,string path,bool onlyIfMissing=false,bool required=false) {
            string temp=Path.Combine(photo.Folder,"origin-"+Guid.NewGuid().ToString("N")+".tmp");
            try {
                using(var f=Open(path)){
                    FileInfoNative info;GetFileInformationByHandle(f.SafeFileHandle,out info);
                    new XElement("original",new XAttribute("path",Path.GetFullPath(path)),new XAttribute("length",f.Length),
                        new XAttribute("volume",info.Volume),new XAttribute("id",info.Id),new XAttribute("birth",info.Birth),
                        new XAttribute("signature",Signature(f))).Save(temp);
                }
                lock(writeLock){
                    string target=Path.Combine(photo.Folder,Metadata);
                    if(onlyIfMissing && File.Exists(target))return;
                    if(File.Exists(target))File.Replace(temp,target,null);else File.Move(temp,target);
                }
            } catch(IOException){if(required)throw;}catch(UnauthorizedAccessException){if(required)throw;}
            finally {try{File.Delete(temp);}catch(IOException quiet) {ErrorLog.Quiet(quiet);}catch(UnauthorizedAccessException quiet) {ErrorLog.Quiet(quiet);}}
        }
        static bool Matches(Reference saved,string path,bool idOnly=false) {
            try {using(var f=Open(path)){
                FileInfoNative info;
                if(GetFileInformationByHandle(f.SafeFileHandle,out info) && saved.SameFile(info))return true;
                return !idOnly && f.Length==saved.Length && Signature(f)==saved.Signature;
            }} catch(IOException){return false;}catch(UnauthorizedAccessException){return false;}
        }
        // A tag editor (Mp3tag and the like) may save a video as a new file under the same name, so both its Windows ID
        // and its content signature change. Same drive, same place, nearly the same size: it is still this original.
        // "Nearly" depends on the file, and on whether it grew or shrank. A picture saved again (turned, its details
        // edited) changes by up to about half; a small video may get a cover picture put in; a big video keeps the room
        // it always had. Nothing shrinks to less than half and is still the same file, and an empty file never is.
        // (64 MB either way for everything let a 1 MB photo pass for a 60 MB file that had taken its name.)
        internal static bool SizeNear(string path,long before,long now) {
            const long MB=1024*1024;
            if(now<=0 || before<=0)return false;
            bool video=path!=null && VideoFrames.Supports(path);
            long room=!video?Math.Max(2*MB,before/2):before<64*MB?Math.Max(8*MB,before):Math.Max(64*MB,before/10);
            if(now>=before)return now-before<=room;
            return before-now<=Math.Min(room,before/2);
        }
        static bool EditedInPlace(Reference saved,string path) {
            try {
                if(!Paths.Equals(Path.GetFullPath(path),Path.GetFullPath(saved.Path)))return false;
                using(var f=Open(path)){
                    FileInfoNative info;
                    if(!GetFileInformationByHandle(f.SafeFileHandle,out info) || info.Volume!=saved.Volume)return false;
                    return SizeNear(path,saved.Length,f.Length);
                }
            } catch(IOException){return false;}catch(UnauthorizedAccessException){return false;}catch(ArgumentException){return false;}
        }
        // ----- For moving to another drive: the original goes only if it is still the very file that was copied -----
        [DllImport("kernel32.dll",SetLastError=true)] static extern bool SetFileInformationByHandle(SafeFileHandle file,int kind,ref byte info,int size);
        // Which file this open file is. Asked only on NTFS, where the number stays with the file; id 0 means "not known".
        internal static void IdOf(FileStream file,string path,out uint volume,out ulong id) {
            volume=0;id=0;
            try {
                string root=Path.GetPathRoot(Path.GetFullPath(path));
                if(String.IsNullOrEmpty(root) || root.StartsWith(@"\\",StringComparison.Ordinal))return;
                var drive=new DriveInfo(root);
                // A network drive may call itself NTFS and still hand out numbers that don't stay.
                if(drive.DriveType!=DriveType.Fixed && drive.DriveType!=DriveType.Removable)return;
                if(!String.Equals(drive.DriveFormat,"NTFS",StringComparison.OrdinalIgnoreCase))return;
                FileInfoNative info;
                if(GetFileInformationByHandle(file.SafeFileHandle,out info) && info.Volume!=0){volume=info.Volume;id=info.Id;}
            } catch(Exception quiet) {ErrorLog.Quiet(quiet);volume=0;id=0;}
        }
        internal enum Gone {Deleted,Changed,Unsure}
        // Deletes the file if it is still the one that was copied: same size, same time and, where known, the same
        // file number. It is looked at and deleted through one open handle that lets nobody write, rename or delete it
        // meanwhile, so nothing can take its place between the look and the delete.
        // Unsure: it could not be done this way (in use, a path Windows won't open like this) and nothing was changed.
        internal static Gone DeleteIfSame(string path,long length,DateTime stampUtc,uint volume,ulong id) {
            try {
                const uint Delete=0x00010000,ReadAttributes=0x80,ShareRead=1,OpenExisting=3,OpenLinkItself=0x00200000;
                using(var file=CreateFile(path,Delete|ReadAttributes,ShareRead,IntPtr.Zero,OpenExisting,OpenLinkItself,IntPtr.Zero)) {
                    if(file.IsInvalid)return Gone.Unsure;
                    FileInfoNative info;
                    if(!GetFileInformationByHandle(file,out info))return Gone.Unsure;
                    if((info.Attributes&(uint)FileAttributes.Directory)!=0)return Gone.Changed;
                    // A link, or a cloud file (OneDrive keeps this mark on files that are here too): its size can't
                    // be read this way, so it is left to the way it was always done.
                    if((info.Attributes&(uint)FileAttributes.ReparsePoint)!=0)return Gone.Unsure;
                    long now=((long)info.SizeHigh<<32)|info.SizeLow;
                    long written=((long)info.Written.dwHighDateTime<<32)|(uint)info.Written.dwLowDateTime;
                    if(now!=length || written!=stampUtc.ToFileTimeUtc())return Gone.Changed;
                    if(id!=0 && (info.Volume!=volume || info.Id!=id))return Gone.Changed;
                    // Windows refuses to delete a read-only file this way; the mark is taken off first, as before.
                    bool readOnly=(info.Attributes&(uint)FileAttributes.ReadOnly)!=0;
                    if(readOnly)File.SetAttributes(path,(FileAttributes)info.Attributes&~FileAttributes.ReadOnly);
                    byte yes=1;
                    if(SetFileInformationByHandle(file,4,ref yes,1))return Gone.Deleted; // 4: FileDispositionInfo
                    // Not deleted after all: it gets its read-only mark back.
                    if(readOnly) {try {File.SetAttributes(path,(FileAttributes)info.Attributes);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}}
                    return Gone.Unsure;
                }
            } catch(Exception quiet) {ErrorLog.Quiet(quiet);return Gone.Unsure;}
        }
        // ----- For tags: a file's own identity, without a cover record -----
        internal static bool Identity(string path,out uint volume,out ulong id,out long birth,out long length) {
            volume=0;id=0;birth=0;length=0;
            try {
                if(!File.Exists(path))return false;
                using(var f=Open(path)) {
                    FileInfoNative info;
                    if(!GetFileInformationByHandle(f.SafeFileHandle,out info) || info.Volume==0)return false;
                    volume=info.Volume;id=info.Id;birth=info.Birth;length=f.Length;
                    return true;
                }
            } catch(IOException){return false;}catch(UnauthorizedAccessException){return false;}
        }
        // Where that file is now, on whichever connected drive has that volume number. hint: a folder to try the drive of first.
        internal static string FindById(string hint,uint volume,ulong id,long birth) {
            try {
                return PathById(new Reference {Path=hint??@"C:\",Id=id,Volume=volume,Birth=birth});
            } catch(IOException){return null;}catch(UnauthorizedAccessException){return null;}catch(ArgumentException){return null;}
        }
        // ----- For DriveLinks -----
        internal static uint VolumeOf(string root) {
            using(var handle=CreateFile(root,0,7,IntPtr.Zero,3,0x02000000,IntPtr.Zero)) {
                FileInfoNative info;
                return !handle.IsInvalid && GetFileInformationByHandle(handle,out info)?info.Volume:0;
            }
        }
        internal static bool Facts(string folder,out string path,out uint volume,out long length) {
            var saved=Read(folder);
            path=saved==null?null:saved.Path;volume=saved==null?0:saved.Volume;length=saved==null?0:saved.Length;
            return saved!=null && !String.IsNullOrEmpty(saved.Path);
        }
        internal static string FoundById(string folder) {
            var saved=Read(folder);
            try {return saved==null?null:PathById(saved);}
            catch(IOException){return null;}catch(UnauthorizedAccessException){return null;}
        }
        // The same content under another name in this folder (for drives whose file IDs don't last).
        internal static string FoundBeside(string folder,string directory) {
            var saved=Read(folder);
            if(saved==null)return null;
            try {
                foreach(var file in new DirectoryInfo(directory).EnumerateFiles())
                    if(file.Length==saved.Length && Compatible(file.FullName,saved.Path) && Matches(saved,file.FullName))return file.FullName;
            } catch(IOException quiet) {ErrorLog.Quiet(quiet);} catch(UnauthorizedAccessException quiet) {ErrorLog.Quiet(quiet);}
            return null;
        }
        internal static void MovePath(string folder,string path) {
            string target=Path.Combine(folder,Metadata);
            if(!File.Exists(target))return;
            string temp=Path.Combine(folder,"origin-"+Guid.NewGuid().ToString("N")+".tmp");
            try {
                lock(writeLock) {
                    var xml=XElement.Load(target);
                    xml.SetAttributeValue("path",path);xml.Save(temp);
                    File.Replace(temp,target,null);
                }
            } finally {try{File.Delete(temp);}catch(IOException quiet) {ErrorLog.Quiet(quiet);}catch(UnauthorizedAccessException quiet) {ErrorLog.Quiet(quiet);}}
        }
        // The person said this file is the original, even though it no longer looks the same.
        internal static void Link(Photo photo,string path) {Remember(photo,path,false,true);}
        static string PathById(Reference saved) {
            if(saved.Id==0)return null;
            var roots=new List<string>{Path.GetPathRoot(saved.Path)};
            roots.AddRange(DriveInfo.GetDrives().Where(d=>d.DriveType==DriveType.Fixed || d.DriveType==DriveType.Removable).Select(d=>d.Name));
            foreach(string root in roots.Distinct(Paths)) {
                using(var volume=CreateFile(root,0,7,IntPtr.Zero,3,0x02000000,IntPtr.Zero)) {
                    FileInfoNative info;
                    if(volume.IsInvalid || !GetFileInformationByHandle(volume,out info) || info.Volume!=saved.Volume)continue;
                    var id=new FileId {Size=24,Id=saved.Id};
                    using(var file=OpenFileById(volume,ref id,0x80,7,IntPtr.Zero,0)) {
                        if(file.IsInvalid || !GetFileInformationByHandle(file,out info) || !saved.SameFile(info))continue;
                        var buffer=new StringBuilder(32768);uint len=GetFinalPathNameByHandle(file,buffer,(uint)buffer.Capacity,0);
                        if(len==0 || len>=buffer.Capacity)continue;
                        string path=buffer.ToString();
                        if(path.StartsWith(@"\\?\UNC\",StringComparison.OrdinalIgnoreCase))path=@"\\"+path.Substring(8);
                        else if(path.StartsWith(@"\\?\"))path=path.Substring(4);
                        if(!path.Split(Path.DirectorySeparatorChar).Contains("$Recycle.Bin",Paths) && File.Exists(path))return path;
                    }
                }
            }
            return null;
        }
        static bool CropMatches(Photo photo,string path,byte[] saved,CancellationToken token) {
            bool match=false;
            ThreadStart compare=delegate {
                try {
                    token.ThrowIfCancellationRequested();
                    var source=AnimationFrames.Supports(path)?AnimationFrames.SavedFrame(path,photo,2200,token):Pictures.Load(path,2200);
                    using(var bytes=new MemoryStream()){
                        Pictures.Thumbnail(source,photo.Crop,bytes,photo.Rotation,photo.Brightness);
                        match=bytes.Length==saved.Length && bytes.ToArray().SequenceEqual(saved);
                        // The earliest editor rounded the edges before scaling the crop to the tile.
                        var bounds=new System.Windows.Rect(0,0,1,1);
                        if(!match && photo.Rotation==0 && bounds.Contains(photo.Crop)){
                            bytes.SetLength(0);
                            Pictures.Thumbnail(Pictures.Cut(source,photo.Crop),bounds,bytes);
                            match=bytes.Length==saved.Length && bytes.ToArray().SequenceEqual(saved);
                        }
                    }
                }catch(Exception quiet) {ErrorLog.Quiet(quiet);/* A missing codec, frame or corrupt candidate is not a match. */}
            };
            if(Thread.CurrentThread.GetApartmentState()==ApartmentState.STA)compare();
            else {var thread=new Thread(compare){IsBackground=true};thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();}
            token.ThrowIfCancellationRequested();return match;
        }
        static bool Compatible(string path,string name) {
            string ext=Path.GetExtension(path),old=Path.GetExtension(name);
            if(Paths.Equals(ext,old))return true;
            string[] jpeg={".jpg",".jpeg",".jfif"};
            return jpeg.Contains(ext,Paths) && jpeg.Contains(old,Paths);
        }
        static IEnumerable<FileInfo> Nearby(GalleryStore store,Photo photo,string previous,CancellationToken token) {
            string dir=Path.GetDirectoryName(previous);
            var dirs=new Queue<string>();dirs.Enqueue(dir);
            foreach(string sibling in Directory.GetDirectories(Path.GetDirectoryName(photo.Folder),"p-*")){
                token.ThrowIfCancellationRequested();
                var reference=Read(sibling);
                if(reference!=null)dirs.Enqueue(Path.GetDirectoryName(reference.Path));
            }
            foreach(var album in store.Albums())if(album.Source!=null)dirs.Enqueue(album.Source);
            var parent=Directory.GetParent(dir);
            if(parent!=null && parent.Parent!=null)dirs.Enqueue(parent.FullName);
            var seen=new HashSet<string>(Paths);
            while(dirs.Count>0) {
                token.ThrowIfCancellationRequested();string next=dirs.Dequeue();
                if(!seen.Add(next) || Paths.Equals(next,store.Root))continue;
                FileInfo[] files;
                try {
                    var directory=new DirectoryInfo(next);
                    files=directory.GetFiles();
                    if(directory.Parent!=null)foreach(var sub in directory.EnumerateDirectories()){
                        if((sub.Attributes&(FileAttributes.ReparsePoint|FileAttributes.Hidden|FileAttributes.System))==0)dirs.Enqueue(sub.FullName);
                    }
                }catch(IOException){continue;}catch(UnauthorizedAccessException){continue;}
                foreach(var file in files)yield return file;
            }
        }
        static List<string> Indexed(string name,Reference saved,CancellationToken token) {
            var paths=new List<string>();
            try {
                using(var db=new OleDbConnection("Provider=Search.CollatorDSO.1;Extended Properties='Application=Windows';")){
                    db.Open();
                    using(var cmd=db.CreateCommand()){
                        cmd.CommandTimeout=4;
                        string condition=saved==null?"System.FileName='"+name.Replace("'","''")+"'":
                            "System.Size="+saved.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        cmd.CommandText="SELECT System.ItemPathDisplay FROM SYSTEMINDEX WHERE "+condition;
                        using(var rows=cmd.ExecuteReader())while(rows.Read()){
                            token.ThrowIfCancellationRequested();
                            if(!rows.IsDBNull(0))paths.Add(rows.GetString(0));
                        }
                    }
                }
            }catch(OperationCanceledException){throw;}catch(Exception quiet) {ErrorLog.Quiet(quiet);/* Windows Search is optional. */}
            return paths;
        }
        // Older releases saved only a filename and the rendered crop. A name collision
        // alone must never bind an existing crop to a different picture.
        static string LegacyMatch(Photo photo,IEnumerable<string> candidates,byte[] thumb,CancellationToken token) {
            string match=null,signature=null;
            foreach(string path in candidates) {
                token.ThrowIfCancellationRequested();
                if(!CropMatches(photo,path,thumb,token))continue;
                if(match==null){match=path;continue;}
                using(var first=Open(match))using(var other=Open(path)){
                    if(first.Length!=other.Length)return String.Empty;
                    if(signature==null)signature=Signature(first);
                    if(signature!=Signature(other))return String.Empty;
                }
            }
            return match;
        }
        internal static string Resolve(GalleryStore store,Photo photo,CancellationToken token) {
            if(!photo.Linked || !photo.HasCrop)return photo.Original;
            var saved=Read(photo.Folder);
            string previous=saved==null?photo.Original:saved.Path;
            if(saved!=null && Matches(saved,previous))return previous;
            // A cover cut from an outside picture doesn't look like its original, so it is never searched for by its looks.
            if(saved==null && photo.IsOutside) {
                if(File.Exists(previous))return previous;
                throw new FileNotFoundException(L.T("Could not locate the original file."),previous);
            }
            byte[] thumb=saved==null?File.ReadAllBytes(photo.Thumb):null;
            if(saved==null && File.Exists(previous) && CropMatches(photo,previous,thumb,token)){
                Remember(photo,previous);return previous;
            }
            token.ThrowIfCancellationRequested();
            string path=saved==null?null:PathById(saved);
            string name=Path.GetFileName(previous);
            if(path==null && saved!=null && File.Exists(previous) && EditedInPlace(saved,previous))path=previous;
            // Its drive is unplugged: never settle for a look-alike copy somewhere else.
            if(path==null && DriveLinks.DriveAway(previous))throw new FileNotFoundException(L.T("The drive with this file isn't connected."),previous);
            if(path==null){
                var remaining=new List<string>();
                var named=new HashSet<string>(Paths);
                foreach(var file in Nearby(store,photo,previous,token)){
                    token.ThrowIfCancellationRequested();
                    string candidate=file.FullName;
                    if(!Compatible(candidate,name) || (saved!=null && file.Length!=saved.Length))continue;
                    if(saved!=null){if(Matches(saved,candidate)){path=candidate;break;}}
                    else if(Paths.Equals(Path.GetFileName(candidate),name))named.Add(candidate);
                    else remaining.Add(candidate);
                }
                if(path==null && saved==null)path=LegacyMatch(photo,named,thumb,token);
                if(path==null){
                    var indexed=Indexed(name,saved,token).Where(p=>Compatible(p,name) && File.Exists(p));
                    path=saved==null?LegacyMatch(photo,indexed.Where(p=>!named.Contains(p)),thumb,token):
                        indexed.FirstOrDefault(p=>Matches(saved,p));
                }
                if(path==null && saved==null)path=LegacyMatch(photo,remaining,thumb,token);
            }
            token.ThrowIfCancellationRequested();
            if(String.IsNullOrEmpty(path))throw new FileNotFoundException(L.T("Could not locate the original file."),previous);
            Remember(photo,path);return path;
        }
        internal static bool MatchesOriginal(Photo photo,string path,CancellationToken token) {
            var saved=Read(photo.Folder);
            // Without its saved facts, a cover cut from an outside picture can't vouch for any file: the cover doesn't look like its original.
            if(saved==null && photo.IsOutside)return false;
            return saved!=null?Matches(saved,path) || EditedInPlace(saved,path):CropMatches(photo,path,File.ReadAllBytes(photo.Thumb),token);
        }
        internal static bool TryChosen(Photo photo,string path,CancellationToken token) {
            if(!MatchesOriginal(photo,path,token))return false;
            Remember(photo,path);return true;
        }
        internal static void TrackExisting(GalleryStore store,CancellationToken token) {
            try {
                foreach(var album in store.Albums().Where(a=>a.Source!=null)){
                    foreach(string folder in Directory.GetDirectories(album.Folder,"p-*")){
                        token.ThrowIfCancellationRequested();
                        if(File.Exists(Path.Combine(folder,Metadata)))continue;
                        try {
                            var photo=GalleryStore.Read(folder);
                            string original=Path.Combine(album.Source,Path.GetFileName(photo.Name));
                            if(File.Exists(original) && CropMatches(photo,original,File.ReadAllBytes(photo.Thumb),token))Remember(photo,original,true);
                        }catch(IOException quiet) {ErrorLog.Quiet(quiet);}catch(UnauthorizedAccessException quiet) {ErrorLog.Quiet(quiet);}catch(System.Xml.XmlException quiet) {ErrorLog.Quiet(quiet);}
                    }
                }
            }catch(OperationCanceledException){}catch(Exception quiet) {ErrorLog.Quiet(quiet);/* Tracking can retry at next startup or on open. */}
        }
    }
}
