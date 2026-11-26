using System;
using System.Windows;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;

namespace LoveMyShelf {
    internal sealed class WindowTitleBar : DockPanel {
        readonly Window window;
        readonly Button minimize,maximize,fullscreen,close;
        readonly WindowChrome chrome=new WindowChrome {
            CaptionHeight=25,ResizeBorderThickness=new Thickness(5),
            GlassFrameThickness=new Thickness(0),CornerRadius=new CornerRadius(0)
        };

        public WindowTitleBar(Window window,Action toggleFullscreen=null) {
            this.window=window;
            Height=30;SetResourceReference(BackgroundProperty,"Paper");
            UseLayoutRounding=true;
            SetDock(this,Dock.Top);
            close=AddButton(()=>window.Close());
            if(toggleFullscreen!=null)fullscreen=AddButton(toggleFullscreen);
            // Both work in full screen too: minimize goes straight down (and comes back in full screen),
            // maximize leaves full screen for a maximized window in one click.
            maximize=AddButton(()=>{
                if(FullScreen.IsFull(window) && MaximizeFromFull!=null){MaximizeFromFull();return;}
                if(window.WindowState==WindowState.Maximized)SystemCommands.RestoreWindow(window);
                else SystemCommands.MaximizeWindow(window);
            });
            minimize=AddButton(()=>{
                // A full-screen window has no frame, so it is minimized directly instead of through the system menu.
                if(FullScreen.IsFull(window))window.WindowState=WindowState.Minimized;
                else SystemCommands.MinimizeWindow(window);
            });

            var title=UI.Text("",12);
            title.Margin=new Thickness(12,0,8,0);
            title.TextTrimming=TextTrimming.CharacterEllipsis;
            title.SetBinding(TextBlock.TextProperty,new Binding("Title"){Source=window});
            Children.Add(title);
            WindowChrome.SetWindowChrome(window,chrome);
            window.StateChanged+=delegate {Update();FitMaximized();};
            window.SizeChanged+=delegate {FitMaximized();};
            window.LocationChanged+=delegate {FitMaximized();}; // a maximized window moved to another screen (Win+Shift+arrow)
            window.SourceInitialized+=delegate {
                var source=HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
                if(source!=null)source.AddHook(MaximizeToWorkArea);
                FitMaximized();
            };
            Update();
        }

        // A window with its own title bar (WindowChrome) has no caption to Windows, and Windows maximizes such a window
        // over the whole monitor, taskbar included, with its frame hanging past every edge (measured at 150%: 0-1080 on a
        // work area of 0-1008, plus 11 pixels each side). Windows then treats it as full screen and the taskbar steps
        // back, so whatever the window doesn't paint (the strip where the taskbar was) shows the window behind it
        // (2026-11-27, "설명서 겸 옵션 하단 짤려서 나와").
        // So the window is put on the work area of its own screen as Windows maximizes it: nothing hangs past, nothing
        // is cut, the taskbar stays.
        // Every window with this title bar gets it.
        IntPtr MaximizeToWorkArea(IntPtr handle,int message,IntPtr wParam,IntPtr lParam,ref bool handled) {
            // WM_WINDOWPOSCHANGING while maximized: the place Windows is about to give the window becomes the work area of
            // the screen it lands on. (WM_GETMINMAXINFO can't do this: its sizes are for the primary screen, and a window
            // maximized on a second screen with another size or scale came out the wrong size.)
            if(message!=0x0046 || !IsZoomed(handle) || FullScreen.IsFull(window))return IntPtr.Zero;
            var place=(WindowPlace)Marshal.PtrToStructure(lParam,typeof(WindowPlace));
            if((place.Flags&0x0003)==0x0003)return IntPtr.Zero; // SWP_NOSIZE|SWP_NOMOVE: not a move or resize
            var target=new Edges {Left=place.X,Top=place.Y,Right=place.X+place.Width,Bottom=place.Y+place.Height};
            var info=new ScreenInfo {Size=Marshal.SizeOf(typeof(ScreenInfo))};
            IntPtr screen=(place.Flags&0x0003)==0?MonitorFromRect(ref target,2):MonitorFromWindow(handle,2);
            if(!GetMonitorInfo(screen,ref info))return IntPtr.Zero;
            place.X=info.Work.Left;place.Y=info.Work.Top;place.Width=info.Work.Right-info.Work.Left;place.Height=info.Work.Bottom-info.Work.Top;
            place.Flags&=~0x0003;
            Marshal.StructureToPtr(place,lParam,false);
            return IntPtr.Zero;
        }
        [StructLayout(LayoutKind.Sequential)] struct WindowPlace {public IntPtr Handle,After;public int X,Y,Width,Height;public int Flags;}
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr window);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromRect(ref Edges rect,int flags);
        // If a window still ends up past the work area anyway, it keeps that much clear (at most 16 points).
        internal void FitMaximized() {
            var none=new Thickness(0);
            if(window.WindowState!=WindowState.Maximized || FullScreen.IsFull(window)){if(window.BorderThickness!=none)window.BorderThickness=none;return;}
            IntPtr handle=new WindowInteropHelper(window).Handle;
            var source=PresentationSource.FromVisual(window);
            if(handle==IntPtr.Zero || source==null || source.CompositionTarget==null)return;
            Edges outside;
            var info=new ScreenInfo {Size=Marshal.SizeOf(typeof(ScreenInfo))};
            if(!GetWindowRect(handle,out outside) || !GetMonitorInfo(MonitorFromWindow(handle,2),ref info))return;
            var scale=source.CompositionTarget.TransformFromDevice;
            Func<int,double,double> part=(pixels,unit)=>Math.Min(16,Math.Max(0,pixels*unit));
            var clear=new Thickness(part(info.Work.Left-outside.Left,scale.M11),part(info.Work.Top-outside.Top,scale.M22),
                part(outside.Right-info.Work.Right,scale.M11),part(outside.Bottom-info.Work.Bottom,scale.M22));
            if(window.BorderThickness!=clear)window.BorderThickness=clear;
        }
        [StructLayout(LayoutKind.Sequential)] struct Edges {public int Left,Top,Right,Bottom;}
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct ScreenInfo {
            public int Size;public Edges Monitor,Work;public int Flags;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string Device;
        }
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window,out Edges edges);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr window,int flags);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr monitor,ref ScreenInfo info);

        // Set by FullScreen: leaves full screen and maximizes the window.
        internal Action MaximizeFromFull;
        Button AddButton(Action action) {
            var button=UI.Btn("",action);
            button.Style=(Style)Application.Current.Resources["TitleBarButton"];
            // Like Windows' own caption buttons, a click here never takes the keys away from the window
            // (in full screen the buttons sit on the video overlay, and the viewer's shortcuts stopped after minimizing).
            button.Focusable=false;
            WindowChrome.SetIsHitTestVisibleInChrome(button,true);
            UI.Right(this,button);
            return button;
        }
        static void Label(Button button,string label,string drawing,string hint=null) {
            if(button==null)return;
            var glyph=new Path {
                Data=Geometry.Parse(drawing),Width=13,Height=13,StrokeThickness=1,IsHitTestVisible=false
            };
            glyph.SetResourceReference(Shape.StrokeProperty,"Text");
            button.Content=glyph;
            AutomationProperties.SetName(button,L.T(label));
            button.ToolTip=L.T(hint??label);
        }
        internal void UseWindowChrome(bool enabled) {
            WindowChrome.SetWindowChrome(window,enabled?chrome:null);
        }
        internal void Update() {
            bool full=FullScreen.IsFull(window),max=window.WindowState==WindowState.Maximized;
            Label(close,"Close","M 2,2 L 11,11 M 11,2 L 2,11");
            Label(minimize,"Minimize","M 1,8 L 12,8");
            Label(maximize,max?"Restore window":"Maximize",max
                ?"M 4,4 V 1.5 H 11.5 V 9 H 9 M 1.5,4 H 9 V 11.5 H 1.5 Z"
                :"M 2,2 H 11 V 11 H 2 Z");
            Label(fullscreen,full?"Windowed":"Full screen",full
                ?"M 1,4 H 4 V 1 M 9,1 V 4 H 12 M 1,9 H 4 V 12 M 9,12 V 9 H 12"
                :"M 1,5 V 1 H 5 M 8,1 H 12 V 5 M 12,8 V 12 H 8 M 5,12 H 1 V 8",
                "Toggle full screen (F11)");
            // Without FullScreen behind it, a full-screen-looking window can't be maximized from here.
            maximize.IsEnabled=!full || MaximizeFromFull!=null;
        }
    }
}
