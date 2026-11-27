using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LoveMyShelf {
    internal sealed partial class MotionWindow {
        StackPanel trackingControls;
        CheckBox followMotion,followSize;
        Button retrack;
        CancellationTokenSource trackWork;
        MotionShape trackingShape,lastTrackingSelection;
        string trackingStatus;
        double trackFrom=Double.NaN,trackTo,trackStep;
        bool trackFlipX,trackFlipY;
        MotionFrames drawingFrames;
        double[] drawingTimeline;

        FrameworkElement TrackingControls() {
            string label=L.P("Follow motion","움직임 따라가기");
            followMotion=new CheckBox {Content=label,Margin=new Thickness(0,3,0,4)};
            AutomationProperties.SetName(followMotion,label);
            followMotion.Click+=delegate {
                if(drawLayer.SelectedShape==null)return;
                var selected=drawLayer.SelectedShape;
                if(followMotion.IsChecked==true)BeginTracking(followSize.IsChecked==true,false);
                else {
                    CancelTracking();
                    if(selected.Track!=null)drawLayer.ReplaceShape(selected,selected.At(DrawingTime()).WithTrack(null));
                    else {UpdateLostStatus();SetStatus();}
                }
                SyncTrackingControls();
            };
            followSize=new CheckBox {Content=L.P("Follow size too","크기도 따라가기"),Margin=new Thickness(0,3,0,4),
                ToolTip=L.P("If you have moved the shape by hand, this applies the next time you track again.",
                    "도형을 직접 옮긴 뒤에는 다시 따라갈 때 적용돼요.")};
            followSize.Click+=delegate {
                if(drawLayer.SelectedShape==null)return;
                var selected=drawLayer.SelectedShape;
                if(selected.Track==null)return;
                bool size=followSize.IsChecked==true;
                if(selected.Track.Keys.Any(key=>key.Fixed))
                    drawLayer.ReplaceShape(selected,selected.WithTrack(selected.Track.WithSize(size)));
                else BeginTracking(size,false);
            };
            var options=new Expander {Header=L.P("Tracking options","따라가기 설정"),Content=followSize,Margin=new Thickness(0,0,0,4)};
            retrack=UI.Btn(L.P("Track again from this scene","이 장면부터 다시 따라가기"),
                ()=>BeginTracking(drawLayer.SelectedShape.Track.FollowSize,true));
            retrack.HorizontalAlignment=HorizontalAlignment.Left;
            retrack.Content=new TextBlock {Text=(string)retrack.Content,TextWrapping=TextWrapping.Wrap};
            AutomationProperties.SetName(retrack,L.P("Track again from this scene","이 장면부터 다시 따라가기"));
            trackingControls=new StackPanel {Margin=new Thickness(0,2,0,4)};
            trackingControls.Children.Add(followMotion);trackingControls.Children.Add(options);trackingControls.Children.Add(retrack);
            drawLayer.Updated+=SyncTrackingControls;
            drawLayer.BeforePointer+=delegate {if(drawLayer.HasTracks)SwitchToDrawingPreview();};
            drawLayer.PointerStarted+=delegate {
                if(trackWork!=null){CancelTracking();UpdateLostStatus();SetStatus();}
                if(drawLayer.SelectedShape!=null && drawLayer.SelectedShape.Track!=null){held=true;player.Hold=true;SetStatus();}
            };
            drawLayer.FixedSceneTime=DrawingTime;
            drawLayer.TrackLimit+=TrackLimit;
            drawLayer.MosaicPicture=TrackedMosaicPreview;
            SyncTrackingControls();
            return trackingControls;
        }

        BitmapSource TrackedMosaicPreview() {
            if(!drawLayer.HasTracks && !drawLayer.HasLiveAreas)return null;
            var frame=preview.Source as BitmapSource;
            if(frame==null || (!flipX && !flipY))return frame;
            return new TransformedBitmap(frame,new ScaleTransform(flipX?-1:1,flipY?-1:1));
        }

        void SyncTrackingControls() {
            var selected=drawLayer.SelectedShape;
            bool eligible=selected!=null && selected.CanTrack,busy=trackWork!=null;
            trackingControls.Visibility=eligible?Visibility.Visible:Visibility.Collapsed;
            bool selectionChanged=lastTrackingSelection!=selected;
            lastTrackingSelection=selected;
            if(!eligible)return;
            bool running=busy && selected==trackingShape;
            bool tracked=selected.Track!=null;
            followMotion.IsEnabled=!Double.IsNaN(playAt) && (!busy || running);
            followMotion.IsChecked=running || tracked;
            followSize.IsEnabled=!busy && (!tracked || !Double.IsNaN(playAt));
            if(!running && (tracked || selectionChanged))followSize.IsChecked=tracked && selected.Track.FollowSize;
            bool available=tracked && !busy && !Double.IsNaN(playAt) && selected.Track.FixedAt(DrawingTime());
            retrack.IsEnabled=available;
        }

        double DrawingTime() {
            if(Double.IsNaN(playAt))return 0;
            return Math.Max(0,Math.Min(end-start,playAt-start)/1000);
        }
        void ShowTrackedScene() {
            if(trackingControls==null)return;
            drawLayer.SetSceneTime(DrawingTime());
            SyncTrackingControls();
        }

        void DrawingsChanged() {
            CancelTracking();
            if(drawLayer.HasTracks && drawPanel.IsEditing && !showingOwn) {
                prepareSoon.Stop();
                if(prepareWork!=null)prepareWork.Cancel();
                preparing=-1;problem=null;
                UpdateSummary();UpdateLostStatus();ShowTrackedScene();SetStatus();
            } else {
                trackingStatus=null;Changed();
            }
        }

        void UpdateTrackingPart() {
            if(trackingControls==null)return;
            var request=Read();
            double step=MotionMaker.SourceSecondsPerFrame(request);
            bool range=start!=trackFrom || end!=trackTo,flipped=flipX!=trackFlipX || flipY!=trackFlipY;
            if(range || flipped || step!=trackStep)CancelTracking();
            double before=trackFrom;
            bool x=flipX!=trackFlipX,y=flipY!=trackFlipY;
            trackFrom=start;trackTo=end;trackFlipX=flipX;trackFlipY=flipY;trackStep=step;
            if(!Double.IsNaN(before) && (range || flipped)) {
                drawLayer.TransformTracks(path=>{
                    if(range)path=path.ForPart((start-before)/1000,(end-start)/1000);
                    return flipped?path.Flip(x,y):path;
                });
            }
            if(trackWork==null)UpdateLostStatus();
            ShowTrackedScene();
        }

        void UpdateLostStatus() {
            trackingStatus=null;
            if(!drawLayer.HasTracks || trackStep<=0)return;
            // Merge each shape's lost intervals on the current output sampling grid.
            int total=Math.Max(1,(int)Math.Round((end-start)/1000/trackStep,MidpointRounding.AwayFromZero));
            var ranges=drawLayer.Tracks.SelectMany(path=>path.LostRanges(trackStep,total)).OrderBy(r=>r.Item1);
            int lost=0,covered=0;
            foreach(var range in ranges) {
                lost+=Math.Max(0,range.Item2-Math.Max(covered,range.Item1));
                covered=Math.Max(covered,range.Item2);
            }
            if(lost>0)trackingStatus=L.P("Lost in "+lost+" scenes",lost+"개 장면에서 놓쳤어요");
        }
        void TrackLimit() {
            trackingStatus=L.P("There are too many tracking points for this shape.","이 도형에 따라갈 지점이 너무 많아요.");
            SetStatus();
        }

        void CancelTracking() {
            if(trackWork==null)return;
            var old=trackWork;
            trackWork=null;trackingShape=null;trackingStatus=null;
            old.Cancel();
            if(make!=null)make.IsEnabled=true;
            SyncTrackingControls();
        }

        async void BeginTracking(bool size,bool forwardOnly) {
            if(trackWork!=null || Double.IsNaN(playAt))return;
            var selected=drawLayer.SelectedShape;
            if(selected==null || !selected.CanTrack)return;
            double at=DrawingTime();
            var box=selected.At(at).Box;
            if(box.Width<=0 || box.Height<=0)return;
            if(forwardOnly && (selected.Track==null || !selected.Track.FixedAt(at)))return;
            SwitchToDrawingPreview();
            var request=Read();
            prepareSoon.Stop();
            if(prepareWork!=null)prepareWork.Cancel();
            preparing=-1;problem=null;
            held=true;player.Hold=true;
            var work=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            trackWork=work;trackingShape=selected;make.IsEnabled=false;
            int expected=request.ForwardFrames;
            if(forwardOnly) {
                double next=selected.Track.NextFixed(at);
                double step=MotionMaker.SourceSecondsPerFrame(request);
                expected=Math.Max(1,Math.Min(expected,(int)Math.Ceiling(Math.Min(request.Seconds,next)/step))-(int)Math.Round(at/step));
            }
            TrackProgress(0,expected);
            SetStatus();SyncTrackingControls();
            try {
                long refreshed=0;
                var path=await Task.Run(()=>MotionMaker.TrackPart(source,request,at,box,selected.Track,size,forwardOnly,work.Token,
                    (n,total)=>{
                        long now=System.Diagnostics.Stopwatch.GetTimestamp();
                        if(n==total || refreshed==0 || now-refreshed>=System.Diagnostics.Stopwatch.Frequency/10) {
                            refreshed=now;
                            Dispatcher.BeginInvoke(new Action(()=>{if(trackWork==work)TrackProgress(n,total);}));
                        }
                    }),work.Token);
                if(trackWork!=work || work.IsCancellationRequested)return;
                trackWork=null;trackingShape=null;
                if(path==null)TrackLimit();
                else drawLayer.ReplaceShape(selected,selected.WithTrack(path));
            } catch(OperationCanceledException) {
            } catch(Exception ex) {
                if(trackWork==work && !work.IsCancellationRequested) {
                    ErrorLog.Quiet(ex);
                    trackingStatus=L.P("Couldn't follow the motion in this part.","이 구간의 움직임을 따라가지 못했어요.");
                }
            } finally {
                if(trackWork==work){trackWork=null;trackingShape=null;}
                work.Dispose();
                if(trackWork==null && !lifetime.IsCancellationRequested) {
                    make.IsEnabled=true;SyncTrackingControls();SetStatus();
                }
            }
        }
        void TrackProgress(int done,int total) {
            trackingStatus=L.P("Following motion… "+done+"/"+total,"움직임을 따라가는 중… "+done+"/"+total);
            status.Text=trackingStatus;statusPill.Visibility=Visibility.Visible;
        }

        static double[] OrderTimes(MotionRequest request,double[] forward) {
            if(request.Loop=="reverse")return forward.Reverse().ToArray();
            if(request.Loop!="bounce" || forward.Length<3)return forward;
            return forward.Concat(forward.Skip(1).Take(forward.Length-2).Reverse()).ToArray();
        }
        static int NearestScene(double[] times,double at) {
            if(Double.IsNaN(at))return 0;
            int result=0;
            for(int i=1;i<times.Length;i++)if(Math.Abs(times[i]-at)<Math.Abs(times[result]-at))result=i;
            return result;
        }
        void StartDraft(MotionFrames frames,double[] times,double at) {
            draftOn=true;showingOwn=false;timeline=times;
            UpdatePreviewLook();
            player.Hold=held;
            player.Start(frames,NearestScene(times,at));
        }
        void SwitchToDrawingPreview() {
            if(showingOwn && drawingFrames!=null)StartDraft(drawingFrames,drawingTimeline,playAt);
        }
        void DrawingPanelOpenedOrClosed() {
            if(!drawLayer.HasTracks || !IsLoaded)return;
            if(drawPanel.IsEditing)SwitchToDrawingPreview();
            else if(AutoMake && !drawPanel.IsOpen && trackWork==null)SchedulePrepare();
        }
        bool SeekDrawingScene(double at) {
            if(drawingFrames==null)return false;
            held=true;player.Hold=true;
            if(showingOwn || timeline!=drawingTimeline)StartDraft(drawingFrames,drawingTimeline,at);
            else player.Seek(NearestScene(timeline,at));
            SetStatus();
            return true;
        }

        double[] EncodedTimeline(MotionRequest request,MotionResult result,MotionFrames frames,bool own,int copyRate,int copyCount) {
            double from=Math.Max(0,source.Moment-request.Before*1000);
            double each=1000*request.Speed/(own?result.Fps:copyRate);
            if(!own) {
                var copied=new double[copyCount];
                for(int i=0;i<copyCount;i++)copied[i]=from+i*each;
                return OrderTimes(request,copied);
            }
            int forward=result.Forward;
            // GIF encoders may merge duplicate images or hold the last image with a longer delay.
            var times=new double[frames.Images.Length];
            for(int i=0;i<times.Length;i++) {
                int j=request.Format=="mp4"?i:(int)Math.Round(frames.Timestamps[i]*result.Fps/1000.0);
                if(request.Loop=="reverse")j=Math.Max(0,forward-1-j);
                else if(request.Loop=="bounce" && forward>=3 && j>=forward)j=Math.Max(1,2*forward-2-j);
                else j=Math.Min(j,forward-1);
                times[i]=from+j*each;
            }
            return times;
        }
    }
}

