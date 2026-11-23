using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Polygon=System.Windows.Shapes.Polygon;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace LoveMyShelf {
    // What the person picked in the Make GIF window: seconds before and after the cover's moment, and how it plays.
    internal sealed class MotionRequest {
        public double Before=1.5,After=1.5;
        public int Fps=15;
        public int Height=960;
        public string Format="gif"; // gif, webp or mp4
        public double Speed=1; // 0.5 = half speed, 2 = twice as fast
        public string Loop="forward"; // forward, bounce (forward, then back) or reverse
        public int Quality=1; // 0 high, 1 normal, 2 smaller file
        // A file the Make GIF window already made with exactly these choices; Make then only has to move it into place.
        public string Prepared,PreparedKey;
        public double PreparedBefore,PreparedAfter;
        internal double Seconds {get {return Before+After;}}
        internal string Extension {get {return "."+Format;}}
        internal int ForwardFrames {get {return Math.Max(1,(int)Math.Round(Seconds/Math.Max(0.1,Speed)*Fps,MidpointRounding.AwayFromZero));}}
        internal bool Bounces {get {return Loop=="bounce" && ForwardFrames>=3;}}
        // Frames in the finished file and how long one round of it plays.
        internal int Frames {get {return Bounces?ForwardFrames*2-2:ForwardFrames;}}
        internal double PlaySeconds {get {return Frames/(double)Math.Max(1,Fps);}}
        // Saved with the cover (photo.xml "motionSettings"), so Edit GIF cover opens with the same part and choices.
        internal string ToText() {
            var c=CultureInfo.InvariantCulture;
            return String.Format(c,"before={0};after={1};fps={2};height={3};format={4};speed={5};loop={6};quality={7}",
                Math.Round(Before,3),Math.Round(After,3),Fps,Height,Format,Speed,Loop,Quality);
        }
        internal static MotionRequest Parse(string text) {
            if(String.IsNullOrEmpty(text))return null;
            try {
                var values=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
                foreach(var part in text.Split(';')){int at=part.IndexOf('=');if(at>0)values[part.Substring(0,at).Trim()]=part.Substring(at+1).Trim();}
                var c=CultureInfo.InvariantCulture;
                var r=new MotionRequest {
                    Before=Double.Parse(values["before"],c),After=Double.Parse(values["after"],c),
                    Fps=Int32.Parse(values["fps"],c),Height=Int32.Parse(values["height"],c),Format=values["format"].ToLowerInvariant(),
                    Speed=Double.Parse(values["speed"],c),Loop=values["loop"].ToLowerInvariant(),Quality=Int32.Parse(values["quality"],c)};
                if(Double.IsNaN(r.Before) || Double.IsNaN(r.After) || r.Before<0 || r.After<0 || r.Before+r.After<=0)return null;
                if(r.Format!="gif" && r.Format!="webp" && r.Format!="mp4")return null;
                if(r.Loop!="forward" && r.Loop!="bounce" && r.Loop!="reverse")return null;
                if(r.Speed<=0 || r.Fps<=0 || r.Height<=0)return null;
                r.Quality=Math.Max(0,Math.Min(2,r.Quality));
                return r;
            } catch(Exception) {return null;}
        }
    }

    // What the GIF is made from: the original, the cover's moment and the cover's look.
    internal sealed class MotionSource {
        public string Path;
        public double Moment;
        public Rect Crop;
        public double Rotation;
        public int Brightness;
        // The JPG cover as it looks now, shown in the preview until the first moving frames are ready.
        public BitmapSource Still;
        // For a cover that isn't saved yet: the moment of a GIF/WebP frame index.
        internal static double MomentOf(string path,double? frameTime,int frameIndex) {
            if(frameTime.HasValue)return frameTime.Value;
            if(frameIndex<=0)return 0;
            using(var file=new AnimationFrames(path))return file.Times.Start(Math.Min(frameIndex,file.Count-1));
        }
    }

    // Make GIF: a strip of scenes around the cover to drag a start and an end on, start and end times that can be
    // nudged, speed and play style, and a preview that is the real file: it is made in the background with every
    // choice, so the preview, the size shown and the saved file are the same (and Make just moves it into place).
    internal sealed class MotionWindow : Window {
        internal MotionRequest Value {get;private set;}
        // Set from Guide & Options → GIF covers: how many seconds the strip shows at first.
        internal static int StripSeconds=10;
        internal static readonly int[] Rates={10,12,15,20,24,30};
        internal static readonly int[] Heights={480,640,720,960,1280};
        static readonly double[] Speeds={0.5,0.75,1,1.5,2};
        static readonly string[] Loops={"forward","bounce","reverse"};
        static readonly string[] Formats={"gif","webp","mp4"};
        // Technical choices start from the saved defaults and stay as last used while LoveMyShelf runs;
        // speed and play style start fresh each time.
        static int lastFps=2,lastHeight=3,lastQuality=1,lastFormat=0;
        static double sideMs=1500;
        // From Guide & Options: how far apart the scenes of the quick preview are, in milliseconds.
        // This is the GIF window's own setting, apart from the scene step (Scenes.StepMs).
        internal static int PreviewStepMs=100;
        internal static void UseDefaults(Preferences settings) {
            StripSeconds=settings.GifStripSeconds;PreviewStepMs=settings.GifPreviewStepMs;Scenes.StepMs=settings.SceneStepMs;
            lastFps=Math.Max(0,Array.IndexOf(Rates,settings.GifFps));if(Rates[lastFps]!=settings.GifFps)lastFps=Nearest(Rates,settings.GifFps);
            lastHeight=Array.IndexOf(Heights,settings.GifHeight);if(lastHeight<0)lastHeight=Nearest(Heights,settings.GifHeight);
            lastQuality=Math.Max(0,Math.Min(2,settings.GifQuality));lastFormat=Math.Max(0,Math.Min(2,settings.GifFormat));
            sideMs=settings.GifSideTenths*100.0;
        }
        static int Nearest(int[] values,int value) {
            int best=0;
            for(int i=1;i<values.Length;i++)if(Math.Abs(values[i]-value)<Math.Abs(values[best]-value))best=i;
            return best;
        }
        const int Thumbs=14; // pictures on the strip before its width is known
        const double StripHeight=84,ThumbGap=2;
        // Strip pictures show the cover's own part of each scene, in its 9:16 shape, so a wider window shows
        // more of them (like a video editor's timeline) instead of stretching each one.
        const double CellWidth=StripHeight*9/16;
        const int CellPixels=(int)(StripHeight*2);
        // Room at each end of the strip for a handle (12 wide, drawn outside the chosen part) and a little dark gap,
        // so a handle at the clip's very start or end stays whole and the empty strip beyond shows the clip ends there.
        // In the middle of a clip this room shows the neighbouring pictures, so the strip still looks full.
        const double EdgeRoom=18;
        // Longer GIFs keep the lighter preview copies instead of reading every frame of the finished file.
        const int ExactPreviewLimit=300;

        readonly MotionSource source;
        readonly CancellationTokenSource lifetime=new CancellationTokenSource();
        readonly Canvas strip=new Canvas {Height=StripHeight,ClipToBounds=true,Background=Brushes.Black};
        readonly Image preview=new Image {Stretch=Stretch.Uniform};
        readonly Border previewFrame,statusPill;
        readonly TextBlock status=new TextBlock {Foreground=Brushes.White,FontSize=12,TextAlignment=TextAlignment.Center};
        readonly FramePlayer player;
        readonly TextBlock summary=UI.Text("",13,true);
        readonly Dictionary<int,BitmapSource> thumbs=new Dictionary<int,BitmapSource>();
        readonly HashSet<int> loading=new HashSet<int>();
        readonly DispatcherTimer edgeScroll=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(40)};
        // The real file is only made once the handles have been still for a moment; the quick preview shows at once.
        readonly DispatcherTimer prepareSoon=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(1500)};
        DraftScenes draft;
        CancellationTokenSource draftWork;
        bool draftOn; // the quick preview is playing and the real file is not ready yet
        readonly Border playMark=new Border {Width=2,Height=StripHeight,Background=Brushes.White,IsHitTestVisible=false,
            BorderBrush=new SolidColorBrush(Color.FromArgb(0x66,0,0,0)),BorderThickness=new Thickness(0.5,0,0.5,0)};
        ComboBox fps,height,quality,format;
        RadioButton[] speedChoices,loopChoices;
        Border startChip,endChip;
        TextBlock startTime,endTime;
        double duration=Double.NaN,viewStart,span,start,end,step;
        int dragging; // 0 none, 1 start handle, 2 end handle, 3 panning
        double dragOrigin,dragView,edgeDirection;
        int activeHandle=2;
        Button make;
        TextBlock help;
        // The background run that makes the real file for the preview, and what it made.
        CancellationTokenSource prepareWork;
        string preparedFile,preparedKey;
        MotionResult preparedResult;
        long preparedBytes;
        readonly List<string> previewFiles=new List<string>();
        double[] timeline; // the source moment of each preview frame, for the play mark on the strip
        double playAt=Double.NaN;
        bool held; // paused by the person
        double preparing=-1; // 0..1 while the preview file is being made
        string problem;
        double coverPixels; // the cover's height in the original's pixels, so the guess uses the real output size
        int preparedFrames=-1;double preparedSeconds;

        // editing: the cover already has a GIF; saved: the choices it was made with (null for a GIF made before they were saved).
        internal MotionWindow(Window owner,MotionSource source,bool fromEditor,bool editing=false,MotionRequest saved=null) {
            this.source=source;
            UI.WindowSetup(this,L.T(editing?"Edit GIF cover":"Make GIF cover"));Owner=owner;ShowInTaskbar=false;
            WindowStartupLocation=WindowStartupLocation.CenterOwner;
            var area=SystemParameters.WorkArea;
            Width=Math.Min(860,area.Width);Height=Math.Min(820,area.Height);MinWidth=Math.Min(600,area.Width);MinHeight=Math.Min(600,area.Height);
            span=Math.Max(2,StripSeconds)*1000.0;step=span/Thumbs;
            start=Math.Max(0,source.Moment-sideMs);end=source.Moment+sideMs;
            viewStart=Math.Max(0,source.Moment-span/2);
            int speedPick=2,loopPick=0,fpsPick=lastFps,heightPick=lastHeight,qualityPick=lastQuality,formatPick=lastFormat;
            if(saved!=null) {
                // Opens on the part and the choices the GIF was made with; the strip widens to show all of it.
                start=Math.Max(0,source.Moment-saved.Before*1000);end=source.Moment+saved.After*1000;
                span=Math.Max(span,(end-start)*1.25);step=span/Thumbs;
                viewStart=Math.Max(0,(start+end)/2-span/2);
                for(int i=0;i<Speeds.Length;i++)if(Math.Abs(Speeds[i]-saved.Speed)<Math.Abs(Speeds[speedPick]-saved.Speed))speedPick=i;
                loopPick=Math.Max(0,Array.IndexOf(Loops,saved.Loop));
                fpsPick=Nearest(Rates,saved.Fps);heightPick=Nearest(Heights,saved.Height);
                qualityPick=saved.Quality;formatPick=Math.Max(0,Array.IndexOf(Formats,saved.Format));
            }
            player=new FramePlayer(preview);
            player.Shown+=ShowPlayMark;
            playMark.BorderBrush.Freeze();
            var root=new DockPanel();root.Children.Add(new WindowTitleBar(this));
            var page=new DockPanel {Margin=new Thickness(20,12,20,16)};root.Children.Add(page);Content=root;

            // Bottom: the strip, start and end, how it plays, more choices, buttons.
            var bottom=new StackPanel();DockPanel.SetDock(bottom,Dock.Bottom);page.Children.Add(bottom);
            var stripBar=new DockPanel {Margin=new Thickness(0,8,0,4)};
            var back=UI.Btn(L.T("Back to the cover"),()=>{viewStart=ClampView(source.Moment-span/2);Layout();ShowDraft(draftOwed);});
            DockPanel.SetDock(back,Dock.Right);stripBar.Children.Add(back);
            help=UI.Text(NoticeLook.KeepWords(editing && saved==null?
                L.T("This GIF was made before Love My Shelf remembered GIF choices, so it starts from the usual part around the cover."):
                L.T("Drag the two handles to choose the part. Drag the pictures to see more.")),12,true);
            help.TextWrapping=TextWrapping.Wrap;help.VerticalAlignment=VerticalAlignment.Center;stripBar.Children.Add(help);
            bottom.Children.Add(stripBar);
            var stripFrame=new Border {Child=strip,CornerRadius=new CornerRadius(6),ClipToBounds=true};
            bottom.Children.Add(stripFrame);

            // Start and end can be nudged by one scene (the scene step); the summary says what the file will be.
            var rangeRow=new DockPanel {Margin=new Thickness(0,10,0,0)};
            summary.VerticalAlignment=VerticalAlignment.Center;summary.Margin=new Thickness(12,0,0,0);
            DockPanel.SetDock(summary,Dock.Right);rangeRow.Children.Add(summary);
            var chips=new StackPanel {Orientation=Orientation.Horizontal};
            startChip=TimeChip(L.T("Start"),1,L.T("Start one scene earlier"),L.T("Start one scene later"),out startTime);
            endChip=TimeChip(L.T("End"),2,L.T("End one scene earlier"),L.T("End one scene later"),out endTime);
            endChip.Margin=new Thickness(8,0,0,0);
            chips.Children.Add(startChip);chips.Children.Add(endChip);rangeRow.Children.Add(chips);
            bottom.Children.Add(rangeRow);

            // Speed and play style change the feel the most, so they're always in view.
            var styleRow=new WrapPanel {Margin=new Thickness(0,10,0,0)};
            styleRow.Children.Add(RowLabel(L.T("Speed"),0));
            styleRow.Children.Add(Segment(Array.ConvertAll(Speeds,s=>L.F("{0}×",s.ToString("0.##",CultureInfo.CurrentCulture))),speedPick,L.T("Speed"),Changed,out speedChoices));
            styleRow.Children.Add(RowLabel(L.T("Play style"),18));
            styleRow.Children.Add(Segment(new[]{L.T("Loop"),L.T("Back and forth"),L.T("Backward")},loopPick,L.T("Play style"),Changed,out loopChoices));
            bottom.Children.Add(styleRow);

            var more=new Expander {Header=L.T("More choices"),Margin=new Thickness(0,8,0,0),IsExpanded=false};
            more.SetResourceReference(Control.ForegroundProperty,"Text");
            // Two columns, so opening these leaves the preview a good size.
            var choices=new StackPanel {Margin=new Thickness(0,6,0,0)};more.Content=choices;
            var grid=new Grid();choices.Children.Add(grid);
            grid.ColumnDefinitions.Add(new ColumnDefinition());grid.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(14)});grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.RowDefinitions.Add(new RowDefinition());grid.RowDefinitions.Add(new RowDefinition());
            fps=Choice(Cell(grid,0,0),L.T("Smoothness (frames per second)"),Array.ConvertAll(Rates,r=>L.F("{0} a second",r)),fpsPick);
            height=Choice(Cell(grid,0,2),L.T("Height"),Array.ConvertAll(Heights,h=>L.F("{0} px",h)),heightPick);
            quality=Choice(Cell(grid,1,0),L.T("Picture quality"),new[]{L.T("High (bigger file)"),L.T("Normal"),L.T("Smaller file")},qualityPick);
            format=Choice(Cell(grid,1,2),L.T("File type"),new[]{L.T("GIF · moves everywhere · big file"),L.T("WebP · smooth colors, small · not everywhere"),L.T("MP4 · smallest, sharpest · goes up as a video")},formatPick);
            // What these mean, in plain words, and the place to choose what they start as.
            var explain=UI.Btn(L.T("What do these mean? (F1)"),()=>GuideWindow.Open(this,"gif"));
            explain.HorizontalAlignment=HorizontalAlignment.Left;explain.Margin=new Thickness(0,2,0,4);choices.Children.Add(explain);
            foreach(var box in new[]{fps,height,quality,format})box.SelectionChanged+=delegate {Changed();};
            bottom.Children.Add(more);
            var actions=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,12,0,0)};
            var cancel=UI.Btn(L.T("Cancel"),()=>DialogResult=false);cancel.IsCancel=true;
            make=UI.Btn(L.T(fromEditor?"Save JPG cover and make GIF cover":"Make"),Finish,true);
            make.IsDefault=true;make.Margin=new Thickness(6,0,0,0);
            foreach(var button in new[]{cancel,make}){button.FontSize=14;button.MinHeight=32;}
            actions.Children.Add(cancel);actions.Children.Add(make);bottom.Children.Add(actions);

            // Top: the preview in the cover's 9:16 shape. Click (or Space) pauses it.
            var previewBox=new Grid();
            previewFrame=new Border {Background=Brushes.Black,CornerRadius=new CornerRadius(8),HorizontalAlignment=HorizontalAlignment.Center,
                Child=preview,Cursor=Cursors.Hand,ClipToBounds=true};
            previewFrame.SizeChanged+=delegate {previewFrame.Width=previewFrame.ActualHeight*9/16;};
            previewFrame.MouseLeftButtonUp+=delegate {TogglePause();};
            AutomationProperties.SetName(previewFrame,L.T("Preview"));
            previewBox.Children.Add(previewFrame);
            if(source.Still!=null) {
                double stillTop,stillBottom;
                preview.Source=CoverImages.VisibleRows(source.Crop,source.Rotation,out stillTop,out stillBottom)?
                    CoverImages.WithoutBars(source.Still,stillTop,stillBottom):source.Still;
            }
            var pillBack=new SolidColorBrush(Color.FromArgb(0xB8,0x10,0x12,0x14));pillBack.Freeze();
            statusPill=new Border {Child=status,Background=pillBack,CornerRadius=new CornerRadius(8),Padding=new Thickness(10,4,10,5),
                HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Bottom,Margin=new Thickness(0,0,0,12),
                IsHitTestVisible=false,Visibility=Visibility.Collapsed};
            previewBox.Children.Add(statusPill);
            page.Children.Add(previewBox);

            strip.SizeChanged+=delegate {Layout();};
            strip.MouseLeftButtonDown+=StripDown;strip.MouseMove+=StripMove;strip.MouseLeftButtonUp+=StripUp;
            strip.LostMouseCapture+=delegate {if(dragging!=0)FinishDrag();};
            strip.MouseWheel+=(s,e)=>e.Handled=true; // the wheel does nothing here, so nothing moves by accident
            edgeScroll.Tick+=delegate {
                if(dragging!=1 && dragging!=2){edgeScroll.Stop();return;}
                viewStart=ClampView(viewStart+edgeDirection*span/40);
                double edge=edgeDirection<0?viewStart:viewStart+span;
                PlaceHandle(dragging,edge);
                Layout();UpdateChips();UpdateSummary();
            };
            prepareSoon.Tick+=delegate {
                prepareSoon.Stop();
                // The real file waits while a handle is held or the strip's scenes are still being read, so it
                // never slows down what the person is doing right now.
                if(dragging!=0 || draftFilling){prepareSoon.Start();return;}
                Prepare();
            };
            PreviewKeyDown+=(s,e)=>{
                if(e.Key==Key.F1){GuideWindow.Open(this,"gif");e.Handled=true;return;}
                if(e.OriginalSource is ComboBox || e.OriginalSource is ComboBoxItem)return;
                if(e.Key==Key.Space){if(!e.IsRepeat)TogglePause();e.Handled=true;return;}
                if(e.Key!=Key.Left && e.Key!=Key.Right)return;
                // ← → move the handle one scene (the scene step); Shift + ← → about a whole second.
                Nudge(activeHandle,(e.Key==Key.Left?-1:1)*(Keyboard.Modifiers==ModifierKeys.Shift?1000:Scenes.StepMs));e.Handled=true;
            };
            Closed+=delegate {
                lifetime.Cancel();player.Stop();edgeScroll.Stop();prepareSoon.Stop();
                foreach(var opening in new[]{roughPlayer,exactPlayer})
                    if(opening!=null)opening.ContinueWith(t=>{if(t.Status==TaskStatus.RanToCompletion && t.Result!=null)t.Result.Dispose();});
                // Preview files go away, except the one handed to Make.
                string keep=Value!=null?Value.Prepared:null;
                foreach(var file in previewFiles)if(file!=keep)MotionMaker.DeleteLater(file);
            };
            // Videos: two hidden players keep the file open. One fills the strip at once with keyframes,
            // the other replaces them with the exact pictures. Without libmpv, FFmpeg reads them instead.
            if(VideoFrames.Supports(source.Path) && MpvPlayer.Available) {
                roughPlayer=Task.Run(()=>MpvThumbnails.TryOpen(source.Path,lifetime.Token));
                exactPlayer=Task.Run(()=>MpvThumbnails.TryOpen(source.Path,lifetime.Token));
            }
            Loaded+=async delegate {
                UpdateChips();UpdateSummary();SetStatus();
                try {
                    var size=await Task.Run(()=>MotionMaker.PixelSize(source.Path,lifetime.Token));
                    coverPixels=source.Crop.Height*size.Height;
                } catch(Exception) {}
                try {
                    duration=await Task.Run(()=>MotionMaker.DurationOf(source.Path,lifetime.Token));
                    if(duration>0) {
                        // A clip shorter than the strip fills the strip instead of leaving it mostly empty.
                        if(duration<span){span=Math.Max(500,duration);Regrid(true);}
                        // A short clip is shown whole, so there's nothing more to drag to; say so instead of inviting a drag.
                        if(WholeClipShown)help.Text=NoticeLook.KeepWords(L.F("The whole clip ({0:0.0} s) is on the strip. Drag the two handles to choose the part.",duration/1000));
                        end=Math.Min(end,duration);viewStart=ClampView(viewStart);
                    }
                } catch(Exception) {}
                ReserveClockWidth();
                Layout();UpdateChips();UpdateSummary();SchedulePrepare();
            };
        }

        double Clamp(double ms) {return Math.Max(0,Double.IsNaN(duration) || duration<=0?ms:Math.Min(duration,ms));}
        double ClampView(double view) {
            if(!Double.IsNaN(duration) && duration>0)view=Math.Min(view,Math.Max(0,duration-span));
            return Math.Max(0,view);
        }
        // The shown part of the clip (viewStart to viewStart+span) fills the strip between the two edge rooms.
        double InnerWidth {get {return Math.Max(1,strip.ActualWidth-2*EdgeRoom);}}
        double PixelsPerMs {get {return strip.ActualWidth>0?InnerWidth/span:0;}}
        double RoomMs {get {return PixelsPerMs>0?EdgeRoom/PixelsPerMs:0;}}
        double X(double ms) {return EdgeRoom+(ms-viewStart)*PixelsPerMs;}
        double Ms(double x) {return viewStart+(x-EdgeRoom)/Math.Max(1e-9,PixelsPerMs);}

        // Redraws the strip: thumbnails on a fixed time grid, the dimmed outside, the cover mark, both handles and the play mark.
        void Layout() {
            strip.Children.Clear();
            if(strip.ActualWidth<=0)return;
            Regrid(false);
            TakeDraftPictures();
            strip.Children.Add(cells);
            // The pictures are only rebuilt when they or the shown part change, so dragging a handle stays light.
            string key=viewStart.ToString("R")+"|"+step.ToString("R")+"|"+strip.ActualWidth.ToString("R")+"|"+duration.ToString("R")+"|"+pictureVersion;
            if(key!=cellsKey){cellsKey=key;BuildCells();}
            PumpThumbs();
            Overlay();
        }
        readonly Canvas cells=new Canvas();
        string cellsKey;
        int pictureVersion; // goes up whenever a strip picture is added or the grid changes
        void BuildCells() {
            cells.Children.Clear();
            double cell=step*PixelsPerMs;
            bool known=!Double.IsNaN(duration) && duration>0;
            for(int k=VisibleFirst;k<=VisibleLast;k++) {
                double at=k*step;
                if(!Exists(k))break;
                // The last picture is cut where the clip ends, so the dark strip after it shows the end.
                double width=cell-ThumbGap;
                if(known)width=Math.Min(width,X(duration)-X(at));
                // A dark gray box stands in while a picture loads, so it never looks like the video ended.
                var placeholder=new Border {Width=Math.Max(1,width),Height=StripHeight,Background=LoadingCell,ClipToBounds=true};
                var image=new Image {Stretch=Stretch.UniformToFill,Width=Math.Max(1,cell-ThumbGap),Height=StripHeight,HorizontalAlignment=HorizontalAlignment.Left};
                RenderOptions.SetBitmapScalingMode(image,BitmapScalingMode.HighQuality);
                BitmapSource picture;
                if(thumbs.TryGetValue(k,out picture) || (picture=Earlier(k*step+step/2))!=null)image.Source=picture;
                placeholder.Child=image;
                Canvas.SetLeft(placeholder,X(at));cells.Children.Add(placeholder);
            }
        }
        // The dimmed outside, the chosen part, the cover mark, the play mark and both handles.
        void Overlay() {
            double left=X(start),right=X(end);
            var dimLeft=new Border {Width=Math.Max(0,left),Height=StripHeight,Background=Dim,IsHitTestVisible=false};
            var dimRight=new Border {Width=Math.Max(0,strip.ActualWidth-right),Height=StripHeight,Background=Dim,IsHitTestVisible=false};
            Canvas.SetLeft(dimRight,Math.Max(0,right));strip.Children.Add(dimLeft);strip.Children.Add(dimRight);
            var chosen=new Border {Width=Math.Max(0,right-left),Height=StripHeight,BorderThickness=new Thickness(0,3,0,3),IsHitTestVisible=false};
            chosen.SetResourceReference(Border.BorderBrushProperty,"Accent");Canvas.SetLeft(chosen,left);strip.Children.Add(chosen);
            var mark=new Polygon {Points=new PointCollection{new Point(-6,0),new Point(6,0),new Point(0,8)},Fill=Brushes.White,IsHitTestVisible=false};
            Canvas.SetLeft(mark,X(source.Moment));strip.Children.Add(mark);
            var markLine=new Border {Width=1.5,Height=StripHeight,Background=MarkLine,IsHitTestVisible=false};
            Canvas.SetLeft(markLine,X(source.Moment)-0.75);strip.Children.Add(markLine);
            strip.Children.Add(playMark);PlacePlayMark();
            foreach(int which in new[]{1,2}) {
                var handle=new Border {Width=12,Height=StripHeight,CornerRadius=new CornerRadius(which==1?6:0,which==1?0:6,which==1?0:6,which==1?6:0),
                    IsHitTestVisible=false};
                handle.SetResourceReference(Border.BackgroundProperty,"Accent");
                var grip=new Border {Width=2,Height=24,Background=Brushes.White,CornerRadius=new CornerRadius(1),Opacity=activeHandle==which?1:0.7,
                    HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
                handle.Child=grip;
                Canvas.SetLeft(handle,which==1?left-12:right);strip.Children.Add(handle);
            }
        }
        static readonly SolidColorBrush Dim=Frozen(Color.FromArgb(0xA0,0,0,0)),MarkLine=Frozen(Color.FromArgb(0xC0,0xFF,0xFF,0xFF));
        // Where the preview is in the original, as a thin line inside the chosen part.
        void ShowPlayMark(int index) {
            playAt=timeline!=null && index>=0 && index<timeline.Length?timeline[index]:Double.NaN;
            PlacePlayMark();
        }
        void PlacePlayMark() {
            bool show=!Double.IsNaN(playAt) && player.Playing && dragging==0 && playAt>=start-1 && playAt<=end+1 && strip.ActualWidth>0;
            playMark.Visibility=show?Visibility.Visible:Visibility.Collapsed;
            if(show)Canvas.SetLeft(playMark,X(playAt)-1);
        }

        static readonly SolidColorBrush LoadingCell=Frozen(Color.FromRgb(0x2B,0x2E,0x33));
        static SolidColorBrush Frozen(Color c){var b=new SolidColorBrush(c);b.Freeze();return b;}
        readonly HashSet<int> failed=new HashSet<int>(),exact=new HashSet<int>(),loadingExact=new HashSet<int>();
        Task<MpvThumbnails> roughPlayer,exactPlayer;
        bool playersFailed;
        int readers,exactReaders,generation;
        const int MaxReaders=4;
        bool UsePlayers {get {return roughPlayer!=null && !playersFailed;}}
        // A cell exists if it starts before the clip ends (1 ms of slack for rounding: a cell starting right at the end shows nothing).
        bool Exists(int k) {return k>=0 && (Double.IsNaN(duration) || duration<=0 || k*step<duration-1);}
        internal string LoadState {get {return "presses="+Presses+" moves="+Moves+" releases="+Releases+" dragging="+dragging+" captured="+strip.IsMouseCaptured+
            " pictures="+thumbs.Count+" exact="+exact.Count+" players="+UsePlayers+" view="+(viewStart/1000).ToString("0.0")+" span="+(span/1000).ToString("0.0");}}
        // Cells on the strip, including the ones partly in the edge rooms.
        int VisibleFirst {get {return Math.Max(0,(int)Math.Floor((viewStart-RoomMs)/step));}}
        int VisibleLast {get {return (int)Math.Ceiling((viewStart+span+RoomMs)/step);}}
        // Cells on screen with no picture yet / without the exact picture yet (for checks).
        internal int MissingThumbs {get {
            if(strip.ActualWidth<=0)return -1;
            int missing=0;
            for(int k=VisibleFirst;k<=VisibleLast;k++)if(Exists(k) && !thumbs.ContainsKey(k) && !failed.Contains(k))missing++;
            return missing;
        }}
        internal int MissingExact {get {
            if(strip.ActualWidth<=0)return -1;
            int missing=0;
            for(int k=VisibleFirst;k<=VisibleLast;k++)if(Exists(k) && !exact.Contains(k) && !failed.Contains(k))missing++;
            return missing;
        }}
        // The best next cell: on screen first, nearest the middle first, then one strip-width on each side in advance.
        int Pick(Func<int,bool> wanted) {
            double center=viewStart+span/2;
            int from=(int)Math.Floor((viewStart-span)/step),to=(int)Math.Ceiling((viewStart+2*span)/step);
            int best=-1;double bestScore=Double.MaxValue;
            for(int k=Math.Max(0,from);k<=to;k++) {
                if(!Exists(k) || failed.Contains(k) || !wanted(k))continue;
                double mid=k*step+step/2;
                bool onScreen=mid>=viewStart-step && mid<=viewStart+span+step;
                double score=(onScreen?0:1e12)+Math.Abs(mid-center);
                if(score<bestScore){bestScore=score;best=k;}
            }
            return best;
        }
        void PumpThumbs() {
            if(lifetime.IsCancellationRequested)return;
            if(UsePlayers) {
                if(readers==0) {
                    int k=Pick(x=>!thumbs.ContainsKey(x) && !loading.Contains(x));
                    if(k>=0){loading.Add(k);readers++;Load(k,false);}
                }
                // Exact pictures wait while the preview file is made or the strip's scenes are read (those
                // become the strip's pictures anyway); the quick keyframe pictures above still fill the strip at once.
                if(exactReaders==0 && preparing<0 && !draftFilling) {
                    int k=Pick(x=>!exact.Contains(x) && !loadingExact.Contains(x));
                    if(k>=0){loadingExact.Add(k);exactReaders++;Load(k,true);}
                }
                return;
            }
            while(readers<MaxReaders) {
                int k=Pick(x=>!thumbs.ContainsKey(x) && !loading.Contains(x));
                if(k<0)return;
                loading.Add(k);readers++;Load(k,true);
            }
        }
        // A new time grid for the strip when its width (or the clip's length) changes. The old pictures stay
        // as stand-ins for the nearest new cells until the new ones arrive, so resizing never flashes empty boxes.
        readonly Dictionary<double,BitmapSource> earlier=new Dictionary<double,BitmapSource>();
        void Regrid(bool force) {
            int count=strip.ActualWidth>0?Math.Max(6,(int)Math.Round(InnerWidth/CellWidth)):Thumbs;
            double next=span/count;
            if(!force && Math.Abs(next-step)<1e-6)return;
            if(earlier.Count>240)earlier.Clear();
            foreach(var pair in thumbs)earlier[pair.Key*step+step/2]=pair.Value;
            step=next;thumbs.Clear();loading.Clear();failed.Clear();exact.Clear();loadingExact.Clear();generation++;pictureVersion++;
        }
        BitmapSource Earlier(double at) {
            BitmapSource best=null;double bestGap=step;
            foreach(var pair in earlier){double gap=Math.Abs(pair.Key-at);if(gap<=bestGap){bestGap=gap;best=pair.Value;}}
            return best;
        }
        // The quick preview's scenes are already the cover's part in its 9:16 shape, so the strip uses them
        // instead of reading the same video a second time.
        void TakeDraftPictures() {
            if(draft==null)return;
            for(int k=VisibleFirst;k<=VisibleLast;k++) {
                if(!Exists(k) || exact.Contains(k))continue;
                var scene=draft.Near(k*step+step/2);
                if(scene==null)continue;
                thumbs[k]=scene;exact.Add(k);failed.Remove(k);pictureVersion++;
            }
        }
        // How big each scene has to be read so the cover's part of it is still sharp on the strip. The cover is
        // 9:16 in pixels, so its share of the width and of the height tells how big the whole picture must be.
        int PictureSide {get {
            var c=source.Crop;
            if(c.Width<=0 || c.Height<=0)return CellPixels;
            return (int)Math.Max(48,Math.Min(2400,Math.Ceiling(CellPixels*Math.Max(1/c.Height,9/(16*c.Width)))));
        }}
        // The cover's part of a scene with the cover's turn and brightness: the same cut as the preview above.
        BitmapSource CoverLook(BitmapSource scene) {
            return Pictures.RenderRotatedCover(scene,source.Crop,source.Rotation,(int)Math.Round(CellPixels*9/16.0),CellPixels,source.Brightness);
        }
        async void Load(int k,bool precise) {
            int started=generation; // pictures for an older strip grid are thrown away
            bool viaPlayer=UsePlayers;
            double at=k*step+step/2;int side=PictureSide;
            var token=lifetime.Token;
            BitmapSource picture=null;
            try {
                picture=await Task.Run(()=>{
                    if(viaPlayer) {
                        var hidden=(precise?exactPlayer:roughPlayer).Result;
                        if(hidden==null)return null;
                        return hidden.Read(at,side,token,precise);
                    }
                    return MotionMaker.Thumbnail(source.Path,at,side,token);
                },token);
            } catch(Exception) {}
            if(viaPlayer){if(precise)exactReaders--;else readers--;}else readers--;
            if(lifetime.IsCancellationRequested)return;
            if(viaPlayer && picture==null && (precise?exactPlayer:roughPlayer).IsCompleted && (precise?exactPlayer:roughPlayer).Result==null) {
                // The hidden player couldn't open this file (for example a rotated video): read with FFmpeg instead.
                playersFailed=true;loading.Clear();loadingExact.Clear();PumpThumbs();return;
            }
            if(started!=generation){PumpThumbs();return;}
            if(viaPlayer && precise)loadingExact.Remove(k);else loading.Remove(k);
            if(picture!=null)try {picture=CoverLook(picture);} catch(Exception) {}
            if(picture!=null) {
                // A keyframe picture never replaces an exact one.
                if(precise || !exact.Contains(k)){thumbs[k]=picture;pictureVersion++;}
                if(precise)exact.Add(k);
            } else if(!viaPlayer || precise)failed.Add(k);
            Layout();
        }

        internal int Presses,Moves,Releases; // for checks
        // Checks can supply the pointer position (a real cursor can't be moved for them); otherwise the mouse's.
        internal Func<Point> PointerForChecks;
        Point Pointer(MouseEventArgs e) {return PointerForChecks!=null?PointerForChecks():e.GetPosition(strip);}
        void StripDown(object sender,MouseButtonEventArgs e) {
            Presses++;
            PressStrip(Pointer(e).X);
            strip.CaptureMouse();e.Handled=true;
        }
        // Pointer logic in strip coordinates, so it can also be checked without a real mouse.
        internal double ViewStart {get {return viewStart;}}
        internal double SpanMs {get {return span;}}
        internal bool WholeClipShown {get {return !Double.IsNaN(duration) && duration>0 && duration<=span+1;}}
        internal void PressStrip(double x) {
            double left=X(start),right=X(end);
            if(Math.Abs(x-left)<=14 || (x<left && left-x<=16)){dragging=1;activeHandle=1;}
            else if(Math.Abs(x-right)<=14 || (x>right && x-right<=16)){dragging=2;activeHandle=2;}
            else {dragging=3;dragOrigin=x;dragView=viewStart;strip.Cursor=Cursors.SizeWE;}
            Layout();UpdateChips();
        }
        void StripMove(object sender,MouseEventArgs e) {Moves++;MoveStrip(Pointer(e).X);}
        internal void MoveStrip(double x) {
            if(dragging==0) {
                double left=X(start),right=X(end);
                strip.Cursor=Math.Abs(x-left)<=14 || Math.Abs(x-right)<=14?Cursors.SizeWE:Cursors.Hand;
                return;
            }
            if(dragging==3){viewStart=ClampView(dragView-(x-dragOrigin)/Math.Max(1e-9,PixelsPerMs));Layout();return;}
            PlaceHandle(dragging,Ms(x));
            // Near the strip's ends, the strip follows the handle so the range can reach past what's shown.
            double zone=EdgeRoom+12;
            edgeDirection=x<zone?-1:x>strip.ActualWidth-zone?1:0;
            if(edgeDirection!=0)edgeScroll.Start();else edgeScroll.Stop();
            Layout();UpdateChips();UpdateSummary();
        }
        void StripUp(object sender,MouseButtonEventArgs e) {Releases++;if(dragging!=0){strip.ReleaseMouseCapture();FinishDrag();}}
        internal void ReleaseStrip() {if(dragging!=0)FinishDrag();}
        void FinishDrag() {
            bool handle=dragging==1 || dragging==2;
            bool pan=dragging==3;
            dragging=0;edgeScroll.Stop();strip.Cursor=Cursors.Hand;
            PlacePlayMark();
            if(handle){UpdateChips();UpdateSummary();SchedulePrepare();}
            else if(pan)ShowDraft(draftOwed);
        }
        // Moves a handle by a small step (the ‹ › buttons and the ← → keys) and keeps it on screen.
        internal void Nudge(int which,double delta) {
            activeHandle=which;
            // Always at least one scene, so Shift + ← → (a second) still moves when a scene is longer than that.
            if(delta!=0 && Math.Abs(delta)<Scenes.StepMs)delta=Math.Sign(delta)*Scenes.StepMs;
            PlaceHandle(which,(which==1?start:end)+delta);
            double at=which==1?start:end;
            if(at<viewStart || at>viewStart+span)viewStart=ClampView(at-span/2);
            Layout();UpdateChips();UpdateSummary();SchedulePrepare();
        }

        // Handles sit on the scene grid that passes through the cover's moment, one scene step apart,
        // and the two handles stay at least one scene apart.
        void PlaceHandle(int which,double ms) {
            double gap=Math.Max(30,Scenes.StepMs);
            double at=Clamp(Scenes.Snap(ms,source.Moment));
            if(which==1)start=Math.Max(0,Math.Min(at,end-gap));else end=Clamp(Math.Max(at,start+gap));
        }

        // "Start ‹ 0:58.20 ›": clicking the chip picks which handle the arrow keys move.
        Border TimeChip(string label,int which,string earlier,string later,out TextBlock time) {
            var row=new StackPanel {Orientation=Orientation.Horizontal};
            var name=UI.Text(label,12,true);name.VerticalAlignment=VerticalAlignment.Center;name.Margin=new Thickness(10,0,2,0);
            row.Children.Add(name);
            row.Children.Add(NudgeButton("‹",which,-1,earlier));
            time=UI.Text("",13);time.VerticalAlignment=VerticalAlignment.Center;time.TextAlignment=TextAlignment.Center;
            row.Children.Add(time);
            row.Children.Add(NudgeButton("›",which,1,later));
            var chip=new Border {Child=row,CornerRadius=new CornerRadius(8),BorderThickness=new Thickness(1),Padding=new Thickness(0,1,2,1),Cursor=Cursors.Hand};
            chip.SetResourceReference(Border.BackgroundProperty,"Panel");
            chip.MouseLeftButtonDown+=(s,e)=>{activeHandle=which;Layout();UpdateChips();};
            AutomationProperties.SetName(chip,label);
            return chip;
        }
        Button NudgeButton(string glyph,int which,int direction,string tip) {
            var button=new Button {Content=glyph,Padding=new Thickness(8,0,8,2),FontSize=16,BorderThickness=new Thickness(0),
                Background=Brushes.Transparent,ToolTip=tip,Focusable=false,VerticalAlignment=VerticalAlignment.Center};
            AutomationProperties.SetName(button,tip);
            button.Click+=delegate {Nudge(which,direction*Scenes.StepMs);};
            return button;
        }
        static TextBlock RowLabel(string text,double left) {
            var label=UI.Text(text,12,true);label.VerticalAlignment=VerticalAlignment.Center;label.Margin=new Thickness(left,0,8,0);
            return label;
        }
        // A row of choices that look like one control; the picked one is raised.
        static Border Segment(string[] labels,int selected,string name,Action changed,out RadioButton[] buttons) {
            var row=new StackPanel {Orientation=Orientation.Horizontal};
            string group="segment-"+Guid.NewGuid().ToString("N");
            var made=new RadioButton[labels.Length];
            for(int i=0;i<labels.Length;i++) {
                var button=new RadioButton {Content=labels[i],GroupName=group,IsChecked=i==selected,Style=(Style)Application.Current.FindResource("SegmentChoice")};
                button.Checked+=delegate {changed();};
                made[i]=button;row.Children.Add(button);
            }
            buttons=made;
            var box=new Border {Child=row,CornerRadius=new CornerRadius(8),BorderThickness=new Thickness(1),Padding=new Thickness(2),VerticalAlignment=VerticalAlignment.Center};
            box.SetResourceReference(Border.BackgroundProperty,"Panel");box.SetResourceReference(Border.BorderBrushProperty,"Line");
            AutomationProperties.SetName(box,name);
            return box;
        }
        static int Picked(RadioButton[] buttons) {for(int i=0;i<buttons.Length;i++)if(buttons[i].IsChecked==true)return i;return 0;}
        static string Clock(double ms) {
            // Hundredths, like the seek bar: a scene can be as short as 0.03 s.
            long hundredths=(long)Math.Round(Math.Max(0,ms)/10);
            long seconds=hundredths/100,minutes=seconds/60;
            if(minutes>=60)return String.Format(CultureInfo.InvariantCulture,"{0}:{1:00}:{2:00}.{3:00}",minutes/60,minutes%60,seconds%60,hundredths%100);
            return String.Format(CultureInfo.InvariantCulture,"{0}:{1:00}.{2:00}",minutes,seconds%60,hundredths%100);
        }
        // Room for the longest time this file can show, so the ‹ › buttons never shift while the numbers change.
        void ReserveClockWidth() {
            string widest=Clock(Double.IsNaN(duration) || duration<=0?599000:duration);
            var chars=widest.ToCharArray();for(int i=0;i<chars.Length;i++)if(Char.IsDigit(chars[i]))chars[i]='0';
            foreach(var time in new[]{startTime,endTime}) {
                var probe=new TextBlock {Text=new string(chars),FontSize=time.FontSize,FontFamily=time.FontFamily};
                System.Windows.Documents.Typography.SetNumeralAlignment(probe,FontNumeralAlignment.Tabular);
                probe.Measure(new Size(Double.PositiveInfinity,Double.PositiveInfinity));
                time.MinWidth=Math.Ceiling(probe.DesiredSize.Width)+2;
            }
        }
        void UpdateChips() {
            startTime.Text=Clock(start);endTime.Text=Clock(end);
            startChip.SetResourceReference(Border.BorderBrushProperty,activeHandle==1?"Accent":"Line");
            endChip.SetResourceReference(Border.BorderBrushProperty,activeHandle==2?"Accent":"Line");
        }

        void Changed() {UpdateSummary();SchedulePrepare();}
        MotionRequest Read() {
            return new MotionRequest {Before=(source.Moment-start)/1000,After=(end-source.Moment)/1000,Fps=Rates[fps.SelectedIndex],
                Height=Heights[height.SelectedIndex],Format=Formats[format.SelectedIndex],Quality=quality.SelectedIndex,
                Speed=Speeds[Picked(speedChoices)],Loop=Loops[Picked(loopChoices)]};
        }
        bool PreparedFor(MotionRequest request) {
            return preparedFile!=null && preparedKey==MotionMaker.Key(source,request) && File.Exists(preparedFile);
        }
        void Finish() {
            var request=Read();
            if(PreparedFor(request)) {
                request.Prepared=preparedFile;request.PreparedKey=preparedKey;
                request.PreparedBefore=preparedResult.Before;request.PreparedAfter=preparedResult.After;
            }
            lastFps=fps.SelectedIndex;lastHeight=height.SelectedIndex;lastQuality=quality.SelectedIndex;lastFormat=format.SelectedIndex;
            Value=request;DialogResult=true;
        }
        void UpdateSummary() {
            var request=Read();
            double mb;string size;
            double seconds=request.PlaySeconds;int frames=request.Frames;
            if(PreparedFor(request)) {
                mb=preparedBytes/1048576.0;size=mb<1?L.F("{0} KB",Math.Max(1,Math.Round(preparedBytes/1024.0)).ToString("N0",CultureInfo.CurrentCulture)):L.F("{0} MB",Mb(mb));
                // The real picture size: a small original can't fill the height picked in More choices.
                if(preparedResult!=null && preparedResult.Width>0)size=preparedResult.Width+"×"+preparedResult.Height+" · "+size;
                if(preparedFrames>0){frames=preparedFrames;seconds=preparedSeconds;}
            } else {mb=Estimate(request,coverPixels);size=mb<1?L.F("about {0} KB",Math.Max(1,Math.Round(mb*1024/10)*10).ToString("N0",CultureInfo.CurrentCulture)):L.F("about {0} MB",Mb(mb));}
            summary.Text=L.F("{0:0.0} s · {1:N0} frames · {2}",seconds,frames,size);
            // Very large files are only pointed out, never blocked.
            if(mb>=50)summary.Foreground=new SolidColorBrush(NoticeLook.Amber);
            else summary.SetResourceReference(TextBlock.ForegroundProperty,"Subtle");
        }
        static string Mb(double mb) {return mb.ToString(mb>=10?"0":"0.0",CultureInfo.CurrentCulture);}
        // A guide until the real file is made, from sizes measured on real videos (bytes per pixel per frame).
        internal static double Estimate(MotionRequest r,double coverPixels=0) {
            double high=coverPixels>2?Math.Min(r.Height,coverPixels):r.Height;
            double pixels=Math.Round(high*9/16.0)*high*(double)r.Frames;
            double perPixel=r.Format=="gif"?new[]{0.52,0.44,0.33}[r.Quality]:r.Format=="webp"?new[]{0.07,0.05,0.03}[r.Quality]:new[]{0.05,0.035,0.02}[r.Quality];
            return Math.Max(0.01,pixels*perPixel/1048576);
        }

        // The little label over the preview: progress while the file is made, "paused", or a problem.
        void SetStatus() {
            string text=problem!=null?problem:preparing>=0 && !draftOn?L.F("Making the preview {0}%",(int)Math.Floor(preparing*100)):held?L.T("Paused · click to play"):draftOn?L.T("Quick preview"):null;
            status.Text=text??"";
            statusPill.Visibility=text==null?Visibility.Collapsed:Visibility.Visible;
        }
        internal void TogglePause() {
            held=!held;player.Hold=held;SetStatus();
        }
        internal bool PreviewReady {get {return preparedFile!=null && preparing<0 && player.Playing;}}
        internal string PreparedFile {get {return preparedFile;}}
        internal int PreviewFrames {get {return timeline!=null?timeline.Length:0;}}
        void SchedulePrepare() {
            prepareSoon.Stop();
            // A real file being made for the old choices is out of date now.
            if(prepareWork!=null)prepareWork.Cancel();
            if(preparing>=0){preparing=-1;SetStatus();}
            prepareSoon.Start();
            ShowDraft();
        }
        // The quick preview: scenes already read are reused, only new ones are read, and it plays as soon as they are there.
        // Then every scene of the strip on screen is read too, so the next change inside it plays at once
        // and the strip shows those same scenes. play=false only reads the strip (after panning).
        bool draftFilling,draftOwed;
        async void ShowDraft(bool play=true) {
            if(lifetime.IsCancellationRequested || !IsLoaded)return;
            if(draftWork!=null)draftWork.Cancel();
            var work=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);draftWork=work;
            double from=start,to=end;
            var request=Read();
            draftFilling=true;
            if(play)draftOwed=true;
            try {
                if(draft==null)draft=new DraftScenes(source,PreviewStepMs,Dispatcher,PreviewSide());
                var scenes=draft;
                if(play) {
                    await Task.Run(()=>scenes.Fill(from,to,work.Token,true),work.Token);
                    if(work.IsCancellationRequested)return;
                    var list=scenes.Scenes(from,to);
                    double rowsTop,rowsBottom;
                    if(CoverImages.VisibleRows(source.Crop,source.Rotation,out rowsTop,out rowsBottom))
                        list=list.ConvertAll(scene=>CoverImages.WithoutBars(scene,rowsTop,rowsBottom));
                    if(list.Count>0) {
                        var frames=ArrangeWith(list,(int)Math.Max(20,Math.Round(scenes.PartStep(from,to)/request.Speed)),request);
                        timeline=Timeline(request,frames.Images.Length);
                        player.Start(frames);player.Hold=held;
                        draftOn=true;SetStatus();
                    }
                    draftOwed=false;
                    Layout();
                }
                double a=Clamp(viewStart-RoomMs),b=Clamp(viewStart+span+RoomMs);
                await Task.Run(()=>scenes.Fill(a,b,work.Token),work.Token);
                if(work.IsCancellationRequested)return;
                Layout();
            } catch(OperationCanceledException) {
            } catch(Exception) {
                // The real file still follows, so a failed quick preview is not shown as a problem.
                if(draftWork==work)draftOwed=false;
            } finally {
                if(draftWork==work){draftFilling=false;PumpThumbs();}
            }
        }
        // Makes the real file in the background. Until it's done the last preview keeps playing (or, the first
        // time, the frames show as they're made); then the file's own frames play and its exact size is shown.
        async void Prepare() {
            prepareSoon.Stop();
            if(lifetime.IsCancellationRequested || !IsLoaded)return;
            var request=Read();
            string key=MotionMaker.Key(source,request);
            if(key==preparedKey && preparedFile!=null && File.Exists(preparedFile)){draftOn=false;SetStatus();UpdateSummary();return;}
            if(prepareWork!=null)prepareWork.Cancel();
            var work=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);prepareWork=work;
            string file=MotionMaker.PreviewFile(request.Extension);previewFiles.Add(file);
            int side=PreviewSide();
            var made=new List<BitmapSource>();int madeRate=request.Fps;
            problem=null;preparing=0;SetStatus();
            try {
                var result=await Task.Run(()=>MotionMaker.Encode(source,request,file,work.Token,
                    p=>Dispatcher.BeginInvoke(new Action(()=>{if(prepareWork==work && preparing>=0 && p<1){preparing=p;SetStatus();}})),
                    (frame,rate)=>{
                        lock(made)made.Add(frame);
                        madeRate=rate;
                        Dispatcher.BeginInvoke(new Action(()=>{if(prepareWork==work && !player.Playing)preview.Source=frame;}));
                    },side),work.Token);
                if(work.IsCancellationRequested){MotionMaker.DeleteLater(file);return;}
                // The finished file's own frames show its real colors and timing.
                MotionFrames frames=null;
                if(request.Frames<=ExactPreviewLimit) {
                    try {frames=await Task.Run(()=>MotionFrames.Read(file,side,work.Token),work.Token);}
                    catch(OperationCanceledException) {throw;}
                    catch(Exception) {}
                }
                if(work.IsCancellationRequested){MotionMaker.DeleteLater(file);return;}
                bool own=frames!=null && frames.Images.Length>0;
                if(!own)lock(made)frames=Arrange(made,madeRate,request);
                string old=preparedFile;
                draftOn=false;
                preparedFile=file;preparedKey=key;preparedResult=result;preparedBytes=new FileInfo(file).Length;
                preparedFrames=own?frames.Images.Length:-1;
                preparedSeconds=0;foreach(int delay in frames.Delays)preparedSeconds+=delay/1000.0;
                if(old!=null && old!=file){MotionMaker.DeleteLater(old);previewFiles.Remove(old);}
                timeline=Timeline(request,frames.Images.Length);
                player.Start(frames);player.Hold=held;
                preparing=-1;SetStatus();UpdateSummary();PumpThumbs();
            } catch(OperationCanceledException) {MotionMaker.DeleteLater(file);}
            catch(Exception ex) {
                MotionMaker.DeleteLater(file);
                if(!work.IsCancellationRequested){preparing=-1;problem=ex.Message;SetStatus();PumpThumbs();}
            }
        }
        // The frames made on the way go forward only; play them the way the file will.
        static MotionFrames Arrange(List<BitmapSource> forward,int rate,MotionRequest r) {
            if(forward.Count==0)throw new InvalidDataException(L.T("There are no frames to display."));
            return ArrangeWith(forward,(int)Math.Round(1000.0/Math.Max(1,rate)),r);
        }
        // The same, with the time each picture stays in milliseconds.
        static MotionFrames ArrangeWith(List<BitmapSource> forward,int delay,MotionRequest r) {
            if(forward.Count==0)throw new InvalidDataException(L.T("There are no frames to display."));
            var order=new List<BitmapSource>(forward);
            if(r.Loop=="reverse")order.Reverse();
            else if(r.Loop=="bounce" && forward.Count>=3)for(int i=forward.Count-2;i>=1;i--)order.Add(forward[i]);
            var frames=new MotionFrames {Images=order.ToArray(),Delays=new int[order.Count]};
            for(int i=0;i<order.Count;i++)frames.Delays[i]=delay;
            return frames;
        }
        // Which moment of the original each preview frame shows.
        double[] Timeline(MotionRequest r,int count) {
            var times=new double[count];
            bool bounce=r.Loop=="bounce" && count>=4;
            int forward=bounce?(count+2)/2:count;
            double from=Math.Max(0,source.Moment-r.Before*1000),to=source.Moment+r.After*1000;
            if(duration>0)to=Math.Min(to,duration);
            double each=(to-from)/Math.Max(1,forward);
            for(int i=0;i<count;i++) {
                int j=i;
                if(r.Loop=="reverse")j=count-1-i;
                else if(bounce && i>=forward)j=2*forward-2-i;
                times[i]=Math.Min(to,from+(j+0.5)*each);
            }
            return times;
        }
        // The preview's height in screen pixels, so it's sharp without wasting memory.
        int PreviewSide() {
            var presentation=PresentationSource.FromVisual(this);
            double scale=presentation!=null && presentation.CompositionTarget!=null?presentation.CompositionTarget.TransformToDevice.M22:1;
            double shown=previewFrame.ActualHeight>0?previewFrame.ActualHeight:480;
            return Math.Max(240,(int)Math.Round(shown*scale));
        }
        static Panel Cell(Grid grid,int row,int column) {
            var cell=new StackPanel();Grid.SetRow(cell,row);Grid.SetColumn(cell,column);grid.Children.Add(cell);return cell;
        }
        static ComboBox Choice(Panel panel,string label,string[] options,int index) {
            panel.Children.Add(UI.Text(label,13));
            var combo=new ComboBox {ItemsSource=options,SelectedIndex=Math.Max(0,Math.Min(options.Length-1,index)),Margin=new Thickness(0,4,0,8),
                Style=(Style)Application.Current.FindResource("OptionsChoice")};
            AutomationProperties.SetName(combo,label);panel.Children.Add(combo);return combo;
        }
        // For checks: choose by position as a person would by clicking.
        internal void ChooseForChecks(int speed,int loop) {
            if(speed>=0)speedChoices[speed].IsChecked=true;
            if(loop>=0)loopChoices[loop].IsChecked=true;
        }
        internal void ChooseMoreForChecks(int fpsIndex,int heightIndex,int qualityIndex,int formatIndex) {
            if(fpsIndex>=0)fps.SelectedIndex=fpsIndex;
            if(heightIndex>=0)height.SelectedIndex=heightIndex;
            if(qualityIndex>=0)quality.SelectedIndex=qualityIndex;
            if(formatIndex>=0)format.SelectedIndex=formatIndex;
        }
        internal double RangeStart {get {return start;}}
        internal double RangeEnd {get {return end;}}
        internal string SummaryText {get {return summary.Text;}}
        internal string StatusText {get {return statusPill.Visibility==Visibility.Visible?status.Text:"";}}
        internal double PlayAt {get {return playMark.Visibility==Visibility.Visible?playAt:Double.NaN;}}
    }

    // Decoded frames of a GIF, WebP or MP4 cover, kept in a small shared cache.
    internal sealed class MotionFrames {
        public BitmapSource[] Images;
        public int[] Delays;
        long Bytes;
        sealed class Entry {public string Key;public MotionFrames Frames;}
        static readonly LinkedList<Entry> cache=new LinkedList<Entry>();
        const long CacheLimit=220L*1024*1024;
        const int MaxFrames=300;

        internal static Task<MotionFrames> LoadAsync(string path,int maxSide,CancellationToken token) {
            return Task.Run(()=>Load(path,maxSide,token),token);
        }
        // A saved GIF cover as it should look: one made before covers were trimmed shows without its empty rows.
        // The file itself is not changed.
        internal static Task<MotionFrames> LoadCoverAsync(Photo photo,int maxSide,CancellationToken token) {
            string path=photo.MotionFile;Rect crop=photo.Crop;double rotation=photo.Rotation;
            return Task.Run(()=>{
                var frames=Load(path,maxSide,token);
                double top,bottom;
                if(!CoverImages.VisibleRows(crop,rotation,out top,out bottom))return frames;
                return new MotionFrames {Images=Array.ConvertAll(frames.Images,image=>CoverImages.WithoutBars(image,top,bottom)),Delays=frames.Delays};
            },token);
        }
        static MotionFrames Load(string path,int maxSide,CancellationToken token) {
            var info=new FileInfo(path);
            string key=path+"|"+info.Length+"|"+info.LastWriteTimeUtc.Ticks+"|"+maxSide;
            lock(cache) {
                for(var node=cache.First;node!=null;node=node.Next)
                    if(node.Value.Key==key){cache.Remove(node);cache.AddFirst(node);return node.Value.Frames;}
            }
            var frames=Read(path,maxSide,token);
            lock(cache) {
                cache.AddFirst(new Entry {Key=key,Frames=frames});
                long total=0;
                for(var node=cache.First;node!=null;) {
                    var following=node.Next;total+=node.Value.Frames.Bytes;
                    if(total>CacheLimit && node!=cache.First)cache.Remove(node);
                    node=following;
                }
            }
            return frames;
        }
        // Every frame (up to the limit) without keeping them in the shared cache.
        internal static MotionFrames Read(string path,int maxSide,CancellationToken token) {
            var frames=new MotionFrames();
            using(var file=new AnimationFrames(path,token)) {
                int count=Math.Min(MaxFrames,file.Count);
                frames.Images=new BitmapSource[count];frames.Delays=new int[count];
                for(int i=0;i<count;i++) {
                    token.ThrowIfCancellationRequested();
                    frames.Images[i]=file.Read(i,maxSide);
                    double end=i+1<file.Times.Count?file.Times.Start(i+1):file.Times.Duration;
                    frames.Delays[i]=Math.Max(20,(int)Math.Round(end-file.Times.Start(i)));
                    frames.Bytes+=(long)frames.Images[i].PixelWidth*frames.Images[i].PixelHeight*4;
                }
            }
            return frames;
        }
    }

    // Plays decoded frames into an Image on the UI thread. Pauses while its own window is minimized
    // (each large view is a window of its own) or Hold is set (the person paused it).
    internal sealed class FramePlayer {
        internal bool Hold;
        internal event Action<int> Shown;
        readonly Image target;
        readonly DispatcherTimer timer=new DispatcherTimer(DispatcherPriority.Render);
        MotionFrames frames;
        int next;
        ImageSource still;
        internal FramePlayer(Image target) {
            this.target=target;
            timer.Tick+=delegate {
                if(frames==null)return;
                if(!Hold && !Minimized())Show(next);
                else timer.Interval=TimeSpan.FromMilliseconds(250);
            };
        }
        bool Minimized() {
            var window=Window.GetWindow(target);
            return window!=null && window.WindowState==WindowState.Minimized;
        }
        void Show(int index) {
            target.Source=frames.Images[index];timer.Interval=TimeSpan.FromMilliseconds(frames.Delays[index]);
            next=(index+1)%frames.Images.Length;
            if(Shown!=null)Shown(index);
        }
        internal bool Playing {get {return frames!=null;}}
        internal void Start(MotionFrames value) {
            if(value==null || value.Images.Length==0)return;
            if(frames==null)still=target.Source;
            frames=value;Show(0);timer.Start();
        }
        internal void Stop() {
            timer.Stop();
            if(frames!=null){frames=null;target.Source=still;}
        }
    }

    // The quick preview of Make GIF cover. It reads scenes at a fixed step (0.1 s by default), already in the cover's
    // look and small (smaller still for big videos), and keeps them. Moving a handle only reads the scenes that are
    // new; shortening the part reads nothing. Speed matters more here than exact frames or quality.
    internal sealed class DraftScenes {
        readonly MotionSource source;
        readonly Dispatcher dispatcher;
        readonly int previewSide;
        readonly Dictionary<long,BitmapSource> scenes=new Dictionary<long,BitmapSource>();
        readonly object gate=new object();
        const int MaxScenes=400;
        // Milliseconds between scenes. For a video it is never smaller than the time between its own pictures.
        internal long Step {get;private set;}
        int width,height,drawSide;
        bool sized;

        internal DraftScenes(MotionSource source,long stepMs,Dispatcher dispatcher,int previewSide) {
            this.source=source;this.dispatcher=dispatcher;this.previewSide=previewSide;
            Step=Math.Max(30,Math.Min(2000,stepMs));
        }
        bool Has(long slot) {lock(gate)return scenes.ContainsKey(slot);}
        long First(double from) {return (long)Math.Ceiling(Math.Max(0,from)/Step-0.001);}
        long Last(double to) {return (long)Math.Floor(Math.Max(0,to)/Step+0.001);}
        // A long part reads every 2nd, 3rd... scene so that it never needs more than MaxScenes pictures.
        static long Stride(long first,long last) {return (last-first)/MaxScenes+1;}
        static long Align(long slot,long stride) {return (slot+stride-1)/stride*stride;}
        long keepFirst=0,keepLast=-1; // the part now playing: its pictures are not trimmed for the strip

        // The real time between the scenes of this part.
        internal long PartStep(double from,double to) {
            long first=First(from);
            return Step*Stride(first,Math.Max(first,Last(to)));
        }
        // Reads whatever scenes of the part are not kept yet.
        internal void Fill(double from,double to,CancellationToken token,bool playing=false) {
            if(VideoFrames.Supports(source.Path))MeasureVideo(token);
            long first=First(from),last=Math.Max(first,Last(to));
            long stride=Stride(first,last);
            if(playing)lock(gate){keepFirst=first;keepLast=last;}
            long run=-1;
            for(long slot=Align(first,stride);slot<=last+stride;slot+=stride) {
                bool missing=slot<=last && !Has(slot);
                if(missing && run<0)run=slot;
                // Long parts are read a few seconds at a time, so a change that stops the reading loses little.
                if(missing && run>=0 && (slot-run)/stride>=ChunkSlots(stride)){token.ThrowIfCancellationRequested();ReadRun(run,slot-stride,stride,token);run=slot;}
                if(!missing && run>=0){token.ThrowIfCancellationRequested();ReadRun(run,slot-stride,stride,token);run=-1;}
            }
            Trim(first,last,stride);
        }
        void MeasureVideo(CancellationToken token) {
            if(sized)return;
            var size=VideoFrames.ReadOrientation(source.Path,token);
            width=size.Width;height=size.Height;
            if(Math.Abs(size.Angle%180)==90){int swap=width;width=height;height=swap;}
            if(width<=0 || height<=0)throw new InvalidDataException(L.T("Could not read the frames."));
            // The bigger the original, the smaller the quick preview.
            drawSide=VideoFrames.IsHighResolution(width,height)?Math.Min(previewSide,270):Math.Min(previewSide,400);
            double gap=VideoFrames.FrameGapMs(source.Path,token);
            if(gap>Step)Step=(long)Math.Ceiling(gap);
            sized=true;
        }
        void ReadRun(long a,long b,long stride,CancellationToken token) {
            if(VideoFrames.Supports(source.Path)) {
                MeasureVideo(token);
                var request=new MotionRequest {Height=drawSide,Fps=15,Speed=1,Format="gif",Loop="forward"};
                int outW,outH;
                string chain=MotionMaker.Filters(source.Crop,source.Rotation,source.Brightness,width,height,request,false,out outW,out outH,false);
                long gap=Step*stride;
                string rate=(1000.0/gap).ToString("0.######",CultureInfo.InvariantCulture);
                string before="-ss "+VideoFrames.Seconds(a*Step)+" -t "+(((b-a)/stride+1)*gap/1000.0).ToString("0.######",CultureInfo.InvariantCulture)+" ";
                var pictures=VideoFrames.Pictures(before,source.Path,"-vf "+VideoFrames.Quote("fps="+rate+","+chain),token);
                lock(gate)for(int i=0;i<pictures.Count && a+i*stride<=b;i++)scenes[a+i*stride]=pictures[i];
                return;
            }
            using(var file=new AnimationFrames(source.Path,token)) {
                for(long slot=a;slot<=b;slot+=stride) {
                    token.ThrowIfCancellationRequested();
                    var raw=file.Read(Math.Min(file.Count-1,file.Times.At(slot*(double)Step)),0);
                    double scale=Math.Min(1,(double)Math.Min(previewSide,400)/Math.Max(1,source.Crop.Height*raw.PixelHeight));
                    int h=Math.Max(2,(int)Math.Round(source.Crop.Height*raw.PixelHeight*scale)),w=Math.Max(2,(int)Math.Round(h*9/16.0));
                    // Drawing needs the UI thread.
                    var cover=(BitmapSource)dispatcher.Invoke(new Func<BitmapSource>(()=>Pictures.RenderRotatedCover(raw,source.Crop,source.Rotation,w,h,source.Brightness)));
                    lock(gate)scenes[slot]=cover;
                }
            }
        }
        // Keeps the memory small: the scenes farthest from the part go first.
        void Trim(long first,long last,long stride) {
            lock(gate) {
                if(scenes.Count<=MaxScenes)return;
                var slots=new List<long>(scenes.Keys);
                double middle=(first+last)/2.0;
                slots.Sort((x,y)=>Math.Abs(y-middle).CompareTo(Math.Abs(x-middle)));
                for(int i=0;i<slots.Count && scenes.Count>MaxScenes;i++)
                    if((slots[i]<first || slots[i]>last) && !(slots[i]>=keepFirst && slots[i]<=keepLast))scenes.Remove(slots[i]);
                // Closer scenes left from a shorter part go next.
                for(int i=0;i<slots.Count && scenes.Count>MaxScenes;i++)
                    if(slots[i]%stride!=0 && !(slots[i]>=keepFirst && slots[i]<=keepLast))scenes.Remove(slots[i]);
            }
        }
        long ChunkSlots(long stride) {return Math.Max(4,3000/(Step*stride));}
        // The scene nearest a moment, if it has been read.
        internal BitmapSource Near(double ms) {
            long slot=(long)Math.Round(Math.Max(0,ms)/Step);
            BitmapSource scene;
            lock(gate)return scenes.TryGetValue(slot,out scene)?scene:null;
        }
        // The scenes of the part, in time order.
        internal List<BitmapSource> Scenes(double from,double to) {
            long first=First(from),last=Math.Max(first,Last(to)),stride=Stride(first,last);
            var list=new List<BitmapSource>();
            lock(gate)for(long slot=Align(first,stride);slot<=last;slot+=stride){BitmapSource scene;if(scenes.TryGetValue(slot,out scene))list.Add(scene);}
            return list;
        }
    }

    // The finished file, and how much time was really available before and after the cover.
    internal sealed class MotionResult {
        public string File;
        public double Before,After;
        public int Width,Height;
    }

    internal static class MotionMaker {
        // Videos go straight to FFmpeg. GIF and WebP frames are read by Windows (FFmpeg can't read
        // animated WebP) and handed to FFmpeg through a temporary file.
        internal static bool Supports(string path) {return AnimationFrames.Supports(path);}
        // The preview copies made while frames are read: up to this many, fewer per second for long GIFs.
        const int PreviewCopyLimit=300;

        // Where the cover's moment is, in milliseconds from the beginning of the file.
        internal static double MomentOf(Photo photo,string original) {
            if(photo.FrameTime.HasValue)return photo.FrameTime.Value;
            if(photo.FrameIndex<=0)return 0;
            using(var file=new AnimationFrames(original))return file.Times.Start(Math.Min(photo.FrameIndex,file.Count-1));
        }

        // Width and height of the picture as it's shown (turned videos upright).
        internal static Size PixelSize(string path,CancellationToken token) {
            if(VideoFrames.Supports(path)) {
                var size=VideoFrames.ReadOrientation(path,token);
                return Math.Abs(size.Angle%180)==90?new Size(size.Height,size.Width):new Size(size.Width,size.Height);
            }
            using(var file=new AnimationFrames(path,token)){var first=file.Read(0,0);return new Size(first.PixelWidth,first.PixelHeight);}
        }
        internal static double DurationOf(string path,CancellationToken token) {
            if(VideoFrames.Supports(path))return VideoFrames.DurationMs(path,token);
            using(var file=new AnimationFrames(path,token))return file.Times.Duration;
        }
        // A quick, rough picture near ms for the strip: videos seek to the nearest keyframe.
        internal static BitmapSource Thumbnail(string path,double ms,int maxSide,CancellationToken token) {
            if(VideoFrames.Supports(path)) {
                string seek="-noaccurate_seek -ss "+VideoFrames.Seconds(Math.Max(0,ms))+" ";
                string after="-frames:v 1 -vf "+VideoFrames.Quote("scale=w='min(iw,"+maxSide+")':h='min(ih,"+maxSide+")':force_original_aspect_ratio=decrease");
                // Probing a big 4K file takes most of the time; a short probe is enough for a rough picture (0.3 s instead of 2 s).
                var frames=VideoFrames.Pictures("-probesize 256K -analyzeduration 0 "+seek,path,after,token);
                if(frames.Count==0)frames=VideoFrames.Pictures(seek,path,after,token);
                return frames.Count>0?frames[0]:null;
            }
            using(var file=new AnimationFrames(path,token))return file.Read(Math.Min(file.Count-1,file.Times.At(ms)),maxSide);
        }
        static int Even(double value) {return Math.Max(2,(int)Math.Round(value/2)*2);}
        static string N(double value) {return value.ToString("0.######",CultureInfo.InvariantCulture);}

        // The same original, moment, look and choices give the same key: the window's file is reused only then.
        internal static string Key(MotionSource s,MotionRequest r) {
            var c=CultureInfo.InvariantCulture;
            return String.Join("|",new[]{(s.Path??"").ToLowerInvariant(),Math.Round(s.Moment).ToString(c),
                Math.Round(s.Crop.X,4).ToString(c),Math.Round(s.Crop.Y,4).ToString(c),Math.Round(s.Crop.Width,4).ToString(c),Math.Round(s.Crop.Height,4).ToString(c),
                Math.Round(s.Rotation,2).ToString(c),s.Brightness.ToString(c),Math.Round(r.Before*1000).ToString(c),Math.Round(r.After*1000).ToString(c),
                r.Fps.ToString(c),r.Height.ToString(c),r.Format,r.Speed.ToString(c),r.Loop,r.Quality.ToString(c),"trimmed"});
        }
        static string PreviewFolder {get {return Path.Combine(Path.GetTempPath(),"LoveMyShelf","GIF preview");}}
        internal static string PreviewFile(string extension) {
            Directory.CreateDirectory(PreviewFolder);
            return Path.Combine(PreviewFolder,Guid.NewGuid().ToString("N")+extension);
        }
        // Deletes a preview file; FFmpeg may still hold it for a moment after being stopped, so it retries briefly.
        internal static void DeleteLater(string path) {
            if(path==null)return;
            Task.Run(async ()=>{
                for(int i=0;i<20;i++) {
                    try {if(File.Exists(path))File.Delete(path);return;}
                    catch(IOException) {}
                    catch(UnauthorizedAccessException) {}
                    await Task.Delay(250);
                }
            });
        }
        // Leftovers from a window that couldn't clean up (for example when LoveMyShelf was ended suddenly).
        internal static void SweepPreviews() {
            try {
                if(!Directory.Exists(PreviewFolder))return;
                foreach(var file in Directory.GetFiles(PreviewFolder))
                    if(File.GetLastWriteTimeUtc(file)<DateTime.UtcNow.AddHours(-6))try {File.Delete(file);} catch(Exception) {}
            } catch(Exception) {}
        }

        // FFmpeg filters that repeat what the cover editor draws. The part around the cover is cut out and
        // shrunk first, so the color steps, brightness and rotation only touch a small picture (a 4K video
        // is several times faster this way). Brightness is applied before the black border, so the border stays black.
        // trim: leave out the empty rows above and below the picture, like the JPG cover. The quick preview keeps the
        // full 9:16 because the strip shows the same pictures, and takes the rows off when it plays them.
        internal static string Filters(Rect f,double rotation,int brightness,int width,int height,MotionRequest request,bool resample,out int outW,out int outH,bool trim=true) {
            double cx=(f.X+f.Width/2)*width,cy=(f.Y+f.Height/2)*height;
            double cw=Math.Max(2,f.Width*width),ch=Math.Max(2,f.Height*height);
            outH=Even(Math.Min(request.Height,ch));outW=Even(outH*9/16.0);
            bool turned=Math.Abs(rotation)>0.001;
            // The area to cut out: the cover itself, or a square that holds it at any angle when it's turned.
            double diagonal=Math.Sqrt(cw*cw+ch*ch)+4;
            int boxW=turned?(int)Math.Ceiling(diagonal):(int)Math.Ceiling(cw)+2,boxH=turned?boxW:(int)Math.Ceiling(ch)+2;
            int x0=(int)Math.Round(cx-boxW/2.0),y0=(int)Math.Round(cy-boxH/2.0);
            int ix0=Math.Max(0,x0),iy0=Math.Max(0,y0),ix1=Math.Min(width,x0+boxW),iy1=Math.Min(height,y0+boxH);
            int iw=ix1-ix0,ih=iy1-iy0;
            if(iw<2 || ih<2)throw new InvalidDataException(L.T("There are no frames to display."));
            // Shrink to about the final size (a little larger when turned, so the turn stays sharp).
            double s=Math.Min(1,(turned?1.5:1.0)*outH/ch);
            int sw=Math.Max(1,(int)Math.Round(iw*s)),sh=Math.Max(1,(int)Math.Round(ih*s));
            int px=(int)Math.Round((ix0-x0)*s),py=(int)Math.Round((iy0-y0)*s);
            int bw=Math.Max((int)Math.Round(boxW*s),px+sw),bh=Math.Max((int)Math.Round(boxH*s),py+sh);
            var steps=new List<string>();
            if(resample) {
                if(Math.Abs(request.Speed-1)>0.001)steps.Add("setpts=(PTS-STARTPTS)/"+N(request.Speed));
                steps.Add("fps="+request.Fps.ToString(CultureInfo.InvariantCulture));
            }
            if(ix0>0 || iy0>0 || iw<width || ih<height)steps.Add("crop="+iw+":"+ih+":"+ix0+":"+iy0+":exact=1");
            if(sw!=iw || sh!=ih)steps.Add("scale="+sw+":"+sh+":flags=lanczos");
            steps.Add("format=rgb24");
            if(brightness!=0) {
                double k=Math.Abs(brightness)/100.0;
                string channel=brightness>0?"val*"+N(1-k)+"+"+N(255*k):"val*"+N(1-k);
                steps.Add("lutrgb=r='"+channel+"':g='"+channel+"':b='"+channel+"'");
            }
            if(bw!=sw || bh!=sh)steps.Add("pad="+bw+":"+bh+":"+px+":"+py+":black");
            if(turned)steps.Add("rotate="+N(rotation*Math.PI/180)+":ow=iw:oh=ih:c=black");
            int cropW=Math.Max(2,Math.Min(bw,(int)Math.Round(cw*s))),cropH=Math.Max(2,Math.Min(bh,(int)Math.Round(ch*s)));
            steps.Add("crop="+cropW+":"+cropH+":(iw-ow)/2:(ih-oh)/2");
            double top,bottom;
            if(trim && CoverImages.VisibleRows(width,height,f,rotation,out top,out bottom)) {
                int first=Math.Min(cropH-2,(int)Math.Ceiling(top*cropH-1e-6));
                int last=Math.Max(first+2,(int)Math.Floor(bottom*cropH+1e-6));
                steps.Add("crop="+cropW+":"+(last-first)+":0:"+first+":exact=1");
                outH=Even(outH*(last-first)/(double)cropH);
            }
            steps.Add("scale="+outW+":"+outH+":flags=lanczos");
            steps.Add("setsar=1");
            return String.Join(",",steps);
        }

        // Writes the motion cover next to the still one and returns it. The time window is clipped
        // to the start and end of the original. A file the window already made with the same choices is reused.
        internal static MotionResult Make(Photo photo,string original,MotionRequest request,string directory,bool beside,CancellationToken token) {
            var source=new MotionSource {Path=original,Moment=MomentOf(photo,original),Crop=photo.Crop,Rotation=photo.Rotation,Brightness=photo.Brightness};
            Directory.CreateDirectory(directory);
            string part=Path.Combine(directory,".lovemyshelf-"+Guid.NewGuid().ToString("N")+".tmp");
            try {
                MotionResult result;
                if(request.Prepared!=null && request.PreparedKey==Key(source,request) && File.Exists(request.Prepared)) {
                    File.Copy(request.Prepared,part,true);
                    result=new MotionResult {Before=request.PreparedBefore,After=request.PreparedAfter};
                } else result=Encode(source,request,part,token);
                if(!File.Exists(part) || new FileInfo(part).Length==0)throw new InvalidDataException(L.T("FFmpeg could not make the file."));
                result.File=SharedCoverFiles.Publish(part,directory,SharedCoverFiles.Stem(photo.Name,photo.Folder,
                    Path.GetFileName(photo.Thumb)+request.Format,beside),token,request.Extension);
                return result;
            } finally {File.Delete(part);}
        }

        // Makes the file at part. For the window's preview it also reports progress (0..1) and hands over
        // small copies of the frames as they're made (with their frame rate).
        internal static MotionResult Encode(MotionSource source,MotionRequest request,string part,CancellationToken token,
            Action<double> progress=null,Action<BitmapSource,int> copies=null,int copyHeight=0) {
            var result=new MotionResult();
            if(VideoFrames.Supports(source.Path))FromVideo(source,request,part,result,token,progress,copies,copyHeight);
            else FromAnimation(source,request,part,result,token,progress,copies,copyHeight);
            if(!File.Exists(part) || new FileInfo(part).Length==0)throw new InvalidDataException(L.T("FFmpeg could not make the file."));
            return result;
        }

        static void FromVideo(MotionSource source,MotionRequest request,string part,MotionResult result,CancellationToken token,
            Action<double> progress,Action<BitmapSource,int> copies,int copyHeight) {
            string original=source.Path;
            var size=VideoFrames.ReadOrientation(original,token);
            int width=size.Width,height=size.Height;
            if(width<=0 || height<=0)throw new InvalidDataException(L.T("Could not read the frames."));
            if(Math.Abs(size.Angle%180)==90){int swap=width;width=height;height=swap;}
            double duration=VideoFrames.DurationMs(original,token);
            double start=Math.Max(0,source.Moment-request.Before*1000);
            double end=source.Moment+request.After*1000;
            if(duration>0)end=Math.Min(end,duration);
            if(end-start<50)throw new InvalidDataException(L.T("There are no frames to display."));
            result.Before=(source.Moment-start)/1000;result.After=Math.Max(0,end-source.Moment)/1000;
            int outW,outH;
            string chain=Filters(source.Crop,source.Rotation,source.Brightness,width,height,request,true,out outW,out outH);
            result.Width=outW;result.Height=outH;
            int forward=Math.Max(1,(int)Math.Round((end-start)/1000/Math.Max(0.1,request.Speed)*request.Fps,MidpointRounding.AwayFromZero));
            // -t before -i makes FFmpeg stop reading at the end of the part. Given after -i, the GIF palette step
            // waited for the end of the whole video (a 3-second GIF from a long 4K video took minutes).
            string input=(start>0?"-ss "+VideoFrames.Seconds(start)+" ":"")+"-t "+N((end-start)/1000)+" -i "+VideoFrames.Quote(original)+" ";
            Run(input,chain,request,forward,outW,outH,part,token,progress,0,1,copies,copyHeight);
        }

        // Samples the GIF/WebP at the chosen frame rate (and speed) into a temporary raw file for FFmpeg.
        // (Piping raw pictures through FFmpeg's standard input on Windows can alter bytes.)
        static void FromAnimation(MotionSource source,MotionRequest request,string part,MotionResult result,CancellationToken token,
            Action<double> progress,Action<BitmapSource,int> copies,int copyHeight) {
            string raw=part+".raw";
            try {
                int width,height,written=0;
                using(var file=new AnimationFrames(source.Path,token)) {
                    double duration=file.Times.Duration;
                    double each=1000.0*Math.Max(0.1,request.Speed)/request.Fps;
                    double start=Math.Max(0,source.Moment-request.Before*1000);
                    double end=Math.Min(duration,source.Moment+request.After*1000);
                    if(end-start<1)end=Math.Min(duration,start+each);
                    result.Before=(source.Moment-start)/1000;result.After=Math.Max(0,end-source.Moment)/1000;
                    var first=Bgra(file.Read(0,0));
                    width=first.PixelWidth;height=first.PixelHeight;
                    int stride=width*4;
                    var pixels=new byte[stride*height];
                    int shown=-1,total=Math.Max(1,(int)Math.Ceiling((end-start-0.001)/each));
                    using(var output=File.Create(raw)) {
                        for(double t=start;t<end-0.001;t+=each) {
                            token.ThrowIfCancellationRequested();
                            int index=Math.Min(file.Count-1,file.Times.At(t));
                            if(index!=shown) {
                                var frame=Bgra(file.Read(index,0));
                                if(frame.PixelWidth!=width || frame.PixelHeight!=height)throw new InvalidDataException(L.T("The video frame format is invalid."));
                                frame.CopyPixels(pixels,stride,0);shown=index;
                            }
                            output.Write(pixels,0,pixels.Length);
                            written++;
                            if(progress!=null)progress(0.4*Math.Min(1,written/(double)total));
                        }
                    }
                }
                int outW,outH;
                string chain=Filters(source.Crop,source.Rotation,source.Brightness,width,height,request,false,out outW,out outH);
                result.Width=outW;result.Height=outH;
                string input="-f rawvideo -pix_fmt bgra -s "+width+"x"+height+" -framerate "+request.Fps+" -i "+VideoFrames.Quote(raw)+" ";
                Run(input,chain,request,Math.Max(1,written),outW,outH,part,token,progress,0.4,0.6,copies,copyHeight);
            } finally {File.Delete(raw);}
        }

        // One FFmpeg run: the cover's look, then the play style, then the file type's own steps.
        // GIF: a palette made from all frames, applied with the chosen dithering (sierra for High is smoothest;
        // bayer is lighter; Smaller uses 128 colors). Very long GIFs make the palette in a first pass instead of
        // holding every frame in memory.
        static void Run(string input,string chain,MotionRequest request,int forward,int outW,int outH,string part,CancellationToken token,
            Action<double> progress,double progressFrom,double progressSpan,Action<BitmapSource,int> copies,int copyHeight) {
            bool bounce=request.Loop=="bounce" && forward>=3,reverse=request.Loop=="reverse";
            bool gif=request.Format=="gif";
            long held=(long)outW*outH*3*forward*(bounce?2:1);
            string palette=null;
            try {
                if(gif && held>700L*1024*1024) {
                    palette=part+".palette.png";
                    string first="-v error -nostdin -y "+input+"-an -sn -dn -filter_complex "+VideoFrames.Quote("[0:v:0]"+chain+","+PaletteGen(request)+"[p]")+
                        " -map [p] -frames:v 1 -update 1 "+VideoFrames.Quote(palette);
                    Execute(first,null,0,0,0,forward,request.Fps,token,null,0,0);
                    if(progress!=null)progress(progressFrom+progressSpan*0.4);
                    progressFrom+=progressSpan*0.4;progressSpan*=0.6;
                    input+="-i "+VideoFrames.Quote(palette)+" ";
                }
                int copyH=0,copyW=0,copyRate=request.Fps;
                if(copies!=null) {
                    copyH=Math.Max(2,Math.Min(outH,copyHeight));copyW=Math.Max(2,(int)Math.Round(outW*(double)copyH/outH));
                    if(forward>PreviewCopyLimit)copyRate=Math.Max(1,(int)Math.Floor(request.Fps*(double)PreviewCopyLimit/forward));
                }
                var g=new StringBuilder("[0:v:0]").Append(chain).Append("[made]");
                string current="[made]";
                if(copies!=null) {
                    g.Append(";[made]split[main][look];[look]");
                    if(copyRate<request.Fps)g.Append("fps=").Append(copyRate).Append(',');
                    g.Append("scale=").Append(copyW).Append(':').Append(copyH).Append(":flags=bicubic,format=bgra[copy]");
                    current="[main]";
                }
                if(reverse){g.Append(';').Append(current).Append("reverse[turned]");current="[turned]";}
                else if(bounce) {
                    // Forward, then back without repeating the two turning frames, so the loop never stalls.
                    g.Append(';').Append(current).Append("split[ahead][back0];[back0]trim=start_frame=1,setpts=PTS-STARTPTS,reverse,trim=start_frame=1,setpts=PTS-STARTPTS[back];")
                        .Append("[ahead][back]concat=n=2:v=1:a=0[turned]");
                    current="[turned]";
                }
                string codec;
                if(gif) {
                    string use="paletteuse=dither="+(request.Quality==0?"sierra2_4a":"bayer:bayer_scale="+(request.Quality==2?5:4))+":diff_mode=rectangle";
                    if(palette!=null)g.Append(';').Append(current).Append("[1:v]").Append(use).Append("[out]");
                    else g.Append(';').Append(current).Append("split[a][b];[a]").Append(PaletteGen(request)).Append("[p];[b][p]").Append(use).Append("[out]");
                    codec="-loop 0 -f gif";
                } else if(request.Format=="webp") {
                    g.Append(';').Append(current).Append("null[out]");
                    codec="-c:v libwebp_anim -quality "+(request.Quality==0?90:request.Quality==2?65:82)+" -loop 0 -f webp";
                } else {
                    g.Append(';').Append(current).Append("format=yuv420p[out]");
                    codec="-c:v libx264 -preset medium -crf "+(request.Quality==0?17:request.Quality==2?25:20)+" -movflags +faststart -f mp4";
                }
                string args="-v error -nostdin -y "+input+"-an -sn -dn -filter_complex "+VideoFrames.Quote(g.ToString())+" -map [out] "+codec+" "+VideoFrames.Quote(part);
                if(copies!=null)args+=" -map [copy] -c:v rawvideo -pix_fmt bgra -f rawvideo pipe:1";
                // GIF frames come out only after the palette is made, so reading the frames is most of the work.
                double share=gif && palette==null?0.85:0.97;
                Execute(args,copies,copyW,copyH,copyRate,forward,request.Fps,token,progress,progressFrom,progressSpan*share);
                if(progress!=null)progress(progressFrom+progressSpan);
            } finally {if(palette!=null)File.Delete(palette);}
        }
        static string PaletteGen(MotionRequest request) {return "palettegen=stats_mode=full"+(request.Quality==2?":max_colors=128":"");}
        static void Execute(string args,Action<BitmapSource,int> copies,int copyW,int copyH,int copyRate,int forward,int fps,CancellationToken token,
            Action<double> progress,double progressFrom,double progressSpan) {
            using(var p=VideoFrames.Run("ffmpeg",args))
            using(token.Register(()=>VideoFrames.Stop(p))) {
                if(copies!=null) {
                    var output=p.StandardOutput.BaseStream;
                    int bytes=copyW*copyH*4,got=0;
                    double expected=Math.Max(1,copyRate<fps?forward*copyRate/(double)fps:forward);
                    while(true) {
                        var data=new byte[bytes];
                        if(!Fill(output,data))break;
                        var frame=BitmapSource.Create(copyW,copyH,96,96,PixelFormats.Bgra32,null,data,copyW*4);frame.Freeze();
                        got++;copies(frame,copyRate);
                        if(progress!=null)progress(progressFrom+progressSpan*Math.Min(1,got/expected));
                    }
                } else p.StandardOutput.ReadToEnd();
                p.WaitForExit();token.ThrowIfCancellationRequested();
                if(p.ExitCode!=0)throw new InvalidDataException(L.T("FFmpeg could not make the file."));
            }
        }
        static bool Fill(Stream stream,byte[] data) {
            int got=0;
            while(got<data.Length) {
                int read=stream.Read(data,got,data.Length-got);
                if(read<=0)return false;
                got+=read;
            }
            return true;
        }

        static BitmapSource Bgra(BitmapSource source) {
            if(source.Format==PixelFormats.Bgra32)return source;
            var converted=new FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0);converted.Freeze();return converted;
        }
    }
}
