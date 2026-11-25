using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Media.Imaging;

namespace LoveMyShelf {
    internal struct VideoOrientation {
        public int Width,Height;
        public double Angle;
        public bool NeedsNormalizedCopy {
            get { return Angle!=0 && (Angle%90!=0 || Angle%180==0 || Width==Height); }
        }
        public double NativeCorrection(int width,int height) {
            return Angle!=0 && (Width>Height)==(width>height)?Angle:0;
        }
    }

    // The scene step: how far one "scene" is, everywhere a scene is picked or stepped through (the scene grid,
    // D / F in the large view, K / L in the cover editor, the seek bar's thumbnails, the Make GIF cover handles).
    // It replaces what used to be "one frame". Set from Guide & Options (Preferences.SceneStepMs).
    internal static class Scenes {
        internal static int StepMs=200;
        // The scene grid and the cover editor read this many scenes before and after the paused one
        // (75 at 0.2 s is 15 s each way), and never less than 4 s each way.
        internal const int Around=75;
        internal static int RadiusMs {get {return Math.Max(4000,(int)Math.Ceiling(Around*(double)Math.Max(1,StepMs)));}}
        // A time on the scene grid that passes through anchorMs (usually the cover's own moment).
        internal static double Snap(double ms,double anchorMs) {
            double step=Math.Max(1,StepMs);
            return anchorMs+Math.Round((ms-anchorMs)/step)*step;
        }
    }

    internal sealed class FrameTimes {
        readonly double[] starts;
        readonly List<double> keyframes;
        public readonly double Duration;
        public int Count {get{return starts.Length;}}
        // The file's own start time in seconds (FFprobe's start_time) when the whole list was made; 0 for most files.
        internal double Origin;
        public FrameTimes(double[] starts,double duration,List<double> keyframes=null) {
            this.starts=starts;Duration=duration;this.keyframes=keyframes;
        }
        // Set when this holds only some of the video's pictures (a scene every so often): the place of each in the full list.
        int[] fullIndex,fullOffset;
        internal bool Thinned {get{return fullIndex!=null;}}
        internal int FullIndex(int i){return fullIndex[i];}
        // One picture about every stepMs, always including the one at anchorMs. Never finer than the video's own pictures.
        internal FrameTimes Thin(double stepMs,double anchorMs) {
            if(starts.Length==0)return this;
            int anchor=At(anchorMs);
            var before=new List<int>();var after=new List<int>{anchor};
            double last=starts[anchor];
            for(int i=anchor+1;i<starts.Length;i++)if(starts[i]-last>=stepMs-0.5){after.Add(i);last=starts[i];}
            last=starts[anchor];
            for(int i=anchor-1;i>=0;i--)if(last-starts[i]>=stepMs-0.5){before.Add(i);last=starts[i];}
            before.Reverse();before.AddRange(after);
            var kept=before.ToArray();
            var thin=new FrameTimes(Array.ConvertAll(kept,i=>starts[i]),Duration,keyframes);
            thin.fullIndex=kept;
            thin.fullOffset=Array.ConvertAll(kept,i=>baseOffset(i));
            return thin;
        }
        int baseOffset(int i) {
            int first=i;
            while(first>0 && starts[first-1]==starts[i])first--;
            return i-first;
        }
        // The pictures from fromMs up to (not including) toMs, as their own list that ends at toMs (like Nearby).
        // Null when none are there or this list is already thinned.
        internal FrameTimes Slice(double fromMs,double toMs) {
            if(Thinned || starts.Length==0)return null;
            int a=Array.BinarySearch(starts,fromMs);if(a<0)a=~a;else while(a>0 && starts[a-1]==fromMs)a--;
            int b=Array.BinarySearch(starts,toMs);if(b<0)b=~b;else while(b>0 && starts[b-1]==toMs)b--;
            if(b<=a)return null;
            // Pictures sharing one time stay together, so each keeps its place among them (TimestampOffset).
            while(a>0 && starts[a-1]==starts[a])a--;
            var part=new double[b-a];Array.Copy(starts,a,part,0,b-a);
            return new FrameTimes(part,toMs,keyframes);
        }
        public double Start(int i){return starts[i];}
        public int TimestampOffset(int i) {
            if(fullOffset!=null)return fullOffset[i];
            int first=i;
            while(first>0 && starts[first-1]==starts[i])first--;
            return i-first;
        }
        public int At(double ms) {
            int i=Array.BinarySearch(starts,ms);
            if(i<0)return Math.Max(0,~i-1);
            while(i+1<starts.Length && starts[i+1]==ms)i++;
            return i;
        }
        public double? KeyframeBefore(double ms) {
            if(keyframes==null)return null;
            int i=keyframes.BinarySearch(ms);
            if(i<0)i=~i-1;
            return i<0?(double?)null:keyframes[i];
        }
    }
    internal sealed class VideoFrames : IDisposable {
        public static readonly string[] Extensions={".mp4",".m4v",".mov",".mkv",".webm",".avi",".wmv",".mpg",".mpeg",".mts",".m2ts",".ts",".3gp"};
        public static bool Supports(string path){return Array.IndexOf(Extensions,Path.GetExtension(path).ToLowerInvariant())>=0;}
        static readonly CultureInfo Inv=CultureInfo.InvariantCulture;
        readonly string path;
        readonly CancellationToken token;
        Process pipe;
        CancellationTokenRegistration stopReading;
        int next=-1,side;
        public readonly FrameTimes Times;
        sealed class Entry {public string Path;public long Bytes,Ticks;public FrameTimes Times;}
        sealed class IndexProgress {public volatile int Checked;public volatile bool Decoding;}
        static readonly List<Entry> cache=new List<Entry>();
        static readonly Dictionary<string,IndexProgress> indexing=new Dictionary<string,IndexProgress>(StringComparer.OrdinalIgnoreCase);
        public VideoFrames(string path,CancellationToken token) {this.path=path;this.token=token;Times=Index(path,token);}
        internal VideoFrames(string path,FrameTimes times,CancellationToken token){this.path=path;Times=times;this.token=token;}
        static string Tool(string name) {
            string app=AppDomain.CurrentDomain.BaseDirectory;
            var paths=new List<string>{Path.Combine(app,"tools"),app,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"DownloadHelper CoApp")};
            paths.AddRange((Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator));
            foreach(var dir in paths) {
                if(!Path.IsPathRooted(dir.Trim('"'))) continue;
                string exe=Path.Combine(dir.Trim('"'),name+".exe");
                if(File.Exists(exe))return exe;
            }
            throw new FileNotFoundException(name+L.T(".exe was not found. The Love My Shelf folder needs its tools folder. Unzip the downloaded ZIP file again from the start."));
        }
        internal static string Quote(string path) {return "\""+path+"\"";}
        internal static Process Run(string name,string arguments) {
            var setup=new ProcessStartInfo(Tool(name),arguments) {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            var p=Process.Start(setup);p.BeginErrorReadLine();return p;
        }
        // Pictures FFmpeg writes as BMPs to its output, for thumbnails and previews.
        internal static List<BitmapSource> Pictures(string before,string path,string after,CancellationToken token) {
            var list=new List<BitmapSource>();
            using(var p=Run("ffmpeg","-v error -nostdin "+before+"-i "+Quote(path)+" -map 0:V:0 -an -sn -dn "+after+" -c:v bmp -pix_fmt bgr24 -f image2pipe pipe:1"))
            using(token.Register(()=>Stop(p))) {
                var output=p.StandardOutput.BaseStream;
                while(true) {
                    token.ThrowIfCancellationRequested();
                    byte[] header=new byte[6];
                    int got=0;while(got<6){int r=output.Read(header,got,6-got);if(r==0)break;got+=r;}
                    if(got<6)break;
                    int size=BitConverter.ToInt32(header,2);
                    if(header[0]!=66 || header[1]!=77 || size<54 || size>512*1024*1024)break;
                    var data=new byte[size];Buffer.BlockCopy(header,0,data,0,6);Fill(output,data,6,size-6);
                    using(var memory=new MemoryStream(data)) {
                        var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.StreamSource=memory;image.EndInit();image.Freeze();
                        list.Add(image);
                    }
                }
                p.WaitForExit();token.ThrowIfCancellationRequested();
            }
            return list;
        }
        // Length of the file in milliseconds, or 0 when FFprobe can't tell.
        internal static double DurationMs(string path,CancellationToken token) {
            using(var p=Run("ffprobe","-v error -show_entries format=duration -of default=nw=1:nk=1 "+Quote(path)))
            using(token.Register(()=>Stop(p))) {
                string text=p.StandardOutput.ReadToEnd();p.WaitForExit();token.ThrowIfCancellationRequested();
                double seconds=Number(text.Trim());
                return p.ExitCode==0 && seconds>0 && !Double.IsInfinity(seconds)?seconds*1000:0;
            }
        }
        // The time between the video's own pictures in milliseconds (0 when FFprobe can't tell).
        internal static double FrameGapMs(string path,CancellationToken token) {
            try {
                using(var p=Run("ffprobe","-v error -select_streams V:0 -show_entries stream=avg_frame_rate -of default=nw=1:nk=1 "+Quote(path)))
                using(token.Register(()=>Stop(p))) {
                    string text=p.StandardOutput.ReadToEnd().Trim();p.WaitForExit();token.ThrowIfCancellationRequested();
                    string[] parts=text.Split('/');
                    double top=Number(parts[0]),bottom=parts.Length>1?Number(parts[1]):1;
                    return top>0 && bottom>0 && !Double.IsInfinity(top)?1000*bottom/top:0;
                }
            } catch(IOException) {return 0;}
            catch(System.ComponentModel.Win32Exception) {return 0;}
        }
        internal static void Stop(Process p) {if(p==null)return;try{if(!p.HasExited)p.Kill();}catch(InvalidOperationException){}}
        static double Number(string s) {double n;return Double.TryParse(s,NumberStyles.Float,Inv,out n)?n:Double.NaN;}
        internal static VideoOrientation ReadOrientation(string path,CancellationToken token) {
            var result=new VideoOrientation();
            try {
                using(var p=Run("ffprobe","-v error -select_streams V:0 -show_entries stream=width,height:stream_side_data=rotation -of default=nw=1 "+Quote(path)))
                using(token.Register(()=>Stop(p))) {
                    string line;
                    while((line=p.StandardOutput.ReadLine())!=null) {
                        if(line.StartsWith("width="))Int32.TryParse(line.Substring(6),out result.Width);
                        else if(line.StartsWith("height="))Int32.TryParse(line.Substring(7),out result.Height);
                        else if(line.StartsWith("rotation=")) {
                            double degrees=Number(line.Substring(9));
                            if(!Double.IsNaN(degrees) && !Double.IsInfinity(degrees))result.Angle=-degrees%360;
                        }
                    }
                    p.WaitForExit();token.ThrowIfCancellationRequested();
                    if(p.ExitCode!=0 || result.Width<=0 || result.Height<=0)return new VideoOrientation();
                }
            } catch(IOException) { return new VideoOrientation(); }
            catch(UnauthorizedAccessException) { return new VideoOrientation(); }
            catch(System.ComponentModel.Win32Exception) { return new VideoOrientation(); }
            return result;
        }

        // The scene times from radius before center up to radius after it.
        // Quickest first: the list the large view already made while playing, then the stored times of the
        // video's packets (nothing decoded), and only when those look wrong, decoding the whole stretch.
        internal static FrameTimes Nearby(string path,double center,CancellationToken token,int radius=2000) {
            token.ThrowIfCancellationRequested();
            double from=Math.Max(0,center-radius),end=center+radius+1;
            // Only files starting at 0: for the others (TS recordings and the like) the lists didn't match in tests.
            var known=Known(path);
            if(known!=null && known.Origin==0) {
                var part=known.Slice(from,end);
                if(part==null)throw new InvalidDataException(L.T("Could not read the video's scene times."));
                return part;
            }
            return NearbyPackets(path,from,end,token)??NearbyDecoded(path,from,end,token);
        }
        // The whole scene list, when it is already made for this very file (same size and time).
        static FrameTimes Known(string path) {
            try {
                var info=new FileInfo(path);long ticks=info.LastWriteTimeUtc.Ticks,bytes=info.Length;
                lock(cache) {
                    var hit=cache.Find(e=>e.Path==path && e.Ticks==ticks && e.Bytes==bytes);
                    return hit==null?null:hit.Times;
                }
            } catch(IOException) {return null;}
            catch(UnauthorizedAccessException) {return null;}
        }
        // Scene times from the packets' own times, read without decoding (well under a second even in 4K).
        // Null whenever they may not match the decoded pictures one for one, since the scene reader counts pictures:
        // interlaced video (two packets per picture), times missing, repeated or out of order, or more packets than
        // the frame rate allows.
        static FrameTimes NearbyPackets(string path,double from,double end,CancellationToken token) {
            try {
                double origin=0,fps=0;string field=null;
                using(var p=Run("ffprobe","-v error -select_streams V:0 -show_entries format=start_time:stream=avg_frame_rate,field_order -of default=nw=1 "+Quote(path)))
                using(token.Register(()=>Stop(p))) {
                    string line;
                    while((line=p.StandardOutput.ReadLine())!=null) {
                        if(line.StartsWith("start_time=")){double v=Number(line.Substring(11));if(!Double.IsNaN(v) && !Double.IsInfinity(v))origin=v;}
                        else if(line.StartsWith("field_order="))field=line.Substring(12).Trim();
                        else if(line.StartsWith("avg_frame_rate=")) {
                            string[] parts=line.Substring(15).Split('/');
                            double top=Number(parts[0]),bottom=parts.Length>1?Number(parts[1]):1;
                            if(top>0 && bottom>0 && !Double.IsInfinity(top))fps=top/bottom;
                        }
                    }
                    p.WaitForExit();token.ThrowIfCancellationRequested();
                    if(p.ExitCode!=0)return null;
                }
                // Files that don't start at 0 (TS recordings and the like) gave other lists than decoding in tests.
                if(origin!=0 || (field!=null && field!="progressive" && field!="unknown"))return null;
                // A margin both ways: packets come in decoding order, so pictures shown just before the end
                // may be stored after it. Near the start it reads from the very beginning, since a cut file's first
                // pictures can be stored before 0.
                string interval=(from>2000?Seconds(from-2000):"")+"%"+Seconds(end+2000);
                var starts=new List<double>();
                using(var p=Run("ffprobe","-v error -select_streams V:0 -read_intervals "+interval+
                    " -show_entries packet=pts_time,flags -of compact=p=1:nk=0 "+Quote(path)))
                using(token.Register(()=>Stop(p))) {
                    string line;bool broken=false;
                    while((line=p.StandardOutput.ReadLine())!=null) {
                        token.ThrowIfCancellationRequested();
                        if(!line.StartsWith("packet"))continue;
                        double pts=Double.NaN;bool discard=false;
                        foreach(string part in line.Split('|')) {
                            if(part.StartsWith("pts_time="))pts=Number(part.Substring(9));
                            else if(part.StartsWith("flags="))discard=part.IndexOf('D',6)>=0;
                        }
                        if(discard)continue;
                        if(Double.IsNaN(pts) || Double.IsInfinity(pts)){broken=true;continue;}
                        double ms=(pts-origin)*1000;
                        if(ms>=from && ms<end)starts.Add(ms);
                    }
                    p.WaitForExit();token.ThrowIfCancellationRequested();
                    if(p.ExitCode!=0 || broken || starts.Count==0)return null;
                }
                starts.Sort();
                for(int i=1;i<starts.Count;i++)if(starts[i]<=starts[i-1])return null;
                if(fps>0) {
                    double gap=1000/fps,span=starts[starts.Count-1]-starts[0]+gap;
                    if(starts.Count>span/gap*1.25+2)return null;
                }
                return new FrameTimes(starts.ToArray(),end);
            } catch(IOException) {token.ThrowIfCancellationRequested();return null;}
            catch(System.ComponentModel.Win32Exception) {token.ThrowIfCancellationRequested();return null;}
        }
        static FrameTimes NearbyDecoded(string path,double from,double end,CancellationToken token) {
            token.ThrowIfCancellationRequested();
            var starts=new List<double>();
            double millisPerTick=0;
            string seek=Seconds(from);
            // Keep the timestamps on the same zero-based timeline as the image reader.
            // The wrapped-frame encoder avoids pixel hashing and image encoding.
            using(var p=Run("ffmpeg","-v error -nostdin -copyts -start_at_zero -ss "+seek+" -noautorotate -i "+Quote(path)+
                " -map 0:V:0 -an -sn -dn -to "+Seconds(end)+" -fps_mode passthrough -enc_time_base -1 -c:v wrapped_avframe -f framecrc pipe:1"))
            using(token.Register(()=>Stop(p))) {
                string line;
                while((line=p.StandardOutput.ReadLine())!=null) {
                    token.ThrowIfCancellationRequested();
                    if(line.StartsWith("#tb 0: ")) {
                        string[] ratio=line.Substring(7).Split('/');
                        if(ratio.Length==2)millisPerTick=1000*Number(ratio[0])/Number(ratio[1]);
                    } else if(line.Length>0 && line[0]!='#' && millisPerTick>0) {
                        string[] row=line.Split(',');
                        long pts;
                        if(row.Length>2 && Int64.TryParse(row[2],NumberStyles.Integer,Inv,out pts))starts.Add(pts*millisPerTick);
                    }
                }
                p.WaitForExit();token.ThrowIfCancellationRequested();
                if(p.ExitCode!=0 || starts.Count==0)throw new InvalidDataException(L.T("Could not read the video's scene times."));
            }
            return new FrameTimes(starts.ToArray(),end);
        }
        internal static string Seconds(double ms) {return (ms/1000).ToString("0.000000",Inv);}
        public static FrameTimes Index(string path,CancellationToken token,bool background=false) {
            var info=new FileInfo(path);long ticks=info.LastWriteTimeUtc.Ticks,bytes=info.Length;
            var progress=new IndexProgress();
            lock(cache) {
                while(indexing.ContainsKey(path)){token.ThrowIfCancellationRequested();Monitor.Wait(cache,100);}
                token.ThrowIfCancellationRequested();
                var hit=cache.Find(e=>e.Path==path&&e.Ticks==ticks&&e.Bytes==bytes);if(hit!=null)return hit.Times;
                indexing.Add(path,progress);
            }
            try {
                var times=Probe(path,false,progress,token,background)??Probe(path,true,progress,token,background);
                if(times==null)throw new InvalidDataException(L.T("Could not read the video's scene times."));
                lock(cache){cache.RemoveAll(e=>e.Path==path);if(cache.Count>=6)cache.RemoveAt(0);cache.Add(new Entry{Path=path,Ticks=ticks,Bytes=bytes,Times=times});}
                return times;
            } finally {lock(cache){indexing.Remove(path);Monitor.PulseAll(cache);}}
        }
        internal static bool TryIndexProgress(string path,out bool decoding,out int scanned) {
            IndexProgress progress;
            lock(cache){indexing.TryGetValue(path,out progress);}
            decoding=progress!=null && progress.Decoding;
            scanned=progress==null?0:progress.Checked;
            return progress!=null;
        }
        static FrameTimes Probe(string path,bool decode,IndexProgress progress,CancellationToken token,bool background) {
            progress.Checked=0;
            progress.Decoding=decode;
            var stamps=new List<double>();var keyframes=new List<double>();
            double origin=0,lastPts=Double.MinValue,lastDuration=0,videoDuration=0;
            int declared=0,width=0,height=0,scanned=0;bool missing=false;string codec=null;
            string fields=decode?"-show_frames -show_entries frame=best_effort_timestamp_time,pkt_duration_time":"-show_packets -show_entries packet=pts_time,duration_time,flags";
            using(var p=Run("ffprobe","-v error "+(decode?"-threads 0 ":"")+"-select_streams V:0 "+fields+
                ":format=start_time:stream=nb_frames,duration,codec_name,width,height -of compact=p=1:nk=0 "+Quote(path)))
            using(token.Register(()=>Stop(p))) {
                if(background) {
                    try{p.PriorityClass=ProcessPriorityClass.BelowNormal;}
                    catch(System.ComponentModel.Win32Exception){}
                    catch(InvalidOperationException){}
                }
                string line;
                while((line=p.StandardOutput.ReadLine())!=null) {
                    token.ThrowIfCancellationRequested();
                    var parts=line.Split('|');double pts=Double.NaN,delay=0;bool discard=false,keyframe=false;
                    foreach(string part in parts) {
                        int sep=part.IndexOf('=');if(sep<0)continue;string key=part.Substring(0,sep),v=part.Substring(sep+1);
                        switch(key) {
                            case "pts_time":case "best_effort_timestamp_time":pts=Number(v);break;
                            case "pkt_duration_time":case "duration_time":delay=Number(v);break;
                            case "flags":discard=v.Contains("D");keyframe=v.Contains("K");break;
                            case "start_time":if(!Double.IsNaN(Number(v)))origin=Number(v);break;
                            case "duration":videoDuration=Number(v);break;
                            case "nb_frames":Int32.TryParse(v,out declared);break;
                            case "codec_name":codec=v;break;
                            case "width":Int32.TryParse(v,out width);break;
                            case "height":Int32.TryParse(v,out height);break;
                        }
                    }
                    if(parts[0]!="packet" && parts[0]!="frame")continue;
                    progress.Checked=++scanned;
                    if(discard)continue;
                    if(Double.IsNaN(pts)){missing=true;continue;}
                    if(keyframe)keyframes.Add(pts*1000);
                    stamps.Add(pts);if(pts>=lastPts){lastPts=pts;lastDuration=delay;}
                }
                p.WaitForExit();token.ThrowIfCancellationRequested();if(p.ExitCode!=0 || stamps.Count==0)return null;
            }
            if(!decode && (missing || (declared>0 && declared!=stamps.Count)))return null;
            stamps.Sort();
            var starts=new double[stamps.Count];
            for(int i=0;i<stamps.Count;i++) {
                starts[i]=Math.Max(0,(stamps[i]-origin)*1000);
                if(!decode && i>0 && starts[i]<=starts[i-1]) return null;
            }
            int last=starts.Length-1;
            if(!(lastDuration>0))lastDuration=last>0?(starts[last]-starts[last-1])/1000:0.04;
            double duration=starts[last]+Math.Max(1,lastDuration*1000);
            if(videoDuration>0 && starts[0]+videoDuration*1000>starts[last])duration=starts[0]+videoDuration*1000;
            bool fastSeek=!decode && origin==0 && codec=="vp9" && IsHighResolution(width,height) &&
                String.Equals(Path.GetExtension(path),".mkv",StringComparison.OrdinalIgnoreCase);
            if(fastSeek)keyframes.Sort();
            return new FrameTimes(starts,duration,fastSeek?keyframes:null) {Origin=origin};
        }
        internal static bool IsHighResolution(int width,int height) {return (long)width*height>=8000000;}
        public static BitmapSource First(string path,int maxSide) {
            return AtTime(path,0,0,maxSide,CancellationToken.None);
        }
        internal static BitmapSource AtTime(string path,double ms,int repeat,int maxSide,CancellationToken token) {
            using(var reader=new VideoFrames(path,null,token))return reader.ReadImage(ms,repeat,maxSide);
        }
        internal static BitmapSource PreviewAt(string path,double ms,int maxSide,CancellationToken token) {
            var info=new FileInfo(path);
            long ticks=info.LastWriteTimeUtc.Ticks,bytes=info.Length;
            Entry hit;
            lock(cache)hit=cache.Find(e=>e.Path==path && e.Ticks==ticks && e.Bytes==bytes);
            // Hovering uses an available index but never waits for one.
            var times=hit==null?null:hit.Times;
            int index=times==null?0:times.At(ms);
            double target=times==null?ms:times.Start(index);
            int repeat=times==null?0:times.TimestampOffset(index);
            double? keyframe=times==null?null:times.KeyframeBefore(target);
            using(var reader=new VideoFrames(path,times,token)) {
                try {
                    token.ThrowIfCancellationRequested();
                    reader.StartRead(target,repeat,maxSide,keyframe,true);
                    return reader.ReadImage();
                } catch(EndOfStreamException) {
                    token.ThrowIfCancellationRequested();
                    return reader.ReadImage(target,repeat,maxSide);
                }
            }
        }
        public BitmapSource Read(int index,int maxSide) {
            token.ThrowIfCancellationRequested();
            if(Times.Thinned) {
                // Only some pictures are wanted. Reading on forward over the ones in between is quicker than starting again.
                int wanted=Times.FullIndex(index);
                if(pipe!=null && side==maxSide && wanted>=next && wanted-next<=48) {
                    while(next<wanted){SkipImage();next++;}
                } else {
                    double start=Times.Start(index);
                    var near=index==0 || maxSide<=0?null:Times.KeyframeBefore(start);
                    StartRead(start,Times.TimestampOffset(index),maxSide,near);
                    side=maxSide;next=wanted;
                }
                next++;
                return ReadImage();
            }
            if(pipe==null || next!=index || side!=maxSide) {
                double ms=Times.Start(index);
                var keyframe=index==0 || maxSide<=0?null:Times.KeyframeBefore(ms);
                StartRead(ms,Times.TimestampOffset(index),maxSide,keyframe);
                side=maxSide;next=index;
            }
            next++;
            return ReadImage();
        }
        BitmapSource ReadImage(double ms,int repeat,int maxSide) {
            token.ThrowIfCancellationRequested();
            StartRead(ms,repeat,maxSide,null);
            return ReadImage();
        }
        void StartRead(double ms,int repeat,int maxSide,double? keyframe,bool preview=false) {
            Dispose();
            // Seek early, then select by presentation timestamp. Input-only seeking can skip the target B-frame.
            double lead=preview?25:1000;
            string seek=ms>lead?"-ss "+Seconds(ms-lead)+" ":"";
            if(keyframe.HasValue && ms-keyframe.Value<1000) {
                // Indexed 4K VP9 MKV can start at the preceding keyframe and keep source timestamps.
                string threads=ms-keyframe.Value<=250?"-threads 2 ":"";
                double start=Math.Max(0,Math.Min(ms,keyframe.Value+1));
                seek=threads+"-noaccurate_seek -ss "+Seconds(start)+" ";
            }
            var filters=new List<string>();
            if(ms>0)filters.Add("select='gte(t,"+Seconds(Math.Max(0,ms-0.002))+")'");
            if(repeat>0)filters.Add("trim=start_frame="+repeat.ToString(Inv));
            if(maxSide>0)filters.Add("scale=w='min(iw,"+maxSide+")':h='min(ih,"+maxSide+")':force_original_aspect_ratio=decrease");
            string filter=filters.Count==0?"":"-vf "+Quote(String.Join(",",filters))+" ";
            pipe=Run("ffmpeg","-v error -nostdin -copyts -start_at_zero "+seek+"-i "+Quote(path)+
                " -map 0:V:0 -an -sn -dn "+filter+(preview?"-frames:v 1 ":"")+
                "-fps_mode passthrough -threads 1 -c:v bmp -pix_fmt bgr24 -f image2pipe pipe:1");
            var process=pipe;
            stopReading=token.Register(()=>Stop(process));
        }
        BitmapSource ReadImage() {
            var output=pipe.StandardOutput.BaseStream;
            byte[] header=new byte[6];Fill(output,header,0,6);int size=BitConverter.ToInt32(header,2);
            if(header[0]!=66 || header[1]!=77 || size<54 || size>512*1024*1024)throw new InvalidDataException(L.T("The video scene is in a format that can't be read."));
            var data=new byte[size];Buffer.BlockCopy(header,0,data,0,6);Fill(output,data,6,size-6);
            using(var memory=new MemoryStream(data)) {
                var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.StreamSource=memory;image.EndInit();image.Freeze();return image;
            }
        }
        // Reads past one picture without turning it into an image.
        void SkipImage() {
            var output=pipe.StandardOutput.BaseStream;
            byte[] header=new byte[6];Fill(output,header,0,6);int size=BitConverter.ToInt32(header,2);
            if(header[0]!=66 || header[1]!=77 || size<54 || size>512*1024*1024)throw new InvalidDataException(L.T("The video scene is in a format that can't be read."));
            var buffer=new byte[Math.Min(size-6,1<<20)];
            int left=size-6;
            while(left>0){int part=Math.Min(left,buffer.Length);Fill(output,buffer,0,part);left-=part;}
        }
        static void Fill(Stream stream,byte[] bytes,int offset,int count) {
            while(count>0){int read=stream.Read(bytes,offset,count);if(read==0)throw new EndOfStreamException(L.T("Could not read the scene chosen from the video."));offset+=read;count-=read;}
        }
        public static string PlayableCopy(string path,string directory,CancellationToken token) {
            Directory.CreateDirectory(directory);
            string copy=Path.Combine(directory,Guid.NewGuid().ToString("N")+".mp4");
            try {
                using(var p=Run("ffmpeg","-v error -nostdin -i "+Quote(path)+" -map 0:V:0 -map 0:a:0? -vf \"pad=ceil(iw/2)*2:ceil(ih/2)*2\" -fps_mode passthrough -c:v libx264 -preset ultrafast -crf 19 -pix_fmt yuv420p -c:a aac -movflags +faststart "+Quote(copy)))
                using(token.Register(()=>Stop(p))){p.WaitForExit();token.ThrowIfCancellationRequested();if(p.ExitCode!=0)throw new InvalidDataException(L.T("Could not play this video."));}
                return copy;
            } catch {RemoveCopy(copy);throw;}
        }
        public static void RemoveCopy(string path){if(path!=null)try{File.Delete(path);}catch(IOException){}catch(UnauthorizedAccessException){}}
        public void Dispose(){if(pipe!=null){stopReading.Dispose();Stop(pipe);pipe.Dispose();pipe=null;}}
    }
}
