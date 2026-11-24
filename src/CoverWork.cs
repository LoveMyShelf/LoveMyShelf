using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LoveMyShelf {
    // The windows that make a cover (choose a scene, edit, Make GIF cover, compare an upscale) open as dialogs, and a
    // WPF dialog locks every window of the program. Here only the window the work started from stays locked, so the
    // other windows can still be looked at and used. One cover is worked on at a time, and its original stays put.
    internal static class CoverWork {
        [DllImport("user32.dll")]
        [return:MarshalAs(UnmanagedType.Bool)]
        static extern bool EnableWindow(IntPtr window,[MarshalAs(UnmanagedType.Bool)] bool enable);

        static readonly List<Window> open=new List<Window>();
        static Photo photo;
        static string original;
        static Window home,starter;

        internal static Window Top {get {return open.Count==0?null:open[open.Count-1];}}
        internal static bool CanStart(Window from) {return open.Count==0 || Top==from;}
        internal static bool BelongsTo(Window window) {return open.Count>0 && home==window;}

        // The first window of a piece of work says what it works on; windows opened from it pass only themselves.
        internal static bool? Show(Window dialog,Photo workingOn=null,Window gallery=null,string originalPath=null,Window startedFrom=null) {
            if(open.Count==0) {
                photo=workingOn;home=gallery;starter=startedFrom;
                original=originalPath??(workingOn==null?null:workingOn.Original);
            }
            open.Add(dialog);
            RoutedEventHandler loaded=null;
            loaded=delegate {dialog.Loaded-=loaded;Release(dialog);};
            dialog.Loaded+=loaded;
            try {return dialog.ShowDialog();}
            finally {
                open.Remove(dialog);
                if(open.Count==0){photo=null;original=null;home=null;starter=null;}
            }
        }

        // WPF has just disabled every window; it enables them again itself when the dialog closes.
        static void Release(Window dialog) {
            var locked=new HashSet<Window>();
            for(var window=dialog.Owner;window!=null;window=window.Owner)locked.Add(window);
            foreach(var window in open)if(window!=dialog)locked.Add(window);
            if(starter!=null)locked.Add(starter);
            foreach(var window in Application.Current.Windows.OfType<Window>().ToList()) {
                if(window==dialog || InLocked(window,locked))continue;
                var handle=new WindowInteropHelper(window).Handle;
                if(handle!=IntPtr.Zero)EnableWindow(handle,true);
            }
        }
        // A window that belongs to a locked one (the controls over its video, say) stays locked with it.
        static bool InLocked(Window window,HashSet<Window> locked) {
            for(;window!=null;window=window.Owner)if(locked.Contains(window))return true;
            return false;
        }

        // Brings the open cover window forward, restoring what it sits on if that was minimized.
        internal static void Front() {
            var top=Top;
            if(top==null)return;
            for(var window=top.Owner;window!=null;window=window.Owner)
                if(window.WindowState==WindowState.Minimized)window.WindowState=WindowState.Normal;
            if(top.WindowState==WindowState.Minimized)top.WindowState=WindowState.Normal;
            top.Activate();
        }

        internal static bool Holds(Photo other) {
            if(photo==null || other==null)return false;
            return GalleryWindow.SameOriginal(photo,other) || HoldsPath(other.Original);
        }
        internal static bool HoldsPath(string path) {
            if(original==null || String.IsNullOrEmpty(path))return false;
            try {return String.Equals(Path.GetFullPath(original),Path.GetFullPath(path),StringComparison.OrdinalIgnoreCase);}
            catch(Exception) {return false;}
        }
    }
}
