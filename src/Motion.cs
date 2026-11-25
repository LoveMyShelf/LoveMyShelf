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
        // MP4 only, from a video, playing forward: the video's sound for the same part goes into the file.
        public bool Sound;
        internal bool WithSound {get {return Sound && Format=="mp4" && Loop=="forward";}}
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
                Math.Round(Before,3),Math.Round(After,3),Fps,Height,Format,Speed,Loop,Quality)+(Sound?";sound=1":"");
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
                // One of the two is below zero when the part lies wholly before or after the cover's moment.
                if(Double.IsNaN(r.Before) || Double.IsNaN(r.After) || Double.IsInfinity(r.Before) || Double.IsInfinity(r.After) || r.Before+r.After<=0)return null;
                if(r.Format!="gif" && r.Format!="webp" && r.Format!="mp4")return null;
                if(r.Loop!="forward" && r.Loop!="bounce" && r.Loop!="reverse")return null;
                if(r.Speed<=0 || r.Fps<=0 || r.Height<=0)return null;
                r.Quality=Math.Max(0,Math.Min(2,r.Quality));
                string sound;
                r.Sound=values.TryGetValue("sound",out sound) && sound=="1"; // covers made before this had no sound
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
        // The part marked while watching ([ and ] in the large view), in milliseconds: Make GIF cover opens on it.
        public double? PartStart,PartEnd;
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
        internal static int StripSeconds=4;
        internal static readonly int[] Rates={10,12,15,20,24,30};
        internal static readonly int[] Heights={480,640,720,960,1280};
        // The speed goes from 0.2× to 4× in steps of 0.1, like playback in the large view. (Speeds: what the checks pick by position.)
        const double SlowestSpeed=0.2,FastestSpeed=4;
        static readonly double[] Speeds={0.5,0.75,1,1.5,2};
        static readonly string[] Loops={"forward","bounce","reverse"};
        static readonly string[] Formats={"gif","webp","mp4"};
        // Technical choices start from the saved defaults and stay as last used while LoveMyShelf runs;
        // speed and play style start fresh each time.
        // The pictures per second and the height are kept as values: a number typed in Guide & Options joins the lists below.
        static int lastFps=30,lastHeight=1280,lastQuality=0,lastFormat=1;
        static bool lastSound=true; // the sound box starts checked; it only shows for an MP4 made from a video
        static double sideMs=1000;
        // From Guide & Options: how far apart the scenes of the preview are, in milliseconds.
        // This is the GIF window's own setting, apart from the scene step (Scenes.StepMs).
        internal static int PreviewStepMs=50;
        // Also from Guide & Options. AutoMake: the real file is made in the background after every change (otherwise
        // only when asked for). MakeDelayMs: how long the handles rest before that starts.
        internal static bool AutoMake;
        internal static int MakeDelayMs=700;
        internal static void UseDefaults(Preferences settings) {
            StripSeconds=settings.GifStripSeconds;PreviewStepMs=settings.GifPreviewStepMs;Scenes.StepMs=settings.SceneStepMs;
            lastFps=settings.GifFps;lastHeight=settings.GifHeight;
            lastQuality=Math.Max(0,Math.Min(2,settings.GifQuality));lastFormat=Math.Max(0,Math.Min(2,settings.GifFormat));
            sideMs=settings.GifSideTenths*100.0;
            AutoMake=settings.GifAutoMake;MakeDelayMs=settings.GifMakeDelayMs;
            MotionMaker.WebpEffort=settings.WebpEffort;
            UpscaleEngine.UseGpu=settings.UpscaleGpu;
        }
        // The usual choices plus one more when the value in use isn't among them (typed in Guide & Options, or saved with an older GIF).
        static int[] With(int[] usual,int value) {
            if(Array.IndexOf(usual,value)>=0)return usual;
            var list=new List<int>(usual);list.Add(value);list.Sort();
            return list.ToArray();
        }
        int[] rates,heights; // what this window's two boxes list
        const int Thumbs=14; // pictures on the strip before its width is known
        // The pictures stay fully visible for exact work: only a three-pixel line just outside the start and end crosses them.
        // The handles, the chosen part and the cover and play marks live in a thin rail above and below the pictures.
        const double PictureHeight=156,TopRail=PartLook.TopRail,StripHeight=PictureHeight+TopRail+PartLook.BottomRail,ThumbGap=2;
        // Strip pictures show the cover's own part of each scene, in its 9:16 shape, so a wider window shows
        // more of them (like a video editor's timeline) instead of stretching each one.
        const double CellWidth=PictureHeight*9/16;
        const int CellPixels=(int)(PictureHeight*2);
        // Room at each end of the strip for a handle and a little empty gap,
        // so a handle at the clip's very start or end stays whole and the empty strip beyond shows the clip ends there.
        // In the middle of a clip this room shows the neighbouring pictures, so the strip still looks full.
        const double EdgeRoom=18;
        // Longer GIFs keep the lighter preview copies instead of reading every frame of the finished file.
        const int ExactPreviewLimit=300;

        readonly MotionSource source;
        readonly CancellationTokenSource lifetime=new CancellationTokenSource();
        readonly Canvas strip=new Canvas {Height=StripHeight,ClipToBounds=true};
        readonly Image preview=new Image {Stretch=Stretch.Uniform};
        readonly Border previewFrame,statusPill;
        readonly TextBlock status=new TextBlock {Foreground=Brushes.White,FontSize=12,TextAlignment=TextAlignment.Center};
        readonly FramePlayer player;
        readonly TextBlock summary=UI.Text("",13,true);
        readonly Dictionary<int,BitmapSource> thumbs=new Dictionary<int,BitmapSource>();
        readonly HashSet<int> loading=new HashSet<int>();
        readonly DispatcherTimer edgeScroll=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(40)};
        // After the wheel stops for a moment, the same follow-up as the end of a drag of the strip.
        readonly DispatcherTimer wheelSettle=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(250)};
        // The real file is only made once the handles have been still for a moment (MakeDelayMs); the preview shows at once.
        readonly DispatcherTimer prepareSoon=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(700)};
        DraftScenes draft;
        CancellationTokenSource draftWork;
        bool draftOn; // the preview is playing and the real file is not ready yet
        // A small round dot in the rail under the pictures, under the scene now playing, and a tiny card in the
        // rail over them at the JPG cover's scene (see PartLook).
        readonly FrameworkElement playMark=PartLook.PlayMark(),coverMark=PartLook.CoverMark();
        double stripPixel=1; // one screen pixel in the strip's units
        // What the mouse can do on the strip, always in view under it: the action in bold and a few words each.
        TextBlock[] hintCaps,hintWords;
        FrameworkElement wheelHint,hintBar,stripFrame;
        WrapPanel hintRow;
        Button back;
        bool hintsPlaced,hintsBelow;
        StackPanel chips;
        readonly List<TextBlock> fieldNames=new List<TextBlock>();
        int pointed=-1;
        ComboBox fps,height,quality,format;
        RadioButton[] loopChoices;
        double speed=1;
        readonly TextBlock speedText=UI.Text("",14);
        readonly DispatcherTimer speedHold=new DispatcherTimer();
        Action speedHoldAction;
        Border startChip,endChip;
        TextBlock startTime,endTime;
        double duration=Double.NaN,viewStart,span,start,end,step;
        int dragging; // 0 none, 1 start handle, 2 end handle, 3 panning
        double dragOrigin,dragView,edgeDirection;
        int activeHandle=2;
        Button make;
        TextBlock help;
        // The layout. Wide: what is happening on the left of the preview, every choice on its right, and under them
        // only the strip and one row. Narrow: the choices come down under the strip, as before.
        const double WideBottom=180,SideGap=18,SideMin=236,SideMax=480;
        DockPanel page,lastRow,compactGuide;
        StackPanel bottom,narrowHost,sidePanel,actions,moreChoices;
        Grid middle;
        ScrollViewer leftScroll,rightScroll;
        WrapPanel narrowRow;
        System.Windows.Controls.Primitives.UniformGrid moreGrid;
        Expander more;
        FrameworkElement speedField,loopField,fpsField,heightField,qualityField,formatField;
        Button explain,realButton,realCompact;
        bool wide,arranged,lastAuto;
        string makeLabel;
        // The card on the left: what the preview is right now, in plain words.
        readonly TextBlock guideTitle=UI.Text("",16),guideBody=UI.Text("",13,true),guideProgress=UI.Text("",13),guideFoot=UI.Text("",12.5,true),compactText=UI.Text("",12.5);
        readonly TextBlock heightNote=UI.Text("",12,true),soundNote=UI.Text("",12,true);
        CheckBox sound;
        readonly Grid guideTrack=new Grid {Height=4,Margin=new Thickness(0,6,0,0)};
        readonly Border guideFill=new Border {CornerRadius=new CornerRadius(2),HorizontalAlignment=HorizontalAlignment.Left};
        // The background run that makes the real file for the preview, and what it made.
        CancellationTokenSource prepareWork;
        string preparedFile,preparedKey;
        MotionFrames preparedPlay; // the real file's frames, to show again when the choices come back to it
        double[] preparedTimeline;
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
        bool fromPart; // opened on the part marked in the large view...
        Viewer partGiver; // ...this one

        // editing: the cover already has a GIF; saved: the choices it was made with (null for a GIF made before they were saved).
        internal MotionWindow(Window owner,MotionSource source,bool fromEditor,bool editing=false,MotionRequest saved=null) {
            this.source=source;
            UI.WindowSetup(this,L.T(editing?"Edit GIF cover":"Make GIF cover"));Owner=owner;ShowInTaskbar=false;
            WindowStartupLocation=WindowStartupLocation.CenterOwner;
            var area=SystemParameters.WorkArea;
            Width=Math.Min(960,area.Width);Height=Math.Min(820,area.Height);MinWidth=Math.Min(600,area.Width);MinHeight=Math.Min(600,area.Height);
            makeLabel=L.T(fromEditor?"Save JPG cover and make GIF cover":"Make");
            lastAuto=AutoMake;
            span=Math.Max(2,StripSeconds)*1000.0;step=span/Thumbs;
            start=Math.Max(0,source.Moment-sideMs);end=source.Moment+sideMs;
            viewStart=Math.Max(0,source.Moment-span/2);
            int loopPick=0,fpsValue=lastFps,heightValue=lastHeight,qualityPick=lastQuality,formatPick=lastFormat;
            // A saved MP4 opens as it was made. A saved GIF or WebP never had the choice, so it starts from the usual one.
            bool soundPick=saved!=null && saved.Format=="mp4"?saved.Sound:lastSound;
            if(saved!=null) {
                // Opens on the part and the choices the GIF was made with; the strip widens to show all of it.
                start=Math.Max(0,source.Moment-saved.Before*1000);end=source.Moment+saved.After*1000;
                span=Math.Max(span,(end-start)*1.25);step=span/Thumbs;
                viewStart=Math.Max(0,(start+end)/2-span/2);
                speed=Math.Max(SlowestSpeed,Math.Min(FastestSpeed,Math.Round(saved.Speed,2))); // a GIF made at 0.75× opens at 0.75×
                loopPick=Math.Max(0,Array.IndexOf(Loops,saved.Loop));
                fpsValue=saved.Fps;heightValue=saved.Height;
                qualityPick=saved.Quality;formatPick=Math.Max(0,Array.IndexOf(Formats,saved.Format));
            } else {
                // A part marked while watching: the window opens on it, on the same scene grid the handles use.
                partGiver=Viewer.GivePart(source,owner);
                if(partGiver!=null && source.PartStart.HasValue && source.PartEnd.HasValue && source.PartEnd.Value>source.PartStart.Value) {
                    start=Math.Max(0,Scenes.Snap(source.PartStart.Value,source.Moment));
                    end=Scenes.Snap(source.PartEnd.Value,source.Moment);
                    span=Math.Max(span,(end-start)*1.25);step=span/Thumbs;
                    viewStart=Math.Max(0,(start+end)/2-span/2);
                    fromPart=true;
                }
            }
            // Never an empty or backward part, whatever was saved or marked.
            end=Math.Max(end,start+Math.Max(50,Scenes.StepMs));
            rates=With(Rates,Math.Max(1,fpsValue));heights=With(Heights,Math.Max(2,heightValue));
            int fpsPick=Array.IndexOf(rates,Math.Max(1,fpsValue)),heightPick=Array.IndexOf(heights,Math.Max(2,heightValue));
            player=new FramePlayer(preview);
            player.Shown+=ShowPlayMark;
            playMark.IsHitTestVisible=false;
            coverMark.ToolTip=L.T("The JPG cover's scene is here.");
            strip.SetResourceReference(Canvas.BackgroundProperty,"Paper");
            Canvas.SetTop(cells,TopRail);
            // Follows its owner into full screen, like Choose a scene and Make cover do.
            var root=new DockPanel();root.Children.Add(FullScreen.TitleBar(this,FullScreen.IsFull(owner)));
            page=new DockPanel {Margin=new Thickness(20,12,20,16)};root.Children.Add(page);Content=root;

            // Bottom: the strip, then start and end with the buttons.
            bottom=new StackPanel();DockPanel.SetDock(bottom,Dock.Bottom);page.Children.Add(bottom);
            // Narrow windows have no room for the card on the left, so one line here says the same.
            compactGuide=new DockPanel {Margin=new Thickness(0,8,0,0)};
            realCompact=UI.Btn(L.T("See how the real file looks"),RealNow);
            realCompact.Margin=new Thickness(8,0,0,0);DockPanel.SetDock(realCompact,Dock.Right);compactGuide.Children.Add(realCompact);
            compactText.TextTrimming=TextTrimming.CharacterEllipsis;compactGuide.Children.Add(compactText);
            bottom.Children.Add(compactGuide);
            back=UI.Btn(L.T("Back to the cover"),()=>{viewStart=ClampView(source.Moment-span/2);Layout();ShowDraft(draftOwed);});
            // The button carries the same tiny card as the strip's cover mark, so the mark explains itself.
            var backLabel=new StackPanel {Orientation=Orientation.Horizontal};
            var backMark=PartLook.CoverMark(false);backMark.Margin=new Thickness(0,1,7,0);backMark.VerticalAlignment=VerticalAlignment.Center;
            backLabel.Children.Add(backMark);backLabel.Children.Add(new TextBlock {Text=L.T("Back to the cover"),VerticalAlignment=VerticalAlignment.Center});
            back.Content=backLabel;back.VerticalAlignment=VerticalAlignment.Center;
            back.ToolTip=L.T("Brings the strip back to the JPG cover's scene. The small card over the pictures marks that scene.");
            AutomationProperties.SetName(back,L.T("Back to the cover"));
            // What the mouse can do on the strip. It sits in the free middle of the row under the strip where that
            // has the room, so the preview keeps the height; otherwise in a row of its own over the strip (see PlaceHints).
            // A note comes first only when there is something to say about how the window opened.
            string note=fromPart?L.T("It starts from the part you marked while watching."):editing && saved==null?
                L.T("This GIF was made earlier, so the part set then is not saved. It starts from the part before and after the cover, as when it was first made."):"";
            help=UI.Text(NoticeLook.KeepWords(note),12,true);
            help.TextWrapping=TextWrapping.Wrap;help.Margin=new Thickness(0,0,0,2);help.Visibility=note==""?Visibility.Collapsed:Visibility.Visible;
            var hints=hintRow=new WrapPanel();
            hintCaps=new TextBlock[3];hintWords=new TextBlock[3];
            hints.Children.Add(Hint(0,L.T("Drag"),L.T("Set the start and the end with the handles")));
            hints.Children.Add(wheelHint=Hint(1,L.T("Wheel"),L.T("See earlier and later scenes")));
            hints.Children.Add(Hint(2,L.T("Double-click"),L.T("Outside the part, the part grows to there")));
            var words=new StackPanel {VerticalAlignment=VerticalAlignment.Center};
            words.Children.Add(help);words.Children.Add(hints);hintBar=words;
            stripFrame=new Border {Child=strip,CornerRadius=new CornerRadius(6),ClipToBounds=true,Margin=new Thickness(0,8,0,0)};
            bottom.Children.Add(stripFrame);

            // Start and end can be nudged by one scene (the scene step). The buttons sit on the same row when the window is wide.
            lastRow=new DockPanel {Margin=new Thickness(0,10,0,0)};
            summary.VerticalAlignment=VerticalAlignment.Center;summary.TextWrapping=TextWrapping.Wrap;
            chips=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Center};
            startChip=TimeChip(L.T("Start"),1,L.T("Start one scene earlier"),L.T("Start one scene later"),out startTime);
            endChip=TimeChip(L.T("End"),2,L.T("End one scene earlier"),L.T("End one scene later"),out endTime);
            endChip.Margin=new Thickness(8,0,0,0);
            chips.Children.Add(startChip);chips.Children.Add(endChip);
            DockPanel.SetDock(chips,Dock.Left);lastRow.Children.Add(chips);
            bottom.Children.Add(lastRow);
            narrowHost=new StackPanel();bottom.Children.Add(narrowHost);

            // Every choice is one row of its own (its name, then its control), so the same rows can stand beside the
            // preview in a wide window or under the strip in a narrow one (see Arrange), and all of them show at once.
            speedField=Field(L.T("Speed"),SpeedControl());
            loopField=Field(L.T("Play style"),Segment(new[]{L.T("Loop"),L.T("Back and forth"),L.T("Backward")},loopPick,L.T("Play style"),Changed,out loopChoices));
            var fpsBox=new StackPanel();var heightBox=new StackPanel();var qualityBox=new StackPanel();var formatBox=new StackPanel();
            fps=Choice(fpsBox,L.T("Smoothness"),L.T("Smoothness (pictures per second)"),Array.ConvertAll(rates,r=>L.F("{0} a second",r)),fpsPick);
            height=Choice(heightBox,L.T("Height"),null,Array.ConvertAll(heights,h=>L.F("{0} px",h)),heightPick);
            // Said only when it applies: a cover smaller than the chosen height is made at its own size.
            heightNote.TextWrapping=TextWrapping.Wrap;heightNote.Margin=new Thickness(1,0,0,6);heightNote.LineHeight=17;heightNote.Visibility=Visibility.Collapsed;
            heightBox.Children.Add(heightNote);
            quality=Choice(qualityBox,L.T("Picture quality"),null,new[]{L.T("High"),L.T("Normal"),L.T("Low")},qualityPick);
            format=Choice(formatBox,L.T("File type"),null,new[]{L.T("GIF · moves everywhere · big size"),L.T("WebP · smooth colors, small size · doesn't work in some places"),L.T("MP4 · smallest size and sharp · goes up as a video")},formatPick);
            // Sound: only an MP4 can carry it, only a video has it, and only forward play keeps it in step with the picture.
            sound=new CheckBox {Content=L.T("Put the sound in"),IsChecked=soundPick,Margin=new Thickness(1,2,0,2),FontSize=13};
            sound.SetResourceReference(Control.ForegroundProperty,"Text");
            soundNote.TextWrapping=TextWrapping.Wrap;soundNote.Margin=new Thickness(1,0,0,6);soundNote.LineHeight=17;
            formatBox.Children.Add(sound);formatBox.Children.Add(soundNote);
            sound.Checked+=delegate {Changed();};sound.Unchecked+=delegate {Changed();};
            UpdateSound();
            fpsField=fpsBox;heightField=heightBox;qualityField=qualityBox;formatField=formatBox;
            // The names share one width (the widest of them in this language), so every control starts on the same
            // line; the notes under a control start there too.
            double nameWidth=0;
            var appFont=Application.Current.Resources["AppFont"] as FontFamily; // measured before the window lends its font
            foreach(var name in fieldNames){if(appFont!=null)name.FontFamily=appFont;name.Measure(new Size(Double.PositiveInfinity,Double.PositiveInfinity));nameWidth=Math.Max(nameWidth,name.DesiredSize.Width);}
            nameWidth=Math.Ceiling(nameWidth)+14;
            foreach(var name in fieldNames)name.Width=nameWidth;
            foreach(var under in new FrameworkElement[]{heightNote,sound,soundNote}){var m=under.Margin;under.Margin=new Thickness(nameWidth+m.Left,m.Top,m.Right,m.Bottom);}
            // What these mean, in plain words, and the place to choose what they start as.
            explain=UI.Btn(L.T("What do these mean? (F1)"),()=>GuideWindow.Open(this,"gif"));
            explain.HorizontalAlignment=HorizontalAlignment.Left;explain.Margin=new Thickness(0,2,0,4);
            foreach(var box in new[]{fps,height,quality,format})box.SelectionChanged+=delegate {Changed();};
            narrowRow=new WrapPanel {Margin=new Thickness(0,10,0,0)};
            more=new Expander {Header=L.T("More choices"),Margin=new Thickness(0,8,0,0),IsExpanded=false};
            more.SetResourceReference(Control.ForegroundProperty,"Text");
            moreChoices=new StackPanel {Margin=new Thickness(0,6,0,0)};more.Content=moreChoices;
            moreGrid=new System.Windows.Controls.Primitives.UniformGrid {Columns=2};moreChoices.Children.Add(moreGrid);
            actions=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Center};
            var cancel=UI.Btn(L.T("Cancel"),()=>DialogResult=false);cancel.IsCancel=true;
            make=UI.Btn(makeLabel,Finish,true);
            make.IsDefault=true;make.Margin=new Thickness(6,0,0,0);
            foreach(var button in new[]{cancel,make}){button.FontSize=14;button.MinHeight=32;}
            actions.Children.Add(cancel);actions.Children.Add(make);

            // Middle: what is happening (left), the preview in the cover's 9:16 shape (click or Space pauses it), the choices (right).
            middle=new Grid();
            middle.ColumnDefinitions.Add(new ColumnDefinition());middle.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});middle.ColumnDefinitions.Add(new ColumnDefinition());
            var previewBox=new Grid();
            previewFrame=new Border {Background=Brushes.Black,CornerRadius=new CornerRadius(8),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,
                Child=preview,Cursor=Cursors.Hand,ClipToBounds=true,Width=0,Height=0};
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
            Grid.SetColumn(previewBox,1);middle.Children.Add(previewBox);

            var guide=new StackPanel();
            guideTitle.FontWeight=FontWeights.SemiBold;guideTitle.TextWrapping=TextWrapping.Wrap;
            guideBody.TextWrapping=TextWrapping.Wrap;guideBody.Margin=new Thickness(0,8,0,0);guideBody.LineHeight=20;
            guideProgress.FontWeight=FontWeights.SemiBold;guideProgress.TextWrapping=TextWrapping.Wrap;guideProgress.Margin=new Thickness(0,12,0,0);
            var track=new Border {CornerRadius=new CornerRadius(2)};track.SetResourceReference(Border.BackgroundProperty,"Line");
            guideFill.SetResourceReference(Border.BackgroundProperty,"Accent");
            guideTrack.Children.Add(track);guideTrack.Children.Add(guideFill);
            guideTrack.SizeChanged+=delegate {ShowProgress();};
            guideFoot.TextWrapping=TextWrapping.Wrap;guideFoot.Margin=new Thickness(0,12,0,0);guideFoot.LineHeight=19;
            realButton=UI.Btn(L.T("See how the real file looks"),RealNow);
            realButton.HorizontalAlignment=HorizontalAlignment.Left;realButton.Margin=new Thickness(0,12,0,0);
            // Whether the real file is made by itself, how soon, and the rest are each person's to choose.
            var ways=UI.Btn(L.T("Change how the preview works"),()=>GuideWindow.Open(this,"gif","gifReal"));
            ways.HorizontalAlignment=HorizontalAlignment.Left;ways.Margin=new Thickness(0,12,0,0);
            foreach(var part in new FrameworkElement[]{guideTitle,guideBody,guideProgress,guideTrack,guideFoot,realButton,ways})guide.Children.Add(part);
            var guideCard=new Border {Child=guide,CornerRadius=new CornerRadius(10),Padding=new Thickness(16,14,16,14),BorderThickness=new Thickness(1)};
            guideCard.SetResourceReference(Border.BackgroundProperty,"Panel");guideCard.SetResourceReference(Border.BorderBrushProperty,"Line");
            leftScroll=new ScrollViewer {Content=guideCard,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,
                HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,SideGap,0),Focusable=false};
            middle.Children.Add(leftScroll);
            sidePanel=new StackPanel();
            rightScroll=new ScrollViewer {Content=sidePanel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,
                HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(SideGap,0,0,0),Focusable=false};
            Grid.SetColumn(rightScroll,2);middle.Children.Add(rightScroll);
            page.Children.Add(middle);
            page.SizeChanged+=delegate {Fit();};middle.SizeChanged+=delegate {Fit();};
            Arrange(false);
            // Coming back from Guide & Options: the way the preview works may have been changed there.
            Activated+=delegate {
                if(!IsLoaded)return;
                if(lastAuto!=AutoMake){lastAuto=AutoMake;SchedulePrepare();}
                else SetStatus();
            };

            strip.SizeChanged+=delegate {Layout();};
            strip.MouseLeftButtonDown+=StripDown;strip.MouseMove+=StripMove;strip.MouseLeftButtonUp+=StripUp;
            strip.LostMouseCapture+=delegate {if(dragging!=0)FinishDrag();};
            strip.MouseLeave+=delegate {if(dragging==0)PointAt(-1);};
            // The wheel over the strip moves the view sideways; the chosen part stays where it is.
            strip.MouseWheel+=(s,e)=>{e.Handled=true;WheelStrip(e.Delta);};
            wheelSettle.Tick+=delegate {
                wheelSettle.Stop();
                if(dragging!=0 || lifetime.IsCancellationRequested)return;
                PlacePlayMark();ShowDraft(draftOwed);SetStatus();
            };
            edgeScroll.Tick+=delegate {
                if(dragging!=1 && dragging!=2){edgeScroll.Stop();return;}
                viewStart=ClampView(viewStart+edgeDirection*span/40);
                double edge=edgeDirection<0?viewStart:viewStart+span;
                PlaceHandle(dragging,edge);
                Layout();UpdateChips();UpdateSummary();
            };
            prepareSoon.Tick+=delegate {
                prepareSoon.Stop();
                if(!AutoMake){SetStatus();return;}
                // The real file waits while a handle is held or the strip's scenes are still being read, so it
                // never slows down what the person is doing right now. It looks again shortly.
                if(dragging!=0 || draftFilling){prepareSoon.Interval=TimeSpan.FromMilliseconds(200);prepareSoon.Start();return;}
                Prepare();
            };
            PreviewKeyDown+=(s,e)=>{
                if(e.Key==Key.F1){GuideWindow.Open(this,"gif");e.Handled=true;return;}
                if(e.OriginalSource is ComboBox || e.OriginalSource is ComboBoxItem)return;
                if(e.Key==Key.Space){if(!e.IsRepeat)TogglePause();e.Handled=true;return;}
                // Z / X / C / V: the same keys as playback speed in the large view, in Korean input mode too.
                var letter=e.Key==Key.ImeProcessed?e.ImeProcessedKey:e.Key;
                if(Keyboard.Modifiers==ModifierKeys.None && (letter==Key.Z || letter==Key.X || letter==Key.C)) {
                    if(letter==Key.Z)SetSpeed(1);else StepSpeed(letter==Key.X?-1:1);
                    e.Handled=true;return;
                }
                if(Keyboard.Modifiers==ModifierKeys.None && letter==Key.V) {
                    if(!e.IsRepeat)ToggleDoubleSpeed();
                    e.Handled=true;return;
                }
                if(e.Key!=Key.Left && e.Key!=Key.Right)return;
                // ← → move the handle one scene (the scene step); Shift + ← → about a whole second.
                Nudge(activeHandle,(e.Key==Key.Left?-1:1)*(Keyboard.Modifiers==ModifierKeys.Shift?1000:Scenes.StepMs));e.Handled=true;
            };
            Closed+=delegate {
                lifetime.Cancel();player.Stop();edgeScroll.Stop();prepareSoon.Stop();speedHold.Stop();wheelSettle.Stop();
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
                        if(WholeClipShown) {
                            wheelHint.Visibility=Visibility.Collapsed;
                            if(help.Visibility!=Visibility.Visible) {
                                help.Text=NoticeLook.KeepWords(L.F("The whole video ({0:0.0} s) shows on the strip.",duration/1000));
                                help.Visibility=Visibility.Visible;
                            }
                            PlaceHints();
                        }
                        end=Math.Min(end,duration);
                        // A part that began at the very end of the file (a mark there, a rounded scene) still gets a scene.
                        if(start>end-Math.Max(50,Scenes.StepMs))start=Math.Max(0,end-Math.Max(50,Scenes.StepMs));
                        viewStart=ClampView(viewStart);
                    }
                } catch(Exception) {}
                ReserveClockWidth();
                Layout();UpdateChips();UpdateSummary();SchedulePrepare();
            };
        }

        // Speed: [Z Reset] [X −] 1.2× [C +] [V 2×]. One press is 0.1; a held button keeps going, like a held key.
        // V goes straight to 2×, and from 2× back to 1×.
        FrameworkElement SpeedControl() {
            var row=new StackPanel {Orientation=Orientation.Horizontal};
            Func<string,string,Action,bool,Button> key=(label,tip,act,repeats)=>{
                var button=new Button {Content=label,MinHeight=30,MinWidth=0,Padding=new Thickness(8,1,8,2),FontSize=12.5,Focusable=false,ToolTip=tip,
                    VerticalAlignment=VerticalAlignment.Center};
                AutomationProperties.SetName(button,tip);
                if(!repeats){button.Click+=delegate {act();};return button;}
                button.ClickMode=ClickMode.Press;
                button.Click+=delegate {
                    act();
                    speedHoldAction=()=>{if(button.IsPressed)act();else speedHold.Stop();};
                    speedHold.Stop();speedHold.Interval=TimeSpan.FromMilliseconds(420);speedHold.Start();
                };
                return button;
            };
            speedHold.Tick+=delegate {
                speedHold.Interval=TimeSpan.FromMilliseconds(90);
                if(speedHoldAction==null)speedHold.Stop();else speedHoldAction();
            };
            speedText.FontWeight=FontWeights.SemiBold;speedText.TextAlignment=TextAlignment.Center;speedText.Width=50;
            var reset=key("Z  "+L.T("Reset"),L.T("Original speed"),()=>SetSpeed(1),false);
            var slower=key("X  −",L.T("Slower"),()=>StepSpeed(-1),true);slower.Margin=new Thickness(4,0,0,0);
            var faster=key("C  +",L.T("Faster"),()=>StepSpeed(1),true);
            doubleSpeed=key("V  "+L.T("2×"),L.T("2× speed. Press again for 1×"),ToggleDoubleSpeed,false);
            doubleSpeed.Margin=new Thickness(4,0,0,0);
            row.Children.Add(reset);row.Children.Add(slower);row.Children.Add(speedText);row.Children.Add(faster);row.Children.Add(doubleSpeed);
            ShowSpeed();
            return row;
        }
        Button doubleSpeed;
        void ToggleDoubleSpeed() {SetSpeed(Math.Abs(speed-2)<0.0001?1:2);}
        void ShowSpeed() {
            speedText.Text=L.F("{0}×",speed.ToString("0.##",CultureInfo.CurrentCulture));
            bool on=Math.Abs(speed-2)<0.0001;
            doubleSpeed.FontWeight=on?FontWeights.Bold:FontWeights.Normal;
            if(on)doubleSpeed.SetResourceReference(Control.BorderBrushProperty,"Accent");
            else doubleSpeed.ClearValue(Control.BorderBrushProperty);
        }
        internal double Speed {get {return speed;}}
        internal void SetSpeed(double value) {
            value=Math.Max(SlowestSpeed,Math.Min(FastestSpeed,Math.Round(value,2)));
            if(Math.Abs(value-speed)<0.0001)return;
            speed=value;ShowSpeed();
            if(IsLoaded)Changed();
        }
        // The next step on the 0.1 grid, so 0.75× goes to 0.8× or 0.7×.
        void StepSpeed(int direction) {
            double at=speed/0.1;
            SetSpeed((direction>0?Math.Floor(at+1e-6)+1:Math.Ceiling(at-1e-6)-1)*0.1);
        }
        // "Speed  [Z Reset] [X −] 1× …": the name and its control on one row. Where the room is too narrow for both,
        // the control goes under its name.
        FrameworkElement Field(string label,FrameworkElement control) {
            var row=new WrapPanel();
            var name=UI.Text(label,13);name.Margin=new Thickness(0,3,0,3);fieldNames.Add(name);
            control.HorizontalAlignment=HorizontalAlignment.Left;control.VerticalAlignment=VerticalAlignment.Center;
            row.Children.Add(name);row.Children.Add(control);
            return row;
        }
        static void Detach(FrameworkElement item) {
            var parent=item.Parent as Panel;
            if(parent!=null)parent.Children.Remove(item);
        }
        // Puts the choices beside the preview (wide) or under the strip (narrow).
        void Arrange(bool wideNow) {
            if(arranged && wideNow==wide)return;
            arranged=true;wide=wideNow;
            foreach(var item in new FrameworkElement[]{back,summary,actions,speedField,loopField,fpsField,heightField,qualityField,formatField,explain,narrowRow,more})Detach(item);
            var fields=new[]{speedField,loopField,fpsField,heightField,qualityField,formatField};
            if(wide) {
                foreach(var field in fields){field.Margin=new Thickness(0,0,0,6);sidePanel.Children.Add(field);}
                explain.Margin=new Thickness(0,6,0,0);sidePanel.Children.Add(explain);
                summary.Margin=new Thickness(0,12,0,0);summary.HorizontalAlignment=HorizontalAlignment.Left;sidePanel.Children.Add(summary);
                actions.Margin=new Thickness(12,0,0,0);DockPanel.SetDock(actions,Dock.Right);lastRow.Children.Insert(0,actions);
                // Back to the cover stands beside the two times.
                back.Margin=new Thickness(10,0,0,0);chips.Children.Add(back);
            } else {
                summary.Margin=new Thickness(12,0,0,0);summary.HorizontalAlignment=HorizontalAlignment.Right;
                DockPanel.SetDock(summary,Dock.Right);lastRow.Children.Insert(0,summary);
                speedField.Margin=new Thickness(0,0,18,0);loopField.Margin=new Thickness(0);
                narrowRow.Children.Add(speedField);narrowRow.Children.Add(loopField);
                foreach(var field in new[]{fpsField,heightField,qualityField,formatField}){field.Margin=new Thickness(0,0,14,0);moreGrid.Children.Add(field);}
                explain.Margin=new Thickness(0,2,0,4);moreChoices.Children.Add(explain);
                actions.Margin=new Thickness(0,12,0,0);
                narrowHost.Children.Add(narrowRow);narrowHost.Children.Add(more);narrowHost.Children.Add(actions);
                // The row of the times also holds the summary here, so Back to the cover goes up beside the other
                // button over the strip, where there is room.
                back.Margin=new Thickness(8,0,0,0);DockPanel.SetDock(back,Dock.Right);compactGuide.Children.Insert(0,back);
            }
            leftScroll.Visibility=rightScroll.Visibility=wide?Visibility.Visible:Visibility.Collapsed;
            compactGuide.Visibility=wide?Visibility.Collapsed:Visibility.Visible;
            hintsPlaced=false;
        }
        // The hints go in the free middle of the last row (between the times and the buttons) when two lines of them
        // fit there; with less room, or with a note to show first, they get a row of their own over the strip.
        // Decided from widths alone, so it can't flip back and forth.
        void PlaceHints() {
            var open=new Size(Double.PositiveInfinity,Double.PositiveInfinity);
            double[] w=new double[hintRow.Children.Count];
            for(int i=0;i<w.Length;i++){var hint=hintRow.Children[i];hint.Measure(open);w[i]=hint.Visibility==Visibility.Visible?hint.DesiredSize.Width:0;}
            double need=Math.Max(w[0]+w[1],w[2]);
            chips.Measure(open);actions.Measure(open);
            double room=page.ActualWidth-chips.DesiredSize.Width-actions.DesiredSize.Width-actions.Margin.Left-24;
            bool below=wide && help.Visibility!=Visibility.Visible && room>=need;
            if(hintsPlaced && below==hintsBelow)return;
            hintsPlaced=true;hintsBelow=below;
            Detach(hintBar);
            if(below) {
                hintBar.Margin=new Thickness(18,0,6,0);hintBar.HorizontalAlignment=HorizontalAlignment.Center;lastRow.Children.Add(hintBar);
            } else {
                hintBar.Margin=new Thickness(0,8,0,0);hintBar.HorizontalAlignment=HorizontalAlignment.Left;
                bottom.Children.Insert(bottom.Children.IndexOf(stripFrame),hintBar);
            }
        }
        // The preview is as tall as the room above the strip; what is left on its sides goes to the two side columns.
        void Fit() {
            double pageWidth=page.ActualWidth,pageHeight=page.ActualHeight;
            if(pageWidth<=0 || pageHeight<=0)return;
            // Decided from the window's size alone (not from the room left after arranging), so it can't flip back and forth.
            double roomIfWide=pageHeight-WideBottom;
            Arrange(roomIfWide>=330 && (pageWidth-roomIfWide*9/16)/2-SideGap>=SideMin);
            PlaceHints();
            double room=middle.ActualHeight,across=middle.ActualWidth;
            if(room<=0 || across<=0)return;
            double width=Math.Floor(Math.Min(room*9/16,across));
            previewFrame.Width=width;previewFrame.Height=Math.Floor(width*16/9);
            if(wide)leftScroll.Width=rightScroll.Width=Math.Max(0,Math.Min(SideMax,Math.Floor((across-width)/2-SideGap)));
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
                // The last picture is cut where the clip ends, so the empty strip after it shows the end.
                double width=cell-ThumbGap;
                if(known)width=Math.Min(width,X(duration)-X(at));
                // A dark gray box stands in while a picture loads, so it never looks like the video ended.
                var placeholder=new Border {Width=Math.Max(1,width),Height=PictureHeight,Background=LoadingCell,ClipToBounds=true};
                var image=new Image {Stretch=Stretch.UniformToFill,Width=Math.Max(1,cell-ThumbGap),Height=PictureHeight,HorizontalAlignment=HorizontalAlignment.Left};
                RenderOptions.SetBitmapScalingMode(image,BitmapScalingMode.HighQuality);
                BitmapSource picture;
                if(thumbs.TryGetValue(k,out picture) || (picture=Earlier(k*step+step/2))!=null)image.Source=picture;
                placeholder.Child=image;
                Canvas.SetLeft(placeholder,X(at));cells.Children.Add(placeholder);
            }
        }
        // Over the pictures only a fine line just outside the start and the end, so it never hides a scene that
        // goes into the GIF; everything else sits in the rails (see PartLook).
        void Overlay() {
            double left=X(start),right=X(end);
            var screen=PresentationSource.FromVisual(strip);
            stripPixel=screen==null || screen.CompositionTarget==null?1:1/screen.CompositionTarget.TransformToDevice.M11;
            double line=PartLook.Line(stripPixel);
            // The track is as long as the clip (as much of it as the strip shows).
            bool known=!Double.IsNaN(duration) && duration>0;
            PartLook.Range(strip,left,right,Math.Max(0,X(0)),known?Math.Min(strip.ActualWidth,X(duration)):strip.ActualWidth,PictureHeight,stripPixel);
            // The cover's card goes in before the knobs: a knob standing on the cover's scene stays whole over it.
            PartLook.PlaceCoverMark(coverMark,X(source.Moment));strip.Children.Add(coverMark);
            // The knob in use goes in last, so in a very short part the other one never covers it.
            foreach(int which in activeHandle==1?new[]{2,1}:new[]{1,2})
                PartLook.Handle(strip,which==1?left-line/2:right+line/2,activeHandle==which);
            strip.Children.Add(playMark);PlacePlayMark();
        }
        // Where the preview is in the original, as a dot under the chosen part.
        void ShowPlayMark(int index) {
            playAt=timeline!=null && index>=0 && index<timeline.Length?timeline[index]:Double.NaN;
            PlacePlayMark();
        }
        void PlacePlayMark() {
            bool show=!Double.IsNaN(playAt) && player.Playing && dragging==0 && playAt>=start-1 && playAt<=end+1 && strip.ActualWidth>0;
            playMark.Visibility=show?Visibility.Visible:Visibility.Collapsed;
            if(show)PartLook.PlacePlayMark(playMark,X(playAt),TopRail+PictureHeight);
        }
        // One hint: the action in bold and what it does, in a few words. Plain text, so it never looks like one
        // more button in the row of buttons it shares.
        FrameworkElement Hint(int which,string cap,string words) {
            var row=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(0,1,18,1)};
            hintCaps[which]=UI.Text(cap,12);hintCaps[which].FontWeight=FontWeights.SemiBold;
            hintWords[which]=UI.Text(words,12,true);hintWords[which].Margin=new Thickness(6,0,0,0);
            row.Children.Add(hintCaps[which]);row.Children.Add(hintWords[which]);
            return row;
        }
        // Lights up the hint for what the pointer can do where it is: 0 a handle, 1 the pictures, 2 outside the part.
        void PointAt(int which) {
            if(which==pointed || hintCaps==null)return;
            pointed=which;
            for(int i=0;i<hintCaps.Length;i++) {
                hintCaps[i].SetResourceReference(TextBlock.ForegroundProperty,i==which?"Accent":"Text");
                hintWords[i].SetResourceReference(TextBlock.ForegroundProperty,i==which?"Text":"Subtle");
            }
        }

        static readonly SolidColorBrush LoadingCell=Frozen(Color.FromRgb(0x2B,0x2E,0x33));
        static SolidColorBrush Frozen(Color c){var b=new SolidColorBrush(c);b.Freeze();return b;}
        readonly HashSet<int> failed=new HashSet<int>(),exact=new HashSet<int>(),loadingExact=new HashSet<int>();        Task<MpvThumbnails> roughPlayer,exactPlayer;
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
        // The preview's scenes are already the cover's part in its 9:16 shape, so the strip uses them
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
            double x=Pointer(e).X;
            if(e.ClickCount==2 && DoubleClickStrip(x)){e.Handled=true;return;}
            PressStrip(x);
            strip.CaptureMouse();e.Handled=true;
        }
        // A double click outside the chosen part brings the nearer end there: left of it the start, right of it the end.
        internal bool DoubleClickStrip(double x) {
            int which=x<X(start)?1:x>X(end)?2:0;
            if(which==0)return false;
            activeHandle=which;
            PlaceHandle(which,Ms(x));
            Layout();UpdateChips();UpdateSummary();SchedulePrepare();
            return true;
        }
        // Pointer logic in strip coordinates, so it can also be checked without a real mouse.
        internal double ViewStart {get {return viewStart;}}
        internal double SpanMs {get {return span;}}
        internal bool WholeClipShown {get {return !Double.IsNaN(duration) && duration>0 && duration<=span+1;}}
        // The handle a press at x takes hold of: 1 the start, 2 the end, 0 neither. In a short part both are
        // within reach, and then it is the nearer one.
        int HandleAt(double x) {
            double left=X(start),right=X(end);
            bool onStart=Math.Abs(x-left)<=14 || (x<left && left-x<=16),onEnd=Math.Abs(x-right)<=14 || (x>right && x-right<=16);
            if(onStart && onEnd)return Math.Abs(x-left)<=Math.Abs(x-right)?1:2;
            return onStart?1:onEnd?2:0;
        }
        internal void PressStrip(double x) {
            int handle=HandleAt(x);
            if(handle!=0){dragging=handle;activeHandle=handle;PointAt(0);}
            else {dragging=3;dragOrigin=x;dragView=viewStart;strip.Cursor=Cursors.SizeWE;}
            Layout();UpdateChips();SetStatus();
        }
        void StripMove(object sender,MouseEventArgs e) {Moves++;MoveStrip(Pointer(e).X);}
        internal void MoveStrip(double x) {
            if(dragging==0) {
                double left=X(start),right=X(end);
                // The hint that lights up is what a press here would do, so it uses the same reach as PressStrip.
                strip.Cursor=Math.Abs(x-left)<=14 || Math.Abs(x-right)<=14?Cursors.SizeWE:Cursors.Hand;
                PointAt(HandleAt(x)!=0?0:x<left || x>right?2:1);
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
        // One wheel notch moves a quarter of the strip: down goes later (right), up goes earlier (left).
        internal void WheelStrip(int delta) {
            if(dragging!=0 || delta==0)return;
            double moved=ClampView(viewStart-delta/120.0*span/4);
            if(moved==viewStart)return;
            viewStart=moved;Layout();
            wheelSettle.Stop();wheelSettle.Start();
        }
        void StripUp(object sender,MouseButtonEventArgs e) {Releases++;if(dragging!=0){strip.ReleaseMouseCapture();FinishDrag();}}
        internal void ReleaseStrip() {if(dragging!=0)FinishDrag();}
        void FinishDrag() {
            bool handle=dragging==1 || dragging==2;
            bool pan=dragging==3;
            dragging=0;edgeScroll.Stop();strip.Cursor=Cursors.Hand;
            if(!strip.IsMouseOver)PointAt(-1); // let go outside the strip: no hint stays lit
            PlacePlayMark();
            if(handle){UpdateChips();UpdateSummary();SchedulePrepare();}
            else if(pan){ShowDraft(draftOwed);SetStatus();}
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

        void Changed() {UpdateSound();UpdateSummary();SchedulePrepare();}
        // The sound box shows only when the file can have sound, and says why when it can't be used right now.
        void UpdateSound() {
            if(sound==null || format==null || loopChoices==null)return;
            bool possible=format.SelectedIndex==2 && VideoFrames.Supports(source.Path);
            bool forward=Picked(loopChoices)==0;
            sound.Visibility=soundNote.Visibility=possible?Visibility.Visible:Visibility.Collapsed;
            sound.IsEnabled=forward;
            soundNote.Text=NoticeLook.KeepWords(!forward?L.T("Sound goes in only when the play style is [Loop]."):
                L.T("In the folder window the sound plays when the mouse is on the tile. It also plays when you open or share the file somewhere else."));
        }
        MotionRequest Read() {
            return new MotionRequest {Before=(source.Moment-start)/1000,After=(end-source.Moment)/1000,Fps=rates[fps.SelectedIndex],
                Height=heights[height.SelectedIndex],Format=Formats[format.SelectedIndex],Quality=quality.SelectedIndex,
                Speed=speed,Loop=Loops[Picked(loopChoices)],
                Sound=sound!=null && sound.IsChecked==true && format.SelectedIndex==2 && VideoFrames.Supports(source.Path)};
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
            lastFps=rates[fps.SelectedIndex];lastHeight=heights[height.SelectedIndex];lastQuality=quality.SelectedIndex;lastFormat=format.SelectedIndex;
            if(sound!=null && sound.Visibility==Visibility.Visible)lastSound=sound.IsChecked==true;
            if(fromPart && partGiver!=null)partGiver.DropPart();
            Value=request;DialogResult=true;
        }
        // "This cover is 1080 pixels tall in the original…": shown under the height box only when the chosen
        // height is more than the cover has, with the real numbers.
        void UpdateHeightNote() {
            int chosen=heights[height.SelectedIndex];
            int own=Math.Max(2,(int)Math.Round(coverPixels/2)*2);
            bool smaller=coverPixels>2 && chosen>own+1;
            heightNote.Visibility=smaller?Visibility.Visible:Visibility.Collapsed;
            if(smaller)heightNote.Text=NoticeLook.KeepWords(L.F("This cover is {0} pixels tall in the original. So it is made {0} pixels tall, not {1} pixels. Stretching it would only make it blurry, so it is never made bigger than the original.",own,chosen));
        }
        void UpdateSummary() {
            UpdateHeightNote();
            var request=Read();
            double mb;string size;
            double seconds=request.PlaySeconds;int frames=request.Frames;
            if(PreparedFor(request)) {
                mb=preparedBytes/1048576.0;size=mb<1?L.F("{0} KB",Math.Max(1,Math.Round(preparedBytes/1024.0)).ToString("N0",CultureInfo.CurrentCulture)):L.F("{0} MB",Mb(mb));
                // The real picture size: a small original can't fill the height picked in More choices.
                if(preparedResult!=null && preparedResult.Width>0)size=preparedResult.Width+"×"+preparedResult.Height+" · "+size;
                if(preparedFrames>0){frames=preparedFrames;seconds=preparedSeconds;}
            } else {mb=Estimate(request,coverPixels);size=mb<1?L.F("about {0} KB",Math.Max(1,Math.Round(mb*1024/10)*10).ToString("N0",CultureInfo.CurrentCulture)):L.F("about {0} MB",Mb(mb));}
            summary.Text=L.F("{0:0.0} s · {1:N0} pictures · {2}",seconds,frames,size);
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

        // The little label over the preview: "paused", the progress when nothing else is moving, or a problem.
        // The card on the left (one line in a narrow window) says the rest.
        void SetStatus() {
            bool making=preparing>=0;
            // While the preview plays nothing sits on it: the card (or the line in a narrow window) already says it is the preview.
            string text=problem!=null?problem:making && !draftOn?L.F("Making the real file · {0}%",(int)Math.Floor(preparing*100)):held?L.T("Paused · click to play"):null;
            status.Text=text??"";
            statusPill.Visibility=text==null?Visibility.Collapsed:Visibility.Visible;
            UpdateGuide();
        }
        void ShowProgress() {
            guideFill.Width=Math.Max(0,guideTrack.ActualWidth*Math.Max(0,Math.Min(1,preparing)));
        }
        void UpdateGuide() {
            if(fps==null || format==null)return;
            bool manual=!AutoMake,making=preparing>=0;
            bool ready=!making && !draftOn && preparedFile!=null && PreparedFor(Read());
            int percent=(int)Math.Floor(Math.Max(0,preparing)*100);
            string title,body,progress=null,foot=null;
            bool offer=false;
            if(problem!=null) {
                title=L.T("Couldn't make the real file");body=problem;offer=manual;
            } else if(ready) {
                title="✓  "+L.T("This is how the real file looks");
                body=preparedFrames>0?
                    L.T("The file that was just made is playing as it is. The movement, the colors and the size are exactly what will be saved."):
                    L.T("This file is long, with more than 300 pictures, so the screen shows a lighter copy of it. The size shown is the real file's.");
                foot=L.F("If you like it, click [{0}]. The file is already made, so it finishes right away.",makeLabel)+"\n\n"+
                    (manual?L.T("If you change the part or a setting, it goes back to the preview."):
                        L.T("If you change the part or a setting, the preview shows first again and the real file is made anew."));
            } else if(dragging==1 || dragging==2) {
                title=L.T("You are setting the part");
                body=L.T("Let go of the handle and that part shows in the preview right away.");
                if(!manual)foot=L.T("Then the real file starts being made in the background.");
            } else if(!draftOn && making) {
                // The preview couldn't be shown (or isn't there yet), so only the real file's progress is.
                title=L.T("Making the real file");
                progress=L.F("Making the real file · {0}%",percent);
                body=L.T("When it is done, it plays exactly as it will be saved.");
            } else {
                title=L.T("This is the preview for now");
                double apart=(draft!=null?draft.PartStep(start,end):PreviewStepMs)/1000.0;
                body=L.F("So that you can see the result right away when you change the part, scenes are taken from the original every {0} seconds and shown first. So it can look a little choppier and less sharp than the real file. Just check where it starts and where it ends.",
                    apart.ToString("0.###",CultureInfo.CurrentCulture));
                if(manual) {
                    foot=L.F("The real file starts being made when you click [{0}]. The window closes right away, and a notice at the bottom right tells you when it is done.",makeLabel);
                    if(making)progress=L.F("The real file is being made in the background · {0}%",percent);else offer=true;
                } else {
                    progress=making?L.F("The real file is being made in the background · {0}%",percent):L.T("The real file will start being made in a moment.");
                    foot=L.F("You don't have to wait until it is done. Click [{0}] any time and it is made just as you chose.",makeLabel);
                }
            }
            guideTitle.Text=NoticeLook.KeepWords(title);
            guideTitle.SetResourceReference(TextBlock.ForegroundProperty,ready?"Accent":"Text");
            guideBody.Text=NoticeLook.KeepWords(body);
            guideProgress.Text=progress==null?"":NoticeLook.KeepWords(progress);
            guideProgress.Visibility=progress==null?Visibility.Collapsed:Visibility.Visible;
            guideTrack.Visibility=making && problem==null?Visibility.Visible:Visibility.Collapsed;
            ShowProgress();
            guideFoot.Text=foot==null?"":NoticeLook.KeepWords(foot);
            guideFoot.Visibility=foot==null?Visibility.Collapsed:Visibility.Visible;
            realButton.Visibility=realCompact.Visibility=offer?Visibility.Visible:Visibility.Collapsed;
            compactText.Text=title+(progress==null?"":"  ·  "+progress);
            compactText.SetResourceReference(TextBlock.ForegroundProperty,ready?"Accent":"Subtle");
        }
        internal string GuideText {get {return guideTitle.Text+"|"+guideProgress.Text;}}
        // [See how the real file looks]: makes the real file now, for someone who has it made only when asked.
        void RealNow() {prepareSoon.Stop();Prepare();}
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
            preparing=-1;
            if(AutoMake){prepareSoon.Interval=TimeSpan.FromMilliseconds(Math.Max(200,MakeDelayMs));prepareSoon.Start();}
            ShowDraft();
            SetStatus();
        }
        // The preview: scenes already read are reused, only new ones are read, and it plays as soon as they are there.
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
                // The real file still follows, so a failed preview is not shown as a problem.
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
            if(key==preparedKey && preparedFile!=null && File.Exists(preparedFile)) {
                // The choices came back to the file already made: its own frames show again instead of the preview.
                if(draftOn && preparedPlay!=null){timeline=preparedTimeline;player.Start(preparedPlay);player.Hold=held;}
                draftOn=false;SetStatus();UpdateSummary();return;
            }
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
                preparedPlay=frames;preparedTimeline=timeline;
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
            if(forward.Count==0)throw new InvalidDataException(L.T("There are no scenes to show."));
            return ArrangeWith(forward,(int)Math.Round(1000.0/Math.Max(1,rate)),r);
        }
        // The same, with the time each picture stays in milliseconds.
        static MotionFrames ArrangeWith(List<BitmapSource> forward,int delay,MotionRequest r) {
            if(forward.Count==0)throw new InvalidDataException(L.T("There are no scenes to show."));
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
        // "Height  [1280 px ▾]": the name and the box on one row. full is the longer name read out and shown on hover.
        ComboBox Choice(Panel panel,string label,string full,string[] options,int index) {
            var row=new DockPanel();
            var name=UI.Text(label,13);fieldNames.Add(name);
            if(full!=null)name.ToolTip=full;
            DockPanel.SetDock(name,Dock.Left);row.Children.Add(name);
            var combo=new ComboBox {ItemsSource=options,SelectedIndex=Math.Max(0,Math.Min(options.Length-1,index)),Margin=new Thickness(0,0,0,0),
                Style=(Style)Application.Current.FindResource("OptionsChoice")};
            // A long name (the file types) ends in … in the box when the column is narrow; the open list shows it whole.
            var line=new FrameworkElementFactory(typeof(TextBlock));
            line.SetBinding(TextBlock.TextProperty,new System.Windows.Data.Binding());
            line.SetValue(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis);
            combo.ItemTemplate=new DataTemplate {VisualTree=line};
            AutomationProperties.SetName(combo,full??label);row.Children.Add(combo);panel.Children.Add(row);return combo;
        }
        // For checks: choose by position as a person would by clicking.
        internal void ChooseForChecks(int speed,int loop) {
            if(speed>=0)SetSpeed(Speeds[speed]);
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
        internal static void ClearCache() {lock(cache)cache.Clear();}
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
    // An MP4 cover plays as a video in the one tile under the mouse: picture and sound come from the same file,
    // so they stay together and the cover plays to its end however long it is (reading pictures one by one stops at 300).
    // Only one plays at a time, and the file is let go as soon as it stops, so the cover can still be renamed, moved or deleted.
    internal static class TileSound {
        static System.Windows.Media.MediaPlayer player;
        static object holder;
        static Action whenStopped;
        // Whether the cover file itself carries sound.
        internal static bool Has(Photo photo) {
            return photo.MotionSettings!=null && photo.MotionSettings.WithSound && IsVideo(photo);
        }
        internal static bool IsVideo(Photo photo) {
            return photo.MotionFile!=null && photo.MotionFile.EndsWith(".mp4",StringComparison.OrdinalIgnoreCase);
        }
        // shown: the moving picture, once the file is open. stopped: it is over (the tile shows its still picture again).
        // failed: Windows couldn't play the file. top and bottom: the rows to show of a cover made before covers were trimmed.
        internal static void Play(object who,string file,int volume,bool silent,double top,double bottom,
            Action<System.Windows.Media.ImageSource> shown,Action stopped,Action failed) {
            Stop();
            try {
                var made=new System.Windows.Media.MediaPlayer {Volume=Math.Max(0,Math.Min(100,volume))/100.0,IsMuted=silent};
                made.MediaFailed+=delegate {
                    if(player!=made)return;
                    Stop();
                    if(failed!=null)failed();
                };
                made.MediaOpened+=delegate {
                    if(player!=made)return;
                    int w=made.NaturalVideoWidth,h=made.NaturalVideoHeight;
                    if(w<=0 || h<=0) { // no picture to show: the same as not being able to play it
                        Stop();
                        if(failed!=null)failed();
                        return;
                    }
                    System.Windows.Media.Drawing picture=new System.Windows.Media.VideoDrawing {Player=made,Rect=new Rect(0,0,w,h)};
                    // A cover made before covers were trimmed is the full 9:16 with empty rows: only the picture's rows show.
                    if((top>0 || bottom<1) && Math.Abs(h/(double)w-16.0/9)<=0.012) {
                        double first=Math.Ceiling(top*h-1e-6),last=Math.Floor(bottom*h+1e-6);
                        if(last-first>=2) {
                            var group=new System.Windows.Media.DrawingGroup {ClipGeometry=new System.Windows.Media.RectangleGeometry(new Rect(0,first,w,last-first))};
                            group.Children.Add(picture);picture=group;
                        }
                    }
                    if(shown!=null)shown(new System.Windows.Media.DrawingImage(picture));
                };
                made.MediaEnded+=delegate {if(player==made)try {made.Position=TimeSpan.Zero;made.Play();} catch(Exception) {}};
                // Remembered before it opens, so a failure right away is still closed and reported.
                player=made;holder=who;whenStopped=stopped;
                made.Open(FileUri(file));made.Play();
            } catch(Exception) {Stop();if(failed!=null)failed();}
        }
        // A file address that keeps "#", "%" and the like in a name as part of the name.
        static Uri FileUri(string file) {
            var parts=Path.GetFullPath(file).Replace('\\','/').Split('/');
            for(int i=0;i<parts.Length;i++)if(!(i==0 && parts[i].EndsWith(":")))parts[i]=Uri.EscapeDataString(parts[i]);
            return new Uri("file:///"+String.Join("/",parts));
        }
        internal static bool PlayingFor(object who) {return holder==who && player!=null;}
        // Safe from any thread: file work that runs in the background calls this before it touches a cover file.
        internal static void Stop(object who=null) {
            if(who!=null && holder!=who)return;
            var old=player;var after=whenStopped;
            player=null;holder=null;whenStopped=null;
            if(old==null)return;
            Action close=()=>{
                try {if(after!=null)after();} catch(Exception) {}
                try {old.Stop();old.Close();} catch(Exception) {}
            };
            try {
                if(old.Dispatcher.CheckAccess())close();
                else old.Dispatcher.Invoke(DispatcherPriority.Send,TimeSpan.FromMilliseconds(700),close);
            } catch(Exception) {}
        }
    }
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

    // The preview of Make GIF cover. It reads scenes at a fixed step (0.1 s by default), already in the cover's
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
            if(width<=0 || height<=0)throw new InvalidDataException(L.T("Could not read the scenes."));
            // The bigger the original, the smaller the preview.
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
        // From Guide & Options: how hard WebP is compressed. 0 smallest file (slow), 1 faster, 2 fastest (bigger file).
        internal static int WebpEffort;

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
                Math.Round(s.Rotation,2).ToString(c),"light"+s.Brightness.ToString(c),Math.Round(r.Before*1000).ToString(c),Math.Round(r.After*1000).ToString(c),
                r.Fps.ToString(c),r.Height.ToString(c),r.Format,r.Speed.ToString(c),r.Loop,r.Quality.ToString(c),"trimmed",
                r.Format=="webp"?"effort"+WebpEffort.ToString(c):"",r.WithSound?"sound":""});
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
        // trim: leave out the empty rows above and below the picture, like the JPG cover. The preview keeps the
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
            if(iw<2 || ih<2)throw new InvalidDataException(L.T("There are no scenes to show."));
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
            string light=Light.Filter(brightness); // the curve, or an old cover's white or black sheet
            if(light!=null)steps.Add(light);
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
            if(width<=0 || height<=0)throw new InvalidDataException(L.T("Could not read the scenes."));
            if(Math.Abs(size.Angle%180)==90){int swap=width;width=height;height=swap;}
            double duration=VideoFrames.DurationMs(original,token);
            double start=Math.Max(0,source.Moment-request.Before*1000);
            double end=source.Moment+request.After*1000;
            if(duration>0)end=Math.Min(end,duration);
            if(end-start<50)throw new InvalidDataException(L.T("There are no scenes to show."));
            result.Before=(source.Moment-start)/1000;result.After=Math.Max(0,end-source.Moment)/1000;
            int outW,outH;
            string chain=Filters(source.Crop,source.Rotation,source.Brightness,width,height,request,true,out outW,out outH);
            result.Width=outW;result.Height=outH;
            int forward=Math.Max(1,(int)Math.Round((end-start)/1000/Math.Max(0.1,request.Speed)*request.Fps,MidpointRounding.AwayFromZero));
            // -t before -i makes FFmpeg stop reading at the end of the part. Given after -i, the GIF palette step
            // waited for the end of the whole video (a 3-second GIF from a long 4K video took minutes).
            string input=(start>0?"-ss "+VideoFrames.Seconds(start)+" ":"")+"-t "+N((end-start)/1000)+" -i "+VideoFrames.Quote(original)+" ";
            Run(input,chain,request,forward,outW,outH,part,token,progress,0,1,copies,copyHeight,request.WithSound);
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
                                if(frame.PixelWidth!=width || frame.PixelHeight!=height)throw new InvalidDataException(L.T("The video scene is in a format that can't be read."));
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
            Action<double> progress,double progressFrom,double progressSpan,Action<BitmapSource,int> copies,int copyHeight,bool sound=false) {
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
                    // Lighter compression is quicker to make and a little bigger; the picture quality setting stays the same.
                    codec="-c:v libwebp_anim -quality "+(request.Quality==0?90:request.Quality==2?65:82)+
                        (WebpEffort==1?" -compression_level 2":WebpEffort>=2?" -compression_level 0":"")+" -loop 0 -f webp";
                } else {
                    g.Append(';').Append(current).Append("format=yuv420p[out]");
                    codec="-c:v libx264 -preset medium -crf "+(request.Quality==0?17:request.Quality==2?25:20)+" -movflags +faststart -f mp4";
                }
                // Sound: the video's first sound track for the same part, at the same speed. "?" lets a video without sound through.
                string audio=sound && !gif && request.Format=="mp4"?"-map 0:a:0? "+Tempo(request.Speed)+"-c:a aac -b:a 160k -ac 2 ":"";
                string args="-v error -nostdin -y "+input+(audio.Length>0?"":"-an ")+"-sn -dn -filter_complex "+VideoFrames.Quote(g.ToString())+" -map [out] "+audio+codec+" "+VideoFrames.Quote(part);
                if(copies!=null)args+=" -map [copy] -c:v rawvideo -pix_fmt bgra -f rawvideo pipe:1";
                // GIF frames come out only after the palette is made, so reading the frames is most of the work.
                double share=gif && palette==null?0.85:0.97;
                Execute(args,copies,copyW,copyH,copyRate,forward,request.Fps,token,progress,progressFrom,progressSpan*share);
                if(progress!=null)progress(progressFrom+progressSpan);
            } finally {if(palette!=null)File.Delete(palette);}
        }
        // The sound keeps its pitch at another speed. One step of FFmpeg's atempo goes from half to double speed, so slower or faster takes several.
        static string Tempo(double speed) {
            // The picture's times start again from zero, so the sound's do too; otherwise the two drift apart
            // in files whose times don't begin at zero (TS files, losslessly cut clips).
            var steps=new List<string> {"asetpts=PTS-STARTPTS"};
            if(Math.Abs(speed-1)>=0.005) {
                double left=speed;
                while(left<0.5){steps.Add("atempo=0.5");left/=0.5;}
                while(left>2){steps.Add("atempo=2.0");left/=2;}
                steps.Add("atempo="+left.ToString("0.####",CultureInfo.InvariantCulture));
            }
            return "-af "+String.Join(",",steps)+" ";
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
