using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace LoveMyShelf {
    internal enum PhotoSort { Name, Modified, CoverOrder, Size, Type }
    // Where Save cover writes the JPG. The cover folder is always kept as the fallback.
    internal enum CoverPlace { CoverFolder, BesideOriginal, Subfolder }
    // How GIF covers move in the grid.
    internal enum GifPlayback { Never, Hover, Always }
    // What Copy, Share and dragging a tile hand over.
    internal enum ShareContent { CoverAndOriginal, CoverOnly, OriginalOnly }
    internal enum ShareCoverKind { Both, MovingOnly, StillOnly }

    internal sealed class Preferences {
        static int editorPreviewSide=2200;
        internal static int EditorPreviewSide {get {return editorPreviewSide;}}
        internal void ApplyEditorPreviewSide() {editorPreviewSide=GifLargeScenes?3840:2200;}
        // GraphicsCards chooses the adapter index. Read its display name for the GIF options.
        static readonly Lazy<string> gifGraphicsCard=new Lazy<string>(ReadGifGraphicsCard);
        internal static string GifGraphicsCard {get {return gifGraphicsCard.Value;}}
        [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(ref Guid id,out IntPtr factory);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int EnumGifAdapter(IntPtr factory,uint index,out IntPtr adapter);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ReadGifAdapter(IntPtr adapter,IntPtr description);
        static string ReadGifGraphicsCard() {
            IntPtr factory=IntPtr.Zero,adapter=IntPtr.Zero,description=IntPtr.Zero;
            try {
                var id=new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
                if(CreateDXGIFactory1(ref id,out factory)<0 || factory==IntPtr.Zero)return null;
                var enumerate=(EnumGifAdapter)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(factory),12*IntPtr.Size),typeof(EnumGifAdapter));
                if(enumerate(factory,(uint)GraphicsCards.Fastest(),out adapter)<0 || adapter==IntPtr.Zero)return null;
                var describe=(ReadGifAdapter)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(adapter),10*IntPtr.Size),typeof(ReadGifAdapter));
                // DXGI_ADAPTER_DESC1 starts with 128 wide characters. The flags follow the IDs, memory sizes and LUID.
                int flagsOffset=128*2+4*4+3*IntPtr.Size+8;
                description=Marshal.AllocHGlobal(flagsOffset+IntPtr.Size);
                if(describe(adapter,description)<0 || (Marshal.ReadInt32(description,flagsOffset)&2)!=0)return null;
                return Marshal.PtrToStringUni(description);
            } catch(Exception) {return null;}
            finally {
                if(description!=IntPtr.Zero)Marshal.FreeHGlobal(description);
                if(adapter!=IntPtr.Zero)Marshal.Release(adapter);
                if(factory!=IntPtr.Zero)Marshal.Release(factory);
            }
        }
        internal const string SubfolderName="Love My Shelf 표지";
        public bool Korean, Descending;
        // False until the person picks a language on the first launch.
        public bool LanguageChosen, UsageNoticeShown;
        // Shown once, the first time the folder tabs don't all fit: how to see the hidden ones.
        public bool TabScrollTipShown;
        // Subfolders show as folder tiles before the files (like File Explorer); off keeps only the files.
        public bool ShowSubfolders;
        // The folder tree on the left (like File Explorer's navigation pane): shown or not, its width, and the
        // top-level groups the person folded ("lms", "onedrive", "pc", "network").
        public bool TreeOpen;
        // The folders area (Show the folders inside a folder): File Explorer's view (see FolderView) and whether it sits below the photos.
        public int FolderView=1;
        public bool FoldersBelow,FoldersFolded;
        // Right-button range selection: how many times it was used (the hint stays until 3) and whether its notice was shown.
        public int MarqueeUses;
        public bool MarqueeTipShown;
        public bool TreeIntroShown;
        public int TreeWidth=220;
        public string TreeFolded="pc network";
        public string CoverDirectory="";
        public CoverPlace CoverPlace;
        public ThemeMode Theme;
        public bool CheckUpdates=true;
        // Files dropped on another drive without Ctrl or Shift: 0 ask, 1 copy (as File Explorer does), 2 move.
        public int DropAcross;
        // After a file is copied to another place, read the copy back and compare it with the original (takes about twice as long).
        public bool VerifyCopies;
        // How many times the notice that points at the queue button in the bottom bar has been shown (it stops after 3).
        public int QueueTips;
        public bool ExplorerMenu=true;   // on unless turned off; "Open with LoveMyShelf" in File Explorer's right-click menu
        public GifPlayback GridGif=GifPlayback.Hover;
        public ShareContent Share;
        public ShareCoverKind ShareCovers;
        // Discord's free upload limit went from 10 MB to 20 MB in August 2026.
        public int ShareLimitMB=20;
        public int GifStripSeconds=4;
        // Starting choices in Make GIF cover (the window remembers changes until LoveMyShelf closes).
        // The defaults changed on 2026-11-24 (see MoveToNewGifDefaults): WebP, high, 30 a second, 1280 px,
        // 1 s before and after, a 4 s strip, the real file only when asked, the preview every 0.05 s.
        public int GifFormat=1;          // 0 GIF, 1 WebP, 2 MP4
        public int GifQuality;           // 0 high, 1 normal, 2 smaller file
        public int GifFps=30;
        public int GifHeight=1280;
        public bool GifHighFps,GifLargeSize,GifLargeScenes;
        // True for one start: the options file was written before the defaults changed, so a notice says they did.
        internal bool GifDefaultsChanged;
        public int SceneStepMs=200;      // scene step: milliseconds between scenes everywhere a scene is picked (what used to be "frames")
        public int GifPreviewStepMs=50;  // Make GIF cover's preview only: milliseconds between the scenes it reads
        public int GifSideTenths=10;    // seconds before and after the cover's moment, in tenths
        // Make GIF cover's preview. Each person picks how it works (Guide & Options → Making GIF covers).
        public bool GifAutoMake;         // true: the real file is made in the background after every change; false: only when asked
        public int GifMakeDelayMs=700;   // how long the handles must rest before the real file starts being made
        public int WebpEffort;           // 0 smallest file (slow), 1 faster, 2 fastest (bigger file)
        public bool UpscaleGpu;          // upscaling on the graphics card (DirectML) instead of the processor
        public int SubtitleScale=100;    // subtitle size in the large view, in percent
        internal static string SuggestedCoverParent {get {return Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);}}
        public PhotoSort Sort;
        public int SkipSeconds=5;
        public int BrightnessStep=3;     // how much one press of E or W changes the brightness
        public int RotationStep=10;      // how many degrees one press of O or P turns the picture in the large view
        public int TileNames;            // file names under the tiles: 0 none, 1 one line, 2 two lines, 3 the whole name
        public bool GifHoverSound=true;  // pointing at a tile also plays the sound of an MP4 cover made with sound
        public bool CoverTimes;          // show where a cover's scene is in the video ("12:34") on its tile
        // The shape of the grid's cells (see Covers.Shapes): 0 = 9:16, 1 = 3:4, 2 = 1:1. A new install starts at 3:4 and
        // picks on the first screen; an options file from before 1.0.9 has none and keeps 9:16, as it always was.
        public int TileShape=Covers.DefaultShape;
        // A cover clearly wider than a cell takes two cells side by side (on unless turned off).
        public bool WideCovers=true;
        public bool FillWideGaps=true;
        // Make cover: dragging the frame keeps its shape (off unless turned on; remembered).
        public bool CropLock;
        // Make cover: the picture zooms in and out with the frame (the old way). Off unless turned on: the picture stays still.
        public bool CropZoom;
        // The shape kept with "Keep this shape" (width over height), so the next cover starts in it; 0 = none yet (the grid's cell).
        public double CropShape;
        internal double KeptShape {get {return CropShape>=Covers.Narrowest && CropShape<=Covers.Widest?CropShape:Covers.Shape(TileShape);}}
        public string CaptureDirectory="";  // where the / key saves a frame; empty means the default folder below
        public bool CaptureAsShown;      // false: the video's own frame; true: the size and E/W brightness on screen
        public bool CaptureJpg;          // false: PNG; true: JPG
        public string SeenVersion="";    // the version last started, for the one small "updated" notice
        public string SubtitleBounds=""; // where the subtitle explorer was last: left,top,width,height
        public string ViewerBounds="";   // where the large view closed last was: left,top,width,height,maximized (0 or 1)
        internal const string CaptureFolderName="Love My Shelf 캡처";
        internal string CaptureFolder {get {
            return String.IsNullOrWhiteSpace(CaptureDirectory)?Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),CaptureFolderName):CaptureDirectory;
        }}
        public int Volume=100;           // the Windows default speaker's volume, and the start for a speaker not used yet
        // The speaker for each monitor (by the monitor's ID), and the volume last used on each speaker.
        public Dictionary<string,SpeakerChoice> MonitorSpeakers=new Dictionary<string,SpeakerChoice>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string,int> SpeakerVolumes=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        internal int VolumeFor(string speaker) {
            int value;
            return !String.IsNullOrEmpty(speaker) && SpeakerVolumes.TryGetValue(speaker,out value)?value:Volume;
        }
        public bool ClearVoice;
        public int ClearVoiceLevel=1;   // 0 gentle, 1 normal, 2 strong
        // The large view's keys the person changed in Advanced (action id → key); empty means all defaults. See ViewerKeys.
        public Dictionary<string,System.Windows.Input.Key> Shortcuts=new Dictionary<string,System.Windows.Input.Key>(StringComparer.Ordinal);
        public bool SnapEdges=true;
        public int WheelZoomPercent=7;
        public bool PinTopOnWheel=true;
        public bool OpenPhotoOriginal, OpenMotionOriginal;
        // True: one click selects a tile and two clicks open the large view (like File Explorer). False: one click opens it.
        public bool OpenByDoubleClick=DefaultOpenByDoubleClick;
        // The checks turn this off because most of them click a tile to open it.
        internal static bool DefaultOpenByDoubleClick=true;

        internal bool ShowCover(string path) {
            return !(AnimationFrames.Supports(path)?OpenMotionOriginal:OpenPhotoOriginal);
        }

        public static Preferences Load(string root) {
            try {
                var xml=XElement.Load(Path.Combine(root,"options.xml"));
                string sort=(string)xml.Attribute("sort"), direction=(string)xml.Attribute("direction");
                var editor=xml.Element("editor")??xml.Element("photos")??xml.Element("motion")??xml;
                string place=(string)xml.Attribute("coverPlace"),gif=(string)xml.Attribute("gridGif"),share=(string)xml.Attribute("share");
                string shareCovers=(string)xml.Attribute("shareCovers");
                string theme=(string)xml.Attribute("theme");
                var loaded=new Preferences {
                    Korean=(string)xml.Attribute("language")=="ko",
                    LanguageChosen=xml.Attribute("language")!=null,
                    UsageNoticeShown=(string)xml.Attribute("usageNotice")=="shown",
                    TabScrollTipShown=(string)xml.Attribute("tabScrollTip")=="shown",
                    ShowSubfolders=(string)xml.Attribute("subfolders")=="show",
                    TreeOpen=(string)xml.Attribute("tree")=="open",
                    FolderView=ReadNumber(xml,"folderView",0,7,1),
                    FoldersBelow=(string)xml.Attribute("folders")=="below",
                    FoldersFolded=(string)xml.Attribute("foldersFolded")=="true",
                    MarqueeUses=ReadNumber(xml,"marqueeUses",0,1000,0),
                    MarqueeTipShown=(string)xml.Attribute("marqueeTip")=="shown",
                    TreeIntroShown=(string)xml.Attribute("treeIntro")=="shown",
                    TreeWidth=ReadNumber(xml,"treeWidth",160,400,220),
                    TreeFolded=(string)xml.Attribute("treeFolded")??"pc network",
                    CoverDirectory=(string)xml.Attribute("coverDirectory")??"",
                    CoverPlace=place=="beside"?CoverPlace.BesideOriginal:place=="subfolder"?CoverPlace.Subfolder:CoverPlace.CoverFolder,
                    CheckUpdates=(string)xml.Attribute("checkUpdates")!="false",
                    ExplorerMenu=(string)xml.Attribute("explorerMenu")!="off",
                    DropAcross=IndexOr(new[]{"ask","copy","move"},(string)xml.Attribute("dropAcross"),0),
                    VerifyCopies=(string)xml.Attribute("verifyCopies")=="on",
                    QueueTips=ReadNumber(xml,"queueTips",0,100,0),
                    Theme=theme=="light"?ThemeMode.Light:theme=="dark"?ThemeMode.Dark:ThemeMode.System,
                    GridGif=gif=="never"?GifPlayback.Never:gif=="always"?GifPlayback.Always:GifPlayback.Hover,
                    Share=share=="cover"?ShareContent.CoverOnly:share=="original"?ShareContent.OriginalOnly:ShareContent.CoverAndOriginal,
                    ShareCovers=shareCovers=="moving"?ShareCoverKind.MovingOnly:shareCovers=="still"?ShareCoverKind.StillOnly:ShareCoverKind.Both,
                    ShareLimitMB=ReadNumber(xml,"shareLimitMB",1,2000,20),
                    GifStripSeconds=ReadNumber(xml,"gifStrip",2,120,4),
                    GifFormat=IndexOr(new[]{"gif","webp","mp4"},(string)xml.Attribute("gifFormat"),1),
                    GifQuality=IndexOr(new[]{"high","normal","small"},(string)xml.Attribute("gifQuality"),0),
                    GifFps=ReadNumber(xml,"gifFps",1,60,30),
                    GifHeight=ReadNumber(xml,"gifHeight",240,3840,1280),
                    GifHighFps=(string)xml.Attribute("gifHighFps")=="on",
                    GifLargeSize=(string)xml.Attribute("gifLargeSize")=="on",
                    GifLargeScenes=(string)xml.Attribute("gifLargeScenes")=="on",
                    GifSideTenths=ReadNumber(xml,"gifSide",1,300,10),
                    SceneStepMs=ReadNumber(xml,"sceneStep",30,2000,200),
                    GifPreviewStepMs=ReadPreviewStep(xml),
                    GifAutoMake=(string)xml.Attribute("gifReal")=="auto",
                    GifMakeDelayMs=ReadNumber(xml,"gifMakeDelay",200,10000,700),
                    WebpEffort=ReadNumber(xml,"webpEffort",0,2,0),
                    UpscaleGpu=(string)xml.Attribute("upscaleDevice")=="gpu",
                    SubtitleScale=ReadNumber(xml,"subtitleSize",40,400,100),
                    OpenPhotoOriginal=(string)xml.Attribute("largePhoto")=="original",
                    OpenMotionOriginal=(string)xml.Attribute("largeMotion")=="original",
                    OpenByDoubleClick=(string)xml.Attribute("openLarge")==null?DefaultOpenByDoubleClick:(string)xml.Attribute("openLarge")=="double",
                    Sort=sort=="date"?PhotoSort.Modified:sort=="cover"?PhotoSort.CoverOrder:sort=="size"?PhotoSort.Size:sort=="type"?PhotoSort.Type:PhotoSort.Name,
                    Descending=direction==null?sort=="date":direction=="desc",
                    SkipSeconds=ReadNumber(xml,"skipSeconds",1,300,5),
                    BrightnessStep=ReadNumber(xml,"brightnessStep",1,20,3),
                    RotationStep=ReadNumber(xml,"turnDegrees",1,45,10),
                    TileNames=ReadNumber(xml,"tileNames",0,3,0),
                    CoverTimes=(string)xml.Attribute("coverTimes")=="true",
                    TileShape=IndexOr(Covers.ShapeKeys,(string)xml.Attribute("tileShape"),0),
                    WideCovers=(string)xml.Attribute("wideCovers")!="off",
                    FillWideGaps=(string)xml.Attribute("wideGaps")!="keep",
                    CropLock=(string)xml.Attribute("cropLock")=="on",
                    CropZoom=(string)xml.Attribute("cropZoom")=="on",
                    CropShape=ReadShape(xml.Attribute("cropShape")),
                    GifHoverSound=(string)xml.Attribute("gifHoverSound")!="off",
                    CaptureDirectory=(string)xml.Attribute("captureDirectory")??"",
                    CaptureAsShown=(string)xml.Attribute("captureLook")=="shown",
                    CaptureJpg=(string)xml.Attribute("captureFormat")=="jpg",
                    SeenVersion=(string)xml.Attribute("seenVersion")??"",
                    SubtitleBounds=(string)xml.Attribute("subtitleBounds")??"",
                    ViewerBounds=(string)xml.Attribute("viewerBounds")??"",
                    Volume=ReadNumber(xml,"volume",0,100,100),
                    MonitorSpeakers=ReadMonitorSpeakers(xml),
                    SpeakerVolumes=ReadSpeakerVolumes(xml),
                    ClearVoice=(string)xml.Attribute("clearVoice")=="true",
                    ClearVoiceLevel=ReadNumber(xml,"clearVoiceLevel",0,2,1),
                    Shortcuts=ViewerKeys.Read(xml),
                    SnapEdges=(string)editor.Attribute("snapEdges")!="false",
                    WheelZoomPercent=ReadNumber(editor,"wheelZoomPercent",1,50,7),
                    PinTopOnWheel=(string)editor.Attribute("pinTop")!="false"
                };
                if(xml.Attribute("gifDefaults")==null)loaded.MoveToNewGifDefaults();
                return loaded;
            } catch(IOException) {} catch(UnauthorizedAccessException) {} catch(System.Xml.XmlException) {}
            return new Preferences();
        }
        // An options file from before the GIF cover defaults changed. A value still at its old default moves to the
        // new one, once; a value the person had changed to something else stays. (The real file only being made
        // when asked needs no step here: a file without "gifReal" already reads that way.)
        void MoveToNewGifDefaults() {
            if(GifFormat==0)GifFormat=1;
            if(GifQuality==1)GifQuality=0;
            if(GifFps==15)GifFps=30;
            if(GifHeight==960)GifHeight=1280;
            if(GifSideTenths==15)GifSideTenths=10;
            if(GifStripSeconds==10)GifStripSeconds=4;
            if(GifPreviewStepMs==100)GifPreviewStepMs=50;
            GifDefaultsChanged=true;
        }
        static int IndexOr(string[] names,string value,int fallback) {
            int index=Array.IndexOf(names,value);return index>=0?index:fallback;
        }
        static int ReadNumber(XElement xml,string name,int min,int max,int fallback) {
            int value;
            return Int32.TryParse((string)xml.Attribute(name),out value) && value>=min && value<=max?value:fallback;
        }
        // 0.2 s was the first default. It moved to 0.1 s once; files saved since carry the mark, so a later choice of 0.2 s stays.
        // (0.1 s then moved to 0.05 s with the other GIF defaults: MoveToNewGifDefaults.)
        static int ReadPreviewStep(XElement xml) {
            int value=ReadNumber(xml,"gifPreviewStep",30,2000,50);
            return value==200 && xml.Attribute("gifPreviewDefault")==null?100:value;
        }
        static Dictionary<string,SpeakerChoice> ReadMonitorSpeakers(XElement xml) {
            var result=new Dictionary<string,SpeakerChoice>(StringComparer.OrdinalIgnoreCase);
            var group=xml.Element("speakers");
            if(group==null)return result;
            foreach(var item in group.Elements("monitor")) {
                string id=(string)item.Attribute("id"),device=(string)item.Attribute("device");
                if(!String.IsNullOrEmpty(id) && !String.IsNullOrEmpty(device))
                    result[id]=new SpeakerChoice {Device=device,Name=(string)item.Attribute("name")??device};
            }
            return result;
        }
        static Dictionary<string,int> ReadSpeakerVolumes(XElement xml) {
            var result=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
            var group=xml.Element("speakers");
            if(group==null)return result;
            foreach(var item in group.Elements("volume")) {
                string device=(string)item.Attribute("device");
                int value;
                if(!String.IsNullOrEmpty(device) && Int32.TryParse((string)item.Attribute("value"),out value) && value>=0 && value<=100)result[device]=value;
            }
            return result;
        }
        XElement SpeakersXml() {
            if(MonitorSpeakers.Count==0 && SpeakerVolumes.Count==0)return null;
            return new XElement("speakers",
                MonitorSpeakers.OrderBy(m=>m.Key,StringComparer.OrdinalIgnoreCase).Select(m=>new XElement("monitor",new XAttribute("id",m.Key),new XAttribute("device",m.Value.Device),new XAttribute("name",m.Value.Name??m.Value.Device))),
                SpeakerVolumes.OrderBy(v=>v.Key,StringComparer.OrdinalIgnoreCase).Select(v=>new XElement("volume",new XAttribute("device",v.Key),new XAttribute("value",v.Value))));
        }
        static double ReadShape(XAttribute value) {
            if(value==null)return 0;
            double shape;
            return Double.TryParse(value.Value,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out shape) &&
                shape>=Covers.Narrowest && shape<=Covers.Widest?shape:0;
        }
        internal Preferences Copy() {
            var copy=(Preferences)MemberwiseClone();
            copy.MonitorSpeakers=new Dictionary<string,SpeakerChoice>(MonitorSpeakers,StringComparer.OrdinalIgnoreCase);
            copy.SpeakerVolumes=new Dictionary<string,int>(SpeakerVolumes,StringComparer.OrdinalIgnoreCase);
            copy.Shortcuts=new Dictionary<string,System.Windows.Input.Key>(Shortcuts,StringComparer.Ordinal);
            return copy;
        }
        internal bool SameAs(Preferences other) {return ToXml().ToString()==other.ToXml().ToString();}
        public void Save(string root) {
            string target=Path.Combine(root,"options.xml");
            string temp=Path.Combine(root,Guid.NewGuid().ToString("N")+".tmp");
            try {
                ToXml().Save(temp);
                if(File.Exists(target))File.Replace(temp,target,null);else File.Move(temp,target);
            } finally {File.Delete(temp);}
        }
        XElement ToXml() {
            {
                return new XElement("options",LanguageChosen || Korean?new XAttribute("language",Korean?"ko":"en"):null,
                    UsageNoticeShown?new XAttribute("usageNotice","shown"):null,
                    TabScrollTipShown?new XAttribute("tabScrollTip","shown"):null,
                    ShowSubfolders?new XAttribute("subfolders","show"):null,
                    TreeOpen?new XAttribute("tree","open"):null,new XAttribute("folderView",FolderView),FoldersBelow?new XAttribute("folders","below"):null,
                    FoldersFolded?new XAttribute("foldersFolded","true"):null,new XAttribute("marqueeUses",MarqueeUses),MarqueeTipShown?new XAttribute("marqueeTip","shown"):null,TreeIntroShown?new XAttribute("treeIntro","shown"):null,new XAttribute("treeWidth",TreeWidth),new XAttribute("treeFolded",TreeFolded??""),
                    new XAttribute("coverDirectory",CoverDirectory),
                    new XAttribute("coverPlace",CoverPlace==CoverPlace.BesideOriginal?"beside":CoverPlace==CoverPlace.Subfolder?"subfolder":"folder"),
                    new XAttribute("checkUpdates",CheckUpdates?"true":"false"),
                    ExplorerMenu?null:new XAttribute("explorerMenu","off"),
                    DropAcross==0?null:new XAttribute("dropAcross",DropAcross==2?"move":"copy"),
                    VerifyCopies?new XAttribute("verifyCopies","on"):null,QueueTips==0?null:new XAttribute("queueTips",QueueTips),
                    new XAttribute("theme",Theme==ThemeMode.Light?"light":Theme==ThemeMode.Dark?"dark":"system"),
                    new XAttribute("gridGif",GridGif==GifPlayback.Never?"never":GridGif==GifPlayback.Always?"always":"hover"),
                    new XAttribute("share",Share==ShareContent.CoverOnly?"cover":Share==ShareContent.OriginalOnly?"original":"both"),
                    new XAttribute("shareCovers",ShareCovers==ShareCoverKind.MovingOnly?"moving":ShareCovers==ShareCoverKind.StillOnly?"still":"both"),
                    new XAttribute("shareLimitMB",ShareLimitMB),new XAttribute("gifStrip",GifStripSeconds),
                    new XAttribute("gifFormat",new[]{"gif","webp","mp4"}[Math.Max(0,Math.Min(2,GifFormat))]),
                    new XAttribute("gifQuality",new[]{"high","normal","small"}[Math.Max(0,Math.Min(2,GifQuality))]),
                    new XAttribute("gifFps",GifFps),new XAttribute("gifHeight",GifHeight),new XAttribute("gifSide",GifSideTenths),new XAttribute("sceneStep",SceneStepMs),new XAttribute("gifPreviewStep",GifPreviewStepMs),new XAttribute("gifPreviewDefault",100),new XAttribute("gifDefaults",2),
                    GifHighFps?new XAttribute("gifHighFps","on"):null,
                GifLargeSize?new XAttribute("gifLargeSize","on"):null,GifLargeScenes?new XAttribute("gifLargeScenes","on"):null,
                new XAttribute("gifReal",GifAutoMake?"auto":"ask"),new XAttribute("gifMakeDelay",GifMakeDelayMs),
                    WebpEffort==0?null:new XAttribute("webpEffort",WebpEffort),UpscaleGpu?new XAttribute("upscaleDevice","gpu"):null,SubtitleScale==100?null:new XAttribute("subtitleSize",SubtitleScale),
                    new XAttribute("largePhoto",OpenPhotoOriginal?"original":"cover"),
                    new XAttribute("largeMotion",OpenMotionOriginal?"original":"cover"),
                    new XAttribute("openLarge",OpenByDoubleClick?"double":"single"),
                    FillWideGaps?null:new XAttribute("wideGaps","keep"),
                    new XAttribute("sort",Sort==PhotoSort.Modified?"date":Sort==PhotoSort.CoverOrder?"cover":Sort==PhotoSort.Size?"size":Sort==PhotoSort.Type?"type":"name"),
                    new XAttribute("direction",Descending?"desc":"asc"),new XAttribute("skipSeconds",SkipSeconds),new XAttribute("brightnessStep",BrightnessStep),new XAttribute("turnDegrees",RotationStep),new XAttribute("tileNames",TileNames),CoverTimes?new XAttribute("coverTimes","true"):null,new XAttribute("tileShape",Covers.ShapeKeys[Math.Max(0,Math.Min(Covers.ShapeKeys.Length-1,TileShape))]),WideCovers?null:new XAttribute("wideCovers","off"),CropLock?new XAttribute("cropLock","on"):null,CropZoom?new XAttribute("cropZoom","on"):null,CropShape>0?new XAttribute("cropShape",CropShape):null,GifHoverSound?null:new XAttribute("gifHoverSound","off"),String.IsNullOrWhiteSpace(CaptureDirectory)?null:new XAttribute("captureDirectory",CaptureDirectory),CaptureAsShown?new XAttribute("captureLook","shown"):null,CaptureJpg?new XAttribute("captureFormat","jpg"):null,String.IsNullOrEmpty(SeenVersion)?null:new XAttribute("seenVersion",SeenVersion),String.IsNullOrEmpty(SubtitleBounds)?null:new XAttribute("subtitleBounds",SubtitleBounds),String.IsNullOrEmpty(ViewerBounds)?null:new XAttribute("viewerBounds",ViewerBounds),new XAttribute("volume",Volume),ClearVoice?new XAttribute("clearVoice","true"):null,new XAttribute("clearVoiceLevel",ClearVoiceLevel),
                    SpeakersXml(),ViewerKeys.Xml(Shortcuts),
                    new XElement("editor",new XAttribute("snapEdges",SnapEdges),new XAttribute("wheelZoomPercent",WheelZoomPercent),new XAttribute("pinTop",PinTopOnWheel)));
            }
        }
    }
}
