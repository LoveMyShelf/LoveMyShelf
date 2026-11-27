using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace LoveMyShelf {
    // Plays video through libmpv (hardware decoding, any container or codec FFmpeg reads) inside a
    // native child window. WPF cannot draw over that window, so the viewer moves its controls to
    // VideoOverlay while this player is shown.
    internal sealed class MpvPlayer : HwndHost {
        const string Library="libmpv-2.dll";
        const int FormatInt64=4,FormatDouble=5;
        const int EventShutdown=1,EventEndFile=7,EventFileLoaded=8,EventVideoReconfig=17,EventPlaybackRestart=21;
        const int EndReasonError=4;
        static readonly CultureInfo Inv=CultureInfo.InvariantCulture;
        static bool? available;

        readonly string path;
        readonly DispatcherTimer events=new DispatcherTimer(DispatcherPriority.Input) {Interval=TimeSpan.FromMilliseconds(15)};
        IntPtr handle,window;
        bool opened,closed;

        public event Action<double,bool> Opened; // duration in ms, has audio
        public event Action<string> Failed;
        public event Action VideoChanged;
        public event Action Restarted; // a seek finished and the new frame is shown

        public double Aspect {get;private set;}
        public bool SeekPending {get;private set;}

        public MpvPlayer(string path) {
            this.path=path;
            events.Tick+=delegate {Drain();};
        }

        public static bool Available {
            get {
                if(available==null)available=Load();
                return available.Value;
            }
        }
        static bool Load() {
            string app=AppDomain.CurrentDomain.BaseDirectory;
            foreach(var dir in new[]{app,Path.Combine(app,"tools")}) {
                string file=Path.Combine(dir,Library);
                if(File.Exists(file) && LoadLibrary(file)!=IntPtr.Zero)return true;
            }
            return false;
        }

        protected override HandleRef BuildWindowCore(HandleRef parent) {
            window=CreateWindowEx(0,"static","",0x40000000|0x10000000|0x02000000|0x04000000|0x4, // child, visible, clip children/siblings, black
                0,0,1,1,parent.Handle,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
            try {Start();}
            catch(Exception ex) {
                string message=ex.Message;
                Dispatcher.BeginInvoke(new Action(()=>Fail(message)));
            }
            return new HandleRef(this,window);
        }
        void Start() {
            handle=mpv_create();
            if(handle==IntPtr.Zero)throw new InvalidOperationException("mpv could not start.");
            Option("wid",window.ToInt64().ToString(Inv));
            Option("config","no");Option("load-scripts","no");Option("osc","no");Option("ytdl","no");
            Option("terminal","no");Option("input-default-bindings","no");Option("input-vo-keyboard","no");
            Option("input-cursor","no");Option("cursor-autohide","no");Option("osd-level","0");
            Option("hwdec","auto-safe");Option("hr-seek","yes");Option("loop-file","inf");
            Option("keep-open","no");Option("idle","yes");Option("audio-display","no");
            Option("background-color","#000000");Option("force-window","no");
            Option("sub-auto","no"); // LoveMyShelf finds and reads subtitle files itself (see Subtitles.cs)
            Option("audio-device",audioDevice);
            // Subtitles use LoveMyShelf's own font (the fonts folder beside the exe), so they match the rest of the
            // program. ASS subtitles keep the fonts their maker chose. A player that doesn't know these keeps its own font.
            string fonts=System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"fonts");
            if(System.IO.File.Exists(System.IO.Path.Combine(fonts,"Pretendard-Bold.otf"))) {
                mpv_set_option_string(handle,Utf8("sub-fonts-dir"),Utf8(fonts));
                mpv_set_option_string(handle,Utf8("sub-font"),Utf8("Pretendard"));
                mpv_set_option_string(handle,Utf8("sub-bold"),Utf8("yes"));
            }
            mpv_set_option_string(handle,Utf8("sub-scale"),Utf8(subtitleScale.ToString("0.###",Inv)));
            Check(mpv_initialize(handle),"initialize");
            events.Start();
            Command("loadfile",path,"replace");
        }

        protected override void DestroyWindowCore(HandleRef hwnd) {
            Shutdown();
            DestroyWindow(hwnd.Handle);
            window=IntPtr.Zero;
        }
        // mpv's video thread owns a child of our window, so keep answering its cross-thread
        // messages while the core stops, without dispatching posted WPF work.
        void Shutdown() {
            events.Stop();closed=true;
            var ctx=handle;handle=IntPtr.Zero;
            if(ctx==IntPtr.Zero)return;
            var stop=Task.Run(()=>mpv_terminate_destroy(ctx));
            var until=DateTime.UtcNow.AddSeconds(5);
            while(!stop.Wait(5) && DateTime.UtcNow<until) {
                NativeMessage message;
                PeekMessage(out message,IntPtr.Zero,0,0,0x400000); // PM_NOREMOVE | PM_QS_SENDMESSAGE
            }
        }

        void Drain() {
            while(handle!=IntPtr.Zero) {
                IntPtr e=mpv_wait_event(handle,0);
                int id=Marshal.ReadInt32(e);
                if(id==0)return;
                switch(id) {
                    case EventFileLoaded:OnFileLoaded();break;
                    case EventVideoReconfig:ReadVideo();break;
                    case EventPlaybackRestart:
                        SeekPending=false;
                        if(Restarted!=null)Restarted();
                        break;
                    case EventEndFile: {
                        IntPtr data=Marshal.ReadIntPtr(e,16);
                        int reason=data==IntPtr.Zero?0:Marshal.ReadInt32(data);
                        int error=data==IntPtr.Zero?0:Marshal.ReadInt32(data,4);
                        if(reason==EndReasonError)Fail(ErrorText(error));
                        else if(!opened)Fail("mpv could not open this video.");
                        break;
                    }
                    case EventShutdown:events.Stop();return;
                }
            }
        }
        void OnFileLoaded() {
            if(opened)return;
            long track;
            if(mpv_get_property(handle,Utf8("current-tracks/video/id"),FormatInt64,out track)<0){Fail("This file has no video track.");return;}
            double seconds;
            if(mpv_get_property(handle,Utf8("duration"),FormatDouble,out seconds)<0 || !(seconds>0)){Fail("mpv could not read the video length.");return;}
            bool audio=mpv_get_property(handle,Utf8("current-tracks/audio/id"),FormatInt64,out track)>=0;
            opened=true;
            ReadVideo();
            if(Opened!=null)Opened(seconds*1000,audio);
        }
        void ReadVideo() {
            long width,height,rotate;
            // The picture as it leaves mpv's filters: a turn that is not a quarter turn is done there and changes
            // the size, while a quarter turn is left to the screen and only reported.
            string from="video-out-params";
            if(!ReadSize(from,out width,out height)) {
                from="video-params";
                if(!ReadSize(from,out width,out height))return;
            }
            if(mpv_get_property(handle,Utf8(from+"/rotate"),FormatInt64,out rotate)<0)rotate=0;
            Aspect=rotate%180==90?(double)height/width:(double)width/height;
            PixelWidth=(int)(rotate%180==90?height:width);PixelHeight=(int)(rotate%180==90?width:height);
            if(uprightAspect<=0 && turn==0)uprightAspect=Aspect; // kept: a late report can still carry an older turn
            if(VideoChanged!=null)VideoChanged();
        }
        bool ReadSize(string from,out long width,out long height) {
            height=0;
            return mpv_get_property(handle,Utf8(from+"/dw"),FormatInt64,out width)>=0 &&
                mpv_get_property(handle,Utf8(from+"/dh"),FormatInt64,out height)>=0 && width>0 && height>0;
        }
        public int PixelWidth {get;private set;}
        public int PixelHeight {get;private set;}
        // Degrees clockwise, on top of the video's own orientation. Only quarter turns work while the frames stay
        // on the graphics card, so any other angle brings them to memory first (heavier, but it turns).
        // Switching that costs a stutter, so it happens once when the picture first tilts and stays through the
        // quarter turns on the way, until the picture is upright again.
        int turn;
        bool copying;
        double uprightAspect;
        // Width over height before any O / P turn.
        internal double UprightAspect {get {return uprightAspect>0?uprightAspect:Aspect;}}
        internal int Turn {get {return turn;} set {
            if(value==turn)return;
            turn=value;
            if(value%90!=0 && !copying){copying=true;Property("hwdec","auto-copy-safe");}
            else if(value==0 && copying){copying=false;Property("hwdec","auto-safe");}
            Property("video-rotate",value.ToString(Inv));
        }}
        // The E / W brightness (a Light value: level and style) as a small shader on the video, the same curve as
        // everywhere else. It is not in screenshot-raw / screenshot "video": those pictures get the curve from Light afterwards.
        int brightness;AdjustValues look;string shaderList="";
        internal int Brightness {set {
            if(value==brightness || handle==IntPtr.Zero)return;
            brightness=value;ApplyShaders();
        }}
        // Contrast, saturation, color temperature and sharpness while watching (MpvAdjust.cs). Brightness comes from above.
        internal AdjustValues Look {set {
            value.Brightness=0;
            if(value==look || handle==IntPtr.Zero)return;
            look=value;ApplyShaders();
        }}
        void ApplyShaders() {
            var files=new List<string>();var options=new List<string>();
            try {
                if(brightness!=0){files.Add(Light.MpvShader());options.Add(Light.MpvOptions(brightness));}
                var more=MpvAdjust.Files(look);
                if(more.Count>0){files.AddRange(more);options.Add(MpvAdjust.Options(look));}
            } catch(System.IO.IOException) {} catch(UnauthorizedAccessException) {}
            if(options.Count>0)Property("glsl-shader-opts",String.Join(",",options.ToArray()));
            string list=String.Join(";",files.ToArray());
            if(list!=shaderList){shaderList=list;Property("glsl-shaders",list);}
        }
        // Turned or resized, the player's window is the whole view instead of just the picture, and mpv itself
        // sizes and moves the picture inside it. A window that changed size with every step would flicker.
        // zoom: how big the picture is against mpv's own fit (below 1 is smaller). panX and panY are fractions of that size.
        bool zoomed;
        internal bool Fill {get {return zoomed || turn!=0;}}
        string view="";
        internal void SetView(double zoom,double panX,double panY) {
            zoomed=Math.Abs(zoom-1)>0.0001;
            string scale=Math.Log(Math.Max(0.01,zoom),2).ToString("0.####",Inv),x=panX.ToString("0.#####",Inv),y=panY.ToString("0.#####",Inv);
            if(view==scale+"|"+x+"|"+y)return;
            view=scale+"|"+x+"|"+y;
            Property("video-zoom",scale);Property("video-pan-x",x);Property("video-pan-y",y);
        }
        void Fail(string message) {
            if(closed)return;
            closed=true;events.Stop();
            if(Failed!=null)Failed(message);
        }
        static string ErrorText(int code) {
            IntPtr text=code<0?mpv_error_string(code):IntPtr.Zero;
            return "mpv: "+(text==IntPtr.Zero?"playback error":Marshal.PtrToStringAnsi(text));
        }

        public double Position {
            get {
                double seconds;
                return handle!=IntPtr.Zero && mpv_get_property(handle,Utf8("time-pos"),FormatDouble,out seconds)>=0?seconds*1000:Double.NaN;
            }
        }
        // Diagnostics, such as "hwdec-current" or "frame-drop-count".
        internal string Read(string name) {
            return handle==IntPtr.Zero?null:ReadFrom(handle,name);
        }
        static string ReadFrom(IntPtr ctx,string name) {
            IntPtr text=mpv_get_property_string(ctx,Utf8(name));
            if(text==IntPtr.Zero)return null;
            try {
                int length=0;
                while(Marshal.ReadByte(text,length)!=0)length++;
                var bytes=new byte[length];Marshal.Copy(text,bytes,0,length);
                return Encoding.UTF8.GetString(bytes);
            } finally {mpv_free(text);}
        }
        // The frame on screen, straight from memory (4K took under 0.05 s when measured), already at the player's turn.
        // Null if it can't be had. Used to hold the picture still while it is being turned.
        internal BitmapSource GrabFrame() {return Grab(handle);}
        // The decoder's picture at the current time, from any mpv instance (the GIF strip's hidden ones too).
        internal static BitmapSource Grab(IntPtr handle) {
            if(handle==IntPtr.Zero)return null;
            byte[] name=Utf8("screenshot-raw"),mode=Utf8("video");
            var pinName=GCHandle.Alloc(name,GCHandleType.Pinned);var pinMode=GCHandle.Alloc(mode,GCHandleType.Pinned);
            IntPtr node=Marshal.AllocHGlobal(16);
            try {
                if(mpv_command_ret(handle,new[]{pinName.AddrOfPinnedObject(),pinMode.AddrOfPinnedObject(),IntPtr.Zero},node)<0)return null;
                try {
                    IntPtr list=Marshal.ReadIntPtr(node);
                    int count=Marshal.ReadInt32(list);
                    IntPtr values=Marshal.ReadIntPtr(list,8),keys=Marshal.ReadIntPtr(list,16),data=IntPtr.Zero;
                    long width=0,height=0,stride=0,size=0;string format=null;
                    for(int i=0;i<count;i++) {
                        string key=Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(keys,i*IntPtr.Size));
                        IntPtr value=values+i*16;
                        if(key=="w")width=Marshal.ReadInt64(value);
                        else if(key=="h")height=Marshal.ReadInt64(value);
                        else if(key=="stride")stride=Marshal.ReadInt64(value);
                        else if(key=="format")format=Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(value));
                        else if(key=="data") {
                            IntPtr bytes=Marshal.ReadIntPtr(value);
                            data=Marshal.ReadIntPtr(bytes);size=Marshal.ReadInt64(bytes,8);
                        }
                    }
                    if(format!="bgr0" || data==IntPtr.Zero || width<=0 || height<=0 || stride<width*4 || size<stride*height)return null;
                    var frame=BitmapSource.Create((int)width,(int)height,96,96,PixelFormats.Bgr32,null,data,(int)(stride*height),(int)stride);
                    frame.Freeze();
                    return frame;
                } finally {mpv_free_node_contents(node);}
            } catch(OutOfMemoryException) {return null;}
            finally {Marshal.FreeHGlobal(node);pinName.Free();pinMode.Free();}
        }
        // The video's own picture at full size, as a PNG or (when the name ends in .jpg) a JPG of the given quality.
        // Light PNG compression keeps playback from stalling on 4K frames.
        internal bool Screenshot(string file,int jpgQuality) {
            Property("screenshot-png-compression","1");
            if(jpgQuality>0)Property("screenshot-jpeg-quality",jpgQuality.ToString(Inv));
            return Command("screenshot-to-file",file,"video")>=0 && System.IO.File.Exists(file);
        }
        // Shows this subtitle file in place of the one added before. mpv reads it fully here, so the file can go afterwards.
        string addedSubtitle;
        internal bool ShowSubtitle(string file) {
            if(addedSubtitle!=null){Command("sub-remove",addedSubtitle);addedSubtitle=null;}
            if(file==null)return true;
            if(Command("sub-add",file,"select")<0)return false;
            addedSubtitle=Read("sid");
            return true;
        }
        internal bool SubtitlesVisible {set {Property("sub-visibility",value?"yes":"no");}}
        // 100 is the usual place at the bottom; a smaller number is higher up.
        int subtitlePosition=100;
        internal int SubtitlePosition {set {
            if(value==subtitlePosition)return;
            subtitlePosition=value;Property("sub-pos",value.ToString(Inv));
        }}
        // 1 is the usual size. It can be set before the player starts and changed while it plays.
        double subtitleScale=1;
        internal double SubtitleScale {set {
            subtitleScale=Math.Max(0.4,Math.Min(4,value));
            Property("sub-scale",subtitleScale.ToString("0.###",Inv));
        }}
        // A subtitle track inside the video file itself.
        internal bool HasOwnSubtitle {get {
            int count;
            if(!Int32.TryParse(Read("track-list/count"),out count))return false;
            for(int i=0;i<count;i++)
                if(Read("track-list/"+i+"/type")=="sub" && Read("track-list/"+i+"/external")!="yes")return true;
            return false;
        }}
        public bool Paused {set {Property("pause",value?"yes":"no");}}
        public double Volume {set {Property("volume",Math.Max(0,Math.Min(100,value)).ToString("0.###",Inv));}}
        public double Speed {set {Property("speed",value.ToString("0.###",Inv));}}
        // The speaker to play on; empty is the Windows default. mpv moves the sound over while playing.
        string audioDevice="auto";
        internal string AudioDevice {set {
            audioDevice=String.IsNullOrEmpty(value)?"auto":value;
            Property("audio-device",audioDevice);
        }}
        internal bool HasAudioDevice(string device) {
            int count;
            if(!Int32.TryParse(Read("audio-device-list/count"),out count))return false;
            for(int i=0;i<count;i++)if(Read("audio-device-list/"+i+"/name")==device)return true;
            return false;
        }
        // The speakers Windows has now, with their names. Each is (device, name).
        static List<Tuple<string,string>> speakers;
        static DateTime speakersRead;
        internal static List<Tuple<string,string>> Speakers() {
            if(speakers!=null && (DateTime.UtcNow-speakersRead).TotalSeconds<3)return speakers;
            var list=new List<Tuple<string,string>>();
            if(Available) {
                IntPtr ctx=mpv_create();
                if(ctx!=IntPtr.Zero) {
                    try {
                        foreach(var name in new[]{"config","terminal","load-scripts","ytdl"})mpv_set_option_string(ctx,Utf8(name),Utf8("no"));
                        if(mpv_initialize(ctx)>=0) {
                            int count;
                            if(Int32.TryParse(ReadFrom(ctx,"audio-device-list/count"),out count))
                                for(int i=0;i<count;i++) {
                                    string device=ReadFrom(ctx,"audio-device-list/"+i+"/name");
                                    if(device==null || !device.StartsWith("wasapi/",StringComparison.Ordinal))continue;
                                    list.Add(Tuple.Create(device,ReadFrom(ctx,"audio-device-list/"+i+"/description")??device));
                                }
                        }
                    } finally {mpv_terminate_destroy(ctx);}
                }
            }
            speakers=list;speakersRead=DateTime.UtcNow;
            return list;
        }
        // Clear voice: trims low rumble, lifts the range that carries speech, and raises quiet speech.
        // The level (gentle, normal, strong) sets how far quiet speech may be raised.
        // strength 1..100. The old three levels stay where they were: 25 gentle (e=3), 50 normal (e=6.25), 100 strong (e=12.5);
        // in between it goes in a straight line, from e=1 at 0. The 3 kHz lift grows to +3 dB by 50 and stays there.
        // (2026-11-27 debate: DeepSeek and Gemini; 0 = off is the N key.)
        public bool SetClearVoice(bool on,int strength) {
            double s=Math.Max(1,Math.Min(100,strength));
            double raise=s<=25?1+2*s/25:s<=50?3+3.25*(s-25)/25:6.25+6.25*(s-50)/50;
            double lift=Math.Min(3,3*s/50);
            string filter=on?"lavfi=[highpass=f=90,equalizer=f=3000:t=q:w=1:g="+lift.ToString("0.##",Inv)+
                ",speechnorm=p=0.9:e="+raise.ToString("0.###",Inv)+":r=0.00001:l=1]":"";
            return handle!=IntPtr.Zero && mpv_set_property_string(handle,Utf8("af"),Utf8(filter))>=0;
        }
        // Exact seeks land on the first frame that starts at or after the target.
        public void Seek(double ms,bool exact=true) {
            SeekPending=true;
            Command("seek",(Math.Max(0,ms)/1000).ToString("0.000000",Inv),exact?"absolute+exact":"absolute+keyframes");
        }

        // Steps one decoded frame while paused. Stepping back is a seek and reports a restart.
        public void Step(int direction) {
            if(direction<0)SeekPending=true;
            Command(direction<0?"frame-back-step":"frame-step");
        }

        void Option(string name,string value) {Check(mpv_set_option_string(handle,Utf8(name),Utf8(value)),name);}
        void Property(string name,string value) {if(handle!=IntPtr.Zero)mpv_set_property_string(handle,Utf8(name),Utf8(value));}
        int Command(params string[] args) {
            if(handle==IntPtr.Zero)return -1;
            var pointers=new IntPtr[args.Length+1];
            try {
                for(int i=0;i<args.Length;i++) {
                    byte[] bytes=Utf8(args[i]);
                    pointers[i]=Marshal.AllocHGlobal(bytes.Length);
                    Marshal.Copy(bytes,0,pointers[i],bytes.Length);
                }
                return mpv_command(handle,pointers);
            } finally {
                foreach(var p in pointers)if(p!=IntPtr.Zero)Marshal.FreeHGlobal(p);
            }
        }
        static void Check(int result,string what) {
            if(result<0)throw new InvalidOperationException(ErrorText(result)+" ("+what+")");
        }
        static byte[] Utf8(string s) {
            var bytes=Encoding.UTF8.GetBytes(s);
            Array.Resize(ref bytes,bytes.Length+1);
            return bytes;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct NativeMessage {public IntPtr hwnd;public uint message;public IntPtr wParam,lParam;public uint time;public int x,y;}
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr LoadLibrary(string file);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr CreateWindowEx(int exStyle,string className,string name,int style,
            int x,int y,int width,int height,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr param);
        [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool PeekMessage(out NativeMessage message,IntPtr hwnd,uint min,uint max,uint remove);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] static extern IntPtr mpv_create();
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] static extern int mpv_initialize(IntPtr ctx);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] static extern void mpv_terminate_destroy(IntPtr ctx);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] static extern IntPtr mpv_error_string(int error);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] static extern int mpv_set_option_string(IntPtr ctx,byte[] name,byte[] value);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] static extern int mpv_set_property_string(IntPtr ctx,byte[] name,byte[] value);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] static extern int mpv_get_property(IntPtr ctx,byte[] name,int format,out double data);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] static extern int mpv_get_property(IntPtr ctx,byte[] name,int format,out long data);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] static extern int mpv_command(IntPtr ctx,IntPtr[] args);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] static extern int mpv_command_ret(IntPtr ctx,IntPtr[] args,IntPtr result);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] static extern void mpv_free_node_contents(IntPtr node);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] static extern IntPtr mpv_get_property_string(IntPtr ctx,byte[] name);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] static extern void mpv_free(IntPtr data);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] static extern IntPtr mpv_wait_event(IntPtr ctx,double timeout);
    }

    // Fast timeline thumbnails from a hidden mpv instance that keeps the file open and renders
    // the nearest keyframe in software at thumbnail size. Use from one thread at a time.
    internal sealed class MpvThumbnails : IDisposable {
        const string Library="libmpv-2.dll";
        const int FormatInt64=4,EventFileLoaded=8,EventEndFile=7,EventPlaybackRestart=21;
        static readonly CultureInfo Inv=CultureInfo.InvariantCulture;
        readonly object gate=new object();
        IntPtr handle,render;
        int width,height;

        public static MpvThumbnails TryOpen(string path,CancellationToken token) {
            if(!MpvPlayer.Available)return null;
            var thumbs=new MpvThumbnails();
            try {return thumbs.Open(path,token)?thumbs:Dispose(thumbs);}
            catch(Exception){return Dispose(thumbs);}
        }
        static MpvThumbnails Dispose(MpvThumbnails thumbs){thumbs.Dispose();return null;}

        bool Open(string path,CancellationToken token) {
            handle=mpv_create();
            if(handle==IntPtr.Zero)return false;
            foreach(var option in new[]{"config=no","load-scripts=no","osc=no","ytdl=no","terminal=no","vo=libmpv","ao=null",
                "aid=no","sid=no","pause=yes","hr-seek=no","hwdec=auto-copy-safe","video-latency-hacks=yes","idle=yes","keep-open=yes"}) {
                int split=option.IndexOf('=');
                mpv_set_option_string(handle,Utf8(option.Substring(0,split)),Utf8(option.Substring(split+1)));
            }
            if(mpv_initialize(handle)<0)return false;
            var type=Marshal.StringToHGlobalAnsi("sw");
            try {
                var create=new RenderParam[]{new RenderParam{type=1,data=type},new RenderParam()};
                if(mpv_render_context_create(out render,handle,create)<0){render=IntPtr.Zero;return false;}
            } finally {Marshal.FreeHGlobal(type);}
            Command("loadfile",path,"replace");
            if(!WaitFor(EventFileLoaded,token,10))return false;
            long w,h,rotate=0;
            var deadline=DateTime.UtcNow.AddSeconds(10);
            while(mpv_get_property(handle,Utf8("video-params/dw"),FormatInt64,out w)<0 ||
                  mpv_get_property(handle,Utf8("video-params/dh"),FormatInt64,out h)<0) {
                if(token.IsCancellationRequested || DateTime.UtcNow>deadline)return false;
                Thread.Sleep(10);
            }
            mpv_get_property(handle,Utf8("video-params/rotate"),FormatInt64,out rotate);
            if(rotate%360!=0 || w<=0 || h<=0)return false; // the software renderer does not rotate
            width=(int)w;height=(int)h;
            return true;
        }
        bool WaitFor(int wanted,CancellationToken token,double seconds) {
            var deadline=DateTime.UtcNow.AddSeconds(seconds);
            while(!token.IsCancellationRequested && DateTime.UtcNow<deadline) {
                IntPtr e=mpv_wait_event(handle,0.05);
                int id=Marshal.ReadInt32(e);
                if(id==wanted)return true;
                if(id==EventEndFile)return false;
            }
            return false;
        }

        // exact: decode up to the requested time instead of stopping at the nearest keyframe (slower, but every picture differs).
        public BitmapSource Read(double ms,int maxSide,CancellationToken token,bool exact=false) {
            lock(gate) {
                if(handle==IntPtr.Zero || render==IntPtr.Zero)throw new ObjectDisposedException("MpvThumbnails");
                // Discard a restart left over from a canceled request.
                while(Marshal.ReadInt32(mpv_wait_event(handle,0))!=0){}
                Command("seek",(Math.Max(0,ms)/1000).ToString("0.000",Inv),exact?"absolute+exact":"absolute+keyframes");
                if(!WaitFor(EventPlaybackRestart,token,5)) {
                    token.ThrowIfCancellationRequested();
                    throw new TimeoutException("Thumbnail seek timed out.");
                }
                // The renderer can still hold the last read's picture right after a quick seek (a scene from the
                // other side of the GIF strip, kept for good as the exact one). The exact picture is therefore
                // taken from the decoder itself, which always has the picture at the new time.
                if(exact)return Smaller(MpvPlayer.Grab(handle),maxSide);
                // The new frame reaches the renderer shortly after the seek finishes.
                var deadline=DateTime.UtcNow.AddMilliseconds(300);
                while((mpv_render_context_update(render)&1)==0 && DateTime.UtcNow<deadline)Thread.Sleep(4);
                double scale=Math.Min(1,Math.Min(maxSide/(double)width,maxSide/(double)height));
                int w=Math.Max(2,(int)Math.Round(width*scale)),h=Math.Max(2,(int)Math.Round(height*scale));
                int stride=(w*4+63)/64*64;
                IntPtr raw=Marshal.AllocHGlobal(stride*h+64),size=Marshal.AllocHGlobal(8),format=Marshal.StringToHGlobalAnsi("bgr0"),strideBox=Marshal.AllocHGlobal(IntPtr.Size);
                try {
                    var pixels=new IntPtr((raw.ToInt64()+63)/64*64);
                    Marshal.WriteInt32(size,w);Marshal.WriteInt32(size,4,h);
                    Marshal.WriteIntPtr(strideBox,new IntPtr(stride));
                    var p=new RenderParam[]{
                        new RenderParam{type=17,data=size},new RenderParam{type=18,data=format},
                        new RenderParam{type=19,data=strideBox},new RenderParam{type=20,data=pixels},new RenderParam()};
                    if(mpv_render_context_render(render,p)<0)throw new InvalidOperationException("mpv could not render a thumbnail.");
                    var image=BitmapSource.Create(w,h,96,96,PixelFormats.Bgr32,null,pixels,stride*h,stride);
                    image.Freeze();
                    return image;
                } finally {
                    Marshal.FreeHGlobal(raw);Marshal.FreeHGlobal(size);Marshal.FreeHGlobal(format);Marshal.FreeHGlobal(strideBox);
                }
            }
        }

        static BitmapSource Smaller(BitmapSource full,int maxSide) {
            if(full==null)throw new InvalidOperationException("mpv could not hand over the picture.");
            double scale=Math.Min(1,Math.Min(maxSide/(double)full.PixelWidth,maxSide/(double)full.PixelHeight));
            if(scale>=1)return full;
            var small=new WriteableBitmap(new TransformedBitmap(full,new ScaleTransform(scale,scale)));
            small.Freeze();
            return small;
        }

        public void Dispose() {
            lock(gate) {
                if(render!=IntPtr.Zero){mpv_render_context_free(render);render=IntPtr.Zero;}
                if(handle!=IntPtr.Zero){mpv_terminate_destroy(handle);handle=IntPtr.Zero;}
            }
        }

        void Command(params string[] args) {
            var pointers=new IntPtr[args.Length+1];
            try {
                for(int i=0;i<args.Length;i++) {
                    byte[] bytes=Utf8(args[i]);
                    pointers[i]=Marshal.AllocHGlobal(bytes.Length);
                    Marshal.Copy(bytes,0,pointers[i],bytes.Length);
                }
                mpv_command(handle,pointers);
            } finally {foreach(var p in pointers)if(p!=IntPtr.Zero)Marshal.FreeHGlobal(p);}
        }
        static byte[] Utf8(string s) {
            var bytes=Encoding.UTF8.GetBytes(s);
            Array.Resize(ref bytes,bytes.Length+1);
            return bytes;
        }
        [StructLayout(LayoutKind.Sequential)] struct RenderParam {public int type;public IntPtr data;}
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] static extern IntPtr mpv_create();
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] static extern int mpv_initialize(IntPtr ctx);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] static extern void mpv_terminate_destroy(IntPtr ctx);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] static extern int mpv_set_option_string(IntPtr ctx,byte[] name,byte[] value);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] static extern int mpv_get_property(IntPtr ctx,byte[] name,int format,out long data);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] static extern int mpv_command(IntPtr ctx,IntPtr[] args);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] static extern IntPtr mpv_wait_event(IntPtr ctx,double timeout);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] static extern int mpv_render_context_create(out IntPtr res,IntPtr ctx,RenderParam[] parameters);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] static extern ulong mpv_render_context_update(IntPtr ctx);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] static extern int mpv_render_context_render(IntPtr ctx,RenderParam[] parameters);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] static extern void mpv_render_context_free(IntPtr ctx);
    }

    // A transparent window that floats over the native video so WPF controls stay visible above it.
    internal sealed class VideoOverlay : Window {
        readonly Window owner;
        readonly FrameworkElement target;
        Rect placed;

        public VideoOverlay(Window owner,FrameworkElement target) {
            this.owner=owner;this.target=target;
            WindowStyle=WindowStyle.None;AllowsTransparency=true;Background=System.Windows.Media.Brushes.Transparent;
            ShowInTaskbar=false;ShowActivated=false;ResizeMode=ResizeMode.NoResize;Owner=owner;
            UseLayoutRounding=true;
            // Keys pressed while a control here has focus belong to the viewer.
            PreviewKeyDown+=(s,e)=>{
                var key=e.Key==System.Windows.Input.Key.ImeProcessed?e.ImeProcessedKey:e.Key;
                if(key==System.Windows.Input.Key.System)key=e.SystemKey;
                // The copy below can't say "held down", so a held key doesn't repeat a once-per-press job here.
                if(e.IsRepeat && ViewerKeys.OncePerPress(ViewerKeys.ActionFor(key))){e.Handled=true;return;}
                var forwarded=new System.Windows.Input.KeyEventArgs(e.KeyboardDevice,PresentationSource.FromVisual(owner),e.Timestamp,key) {
                    RoutedEvent=System.Windows.Input.Keyboard.PreviewKeyDownEvent};
                owner.RaiseEvent(forwarded);
                if(forwarded.Handled)e.Handled=true;
            };
            owner.LocationChanged+=delegate {Follow();};
            owner.SizeChanged+=delegate {Follow();};
            owner.StateChanged+=delegate {Follow();};
            target.LayoutUpdated+=delegate {Follow();};
        }
        internal void Follow() {
            if(!IsVisible || PresentationSource.FromVisual(target)==null)return;
            var source=PresentationSource.FromVisual(owner);
            if(source==null || source.CompositionTarget==null)return;
            var topLeft=source.CompositionTarget.TransformFromDevice.Transform(target.PointToScreen(new Point(0,0)));
            var rect=new Rect(topLeft,new Size(target.ActualWidth,target.ActualHeight));
            if(rect==placed)return;
            placed=rect;
            Left=rect.Left;Top=rect.Top;Width=rect.Width;Height=rect.Height;
        }
        internal void Open() {
            if(!IsVisible){placed=Rect.Empty;Show();}
            Follow();
        }
    }
}
