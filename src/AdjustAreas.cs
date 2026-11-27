using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LoveMyShelf {
    // Adjusting only part of the picture (2026-11-27 user: "brightness or contrast for just a part of the photo").
    // A part is a draw shape: box 'g', ellipse 'o' or painted 'q', with its own five adjustment values and a soft edge.
    // Each part: a mask (the shape, softened by three box blurs) over its bounds B; the picture inside B plus a margin
    // R is adjusted with the same code as the whole-picture adjustment, then blended into B by the mask.
    // C# (JPG covers, previews) and FFmpeg (GIF covers) do exactly the same steps, so the made file matches the preview.
    internal static class AdjustAreas {
        const int Pad = 16;   // room around B so sharpening near its edge sees the same pixels in both paths

        internal static bool Active(MotionShape shape) { return shape.IsArea && !shape.Area.IsIdentity; }

        // How far the soft edge reaches, in pixels: a share of the shape's smaller side (or of the brush).
        internal static double FeatherPixels(MotionShape shape, int width, int height) {
            double basis;
            if (shape.Kind == 'q') basis = MotionDraw.Pixels(shape, height);
            else {
                var a = MotionDraw.At(shape.Points[0], width, height);
                var b = MotionDraw.At(shape.Points[1], width, height);
                basis = Math.Min(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
            }
            return Math.Max(0, shape.Feather) / 100.0 * 0.5 * basis;
        }

        static int BlurRadius(double feather) { return (int)Math.Round(feather / 3.0); }

        // The mask's box: the shape's bounds grown by the soft edge, inside the picture. Empty when nothing shows.
        internal static Int32Rect Bounds(MotionShape shape, int width, int height) {
            if (width < 2 || height < 2) return Int32Rect.Empty;
            double px = MotionDraw.Pixels(shape, height);
            var geometry = MotionDraw.GeometryOf(shape, width, height, px);
            Rect r = shape.Kind == 'q' ? geometry.GetRenderBounds(MotionDraw.Stroke(Brushes.White, px)) : geometry.Bounds;
            if (r.IsEmpty) return Int32Rect.Empty;
            int grow = 3 * BlurRadius(FeatherPixels(shape, width, height)) + 2;
            int x = Math.Max(0, (int)Math.Floor(r.Left) - grow), y = Math.Max(0, (int)Math.Floor(r.Top) - grow);
            int right = Math.Min(width, (int)Math.Ceiling(r.Right) + grow), bottom = Math.Min(height, (int)Math.Ceiling(r.Bottom) + grow);
            if (right - x < 1 || bottom - y < 1) return Int32Rect.Empty;
            return new Int32Rect(x, y, right - x, bottom - y);
        }

        static Int32Rect Padded(Int32Rect b, int width, int height) {
            int x = Math.Max(0, b.X - Pad), y = Math.Max(0, b.Y - Pad);
            int right = Math.Min(width, b.X + b.Width + Pad), bottom = Math.Min(height, b.Y + b.Height + Pad);
            return new Int32Rect(x, y, right - x, bottom - y);
        }

        // 0..255 per pixel of box b: the shape drawn with smooth edges, then softened.
        internal static byte[] Mask(MotionShape shape, int width, int height, Int32Rect b) {
            byte[] mask = new byte[b.Width * b.Height];
            MotionDraw.RunSta(() => {
                double px = MotionDraw.Pixels(shape, height);
                var geometry = MotionDraw.GeometryOf(shape, width, height, px);
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen()) {
                    dc.PushTransform(new TranslateTransform(-b.X, -b.Y));
                    if (shape.Kind == 'q') dc.DrawGeometry(null, MotionDraw.Stroke(Brushes.White, px), geometry);
                    else dc.DrawGeometry(Brushes.White, null, geometry);
                    dc.Pop();
                }
                var target = new RenderTargetBitmap(b.Width, b.Height, 96, 96, PixelFormats.Pbgra32);
                target.Render(visual);
                var pixels = new byte[b.Width * b.Height * 4];
                target.CopyPixels(pixels, b.Width * 4, 0);
                for (int i = 0; i < mask.Length; i++) mask[i] = pixels[i * 4 + 3];
            });
            int radius = BlurRadius(FeatherPixels(shape, width, height));
            if (radius > 0) for (int pass = 0; pass < 3; pass++) BoxBlur(mask, b.Width, b.Height, radius);
            return mask;
        }

        // One box blur pass across and then down, with the edge pixels repeated, rounded to the nearest whole value.
        static void BoxBlur(byte[] data, int w, int h, int r) {
            int span = 2 * r + 1;
            var line = new int[Math.Max(w, h)];
            for (int y = 0; y < h; y++) {
                int row = y * w, sum = 0;
                for (int k = -r; k <= r; k++) sum += data[row + Math.Max(0, Math.Min(w - 1, k))];
                for (int x = 0; x < w; x++) {
                    line[x] = (sum + span / 2) / span;
                    sum += data[row + Math.Min(w - 1, x + r + 1)] - data[row + Math.Max(0, x - r)];
                }
                for (int x = 0; x < w; x++) data[row + x] = (byte)line[x];
            }
            for (int x = 0; x < w; x++) {
                int sum = 0;
                for (int k = -r; k <= r; k++) sum += data[Math.Max(0, Math.Min(h - 1, k)) * w + x];
                for (int y = 0; y < h; y++) {
                    line[y] = (sum + span / 2) / span;
                    sum += data[Math.Min(h - 1, y + r + 1) * w + x] - data[Math.Max(0, y - r) * w + x];
                }
                for (int y = 0; y < h; y++) data[y * w + x] = (byte)line[y];
            }
        }

        // FFmpeg's overlay blend for packed RGB: (dst * (255 - a) + src * a) / 255, rounded the same way.
        static byte Blend(int dst, int src, int a) {
            int v = dst * (255 - a) + src * a;
            return (byte)(((v + 128) * 257) >> 16);
        }

        // C# path: every part, in order, on the picture as it is after the parts before it.
        internal static BitmapSource Apply(BitmapSource picture, IEnumerable<MotionShape> shapes) {
            if (picture == null) return null;
            var parts = shapes.Where(Active).ToList();
            if (parts.Count == 0) return picture;
            int w = picture.PixelWidth, h = picture.PixelHeight;
            if (w < 2 || h < 2) return picture;
            var bgraSource = picture.Format == PixelFormats.Bgra32 ? picture : new FormatConvertedBitmap(picture, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[w * h * 4];
            bgraSource.CopyPixels(pixels, w * 4, 0);
            foreach (var part in parts) {
                var b = Bounds(part, w, h);
                if (b.IsEmpty) continue;
                var mask = Mask(part, w, h, b);
                var r = Padded(b, w, h);
                var rgb = new byte[r.Width * r.Height * 3];
                for (int y = 0; y < r.Height; y++)
                    for (int x = 0; x < r.Width; x++) {
                        int s = ((r.Y + y) * w + r.X + x) * 4, d = (y * r.Width + x) * 3;
                        rgb[d] = pixels[s + 2]; rgb[d + 1] = pixels[s + 1]; rgb[d + 2] = pixels[s];
                    }
                var made = ColorAdjust.ApplyRgb(rgb, r.Width, r.Height, part.Area);
                for (int y = 0; y < b.Height; y++)
                    for (int x = 0; x < b.Width; x++) {
                        int a = mask[y * b.Width + x];
                        if (a == 0) continue;
                        int s = ((b.Y + y) * w + b.X + x) * 4;
                        int m = ((b.Y - r.Y + y) * r.Width + b.X - r.X + x) * 3;
                        pixels[s + 2] = Blend(pixels[s + 2], made[m], a);
                        pixels[s + 1] = Blend(pixels[s + 1], made[m + 1], a);
                        pixels[s] = Blend(pixels[s], made[m + 2], a);
                    }
            }
            var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
            result.Freeze();
            return result;
        }

        // GIF path, step 1: one mask PNG per part (white, the mask in the alpha). Returns (file, box) for each part used.
        internal static List<Tuple<string, Int32Rect, MotionShape>> WriteMasks(IEnumerable<MotionShape> shapes, int width, int height, string folder) {
            var made = new List<Tuple<string, Int32Rect, MotionShape>>();
            int n = 0;
            foreach (var part in shapes.Where(Active)) {
                var b = Bounds(part, width, height);
                if (b.IsEmpty) continue;
                var mask = Mask(part, width, height, b);
                var bgra = new byte[mask.Length * 4];
                for (int i = 0; i < mask.Length; i++) { bgra[i * 4] = bgra[i * 4 + 1] = bgra[i * 4 + 2] = 255; bgra[i * 4 + 3] = mask[i]; }
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, "area" + (++n).ToString(CultureInfo.InvariantCulture) + ".png");
                MotionDraw.RunSta(() => {
                    var bitmap = BitmapSource.Create(b.Width, b.Height, 96, 96, PixelFormats.Bgra32, null, bgra, b.Width * 4);
                    EraseStore.SaveBgraPng(bitmap, path);
                });
                made.Add(Tuple.Create(path, b, part));
            }
            return made;
        }

        // GIF path, step 2: for each part, crop R, adjust (the same filters as the whole-picture adjustment),
        // crop back to B, take the mask as its alpha, and lay it over the picture at B.
        internal static string AppendFfmpeg(StringBuilder graph, string current, List<Tuple<string, Int32Rect, MotionShape>> parts,
            int firstInput, int width, int height) {
            var inv = CultureInfo.InvariantCulture;
            for (int i = 0; i < parts.Count; i++) {
                var b = parts[i].Item2;
                var r = Padded(b, width, height);
                string id = (i + 1).ToString(inv);
                string tone = ColorAdjust.BuildFfmpegFilter(parts[i].Item3.Area, false);
                graph.AppendFormat(inv,
                    ";{0}split[arb{1}][ars{1}];[ars{1}]crop={2}:{3}:{4}:{5}:exact=1,{6},crop={7}:{8}:{9}:{10}:exact=1,format=rgba[ara{1}];" +
                    "[{11}:v]format=rgba,alphaextract[arm{1}];[ara{1}][arm{1}]alphamerge[arc{1}];[arb{1}][arc{1}]overlay={12}:{13}:format=rgb[area{1}]",
                    current, id, r.Width, r.Height, r.X, r.Y, tone, b.Width, b.Height, b.X - r.X, b.Y - r.Y, firstInput + i, b.X, b.Y);
                current = "[area" + id + "]";
            }
            return current;
        }
    }
}
