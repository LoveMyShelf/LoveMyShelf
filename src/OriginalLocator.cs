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
            finally {try{File.Delete(temp);}catch(IOException){}catch(UnauthorizedAccessException){}}
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
        static bool EditedInPlace(Reference saved,string path) {
            try {
                if(!Paths.Equals(Path.GetFullPath(path),Path.GetFullPath(saved.Path)))return false;
                using(var f=Open(path)){
                    FileInfoNative info;
                    if(!GetFileInformationByHandle(f.SafeFileHandle,out info) || info.Volume!=saved.Volume)return false;
                    return Math.Abs(f.Length-saved.Length)<=Math.Max(64L*1024*1024,saved.Length/10);
                }
            } catch(IOException){return false;}catch(UnauthorizedAccessException){return false;}catch(ArgumentException){return false;}
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
            } catch(IOException){} catch(UnauthorizedAccessException){}
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
            } finally {try{File.Delete(temp);}catch(IOException){}catch(UnauthorizedAccessException){}}
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
                }catch(Exception){/* A missing codec, frame or corrupt candidate is not a match. */}
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
            }catch(OperationCanceledException){throw;}catch(Exception){/* Windows Search is optional. */}
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
                        }catch(IOException){}catch(UnauthorizedAccessException){}catch(System.Xml.XmlException){}
                    }
                }
            }catch(OperationCanceledException){}catch(Exception){/* Tracking can retry at next startup or on open. */}
        }
    }
}
