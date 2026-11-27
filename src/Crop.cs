using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Automation;

namespace LoveMyShelf {
    public class CropSurface : FrameworkElement {
        BitmapSource image;
        // The cover's shape, width over height in pixels. Free since 1.0.9: a new cover starts as the whole picture,
        // and the edges and corners of the frame can be dragged, or a shape picked below (see CropWindow).
        double aspect;
        public const double MaxZoom=64;
        internal const double HalfTurn=180;
        internal const double RotationStep=0.1;
        double zoom=1, centerX, centerY;
        double dragX,dragY;
        double? snappedX,snappedY;
        readonly bool snapEdges;
        readonly double wheelZoomFactor;
        public double Rotation { get; private set; }
        // The value saved with the cover: the level and its style (see Light). An old cover's white or black sheet is Milky.
        internal int Brightness {get {return Light.Encode(Level,style);}}
        AdjustValues adjust;
        internal AdjustValues Adjust {
            get {
                var a=adjust;
                a.Brightness=Light.Encode(Level,style);
                return a;
            }
        }
        int style=Light.Soft;
        // The -100..100 number on the slider.
        internal int Level {get;private set;}
        // Soft, See in the dark or Milky.
        internal int LightStyle {get {return style;}}
        Point last;
        internal event Action RotationChanged;
        internal event Action BrightnessChanged;
        internal event Action AdjustChanged;
        // The frame's shape or size changed (a shape picked, an edge dragged, the wheel, Reset).
        internal event Action ShapeChanged;
        public CropSurface(BitmapSource image, Rect? saved, double rotation=0,bool snapEdges=true,int wheelZoomPercent=7,int brightness=0,
            AdjustValues adjust=default(AdjustValues)) {
            this.image=image;this.snapEdges=snapEdges;Focusable=true; Cursor=Cursors.SizeAll;
            wheelZoomFactor=1+wheelZoomPercent/100.0;
            RenderOptions.SetBitmapScalingMode(this,BitmapScalingMode.HighQuality);
            Reset();
            // Brightness and adjustments carry over even for a new cover (from the video's playback brightness);
            // the turn and the framing only come from a saved cover.
            if(adjust.IsIdentity && brightness!=0)adjust.Brightness=brightness;
            this.adjust=adjust.Clamped();
            Level=Light.Level(this.adjust.Brightness);
            style=this.adjust.Brightness!=0?Light.Style(this.adjust.Brightness):Light.Soft;
            if(saved.HasValue) {
                Rotation=rotation;
                aspect=Covers.AspectOf(saved.Value,image.PixelWidth,image.PixelHeight);
                ApplyFraction(saved.Value);
            }
            AutomationProperties.SetName(this,L.T("Cover frame")); ClipToBounds=true;
        }
        void ApplyFraction(Rect r) {
            zoom=Limit(BaseWidth/(r.Width*image.PixelWidth),MinZoom,MaxZoom);
            centerX=(r.X+r.Width/2)*image.PixelWidth; centerY=(r.Y+r.Height/2)*image.PixelHeight; Clamp();
        }
        internal void ShowFrame(BitmapSource frame) {
            Rect current=Fraction;
            if(frame.PixelWidth!=image.PixelWidth || frame.PixelHeight!=image.PixelHeight)viewSet=false;
            image=frame;
            ApplyFraction(current);
        }
        public double Zoom { get { return zoom; } }
        public BitmapSource Source { get { return image; } }
        // Width over height of the cover being cut.
        internal double Aspect {get {return aspect;}}
        // The whole picture's own shape.
        internal double PictureAspect {get {return image.PixelWidth/(double)Math.Max(1,image.PixelHeight);}}
        // The cover's size in the picture's pixels.
        internal Size PixelSize {get {return new Size(CropW,CropH);}}
        double BaseWidth { get { return Math.Min(image.PixelWidth,image.PixelHeight*aspect); } }
        public double MinZoom { get { return BaseWidth/(2*Math.Max(image.PixelWidth,image.PixelHeight*aspect)); } }
        double CropW { get { return BaseWidth/zoom; } }
        double CropH { get { return CropW/aspect; } }
        public Rect Fraction {
            get { return new Rect((centerX-CropW/2)/image.PixelWidth,(centerY-CropH/2)/image.PixelHeight,
                    CropW/image.PixelWidth,CropH/image.PixelHeight); }
        }
        // The cover as it looks now, at most this many pixels on its long side (the GIF window's still picture).
        internal BitmapSource Still(int longSide=960) {
            int w=(int)Math.Max(1,Math.Round(aspect>=1?longSide:longSide*aspect)),h=(int)Math.Max(1,Math.Round(aspect>=1?longSide/aspect:longSide));
            return Pictures.RenderRotatedCover(image,Fraction,Rotation,w,h,Brightness,Adjust);
        }
        internal BitmapSource UnadjustedStill(int longSide=320) {
            int w=(int)Math.Max(1,Math.Round(aspect>=1?longSide:longSide*aspect)),h=(int)Math.Max(1,Math.Round(aspect>=1?longSide/aspect:longSide));
            return Pictures.RenderRotatedCover(image,Fraction,Rotation,w,h,0,AdjustValues.None);
        }
        static double Limit(double value,double lo,double hi) { return Math.Max(lo,Math.Min(hi,value)); }
        static double LimitCenter(double center,double cropSize,double imageSize) {
            double overlap=Math.Min(cropSize,imageSize)*0.05;
            return Limit(center,overlap-cropSize/2,imageSize-overlap+cropSize/2);
        }
        void Clamp() {
            // Keep the crop overlapping the photo even when their edges are angled. Upright or a quarter turn: the crop's
            // own width and height (swapped when turned). Any other angle: the square in the middle of the crop, turned,
            // whose upright box always lies inside the crop, so that part keeps touching the photo whatever the shape.
            double across=CropW,down=CropH;
            double turn=Math.Abs(NormalizeRotation(Rotation));
            if(Math.Abs(turn-90)<1e-9){across=CropH;down=CropW;}
            else if(turn>1e-9 && Math.Abs(turn-180)>1e-9) {
                double a=turn*Math.PI/180,core=Math.Min(CropW,CropH)/(Math.Abs(Math.Cos(a))+Math.Abs(Math.Sin(a)));
                across=down=core;
            }
            centerX=LimitCenter(centerX,across,image.PixelWidth);
            centerY=LimitCenter(centerY,down,image.PixelHeight); InvalidateVisual();
            if(Moved!=null)Moved();
        }
        // On: dragging the frame's edges and corners keeps its shape and changes only its size.
        // The shape itself is still picked with the buttons and the width:height boxes.
        internal bool LockShape;
        // The frame moved, changed or got a new picture under it (the grid preview redraws).
        internal event Action Moved;
        // Reset: the whole picture, upright, as a new cover starts.
        public void Reset() {
            viewSet=false;
            aspect=PictureAspect;
            zoom=1; Rotation=0; centerX=image.PixelWidth/2.0; centerY=image.PixelHeight/2.0; Clamp();
            SetAdjust(AdjustValues.None,Light.Soft);
            if(RotationChanged!=null) RotationChanged();
            if(ShapeChanged!=null)ShapeChanged();
        }
        // Everything [Reset] changes, to bring it back with [Undo reset] (ResetToggle).
        internal object State() {return Tuple.Create(new Rect(centerX,centerY,zoom,aspect),Rotation,adjust,style);}
        internal void SetState(object value) {
            var s=(Tuple<Rect,double,AdjustValues,int>)value;
            centerX=s.Item1.X;centerY=s.Item1.Y;zoom=s.Item1.Width;aspect=s.Item1.Height;Rotation=s.Item2;viewSet=false;Clamp();
            SetAdjust(s.Item3,s.Item4);
            if(RotationChanged!=null) RotationChanged();
            if(ShapeChanged!=null)ShapeChanged();
            InvalidateVisual();
        }
        // The whole picture in its own shape, keeping the turn and the brightness.
        internal void WholePicture() {
            aspect=PictureAspect;zoom=1;centerX=image.PixelWidth/2.0;centerY=image.PixelHeight/2.0;Clamp();
            if(ShapeChanged!=null)ShapeChanged();
        }
        // A new shape for the frame around the same middle, about as big as before. A frame that was inside the picture
        // stays inside it, so trying shapes one after another never adds black bands or keeps shrinking the cover.
        internal void SetAspect(double value) {
            value=Limit(value,Covers.Narrowest,Covers.Widest);
            if(Math.Abs(value-aspect)<1e-9)return;
            double area=CropW*CropH;
            var r=Fraction;
            bool inside=Rotation==0 && r.Left>=-0.002 && r.Top>=-0.002 && r.Right<=1.002 && r.Bottom<=1.002;
            double w=Math.Sqrt(area*value),h=w/value;
            if(inside) {
                double shrink=Math.Min(1,Math.Min(image.PixelWidth/w,image.PixelHeight/h));
                w*=shrink;h*=shrink;
            }
            aspect=value;
            zoom=Limit(BaseWidth/w,MinZoom,MaxZoom);
            if(inside) {
                // Back inside the picture around the same middle, where it fits.
                centerX=Limit(centerX,CropW/2,Math.Max(CropW/2,image.PixelWidth-CropW/2));
                centerY=Limit(centerY,CropH/2,Math.Max(CropH/2,image.PixelHeight-CropH/2));
            }
            Clamp();
            if(ShapeChanged!=null)ShapeChanged();
        }
        internal void SetBrightness(double value) {
            int level=Light.Clamp((int)Math.Round(value));
            if(level==Level)return;
            Level=level;adjust.Brightness=Light.Encode(Level,style);InvalidateVisual();
            if(BrightnessChanged!=null)BrightnessChanged();
            if(AdjustChanged!=null)AdjustChanged();
        }
        // R: Soft, See in the dark and Milky take turns. The level stays.
        internal void SwitchStyle() {
            style=Light.NextStyle(style);adjust.Brightness=Light.Encode(Level,style);InvalidateVisual();
            if(BrightnessChanged!=null)BrightnessChanged();
            if(AdjustChanged!=null)AdjustChanged();
        }
        internal void SetAdjust(AdjustValues values,int? explicitStyle=null) {
            values=values.Clamped();
            int nextStyle=explicitStyle.HasValue?explicitStyle.Value:(values.Brightness!=0?Light.Style(values.Brightness):style);
            int nextLevel=Light.Level(values.Brightness);
            values.Brightness=Light.Encode(nextLevel,nextStyle);
            bool brightChanged=nextLevel!=Level || nextStyle!=style;
            bool anyChanged=values!=Adjust || brightChanged;
            adjust=values;Level=nextLevel;style=nextStyle;
            if(!anyChanged)return;
            InvalidateVisual();
            if(brightChanged && BrightnessChanged!=null)BrightnessChanged();
            if(AdjustChanged!=null)AdjustChanged();
        }
        internal static double NormalizeRotation(double angle) {
            double turn=2*HalfTurn;
            angle%=turn;
            if(angle>HalfTurn)return angle-turn;
            if(angle<-HalfTurn)return angle+turn;
            return angle;
        }
        internal void SetRotation(double angle) {
            angle=Math.Round(NormalizeRotation(angle),1);
            if(angle==Rotation)return;
            Rotation=angle;viewSet=false;
            Clamp();
            if(RotationChanged!=null) RotationChanged();
        }
        public void SetZoom(double value) {
            zoom=Limit(value,MinZoom,MaxZoom);Clamp();BeginDrag();
            if(ShapeChanged!=null)ShapeChanged();
        }
        // Room around the frame for its handles, and how near an edge the pointer grabs it.
        const double Room=40,Grab=9;
        internal Rect Frame {get {return StillPicture?StillFrame:FittedFrame;}}
        // Zooming with the frame (the old way): the frame always fills the middle of the view, so the picture zooms
        // in and out whenever the frame changes size or shape.
        Rect FittedFrame {
            get { double w=Math.Max(1,Math.Min(ActualWidth-Room,(ActualHeight-Room)*aspect));
                return new Rect((ActualWidth-w)/2,(ActualHeight-w/aspect)/2,w,w/aspect); }
        }
        // ---------- Still picture (the default since 1.0.11) ----------
        // The picture stays where it is on screen and the frame moves and changes size over it, like a phone's photo app.
        // The view (how big the picture is, which part is in the middle) changes only when the window or the turn
        // changes, a new picture comes, or [Fit to the window] is pressed.
        internal bool StillPicture,Drawing;
        double viewScale;Vector viewMid;bool viewSet;
        internal void SetStill(bool on) {StillPicture=on;viewSet=false;InvalidateVisual();if(Moved!=null)Moved();}
        // The whole picture in view; with frame, the frame too (when it reaches past the picture or the view).
        void FitView(bool withFrame) {
            viewSet=false;
            if(ActualWidth<=Room+10 || ActualHeight<=Room+10)return;
            var turn=Matrix.Identity;turn.Rotate(Rotation);
            double w=image.PixelWidth,h=image.PixelHeight;
            double minX=Double.MaxValue,minY=Double.MaxValue,maxX=Double.MinValue,maxY=Double.MinValue;
            Action<Vector> take=v=>{minX=Math.Min(minX,v.X);maxX=Math.Max(maxX,v.X);minY=Math.Min(minY,v.Y);maxY=Math.Max(maxY,v.Y);};
            foreach(var corner in new[]{new Vector(-w/2,-h/2),new Vector(w/2,-h/2),new Vector(w/2,h/2),new Vector(-w/2,h/2)})take(turn.Transform(corner));
            if(withFrame) {
                var mid=turn.Transform(new Vector(centerX-w/2,centerY-h/2));
                take(mid+new Vector(-CropW/2,-CropH/2));take(mid+new Vector(CropW/2,CropH/2));
            }
            viewScale=Math.Min((ActualWidth-Room)/Math.Max(1,maxX-minX),(ActualHeight-Room)/Math.Max(1,maxY-minY));
            viewMid=new Vector((minX+maxX)/2,(minY+maxY)/2);
            viewSet=viewScale>0 && !Double.IsInfinity(viewScale);
        }
        // [Fit to the window]: the picture and the frame both in view.
        internal void FitToWindow() {if(!StillPicture)return;FitView(true);InvalidateVisual();if(Moved!=null)Moved();}
        Rect StillFrame {
            get {
                if(!viewSet)FitView(false);
                if(!viewSet)return FittedFrame;
                var turn=Matrix.Identity;turn.Rotate(Rotation);
                var mid=turn.Transform(new Vector(centerX-image.PixelWidth/2.0,centerY-image.PixelHeight/2.0));
                double x=ActualWidth/2+(mid.X-viewMid.X)*viewScale,y=ActualHeight/2+(mid.Y-viewMid.Y)*viewScale;
                double fw=Math.Max(1,CropW*viewScale),fh=Math.Max(1,CropH*viewScale);
                return new Rect(x-fw/2,y-fh/2,fw,fh);
            }
        }
        protected override void OnRenderSizeChanged(SizeChangedInfo info) {base.OnRenderSizeChanged(info);viewSet=false;}
        Vector SourceDelta(double dx,double dy) {
            double scale=Frame.Width/CropW;
            var transform=Matrix.Identity;
            transform.Rotate(-Rotation);
            return transform.Transform(new Vector(dx/scale,dy/scale));
        }
        public void Pan(double dx,double dy) {
            var delta=SourceDelta(dx,dy);
            centerX-=delta.X; centerY-=delta.Y; Clamp();
        }
        // From Guide & Options: how many screen pixels away an edge starts to stick (0 = never). It lets go 1.5 times as far.
        internal static int SnapPixels=20;
        static double SnapCenter(double center,double cropSize,double imageSize,double unitsPerPixel,ref double? snapped) {
            double engage=SnapPixels,release=SnapPixels*1.5;
            if(snapped.HasValue && Math.Abs(center-snapped.Value)<=release*unitsPerPixel)return snapped.Value;
            double near=cropSize/2,far=imageSize-near;
            double edge=Math.Abs(center-near)<=Math.Abs(center-far)?near:far;
            snapped=Math.Abs(center-edge)<=engage*unitsPerPixel?(double?)edge:null;
            return snapped??center;
        }
        internal void BeginDrag() {dragX=centerX;dragY=centerY;snappedX=snappedY=null;}
        internal void Drag(double dx,double dy) {
            var delta=SourceDelta(dx,dy);
            // Still picture: the frame follows the pointer over the picture. Otherwise the picture moves under the frame.
            if(StillPicture){centerX=dragX+delta.X;centerY=dragY+delta.Y;}
            else {centerX=dragX-delta.X;centerY=dragY-delta.Y;}
            Clamp();
            // Keep the pointer's raw position so the stronger hold still releases predictably.
            dragX=centerX;dragY=centerY;
            if(!snapEdges || SnapPixels<=0 || Rotation%90!=0)return;
            double unitsPerPixel=CropW/Frame.Width;
            bool quarterTurn=Math.Abs(Rotation)==90;
            centerX=SnapCenter(dragX,quarterTurn?CropH:CropW,image.PixelWidth,unitsPerPixel,ref snappedX);
            centerY=SnapCenter(dragY,quarterTurn?CropW:CropH,image.PixelHeight,unitsPerPixel,ref snappedY);
        }
        // ---------- Dragging an edge or a corner of the frame ----------
        // Which edges the pointer holds: -1 left/top, 1 right/bottom, 0 neither. The opposite edges stay where they are on the picture.
        int edgeX,edgeY;
        double startW,startH,startX,startY,startUnits;
        Point startPoint;
        bool resizing;
        void EdgesAt(Point p,out int x,out int y) {
            var r=Frame;x=y=0;
            bool across=p.Y>=r.Top-Grab && p.Y<=r.Bottom+Grab,down=p.X>=r.Left-Grab && p.X<=r.Right+Grab;
            if(across) {if(Math.Abs(p.X-r.Left)<=Grab)x=-1;else if(Math.Abs(p.X-r.Right)<=Grab)x=1;}
            if(down) {if(Math.Abs(p.Y-r.Top)<=Grab)y=-1;else if(Math.Abs(p.Y-r.Bottom)<=Grab)y=1;}
        }
        static Cursor CursorFor(int x,int y) {
            if(x!=0 && y!=0)return x==y?Cursors.SizeNWSE:Cursors.SizeNESW;
            if(x!=0)return Cursors.SizeWE;
            if(y!=0)return Cursors.SizeNS;
            return Cursors.SizeAll;
        }
        void Resize(Point p) {
            // Worked out from where the drag began each time, so the frame changing size on screen never feeds back.
            // The frame stays in the middle of the view, so an edge moves half as far on screen as it does on the picture.
            double w=startW,h=startH;
            // A still picture: the edge moves exactly with the pointer. Otherwise the frame stays in the middle of the view,
            // so an edge moves half as far on screen as it does on the picture.
            double reach=StillPicture?1:2;
            if(edgeX!=0)w=Math.Max(16,startW+edgeX*(p.X-startPoint.X)*startUnits*reach);
            if(edgeY!=0)h=Math.Max(16,startH+edgeY*(p.Y-startPoint.Y)*startUnits*reach);
            double shape=Limit(w/h,Covers.Narrowest,Covers.Widest);
            // A shape very near the grid's cells, or the whole picture's, settles on it.
            foreach(double near in new[]{GridCell,PictureAspect})
                if(Math.Abs(shape/near-1)<0.015){shape=near;break;}
            if(LockShape) {
                // Shape locked: only the size changes. An edge sets its own side; a corner follows the side pulled more.
                shape=aspect;
                if(edgeX!=0 && edgeY!=0 && Math.Abs(h/startH-1)>Math.Abs(w/startW-1))w=h*shape;
                else if(edgeY!=0 && edgeX==0)w=h*shape;
            }
            if(edgeX!=0 && edgeY==0 && !LockShape)w=h*shape;else h=w/shape;
            aspect=shape;
            zoom=Limit(BaseWidth/w,MinZoom,MaxZoom);
            // The edge held moves; the opposite one stays put on the picture.
            double moveX=edgeX==0?0:edgeX*(CropW-startW)/2,moveY=edgeY==0?0:edgeY*(CropH-startH)/2;
            var turn=Matrix.Identity;turn.Rotate(-Rotation);
            var shift=turn.Transform(new Vector(moveX,moveY));
            centerX=startX+shift.X;centerY=startY+shift.Y;
            Clamp();
            if(ShapeChanged!=null)ShapeChanged();
        }
        // The grid's cell shape (see Covers), from the folder window's options.
        internal static double GridCell {
            get {
                var prefs=Prefs;
                return Covers.Shape(prefs!=null?prefs.TileShape:Covers.DefaultShape);
            }
        }
        internal static Preferences Prefs {
            get {
                if(Application.Current==null)return null;
                foreach(Window window in Application.Current.Windows) {
                    var gallery=window as GalleryWindow;
                    if(gallery!=null)return gallery.Settings;
                }
                return null;
            }
        }
        protected override void OnRender(DrawingContext dc) {
            base.OnRender(dc);
            if(ActualWidth<1 || ActualHeight<1) return;
            Rect r=Frame;
            dc.DrawRectangle(Brushes.Black,null,new Rect(RenderSize));
            Pictures.DrawCover(dc,image,Fraction,Rotation,r,Brightness,Adjust);
            // Still picture: the part outside the frame is dimmed, so what goes into the cover stands out.
            if(StillPicture) {
                var outside=Geometry.Combine(new RectangleGeometry(new Rect(RenderSize)),new RectangleGeometry(r),GeometryCombineMode.Exclude,null);
                dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(0x99,0,0,0)),null,outside);
            }
            var accent=UI.B("Accent");
            dc.DrawRectangle(null,new Pen(accent,2),r);
            // Handles at the corners and the middle of each edge show that the frame can be pulled into any shape.
            if(Drawing)return;
            var rim=new Pen(Brushes.White,1);
            foreach(var x in new[]{r.Left,r.Left+r.Width/2,r.Right})
                foreach(var y in new[]{r.Top,r.Top+r.Height/2,r.Bottom}) {
                    bool middleX=x!=r.Left && x!=r.Right,middleY=y!=r.Top && y!=r.Bottom;
                    if(middleX && middleY)continue;
                    double w=middleX?20:middleY?6:11,h=middleY?20:middleX?6:11;
                    dc.DrawRoundedRectangle(accent,rim,new Rect(x-w/2,y-h/2,w,h),2,2);
                }
        }
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) {
            if(Drawing){e.Handled=true;return;}
            base.OnMouseLeftButtonDown(e); Focus(); last=e.GetPosition(this);
            EdgesAt(last,out edgeX,out edgeY);
            resizing=edgeX!=0 || edgeY!=0;
            if(resizing) {
                startW=CropW;startH=CropH;startX=centerX;startY=centerY;startPoint=last;
                startUnits=CropW/Math.Max(1,Frame.Width);
            } else BeginDrag();
            CaptureMouse(); e.Handled=true;
        }
        protected override void OnMouseMove(MouseEventArgs e) {
            Point p=e.GetPosition(this);
            if(IsMouseCaptured && e.LeftButton==MouseButtonState.Pressed) {
                if(resizing)Resize(p);else Drag(p.X-last.X,p.Y-last.Y);
                last=p;
                return;
            }
            int x,y;EdgesAt(p,out x,out y);
            Cursor=CursorFor(x,y);
        }
        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) {
            if(IsMouseCaptured) ReleaseMouseCapture();
            resizing=false;
        }
        protected override void OnLostMouseCapture(MouseEventArgs e) {base.OnLostMouseCapture(e);resizing=false;}
        protected override void OnMouseWheel(MouseWheelEventArgs e) {
            if(Drawing){e.Handled=true;return;}
            double target=Limit(zoom*Math.Pow(wheelZoomFactor,e.Delta/120.0),MinZoom,MaxZoom);
            if(!PinTopOnWheel || target==zoom){SetZoom(target);e.Handled=true;return;}
            // The top line of the frame stays on the same spot of the picture, so a head that touches it keeps touching it.
            var up=Matrix.Identity;up.Rotate(-Rotation);
            Vector toTop=up.Transform(new Vector(0,-CropH/2));
            double pinX=centerX+toTop.X,pinY=centerY+toTop.Y;
            SetZoom(target);
            Vector newTop=up.Transform(new Vector(0,-CropH/2));
            centerX=pinX-newTop.X;centerY=pinY-newTop.Y;Clamp();BeginDrag();
            e.Handled=true;
        }
        // From Guide & Options → Making a cover: keep the frame's top line fixed while the wheel zooms.
        static bool PinTopOnWheel {
            get {
                var prefs=Prefs;
                return prefs==null || prefs.PinTopOnWheel;
            }
        }
        protected override void OnKeyDown(KeyEventArgs e) {
            if(Drawing)return;
            switch(e.Key) {
                case Key.Add: case Key.OemPlus: SetZoom(zoom*1.14);break;
                case Key.Subtract: case Key.OemMinus: SetZoom(zoom/1.14);break;
                default: base.OnKeyDown(e);return;
            }
            e.Handled=true;
        }
    }

    // Keep the drawing layer aligned with the frame, including when the frame extends outside the view.
    internal sealed class CropDrawingPicture : Panel {
        readonly CropSurface surface;
        readonly MotionDrawLayer drawing;
        BitmapSource mosaicPicture;
        internal CropDrawingPicture(CropSurface surface,MotionDrawLayer drawing) {
            this.surface=surface;this.drawing=drawing;
            ClipToBounds=true;Children.Add(surface);Children.Add(drawing);
            surface.Moved+=FrameMoved;
            surface.BrightnessChanged+=FrameMoved;
            surface.AdjustChanged+=FrameMoved;
            drawing.MosaicPicture=GetMosaicPicture;
            drawing.ErasePicture=GetMosaicPicture;
        }
        internal BitmapSource RenderBasePicture() {return GetMosaicPicture();}
        BitmapSource GetMosaicPicture() {
            if(mosaicPicture==null) {
                Size size=surface.PixelSize;
                double scale=Math.Min(1,1920/Math.Max(size.Width,size.Height));
                mosaicPicture=Pictures.RenderRotatedCover(surface.Source,surface.Fraction,surface.Rotation,
                    Math.Max(1,(int)Math.Round(size.Width*scale)),Math.Max(1,(int)Math.Round(size.Height*scale)),surface.Brightness,surface.Adjust);
            }
            return mosaicPicture;
        }
        void FrameMoved() {mosaicPicture=null;drawing.InvalidateVisual();InvalidateArrange();}
        protected override Size MeasureOverride(Size size) {
            surface.Measure(size);drawing.Measure(size);return surface.DesiredSize;
        }
        protected override Size ArrangeOverride(Size size) {
            surface.Arrange(new Rect(size));
            drawing.Arrange(surface.Frame);
            drawing.SetLook(false,Math.Max(1,Math.Round(surface.PixelSize.Height)));
            return size;
        }
    }

    public class CropWindow : Window {
        public readonly CropSurface Surface;
        readonly MotionDrawLayer drawing=new MotionDrawLayer();
        readonly CropDrawingPicture cropPicture;
        readonly AdjustPanel adjustPanel;
        readonly EraseRunner eraseRunner=new EraseRunner();
        internal string Drawings {get {return drawing.Value;}}
        internal EraseAiPatch EraseAi {get {return drawing.AiPatch;}}
        internal string SavedEditedPath {get;private set;}
        internal bool BackToFrames { get; private set; }
        internal int FrameIndex { get; private set; }
        internal double? FrameTime {get{return nearby==null?(double?)null:nearby.Start(FrameIndex);}}
        internal int FrameTimeOffset {get{return nearby==null?0:nearby.TimestampOffset(FrameIndex);}}
        internal Func<CropWindow,Task> CommitCover;
        internal Func<CropWindow,Task<string>> SaveEditedCopy;
        internal Func<string> SourceIdentityProvider;
        // How much one press of E or W changes the brightness (the slider still moves one at a time).
        internal int BrightnessStep=3;
        // From this window width the [Adjust]/[Draw] controls stand in a column of this width beside the picture.
        const double SideToolsWidth=900,SideColumn=300,FoldedColumn=30;
        // The column folded to a thin strip with one button, so a wide picture can take almost the whole width.
        // Kept while LoveMyShelf runs (2026-11-27, "pictures and videos must show big").
        static bool columnFolded;
        Action placeTools;
        StackPanel sideStack;
        // −90° 0° +90°: beside the slider under the picture, on a line of their own in the narrow column.
        FrameworkElement rotationShortcuts;
        FrameworkElement sceneBar;
        // Runs after the cover is saved and before the editor closes (the large view opens here, so no gap shows).
        internal Func<Task> AfterCommit;
        readonly Button save,motionButton,saveNewButton;
        Func<CropWindow,MotionRequest,Task> commitMotion;
        Func<MotionSource> motionSource;
        readonly int previewSide;
        bool saving;
        Button previousFrame,nextFrame;
        TextBlock frameNumber;
        string framePath;
        int frameCount;
        FrameTimes nearby;
        // True once nothing more can be read before / after the scene list (the video's start or end).
        bool nearbyAtStart,nearbyAtEnd,readingMore;
        public CropWindow(Window owner,BitmapSource image,string name,Rect? saved,double rotation=0,bool snapEdges=true,int wheelZoomPercent=7,
            int previewSide=0,int brightness=0,string drawings=null,AdjustValues adjust=default(AdjustValues),EraseAiPatch initialEraseAi=null) {
            this.previewSide=previewSide>0?previewSide:Preferences.EditorPreviewSide;
            UI.WindowSetup(this,saved.HasValue?L.T("Make one more cover"):L.T("Make cover")); Owner=owner; ShowInTaskbar=false;
            WindowStartupLocation=WindowStartupLocation.CenterOwner;
            Width=Math.Min(880,SystemParameters.WorkArea.Width); Height=Math.Min(850,SystemParameters.WorkArea.Height);
            MinWidth=390; MinHeight=360;
            var root=new DockPanel();
            root.Children.Add(FullScreen.TitleBar(this,owner!=null && FullScreen.IsFull(owner)));
            var head=UI.Bar(new Thickness(6,2,6,2));
            var seq=UI.Text("",11,true);seq.Margin=new Thickness(4,0,6,0);UI.Right(head,seq);
            var title=UI.Text(name,12); title.TextTrimming=TextTrimming.CharacterEllipsis; head.Children.Add(title);
            DockPanel.SetDock(head,Dock.Top);root.Children.Add(head);
            Surface=new CropSurface(image,saved,rotation,snapEdges,wheelZoomPercent,brightness,adjust);
            AutomationProperties.SetHelpText(Surface,L.T("Drag to move it, and roll the wheel to fit the size."));
            var bar=new DockPanel {Margin=new Thickness(6,2,6,2),LastChildFill=true};
            // [Reset] puts the frame, the turn and the adjustments back; pressed again, it brings back what was just before.
            var reset=UI.Btn(L.T("Reset"),()=>{});
            var resetToggle=ResetToggle.Attach(reset,Surface.State,Surface.SetState,()=>{Surface.Reset();StartInKeptShape();});
            Surface.Moved+=resetToggle.Changed;Surface.ShapeChanged+=resetToggle.Changed;
            Surface.RotationChanged+=resetToggle.Changed;Surface.AdjustChanged+=resetToggle.Changed;
            reset.Margin=new Thickness(0,0,8,2);reset.VerticalAlignment=VerticalAlignment.Center;
            DockPanel.SetDock(reset,Dock.Left);bar.Children.Add(reset);
            var rightButtons=new WrapPanel {HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Center};
            saveNewButton=UI.Btn(L.P("Save as new file","새 파일로 저장"),SaveAsNewFile);
            saveNewButton.Margin=new Thickness(0,0,5,2);
            saveNewButton.Visibility=Visibility.Collapsed;
            saveNewButton.ToolTip=L.P("Saves the cropped, adjusted, erased and drawn picture as a new file next to the original, and shows it in the folder. The original stays as it is.",
                "자르고 보정하고 지우고 그린 모습을 원본 옆에 새 파일(예: 이름_편집.jpg)로 저장하고 폴더에서 바로 보여 줘요. 원본은 그대로예요.");
            motionButton=UI.Btn(L.T("Make GIF cover"),MakeMotion);motionButton.Margin=new Thickness(0,0,5,2);
            motionButton.Visibility=Visibility.Collapsed;motionButton.ToolTip=L.T("Saves the JPG cover and also makes a moving GIF cover around this scene.");
            var cancel=UI.Btn(L.T("Cancel"),()=>{
                BackToFrames=previousFrame!=null;
                DialogResult=false;
            });
            cancel.Margin=new Thickness(0,0,5,2);
            save=UI.Btn(L.T("Save cover"),SaveCover,true); save.IsDefault=true; save.Margin=new Thickness(0,0,0,2);
            rightButtons.Children.Add(saveNewButton);
            rightButtons.Children.Add(motionButton);
            rightButtons.Children.Add(cancel);
            rightButtons.Children.Add(save);
            bar.Children.Add(rightButtons);
            DockPanel.SetDock(bar,Dock.Bottom);root.Children.Add(bar);
            var rotationBar=RotationBar();
            DockPanel.SetDock(rotationBar,Dock.Bottom);root.Children.Add(rotationBar);
            var shapeBar=ShapeBar();
            DockPanel.SetDock(shapeBar,Dock.Bottom);root.Children.Add(shapeBar);
            // The column beside the picture: "How it shows in my grid", and in a wide window also [Adjust] and [Draw]
            // (2026-11-27, DeepSeek·Gemini·Codex debate): the picture keeps its full height while adjusting or drawing.
            var peekPanel=PeekPanel();
            var side=new DockPanel {LastChildFill=true};
            ((FrameworkElement)peekPanel).HorizontalAlignment=HorizontalAlignment.Left;
            var fold=UI.Btn("◂",()=>{columnFolded=true;if(placeTools!=null)placeTools();});
            fold.Focusable=false;fold.MinWidth=26;fold.Padding=new Thickness(6,0,6,0);fold.Margin=new Thickness(0,8,8,0);
            fold.HorizontalAlignment=HorizontalAlignment.Right;fold.VerticalAlignment=VerticalAlignment.Top;
            fold.ToolTip=L.P("Fold this column so the picture gets the whole width. ▸ brings it back.","이 칸을 접어서 사진을 가로로 크게 봐요. ▸를 누르면 다시 펴져요.");
            AutomationProperties.SetName(fold,L.P("Fold the column","옆 칸 접기"));
            var unfold=UI.Btn("▸",()=>{columnFolded=false;if(placeTools!=null)placeTools();});
            unfold.Focusable=false;unfold.MinWidth=24;unfold.Padding=new Thickness(4,0,4,0);unfold.Margin=new Thickness(2,8,2,0);unfold.VerticalAlignment=VerticalAlignment.Top;
            unfold.ToolTip=L.P("Show the column again: grid preview, shape, rotation, Adjust, Draw.","옆 칸을 다시 펴요: 바둑판 미리보기, 모양, 회전, 보정, 그림 그리기.");
            AutomationProperties.SetName(unfold,L.P("Show the column","옆 칸 펴기"));
            var peekRow=new Grid();peekRow.Children.Add(peekPanel);peekRow.Children.Add(fold);
            DockPanel.SetDock(peekRow,Dock.Top);side.Children.Add(peekRow);
            DockPanel.SetDock(unfold,Dock.Top);side.Children.Add(unfold);
            DockPanel.SetDock(side,Dock.Right);root.Children.Add(side);
            string baseTitle=Title;
            Action showShape=()=>{
                var size=Surface.PixelSize;seq.Text=Covers.Name(Surface.Aspect)+"  ·  "+Math.Round(size.Width)+" × "+Math.Round(size.Height);
                // With the column, the file's name and the shape ride in the title bar instead of a row of their own.
                Title=head.Visibility==Visibility.Visible?baseTitle:baseTitle+"  ·  "+name+"  ·  "+seq.Text;
            };
            Surface.ShapeChanged+=showShape;showShape();
            drawing.Load(drawings,initialEraseAi);
            cropPicture=new CropDrawingPicture(Surface,drawing);
            adjustPanel=new AdjustPanel(Surface.Adjust,()=>Surface.UnadjustedStill(320));
            var drawPanel=new MotionDrawPanel(drawing);
            var pauseNote=UI.Text(L.P("Fold [Draw] or [Erase] to change the crop again.",
                "그리거나 지우는 중에는 자르기를 바꿀 수 없어요. 자르기를 바꾸려면 [그림 그리기]나 [지우기]를 접어요."),12,true);
            var positionNote=UI.Text(L.P("Drawings keep their place in the cover when you change the crop.",
                "자르는 곳을 바꿔도 그림은 표지 안에서 같은 자리에 있어요."),12,true);
            pauseNote.TextWrapping=positionNote.TextWrapping=TextWrapping.Wrap;
            positionNote.Visibility=Visibility.Collapsed;
            // [Adjust ▾] [Draw ▾] on one line (one open at a time), the notes, then the open one's controls.
            var openers=new WrapPanel();
            foreach(var opener in new Button[]{adjustPanel.Opener,drawPanel.Opener,drawPanel.EraseOpener}) {
                ((Panel)opener.Parent).Children.Remove(opener);
                opener.Margin=new Thickness(0,2,6,2);openers.Children.Add(opener);
            }
            var toolHeads=new StackPanel();
            toolHeads.Children.Add(openers);toolHeads.Children.Add(pauseNote);toolHeads.Children.Add(positionNote);
            var toolBodies=new StackPanel();
            toolBodies.Children.Add(adjustPanel);toolBodies.Children.Add(drawPanel);
            // Narrow window: the same controls under the picture, as before.
            var drawDock=new StackPanel {Margin=new Thickness(6,2,6,2)};
            DockPanel.SetDock(drawDock,Dock.Bottom);
            root.Children.Insert(root.Children.IndexOf(side),drawDock);
            // Wide window: the column under the grid preview holds the scene buttons, shape, rotation, [Adjust] [Draw]
            // and the open one, and scrolls; only the Save row stays under the picture.
            sideStack=new StackPanel();
            var sideScroll=new ScrollViewer {VerticalScrollBarVisibility=ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,Focusable=false,Content=sideStack};
            // The panels' own scroll boxes have no height limit here, so the wheel scrolls the column itself.
            sideScroll.PreviewMouseWheel+=(s,e)=>{
                if(sideScroll.ScrollableHeight<=0 || AdjustRow.TakesWheel(e.OriginalSource))return;
                sideScroll.ScrollToVerticalOffset(sideScroll.VerticalOffset-e.Delta/3.0);e.Handled=true;
            };
            var peekExtras=new List<UIElement>();
            var peekItems=((Panel)peekPanel).Children;
            for(int i=2;i<peekItems.Count;i++)peekExtras.Add(peekItems[i]);
            bool inSide=false,placed=false,folded=false;
            side.Children.Add(sideScroll);
            Action place=()=>{
                bool wideNow=ActualWidth>=SideToolsWidth;
                side.Visibility=ActualWidth>=640?Visibility.Visible:Visibility.Collapsed;
                bool foldNow=wideNow && columnFolded;
                if(!placed || wideNow!=inSide || foldNow!=folded || (sceneBar!=null && sceneBar.Parent==null)) {
                    placed=true;inSide=wideNow;folded=foldNow;
                    var moving=new List<FrameworkElement>();
                    if(sceneBar!=null)moving.Add(sceneBar);
                    moving.Add(shapeBar);moving.Add(rotationBar);moving.Add(toolHeads);moving.Add(toolBodies);
                    foreach(var item in moving){var p=item.Parent as Panel;if(p!=null)p.Children.Remove(item);}
                    var turnBar=(Panel)rotationBar;
                    if(rotationShortcuts.Parent!=null)((Panel)rotationShortcuts.Parent).Children.Remove(rotationShortcuts);
                    if(inSide) {
                        side.Width=folded?FoldedColumn:SideColumn;
                        foreach(var item in moving){item.Margin=new Thickness(0,2,0,4);sideStack.Children.Add(item);}
                        rotationShortcuts.Margin=new Thickness(0,0,0,6);rotationShortcuts.HorizontalAlignment=HorizontalAlignment.Left;
                        sideStack.Children.Insert(sideStack.Children.IndexOf(rotationBar)+1,rotationShortcuts);
                        sideScroll.Margin=new Thickness(4,0,10,6);
                    } else {
                        side.Width=Double.NaN;
                        // Back under the picture, in their old order: Save row, rotation, shape, then the tools.
                        shapeBar.Margin=new Thickness(6,3,6,3);rotationBar.Margin=new Thickness(6,2,6,2);toolHeads.Margin=new Thickness(0);
                        rotationShortcuts.Margin=new Thickness(8,0,0,0);rotationShortcuts.HorizontalAlignment=HorizontalAlignment.Stretch;
                        DockPanel.SetDock(rotationShortcuts,Dock.Right);turnBar.Children.Insert(Math.Max(0,turnBar.Children.Count-1),rotationShortcuts);
                        root.Children.Insert(root.Children.IndexOf(drawDock),rotationBar);
                        root.Children.Insert(root.Children.IndexOf(drawDock),shapeBar);
                        if(sceneBar!=null){sceneBar.Margin=new Thickness(6,2,6,2);root.Children.Insert(root.Children.IndexOf(cropPicture)<0?root.Children.Count:root.Children.IndexOf(cropPicture),sceneBar);}
                        drawDock.Children.Add(toolHeads);drawDock.Children.Add(toolBodies);
                    }
                    peekRow.Visibility=sideScroll.Visibility=inSide && folded?Visibility.Collapsed:Visibility.Visible;
                    unfold.Visibility=inSide && folded?Visibility.Visible:Visibility.Collapsed;
                    fold.Visibility=inSide?Visibility.Visible:Visibility.Collapsed;
                    head.Visibility=inSide?Visibility.Collapsed:Visibility.Visible;
                    showShape();
                    drawDock.Visibility=inSide?Visibility.Collapsed:Visibility.Visible;
                }
                double room=inSide?Double.PositiveInfinity:Math.Max(72,ActualHeight*0.26);
                drawPanel.ControlsHeight=room;
                adjustPanel.ControlsHeight=room;
                // While a panel is open in the column, the grid preview keeps its picture and drops its note and cell button.
                bool tight=inSide && (adjustPanel.IsOpen || drawPanel.IsOpen);
                foreach(var extra in peekExtras)extra.Visibility=tight?Visibility.Collapsed:Visibility.Visible;
            };
            placeTools=place;
            SizeChanged+=delegate {place();};
            adjustPanel.OpenChanged+=place;drawPanel.OpenChanged+=place;
            // An opened panel sits low in the column: bring it into view.
            adjustPanel.OpenChanged+=delegate {if(adjustPanel.IsOpen)Dispatcher.BeginInvoke(new Action(()=>adjustPanel.BringIntoView()),System.Windows.Threading.DispatcherPriority.Loaded);};
            drawPanel.OpenChanged+=delegate {if(drawPanel.IsOpen)Dispatcher.BeginInvoke(new Action(()=>drawPanel.BringIntoView()),System.Windows.Threading.DispatcherPriority.Loaded);};
            root.Children.Add(cropPicture);Content=root;
            place();

            bool syncingAdjust=false;
            adjustPanel.Changed+=delegate {
                if(syncingAdjust)return;
                syncingAdjust=true;
                Surface.SetAdjust(adjustPanel.Values,adjustPanel.LightStyle);
                syncingAdjust=false;
            };
            Surface.AdjustChanged+=delegate {
                if(!syncingAdjust)adjustPanel.Load(Surface.Adjust,Surface.LightStyle);
            };
            adjustPanel.AreaRequested+=delegate {drawing.SelectTool(DrawPreferences.Last.AreaKind);drawPanel.SetOpen(true);};
            bool syncingFold=false;
            adjustPanel.OpenChanged+=delegate {
                if(syncingFold)return;
                if(adjustPanel.IsOpen && drawPanel.IsOpen){syncingFold=true;drawPanel.SetOpen(false);syncingFold=false;}
            };
            Action showDrawing=()=>{
                if(!syncingFold && drawPanel.IsOpen && adjustPanel.IsOpen){syncingFold=true;adjustPanel.SetOpen(false);syncingFold=false;}
                Surface.Drawing=drawPanel.IsOpen;
                if(Surface.Drawing && Surface.IsMouseCaptured)Surface.ReleaseMouseCapture();
                Surface.InvalidateVisual();
                pauseNote.Visibility=Surface.Drawing?Visibility.Visible:Visibility.Collapsed;
                foreach(var barToPause in new FrameworkElement[]{shapeBar,rotationBar,reset})
                    barToPause.IsEnabled=!Surface.Drawing;
            };
            drawPanel.OpenChanged+=showDrawing;showDrawing();
            Rect previousCrop=Surface.Fraction;
            double previousRotation=Surface.Rotation;
            AdjustValues previousAdjust=Surface.Adjust;
            Action invalidateAiIfBaseChanged=()=>{
                if(Surface.Fraction!=previousCrop || Surface.Rotation!=previousRotation || Surface.Adjust!=previousAdjust) {
                    eraseRunner.Cancel();
                    drawing.InvalidateAiPatch();
                }
                if((Surface.Fraction!=previousCrop || Surface.Rotation!=previousRotation) && drawing.HasShapes)
                    positionNote.Visibility=Visibility.Visible;
                previousCrop=Surface.Fraction;previousRotation=Surface.Rotation;previousAdjust=Surface.Adjust;
            };
            Surface.Moved+=invalidateAiIfBaseChanged;
            Surface.AdjustChanged+=invalidateAiIfBaseChanged;
            drawing.EraseStrokesChanged+=delegate {if(eraseRunner.IsRunning)eraseRunner.Cancel();};
            eraseRunner.StateChanged+=()=>Dispatcher.BeginInvoke(new Action(()=>drawPanel.SetAiRunning(eraseRunner.IsRunning)));
            drawPanel.OnCancelAiErase=()=>eraseRunner.Cancel();
            drawPanel.OnRunAiErase=async ()=>{
                if(!drawing.HasEraseShapes || !EraseEngine.Available)return;
                var basePic=cropPicture.RenderBasePicture();
                var shapesSnap=new System.Collections.Generic.List<MotionShape>(drawing.Shapes);
                string strokesSpec=EraseStore.CanonicalEraseStrokes(shapesSnap);
                Rect cropSnap=Surface.Fraction;double rotSnap=Surface.Rotation;AdjustValues adjSnap=Surface.Adjust;
                int frameSnap=FrameIndex;
                string srcId=SourceIdentityProvider!=null?SourceIdentityProvider():"";
                // Strokes added after an AI fill: only they are filled, on top of the earlier fill. [Fill again]: a new try at all of them.
                int variant=drawPanel.AiVariant;
                var earlier=drawing.EarlierAi;
                var added=variant==0 && drawing.AiPatch==null?EraseAgain.Added(shapesSnap,earlier):null;
                if(added!=null && (earlier.Patch.PixelWidth!=basePic.PixelWidth || earlier.Patch.PixelHeight!=basePic.PixelHeight))added=null;
                var runOn=added!=null?EraseStore.Composite(basePic,earlier.Patch):basePic;
                var patch=await eraseRunner.RunAsync(EraseAgain.Turn(runOn,variant),EraseAgain.Shapes(added??shapesSnap,variant,runOn.PixelHeight));
                if(patch==null || !IsVisible || FrameIndex!=frameSnap)return;
                if(Surface.Fraction!=cropSnap || Surface.Rotation!=rotSnap || Surface.Adjust!=adjSnap)return;
                if(EraseStore.CanonicalEraseStrokes(drawing.Shapes)!=strokesSpec)return;
                patch=EraseAgain.Turn(patch,variant);
                if(added!=null)patch=EraseAgain.Merge(earlier.Patch,patch);
                string key=EraseStore.BuildCacheKey(srcId,cropSnap,rotSnap,adjSnap,strokesSpec);
                drawing.ApplyAiPatch(new EraseAiPatch(patch,strokesSpec,key,0));
            };
            Closed+=delegate {eraseRunner.Dispose();};
            drawing.Changed+=delegate {if(!drawing.HasShapes)positionNote.Visibility=Visibility.Collapsed;};
            PreviewMouseWheel+=(s,e)=>{if(Surface.Drawing && cropPicture.IsMouseOver)e.Handled=true;};
            ContentRendered+=delegate { Surface.Focus(); };
            Closing+=(s,e)=>{if(saving)e.Cancel=true;};
            PreviewKeyDown+=(s,e)=>{
                if(saving){e.Handled=true;return;}
                if(AdjustRow.OwnsKey(e))return;
                if(drawPanel.HandleKey(e,true)){e.Handled=true;return;}
                var key=e.Key==Key.ImeProcessed?e.ImeProcessedKey:e.Key;
                // Typing a width or height: the arrows, letters and numbers stay in the box.
                if(Keyboard.FocusedElement is TextBox && key!=Key.Escape && key!=Key.F1)return;
                switch(key) {
                    case Key.F1: GuideWindow.Open(this,previousFrame!=null?"video":"editor");break;
                    case Key.Escape: DialogResult=false;break;
                    // The same keys as the video's large view: D / F one scene, arrows skip, E / W / Q brightness.
                    case Key.D: case Key.F:
                        if(previousFrame==null || Keyboard.Modifiers!=ModifierKeys.None)return;
                        MoveFrame(key==Key.D?-1:1);break;
                    case Key.Left: case Key.Right:
                        // Shift + arrow skips one second; an arrow alone the seconds chosen in the options. Only for a video.
                        if(nearby!=null && previousFrame!=null && (Keyboard.Modifiers==ModifierKeys.None || Keyboard.Modifiers==ModifierKeys.Shift))
                            SkipBy((key==Key.Left?-1:1)*(Keyboard.Modifiers==ModifierKeys.Shift?1:SkipSeconds));
                        break;
                    case Key.Up: case Key.Down: break;
                    case Key.E: case Key.W: case Key.Q:
                        if(Keyboard.Modifiers!=ModifierKeys.None)return;
                        Surface.SetBrightness(key==Key.Q?0:Math.Max(-100,Math.Min(100,Surface.Level+(key==Key.E?BrightnessStep:-BrightnessStep))));break;
                    case Key.R:
                        if(Keyboard.Modifiers!=ModifierKeys.None)return;
                        Surface.SwitchStyle();break;
                    default:return;
                }
                e.Handled=true;
            };
        }
        internal void EnableSaveEditedCopy(Func<CropWindow,Task<string>> saveCopy) {
            SaveEditedCopy=saveCopy;
            saveNewButton.Visibility=saveCopy==null?Visibility.Collapsed:Visibility.Visible;
        }
        async void SaveAsNewFile() {
            if(saving || !save.IsEnabled || SaveEditedCopy==null)return;
            saving=true;
            var layout=(UIElement)Content;
            layout.IsEnabled=false;
            string prevLabel=(string)saveNewButton.Content;
            saveNewButton.Content=L.P("Saving…","저장하는 중…");
            try {
                SavedEditedPath=await SaveEditedCopy(this);
            } catch(Exception ex) {
                UI.Error(this,ex);
                return;
            } finally {
                saving=false;layout.IsEnabled=true;saveNewButton.Content=prevLabel;
            }
            if(!String.IsNullOrEmpty(SavedEditedPath))DialogResult=false;
        }
        // The cover's shape: fill the grid's cell, the whole picture, a common shape, or any width and height typed in.
        FrameworkElement ShapeBar() {
            var bar=new WrapPanel {Margin=new Thickness(6,3,6,3),VerticalAlignment=VerticalAlignment.Center};
            var label=UI.Text(L.P("Shape","모양"),12);label.Margin=new Thickness(0,0,8,0);bar.Children.Add(label);
            // Lock the shape: dragging the frame then keeps this shape (remembered for next time; off at first).
            var prefs=CropSurface.Prefs;
            var keep=new CheckBox {Content=L.P("🔒 Keep this shape","🔒 비율 고정"),IsChecked=prefs!=null && prefs.CropLock,
                VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,2,10,2),FontSize=13,FontWeight=FontWeights.SemiBold};
            keep.SetResourceReference(Control.ForegroundProperty,"Text");
            keep.ToolTip=L.P("On: however you drag the frame's edges and corners, the shape stays as it is and only the size changes. Pick the shape with the buttons or the numbers.",
                "켜면 테두리의 모서리나 변을 아무리 끌어도 비율은 그대로이고 크기만 바뀌어요. 비율은 버튼이나 숫자로 정해요.");
            AutomationProperties.SetName(keep,L.P("Keep this shape","비율 고정"));
            Surface.LockShape=keep.IsChecked==true;
            // Turning it on keeps the shape the frame has now, for this cover and the next ones.
            RoutedEventHandler locked=delegate {
                Surface.LockShape=keep.IsChecked==true;GalleryWindow.RememberCropLock(Surface.LockShape);
                if(Surface.LockShape)GalleryWindow.RememberCropShape(Surface.Aspect);
            };
            // A new cover with the shape kept starts in that shape (the person who always cuts 9:16 never has to pick it again).
            Loaded+=delegate {StartInKeptShape();};
            keep.Checked+=locked;keep.Unchecked+=locked;
            bar.Children.Add(keep);
            // Zoom with the frame or not: off (the picture stays still) unless turned on; remembered.
            var follow=new CheckBox {Content=L.P("🔍 Zoom the picture with the frame","🔍 틀 따라 사진 확대·축소"),IsChecked=prefs!=null && prefs.CropZoom,
                VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,2,10,2),FontSize=13};
            follow.SetResourceReference(Control.ForegroundProperty,"Text");
            follow.ToolTip=L.P("Off: the picture stays still and only the frame moves and changes size. On: the frame always fills the middle and the picture zooms in and out with it (the way it was before).",
                "끄면 사진은 가만히 있고 틀만 움직이고 커지고 작아져요. 켜면 틀이 늘 가운데를 꽉 채우고 사진이 따라서 확대·축소돼요(예전 방식).");
            AutomationProperties.SetName(follow,L.P("Zoom the picture with the frame","틀 따라 사진 확대·축소"));
            var fit=UI.Btn(L.P("Fit to the window","화면에 맞추기"),()=>Surface.FitToWindow());
            fit.Margin=new Thickness(0,2,10,2);fit.Padding=new Thickness(8,0,8,0);
            fit.ToolTip=L.P("Shows the whole picture and the frame in the window.","사진 전체와 틀이 창에 다 보이게 해요.");
            Surface.SetStill(follow.IsChecked!=true);
            fit.Visibility=follow.IsChecked==true?Visibility.Collapsed:Visibility.Visible;
            RoutedEventHandler zoomed=delegate {
                Surface.SetStill(follow.IsChecked!=true);fit.Visibility=follow.IsChecked==true?Visibility.Collapsed:Visibility.Visible;
                GalleryWindow.RememberCropZoom(follow.IsChecked==true);
            };
            follow.Checked+=zoomed;follow.Unchecked+=zoomed;
            bar.Children.Add(follow);bar.Children.Add(fit);
            Func<string,string,Action,Button> add=(text,tip,act)=>{
                var b=UI.Btn(text,act);b.Margin=new Thickness(0,2,4,2);b.Padding=new Thickness(8,0,8,0);b.ToolTip=tip;
                AutomationProperties.SetName(b,tip);bar.Children.Add(b);return b;
            };
            // Only the shapes the grid uses (the person's choice): one cell, and two cells side by side for a wide cover.
            var fill=add("","",()=>{Surface.SetAspect(CropSurface.GridCell);KeepIfLocked();});
            fill.Style=(Style)Application.Current.Resources["Primary"];
            var fillTwo=add("","",()=>{Surface.SetAspect(Covers.TwoCells(CropSurface.GridCell));KeepIfLocked();});
            Action nameFill=()=>{
                double cell=CropSurface.GridCell;
                fill.Content=Covers.OneCellLabel(cell);
                fill.ToolTip=L.P("Cuts the cover in the same shape as the grid's cells, so it fills its cell with no empty bands.",
                    "바둑판 칸과 같은 모양으로 잘라서 칸에 빈 띠 없이 꽉 차게 보여요.");
                fillTwo.Content=Covers.TwoCellsLabel(cell);
                fillTwo.ToolTip=L.P("Cuts a wide cover that takes two cells side by side and fills both with no empty bands.",
                    "가로로 긴 표지로 잘라서 칸 두 개를 빈 띠 없이 꽉 채워요.");
                AutomationProperties.SetName(fill,(string)fill.Content);AutomationProperties.SetName(fillTwo,(string)fillTwo.Content);
                var grid=CropSurface.Prefs;
                fillTwo.Visibility=grid==null || grid.WideCovers?Visibility.Visible:Visibility.Collapsed;
            };
            nameFill();
            add(L.P("Whole picture","원본 그대로"),L.P("Goes back to the whole picture, in its own shape.","원본 사진 전체를 원래 모양 그대로 써요."),()=>Surface.WholePicture());
            // Any shape: width and height numbers (16 and 9, or 1080 and 1920), applied with Enter or when leaving the box.
            var custom=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(6,2,0,2),VerticalAlignment=VerticalAlignment.Center};
            Func<string,TextBox> box=tip=>{
                var t=new TextBox {Width=54,MinHeight=26,VerticalContentAlignment=VerticalAlignment.Center,HorizontalContentAlignment=HorizontalAlignment.Center,ToolTip=tip};
                AutomationProperties.SetName(t,tip);return t;
            };
            var across=box(L.P("Width (any number, like 16 or 1080)","가로 (16이나 1080처럼 아무 숫자)"));
            var tall=box(L.P("Height (any number, like 9 or 1920)","세로 (9나 1920처럼 아무 숫자)"));
            var colon=UI.Text(":",13);colon.Margin=new Thickness(4,0,4,0);
            custom.Children.Add(across);custom.Children.Add(colon);custom.Children.Add(tall);
            bar.Children.Add(custom);
            // Set when the person types in a box; a click in and out alone changes nothing.
            bool typed=false;
            foreach(var t in new[]{across,tall})t.TextChanged+=delegate {if(t.IsKeyboardFocusWithin)typed=true;};
            Action apply=()=>{
                double w,h;
                if(!Double.TryParse(across.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out w) &&
                    !Double.TryParse(across.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out w))return;
                if(!Double.TryParse(tall.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out h) &&
                    !Double.TryParse(tall.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out h))return;
                if(!typed)return;
                typed=false;
                // Only real numbers: positive, finite, and a finite shape (not "Infinity" in both boxes).
                if(w>0 && h>0 && !Double.IsInfinity(w) && !Double.IsInfinity(h) && !Double.IsNaN(w/h) && !Double.IsInfinity(w/h) && w/h>0)
                {Surface.SetAspect(w/h);KeepIfLocked();}
            };
            // Applied with Enter, or once the keyboard leaves both boxes (not between typing the width and the height).
            foreach(var t in new[]{across,tall})
                t.KeyDown+=(s,e)=>{if(e.Key==Key.Enter){apply();Surface.Focus();e.Handled=true;}};
            custom.IsKeyboardFocusWithinChanged+=(s,e)=>{if(!(bool)e.NewValue)apply();};
            Action show=()=>{
                if(across.IsKeyboardFocusWithin || tall.IsKeyboardFocusWithin)return;
                string name=Covers.Name(Surface.Aspect);int at=name.IndexOf(':');
                across.Text=name.Substring(0,at);tall.Text=name.Substring(at+1);
            };
            Surface.ShapeChanged+=show;show();
            Activated+=delegate {nameFill();};
            GalleryWindow.TileShapeChanged+=nameFill;
            Closed+=delegate {GalleryWindow.TileShapeChanged-=nameFill;};
            return bar;
        }
        // Editing a saved cover: its own shape stays even with Keep this shape on.
        internal bool KeepsSavedShape;
        void StartInKeptShape() {
            var prefs=CropSurface.Prefs;
            if(KeepsSavedShape || !Surface.LockShape || prefs==null)return;
            Surface.SetAspect(prefs.KeptShape);
        }
        void KeepIfLocked() {if(Surface.LockShape)GalleryWindow.RememberCropShape(Surface.Aspect);}
        // "How it shows in my grid": a few cells of the grid in the shape the person picked, with this cover in the first.
        FrameworkElement PeekPanel() {
            var panel=new StackPanel {Width=196,Margin=new Thickness(4,10,10,6)};
            var title=UI.Text(L.P("How it shows in your grid","내 바둑판에서는 이렇게 보여요"),12.5);
            title.FontWeight=FontWeights.SemiBold;title.TextWrapping=TextWrapping.Wrap;panel.Children.Add(title);
            var peek=new GridPeek(Surface) {Margin=new Thickness(0,8,0,6)};panel.Children.Add(peek);
            var note=UI.Text("",12,true);note.TextWrapping=TextWrapping.Wrap;panel.Children.Add(note);
            var cells=UI.Btn("",()=>{});cells.Margin=new Thickness(0,10,0,0);cells.HorizontalAlignment=HorizontalAlignment.Left;
            cells.Padding=new Thickness(8,0,8,0);panel.Children.Add(cells);
            cells.ToolTip=L.P("The shape of the grid's cells. Covers keep their own shape; only how they sit in the cells changes.",
                "바둑판 칸의 모양이에요. 표지는 그대로이고 칸에 들어가는 모습만 바뀌어요.");
            cells.Click+=delegate {
                var menu=new ContextMenu {PlacementTarget=cells,Placement=System.Windows.Controls.Primitives.PlacementMode.Bottom};
                var prefs=CropSurface.Prefs;
                for(int i=0;i<Covers.Shapes.Length;i++) {
                    int shape=i;
                    var item=new MenuItem {Header=Covers.ShapeLabel(i),IsCheckable=true,IsChecked=prefs!=null && prefs.TileShape==i};
                    item.Click+=delegate {GalleryWindow.ChangeTileShape(shape);};
                    menu.Items.Add(item);
                }
                menu.IsOpen=true;
            };
            Action update=()=>{
                var prefs=CropSurface.Prefs;
                int shape=prefs!=null?prefs.TileShape:Covers.DefaultShape;
                double cell=Covers.Shape(shape);
                cells.Content=L.P("Grid cells: ","바둑판 칸: ")+Covers.ShapeLabel(shape)+"  ▾";
                bool two=(prefs==null || prefs.WideCovers) && Covers.TakesTwoCells(Surface.Aspect,cell);
                double filled=Covers.Fill(Surface.Aspect,two?cell*2:cell);
                int percent=(int)Math.Round(filled*100);
                note.Text=two?L.P("It's wide, so it takes two cells side by side ("+percent+"% filled).","가로로 길어서 칸 두 개에 넓게 보여요 (칸을 "+percent+"% 채워요)."):
                    percent>=99?L.P("It fills its cell exactly.","칸에 꽉 차요."):
                    L.P("It fills "+percent+"% of its cell; the rest shows as a plain band. [Fill the grid's cell] makes it fill the cell.",
                        "칸의 "+percent+"%를 채우고 남는 곳은 빈 띠로 보여요. [칸에 꽉 맞추기]를 누르면 칸에 꽉 차요.");
                peek.InvalidateMeasure();peek.InvalidateVisual();
            };
            Surface.ShapeChanged+=update;Surface.RotationChanged+=update;Surface.BrightnessChanged+=update;Surface.AdjustChanged+=update;
            GalleryWindow.TileShapeChanged+=update;
            Closed+=delegate {GalleryWindow.TileShapeChanged-=update;};
            update();
            return panel;
        }
        // A row of grid cells in the person's cell shape, drawn with the cover as it is being cut.
        sealed class GridPeek : FrameworkElement {
            readonly CropSurface surface;
            internal GridPeek(CropSurface surface) {this.surface=surface;IsHitTestVisible=false;surface.Moved+=InvalidateVisual;}
            const int Cells=3;const double Gap=3;
            protected override Size MeasureOverride(Size available) {
                double width=Double.IsInfinity(available.Width)?196:available.Width;
                double cell=(width-Gap*(Cells-1))/Cells;
                return new Size(width,Math.Ceiling(cell/CropSurface.GridCell));
            }
            protected override void OnRender(DrawingContext dc) {
                double width=ActualWidth;if(width<=0)return;
                var prefs=CropSurface.Prefs;
                double shape=CropSurface.GridCell,cell=(width-Gap*(Cells-1))/Cells,height=cell/shape;
                bool two=(prefs==null || prefs.WideCovers) && Covers.TakesTwoCells(surface.Aspect,shape);
                var band=UI.B("TileBand");var line=new Pen(UI.B("Line"),1);
                int used=two?2:1;
                var box=new Rect(0,0,cell*used+Gap*(used-1),height);
                dc.DrawRectangle(band,null,box);
                double a=surface.Aspect,w=box.Width,h=box.Height;
                if(a>w/h)h=w/a;else w=h*a;
                var to=new Rect(box.X+(box.Width-w)/2,box.Y+(box.Height-h)/2,w,h);
                dc.PushClip(new RectangleGeometry(to));
                dc.DrawRectangle(Brushes.Black,null,to);
                Pictures.DrawCover(dc,surface.Source,surface.Fraction,surface.Rotation,to,surface.Brightness,surface.Adjust);
                dc.Pop();
                dc.DrawRectangle(null,new Pen(UI.B("Accent"),2),box);
                // The other cells: empty stand-ins for the files around it.
                for(int i=used;i<Cells;i++) {
                    var other=new Rect(i*(cell+Gap),0,cell,height);
                    dc.DrawRectangle(band,line,other);
                }
            }
        }
        // Shows Make GIF for videos and GIFs. The callback saves the cover, then makes the GIF in the background.
        Func<MotionRequest,System.Threading.CancellationToken,Action<double>,Task<string>> saveEditedMotion;
        internal void EnableMotion(Func<CropWindow,MotionRequest,Task> commit,Func<MotionSource> source,
            Func<MotionRequest,System.Threading.CancellationToken,Action<double>,Task<string>> saveEditedMotion=null) {
            commitMotion=commit;motionSource=source;this.saveEditedMotion=saveEditedMotion;
            motionButton.Visibility=commit==null?Visibility.Collapsed:Visibility.Visible;
        }
        void MakeMotion() {
            if(saving || !save.IsEnabled || commitMotion==null)return;
            if(motionSource==null)return;
            var dialog=new MotionWindow(this,motionSource(),true);
            dialog.SaveEditedFile=saveEditedMotion;
            if(CoverWork.Show(dialog)!=true) {
                if(!String.IsNullOrEmpty(dialog.SavedEditedPath)) {
                    SavedEditedPath=dialog.SavedEditedPath;
                    DialogResult=false;
                }
                return;
            }
            var request=dialog.Value;
            SaveCover(editor=>commitMotion(editor,request));
        }
        void SaveCover() {SaveCover(null);}
        async void SaveCover(Func<CropWindow,Task> commit) {
            if(saving || !save.IsEnabled)return;
            commit=commit??CommitCover;
            if(commit!=null) {
                saving=true;
                var layout=(UIElement)Content;
                layout.IsEnabled=false;save.Content=L.T("Saving cover…");
                try {await commit(this);if(AfterCommit!=null)await AfterCommit();}
                catch(Exception ex){UI.Error(this,ex);return;}
                finally {
                    saving=false;layout.IsEnabled=true;save.Content=L.T("Save cover");
                }
            }
            DialogResult=true;
        }
        DockPanel RotationBar() {
            var bar=UI.Bar(new Thickness(6,2,6,2));
            var label=UI.Text(L.T("Rotation"),12);
            label.Margin=new Thickness(0,0,8,0);
            bar.Children.Add(label);
            var left=UI.Btn("−",()=>Surface.SetRotation(Surface.Rotation-CropSurface.RotationStep));
            var right=UI.Btn("+",()=>Surface.SetRotation(Surface.Rotation+CropSurface.RotationStep));
            string leftLabel=L.T("Rotate counterclockwise by 0.1°"),rightLabel=L.T("Rotate clockwise by 0.1°");
            AutomationProperties.SetName(left,leftLabel);
            AutomationProperties.SetName(right,rightLabel);
            left.ToolTip=leftLabel;right.ToolTip=rightLabel;
            bar.Children.Add(left);
            var value=UI.Text("",12);
            value.MinWidth=52;value.TextAlignment=TextAlignment.Center;
            bar.Children.Add(value);
            bar.Children.Add(right);
            var shortcuts=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(8,0,0,0)};
            foreach(var button in new[]{
                RotationShortcut("−90°","Rotate counterclockwise by 90°",()=>Surface.SetRotation(Surface.Rotation-90)),
                RotationShortcut("0°","Reset rotation to 0°",()=>Surface.SetRotation(0)),
                RotationShortcut("+90°","Rotate clockwise by 90°",()=>Surface.SetRotation(Surface.Rotation+90))
            })shortcuts.Children.Add(button);
            UI.Right(bar,shortcuts);
            rotationShortcuts=shortcuts;
            var slider=new DragSlider {Minimum=-CropSurface.HalfTurn,Maximum=CropSurface.HalfTurn,
                TickFrequency=CropSurface.RotationStep,IsSnapToTickEnabled=true,SmallChange=CropSurface.RotationStep,LargeChange=1,
                IsMoveToPointEnabled=true,Margin=new Thickness(8,0,0,0),VerticalAlignment=VerticalAlignment.Center};
            AutomationProperties.SetName(slider,L.T("Rotation"));
            bar.Children.Add(slider);
            Action update=()=>{
                slider.Value=Surface.Rotation;
                value.Text=Surface.Rotation.ToString("0.0",CultureInfo.CurrentCulture)+"°";
            };
            Surface.RotationChanged+=update;
            update();
            slider.ValueChanged+=(s,e)=>Surface.SetRotation(slider.Value);
            return bar;
        }
        static Button RotationShortcut(string label,string description,Action action) {
            var button=UI.Btn(label,action);
            button.Margin=new Thickness(3,0,0,0);
            button.ToolTip=L.T(description);
            AutomationProperties.SetName(button,L.T(description));
            return button;
        }
        internal void AddFrameControls(string path,int index,int count,FrameTimes localTimes=null) {
            framePath=path;FrameIndex=index;frameCount=count;nearby=localTimes;
            nearbyAtStart=localTimes!=null && localTimes.Count>0 && localTimes.Start(0)<=0.5;nearbyAtEnd=false;
            var bar=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(6,2,6,2)};
            previousFrame=UI.Btn(L.T("Previous scene (D)"),()=>MoveFrame(-1));bar.Children.Add(previousFrame);
            frameNumber=UI.Text("",11,true);frameNumber.Margin=new Thickness(6,0,6,0);frameNumber.TextAlignment=TextAlignment.Center;
            // Wide enough for the longest label so the Next button never moves.
            var probe=new TextBlock {Text=localTimes==null?String.Format("{0:N0} / {0:N0}",count).Replace('1','0').Replace('2','0').Replace('3','0').Replace('4','0').Replace('5','0').Replace('6','0').Replace('7','0').Replace('8','0').Replace('9','0'):Viewer.Stamp(0),FontSize=11};
            Typography.SetNumeralAlignment(probe,FontNumeralAlignment.Tabular);probe.Measure(new Size(Double.PositiveInfinity,Double.PositiveInfinity));
            frameNumber.MinWidth=Math.Ceiling(probe.DesiredSize.Width)+2;
            bar.Children.Add(frameNumber);
            nextFrame=UI.Btn(L.T("Next scene (F)"),()=>MoveFrame(1));bar.Children.Add(nextFrame);
            DockPanel.SetDock(bar,Dock.Bottom);
            sceneBar=bar;
            if(placeTools!=null)placeTools();
            else {var root=(DockPanel)Content;root.Children.Insert(root.Children.IndexOf(cropPicture),bar);}
            UpdateFrameControls(true);
        }
        void UpdateFrameControls(bool ready) {
            save.IsEnabled=ready;
            // A video's list can be read further past either end, until the video's start or end.
            previousFrame.IsEnabled=ready && (FrameIndex>0 || (nearby!=null && !nearbyAtStart));
            nextFrame.IsEnabled=ready && (FrameIndex<frameCount-1 || (nearby!=null && !nearbyAtEnd));
            frameNumber.Text=!ready?L.T(readingMore?"Loading more…":"Loading…"):nearby==null?String.Format("{0:N0} / {1:N0}",FrameIndex+1,frameCount):
                Viewer.Stamp(nearby.Start(FrameIndex));
        }
        async void MoveFrame(int step) {
            if(!save.IsEnabled)return;
            if(nearby!=null && (step<0?FrameIndex<=0 && !nearbyAtStart:FrameIndex>=frameCount-1 && !nearbyAtEnd)) {
                // At the end of the scenes read so far: read the next stretch around this scene, then move on.
                if(!await ReadMoreScenes(step))return;
            }
            int target=FrameIndex+step;
            if(nearby!=null) {
                // A video moves one scene at a time (0.2 s unless changed in the options), never finer than its own pictures.
                int scene=nearby.At(nearby.Start(FrameIndex)+step*(double)Scenes.StepMs);
                if(step>0?scene>target:scene<target)target=scene;
                target=Math.Max(0,Math.Min(frameCount-1,target));
                if(target==FrameIndex)return;
            }
            await ShowScene(target);
        }
        // Seconds the arrow keys skip (the large view's setting).
        internal int SkipSeconds=5;
        // Moves to the scene nearest a time this many seconds away, reading a new stretch of scenes there when needed.
        async void SkipBy(double seconds) {
            if(nearby==null || !save.IsEnabled || frameCount==0)return;
            double here=nearby.Start(FrameIndex);
            // On the same scene grid as now, so the scene times stay the ones the grid showed.
            double wanted=Math.Max(0,Scenes.Snap(here+seconds*1000,here));
            double first=nearby.Start(0),last=nearby.Start(frameCount-1);
            bool outside=seconds<0?wanted<first-0.5 && !nearbyAtStart:wanted>last+0.5 && !nearbyAtEnd;
            if(outside) {
                readingMore=true;UpdateFrameControls(false);
                FrameTimes wider=null;
                try {
                    string path=framePath;double stepMs=Scenes.StepMs;int radius=Scenes.RadiusMs;
                    wider=await Task.Run(()=>VideoFrames.Nearby(path,wanted,System.Threading.CancellationToken.None,radius).Thin(stepMs,wanted));
                } catch(Exception) {}
                readingMore=false;
                if(!IsVisible)return;
                if(wider==null || wider.Count==0){UpdateFrameControls(true);return;}
                double keep=nearby.Start(FrameIndex);
                nearby=wider;frameCount=wider.Count;
                FrameIndex=Math.Max(0,Math.Min(frameCount-1,wider.At(keep)));
                nearbyAtStart=wider.Start(0)<=0.5;
                // Past the video's end the new stretch stops short of the time asked for.
                nearbyAtEnd=seconds>0 && wider.Start(wider.Count-1)<wanted-0.5;
                UpdateFrameControls(true);
            }
            int target=Math.Max(0,Math.Min(frameCount-1,nearby.At(wanted)));
            if(target==FrameIndex)return;
            await ShowScene(target);
        }
        async Task ShowScene(int target) {
            if(!save.IsEnabled || target<0 || target>=frameCount)return;
            UpdateFrameControls(false);
            try {
                var image=await Task.Run(()=>nearby==null?AnimationFrames.Still(framePath,target,previewSide):
                    VideoFrames.AtTime(framePath,nearby.Start(target),nearby.TimestampOffset(target),previewSide,System.Threading.CancellationToken.None));
                if(!IsVisible)return;
                eraseRunner.Cancel();
                drawing.InvalidateAiPatch();
                Surface.ShowFrame(image);FrameIndex=target;
            } catch(Exception ex){if(IsVisible)UI.Error(this,ex);}
            finally {if(IsVisible)UpdateFrameControls(true);}
        }
        // Replaces the scene list with one centered on the current scene (the same scene grid), so K / L can go on.
        // Returns false when there is nothing more that way (the video's start or end) or reading failed.
        async Task<bool> ReadMoreScenes(int step) {
            double here=nearby.Start(FrameIndex);
            readingMore=true;UpdateFrameControls(false);
            FrameTimes wider=null;
            try {
                string path=framePath;double stepMs=Scenes.StepMs;int radius=Scenes.RadiusMs;
                wider=await Task.Run(()=>VideoFrames.Nearby(path,here,System.Threading.CancellationToken.None,radius).Thin(stepMs,here));
            } catch(Exception) {}
            readingMore=false;
            if(!IsVisible)return false;
            if(wider==null || wider.Count==0) {
                if(step<0)nearbyAtStart=true;else nearbyAtEnd=true;
                UpdateFrameControls(true);return false;
            }
            int at=wider.At(here);
            bool more=step<0?at>0:at<wider.Count-1;
            if(!more) {
                if(step<0)nearbyAtStart=true;else nearbyAtEnd=true;
                UpdateFrameControls(true);return false;
            }
            nearby=wider;frameCount=wider.Count;FrameIndex=at;
            nearbyAtStart=wider.Start(0)<=0.5;nearbyAtEnd=false; // the start of the video is known; its end shows when nothing more comes
            UpdateFrameControls(true);
            return true;
        }
    }
}
