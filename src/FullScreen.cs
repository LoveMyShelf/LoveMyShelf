using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Forms=System.Windows.Forms;

namespace LoveMyShelf {
    internal sealed class FullScreen {
        readonly Window window;
        readonly Action<bool> remember;
        readonly Action<bool> changed;
        readonly WindowTitleBar titleBar;
        WindowState oldState;
        WindowStyle oldStyle;
        ResizeMode oldResize;
        Rect oldBounds;
        bool active;

        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window,FullScreen> instances=new System.Runtime.CompilerServices.ConditionalWeakTable<Window,FullScreen>();
        // Takes a window out of full screen without remembering the choice (used when a second window opens beside it).
        internal static void Leave(Window owner) {
            FullScreen found;
            if(instances.TryGetValue(owner,out found) && found.active)found.Set(false,false);
        }
        // Puts a window into full screen without remembering the choice (a large view taking another's place).
        internal static void Enter(Window owner) {
            FullScreen found;
            if(instances.TryGetValue(owner,out found) && !found.active)found.Set(true,false);
        }
        // Where a full-screen window goes back to when it leaves full screen. False when it is not in full screen.
        internal static bool Restore(Window owner,out Rect bounds,out bool maximized) {
            FullScreen found;
            bool full=instances.TryGetValue(owner,out found) && found.active;
            bounds=full?found.oldBounds:Rect.Empty;maximized=full && found.oldState==WindowState.Maximized;
            return full;
        }
        public static bool IsFull(Window owner) { return owner.WindowStyle==WindowStyle.None && owner.ResizeMode==ResizeMode.NoResize; }
        public static WindowTitleBar TitleBar(Window window,bool start,Action<bool> remember=null,bool escapeExits=true,Action<bool> changed=null) {
            return new FullScreen(window,start,remember,escapeExits,changed).titleBar;
        }
        FullScreen(Window window,bool start,Action<bool> remember,bool escapeExits,Action<bool> changed) {
            this.window=window;this.remember=remember;this.changed=changed;
            instances.Add(window,this);
            titleBar=new WindowTitleBar(window,()=>Set(!active,true));
            titleBar.MaximizeFromFull=()=>{
                if(!active)return;
                oldState=WindowState.Maximized; // leaving full screen lands on a maximized window
                Set(false,true);
            };
            window.Loaded+=delegate { if(start && !active)Set(true,false); };
            window.PreviewKeyDown+=(s,e)=>{
                if(e.Handled)return;
                if(e.Key==Key.F11 || (e.Key==Key.Escape && active && escapeExits)) {
                    if(!e.IsRepeat) Set(!active,true);
                    e.Handled=true;
                }
            };
        }
        void Set(bool full,bool save) {
            var handle=new WindowInteropHelper(window).Handle;
            if(full) {
                var screen=Forms.Screen.FromHandle(handle);
                oldState=window.WindowState;oldStyle=window.WindowStyle;oldResize=window.ResizeMode;
                oldBounds=oldState==WindowState.Normal?new Rect(window.Left,window.Top,window.ActualWidth,window.ActualHeight):window.RestoreBounds;
                titleBar.UseWindowChrome(false);
                window.WindowState=WindowState.Normal;window.WindowStyle=WindowStyle.None;window.ResizeMode=ResizeMode.NoResize;
                Cover(handle,screen);
            } else {
                Mark(handle,false);
                window.WindowStyle=oldStyle;window.ResizeMode=oldResize;
                window.Left=oldBounds.Left;window.Top=oldBounds.Top;window.Width=oldBounds.Width;window.Height=oldBounds.Height;
                titleBar.UseWindowChrome(true);
                window.WindowState=oldState;
            }
            active=full;titleBar.Update();
            if(changed!=null)changed(full);
            if(save && remember!=null) remember(full);
        }
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int w,int h,uint flags);
        static void Cover(IntPtr handle,Forms.Screen screen) {
            var r=screen.Bounds;
            SetWindowPos(handle,IntPtr.Zero,r.X,r.Y,r.Width,r.Height,0x0034);
            Mark(handle,true);
        }
        [ComImport,Guid("602D4995-B13A-429B-A66E-1935E44F4317"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ITaskbarList2 {
            void HrInit();
            void AddTab(IntPtr hwnd);void DeleteTab(IntPtr hwnd);void ActivateTab(IntPtr hwnd);void SetActiveAlt(IntPtr hwnd);
            void MarkFullscreenWindow(IntPtr hwnd,[MarshalAs(UnmanagedType.Bool)] bool full);
        }
        static void Mark(IntPtr handle,bool full) {
            ITaskbarList2 shell=null;
            try {
                var type=Type.GetTypeFromCLSID(new Guid("56FDF344-FD6D-11D0-958A-006097C9A090"));
                shell=(ITaskbarList2)Activator.CreateInstance(type);shell.HrInit();shell.MarkFullscreenWindow(handle,full);
            } catch(COMException) { } finally { if(shell!=null) Marshal.ReleaseComObject(shell); }
        }
    }
}
