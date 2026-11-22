using System;
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
        const double Aspect=9.0/16.0;
        public const double MaxZoom=64;
        internal const double HalfTurn=180;
        internal const double RotationStep=0.1;
        double zoom=1, centerX, centerY;
        double dragX,dragY;
        double? snappedX,snappedY;
        readonly bool snapEdges;
        readonly double wheelZoomFactor;
        public double Rotation { get; private set; }
        internal int Brightness {get;private set;}
        Point last;
        internal event Action RotationChanged;
        internal event Action BrightnessChanged;
        public CropSurface(BitmapSource image, Rect? saved, double rotation=0,bool snapEdges=true,int wheelZoomPercent=7,int brightness=0) {
            this.image=image;this.snapEdges=snapEdges;Focusable=true; Cursor=Cursors.SizeAll;
            wheelZoomFactor=1+wheelZoomPercent/100.0;
            RenderOptions.SetBitmapScalingMode(this,BitmapScalingMode.HighQuality);
            Reset();
            if(saved.HasValue) {
                Rotation=rotation;
                Brightness=brightness;
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
            image=frame;
            ApplyFraction(current);
        }
        public double Zoom { get { return zoom; } }
        public BitmapSource Source { get { return image; } }
        double BaseWidth { get { return Math.Min(image.PixelWidth,image.PixelHeight*Aspect); } }
        public double MinZoom { get { return BaseWidth/(2*Math.Max(image.PixelWidth,image.PixelHeight*Aspect)); } }
        double CropW { get { return BaseWidth/zoom; } }
        double CropH { get { return CropW/Aspect; } }
        public Rect Fraction {
            get { return new Rect((centerX-CropW/2)/image.PixelWidth,(centerY-CropH/2)/image.PixelHeight,
                    CropW/image.PixelWidth,CropH/image.PixelHeight); }
        }
        static double Limit(double value,double lo,double hi) { return Math.Max(lo,Math.Min(hi,value)); }
        static double LimitCenter(double center,double cropSize,double imageSize) {
            double overlap=Math.Min(cropSize,imageSize)*0.05;
            return Limit(center,overlap-cropSize/2,imageSize-overlap+cropSize/2);
        }
        void Clamp() {
            // Keep the crop overlapping the photo even when their edges are angled.
            var axes=Matrix.Identity;
            axes.Rotate(Rotation);
            double inset=1/(Math.Abs(axes.M11)+Math.Abs(axes.M12)/Aspect);
            centerX=LimitCenter(centerX,CropW*inset,image.PixelWidth);
            centerY=LimitCenter(centerY,CropH*inset,image.PixelHeight); InvalidateVisual();
        }
        public void Reset() {
            zoom=1; Rotation=0; centerX=image.PixelWidth/2.0; centerY=image.PixelHeight/2.0; Clamp();
            SetBrightness(0);
            if(RotationChanged!=null) RotationChanged();
        }
        internal void SetBrightness(double value) {
            int level=(int)Math.Round(value);
            if(level==Brightness)return;
            Brightness=level;InvalidateVisual();
            if(BrightnessChanged!=null)BrightnessChanged();
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
            Rotation=angle;
            Clamp();
            if(RotationChanged!=null) RotationChanged();
        }
        public void SetZoom(double value) {
            zoom=Limit(value,MinZoom,MaxZoom);Clamp();BeginDrag();
        }
        Rect Frame {
            get { double w=Math.Max(1,Math.Min(ActualWidth-40,(ActualHeight-40)*Aspect));
                return new Rect((ActualWidth-w)/2,(ActualHeight-w/Aspect)/2,w,w/Aspect); }
        }
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
        static double SnapCenter(double center,double cropSize,double imageSize,double unitsPerPixel,ref double? snapped) {
            const double engage=8,release=12;
            if(snapped.HasValue && Math.Abs(center-snapped.Value)<=release*unitsPerPixel)return snapped.Value;
            double near=cropSize/2,far=imageSize-near;
            double edge=Math.Abs(center-near)<=Math.Abs(center-far)?near:far;
            snapped=Math.Abs(center-edge)<=engage*unitsPerPixel?(double?)edge:null;
            return snapped??center;
        }
        internal void BeginDrag() {dragX=centerX;dragY=centerY;snappedX=snappedY=null;}
        internal void Drag(double dx,double dy) {
            var delta=SourceDelta(dx,dy);
            centerX=dragX-delta.X;centerY=dragY-delta.Y;Clamp();
            // Keep the pointer's raw position so the stronger hold still releases predictably.
            dragX=centerX;dragY=centerY;
            if(!snapEdges || Rotation%90!=0)return;
            double unitsPerPixel=CropW/Frame.Width;
            bool quarterTurn=Math.Abs(Rotation)==90;
            centerX=SnapCenter(dragX,quarterTurn?CropH:CropW,image.PixelWidth,unitsPerPixel,ref snappedX);
            centerY=SnapCenter(dragY,quarterTurn?CropW:CropH,image.PixelHeight,unitsPerPixel,ref snappedY);
        }
        protected override void OnRender(DrawingContext dc) {
            base.OnRender(dc);
            if(ActualWidth<1 || ActualHeight<1) return;
            Rect r=Frame;
            dc.DrawRectangle(Brushes.Black,null,new Rect(RenderSize));
            Pictures.DrawCover(dc,image,Fraction,Rotation,r,Brightness);
            dc.DrawRectangle(null,new Pen(UI.B("Accent"),2),r);
        }
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) {
            base.OnMouseLeftButtonDown(e); Focus(); last=e.GetPosition(this); BeginDrag(); CaptureMouse(); e.Handled=true;
        }
        protected override void OnMouseMove(MouseEventArgs e) {
            if(IsMouseCaptured && e.LeftButton==MouseButtonState.Pressed) {
                Point p=e.GetPosition(this); Drag(p.X-last.X,p.Y-last.Y); last=p;
            }
        }
        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { if(IsMouseCaptured) ReleaseMouseCapture(); }
        protected override void OnMouseWheel(MouseWheelEventArgs e) {
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
                foreach(Window window in Application.Current.Windows) {
                    var gallery=window as GalleryWindow;
                    if(gallery!=null)return gallery.Settings.PinTopOnWheel;
                }
                return true;
            }
        }
        protected override void OnKeyDown(KeyEventArgs e) {
            switch(e.Key) {
                case Key.Add: case Key.OemPlus: SetZoom(zoom*1.14);break;
                case Key.Subtract: case Key.OemMinus: SetZoom(zoom/1.14);break;
                default: base.OnKeyDown(e);return;
            }
            e.Handled=true;
        }
    }
    public class CropWindow : Window {
        public readonly CropSurface Surface;
        internal bool BackToFrames { get; private set; }
        internal int FrameIndex { get; private set; }
        internal double? FrameTime {get{return nearby==null?(double?)null:nearby.Start(FrameIndex);}}
        internal int FrameTimeOffset {get{return nearby==null?0:nearby.TimestampOffset(FrameIndex);}}
        internal Func<CropWindow,Task> CommitCover;
        // Runs after the cover is saved and before the editor closes (the large view opens here, so no gap shows).
        internal Func<Task> AfterCommit;
        readonly Button save,motionButton;
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
            int previewSide=2200,int brightness=0) {
            this.previewSide=previewSide;
            UI.WindowSetup(this,saved.HasValue?L.T("Make another cover"):L.T("Make cover")); Owner=owner; ShowInTaskbar=false;
            WindowStartupLocation=WindowStartupLocation.CenterOwner;
            Width=Math.Min(880,SystemParameters.WorkArea.Width); Height=Math.Min(850,SystemParameters.WorkArea.Height);
            MinWidth=390; MinHeight=360;
            var root=new DockPanel();
            root.Children.Add(FullScreen.TitleBar(this,FullScreen.IsFull(owner)));
            var head=UI.Bar(new Thickness(6,2,6,2));
            var seq=UI.Text("9:16",11,true);seq.Margin=new Thickness(4,0,6,0);UI.Right(head,seq);
            var title=UI.Text(name,12); title.TextTrimming=TextTrimming.CharacterEllipsis; head.Children.Add(title);
            DockPanel.SetDock(head,Dock.Top);root.Children.Add(head);
            Surface=new CropSurface(image,saved,rotation,snapEdges,wheelZoomPercent,brightness);
            AutomationProperties.SetHelpText(Surface,L.T("Drag to reposition and scroll to zoom."));
            var bar=UI.Bar(new Thickness(6,2,6,2));
            save=UI.Btn(L.T("Save cover"),SaveCover,true); save.IsDefault=true; UI.Right(bar,save);
            var cancel=UI.Btn(L.T("Cancel"),()=>{
                BackToFrames=previousFrame!=null;
                DialogResult=false;
            });
            cancel.Margin=new Thickness(0,0,5,0); UI.Right(bar,cancel);
            motionButton=UI.Btn(L.T("Make GIF cover"),MakeMotion);motionButton.Margin=new Thickness(0,0,5,0);
            motionButton.Visibility=Visibility.Collapsed;motionButton.ToolTip=L.T("Save the JPG cover and also make a GIF cover around this moment.");
            UI.Right(bar,motionButton);
            var reset=UI.Btn(L.T("Reset"),()=>Surface.Reset()); reset.Margin=new Thickness(0,0,8,0); DockPanel.SetDock(reset,Dock.Left);bar.Children.Add(reset);
            bar.LastChildFill=false;
            DockPanel.SetDock(bar,Dock.Bottom);root.Children.Add(bar);
            var rotationBar=RotationBar();
            DockPanel.SetDock(rotationBar,Dock.Bottom);root.Children.Add(rotationBar);
            var brightnessBar=BrightnessBar();
            DockPanel.SetDock(brightnessBar,Dock.Bottom);root.Children.Add(brightnessBar);
            root.Children.Add(Surface);Content=root;
            ContentRendered+=delegate { Surface.Focus(); };
            Closing+=(s,e)=>{if(saving)e.Cancel=true;};
            PreviewKeyDown+=(s,e)=>{
                if(saving){e.Handled=true;return;}
                var key=e.Key==Key.ImeProcessed?e.ImeProcessedKey:e.Key;
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
                        Surface.SetBrightness(key==Key.Q?0:Math.Max(-100,Math.Min(100,Surface.Brightness+(key==Key.E?1:-1))));break;
                    default:return;
                }
                e.Handled=true;
            };
        }
        DockPanel BrightnessBar() {
            var bar=UI.Bar(new Thickness(6,2,6,2));
            var label=UI.Text(L.T("Brightness"),12);
            label.Margin=new Thickness(0,0,8,0);bar.Children.Add(label);
            var value=UI.Text("",12);value.MinWidth=40;bar.Children.Add(value);
            var reset=RotationShortcut("0","Reset brightness to 0",()=>Surface.SetBrightness(0));
            UI.Right(bar,reset);
            var slider=new DragSlider {Minimum=-100,Maximum=100,TickFrequency=1,IsSnapToTickEnabled=true,
                SmallChange=1,LargeChange=10,IsMoveToPointEnabled=true,Margin=new Thickness(6,0,4,0)};
            AutomationProperties.SetName(slider,L.T("Brightness"));
            Action update=()=>{slider.Value=Surface.Brightness;value.Text=Surface.Brightness.ToString("+0;-0;0");};
            Surface.BrightnessChanged+=update;update();
            slider.ValueChanged+=(s,e)=>Surface.SetBrightness(slider.Value);
            bar.Children.Add(slider);
            return bar;
        }
        // Shows Make GIF for videos and GIFs. The callback saves the cover, then makes the GIF in the background.
        internal void EnableMotion(Func<CropWindow,MotionRequest,Task> commit,Func<MotionSource> source) {
            commitMotion=commit;motionSource=source;
            motionButton.Visibility=commit==null?Visibility.Collapsed:Visibility.Visible;
        }
        void MakeMotion() {
            if(saving || !save.IsEnabled || commitMotion==null)return;
            if(motionSource==null)return;
            var dialog=new MotionWindow(this,motionSource(),true);
            if(dialog.ShowDialog()!=true)return;
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
            previousFrame=UI.Btn(L.T("Previous frame (D)"),()=>MoveFrame(-1));bar.Children.Add(previousFrame);
            frameNumber=UI.Text("",11,true);frameNumber.Margin=new Thickness(6,0,6,0);frameNumber.TextAlignment=TextAlignment.Center;
            // Wide enough for the longest label so the Next button never moves.
            var probe=new TextBlock {Text=localTimes==null?String.Format("{0:N0} / {0:N0}",count).Replace('1','0').Replace('2','0').Replace('3','0').Replace('4','0').Replace('5','0').Replace('6','0').Replace('7','0').Replace('8','0').Replace('9','0'):Viewer.Stamp(0),FontSize=11};
            Typography.SetNumeralAlignment(probe,FontNumeralAlignment.Tabular);probe.Measure(new Size(Double.PositiveInfinity,Double.PositiveInfinity));
            frameNumber.MinWidth=Math.Ceiling(probe.DesiredSize.Width)+2;
            bar.Children.Add(frameNumber);
            nextFrame=UI.Btn(L.T("Next frame (F)"),()=>MoveFrame(1));bar.Children.Add(nextFrame);
            DockPanel.SetDock(bar,Dock.Bottom);
            var root=(DockPanel)Content;root.Children.Insert(root.Children.IndexOf(Surface),bar);
            UpdateFrameControls(true);
        }
        void UpdateFrameControls(bool ready) {
            save.IsEnabled=ready;
            // A video's list can be read further past either end, until the video's start or end.
            previousFrame.IsEnabled=ready && (FrameIndex>0 || (nearby!=null && !nearbyAtStart));
            nextFrame.IsEnabled=ready && (FrameIndex<frameCount-1 || (nearby!=null && !nearbyAtEnd));
            frameNumber.Text=!ready?L.T(readingMore?"Reading more…":"Loading…"):nearby==null?String.Format("{0:N0} / {1:N0}",FrameIndex+1,frameCount):
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
