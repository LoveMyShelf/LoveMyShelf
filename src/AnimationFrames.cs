using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing=System.Drawing;
using Imaging=System.Drawing.Imaging;

namespace LoveMyShelf {
    // Each reader belongs to one decoding thread. Only detached, frozen images leave it.
    internal sealed class AnimationFrames : IDisposable {
        readonly FileStream stream;
        Drawing.Image gif;
        BitmapDecoder webp;
        readonly VideoFrames video;
        public FrameTimes Times {get;private set;}
        readonly List<int> delays=new List<int>();
        public int Count { get; private set; }

        public static bool Supports(string path) {
            string ext=Path.GetExtension(path);
            return VideoFrames.Supports(path) || String.Equals(ext,".gif",StringComparison.OrdinalIgnoreCase) ||
                String.Equals(ext,".webp",StringComparison.OrdinalIgnoreCase);
        }
        public AnimationFrames(string path,CancellationToken token=default(CancellationToken),FrameTimes nearby=null) {
            if(VideoFrames.Supports(path)) {
                video=nearby==null?new VideoFrames(path,token):new VideoFrames(path,nearby,token);
                Times=video.Times;Count=Times.Count;return;
            }
            stream=File.OpenRead(path);
            try {
                if(String.Equals(Path.GetExtension(path),".gif",StringComparison.OrdinalIgnoreCase)) {
                    gif=Drawing.Image.FromStream(stream,false,false);
                    Count=gif.GetFrameCount(Imaging.FrameDimension.Time);
                    if(Count>1 && Array.IndexOf(gif.PropertyIdList,0x5100)>=0) {
                        byte[] data=gif.GetPropertyItem(0x5100).Value;
                        for(int i=0;i+4<=data.Length;i+=4) delays.Add(BitConverter.ToInt32(data,i)*10);
                    }
                } else {
                    ReadWebpDelays();
                    stream.Position=0;
                    webp=BitmapDecoder.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.None);
                    Count=webp.Frames.Count;
                    if(delays.Count>1 && Count!=delays.Count) throw new NotSupportedException(L.T("Windows on this computer can't read every scene of this WebP."));
                }
                if(Count<1) throw new InvalidDataException(L.T("There are no scenes to show."));
                var starts=new double[Count];double ms=0;
                for(int i=0;i<Count;i++){starts[i]=ms;ms+=Delay(i);}
                Times=new FrameTimes(starts,ms);
            } catch { Dispose(); throw; }
        }
        void ReadWebpDelays() {
            using(var input=new BinaryReader(stream,Encoding.ASCII,true)) {
                if(input.ReadUInt32()!=0x46464952) throw new InvalidDataException(L.T("Could not read the WebP file."));
                long end=Math.Min(stream.Length,8L+input.ReadUInt32());
                if(input.ReadUInt32()!=0x50424557) throw new InvalidDataException(L.T("Could not read the WebP file."));
                while(stream.Position+8<=end) {
                    uint tag=input.ReadUInt32(),size=input.ReadUInt32();
                    long start=stream.Position, next=start+size+(size&1);
                    if(next>end) throw new InvalidDataException(L.T("The WebP file is incomplete."));
                    if(tag==0x464D4E41 && size>=16) { // ANMF stores duration as 24-bit milliseconds.
                        stream.Position=start+12;
                        delays.Add(input.ReadByte() | (input.ReadByte()<<8) | (input.ReadByte()<<16));
                    }
                    stream.Position=next;
                }
            }
        }
        public int Delay(int index) { return index<delays.Count && delays[index]>0?delays[index]:100; }
        public BitmapSource Read(int index,int maxSide) {
            if(index<0 || index>=Count) throw new ArgumentOutOfRangeException("index");
            if(video!=null)return video.Read(index,maxSide);
            if(gif!=null) return ReadGif(index,maxSide);
            BitmapSource frame=new FormatConvertedBitmap(webp.Frames[index],PixelFormats.Pbgra32,null,0);
            double scale=Scale(frame.PixelWidth,frame.PixelHeight,maxSide);
            if(scale<1) frame=new TransformedBitmap(frame,new ScaleTransform(scale,scale));
            var snapshot=new WriteableBitmap(frame); snapshot.Freeze(); return snapshot;
        }
        static double Scale(int width,int height,int maxSide) {
            return maxSide>0?Math.Min(1,maxSide/(double)Math.Max(width,height)):1;
        }
        BitmapSource ReadGif(int index,int maxSide) {
            if(Count>1) gif.SelectActiveFrame(Imaging.FrameDimension.Time,index);
            double scale=Scale(gif.Width,gif.Height,maxSide);
            int w=Math.Max(1,(int)Math.Round(gif.Width*scale)),h=Math.Max(1,(int)Math.Round(gif.Height*scale));
            var bounds=new Drawing.Rectangle(0,0,w,h);
            using(var canvas=new Drawing.Bitmap(w,h,Imaging.PixelFormat.Format32bppPArgb)) {
                using(var g=Drawing.Graphics.FromImage(canvas)) {
                    g.CompositingMode=Drawing.Drawing2D.CompositingMode.SourceCopy;
                    if(scale<1) g.InterpolationMode=Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(gif,bounds,0,0,gif.Width,gif.Height,Drawing.GraphicsUnit.Pixel);
                }
                var data=canvas.LockBits(bounds,Imaging.ImageLockMode.ReadOnly,canvas.PixelFormat);
                try {
                    var frame=BitmapSource.Create(w,h,96,96,PixelFormats.Pbgra32,null,data.Scan0,checked(data.Stride*h),data.Stride);
                    frame.Freeze();return frame;
                } finally { canvas.UnlockBits(data); }
            }
        }
        public static BitmapSource Still(string path,int index,int maxSide) {
            if(index==0 && VideoFrames.Supports(path))return VideoFrames.First(path,maxSide);
            using(var file=new AnimationFrames(path)) return file.Read(index,maxSide);
        }
        internal static BitmapSource SavedFrame(string path,Photo photo,int maxSide,CancellationToken token) {
            if(photo.FrameTime.HasValue && VideoFrames.Supports(path))
                return VideoFrames.AtTime(path,photo.FrameTime.Value,photo.FrameTimeOffset,maxSide,token);
            if(photo.FrameIndex==0 && maxSide>0 && VideoFrames.Supports(path))
                return VideoFrames.AtTime(path,0,0,maxSide,token);
            using(var file=new AnimationFrames(path,token))return file.Read(photo.FrameIndex,maxSide);
        }
        public void Dispose() { if(video!=null)video.Dispose();if(gif!=null) gif.Dispose();if(stream!=null)stream.Dispose(); }
    }
}
