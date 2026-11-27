using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LoveMyShelf {
    // [Fill again] and fixing only one spot after an AI fill (2026-11-27 user: "when the AI fill looks odd, can it fill again?").
    // The AI (LaMa) always gives the same answer for the same picture and spots, so a new try changes what it is given:
    // 1 = the painted spots a little wider, 2 = the picture mirrored, 3 = turned upside down and mirrored. Then 1 again.
    // (Gemini's idea in the 2026-11-27 debate: a mirrored picture gives a really different fill without a bigger hole.)
    internal static class EraseAgain {
        internal const int Variants = 3;

        internal static int Next(int variant) { return variant % Variants + 1; }

        static bool Mirrors(int variant) { return variant == 2 || variant == 3; }
        static bool Flips(int variant) { return variant == 3; }

        // The painted strokes as the AI should see them for this try.
        internal static List<MotionShape> Shapes(IEnumerable<MotionShape> shapes, int variant, int height) {
            var list = new List<MotionShape>();
            foreach (var shape in shapes) {
                var s = shape;
                if (variant == 1 && s.IsErase) {
                    // About 6 px wider in all (3 px on each side) at this picture's height.
                    int wider = Math.Max(1, (int)Math.Round(6000.0 / Math.Max(1, height)));
                    s = new MotionShape(s.Kind, s.Color, Math.Min(50, s.Thickness + wider), s.FillColor, s.Opacity, s.Block, s.Points, s.Track);
                }
                if (Mirrors(variant) || Flips(variant)) {
                    bool flip = Flips(variant);
                    s = s.WithPoints(Array.ConvertAll(s.Points, p => new Point(1 - p.X, flip ? 1 - p.Y : p.Y)));
                }
                list.Add(s);
            }
            return list;
        }

        // The picture turned for this try; the same call turns the AI's result back.
        internal static BitmapSource Turn(BitmapSource picture, int variant) {
            if (picture == null || !Mirrors(variant)) return picture;
            BitmapSource result = null;
            MotionDraw.RunSta(() => {
                var turned = new TransformedBitmap(picture, new ScaleTransform(-1, Flips(variant) ? -1 : 1));
                var copy = new WriteableBitmap(turned);
                copy.Freeze();
                result = copy;
            });
            return result;
        }

        // The strokes painted after an earlier AI fill (only when everything before them is unchanged), or null.
        internal static List<MotionShape> Added(IList<MotionShape> shapes, EraseAiPatch earlier) {
            if (earlier == null || earlier.Patch == null || String.IsNullOrEmpty(earlier.StrokesSpec)) return null;
            var erase = shapes.Where(s => s.IsErase).ToList();
            for (int k = erase.Count - 1; k >= 1; k--)
                if (EraseStore.CanonicalEraseStrokes(erase.Take(k)) == earlier.StrokesSpec) return erase.Skip(k).ToList();
            return null;
        }

        // The earlier fill with the new spots laid over it.
        internal static BitmapSource Merge(BitmapSource earlier, BitmapSource added) {
            if (earlier == null) return added;
            if (added == null) return earlier;
            return EraseStore.Composite(earlier, added);
        }
    }
}
