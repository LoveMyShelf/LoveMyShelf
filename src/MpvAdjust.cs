using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace LoveMyShelf {
    // Contrast, saturation, color temperature and sharpness on the playing video (2026-11-27 user: "put saturation
    // and contrast in the video window too"). View only: the file stays as it is. Debate (DeepSeek, Gemini, Codex):
    // on the GPU, in mpv user shaders that follow the same steps as ColorAdjust (C#) and the GIF's FFmpeg filters:
    // colortemperature (pl=1), then BT.601 limited YUV, eq contrast on Y and saturation on Cb/Cr, unsharp 5x5 on Y.
    // GPU floats may differ from the integer math by about one step; the cover made afterwards uses the exact code.
    // Each shader is loaded only while its values are not 0, so nothing costs anything when all are 0.
    internal static class MpvAdjust {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static string N(double v) { return v.ToString("0.######", Inv); }

        // The shader files to load after the brightness one (none when the values are all 0).
        internal static List<string> Files(AdjustValues a) {
            var files = new List<string>();
            if (a.Temperature != 0 || a.Contrast != 0 || a.Saturation != 0) files.Add(Write("adjust-tone-2.glsl", ToneText));
            if (a.Sharpness > 0) files.Add(Write("adjust-sharp-2.glsl", SharpText));
            return files;
        }

        // Parameters for the loaded files, as mpv's glsl-shader-opts wants them.
        internal static string Options(AdjustValues a) {
            var parts = new List<string>();
            if (a.Temperature != 0 || a.Contrast != 0 || a.Saturation != 0) {
                double r, g, b;
                Temperature(a.Temperature, out r, out g, out b);
                parts.Add("lms_tr=" + N(r)); parts.Add("lms_tg=" + N(g)); parts.Add("lms_tb=" + N(b));
                parts.Add("lms_ton=" + (a.Temperature != 0 ? "1" : "0"));
                parts.Add("lms_ct=" + N(ColorAdjust.ContrastToFfmpeg(a.Contrast)));
                parts.Add("lms_sa=" + N(ColorAdjust.SaturationToFfmpeg(a.Saturation)));
            }
            bool tone = a.Temperature != 0 || a.Contrast != 0 || a.Saturation != 0;
            if (tone) parts.Add("lms_yuvout=" + (a.Sharpness > 0 ? "1" : "0"));
            if (a.Sharpness > 0) {
                parts.Add("lms_sh=" + N(ColorAdjust.SharpnessToFfmpeg(a.Sharpness)));
                parts.Add("lms_yuvin=" + (tone ? "1" : "0"));
            }
            return String.Join(",", parts.ToArray());
        }

        // The same red, green and blue factors as FFmpeg's colortemperature (see ColorAdjust.ApplyTemperature).
        static void Temperature(int temp, out double mr, out double mg, out double mb) {
            int kelvin; double mix;
            ColorAdjust.TemperatureToFfmpeg(temp, out kelvin, out mix);
            float k = kelvin / 100.0f, kr, kg, kb;
            if (k <= 66.0f) {
                kr = 1.0f;
                kg = Clamp01((float)(0.39008157876901960784 * Math.Log(k) - 0.63184144378862745098));
                kb = k <= 19.0f ? 0.0f : Clamp01((float)(0.54320678911019607843 * Math.Log(k - 10.0f) - 1.19625408914));
            } else {
                float t = Math.Max(k - 60.0f, 0.0f);
                kr = Clamp01((float)(1.29293618606274509804 * Math.Pow(t, -0.1332047592)));
                kg = Clamp01((float)(1.12989086089529411765 * Math.Pow(t, -0.0755148492)));
                kb = 1.0f;
            }
            mr = 1.0 + mix * (kr - 1.0); mg = 1.0 + mix * (kg - 1.0); mb = 1.0 + mix * (kb - 1.0);
        }
        static float Clamp01(float v) { return v < 0f ? 0f : (v > 1f ? 1f : v); }

        static string Write(string name, string text) {
            string folder = Path.Combine(Path.GetTempPath(), "LoveMyShelf");
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, name);
            if (!File.Exists(file)) File.WriteAllText(file, text);
            return file;
        }

        // Colors are worked in 0..255 like the C# code, with its rounding.
        const string ToneText =
@"//!PARAM lms_tr
//!TYPE float
1.0
//!PARAM lms_tg
//!TYPE float
1.0
//!PARAM lms_tb
//!TYPE float
1.0
//!PARAM lms_ton
//!TYPE float
0.0
//!PARAM lms_ct
//!TYPE float
1.0
//!PARAM lms_sa
//!TYPE float
1.0
//!PARAM lms_yuvout
//!TYPE float
0.0
//!HOOK MAIN
//!BIND HOOKED
//!DESC LoveMyShelf adjust: color temperature, contrast, saturation
vec4 hook() {
    vec4 color = HOOKED_texOff(0);
    vec3 c = floor(clamp(color.rgb, 0.0, 1.0) * 255.0 + 0.5);
    if (lms_ton > 0.5) {
        vec3 n = clamp(floor(c * vec3(lms_tr, lms_tg, lms_tb) + 0.5), 0.0, 255.0);
        float l0 = max(c.r, max(c.g, c.b)) + min(c.r, min(c.g, c.b));
        float l1 = max(n.r, max(n.g, n.b)) + min(n.r, min(n.g, n.b));
        if (l1 > 0.0 && l0 != l1) n = clamp(floor(n * (l0 / l1) + 0.5), 0.0, 255.0);
        c = n;
    }
    if (lms_ct == 1.0 && lms_sa == 1.0 && lms_yuvout < 0.5) {
        color.rgb = c / 255.0;
        return color;
    }
    float y  = clamp(floor((66.0 * c.r + 129.0 * c.g + 25.0 * c.b + 128.0) / 256.0) + 16.0, 0.0, 255.0);
    float cb = clamp(floor((-38.0 * c.r - 74.0 * c.g + 112.0 * c.b + 128.0) / 256.0) + 128.0, 0.0, 255.0);
    float cr = clamp(floor((112.0 * c.r - 94.0 * c.g - 18.0 * c.b + 128.0) / 256.0) + 128.0, 0.0, 255.0);
    if (lms_ct != 1.0) y = clamp(floor(((y / 255.0 - 0.5) * lms_ct + 0.5) * 256.0), 0.0, 255.0);
    if (lms_sa != 1.0) {
        cb = clamp(floor(((cb / 255.0 - 0.5) * lms_sa + 0.5) * 256.0), 0.0, 255.0);
        cr = clamp(floor(((cr / 255.0 - 0.5) * lms_sa + 0.5) * 256.0), 0.0, 255.0);
    }
    // Sharpening follows: it gets Y, Cb, Cr as they are (like the C# code, which sharpens before going back to RGB).
    if (lms_yuvout > 0.5) {
        color.rgb = vec3(y, cb, cr) / 255.0;
        return color;
    }
    float e = y - 16.0, d = cb - 128.0, f = cr - 128.0;
    vec3 rgb = vec3(floor((298.0 * e + 409.0 * f + 128.0) / 256.0),
                    floor((298.0 * e - 100.0 * d - 208.0 * f + 128.0) / 256.0),
                    floor((298.0 * e + 516.0 * d + 128.0) / 256.0));
    color.rgb = clamp(rgb, 0.0, 255.0) / 255.0;
    return color;
}
";

        // Unsharp 5x5 on Y (weights 1 4 6 4 1 each way), in two passes: across, then down and sharpen.
        const string SharpText =
@"//!PARAM lms_sh
//!TYPE float
0.0
//!PARAM lms_yuvin
//!TYPE float
0.0
//!HOOK MAIN
//!BIND HOOKED
//!SAVE LMSBLUR
//!COMPONENTS 1
//!DESC LoveMyShelf sharpness (across)
float lumaAt(vec2 off) {
    vec3 c = floor(clamp(HOOKED_texOff(off).rgb, 0.0, 1.0) * 255.0 + 0.5);
    if (lms_yuvin > 0.5) return c.r;
    return clamp(floor((66.0 * c.r + 129.0 * c.g + 25.0 * c.b + 128.0) / 256.0) + 16.0, 0.0, 255.0);
}
vec4 hook() {
    float s = lumaAt(vec2(-2.0, 0.0)) + 4.0 * lumaAt(vec2(-1.0, 0.0)) + 6.0 * lumaAt(vec2(0.0, 0.0))
            + 4.0 * lumaAt(vec2(1.0, 0.0)) + lumaAt(vec2(2.0, 0.0));
    return vec4(s / 4096.0, 0.0, 0.0, 1.0);
}
//!HOOK MAIN
//!BIND HOOKED
//!BIND LMSBLUR
//!DESC LoveMyShelf sharpness (down)
vec4 hook() {
    float sum = LMSBLUR_texOff(vec2(0.0, -2.0)).x + 4.0 * LMSBLUR_texOff(vec2(0.0, -1.0)).x + 6.0 * LMSBLUR_texOff(vec2(0.0, 0.0)).x
              + 4.0 * LMSBLUR_texOff(vec2(0.0, 1.0)).x + LMSBLUR_texOff(vec2(0.0, 2.0)).x;
    float blurred = floor((sum * 4096.0 + 128.0) / 256.0);
    vec4 color = HOOKED_texOff(0);
    vec3 c = floor(clamp(color.rgb, 0.0, 1.0) * 255.0 + 0.5);
    float y = c.r, cb = c.g, cr = c.b;
    if (lms_yuvin < 0.5) {
        y  = clamp(floor((66.0 * c.r + 129.0 * c.g + 25.0 * c.b + 128.0) / 256.0) + 16.0, 0.0, 255.0);
        cb = clamp(floor((-38.0 * c.r - 74.0 * c.g + 112.0 * c.b + 128.0) / 256.0) + 128.0, 0.0, 255.0);
        cr = clamp(floor((112.0 * c.r - 94.0 * c.g - 18.0 * c.b + 128.0) / 256.0) + 128.0, 0.0, 255.0);
    }
    y = clamp(y + floor((y - blurred) * lms_sh), 0.0, 255.0);
    float e = y - 16.0, d = cb - 128.0, f = cr - 128.0;
    vec3 rgb = vec3(floor((298.0 * e + 409.0 * f + 128.0) / 256.0),
                    floor((298.0 * e - 100.0 * d - 208.0 * f + 128.0) / 256.0),
                    floor((298.0 * e + 516.0 * d + 128.0) / 256.0));
    color.rgb = clamp(rgb, 0.0, 255.0) / 255.0;
    return color;
}
";
    }
}
