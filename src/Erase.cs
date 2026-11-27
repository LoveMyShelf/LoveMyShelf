using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace LoveMyShelf {
    // Holds a non-destructive LaMa AI erase layer (transparent BGRA32 PNG of the cover's size) and its cache key.
    public sealed class EraseAiPatch {
        internal readonly BitmapSource Patch;
        internal readonly string StrokesSpec;
        internal readonly string CacheKey;
        internal readonly double SceneMs;

        internal EraseAiPatch(BitmapSource patch, string strokesSpec, string cacheKey, double sceneMs = 0) {
            Patch = patch;
            StrokesSpec = strokesSpec ?? "";
            CacheKey = cacheKey ?? "";
            SceneMs = sceneMs;
        }
    }

    internal static class EraseStore {
        internal const string JpgPatchFile = "erase-ai.png";
        internal const string JpgKeyFile = "erase-ai.key";
        internal const string GifPatchFile = "erase-ai-gif.png";
        internal const string GifKeyFile = "erase-ai-gif.key";

        internal static string CanonicalEraseStrokes(IEnumerable<MotionShape> shapes) {
            if (shapes == null) return "";
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            foreach (var s in shapes) {
                if (!s.IsErase) continue;
                sb.Append(s.Thickness.ToString(inv)).Append(':').Append(s.IsPerFrameErase ? '1' : '0');
                for (int i = 0; i < s.Points.Length; i++) {
                    sb.Append(',').Append(MotionShape.Clamp(s.Points[i].X).ToString("0.####", inv))
                      .Append(',').Append(MotionShape.Clamp(s.Points[i].Y).ToString("0.####", inv));
                }
                sb.Append('|');
            }
            return sb.ToString();
        }

        internal static string BuildCacheKey(string sourceIdentity, Rect crop, double rotation, AdjustValues adjust,
            string canonicalStrokes, bool flipX = false, bool flipY = false) {
            var inv = CultureInfo.InvariantCulture;
            string strokeHash = Sha256Hex(canonicalStrokes ?? "");
            string raw = String.Join("|", new[] {
                sourceIdentity ?? "",
                Math.Round(crop.X, 5).ToString(inv),
                Math.Round(crop.Y, 5).ToString(inv),
                Math.Round(crop.Width, 5).ToString(inv),
                Math.Round(crop.Height, 5).ToString(inv),
                Math.Round(CropSurface.NormalizeRotation(rotation), 2).ToString(inv),
                adjust.Clamped().KeyText(),
                flipX ? "fx1" : "fx0",
                flipY ? "fy1" : "fy0",
                strokeHash,
                EraseEngine.ExpectedSha256
            });
            return Sha256Hex(raw);
        }

        internal static string SourceIdentity(string path, int frameIndex, double? frameTime, string outsidePicture = null) {
            var inv = CultureInfo.InvariantCulture;
            string target = !String.IsNullOrEmpty(outsidePicture) ? outsidePicture : path;
            string filePart = target ?? "";
            try {
                if (!String.IsNullOrEmpty(target) && File.Exists(target)) {
                    var info = new FileInfo(target);
                    filePart = Path.GetFileName(target).ToLowerInvariant() + ":" + info.Length.ToString(inv) + ":" + info.LastWriteTimeUtc.Ticks.ToString(inv);
                }
            } catch (Exception) { }
            return filePart + ":f" + frameIndex.ToString(inv) + ":t" + (frameTime.HasValue ? Math.Round(frameTime.Value).ToString(inv) : "-");
        }

        internal static string Sha256Hex(string text) {
            using (var sha = SHA256.Create()) {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? ""));
                return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
            }
        }

        internal static void SaveToRecord(string recordFolder, EraseAiPatch patch, bool forGif = false) {
            if (String.IsNullOrEmpty(recordFolder)) return;
            string pngPath = Path.Combine(recordFolder, forGif ? GifPatchFile : JpgPatchFile);
            string keyPath = Path.Combine(recordFolder, forGif ? GifKeyFile : JpgKeyFile);
            if (patch == null || patch.Patch == null || String.IsNullOrEmpty(patch.CacheKey)) {
                DeleteQuiet(pngPath);
                DeleteQuiet(keyPath);
                return;
            }
            Directory.CreateDirectory(recordFolder);
            string tmpPng = pngPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            string tmpKey = keyPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                SaveBgraPng(patch.Patch, tmpPng);
                File.WriteAllText(tmpKey, patch.CacheKey + "\n" + patch.SceneMs.ToString("R", CultureInfo.InvariantCulture) + "\n" + patch.StrokesSpec, Encoding.UTF8);
                ReplaceOrMove(tmpPng, pngPath);
                ReplaceOrMove(tmpKey, keyPath);
            } finally {
                DeleteQuiet(tmpPng);
                DeleteQuiet(tmpKey);
            }
        }

        internal static EraseAiPatch TryLoadFromRecord(string recordFolder, string expectedKey, string expectedStrokes, bool forGif = false) {
            if (String.IsNullOrEmpty(recordFolder) || String.IsNullOrEmpty(expectedKey)) return null;
            var loaded = TryLoadSingle(recordFolder, forGif ? GifPatchFile : JpgPatchFile, forGif ? GifKeyFile : JpgKeyFile, expectedKey, expectedStrokes);
            if (loaded != null) return loaded;
            if (forGif) return TryLoadSingle(recordFolder, JpgPatchFile, JpgKeyFile, expectedKey, expectedStrokes);
            return null;
        }

        static EraseAiPatch TryLoadSingle(string folder, string pngName, string keyName, string expectedKey, string expectedStrokes) {
            try {
                string pngPath = Path.Combine(folder, pngName);
                string keyPath = Path.Combine(folder, keyName);
                if (!File.Exists(pngPath) || !File.Exists(keyPath)) return null;
                string[] lines = File.ReadAllLines(keyPath, Encoding.UTF8);
                if (lines.Length < 1 || !String.Equals(lines[0].Trim(), expectedKey, StringComparison.OrdinalIgnoreCase)) return null;
                double sceneMs = 0;
                if (lines.Length >= 2) Double.TryParse(lines[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out sceneMs);
                string strokes = lines.Length >= 3 ? lines[2] : (expectedStrokes ?? "");
                if (!String.IsNullOrEmpty(expectedStrokes) && strokes != expectedStrokes) return null;
                using (var stream = File.Open(pngPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete)) {
                    var frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    var bgra = frame.Format == PixelFormats.Bgra32 ? (BitmapSource)frame : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                    bgra.Freeze();
                    return new EraseAiPatch(bgra, strokes, expectedKey, sceneMs);
                }
            } catch (Exception) {
                return null;
            }
        }

        internal static void SaveBgraPng(BitmapSource source, string path) {
            var bgra = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bgra));
            using (var stream = File.Create(path)) encoder.Save(stream);
        }

        internal static BitmapSource Composite(BitmapSource basePicture, BitmapSource patchBgra) {
            if (basePicture == null || patchBgra == null) return basePicture;
            int w = basePicture.PixelWidth, h = basePicture.PixelHeight;
            if (w < 1 || h < 1) return basePicture;
            BitmapSource result = null;
            MotionDraw.RunSta(() => {
                var pbgraPatch = patchBgra.Format == PixelFormats.Pbgra32 ? patchBgra : new FormatConvertedBitmap(patchBgra, PixelFormats.Pbgra32, null, 0);
                var visual = new DrawingVisual();
                RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
                using (var dc = visual.RenderOpen()) {
                    dc.DrawImage(basePicture, new Rect(0, 0, w, h));
                    dc.DrawImage(pbgraPatch, new Rect(0, 0, w, h));
                }
                var target = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                target.Render(visual);
                target.Freeze();
                result = target;
            });
            return result;
        }

        static void ReplaceOrMove(string temp, string dest) {
            if (File.Exists(dest)) File.Replace(temp, dest, null);
            else File.Move(temp, dest);
        }

        static void DeleteQuiet(string path) {
            if (String.IsNullOrEmpty(path)) return;
            try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
        }
    }

    // ---------------- Pure C# Hybrid Inpainter (Telea + Multi-Scale EM PatchMatch) ----------------

    internal static class CoverInpaint {
        const byte Known = 0, Band = 1, Inside = 2;
        const float Inf = 1e9f, FloatMax = 1e30f;

        sealed class Kept {
            internal WeakReference Source;
            internal string Key;
            internal BitmapSource Result;
        }
        static readonly List<Kept> recentFull = new List<Kept>();
        static readonly List<Kept> recentPatch = new List<Kept>();

        internal static byte[] RasterizeMask(IList<MotionShape> shapes, int w, int h) {
            byte[] mask = new byte[w * h];
            if (shapes == null || w < 2 || h < 2) return mask;
            var eraseShapes = shapes.Where(s => s.IsErase && s.Points != null && s.Points.Length >= 1).ToList();
            if (eraseShapes.Count == 0) return mask;

            MotionDraw.RunSta(() => {
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen()) {
                    dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, w, h));
                    for (int i = 0; i < eraseShapes.Count; i++) {
                        var s = eraseShapes[i];
                        double px = MotionDraw.Pixels(s, h);
                        var pen = MotionDraw.Stroke(Brushes.White, px);
                        if (s.Points.Length == 1 || (s.Points.Length == 2 && s.Points[0] == s.Points[1])) {
                            Point c = MotionDraw.At(s.Points[0], w, h);
                            dc.DrawEllipse(Brushes.White, null, c, px * 0.5, px * 0.5);
                        } else {
                            var geom = new StreamGeometry();
                            using (var g = geom.Open()) {
                                g.BeginFigure(MotionDraw.At(s.Points[0], w, h), false, false);
                                for (int k = 1; k < s.Points.Length; k++)
                                    g.LineTo(MotionDraw.At(s.Points[k], w, h), true, false);
                            }
                            dc.DrawGeometry(null, pen, geom);
                        }
                    }
                }
                var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(visual);
                var pixels = new byte[w * h * 4];
                rtb.CopyPixels(pixels, w * 4, 0);
                for (int i = 0, p = 0; i < mask.Length; i++, p += 4) {
                    mask[i] = pixels[p + 1] >= 48 ? (byte)1 : (byte)0;
                }
            });
            return mask;
        }

        internal static BitmapSource ApplyHybrid(BitmapSource source, IList<MotionShape> shapes) {
            if (source == null || shapes == null || !shapes.Any(s => s.IsErase)) return source;
            string spec = EraseStore.CanonicalEraseStrokes(shapes);
            lock (recentFull) {
                for (int i = 0; i < recentFull.Count; i++) {
                    if (recentFull[i].Key == spec && recentFull[i].Source.Target == source) return recentFull[i].Result;
                }
            }
            int w = source.PixelWidth, h = source.PixelHeight;
            if (w < 4 || h < 4) return source;
            byte[] rawMask = RasterizeMask(shapes, w, h);
            var rgbBmp = source.Format == PixelFormats.Rgb24 ? source : new FormatConvertedBitmap(source, PixelFormats.Rgb24, null, 0);
            var srcRgb = new byte[w * h * 3];
            rgbBmp.CopyPixels(srcRgb, w * 3, 0);
            byte[] outRgb = RunHybridRgb(srcRgb, w, h, rawMask, 2);
            var result = BitmapSource.Create(w, h, source.DpiX, source.DpiY, PixelFormats.Rgb24, null, outRgb, w * 3);
            result.Freeze();
            lock (recentFull) {
                recentFull.Insert(0, new Kept { Source = new WeakReference(source), Key = spec, Result = result });
                if (recentFull.Count > 24) recentFull.RemoveAt(recentFull.Count - 1);
            }
            return result;
        }

        // Builds a transparent BGRA32 patch of (w, h) containing only the erased hole + 2px soft feather border.
        internal static BitmapSource BuildHybridPatch(BitmapSource source, IList<MotionShape> shapes) {
            if (source == null || shapes == null || !shapes.Any(s => s.IsErase)) return null;
            string spec = EraseStore.CanonicalEraseStrokes(shapes);
            lock (recentPatch) {
                for (int i = 0; i < recentPatch.Count; i++) {
                    if (recentPatch[i].Key == spec && recentPatch[i].Source.Target == source) return recentPatch[i].Result;
                }
            }
            int w = source.PixelWidth, h = source.PixelHeight;
            if (w < 4 || h < 4) return null;
            byte[] rawMask = RasterizeMask(shapes, w, h);
            byte[] coreMask = DilateMask(rawMask, w, h, 2);
            byte[] outerMask = DilateMask(coreMask, w, h, 2);
            byte[] guardMask = DilateMask(outerMask, w, h, 2);
            float[] alpha = BuildSoftBlendAlpha(coreMask, outerMask, w, h, 2);

            var rgbBmp = source.Format == PixelFormats.Rgb24 ? source : new FormatConvertedBitmap(source, PixelFormats.Rgb24, null, 0);
            var srcRgb = new byte[w * h * 3];
            rgbBmp.CopyPixels(srcRgb, w * 3, 0);
            byte[] predRgb = RunHybridRgb(srcRgb, w, h, outerMask, 0);

            byte[] bgra = new byte[w * h * 4];
            for (int i = 0; i < coreMask.Length; i++) {
                if (guardMask[i] == 0) continue;
                int s3 = i * 3, d4 = i * 4;
                bgra[d4] = predRgb[s3 + 2];
                bgra[d4 + 1] = predRgb[s3 + 1];
                bgra[d4 + 2] = predRgb[s3];
                bgra[d4 + 3] = (byte)Clamp255((int)(alpha[i] * 255f + 0.5f));
            }
            var patch = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
            patch.Freeze();
            lock (recentPatch) {
                recentPatch.Insert(0, new Kept { Source = new WeakReference(source), Key = spec, Result = patch });
                if (recentPatch.Count > 8) recentPatch.RemoveAt(recentPatch.Count - 1);
            }
            return patch;
        }

        internal static byte[] DilateMask(byte[] mask, int w, int h, int radius) {
            if (radius <= 0) {
                byte[] copy = new byte[mask.Length];
                Buffer.BlockCopy(mask, 0, copy, 0, mask.Length);
                return copy;
            }
            byte[] dst = new byte[w * h];
            int r2 = radius * radius;
            for (int y = 0; y < h; y++) {
                int y0 = Math.Max(0, y - radius), y1 = Math.Min(h - 1, y + radius);
                for (int x = 0; x < w; x++) {
                    if (mask[y * w + x] != 0) { dst[y * w + x] = 1; continue; }
                    int x0 = Math.Max(0, x - radius), x1 = Math.Min(w - 1, x + radius);
                    byte hit = 0;
                    for (int ny = y0; ny <= y1 && hit == 0; ny++) {
                        int dy = ny - y, row = ny * w;
                        for (int nx = x0; nx <= x1; nx++) {
                            int dx = nx - x;
                            if (dx * dx + dy * dy <= r2 && mask[row + nx] != 0) { hit = 1; break; }
                        }
                    }
                    dst[y * w + x] = hit;
                }
            }
            return dst;
        }

        internal static float[] BuildSoftBlendAlpha(byte[] evalMask, byte[] outerMask, int w, int h, int featherRing) {
            int n = w * h;
            float[] alpha = new float[n];
            if (featherRing <= 0) {
                for (int i = 0; i < n; i++) alpha[i] = evalMask[i] != 0 ? 1f : 0f;
                return alpha;
            }
            float inf = 1e6f, diag = 1.41421356f;
            float[] dist = new float[n];
            for (int i = 0; i < n; i++) dist[i] = evalMask[i] != 0 ? 0f : inf;

            for (int y = 0; y < h; y++) {
                int row = y * w;
                for (int x = 0; x < w; x++) {
                    int p = row + x;
                    if (dist[p] == 0f) continue;
                    float best = dist[p];
                    if (x > 0 && dist[p - 1] + 1f < best) best = dist[p - 1] + 1f;
                    if (y > 0 && dist[p - w] + 1f < best) best = dist[p - w] + 1f;
                    if (x > 0 && y > 0 && dist[p - w - 1] + diag < best) best = dist[p - w - 1] + diag;
                    if (x + 1 < w && y > 0 && dist[p - w + 1] + diag < best) best = dist[p - w + 1] + diag;
                    dist[p] = best;
                }
            }
            for (int y = h - 1; y >= 0; y--) {
                int row = y * w;
                for (int x = w - 1; x >= 0; x--) {
                    int p = row + x;
                    if (dist[p] == 0f) continue;
                    float best = dist[p];
                    if (x + 1 < w && dist[p + 1] + 1f < best) best = dist[p + 1] + 1f;
                    if (y + 1 < h && dist[p + w] + 1f < best) best = dist[p + w] + 1f;
                    if (x + 1 < w && y + 1 < h && dist[p + w + 1] + diag < best) best = dist[p + w + 1] + diag;
                    if (x > 0 && y + 1 < h && dist[p + w - 1] + diag < best) best = dist[p + w - 1] + diag;
                    dist[p] = best;
                }
            }
            float span = featherRing + 0.5f;
            for (int i = 0; i < n; i++) {
                if (evalMask[i] != 0) alpha[i] = 1f;
                else if (outerMask[i] != 0 && dist[i] < span) alpha[i] = (float)(0.5 * (1.0 + Math.Cos(Math.PI * (dist[i] / span))));
                else alpha[i] = 0f;
            }
            return alpha;
        }

        internal static byte[] RunHybridRgb(byte[] srcRgb, int w, int h, byte[] rawMask, int dilateRadius) {
            byte[] mask = DilateMask(rawMask, w, h, dilateRadius);
            byte[] outRgb = new byte[srcRgb.Length];
            Buffer.BlockCopy(srcRgb, 0, outRgb, 0, srcRgb.Length);
            bool anyHole = false;
            for (int i = 0; i < mask.Length; i++) if (mask[i] != 0) { anyHole = true; break; }
            if (!anyHole) return outRgb;

            List<IntRect> rois = FindHoleRois(mask, w, h, 20);
            for (int rIdx = 0; rIdx < rois.Count; rIdx++) {
                IntRect holeBox = rois[rIdx];
                int estThick = Math.Min(holeBox.W, holeBox.H);
                int pad = Math.Max(26, Math.Min(56, estThick / 2 + 24));
                int rx0 = Math.Max(0, holeBox.X - pad), ry0 = Math.Max(0, holeBox.Y - pad);
                int rx1 = Math.Min(w, holeBox.X + holeBox.W + pad), ry1 = Math.Min(h, holeBox.Y + holeBox.H + pad);
                int rw = rx1 - rx0, rh = ry1 - ry0;

                byte[] roiRgb = ExtractRgb(outRgb, w, rx0, ry0, rw, rh);
                byte[] roiMask = ExtractMask(mask, w, rx0, ry0, rw, rh);

                byte[] teleaGuide = new byte[roiRgb.Length];
                Buffer.BlockCopy(roiRgb, 0, teleaGuide, 0, roiRgb.Length);
                float maxDist;
                float[] distField = RunTeleaRoi(teleaGuide, rw, rh, roiMask, 5, out maxDist);
                Buffer.BlockCopy(teleaGuide, 0, roiRgb, 0, roiRgb.Length);

                double ringMeanGrad, ringP90Grad;
                MeasureSurroundingTexture(roiRgb, rw, rh, roiMask, out ringMeanGrad, out ringP90Grad);

                bool needsTexture = (maxDist > 2.0f && (ringMeanGrad >= 4.2 || ringP90Grad >= 11.0));
                if (needsTexture) {
                    RunMultiScalePatchMatch(roiRgb, rw, rh, roiMask, teleaGuide, maxDist, 20261127 + rIdx * 97);
                    for (int i = 0; i < roiMask.Length; i++) {
                        if (roiMask[i] == 0) continue;
                        float d = distField[i];
                        if (d < 1.6f) {
                            float a = Math.Max(0f, Math.Min(1f, (d - 0.3f) / 1.3f));
                            int p3 = i * 3;
                            roiRgb[p3] = (byte)Clamp255((int)(teleaGuide[p3] * (1f - a) + roiRgb[p3] * a + 0.5f));
                            roiRgb[p3 + 1] = (byte)Clamp255((int)(teleaGuide[p3 + 1] * (1f - a) + roiRgb[p3 + 1] * a + 0.5f));
                            roiRgb[p3 + 2] = (byte)Clamp255((int)(teleaGuide[p3 + 2] * (1f - a) + roiRgb[p3 + 2] * a + 0.5f));
                        }
                    }
                }
                PasteMaskedRgb(outRgb, w, roiRgb, roiMask, rx0, ry0, rw, rh);
            }
            return outRgb;
        }

        internal struct IntRect {
            public int X, Y, W, H;
            public IntRect(int x, int y, int w, int h) { X = x; Y = y; W = w; H = h; }
        }

        static List<IntRect> FindHoleRois(byte[] mask, int w, int h, int mergeGap) {
            byte[] visited = new byte[w * h];
            int[] queue = new int[w * h];
            var boxes = new List<IntRect>();
            for (int y = 0; y < h; y++) {
                int row = y * w;
                for (int x = 0; x < w; x++) {
                    int start = row + x;
                    if (mask[start] == 0 || visited[start] != 0) continue;
                    int head = 0, tail = 0;
                    queue[tail++] = start;
                    visited[start] = 1;
                    int minX = x, maxX = x, minY = y, maxY = y;
                    while (head < tail) {
                        int cur = queue[head++];
                        int cy = cur / w, cx = cur - cy * w;
                        if (cx < minX) minX = cx;
                        if (cx > maxX) maxX = cx;
                        if (cy < minY) minY = cy;
                        if (cy > maxY) maxY = cy;
                        if (cx > 0 && mask[cur - 1] != 0 && visited[cur - 1] == 0) { visited[cur - 1] = 1; queue[tail++] = cur - 1; }
                        if (cx + 1 < w && mask[cur + 1] != 0 && visited[cur + 1] == 0) { visited[cur + 1] = 1; queue[tail++] = cur + 1; }
                        if (cy > 0 && mask[cur - w] != 0 && visited[cur - w] == 0) { visited[cur - w] = 1; queue[tail++] = cur - w; }
                        if (cy + 1 < h && mask[cur + w] != 0 && visited[cur + w] == 0) { visited[cur + w] = 1; queue[tail++] = cur + w; }
                    }
                    boxes.Add(new IntRect(minX, minY, maxX - minX + 1, maxY - minY + 1));
                }
            }
            bool merged = true;
            while (merged && boxes.Count > 1) {
                merged = false;
                for (int i = 0; i < boxes.Count && !merged; i++) {
                    for (int j = i + 1; j < boxes.Count; j++) {
                        IntRect a = boxes[i], b = boxes[j];
                        if (a.X - mergeGap <= b.X + b.W && a.X + a.W + mergeGap >= b.X &&
                            a.Y - mergeGap <= b.Y + b.H && a.Y + a.H + mergeGap >= b.Y) {
                            int nx0 = Math.Min(a.X, b.X), ny0 = Math.Min(a.Y, b.Y);
                            int nx1 = Math.Max(a.X + a.W, b.X + b.W), ny1 = Math.Max(a.Y + a.H, b.Y + b.H);
                            boxes[i] = new IntRect(nx0, ny0, nx1 - nx0, ny1 - ny0);
                            boxes.RemoveAt(j);
                            merged = true;
                            break;
                        }
                    }
                }
            }
            return boxes;
        }

        static byte[] ExtractRgb(byte[] src, int w, int rx0, int ry0, int rw, int rh) {
            byte[] dst = new byte[rw * rh * 3];
            int rowBytes = rw * 3;
            for (int y = 0; y < rh; y++) Buffer.BlockCopy(src, ((ry0 + y) * w + rx0) * 3, dst, y * rowBytes, rowBytes);
            return dst;
        }

        static byte[] ExtractMask(byte[] mask, int w, int rx0, int ry0, int rw, int rh) {
            byte[] dst = new byte[rw * rh];
            for (int y = 0; y < rh; y++) Buffer.BlockCopy(mask, (ry0 + y) * w + rx0, dst, y * rw, rw);
            return dst;
        }

        static void PasteMaskedRgb(byte[] dst, int w, byte[] roiRgb, byte[] roiMask, int rx0, int ry0, int rw, int rh) {
            for (int y = 0; y < rh; y++) {
                int srcRow = y * rw, dstRow = (ry0 + y) * w + rx0;
                for (int x = 0; x < rw; x++) {
                    if (roiMask[srcRow + x] != 0) {
                        int s3 = (srcRow + x) * 3, d3 = (dstRow + x) * 3;
                        dst[d3] = roiRgb[s3]; dst[d3 + 1] = roiRgb[s3 + 1]; dst[d3 + 2] = roiRgb[s3 + 2];
                    }
                }
            }
        }

        sealed class MinHeap {
            int[] idx;
            float[] key;
            int count;
            public MinHeap(int capacity) { idx = new int[Math.Max(16, capacity)]; key = new float[Math.Max(16, capacity)]; }
            public int Count { get { return count; } }
            public void Push(int pixelIndex, float dist) {
                if (count == idx.Length) { Array.Resize(ref idx, idx.Length * 2); Array.Resize(ref key, key.Length * 2); }
                int cur = count++;
                while (cur > 0) {
                    int parent = (cur - 1) >> 1;
                    if (key[parent] <= dist) break;
                    idx[cur] = idx[parent]; key[cur] = key[parent]; cur = parent;
                }
                idx[cur] = pixelIndex; key[cur] = dist;
            }
            public int Pop(out float dist) {
                int topIdx = idx[0]; dist = key[0]; count--;
                if (count > 0) {
                    int lastIdx = idx[count]; float lastKey = key[count];
                    int cur = 0;
                    while (true) {
                        int left = (cur << 1) + 1;
                        if (left >= count) break;
                        int right = left + 1;
                        int best = (right < count && key[right] < key[left]) ? right : left;
                        if (lastKey <= key[best]) break;
                        idx[cur] = idx[best]; key[cur] = key[best]; cur = best;
                    }
                    idx[cur] = lastIdx; key[cur] = lastKey;
                }
                return topIdx;
            }
        }

        static float[] RunTeleaRoi(byte[] rgb, int w, int h, byte[] mask, int radius, out float maxDist) {
            int n = w * h;
            byte[] state = new byte[n];
            float[] dist = new float[n];
            var heap = new MinHeap(n / 4 + 32);
            for (int i = 0; i < n; i++) {
                if (mask[i] == 0) { state[i] = Known; dist[i] = 0f; }
                else { state[i] = Inside; dist[i] = Inf; }
            }
            for (int y = 0; y < h; y++) {
                int row = y * w;
                for (int x = 0; x < w; x++) {
                    int p = row + x;
                    if (state[p] != Inside) continue;
                    if ((x > 0 && state[p - 1] == Known) || (x + 1 < w && state[p + 1] == Known) ||
                        (y > 0 && state[p - w] == Known) || (y + 1 < h && state[p + w] == Known)) {
                        state[p] = Band; dist[p] = 1f; heap.Push(p, 1f);
                    }
                }
            }
            maxDist = 0f;
            int r2Max = radius * radius;
            int[] dx4 = new int[] { -1, 1, 0, 0 }, dy4 = new int[] { 0, 0, -1, 1 };

            while (heap.Count > 0) {
                float poppedDist;
                int p = heap.Pop(out poppedDist);
                if (state[p] == Known) continue;
                state[p] = Known;
                if (dist[p] > maxDist && dist[p] < Inf * 0.5f) maxDist = dist[p];

                int py = p / w, px = p - py * w;
                float txL = (px > 0 && state[p - 1] != Inside) ? dist[p - 1] : dist[p];
                float txR = (px + 1 < w && state[p + 1] != Inside) ? dist[p + 1] : dist[p];
                float tyU = (py > 0 && state[p - w] != Inside) ? dist[p - w] : dist[p];
                float tyD = (py + 1 < h && state[p + w] != Inside) ? dist[p + w] : dist[p];
                float nx = (txR - txL) * 0.5f, ny = (tyD - tyU) * 0.5f;
                float nLen = (float)Math.Sqrt(nx * nx + ny * ny);
                if (nLen > 1e-5f) { nx /= nLen; ny /= nLen; } else { nx = 0f; ny = 0f; }

                double sumR = 0, sumG = 0, sumB = 0, sumW = 0;
                int y0 = Math.Max(0, py - radius), y1 = Math.Min(h - 1, py + radius);
                int x0 = Math.Max(0, px - radius), x1 = Math.Min(w - 1, px + radius);
                float tp = dist[p];

                for (int qy = y0; qy <= y1; qy++) {
                    int ry = py - qy, qRow = qy * w;
                    for (int qx = x0; qx <= x1; qx++) {
                        int rx = px - qx;
                        int d2 = rx * rx + ry * ry;
                        if (d2 == 0 || d2 > r2Max) continue;
                        int q = qRow + qx;
                        if (state[q] != Known) continue;

                        float d = (float)Math.Sqrt(d2);
                        float dir = (nx == 0f && ny == 0f) ? 1f : Math.Max(0.06f, Math.Abs((rx * nx + ry * ny) / d));
                        double wgt = dir * (1f / (d2 * d)) * (1f / (1f + Math.Abs(tp - dist[q])));

                        int q3 = q * 3;
                        float cr = rgb[q3], cg = rgb[q3 + 1], cb = rgb[q3 + 2];
                        if (mask[q] == 0 && qx > 0 && qx + 1 < w && qy > 0 && qy + 1 < h &&
                            mask[q - 1] == 0 && mask[q + 1] == 0 && mask[q - w] == 0 && mask[q + w] == 0) {
                            int ql = (q - 1) * 3, qr = (q + 1) * 3, qu = (q - w) * 3, qd = (q + w) * 3;
                            cr += ClampFloat((rgb[qr] - rgb[ql]) * 0.5f * rx + (rgb[qd] - rgb[qu]) * 0.5f * ry, -18f, 18f);
                            cg += ClampFloat((rgb[qr + 1] - rgb[ql + 1]) * 0.5f * rx + (rgb[qd + 1] - rgb[qu + 1]) * 0.5f * ry, -18f, 18f);
                            cb += ClampFloat((rgb[qr + 2] - rgb[ql + 2]) * 0.5f * rx + (rgb[qd + 2] - rgb[qu + 2]) * 0.5f * ry, -18f, 18f);
                        }
                        sumR += wgt * cr; sumG += wgt * cg; sumB += wgt * cb; sumW += wgt;
                    }
                }
                if (sumW > 0) {
                    int p3 = p * 3;
                    rgb[p3] = (byte)Clamp255((int)(sumR / sumW + 0.5));
                    rgb[p3 + 1] = (byte)Clamp255((int)(sumG / sumW + 0.5));
                    rgb[p3 + 2] = (byte)Clamp255((int)(sumB / sumW + 0.5));
                }
                for (int k = 0; k < 4; k++) {
                    int nxPos = px + dx4[k], nyPos = py + dy4[k];
                    if (nxPos < 0 || nxPos >= w || nyPos < 0 || nyPos >= h) continue;
                    int nb = nyPos * w + nxPos;
                    if (state[nb] == Known) continue;
                    float solved = SolveEikonal(nxPos, nyPos, w, h, state, dist);
                    if (solved < dist[nb] || state[nb] == Inside) {
                        dist[nb] = solved; state[nb] = Band; heap.Push(nb, solved);
                    }
                }
            }
            return dist;
        }

        static float SolveEikonal(int x, int y, int w, int h, byte[] state, float[] dist) {
            int p = y * w + x;
            float tx = Inf, ty = Inf;
            if (x > 0 && state[p - 1] == Known && dist[p - 1] < tx) tx = dist[p - 1];
            if (x + 1 < w && state[p + 1] == Known && dist[p + 1] < tx) tx = dist[p + 1];
            if (y > 0 && state[p - w] == Known && dist[p - w] < ty) ty = dist[p - w];
            if (y + 1 < h && state[p + w] == Known && dist[p + w] < ty) ty = dist[p + w];
            if (tx >= Inf * 0.5f && ty >= Inf * 0.5f) return 1f;
            float diff = Math.Abs(tx - ty);
            return diff < 1f ? 0.5f * (tx + ty + (float)Math.Sqrt(2f - diff * diff)) : Math.Min(tx, ty) + 1f;
        }

        static void MeasureSurroundingTexture(byte[] rgb, int w, int h, byte[] mask, out double meanGrad, out double p90Grad) {
            byte[] dilated = DilateMask(mask, w, h, 10);
            var grads = new List<float>(1024);
            double sum = 0;
            for (int y = 1; y + 1 < h; y++) {
                int row = y * w;
                for (int x = 1; x + 1 < w; x++) {
                    int p = row + x;
                    if (dilated[p] == 0 || mask[p] != 0) continue;
                    if (mask[p - 1] != 0 || mask[p + 1] != 0 || mask[p - w] != 0 || mask[p + w] != 0) continue;
                    float lL = Luma(rgb, p - 1), lR = Luma(rgb, p + 1), lU = Luma(rgb, p - w), lD = Luma(rgb, p + w);
                    float g = (float)Math.Sqrt((lR - lL) * (lR - lL) * 0.25f + (lD - lU) * (lD - lU) * 0.25f);
                    grads.Add(g); sum += g;
                }
            }
            if (grads.Count == 0) { meanGrad = 0; p90Grad = 0; return; }
            grads.Sort();
            meanGrad = sum / grads.Count;
            p90Grad = grads[(int)(grads.Count * 0.90)];
        }

        static float Luma(byte[] rgb, int idx) {
            int p3 = idx * 3;
            return 0.299f * rgb[p3] + 0.587f * rgb[p3 + 1] + 0.114f * rgb[p3 + 2];
        }

        sealed class Level {
            public int W, H;
            public byte[] WorkRgb, OrigRgb, GuideRgb, Mask;
        }

        static void RunMultiScalePatchMatch(byte[] roiRgb, int rw, int rh, byte[] roiMask, byte[] teleaGuide, float maxHoleRadius, int seed) {
            var levels = new List<Level>();
            var fine = new Level { W = rw, H = rh, WorkRgb = roiRgb, OrigRgb = new byte[roiRgb.Length], GuideRgb = teleaGuide, Mask = roiMask };
            Buffer.BlockCopy(roiRgb, 0, fine.OrigRgb, 0, roiRgb.Length);
            levels.Add(fine);

            float r = maxHoleRadius;
            int cw = rw, ch = rh;
            while (r > 4.5f && cw >= 48 && ch >= 48 && levels.Count < 4) {
                Level prev = levels[levels.Count - 1];
                int nw = Math.Max(24, cw / 2), nh = Math.Max(24, ch / 2);
                levels.Add(new Level {
                    W = nw, H = nh,
                    WorkRgb = DownsampleRgb(prev.WorkRgb, prev.W, prev.H, nw, nh),
                    OrigRgb = DownsampleRgb(prev.OrigRgb, prev.W, prev.H, nw, nh),
                    GuideRgb = prev.GuideRgb != null ? DownsampleRgb(prev.GuideRgb, prev.W, prev.H, nw, nh) : null,
                    Mask = DownsampleMask(prev.Mask, prev.W, prev.H, nw, nh)
                });
                cw = nw; ch = nh; r *= 0.5f;
            }

            var rng = new Random(seed);
            int[] prevNnfX = null, prevNnfY = null;
            int prevW = 0, prevH = 0;

            for (int lv = levels.Count - 1; lv >= 0; lv--) {
                Level cur = levels[lv];
                int patchHalf = 2;
                byte[] validSource = BuildValidSourceMap(cur.Mask, cur.W, cur.H, patchHalf);
                List<int> sourceList = CollectValidIndices(validSource);
                if (sourceList.Count == 0) {
                    patchHalf = 1;
                    validSource = BuildValidSourceMap(cur.Mask, cur.W, cur.H, patchHalf);
                    sourceList = CollectValidIndices(validSource);
                    if (sourceList.Count == 0) continue;
                }

                byte[] targetPatchMask = DilateMask(cur.Mask, cur.W, cur.H, patchHalf);
                int n = cur.W * cur.H;
                int[] nnfX = new int[n], nnfY = new int[n];
                float[] nnfCost = new float[n];

                for (int y = 0; y < cur.H; y++) {
                    for (int x = 0; x < cur.W; x++) {
                        int p = y * cur.W + x;
                        if (validSource[p] != 0 && targetPatchMask[p] == 0) { nnfX[p] = x; nnfY[p] = y; continue; }
                        int sx = -1, sy = -1;
                        if (prevNnfX != null) {
                            int px = Math.Min(prevW - 1, x * prevW / cur.W);
                            int py = Math.Min(prevH - 1, y * prevH / cur.H);
                            int candX = Math.Min(cur.W - 1 - patchHalf, Math.Max(patchHalf, prevNnfX[py * prevW + px] * cur.W / prevW));
                            int candY = Math.Min(cur.H - 1 - patchHalf, Math.Max(patchHalf, prevNnfY[py * prevW + px] * cur.H / prevH));
                            if (validSource[candY * cur.W + candX] != 0) { sx = candX; sy = candY; }
                        }
                        if (sx < 0) {
                            int pick = sourceList[rng.Next(sourceList.Count)];
                            sy = pick / cur.W; sx = pick - sy * cur.W;
                        }
                        nnfX[p] = sx; nnfY[p] = sy;
                    }
                }

                if (lv < levels.Count - 1) {
                    Level coarser = levels[lv + 1];
                    byte[] upsampled = UpsampleRgbBilinear(coarser.WorkRgb, coarser.W, coarser.H, cur.W, cur.H);
                    for (int i = 0; i < n; i++) {
                        if (cur.Mask[i] != 0) {
                            int i3 = i * 3;
                            cur.WorkRgb[i3] = upsampled[i3];
                            cur.WorkRgb[i3 + 1] = upsampled[i3 + 1];
                            cur.WorkRgb[i3 + 2] = upsampled[i3 + 2];
                        }
                    }
                }

                int emSteps = (lv == 0) ? 1 : 2;
                float guideWeight = (cur.GuideRgb != null) ? (lv == levels.Count - 1 ? 0.16f : 0.05f) : 0f;

                for (int em = 0; em < emSteps; em++) {
                    byte[] gradX, gradY;
                    ComputeLumaGradients(cur.WorkRgb, cur.W, cur.H, out gradX, out gradY);

                    for (int y = patchHalf; y < cur.H - patchHalf; y++)
                        for (int x = patchHalf; x < cur.W - patchHalf; x++) {
                            int p = y * cur.W + x;
                            if (targetPatchMask[p] != 0)
                                nnfCost[p] = PatchDistance(cur, gradX, gradY, x, y, nnfX[p], nnfY[p], patchHalf, guideWeight, FloatMax);
                        }

                    int pmIters = (lv == 0) ? 2 : 3;
                    for (int iter = 0; iter < pmIters; iter++) {
                        bool reverse = (iter & 1) == 1;
                        int yStart = reverse ? cur.H - 1 - patchHalf : patchHalf;
                        int yEnd = reverse ? patchHalf - 1 : cur.H - patchHalf;
                        int yStep = reverse ? -1 : 1;
                        int xStart = reverse ? cur.W - 1 - patchHalf : patchHalf;
                        int xEnd = reverse ? patchHalf - 1 : cur.W - patchHalf;
                        int xStep = reverse ? -1 : 1;

                        for (int y = yStart; y != yEnd; y += yStep) {
                            int row = y * cur.W;
                            for (int x = xStart; x != xEnd; x += xStep) {
                                int p = row + x;
                                if (targetPatchMask[p] == 0) continue;
                                float best = nnfCost[p];
                                int bx = nnfX[p], by = nnfY[p];

                                int nxH = x - xStep;
                                if (nxH >= patchHalf && nxH < cur.W - patchHalf) {
                                    int nb = row + nxH, cx = nnfX[nb] + xStep, cy = nnfY[nb];
                                    if (cx >= patchHalf && cx < cur.W - patchHalf && validSource[cy * cur.W + cx] != 0) {
                                        float c = PatchDistance(cur, gradX, gradY, x, y, cx, cy, patchHalf, guideWeight, best);
                                        if (c < best) { best = c; bx = cx; by = cy; }
                                    }
                                }
                                int nyV = y - yStep;
                                if (nyV >= patchHalf && nyV < cur.H - patchHalf) {
                                    int nb = nyV * cur.W + x, cx = nnfX[nb], cy = nnfY[nb] + yStep;
                                    if (cy >= patchHalf && cy < cur.H - patchHalf && validSource[cy * cur.W + cx] != 0) {
                                        float c = PatchDistance(cur, gradX, gradY, x, y, cx, cy, patchHalf, guideWeight, best);
                                        if (c < best) { best = c; bx = cx; by = cy; }
                                    }
                                }

                                int rad = Math.Max(cur.W, cur.H) / 2;
                                while (rad >= 1) {
                                    int rx = Math.Min(cur.W - 1 - patchHalf, Math.Max(patchHalf, bx + rng.Next(-rad, rad + 1)));
                                    int ry = Math.Min(cur.H - 1 - patchHalf, Math.Max(patchHalf, by + rng.Next(-rad, rad + 1)));
                                    if (validSource[ry * cur.W + rx] != 0) {
                                        float c = PatchDistance(cur, gradX, gradY, x, y, rx, ry, patchHalf, guideWeight, best);
                                        if (c < best) { best = c; bx = rx; by = ry; }
                                    }
                                    rad >>= 1;
                                }
                                nnfX[p] = bx; nnfY[p] = by; nnfCost[p] = best;
                            }
                        }
                    }
                    VotePatches(cur, targetPatchMask, nnfX, nnfY, nnfCost, patchHalf);
                    guideWeight *= 0.5f;
                }
                prevNnfX = nnfX; prevNnfY = nnfY; prevW = cur.W; prevH = cur.H;
            }
        }

        static float PatchDistance(Level cur, byte[] gradX, byte[] gradY, int tx, int ty, int sx, int sy, int half, float guideWeight, float cutoff) {
            int w = cur.W;
            byte[] rgb = cur.WorkRgb;
            int ddx = tx - sx, ddy = ty - sy, area = (2 * half + 1) * (2 * half + 1);
            float sum = (ddx * ddx + ddy * ddy) * 0.015f * area;
            if (guideWeight > 0f && cur.GuideRgb != null) {
                int t3 = (ty * w + tx) * 3, s3 = (sy * w + sx) * 3;
                int gr = cur.GuideRgb[t3] - rgb[s3], gg = cur.GuideRgb[t3 + 1] - rgb[s3 + 1], gb = cur.GuideRgb[t3 + 2] - rgb[s3 + 2];
                sum += (gr * gr + gg * gg + gb * gb) * guideWeight * area;
            }
            if (sum >= cutoff) return sum;
            for (int dy = -half; dy <= half; dy++) {
                int trow = (ty + dy) * w + tx, srow = (sy + dy) * w + sx;
                for (int dx = -half; dx <= half; dx++) {
                    int tp = trow + dx, sp = srow + dx, tp3 = tp * 3, sp3 = sp * 3;
                    int dr = rgb[tp3] - rgb[sp3], dg = rgb[tp3 + 1] - rgb[sp3 + 1], db = rgb[tp3 + 2] - rgb[sp3 + 2];
                    int gx = gradX[tp] - gradX[sp], gy = gradY[tp] - gradY[sp];
                    float pixErr = dr * dr + dg * dg + db * db + 0.6f * (gx * gx + gy * gy);
                    if (cur.Mask[tp] == 0) pixErr *= 2.0f;
                    sum += pixErr;
                }
                if (sum >= cutoff) return sum;
            }
            return sum;
        }

        static void VotePatches(Level cur, byte[] targetPatchMask, int[] nnfX, int[] nnfY, float[] nnfCost, int half) {
            int w = cur.W, h = cur.H, n = w * h;
            var costs = new List<float>(512);
            for (int y = half; y < h - half; y++) {
                int row = y * w;
                for (int x = half; x < w - half; x++) {
                    int p = row + x;
                    if (targetPatchMask[p] != 0 && cur.Mask[p] != 0) costs.Add(nnfCost[p]);
                }
            }
            float sigma2 = 1f;
            if (costs.Count > 0) { costs.Sort(); sigma2 = Math.Max(16f, costs[(int)(costs.Count * 0.75)] * 0.65f); }
            double[] accR = new double[n], accG = new double[n], accB = new double[n], accW = new double[n];
            for (int y = half; y < h - half; y++) {
                int row = y * w;
                for (int x = half; x < w - half; x++) {
                    int p = row + x;
                    if (targetPatchMask[p] == 0) continue;
                    int sx = nnfX[p], sy = nnfY[p];
                    double simW = Math.Max(1e-6, Math.Exp(-nnfCost[p] / (2.0 * sigma2)));
                    for (int dy = -half; dy <= half; dy++) {
                        int trow = (y + dy) * w, srow = (sy + dy) * w;
                        for (int dx = -half; dx <= half; dx++) {
                            int tp = trow + (x + dx);
                            if (cur.Mask[tp] == 0) continue;
                            double wgt = simW / (1.0 + 0.5 * (dx * dx + dy * dy));
                            int sp3 = (srow + (sx + dx)) * 3;
                            accR[tp] += wgt * cur.OrigRgb[sp3];
                            accG[tp] += wgt * cur.OrigRgb[sp3 + 1];
                            accB[tp] += wgt * cur.OrigRgb[sp3 + 2];
                            accW[tp] += wgt;
                        }
                    }
                }
            }
            for (int i = 0; i < n; i++) {
                int i3 = i * 3;
                if (cur.Mask[i] == 0) {
                    cur.WorkRgb[i3] = cur.OrigRgb[i3]; cur.WorkRgb[i3 + 1] = cur.OrigRgb[i3 + 1]; cur.WorkRgb[i3 + 2] = cur.OrigRgb[i3 + 2];
                } else if (accW[i] > 0) {
                    cur.WorkRgb[i3] = (byte)Clamp255((int)(accR[i] / accW[i] + 0.5));
                    cur.WorkRgb[i3 + 1] = (byte)Clamp255((int)(accG[i] / accW[i] + 0.5));
                    cur.WorkRgb[i3 + 2] = (byte)Clamp255((int)(accB[i] / accW[i] + 0.5));
                }
            }
        }

        static byte[] BuildValidSourceMap(byte[] mask, int w, int h, int half) {
            int sw = w + 1;
            int[] sat = new int[(w + 1) * (h + 1)];
            for (int y = 0; y < h; y++) {
                int rowSum = 0, mRow = y * w, sPrev = y * sw, sCur = (y + 1) * sw;
                for (int x = 0; x < w; x++) {
                    if (mask[mRow + x] != 0) rowSum++;
                    sat[sCur + x + 1] = sat[sPrev + x + 1] + rowSum;
                }
            }
            byte[] valid = new byte[w * h];
            for (int y = half; y < h - half; y++) {
                int y0 = y - half, y1 = y + half + 1;
                for (int x = half; x < w - half; x++) {
                    int x0 = x - half, x1 = x + half + 1;
                    int holeSum = sat[y1 * sw + x1] - sat[y0 * sw + x1] - sat[y1 * sw + x0] + sat[y0 * sw + x0];
                    valid[y * w + x] = (byte)(holeSum == 0 ? 1 : 0);
                }
            }
            return valid;
        }

        static List<int> CollectValidIndices(byte[] valid) {
            var list = new List<int>(valid.Length / 2);
            for (int i = 0; i < valid.Length; i++) if (valid[i] != 0) list.Add(i);
            return list;
        }

        static void ComputeLumaGradients(byte[] rgb, int w, int h, out byte[] gx, out byte[] gy) {
            int n = w * h;
            byte[] luma = new byte[n];
            for (int i = 0; i < n; i++) {
                int i3 = i * 3;
                luma[i] = (byte)((77 * rgb[i3] + 150 * rgb[i3 + 1] + 29 * rgb[i3 + 2] + 128) >> 8);
            }
            gx = new byte[n]; gy = new byte[n];
            for (int y = 0; y < h; y++) {
                int row = y * w, yu = Math.Max(0, y - 1) * w, yd = Math.Min(h - 1, y + 1) * w;
                for (int x = 0; x < w; x++) {
                    int xl = Math.Max(0, x - 1), xr = Math.Min(w - 1, x + 1);
                    gx[row + x] = (byte)Clamp255(128 + ((luma[row + xr] - luma[row + xl]) >> 1));
                    gy[row + x] = (byte)Clamp255(128 + ((luma[yd + x] - luma[yu + x]) >> 1));
                }
            }
        }

        internal static byte[] DownsampleRgb(byte[] src, int sw, int sh, int dw, int dh) {
            byte[] dst = new byte[dw * dh * 3];
            for (int y = 0; y < dh; y++) {
                int sy0 = y * sh / dh, sy1 = Math.Max(sy0 + 1, (y + 1) * sh / dh);
                for (int x = 0; x < dw; x++) {
                    int sx0 = x * sw / dw, sx1 = Math.Max(sx0 + 1, (x + 1) * sw / dw);
                    int sr = 0, sg = 0, sb = 0, cnt = 0;
                    for (int sy = sy0; sy < sy1 && sy < sh; sy++)
                        for (int sx = sx0; sx < sx1 && sx < sw; sx++) {
                            int p3 = (sy * sw + sx) * 3;
                            sr += src[p3]; sg += src[p3 + 1]; sb += src[p3 + 2]; cnt++;
                        }
                    int d3 = (y * dw + x) * 3;
                    dst[d3] = (byte)((sr + cnt / 2) / cnt);
                    dst[d3 + 1] = (byte)((sg + cnt / 2) / cnt);
                    dst[d3 + 2] = (byte)((sb + cnt / 2) / cnt);
                }
            }
            return dst;
        }

        internal static byte[] DownsampleMask(byte[] src, int sw, int sh, int dw, int dh) {
            byte[] dst = new byte[dw * dh];
            for (int y = 0; y < dh; y++) {
                int sy0 = y * sh / dh, sy1 = Math.Max(sy0 + 1, (y + 1) * sh / dh);
                for (int x = 0; x < dw; x++) {
                    int sx0 = x * sw / dw, sx1 = Math.Max(sx0 + 1, (x + 1) * sw / dw);
                    byte any = 0;
                    for (int sy = sy0; sy < sy1 && sy < sh && any == 0; sy++)
                        for (int sx = sx0; sx < sx1 && sx < sw; sx++)
                            if (src[sy * sw + sx] != 0) { any = 1; break; }
                    dst[y * dw + x] = any;
                }
            }
            return dst;
        }

        static byte[] UpsampleRgbBilinear(byte[] src, int sw, int sh, int dw, int dh) {
            byte[] dst = new byte[dw * dh * 3];
            for (int y = 0; y < dh; y++) {
                float fy = (y + 0.5f) * sh / dh - 0.5f;
                int y0 = Math.Max(0, Math.Min(sh - 1, (int)Math.Floor(fy))), y1 = Math.Max(0, Math.Min(sh - 1, y0 + 1));
                float wy = Math.Max(0f, Math.Min(1f, fy - y0));
                for (int x = 0; x < dw; x++) {
                    float fx = (x + 0.5f) * sw / dw - 0.5f;
                    int x0 = Math.Max(0, Math.Min(sw - 1, (int)Math.Floor(fx))), x1 = Math.Max(0, Math.Min(sw - 1, x0 + 1));
                    float wx = Math.Max(0f, Math.Min(1f, fx - x0));
                    int p00 = (y0 * sw + x0) * 3, p01 = (y0 * sw + x1) * 3, p10 = (y1 * sw + x0) * 3, p11 = (y1 * sw + x1) * 3;
                    int d3 = (y * dw + x) * 3;
                    for (int c = 0; c < 3; c++) {
                        float top = src[p00 + c] * (1f - wx) + src[p01 + c] * wx;
                        float bot = src[p10 + c] * (1f - wx) + src[p11 + c] * wx;
                        dst[d3 + c] = (byte)Clamp255((int)(top * (1f - wy) + bot * wy + 0.5f));
                    }
                }
            }
            return dst;
        }

        static int Clamp255(int v) { return v < 0 ? 0 : (v > 255 ? 255 : v); }
        static float ClampFloat(float v, float lo, float hi) { return v < lo ? lo : (v > hi ? hi : v); }
    }

    // ---------------- LaMa INT8 ONNX Engine & Child Process Runner ----------------

    internal static class EraseEngine {
        internal const string ChildSwitch = "--erase";
        internal const string ModelFileName = "lama_512_int8.onnx";
        internal const string ExpectedSha256 = "cab19978adc306622fe37ef60d4a52103b99c98141d499c2a2366a7ed1255dbe";
        internal const long ExpectedBytes = 62074990L;
        const int Side = 512;
        const int TileOverlap = 64;

        static string verifiedSignature;
        static bool verifiedOk;
        static readonly object verifyLock = new object();

        internal static string Folder { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "erase"); } }
        internal static string ModelPath { get { return Path.Combine(Folder, ModelFileName); } }
        static string RuntimePath { get { return Path.Combine(UpscaleEngine.Folder, "onnxruntime.dll"); } }

        // If missing or damaged, returns false silently so the AI button stays hidden without any error popup.
        internal static bool Available {
            get {
                try {
                    if (!File.Exists(RuntimePath) || !File.Exists(ModelPath)) return false;
                    var info = new FileInfo(ModelPath);
                    if (info.Length != ExpectedBytes) return false;
                    string sig = info.FullName + "|" + info.Length.ToString(CultureInfo.InvariantCulture) + "|" + info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
                    lock (verifyLock) {
                        if (verifiedSignature == sig) return verifiedOk;
                        using (var sha = SHA256.Create())
                        using (var fs = new FileStream(ModelPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
                            string hash = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
                            verifiedSignature = sig;
                            verifiedOk = (hash == ExpectedSha256);
                            return verifiedOk;
                        }
                    }
                } catch (Exception) {
                    return false;
                }
            }
        }

        // Child process entry: LoveMyShelf.exe --erase <inputPng> <maskPng> <outputPatchPng>
        internal static int RunChild(string inputPath, string maskPath, string outputPatchPath) {
            try {
                if (!Available) return 2;
                int w, h;
                byte[] srcRgb = LoadRgb(inputPath, out w, out h);
                int mw, mh;
                byte[] rawMask = LoadMask(maskPath, out mw, out mh);
                if (w != mw || h != mh || w < 4 || h < 4) return 3;

                int threads = Math.Min(6, Math.Max(1, Environment.ProcessorCount - 2));
                byte[] patchBgra;
                using (var session = new OnnxSession(RuntimePath, ModelPath, threads, "input", new[] { "output" }, gpu: false)) {
                    patchBgra = RunLamaPatch(session, srcRgb, rawMask, w, h);
                }
                if (patchBgra == null) return 4;
                var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, patchBgra, w * 4);
                bitmap.Freeze();
                EraseStore.SaveBgraPng(bitmap, outputPatchPath);
                return 0;
            } catch (Exception ex) {
                try { Console.Error.WriteLine(ex.Message); Console.Error.Flush(); } catch (IOException) { }
                return 1;
            }
        }

        static byte[] LoadRgb(string path, out int w, out int h) {
            using (var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete)) {
                var frame = BitmapFrame.Create(file, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
                var rgb = frame.Format == PixelFormats.Rgb24 ? (BitmapSource)frame : new FormatConvertedBitmap(frame, PixelFormats.Rgb24, null, 0);
                w = rgb.PixelWidth; h = rgb.PixelHeight;
                var pixels = new byte[w * h * 3];
                rgb.CopyPixels(pixels, w * 3, 0);
                return pixels;
            }
        }

        static byte[] LoadMask(string path, out int w, out int h) {
            using (var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete)) {
                var frame = BitmapFrame.Create(file, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
                var bgra = frame.Format == PixelFormats.Bgra32 ? (BitmapSource)frame : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                w = bgra.PixelWidth; h = bgra.PixelHeight;
                var pixels = new byte[w * h * 4];
                bgra.CopyPixels(pixels, w * 4, 0);
                var mask = new byte[w * h];
                for (int i = 0, p = 0; i < mask.Length; i++, p += 4) mask[i] = pixels[p + 1] >= 48 ? (byte)1 : (byte)0;
                return mask;
            }
        }

        internal static byte[] RunLamaPatch(OnnxSession session, byte[] srcRgb, byte[] rawMask, int w, int h) {
            byte[] evalMask = CoverInpaint.DilateMask(rawMask, w, h, 2);
            int featherRing = 3;
            byte[] lamaMask = CoverInpaint.DilateMask(evalMask, w, h, featherRing);
            byte[] guardMask = CoverInpaint.DilateMask(lamaMask, w, h, 2);
            float[] featherAlpha = CoverInpaint.BuildSoftBlendAlpha(evalMask, lamaMask, w, h, featherRing);

            byte[] predFull = new byte[srcRgb.Length];
            Buffer.BlockCopy(srcRgb, 0, predFull, 0, srcRgb.Length);

            bool any = false;
            for (int i = 0; i < evalMask.Length; i++) if (evalMask[i] != 0) { any = true; break; }
            if (!any) return new byte[w * h * 4];

            List<CoverInpaint.IntRect> rois = FindMergedRoisFor512(lamaMask, w, h);
            for (int rIdx = 0; rIdx < rois.Count; rIdx++) {
                CoverInpaint.IntRect hole = rois[rIdx];
                int longSide = Math.Max(hole.W, hole.H);
                int shortSide = Math.Max(1, Math.Min(hole.W, hole.H));
                double ratio = (double)longSide / shortSide;

                if (longSide > 448 && ratio > 2.2) {
                    // Long thin mask (> 2.2:1 and long side > 448px): split into 64px-overlapping 512x512 tiles!
                    InpaintTiledThinMask(session, srcRgb, predFull, lamaMask, w, h, hole);
                    continue;
                }

                int minMargin = Math.Max(64, (int)Math.Ceiling(longSide * 0.35));
                int targetSide = Math.Max(Side, longSide + 2 * minMargin);
                int cx = hole.X + hole.W / 2, cy = hole.Y + hole.H / 2;

                if (targetSide <= Side) {
                    int x0 = ClampWindowStart(cx - Side / 2, w, Side);
                    int y0 = ClampWindowStart(cy - Side / 2, h, Side);
                    byte[] patchRgb, patchMask;
                    ExtractReflectedWindow(srcRgb, lamaMask, w, h, x0, y0, Side, Side, out patchRgb, out patchMask);
                    byte[] pred512 = Infer512(session, patchRgb, patchMask);
                    if (pred512 == null) return null;
                    CopyMaskedPrediction(predFull, w, h, pred512, Side, Side, x0, y0, guardMask);
                } else {
                    int x0 = ClampWindowStart(cx - targetSide / 2, w, targetSide);
                    int y0 = ClampWindowStart(cy - targetSide / 2, h, targetSide);
                    byte[] boxRgb, boxMask;
                    ExtractReflectedWindow(srcRgb, lamaMask, w, h, x0, y0, targetSide, targetSide, out boxRgb, out boxMask);
                    byte[] rgb512 = CoverInpaint.DownsampleRgb(boxRgb, targetSide, targetSide, Side, Side);
                    byte[] mask512 = CoverInpaint.DownsampleMask(boxMask, targetSide, targetSide, Side, Side);
                    byte[] pred512 = Infer512(session, rgb512, mask512);
                    if (pred512 == null) return null;
                    byte[] predBox = BicubicUpsampleRgb(pred512, Side, Side, targetSide, targetSide);
                    CopyMaskedPrediction(predFull, w, h, predBox, targetSide, targetSide, x0, y0, guardMask);
                }
            }

            byte[] outBgra = new byte[w * h * 4];
            for (int i = 0; i < evalMask.Length; i++) {
                if (guardMask[i] == 0) continue;
                int s3 = i * 3, d4 = i * 4;
                outBgra[d4] = predFull[s3 + 2];
                outBgra[d4 + 1] = predFull[s3 + 1];
                outBgra[d4 + 2] = predFull[s3];
                outBgra[d4 + 3] = (byte)ClampByte(featherAlpha[i] * 255f);
            }
            return outBgra;
        }

        static void InpaintTiledThinMask(OnnxSession session, byte[] srcRgb, byte[] predFull, byte[] lamaMask, int w, int h, CoverInpaint.IntRect hole) {
            int shortSide = Math.Min(hole.W, hole.H);
            if (shortSide <= Side - 2 * TileOverlap) {
                // Native 1:1 resolution overlapping 512x512 tiles (64px overlap -> stride 448px).
                List<int> xs = TileStarts(hole.X - TileOverlap, hole.X + hole.W + TileOverlap, w, Side, TileOverlap);
                List<int> ys = TileStarts(hole.Y - TileOverlap, hole.Y + hole.H + TileOverlap, h, Side, TileOverlap);
                float[] accR = new float[w * h], accG = new float[w * h], accB = new float[w * h], accW = new float[w * h];

                for (int iy = 0; iy < ys.Count; iy++) {
                    for (int ix = 0; ix < xs.Count; ix++) {
                        int x0 = xs[ix], y0 = ys[iy];
                        byte[] patchRgb, patchMask;
                        ExtractReflectedWindow(srcRgb, lamaMask, w, h, x0, y0, Side, Side, out patchRgb, out patchMask);
                        bool hasHole = false;
                        for (int k = 0; k < patchMask.Length; k++) if (patchMask[k] != 0) { hasHole = true; break; }
                        if (!hasHole) continue;
                        byte[] pred512 = Infer512(session, patchRgb, patchMask);
                        if (pred512 == null) continue;

                        bool blendLeft = ix > 0, blendRight = ix + 1 < xs.Count;
                        bool blendTop = iy > 0, blendBottom = iy + 1 < ys.Count;
                        for (int py = 0; py < Side; py++) {
                            int dy = y0 + py;
                            if (dy < 0 || dy >= h) continue;
                            float wy = EdgeWeight(py, Side, TileOverlap, blendTop, blendBottom);
                            for (int px = 0; px < Side; px++) {
                                int dx = x0 + px;
                                if (dx < 0 || dx >= w) continue;
                                float wgt = wy * EdgeWeight(px, Side, TileOverlap, blendLeft, blendRight);
                                int p = dy * w + dx, s3 = (py * Side + px) * 3;
                                accR[p] += pred512[s3] * wgt;
                                accG[p] += pred512[s3 + 1] * wgt;
                                accB[p] += pred512[s3 + 2] * wgt;
                                accW[p] += wgt;
                            }
                        }
                    }
                }
                for (int i = 0; i < accW.Length; i++) {
                    if (accW[i] <= 0f) continue;
                    int i3 = i * 3;
                    predFull[i3] = ClampByte(accR[i] / accW[i]);
                    predFull[i3 + 1] = ClampByte(accG[i] / accW[i]);
                    predFull[i3 + 2] = ClampByte(accB[i] / accW[i]);
                }
            } else {
                // Thick elongated banner: scale proportionally so short side + 128 = 512, tile along long axis with 64px overlap, then upsample.
                double scale = (double)(Side - 2 * TileOverlap) / shortSide;
                int sw = Math.Max(Side, (int)Math.Round(w * scale)), sh = Math.Max(Side, (int)Math.Round(h * scale));
                byte[] smallRgb = CoverInpaint.DownsampleRgb(srcRgb, w, h, sw, sh);
                byte[] smallMask = CoverInpaint.DownsampleMask(lamaMask, w, h, sw, sh);
                byte[] smallPred = new byte[smallRgb.Length];
                Buffer.BlockCopy(smallRgb, 0, smallPred, 0, smallRgb.Length);
                var smallHole = new CoverInpaint.IntRect(
                    (int)Math.Floor(hole.X * scale), (int)Math.Floor(hole.Y * scale),
                    Math.Max(1, (int)Math.Ceiling(hole.W * scale)), Math.Max(1, (int)Math.Ceiling(hole.H * scale)));
                InpaintTiledThinMask(session, smallRgb, smallPred, smallMask, sw, sh, smallHole);
                byte[] up = BicubicUpsampleRgb(smallPred, sw, sh, w, h);
                Buffer.BlockCopy(up, 0, predFull, 0, predFull.Length);
            }
        }

        static List<int> TileStarts(int wantedStart, int wantedEnd, int imgLen, int tile, int overlap) {
            var list = new List<int>();
            if (imgLen <= tile) {
                list.Add(-(tile - imgLen) / 2);
                return list;
            }
            int first = Math.Max(0, Math.Min(imgLen - tile, wantedStart));
            int last = Math.Max(0, Math.Min(imgLen - tile, wantedEnd - tile));
            if (last <= first) { list.Add(first); return list; }
            int stride = Math.Max(1, tile - overlap);
            for (int pos = first; ; pos += stride) {
                if (pos >= last) { list.Add(last); break; }
                list.Add(pos);
            }
            return list;
        }

        static float EdgeWeight(int p, int side, int overlap, bool rampStart, bool rampEnd) {
            if (rampStart && p < overlap) return 0.5f - 0.5f * (float)Math.Cos(Math.PI * (p + 0.5f) / overlap);
            if (rampEnd && p >= side - overlap) return 0.5f - 0.5f * (float)Math.Cos(Math.PI * (side - 1 - p + 0.5f) / overlap);
            return 1f;
        }

        static void ExtractReflectedWindow(byte[] srcRgb, byte[] lamaMask, int w, int h, int x0, int y0, int winW, int winH,
            out byte[] patchRgb, out byte[] patchMask) {
            patchRgb = new byte[winW * winH * 3];
            patchMask = new byte[winW * winH];
            for (int py = 0; py < winH; py++) {
                int sy = y0 + py;
                bool inY = (sy >= 0 && sy < h);
                int rsy = ReflectCoord(sy, h);
                for (int px = 0; px < winW; px++) {
                    int sx = x0 + px;
                    bool inX = (sx >= 0 && sx < w);
                    int rsx = ReflectCoord(sx, w);
                    int s3 = (rsy * w + rsx) * 3, d = py * winW + px, d3 = d * 3;
                    patchRgb[d3] = srcRgb[s3];
                    patchRgb[d3 + 1] = srcRgb[s3 + 1];
                    patchRgb[d3 + 2] = srcRgb[s3 + 2];
                    patchMask[d] = (inX && inY) ? lamaMask[sy * w + sx] : (byte)0;
                }
            }
        }

        static void CopyMaskedPrediction(byte[] predFull, int w, int h, byte[] predWin, int winW, int winH, int x0, int y0, byte[] guardMask) {
            for (int py = 0; py < winH; py++) {
                int dy = y0 + py;
                if (dy < 0 || dy >= h) continue;
                for (int px = 0; px < winW; px++) {
                    int dx = x0 + px;
                    if (dx < 0 || dx >= w) continue;
                    int imgIdx = dy * w + dx;
                    if (guardMask[imgIdx] == 0) continue;
                    int s3 = (py * winW + px) * 3, d3 = imgIdx * 3;
                    predFull[d3] = predWin[s3];
                    predFull[d3 + 1] = predWin[s3 + 1];
                    predFull[d3 + 2] = predWin[s3 + 2];
                }
            }
        }

        static byte[] Infer512(OnnxSession session, byte[] rgb512, byte[] mask512) {
            int plane = Side * Side;
            float[] input = new float[4 * plane];
            for (int i = 0; i < plane; i++) {
                if (mask512[i] != 0) {
                    input[3 * plane + i] = 1f;
                } else {
                    int i3 = i * 3;
                    input[i] = rgb512[i3] * (1f / 255f);
                    input[plane + i] = rgb512[i3 + 1] * (1f / 255f);
                    input[2 * plane + i] = rgb512[i3 + 2] * (1f / 255f);
                }
            }
            float[] raw = session.RunAll(input, new long[] { 1, 4, Side, Side })[0];
            if (raw == null || raw.Length != 3 * plane) return null;
            byte[] dst = new byte[3 * plane];
            for (int i = 0; i < plane; i++) {
                float r = raw[i] * 255f, g = raw[plane + i] * 255f, b = raw[2 * plane + i] * 255f;
                if (Single.IsNaN(r) || Single.IsInfinity(r) || Single.IsNaN(g) || Single.IsInfinity(g) || Single.IsNaN(b) || Single.IsInfinity(b))
                    return null;
                int i3 = i * 3;
                dst[i3] = ClampByte(r);
                dst[i3 + 1] = ClampByte(g);
                dst[i3 + 2] = ClampByte(b);
            }
            return dst;
        }

        static List<CoverInpaint.IntRect> FindMergedRoisFor512(byte[] mask, int w, int h) {
            byte[] visited = new byte[w * h];
            int[] queue = new int[w * h];
            var boxes = new List<CoverInpaint.IntRect>();
            for (int y = 0; y < h; y++) {
                int row = y * w;
                for (int x = 0; x < w; x++) {
                    int start = row + x;
                    if (mask[start] == 0 || visited[start] != 0) continue;
                    int head = 0, tail = 0;
                    queue[tail++] = start;
                    visited[start] = 1;
                    int minX = x, maxX = x, minY = y, maxY = y;
                    while (head < tail) {
                        int cur = queue[head++];
                        int cy = cur / w, cx = cur - cy * w;
                        if (cx < minX) minX = cx;
                        if (cx > maxX) maxX = cx;
                        if (cy < minY) minY = cy;
                        if (cy > maxY) maxY = cy;
                        if (cx > 0 && mask[cur - 1] != 0 && visited[cur - 1] == 0) { visited[cur - 1] = 1; queue[tail++] = cur - 1; }
                        if (cx + 1 < w && mask[cur + 1] != 0 && visited[cur + 1] == 0) { visited[cur + 1] = 1; queue[tail++] = cur + 1; }
                        if (cy > 0 && mask[cur - w] != 0 && visited[cur - w] == 0) { visited[cur - w] = 1; queue[tail++] = cur - w; }
                        if (cy + 1 < h && mask[cur + w] != 0 && visited[cur + w] == 0) { visited[cur + w] = 1; queue[tail++] = cur + w; }
                    }
                    boxes.Add(new CoverInpaint.IntRect(minX, minY, maxX - minX + 1, maxY - minY + 1));
                }
            }
            int maxUnionSide = Side - 128;
            bool merged = true;
            while (merged && boxes.Count > 1) {
                merged = false;
                for (int i = 0; i < boxes.Count && !merged; i++) {
                    for (int j = i + 1; j < boxes.Count; j++) {
                        CoverInpaint.IntRect a = boxes[i], b = boxes[j];
                        int ux0 = Math.Min(a.X, b.X), uy0 = Math.Min(a.Y, b.Y);
                        int ux1 = Math.Max(a.X + a.W, b.X + b.W), uy1 = Math.Max(a.Y + a.H, b.Y + b.H);
                        int uw = ux1 - ux0, uh = uy1 - uy0;
                        bool overlapSoon = (a.X - 32 <= b.X + b.W && a.X + a.W + 32 >= b.X &&
                                            a.Y - 32 <= b.Y + b.H && a.Y + a.H + 32 >= b.Y);
                        if ((uw <= maxUnionSide && uh <= maxUnionSide) || overlapSoon) {
                            boxes[i] = new CoverInpaint.IntRect(ux0, uy0, uw, uh);
                            boxes.RemoveAt(j);
                            merged = true;
                            break;
                        }
                    }
                }
            }
            return boxes;
        }

        static int ClampWindowStart(int idealStart, int imgLen, int winLen) {
            if (imgLen >= winLen) return Math.Max(0, Math.Min(imgLen - winLen, idealStart));
            return -(winLen - imgLen) / 2;
        }

        static int ReflectCoord(int i, int n) {
            if (n <= 1) return 0;
            while (i < 0 || i >= n) {
                if (i < 0) i = -i;
                if (i >= n) i = 2 * n - 2 - i;
            }
            return i;
        }

        static float CubicWeight(float x) {
            x = Math.Abs(x);
            if (x <= 1f) return (1.5f * x - 2.5f) * x * x + 1f;
            if (x < 2f) return ((-0.5f * x + 2.5f) * x - 4f) * x + 2f;
            return 0f;
        }

        static byte[] BicubicUpsampleRgb(byte[] src, int sw, int sh, int dw, int dh) {
            byte[] dst = new byte[dw * dh * 3];
            float scaleX = (float)sw / dw, scaleY = (float)sh / dh;
            for (int dy = 0; dy < dh; dy++) {
                float fy = (dy + 0.5f) * scaleY - 0.5f;
                int iy = (int)Math.Floor(fy);
                float ty = fy - iy;
                float[] wy = new float[] { CubicWeight(ty + 1f), CubicWeight(ty), CubicWeight(1f - ty), CubicWeight(2f - ty) };
                for (int dx = 0; dx < dw; dx++) {
                    float fx = (dx + 0.5f) * scaleX - 0.5f;
                    int ix = (int)Math.Floor(fx);
                    float tx = fx - ix;
                    float[] wx = new float[] { CubicWeight(tx + 1f), CubicWeight(tx), CubicWeight(1f - tx), CubicWeight(2f - tx) };
                    float sr = 0f, sg = 0f, sb = 0f, swgt = 0f;
                    for (int m = -1; m <= 2; m++) {
                        int sy = Math.Max(0, Math.Min(sh - 1, iy + m)), sRow = sy * sw;
                        float wym = wy[m + 1];
                        for (int n = -1; n <= 2; n++) {
                            int sx = Math.Max(0, Math.Min(sw - 1, ix + n));
                            float wgt = wym * wx[n + 1];
                            int s3 = (sRow + sx) * 3;
                            sr += src[s3] * wgt; sg += src[s3 + 1] * wgt; sb += src[s3 + 2] * wgt; swgt += wgt;
                        }
                    }
                    int d3 = (dy * dw + dx) * 3;
                    if (swgt > 0f) {
                        dst[d3] = ClampByte(sr / swgt);
                        dst[d3 + 1] = ClampByte(sg / swgt);
                        dst[d3 + 2] = ClampByte(sb / swgt);
                    }
                }
            }
            return dst;
        }

        static byte ClampByte(float v) {
            int iv = (int)(v + 0.5f);
            return (byte)(iv < 0 ? 0 : (iv > 255 ? 255 : iv));
        }
    }

    // Manages at most one --erase child process for an editor window: BelowNormal priority, 30s timeout,
    // kills any earlier run when a new one starts, and cleans up when the window closes.
    internal sealed class EraseRunner : IDisposable {
        const int TimeoutMs = 30000;
        readonly object sync = new object();
        Process child;
        int runId;
        bool disposed;

        internal bool IsRunning { get { lock (sync) return child != null; } }
        internal event Action StateChanged;

        internal void Cancel() {
            lock (sync) {
                runId++;
                KillCurrentLocked();
            }
            Raise();
        }

        void KillCurrentLocked() {
            var p = child;
            child = null;
            if (p != null) {
                try { if (!p.HasExited) p.Kill(); } catch (Exception) { }
                try { p.Dispose(); } catch (Exception) { }
            }
        }

        void Raise() {
            bool isDisp;
            lock (sync) isDisp = disposed;
            if (!isDisp && StateChanged != null) StateChanged();
        }

        internal async Task<BitmapSource> RunAsync(BitmapSource source, IList<MotionShape> shapes, CancellationToken callerToken = default(CancellationToken)) {
            if (source == null || shapes == null || callerToken.IsCancellationRequested || !EraseEngine.Available) return null;
            int myId;
            lock (sync) {
                if (disposed) return null;
                myId = ++runId;
                KillCurrentLocked();
            }

            int w = source.PixelWidth, h = source.PixelHeight;
            if (w < 4 || h < 4) return null;
            byte[] mask = CoverInpaint.RasterizeMask(shapes, w, h);
            bool any = false;
            for (int i = 0; i < mask.Length; i++) if (mask[i] != 0) { any = true; break; }
            if (!any) return null;

            string workDir = Path.Combine(Path.GetTempPath(), "LoveMyShelf", "erase-work");
            Directory.CreateDirectory(workDir);
            string tag = Guid.NewGuid().ToString("N");
            string inFile = Path.Combine(workDir, tag + "-in.png");
            string maskFile = Path.Combine(workDir, tag + "-mask.png");
            string outFile = Path.Combine(workDir, tag + "-out.png");
            Process proc = null;

            try {
                EraseStore.SaveBgraPng(source, inFile);
                byte[] maskBgra = new byte[w * h * 4];
                for (int i = 0, p = 0; i < mask.Length; i++, p += 4) {
                    byte v = mask[i] != 0 ? (byte)255 : (byte)0;
                    maskBgra[p] = v; maskBgra[p + 1] = v; maskBgra[p + 2] = v; maskBgra[p + 3] = 255;
                }
                var maskBmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, maskBgra, w * 4);
                maskBmp.Freeze();
                EraseStore.SaveBgraPng(maskBmp, maskFile);

                var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                var start = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,
                    EraseEngine.ChildSwitch + " \"" + inFile + "\" \"" + maskFile + "\" \"" + outFile + "\"") {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                proc = new Process { StartInfo = start, EnableRaisingEvents = true };
                proc.OutputDataReceived += delegate { };
                proc.ErrorDataReceived += delegate { };
                proc.Exited += delegate {
                    try { proc.WaitForExit(); } catch (Exception) { }
                    int exit = -1;
                    try { exit = proc.ExitCode; } catch (Exception) { }
                    tcs.TrySetResult(exit);
                };

                bool abortNow = false;
                lock (sync) {
                    if (disposed || myId != runId || callerToken.IsCancellationRequested) {
                        abortNow = true;
                    } else {
                        proc.Start();
                        child = proc;
                    }
                }
                if (abortNow) return null;

                try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception) { }
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                Raise();

                using (var waitCts = CancellationTokenSource.CreateLinkedTokenSource(callerToken)) {
                    Task delayTask = Task.Delay(TimeoutMs, waitCts.Token);
                    Task completed = await Task.WhenAny(tcs.Task, delayTask);
                    if (completed != tcs.Task) {
                        lock (sync) { if (myId == runId) KillCurrentLocked(); }
                        return null;
                    }
                    waitCts.Cancel();
                }

                int code = await tcs.Task;
                lock (sync) {
                    if (myId != runId || disposed) return null;
                    if (child == proc) child = null;
                }
                if (code != 0 || !File.Exists(outFile)) return null;

                using (var fs = File.Open(outFile, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete)) {
                    var frame = BitmapFrame.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    var bgra = frame.Format == PixelFormats.Bgra32 ? (BitmapSource)frame : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                    bgra.Freeze();
                    return bgra;
                }
            } catch (Exception ex) {
                ErrorLog.Quiet(ex);
                return null;
            } finally {
                lock (sync) {
                    if (myId == runId && child == proc) child = null;
                }
                if (proc != null) {
                    try { if (!proc.HasExited) proc.Kill(); } catch (Exception) { }
                    try { proc.Dispose(); } catch (Exception) { }
                }
                DeleteQuiet(inFile);
                DeleteQuiet(maskFile);
                DeleteQuiet(outFile);
                Raise();
            }
        }

        static void DeleteQuiet(string path) {
            try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
        }

        public void Dispose() {
            lock (sync) {
                disposed = true;
                runId++;
                KillCurrentLocked();
            }
        }
    }
}
