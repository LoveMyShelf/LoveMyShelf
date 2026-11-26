using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows.Media.Imaging;

namespace LoveMyShelf {
    // Every picture type LoveMyShelf lists and opens, in one place.
    internal static class PictureTypes {
        internal static readonly string[] Extensions={".jpg",".jpeg",".jfif",".png",".bmp",".tif",".tiff",".gif",".webp",".heic",".heif",".avif"};
        static readonly HashSet<string> all=new HashSet<string>(Extensions,StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> modern=new HashSet<string>(StringComparer.OrdinalIgnoreCase) {".heic",".heif",".avif"};
        internal static bool IsPicture(string path) {return all.Contains(Path.GetExtension(path)??"");}
        // HEIC, HEIF and AVIF are read with the bundled FFmpeg first: it turns iPhone photos upright and joins their tiles
        // the same way on every PC, with or without Microsoft's HEIF and HEVC extensions. Windows' own reader is the fallback.
        internal static bool UsesFfmpeg(string path) {return modern.Contains(Path.GetExtension(path)??"");}

        // A few at a time, so a folder full of iPhone photos doesn't start dozens of FFmpeg copies at once.
        static readonly SemaphoreSlim gate=new SemaphoreSlim(Math.Max(2,Math.Min(4,Environment.ProcessorCount/2)));

        // The first picture of the file, upright, no larger than maxSide on its long side (0 = full size).
        // FFmpeg writes the whole picture and WPF shrinks it while reading: a tiled iPhone photo is put together
        // by FFmpeg's own filter, which can't be followed by a size filter. Transparency is not kept (Windows' reader drops it too).
        internal static BitmapSource Decode(string path,int maxSide) {
            gate.Wait();
            try {
                using(var p=VideoFrames.Run("ffmpeg","-v error -nostdin -i "+VideoFrames.Quote(path)+" -an -sn -dn -frames:v 1 -c:v bmp -pix_fmt bgr24 -f image2pipe pipe:1"))
                using(var memory=new MemoryStream()) {
                    p.StandardOutput.BaseStream.CopyTo(memory);
                    p.WaitForExit();
                    if(memory.Length<54)throw new NotSupportedException("FFmpeg could not read the picture.");
                    memory.Position=0;
                    int w=BitConverter.ToInt32(memory.GetBuffer(),18),h=Math.Abs(BitConverter.ToInt32(memory.GetBuffer(),22));
                    var image=new BitmapImage();
                    image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.StreamSource=memory;
                    if(maxSide>0 && Math.Max(w,h)>maxSide) {
                        if(w>=h)image.DecodePixelWidth=maxSide;else image.DecodePixelHeight=maxSide;
                    }
                    image.EndInit();image.Freeze();
                    return image;
                }
            } finally {gate.Release();}
        }

        // Pixel size: from the file's header when Windows can read it, otherwise by reading the picture itself.
        internal static void ReadSize(string path,out int w,out int h) {
            try {
                using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete)) {
                    var frame=BitmapDecoder.Create(stream,BitmapCreateOptions.DelayCreation,BitmapCacheOption.None).Frames[0];
                    w=frame.PixelWidth;h=frame.PixelHeight;
                    return;
                }
            } catch(Exception) {
                if(!UsesFfmpeg(path))throw;
            }
            var picture=Decode(path,0);
            w=picture.PixelWidth;h=picture.PixelHeight;
        }
    }
}
