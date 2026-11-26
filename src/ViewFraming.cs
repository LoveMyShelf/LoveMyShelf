using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LoveMyShelf {
    // How the large view shows the picture right now (turned, zoomed, moved). The seek-bar thumbnail, the scene list
    // and the cover editor all start from this, so the picture looks the same from one to the next.
    internal sealed class ViewFraming {
        internal double Turn;                         // degrees clockwise, -179 to 180
        internal double PictureWidth,PictureHeight;   // the upright picture, as big as it is on screen
        internal double PanX,PanY;                    // how far its middle sits from the middle of the view
        internal double ShownWidth,ShownHeight;       // the part of the view the picture reaches, around the middle
        // How much bigger than its tile the picture is drawn, so a tile can read a sharper frame.
        internal double Magnify {get {return Math.Max(PictureWidth/ShownWidth,PictureHeight/ShownHeight);}}

        // The cover frame for a new cover: everything that shows in the view right now (the whole picture when it isn't
        // zoomed in), at the size it has on screen, as fractions of the upright image. Its shape is then free to change.
        internal Rect CoverFrame(double imageWidth,double imageHeight) {
            var back=Matrix.Identity;back.Rotate(-Turn);
            Vector middle=back.Transform(new Vector(-PanX,-PanY));
            double x=0.5+middle.X/PictureWidth,y=0.5+middle.Y/PictureHeight;
            double height=ShownHeight/PictureHeight,width=ShownWidth/PictureWidth;
            return new Rect(x-width/2,y-height/2,width,height);
        }
        internal void Draw(DrawingContext dc,ImageSource source,Rect to,int brightness) {
            dc.DrawRectangle(Brushes.Black,null,to);
            dc.PushClip(new RectangleGeometry(to));
            double scale=to.Width/ShownWidth;
            var place=Matrix.Identity;
            place.Translate(-PictureWidth/2,-PictureHeight/2);
            place.Rotate(Turn);
            place.Translate(PanX,PanY);
            place.Scale(scale,scale);
            place.Translate(to.X+to.Width/2,to.Y+to.Height/2);
            dc.PushTransform(new MatrixTransform(place));
            var whole=new Rect(0,0,PictureWidth,PictureHeight);
            var bitmap=source as BitmapSource;
            dc.DrawImage(bitmap!=null?Light.Apply(bitmap,brightness):source,whole);
            dc.Pop();
            dc.Pop();
        }
    }

    // An Image that, once given a framing, shows its picture the way the large view does.
    internal sealed class FramedImage : Image {
        ViewFraming framing;
        int brightness;
        internal ViewFraming Framing {
            get {return framing;}
            set {framing=value;InvalidateMeasure();InvalidateVisual();}
        }
        // The E / W brightness (see Light), with or without a framing.
        internal int Brightness {
            set {if(brightness==value)return;brightness=value;InvalidateVisual();}
        }
        protected override Size MeasureOverride(Size constraint) {
            if(framing==null)return base.MeasureOverride(constraint);
            double aspect=framing.ShownWidth/framing.ShownHeight,width=constraint.Width,height=constraint.Height;
            if(Double.IsInfinity(width) && Double.IsInfinity(height)){width=framing.ShownWidth;height=framing.ShownHeight;}
            else if(Double.IsInfinity(width))width=height*aspect;
            else if(Double.IsInfinity(height))height=width/aspect;
            else if(width/Math.Max(1,height)>aspect)width=height*aspect;
            else height=width/aspect;
            return new Size(width,height);
        }
        protected override Size ArrangeOverride(Size arrangeSize) {
            return framing==null?base.ArrangeOverride(arrangeSize):arrangeSize;
        }
        protected override void OnRender(DrawingContext dc) {
            if(framing==null) {
                // Stretched to fill (the seek-bar thumbnail) or kept whole in the middle (the scene list).
                var bitmap=Source as BitmapSource;
                if(brightness==0 || bitmap==null || ActualWidth<=0 || ActualHeight<=0){base.OnRender(dc);return;}
                var to=new Rect(0,0,ActualWidth,ActualHeight);
                if(Stretch!=Stretch.Fill && bitmap.Width>0 && bitmap.Height>0) {
                    double scale=Math.Min(ActualWidth/bitmap.Width,ActualHeight/bitmap.Height);
                    to=new Rect((ActualWidth-bitmap.Width*scale)/2,(ActualHeight-bitmap.Height*scale)/2,bitmap.Width*scale,bitmap.Height*scale);
                }
                dc.DrawImage(Light.Apply(bitmap,brightness),to);
                return;
            }
            if(Source!=null && ActualWidth>0 && ActualHeight>0)
                framing.Draw(dc,Source,new Rect(0,0,ActualWidth,ActualHeight),brightness);
        }
    }
}
