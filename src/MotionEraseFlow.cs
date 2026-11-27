using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LoveMyShelf {
    // Erasing on a GIF cover (2026-11-27 user: "nothing stops the screen, I don't know what to do or what 'fill' means").
    // Picking [9 Erase] or starting a stroke stops the preview on the scene shown; the strip picks another scene.
    // After each stroke the background around the painted spots is compared across the part's scenes:
    // still -> the fill made on one scene is used for all; moving -> each scene is filled on its own.
    // Thresholds from the 2026-11-27 debate (Codex): in a ring 4-12 px outside the spots, the largest channel
    // difference to the painted scene, on 8 scenes spread over the part: still only if every scene has mean <= 4 and
    // 95th percentile <= 12 (0..255). Once the person presses [Change], their choice stays for this window.
    internal sealed partial class MotionWindow {
        bool eraseModeChosen;
        char lastDrawTool;

        void SetupEraseFlow() {
            Action check=()=>{
                bool erasing=drawPanel.IsEditing && drawLayer.IsErase;
                if(erasing && lastDrawTool!='x')HoldForErase();
                lastDrawTool=erasing?'x':'\0';
            };
            drawLayer.Updated+=check;
            drawPanel.VisibilityChanged+=check;
            drawLayer.PointerStarted+=delegate {if(drawLayer.IsErase)HoldForErase();};
            drawLayer.EraseStrokesChanged+=delegate {if(!eraseModeChosen)PickEraseMode();};
            drawPanel.EraseModeSwitched+=delegate {eraseModeChosen=true;};
            drawPanel.FillAllRequested+=FillEveryScene;
            drawLayer.ShapeLimit+=delegate {
                trackingStatus=L.P("That's as much as can be painted. Undo a little, or paint with a bigger brush.","더 칠할 수 없어요. 조금 되돌리거나 붓을 키워서 칠해 주세요.");
                SetStatus();
                limitNote.Stop();limitNote.Start();
            };
            limitNote.Tick+=delegate {limitNote.Stop();if(trackWork==null){trackingStatus=null;SetStatus();}};
        }
        // Each scene filled on its own: every scene is filled in the background (low priority, stopped by the next change)
        // when asked: [Fill every scene], Enter or ▶ Play. Never by itself right after a stroke, so the painted spot can be
        // fixed first (2026-11-28 user, DeepSeek).
        internal bool WantsFillEveryScene {get {return drawPanel.IsEditing && drawLayer.IsErase && drawLayer.EraseMode==1 && drawLayer.HasEraseShapes;}}
        void FillEveryScene() {if(WantsFillEveryScene && !lifetime.IsCancellationRequested)RealNow();}
        readonly System.Windows.Threading.DispatcherTimer limitNote=new System.Windows.Threading.DispatcherTimer {Interval=TimeSpan.FromSeconds(4)};


        void HoldForErase() {
            if(showingOwn)SwitchToDrawingPreview();
            if(!held){held=true;player.Hold=true;SetStatus();}
        }

        void PickEraseMode() {
            if(!drawLayer.HasEraseShapes)return;
            bool? still=BackgroundStill();
            if(!still.HasValue)return;
            int mode=still.Value?0:1;
            if(drawLayer.EraseMode!=mode)drawLayer.SetEraseMode(mode,false);
        }

        // Null when there is nothing to compare yet (the preview's scenes are still being read).
        bool? BackgroundStill() {
            var frames=drawingFrames;
            var shown=preview.Source as BitmapSource;
            if(frames==null || frames.Images==null || frames.Images.Length<2 || shown==null)return null;
            int w=shown.PixelWidth,h=shown.PixelHeight;
            if(w<8 || h<8)return null;
            // The spots are drawn on the picture as it shows (turned by flips); the scenes are not, so turn the spots back.
            var spots=drawLayer.Shapes.Where(s=>s.IsErase).Select(s=>s.WithPoints(Array.ConvertAll(s.Points,
                p=>new Point(flipX?1-p.X:p.X,flipY?1-p.Y:p.Y)))).ToList();
            var mask=CoverInpaint.RasterizeMask(spots,w,h);
            var inner=CoverInpaint.DilateMask(mask,w,h,4);
            var outer=CoverInpaint.DilateMask(mask,w,h,12);
            var ring=new List<int>();
            for(int i=0;i<mask.Length;i++)if(outer[i]!=0 && inner[i]==0)ring.Add(i);
            if(ring.Count<20)return null;
            var reference=Pixels(shown);
            int count=frames.Images.Length,samples=Math.Min(8,count);
            var diffs=new int[ring.Count];
            for(int k=0;k<samples;k++) {
                var image=frames.Images[samples==1?0:k*(count-1)/(samples-1)];
                if(image==null || image.PixelWidth!=w || image.PixelHeight!=h)continue;
                var other=Pixels(image);
                long sum=0;
                for(int j=0;j<ring.Count;j++) {
                    int p=ring[j]*4;
                    int d=Math.Max(Math.Abs(reference[p]-other[p]),Math.Max(Math.Abs(reference[p+1]-other[p+1]),Math.Abs(reference[p+2]-other[p+2])));
                    diffs[j]=d;sum+=d;
                }
                if(sum>4L*ring.Count)return false;
                Array.Sort(diffs);
                if(diffs[(int)Math.Floor(0.95*(diffs.Length-1))]>12)return false;
            }
            return true;
        }

        static byte[] Pixels(BitmapSource picture) {
            var bgra=picture.Format==PixelFormats.Bgra32?picture:new FormatConvertedBitmap(picture,PixelFormats.Bgra32,null,0);
            var bytes=new byte[picture.PixelWidth*picture.PixelHeight*4];
            bgra.CopyPixels(bytes,picture.PixelWidth*4,0);
            return bytes;
        }
    }
}
