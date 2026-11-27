using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;

namespace LoveMyShelf {
    // Cover-only color and clarity adjustments: brightness (with Light.cs's 3 styles), contrast, saturation,
    // color temperature (negative = cooler, positive = warmer), and sharpness. Originals are never changed.
    public struct AdjustValues : IEquatable<AdjustValues> {
        public int Brightness;   // Light.Encode(level, style)
        public int Contrast;     // -100..+100
        public int Saturation;   // -100..+100
        public int Temperature;  // -100..+100 (- cooler, + warmer)
        public int Sharpness;    // 0..100

        public static AdjustValues None { get { return default(AdjustValues); } }

        public bool IsIdentity {
            get { return Brightness == 0 && Contrast == 0 && Saturation == 0 && Temperature == 0 && Sharpness == 0; }
        }

        public bool HasBeyondBrightness {
            get { return Contrast != 0 || Saturation != 0 || Temperature != 0 || Sharpness > 0; }
        }

        public AdjustValues Clamped() {
            return new AdjustValues {
                Brightness = Light.Encode(Light.Clamp(Light.Level(Brightness)), Light.Style(Brightness)),
                Contrast = ClampSym(Contrast),
                Saturation = ClampSym(Saturation),
                Temperature = ClampSym(Temperature),
                Sharpness = Math.Max(0, Math.Min(100, Sharpness))
            };
        }

        static int ClampSym(int v) { return Math.Max(-100, Math.Min(100, v)); }

        public bool Equals(AdjustValues other) {
            return Brightness == other.Brightness && Contrast == other.Contrast &&
                Saturation == other.Saturation && Temperature == other.Temperature && Sharpness == other.Sharpness;
        }
        public override bool Equals(object obj) { return obj is AdjustValues && Equals((AdjustValues)obj); }
        public override int GetHashCode() {
            unchecked {
                int h = Brightness;
                h = (h * 397) ^ Contrast;
                h = (h * 397) ^ Saturation;
                h = (h * 397) ^ Temperature;
                h = (h * 397) ^ Sharpness;
                return h;
            }
        }
        public static bool operator ==(AdjustValues a, AdjustValues b) { return a.Equals(b); }
        public static bool operator !=(AdjustValues a, AdjustValues b) { return !a.Equals(b); }

        internal string KeyText() {
            var inv = CultureInfo.InvariantCulture;
            return Brightness.ToString(inv) + ":" + Contrast.ToString(inv) + ":" +
                Saturation.ToString(inv) + ":" + Temperature.ToString(inv) + ":" + Sharpness.ToString(inv);
        }

        internal static bool TryParse(string text, out AdjustValues values) {
            values = default(AdjustValues);
            if (String.IsNullOrEmpty(text)) return false;
            var parts = text.Split(':');
            if (parts.Length != 5) return false;
            var inv = CultureInfo.InvariantCulture;
            int b, c, s, t, sh;
            if (!Int32.TryParse(parts[0], NumberStyles.Integer, inv, out b) ||
                !Int32.TryParse(parts[1], NumberStyles.Integer, inv, out c) ||
                !Int32.TryParse(parts[2], NumberStyles.Integer, inv, out s) ||
                !Int32.TryParse(parts[3], NumberStyles.Integer, inv, out t) ||
                !Int32.TryParse(parts[4], NumberStyles.Integer, inv, out sh)) return false;
            values = new AdjustValues { Brightness = b, Contrast = c, Saturation = s, Temperature = t, Sharpness = sh }.Clamped();
            return true;
        }

        internal static AdjustValues ReadXml(XElement n, int brightness) {
            return new AdjustValues {
                Brightness = brightness,
                Contrast = ReadInt(n, "contrast"),
                Saturation = ReadInt(n, "saturation"),
                Temperature = ReadInt(n, "temp"),
                Sharpness = ReadInt(n, "sharp")
            }.Clamped();
        }

        static int ReadInt(XElement n, string attr) {
            try { return (int?)n.Attribute(attr) ?? 0; }
            catch (FormatException) { return 0; }
        }
    }

    internal static class ColorAdjust {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ---------------- 1. 4-Stage Deadzone Auto-Adjust ----------------

        internal sealed class Diagnosis {
            public double MeanLuma, MedianLuma, P2Luma, P5Luma, P95Luma, P98Luma;
            public double Spread5To95, Spread2To98;
            public double WarmBiasRB, NeutralFraction, MeanChroma, MeanEdgeGrad;
        }

        internal static AdjustValues EstimateAuto(BitmapSource source) {
            if (source == null || source.PixelWidth < 2 || source.PixelHeight < 2) return AdjustValues.None;
            int w = source.PixelWidth, h = source.PixelHeight;
            BitmapSource work = source;
            int longSide = Math.Max(w, h);
            if (longSide > 320) {
                double scale = 320.0 / longSide;
                work = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            }
            var rgbBmp = work.Format == PixelFormats.Rgb24 ? work : new FormatConvertedBitmap(work, PixelFormats.Rgb24, null, 0);
            int rw = rgbBmp.PixelWidth, rh = rgbBmp.PixelHeight;
            var rgb = new byte[rw * rh * 3];
            rgbBmp.CopyPixels(rgb, rw * 3, 0);
            Diagnosis diag;
            return EstimateAuto(rgb, rw, rh, out diag);
        }

        internal static AdjustValues EstimateAuto(byte[] srcRgb, int w, int h, out Diagnosis diag) {
            int aw = w, ah = h;
            byte[] sample = srcRgb;
            int longSide = Math.Max(w, h);
            if (longSide > 320) {
                double scale = 320.0 / longSide;
                aw = Math.Max(8, (int)Math.Round(w * scale));
                ah = Math.Max(8, (int)Math.Round(h * scale));
                sample = Subsample(srcRgb, w, h, aw, ah);
            }

            diag = Analyze(sample, aw, ah);
            var p = new AdjustValues();

            // Stage 1: Color temperature (-100..+100) from the least-saturated 20% midtone pixels.
            double rb = diag.WarmBiasRB;
            if (diag.MeanChroma >= 4.0 && diag.NeutralFraction < 0.15) {
                if (rb > 0.16) {
                    double excess = rb - 0.10;
                    int cool = -(int)Math.Round(Math.Min(88.0, excess * 185.0));
                    if (Math.Abs(cool) >= 5) p.Temperature = cool;
                } else if (rb < -0.16) {
                    double excess = (-rb) - 0.10;
                    int warm = (int)Math.Round(Math.Min(80.0, excess * 170.0));
                    if (warm >= 5) p.Temperature = warm;
                }
            }

            byte[] tempAdjusted = p.Temperature != 0 ? ApplyTemperature(sample, p.Temperature) : sample;
            Diagnosis afterTemp = p.Temperature != 0 ? Analyze(tempAdjusted, aw, ah) : diag;

            // Stage 2: Brightness (-100..+100, Light.Soft curve). Normal midtones [95..160] stay at 0.
            int brightLevel = 0;
            double effMid = 0.60 * afterTemp.MedianLuma + 0.40 * afterTemp.MeanLuma;
            if (effMid < 95.0) {
                double xCurr = Math.Max(0.06, effMid / 255.0);
                double target = 115.0 + Math.Max(0.0, (95.0 - effMid) * 0.10);
                if (afterTemp.P98Luma > 225.0) target = Math.Min(target, 108.0);
                double xTarget = Math.Min(0.50, target / 255.0);
                double s = Math.Log(1.0 - xTarget) / Math.Log(1.0 - xCurr);
                double k = Math.Log(Math.Max(1.0, s)) / Math.Log(3.0);
                double ramp = Math.Min(1.0, (95.0 - effMid) / 15.0);
                int bVal = (int)Math.Round(k * 100.0 * 0.88 * ramp);
                if (bVal >= 4) brightLevel = Math.Min(70, bVal);
            } else if (effMid > 160.0 && afterTemp.P2Luma > 40.0) {
                double ramp = Math.Min(1.0, (effMid - 160.0) / 22.0);
                int bVal = -(int)Math.Round((effMid - 142.0) * 0.65 * ramp);
                if (bVal <= -4) brightLevel = Math.Max(-32, bVal);
            } else if (afterTemp.Spread2To98 < 135.0 && afterTemp.P2Luma > 52.0 && effMid > 130.0) {
                int pull = -(int)Math.Round(Math.Min(14.0, (effMid - 122.0) * 0.60));
                if (pull <= -4) brightLevel = pull;
            }
            p.Brightness = Light.Encode(brightLevel, Light.Soft);

            byte[] brightAdjusted = p.Brightness != 0 ? ApplyBrightness(tempAdjusted, p.Brightness) : tempAdjusted;
            Diagnosis afterBright = p.Brightness != 0 ? Analyze(brightAdjusted, aw, ah) : afterTemp;

            // Stage 3: Contrast (-100..+100). Only genuine haze or compressed tone span triggers contrast boost.
            bool isHazyOrFlat = (afterBright.Spread2To98 < 152.0) ||
                                (afterBright.P2Luma > 42.0 && afterBright.Spread5To95 < 118.0);
            if (isHazyOrFlat) {
                double targetSpan = 192.0;
                double wantedRatio = targetSpan / Math.Max(60.0, afterBright.Spread2To98);
                double veilBoost = Math.Max(0.0, (afterBright.P2Luma - 34.0) * 0.004);
                double cMul = Math.Min(1.58, 1.0 + (wantedRatio - 1.0) * 0.75 + veilBoost);
                int cVal = (int)Math.Round((cMul - 1.0) / 0.008);
                if (cVal >= 5) p.Contrast = Math.Min(68, cVal);
            }

            byte[] contrastAdjusted = p.Contrast != 0 ? ApplyEqAndUnsharp(brightAdjusted, aw, ah, p.Contrast, 0, 0) : brightAdjusted;
            Diagnosis afterContrast = p.Contrast != 0 ? Analyze(contrastAdjusted, aw, ah) : afterBright;

            // Stage 4: Saturation (-100..+100) and Sharpness (0..100).
            double chroma = afterContrast.MeanChroma;
            if (chroma >= 4.5 && chroma < 25.0) {
                double deficit = 28.0 - chroma;
                int sVal = (int)Math.Round(Math.Min(36.0, deficit * 1.45));
                if (sVal >= 5) p.Saturation = sVal;
            } else if (chroma > 96.0) {
                int sVal = -(int)Math.Round(Math.Min(16.0, (chroma - 92.0) * 0.45));
                if (sVal <= -5) p.Saturation = sVal;
            }

            if (diag.MeanLuma >= 75.0 && (diag.Spread2To98 < 135.0 || diag.MeanEdgeGrad < 3.6)) {
                double hazeFactor = diag.Spread2To98 < 135.0 ? (135.0 - diag.Spread2To98) * 0.22 : 0.0;
                double softFactor = diag.MeanEdgeGrad < 3.6 ? (3.6 - diag.MeanEdgeGrad) * 4.5 : 0.0;
                int sh = (int)Math.Round(Math.Min(24.0, hazeFactor + softFactor));
                if (sh >= 8) p.Sharpness = sh;
            }

            return p.Clamped();
        }

        struct MidPixel { public float Sat; public int R, G, B; }

        internal static Diagnosis Analyze(byte[] rgb, int w, int h) {
            var d = new Diagnosis();
            int n = w * h;
            if (n == 0) return d;
            int[] hist = new int[256];
            double sumY = 0, sumChroma = 0;
            int midCount = 0, neutralCount = 0;
            var midPixels = new List<MidPixel>(n);

            for (int i = 0; i < n; i++) {
                int i3 = i * 3;
                int r = rgb[i3], g = rgb[i3 + 1], b = rgb[i3 + 2];
                int y = (77 * r + 150 * g + 29 * b + 128) >> 8;
                hist[y]++;
                sumY += y;
                int maxC = r > g ? (r > b ? r : b) : (g > b ? g : b);
                int minC = r < g ? (r < b ? r : b) : (g < b ? g : b);
                int chr = maxC - minC;
                if (y >= 28 && y <= 232) {
                    sumChroma += chr;
                    midCount++;
                    if (Math.Abs(r - g) <= 8 && Math.Abs(g - b) <= 8 && Math.Abs(r - b) <= 10) neutralCount++;
                    MidPixel mp;
                    mp.Sat = chr / (float)Math.Max(28, maxC);
                    mp.R = r; mp.G = g; mp.B = b;
                    midPixels.Add(mp);
                }
            }

            d.MeanLuma = sumY / n;
            d.P2Luma = Percentile(hist, n, 0.02);
            d.P5Luma = Percentile(hist, n, 0.05);
            d.MedianLuma = Percentile(hist, n, 0.50);
            d.P95Luma = Percentile(hist, n, 0.95);
            d.P98Luma = Percentile(hist, n, 0.98);
            d.Spread5To95 = d.P95Luma - d.P5Luma;
            d.Spread2To98 = d.P98Luma - d.P2Luma;
            d.MeanChroma = midCount > 0 ? sumChroma / midCount : 0;
            d.NeutralFraction = midCount > 0 ? neutralCount / (double)midCount : 0;

            if (midPixels.Count >= 16) {
                midPixels.Sort(delegate(MidPixel a, MidPixel b) { return a.Sat.CompareTo(b.Sat); });
                int take = Math.Max(8, midPixels.Count / 5);
                double qR = 0, qG = 0, qB = 0;
                for (int i = 0; i < take; i++) { qR += midPixels[i].R; qG += midPixels[i].G; qB += midPixels[i].B; }
                d.WarmBiasRB = (qR - qB) / Math.Max(16.0 * take, qG);
            }

            double edgeSum = 0;
            int edgeCnt = 0;
            for (int y = 1; y + 1 < h; y++) {
                int row = y * w;
                for (int x = 1; x + 1 < w; x++) {
                    int p = row + x;
                    int yl = (77 * rgb[(p - 1) * 3] + 150 * rgb[(p - 1) * 3 + 1] + 29 * rgb[(p - 1) * 3 + 2]) >> 8;
                    int yr = (77 * rgb[(p + 1) * 3] + 150 * rgb[(p + 1) * 3 + 1] + 29 * rgb[(p + 1) * 3 + 2]) >> 8;
                    int yu = (77 * rgb[(p - w) * 3] + 150 * rgb[(p - w) * 3 + 1] + 29 * rgb[(p - w) * 3 + 2]) >> 8;
                    int yd = (77 * rgb[(p + w) * 3] + 150 * rgb[(p + w) * 3 + 1] + 29 * rgb[(p + w) * 3 + 2]) >> 8;
                    edgeSum += (Math.Abs(yr - yl) + Math.Abs(yd - yu)) * 0.25;
                    edgeCnt++;
                }
            }
            d.MeanEdgeGrad = edgeCnt > 0 ? edgeSum / edgeCnt : 0;
            return d;
        }

        static double Percentile(int[] hist, int total, double q) {
            int target = Math.Max(0, Math.Min(total - 1, (int)Math.Round(total * q)));
            int acc = 0;
            for (int i = 0; i < 256; i++) {
                acc += hist[i];
                if (acc > target) return i;
            }
            return 255;
        }

        static byte[] Subsample(byte[] src, int sw, int sh, int dw, int dh) {
            byte[] dst = new byte[dw * dh * 3];
            for (int y = 0; y < dh; y++) {
                int sy = Math.Min(sh - 1, (y * sh + sh / 2) / dh);
                for (int x = 0; x < dw; x++) {
                    int sx = Math.Min(sw - 1, (x * sw + sw / 2) / dw);
                    int s3 = (sy * sw + sx) * 3, d3 = (y * dw + x) * 3;
                    dst[d3] = src[s3]; dst[d3 + 1] = src[s3 + 1]; dst[d3 + 2] = src[s3 + 2];
                }
            }
            return dst;
        }

        // ---------------- 2. Exact C# Pixel Pipeline & FFmpeg Filter Builder ----------------

        sealed class Kept {
            internal WeakReference Source;
            internal AdjustValues Values;
            internal BitmapSource Result;
        }
        static readonly List<Kept> recent = new List<Kept>();

        internal static BitmapSource Apply(BitmapSource source, AdjustValues values) {
            if (source == null || values.IsIdentity) return source;
            values = values.Clamped();
            if (!values.HasBeyondBrightness) return Light.Apply(source, values.Brightness);
            bool canCache = source.IsFrozen;
            if (canCache) {
                lock (recent) {
                    for (int i = 0; i < recent.Count; i++) {
                        if (recent[i].Values == values && recent[i].Source.Target == source) return recent[i].Result;
                    }
                }
            }
            BitmapSource result = RunBitmap(source, values);
            if (canCache) {
                lock (recent) {
                    recent.Insert(0, new Kept { Source = new WeakReference(source), Values = values, Result = result });
                    if (recent.Count > 6) recent.RemoveAt(recent.Count - 1);
                }
            }
            return result;
        }

        static BitmapSource RunBitmap(BitmapSource source, AdjustValues values) {
            int w = source.PixelWidth, h = source.PixelHeight;
            if (w < 1 || h < 1) return source;
            bool hasAlpha = source.Format != PixelFormats.Bgr32 && source.Format != PixelFormats.Bgr24 && source.Format != PixelFormats.Rgb24;
            if (!hasAlpha) {
                var rgbBmp = source.Format == PixelFormats.Rgb24 ? source : new FormatConvertedBitmap(source, PixelFormats.Rgb24, null, 0);
                int stride = w * 3;
                var rgb = new byte[stride * h];
                rgbBmp.CopyPixels(rgb, stride, 0);
                byte[] outRgb = ApplyRgb(rgb, w, h, values);
                var made = BitmapSource.Create(w, h, source.DpiX, source.DpiY, PixelFormats.Rgb24, null, outRgb, stride);
                made.Freeze();
                return made;
            }
            var bgra = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int bStride = w * 4;
            var pixels = new byte[bStride * h];
            bgra.CopyPixels(pixels, bStride, 0);
            var rgbIn = new byte[w * h * 3];
            for (int i = 0, j = 0; i < pixels.Length; i += 4, j += 3) {
                rgbIn[j] = pixels[i + 2];
                rgbIn[j + 1] = pixels[i + 1];
                rgbIn[j + 2] = pixels[i];
            }
            byte[] rgbOut = ApplyRgb(rgbIn, w, h, values);
            for (int i = 0, j = 0; i < pixels.Length; i += 4, j += 3) {
                if (pixels[i + 3] == 0) continue;
                pixels[i + 2] = rgbOut[j];
                pixels[i + 1] = rgbOut[j + 1];
                pixels[i] = rgbOut[j + 2];
            }
            var result = BitmapSource.Create(w, h, source.DpiX, source.DpiY, PixelFormats.Bgra32, null, pixels, bStride);
            result.Freeze();
            return result;
        }

        internal static byte[] ApplyRgb(byte[] srcRgb, int w, int h, AdjustValues p) {
            p = p.Clamped();
            byte[] cur = new byte[srcRgb.Length];
            Buffer.BlockCopy(srcRgb, 0, cur, 0, srcRgb.Length);
            if (p.IsIdentity) return cur;
            if (p.Brightness != 0) cur = ApplyBrightness(cur, p.Brightness);
            if (p.Temperature != 0) cur = ApplyTemperature(cur, p.Temperature);
            if (p.Contrast != 0 || p.Saturation != 0 || p.Sharpness > 0)
                cur = ApplyEqAndUnsharp(cur, w, h, p.Contrast, p.Saturation, p.Sharpness);
            return cur;
        }

        // Appends the exact FFmpeg filter steps matching ApplyRgb (expects rgb24/rgba input, leaves rgb24/rgba output).
        internal static void AppendFfmpegTone(List<string> steps, AdjustValues p, bool keepAlpha) {
            p = p.Clamped();
            if (p.Brightness != 0) {
                string light = Light.Filter(p.Brightness);
                if (light != null) steps.Add(light);
            }
            if (p.Temperature != 0) {
                int kelvin;
                double mix;
                TemperatureToFfmpeg(p.Temperature, out kelvin, out mix);
                steps.Add("colortemperature=temperature=" + kelvin.ToString(Inv) + ":mix=" + Fmt(mix) + ":pl=1");
            }
            if (p.Contrast != 0 || p.Saturation != 0) {
                steps.Add(keepAlpha ? "format=yuva444p" : "format=yuv444p");
                steps.Add("eq=contrast=" + Fmt(ContrastToFfmpeg(p.Contrast)) + ":saturation=" + Fmt(SaturationToFfmpeg(p.Saturation)));
                steps.Add(keepAlpha ? "format=rgba" : "format=rgb24");
            }
        }

        internal static void AppendFfmpegSharpness(List<string> steps, int sharpness, bool keepAlpha) {
            sharpness = Math.Max(0, Math.Min(100, sharpness));
            if (sharpness <= 0) return;
            steps.Add(keepAlpha ? "format=yuva444p" : "format=yuv444p");
            steps.Add("unsharp=lx=5:ly=5:la=" + Fmt(SharpnessToFfmpeg(sharpness)) + ":cx=5:cy=5:ca=0");
            steps.Add(keepAlpha ? "format=rgba" : "format=rgb24");
        }

        internal static string BuildFfmpegFilter(AdjustValues p, bool keepAlpha = false) {
            p = p.Clamped();
            string rgbFmt = keepAlpha ? "format=rgba" : "format=rgb24";
            if (p.IsIdentity) return rgbFmt;
            var steps = new List<string> { rgbFmt };
            if (p.Brightness != 0) {
                string light = Light.Filter(p.Brightness);
                if (light != null) steps.Add(light);
            }
            if (p.Temperature != 0) {
                int kelvin;
                double mix;
                TemperatureToFfmpeg(p.Temperature, out kelvin, out mix);
                steps.Add("colortemperature=temperature=" + kelvin.ToString(Inv) + ":mix=" + Fmt(mix) + ":pl=1");
            }
            if (p.Contrast != 0 || p.Saturation != 0 || p.Sharpness > 0) {
                steps.Add(keepAlpha ? "format=yuva444p" : "format=yuv444p");
                if (p.Contrast != 0 || p.Saturation != 0)
                    steps.Add("eq=contrast=" + Fmt(ContrastToFfmpeg(p.Contrast)) + ":saturation=" + Fmt(SaturationToFfmpeg(p.Saturation)));
                if (p.Sharpness > 0)
                    steps.Add("unsharp=lx=5:ly=5:la=" + Fmt(SharpnessToFfmpeg(p.Sharpness)) + ":cx=5:cy=5:ca=0");
                steps.Add(rgbFmt);
            }
            return String.Join(",", steps.ToArray());
        }

        internal static double ContrastToFfmpeg(int contrast) { return Math.Max(0.20, Math.Min(1.80, 1.0 + contrast * 0.008)); }
        internal static double SaturationToFfmpeg(int saturation) { return Math.Max(0.0, Math.Min(2.0, 1.0 + saturation * 0.01)); }
        internal static double SharpnessToFfmpeg(int sharpness) { return Math.Max(0.0, Math.Min(2.0, sharpness * 0.02)); }

        internal static void TemperatureToFfmpeg(int temp, out int kelvin, out double mix) {
            temp = Math.Max(-100, Math.Min(100, temp));
            if (temp >= 0) { kelvin = 2600; mix = temp * 0.0085; }
            else { kelvin = 24000; mix = (-temp) * 0.0095; }
        }

        static byte[] ApplyBrightness(byte[] src, int encodedBrightness) {
            byte[] table = Light.Table(encodedBrightness);
            byte[] dst = new byte[src.Length];
            for (int i = 0; i < src.Length; i++) dst[i] = table[src[i]];
            return dst;
        }

        // Exact replica of FFmpeg libavfilter/vf_colortemperature.c (kelvin2rgb + mix + pl=1).
        static byte[] ApplyTemperature(byte[] src, int temp) {
            int kelvinInt;
            double mixD;
            TemperatureToFfmpeg(temp, out kelvinInt, out mixD);
            float mix = (float)mixD;
            float k = kelvinInt / 100.0f;

            float kr, kg, kb;
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

            float mr = 1.0f + mix * (kr - 1.0f);
            float mg = 1.0f + mix * (kg - 1.0f);
            float mb = 1.0f + mix * (kb - 1.0f);

            byte[] dst = new byte[src.Length];
            for (int i = 0; i < src.Length; i += 3) {
                int r0 = src[i], g0 = src[i + 1], b0 = src[i + 2];
                int nr = Clamp255((int)Math.Round(r0 * mr));
                int ng = Clamp255((int)Math.Round(g0 * mg));
                int nb = Clamp255((int)Math.Round(b0 * mb));

                int max0 = r0 > g0 ? (r0 > b0 ? r0 : b0) : (g0 > b0 ? g0 : b0);
                int min0 = r0 < g0 ? (r0 < b0 ? r0 : b0) : (g0 < b0 ? g0 : b0);
                int max1 = nr > ng ? (nr > nb ? nr : nb) : (ng > nb ? ng : nb);
                int min1 = nr < ng ? (nr < nb ? nr : nb) : (ng < nb ? ng : nb);

                int l0 = max0 + min0, l1 = max1 + min1;
                if (l1 > 0 && l0 != l1) {
                    float scale = (float)l0 / l1;
                    nr = Clamp255((int)(nr * scale + 0.5f));
                    ng = Clamp255((int)(ng * scale + 0.5f));
                    nb = Clamp255((int)(nb * scale + 0.5f));
                }
                dst[i] = (byte)nr;
                dst[i + 1] = (byte)ng;
                dst[i + 2] = (byte)nb;
            }
            return dst;
        }

        // Exact replica of FFmpeg format=yuv444p (BT.601) + vf_eq.c + vf_unsharp.c + format=rgb24.
        internal static byte[] ApplyEqAndUnsharp(byte[] src, int w, int h, int contrast, int saturation, int sharpness) {
            int n = w * h;
            byte[] yPlane = new byte[n];
            byte[] cbPlane = new byte[n];
            byte[] crPlane = new byte[n];

            for (int i = 0; i < n; i++) {
                int i3 = i * 3;
                int r = src[i3], g = src[i3 + 1], b = src[i3 + 2];
                yPlane[i] = (byte)Clamp255(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
                cbPlane[i] = (byte)Clamp255(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
                crPlane[i] = (byte)Clamp255(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
            }

            if (contrast != 0) {
                double c = ContrastToFfmpeg(contrast);
                byte[] lutY = new byte[256];
                for (int v = 0; v < 256; v++) lutY[v] = (byte)Clamp255((int)((((v / 255.0) - 0.5) * c + 0.5) * 256.0));
                for (int i = 0; i < n; i++) yPlane[i] = lutY[yPlane[i]];
            }

            if (saturation != 0) {
                double s = SaturationToFfmpeg(saturation);
                byte[] lutC = new byte[256];
                for (int v = 0; v < 256; v++) lutC[v] = (byte)Clamp255((int)((((v / 255.0) - 0.5) * s + 0.5) * 256.0));
                for (int i = 0; i < n; i++) {
                    cbPlane[i] = lutC[cbPlane[i]];
                    crPlane[i] = lutC[crPlane[i]];
                }
            }

            if (sharpness > 0 && w >= 5 && h >= 5) {
                int amount = (int)(SharpnessToFfmpeg(sharpness) * 65536.0);
                yPlane = Unsharp5x5(yPlane, w, h, amount);
            }

            byte[] dst = new byte[src.Length];
            for (int i = 0; i < n; i++) {
                int c = yPlane[i] - 16, d = cbPlane[i] - 128, e = crPlane[i] - 128;
                int i3 = i * 3;
                dst[i3] = (byte)Clamp255((298 * c + 409 * e + 128) >> 8);
                dst[i3 + 1] = (byte)Clamp255((298 * c - 100 * d - 208 * e + 128) >> 8);
                dst[i3 + 2] = (byte)Clamp255((298 * c + 516 * d + 128) >> 8);
            }
            return dst;
        }

        static byte[] Unsharp5x5(byte[] plane, int w, int h, int amountFixed16) {
            int n = w * h;
            int[] rowBlur = new int[n];
            for (int y = 0; y < h; y++) {
                int row = y * w;
                for (int x = 0; x < w; x++) {
                    int x0 = x - 2 < 0 ? 0 : x - 2;
                    int x1 = x - 1 < 0 ? 0 : x - 1;
                    int x3 = x + 1 >= w ? w - 1 : x + 1;
                    int x4 = x + 2 >= w ? w - 1 : x + 2;
                    rowBlur[row + x] = plane[row + x0] + 4 * plane[row + x1] + 6 * plane[row + x] + 4 * plane[row + x3] + plane[row + x4];
                }
            }
            byte[] dst = new byte[n];
            for (int y = 0; y < h; y++) {
                int y0 = (y - 2 < 0 ? 0 : y - 2) * w;
                int y1 = (y - 1 < 0 ? 0 : y - 1) * w;
                int y2 = y * w;
                int y3 = (y + 1 >= h ? h - 1 : y + 1) * w;
                int y4 = (y + 2 >= h ? h - 1 : y + 2) * w;
                for (int x = 0; x < w; x++) {
                    int sum = rowBlur[y0 + x] + 4 * rowBlur[y1 + x] + 6 * rowBlur[y2 + x] + 4 * rowBlur[y3 + x] + rowBlur[y4 + x];
                    int blurred = (sum + 128) >> 8;
                    int srcVal = plane[y2 + x];
                    int res = srcVal + (int)(((long)(srcVal - blurred) * amountFixed16) >> 16);
                    dst[y2 + x] = (byte)Clamp255(res);
                }
            }
            return dst;
        }

        static string Fmt(double v) { return v.ToString("0.########", Inv); }
        static int Clamp255(int v) { return v < 0 ? 0 : (v > 255 ? 255 : v); }
        static float Clamp01(float v) { return v < 0f ? 0f : (v > 1f ? 1f : v); }
    }

    // Folded [Adjust ▾] panel shared by CropWindow (JPG cover) and MotionWindow (GIF cover).
    internal sealed class AdjustPanel : StackPanel {
        readonly Button opener, autoBtn, resetBtn, styleBtn;
        readonly StackPanel body = new StackPanel { Margin = new Thickness(0, 6, 0, 4) };
        readonly ScrollViewer scroller;
        readonly Func<BitmapSource> samplePicture;
        readonly AdjustRows rows;
        readonly TextBlock statusNote;
        AdjustValues current;
        int lightStyle = Light.Soft;

        internal event Action Changed, OpenChanged, AreaRequested;
        internal bool IsOpen { get { return body.Visibility == Visibility.Visible; } }
        // The [Adjust ▾] button, which a window may take out and put beside the [Draw ▾] button.
        internal Button Opener { get { return opener; } }
        internal double ControlsHeight { set { scroller.MaxHeight = Math.Max(0, value); } }
        internal AdjustValues Values { get { return current; } }
        internal int LightStyle { get { return lightStyle; } }

        internal AdjustPanel(AdjustValues initial, Func<BitmapSource> samplePicture) {
            this.samplePicture = samplePicture;
            opener = UI.Btn("", Toggle);
            opener.HorizontalAlignment = HorizontalAlignment.Left;
            opener.Margin = new Thickness(0, 2, 0, 2);
            AutomationProperties.SetName(opener, L.P("Adjust", "보정"));
            opener.ToolTip = L.P("Adjust brightness, contrast, saturation, color temperature and sharpness for this cover.",
                "표지의 밝기, 대비, 채도, 색 온도, 선명하게를 조절해요.");

            scroller = new ScrollViewer {
                Content = body,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false
            };
            Children.Add(opener);
            Children.Add(scroller);

            var topRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 2), VerticalAlignment = VerticalAlignment.Center };
            autoBtn = UI.Btn(L.P("Auto adjust", "자동 보정"), RunAuto, true);
            autoBtn.Margin = new Thickness(0, 0, 6, 2);
            autoBtn.ToolTip = L.P("Finds natural brightness and color settings in one click. Pictures that already look good stay as they are.",
                "한 번 누르면 사진에 맞는 밝기와 색을 찾아 줘요. 이미 괜찮은 사진은 그대로 둬요.");
            resetBtn = UI.Btn(L.T("Reset"), ResetAdjust);
            resetBtn.Margin = new Thickness(0, 0, 6, 2);
            resetBtn.ToolTip = L.P("Puts all adjustment numbers back to 0.", "보정 숫자를 모두 0으로 되돌려요.");
            // The brightness style sits with the buttons for the whole picture, so every row below has the same shape.
            styleBtn = UI.Btn("", SwitchStyle);
            styleBtn.Margin = new Thickness(0, 0, 6, 2);
            styleBtn.ToolTip = L.P("Switch brightness style (R): Soft, See in the dark, Milky",
                "밝기 방식을 바꿔요 (R): 부드럽게, 어두운 곳 보기, 뽀얗게");
            statusNote = UI.Text("", 12, true);
            statusNote.Margin = new Thickness(2, 0, 0, 2);
            statusNote.TextWrapping = TextWrapping.Wrap;
            statusNote.Visibility = Visibility.Collapsed;
            topRow.Children.Add(autoBtn);
            topRow.Children.Add(resetBtn);
            topRow.Children.Add(styleBtn);
            // Only a part of the picture: [Draw] opens with [0 Adjust part] chosen.
            var areaBtn = UI.Btn(L.P("Only a part", "일부만 보정"), () => { if (AreaRequested != null) AreaRequested(); });
            areaBtn.Margin = new Thickness(0, 0, 6, 2);
            areaBtn.ToolTip = L.P("Drag over a part of the picture and adjust only that part.", "사진에서 한 부분을 끌어서 고르고 그곳만 보정해요.");
            topRow.Children.Add(areaBtn);
            body.Children.Add(topRow);
            body.Children.Add(statusNote);

            rows = new AdjustRows((field, v) => {
                if (field == 0) SetField(brightnessLevel: v);
                else if (field == 1) SetField(contrast: v);
                else if (field == 2) SetField(saturation: v);
                else if (field == 3) SetField(temperature: v);
                else SetField(sharpness: v);
            });
            body.Children.Add(rows);

            body.Visibility = scroller.Visibility = Visibility.Collapsed;
            Load(initial);
        }

        internal void SetOpen(bool open) {
            if (IsOpen == open) return;
            body.Visibility = scroller.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            ShowHeader();
            if (OpenChanged != null) OpenChanged();
        }

        void Toggle() { SetOpen(!IsOpen); }

        internal void Load(AdjustValues values, int? explicitStyle = null) {
            current = values.Clamped();
            lightStyle = explicitStyle.HasValue ? explicitStyle.Value : (current.Brightness != 0 ? Light.Style(current.Brightness) : Light.Soft);
            statusNote.Visibility = Visibility.Collapsed;
            SyncControls();
        }

        internal void SetBrightnessLevel(int level) {
            SetField(brightnessLevel: Light.Clamp(level));
        }

        internal void SwitchStyle() {
            lightStyle = Light.NextStyle(lightStyle);
            int level = Light.Level(current.Brightness);
            var next = current;
            next.Brightness = Light.Encode(level, lightStyle);
            bool changed = next != current;
            current = next;
            statusNote.Visibility = Visibility.Collapsed;
            SyncControls();
            if (changed && Changed != null) Changed();
        }

        void SetField(int? brightnessLevel = null, int? contrast = null, int? saturation = null, int? temperature = null, int? sharpness = null) {
            var next = current;
            if (brightnessLevel.HasValue) next.Brightness = Light.Encode(Light.Clamp(brightnessLevel.Value), lightStyle);
            if (contrast.HasValue) next.Contrast = contrast.Value;
            if (saturation.HasValue) next.Saturation = saturation.Value;
            if (temperature.HasValue) next.Temperature = temperature.Value;
            if (sharpness.HasValue) next.Sharpness = sharpness.Value;
            next = next.Clamped();
            statusNote.Visibility = Visibility.Collapsed;
            if (next == current) { SyncControls(); return; }
            current = next;
            SyncControls();
            if (Changed != null) Changed();
        }

        void RunAuto() {
            BitmapSource sample = samplePicture != null ? samplePicture() : null;
            if (sample == null) return;
            AdjustValues estimated = ColorAdjust.EstimateAuto(sample);
            lightStyle = Light.Soft;
            bool wasSame = estimated == current;
            current = estimated;
            if (estimated.IsIdentity) {
                statusNote.Text = L.P("Already looks balanced, so nothing was changed.", "이미 균형이 괜찮아서 그대로 두었어요.");
                statusNote.Visibility = Visibility.Visible;
            } else {
                statusNote.Visibility = Visibility.Collapsed;
            }
            SyncControls();
            if (!wasSame && Changed != null) Changed();
        }

        void ResetAdjust() {
            lightStyle = Light.Soft;
            statusNote.Visibility = Visibility.Collapsed;
            if (current.IsIdentity) { SyncControls(); return; }
            current = AdjustValues.None;
            SyncControls();
            if (Changed != null) Changed();
        }

        void SyncControls() {
            rows.Show(current);
            styleBtn.Content = "R  " + Light.StyleName(lightStyle);
            resetBtn.IsEnabled = !current.IsIdentity || lightStyle != Light.Soft;
            ShowHeader();
        }

        void ShowHeader() {
            string label = L.P("Adjust", "보정");
            if (!IsOpen && !current.IsIdentity) {
                var parts = new List<string>();
                int b = Light.Level(current.Brightness);
                if (b != 0) parts.Add(L.T("Brightness") + " " + b.ToString("+0;-0;0", CultureInfo.CurrentCulture));
                if (current.Contrast != 0) parts.Add(L.P("Contrast", "대비") + " " + current.Contrast.ToString("+0;-0;0", CultureInfo.CurrentCulture));
                if (current.Saturation != 0) parts.Add(L.P("Saturation", "채도") + " " + current.Saturation.ToString("+0;-0;0", CultureInfo.CurrentCulture));
                if (current.Temperature != 0) parts.Add(L.P("Color temp", "색 온도") + " " + current.Temperature.ToString("+0;-0;0", CultureInfo.CurrentCulture));
                if (current.Sharpness > 0) parts.Add(L.P("Sharpness", "선명하게") + " " + current.Sharpness.ToString(CultureInfo.CurrentCulture));
                if (parts.Count > 2) label += " (" + parts[0] + " · " + parts[1] + L.P(" +" + (parts.Count - 2), " 외 " + (parts.Count - 2)) + ")";
                else if (parts.Count > 0) label += " (" + String.Join(" · ", parts.ToArray()) + ")";
            }
            opener.Content = label + (IsOpen ? "  ▴" : "  ▾");
        }
    }
}
