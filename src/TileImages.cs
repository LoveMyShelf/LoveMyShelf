using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LoveMyShelf {
    // Pictures for grid tiles. A tile without a cover shows what File Explorer shows (the Windows thumbnail);
    // only when Windows has none does LoveMyShelf read the file itself.
    internal static class TileImages {
        // Windows thumbnails: their own threads, since thumbnail handlers expect a single-threaded apartment.
        static readonly TileQueue shell=new TileQueue(3,true,"Tile thumbnails");
        // Cover files and photos Windows could not show.
        static readonly TileQueue files=new TileQueue(3,false,"Tile pictures");
        // Videos Windows could not show; kept apart so they never hold up photos.
        static readonly TileQueue videos=new TileQueue(2,false,"Tile videos");

        // side: the tile height in device pixels. row and center place the job so the middle of the screen loads first.
        internal static async Task<BitmapSource> Load(Photo photo,int side,int row,Func<int> center,CancellationToken token) {
            string path=TileRow.ShownImage(photo);
            bool cover=photo.ShowsCover;
            long ticks=Ticks(path);
            string key=(cover?"c|":"o|")+side+"|"+path;
            var hit=TileCache.Get(key,ticks);
            if(hit!=null)return hit;
            BitmapSource image;
            if(cover) image=await files.Add(()=>Pictures.Load(path,side),row,center,token);
            else {
                image=await shell.Add(()=>ShellThumbnails.Get(path,side),row,center,token);
                if(image==null) {
                    bool video=VideoFrames.Supports(path);
                    image=await (video?videos:files).Add(()=>video?VideoStill(path,side,token):Pictures.Load(path,side),row,center,token);
                }
            }
            if(image!=null)TileCache.Put(key,ticks,image);
            return image;
        }
        // The small copy kept with the record, for when the cover file can't be read (its drive is away or busy).
        internal static Task<BitmapSource> LoadSavedCopy(Photo photo,int side,int row,Func<int> center,CancellationToken token) {
            string thumb=photo.Thumb;
            return files.Add(()=>Pictures.Load(thumb,side),row,center,token);
        }
        // Reads the first picture inside LoveMyShelf (libmpv) instead of starting FFmpeg; FFmpeg is the last resort.
        static BitmapSource VideoStill(string path,int side,CancellationToken token) {
            MpvThumbnails thumbs=null;
            try {
                thumbs=MpvThumbnails.TryOpen(path,token);
                if(thumbs!=null) {
                    try {return thumbs.Read(0,side,token);}
                    catch(OperationCanceledException){throw;}
                    catch(Exception){}
                }
            } finally {if(thumbs!=null)thumbs.Dispose();}
            token.ThrowIfCancellationRequested();
            return Pictures.Load(path,side);
        }
        static long Ticks(string path) {
            try {return File.GetLastWriteTimeUtc(path).Ticks;} catch(Exception){return 0;}
        }
    }

    // Recently shown tile pictures, so scrolling back shows them at once. Used on the UI thread.
    internal static class TileCache {
        const long Limit=300L*1024*1024;
        sealed class Entry {public string Key;public long Ticks,Bytes;public BitmapSource Image;}
        static readonly LinkedList<Entry> order=new LinkedList<Entry>();
        static readonly Dictionary<string,LinkedListNode<Entry>> entries=new Dictionary<string,LinkedListNode<Entry>>(StringComparer.OrdinalIgnoreCase);
        static long bytes;
        internal static BitmapSource Get(string key,long ticks) {
            LinkedListNode<Entry> node;
            if(!entries.TryGetValue(key,out node))return null;
            if(node.Value.Ticks!=ticks){Remove(node);return null;}
            order.Remove(node);order.AddFirst(node);
            return node.Value.Image;
        }
        internal static void Put(string key,long ticks,BitmapSource image) {
            LinkedListNode<Entry> old;
            if(entries.TryGetValue(key,out old))Remove(old);
            long size=(long)image.PixelWidth*image.PixelHeight*4;
            if(size>Limit/4)return;
            var node=order.AddFirst(new Entry {Key=key,Ticks=ticks,Bytes=size,Image=image});
            entries[key]=node;bytes+=size;
            while(bytes>Limit && order.Last!=null)Remove(order.Last);
        }
        static void Remove(LinkedListNode<Entry> node) {
            order.Remove(node);entries.Remove(node.Value.Key);bytes-=node.Value.Bytes;
        }
        // Lets go of every picture (while every folder window is minimized); they are read again when needed.
        internal static void Clear() {order.Clear();entries.Clear();bytes=0;}
    }

    // A small set of worker threads. The waiting job closest to the middle of the visible rows runs next;
    // jobs whose tile has left the screen are dropped before they start.
    internal sealed class TileQueue {
        sealed class Job {
            public Func<BitmapSource> Work;public int Row;public Func<int> Center;public CancellationToken Token;public long Order;
            public TaskCompletionSource<BitmapSource> Done=new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        readonly List<Job> jobs=new List<Job>();
        long added;
        internal TileQueue(int workers,bool sta,string name) {
            for(int i=0;i<workers;i++) {
                var thread=new Thread(Run){IsBackground=true,Name=name,Priority=ThreadPriority.BelowNormal};
                if(sta)thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
            }
        }
        internal Task<BitmapSource> Add(Func<BitmapSource> work,int row,Func<int> center,CancellationToken token) {
            var job=new Job {Work=work,Row=row,Center=center,Token=token};
            if(token.IsCancellationRequested){job.Done.TrySetCanceled();return job.Done.Task;}
            lock(jobs){job.Order=++added;jobs.Add(job);Monitor.Pulse(jobs);}
            return job.Done.Task;
        }
        void Run() {
            while(true) {
                Job job=null;
                lock(jobs) {
                    while(job==null) {
                        for(int i=jobs.Count-1;i>=0;i--)
                            if(jobs[i].Token.IsCancellationRequested){jobs[i].Done.TrySetCanceled();jobs.RemoveAt(i);}
                        if(jobs.Count==0){Monitor.Wait(jobs);continue;}
                        int best=0;long bestDistance=Int64.MaxValue;
                        for(int i=0;i<jobs.Count;i++) {
                            int center;
                            try{center=jobs[i].Center==null?jobs[i].Row:jobs[i].Center();}catch(Exception){center=jobs[i].Row;}
                            long distance=Math.Abs((long)jobs[i].Row-center);
                            // Among equals, the newest request (what was just scrolled to) goes first.
                            if(distance<bestDistance || (distance==bestDistance && jobs[i].Order>jobs[best].Order)){best=i;bestDistance=distance;}
                        }
                        job=jobs[best];jobs.RemoveAt(best);
                    }
                }
                try {
                    job.Token.ThrowIfCancellationRequested();
                    job.Done.TrySetResult(job.Work());
                }
                catch(OperationCanceledException){job.Done.TrySetCanceled();}
                catch(Exception ex){job.Done.TrySetException(ex);}
            }
        }
    }

    // The thumbnail File Explorer shows, from the Windows thumbnail cache (made by Windows when missing).
    internal static class ShellThumbnails {
        const int ResizeToFit=0,ThumbnailOnly=8;
        // Returns null when Windows has no thumbnail for the file (only an icon).
        internal static BitmapSource Get(string path,int side) {
            IShellItemImageFactory factory=null;
            IntPtr bitmap=IntPtr.Zero;
            try {
                Guid id=typeof(IShellItemImageFactory).GUID;
                if(SHCreateItemFromParsingName(path,IntPtr.Zero,ref id,out factory)!=0 || factory==null)return null;
                if(factory.GetImage(new NativeSize {cx=side,cy=side},ResizeToFit|ThumbnailOnly,out bitmap)!=0 || bitmap==IntPtr.Zero)return null;
                return FromHBitmap(bitmap);
            } catch(Exception) {return null;}
            finally {
                if(bitmap!=IntPtr.Zero)DeleteObject(bitmap);
                if(factory!=null)Marshal.ReleaseComObject(factory);
            }
        }
        static BitmapSource FromHBitmap(IntPtr bitmap) {
            NativeBitmap info;
            if(GetObject(bitmap,Marshal.SizeOf(typeof(NativeBitmap)),out info)==0 || info.bmWidth<=0 || info.bmHeight==0)return null;
            int w=info.bmWidth,h=Math.Abs(info.bmHeight),stride=w*4;
            var header=new BitmapInfoHeader {biSize=Marshal.SizeOf(typeof(BitmapInfoHeader)),biWidth=w,biHeight=-h,biPlanes=1,biBitCount=32};
            var pixels=new byte[stride*h];
            IntPtr dc=GetDC(IntPtr.Zero);
            try {if(GetDIBits(dc,bitmap,0,(uint)h,pixels,ref header,0)==0)return null;}
            finally {ReleaseDC(IntPtr.Zero,dc);}
            // Most thumbnails (photos, videos) leave alpha at zero: they are opaque.
            bool alpha=false;
            for(int i=3;i<pixels.Length;i+=4)if(pixels[i]!=0){alpha=true;break;}
            var image=BitmapSource.Create(w,h,96,96,alpha?PixelFormats.Pbgra32:PixelFormats.Bgr32,null,pixels,stride);
            image.Freeze();
            if(alpha) {
                var picture=Letterbox(pixels,w,h);
                if(picture.HasValue) {
                    var cropped=new CroppedBitmap(image,picture.Value);cropped.Freeze();return cropped;
                }
            }
            return image;
        }
        // Some thumbnail handlers (WebP) put the picture in the middle of a square and leave the sides (or the top
        // and bottom) see-through. A tile fits the whole square, so the picture came out small. Returns the picture
        // part only when the see-through edges are on two opposite sides, about the same width, with a fully solid
        // picture edge next to them (Windows pads a tall picture into a square or a less tall shape); a picture that is see-through itself (a logo, a sticker) is left as it is.
        static System.Windows.Int32Rect? Letterbox(byte[] pixels,int w,int h) {
            int stride=w*4;
            Func<int,bool> clearColumn=x=>{for(int y=0;y<h;y++)if(pixels[y*stride+x*4+3]!=0)return false;return true;};
            Func<int,bool> clearRow=y=>{for(int x=0;x<w;x++)if(pixels[y*stride+x*4+3]!=0)return false;return true;};
            Func<int,bool> solidColumn=x=>{for(int y=0;y<h;y++)if(pixels[y*stride+x*4+3]!=255)return false;return true;};
            Func<int,bool> solidRow=y=>{for(int x=0;x<w;x++)if(pixels[y*stride+x*4+3]!=255)return false;return true;};
            int left=0,right=0,top=0,bottom=0;
            while(left<w && clearColumn(left))left++;
            if(left==w)return null;
            while(clearColumn(w-1-right))right++;
            while(clearRow(top))top++;
            while(clearRow(h-1-bottom))bottom++;
            bool sides=left>0 && right>0 && top==0 && bottom==0 && Math.Abs(left-right)<=1;
            bool ends=top>0 && bottom>0 && left==0 && right==0 && Math.Abs(top-bottom)<=1;
            // The picture's edge may land between two pixels and come out half see-through: that one line goes too.
            if(sides) {
                if(!solidColumn(left) && left+2<w-right && solidColumn(left+1))left++;
                if(!solidColumn(w-1-right) && w-2-right>left && solidColumn(w-2-right))right++;
                sides=solidColumn(left) && solidColumn(w-1-right);
            }
            if(ends) {
                if(!solidRow(top) && top+2<h-bottom && solidRow(top+1))top++;
                if(!solidRow(h-1-bottom) && h-2-bottom>top && solidRow(h-2-bottom))bottom++;
                ends=solidRow(top) && solidRow(h-1-bottom);
            }
            if(!sides && !ends)return null;
            return new System.Windows.Int32Rect(left,top,w-left-right,h-top-bottom);
        }
        [ComImport,Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItemImageFactory {
            [PreserveSig] int GetImage(NativeSize size,int flags,out IntPtr bitmap);
        }
        [StructLayout(LayoutKind.Sequential)] struct NativeSize {public int cx,cy;}
        [StructLayout(LayoutKind.Sequential)] struct NativeBitmap {
            public int bmType,bmWidth,bmHeight,bmWidthBytes;public ushort bmPlanes,bmBitsPixel;public IntPtr bmBits;
        }
        [StructLayout(LayoutKind.Sequential)] struct BitmapInfoHeader {
            public int biSize,biWidth,biHeight;public short biPlanes,biBitCount;public int biCompression,biSizeImage,biXPelsPerMeter,biYPelsPerMeter,biClrUsed,biClrImportant;
        }
        [DllImport("shell32.dll",CharSet=CharSet.Unicode)]
        static extern int SHCreateItemFromParsingName(string path,IntPtr bindContext,ref Guid id,[MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);
        [DllImport("gdi32.dll")] static extern int GetObject(IntPtr handle,int size,out NativeBitmap bitmap);
        [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc,IntPtr bitmap,uint start,uint lines,[Out] byte[] bits,ref BitmapInfoHeader info,uint usage);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr handle);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window,IntPtr dc);
    }
}
