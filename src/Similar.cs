using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LoveMyShelf {
    // One file looked at by "Find similar photos".
    internal sealed class SimilarFile {
        internal string Path;
        internal long Size,Ticks;          // what the file was when it was read; checked again before anything is done to it
        internal int Width,Height;         // 0 when unknown (videos, files that couldn't be read)
        internal ulong Hash;               // the picture's fingerprint (pHash, 64 bits)
        internal bool HasHash,Flat,Moving,Failed;
        internal string Sha;               // the whole file's SHA-256, only worked out when another file has the same size
        internal bool HasCover;
        internal DateTime Taken,Created;   // read when the file is shown (SimilarInfo.ReadDates); MinValue when unknown
        internal bool DatesRead;
        internal string Name {get {return System.IO.Path.GetFileName(Path);}}
        internal string Folder {get {return System.IO.Path.GetDirectoryName(Path);}}
        internal bool IsPicture {get {return SimilarFiles.IsPicture(Path);}}
        internal long Pixels {get {return (long)Width*Height;}}
        // Remembered "not the same" pairs follow the picture, not the path: a renamed or moved file stays apart.
        internal string Identity {get {return Hash.ToString("x16")+":"+Size.ToString(CultureInfo.InvariantCulture);}}
    }
    internal sealed class SimilarGroup {
        internal List<SimilarFile> Files=new List<SimilarFile>();
        internal SimilarFile Keep;          // the one suggested to keep (only shown as a suggestion)
        internal bool Exact;
        internal long Saving {get {return Files.Where(f=>f!=Keep).Sum(f=>f.Size);}}
    }

    internal static class SimilarFiles {
        // Held while the search writes what it found into a file and while grouping copies it out (they run on different threads).
        internal static readonly object Gate=new object();
        [StructLayout(LayoutKind.Sequential)]
        struct FileInfoByHandle {
            public uint Attributes;public System.Runtime.InteropServices.ComTypes.FILETIME Created,Accessed,Written;
            public uint Volume,SizeHigh,SizeLow,Links,IndexHigh,IndexLow;
        }
        [DllImport("kernel32.dll",SetLastError=true)]
        static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file,out FileInfoByHandle info);
        // The drive's number and the file's own number: the same for one file however it is reached. Null when unknown.
        internal static string IdOf(FileStream stream) {
            FileInfoByHandle info;
            try {
                if(GetFileInformationByHandle(stream.SafeFileHandle,out info) && (info.IndexHigh|info.IndexLow)!=0)
                    return info.Volume.ToString("x8")+":"+info.IndexHigh.ToString("x8")+info.IndexLow.ToString("x8");
            } catch(Exception) {}
            return null;
        }
        internal static bool IsPicture(string path) {return PictureTypes.IsPicture(path);}
        internal static bool Supports(string path) {return IsPicture(path) || VideoFrames.Supports(path);}

        // Every photo and video in the folders (and the folders inside them), as File Explorer shows them.
        // LoveMyShelf's own cover files, hidden and system folders and folder links are left out.
        internal static List<SimilarFile> List(IList<string> folders,bool inside,CancellationToken token,Action<int> progress) {
            var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var found=new List<SimilarFile>();
            var pending=new Stack<string>();
            foreach(string folder in folders)pending.Push(folder);
            var visited=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while(pending.Count>0) {
                token.ThrowIfCancellationRequested();
                string folder=pending.Pop();
                string full;
                try {full=System.IO.Path.GetFullPath(folder).TrimEnd('\\');} catch(Exception) {continue;}
                if(!visited.Add(full))continue;
                DirectoryInfo dir;
                try {dir=new DirectoryInfo(folder);if(!dir.Exists)continue;} catch(Exception) {continue;}
                try {
                    foreach(var file in dir.EnumerateFiles()) {
                        try {
                            if((file.Attributes&(FileAttributes.Hidden|FileAttributes.System|FileAttributes.ReparsePoint))!=0)continue; // a link is not a second copy
                            if(!Supports(file.FullName) || SharedCoverFiles.IsCoverFile(file.FullName))continue;
                            if(!seen.Add(file.FullName))continue;
                            found.Add(new SimilarFile {Path=file.FullName,Size=file.Length,Ticks=file.LastWriteTimeUtc.Ticks});
                            if(found.Count%500==0 && progress!=null)progress(found.Count);
                        } catch(IOException) {} catch(UnauthorizedAccessException) {}
                    }
                    if(!inside)continue;
                    foreach(var sub in dir.EnumerateDirectories()) {
                        try {
                            if((sub.Attributes&(FileAttributes.Hidden|FileAttributes.System|FileAttributes.ReparsePoint))!=0)continue;
                            if(String.Equals(sub.Name,Preferences.SubfolderName,StringComparison.OrdinalIgnoreCase))continue; // covers saved in a folder of their own
                            pending.Push(sub.FullName);
                        } catch(IOException) {} catch(UnauthorizedAccessException) {}
                    }
                } catch(IOException) {} catch(UnauthorizedAccessException) {} catch(System.Security.SecurityException) {}
            }
            if(progress!=null)progress(found.Count);
            return found;
        }
        // Whether LoveMyShelf keeps a cover record for this original (shown on the tile, and it counts for "keep").
        internal static bool HasCover(GalleryStore store,string path) {
            try {
                string record=System.IO.Path.Combine(store.AlbumFolderOf(System.IO.Path.GetDirectoryName(path)),"p-"+GalleryStore.KeyOf(System.IO.Path.GetFileName(path)));
                return File.Exists(System.IO.Path.Combine(record,"photo.xml"));
            } catch(Exception) {return false;}
        }
        // Whether the file is still exactly what was looked at.
        internal static bool Unchanged(SimilarFile file) {
            try {
                var info=new FileInfo(file.Path);
                return info.Exists && info.Length==file.Size && info.LastWriteTimeUtc.Ticks==file.Ticks;
            } catch(Exception) {return false;}
        }
        // Byte for byte the same (for "exactly the same file", checked once more right before a copy goes to the Recycle Bin).
        internal static bool SameBytes(Stream x,Stream y,CancellationToken token) {
            const int Chunk=1<<20;
            if(x.Length!=y.Length)return false;
            x.Position=0;y.Position=0;
            var p=new byte[Chunk];var q=new byte[Chunk];
            for(;;) {
                token.ThrowIfCancellationRequested();
                int n=ReadFull(x,p),m=ReadFull(y,q);
                if(n!=m)return false;
                if(n==0)return true;
                for(int i=0;i<n;i++)if(p[i]!=q[i])return false;
            }
        }
        static int ReadFull(Stream s,byte[] buffer) {
            int total=0;
            while(total<buffer.Length){int n=s.Read(buffer,total,buffer.Length-total);if(n==0)break;total+=n;}
            return total;
        }
        // A quick look first (the start and the end of the file), so files of the same size that differ aren't read whole.
        internal static string Peek(string path) {
            const int Part=64*1024;
            using(var s=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete,Part))
            using(var sha=SHA256.Create()) {
                var buffer=new byte[Part];
                int n=ReadFull(s,buffer);
                sha.TransformBlock(buffer,0,n,null,0);
                if(s.Length>2*Part) {
                    s.Seek(-Part,SeekOrigin.End);
                    n=ReadFull(s,buffer);
                }
                else n=0;
                sha.TransformFinalBlock(buffer,0,n);
                return Hex(sha.Hash);
            }
        }
        internal static string WholeSha(string path,CancellationToken token) {
            const int Chunk=1<<20;
            using(var s=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete,Chunk,FileOptions.SequentialScan))
            using(var sha=SHA256.Create()) {
                var buffer=new byte[Chunk];
                int n;
                while((n=s.Read(buffer,0,buffer.Length))>0) {
                    token.ThrowIfCancellationRequested();
                    sha.TransformBlock(buffer,0,n,null,0);
                }
                sha.TransformFinalBlock(buffer,0,0);
                return Hex(sha.Hash);
            }
        }
        static string Hex(byte[] bytes) {
            var text=new StringBuilder(bytes.Length*2);
            foreach(byte b in bytes)text.Append(b.ToString("x2"));
            return text.ToString();
        }
        internal static int Distance(ulong a,ulong b) {
            ulong x=a^b;int n=0;
            while(x!=0){x&=x-1;n++;}
            return n;
        }
    }

    // What was worked out about each file, kept per folder so the next search only reads new and changed files.
    // %LOCALAPPDATA%\LoveMyShelf\Folders\similar\f-<key>.txt: a head line, then one line per file.
    internal static class SimilarCache {
        const string Version="similar1";
        static string Root(GalleryStore store) {return Path.Combine(store.Root,"similar");}
        static string FileFor(GalleryStore store,string folder) {
            return Path.Combine(Root(store),"f-"+GalleryStore.KeyOf(folder.TrimEnd('\\').ToLowerInvariant())+".txt");
        }
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
        static extern bool GetVolumeInformation(string root,StringBuilder name,int nameSize,out uint serial,out uint maxLength,out uint flags,StringBuilder system,int systemSize);
        // The drive's own number, so another drive that took the same letter doesn't reuse what was read on this one.
        internal static string Volume(string folder) {
            try {
                string root=Path.GetPathRoot(folder);
                if(String.IsNullOrEmpty(root))return "?";
                uint serial,max,flags;
                if(GetVolumeInformation(root.EndsWith("\\")?root:root+"\\",null,0,out serial,out max,out flags,null,0))return serial.ToString("x8");
            } catch(Exception) {}
            return "?";
        }
        // Fills in what is known for these files (all in one folder) and is still true.
        internal static void Fill(GalleryStore store,string folder,List<SimilarFile> files) {
            string path=FileFor(store,folder);
            try {
                if(!File.Exists(path))return;
                var lines=File.ReadAllLines(path,Encoding.UTF8);
                if(lines.Length==0)return;
                var head=lines[0].Split('\t');
                if(head.Length<3 || head[0]!=Version || head[1]!=Volume(folder) || !String.Equals(head[2],folder,StringComparison.OrdinalIgnoreCase))return;
                var byName=files.ToDictionary(f=>f.Name,StringComparer.OrdinalIgnoreCase);
                var inv=CultureInfo.InvariantCulture;
                for(int i=1;i<lines.Length;i++) {
                    var p=lines[i].Split('\t');
                    if(p.Length<8)continue;
                    SimilarFile file;
                    if(!byName.TryGetValue(p[0],out file))continue;
                    long size,ticks;
                    if(!Int64.TryParse(p[1],NumberStyles.Integer,inv,out size) || !Int64.TryParse(p[2],NumberStyles.Integer,inv,out ticks))continue;
                    if(size!=file.Size || ticks!=file.Ticks)continue; // changed since
                    Int32.TryParse(p[3],NumberStyles.Integer,inv,out file.Width);
                    Int32.TryParse(p[4],NumberStyles.Integer,inv,out file.Height);
                    ulong hash=0;
                    if(p[5]=="x")file.Failed=true;
                    else if(p[5].Length==16 && UInt64.TryParse(p[5],NumberStyles.HexNumber,inv,out hash)){file.Hash=hash;file.HasHash=true;}
                    file.Flat=p[6].Contains("f");file.Moving=p[6].Contains("m");
                    if(p[7].Length==64)file.Sha=p[7];
                }
            } catch(IOException) {} catch(UnauthorizedAccessException) {}
        }
        internal static void Save(GalleryStore store,string folder,List<SimilarFile> files) {
            try {
                Directory.CreateDirectory(Root(store));
                string path=FileFor(store,folder),part=path+"."+Guid.NewGuid().ToString("N")+".tmp";
                var inv=CultureInfo.InvariantCulture;
                var text=new StringBuilder();
                text.Append(Version).Append('\t').Append(Volume(folder)).Append('\t').Append(folder).Append('\n');
                foreach(var f in files) {
                    if(!f.HasHash && !f.Failed && f.Sha==null && !f.Moving)continue; // nothing worked out yet
                    text.Append(f.Name).Append('\t').Append(f.Size.ToString(inv)).Append('\t').Append(f.Ticks.ToString(inv)).Append('\t')
                        .Append(f.Width.ToString(inv)).Append('\t').Append(f.Height.ToString(inv)).Append('\t')
                        .Append(f.HasHash?f.Hash.ToString("x16"):f.Failed?"x":"-").Append('\t')
                        .Append(f.Flat?"f":"").Append(f.Moving?"m":"").Append('\t').Append(f.Sha??"-").Append('\n');
                }
                try {
                    File.WriteAllText(part,text.ToString(),new UTF8Encoding(false));
                    if(File.Exists(path))File.Replace(part,path,null);else File.Move(part,path);
                } finally {try {File.Delete(part);} catch(Exception) {}}
            } catch(IOException quiet) {ErrorLog.Quiet(quiet);} catch(UnauthorizedAccessException quiet) {ErrorLog.Quiet(quiet);}
        }
        // Pairs of pictures the person said are not the same.
        static string NotSameFile(GalleryStore store) {return Path.Combine(Root(store),"not-same.txt");}
        internal static HashSet<string> NotSame(GalleryStore store) {
            var set=new HashSet<string>(StringComparer.Ordinal);
            try {
                string path=NotSameFile(store);
                if(File.Exists(path))foreach(string line in File.ReadAllLines(path,Encoding.UTF8))if(line.Length>0)set.Add(line);
            } catch(IOException) {} catch(UnauthorizedAccessException) {}
            return set;
        }
        internal static string Pair(SimilarFile a,SimilarFile b) {
            string x=a.Identity,y=b.Identity;
            return String.CompareOrdinal(x,y)<0?x+"\t"+y:y+"\t"+x;
        }
        internal static void AddNotSame(GalleryStore store,IEnumerable<string> pairs) {
            Directory.CreateDirectory(Root(store));
            File.AppendAllLines(NotSameFile(store),pairs,new UTF8Encoding(false));
        }
    }

    // Works out a picture's fingerprint. Runs in a separate copy of the program: a picture that makes a decoder
    // fail badly ends only that copy, and the search carries on.
    internal static class SimilarEngine {
        internal const string ChildSwitch="--similar";
        const int Side=32;
        static double[,] cosines;

        // LoveMyShelf.exe --similar <list> <output>. list: one picture per line (UTF-8).
        // output: "pic <index> <width> <height> <hash|x> <flags>" per picture; progress as "progress <done> <total>".
        internal static int RunChild(string list,string output) {
            try {
                var paths=File.ReadAllLines(list,Encoding.UTF8);
                using(var writer=new StreamWriter(output,false,new UTF8Encoding(false))) {
                    for(int i=0;i<paths.Length;i++) {
                        int w=0,h=0;ulong hash=0;bool ok=false,flat=false,moving=false;
                        try {ok=Read(paths[i],out w,out h,out hash,out flat,out moving);} catch(Exception) {}
                        var inv=CultureInfo.InvariantCulture;
                        writer.Write("pic "+i+" "+w.ToString(inv)+" "+h.ToString(inv)+" "+(ok?hash.ToString("x16"):moving?"-":"x")+" "+(flat?"f":"")+(moving?"m":"")+"_\n");
                        writer.Flush(); // each picture's line is out before the next picture is opened (it may be the one that breaks)
                        if(i%8==7 || i==paths.Length-1){Console.Out.WriteLine("progress "+(i+1)+" "+paths.Length);Console.Out.Flush();}
                    }
                }
                return 0;
            } catch(Exception ex) {
                try{Console.Error.WriteLine(ex.Message);Console.Error.Flush();}catch(IOException){}
                return 1;
            }
        }
        // Moving pictures (animated GIF and WebP) get no fingerprint in this first version: only exact copies are found.
        static bool Read(string path,out int w,out int h,out ulong hash,out bool flat,out bool moving) {
            w=h=0;hash=0;flat=false;
            moving=Animated(path);
            if(PictureTypes.UsesFfmpeg(path)) {
                PictureTypes.ReadSize(path,out w,out h);
                var upright=Pictures.Load(path,128); // only the first picture of an image sequence
                // The header may give the size before an iPhone photo is turned upright.
                if((upright.PixelWidth>upright.PixelHeight)!=(w>h) && upright.PixelWidth!=upright.PixelHeight){int t=w;w=h;h=t;}
                hash=Fingerprint(Gray(upright),out flat);
                return true;
            }
            using(var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete)) {
                var decoder=BitmapDecoder.Create(input,BitmapCreateOptions.DelayCreation|BitmapCreateOptions.IgnoreColorProfile,BitmapCacheOption.None);
                if(decoder.Frames.Count>1)moving=true;
                w=decoder.Frames[0].PixelWidth;h=decoder.Frames[0].PixelHeight;
            }
            if(moving)return false;
            var picture=Pictures.Load(path,128); // upright (EXIF turn applied) and small
            var gray=Gray(picture);
            hash=Fingerprint(gray,out flat);
            return true;
        }
        // A WebP says in its head whether it moves (VP8X chunk, animation bit).
        static bool Animated(string path) {
            if(!String.Equals(Path.GetExtension(path),".webp",StringComparison.OrdinalIgnoreCase))return false;
            var head=new byte[21];
            using(var s=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete)) {
                if(s.Read(head,0,21)<21)return false;
            }
            return head[12]=='V' && head[13]=='P' && head[14]=='8' && head[15]=='X' && (head[20]&0x02)!=0;
        }
        // The picture as 32x32 brightness values, see-through parts laid on white.
        static double[,] Gray(BitmapSource picture) {
            var bgra=new FormatConvertedBitmap(picture,PixelFormats.Bgra32,null,0);
            int w=bgra.PixelWidth,h=bgra.PixelHeight,stride=w*4;
            var pixels=new byte[stride*h];
            bgra.CopyPixels(pixels,stride,0);
            var sum=new double[Side,Side];var count=new double[Side,Side];
            for(int y=0;y<h;y++) {
                int gy=Math.Min(Side-1,y*Side/h);
                for(int x=0;x<w;x++) {
                    int gx=Math.Min(Side-1,x*Side/w),i=y*stride+x*4;
                    double a=pixels[i+3]/255.0;
                    double lum=0.114*pixels[i]+0.587*pixels[i+1]+0.299*pixels[i+2];
                    sum[gy,gx]+=lum*a+255*(1-a);count[gy,gx]+=1;
                }
            }
            // A picture smaller than 32 pixels leaves cells empty: those take the nearest pixel.
            for(int y=0;y<Side;y++)for(int x=0;x<Side;x++) {
                if(count[y,x]>0){sum[y,x]/=count[y,x];continue;}
                int sx=Math.Min(w-1,x*w/Side),sy=Math.Min(h-1,y*h/Side),i=sy*stride+sx*4;
                double a=pixels[i+3]/255.0;
                sum[y,x]=(0.114*pixels[i]+0.587*pixels[i+1]+0.299*pixels[i+2])*a+255*(1-a);
            }
            return sum;
        }
        // pHash: the low frequencies of the picture's DCT, each above or below their middle value.
        // flat: the picture is nearly one color, so its fingerprint says little (it joins only exact copies).
        internal static ulong Fingerprint(double[,] gray,out bool flat) {
            double mean=0,spread=0;
            for(int y=0;y<Side;y++)for(int x=0;x<Side;x++)mean+=gray[y,x];
            mean/=Side*Side;
            for(int y=0;y<Side;y++)for(int x=0;x<Side;x++)spread+=(gray[y,x]-mean)*(gray[y,x]-mean);
            flat=Math.Sqrt(spread/(Side*Side))<6;
            var c=Cosines();
            var low=new double[64];
            for(int u=0;u<8;u++)for(int v=0;v<8;v++) {
                double total=0;
                for(int y=0;y<Side;y++) {
                    double row=0;
                    for(int x=0;x<Side;x++)row+=gray[y,x]*c[v,x];
                    total+=row*c[u,y];
                }
                low[u*8+v]=total;
            }
            var sorted=low.Skip(1).OrderBy(d=>d).ToArray(); // the first value is the overall brightness: left out of the middle
            double middle=(sorted[31]+sorted[32])/2;
            ulong hash=0;
            for(int i=0;i<64;i++)if(low[i]>middle)hash|=1UL<<i;
            return hash;
        }
        static double[,] Cosines() {
            if(cosines!=null)return cosines;
            var c=new double[8,Side];
            for(int u=0;u<8;u++)for(int x=0;x<Side;x++)c[u,x]=Math.Cos((2*x+1)*u*Math.PI/(2*Side));
            return cosines=c;
        }
    }

    // Reads pictures for fingerprints in the separate copy, a batch at a time (below-normal priority).
    internal sealed class SimilarChild {
        readonly object gate=new object();
        Process child;
        bool stopped;
        internal void Stop() {
            lock(gate) {
                stopped=true;
                if(child!=null)try{child.Kill();}catch(ObjectDisposedException){}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}
                child=null;
            }
        }
        // Fills Hash/Width/Height (or Failed/Moving) of the given pictures. progress: pictures done in this batch.
        internal string Read(List<SimilarFile> batch,Action<int> progress) {
            lock(gate)if(stopped)return null;
            string folder=Path.Combine(Path.GetTempPath(),"LoveMyShelf-similar-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try {
                string list=Path.Combine(folder,"list.txt"),output=Path.Combine(folder,"found.txt");
                File.WriteAllLines(list,batch.Select(f=>f.Path),new UTF8Encoding(false));
                string error=Run(list,output,progress);
                Keep(batch,output);
                lock(gate)return stopped?null:error;
            } finally {try {Directory.Delete(folder,true);} catch(Exception) {}}
        }
        static void Keep(List<SimilarFile> batch,string output) {
            if(!File.Exists(output))return;
            var inv=CultureInfo.InvariantCulture;
            foreach(string line in File.ReadLines(output,Encoding.UTF8)) {
                var p=line.Split(' ');
                // The closing "_" says the line is whole (the copy may have been stopped in the middle of writing).
                if(p.Length!=6 || p[0]!="pic" || !p[5].EndsWith("_"))continue;
                int index,w,h;
                if(!Int32.TryParse(p[1],NumberStyles.Integer,inv,out index) || index<0 || index>=batch.Count)continue;
                Int32.TryParse(p[2],NumberStyles.Integer,inv,out w);Int32.TryParse(p[3],NumberStyles.Integer,inv,out h);
                var file=batch[index];
                string flags=p[5];
                ulong hash=0;
                bool ok=p[4].Length==16 && UInt64.TryParse(p[4],NumberStyles.HexNumber,inv,out hash);
                if(!ok)hash=0;
                lock(SimilarFiles.Gate) {
                    file.Width=w;file.Height=h;
                    file.Flat=flags.Contains("f");file.Moving=flags.Contains("m");
                    if(ok){file.Hash=hash;file.HasHash=true;}
                    else if(p[4]=="x")file.Failed=true;
                }
            }
        }
        string Run(string list,string output,Action<int> progress) {
            var start=new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,
                SimilarEngine.ChildSwitch+" \""+list+"\" \""+output+"\"") {
                UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,
                StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
            var errors=new StringBuilder();
            using(var process=new Process {StartInfo=start}) {
                process.OutputDataReceived+=(s,e)=>{
                    if(e.Data==null || !e.Data.StartsWith("progress "))return;
                    var parts=e.Data.Split(' ');int n;
                    if(parts.Length==3 && Int32.TryParse(parts[1],out n) && progress!=null)progress(n);
                };
                process.ErrorDataReceived+=(s,e)=>{if(e.Data!=null)lock(errors)errors.AppendLine(e.Data);};
                lock(gate) {
                    if(stopped)return null;
                    process.Start();
                    child=process;
                }
                try{process.PriorityClass=ProcessPriorityClass.BelowNormal;}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}
                process.BeginOutputReadLine();process.BeginErrorReadLine();
                process.WaitForExit();
                lock(gate) {
                    child=null;
                    if(process.ExitCode==0 || stopped)return null;
                }
                lock(errors){string message=errors.ToString().Trim();return message.Length>0?message:L.P("It stopped in the middle of reading the pictures.","사진을 읽다가 중간에 멈췄어요.");}
            }
        }
    }

    // Puts files that are the same together.
    internal static class SimilarGroups {
        // Byte-for-byte copies: same size and same SHA-256.
        internal static List<SimilarGroup> Exact(IEnumerable<SimilarFile> files) {
            var groups=new List<SimilarGroup>();
            List<KeyValuePair<string,SimilarFile>> known;
            lock(SimilarFiles.Gate)known=files.Where(f=>f.Sha!=null).Select(f=>new KeyValuePair<string,SimilarFile>(f.Size.ToString(CultureInfo.InvariantCulture)+":"+f.Sha,f)).ToList();
            foreach(var same in known.GroupBy(k=>k.Key,k=>k.Value)) {
                var list=same.ToList();
                if(list.Count<2)continue;
                var group=new SimilarGroup {Exact=true,Files=OrderCopies(list)};
                group.Keep=group.Files[0];
                groups.Add(group);
            }
            return groups.OrderByDescending(g=>g.Saving).ToList();
        }
        // The suggested one to keep: one with a cover, then the most pixels, then the biggest file, then the oldest, then the name.
        // Byte-for-byte copies: one with a cover, then one outside Downloads, Desktop and temporary folders (a copy there
        // is usually the extra one), then the oldest, then by path.
        static List<SimilarFile> OrderCopies(List<SimilarFile> files) {
            return files.OrderByDescending(f=>f.HasCover).ThenBy(f=>SimilarInfo.Throwaway(f.Folder)).ThenBy(f=>f.Ticks)
                .ThenBy(f=>f.Path,StringComparer.OrdinalIgnoreCase).ToList();
        }
        static List<SimilarFile> Order(List<SimilarFile> files) {
            return files.OrderByDescending(f=>f.HasCover).ThenByDescending(f=>f.Pixels).ThenByDescending(f=>f.Size)
                .ThenBy(f=>f.Ticks).ThenBy(f=>f.Path,StringComparer.OrdinalIgnoreCase).ToList();
        }
        // Nearly the same pictures: fingerprints at most "most" bits apart. Each group has a lead picture
        // (picked by a fixed rule, not by the order the files were read in) and every member is close to the lead itself,
        // so a chain of small steps can't pull in a picture that looks different.
        // A group made only of exact copies is left to the other list.
        internal static List<SimilarGroup> Near(List<SimilarFile> all,int most,HashSet<string> notSame) {
            List<SimilarFile> files;ulong[] hashes;
            // A picture's fingerprint arrives from another thread while the search goes on: taken once, here.
            lock(SimilarFiles.Gate) {
                files=Order(all.Where(f=>f.HasHash && !f.Flat && !f.Moving).ToList());
                hashes=files.Select(f=>f.Hash).ToArray();
            }
            int count=files.Count;
            int bands=most+1; // if two fingerprints differ in at most "most" bits, one of most+1 parts is the same in both
            var starts=new int[bands+1];
            for(int b=0;b<=bands;b++)starts[b]=b*64/bands;
            var index=new Dictionary<ulong,List<int>>[bands];
            for(int b=0;b<bands;b++) {
                index[b]=new Dictionary<ulong,List<int>>();
                for(int i=0;i<count;i++) {
                    ulong key=Part(hashes[i],starts[b],starts[b+1]);
                    List<int> list;
                    if(!index[b].TryGetValue(key,out list))index[b][key]=list=new List<int>();
                    list.Add(i);
                }
            }
            // Every picture's close neighbours (found through the parts, then checked bit by bit).
            var neighbours=new List<int>[count];
            var near=new HashSet<int>();
            for(int i=0;i<count;i++) {
                near.Clear();
                for(int b=0;b<bands;b++)
                    foreach(int other in index[b][Part(hashes[i],starts[b],starts[b+1])])
                        if(other!=i)near.Add(other);
                neighbours[i]=near.Where(o=>SimilarFiles.Distance(hashes[i],hashes[o])<=most).OrderBy(o=>o).ToList();
            }
            // The picture close to the most others leads first: the middle of a group, not an edited copy at its edge.
            var leads=Enumerable.Range(0,count).OrderByDescending(i=>neighbours[i].Count).ThenBy(i=>i).ToList();
            var taken=new bool[count];
            var groups=new List<SimilarGroup>();
            foreach(int lead in leads) {
                if(taken[lead] || neighbours[lead].Count==0)continue;
                var members=new List<SimilarFile>{files[lead]};
                var chosen=new List<int>{lead};
                foreach(int other in neighbours[lead]) {
                    if(taken[other])continue;
                    if(notSame.Count>0 && members.Any(m=>notSame.Contains(SimilarCache.Pair(m,files[other]))))continue;
                    members.Add(files[other]);chosen.Add(other);
                }
                if(members.Count<2)continue;
                foreach(int i in chosen)taken[i]=true;
                // Only copies of one file: that is for "exactly the same files".
                if(members.All(m=>m.Sha!=null && m.Sha==members[0].Sha && m.Size==members[0].Size))continue;
                groups.Add(Suggest(members));
            }
            return groups.OrderByDescending(g=>g.Files.Count).ThenByDescending(g=>g.Saving).ToList();
        }
        // The one suggested to keep: the most pixels (the best original; a cover moves to it when the other goes), then one
        // with a cover, then the one closest to all the others (a copy with a watermark or other change sits at the edge),
        // then the oldest. It is listed first.
        static SimilarGroup Suggest(List<SimilarFile> members) {
            var ordered=members.OrderByDescending(f=>f.Pixels).ThenByDescending(f=>f.HasCover)
                .ThenBy(f=>members.Sum(o=>SimilarFiles.Distance(f.Hash,o.Hash))).ThenBy(f=>f.Ticks)
                .ThenBy(f=>f.Path,StringComparer.OrdinalIgnoreCase).ToList();
            return new SimilarGroup {Files=ordered,Keep=ordered[0]};
        }
        static ulong Part(ulong hash,int from,int to) {
            int bits=to-from;
            ulong mask=bits>=64?ulong.MaxValue:(1UL<<bits)-1;
            return (hash>>from)&mask;
        }
    }
}
