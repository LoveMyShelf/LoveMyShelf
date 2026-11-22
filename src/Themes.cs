using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace LoveMyShelf {
    internal enum ThemeMode { System, Light, Dark }

    // Light and dark color sets. Every window refers to these names through DynamicResource or
    // SetResourceReference, so swapping the brushes recolors open windows at once.
    // Pictures keep a black surrounding in both modes; only buttons, text and panels change.
    internal static class Themes {
        static readonly Dictionary<string,uint> Dark=new Dictionary<string,uint> {
            {"Paper",0xFF17191C},{"Panel",0xFF23262A},{"Line",0xFF41454B},{"Text",0xFFECEEF1},{"Subtle",0xFFB6BBC2},
            {"Accent",0xFFD3D5D9},{"Raised",0xFF373A3F},{"StrongLine",0xFF565B63},{"FocusLine",0xFFFFFFFF},
            {"ScrollThumb",0xFF64676B},{"CardBack",0xF226292E},{"CardLine",0x26FFFFFF},{"ViewerBar",0x00000000},
            {"TileBand",0xFF23262A},{"OnVideo",0xB3000000}
        };
        // Palette A, "calm cool gray" (chosen by the user): no pure white anywhere, soft dark-gray text,
        // and the empty bands around pictures in the grid a step darker than the background.
        static readonly Dictionary<string,uint> Light=new Dictionary<string,uint> {
            {"Paper",0xFFECEEF1},{"Panel",0xFFF4F5F7},{"Line",0xFFD3D7DD},{"Text",0xFF333D4B},{"Subtle",0xFF6B7684},
            {"Accent",0xFF3182F6},{"Raised",0xFFE6E8EC},{"StrongLine",0xFFC5CAD1},{"FocusLine",0xFF3182F6},
            {"ScrollThumb",0xFFC2C7CE},{"CardBack",0xF7F4F5F7},{"CardLine",0x1F000000},{"ViewerBar",0x00000000},
            {"TileBand",0xFFDADDE2},{"OnVideo",0xB3000000}
        };
        static ThemeMode mode=ThemeMode.System;
        static bool listening;
        internal static bool IsLight {get;private set;}
        internal static event Action Changed;

        // True when Windows is set to light mode for apps (the usual default).
        internal static bool WindowsUsesLight() {
            try {
                using(var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")) {
                    object value=key==null?null:key.GetValue("AppsUseLightTheme");
                    return !(value is int) || (int)value!=0;
                }
            } catch(Exception) {return true;}
        }

        internal static void Apply(ThemeMode value) {
            mode=value;
            bool light=value==ThemeMode.Light || (value==ThemeMode.System && WindowsUsesLight());
            var app=Application.Current;
            if(app==null)return;
            foreach(var pair in light?Light:Dark) {
                uint c=pair.Value;
                var brush=new SolidColorBrush(Color.FromArgb((byte)(c>>24),(byte)(c>>16),(byte)(c>>8),(byte)c));
                brush.Freeze();
                app.Resources[pair.Key]=brush;
            }
            bool changed=IsLight!=light;IsLight=light;
            if(!listening) {
                listening=true;
                SystemEvents.UserPreferenceChanged+=(s,e)=>{
                    if(e.Category!=UserPreferenceCategory.General || mode!=ThemeMode.System)return;
                    var current=Application.Current;
                    if(current!=null)current.Dispatcher.BeginInvoke(new Action(()=>Apply(ThemeMode.System)));
                };
            }
            if(changed && Changed!=null)Changed();
        }
    }
}
