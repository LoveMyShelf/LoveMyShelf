using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LoveMyShelf {
    // Changing where the GIF cover is cut, right in the Make GIF cover window (2026-11-27 user: "in the GIF window the
    // size can't be changed, so I have to go back; let me adjust the cutting frame here too").
    // [Change where it's cut] shows the scene now on screen whole, with the same frame as the cover editor (drag
    // corners and edges, wheel, keep the shape, width : height numbers). [Use this] cuts the GIF there from then on.
    // The GIF keeps its own cut in its settings (MotionRequest.HasOwnCrop), so the JPG cover stays as it is.
    internal sealed partial class MotionWindow {
        bool ownCrop;
        Grid cropHost;
        FrameworkElement cropField;

        FrameworkElement CropField() {
            // 2026-11-27 user: "'where it's cut', 'change where it's cut'… just say Crop". One button, no row name.
            var open=UI.Btn(L.P("Crop","자르기"),()=>EditCrop());
            open.Margin=new Thickness(0,4,0,4);open.HorizontalAlignment=HorizontalAlignment.Left;
            open.ToolTip=L.P("Shows the scene whole and lets you move and resize the frame, like when you make a cover. Only the GIF changes; the JPG cover stays as it is.",
                "장면 전체를 보여 주고, 표지 만들 때처럼 테두리를 옮기고 크기를 바꿀 수 있어요. GIF만 바뀌고 JPG 표지는 그대로예요.");
            return open;
        }

        async void EditCrop() {
            if(cropHost!=null)return;
            held=true;player.Hold=true;SetStatus();
            // The scene now on screen, whole: no cut, no turn (the frame below turns it as the cover editor does).
            var whole=new MotionSource {Path=source.Path,Moment=source.Moment,Crop=new Rect(0,0,1,1),Rotation=0,
                Brightness=source.Brightness,Adjust=source.Adjust,RecordFolder=source.RecordFolder,Aspect=1};
            var request=Read();request.FlipX=request.FlipY=false;request.HasOwnAdjust=false;request.Text="";request.Shapes="";
            double at=CurrentSceneSourceMs();
            BitmapSource picture=null;
            try {
                var token=lifetime.Token;
                picture=await Task.Run(()=>MotionMaker.RenderSceneAt(whole,request,at,1600,token),token);
            } catch(Exception ex) {ErrorLog.Quiet(ex);}
            if(picture==null || lifetime.IsCancellationRequested)return;
            picture.Freeze();

            var surface=new CropSurface(picture,source.Crop,source.Rotation,CropSurface.SnapPixels>0,7);
            AutomationProperties.SetHelpText(surface,L.T("Drag to move it, and roll the wheel to fit the size."));
            var bar=new WrapPanel {Margin=new Thickness(0,8,0,0),HorizontalAlignment=HorizontalAlignment.Center};
            var keep=new CheckBox {Content=L.P("🔒 Keep this shape","🔒 비율 고정"),VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,2,12,2),FontSize=13};
            keep.SetResourceReference(Control.ForegroundProperty,"Text");
            keep.Checked+=delegate {surface.LockShape=true;};keep.Unchecked+=delegate {surface.LockShape=false;};
            bar.Children.Add(keep);
            Func<string,TextBox> box=tip=>new TextBox {Width=54,MinHeight=26,VerticalContentAlignment=VerticalAlignment.Center,HorizontalContentAlignment=HorizontalAlignment.Center,ToolTip=tip};
            var across=box(L.P("Width (any number, like 16 or 1080)","가로 (16이나 1080처럼 아무 숫자)"));
            var tall=box(L.P("Height (any number, like 9 or 1920)","세로 (9나 1920처럼 아무 숫자)"));
            AutomationProperties.SetName(across,L.P("Width","가로"));AutomationProperties.SetName(tall,L.P("Height","세로"));
            Action applyShape=()=>{
                double w,h;
                if(Double.TryParse(across.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out w) && Double.TryParse(tall.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out h) && w>0 && h>0)
                    surface.SetAspect(w/h);
            };
            foreach(var t in new[]{across,tall}) {
                t.KeyDown+=(s,e)=>{if(e.Key==Key.Enter){applyShape();surface.Focus();e.Handled=true;}};
                t.LostKeyboardFocus+=delegate {applyShape();};
            }
            Action showShape=()=>{
                var size=surface.PixelSize;
                if(!across.IsKeyboardFocusWithin)across.Text=Math.Round(size.Width).ToString(CultureInfo.CurrentCulture);
                if(!tall.IsKeyboardFocusWithin)tall.Text=Math.Round(size.Height).ToString(CultureInfo.CurrentCulture);
            };
            surface.ShapeChanged+=showShape;surface.Moved+=showShape;showShape();
            var colon=UI.Text(":",13);colon.Margin=new Thickness(4,0,4,0);colon.VerticalAlignment=VerticalAlignment.Center;
            bar.Children.Add(across);bar.Children.Add(colon);bar.Children.Add(tall);
            var whole2=UI.Btn(L.P("No crop","자르지 않기"),()=>surface.WholePicture());whole2.Margin=new Thickness(12,0,0,2);
            var cancel=UI.Btn(L.T("Cancel"),()=>CloseCrop());cancel.Margin=new Thickness(12,0,0,2);
            var use=UI.Btn(L.P("Crop here","이대로 자르기"),()=>{UseCrop(surface);CloseCrop();},true);use.Margin=new Thickness(6,0,0,2);
            bar.Children.Add(whole2);bar.Children.Add(cancel);bar.Children.Add(use);
            var note=UI.Text(L.P("Drag the frame's corners and edges, or roll the wheel. Only the GIF cover changes.",
                "테두리의 모서리와 변을 끌거나 휠을 굴려요. GIF 표지만 바뀌어요."),12,true);
            note.HorizontalAlignment=HorizontalAlignment.Center;note.Margin=new Thickness(0,6,0,0);
            var dock=new DockPanel();
            DockPanel.SetDock(note,Dock.Bottom);dock.Children.Add(note);
            DockPanel.SetDock(bar,Dock.Bottom);dock.Children.Add(bar);
            dock.Children.Add(surface);
            cropHost=new Grid();cropHost.SetResourceReference(Panel.BackgroundProperty,"Paper");cropHost.Children.Add(dock);
            // While the frame is open, Esc closes only the frame and Enter uses it (not the window's Cancel / Make).
            make.IsDefault=false;if(cancelButton!=null)cancelButton.IsCancel=false;
            cropHost.PreviewKeyDown+=(s,e)=>{
                if(e.Key==Key.Escape){CloseCrop();e.Handled=true;}
                else if(e.Key==Key.Enter && !(Keyboard.FocusedElement is TextBox)){UseCrop(surface);CloseCrop();e.Handled=true;}
            };
            Grid.SetColumnSpan(cropHost,3);middle.Children.Add(cropHost);
            surface.Focus();
        }

        void CloseCrop() {
            if(cropHost==null)return;
            middle.Children.Remove(cropHost);cropHost=null;
            make.IsDefault=true;if(cancelButton!=null)cancelButton.IsCancel=true;
        }

        void UseCrop(CropSurface surface) {
            var cut=surface.Fraction;double turn=surface.Rotation,shape=surface.Aspect;
            if(cut==source.Crop && turn==source.Rotation && Math.Abs(shape-source.Aspect)<1e-9)return;
            var old=source.Crop;double oldTurn=source.Rotation;
            source.Crop=cut;source.Rotation=turn;source.Aspect=shape;ownCrop=true;
            // Drawings, mosaics and followed boxes stay on the same spot of the video (upright pictures; a turned
            // cut keeps them where they are on the cover).
            if(oldTurn==0 && turn==0 && cut.Width>0 && cut.Height>0)
                drawLayer.RemapAll(p=>new Point((old.X+p.X*old.Width-cut.X)/cut.Width,(old.Y+p.Y*old.Height-cut.Y)/cut.Height));
            // An AI fill and the quick erase picture were made for the old cut.
            eraseRunner.Cancel();drawLayer.InvalidateAiPatch();staticEraseCacheKey=null;
            if(sourcePixels.Width>0 && sourcePixels.Height>0)coverPixels=MotionMaker.CropPixels(source,sourcePixels);
            try {source.Still=surface.Still(960);} catch(Exception ex) {ErrorLog.Quiet(ex);}
            preview.Source=source.Still;
            // Everything read with the old cut is read again: the preview's scenes, the strip, the made file.
            draft=null;drawingFrames=null;
            cellsKey=null;Regrid(true);earlier.Clear();Layout();Fit(); // no stand-ins with the old cut
            ShowDraft();Changed();
        }

        // A saved GIF cover with its own cut opens with it.
        void TakeSavedCrop(MotionRequest saved) {
            if(saved==null || !saved.HasOwnCrop)return;
            source.Crop=saved.Crop;source.Rotation=saved.Rotation;source.Aspect=saved.Aspect;ownCrop=true;
            source.Still=null; // the JPG cover's picture has the other cut: wait for the GIF's own scenes
        }
    }
}
