using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace LoveMyShelf {
    // The speaker chosen for a monitor. Name is kept so the options can still say which one it was while it is unplugged.
    internal sealed class SpeakerChoice {public string Device,Name;}
    internal sealed class ScreenSpot {public string Key,Name;}

    // Which speaker the sound of a window should come from, going by the monitor the window is on.
    internal static class SpeakerRouting {
        const int NearestMonitor=2,DeviceInterfaceName=1,DisplayActive=1;
        static readonly object gate=new object();
        static readonly Dictionary<IntPtr,string> keys=new Dictionary<IntPtr,string>();

        static SpeakerRouting() {
            SystemEvents.DisplaySettingsChanged+=delegate {lock(gate)keys.Clear();};
        }

        // The device to play on: "" means the Windows default, also when the chosen speaker isn't connected now.
        internal static string For(Preferences prefs,IntPtr window,Func<string,bool> connected) {
            if(window==IntPtr.Zero || prefs.MonitorSpeakers.Count==0)return "";
            string key=KeyOf(MonitorFromWindow(window,NearestMonitor));
            SpeakerChoice choice;
            if(key==null || !prefs.MonitorSpeakers.TryGetValue(key,out choice))return "";
            return connected(choice.Device)?choice.Device:"";
        }

        // Every monitor from left to right, named the way a person would point at it.
        internal static List<ScreenSpot> Screens() {
            var found=new List<Tuple<IntPtr,Rect>>();
            MonitorEnum each=(IntPtr monitor,IntPtr dc,ref Rect area,IntPtr data)=>{found.Add(Tuple.Create(monitor,area));return true;};
            EnumDisplayMonitors(IntPtr.Zero,IntPtr.Zero,each,IntPtr.Zero);
            GC.KeepAlive(each);
            var sorted=found.OrderBy(m=>m.Item2.Left).ThenBy(m=>m.Item2.Top).ToList();
            bool sideBySide=sorted.Count==2 && sorted[0].Item2.Left!=sorted[1].Item2.Left;
            var spots=new List<ScreenSpot>();
            for(int i=0;i<sorted.Count;i++) {
                string key=KeyOf(sorted[i].Item1);
                if(key==null)continue;
                var area=sorted[i].Item2;
                spots.Add(new ScreenSpot {Key=key,Name=PlaceName(i,sorted.Count,sideBySide)+" ("+(area.Right-area.Left)+"×"+(area.Bottom-area.Top)+")"});
            }
            return spots;
        }
        static string PlaceName(int index,int count,bool sideBySide) {
            if(count==1)return L.Korean?"모니터":"Monitor";
            if(count==2 && sideBySide)return index==0?(L.Korean?"왼쪽 모니터":"Left monitor"):(L.Korean?"오른쪽 모니터":"Right monitor");
            if(count==2)return index==0?(L.Korean?"위쪽 모니터":"Upper monitor"):(L.Korean?"아래쪽 모니터":"Lower monitor");
            return L.Korean?"왼쪽에서 "+(index+1)+"번째 모니터":"Monitor "+(index+1)+" from the left";
        }

        // The monitor's own ID stays the same when Windows renumbers the displays.
        static string KeyOf(IntPtr monitor) {
            if(monitor==IntPtr.Zero)return null;
            lock(gate) {
                string known;
                if(keys.TryGetValue(monitor,out known))return known;
                var info=new MonitorInfo {Size=Marshal.SizeOf(typeof(MonitorInfo))};
                if(!GetMonitorInfo(monitor,ref info))return null;
                string key=info.Device;
                var device=new DisplayDevice {Size=Marshal.SizeOf(typeof(DisplayDevice))};
                for(uint i=0;EnumDisplayDevices(info.Device,i,ref device,DeviceInterfaceName);i++) {
                    if((device.State&DisplayActive)!=0 && !String.IsNullOrEmpty(device.Id)){key=device.Id;break;}
                    device.Size=Marshal.SizeOf(typeof(DisplayDevice));
                }
                keys[monitor]=key;
                return key;
            }
        }

        delegate bool MonitorEnum(IntPtr monitor,IntPtr dc,ref Rect area,IntPtr data);
        [StructLayout(LayoutKind.Sequential)] struct Rect {public int Left,Top,Right,Bottom;}
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
        struct MonitorInfo {
            public int Size;public Rect Area,Work;public int Flags;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string Device;
        }
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
        struct DisplayDevice {
            public int Size;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string Name;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string Text;
            public int State;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string Id;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string RegistryKey;
        }
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr window,int flags);
        [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr dc,IntPtr clip,MonitorEnum callback,IntPtr data);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr monitor,ref MonitorInfo info);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern bool EnumDisplayDevices(string device,uint index,ref DisplayDevice info,int flags);
    }
}
