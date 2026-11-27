using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace LoveMyShelf {
    // G in the large view: the part marked with [ and ] becomes a GIF cover in the background, exactly as the video
    // shows right now (turn, zoom and place, the drawn box, brightness, contrast, saturation, color, sharpness),
    // with the Make GIF cover window's last choices. No window opens: watching goes on, a notice says when it's ready,
    // and Edit GIF cover can change it later.
    // 2026-11-27 user: "키보드 버튼 하나 눌러서 바로 … GIF 바로 뒤에서 만들고 바로 그냥 계속 동영상 창 볼 수 있도록",
    // "밝기나 뭐 확대나 … 그렇게 했으면 그 상태 그대로 그냥 뒤에서 이렇게 만들도록"
    internal sealed partial class MotionWindow {
        // The technical choices Make GIF cover would start with now (its last ones while LoveMyShelf runs, else the
        // saved defaults). Text, drawings, flips and the size limit belong to one GIF and are left out.
        internal static MotionRequest QuickChoices() {
            return new MotionRequest {Fps=lastFps,Height=lastHeight,FpsOriginal=lastFpsOriginal,SizeOriginal=lastSizeOriginal,
                Quality=Math.Max(0,Math.Min(2,lastQuality)),Format=Formats[Math.Max(0,Math.Min(Formats.Length-1,lastFormat))],
                Sound=lastSound,Plays=lastPlays};
        }
    }

    public partial class GalleryWindow {
        // from/to: the part in milliseconds. framing/area: how the view shows the picture (null = upright, whole).
        // look: the brightness and colors on screen.
        internal async void MakeQuickGif(Photo photo,double fromMs,double toMs,ViewFraming framing,DrawnArea area,AdjustValues look,Viewer watcher=null) {
            if(photo==null || photo.IsOutside || toMs<=fromMs)return;
            var token=lifetime.Token;
            try {
                string original=photo.Original;
                if(photo.Linked && !File.Exists(original))original=OriginalLocator.Resolve(store,photo,token);
                if(!MotionMaker.Supports(original))return;
                var target=AddTarget(photo,original);
                double moment=(fromMs+toMs)/2; // the JPG cover is the scene in the middle of the part
                var request=MotionWindow.QuickChoices();
                request.Before=(moment-fromMs)/1000;request.After=(toMs-moment)/1000;
                string directory=settings.CoverDirectory;var place=settings.CoverPlace;
                var adjust=look.Clamped();
                var updated=await Task.Run(()=>{
                    var upright=MotionMaker.PixelSize(original,token);
                    var crop=new Rect(0,0,1,1);double rotation=0;
                    if(area!=null){crop=area.Fraction;rotation=area.Turn;}
                    else if(framing!=null){rotation=framing.Turn;crop=framing.CoverFrame(upright.Width,upright.Height);}
                    // Upright and moved past an edge: only the picture itself goes in.
                    if(Math.Abs(rotation)<0.01){crop.Intersect(new Rect(0,0,1,1));if(crop.IsEmpty || crop.Width<=0 || crop.Height<=0)crop=new Rect(0,0,1,1);}
                    BitmapSource preview;int frame;double? time;
                    if(VideoFrames.Supports(original)) {
                        preview=VideoFrames.AtTime(original,moment,0,Preferences.EditorPreviewSide,token);frame=1;time=moment;
                    } else {
                        using(var file=new AnimationFrames(original,token))frame=Math.Max(0,Math.Min(file.Count-1,file.Times.At(moment)));
                        preview=AnimationFrames.Still(original,frame,Preferences.EditorPreviewSide);time=null;
                    }
                    return store.Reframe(target,preview,crop,frame,rotation,time,0,CoverOutputMode.Native,0,token,directory,
                        adjust.Brightness,place,null,null,adjust,null);
                },token);
                if(token.IsCancellationRequested)return;
                int index=photos.FindIndex(p=>p!=null && p.Folder==target.Folder);
                if(index>=0)UpdatePhoto(index,updated);
                else if(!RefreshHiddenCover(updated) && target.Folder!=photo.Folder)InsertCover(updated);
                MakeMotion(updated,request,watcher);
            } catch(OperationCanceledException) {}
            catch(Exception ex) {
                ErrorLog.Quiet(ex);
                if(!token.IsCancellationRequested)
                    Notify(L.T("Couldn’t make the GIF cover"),photo.Name+Environment.NewLine+ex.Message,null,NoticeKind.Warning);
            }
        }
    }
}
