using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Forms=System.Windows.Forms;

namespace LoveMyShelf {
    // Where a large view opens, like a video player opened from File Explorer: the first one where the last one
    // was left (not where the folder window is), and one opened while others are showing in the free space beside
    // them, so two videos can be watched together without moving windows by hand.
    internal static class ViewerPlacement {
        // Windows may share an edge this wide (the invisible resize borders) without counting as covering each other.
        const double Slack=24;
        // No room beside: a step down and to the right, like a new File Explorer window.
        const double Step=36;

        internal static void Place(Viewer viewer,Window from,string remembered) {
            double scale=Scale(from);
            var screens=Forms.Screen.AllScreens;
            var areas=screens.Select(s=>Units(s.WorkingArea,scale)).ToList();
            var open=Application.Current.Windows.OfType<Viewer>()
                .Where(v=>v!=viewer && v.IsVisible && !v.ForCrop && v.WindowState!=WindowState.Minimized).ToList();
            if(open.Count==0) {
                Rect box;bool maximized;
                if(Read(remembered,areas,out box,out maximized))viewer.PlaceAt(box,maximized,null);
                return;
            }
            var last=open[open.Count-1];
            Func<Viewer,bool> whole=v=>v.WindowState==WindowState.Maximized || FullScreen.IsFull(v);
            Func<Viewer,int> monitor=v=>{
                string name=Forms.Screen.FromHandle(new WindowInteropHelper(v).Handle).DeviceName;
                return Math.Max(0,Array.FindIndex(screens,s=>s.DeviceName==name));
            };
            if(whole(last)) {
                // A maximized or full-screen view fills its monitor: the next one fills a monitor that has none.
                // With no such monitor it opens the same way on top, as before.
                bool full=FullScreen.IsFull(last);
                var busy=new HashSet<int>(open.Where(whole).Select(monitor));
                int at=monitor(last);
                var free=Enumerable.Range(0,screens.Length).Where(i=>!busy.Contains(i)).OrderBy(i=>Distance(areas[i],areas[at])).ToList();
                if(free.Count==0){viewer.PlaceLike(last);return;}
                var area=areas[free[0]];
                // Its size once it is restored or leaves full screen.
                double w=Math.Round(area.Width*0.7),h=Math.Round(area.Height*0.75);
                viewer.PlaceAt(new Rect(area.Left+(area.Width-w)/2,area.Top+(area.Height-h)/2,w,h),!full,full);
                return;
            }
            var taken=open.Select(v=>whole(v)?Units(screens[monitor(v)].Bounds,scale):Box(v)).ToList();
            viewer.PlaceAt(Beside(Box(last),taken,areas),false,false);
        }

        // The place for a window the size of the last one: right of it, left, below or above when that fits on its
        // monitor without covering another large view; else the same spot on another monitor; else a step away.
        internal static Rect Beside(Rect last,IList<Rect> taken,IList<Rect> areas) {
            if(areas.Count==0)return new Rect(last.Left+Step,last.Top+Step,last.Width,last.Height);
            var home=areas.OrderByDescending(a=>Shared(a,last)).First();
            var beside=new[] {
                new Rect(last.Right,last.Top,last.Width,last.Height),new Rect(last.Left-last.Width,last.Top,last.Width,last.Height),
                new Rect(last.Left,last.Bottom,last.Width,last.Height),new Rect(last.Left,last.Top-last.Height,last.Width,last.Height)};
            foreach(var spot in beside) {
                Rect fit;
                if(Fit(spot,home,out fit) && !taken.Any(t=>Covers(t,fit)))return fit;
            }
            foreach(var area in areas.Where(a=>a!=home).OrderBy(a=>Distance(a,home))) {
                double w=Math.Min(last.Width,area.Width),h=Math.Min(last.Height,area.Height);
                var spot=new Rect(area.Left+Math.Min(Math.Max(0,last.Left-home.Left),area.Width-w),
                    area.Top+Math.Min(Math.Max(0,last.Top-home.Top),area.Height-h),w,h);
                if(!taken.Any(t=>Covers(t,spot)))return spot;
            }
            // Toward the side that still has room, so the window stays on the monitor.
            double dx=last.Right+Step<=home.Right+Slack?Step:last.Left-Step>=home.Left-Slack?-Step:0;
            double dy=last.Bottom+Step<=home.Bottom+Slack?Step:last.Top-Step>=home.Top-Slack?-Step:0;
            return new Rect(last.Left+dx,last.Top+dy,last.Width,last.Height);
        }
        static bool Covers(Rect a,Rect b) {
            var both=Rect.Intersect(a,b);
            return !both.IsEmpty && both.Width>Slack && both.Height>Slack;
        }
        // A spot that sticks out of the monitor a little is moved back in; one that sticks out more does not fit.
        static bool Fit(Rect spot,Rect area,out Rect fit) {
            fit=spot;
            double dx,dy;
            if(!Shift(spot.Left,spot.Right,area.Left,area.Right,out dx) || !Shift(spot.Top,spot.Bottom,area.Top,area.Bottom,out dy))return false;
            fit.Offset(dx,dy);return true;
        }
        // One direction of Fit. A window as long as the monitor (snapped, with its invisible borders outside) stays as it is.
        static bool Shift(double from,double to,double areaFrom,double areaTo,out double by) {
            by=0;
            if(to-from>areaTo-areaFrom)return from>=areaFrom-Slack && to<=areaTo+Slack;
            by=from<areaFrom?areaFrom-from:to>areaTo?areaTo-to:0;
            return Math.Abs(by)<=Slack;
        }
        static double Shared(Rect a,Rect b) {
            var both=Rect.Intersect(a,b);
            return both.IsEmpty?0:both.Width*both.Height;
        }
        static double Distance(Rect a,Rect b) {
            return Math.Abs(a.Left+a.Width/2-b.Left-b.Width/2)+Math.Abs(a.Top+a.Height/2-b.Top-b.Height/2);
        }

        // "left,top,width,height,maximized": where the large view closed last was (its restored place when it
        // was maximized, minimized or in full screen). Null when the window has no usable place.
        internal static string Write(Viewer viewer) {
            Rect box;bool maximized;
            if(!FullScreen.Restore(viewer,out box,out maximized)) {
                maximized=viewer.WindowState==WindowState.Maximized;
                box=viewer.WindowState==WindowState.Normal?Box(viewer):viewer.RestoreBounds;
            }
            if(box.IsEmpty || Double.IsNaN(box.Left) || Double.IsNaN(box.Top) || box.Width<200 || box.Height<150)return null;
            return String.Join(",",new[]{box.Left,box.Top,box.Width,box.Height}.Select(v=>Math.Round(v).ToString(CultureInfo.InvariantCulture)))+(maximized?",1":",0");
        }
        // False when nothing was remembered or its monitor is gone (the window must not open where nobody can reach it).
        internal static bool Read(string text,IList<Rect> areas,out Rect box,out bool maximized) {
            box=Rect.Empty;maximized=false;
            var parts=(text??"").Split(',');
            double l,t,w,h;
            if(parts.Length<4 || !Number(parts[0],out l) || !Number(parts[1],out t) || !Number(parts[2],out w) || !Number(parts[3],out h))return false;
            if(w<200 || h<150 || areas.Count==0)return false;
            var grip=new Rect(l,t,w,40);
            var area=areas.OrderByDescending(a=>Shared(a,grip)).First();
            if(Shared(area,grip)<=0)return false;
            // A monitor that became smaller: the window still fits on it.
            if(w>area.Width+Slack){w=area.Width;l=area.Left;}
            if(h>area.Height+Slack){h=area.Height;t=area.Top;}
            box=new Rect(l,t,w,h);maximized=parts.Length>4 && parts[4]=="1";
            return true;
        }
        static bool Number(string text,out double value) {return Double.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out value);}
        static Rect Box(Window window) {return new Rect(window.Left,window.Top,window.ActualWidth,window.ActualHeight);}
        // Screen pixels to WPF units. Love My Shelf uses one scale for every monitor (system DPI aware).
        static Rect Units(System.Drawing.Rectangle box,double scale) {
            return new Rect(box.Left/scale,box.Top/scale,box.Width/scale,box.Height/scale);
        }
        static double Scale(Visual visual) {
            var source=visual==null?null:PresentationSource.FromVisual(visual);
            double scale=source!=null && source.CompositionTarget!=null?source.CompositionTarget.TransformToDevice.M11:1;
            return scale>0?scale:1;
        }
    }
}
