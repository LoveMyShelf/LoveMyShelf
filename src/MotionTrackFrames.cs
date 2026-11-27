using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LoveMyShelf {
    internal static partial class MotionMaker {
        static string VideoPartInput(string path,double start,double end) {
            return (start>0?"-ss "+VideoFrames.Seconds(start)+" ":"")+"-t "+N((end-start)/1000)+" -i "+VideoFrames.Quote(path)+" ";
        }
        static string AnimationPartInput(string raw,int fps,int width,int height) {
            return "-f rawvideo -pix_fmt bgra -s "+width+"x"+height+" -framerate "+fps+" -i "+VideoFrames.Quote(raw)+" ";
        }
        internal static double SourceSecondsPerFrame(MotionRequest request) {return request.Speed/request.Fps;}

        static int RenderVariableTracks(string input,string chain,string flips,List<MotionShape> shapes,MotionRequest request,
            int width,int height,string output,int expected,CancellationToken token,Action<double> progress,
            Action<BitmapSource,int> copies,int copyHeight) {
            int count=0;
            MotionDraw.RunSta(()=>{
                string decode="-v error -nostdin "+input+"-map 0:v:0 -an -sn -dn -vf "+
                    VideoFrames.Quote(chain+(flips==null?"":","+flips)+",format=bgra")+
                    " -fps_mode passthrough -threads:v 1 -f rawvideo -pix_fmt bgra pipe:1";
                string encode="-v error -y "+AnimationPartInput("pipe:0",request.Fps,width,height)+
                    "-an -c:v ffv1 -threads:v 2 -pix_fmt bgra -f matroska "+VideoFrames.Quote(output);
                using(var decoder=VideoFrames.Run("ffmpeg",decode))
                using(token.Register(()=>VideoFrames.Stop(decoder))) {
                    Quiet(decoder);
                    try {
                        using(var encoder=VideoFrames.Run("ffmpeg",encode,true))
                        using(token.Register(()=>VideoFrames.Stop(encoder))) {
                            Quiet(encoder);
                            try {
                                int stride=checked(width*4);
                                var unbaked=decoder.StandardOutput.BaseStream;
                                var baked=encoder.StandardInput.BaseStream;
                                double step=SourceSecondsPerFrame(request);
                                int copyRate=copies==null?0:Math.Max(1,Math.Min(request.Fps,request.Fps*PreviewCopyLimit/Math.Max(1,expected)));
                                int copied=0;
                                // Erasing, mosaics and part adjustments only change pixels, so several pictures are done at once
                                // (half the PC's cores, below normal priority); pen drawings need the one drawing thread.
                                // 2026-11-27 user: "1% a second" on a 12-core laptop.
                                int lanes=shapes.All(s=>s.IsErase || s.IsMosaic || s.IsArea)?Math.Max(1,Math.Min(8,Environment.ProcessorCount/2)):1;
                                // Each lane holds about three pictures at a time: big pictures get fewer lanes (about 300 MB at most).
                                lanes=(int)Math.Max(1,Math.Min(lanes,300L*1024*1024/Math.Max(1L,3L*stride*height)));
                                var batch=new byte[lanes][];
                                for(int i=0;i<lanes;i++)batch[i]=new byte[checked(stride*height)];
                                var options=new System.Threading.Tasks.ParallelOptions {MaxDegreeOfParallelism=lanes,CancellationToken=token};
                                bool more=true;
                                while(more) {
                                    int n=0;
                                    while(n<lanes && (more=Fill(unbaked,batch[n])))n++;
                                    if(n==0)break;
                                    token.ThrowIfCancellationRequested();
                                    int first=count;
                                    var originals=new BitmapSource[n];
                                    for(int k=0;k<n;k++) {
                                        originals[k]=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,batch[k],stride);originals[k].Freeze();
                                        if(copies!=null && copied<PreviewCopyLimit && (long)(first+k)*copyRate>=(long)copied*request.Fps) {
                                            copies(UndressedCopy(originals[k],request,copyHeight),copyRate);copied++;
                                        }
                                    }
                                    Action<int> bake=k=>{
                                        var at=new MotionShape[shapes.Count];
                                        for(int i=0;i<at.Length;i++)at[i]=shapes[i].At((first+k)*step);
                                        Bgra(MotionDraw.Apply(originals[k],at)).CopyPixels(batch[k],stride,0);
                                    };
                                    if(n==1)bake(0);
                                    else {
                                        try {
                                            System.Threading.Tasks.Parallel.For(0,n,options,k=>{
                                                var thread=Thread.CurrentThread;var was=thread.Priority;
                                                thread.Priority=ThreadPriority.BelowNormal;
                                                try {bake(k);} finally {thread.Priority=was;}
                                            });
                                        } catch(AggregateException many) {
                                            // The first real problem, as one lane would have thrown it.
                                            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(many.Flatten().InnerExceptions[0]).Throw();
                                        }
                                    }
                                    for(int k=0;k<n;k++)baked.Write(batch[k],0,batch[k].Length);
                                    count+=n;
                                    if(progress!=null)progress(Math.Min(1,count/(double)Math.Max(1,expected)));
                                }
                                baked.Dispose();
                                decoder.WaitForExit();encoder.WaitForExit();token.ThrowIfCancellationRequested();
                                if(decoder.ExitCode!=0 || encoder.ExitCode!=0 || count==0)
                                    throw new InvalidDataException(L.T("FFmpeg could not make the file."));
                            } finally {VideoFrames.Stop(encoder);}
                        }
                    } finally {VideoFrames.Stop(decoder);}
                }
            });
            return count;
        }

        static BitmapSource UndressedCopy(BitmapSource picture,MotionRequest request,int height) {
            double scale=Math.Min(1,Math.Max(2,height)/(double)picture.PixelHeight);
            double x=request.FlipX?-scale:scale,y=request.FlipY?-scale:scale;
            var copy=new TransformedBitmap(picture,new ScaleTransform(x,y));
            copy.Freeze();
            return copy;
        }

        internal static MotionTrackPath TrackPart(MotionSource source,MotionRequest request,double scene,Rect box,
            MotionTrackPath previous,bool followSize,bool forwardOnly,CancellationToken token,Action<int,int> progress) {
            request=request.Copy();
            request.Height=480;
            double step=SourceSecondsPerFrame(request);
            double until=forwardOnly?previous.NextFixed(scene):Double.PositiveInfinity;
            bool video=VideoFrames.Supports(source.Path);
            string folder=Path.Combine(PreviewFolder,"track-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try {
                string input;
                int width,height;
                if(video) {
                    var size=VideoFrames.ReadOrientation(source.Path,token);
                    width=size.Width;height=size.Height;
                    if(Math.Abs(size.Angle%180)==90){int swap=width;width=height;height=swap;}
                    double start=Math.Max(0,source.Moment-request.Before*1000);
                    double end=source.Moment+request.After*1000,duration=VideoFrames.DurationMs(source.Path,token);
                    if(duration>0)end=Math.Min(end,duration);
                    if(end<=start || width<2 || height<2)throw new InvalidDataException();
                    input=VideoPartInput(source.Path,start,end);
                } else {
                    string raw=Path.Combine(folder,"animation.raw");
                    WriteAnimationPart(source,request,raw,new MotionResult(),token,null,out width,out height);
                    input=AnimationPartInput(raw,request.Fps,width,height);
                }
                int outW,outH;
                string chain=Filters(source.Crop,source.Rotation,source.Brightness,width,height,request,video,out outW,out outH,true,source.EffectiveAdjust);
                string flips=MotionLook.Flips(request);
                if(flips!=null)chain+=","+flips;
                string limit=Double.IsInfinity(until)?"":" -frames:v "+Math.Max(1,(int)Math.Ceiling((until-MotionTrackPath.TimeTolerance)/step)).ToString(CultureInfo.InvariantCulture);
                string args="-v error -nostdin "+input+"-map 0:v:0 -an -sn -dn -vf "+VideoFrames.Quote(chain+",setpts=N,format=gray")+
                    limit+" -fps_mode passthrough -threads:v 1 -f rawvideo -pix_fmt gray pipe:1";
                int frameBytes=checked(outW*outH),count=0;
                using(var cache=new FileStream(Path.Combine(folder,"frames.gray"),FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None,65536,FileOptions.DeleteOnClose))
                using(var p=VideoFrames.Run("ffmpeg",args))
                using(token.Register(()=>VideoFrames.Stop(p))) {
                    // Forward and backward passes read the same small frames without keeping the whole part in RAM.
                    var buffer=new byte[frameBytes];
                    while(Fill(p.StandardOutput.BaseStream,buffer)) {
                        token.ThrowIfCancellationRequested();
                        cache.Write(buffer,0,buffer.Length);count++;
                    }
                    p.WaitForExit();token.ThrowIfCancellationRequested();
                    if(p.ExitCode!=0 || count==0)throw new InvalidDataException();
                    int seed=Math.Max(0,Math.Min(count-1,(int)Math.Round(scene/step)));
                    Func<int,byte[]> read=index=>{
                        cache.Position=(long)index*frameBytes;
                        if(!Fill(cache,buffer))throw new EndOfStreamException();
                        return buffer;
                    };
                    var tracked=MotionTrack.Follow(read,count,outW,outH,seed,box,followSize,step,forwardOnly,token,progress);
                    if(!forwardOnly)return MotionTrackPath.Create(tracked,followSize,seed*step);
                    var from=tracked.Skip(seed+1).Where(k=>k.Time>scene+MotionTrackPath.TimeTolerance && k.Time<until-MotionTrackPath.TimeTolerance);
                    var anchor=new MotionTrackKey(scene,box,true,false);
                    return previous.ReplaceForward(scene,until,new[]{anchor}.Concat(from));
                }
            } finally {
                try {Directory.Delete(folder,true);}
                catch(IOException ex) {ErrorLog.Quiet(ex);}
                catch(UnauthorizedAccessException ex) {ErrorLog.Quiet(ex);}
            }
        }
    }
}

