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

        // Which part of a cover's height holds the picture (0..1), by the same rule as the JPG cover above.
        // True when there is empty space above or below it.
        internal static bool VisibleRows(double w,double h,Rect frame,double rotation,out double top,out double bottom) {
            top=0;bottom=1;
            if(frame.Width<=0 || frame.Height<=0 || w<=0 || h<=0)return false;
            if(rotation==0) {
                top=Math.Max(0,-frame.Y/frame.Height);
                bottom=Math.Min(1,(1-frame.Y)/frame.Height);
            } else {
                var destination=new Rect(0,0,frame.Width*w,frame.Height*h);
                var footprint=new RectangleGeometry(new Rect(0,0,w,h),0,0,new MatrixTransform(Pictures.CoverTransform(w,h,frame,rotation,destination)));
                var visible=Geometry.Combine(footprint,new RectangleGeometry(destination),GeometryCombineMode.Intersect,null).Bounds;
                if(visible.IsEmpty || visible.Width<=0 || visible.Height<=0)return false;
                top=Math.Max(0,visible.Top/destination.Height);
                bottom=Math.Min(1,visible.Bottom/destination.Height);
            }
            if(bottom<=top){top=0;bottom=1;return false;}
            return top>0.001 || bottom<0.999;
        }
        // The same when only the saved frame is known: the cover's shape in pixels (9:16 for covers made before 1.0.9)
        // tells the original's shape.
        internal static bool VisibleRows(Rect frame,double rotation,double aspect,out double top,out double bottom) {
            if(frame.Width<=0 || frame.Height<=0 || aspect<=0){top=0;bottom=1;return false;}
            return VisibleRows(aspect*frame.Height/frame.Width,1,frame,rotation,out top,out bottom);
        }
        // Takes the empty rows off a picture that still has the cover's full shape (a quick preview, or a GIF cover made
        // before covers were trimmed). A picture already trimmed has another shape and stays as it is.
        internal static BitmapSource WithoutBars(BitmapSource image,double top,double bottom,double aspect) {
            if(image==null || aspect<=0 || Math.Abs(image.PixelHeight/(double)image.PixelWidth-1/aspect)>0.012/aspect)return image;
            int first=Math.Max(0,(int)Math.Ceiling(top*image.PixelHeight-1e-6));
            int last=Math.Min(image.PixelHeight,(int)Math.Floor(bottom*image.PixelHeight+1e-6));
            return last-first<2?image:CropRows(image,first,last);
        }

        internal static void Save(BitmapSource source,Rect crop,double rotation,Stream output) {
            Save(source,crop,rotation,output,1920,false,CancellationToken.None);
        }

        internal static void Save(BitmapSource source,Rect crop,double rotation,Stream output,int maxHeight,bool native,CancellationToken token,int brightness=0) {
            double width=source.PixelWidth*crop.Width,height=source.PixelHeight*crop.Height;
            // maxHeight is the long side: 1080 x 1920 for a 9:16 cover, as before; a wide cover gets 1920 across.
            double scale=maxHeight==0?1:Math.Min(1,maxHeight/Math.Max(width,height));
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
            catch(FormatException) {return null;} // a damaged picture (FileFormatException)
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
