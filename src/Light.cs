using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LoveMyShelf {
    // E / W brightness, one rule for every place that shows it: the large view (mpv), the seek-bar thumbnail,
    // the scene list, the cover editor, saved covers and GIF covers. R switches between two styles:
    //  - Soft (the usual one): a thin white veil and the middle tones (faces) lifted, highlights kept. Gentle on
    //    the blocks a video's compression leaves in the dark.
    //  - See in the dark: a gamma curve that lifts the dark parts a lot, but eases off just above black so the
    //    compression blocks there aren't blown up. +100 is gamma 3.5.
    // Covers saved before these used a white or black sheet laid over the picture ("Sheet"). They keep that look.
    // A value carries both: the -100..100 level, plus 1000 (Sheet) or 2000 (See in the dark) away from zero.
    internal static class Light {
        internal const int Soft=0,Sheet=1,Dark=2;
        const double VeilMost=0.04;   // the veil at +100: black becomes 10 of 255 (black-band trimming takes up to 12 as black)
        const double Toe=0.01;        // how gently See in the dark starts just above black
        internal static int Clamp(int level) {return Math.Max(-100,Math.Min(100,level));}
        internal static int Encode(int level,int style) {
            level=Clamp(level);
            int mark=style==Sheet?1000:style==Dark?2000:0;
            return level>0?level+mark:level<0?level-mark:0;
        }
        internal static int Legacy(int level) {return Encode(level,Sheet);}
        internal static int Style(int value) {int a=Math.Abs(value);return a>2000?Dark:a>1000?Sheet:Soft;}
        internal static bool IsLegacy(int value) {return Style(value)==Sheet;}
        // The -100..100 number shown on the slider, whichever way it is drawn.
        internal static int Level(int value) {
            int a=Math.Abs(value),level=a>2000?a-2000:a>1000?a-1000:a;
            return value<0?-level:level;
        }
        internal static string StyleName(int style) {return L.T(style==Dark?"See in the dark":"Soft");}

        // The curve on one color, 0..1 in and out: first a gamma that eases off near black (p, toe),
        // then the middle tones lifted or lowered (c), then a thin veil (b).
        struct Curve {internal double P,E,C,B;}
        static Curve CurveOf(int value) {
            var c=new Curve {P=1,E=0,C=0,B=0};
            int level=Level(value);double k=level/100.0;
            if(Style(value)==Dark) {
                if(k>0){c.P=Math.Pow(3.5,-k);c.E=Toe;}
                else c.P=Math.Pow(2,-k);
            } else {
                c.C=k;                       // x + k·x(1-x): never past white, never below black
                if(k>0)c.B=VeilMost*k;
            }
            return c;
        }
        static double Bend(Curve c,double x) {
            double f=x;
            if(c.P!=1) {
                double floor=Math.Pow(c.E/(1+c.E),c.P);
                f=(Math.Pow((x+c.E)/(1+c.E),c.P)-floor)/(1-floor);
            }
            f+=c.C*f*(1-f);
            return c.B+(1-c.B)*f;
        }
        // 0..255 in, 0..255 out, rounded the same way FFmpeg's filter below rounds.
        internal static byte[] Table(int value) {
            var table=new byte[256];var c=CurveOf(value);
            for(int v=0;v<256;v++)table[v]=(byte)Math.Max(0,Math.Min(255,(int)(255*Bend(c,v/255.0)+0.5)));
            return table;
        }
        // The FFmpeg filter for a value, or null for none. It works on rgb24.
        internal static string Filter(int value) {
            if(value==0)return null;
            string channel;
            if(IsLegacy(value)) {
                int level=Level(value);double k=Math.Abs(level)/100.0;
                channel=level>0?"val*"+N(1-k)+"+"+N(255*k):"val*"+N(1-k);
            } else {
                // Written without commas (pow is exp·log), so it needs no escaping inside the filter chain.
                var c=CurveOf(value);
                string f="(val/255)";
                if(c.P!=1) {
                    double floor=Math.Pow(c.E/(1+c.E),c.P);
                    string inside=c.E>0?"((val/255+"+N(c.E)+")/"+N(1+c.E)+")":"(val/255+0.000001)";
                    f="((exp("+N(c.P)+"*log"+inside+")-"+N(floor)+")/"+N(1-floor)+")";
                }
                string s=c.C==0?f:"("+f+"+"+N(c.C)+"*"+f+"*(1-"+f+"))";
                channel="255*("+N(c.B)+"+"+N(1-c.B)+"*"+s+")+0.5";
            }
            return "lutrgb=r='"+channel+"':g='"+channel+"':b='"+channel+"'";
        }
        static string N(double n) {return n.ToString("0.########",CultureInfo.InvariantCulture);}

        // The same curve for mpv: a small shader on the video itself (not the subtitles or the black bars around it),
        // with its four numbers changed through glsl-shader-opts. Written once to the temp folder.
        internal static string MpvOptions(int value) {
            var c=CurveOf(value);
            return "lms_p="+N(c.P)+",lms_e="+N(c.E)+",lms_c="+N(c.C)+",lms_b="+N(c.B);
        }
        static string shaderFile;
        internal static string MpvShader() {
            if(shaderFile!=null && File.Exists(shaderFile))return shaderFile;
            string folder=Path.Combine(Path.GetTempPath(),"LoveMyShelf");
            Directory.CreateDirectory(folder);
            string file=Path.Combine(folder,"brightness-1.glsl");
            if(!File.Exists(file))File.WriteAllText(file,ShaderText);
            return shaderFile=file;
        }
        const string ShaderText=
@"//!PARAM lms_p
//!TYPE float
1.0
//!PARAM lms_e
//!TYPE float
0.0
//!PARAM lms_c
//!TYPE float
0.0
//!PARAM lms_b
//!TYPE float
0.0
//!HOOK MAIN
//!BIND HOOKED
//!DESC Love My Shelf brightness
vec4 hook() {
    vec4 color = HOOKED_texOff(0);
    vec3 f = clamp(color.rgb, 0.0, 1.0);
    if (lms_p != 1.0) {
        float floorValue = pow(lms_e / (1.0 + lms_e), lms_p);
        f = (pow((f + lms_e) / (1.0 + lms_e), vec3(lms_p)) - floorValue) / (1.0 - floorValue);
    }
    f = f + lms_c * f * (1.0 - f);
    color.rgb = lms_b + (1.0 - lms_b) * f;
    return color;
}
";

        // The picture with the curve on its colors (transparent pixels left alone). Sheet values and 0 return it
        // as it is: those are drawn by the caller. The last few results are kept, since the cover editor redraws on every move.
        internal static BitmapSource Apply(BitmapSource source,int value) {
            if(source==null || value==0 || IsLegacy(value))return source;
            lock(recent) {
                foreach(var kept in recent)
                    if(kept.Value==value && kept.Source.Target==source)return kept.Result;
            }
            var result=Lift(source,value);
            lock(recent) {
                recent.Insert(0,new Kept {Source=new WeakReference(source),Value=value,Result=result});
                if(recent.Count>4)recent.RemoveAt(recent.Count-1);
            }
            return result;
        }
        sealed class Kept {internal WeakReference Source;internal int Value;internal BitmapSource Result;}
        static readonly List<Kept> recent=new List<Kept>();
        static BitmapSource Lift(BitmapSource source,int value) {
            // Bgr32 (mpv's frames) stays as it is; anything else is read as straight (not premultiplied) BGRA.
            bool opaque=source.Format==PixelFormats.Bgr32;
            BitmapSource bgra=opaque || source.Format==PixelFormats.Bgra32?source:
                new FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0);
            int width=bgra.PixelWidth,height=bgra.PixelHeight,stride=width*4;
            var pixels=new byte[stride*height];
            bgra.CopyPixels(pixels,stride,0);
            var table=Table(value);
            for(int i=0;i<pixels.Length;i+=4) {
                if(!opaque && pixels[i+3]==0)continue; // the veil would leave a grey edge around see-through parts
                pixels[i]=table[pixels[i]];pixels[i+1]=table[pixels[i+1]];pixels[i+2]=table[pixels[i+2]];
            }
            var lifted=BitmapSource.Create(width,height,source.DpiX,source.DpiY,bgra.Format,null,pixels,stride);
            lifted.Freeze();
            return lifted;
        }
    }
}
