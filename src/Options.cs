using System;
using System.IO;
using System.Xml.Linq;

namespace LoveMyShelf {
    internal enum PhotoSort { Name, Modified, CoverOrder, Size, Type }
    // Where Save cover writes the JPG. The cover folder is always kept as the fallback.
    internal enum CoverPlace { CoverFolder, BesideOriginal, Subfolder }
    // How GIF covers move in the grid.
    internal enum GifPlayback { Never, Hover, Always }
    // What Copy, Share and dragging a tile hand over.
    internal enum ShareContent { CoverAndOriginal, CoverOnly, OriginalOnly }

    internal sealed class Preferences {
        internal const int EditorPreviewSide=2200;
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
        public GifPlayback GridGif=GifPlayback.Hover;
        public ShareContent Share;
        // Discord's free upload limit went from 10 MB to 20 MB in August 2026.
        public int ShareLimitMB=20;
        public int GifStripSeconds=10;
        // Starting choices in Make GIF cover (the window remembers changes until LoveMyShelf closes).
        public int GifFormat;            // 0 GIF, 1 WebP, 2 MP4
        public int GifQuality=1;         // 0 high, 1 normal, 2 smaller file
        public int GifFps=15;
        public int GifHeight=960;
        public int SceneStepMs=200;      // scene step: milliseconds between scenes everywhere a scene is picked (what used to be "frames")
        public int GifPreviewStepMs=100; // Make GIF cover's quick preview only: milliseconds between the scenes it reads
        public int GifSideTenths=15;    // seconds before and after the cover's moment, in tenths
        internal static string SuggestedCoverParent {get {return Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);}}
        public PhotoSort Sort;
        public int SkipSeconds=5;
        public int BrightnessStep=3;     // how much one press of E or W changes the brightness
        public int Volume=100;
        public bool ClearVoice;
        public int ClearVoiceLevel=1;   // 0 gentle, 1 normal, 2 strong
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
                string theme=(string)xml.Attribute("theme");
                return new Preferences {
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
                    Theme=theme=="light"?ThemeMode.Light:theme=="dark"?ThemeMode.Dark:ThemeMode.System,
                    GridGif=gif=="never"?GifPlayback.Never:gif=="always"?GifPlayback.Always:GifPlayback.Hover,
                    Share=share=="cover"?ShareContent.CoverOnly:share=="original"?ShareContent.OriginalOnly:ShareContent.CoverAndOriginal,
                    ShareLimitMB=ReadNumber(xml,"shareLimitMB",1,2000,20),
                    GifStripSeconds=ReadNumber(xml,"gifStrip",2,120,10),
                    GifFormat=IndexOr(new[]{"gif","webp","mp4"},(string)xml.Attribute("gifFormat"),0),
                    GifQuality=IndexOr(new[]{"high","normal","small"},(string)xml.Attribute("gifQuality"),1),
                    GifFps=ReadNumber(xml,"gifFps",5,60,15),
                    GifHeight=ReadNumber(xml,"gifHeight",240,2160,960),
                    GifSideTenths=ReadNumber(xml,"gifSide",1,300,15),
                    SceneStepMs=ReadNumber(xml,"sceneStep",30,2000,200),
                    GifPreviewStepMs=ReadPreviewStep(xml),
                    OpenPhotoOriginal=(string)xml.Attribute("largePhoto")=="original",
                    OpenMotionOriginal=(string)xml.Attribute("largeMotion")=="original",
                    OpenByDoubleClick=(string)xml.Attribute("openLarge")==null?DefaultOpenByDoubleClick:(string)xml.Attribute("openLarge")=="double",
                    Sort=sort=="date"?PhotoSort.Modified:sort=="cover"?PhotoSort.CoverOrder:sort=="size"?PhotoSort.Size:sort=="type"?PhotoSort.Type:PhotoSort.Name,
                    Descending=direction==null?sort=="date":direction=="desc",
                    SkipSeconds=ReadNumber(xml,"skipSeconds",1,300,5),
                    BrightnessStep=ReadNumber(xml,"brightnessStep",1,20,3),
                    Volume=ReadNumber(xml,"volume",0,100,100),
                    ClearVoice=(string)xml.Attribute("clearVoice")=="true",
                    ClearVoiceLevel=ReadNumber(xml,"clearVoiceLevel",0,2,1),
                    SnapEdges=(string)editor.Attribute("snapEdges")!="false",
                    WheelZoomPercent=ReadNumber(editor,"wheelZoomPercent",1,50,7),
                    PinTopOnWheel=(string)editor.Attribute("pinTop")!="false"
                };
            } catch(IOException) {} catch(UnauthorizedAccessException) {} catch(System.Xml.XmlException) {}
            return new Preferences();
        }
        static int IndexOr(string[] names,string value,int fallback) {
            int index=Array.IndexOf(names,value);return index>=0?index:fallback;
        }
        static int ReadNumber(XElement xml,string name,int min,int max,int fallback) {
            int value;
            return Int32.TryParse((string)xml.Attribute(name),out value) && value>=min && value<=max?value:fallback;
        }
        // 0.2 s was the old default. It moves to 0.1 s once; files saved since carry the mark, so a later choice of 0.2 s stays.
        static int ReadPreviewStep(XElement xml) {
            int value=ReadNumber(xml,"gifPreviewStep",30,2000,100);
            return value==200 && xml.Attribute("gifPreviewDefault")==null?100:value;
        }
        internal Preferences Copy() {return (Preferences)MemberwiseClone();}
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
                    new XAttribute("theme",Theme==ThemeMode.Light?"light":Theme==ThemeMode.Dark?"dark":"system"),
                    new XAttribute("gridGif",GridGif==GifPlayback.Never?"never":GridGif==GifPlayback.Always?"always":"hover"),
                    new XAttribute("share",Share==ShareContent.CoverOnly?"cover":Share==ShareContent.OriginalOnly?"original":"both"),
                    new XAttribute("shareLimitMB",ShareLimitMB),new XAttribute("gifStrip",GifStripSeconds),
                    new XAttribute("gifFormat",new[]{"gif","webp","mp4"}[Math.Max(0,Math.Min(2,GifFormat))]),
                    new XAttribute("gifQuality",new[]{"high","normal","small"}[Math.Max(0,Math.Min(2,GifQuality))]),
                    new XAttribute("gifFps",GifFps),new XAttribute("gifHeight",GifHeight),new XAttribute("gifSide",GifSideTenths),new XAttribute("sceneStep",SceneStepMs),new XAttribute("gifPreviewStep",GifPreviewStepMs),new XAttribute("gifPreviewDefault",100),
                    new XAttribute("largePhoto",OpenPhotoOriginal?"original":"cover"),
                    new XAttribute("largeMotion",OpenMotionOriginal?"original":"cover"),
                    new XAttribute("openLarge",OpenByDoubleClick?"double":"single"),
                    new XAttribute("sort",Sort==PhotoSort.Modified?"date":Sort==PhotoSort.CoverOrder?"cover":Sort==PhotoSort.Size?"size":Sort==PhotoSort.Type?"type":"name"),
                    new XAttribute("direction",Descending?"desc":"asc"),new XAttribute("skipSeconds",SkipSeconds),new XAttribute("brightnessStep",BrightnessStep),new XAttribute("volume",Volume),ClearVoice?new XAttribute("clearVoice","true"):null,new XAttribute("clearVoiceLevel",ClearVoiceLevel),
                    new XElement("editor",new XAttribute("snapEdges",SnapEdges),new XAttribute("wheelZoomPercent",WheelZoomPercent),new XAttribute("pinTop",PinTopOnWheel)));
            }
        }
    }
}
