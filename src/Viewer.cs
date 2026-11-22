using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
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
        readonly Button toggle,prev,next,pause,locate,recrop,editCover,upscale,motionToggle,makeGif;
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
        string CropLabel {get{return CurrentPhoto.HasCrop?L.T("Make another cover"):L.T("Make cover");}}
        int index,shown=-1;
        bool fullscreen,original,hidden,motion,paused,sync,resumeAfterDrag,selectedMovie,largeVideo,draggingVideo,cachedCover;
        int playbackBrightness;
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
        internal int PlaybackBrightness {get {return playbackBrightness;}}

        sealed class MediaViewport : Panel {
            internal static double Aspect(UIElement element) {
                var image=element as Image;
                if(image!=null)return image.Source==null?0:image.Source.Width/image.Source.Height;
                var native=element as MpvPlayer;
                if(native!=null)return native.Aspect;
                var video=(MediaElement)element;
                if(video.NaturalVideoWidth<=0 || video.NaturalVideoHeight<=0)return 0;
                return Math.Abs(video.LayoutTransform.Value.M12)>0.5
                    ? (double)video.NaturalVideoHeight/video.NaturalVideoWidth
                    : (double)video.NaturalVideoWidth/video.NaturalVideoHeight;
            }
            static Rect Frame(UIElement child,Size viewport) {
                double aspect=Aspect(child);
                double width=aspect>0?viewport.Height*aspect:viewport.Width;
                return new Rect((viewport.Width-width)/2,0,width,viewport.Height);
            }
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
            hit.MouseLeftButtonDown+=(s,e)=>{
                if(!motion)return;
                surfaceClick.Stop();
                if(e.ClickCount==2)SetPaused(!paused);
                else if(e.ClickCount==1 && !fullscreen)surfaceClick.Start();
                e.Handled=true;
            };

            ui.Children.Add(hit);
            TextOptions.SetTextFormattingMode(cornerNote,TextFormattingMode.Ideal);
            ui.Children.Add(cornerNote);
            cornerNoteTimer.Tick+=delegate {
                cornerNoteTimer.Stop();
                var fade=new System.Windows.Media.Animation.DoubleAnimation(0,TimeSpan.FromMilliseconds(250));
                fade.Completed+=delegate {if(!cornerNoteTimer.IsEnabled)cornerNote.Visibility=Visibility.Collapsed;};
                cornerNote.BeginAnimation(OpacityProperty,fade);
            };
            panel.VerticalAlignment=VerticalAlignment.Bottom;
            panel.SizeChanged+=delegate {FitPictureAbovePanel();};
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
            volume.ValueChanged+=delegate {ApplyAudio();};
            playbackButtons.Children.Add(volume);
            // Speed has no box: C / X / Z like PotPlayer (see the key handler).
            root.SizeChanged+=delegate {ApplyPlaybackBrightness();};
            seek.Style=(Style)Application.Current.Resources["PlaybackTimeline"];
            AutomationProperties.SetName(seek,L.T("Playback position"));transport.Children.Add(seek);
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
                UpdateMotionToggle();
            });
            motionToggle.Visibility=Visibility.Collapsed;
            if(!forCrop)controls.Children.Add(motionToggle);
            Closed+=delegate {StopMotion();prepared.Clear();};
            locate=UI.Btn(L.T("Locate original"),ChooseOriginal);locate.Visibility=Visibility.Collapsed;controls.Children.Add(locate);
            controls.Children.Add(UI.Btn(L.T("Close"),Close));ui.Children.Add(panel);
            status.HorizontalAlignment=HorizontalAlignment.Center;status.VerticalAlignment=VerticalAlignment.Center;
            ui.Children.Add(status);
            AddHandler(Mouse.PreviewMouseMoveEvent,new MouseEventHandler((s,e)=>RefreshFullscreenControls()),true);
            AddHandler(Mouse.LostMouseCaptureEvent,new MouseEventHandler((s,e)=>RefreshFullscreenControls()),true);
            MouseLeave+=delegate {RefreshFullscreenControls();};
            Activated+=delegate {RefreshFullscreenControls();};
            Deactivated+=delegate {RefreshFullscreenControls();};
            SizeChanged+=delegate {RefreshFullscreenControls();};
            PreviewKeyDown+=(s,e)=>{
                var key=e.Key==Key.ImeProcessed?e.ImeProcessedKey:e.Key;
                switch(key) {
                    case Key.F1:GuideWindow.Open(this,"large");break;
                    case Key.Escape:Close();break;
                    case Key.Left:case Key.Right:
                        // ← → skip by the chosen number of seconds; Shift + ← → skips one second.
                        if(!motion || (Keyboard.Modifiers!=ModifierKeys.None && Keyboard.Modifiers!=ModifierKeys.Shift) || draggingVideo || resumeAfterDrag)return;
                        Skip(key==Key.Left?-1:1,Keyboard.Modifiers==ModifierKeys.Shift?1:skipSeconds);break;
                    case Key.K:case Key.L:
                        if(forCrop || Keyboard.Modifiers!=ModifierKeys.None)return;
                        Navigate(key==Key.K?-1:1);break;
                    case Key.D:case Key.F:
                        if(!motion || Keyboard.Modifiers!=ModifierKeys.None)return;
                        StepFrame(key==Key.D?-1:1);break;
                    case Key.E:case Key.W:case Key.Q:
                        if(!motion || Keyboard.Modifiers!=ModifierKeys.None)return;
                        playbackBrightness=key==Key.Q?0:Math.Max(-100,Math.Min(100,playbackBrightness+(key==Key.E?1:-1)));
                        ApplyPlaybackBrightness();seekPreview.Brightness=playbackBrightness;
                        ShowCornerNote(playbackBrightness==0?L.T("Brightness: normal"):L.F("Brightness {0:+0;-0}",playbackBrightness));break;
                    case Key.Space:if(!e.IsRepeat){if(motion)SetPaused(!paused);else if(!WantsCrop)Primary();}break;
                    case Key.C:case Key.X:case Key.Z:
                        // Like PotPlayer: C faster, X slower by 0.1, Z back to normal.
                        if(!motion || Keyboard.Modifiers!=ModifierKeys.None)return;
                        double newRate=key==Key.Z?1:Math.Round(rate+(key==Key.C?0.1:-0.1),1);
                        SetRate(Math.Max(0.2,Math.Min(4,newRate)));
                        ShowCornerNote(L.F("Playback speed {0:0.0}×",rate));
                        break;
                    case Key.Tab:if(!fullscreen)SetHidden(false);return;default:return;
                } e.Handled=true;
            };
            ticker.Tick+=delegate {Tick();};Loaded+=delegate {LoadCurrent(openCover);};
            Closed+=delegate {
                if(loading!=null)loading.Cancel();
                ClearTransport();picture.Source=null;
            };
            ContentRendered+=delegate {Focus();};
        }
        void ShowCornerNote(string text) {
            cornerNote.Text=text;cornerNote.BeginAnimation(OpacityProperty,null);cornerNote.Opacity=1;
            cornerNote.Visibility=Visibility.Visible;cornerNoteTimer.Stop();cornerNoteTimer.Start();
        }
        static void LabelControl(Control control,string label) {
            AutomationProperties.SetName(control,label);control.ToolTip=label;
        }
        void ApplyAudio() {
            if(mpv!=null)mpv.Volume=volume.Value;
            if(media==null)return;
            media.Volume=volume.Value/100;
        }
        readonly TaskCompletionSource<bool> firstPicture=new TaskCompletionSource<bool>();
        // Completes when the first picture is on screen.
        internal Task FirstPicture {get {return firstPicture.Task;}}
        // Set when the view was opened without ShowDialog: Edit then closes it and calls this instead of ending a dialog.
        internal Action<Photo> EditLater;
        void SetPicture(BitmapSource frame) {
            if(frame!=null)Dispatcher.BeginInvoke(new Action(()=>firstPicture.TrySetResult(true)),DispatcherPriority.ContextIdle);
            picture.Source=frame;
            viewport.InvalidateMeasure();
            ApplyPlaybackBrightness();
        }
        void ApplyPlaybackBrightness() {
            int level=playbackBrightness;
            if(!motion || level==0){brightnessLayer.Visibility=Visibility.Collapsed;return;}
            double aspect=picture.Source!=null?MediaViewport.Aspect(picture):mpv!=null?mpv.Aspect:media==null?0:MediaViewport.Aspect(media);
            if(aspect<=0 || root.ActualWidth<=0 || root.ActualHeight<=0){
                brightnessLayer.Visibility=Visibility.Collapsed;return;
            }
            double room=Math.Max(0,root.ActualHeight-viewport.Margin.Bottom);
            brightnessLayer.Width=Math.Min(root.ActualWidth,room*aspect);
            brightnessLayer.Height=room;
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
            SetPaused(true);
            var timing=times;
            if(Native && videoIndex!=null && videoIndex.Status==TaskStatus.RanToCompletion)timing=videoIndex.Result;
            if(timing==null && mpv!=null){mpv.Step(direction);return;}
            if(timing==null) {
                // No list of the video's own pictures (yet): still one scene step.
                SeekTo(Math.Min(Math.Max(0,duration-1),Math.Max(0,startMs+direction*(double)Scenes.StepMs)));
                return;
            }
            int current=timing.At(startMs);
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
        void SetFullscreen(bool full) {
            fullscreen=full;
            surfaceClick.Stop();
            hidden=false;
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
        }
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
                    overlay.Activated+=delegate {RefreshFullscreenControls();};
                    overlay.Deactivated+=delegate {RefreshFullscreenControls();};
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
            double bottomZone=Math.Max(bottomDistance,panel.ActualHeight);
            bool nearPanel=inside && ActualHeight-pointer.Y<=bottomZone;
            titleBar.Visibility=nearTitle || titleBar.IsMouseCaptureWithin?Visibility.Visible:Visibility.Collapsed;
            SetPanelVisible(nearPanel || panel.IsMouseCaptureWithin);
        }
        void SetHidden(bool hide) {
            hidden=hide;
            SetPanelVisible(!hide);
        }
        void SetPanelVisible(bool visible) {
            if(!fullscreen)visible=true; // windowed: always shown
            var visibility=visible?Visibility.Visible:Visibility.Collapsed;
            if(panel.Visibility==visibility)return;
            panel.Visibility=visibility;
            if(!visible)seekPreview.Hide();
            FitPictureAbovePanel();
        }
        // Windowed, the picture uses only the space above the bar so the two never overlap.
        void FitPictureAbovePanel() {
            double reserve=!fullscreen && panel.Visibility==Visibility.Visible?panel.ActualHeight:0;
            var margin=new Thickness(0,0,0,reserve);
            if(viewport.Margin!=margin){viewport.Margin=margin;brightnessLayer.Margin=margin;ApplyPlaybackBrightness();}
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
            seekPreview.Clear();
            ticker.Stop();surfaceClick.Stop();clock.Reset();
            motion=false;paused=false;selectedMovie=false;resumeAfterDrag=false;largeVideo=false;draggingVideo=false;
            if(seek.IsMouseCaptured)seek.ReleaseMouseCapture();
            duration=0;startMs=0;shown=-1;times=null;requests=null;videoIndex=null;
            transport.Visibility=playbackButtons.Visibility=pause.Visibility=Visibility.Collapsed;
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
            transport.Visibility=playbackButtons.Visibility=pause.Visibility=Visibility.Visible;
            ReserveWidth(time,Widest(Stamp(duration)+" / "+Stamp(duration)));
            clock.Restart();ticker.Start();UpdateBar(0);
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
            if(!motion || paused || draggingVideo)return;
            double ms=Position();UpdateBar(ms);
            if(requests!=null)requests.Ask(times.At(ms),false);
        }
        void SeekTo(double ms,double? nativeMs=null) {
            ms=Math.Min(Math.Max(0,ms),duration);startMs=ms;clock.Restart();
            if(media!=null){
                if(largeVideo && paused)media.ScrubbingEnabled=true;
                media.Position=TimeSpan.FromMilliseconds(Math.Min(duration,nativeMs??ms));
            }
            else if(mpv!=null)mpv.Seek(ms);
            else if(requests!=null){shown=-1;requests.Ask(times.At(ms),true);}
            UpdateBar(ms);
        }
        void SetPaused(bool value){
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
                L.T("Enlarge 4× with AI");
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
            status.Show(L.T("Upscaling has started"),L.T("AI is making this cover 4 times bigger and sharper. It runs in the background, so feel free to keep looking at other photos. We’ll let you know here when it’s done."),NoticeKind.Info,7);
            UpdateUpscale();
        }
        void ChooseFrames() {
            var gallery=home;
            if(gallery==null)return;
            seekPreview.Hide();
            bool resume=motion && !paused;
            if(resume)SetPaused(true);
            CropNearby=mpv!=null;
            CropTime=Native?Position():(double?)null;
            CropFrame=times==null?0:times.At(startMs);
            if(gallery.EditFrames(this))Close();
            else if(resume && IsVisible)SetPaused(false);
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
            var current=new CancellationTokenSource();loading=current;var token=current.Token;
            Photo photo=items[index];
            if(!forCrop)Title=photo.Name+" — Love My Shelf";
            var gallery=home;
            bool selected=!WantsCrop && (showCover??(gallery==null || gallery.Settings.ShowCover(photo.Original)));
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
                        status.Text=L.T("Looking for the original…");status.VerticalAlignment=VerticalAlignment.Top;
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
                    try{SetPicture(CoverImages.TrimBlackBands(CoverImages.TryLoad(photo)??Pictures.SavedPreview(photo)));status.VerticalAlignment=VerticalAlignment.Top;status.Warn(L.T("The original was moved or renamed"),L.T("You are seeing the preview saved with the cover. Click Locate original and choose the file again to see it in full quality."));}catch(Exception){}
                    if(photo.Linked)locate.Visibility=Visibility.Visible;
                }
            }finally{
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
                try{cover=Pictures.Load(photo.Thumb);}catch(IOException){}
            }
            // The upscale size limit applies to the saved cover file, not the version currently shown.
            try {
                if(System.IO.File.Exists(photo.CoverFile))
                    using(var file=System.IO.File.Open(photo.CoverFile,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete))
                        sourceHeight=BitmapFrame.Create(file,BitmapCreateOptions.DelayCreation,BitmapCacheOption.None).PixelHeight;
            } catch(IOException){} catch(NotSupportedException){}
            return Tuple.Create(cover==null?null:CoverImages.TrimBlackBands(cover),sourceHeight);
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
            status.Warn(L.T("The saved cover file is missing."),L.T("It may have been moved or deleted from the cover folder. You can still view the original or make the cover again."));
            toggle.Content=L.T(AnimationFrames.Supports(photo.Original)?"Play original":"View original");
            toggle.IsEnabled=true;cachedCover=true;editCover.Visibility=Visibility.Visible;
            if(photo.Linked && !System.IO.File.Exists(photo.Original))locate.Visibility=Visibility.Visible;
        }
        void ShowCachedCover(Photo photo,BitmapSource display) {
            SetPicture(display);cachedCover=true;editCover.Visibility=Visibility.Visible;
            toggle.Content=L.T(AnimationFrames.Supports(photo.Original)?"Play original":"View original");
            toggle.IsEnabled=true;status.Text="";
            UpdateUpscale();
            if(!forCrop && photo.HasCrop && MotionMaker.Supports(photo.Original)) {
                // A cover that has a GIF already: edit that GIF (same part and choices) instead of making a new one.
                makeGif.Content=L.T(photo.HasMotion?"Edit GIF cover":"Make GIF cover");
                makeGif.Visibility=Visibility.Visible;
            }
            if(photo.HasMotion && !forCrop) {
                motionToggle.Visibility=Visibility.Visible;UpdateMotionToggle();
                if(showMotion)PlayMotion(photo);
            }
        }
        void UpdateMotionToggle() {
            string ext=System.IO.Path.GetExtension(items[index].MotionFile??"").TrimStart('.').ToUpperInvariant();
            motionToggle.Content=showMotion?L.T("View JPG cover"):L.T("View GIF cover");
        }
        async void PlayMotion(Photo photo) {
            StopMotion();
            var current=new CancellationTokenSource();motionLoading=current;
            try {
                var frames=await MotionFrames.LoadAsync(photo.MotionFile,1600,current.Token);
                if(current.IsCancellationRequested || items[index]!=photo || !cachedCover || !showMotion)return;
                motionPlayer.Start(frames);
            } catch(Exception) {}
        }
        void StopMotion() {
            if(motionLoading!=null){motionLoading.Cancel();motionLoading=null;}
            if(motionPlayer!=null)motionPlayer.Stop();
        }
        async void ChooseOriginal(){
            var dialog=new Microsoft.Win32.OpenFileDialog {Title=L.T("Select the original file"),FileName=items[index].Name};
            if(dialog.ShowDialog(this)!=true)return;
            var photo=items[index];string path=dialog.FileName;locate.IsEnabled=false;
            try{
                bool matches=await Task.Run(()=>OriginalLocator.TryChosen(photo,path,CancellationToken.None));
                if(!IsVisible || items[index]!=photo)return;
                if(matches){
                    photo.Original=path;
                    var gallery=home;
                    if(gallery!=null)await gallery.MigrateLocatedCover(photo);
                    if(IsVisible && items[index]==photo)LoadCurrent(!original);
                }
                else status.Warn(L.T("This is not the original of this cover"),L.T("Choose the file you used when you made this cover."));
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
                token.ThrowIfCancellationRequested();ClearTransport();toggle.IsEnabled=false;SetPicture(null);status.Text=L.T("Preparing a temporary file for playback…");
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
                largeVideo=VideoFrames.IsHighResolution(player.PixelWidth,player.PixelHeight);
                viewport.InvalidateMeasure();
                StartTransport(length);ReadyMotion();
            };
            player.VideoChanged+=delegate {
                if(mpv!=player)return;
                viewport.InvalidateMeasure();ApplyPlaybackBrightness();
            };
            mpv=player;mpvAudio=false;
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
                double rotation=orientation.NativeCorrection(player.NaturalVideoWidth,player.NaturalVideoHeight);
                if(rotation!=0)player.LayoutTransform=new RotateTransform(rotation);
                viewport.InvalidateMeasure();
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
        void Navigate(int delta){int target=index+delta;if(target<0||target>=items.Count)return;index=target;LoadCurrent();}
    }
}
