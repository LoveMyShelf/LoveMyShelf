using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace LoveMyShelf {
    // The large view's [Adjust] panel: brightness, contrast, saturation, color temperature and sharpness in a small card
    // at the top right of the video, instead of unfolding inside the tall controls window that covered the picture.
    // 2026-11-27 user: "오른쪽 클릭하면 화면에 다 가려서 보정할 때 화면이 잘 안 보여". Debate (DeepSeek, Gemini, Codex, 2 rounds):
    // top right, a grip to move it and the place remembered; while a value changes (hold, wheel, drag) the other lines
    // fade so the picture shows; the lines work as in the cover editor (AdjustRows, made by the adjust session).
    public partial class Viewer {
        Popup adjustPanel;
        Border adjustCard;
        AdjustRows adjustLines;
        FrameworkElement adjustHead;
        DispatcherTimer adjustTicker;
        DateTime adjustFirst,adjustLast;
        int adjustField=-1;
        bool adjustFaded;
        // What the lines were last told or last sent, so only a change from elsewhere (Q, W, E, R, Reset) is shown
        // again. Showing the panel's own value back would cancel a step a line has not applied yet (it applies 60 ms
        // later): holding an arrow then bounced -1 / +1 (2026-11-27 user report).
        AdjustValues adjustKnown;
        // [Reset] and [Auto] both turn into [Undo] until something else changes (ResetToggle, the adjust session's).
        ResetToggle adjustReset,adjustAuto;
        // The panel's distance from the video's top-right corner (DIPs), kept between runs.
        static Vector adjustOffset=new Vector(12,12);
        static bool adjustOffsetRead;
        const double AdjustWidth=318;
        static string AdjustPlaceFile {get {return Path.Combine(ErrorLog.Folder,"viewer-adjust-place.txt");}}

        // From the controls window: it closes and the panel opens. Again from the panel's [← Menu].
        void OpenAdjustPanel() {
            if(mpv==null || !motion)return;
            if(keyPanel!=null)keyPanel.IsOpen=false;
            if(adjustPanel==null)BuildAdjustPanel();
            ReadAdjustPlace();
            ShowAdjustValues();
            PlaceAdjustPanel();
            adjustPanel.IsOpen=true;
            adjustTicker.Start();
        }
        void CloseAdjustPanel() {if(adjustPanel!=null && adjustPanel.IsOpen)adjustPanel.IsOpen=false;}

        void BuildAdjustPanel() {
            adjustLines=new AdjustRows((field,v)=>{SetPlaybackAdjust(field,v);adjustKnown=AdjustNow;NoteAdjusting(field);LookChanged();});
            // Head: a grip to move the panel, then Reset, back to the menu, and close.
            var head=new DockPanel {Margin=new Thickness(0,0,0,4),LastChildFill=true};
            Func<string,string,Action,Button> small=(text,tip,act)=>{
                var b=new Button {Content=text,MinHeight=24,MinWidth=0,Padding=new Thickness(8,0,8,1),Margin=new Thickness(4,0,0,0),FontSize=12.5,Focusable=false,ToolTip=tip};
                if(act!=null)b.Click+=delegate {act();};
                return b;
            };
            var close=small("✕",L.P("Close (Esc)","닫기 (Esc)"),CloseAdjustPanel);DockPanel.SetDock(close,Dock.Right);
            var menu=small(L.P("← Menu","← 메뉴"),L.P("Back to all the controls","모든 조작으로 돌아가요"),()=>{CloseAdjustPanel();ShowControls();});DockPanel.SetDock(menu,Dock.Right);
            var reset=small(L.T("Reset"),L.P("Brightness and colors back to how the file is","밝기와 색을 파일 그대로 되돌려요"),null);DockPanel.SetDock(reset,Dock.Right);
            adjustReset=ResetToggle.Attach(reset,ReadLook,SetLook,()=>{
                playbackBrightness=0;playbackStyle=Light.Soft;playbackAdjust=AdjustValues.None;brightnessChosen=adjustChosen=true;
                ApplyPlaybackBrightness();seekPreview.Brightness=PlaybackValue;ShowAdjustValues();
            });
            string autoTip=L.P("Looks at the scene on screen now and sets brightness and colors for the whole video, as the cover editor's Auto does. Press again to go back.",
                "지금 화면의 장면을 보고 밝기와 색을 정해서 영상 전체에 적용해요. 표지 창의 자동 보정과 같은 계산이에요. 한 번 더 누르면 되돌려요.");
            var auto=small(L.P("Auto","자동"),autoTip,null);DockPanel.SetDock(auto,Dock.Right);
            adjustAuto=ResetToggle.For(auto,ReadLook,SetLook,AutoAdjust);
            auto.Click+=delegate {
                adjustAuto.Press();
                // Its own words: what [Undo] brings back is the look from just before Auto.
                if(adjustAuto.CanUndo)auto.ToolTip=L.P("Brings back the brightness and colors from just before Auto.","자동 보정 바로 전 밝기와 색으로 돌아가요.");
                adjustReset.Changed();
            };
            reset.Click+=delegate {adjustAuto.Changed();};
            var grip=new TextBlock {Text="⠿  "+L.P("Adjust","보정"),FontSize=13.5,FontWeight=FontWeights.SemiBold,VerticalAlignment=VerticalAlignment.Center,
                Cursor=Cursors.SizeAll,Background=Brushes.Transparent,Padding=new Thickness(2,2,8,2),
                ToolTip=L.P("Drag here to move the panel. It stays where you put it.","여기를 끌어 패널을 옮겨요. 놓은 자리를 기억해요.")};
            grip.SetResourceReference(TextBlock.ForegroundProperty,"Text");
            head.Children.Add(close);head.Children.Add(menu);head.Children.Add(reset);head.Children.Add(auto);head.Children.Add(grip);
            adjustHead=head;
            var stack=new StackPanel();stack.Children.Add(head);stack.Children.Add(adjustLines);
            adjustCard=new Border {Child=stack,Width=AdjustWidth,CornerRadius=new CornerRadius(8),Padding=new Thickness(10,8,10,8),BorderThickness=new Thickness(1),
                Effect=new System.Windows.Media.Effects.DropShadowEffect {Color=Colors.Black,BlurRadius=8,ShadowDepth=2,Direction=270,Opacity=0.3}};
            adjustCard.SetResourceReference(Border.BorderBrushProperty,"Line");
            adjustCard.SetResourceReference(System.Windows.Documents.TextElement.FontFamilyProperty,"AppFont");
            SetAdjustBackground(false);
            adjustCard.PreviewKeyDown+=(s,e)=>{
                // Esc while typing a number puts the number back; otherwise it closes the panel.
                if(e.Key==Key.Escape && Keyboard.FocusedElement is TextBox && AdjustRow.OwnsKey(e))return;
                if(e.Key==Key.Escape){if(!e.IsRepeat)CloseAdjustPanel();e.Handled=true;}
            };
            // Dragging the grip moves the panel inside the video; the place is kept for next time.
            Point? from=null;Vector startOffset=adjustOffset;
            grip.MouseLeftButtonDown+=(s,e)=>{from=e.GetPosition(ui);startOffset=adjustOffset;grip.CaptureMouse();e.Handled=true;};
            grip.MouseMove+=(s,e)=>{
                if(from==null || !grip.IsMouseCaptured)return;
                var now=e.GetPosition(ui);
                adjustOffset=new Vector(startOffset.X-(now.X-from.Value.X),startOffset.Y+(now.Y-from.Value.Y));
                PlaceAdjustPanel();
            };
            grip.MouseLeftButtonUp+=(s,e)=>{if(from==null)return;from=null;grip.ReleaseMouseCapture();SaveAdjustPlace();e.Handled=true;};
            adjustPanel=new Popup {AllowsTransparency=true,StaysOpen=true,Placement=PlacementMode.Relative,PlacementTarget=ui,Child=adjustCard};
            adjustPanel.Closed+=delegate {adjustTicker.Stop();SetAdjustFaded(false);adjustField=-1;Focus();};
            adjustTicker=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(50)};
            adjustTicker.Tick+=delegate {AdjustTick();};
            // The panel follows the window when it moves, changes size or goes full screen; it closes with the file.
            EventHandler follow=delegate {if(adjustPanel.IsOpen)Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(PlaceAdjustPanel));};
            LocationChanged+=follow;StateChanged+=follow;SizeChanged+=(s,e)=>follow(s,e);
            Deactivated+=delegate {if(adjustPanel.IsOpen && !adjustCard.IsKeyboardFocusWithin)SetAdjustFaded(false);};
            Closed+=delegate {CloseAdjustPanel();adjustTicker.Stop();};
        }
        void SetAdjustBackground(bool faded) {
            var paper=TryFindResource("Paper") as SolidColorBrush;
            var color=paper!=null?paper.Color:Colors.White;
            adjustCard.Background=new SolidColorBrush(color) {Opacity=faded?0.12:0.92};
        }
        // Where the panel goes: its top-right corner at the chosen distance from the video's top-right corner, inside it.
        void PlaceAdjustPanel() {
            if(adjustPanel==null)return;
            adjustCard.Measure(new Size(Double.PositiveInfinity,Double.PositiveInfinity));
            var size=adjustCard.DesiredSize;
            double width=ui.ActualWidth,height=ui.ActualHeight;
            double x=width-size.Width-adjustOffset.X,y=adjustOffset.Y;
            x=Math.Max(4,Math.Min(Math.Max(4,width-size.Width-4),x));
            y=Math.Max(4,Math.Min(Math.Max(4,height-size.Height-4),y));
            adjustOffset=new Vector(width-size.Width-x,y);
            // Set twice: the same numbers again would not move an open popup after the window moved.
            adjustPanel.HorizontalOffset=x+0.5;adjustPanel.HorizontalOffset=x;adjustPanel.VerticalOffset=y;
        }
        static void ReadAdjustPlace() {
            if(adjustOffsetRead)return;
            adjustOffsetRead=true;
            try {
                if(!File.Exists(AdjustPlaceFile))return;
                var parts=File.ReadAllText(AdjustPlaceFile).Trim().Split(',');
                double right,top;
                if(parts.Length==2 && Double.TryParse(parts[0],NumberStyles.Float,CultureInfo.InvariantCulture,out right) &&
                    Double.TryParse(parts[1],NumberStyles.Float,CultureInfo.InvariantCulture,out top) && right>=0 && top>=0 && right<20000 && top<20000)
                    adjustOffset=new Vector(right,top);
            } catch(Exception quiet) {ErrorLog.Quiet(quiet);}
        }
        static void SaveAdjustPlace() {
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(AdjustPlaceFile));
                File.WriteAllText(AdjustPlaceFile,adjustOffset.X.ToString("0",CultureInfo.InvariantCulture)+","+adjustOffset.Y.ToString("0",CultureInfo.InvariantCulture));
            } catch(Exception quiet) {ErrorLog.Quiet(quiet);}
        }
        // [Auto]: the scene on screen now decides; the values then hold for the whole video (2026-11-27 user request,
        // passed on by the adjust session: "누른 순간 화면 기준으로 정해서 영상 전체에 적용"). Same math as the cover editor.
        void AutoAdjust() {
            if(mpv==null)return;
            System.Windows.Media.Imaging.BitmapSource frame=null;
            try {frame=mpv.GrabFrame();} catch(Exception quiet) {ErrorLog.Quiet(quiet);}
            if(frame==null){ShowCornerNote(L.P("Couldn't read the scene on screen. Try again in a moment.","지금 장면을 읽지 못했어요. 잠시 뒤에 다시 눌러 주세요."));return;}
            var estimated=ColorAdjust.EstimateAuto(frame);
            if(estimated.IsIdentity){ShowCornerNote(L.P("Already looks balanced, so nothing was changed.","이미 균형이 괜찮아서 그대로 두었어요."));return;}
            playbackStyle=Light.Soft;playbackBrightness=Light.Level(estimated.Brightness);
            estimated.Brightness=0;playbackAdjust=estimated.Clamped();
            brightnessChosen=adjustChosen=true;
            ApplyPlaybackBrightness();seekPreview.Brightness=PlaybackValue;ShowAdjustValues();
            ShowCornerNote(L.P("Auto adjust: set from this scene for the whole video","자동 보정: 이 장면을 기준으로 영상 전체에 적용했어요"));
        }
        // The whole look on screen, for [Undo] of Reset and Auto.
        object ReadLook() {return Tuple.Create(playbackBrightness,playbackStyle,playbackAdjust);}
        void SetLook(object saved) {
            var look=(Tuple<int,int,AdjustValues>)saved;
            playbackBrightness=look.Item1;playbackStyle=look.Item2;playbackAdjust=look.Item3;brightnessChosen=adjustChosen=true;
            ApplyPlaybackBrightness();seekPreview.Brightness=PlaybackValue;ShowAdjustValues();
        }
        void LookChanged() {if(adjustReset!=null)adjustReset.Changed();if(adjustAuto!=null)adjustAuto.Changed();}
        AdjustValues AdjustNow {get {var now=playbackAdjust;now.Brightness=PlaybackValue;return now;}}
        void ShowAdjustValues() {
            if(adjustLines==null)return;
            adjustKnown=AdjustNow;adjustLines.Show(adjustKnown);
        }
        // A value changed by holding an arrow, the wheel or dragging: after 150 ms the other lines fade (not while typing).
        void NoteAdjusting(int field) {
            if(Keyboard.FocusedElement is TextBox)return;
            var now=DateTime.UtcNow;
            if((now-adjustLast).TotalMilliseconds>500)adjustFirst=now;
            adjustLast=now;adjustField=field;
        }
        void AdjustTick() {
            if(adjustPanel==null)return; // runs only while the panel is open (started in OpenAdjustPanel)
            var now=DateTime.UtcNow;
            bool changing=(now-adjustLast).TotalMilliseconds<500 && (now-adjustFirst).TotalMilliseconds>=150 && !(Keyboard.FocusedElement is TextBox);
            SetAdjustFaded(changing);
            // Q, W, E, R and Reset change the values from elsewhere: only then the lines show them.
            if(!AdjustNow.Equals(adjustKnown) && !adjustCard.IsMouseCaptureWithin && !(Keyboard.FocusedElement is TextBox)){ShowAdjustValues();LookChanged();}
        }
        void SetAdjustFaded(bool faded) {
            if(adjustCard==null || adjustFaded==faded)return;
            adjustFaded=faded;
            SetAdjustBackground(faded);
            adjustCard.Effect=faded?null:new System.Windows.Media.Effects.DropShadowEffect {Color=Colors.Black,BlurRadius=8,ShadowDepth=2,Direction=270,Opacity=0.3};
            adjustHead.Opacity=faded?0.12:1;
            // The line being changed stays as it is (a chosen line has its own solid background, so its number stays
            // readable over the video); everything else fades.
            int i=0;
            foreach(UIElement child in adjustLines.Children) {
                bool active=child is AdjustRow && i==adjustField;
                child.Opacity=faded && !active?0.12:1;
                if(child is AdjustRow)i++;
            }
        }
    }
}
