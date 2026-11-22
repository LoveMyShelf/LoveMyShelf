using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LoveMyShelf {
    internal enum CoverOutputMode { SavedImage, Native }

    internal static class CoverImages {
        // Saved coordinates are fractions of the upright original. Keep the crop at
        // native resolution, copying source pixels directly when the angle is zero.
        internal static BitmapSource NativeCover(BitmapSource source,Rect frame,double rotation,CancellationToken token,int brightness=0,
            bool trimVerticalPadding=false) {
            int w=source.PixelWidth,h=source.PixelHeight;
            int width=Math.Max(1,checked((int)Math.Round(frame.Width*w)));
            int height=Math.Max(1,checked((int)Math.Round(frame.Height*h)));
            if(rotation!=0 || brightness!=0) {
                var rendered=Pictures.RenderRotatedCover(source,frame,rotation,width,height,brightness);
                if(!trimVerticalPadding)return rendered;
                // The bounds of the visible source tell us which rows contain image pixels, even for dark photos.
                var destination=new Rect(0,0,width,height);
                var transform=new MatrixTransform(Pictures.CoverTransform(w,h,frame,rotation,destination));
                var footprint=new RectangleGeometry(new Rect(0,0,w,h),0,0,transform);
                var visible=Geometry.Combine(footprint,new RectangleGeometry(destination),GeometryCombineMode.Intersect,null).Bounds;
                if(visible.IsEmpty || visible.Width<=0 || visible.Height<=0)return rendered;
                int first=Math.Max(0,(int)Math.Floor(visible.Top+1e-7));
                int last=Math.Min(height,(int)Math.Ceiling(visible.Bottom-1e-7));
                return CropRows(rendered,first,last);
            }
            int x=checked((int)Math.Round(frame.X*w)), y=checked((int)Math.Round(frame.Y*h));
            int left=Math.Max(0,x),top=Math.Max(0,y);
            int columns=Math.Min(w,checked(x+width))-left,rows=Math.Min(h,checked(y+height))-top;
            bool trim=trimVerticalPadding && columns>0 && rows>0;
            var result=new WriteableBitmap(width,trim?rows:height,96,96,PixelFormats.Bgr32,null);
            if(columns>0 && rows>0) {
                // Premultiplied RGB over black has the same color bytes as opaque BGR.
                var pixels=new FormatConvertedBitmap(source,PixelFormats.Pbgra32,null,0);
                int stride=checked(columns*4);
                int block=Math.Min(rows,Math.Max(1,1048576/stride));
                var buffer=new byte[checked(block*stride)];
                for(int row=0;row<rows;row+=block) {
                    token.ThrowIfCancellationRequested();
                    int n=Math.Min(block,rows-row);
                    pixels.CopyPixels(new Int32Rect(left,top+row,columns,n),buffer,stride,0);
                    result.WritePixels(new Int32Rect(left-x,(trim?0:top-y)+row,columns,n),buffer,stride,0);
                }
            }
            result.Freeze();return result;
        }

        internal static void Save(BitmapSource source,Rect crop,double rotation,Stream output) {
            Save(source,crop,rotation,output,1920,false,CancellationToken.None);
        }

        internal static void Save(BitmapSource source,Rect crop,double rotation,Stream output,int maxHeight,bool native,CancellationToken token,int brightness=0) {
            double width=source.PixelWidth*crop.Width,height=source.PixelHeight*crop.Height;
            double scale=maxHeight==0?1:Math.Min(1,Math.Min(maxHeight*9.0/16/width,maxHeight/height));
            token.ThrowIfCancellationRequested();
            var image=native && scale==1?NativeCover(source,crop,rotation,token,brightness):
                Pictures.RenderRotatedCover(source,crop,rotation,
                Math.Max(1,(int)Math.Round(width*scale)),Math.Max(1,(int)Math.Round(height*scale)),brightness);
            var encoder=new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));encoder.Save(output);
        }

        internal static BitmapSource Load(Stream input) {
            input.Position=0;
            var frame=BitmapFrame.Create(input,BitmapCreateOptions.None,BitmapCacheOption.OnLoad);
            frame.Freeze();
            return frame;
        }

        internal static BitmapSource TryLoad(Photo photo) {
            try {
                using(var file=File.Open(photo.DisplayImage,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete))
                    return Load(file);
            } catch(IOException) {return null;}
            catch(NotSupportedException) {return null;}
        }

        internal static BitmapSource TrimBlackBands(BitmapSource cover) {
            // Inspect a small copy, then crop the original pixels for fullscreen display.
            double scale=Math.Min(1,768.0/Math.Max(cover.PixelWidth,cover.PixelHeight));
            BitmapSource sample=scale<1?new TransformedBitmap(cover,new ScaleTransform(scale,scale)):cover;
            var rgb=new FormatConvertedBitmap(sample,PixelFormats.Bgr24,null,0);
            int width=rgb.PixelWidth,height=rgb.PixelHeight,stride=width*3;
            var pixels=new byte[stride*height];
            rgb.CopyPixels(pixels,stride,0);
            int top=0,bottom=height;
            while(top<bottom && IsBlackRow(pixels,top*stride,width))top++;
            while(bottom>top && IsBlackRow(pixels,(bottom-1)*stride,width))bottom--;
            if(bottom-top<Math.Max(1,height/10))return cover;

            // A single dark row can belong to the subject or be a JPEG edge artifact.
            if(top<2)top=0;
            if(height-bottom<2)bottom=height;
            if(top==0 && bottom==height)return cover;
            int first=(int)Math.Round(top*(double)cover.PixelHeight/height);
            int last=(int)Math.Round(bottom*(double)cover.PixelHeight/height);
            return CropRows(cover,first,last);
        }
        static BitmapSource CropRows(BitmapSource cover,int first,int last) {
            if(last<=first || (first==0 && last==cover.PixelHeight))return cover;
            var result=new CroppedBitmap(cover,new Int32Rect(0,first,cover.PixelWidth,last-first));
            result.Freeze();return result;
        }
        static bool IsBlackRow(byte[] pixels,int start,int width) {
            int nonBlack=0;
            for(int i=start;i<start+width*3;i+=3) {
                if((pixels[i]>12 || pixels[i+1]>12 || pixels[i+2]>12) && ++nonBlack>width/100)return false;
            }
            return true;
        }

    }
}
