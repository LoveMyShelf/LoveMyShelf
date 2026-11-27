using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace LoveMyShelf {
    public class Viewer : Window {
        readonly IList<Photo> items;
        readonly Grid root=new Grid {Background=Brushes.Black};
        // Everything drawn above the media. It moves into a floating overlay window while mpv plays.
        readonly Grid ui=new Grid();
        readonly Grid windowContent=new Grid();
        readonly WindowTitleBar titleBar;
        readonly MediaViewport viewport=new MediaViewport {ClipToBounds=true};
        readonly Image picture=new Image {Stretch=Stretch.Uniform};
        readonly Border brightnessLayer=new Border {IsHitTestVisible=false,HorizontalAlignment=HorizontalAlignment.Center,
            VerticalAlignment=VerticalAlignment.Center,Visibility=Visibility.Collapsed};
        readonly TextBlock caption=UI.Text("",12),time=UI.Text("",11);
        readonly NoticeCard status=new NoticeCard();
        // Small corner note for shortcut changes (speed, brightness), like PotPlayer's on-screen text.
        readonly TextBlock cornerNote=new TextBlock {FontSize=15,FontWeight=FontWeights.SemiBold,Foreground=Brushes.White,
            Margin=new Thickness(16,12,16,12),HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top,
            IsHitTestVisible=false,Visibility=Visibility.Collapsed,
            Effect=new System.Windows.Media.Effects.DropShadowEffect {Color=Colors.Black,BlurRadius=5,ShadowDepth=1,Direction=315,Opacity=0.95}};
        readonly DispatcherTimer cornerNoteTimer=new DispatcherTimer {Interval=TimeSpan.FromSeconds(1.6)};
        internal string CornerNote {get{return cornerNote.Visibility==Visibility.Visible?cornerNote.Text:null;}}
        readonly StackPanel controls=new StackPanel {Orientation=Orientation.Horizontal};
        readonly StackPanel panel=new StackPanel();
        readonly DockPanel transport=UI.Bar(new Thickness(5,1,5,3));
        readonly StackPanel playbackButtons=new StackPanel {Orientation=Orientation.Horizontal};
        readonly DragSlider seek=new DragSlider {Minimum=0,IsMoveToPointEnabled=true,VerticalAlignment=VerticalAlignment.Center};
        readonly SeekPreview seekPreview;
        readonly DispatcherTimer ticker=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(30)};
        readonly DispatcherTimer surfaceClick=new DispatcherTimer {
            Interval=TimeSpan.FromMilliseconds(System.Windows.Forms.SystemInformation.DoubleClickTime)};
        readonly Stopwatch clock=new Stopwatch();
        readonly Button toggle,prev,next,pause,locate,recrop,editCover,upscale,motionToggle,makeGif,areaButton;
        // The box drawn over a playing video, GIF or WebP: where a new cover starts (apart from zoom and turn).
        readonly CoverAreaLayer coverArea;
        readonly Popup areaPopup;
        DrawnArea deletedArea,enlargedArea;
        string viewedFile;
        double previousZoom,lastPanelHeight;
        int previousTurn;
        Vector previousPan;
        string areaFile; // the original the box was drawn on
        FramePlayer motionPlayer;
        CancellationTokenSource motionLoading;
        bool showMotion=true;
        int coverHeight;
        readonly Slider volume;
        readonly int skipSeconds;
        readonly int displaySide;
        readonly bool forCrop;
        readonly bool? openCover;
        public Photo CurrentPhoto {get{return items[index];}}
        // The folder window this large view was opened from (it is not the owner once the view is open).
        readonly GalleryWindow home;
        internal GalleryWindow Home {get{return home;}}
        internal bool ForCrop {get{return forCrop;}}
        bool WantsCrop {get{return forCrop || !CurrentPhoto.HasCrop;}}
        string CropLabel {get{return CurrentPhoto.HasCrop?L.T("Make one more cover"):L.T("Make cover");}}
        int index,shown=-1;
        bool fullscreen,original,hidden,motion,paused,sync,resumeAfterDrag,selectedMovie,largeVideo,draggingVideo,cachedCover;
        int playbackBrightness;
        int playbackStyle=Light.Soft; // R: Soft, See in the dark or Milky (see Light)
        int PlaybackValue {get {return Light.Encode(playbackBrightness,playbackStyle);}}
        // E / W / Q was pressed for this file (a 0 then means "back to normal"). Both start over with every new file.
        bool brightnessChosen;string brightnessFile;
        // The picture and the turning still as they came, before the brightness curve (see Light) is put on them.
        BitmapSource pictureRaw,turnRaw;
        // The playback speed stays for every video opened while LoveMyShelf runs, not only in one viewer window.
        static double lastRate=1;
        double duration,startMs,rate=lastRate;
        FrameTimes times;
        Task<FrameTimes> videoIndex;
        FrameRequests requests;
        MediaElement media;
        MpvPlayer mpv;
        VideoOverlay overlay;
        bool Native {get{return media!=null || mpv!=null;}}
        bool mpvAudio;
        BitmapSource full,cropped;
        CancellationTokenSource loading;
        public int CropFrame {get;private set;}
        public double? CropTime {get;private set;}
        // Make cover from mpv playback lists the frames around the paused time first.
        internal bool CropNearby {get;private set;}
        // The brightness set with E/W while watching; the scene grid and the cover editor start from it.
        internal int PlaybackBrightness {get {return PlaybackValue;}}
        // Contrast, saturation, color temperature and sharpness while watching (brightness is playbackBrightness).
        // Like brightness: for this video only, and the cover editor and the GIF cover start from them.
        AdjustValues playbackAdjust;
        bool adjustShown,adjustChosen;
        // True once contrast, saturation, color temperature or sharpness was changed for this video.
        internal bool AdjustChosen {get {return adjustChosen;}}
        internal AdjustValues PlaybackAdjust {get {var a=playbackAdjust;a.Brightness=PlaybackValue;return a;}}
        internal bool BrightnessChosen {get {return brightnessChosen;}}
        // The turn, zoom and place on screen, for the seek thumbnail, the scene list and the cover editor.
        // Null when the picture is simply upright at its own size.
        internal ViewFraming CurrentFraming {get {
            double aspect=UprightAspect;
            var area=ViewArea;
            if((drawnTurn==0 && zoom==1) || aspect<=0 || area.Width<=0 || area.Height<=0)return null;
            var upright=MediaViewport.Fit(aspect,area.Size);
            var box=TurnedBox(upright.Size);
            return new ViewFraming {Turn=drawnTurn>180?drawnTurn-360:drawnTurn,
                PictureWidth=upright.Width*zoom,PictureHeight=upright.Height*zoom,PanX=pan.X,PanY=pan.Y,
                ShownWidth=Math.Min(area.Width,box.Width*zoom),ShownHeight=Math.Min(area.Height,box.Height*zoom)};
        }}

        // Where the picture is right now, for the cover box (CoverAreaLayer).
        AreaView? AreaNow() {
            double aspect=UprightAspect;
            var area=ViewArea;
            if(aspect<=0 || area.Width<=0 || area.Height<=0)return null;
            var upright=MediaViewport.Fit(aspect,area.Size);
            return new AreaView {Middle=new Point(area.Width/2+pan.X,area.Height/2+pan.Y),
                Picture=new Size(upright.Width*zoom,upright.Height*zoom),Turn=drawnTurn>180?drawnTurn-360:drawnTurn,Room=area};
        }
        // The cover box drawn on the file shown now, or null.
        internal DrawnArea CoverArea {get {
            if(coverArea==null || !motion || areaFile==null || !String.Equals(areaFile,CurrentPhoto.Original,StringComparison.OrdinalIgnoreCase))return null;
            return coverArea.Area;
        }}
        void UpdateAreaButton() {
            if(areaButton==null)return;
            areaButton.Visibility=motion?Visibility.Visible:Visibility.Collapsed;
            areaButton.SetResourceReference(Control.BorderBrushProperty,coverArea.Editing?"Accent":"Line");
        }
        void ToggleAreaPopup() {
            if(areaPopup.IsOpen){CloseAreaPopup(true);return;}
            if(!motion)return;
            PositionAreaPopup();
            areaPopup.IsOpen=true;
            if(CoverArea==null && enlargedArea==null)coverArea.StartEditing();
            RefreshFullscreenControls();
        }
        void CloseAreaPopup(bool takeKeys=false) {
            if(areaPopup==null || !areaPopup.IsOpen)return;
            coverArea.ApplyTyping();
            areaPopup.IsOpen=false;
            if(takeKeys)ReturnAreaKeys();
            RefreshFullscreenControls();
        }
        void ReturnAreaKeys() {
            if(areaPopup.IsOpen)Keyboard.Focus(coverArea.Menu);
            else {Keyboard.Focus(this);TakeKeys();}
        }
        void AreaOutsidePress(object sender,MouseButtonEventArgs e) {
            if(areaPopup==null || !areaPopup.IsOpen)return;
            // The same press can start a drag; leave the event on its normal route.
            for(var source=e.OriginalSource as DependencyObject;source!=null;
                source=source is Visual?VisualTreeHelper.GetParent(source):LogicalTreeHelper.GetParent(source)) {
                if(source==coverArea.Menu || (source==areaButton && e.ChangedButton==MouseButton.Left))return;
            }
            CloseAreaPopup(true);
        }
        void AreaDeactivated() {
            Dispatcher.BeginInvoke(DispatcherPriority.Input,new Action(()=>{
                if(!areaPopup.IsOpen)return;
                var source=PresentationSource.FromVisual(coverArea.Menu) as HwndSource;
                if(!IsActive && (overlay==null || !overlay.IsActive) &&
                    (source==null || source.Handle!=GetForegroundWindow()))CloseAreaPopup();
            }));
        }
        Rect AreaAnchor(Size size) {
            var anchor=areaButton.TranslatePoint(new Point(),ui);
            double right=Math.Max(0,ui.ActualWidth-size.Width-4);
            double x=Math.Max(4,Math.Min(right,areaButton.TranslatePoint(new Point(areaButton.ActualWidth,0),ui).X-size.Width));
            // Above the whole bar (the seek line too), so nothing of it is covered.
            double barTop=panel.Visibility==Visibility.Visible && panel.ActualHeight>0?panel.TranslatePoint(new Point(),ui).Y:anchor.Y;
            double y=Math.Max(0,Math.Min(anchor.Y,barTop)-size.Height-4);
            var normal=new Rect(new Point(x,y),size);
            var overlap=Rect.Intersect(normal,coverArea.ScreenBox);
            if(!overlap.IsEmpty && overlap.Width*overlap.Height>normal.Width*normal.Height*0.20) {
                var alternate=new Rect(x>right/2?4:right,y,size.Width,size.Height);
                var other=Rect.Intersect(alternate,coverArea.ScreenBox);
                if(other.IsEmpty || other.Width*other.Height<overlap.Width*overlap.Height)return alternate;
            }
            return normal;
        }
        void DeleteDrawnArea() {
            if(CoverArea==null)return;
            if(enlargedArea!=null)ToggleAreaView();
            deletedArea=CoverArea;
            coverArea.Clear();
            status.VerticalAlignment=VerticalAlignment.Bottom;
            PlaceAreaNotice();
            status.Show(L.P("Box cleared · Undo Ctrl+Z","네모를 지웠어요 · 되돌리기 Ctrl+Z"),null,NoticeKind.Info,4);
            areaNoticeDone.Stop();areaNoticeDone.Start();
        }
        // The box notice sits over the bar; the next notice shows in the middle again.
        readonly DispatcherTimer areaNoticeDone=new DispatcherTimer {Interval=TimeSpan.FromSeconds(4.3)};
        void NoticeBackToMiddle() {
            areaNoticeDone.Stop();
            if(status.VerticalAlignment!=VerticalAlignment.Bottom)return;
            status.VerticalAlignment=VerticalAlignment.Center;status.Margin=new Thickness(20,28,20,28);
        }
        void PlaceAreaNotice() {
            if(status.VerticalAlignment==VerticalAlignment.Bottom)
                status.Margin=new Thickness(20,0,20,(panel.Visibility==Visibility.Visible?panel.ActualHeight:0)+10);
        }
        bool HandleAreaKey(Key key,ModifierKeys modifiers,bool typing) {
            if(typing) {
                if(key==Key.Escape)coverArea.CancelTyping();
                else if(key==Key.Enter)coverArea.ApplyTyping();
                else return false;
                return true;
            }
            if(key==Key.Z && modifiers==ModifierKeys.Control) {
                if(deletedArea!=null && CoverArea==null && motion) {
                    coverArea.Restore(deletedArea);deletedArea=null;status.Text="";NoticeBackToMiddle();
                }
                return true;
            }
            if(key==Key.Enter && coverArea.Editing && modifiers==ModifierKeys.None) {
                coverArea.StopEditing();return true;
            }
            if(key!=Key.Escape)return false;
            if(areaPopup.IsOpen)CloseAreaPopup(true);
            else if(keyPanel!=null && keyPanel.IsOpen)keyPanel.IsOpen=false;
            else if(enlargedArea!=null)ToggleAreaView();
            else if(CoverArea!=null)DeleteDrawnArea();
            else if(coverArea.Editing)coverArea.StopEditing();
            else Close();
            return true;
        }
        void ToggleAreaView() {
            if(enlargedArea!=null) {
                enlargedArea=null;coverArea.SetEnlarged(false);
                zoom=previousZoom;pan=previousPan;turn=previousTurn;
                ShowTurn();
                return;
            }
            var box=CoverArea;
            if(box==null || !AreaNow().HasValue)return;
            bool resume=turnStillOn && turnResume;
            CancelTurn();
            if(resume)SetPaused(false);
            previousZoom=zoom;previousPan=pan;previousTurn=turn;
            enlargedArea=box;
            coverArea.SetEnlarged(true);
            turn=((int)Math.Round(box.Turn)%360+360)%360;
            ShowTurn();
        }
        void LeaveAreaView() {
            if(enlargedArea==null)return;
            enlargedArea=null;coverArea.SetEnlarged(false);
        }
        sealed class MediaViewport : Panel {
            internal static double Aspect(UIElement element) {
                var image=element as Image;
                if(image!=null)return image.Source==null?0:Turned(image.Source.Width,image.Source.Height,image.LayoutTransform);
                var native=element as MpvPlayer;
                if(native!=null)return native.Fill?0:native.Aspect; // zoomed in, mpv has the whole view
                var video=(MediaElement)element;
                if(video.NaturalVideoWidth<=0 || video.NaturalVideoHeight<=0)return 0;
                return Turned(video.NaturalVideoWidth,video.NaturalVideoHeight,video.LayoutTransform);
            }
            // Width over height of the box around a picture turned by its layout transform (O / P, or a video's own orientation).
            static double Turned(double width,double height,Transform turn) {
                var m=turn==null?Matrix.Identity:turn.Value;
                double w=Math.Abs(width*m.M11)+Math.Abs(height*m.M21),h=Math.Abs(width*m.M12)+Math.Abs(height*m.M22);
                return h<=0?0:w/h;
            }
            // The whole picture always fits inside, like PotPlayer: black bars at the sides in a wide window,
            // above and below in a tall one (two windows side by side). Nothing is ever cut off.
            internal static Rect Fit(double aspect,Size viewport) {
                if(aspect<=0)return new Rect(0,0,viewport.Width,viewport.Height);
                double width=viewport.Height*aspect,height=viewport.Height;
                if(width>viewport.Width){width=viewport.Width;height=width/aspect;}
                return new Rect((viewport.Width-width)/2,(viewport.Height-height)/2,width,height);
            }
            static Rect Frame(UIElement child,Size viewport) {return Fit(Aspect(child),viewport);}
            protected override Size MeasureOverride(Size available) {
                var size=new Size(Double.IsInfinity(available.Width)?0:available.Width,
                    Double.IsInfinity(available.Height)?0:available.Height);
                foreach(UIElement child in InternalChildren)child.Measure(Frame(child,size).Size);
                return new Size();
            }
            protected override Size ArrangeOverride(Size finalSize) {
                foreach(UIElement child in InternalChildren)child.Arrange(Frame(child,finalSize));
                return finalSize;
            }
        }

        // Wake the decoder for the newest request. A seek invalidates an already-decoding frame.
        sealed class FrameRequests {
            readonly object gate=new object();
            int target=-1,epoch;
            public void Ask(int i,bool jump) {
                lock(gate){if(jump)epoch++;target=i;Monitor.PulseAll(gate);}
            }
            public void Decode(AnimationFrames file,int side,CancellationToken token,Dispatcher dispatcher,Action<int,BitmapSource> present) {
                int last=-1,version=-1;
                using(token.Register(()=>{lock(gate)Monitor.PulseAll(gate);})) {
                    while(!token.IsCancellationRequested) {
                        lock(gate) {
                            while(!token.IsCancellationRequested && (target<0 || (last==target && version==epoch)))Monitor.Wait(gate);
                            if(token.IsCancellationRequested)return;
                            last=target;version=epoch;
                        }
                        var image=file.Read(last,side);
                        dispatcher.Invoke(new Action(()=>{
                            lock(gate){if(token.IsCancellationRequested || epoch!=version)return;}
                            present(last,image);
                        }));
                    }
                }
            }
        }
        public Viewer(Window owner,IList<Photo> photos,int index,bool forCrop=false)
            :this(owner,photos,index,forCrop,null) {}
        internal Viewer(Window owner,IList<Photo> photos,int index,bool forCrop,bool? openCover) {
            UI.WindowSetup(this,L.T("Large view"));Owner=owner;items=photos;this.index=index;this.forCrop=forCrop;
            this.openCover=openCover;
            var gallery=owner as GalleryWindow;home=gallery;
            // Like a video player opened from File Explorer: each large view is a window of its own, with its own
            // taskbar button, and the folder window stays usable behind it. The owner is only kept while it opens,
            // so it opens over the folder window; choosing a cover's frame (forCrop) stays a dialog of its owner.
            ShowInTaskbar=!forCrop;
            if(!forCrop)Loaded+=delegate {Owner=null;};
            skipSeconds=gallery==null?5:gallery.Settings.SkipSeconds;
            Background=Brushes.Black;
            var screen=System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(owner).Handle);
            displaySide=Math.Max(screen.Bounds.Width,screen.Bounds.Height);
            bool startFull=FullScreen.IsFull(owner);
            WindowStartupLocation=WindowStartupLocation.CenterOwner;
            Width=startFull?Math.Min(1120,SystemParameters.WorkArea.Width):owner.ActualWidth;
            Height=startFull?Math.Min(800,SystemParameters.WorkArea.Height):owner.ActualHeight;
            WindowState=owner.WindowState==WindowState.Maximized?WindowState.Maximized:WindowState.Normal;
            windowContent.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});
            windowContent.RowDefinitions.Add(new RowDefinition());
            Grid.SetRow(root,1);windowContent.Children.Add(root);
            titleBar=FullScreen.TitleBar(this,startFull,escapeExits:false,changed:SetFullscreen);
            titleBar.VerticalAlignment=VerticalAlignment.Top;
            windowContent.Children.Add(titleBar);Content=windowContent;
            RenderOptions.SetBitmapScalingMode(picture,BitmapScalingMode.HighQuality);
            viewport.Children.Add(picture);root.Children.Add(viewport);root.Children.Add(ui);
            ui.Children.Add(brightnessLayer);
            // Nearly transparent rather than transparent, so it still receives clicks in the overlay window.
            var hit=new Border {Background=new SolidColorBrush(Color.FromArgb(1,0,0,0))};
            surfaceClick.Tick+=delegate {surfaceClick.Stop();};
            turnSettle.Tick+=delegate {SettleTurn();};
            turnReady.Tick+=delegate {EndTurnStill();};
            RenderOptions.SetBitmapScalingMode(turnImage,BitmapScalingMode.HighQuality);
            turnLayer.Children.Add(turnImage);ui.Children.Insert(0,turnLayer); // over the video, under everything else
            // Part of the picture is past the edge: dragging moves it. Right-click: the controls with their keys.
            hit.MouseLeftButtonDown+=(s,e)=>{
                // Drawing the cover box: the left button draws, moves or resizes it instead of moving the picture.
                // A double-click still plays or pauses; a right-click still opens the controls.
                if(coverArea.Editing && e.ClickCount==1 && coverArea.Press(e.GetPosition(ui))) {
                    hit.CaptureMouse();surfaceClick.Stop();e.Handled=true;return;
                }
                if(!coverArea.Editing && PictureOverflows() && e.ClickCount==1){dragFrom=e.GetPosition(ui);panFrom=pan;panning=true;hit.CaptureMouse();}
                if(!motion)return;
                surfaceClick.Stop();
                if(e.ClickCount==2)SetPaused(!paused);
                else if(e.ClickCount==1 && !fullscreen)surfaceClick.Start();
                e.Handled=true;
            };
            hit.MouseMove+=(s,e)=>{
                if(coverArea.Dragging){coverArea.Drag(e.GetPosition(ui));return;}
                hit.Cursor=coverArea.Editing?coverArea.CursorAt(e.GetPosition(ui)):null;
                if(!panning || !hit.IsMouseCaptured)return;
                var delta=e.GetPosition(ui)-dragFrom;
                if(delta.LengthSquared>0)LeaveAreaView();
                pan=panFrom+delta;ApplyZoom();
            };
            hit.MouseLeftButtonUp+=delegate {
                if(coverArea.Dragging)coverArea.Release();
                panning=false;
                if(hit.IsMouseCaptured)hit.ReleaseMouseCapture();
            };
            hit.LostMouseCapture+=delegate {panning=false;if(coverArea.Dragging)coverArea.Release();};
            hit.MouseRightButtonUp+=(s,e)=>{ShowControls();e.Handled=true;};
            PreviewMouseWheel+=Wheel;
            ui.Children.Add(hit);
            coverArea=new CoverAreaLayer(AreaNow);
            coverArea.MakeCover=()=>{CloseAreaPopup(true);coverArea.StopEditing();StartCrop();};
            coverArea.ToggleZoom=ToggleAreaView;coverArea.DeleteArea=DeleteDrawnArea;
            areaNoticeDone.Tick+=delegate {NoticeBackToMiddle();};
            coverArea.Changed+=delegate {areaFile=coverArea.Area==null?null:CurrentPhoto.Original;};
            coverArea.ReturnKeys=ReturnAreaKeys;
            areaPopup=new Popup {AllowsTransparency=true,StaysOpen=true,Placement=PlacementMode.Relative,
                PlacementTarget=ui,Child=coverArea.Menu};
            coverArea.Menu.Focusable=true;
            areaPopup.Opened+=delegate {Keyboard.Focus(coverArea.Menu);};
            coverArea.Menu.PreviewKeyDown+=(s,e)=>{
                var key=e.Key==Key.ImeProcessed?e.ImeProcessedKey:e.Key==Key.System?e.SystemKey:e.Key;
                if(key==Key.Escape && e.IsRepeat){e.Handled=true;return;}
                if((key==Key.Enter || key==Key.Space) && e.OriginalSource is ButtonBase)return; // a button the keyboard is on gets pressed
                if(HandleAreaKey(key,Keyboard.Modifiers,coverArea.Typing || e.OriginalSource is TextBox))e.Handled=true;
                else if(key==Key.Enter && ViewerKeys.ActionFor(key)!=null) {
                    var forwarded=new KeyEventArgs(e.KeyboardDevice,PresentationSource.FromVisual(this),e.Timestamp,key) {
                        RoutedEvent=Keyboard.PreviewKeyDownEvent};
                    RaiseEvent(forwarded);e.Handled=forwarded.Handled;
                }
            };
            // Drawing ended or stopped mid-drag (Esc, the next file): the mouse is let go and the cursor is plain again.
            coverArea.EditingChanged+=delegate {
                UpdateAreaButton();
                if(!coverArea.Editing){hit.Cursor=null;if(!panning && hit.IsMouseCaptured)hit.ReleaseMouseCapture();}
                RefreshFullscreenControls();
            };
            ui.Children.Add(coverArea); // over the picture, under the bar, the notices and the controls
            TextOptions.SetTextFormattingMode(cornerNote,TextFormattingMode.Ideal);
            TextOptions.SetTextFormattingMode(infoText,TextFormattingMode.Ideal);
            // The file information (Tab) sits at the top left with the short notes under it, so a note never moves it.
            var corner=new StackPanel {HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top,IsHitTestVisible=false};
            corner.Children.Add(infoText);corner.Children.Add(cornerNote);
            ui.Children.Add(corner);
            infoTimer.Tick+=delegate {RefreshInfo();};
            holdTimer.Tick+=delegate {
                holdTimer.Interval=TimeSpan.FromMilliseconds(90);
                if(holdAction==null){holdTimer.Stop();return;}
                holdRepeating=true;
                try {holdAction();} finally {holdRepeating=false;}
            };
            fromCollection=gallery!=null && gallery.ShowingAllCrops;
            Closing+=delegate {CloseAreaPopup();};
            Closed+=delegate {deletedArea=null;infoTimer.Stop();holdTimer.Stop();if(keyPanel!=null)keyPanel.IsOpen=false;};
            cornerNoteTimer.Tick+=delegate {
                cornerNoteTimer.Stop();
                var fade=new System.Windows.Media.Animation.DoubleAnimation(0,TimeSpan.FromMilliseconds(250));
                fade.Completed+=delegate {if(!cornerNoteTimer.IsEnabled)cornerNote.Visibility=Visibility.Collapsed;};
                cornerNote.BeginAnimation(OpacityProperty,fade);
            };
            panel.VerticalAlignment=VerticalAlignment.Bottom;
            panel.SizeChanged+=delegate {
                if(panel.Visibility==Visibility.Visible && panel.ActualHeight>0)lastPanelHeight=panel.ActualHeight;
                FitPictureAbovePanel();PlaceAreaNotice();
            };
            panel.SetResourceReference(Panel.BackgroundProperty,"Paper");
            var footer=new Grid {Margin=new Thickness(5,0,5,4)};
            footer.ColumnDefinitions.Add(new ColumnDefinition());
            footer.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
            footer.ColumnDefinitions.Add(new ColumnDefinition());
            var playbackRow=new Viewbox {Child=playbackButtons,StretchDirection=StretchDirection.DownOnly,
                HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(5,0,5,0)};
            var actionRow=new Viewbox {Child=controls,StretchDirection=StretchDirection.DownOnly,
                HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Center};
            footer.Children.Add(playbackRow);
            Grid.SetColumn(actionRow,2);footer.Children.Add(actionRow);
            playbackButtons.Children.Add(Pill(time,new Thickness(6,0,8,0)));
            volume=new DragSlider {Minimum=0,Maximum=100,Value=100,Width=82,SmallChange=5,LargeChange=10,
                IsMoveToPointEnabled=true,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(4,0,8,0)};
            LabelControl(volume,L.T("Volume"));
            if(gallery!=null)volume.Value=gallery.Settings.Volume; // the volume used last time
            volume.ValueChanged+=delegate {muted=false;ApplyAudio();}; // moving the bar turns the sound back on
            playbackButtons.Children.Add(volume);
            // Clear voice, capture and the subtitle explorer have no button here: they are in the right-click
            // controls (ShowControls) with their keys, so the bar stays short.
            clearVoice=gallery!=null && gallery.Settings.ClearVoice;
            if(gallery!=null)clearVoiceLevel=gallery.Settings.ClearVoiceLevel;
            if(gallery!=null) {
                // The subtitle size is shared: one chosen here or in Guide & Options is used by the next video too.
                subtitleSize=gallery.Settings.SubtitleScale;
                Action follow=()=>{
                    if(subtitleSize==gallery.Settings.SubtitleScale)return;
                    subtitleSize=gallery.Settings.SubtitleScale;
                    if(mpv!=null)mpv.SubtitleScale=subtitleSize/100.0;
                };
                gallery.SettingsChangedEvent+=follow;Closed+=delegate {gallery.SettingsChangedEvent-=follow;};
            }
            // Speed has no box: Z / X / C like PotPlayer (see the key handler).
            root.SizeChanged+=delegate {ApplyZoom();}; // also brightness and subtitles, which follow the picture's place
            seek.Style=(Style)Application.Current.Resources["PlaybackTimeline"];
            AutomationProperties.SetName(seek,L.T("Playback position"));
            // The marks of the GIF cover part lie over the bar and never take a click from it.
            var seekHost=new Grid();seekHost.Children.Add(seek);seekHost.Children.Add(partMarks);transport.Children.Add(seekHost);
            seek.SizeChanged+=delegate {ShowPart();};
            // Right-click the bar twice: the part between the two places plays again and again (the same part as [ and ]).
            // Only a right-click that starts and ends on the bar counts, and not while the left button drags the bar.
            bool seekRight=false;
            seek.PreviewMouseRightButtonDown+=(s,e)=>{
                if(!motion || e.LeftButton==MouseButtonState.Pressed)return;
                seekRight=true;e.Handled=true;
            };
            seek.MouseLeave+=delegate {seekRight=false;};
            seek.PreviewMouseRightButtonUp+=(s,e)=>{
                if(!seekRight)return;
                seekRight=false;e.Handled=true;
                if(!motion || duration<=1 || seek.ActualWidth<=12)return;
                double x=e.GetPosition(seek).X;
                double ms=Math.Max(0,Math.Min(1,(x-5)/(seek.ActualWidth-10)))*duration;
                if((Keyboard.Modifiers&ModifierKeys.Shift)!=0)ClearPart();else PointPart(ms);
            };
            seek.ToolTip=L.P("Right-click two places on the bar: the part between them repeats. Shift + right-click clears it.",
                "재생바의 두 곳을 오른쪽 클릭하면 그 사이가 반복돼요. Shift+오른쪽 클릭이면 지워져요.");
            seekPreview=new SeekPreview(ui,seek);
            seek.ValueChanged+=(s,e)=>{
                if(sync || !motion)return;
                if(draggingVideo) {
                    UpdateBar(e.NewValue);
                    if(mpv!=null)mpv.Seek(e.NewValue,false); // fast keyframe scrubbing, exact on release
                }
                else SeekTo(e.NewValue);
            };
            seek.PointerDragStarted+=delegate {
                if(!motion)return;
                draggingVideo=Native;resumeAfterDrag=!draggingVideo && !paused;
                if(resumeAfterDrag)SetPaused(true);
            };
            seek.PointerDragCompleted+=delegate {
                if(!motion)return;
                if(draggingVideo){draggingVideo=false;SeekTo(seek.Value);}
                else if(resumeAfterDrag){resumeAfterDrag=false;SetPaused(false);}
            };
            panel.Children.Add(transport);panel.Children.Add(footer);
            prev=UI.Btn("‹ K",()=>Navigate(-1));prev.Width=48;next=UI.Btn("L ›",()=>Navigate(1));next.Width=48;
            LabelControl(prev,L.T("Previous file (K)"));LabelControl(next,L.T("Next file (L)"));
            toggle=UI.Btn(WantsCrop?CropLabel:L.T("View original"),Primary,true);toggle.MinWidth=66;
            pause=UI.Btn(L.T("Pause"),()=>SetPaused(!paused));
            pause.ToolTip=L.T("Play or pause (Space)");
            pause.Visibility=Visibility.Collapsed;
            pause.SetBinding(WidthProperty,new Binding("ActualWidth") {Source=toggle});
            var captionPill=Pill(caption,new Thickness(8,0,8,0));
            if(!forCrop) {
                var navigation=new StackPanel {Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};
                navigation.Children.Add(prev);navigation.Children.Add(captionPill);navigation.Children.Add(next);
                Grid.SetColumn(navigation,1);footer.Children.Add(navigation);
            }
            // Order: Pause · Add cover · View cover/original · … · Close. While "Add cover" shows it is the blue button.
            controls.Children.Add(pause);
            recrop=UI.Btn(L.T("Add cover"),StartCrop,true);recrop.Visibility=Visibility.Collapsed;
            recrop.Margin=new Thickness(4,0,0,0);
            if(!forCrop)controls.Children.Add(recrop);
            areaButton=UI.Btn(L.P("▭ Where to cut ▾","▭ 자를 곳 ▾"),ToggleAreaPopup);
            areaButton.Margin=new Thickness(4,0,0,0);areaButton.Visibility=Visibility.Collapsed;
            areaButton.ToolTip=L.P("Draw a box over the video: Make cover starts from it. Separate from zooming and turning.",
                "영상 위에 네모를 그리면 표지 만들기가 그 네모로 시작해요. 확대·회전과는 따로예요.");
            controls.Children.Add(areaButton);
            toggle.Margin=new Thickness(4,0,4,0);controls.Children.Add(toggle);
            editCover=UI.Btn(L.T("Edit cover"),EditCover);editCover.Visibility=Visibility.Collapsed;
            if(!forCrop)controls.Children.Add(editCover);
            upscale=UI.Btn(L.T("Upscale"),UpscaleCover);upscale.Visibility=Visibility.Collapsed;
            // Make GIF from the saved cover, shown on the cover screen of videos, GIFs and WebPs.
            makeGif=UI.Btn(L.T("Make GIF cover"),()=>{if(home!=null)home.ShowMakeGif(this,CurrentPhoto);});
            makeGif.Visibility=Visibility.Collapsed;
            if(!forCrop && home!=null)controls.Children.Add(makeGif);
            if(!forCrop && gallery!=null) {
                controls.Children.Add(upscale);
                Action<string> stageChanged=folder=>{if(String.Equals(folder,CurrentPhoto.Folder,StringComparison.OrdinalIgnoreCase))UpdateUpscale();};
                Action<Photo> replaced=photo=>{
                    if(!String.Equals(photo.Folder,CurrentPhoto.Folder,StringComparison.OrdinalIgnoreCase))return;
                    bool showingCover=cachedCover && !motion;
                    items[index]=photo;
                    if(showingCover)LoadCurrent(true);else UpdateUpscale();
                };
                gallery.Upscales.Changed+=stageChanged;gallery.CoverReplaced+=replaced;
                Closed+=delegate {gallery.Upscales.Changed-=stageChanged;gallery.CoverReplaced-=replaced;};
            }
            motionPlayer=new FramePlayer(picture);
            motionToggle=UI.Btn(L.T("View JPG cover"),()=>{
                showMotion=!showMotion;
                if(showMotion)PlayMotion(items[index]);else StopMotion();
                UpdateMotionToggle();UpdateCoverButtons();
            });
            motionToggle.Visibility=Visibility.Collapsed;
            if(!forCrop)controls.Children.Add(motionToggle);
            Closed+=delegate {StopMotion();prepared.Clear();};
            locate=UI.Btn(L.T("Find the original again"),ChooseOriginal);locate.Visibility=Visibility.Collapsed;controls.Children.Add(locate);
            controls.Children.Add(UI.Btn(L.T("Close"),Close));
            if(!forCrop && gallery!=null) {
                coverStrip.Chosen=ChooseSibling;
                ui.Children.Add(coverStrip); // under the bar, which floats over it in full screen
                root.SizeChanged+=delegate {PlaceCoverStrip();};
                stripSleep.Tick+=delegate {
                    if(coverStrip.IsMouseOver)return;
                    stripSleep.Stop();stripAwake=false;PlaceCoverStrip();
                };
                AddHandler(Mouse.PreviewMouseMoveEvent,new MouseEventHandler((s,e)=>WakeCoverStrip()),true);
                Closed+=delegate {stripSleep.Stop();stripRequest++;};
            }
            ui.Children.Add(panel);
            status.HorizontalAlignment=HorizontalAlignment.Center;status.VerticalAlignment=VerticalAlignment.Center;
            ui.Children.Add(status);
            AddHandler(Mouse.PreviewMouseMoveEvent,new MouseEventHandler((s,e)=>RefreshFullscreenControls()),true);
            AddHandler(Mouse.LostMouseCaptureEvent,new MouseEventHandler((s,e)=>RefreshFullscreenControls()),true);
            MouseLeave+=delegate {RefreshFullscreenControls();};
            Activated+=delegate {RefreshFullscreenControls();TakeKeys();};
            AddHandler(Mouse.PreviewMouseDownEvent,new MouseButtonEventHandler(AreaOutsidePress),true);
            Deactivated+=delegate {AreaDeactivated();RefreshFullscreenControls();};
            SizeChanged+=delegate {RefreshFullscreenControls();RepositionAreaPopup();};
            LocationChanged+=delegate {RepositionAreaPopup();};
            StateChanged+=delegate {if(WindowState==WindowState.Minimized)CloseAreaPopup();else TakeKeys();};
            PreviewKeyDown+=(s,e)=>{
                var key=e.Key==Key.ImeProcessed?e.ImeProcessedKey:e.Key==Key.System?e.SystemKey:e.Key;
                // Esc and F1 always keep their jobs. Every other key does what Guide & Options → Advanced says
                // (the defaults unless the person changed them: ViewerKeys).
                if(key==Key.Escape && e.IsRepeat){e.Handled=true;return;}
                if(HandleAreaKey(key,Keyboard.Modifiers,coverArea.Typing)){e.Handled=true;return;}
                if(coverArea.Typing)return;
                if(key==Key.F1){GuideWindow.Open(this,"large");e.Handled=true;return;}
                string job=ViewerKeys.ActionFor(key);
                // A key the person chose (Enter, Tab, …) is used up even when its job can't run right now, so it
                // doesn't press a focused button or move the focus instead. The first keys behave as they always did.
                if(job!=null && Keyboard.Modifiers==ModifierKeys.None && !ViewerKeys.IsFirstKey(job,key))e.Handled=true;
                switch(job) {
                    case "back":case "forward":
                        // ← → skip by the chosen number of seconds; Shift + ← → skips one second.
                        if(!motion || (Keyboard.Modifiers!=ModifierKeys.None && Keyboard.Modifiers!=ModifierKeys.Shift) || draggingVideo || resumeAfterDrag)return;
                        Skip(job=="back"?-1:1,Keyboard.Modifiers==ModifierKeys.Shift?1:skipSeconds);break;
                    case "previous":case "next":
                        if(forCrop || Keyboard.Modifiers!=ModifierKeys.None)return;
                        Navigate(job=="previous"?-1:1);break;
                    case "sceneBack":case "sceneForward":
                        if(!motion || Keyboard.Modifiers!=ModifierKeys.None)return;
                        StepFrame(job=="sceneBack"?-1:1);break;
                    case "brightnessReset":case "brighter":case "darker":
                        if(!motion || Keyboard.Modifiers!=ModifierKeys.None)return;
                        ChangeBrightness(job=="brightnessReset"?0:job=="brighter"?1:-1);break;
                    case "brightnessStyle":
                        if(!motion || Keyboard.Modifiers!=ModifierKeys.None)return;
                        SwitchBrightnessStyle();break;
                    case "play":
                        if(Keyboard.Modifiers!=ModifierKeys.None)return;
                        if(!e.IsRepeat){if(motion)SetPaused(!paused);else if(!WantsCrop)Primary();}break;
                    case "faster":case "slower":case "speedReset":
                        // Like PotPlayer: C faster, X slower by 0.1, Z back to normal.
                        if(!motion || Keyboard.Modifiers!=ModifierKeys.None)return;
                        double newRate=job=="speedReset"?1:Math.Round(rate+(job=="faster"?0.1:-0.1),1);
                        SetRate(Math.Max(0.2,Math.Min(4,newRate)));
                        ShowCornerNote(L.F("Playback speed {0:0.0}×",rate));
                        break;
                    case "volumeUp":case "volumeDown":
                        // Like PotPlayer and YouTube: ↑ ↓ change the volume by 5; held down, it keeps going.
                        if(!motion || !volume.IsEnabled || Keyboard.Modifiers!=ModifierKeys.None)return;
                        muted=false;
                        volume.Value=Math.Max(0,Math.Min(100,Math.Round(volume.Value/5)*5+(job=="volumeUp"?5:-5)));
                        ApplyAudio();
                        ShowCornerNote(L.F("Volume {0}%",(int)volume.Value));break;
                    case "mute":
                        // M turns the sound off and on again, like PotPlayer; the volume itself stays as it was.
                        if(!motion || !volume.IsEnabled || Keyboard.Modifiers!=ModifierKeys.None || e.IsRepeat)return;
                        muted=!muted;ApplyAudio();
                        ShowCornerNote(muted?L.T("Sound off"):L.F("Volume {0}%",(int)volume.Value));break;
                    case "double":
                        // Straight to 2×, and from 2× back to 1×: the same key as in Make GIF cover.
                        if(!motion || Keyboard.Modifiers!=ModifierKeys.None || e.IsRepeat)return;
                        ToggleDoubleRate();break;
                    case "clearVoice":
                        if(!motion || !clearVoiceReady || Keyboard.Modifiers!=ModifierKeys.None || e.IsRepeat)return;
                        ToggleClearVoice();break;
                    case "turnLeft":case "turnRight":
                        // O turns the picture left and P right, a little at a time; held down, it keeps turning.
                        if(Keyboard.Modifiers!=ModifierKeys.None)return;
                        Turn(job=="turnRight"?1:-1,e.IsRepeat);break;
                    case "turnReset":
                        if(Keyboard.Modifiers!=ModifierKeys.None || e.IsRepeat)return;
                        ResetTurn();break;
                    case "recycle":
                        if(Keyboard.Modifiers!=ModifierKeys.None || e.IsRepeat)return;
                        RecycleCurrent();break;
                    case "info":
                        if(forCrop || Keyboard.Modifiers!=ModifierKeys.None || e.IsRepeat)return;
                        ToggleInfo();break;
                    case "partStart":case "partEnd":
                        // Like PotPlayer's A-B repeat: [ marks where the GIF cover's part starts, ] where it ends.
                        if(!motion || Keyboard.Modifiers!=ModifierKeys.None || e.IsRepeat)return;
                        MarkPart(job=="partStart"?1:2);break;
                    case "partClear": // \ (the won sign on a Korean keyboard)
                        if(!motion || Keyboard.Modifiers!=ModifierKeys.None || e.IsRepeat)return;
                        ClearPart();break;
                    case "quickGif": // G: the part becomes a GIF cover in the background (QuickGif.cs)
                        if(!motion || Keyboard.Modifiers!=ModifierKeys.None || e.IsRepeat)return;
                        QuickGif();break;
                    case "capture": // / next to the arrow keys (and the one on the number pad)
                        if(!motion || Keyboard.Modifiers!=ModifierKeys.None || e.IsRepeat)return;
                        CaptureFrame();break;
                    default:return;
                } e.Handled=true;
            };
            ticker.Tick+=delegate {Tick();};Loaded+=delegate {LoadCurrent(openCover);};
            Closing+=(s,e)=>{
                if(!ConfirmSubtitles(true)){e.Cancel=true;return;}
                if(explorer!=null){explorer.Leaving=true;explorer.Close();}
                // Like a video player: the next large view opens where this one was.
                if(!forCrop && home!=null && IsLoaded){string place=ViewerPlacement.Write(this);if(place!=null)home.SaveViewerBounds(place);}
            };
            Closed+=delegate {
                if(loading!=null)loading.Cancel();
                ClearTransport();picture.Source=null;pictureRaw=null;
                speakerTimer.Stop();
                if(home!=null)home.SaveAudio((int)Math.Round(volume.Value),clearVoice,clearVoiceLevel,speaker);
            };
            speakerTimer.Tick+=delegate {CheckSpeaker();};
            ContentRendered+=delegate {Focus();};
        }
        // ----- The other covers of this original, in the space beside a tall cover -----
        readonly CoverStrip coverStrip=new CoverStrip();
        readonly DispatcherTimer stripSleep=new DispatcherTimer {Interval=TimeSpan.FromSeconds(2.6)};
        bool stripAwake=true,holdStrip;
        int stripRequest;
        System.Drawing.Point stripPointer;
        // The covers of an original were put in another order in a folder window: this window follows.
        internal void CoverOrderChanged() {
            if(forCrop || home==null || !IsVisible || index<0 || index>=items.Count)return;
            try {
                var ranks=new Dictionary<string,double?>(StringComparer.OrdinalIgnoreCase);
                foreach(var cover in GalleryStore.CoversOf(items[index]))ranks[cover.Folder]=cover.Rank;
                foreach(var item in items){double? rank;if(item!=null && ranks.TryGetValue(item.Folder,out rank))item.Rank=rank;}
            } catch(Exception) {}
            if(cachedCover)RefreshCoverStrip(items[index]);
        }
        async void RefreshCoverStrip(Photo photo) {
            if(forCrop || home==null)return;
            int request=++stripRequest;
            if(!photo.HasCrop){coverStrip.Clear();return;}
            coverStrip.Mark(photo.Folder);
            List<Photo> covers;
            try {
                covers=await Task.Run(()=>GalleryStore.InCoverOrder(GalleryStore.CoversOf(photo).FindAll(p=>p.HasCrop && !p.ExcludedFromCollection)));
            } catch(Exception) {return;}
            if(request!=stripRequest || !IsVisible)return;
            coverStrip.Fill(covers,CurrentPhoto.Folder);
            PlaceCoverStrip();
        }
        void PlaceCoverStrip() {
            if(forCrop || home==null || holdStrip)return;
            double height=Math.Max(0,root.ActualHeight-viewport.Margin.Bottom),room=0;
            bool show=cachedCover && !motion && coverStrip.Count>1 && picture.Source!=null && root.ActualWidth>0;
            if(show) {
                room=MediaViewport.Fit(MediaViewport.Aspect(picture),new Size(root.ActualWidth,height)).X;
                show=room>=CoverStrip.MinRoom && (!fullscreen || stripAwake);
            }
            if(!show){coverStrip.Visibility=Visibility.Collapsed;return;}
            coverStrip.Place(room,height,viewport.Margin.Bottom);
            coverStrip.Visibility=Visibility.Visible;
            if(fullscreen && !stripSleep.IsEnabled)stripSleep.Start();
        }
        // In full screen the list rests with the mouse, so only the cover is left on screen.
        void WakeCoverStrip() {
            if(!fullscreen)return;
            var now=System.Windows.Forms.Cursor.Position;
            if(now==stripPointer)return;
            stripPointer=now;stripAwake=true;
            stripSleep.Stop();stripSleep.Start();
            PlaceCoverStrip();
        }
        void ChooseSibling(Photo cover) {
            int at=-1;
            for(int i=0;i<items.Count;i++)
                if(String.Equals(items[i].Folder,cover.Folder,StringComparison.OrdinalIgnoreCase)){at=i;break;}
            if(at<0) {
                // Left out of this list by a filter or a search: it joins right here, so K and L still work.
                var list=items as List<Photo>;
                if(list==null)return;
                at=index+1;list.Insert(at,cover);
            }
            index=at;
            LoadCurrent(true);
        }
        // ----- The part for a GIF cover, marked while watching: [ start, ] end, \ clear -----
        // With both ends set, that part plays again and again (what the GIF will show), a band on the playback bar
        // shows it, and Make GIF cover opens on it instead of the usual second before and after the cover.
        // It stays while this original is watched (also across View cover / Play original) and goes with the next file.
        double? partStart,partEnd;
        string partFile;
        readonly Stopwatch partJump=new Stopwatch();
        readonly Canvas partMarks=new Canvas {IsHitTestVisible=false,Height=0,VerticalAlignment=VerticalAlignment.Center};
        bool PartIsHere {get {return partFile!=null && index>=0 && index<items.Count && String.Equals(partFile,items[index].Original,StringComparison.OrdinalIgnoreCase);}}
        // Both ends, for the original they were marked in.
        internal bool PartFor(string original,out double from,out double to) {
            from=to=0;
            if(partFile==null || !partStart.HasValue || !partEnd.HasValue || !String.Equals(partFile,original,StringComparison.OrdinalIgnoreCase))return false;
            from=partStart.Value;to=partEnd.Value;return true;
        }
        // Make GIF cover asks for the part: the large view it was opened from (through Choose a scene and Make
        // cover), or, opened from a folder window or a notice, any large view showing that original. A part that
        // lies far from the cover's own moment was marked for something else and is left alone.
        internal static Viewer GivePart(MotionSource source,Window owner) {
            if(source==null || source.Path==null || Application.Current==null)return null;
            Viewer mine=null;
            for(var w=owner;w!=null && mine==null;w=w.Owner)mine=w as Viewer;
            var asked=new List<Viewer>();
            if(mine!=null)asked.Add(mine);
            else foreach(Window window in Application.Current.Windows){var viewer=window as Viewer;if(viewer!=null)asked.Add(viewer);}
            foreach(var viewer in asked) {
                double from,to;
                if(!viewer.PartFor(source.Path,out from,out to))continue;
                if(source.Moment<from-PartNearMs || source.Moment>to+PartNearMs)continue;
                source.PartStart=from;source.PartEnd=to;return viewer;
            }
            return null;
        }
        // G: the marked part as a GIF cover, made in the background just as the video shows now; watching goes on.
        void QuickGif() {
            if(!PartIsHere || !partStart.HasValue || !partEnd.HasValue) {
                ShowCornerNote(L.P("Mark the part with [ and ] first, then press G.","먼저 [ 와 ]로 구간을 정한 뒤 G를 눌러요."));return;
            }
            if(home==null || forCrop)return;
            home.MakeQuickGif(CurrentPhoto,partStart.Value,partEnd.Value,CoverArea==null?CurrentFraming:null,CoverArea,PlaybackAdjust);
            ShowCornerNote(L.P("Making the GIF cover in the background. Keep watching.","GIF 표지를 뒤에서 만들고 있어요. 계속 보셔도 돼요."));
        }
        // The GIF cover was made from the part, so the next cover starts fresh.
        internal void DropPart() {partStart=partEnd=null;partFile=null;partInside=false;partJump.Reset();ShowPart();}
        // A part is at least a twentieth of a second (less can't be made into a GIF) and at most a minute.
        const double PartNearMs=2000,PartLongestMs=60000;
        void MarkPart(int which) {
            if(!motion || duration<=1)return;
            if(!PartIsHere){partStart=partEnd=null;}
            double at=Position();
            if(mpv!=null && !mpv.SeekPending){double exact=mpv.Position;if(!Double.IsNaN(exact))at=exact;}
            at=Math.Max(0,Math.Min(duration,at));
            // The two ends stay at least one scene apart; a mark on the wrong side of the other one starts a new part.
            double gap=Math.Max(50,Scenes.StepMs);
            bool tooLong=false;
            if(which==1) {
                partStart=at;
                if(partEnd.HasValue && partEnd.Value-at>PartLongestMs){partEnd=null;tooLong=true;}
                else if(partEnd.HasValue && partEnd.Value<at+gap)partEnd=null;
            } else {
                partEnd=at;
                if(partStart.HasValue && at-partStart.Value>PartLongestMs){partStart=null;tooLong=true;}
                else if(partStart.HasValue && partStart.Value>at-gap)partStart=null;
            }
            partFile=CurrentPhoto.Original;
            partJump.Reset();
            partInside=partStart.HasValue && partEnd.HasValue; // the mark is at one of its ends, so playing on stays in it
            string longest=tooLong?L.F("A GIF cover part can be up to {0} s",(int)(PartLongestMs/1000))+" · ":"";
            if(partStart.HasValue && partEnd.HasValue)
                ShowCornerNote(L.F("GIF cover part {0} – {1} · {2:0.0} s",Stamp(partStart.Value),Stamp(partEnd.Value),(partEnd.Value-partStart.Value)/1000));
            else if(which==1)ShowCornerNote(longest+L.F("GIF cover part starts at {0} · press ] where it ends",Stamp(at)));
            else ShowCornerNote(longest+L.F("GIF cover part ends at {0} · press [ where it starts",Stamp(at)));
            ShowPart();
        }
        // A right-click on the bar: the first marks one place, the second the other (whichever comes first is the start),
        // and the part between them repeats from its start. A third right-click begins a new part at that place.
        void PointPart(double at) {
            if(!PartIsHere || (partStart.HasValue && partEnd.HasValue) || (!partStart.HasValue && !partEnd.HasValue)) {
                partStart=at;partEnd=null;partFile=CurrentPhoto.Original;partJump.Reset();partInside=false;
                ShowCornerNote(L.P("Repeat from "+Stamp(at)+" · right-click the other end on the bar",Stamp(at)+"부터 · 재생바에서 다른 끝을 오른쪽 클릭해 주세요"));
                ShowPart();return;
            }
            double first=partStart??partEnd.Value;
            double from=Math.Min(first,at),to=Math.Max(first,at);
            double gap=Math.Max(50,Scenes.StepMs);
            if(to-from<gap){ShowCornerNote(L.P("Too close · right-click a little farther away","너무 가까워요 · 조금 더 떨어진 곳을 오른쪽 클릭해 주세요"));return;}
            if(to-from>PartLongestMs){ShowCornerNote(L.F("A GIF cover part can be up to {0} s",(int)(PartLongestMs/1000)));return;}
            partStart=from;partEnd=to;partFile=CurrentPhoto.Original;
            ShowCornerNote(L.F("GIF cover part {0} – {1} · {2:0.0} s",Stamp(from),Stamp(to),(to-from)/1000));
            ShowPart();
            // It plays from the start of the part and keeps repeating it.
            // The player may report the old place for a moment after the jump; LoopPart waits for it (as when it loops).
            partInside=true;partJump.Restart();SeekTo(from);
        }
        void ClearPart() {
            bool had=PartIsHere && (partStart.HasValue || partEnd.HasValue);
            DropPart();
            ShowCornerNote(had?L.T("GIF cover part cleared"):L.T("No GIF cover part is set · [ start, ] end"));
        }
        // While the part is being watched, playing out of it goes back to its start: past its end, or (a part that
        // reaches the end of the file) around to the beginning. Skipping or dragging to another place leaves the
        // part, so the rest of the video can still be watched without clearing it; skipping back in joins it again.
        bool partInside;
        const double PartLeadMs=250; // a seek may land on the picture just before the start
        bool InPart(double ms,double from,double to) {return ms>=from-PartLeadMs && ms<to;}
        void FollowPart(double ms) {
            double from,to;
            if(PartIsHere && PartFor(partFile,out from,out to))partInside=InPart(ms,from,to);
        }
        bool LoopPart(double ms) {
            double from,to;
            if(!PartIsHere || !PartFor(partFile,out from,out to)){partInside=false;return false;}
            bool within=InPart(ms,from,to);
            if(partJump.IsRunning) {
                // On the way back to the start: the Windows player still reports the old place for a moment.
                if(!within && partJump.ElapsedMilliseconds<1000)return true;
                partJump.Reset();
            }
            if(within){partInside=true;return false;}
            if(!partInside)return false;
            partJump.Restart();
            SeekTo(from);
            return true;
        }
        // A pale band around the bar's rail from the start to the end, closed by a short bar at each mark (one bar
        // while only one is set). The same look as the GIF cover window's strip (see PartLook).
        void ShowPart() {
            partMarks.Children.Clear();
            double width=seek.ActualWidth;
            if(!motion || !PartIsHere || duration<=1 || width<=12)return;
            const double thumb=10; // the bar's playhead: its middle runs from 5 to width-5
            Func<double,double> x=ms=>thumb/2+Math.Max(0,Math.Min(1,ms/duration))*(width-thumb);
            PartLook.OnBar(partMarks,partStart.HasValue?x(partStart.Value):(double?)null,partEnd.HasValue?x(partEnd.Value):(double?)null);
        }
        // JPG quality for captures. Used inside only: no number like this is ever shown on screen or in the guide.
        const int CaptureJpgQuality=95;
        // Saves the scene on screen as a picture. Options choose the look (the video's own frame, or the size and
        // E / W brightness on screen) and the file (PNG or JPG). The original is not touched.
        void CaptureFrame() {
            if(!motion)return;
            // The still is about to give way to the video at this angle: capture that, not the frame under it.
            if(turnStillOn){captureAfterTurn=true;FinishTurn();return;}
            try {
                var prefs=home!=null?home.Settings:new Preferences();
                string folder=prefs.CaptureFolder,extension=prefs.CaptureJpg?".jpg":".png";
                System.IO.Directory.CreateDirectory(folder);
                double ms=Position();
                if(mpv!=null){double exact=mpv.Position;if(!Double.IsNaN(exact))ms=exact;}
                var at=TimeSpan.FromMilliseconds(Math.Max(0,ms));
                string stem=System.IO.Path.GetFileNameWithoutExtension(CurrentPhoto.Name);
                if(stem.Length>120)stem=stem.Substring(0,120).TrimEnd();
                stem+=String.Format(System.Globalization.CultureInfo.InvariantCulture,"_{0:00}-{1:00}-{2:00}.{3:000}",(int)at.TotalHours,at.Minutes,at.Seconds,at.Milliseconds);
                string file=System.IO.Path.Combine(folder,stem+extension);
                for(int i=2;System.IO.File.Exists(file);i++)file=System.IO.Path.Combine(folder,stem+" ("+i+")"+extension);
                bool saved;
                if(mpv!=null && !prefs.CaptureAsShown)saved=mpv.Screenshot(file,prefs.CaptureJpg?CaptureJpgQuality:0);
                else {
                    BitmapSource frame=mpv!=null?MpvFrame():ShownFrame();
                    if(frame!=null && prefs.CaptureAsShown)frame=AsShown(frame);
                    saved=frame!=null;
                    if(saved) {
                        BitmapEncoder encoder=prefs.CaptureJpg?(BitmapEncoder)new JpegBitmapEncoder {QualityLevel=CaptureJpgQuality}:new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(frame));
                        using(var output=System.IO.File.Create(file))encoder.Save(output);
                    }
                }
                ShowCornerNote(saved?L.F("Captured · {0}",System.IO.Path.GetFileName(file)):L.T("Couldn't capture this scene"));
            } catch(Exception ex) {
                if(ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is InvalidOperationException)
                    ShowCornerNote(L.T("Couldn't capture this scene"));
                else throw;
            }
        }
        // The video's own frame from the mpv engine, through a temporary file that is removed at once.
        BitmapSource MpvFrame() {
            string temp=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"LoveMyShelf-"+Guid.NewGuid().ToString("N")+".png");
            try {
                if(!mpv.Screenshot(temp,0))return null;
                var image=new BitmapImage();
                image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.UriSource=new Uri(temp);image.EndInit();image.Freeze();
                return image;
            } finally {try{System.IO.File.Delete(temp);}catch(IOException){}catch(UnauthorizedAccessException){}}
        }
        // Without the mpv engine: the picture on screen (GIF, WebP) or the Windows video player's current frame.
        BitmapSource ShownFrame() {
            if(media!=null && media.NaturalVideoWidth>0) {
                int width=media.NaturalVideoWidth,height=media.NaturalVideoHeight;
                var visual=new DrawingVisual();
                using(var dc=visual.RenderOpen())dc.DrawRectangle(new VisualBrush(media),null,new Rect(0,0,width,height));
                var target=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);
                target.Render(visual);return target;
            }
            return pictureRaw;
        }
        // The frame the way it looks now: the size the video has on screen (in real screen dots) with the E / W
        // brightness on it. Buttons and the black bars around the video are not part of it.
        BitmapSource AsShown(BitmapSource frame) {
            // A GIF or WebP turned with O / P: mpv hands over its frame already turned, these come as they are in the file.
            if(mpv==null && media==null && turn!=0)frame=TurnedFrame(frame,turn);
            int width=frame.PixelWidth,height=frame.PixelHeight;
            // Where the whole picture would be drawn, counted from the corner of the part that shows.
            var whole=new Rect(0,0,width,height);
            if(root.ActualWidth>0 && root.ActualHeight>0 && ShownAspect>0) {
                Rect all=PictureRect(),shown=Rect.Intersect(all,ViewArea);
                var source=PresentationSource.FromVisual(this);
                double scale=source!=null && source.CompositionTarget!=null?source.CompositionTarget.TransformToDevice.M11:1;
                if(!shown.IsEmpty && shown.Width>=1 && shown.Height>=1) {
                    width=Math.Max(1,(int)Math.Round(shown.Width*scale));height=Math.Max(1,(int)Math.Round(shown.Height*scale));
                    whole=new Rect((all.X-shown.X)*scale,(all.Y-shown.Y)*scale,all.Width*scale,all.Height*scale);
                }
            }
            var visual=new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual,BitmapScalingMode.HighQuality);
            using(var dc=visual.RenderOpen()) {
                var area=new Rect(0,0,width,height);
                dc.PushClip(new RectangleGeometry(area));
                // The same brightness as on screen: the curve, or the Windows player's white or black layer.
                var look=playbackAdjust;look.Brightness=0;
                dc.DrawImage(media==null?ColorAdjust.Apply(Light.Apply(frame,PlaybackValue),look):frame,whole);
                if(media!=null && playbackBrightness!=0) {
                    var cover=new SolidColorBrush(playbackBrightness>0?Colors.White:Colors.Black) {Opacity=Math.Abs(playbackBrightness)/100.0};
                    dc.DrawRectangle(cover,null,area);
                }
                dc.Pop();
            }
            var target=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);
            target.Render(visual);target.Freeze();
            return target;
        }
        // ----- Subtitles -----
        // subtitlesReady: a video is playing through mpv, so the subtitle explorer can open.
        bool subtitlesReady;
        SubtitleExplorer explorer;
        string subtitleVideo;
        bool subtitlesVisible=true;
        int subtitleSize=100; // percent
        // direction 0 puts the size back to 100%.
        void ChangeSubtitleSize(int direction) {
            subtitleSize=direction==0?100:Math.Max(50,Math.Min(300,(int)Math.Round(subtitleSize/10.0)*10+direction*10));
            if(mpv!=null)mpv.SubtitleScale=subtitleSize/100.0;
            if(home!=null)home.SaveSubtitleSize(subtitleSize);
            ShowCornerNote(L.F("Subtitle size {0}%",subtitleSize));
        }
        internal SubtitleFile Subtitle {get;private set;}
        internal bool CanSyncSubtitle {get {return motion && mpv!=null;}}
        internal bool HasOwnSubtitle {get {return mpv!=null && mpv.HasOwnSubtitle;}}
        internal double SubtitleClock {get {
            if(mpv!=null){double now=mpv.Position;if(!Double.IsNaN(now) && !mpv.SeekPending)return now;}
            return Position();
        }}
        internal bool SubtitlesVisible {
            get {return subtitlesVisible;}
            set {subtitlesVisible=value;if(mpv!=null)mpv.SubtitlesVisible=value;}
        }
        internal void SeekSubtitle(double ms) {if(motion)SeekTo(Math.Max(0,ms));}
        void OpenSubtitleExplorer() {
            if(explorer!=null){if(explorer.WindowState==WindowState.Minimized)explorer.WindowState=WindowState.Normal;explorer.Activate();return;}
            explorer=new SubtitleExplorer(this);explorer.Show();
        }
        internal void ExplorerClosed(SubtitleExplorer closed) {
            if(explorer!=closed)return;
            if(home!=null)home.SaveSubtitleBounds(closed.Bounds);
            explorer=null;
        }
        // The video is open: find its subtitle, or put the one being worked on back on screen.
        void AttachSubtitle(string path,MpvPlayer player) {
            if(!String.Equals(subtitleVideo,path,StringComparison.OrdinalIgnoreCase)) {
                ConfirmSubtitles(false);
                subtitleVideo=path;Subtitle=null;
                string found=SubtitleFile.FindFor(path);
                if(found!=null) {
                    try {Subtitle=SubtitleFile.Load(found);}
                    catch(Exception ex) {
                        if(!(ex is IOException || ex is UnauthorizedAccessException || ex is System.IO.InvalidDataException || ex is FormatException || ex is OverflowException))throw;
                        ShowCornerNote(L.T("Couldn't read the subtitle file"));
                    }
                }
                if(explorer!=null)explorer.Fill(false);
            }
            player.SubtitlesVisible=subtitlesVisible;
            RefreshSubtitle();
            PlaceSubtitles();
            if(explorer!=null)explorer.PlaybackChanged();
        }
        // mpv shows a temporary copy with the current times, so nothing on disk changes until Save.
        internal void RefreshSubtitle() {
            if(mpv==null || Subtitle==null)return;
            string temp=null;
            try {
                string extension,text=Subtitle.PlayerText(out extension);
                string folder=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"LoveMyShelf-subtitles");
                System.IO.Directory.CreateDirectory(folder);
                temp=System.IO.Path.Combine(folder,Guid.NewGuid().ToString("N")+extension);
                System.IO.File.WriteAllText(temp,text,new System.Text.UTF8Encoding(true));
                if(!mpv.ShowSubtitle(temp))ShowCornerNote(L.T("Couldn't show this subtitle"));
            } catch(IOException) {ShowCornerNote(L.T("Couldn't show this subtitle"));}
            catch(UnauthorizedAccessException) {ShowCornerNote(L.T("Couldn't show this subtitle"));}
            finally {if(temp!=null)try {System.IO.File.Delete(temp);} catch(IOException) {} catch(UnauthorizedAccessException) {}}
            PlaceSubtitles(); // ASS and plain subtitles are placed differently
        }
        string SubtitleBackups {get {
            string data=home!=null?System.IO.Path.GetDirectoryName(home.Store.Root):AppInfo.DataRoot;
            return System.IO.Path.Combine(data,"Subtitle backups");
        }}
        Window SubtitleDialogOwner {get {return explorer!=null && explorer.IsVisible?(Window)explorer:this;}}
        internal bool SaveSubtitle() {
            var file=Subtitle;
            if(file==null || !file.Dirty)return true;
            var owner=SubtitleDialogOwner;
            int early=file.LinesBeforeZero;
            if(early>0 && MessageBox.Show(owner,L.F("{0:N0} lines get pushed to before the video starts, so they will be saved at the very start of the video. Save anyway?",early),
                "LoveMyShelf",MessageBoxButton.OKCancel,MessageBoxImage.Warning,MessageBoxResult.Cancel)!=MessageBoxResult.OK)return false;
            try {
                try {Subtitle=file.Save(SubtitleBackups);}
                catch(SubtitleChangedException) {
                    if(MessageBox.Show(owner,L.T("Another program changed this file after the subtitle was opened. Overwrite it with what you see now?"),
                        "LoveMyShelf",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)!=MessageBoxResult.Yes)return false;
                    Subtitle=file.Save(SubtitleBackups,true);
                }
            } catch(Exception ex) {
                if(!(ex is IOException || ex is UnauthorizedAccessException))throw;
                MessageBox.Show(owner,L.T("Couldn't save the subtitle file. It may be read-only or in use by another program.")+"\n"+ex.Message,"LoveMyShelf",MessageBoxButton.OK,MessageBoxImage.Warning);
                return false;
            }
            RefreshSubtitle();
            ShowCornerNote(L.T("Subtitle saved"));
            return true;
        }
        // Before leaving a subtitle with changes. Returns false when the person wants to stay.
        internal bool ConfirmSubtitles(bool canStay) {
            var file=Subtitle;
            if(file==null || !file.Dirty)return true;
            var answer=MessageBox.Show(SubtitleDialogOwner,L.F("Save the changes to “{0}”?",System.IO.Path.GetFileName(file.Path)),"LoveMyShelf",
                canStay?MessageBoxButton.YesNoCancel:MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.Yes);
            if(answer==MessageBoxResult.Cancel)return false;
            if(answer==MessageBoxResult.Yes) {
                if(SaveSubtitle())return true;
                if(canStay)return false;
            }
            file.Revert();
            RefreshSubtitle();
            if(explorer!=null)explorer.Fill(true);
            return true;
        }
        internal bool ChooseSubtitleFile(Window owner) {
            if(!ConfirmSubtitles(true))return false;
            var dialog=new Microsoft.Win32.OpenFileDialog {Title=L.T("Open subtitle file…"),
                Filter=L.T("Subtitle files")+"|*.smi;*.sami;*.srt;*.ass;*.ssa;*.vtt"};
            string video=CurrentPhoto.Original;
            try {dialog.InitialDirectory=System.IO.Path.GetDirectoryName(Subtitle!=null?Subtitle.Path:video);} catch(ArgumentException) {}
            if(dialog.ShowDialog(owner)!=true)return false;
            try {
                Subtitle=SubtitleFile.Load(dialog.FileName);
                subtitleVideo=motion?video:subtitleVideo;
                RefreshSubtitle();
                return true;
            } catch(Exception ex) {
                if(!(ex is IOException || ex is UnauthorizedAccessException || ex is System.IO.InvalidDataException || ex is FormatException || ex is OverflowException))throw;
                MessageBox.Show(owner,L.T("Couldn't read the subtitle file")+"\n"+ex.Message,"LoveMyShelf",MessageBoxButton.OK,MessageBoxImage.Warning);
                return false;
            }
        }
        // ----- Turning (I / O / P) and zooming (wheel) the picture. Both start over with every file. -----
        const double MaxZoom=8;
        int turn;               // degrees clockwise, 0 to 359
        int drawnTurn;          // the turn on screen: mpv gets a held key's turns a little apart
        double mediaTurn;       // the Windows player's own correction for a video stored sideways
        double zoom=1;          // against the upright picture's fit, so turning never changes the size
        Vector pan,panFrom;     // how far the middle of the picture sits from the middle of the view
        bool panning;           // the left button is moving the picture (not drawing the cover box)
        Point dragFrom;
        DateTime turnRests;     // a held key rests a moment at every quarter turn, so it is easy to stop there
        // While O / P are in use a video rests as a still picture, because mpv stutters rebuilding itself for every
        // angle. The picture is turned instead; a moment after the last press the video takes over at that angle.
        const double TurnCircle=0.96;      // the circle the picture turns in, against the short side of the view
        bool turning;                      // keys are in use; the next press after this ends shrinks to the circle again
        bool turnStillOn;                  // the still covers the video
        bool turnWaiting;                  // the angle is settled and the still stays until mpv shows it
        bool turnResume;                   // it was playing, so it plays on from the same scene
        bool captureAfterTurn;
        int stillTurn;                     // the turn already in the still (mpv hands its frames over turned)
        readonly DispatcherTimer turnSettle=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(500)};
        readonly DispatcherTimer turnReady=new DispatcherTimer();
        readonly Canvas turnLayer=new Canvas {Background=Brushes.Black,ClipToBounds=true,IsHitTestVisible=false,Visibility=Visibility.Collapsed};
        readonly Image turnImage=new Image {Stretch=Stretch.Fill,RenderTransformOrigin=new Point(0.5,0.5)};
        // The room the picture has: the window without the bar under it.
        Rect ViewArea {get {return new Rect(0,0,root.ActualWidth,Math.Max(0,root.ActualHeight-viewport.Margin.Bottom));}}
        double ShownAspect {get {return picture.Source!=null?MediaViewport.Aspect(picture):mpv!=null?mpv.Aspect:media==null?0:MediaViewport.Aspect(media);}}
        // Width over height before O / P.
        internal double UprightAspect {get {
            if(picture.Source!=null)return picture.Source.Height>0?picture.Source.Width/picture.Source.Height:0;
            if(mpv!=null)return mpv.UprightAspect;
            if(media==null || media.NaturalVideoWidth<=0 || media.NaturalVideoHeight<=0)return 0;
            double aspect=(double)media.NaturalVideoWidth/media.NaturalVideoHeight;
            return ((int)mediaTurn%180+180)%180==90?1/aspect:aspect;
        }}
        // The box around a picture of this upright size at the turn on screen.
        Size TurnedBox(Size upright) {return TurnedBox(upright,drawnTurn);}
        static Size TurnedBox(Size upright,int degrees) {
            double angle=degrees*Math.PI/180,cos=Math.Abs(Math.Cos(angle)),sin=Math.Abs(Math.Sin(angle));
            return new Size(upright.Width*cos+upright.Height*sin,upright.Width*sin+upright.Height*cos);
        }
        // Small enough that the picture's corners never reach the edge of the view, at any angle.
        double CircleZoom() {
            var area=ViewArea;
            var upright=MediaViewport.Fit(UprightAspect,area.Size);
            double across=Math.Sqrt(upright.Width*upright.Width+upright.Height*upright.Height);
            return across<=0?1:Math.Min(1,TurnCircle*Math.Min(area.Width,area.Height)/across);
        }
        // Where the whole picture is, turned, zoomed and moved, before the edge of the view cuts it off.
        Rect PictureRect() {
            var area=ViewArea;
            var box=TurnedBox(MediaViewport.Fit(UprightAspect,area.Size).Size);
            double w=box.Width*zoom,h=box.Height*zoom;
            return new Rect(area.Width/2+pan.X-w/2,area.Height/2+pan.Y-h/2,w,h);
        }
        bool PictureOverflows() {
            var whole=PictureRect();var area=ViewArea;
            return whole.Width>area.Width+0.5 || whole.Height>area.Height+0.5;
        }
        // The wheel goes down to where the whole turned picture fits in the view.
        double LeastZoom() {
            var area=ViewArea;
            var box=TurnedBox(MediaViewport.Fit(UprightAspect,area.Size).Size);
            if(box.Width<=0 || box.Height<=0)return 1;
            return Math.Min(1,Math.Min(area.Width/box.Width,area.Height/box.Height));
        }
        void Turn(int direction,bool held) {
            LeaveAreaView();
            if(turning){turnSettle.Stop();turnSettle.Start();} // a held key resting at a quarter turn is still turning
            if(held && DateTime.UtcNow<turnRests)return;
            if(!turning)StartTurning();
            turnSettle.Stop();turnSettle.Start();
            int step=home==null?10:Math.Max(1,home.Settings.RotationStep);
            int to=turn+direction*step;
            // A quarter turn is never stepped over: the picture lands on it first.
            int quarter=direction>0?(int)Math.Floor(turn/90.0)*90+90:(int)Math.Ceiling(turn/90.0)*90-90;
            if(direction>0?to>=quarter:to<=quarter){to=quarter;turnRests=DateTime.UtcNow.AddMilliseconds(450);}
            turn=((to%360)+360)%360;
            ApplyTurn();
            ShowCornerNote(turn==0?L.T("Rotation: normal"):L.F("Rotation {0}°",turn>180?turn-360:turn));
        }
        // I: the angle, the size and the place all go back. A turned video hides its rebuild behind the still here too.
        void ResetTurn() {
            LeaveAreaView();
            if(drawnTurn!=0)HoldStill();
            turnSettle.Stop();turning=false;turnWaiting=false;turnReady.Stop();
            turn=0;zoom=1;pan=new Vector();
            if(turnStillOn){drawnTurn=0;ApplyZoom();SettleTurn();}
            else ShowTurn();
            ShowCornerNote(L.T("Rotation: normal"));
        }
        // The first press: the picture goes to the middle, small enough to turn without touching the edges.
        void StartTurning() {
            turning=true;
            HoldStill();
            turnWaiting=false;turnReady.Stop();
            zoom=CircleZoom();pan=new Vector();
        }
        // A video stops on this scene and shows it as a still. If the frame can't be had, it simply turns as a video.
        void HoldStill() {
            if(turnStillOn || mpv==null || !motion)return;
            bool playing=!paused;
            if(playing)SetPaused(true);
            var frame=mpv.GrabFrame();
            if(frame==null || !ShapeMatches((double)frame.PixelWidth/frame.PixelHeight,drawnTurn)) {
                if(playing)SetPaused(false);
                return;
            }
            stillTurn=drawnTurn;turnResume=playing;turnStillOn=true;
            turnRaw=frame;turnImage.Source=Light.Apply(frame,PlaybackValue);turnLayer.Visibility=Visibility.Visible;
        }
        bool ShapeMatches(double aspect,int degrees) {
            var box=TurnedBox(new Size(UprightAspect,1),degrees);
            return box.Width>0 && box.Height>0 && Math.Abs(aspect/(box.Width/box.Height)-1)<0.03;
        }
        void ApplyTurn() {
            if(turnStillOn){drawnTurn=turn;ApplyZoom();return;}
            ShowTurn();
        }
        void ShowTurn() {
            drawnTurn=turn;
            picture.LayoutTransform=turn==0?Transform.Identity:new RotateTransform(turn);
            if(media!=null)media.LayoutTransform=mediaTurn+turn==0?Transform.Identity:new RotateTransform(mediaTurn+turn);
            if(mpv!=null)mpv.Turn=turn;
            ApplyZoom();
        }
        // The angle is decided: mpv gets it once, behind the still, and the still leaves when mpv shows it.
        void SettleTurn() {
            turnSettle.Stop();turning=false;
            if(!turnStillOn || mpv==null)return;
            bool already=mpv.Turn==turn;
            turnWaiting=true;drawnTurn=turn;mpv.Turn=turn;ApplyZoom();
            turnReady.Stop();turnReady.Interval=TimeSpan.FromMilliseconds(already?60:1000);turnReady.Start();
        }
        // mpv reports the new shape a moment before it has drawn it.
        void CheckTurnReady() {
            if(!turnWaiting || !ShapeMatches(mpv.Aspect,mpv.Turn))return;
            turnReady.Stop();turnReady.Interval=TimeSpan.FromMilliseconds(160);turnReady.Start();
        }
        void EndTurnStill() {
            turnReady.Stop();
            if(!turnStillOn)return;
            turnWaiting=false;turnStillOn=false;
            turnLayer.Visibility=Visibility.Collapsed;turnImage.Source=null;turnRaw=null;
            bool resume=turnResume;turnResume=false;
            if(captureAfterTurn){captureAfterTurn=false;CaptureFrame();}
            if(resume)SetPaused(false);
        }
        void CancelTurn() {
            turnSettle.Stop();turnReady.Stop();
            turning=turnStillOn=turnWaiting=turnResume=captureAfterTurn=false;
            turnLayer.Visibility=Visibility.Collapsed;turnImage.Source=null;turnRaw=null;
        }
        void PlaceTurnStill() {
            if(!turnStillOn)return;
            var area=ViewArea;
            var box=TurnedBox(MediaViewport.Fit(UprightAspect,area.Size).Size,stillTurn);
            double w=box.Width*zoom,h=box.Height*zoom;
            turnLayer.Margin=new Thickness(0,0,0,viewport.Margin.Bottom);
            turnImage.Width=w;turnImage.Height=h;
            Canvas.SetLeft(turnImage,area.Width/2-w/2);Canvas.SetTop(turnImage,area.Height/2-h/2);
            var place=new TransformGroup();
            place.Children.Add(new RotateTransform(turn-stillTurn));place.Children.Add(new TranslateTransform(pan.X,pan.Y));
            turnImage.RenderTransform=place;
        }
        // Before anything needs the angle to be final.
        void FinishTurn() {if(turning)SettleTurn();}
        // about: the spot (in the view) that stays where it is, usually the pointer; the middle when left out.
        void SetZoom(double value,Point? about=null) {
            LeaveAreaView();
            var area=ViewArea;
            // A picture made smaller at one angle may stay that small at another.
            double least=Math.Min(zoom,LeastZoom());
            value=Math.Max(least,Math.Min(MaxZoom,value));
            if(value!=least && Math.Abs(value-1)<0.03)value=1;
            var middle=new Point(area.Width/2,area.Height/2);
            Vector fromPicture=(about??middle)-middle-pan;
            pan=(about??middle)-middle-fromPicture*(value/zoom);
            zoom=value;
            ApplyZoom();
            ShowCornerNote(zoom==1?L.T("Zoom: normal"):L.F("Zoom {0:0.0}×",zoom));
        }
        // Puts the zoom, the place and the turn on whatever shows the picture, and everything that follows its shape.
        void ApplyZoom() {
            var area=ViewArea;
            var upright=MediaViewport.Fit(UprightAspect,area.Size).Size;
            if(enlargedArea!=null && upright.Width>0 && upright.Height>0) {
                var frame=enlargedArea.Framing(upright);
                zoom=Math.Min(MaxZoom,Math.Min(area.Width/frame.ShownWidth,area.Height/frame.ShownHeight)); // the whole box shows
                pan=new Vector(frame.PanX*zoom,frame.PanY*zoom);
            }
            var box=TurnedBox(upright);
            double w=box.Width*zoom,h=box.Height*zoom;
            // The picture can be moved only as far as it sticks out of the view.
            double roomX=Math.Max(0,(w-area.Width)/2),roomY=Math.Max(0,(h-area.Height)/2);
            if(enlargedArea==null)
                pan=new Vector(Math.Max(-roomX,Math.Min(roomX,pan.X)),Math.Max(-roomY,Math.Min(roomY,pan.Y)));
            // Whatever shows the picture fits the turned box into the view by itself; this is the size on top of that.
            var fit=MediaViewport.Fit(box.Height>0?box.Width/box.Height:0,area.Size);
            double scale=fit.Width>0?w/fit.Width:1;
            Transform view=Transform.Identity;
            if(Math.Abs(scale-1)>0.0001 || pan.X!=0 || pan.Y!=0) {
                var group=new TransformGroup();
                group.Children.Add(new ScaleTransform(scale,scale));group.Children.Add(new TranslateTransform(pan.X,pan.Y));
                view=group;
            }
            picture.RenderTransformOrigin=new Point(0.5,0.5);picture.RenderTransform=view;
            if(media!=null){media.RenderTransformOrigin=new Point(0.5,0.5);media.RenderTransform=view;}
            // While the still is being turned, mpv is left alone.
            if(mpv!=null && (!turnStillOn || turnWaiting))mpv.SetView(scale,w>0?pan.X/w:0,h>0?pan.Y/h:0);
            PlaceTurnStill();
            viewport.InvalidateMeasure();
            if(seekPreview!=null)seekPreview.Framing=CurrentFraming;
            if(coverArea!=null)coverArea.Refresh();
            ApplyPlaybackBrightness();PlaceSubtitles();PlaceCoverStrip();
        }
        void Wheel(object sender,MouseWheelEventArgs e) {
            if(e.Handled || ShownAspect<=0 || panel.IsMouseOver || coverStrip.IsMouseOver)return;
            var cursor=System.Windows.Forms.Cursor.Position;
            Point at;
            try {at=root.PointFromScreen(new Point(cursor.X,cursor.Y));} catch(InvalidOperationException) {return;}
            if(!ViewArea.Contains(at))return;
            SetZoom(zoom*Math.Pow(1.2,e.Delta/120.0),at);
            e.Handled=true;
        }
        // A picture turned by any angle, in the smallest box that holds it.
        static BitmapSource TurnedFrame(BitmapSource frame,int degrees) {
            double w=frame.PixelWidth,h=frame.PixelHeight,angle=degrees*Math.PI/180;
            int width=Math.Max(1,(int)Math.Round(Math.Abs(w*Math.Cos(angle))+Math.Abs(h*Math.Sin(angle)))),
                height=Math.Max(1,(int)Math.Round(Math.Abs(w*Math.Sin(angle))+Math.Abs(h*Math.Cos(angle))));
            var visual=new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual,BitmapScalingMode.HighQuality);
            using(var dc=visual.RenderOpen()) {
                dc.PushTransform(new RotateTransform(degrees,width/2.0,height/2.0));
                dc.DrawImage(frame,new Rect((width-w)/2,(height-h)/2,w,h));
                dc.Pop();
            }
            var target=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);
            target.Render(visual);target.Freeze();
            return target;
        }

        // ----- File information (Tab), like a video player's: what anyone would want to know about the file -----
        // Stays on for the next file and the next window, like the playback speed.
        static bool infoOn;
        readonly TextBlock infoText=new TextBlock {FontSize=13,Foreground=Brushes.White,Margin=new Thickness(16,12,16,0),
            TextWrapping=TextWrapping.Wrap,MaxWidth=720,LineHeight=20,Visibility=Visibility.Collapsed,
            Effect=new System.Windows.Media.Effects.DropShadowEffect {Color=Colors.Black,BlurRadius=5,ShadowDepth=1,Direction=315,Opacity=0.95}};
        readonly DispatcherTimer infoTimer=new DispatcherTimer {Interval=TimeSpan.FromSeconds(1)};
        static readonly Brush InfoLabel=Frozen(new SolidColorBrush(Color.FromRgb(0xA9,0xD6,0xFF)));
        static Brush Frozen(Brush brush){brush.Freeze();return brush;}
        // What does not change while the file shows, read once per file.
        string infoPath,infoPlace,infoSize,speakerShown,speakerName;
        void ToggleInfo() {infoOn=!infoOn;RefreshInfo();}
        void RefreshInfo() {
            if(!infoOn || forCrop || index<0 || index>=items.Count){infoText.Visibility=Visibility.Collapsed;infoTimer.Stop();return;}
            var photo=items[index];
            string path=photo.Original??"";
            if(path!=infoPath) {
                infoPath=path;infoPlace=PlaceText(path);infoSize="";
                try {var file=new FileInfo(path);if(file.Exists)infoSize=SizeText(file.Length);} catch(Exception) {}
            }
            var lines=new List<Tuple<string,string>>();
            lines.Add(Tuple.Create(L.T("File"),photo.Name??System.IO.Path.GetFileName(path)));
            if(infoPlace.Length>0)lines.Add(Tuple.Create(L.T("Location"),infoPlace));
            var format=new List<string>();
            string extension=System.IO.Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
            if(extension.Length>0)format.Add(extension);
            if(infoSize.Length>0)format.Add(infoSize);
            if(motion && duration>1)format.Add(LengthText(duration));
            if(format.Count>0)lines.Add(Tuple.Create(L.T("Format"),String.Join(" · ",format)));
            if(mpv!=null && motion) {
                var video=new List<string>();
                if(mpv.PixelWidth>0)video.Add(mpv.PixelWidth+"×"+mpv.PixelHeight);
                double fps=Number(mpv.Read("container-fps"));
                if(fps>0)video.Add(fps.ToString("0.##",System.Globalization.CultureInfo.InvariantCulture)+"fps");
                string codec=CodecName(mpv.Read("video-format"));
                if(codec.Length>0)video.Add(codec);
                // Left out when the player has no number for it.
                double rate=Number(mpv.Read("video-bitrate"));
                if(rate>0)video.Add(L.F("{0:N0}kbps",Math.Round(rate/1000)));
                if(video.Count>0)lines.Add(Tuple.Create(L.T("Video"),String.Join(" · ",video)));
                if(mpvAudio) {
                    var sound=new List<string>();
                    string audio=CodecName(mpv.Read("audio-codec-name"));
                    if(audio.Length>0)sound.Add(audio);
                    double hertz=Number(mpv.Read("audio-params/samplerate"));
                    if(hertz>0)sound.Add((hertz/1000).ToString("0.#",System.Globalization.CultureInfo.InvariantCulture)+"kHz");
                    double channels=Number(mpv.Read("audio-params/channel-count"));
                    if(channels>0)sound.Add(L.F("{0} channels",(int)channels));
                    double audioRate=Number(mpv.Read("audio-bitrate"));
                    if(audioRate>0)sound.Add(L.F("{0:N0}kbps",Math.Round(audioRate/1000)));
                    if(sound.Count>0)lines.Add(Tuple.Create(L.T("Sound"),String.Join(" · ",sound)));
                }
                var play=new List<string>();
                string hardware=mpv.Read("hwdec-current");
                if(hardware!=null)play.Add(L.T(hardware=="no" || hardware.Length==0?"Hardware acceleration off":"Hardware acceleration on"));
                double dropped=Number(mpv.Read("frame-drop-count"));
                if(dropped>=0)play.Add(L.F("Dropped frames {0:N0}",(int)dropped));
                if(mpvAudio) {
                    if(speakerShown!=speaker) {
                        speakerShown=speaker;
                        speakerName=speaker.Length==0?L.T("Windows default speaker"):speaker;
                        if(speaker.Length>0) {
                            var found=MpvPlayer.Speakers().Find(s=>s.Item1==speaker);
                            if(found!=null)speakerName=found.Item2;
                        }
                    }
                    play.Add(speakerName);
                }
                if(play.Count>0)lines.Add(Tuple.Create(L.T("Play"),String.Join(" · ",play)));
            } else {
                // A photo, a GIF or WebP, a cover, or the Windows player: the picture's own size, when it is known.
                var size=new List<string>();
                if(media!=null && media.NaturalVideoWidth>0)size.Add(media.NaturalVideoWidth+"×"+media.NaturalVideoHeight);
                else if(!motion && !cachedCover && full!=null)size.Add(full.PixelWidth+"×"+full.PixelHeight);
                if(motion && times!=null && times.Count>1)size.Add(L.F("{0:N0} frames",times.Count));
                if(size.Count>0)lines.Add(Tuple.Create(L.T("Size"),String.Join(" · ",size)));
                var cover=picture.Source as BitmapSource;
                if(cachedCover && cover!=null && !motion)lines.Add(Tuple.Create(L.T("Cover"),cover.PixelWidth+"×"+cover.PixelHeight));
            }
            infoText.Inlines.Clear();
            for(int i=0;i<lines.Count;i++) {
                if(i>0)infoText.Inlines.Add(new LineBreak());
                infoText.Inlines.Add(new Run(lines[i].Item1+"   ") {Foreground=InfoLabel});
                infoText.Inlines.Add(new Run(lines[i].Item2));
            }
            infoText.Visibility=Visibility.Visible;
            if(!infoTimer.IsEnabled)infoTimer.Start();
        }
        static double Number(string text) {
            double value;
            return Double.TryParse(text,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out value)?value:-1;
        }
        // "E: (drive name) › folder › folder inside": which drive and which folder the file is in.
        static string PlaceText(string path) {
            try {
                string folder=System.IO.Path.GetDirectoryName(path);
                if(String.IsNullOrEmpty(folder))return "";
                string drive=System.IO.Path.GetPathRoot(folder)??"";
                var parts=new List<string>();
                string head=drive.TrimEnd('\\');
                if(drive.Length>=2 && drive[1]==':') {
                    try {string label=new DriveInfo(drive).VolumeLabel;if(!String.IsNullOrWhiteSpace(label))head+=" ("+label+")";}
                    catch(Exception) {}
                }
                if(head.Length>0)parts.Add(head);
                foreach(string part in folder.Substring(drive.Length).Split(new[]{'\\','/'},StringSplitOptions.RemoveEmptyEntries))parts.Add(part);
                return String.Join(" › ",parts);
            } catch(Exception) {return "";}
        }
        static string SizeText(long bytes) {
            var inv=System.Globalization.CultureInfo.InvariantCulture;
            if(bytes>=1L<<30)return (bytes/(double)(1L<<30)).ToString("0.##",inv)+"GB";
            if(bytes>=1L<<20)return (bytes/(double)(1L<<20)).ToString(bytes>=100L<<20?"0":"0.#",inv)+"MB";
            return Math.Max(1,(long)Math.Round(bytes/1024.0)).ToString(inv)+"KB";
        }
        static string LengthText(double ms) {
            var t=TimeSpan.FromMilliseconds(ms);
            return t.TotalHours>=1?String.Format("{0}:{1:00}:{2:00}",(int)t.TotalHours,t.Minutes,t.Seconds):String.Format("{0}:{1:00}",t.Minutes,t.Seconds);
        }
        // The short names people know. Anything else is shown as mpv names it, in capitals.
        static string CodecName(string name) {
            if(String.IsNullOrEmpty(name))return "";
            switch(name.ToLowerInvariant()) {
                case "h264":return "H.264";case "hevc":return "H.265 (HEVC)";case "av1":return "AV1";case "vp9":return "VP9";case "vp8":return "VP8";
                case "mpeg4":return "MPEG-4";case "mpeg2video":return "MPEG-2";case "wmv3":return "WMV";case "vc1":return "VC-1";
                case "aac":return "AAC";case "mp3":return "MP3";case "ac3":return "AC3";case "eac3":return "E-AC3";case "opus":return "Opus";
                case "vorbis":return "Vorbis";case "flac":return "FLAC";case "dts":return "DTS";
            }
            return name.StartsWith("pcm_",StringComparison.OrdinalIgnoreCase)?"PCM":name.ToUpperInvariant();
        }

        // ----- The original to the Recycle Bin (Delete), then on to the next file -----
        // Not from the "covers" collection, where the folder window doesn't delete originals either.
        readonly bool fromCollection;
        bool recycling;
        bool CanRecycle {get {return !forCrop && home!=null && !fromCollection && index>=0 && index<items.Count && items[index].Linked && items is List<Photo>;}}
        async void RecycleCurrent() {
            if(recycling || !CanRecycle)return;
            var photo=CurrentPhoto;
            if(!ConfirmSubtitles(true))return;
            recycling=true;
            try {
                // The player lets go of the file first. What reads the file in the background (the scene list,
                // the playback bar's small pictures) stops a moment later, so it is tried a few times.
                if(loading!=null)loading.Cancel();
                ClearTransport();StopMotion();SetPicture(null);status.Text="";
                string problem=null;
                for(int attempt=0;attempt<4;attempt++) {
                    if(attempt>0)await Task.Delay(350);
                    if(!IsVisible)return;
                    problem=home.RecycleFromViewer(photo,new WindowInteropHelper(this).Handle);
                    if(problem==null || !System.IO.File.Exists(photo.Original))break;
                }
                if(!IsVisible)return;
                if(problem!=null) {
                    LoadCurrent();
                    ShowCornerNote(L.T("Couldn't move it to the Recycle Bin")+" · "+problem);
                    return;
                }
                // Every cover of that original leaves this window's list too.
                var list=(List<Photo>)items;
                int before=0;
                for(int i=0;i<index;i++)if(GalleryWindow.SameOriginal(list[i],photo))before++;
                list.RemoveAll(p=>GalleryWindow.SameOriginal(p,photo));
                if(list.Count==0){Close();return;}
                index=Math.Max(0,Math.Min(index-before,list.Count-1));
                LoadCurrent();
                ShowCornerNote(L.F("Moved to the Recycle Bin · {0}",photo.Name));
            } finally {recycling=false;}
        }

        // ----- Right-click: every control with its key, to learn the keys or to use the mouse alone -----
        System.Windows.Controls.Primitives.Popup keyPanel;
        readonly DispatcherTimer holdTimer=new DispatcherTimer();
        Action holdAction;
        bool holdRepeating; // a held button repeats like a held key (it rests at a quarter turn too)
        void ShowControls() {
            CloseAreaPopup();
            if(keyPanel!=null)keyPanel.IsOpen=false;
            var rows=new Grid();
            rows.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});rows.ColumnDefinitions.Add(new ColumnDefinition());
            var popup=new System.Windows.Controls.Primitives.Popup {AllowsTransparency=true,StaysOpen=false,
                Placement=System.Windows.Controls.Primitives.PlacementMode.MousePoint,PlacementTarget=ui};
            var live=new List<Action>(); // labels that follow what they show (on or off, the chosen strength)
            Action refresh=()=>{foreach(var update in live)update();};
            // A button that keeps going while it is held (stays): one press is one step, like one press of the key.
            Func<string,Action,bool,Button> key=(label,act,stays)=>{
                var button=new Button {Content=label,MinHeight=25,MinWidth=0,Padding=new Thickness(9,1,9,2),Margin=new Thickness(0,0,4,0),FontSize=12.5,Focusable=false};
                if(stays) {
                    button.ClickMode=ClickMode.Press;
                    button.Click+=delegate {
                        act();refresh();
                        holdAction=()=>{if(button.IsPressed && popup.IsOpen){act();refresh();}else holdTimer.Stop();};
                        holdTimer.Stop();holdTimer.Interval=TimeSpan.FromMilliseconds(420);holdTimer.Start();
                    };
                } else button.Click+=delegate {popup.IsOpen=false;act();};
                return button;
            };
            Action<string,Button[]> row=(label,buttons)=>{
                int at=rows.RowDefinitions.Count;
                rows.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});
                var name=UI.Text(label,12.5,true);name.Margin=new Thickness(0,0,12,0);
                Grid.SetRow(name,at);rows.Children.Add(name);
                var line=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(0,2,0,2)};
                foreach(var button in buttons)line.Children.Add(button);
                Grid.SetRow(line,at);Grid.SetColumn(line,1);rows.Children.Add(line);
            };
            Action rule=()=>{
                int at=rows.RowDefinitions.Count;
                rows.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});
                var bar=new Border {Height=1,Margin=new Thickness(0,6,0,6)};bar.SetResourceReference(Border.BackgroundProperty,"Line");
                Grid.SetRow(bar,at);Grid.SetColumnSpan(bar,2);rows.Children.Add(bar);
            };
            string reset=L.T("Reset");
            if(motion) {
                var pauseKey=key("",()=>SetPaused(!paused),true);
                live.Add(()=>pauseKey.Content="Space  "+L.T(paused?"Play":"Pause"));
                row(L.T("Play"),new[]{pauseKey,key("/  "+L.T("Capture scene"),CaptureFrame,true)});
                row(L.T("Move by scene"),new[]{key("D  ◀",()=>StepFrame(-1),true),key("F  ▶",()=>StepFrame(1),true)});
                var partFrom=key("[  "+L.T("Start"),()=>{if(!holdRepeating)MarkPart(1);},true);
                var partTo=key("]  "+L.T("End"),()=>{if(!holdRepeating)MarkPart(2);},true);
                var partOff=key("\\  "+L.T("Clear"),()=>{if(!holdRepeating)ClearPart();},true);
                partFrom.ToolTip=L.T("The GIF cover starts at the scene you see now");partTo.ToolTip=L.T("The GIF cover ends at the scene you see now");
                live.Add(()=>partOff.IsEnabled=PartIsHere && (partStart.HasValue || partEnd.HasValue));
                var quick=key("G  "+L.P("Make now","바로 만들기"),()=>{if(!holdRepeating)QuickGif();},true);
                quick.ToolTip=L.P("Makes a GIF cover from the part in the background, as the video looks now. Keep watching meanwhile.",
                    "구간을 지금 보이는 그대로 GIF 표지로 뒤에서 만들어요. 그동안 계속 보면 돼요.");
                live.Add(()=>quick.IsEnabled=PartIsHere && partStart.HasValue && partEnd.HasValue);
                row(L.T("GIF cover part"),new[]{partFrom,partTo,partOff,quick});
                var drawBox=key("",()=>{if(coverArea.Editing)coverArea.StopEditing();else coverArea.StartEditing();},false);
                live.Add(()=>{
                    drawBox.IsEnabled=enlargedArea==null;
                    drawBox.Content=coverArea.Editing?L.P("Done drawing","다 그렸어요"):
                        CoverArea==null?L.P("▭ Start drawing","▭ 그리기 시작"):L.P("▭ Edit box","▭ 네모 고치기");
                });
                var zoomBox=key(enlargedArea==null?L.P("⛶ View cut area large","⛶ 자를 곳 크게 보기"):
                    L.P("⛶ Back to previous view","⛶ 원래 보기"),ToggleAreaView,false);
                live.Add(()=>{
                    zoomBox.IsEnabled=CoverArea!=null;
                    zoomBox.Content=enlargedArea==null?L.P("⛶ View cut area large","⛶ 자를 곳 크게 보기"):L.P("⛶ Back to previous view","⛶ 원래 보기");
                });
                var clearBox=key(L.P("Clear","지우기"),DeleteDrawnArea,false);
                live.Add(()=>clearBox.IsEnabled=CoverArea!=null);
                row(L.P("Where to cut","자를 곳"),new[]{drawBox,zoomBox,clearBox});
                row(L.T("Skip"),new[]{key("←  "+L.F("{0} s",skipSeconds),()=>Skip(-1,skipSeconds),true),key(L.F("{0} s",skipSeconds)+"  →",()=>Skip(1,skipSeconds),true)});
                Action<double> speed=change=>{
                    SetRate(change==0?1:Math.Max(0.2,Math.Min(4,Math.Round(rate+change,1))));
                    ShowCornerNote(L.F("Playback speed {0:0.0}×",rate));
                };
                var twice=key("",()=>{if(!holdRepeating)ToggleDoubleRate();},true);
                live.Add(()=>twice.Content=(Math.Abs(rate-2)<0.001?"✓ ":"")+"V  "+L.T("2×"));
                twice.ToolTip=L.T("2× speed. Press again for 1×");
                row(L.T("Playback speed"),new[]{key("Z  "+reset,()=>speed(0),true),key("X  "+L.T("Slower"),()=>speed(-0.1),true),key("C  "+L.T("Faster"),()=>speed(0.1),true),twice});
                // Shows the style in use; a click switches to the other one, and the controls stay open.
                var style=key("",()=>{if(!holdRepeating)SwitchBrightnessStyle();},true);
                live.Add(()=>style.Content="R  "+Light.StyleName(playbackStyle));
                style.ToolTip=L.T("Switch brightness style");
                row(L.T("Brightness"),new[]{key("Q  "+reset,()=>ChangeBrightness(0),true),key("W  "+L.T("Darker"),()=>ChangeBrightness(-1),true),
                    key("E  "+L.T("Brighter"),()=>ChangeBrightness(1),true),style});
                if(mpv!=null) {
                    // [Adjust ▾]: contrast, saturation, color temperature, sharpness (and brightness) as lines: click one,
                    // hold ← / →, roll the wheel over it, or type the number. Only while watching; the file stays as it is.
                    var adjustRows=new AdjustRows((field,v)=>SetPlaybackAdjust(field,v));
                    var adjustReset=UI.Btn(reset,()=>{
                        playbackBrightness=0;playbackStyle=Light.Soft;playbackAdjust=AdjustValues.None;brightnessChosen=adjustChosen=true;
                        ApplyPlaybackBrightness();seekPreview.Brightness=PlaybackValue;refresh();
                    });
                    adjustReset.Focusable=false;adjustReset.HorizontalAlignment=HorizontalAlignment.Left;adjustReset.Margin=new Thickness(0,2,0,4);
                    var adjustBox=new StackPanel {Width=330,Margin=new Thickness(0,2,0,4)};
                    adjustBox.Children.Add(adjustReset);adjustBox.Children.Add(adjustRows);
                    adjustBox.Visibility=adjustShown?Visibility.Visible:Visibility.Collapsed;
                    var adjustToggle=key("",()=>{
                        if(holdRepeating)return;
                        adjustShown=!adjustShown;adjustBox.Visibility=adjustShown?Visibility.Visible:Visibility.Collapsed;
                    },true);
                    live.Add(()=>{
                        adjustToggle.Content=L.P("Contrast · saturation · color","대비·채도·색")+(adjustShown?"  ▴":"  ▾");
                        var now=playbackAdjust;now.Brightness=PlaybackValue;adjustRows.Show(now);
                    });
                    adjustToggle.ToolTip=L.P("Contrast, saturation, color temperature and sharpness while watching. The file stays as it is; a cover made from here starts with them.",
                        "보는 동안 대비·채도·색 온도·선명하게를 바꿔요. 파일은 그대로이고, 여기서 만드는 표지는 이 값으로 시작해요.");
                    row(L.P("Adjust","보정"),new[]{adjustToggle});
                    int at=rows.RowDefinitions.Count;
                    rows.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});
                    Grid.SetRow(adjustBox,at);Grid.SetColumn(adjustBox,1);rows.Children.Add(adjustBox);
                }
            }
            row(L.T("Rotation"),new[]{key("I  "+reset,ResetTurn,true),key("O  ↺",()=>Turn(-1,holdRepeating),true),key("P  ↻",()=>Turn(1,holdRepeating),true)});
            row(L.T("Zoom (wheel)"),new[]{key(reset,()=>SetZoom(1),true),key("−  "+L.T("Smaller"),()=>SetZoom(zoom/1.2),true),key("+  "+L.T("Bigger"),()=>SetZoom(zoom*1.2),true)});
            if(motion && volume.IsEnabled) {
                Action<int> louder=direction=>{
                    muted=false;
                    volume.Value=Math.Max(0,Math.Min(100,Math.Round(volume.Value/5)*5+direction*5));
                    ApplyAudio();ShowCornerNote(L.F("Volume {0}%",(int)volume.Value));
                };
                var mute=key("",()=>{muted=!muted;ApplyAudio();ShowCornerNote(muted?L.T("Sound off"):L.F("Volume {0}%",(int)volume.Value));},true);
                live.Add(()=>mute.Content="M  "+L.T(muted?"Turn on":"Turn off"));
                row(L.T("Sound"),new[]{mute,key("↓  "+L.T("Quieter"),()=>louder(-1),true),key("↑  "+L.T("Louder"),()=>louder(1),true)});
            }
            if(motion && clearVoiceReady) {
                var voice=key("",ToggleClearVoice,true);
                live.Add(()=>voice.Content="N  "+L.T(clearVoice?"Turn off":"Turn on"));
                row(L.T("Clear voice"),new[]{voice});
                // The strength as a number (2026-11-27 user: "make Clear voice take a number too"): 25 gentle, 50 normal, 100 strong.
                var strength=new AdjustRow(L.P("Strength","세기"),1,100,v=>{SetClearVoiceStrength(v);refresh();}) {Width=330,Margin=new Thickness(0,0,0,4)};
                strength.ToolTip=L.P("1 to 100. About 25 is gentle, 50 normal, 100 strong. Strong raises quiet voices the most, but noise in quiet scenes too. Click the line once and hold ← or →, or roll the wheel over it.",
                    "1~100이에요. 25쯤이 약하게, 50이 보통, 100이 강하게예요. 세게 할수록 작은 목소리가 커지지만 조용한 장면의 잡음도 커져요. 줄을 한 번 누르고 ←/→를 꾹 누르거나 휠을 굴려요.");
                live.Add(()=>strength.Show(clearVoiceLevel));
                int voiceAt=rows.RowDefinitions.Count;
                rows.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});
                Grid.SetRow(strength,voiceAt);Grid.SetColumn(strength,1);rows.Children.Add(strength);
            }
            if(motion && subtitlesReady && !forCrop)row(L.T("Subtitles"),new[]{key(L.T("Open the subtitle explorer"),OpenSubtitleExplorer,false)});
            if(motion && mpv!=null && (Subtitle!=null || HasOwnSubtitle))
                row(L.T("Subtitle size"),new[]{key(reset,()=>ChangeSubtitleSize(0),true),key("−  "+L.T("Smaller"),()=>ChangeSubtitleSize(-1),true),key("+  "+L.T("Bigger"),()=>ChangeSubtitleSize(1),true)});
            if(!forCrop) {
                var back=key("K  ◀ "+L.T("Previous"),()=>Navigate(-1),false);back.IsEnabled=index>0;
                var forward=key("L  "+L.T("Next")+" ▶",()=>Navigate(1),false);forward.IsEnabled=index<items.Count-1;
                row(L.T("File"),new[]{back,forward,key("Tab  "+L.T("File info"),ToggleInfo,false)});
                var more=new List<Button>();
                if(home!=null && index<items.Count && items[index].Linked)more.Add(key(L.T("Open file location"),()=>home.ShowInExplorer(CurrentPhoto,false),false));
                // Watching only the video: the folder window can go down from here, and it gives its memory back while it is down.
                if(home!=null && home.IsVisible && home.WindowState!=WindowState.Minimized)more.Add(key(L.T("Minimize the folder window"),GalleryWindow.MinimizeAll,false));
                if(more.Count>0)row("",more.ToArray());
                if(CanRecycle) {
                    rule();
                    row("",new[]{key("Delete  "+L.T("Move the original file to the Recycle Bin"),RecycleCurrent,false)});
                }
            }
            refresh();
            var card=new Border {Child=rows,CornerRadius=new CornerRadius(8),Padding=new Thickness(12,9,10,9),BorderThickness=new Thickness(1),Margin=new Thickness(0,0,8,8),
                Effect=new System.Windows.Media.Effects.DropShadowEffect {Color=Colors.Black,BlurRadius=8,ShadowDepth=2,Direction=270,Opacity=0.35}};
            card.SetResourceReference(Border.BackgroundProperty,"Paper");card.SetResourceReference(Border.BorderBrushProperty,"Line");
            card.SetResourceReference(TextElement.FontFamilyProperty,"AppFont");
            popup.Child=card;
            card.PreviewKeyDown+=(s,e)=>{
                // Esc while typing a number puts the number back instead of closing.
                if(e.Key==Key.Escape && Keyboard.FocusedElement is TextBox && AdjustRow.OwnsKey(e))return;
                if(e.Key==Key.Escape){if(!e.IsRepeat){popup.IsOpen=false;ReturnAreaKeys();}e.Handled=true;}
            };
            popup.Closed+=delegate {holdTimer.Stop();if(keyPanel==popup)keyPanel=null;};
            keyPanel=popup;popup.IsOpen=true;
        }

        void ShowCornerNote(string text) {
            cornerNote.Text=text;cornerNote.BeginAnimation(OpacityProperty,null);cornerNote.Opacity=1;
            cornerNote.Visibility=Visibility.Visible;cornerNoteTimer.Stop();cornerNoteTimer.Start();
        }
        static void LabelControl(Control control,string label) {
            AutomationProperties.SetName(control,label);control.ToolTip=label;
        }
        // clearVoiceReady: this video plays with sound through mpv, so Clear voice can be used.
        bool clearVoice,clearVoiceReady;
        int clearVoiceLevel=50; // strength 1..100
        void ToggleDoubleRate() {
            SetRate(Math.Abs(rate-2)<0.001?1:2);
            ShowCornerNote(L.F("Playback speed {0:0.0}×",rate));
        }
        void ToggleClearVoice() {
            if(mpv==null || !mpv.SetClearVoice(!clearVoice,clearVoiceLevel)) {
                ShowCornerNote(L.T("Clear voice isn't available for this video"));
                return;
            }
            clearVoice=!clearVoice;
            ShowCornerNote(clearVoice?L.F("Clear voice: {0}",clearVoiceLevel):L.T("Clear voice off"));
        }
        // Choosing a strength also turns the feature on.
        void SetClearVoiceStrength(int strength) {
            strength=Math.Max(1,Math.Min(100,strength));
            if(!clearVoice || strength!=clearVoiceLevel) { // the same strength again would only interrupt the sound
                if(mpv==null || !mpv.SetClearVoice(true,strength)) {
                    ShowCornerNote(L.T("Clear voice isn't available for this video"));
                    return;
                }
                clearVoice=true;clearVoiceLevel=strength;
            }
            ShowCornerNote(L.F("Clear voice: {0}",strength));
        }
        bool muted;
        void ApplyAudio() {
            double level=muted?0:volume.Value;
            // A muted volume bar looks faded, so it is clear why there is no sound.
            volume.Opacity=muted?0.45:1;
            if(mpv!=null)mpv.Volume=level;
            if(media==null)return;
            media.Volume=level/100;
        }
        // ----- The sound follows the monitor this window is on (Guide & Options picks a speaker per monitor) -----
        readonly DispatcherTimer speakerTimer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(400)};
        string speaker="",speakerWaiting; // "" is the Windows default
        string WantedSpeaker(MpvPlayer player) {
            if(home==null)return "";
            return SpeakerRouting.For(home.Settings,new System.Windows.Interop.WindowInteropHelper(this).Handle,player.HasAudioDevice);
        }
        // Also notices a speaker being unplugged or plugged back in, and a changed choice in the options.
        void CheckSpeaker() {
            if(mpv==null){speakerTimer.Stop();speakerWaiting=null;return;}
            if(WindowState==WindowState.Minimized)return; // a minimized window sits off every monitor
            string wanted=WantedSpeaker(mpv);
            if(wanted==speaker){speakerWaiting=null;return;}
            // Only after two checks in a row, so dragging a window across a monitor doesn't switch on the way.
            if(wanted!=speakerWaiting){speakerWaiting=wanted;return;}
            speakerWaiting=null;UseSpeaker(wanted);
        }
        void UseSpeaker(string next) {
            if(next==speaker)return;
            if(home!=null)home.SaveAudio((int)Math.Round(volume.Value),clearVoice,clearVoiceLevel,speaker);
            speaker=next;
            if(mpv!=null)mpv.AudioDevice=next;
            if(home==null)return;
            bool wasMuted=muted;
            volume.Value=home.Settings.VolumeFor(next);
            muted=wasMuted;ApplyAudio();
        }
        readonly TaskCompletionSource<bool> firstPicture=new TaskCompletionSource<bool>();
        // Completes when the first picture is on screen.
        internal Task FirstPicture {get {return firstPicture.Task;}}
        // Set when the view was opened without ShowDialog: Edit then closes it and calls this instead of ending a dialog.
        internal Action<Photo> EditLater;
        void SetPicture(BitmapSource frame) {
            if(frame!=null)Dispatcher.BeginInvoke(new Action(()=>firstPicture.TrySetResult(true)),DispatcherPriority.ContextIdle);
            pictureRaw=frame;picture.Source=frame;
            viewport.InvalidateMeasure();
            ApplyPlaybackBrightness();
        }
        // The mpv engine bends its own colors (its "gamma"), a GIF or WebP gets the curve on each frame, and the
        // still shown while turning gets it too. Only the Windows player, which can't, still gets the white or black layer.
        // E / W / Q (and their buttons): +1 brighter, -1 darker, 0 back to normal.
        void ChangeBrightness(int direction) {
            int step=home==null?3:home.Settings.BrightnessStep;
            playbackBrightness=direction==0?0:Math.Max(-100,Math.Min(100,playbackBrightness+direction*step));brightnessChosen=true;
            ApplyPlaybackBrightness();seekPreview.Brightness=PlaybackValue;
            ShowCornerNote(playbackBrightness==0?L.T("Brightness: normal"):L.F("Brightness {0:+0;-0} · {1}",playbackBrightness,Light.StyleName(playbackStyle)));
        }
        // R: Soft, See in the dark and Milky take turns. The level stays.
        // One line of [Adjust] changed: field 0 brightness level, 1 contrast, 2 saturation, 3 color temperature, 4 sharpness.
        void SetPlaybackAdjust(int field,int value) {
            if(field==0){playbackBrightness=Math.Max(-100,Math.Min(100,value));brightnessChosen=true;}
            else {
                if(field==1)playbackAdjust.Contrast=value;
                else if(field==2)playbackAdjust.Saturation=value;
                else if(field==3)playbackAdjust.Temperature=value;
                else playbackAdjust.Sharpness=value;
                adjustChosen=true;
            }
            playbackAdjust=playbackAdjust.Clamped();
            ApplyPlaybackBrightness();seekPreview.Brightness=PlaybackValue;
        }
        void SwitchBrightnessStyle() {
            playbackStyle=Light.NextStyle(playbackStyle);brightnessChosen=true;
            ApplyPlaybackBrightness();seekPreview.Brightness=PlaybackValue;
            ShowCornerNote(L.F("Brightness style: {0}",Light.StyleName(playbackStyle)));
        }
        void ApplyPlaybackBrightness() {
            int value=motion?PlaybackValue:0,level=Light.Level(value);
            if(mpv!=null){mpv.Brightness=value;mpv.Look=motion?playbackAdjust:AdjustValues.None;}
            if(pictureRaw!=null)picture.Source=Light.Apply(pictureRaw,value);
            if(turnRaw!=null)turnImage.Source=Light.Apply(turnRaw,value);
            if(media==null || level==0){brightnessLayer.Visibility=Visibility.Collapsed;return;}
            if(ShownAspect<=0 || root.ActualWidth<=0 || root.ActualHeight<=0){
                brightnessLayer.Visibility=Visibility.Collapsed;return;
            }
            // Exactly over the part of the picture that shows (it can be zoomed in and moved).
            var shown=Rect.Intersect(PictureRect(),ViewArea);
            if(shown.IsEmpty){brightnessLayer.Visibility=Visibility.Collapsed;return;}
            brightnessLayer.HorizontalAlignment=HorizontalAlignment.Left;brightnessLayer.VerticalAlignment=VerticalAlignment.Top;
            brightnessLayer.Margin=new Thickness(shown.X,shown.Y,0,0);
            brightnessLayer.Width=shown.Width;brightnessLayer.Height=shown.Height;
            brightnessLayer.Background=level>0?Brushes.White:Brushes.Black;
            brightnessLayer.Opacity=Math.Abs(level)/100.0;brightnessLayer.Visibility=Visibility.Visible;
        }
        void SetRate(double value) {
            if(motion && !paused && !Native) {
                startMs=Position();clock.Restart();
            }
            rate=lastRate=value;
            if(media!=null)media.SpeedRatio=rate;
            if(mpv!=null)mpv.Speed=rate;
        }
        void Skip(int direction,double seconds) {
            if(motion)SeekTo(Math.Min(Math.Max(0,duration-1),Position()+direction*seconds*1000));
        }
        void StepFrame(int direction) {
            if(draggingVideo || resumeAfterDrag)return;
            FinishTurn();
            SetPaused(true);
            var timing=times;
            if(Native && videoIndex!=null && videoIndex.Status==TaskStatus.RanToCompletion)timing=videoIndex.Result;
            if(timing==null && mpv!=null){mpv.Step(direction);return;}
            if(timing==null) {
                // No list of the video's own pictures (yet): still one scene step.
                SeekTo(Math.Min(Math.Max(0,duration-1),Math.Max(0,startMs+direction*(double)Scenes.StepMs)));
                return;
            }
            // mpv can report a picture's time a fraction of a millisecond before the list does.
            int current=timing.At(startMs+1);
            int target=current+direction;
            while(target>=0 && target<timing.Count && timing.Start(target)==timing.Start(current))target+=direction;
            // One step is one scene (0.2 s unless changed in the options), never finer than the video's own pictures.
            int sceneTarget=timing.At(timing.Start(current)+direction*(double)Scenes.StepMs);
            if(direction>0?sceneTarget>target:sceneTarget<target)target=sceneTarget;
            if(target<0 || target>=timing.Count)return;
            double position=timing.Start(target);
            // mpv's exact seek already shows the frame starting at the target time.
            if(media==null)SeekTo(position);
            else {
                double end=target+1<timing.Count?timing.Start(target+1):duration;
                // Seek just inside the frame so rounding at its leading edge does not select its neighbor.
                SeekTo(position,position+Math.Max(0,Math.Min(1,(end-position)/2)));
            }
        }
        void PositionAreaPopup() {
            coverArea.Menu.Measure(new Size(Double.PositiveInfinity,Double.PositiveInfinity));
            var anchor=AreaAnchor(coverArea.Menu.DesiredSize);
            // Set twice: the same numbers again would not move an open popup after the window moved.
            areaPopup.HorizontalOffset=anchor.X+0.5;areaPopup.HorizontalOffset=anchor.X;areaPopup.VerticalOffset=anchor.Y;
        }
        void RepositionAreaPopup() {
            if(!areaPopup.IsOpen)return;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(()=>{if(areaPopup.IsOpen)PositionAreaPopup();}));
        }
        void SetFullscreen(bool full) {
            CloseAreaPopup();
            fullscreen=full;
            surfaceClick.Stop();
            hidden=false;
            stripAwake=true;stripSleep.Stop();
            // Windowed (like PotPlayer): the bar sits below the picture on the window color.
            // Full screen: it floats over the picture with nothing behind it.
            panel.SetResourceReference(Panel.BackgroundProperty,full?"ViewerBar":"Paper");
            Grid.SetRow(root,full?0:1);
            Grid.SetRowSpan(root,full?2:1);
            PlaceTitleBar();
            if(full)RefreshFullscreenControls();
            else {
                titleBar.Visibility=Visibility.Visible;
                SetPanelVisible(!hidden);
            }
            FitPictureAbovePanel();RepositionAreaPopup();
        }
        // Back from the taskbar (minimized in full screen with the minimize button on the overlay), Windows could
        // hand the keys to the video's own child window or to nothing, so no shortcut worked until F11 went out and
        // back in. Once the window has settled, the keys come back here, unless something of ours (the overlay,
        // the controls popup) already has them.
        void TakeKeys() {
            Dispatcher.BeginInvoke(DispatcherPriority.Input,new Action(()=>{
                if(!IsVisible || WindowState==WindowState.Minimized || !(IsActive || (overlay!=null && overlay.IsActive)))return;
                IntPtr me=new WindowInteropHelper(this).Handle,focus=GetFocus();
                if(me==IntPtr.Zero)return;
                // The overlay is its own window, not a child: with nothing focused inside it, keys go nowhere.
                bool emptyOverlay=overlay!=null && focus!=IntPtr.Zero && focus==new WindowInteropHelper(overlay).Handle &&
                    !overlay.IsKeyboardFocusWithin;
                if(focus==IntPtr.Zero || emptyOverlay || (focus!=me && IsChild(me,focus))) {
                    if(!IsActive)Activate();
                    SetFocus(me);Keyboard.Focus(this);
                } else if(focus==me && Keyboard.FocusedElement==null) Keyboard.Focus(this);
            }));
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetFocus();
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SetFocus(IntPtr hwnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")] [return:System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] static extern bool IsChild(IntPtr parent,IntPtr child);
        void RefreshFullscreenControls() {
            if(!fullscreen)return;
            bool active=IsActive || (overlay!=null && overlay.IsActive);
            Point pointer=new Point(-1,-1);
            if(active) {
                var cursor=System.Windows.Forms.Cursor.Position;
                pointer=PointFromScreen(new Point(cursor.X,cursor.Y));
            }
            UpdateFullscreenControls(pointer);
        }
        // Native video is a separate window that hides WPF content beneath it, so while it is shown
        // the controls live in a transparent window above it.
        void UseOverlay(bool on) {
            if(on) {
                if(overlay==null) {
                    overlay=new VideoOverlay(this,root);
                    overlay.AddHandler(Mouse.PreviewMouseMoveEvent,new MouseEventHandler((s,e)=>RefreshFullscreenControls()),true);
                    overlay.AddHandler(Mouse.LostMouseCaptureEvent,new MouseEventHandler((s,e)=>RefreshFullscreenControls()),true);
                    overlay.MouseLeave+=delegate {RefreshFullscreenControls();};
                    overlay.Activated+=delegate {RefreshFullscreenControls();TakeKeys();};
                    overlay.AddHandler(Mouse.PreviewMouseDownEvent,new MouseButtonEventHandler(AreaOutsidePress),true);
                    overlay.Deactivated+=delegate {AreaDeactivated();RefreshFullscreenControls();};
                    overlay.PreviewMouseWheel+=Wheel;
                }
                if(overlay.Content!=ui){root.Children.Remove(ui);overlay.Content=ui;}
                overlay.Open();
            } else if(overlay!=null && overlay.Content==ui) {
                bool focused=overlay.IsActive;
                overlay.Content=null;overlay.Hide();root.Children.Add(ui);
                if(focused)Activate();
            }
            PlaceTitleBar();
        }
        void PlaceTitleBar() {
            var parent=fullscreen && overlay!=null && overlay.Content==ui?ui:windowContent;
            if(titleBar.Parent==parent)return;
            ((Panel)titleBar.Parent).Children.Remove(titleBar);
            parent.Children.Add(titleBar);
        }
        void UpdateFullscreenControls(Point pointer) {
            const double topDistance=40,bottomDistance=64;
            bool inside=new Rect(RenderSize).Contains(pointer);
            bool nearTitle=inside && pointer.Y<=topDistance;
            double bottomZone=coverArea.Editing
                ?Math.Max(12,panel.Visibility==Visibility.Visible?lastPanelHeight:0)
                :Math.Max(bottomDistance,panel.ActualHeight);
            bool nearPanel=inside && ActualHeight-pointer.Y<=bottomZone;
            titleBar.Visibility=nearTitle || titleBar.IsMouseCaptureWithin?Visibility.Visible:Visibility.Collapsed;
            SetPanelVisible(!coverArea.Dragging && (areaPopup.IsOpen || coverArea.Typing || nearPanel || panel.IsMouseCaptureWithin));
        }
        void SetHidden(bool hide) {
            hidden=hide;
            SetPanelVisible(!hide);
        }
        void SetPanelVisible(bool visible) {
            if(!fullscreen)visible=true; // windowed: always shown
            var visibility=visible?Visibility.Visible:Visibility.Collapsed;
            if(panel.Visibility==visibility)return;
            panel.Visibility=visibility;PlaceAreaNotice();
            if(!visible)seekPreview.Hide();
            FitPictureAbovePanel();
            Dispatcher.BeginInvoke(new Action(PlaceSubtitles),DispatcherPriority.Loaded); // once the bar has its height
        }
        // In full screen the bar floats over the picture, so while it shows the subtitles sit just above it.
        // mpv's window is exactly the picture, so everything is measured against it (steps measured on the bundled mpv).
        // An ASS style sets its own bottom margin, so ASS counts from the very bottom of the picture to be safe.
        void PlaceSubtitles() {
            if(mpv==null)return;
            const double gap=8,plainBottom=0.948,plainStep=0.00954,assStep=0.0097;
            double position=100;
            if(fullscreen && panel.Visibility==Visibility.Visible && panel.ActualHeight>0 && root.ActualHeight>0 && mpv.Aspect>0) {
                double limit=root.ActualHeight-panel.ActualHeight-gap;
                // Zoomed in, mpv's window is the whole view, so the subtitles count from its bottom.
                var frame=mpv.Fill?new Rect(0,0,root.ActualWidth,root.ActualHeight):MediaViewport.Fit(mpv.Aspect,new Size(root.ActualWidth,root.ActualHeight));
                bool ass=Subtitle!=null && Subtitle.Kind==SubtitleKind.Ass;
                double bottom=ass?frame.Bottom:frame.Top+frame.Height*plainBottom;
                if(bottom>limit)position=100-(bottom-limit)/(frame.Height*(ass?assStep:plainStep));
            }
            mpv.SubtitlePosition=(int)Math.Max(0,Math.Min(100,Math.Floor(position)));
        }
        // Windowed, the picture uses only the space above the bar so the two never overlap.
        void FitPictureAbovePanel() {
            double reserve=!fullscreen && panel.Visibility==Visibility.Visible?panel.ActualHeight:0;
            var margin=new Thickness(0,0,0,reserve);
            if(viewport.Margin!=margin){viewport.Margin=margin;ApplyZoom();}
            PlaceCoverStrip();
            PlaceSubtitles();
        }
        // A small rounded backing for text that sits over the picture, with steady-width digits.
        static Border Pill(TextBlock text,Thickness margin) {
            var pill=new Border {Child=text,CornerRadius=new CornerRadius(7),Padding=new Thickness(8,3,8,3),Margin=margin,VerticalAlignment=VerticalAlignment.Center};
            pill.SetResourceReference(Border.BackgroundProperty,"Panel");
            Typography.SetNumeralAlignment(text,FontNumeralAlignment.Tabular);
            text.TextAlignment=TextAlignment.Center;
            return pill;
        }
        // Reserves the widest text a counter can show so neighbours never shift while it changes.
        static void ReserveWidth(TextBlock text,string widest) {
            var probe=new TextBlock {Text=widest,FontSize=text.FontSize,FontFamily=text.FontFamily,FontWeight=text.FontWeight};
            Typography.SetNumeralAlignment(probe,FontNumeralAlignment.Tabular);
            probe.Measure(new Size(Double.PositiveInfinity,Double.PositiveInfinity));
            text.MinWidth=Math.Ceiling(probe.DesiredSize.Width)+1;
        }
        static string Widest(string sample) {
            var chars=sample.ToCharArray();
            for(int i=0;i<chars.Length;i++)if(Char.IsDigit(chars[i]))chars[i]='0';
            return new string(chars);
        }
        void ClearTransport() {
            CloseAreaPopup();LeaveAreaView();
            if(status.VerticalAlignment==VerticalAlignment.Bottom){status.Text="";NoticeBackToMiddle();}
            CancelTurn();
            seekPreview.Clear();
            ticker.Stop();surfaceClick.Stop();clock.Reset();
            motion=false;paused=false;selectedMovie=false;resumeAfterDrag=false;largeVideo=false;draggingVideo=false;
            if(seek.IsMouseCaptured)seek.ReleaseMouseCapture();
            duration=0;startMs=0;shown=-1;times=null;requests=null;videoIndex=null;
            transport.Visibility=playbackButtons.Visibility=pause.Visibility=Visibility.Collapsed;
            ShowPart();
            coverArea.Available=false;UpdateAreaButton();
            subtitlesReady=false;clearVoiceReady=false;
            if(explorer!=null)explorer.PlaybackChanged();
            ShowRecrop(false);
            editCover.Visibility=Visibility.Collapsed;
            ApplyPlaybackBrightness();
            if(media!=null){var old=media;media=null;old.Close();viewport.Children.Remove(old);}
            if(mpv!=null){var old=mpv;mpv=null;viewport.Children.Remove(old);old.Dispose();}
            UseOverlay(false);
        }
        void StartTransport(double length){
            duration=Math.Max(1,length);startMs=0;paused=false;motion=true;
            seekPreview.Start(CurrentPhoto.Original);
            sync=true;seek.Maximum=duration;seek.Value=0;sync=false;pause.Content=L.T("Pause");pause.IsEnabled=true;seek.IsEnabled=true;
            ticker.Interval=TimeSpan.FromMilliseconds(largeVideo?100:30);
            volume.IsEnabled=(media!=null && media.HasAudio) || (mpv!=null && mpvAudio);
            clearVoiceReady=mpv!=null && mpvAudio;
            transport.Visibility=playbackButtons.Visibility=pause.Visibility=Visibility.Visible;
            ReserveWidth(time,Widest(Stamp(duration)+" / "+Stamp(duration)));
            // The cover box: back on for the file it was drawn on, gone for any other.
            if(areaFile!=null && !String.Equals(areaFile,CurrentPhoto.Original,StringComparison.OrdinalIgnoreCase)){coverArea.Clear();areaFile=null;}
            coverArea.Available=true;UpdateAreaButton();
            clock.Restart();ticker.Start();UpdateBar(0);
            partInside=false;partJump.Reset(); // playing starts outside the part until it is reached or skipped into
            ShowPart();
            ApplyPlaybackBrightness();
            if(resumeMs.HasValue){
                double from=resumeMs.Value;resumeMs=null;
                if(from>0 && (mpv!=null || media!=null))SeekTo(from);
            }
        }
        double? resumeMs;
        internal static string Stamp(double ms){
            var t=TimeSpan.FromMilliseconds(Math.Max(0,ms));
            // Same look as the seek-bar thumbnail (SeekPreview.Clock): 00:01:02.34.
            return SeekPreview.Clock(ms);
        }
        void UpdateBar(double ms){sync=true;seek.Value=ms;sync=false;time.Text=Stamp(ms)+" / "+Stamp(duration);}
        double Position(){
            if(paused)return startMs;
            if(media!=null)return Math.Min(duration,media.Position.TotalMilliseconds);
            if(mpv!=null) {
                double now=mpv.Position;
                return mpv.SeekPending || Double.IsNaN(now)?startMs:Math.Min(duration,now);
            }
            return (startMs+clock.Elapsed.TotalMilliseconds*rate)%duration;
        }
        void Tick(){
            if(motion && paused && mpv!=null && !draggingVideo && !mpv.SeekPending) {
                // Follow the frame mpv actually shows after a seek or frame step.
                double shownMs=mpv.Position;
                if(!Double.IsNaN(shownMs) && Math.Abs(shownMs-startMs)>0.5){startMs=Math.Min(duration,shownMs);UpdateBar(startMs);}
            }
            if(explorer!=null && motion)explorer.ShowTime(Position(),!paused);
            if(!motion || paused || draggingVideo)return;
            double ms=Position();UpdateBar(ms);
            if(LoopPart(ms))return;
            if(requests!=null)requests.Ask(times.At(ms),false);
        }
        void SeekTo(double ms,double? nativeMs=null) {
            FinishTurn();
            ms=Math.Min(Math.Max(0,ms),duration);startMs=ms;clock.Restart();
            FollowPart(ms);
            if(media!=null){
                if(largeVideo && paused)media.ScrubbingEnabled=true;
                media.Position=TimeSpan.FromMilliseconds(Math.Min(duration,nativeMs??ms));
            }
            else if(mpv!=null)mpv.Seek(ms);
            else if(requests!=null){shown=-1;requests.Ask(times.At(ms),true);}
            UpdateBar(ms);
        }
        void SetPaused(bool value){
            if(turnStillOn){turnResume=!value;return;} // the video waits under the still; this is what it does afterwards
            if(!motion || paused==value)return;
            if(value){
                if(media!=null)media.Pause();
                if(mpv!=null)mpv.Paused=true;
                double ms=Position();
                if(requests!=null && shown>=0)ms=times.Start(shown);
                paused=true;pause.Content=L.T("Play");
                if(Native){startMs=ms;UpdateBar(ms);}
                else SeekTo(ms);
                clock.Stop();
            } else {
                paused=false;pause.Content=L.T("Pause");clock.Restart();
                if(media!=null){
                    if(largeVideo)media.ScrubbingEnabled=false;
                    if(startMs>=duration)media.Position=TimeSpan.Zero;
                    media.Play();
                }
                if(mpv!=null){
                    if(startMs>=duration)mpv.Seek(0);
                    mpv.Paused=false;
                }
                if(startMs>=duration)startMs=0;
            }
        }
        void StartCrop(){
            if(home!=null && !home.CoverWorkFree(this))return;
            if(!AnimationFrames.Supports(CurrentPhoto.Original)){
                if(EditLater!=null){var photo=CurrentPhoto;var later=EditLater;Close();later(photo);return;}
                DialogResult=true;return;
            }
            ChooseFrames();
        }
        async void EditCover(){
            var gallery=home;
            if(gallery==null || !editCover.IsEnabled)return;
            var photo=CurrentPhoto;
            editCover.IsEnabled=false;
            try {
                var updated=await gallery.EditSavedCover(this,photo);
                if(updated!=null && IsVisible && CurrentPhoto==photo){items[index]=updated;LoadCurrent(true);}
            } finally {editCover.IsEnabled=true;}
        }
        // Shown only for a saved cover. Starts a background upscale, or reopens the comparison once one exists.
        void UpdateUpscale() {
            var gallery=home;
            var photo=CurrentPhoto;
            int percent=0;
            var stage=gallery==null?UpscaleStage.None:gallery.Upscales.Stage(photo.Folder,out percent);
            bool showing=gallery!=null && cachedCover && System.IO.File.Exists(photo.CoverFile);
            if(!showing){upscale.Visibility=Visibility.Collapsed;return;}
            if(stage==UpscaleStage.Running || stage==UpscaleStage.Queued) {
                upscale.Content=stage==UpscaleStage.Queued?L.T("Waiting to upscale"):L.F("Upscaling {0}%",percent);
                if(upscale.MinWidth<1){var probe=new TextBlock {Text=L.F("Upscaling {0}%",100),FontSize=upscale.FontSize};
                    probe.Measure(new Size(Double.PositiveInfinity,Double.PositiveInfinity));upscale.MinWidth=Math.Ceiling(probe.DesiredSize.Width)+upscale.Padding.Left+upscale.Padding.Right+4;}
                upscale.IsEnabled=false;upscale.Visibility=Visibility.Visible;
            } else if(photo.HasUpscale) {
                upscale.Content=L.T("Compare upscale");upscale.IsEnabled=true;upscale.Visibility=Visibility.Visible;
            } else if(UpscaleEngine.Suitable(coverHeight)) {
                upscale.Content=L.T("Upscale");upscale.IsEnabled=true;upscale.Visibility=Visibility.Visible;
            } else upscale.Visibility=Visibility.Collapsed;
            upscale.ToolTip=photo.HasUpscale || stage!=UpscaleStage.None?null:
                L.T("AI enlarges it 4× and makes it sharper");
        }
        async void UpscaleCover() {
            var gallery=home;
            if(gallery==null || !upscale.IsEnabled)return;
            var photo=CurrentPhoto;
            if(photo.HasUpscale) {
                await gallery.ShowUpscaleChoice(this,photo);
                return;
            }
            gallery.Upscales.Enqueue(photo);
            status.VerticalAlignment=VerticalAlignment.Top;
            status.Show(L.T("Upscaling has started"),L.T("AI is making this cover 4 times bigger and sharper. It runs by itself in the background, so you can look at other photos meanwhile. We’ll let you know here when it’s done."),NoticeKind.Info,7);
            UpdateUpscale();
        }
        void ChooseFrames() {
            var gallery=home;
            if(gallery==null)return;
            seekPreview.Hide();
            FinishTurn();
            bool resume=motion && (turnStillOn?turnResume:!paused);
            if(resume)SetPaused(true);
            CropNearby=mpv!=null;
            CropTime=Native?Position():(double?)null;
            CropFrame=times==null?0:times.At(startMs);
            bool saved=gallery.EditFrames(this);
            if(saved && forCrop){Close();return;}
            if(!IsVisible)return;
            if(saved)Activate();
            if(resume)SetPaused(false);
        }
        // A cover was made while watching. The video keeps playing from the same spot, and this window now knows
        // the new cover, so the next one is added beside it instead of replacing it.
        internal void CoverSaved(Photo saved) {
            if(String.Equals(saved.Folder,CurrentPhoto.Folder,StringComparison.OrdinalIgnoreCase))items[index]=saved;
            else {
                var list=items as List<Photo>;
                if(list!=null) {
                    string family=GalleryStore.CoverFamily(saved);
                    var covers=list.FindAll(p=>String.Equals(GalleryStore.CoverFamily(p),family,StringComparison.OrdinalIgnoreCase));
                    var order=GalleryStore.InCoverOrder(new List<Photo>(covers){saved});
                    int at=order.IndexOf(saved);
                    int place=at+1<order.Count?list.IndexOf(order[at+1]):(covers.Count>0?list.IndexOf(covers[covers.Count-1])+1:index+1);
                    if(place<0)place=index+1;
                    list.Insert(place,saved);
                    if(place<=index)index++;
                }
            }
            prev.IsEnabled=index>0;next.IsEnabled=index<items.Count-1;caption.Text=(index+1)+" / "+items.Count;
            ReserveWidth(caption,Widest(items.Count+" / "+items.Count));
            toggle.Content=L.T("View cover");
            ShowRecrop(true);
        }
        void Primary(){
            if(!toggle.IsEnabled)return;
            if(WantsCrop){StartCrop();return;}
            if(motion){LoadCurrent(true);return;}
            if(selectedMovie || cachedCover){LoadCurrent(false);return;}
            if(original){LoadCurrent(true);return;}
            if(full!=null){original=!original;DisplayStill();}
        }
        async void LoadCurrent(bool? showCover=null) {
            if(loading!=null)loading.Cancel();ClearTransport();
            if(!String.Equals(viewedFile,CurrentPhoto.Original,StringComparison.OrdinalIgnoreCase)) {
                viewedFile=CurrentPhoto.Original;deletedArea=null;coverArea.Clear();
            }
            turn=0;zoom=1;pan=new Vector();mediaTurn=0;ShowTurn(); // every file starts upright and at its own size
            // ...and at its own brightness. Going back and forth between the cover and the video keeps it.
            if(items[index].Original!=brightnessFile) {
                brightnessFile=items[index].Original;playbackBrightness=0;playbackStyle=Light.Soft;brightnessChosen=false;seekPreview.Brightness=0;
                playbackAdjust=AdjustValues.None;adjustChosen=false;
            }
            if(partFile!=null && !PartIsHere)DropPart(); // a part belongs to the file it was marked in
            var current=new CancellationTokenSource();loading=current;var token=current.Token;
            Photo photo=items[index];
            RefreshInfo();
            if(!forCrop)Title=photo.Name; // only the file name, like the folder windows
            var gallery=home;
            bool selected=!WantsCrop && (showCover??(gallery==null || gallery.Settings.ShowCover(photo.Original)));
            // Moving between covers of one original keeps the list still; anything else takes it away until it's known.
            holdStrip=selected && coverStrip.Holds(photo);
            if(!holdStrip)coverStrip.Visibility=Visibility.Collapsed;
            full=null;cropped=null;original=!selected;cachedCover=false;SetPicture(null);
            status.Text=L.T("Loading…");status.VerticalAlignment=VerticalAlignment.Center;
            prev.IsEnabled=index>0;next.IsEnabled=index<items.Count-1;caption.Text=(index+1)+" / "+items.Count;
            ReserveWidth(caption,Widest(items.Count+" / "+items.Count));
            toggle.IsEnabled=false;toggle.Content=WantsCrop?CropLabel:L.T("View original");
            locate.Visibility=Visibility.Collapsed;upscale.Visibility=Visibility.Collapsed;coverHeight=0;
            StopMotion();motionToggle.Visibility=Visibility.Collapsed;makeGif.Visibility=Visibility.Collapsed;
            try {
                if(selected) {
                    await LoadSavedCover(photo,token);
                    return;
                }
                if(photo.Linked && photo.HasCrop){
                    if(!System.IO.File.Exists(photo.Original)){
                        status.Text=L.T("Looking for the moved original…");status.VerticalAlignment=VerticalAlignment.Top;
                        try{SetPicture(Pictures.SavedPreview(photo));}catch(Exception){}
                    }
                    var store=home.Store;
                    string path=await Task.Run(()=>OriginalLocator.Resolve(store,photo,token),token);
                    token.ThrowIfCancellationRequested();photo.Original=path;
                    SetPicture(null);
                }
                if(VideoFrames.Supports(photo.Original) && !selected){
                    // Whichever way the original is opened (double-click in the folder, Play original, the next file),
                    // it starts one second before the scene this cover was made from, so "watch, make a cover, watch on"
                    // can repeat. The scene is stored with the cover itself, so older covers work too.
                    resumeMs=null;
                    if(photo.HasCrop) {
                        double? scene=photo.FrameTime;
                        if(!scene.HasValue && photo.FrameIndex>0) {
                            try{scene=await Task.Run(()=>MotionMaker.MomentOf(photo,photo.Original),token);}catch(OperationCanceledException){throw;}catch(Exception){}
                        }
                        token.ThrowIfCancellationRequested();
                        if(scene.HasValue && scene.Value>1000)resumeMs=scene.Value-1000;
                    }
                    await PlayVideo(photo.Original,token);return;
                }
                if(AnimationFrames.Supports(photo.Original)){
                    await Task.Run(()=>ShowFrames(photo,selected,token),token);return;
                }
                string stillPath=photo.Original;
                var loaded=(await Prepared(photo,false,()=>Tuple.Create(Pictures.Load(stillPath),0),token)).Item1;
                if(!token.IsCancellationRequested)ShowStill(loaded,false);
            }catch(Exception ex){
                if(token.IsCancellationRequested)return;
                ClearTransport();toggle.IsEnabled=false;status.Warn(L.T("Could not open the original.\n").Trim(),ex.Message);
                if(photo.HasCrop){
                    bool driveAway=DriveLinks.DriveAway(photo.Original);
                    try{
                        var cover=CoverImages.TryLoad(photo)??Pictures.SavedPreview(photo);
                        SetPicture(String.IsNullOrEmpty(photo.Drawings)?CoverImages.TrimBlackBands(cover):cover);status.VerticalAlignment=VerticalAlignment.Top;
                        if(driveAway)status.Warn(L.T("The drive with this file isn't connected"),L.T("Connect the drive again and it comes back by itself. It's fine even if the drive letter changes. The cover and the original are still there."));
                        else status.Warn(L.T("The original was moved or renamed"),L.T("Right now you are seeing the small preview saved when the cover was made. Click [Find the original again] below and choose the moved file to see it in its original quality."));
                    }catch(Exception){}
                    if(photo.Linked && !driveAway)locate.Visibility=Visibility.Visible;
                }
            }finally{
                if(!token.IsCancellationRequested){holdStrip=false;PlaceCoverStrip();}
                if(!token.IsCancellationRequested){var later=Dispatcher.BeginInvoke(new Action(()=>{if(loading==null && IsVisible)PrepareNeighbours();}),DispatcherPriority.ApplicationIdle);}
                if(loading==current)loading=null;current.Dispose();
            }
        }
        // Pictures for the current file and its neighbours, prepared in the background so K/L and the arrows
        // switch without waiting. Videos and GIFs are not prepared (they start playing instead).
        readonly Dictionary<string,Task<Tuple<BitmapSource,int>>> prepared=new Dictionary<string,Task<Tuple<BitmapSource,int>>>();
        const long PrepareLimit=60L*1024*1024;
        // What LoadCurrent would read for this file when reached with K/L: a key, and the work. Null when nothing is worth preparing.
        bool OpensAsCover(Photo photo) {
            var gallery=home;
            return !forCrop && photo.HasCrop && (gallery==null || gallery.Settings.ShowCover(photo.Original));
        }
        string PrepareKey(Photo photo,bool cover,out Func<Tuple<BitmapSource,int>> work) {
            work=null;
            try {
                if(cover) {
                    work=()=>ReadCover(photo);
                    return "c|"+photo.Folder+"|"+photo.ActiveCoverFile+"|"+photo.CoverFileTicks;
                }
                string path=photo.Original;
                if(path==null || AnimationFrames.Supports(path))return null;
                var info=new FileInfo(path);
                if(!info.Exists || info.Length>PrepareLimit)return null;
                work=()=>Tuple.Create(Pictures.Load(path),0);
                return "o|"+path+"|"+info.LastWriteTimeUtc.Ticks+"|"+info.Length;
            } catch(Exception) {return null;}
        }
        Task<Tuple<BitmapSource,int>> Prepared(Photo photo,bool cover,Func<Tuple<BitmapSource,int>> fallback,CancellationToken token) {
            Func<Tuple<BitmapSource,int>> work;
            string key=PrepareKey(photo,cover,out work);
            Task<Tuple<BitmapSource,int>> ready;
            if(key!=null && prepared.TryGetValue(key,out ready) && !ready.IsFaulted && !ready.IsCanceled)return ready;
            var task=Task.Run(fallback,token);
            if(key!=null)prepared[key]=task;
            return task;
        }
        // Keeps the current file and the ones on either side; anything else is let go.
        void PrepareNeighbours() {
            if(items.Count==0 || index<0 || index>=items.Count)return;
            var keep=new Dictionary<string,Func<Tuple<BitmapSource,int>>>();
            foreach(int i in new[]{index,index+1,index-1}) {
                if(i<0 || i>=items.Count)continue;
                Func<Tuple<BitmapSource,int>> work;
                string key=PrepareKey(items[i],OpensAsCover(items[i]),out work);
                if(key!=null && !keep.ContainsKey(key))keep[key]=work;
            }
            foreach(var old in new List<string>(prepared.Keys))if(!keep.ContainsKey(old))prepared.Remove(old);
            foreach(var pair in keep) {
                Task<Tuple<BitmapSource,int>> ready;
                if(prepared.TryGetValue(pair.Key,out ready) && !ready.IsFaulted && !ready.IsCanceled)continue;
                var work=pair.Value;
                prepared[pair.Key]=Task.Run(work);
            }
        }
        static Tuple<BitmapSource,int> ReadCover(Photo photo) {
            int sourceHeight=0;
            var cover=CoverImages.TryLoad(photo);
            if(cover==null) {
                try{cover=Pictures.Load(photo.Thumb);}catch(IOException){}catch(NotSupportedException){}catch(FormatException){}
            }
            // The upscale size limit applies to the saved cover file, not the version currently shown.
            try {
                if(System.IO.File.Exists(photo.CoverFile))
                    using(var file=System.IO.File.Open(photo.CoverFile,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete))
                    {
                        // The long side: the height of a tall cover, the width of a wide one.
                        var frame=BitmapFrame.Create(file,BitmapCreateOptions.DelayCreation,BitmapCacheOption.None);
                        sourceHeight=Math.Max(frame.PixelHeight,frame.PixelWidth);
                    }
            } catch(IOException){} catch(NotSupportedException){} catch(FormatException){}
            return Tuple.Create(cover!=null && String.IsNullOrEmpty(photo.Drawings)?CoverImages.TrimBlackBands(cover):cover,sourceHeight);
        }
        async Task LoadSavedCover(Photo photo,CancellationToken token) {
            var read=await Prepared(photo,true,()=>ReadCover(photo),token);
            int sourceHeight=read.Item2;
            var display=read.Item1;
            if(token.IsCancellationRequested)return;
            coverHeight=sourceHeight;
            if(display!=null){
                ShowCachedCover(photo,display);
                if(photo.CoverFile==null && photo.Linked && !System.IO.File.Exists(photo.Original))
                    locate.Visibility=Visibility.Visible;
                return;
            }
            status.Warn(L.T("The saved cover file can't be found."),L.T("The file may have been moved or deleted from the cover folder. You can still view the original, and you can make the cover again."));
            toggle.Content=L.T(AnimationFrames.Supports(photo.Original)?"Play original":"View original");
            toggle.IsEnabled=true;cachedCover=true;editCover.Visibility=Visibility.Visible;
            if(photo.Linked && !System.IO.File.Exists(photo.Original))locate.Visibility=Visibility.Visible;
        }
        void ShowCachedCover(Photo photo,BitmapSource display) {
            SetPicture(display);cachedCover=true;editCover.Visibility=Visibility.Visible;
            toggle.Content=L.T(AnimationFrames.Supports(photo.Original)?"Play original":"View original");
            toggle.IsEnabled=true;status.Text="";
            UpdateUpscale();
            RefreshCoverStrip(photo);
            if(!forCrop && photo.HasCrop && !photo.IsOutside && MotionMaker.Supports(photo.Original)) {
                // A cover that has a GIF already: edit that GIF (same part and choices) instead of making a new one.
                makeGif.Content=L.T(photo.HasMotion?"Edit GIF cover":"Make GIF cover");
                makeGif.Visibility=Visibility.Visible;
            }
            UpdateCoverButtons();
            if(photo.HasMotion && !forCrop) {
                motionToggle.Visibility=Visibility.Visible;UpdateMotionToggle();
                if(showMotion)PlayMotion(photo);
            }
        }
        // A cover with a GIF shows only the edit button for the cover on screen:
        // GIF cover → Edit GIF cover, JPG cover → Edit JPG cover.
        void UpdateCoverButtons() {
            var photo=items[index];
            bool hasGif=!forCrop && home!=null && photo.HasMotion && photo.HasCrop && MotionMaker.Supports(photo.Original);
            if(!hasGif){editCover.Content=L.T("Edit cover");return;}
            editCover.Content=L.T("Edit JPG cover");
            editCover.Visibility=showMotion?Visibility.Collapsed:Visibility.Visible;
            makeGif.Visibility=showMotion?Visibility.Visible:Visibility.Collapsed;
        }
        void UpdateMotionToggle() {
            string ext=System.IO.Path.GetExtension(items[index].MotionFile??"").TrimStart('.').ToUpperInvariant();
            motionToggle.Content=showMotion?L.T("View JPG cover"):L.T("View GIF cover");
        }
        async void PlayMotion(Photo photo) {
            StopMotion();
            var current=new CancellationTokenSource();motionLoading=current;
            try {
                var frames=await MotionFrames.LoadCoverAsync(photo,1600,current.Token);
                if(current.IsCancellationRequested || items[index]!=photo || !cachedCover || !showMotion)return;
                motionPlayer.Start(frames);
            } catch(Exception) {}
        }
        void StopMotion() {
            if(motionLoading!=null){motionLoading.Cancel();motionLoading=null;}
            if(motionPlayer!=null)motionPlayer.Stop();
        }
        async void ChooseOriginal(){
            var dialog=new Microsoft.Win32.OpenFileDialog {Title=L.T("Choose the original file you moved"),FileName=items[index].Name};
            if(dialog.ShowDialog(this)!=true)return;
            var photo=items[index];string path=dialog.FileName;locate.IsEnabled=false;
            try{
                bool matches=await Task.Run(()=>OriginalLocator.TryChosen(photo,path,CancellationToken.None));
                if(!IsVisible || items[index]!=photo)return;
                // The file may have been edited since (tags added, for example). The person knows best, so ask instead of refusing.
                if(!matches && MessageBox.Show(this,L.F("The file “{0}” is a little different from the file this cover was made from. It may have been edited since then. Connect this cover to this file anyway?",System.IO.Path.GetFileName(path)),
                    "LoveMyShelf",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)==MessageBoxResult.Yes) {
                    await Task.Run(()=>OriginalLocator.Link(photo,path));
                    if(!IsVisible || items[index]!=photo)return;
                    matches=true;
                }
                if(matches){
                    photo.Original=path;
                    var gallery=home;
                    if(gallery!=null)await gallery.MigrateLocatedCover(photo);
                    if(IsVisible && items[index]==photo)LoadCurrent(!original);
                }
                else status.Warn(L.T("This is not the original of this cover"),L.T("Choose the photo or video you used when you made this cover."));
            }catch(Exception ex){if(IsVisible && items[index]==photo)status.Warn(L.T("Something went wrong"),ex.Message);}
            finally{locate.IsEnabled=true;}
        }
        void ShowFrames(Photo photo,bool selected,CancellationToken token) {
            if(selected && photo.FrameTime.HasValue && VideoFrames.Supports(photo.Original)) {
                var saved=AnimationFrames.SavedFrame(photo.Original,photo,0,token);
                Dispatcher.Invoke(new Action(()=>{if(!token.IsCancellationRequested)ShowStill(saved,true);}));
                return;
            }
            using(var file=new AnimationFrames(photo.Original,token)){
                bool movie=file.Count>1 || VideoFrames.Supports(photo.Original);
                if(!movie || selected){
                    var still=file.Read(Math.Max(0,Math.Min(file.Count-1,photo.FrameIndex)),0);
                    Dispatcher.Invoke(new Action(()=>{if(!token.IsCancellationRequested)ShowStill(still,movie);}));return;
                }
                var queue=new FrameRequests();
                Dispatcher.Invoke(new Action(()=>{
                    if(token.IsCancellationRequested)return;
                    times=file.Times;requests=queue;StartTransport(times.Duration);queue.Ask(0,false);
                }));
                queue.Decode(file,displaySide,token,Dispatcher,(i,frame)=>{
                    SetPicture(frame);shown=i;ReadyMotion();
                });
            }
        }
        void ReadyMotion(){
            if(toggle.IsEnabled)return;
            toggle.IsEnabled=true;status.Text="";toggle.Content=WantsCrop?CropLabel:L.T("View cover");
            ShowRecrop(!WantsCrop);
        }
        async Task PlayVideo(string path,CancellationToken token) {
            string copy=null;
            var indexTask=WarmIndex(path,token);
            videoIndex=indexTask;
            try{
                if(MpvPlayer.Available) {
                    string problem=await PlayMpv(path,token);
                    if(problem==null)return;
                    token.ThrowIfCancellationRequested();ClearTransport();toggle.IsEnabled=false;SetPicture(null);
                    Trace.WriteLine("mpv playback failed, using Windows playback: "+problem);
                    videoIndex=indexTask;
                }
                var orientation=await Task.Run(()=>VideoFrames.ReadOrientation(path,token),token);
                token.ThrowIfCancellationRequested();
                if(!orientation.NeedsNormalizedCopy && await PlaySource(path,token,orientation))return;
                token.ThrowIfCancellationRequested();ClearTransport();toggle.IsEnabled=false;SetPicture(null);status.Text=L.T("Getting ready to play…");
                var gallery=home;
                string directory=gallery==null?System.IO.Path.GetTempPath():gallery.PlaybackDirectory;
                copy=await Task.Run(()=>VideoFrames.PlayableCopy(path,directory,token),token);
                token.ThrowIfCancellationRequested();
                videoIndex=indexTask;
                if(!await PlaySource(copy,token,new VideoOrientation()))throw new System.IO.InvalidDataException(L.T("Windows could not play this video."));
            }finally{VideoFrames.RemoveCopy(copy);}
        }
        static Task<FrameTimes> WarmIndex(string path,CancellationToken token) {
            return Task.Run(()=>{
                try{return VideoFrames.Index(path,token,true);}
                catch(Exception){return null;} // Playback can still step without an index.
            });
        }
        // Plays the original with mpv. Completes with an error message if mpv cannot play it;
        // otherwise it keeps playing until the viewer moves on and the token cancels.
        async Task<string> PlayMpv(string path,CancellationToken token) {
            var player=new MpvPlayer(path);
            var done=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            player.Failed+=message=>done.TrySetResult(message??"mpv error");
            player.Opened+=(length,audio)=>{
                if(token.IsCancellationRequested || mpv!=player)return;
                mpvAudio=audio;ApplyAudio();player.Speed=rate;
                if(audio && clearVoice && !player.SetClearVoice(true,clearVoiceLevel))ShowCornerNote(L.T("Clear voice isn't available for this video"));
                largeVideo=VideoFrames.IsHighResolution(player.PixelWidth,player.PixelHeight);
                viewport.InvalidateMeasure();
                StartTransport(length);ReadyMotion();
                subtitlesReady=true;
                AttachSubtitle(path,player);
                if(home!=null && !forCrop)home.TipMinimize();
            };
            player.VideoChanged+=delegate {
                if(mpv!=player)return;
                ApplyZoom(); // the picture's shape may have changed (a turn): fit, brightness and subtitles again
                CheckTurnReady();
            };
            mpv=player;mpvAudio=false;
            // Picked before playback starts, so the first moment of sound doesn't come from the wrong speaker.
            if(home!=null)UseSpeaker(SpeakerRouting.For(home.Settings,new System.Windows.Interop.WindowInteropHelper(this).Handle,
                device=>MpvPlayer.Speakers().Exists(s=>s.Item1==device)));
            player.AudioDevice=speaker;
            player.SubtitleScale=subtitleSize/100.0;
            speakerWaiting=null;speakerTimer.Start();
            viewport.Children.Insert(0,player);
            UseOverlay(true);
            try{
                using(token.Register(()=>done.TrySetCanceled())){
                    return await done.Task;
                }
            }finally{
                if(mpv==player){mpv=null;viewport.Children.Remove(player);player.Dispose();UseOverlay(false);}
            }
        }
        async Task<bool> PlaySource(string path,CancellationToken token,VideoOrientation orientation) {
            var player=new MediaElement {LoadedBehavior=MediaState.Manual,UnloadedBehavior=MediaState.Manual,Stretch=Stretch.Uniform,ScrubbingEnabled=true,SpeedRatio=rate};
            var done=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            media=player;ApplyAudio();viewport.Children.Insert(0,player);
            player.MediaOpened+=delegate {
                if(token.IsCancellationRequested||media!=player)return;
                if(!player.NaturalDuration.HasTimeSpan || !player.HasVideo){done.TrySetResult(false);return;}
                mediaTurn=orientation.NativeCorrection(player.NaturalVideoWidth,player.NaturalVideoHeight);
                ApplyTurn();
                largeVideo=VideoFrames.IsHighResolution(player.NaturalVideoWidth,player.NaturalVideoHeight);
                if(largeVideo)player.ScrubbingEnabled=false;
                StartTransport(player.NaturalDuration.TimeSpan.TotalMilliseconds);ReadyMotion();
            };
            player.MediaFailed+=delegate {done.TrySetResult(false);};
            player.MediaEnded+=delegate{if(media==player&&!paused&&!token.IsCancellationRequested){player.Position=TimeSpan.Zero;player.Play();}};
            try{
                using(token.Register(()=>done.TrySetCanceled())){
                    player.Source=new Uri(path,UriKind.Absolute);player.Play();
                    return await done.Task;
                }
            }finally{if(media==player){media=null;player.Close();viewport.Children.Remove(player);}}
        }
        void ShowStill(BitmapSource loaded,bool movie){
            full=loaded;selectedMovie=movie;toggle.IsEnabled=true;status.Text="";
            if(WantsCrop){SetPicture(full);return;}
            DisplayStill();if(movie)toggle.Content=L.T("Play original");
        }
        void DisplayStill(){
            if(!original&&cropped==null)cropped=Pictures.ViewCover(full,items[index]);
            SetPicture(original?full:cropped);toggle.Content=original?L.T("View cover"):L.T("View original");
            ShowRecrop(original);
        }
        // "Add cover" is the blue button while it shows; otherwise the view/play toggle is.
        void ShowRecrop(bool show) {
            recrop.Visibility=show && !forCrop?Visibility.Visible:Visibility.Collapsed;
            if(recrop.Visibility==Visibility.Visible)toggle.ClearValue(StyleProperty);
            else toggle.Style=(Style)Application.Current.Resources["Primary"];
        }
        // Opens in the same place, size and full screen as another window (the video it replaces), so nothing jumps.
        internal void PlaceLike(Window other) {
            if(other==null)return;
            bool full=FullScreen.IsFull(other);
            if(!full) {
                WindowStartupLocation=WindowStartupLocation.Manual;
                if(other.WindowState==WindowState.Normal){Left=other.Left;Top=other.Top;Width=other.ActualWidth;Height=other.ActualHeight;}
                WindowState=other.WindowState==WindowState.Maximized?WindowState.Maximized:WindowState.Normal;
            }
            // Full screen is set when the window loads (after its own starting choice).
            Loaded+=delegate {if(full)FullScreen.Enter(this);else FullScreen.Leave(this);};
        }
        // Opens at a place of its own (see ViewerPlacement). full: start in full screen or not; null keeps the starting choice.
        internal void PlaceAt(Rect box,bool maximized,bool? full) {
            WindowStartupLocation=WindowStartupLocation.Manual;
            Left=box.Left;Top=box.Top;Width=Math.Max(MinWidth,box.Width);Height=Math.Max(MinHeight,box.Height);
            WindowState=maximized?WindowState.Maximized:WindowState.Normal;
            if(full!=null)Loaded+=delegate {if(full.Value)FullScreen.Enter(this);else FullScreen.Leave(this);};
        }
        // The "GIF cover is ready" notice: shows that cover in this large view instead of opening another one.
        internal bool ShowCoverHere(Photo photo) {
            if(forCrop || !IsVisible || photo==null)return false;
            int at=-1;
            for(int i=0;i<items.Count;i++)if(items[i]!=null && String.Equals(items[i].Folder,photo.Folder,StringComparison.OrdinalIgnoreCase)){at=i;break;}
            if(at<0) {
                if(items.IsReadOnly)return false;
                at=Math.Min(items.Count,index+1);items.Insert(at,photo);
            } else items[at]=photo;
            index=at;LoadCurrent(true);
            if(WindowState==WindowState.Minimized)WindowState=WindowState.Normal;
            Activate();
            return true;
        }
        void Navigate(int delta){
            int target=index+delta;if(target<0||target>=items.Count)return;
            if(!String.Equals(items[target].Original,subtitleVideo,StringComparison.OrdinalIgnoreCase) && !ConfirmSubtitles(true))return;
            index=target;LoadCurrent();
        }
    }
}
