using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace LoveMyShelf {
    public static class Pictures {
        public static BitmapSource Load(string path, int maxSide = 0) {
            if(AnimationFrames.Supports(path)) return AnimationFrames.Still(path,0,maxSide);
            if(PictureTypes.UsesFfmpeg(path)) {
                try {return PictureTypes.Decode(path,maxSide);}
                catch(Exception quiet) {ErrorLog.Quiet(quiet);} // Windows' own reader tries next
            }
            int w, h, orientation = 1;
            using (var input = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete)) {
                var frame = BitmapDecoder.Create(input, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
                w = frame.PixelWidth; h = frame.PixelHeight;
                try {
                    var meta = frame.Metadata as BitmapMetadata;
                    if (meta != null) {
                        foreach (string query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" }) {
                            try {
                                object val = meta.GetQuery(query);
                                if (val != null) { orientation = Convert.ToInt32(val); break; }
                            } catch (NotSupportedException) { } catch (System.Runtime.InteropServices.COMException) { }
                        }
                    }
                } catch (NotSupportedException) { } catch (System.Runtime.InteropServices.COMException) { }
            }
            var image = new BitmapImage();
            using (var input = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete)) {
                image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = input;
                if (maxSide > 0 && Math.Max(w,h) > maxSide) {
                    if (w >= h) image.DecodePixelWidth = maxSide; else image.DecodePixelHeight = maxSide;
                }
                image.EndInit(); image.Freeze();
            }
            BitmapSource result = image;
            // EXIF orientation maps stored pixels to the upright viewing coordinates.
            Matrix[] transforms = { Matrix.Identity, Matrix.Identity, new Matrix(-1,0,0,1,0,0),
                new Matrix(-1,0,0,-1,0,0), new Matrix(1,0,0,-1,0,0), new Matrix(0,1,1,0,0,0),
                new Matrix(0,1,-1,0,0,0), new Matrix(0,-1,-1,0,0,0), new Matrix(0,-1,1,0,0,0) };
            if (orientation > 1 && orientation < transforms.Length) {
                result = new TransformedBitmap(image, new MatrixTransform(transforms[orientation])); result.Freeze();
            }
            return result;
        }
        public static BitmapSource Cut(BitmapSource source, Rect fraction) {
            int w = source.PixelWidth, h = source.PixelHeight;
            fraction.Intersect(new Rect(0,0,1,1));
            int x = Math.Max(0, Math.Min(w-1,(int)Math.Round(fraction.X*w)));
            int y = Math.Max(0, Math.Min(h-1,(int)Math.Round(fraction.Y*h)));
            int right = Math.Min(w, Math.Max(x+1,(int)Math.Round(fraction.Right*w)));
            int bottom = Math.Min(h, Math.Max(y+1,(int)Math.Round(fraction.Bottom*h)));
            var cropped = new CroppedBitmap(source, new Int32Rect(x,y,right-x,bottom-y)); cropped.Freeze(); return cropped;
        }
        public static BitmapSource TrimPreview(BitmapSource thumb,Rect fraction) {
            var inside=Rect.Intersect(new Rect(0,0,1,1),fraction);
            if(inside==fraction) return thumb;
            return Cut(thumb,new Rect((inside.X-fraction.X)/fraction.Width,(inside.Y-fraction.Y)/fraction.Height,
                inside.Width/fraction.Width,inside.Height/fraction.Height));
        }
        // The crop center is stored in upright source coordinates. Rotate around that point
        // before scaling to the destination so dragging, the preview and saving share one mapping.
        internal static Matrix CoverTransform(double w,double h,Rect fraction,double rotation,Rect destination) {
            var transform=Matrix.Identity;
            transform.Translate(-(fraction.X+fraction.Width/2)*w,-(fraction.Y+fraction.Height/2)*h);
            transform.Rotate(rotation);
            transform.Scale(destination.Width/(fraction.Width*w),destination.Height/(fraction.Height*h));
            transform.Translate(destination.X+destination.Width/2,destination.Y+destination.Height/2);
            return transform;
        }
        internal static void DrawCover(DrawingContext dc,BitmapSource source,Rect fraction,double rotation,Rect destination,int brightness=0) {
            double w=source.PixelWidth,h=source.PixelHeight;
            dc.PushTransform(new MatrixTransform(CoverTransform(w,h,fraction,rotation,destination)));
            var bounds=new Rect(0,0,w,h);
            // Every style, Milky's white or black sheet too, is drawn on the colors (see Light).
            dc.DrawImage(Light.Apply(source,brightness),bounds);
            dc.Pop();
        }
        internal static BitmapSource RenderRotatedCover(BitmapSource source,Rect fraction,double rotation,int width,int height,int brightness=0) {
            var visual=new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual,BitmapScalingMode.HighQuality);
            var destination=new Rect(0,0,width,height);
            using(var dc=visual.RenderOpen()) {
                dc.DrawRectangle(Brushes.Black,null,destination);
                DrawCover(dc,source,fraction,rotation,destination,brightness);
            }
            var target=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);
            target.Render(visual);target.Freeze();return target;
        }
        internal static BitmapSource ViewCover(BitmapSource source,Photo photo) {
            if(!String.IsNullOrEmpty(photo.Drawings))
                return MotionDraw.Apply(CoverImages.NativeCover(source,photo.Crop,photo.Rotation,
                    System.Threading.CancellationToken.None,photo.Brightness),photo.Drawings);
            if(photo.Rotation==0) {
                var cut=Cut(source,photo.Crop);
                return photo.Brightness==0?cut:RenderRotatedCover(cut,new Rect(0,0,1,1),0,cut.PixelWidth,cut.PixelHeight,photo.Brightness);
            }
            Rect frame=photo.Crop;
            double width=frame.Width*source.PixelWidth,height=frame.Height*source.PixelHeight;
            double scale=Math.Min(1,Math.Max(source.PixelWidth,source.PixelHeight)/Math.Max(width,height));
            return RenderRotatedCover(source,frame,photo.Rotation,
                Math.Max(1,(int)Math.Round(width*scale)),Math.Max(1,(int)Math.Round(height*scale)),photo.Brightness);
        }
        internal static BitmapSource SavedPreview(Photo photo) {
            if(File.Exists(photo.ActiveCoverFile))return Load(photo.ActiveCoverFile,448);
            var thumb=Load(photo.Thumb);
            return photo.Rotation==0 && String.IsNullOrEmpty(photo.Drawings)?TrimPreview(thumb,photo.Crop):thumb;
        }
        static BitmapSource RenderCrop(BitmapSource source, Rect fraction, int width, int height) {
            double w=source.PixelWidth, h=source.PixelHeight;
            int left=(int)Math.Max(0,Math.Floor(fraction.Left*w)), top=(int)Math.Max(0,Math.Floor(fraction.Top*h));
            int right=(int)Math.Min(w,Math.Ceiling(fraction.Right*w)), bottom=(int)Math.Min(h,Math.Ceiling(fraction.Bottom*h));
            var pixels=new Int32Rect(left,top,right-left,bottom-top);
            var visible=new CroppedBitmap(source,pixels);
            double scaleX=width/(fraction.Width*w), scaleY=height/(fraction.Height*h);
            var visual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            using (var dc = visual.RenderOpen()) {
                dc.DrawRectangle(Brushes.Black,null,new Rect(0,0,width,height));
                dc.DrawImage(visible,new Rect((left-fraction.X*w)*scaleX,(top-fraction.Y*h)*scaleY,
                    pixels.Width*scaleX,pixels.Height*scaleY));
            }
            var target = new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);
            target.Render(visual); target.Freeze(); return target;
        }
        public static void Thumbnail(BitmapSource source, Rect fraction, string path,double rotation=0,int brightness=0) {
            using (var output = File.Create(path)) Thumbnail(source,fraction,output,rotation,brightness);
        }
        internal static void Thumbnail(BitmapSource source, Rect fraction, Stream output,double rotation=0,int brightness=0,string drawings=null) {
            var encoder = new JpegBitmapEncoder { QualityLevel = 88 };
            // At most 448 on the long side, in the cover's own shape (252 x 448 for a 9:16 cover).
            double aspect=Covers.AspectOf(fraction,source.PixelWidth,source.PixelHeight);
            int w=Math.Max(1,(int)Math.Round(aspect>=1?448:448*aspect)),h=Math.Max(1,(int)Math.Round(aspect>=1?448/aspect:448));
            var picture=rotation==0 && brightness==0?RenderCrop(source,fraction,w,h):RenderRotatedCover(source,fraction,rotation,w,h,brightness);
            encoder.Frames.Add(BitmapFrame.Create(MotionDraw.Apply(picture,drawings)));
            encoder.Save(output);
        }
    }
    public class Album {
        public readonly string Folder, Name, Source;
        public Album(string folder,string name,string source=null) { Folder = folder; Name = name; Source=source; }
    }
    public class Photo {
        public string Folder, Original, Thumb, Name;
        // Set only in the "Similar photos" collection: the group, the file, the group's label (on its first tile),
        // whether it is the one suggested to keep, and whether the tile shows the original instead of the cover.
        internal SimilarGroup Similar;
        internal SimilarFile SimilarFile;
        internal string SimilarLabel;
        internal bool SimilarKeep,ShowOriginal;
        internal string SimilarInfo;   // the lines over the bottom of the tile (size, type, date, how it differs)
        internal int SimilarCovers;
        internal bool ShowsCover {get {return HasCrop && !ExcludedFromCollection && !ShowOriginal;}}
        internal string CoverFile;
        internal long CoverFileTicks;
        // Set only on the result of a save that could not write to the chosen place.
        internal bool SavedToFallback;
        // An optional GIF (or WebP/MP4) made from this cover with Make GIF.
        internal string MotionFile;
        // The part and choices the GIF was made with (null for one made before these were saved).
        internal MotionRequest MotionSettings;
        internal string Drawings;
        internal bool HasMotion {get {return MotionFile!=null && File.Exists(MotionFile);}}
        // An upscaled copy of the cover file. Choice: "pending" (not chosen yet), "upscaled", or "original".
        internal string UpscaleFile,UpscaleChoice;
        internal bool HasUpscale {get {return UpscaleFile!=null && File.Exists(UpscaleFile) && File.Exists(CoverFile);}}
        internal bool UpscalePending {get {return UpscaleChoice=="pending" && HasUpscale;}}
        // The file the app shows for this cover. Both files always remain in the cover folder.
        internal string ActiveCoverFile {get {return UpscaleChoice=="upscaled" && HasUpscale?UpscaleFile:CoverFile;}}
        internal string GridImage {get {return File.Exists(ActiveCoverFile)?ActiveCoverFile:Thumb;}}
        internal string LegacyImage {get {return Path.ChangeExtension(Thumb,".view.png");}}
        internal string DisplayImage {get {return File.Exists(ActiveCoverFile)?ActiveCoverFile:LegacyImage;}}
        public Rect Crop = Rect.Empty;
        // The cover's own shape in pixels, width over height ("aspect" in photo.xml). Covers made before 1.0.9 are 9:16.
        internal double Aspect=Covers.Tall;
        public double Rotation;
        internal int Brightness;
        public bool Linked, ExcludedFromCollection;
        public int FrameIndex;
        internal double? FrameTime;
        internal int FrameTimeOffset;
        // Where the cover's scene is in the video, for ordering only. Covers picked from the full frame list are
        // decoded by frame number, so this stays apart from FrameTime and never changes which picture is read.
        internal double? SavedSceneMs;
        internal double? SceneMs {get {return Picture!=null?null:FrameTime??SavedSceneMs??(FrameIndex<=0?(double?)0:null);}}
        // A cover cut from a picture made outside LoveMyShelf: the name of the copy kept in the record folder.
        // It has no scene, so it has no scene time and no GIF cover, and Edit cover opens this copy instead of the original.
        internal string Picture;
        internal bool IsOutside {get {return Picture!=null;}}
        internal string PictureFile {get {return Picture==null?null:Path.Combine(Folder,Picture);}}
        // The place the person gave this cover among the covers of its original (null: the automatic order).
        internal double? Rank;
        public bool HasCrop { get { return !Crop.IsEmpty; } }
        // A saved cover record exists but could not be read: the tile has no crop, yet the work was done.
        internal bool RecordDamaged;
        // In a folder grid an original with several covers has one tile per cover. The first tile is the main record;
        // the others are "extra" tiles that carry the folder of the main record they follow.
        internal string LeadFolder;
        internal bool IsExtra {get {return LeadFolder!=null;}}
        public long Order;
        internal long CoverCreated;
    }
    public class GalleryStore {
        public readonly string Root;
        const string ExclusionFile = "exclude-from-collection";
        internal const string HiddenTabFile = "hide-folder-tab";
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public GalleryStore(string root) { Root = root; Directory.CreateDirectory(root); Tags.Use(root); }
        // The originals that carry a tag, from every folder, each with its covers like in its own folder.
        // Folders on a drive that isn't connected are left out; they come back with the drive.
        internal List<Photo> Tagged(string tag,out int skipped,PhotoSort sort,bool descending) {return Tagged(new[]{tag},out skipped,sort,descending);}
        // Several tags: every original that carries any of them, once.
        internal List<Photo> Tagged(IEnumerable<string> tags,out int skipped,PhotoSort sort,bool descending) {
            skipped=0;
            var items=new List<Photo>();
            var files=new Dictionary<string,HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach(string tag in tags)
                foreach(var folder in Tags.FilesWith(tag)) {
                    HashSet<string> names;
                    if(!files.TryGetValue(folder.Key,out names))files.Add(folder.Key,names=new HashSet<string>(folder.Value,folder.Value.Comparer));
                    else names.UnionWith(folder.Value);
                }
            foreach(var folder in files) {
                try {
                    string source=SourceOfRecords(folder.Key);
                    if(source==null || !Directory.Exists(source))continue;
                    // Another drive may be using this folder's old drive letter: its files are not the tagged ones.
                    bool? sameDrive=DriveLinks.SameDriveAsBefore(folder.Key,source);
                    if(sameDrive==false)continue;
                    int unreadable;
                    var found=FolderPhotos(new Album(folder.Key,Path.GetFileName(source),source),out unreadable)
                        .Where(p=>folder.Value.Contains(Path.GetFileName(p.Name))).ToList();
                    // A tagged file that isn't there right now is only left out of the view. Looking never removes a tag:
                    // the file may be back later (moved away for a moment, renamed outside, its records waiting for a drive).
                    items.AddRange(found);
                } catch(IOException quiet) {ErrorLog.Quiet(quiet);} catch(UnauthorizedAccessException quiet) {ErrorLog.Quiet(quiet);}
            }
            return SortCoverFamilies(items,sort,descending);
        }
        public List<Album> Albums() {
            return Directory.GetDirectories(Root).Where(f=>File.Exists(Path.Combine(f,"album.txt")))
                .Select(f=>new Album(f,File.ReadAllText(Path.Combine(f,"album.txt")),
                    File.Exists(Path.Combine(f,"source.txt")) ? File.ReadAllText(Path.Combine(f,"source.txt")) : null))
                .OrderBy(a=>Path.GetFileName(a.Folder),StringComparer.Ordinal).ToList();
        }
        internal List<Album> VisibleAlbums() {
            var visible=Albums().Where(a=>a.Source!=null && !File.Exists(Path.Combine(a.Folder,HiddenTabFile))).ToList();
            // Folders the person arranged come first in that order; others keep their old order and follow.
            var order=TabOrder();
            return visible.Select((a,i)=>new {a,i,rank=order.IndexOf(Path.GetFileName(a.Folder))})
                .OrderBy(x=>x.rank<0?Int32.MaxValue:x.rank).ThenBy(x=>x.i).Select(x=>x.a).ToList();
        }
        const string TabOrderFile="tab-order.txt";
        List<string> TabOrder() {
            try {
                string path=Path.Combine(Root,TabOrderFile);
                return File.Exists(path)?File.ReadAllLines(path).Where(l=>l.Length>0).ToList():new List<string>();
            } catch(IOException) {return new List<string>();} catch(UnauthorizedAccessException) {return new List<string>();}
        }
        // Moves a folder tab to a new position among the visible tabs and remembers the whole order.
        internal void MoveTab(Album album,int position) {
            var names=VisibleAlbums().Select(a=>Path.GetFileName(a.Folder)).ToList();
            string name=Path.GetFileName(album.Folder);
            int from=names.IndexOf(name);if(from<0)return;
            names.RemoveAt(from);
            names.Insert(Math.Max(0,Math.Min(names.Count,position)),name);
            // Keep hidden folders' places too, so showing them again doesn't scramble anything.
            names.AddRange(TabOrder().Where(n=>!names.Contains(n)));
            string target=Path.Combine(Root,TabOrderFile),temp=target+".tmp";
            File.WriteAllLines(temp,names);
            if(File.Exists(target))File.Replace(temp,target,null);else File.Move(temp,target);
        }
        internal void HideFolder(Album album) {
            if(album.Source==null)throw new InvalidOperationException(L.T("Choose a folder tab."));
            File.WriteAllText(Path.Combine(album.Folder,HiddenTabFile),"");
        }
        internal static string KeyOf(string fileName) {return Key(fileName);}
        static string Key(string value) {
            using(var sha=SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value.ToUpperInvariant())),0,16).Replace("-","");
        }
        // Where this store keeps the records of a source folder (the folder may not have been added yet).
        internal string AlbumFolderOf(string source) {
            string path=Path.GetFullPath(source);
            if(path.Length>Path.GetPathRoot(path).Length) path=path.TrimEnd(Path.DirectorySeparatorChar);
            return Path.Combine(Root,"folder-"+Key(path));
        }
        public Album OpenFolder(string source) {
            var dir=new DirectoryInfo(source);
            if(!dir.Exists) throw new DirectoryNotFoundException(L.T("The source folder could not be found."));
            string path=dir.FullName;
            if(path.Length>Path.GetPathRoot(path).Length) path=path.TrimEnd(Path.DirectorySeparatorChar);
            string folder=Path.Combine(Root,"folder-"+Key(path));
            if(!Directory.Exists(folder)) {
                string temp=Path.Combine(Root,"setup-"+Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temp);
                try {
                    File.WriteAllText(Path.Combine(temp,"source.txt"),path);
                    File.WriteAllText(Path.Combine(temp,"album.txt"),dir.Name);
                    Directory.Move(temp,folder);
                } finally { if(Directory.Exists(temp)) Directory.Delete(temp,true); }
            }
            File.Delete(Path.Combine(folder,HiddenTabFile));
            return new Album(folder,dir.Name,path);
        }
        // A folder opened from a folder tile or the folder tree: it gets its own records like any folder, but a new one doesn't become a tab.
        internal Album OpenSubfolder(string source) {
            var dir=new DirectoryInfo(source);
            if(!dir.Exists) throw new DirectoryNotFoundException(L.T("The source folder could not be found."));
            string folder=AlbumFolderOf(dir.FullName);
            if(File.Exists(Path.Combine(folder,"album.txt"))) // already known (a tab or visited before): keep whether it is a tab
                return new Album(folder,dir.Name,File.ReadAllText(Path.Combine(folder,"source.txt")));
            var album=OpenFolder(dir.FullName);
            File.WriteAllText(Path.Combine(folder,HiddenTabFile),"");
            return album;
        }
        // The folders right inside a folder, for folder tiles, as File Explorer shows them (see ShownFolder; OneDrive
        // folders stay, though Windows marks them as links). Count = photos and videos right inside.
        internal static List<Tuple<string,int>> Subfolders(string source,PhotoSort sort,bool descending) {
            var list=new List<Tuple<DirectoryInfo,int>>();
            foreach(var dir in new DirectoryInfo(source).EnumerateDirectories()) {
                try {
                    if(!ShownFolder(dir))continue;
                    if(String.Equals(dir.Name,Preferences.SubfolderName,StringComparison.OrdinalIgnoreCase))continue;
                    int count=0;
                    try {
                        foreach(string path in Directory.EnumerateFiles(dir.FullName))
                            if((ImageExtensions.Contains(Path.GetExtension(path)) || VideoFrames.Supports(path)) && !SharedCoverFiles.IsBesideCover(path))count++;
                    } catch(UnauthorizedAccessException) {count=-1;} catch(IOException) {count=-1;}
                    list.Add(Tuple.Create(dir,count));
                } catch(IOException quiet) {ErrorLog.Quiet(quiet);} catch(UnauthorizedAccessException quiet) {ErrorLog.Quiet(quiet);}
            }
            // Folders follow the date sort like File Explorer; every other sort orders them by name.
            IEnumerable<Tuple<DirectoryInfo,int>> ordered=sort==PhotoSort.Modified?
                list.OrderBy(t=>t.Item1.LastWriteTimeUtc):list.OrderBy(t=>t.Item1.Name,Comparer<string>.Create((a,b)=>StrCmpLogicalW(a,b)));
            if(descending)ordered=ordered.Reverse();
            return ordered.Select(t=>Tuple.Create(t.Item1.FullName,t.Item2)).ToList();
        }
        // Folders a person would see in File Explorer: not hidden or system ones (those also cover the old
        // "Application Data" style links), and never the "Love My Shelf 표지" cover folder.
        internal static bool ShownFolder(DirectoryInfo dir) {
            if((dir.Attributes&(FileAttributes.Hidden|FileAttributes.System))!=0)return false;
            return !String.Equals(dir.Name,Preferences.SubfolderName,StringComparison.OrdinalIgnoreCase);
        }
        // The folders right inside a folder, by name like File Explorer (for the folder tree).
        internal static List<string> ChildFolders(string path) {
            var list=new List<string>();
            try {
                foreach(var dir in new DirectoryInfo(path).EnumerateDirectories())
                    try {if(ShownFolder(dir))list.Add(dir.FullName);} catch(IOException quiet) {ErrorLog.Quiet(quiet);} catch(UnauthorizedAccessException quiet) {ErrorLog.Quiet(quiet);}
            } catch(IOException quiet) {ErrorLog.Quiet(quiet);} catch(UnauthorizedAccessException quiet) {ErrorLog.Quiet(quiet);} catch(System.Security.SecurityException quiet) {ErrorLog.Quiet(quiet);}
            list.Sort((a,b)=>StrCmpLogicalW(Path.GetFileName(a),Path.GetFileName(b)));
            return list;
        }
        static string Num(double n) { return n.ToString("R",Inv); }
        static void WriteMeta(string path, string name, string original, string thumb, Rect crop, long order,long coverCreated,
            int frameIndex,double rotation,double? frameTime,int frameTimeOffset,string coverFile,int brightness,double? sceneMs,
            string picture=null,double? rank=null,double aspect=Covers.Tall,string drawings=null) {
            new XElement("photo",new XAttribute("name",name),new XAttribute("original",original),
                new XAttribute("thumb",thumb), new XAttribute("order",order),new XAttribute("coverCreated",coverCreated),new XAttribute("frame",frameIndex),
                new XAttribute("x",Num(crop.X)),new XAttribute("y",Num(crop.Y)),
                new XAttribute("w",Num(crop.Width)),new XAttribute("h",Num(crop.Height)),
                rotation==0?null:new XAttribute("rotation",Num(rotation)),
                new XAttribute("aspect",Num(aspect)),
                // "brightness": the old white or black sheet; "soft" and "light" (See in the dark): the curves (see Light).
                // Older versions ignore "soft" and "light".
                brightness==0?null:new XAttribute(Light.Style(brightness)==Light.Sheet?"brightness":Light.Style(brightness)==Light.Dark?"light":"soft",Light.Level(brightness)),
                frameTime.HasValue?new XAttribute("frameTimeMs",Num(frameTime.Value)):null,
                frameTimeOffset>0?new XAttribute("frameTimeOffset",frameTimeOffset):null,
                sceneMs.HasValue?new XAttribute("sceneMs",Num(sceneMs.Value)):null,
                picture==null?null:new XAttribute("picture",picture),
                String.IsNullOrEmpty(drawings)?null:new XAttribute("drawings",drawings),
                rank.HasValue?new XAttribute("rank",Num(rank.Value)):null,
                coverFile==null?null:new XAttribute("coverFile",coverFile),
                coverFile==null?null:new XAttribute("coverFileTicks",File.GetLastWriteTimeUtc(coverFile).Ticks)).Save(path);
        }
        public static Photo Read(string folder,string linkedSource=null) {
            string metadata=Path.Combine(folder,"photo.xml");
            XElement n;
            long coverCreated;
            lock(SharedCoverFiles.MetadataLock) {
                n=XElement.Load(metadata);
                coverCreated=(long?)n.Attribute("coverCreated")??File.GetLastWriteTimeUtc(metadata).Ticks;
            }
            var r = new Rect((double)n.Attribute("x"),(double)n.Attribute("y"),(double)n.Attribute("w"),(double)n.Attribute("h"));
            double rotation=(double?)n.Attribute("rotation")??0;
            double? frameTime=(double?)n.Attribute("frameTimeMs");
            int frameTimeOffset=(int?)n.Attribute("frameTimeOffset")??0;
            if (!(r.Width>0 && r.Height>0 && r.X<1 && r.Y<1 && r.Right>0 && r.Bottom>0) ||
                Double.IsInfinity(r.Right) || Double.IsInfinity(r.Bottom))
                throw new InvalidDataException(L.T("The saved cover information is damaged and can't be read."));
            if(Double.IsNaN(rotation) || Double.IsInfinity(rotation))
                throw new InvalidDataException(L.T("The saved cover's rotation information is damaged and can't be read."));
            if(frameTime.HasValue && (Double.IsNaN(frameTime.Value) || Double.IsInfinity(frameTime.Value) || frameTime.Value<0 || frameTimeOffset<0))
                throw new InvalidDataException(L.T("Could not read the video's scene times."));
            double? sceneMs=null;
            try {
                sceneMs=(double?)n.Attribute("sceneMs");
                if(sceneMs.HasValue && (Double.IsNaN(sceneMs.Value) || Double.IsInfinity(sceneMs.Value) || sceneMs.Value<0))sceneMs=null;
            } catch(FormatException) {}
            double? rank=null;
            try {
                rank=(double?)n.Attribute("rank");
                if(rank.HasValue && (Double.IsNaN(rank.Value) || Double.IsInfinity(rank.Value)))rank=null;
            } catch(FormatException) {}
            string picture=(string)n.Attribute("picture");
            if(picture!=null) {
                try {picture=Path.GetFileName(picture);} catch(ArgumentException) {picture=null;}
                if(String.IsNullOrEmpty(picture))picture=null;
            }
            return new Photo { Folder=folder,Name=(string)n.Attribute("name"), Crop=r,Order=(long)n.Attribute("order"),
                SavedSceneMs=sceneMs,Picture=picture,Rank=rank,Drawings=(string)n.Attribute("drawings"),
                CoverCreated=coverCreated,
                Rotation=CropSurface.NormalizeRotation(rotation),
                Brightness=n.Attribute("soft")!=null?Light.Encode((int)n.Attribute("soft"),Light.Soft):
                    n.Attribute("light")!=null?Light.Encode((int)n.Attribute("light"),Light.Dark):Light.Legacy((int?)n.Attribute("brightness")??0),
                FrameIndex=(int?)n.Attribute("frame")??0,
                Aspect=Covers.Valid((double?)n.Attribute("aspect")),
                FrameTime=frameTime,FrameTimeOffset=frameTimeOffset,
                ExcludedFromCollection=File.Exists(Path.Combine(folder,ExclusionFile)),
                Original=linkedSource??Path.Combine(folder,Path.GetFileName((string)n.Attribute("original"))),Linked=linkedSource!=null,
                Thumb=Path.Combine(folder,Path.GetFileName((string)n.Attribute("thumb"))),
                CoverFile=(string)n.Attribute("coverFile"),CoverFileTicks=(long?)n.Attribute("coverFileTicks")??0,
                UpscaleFile=(string)n.Attribute("upscaleFile"),UpscaleChoice=(string)n.Attribute("upscaleChoice"),
                MotionFile=(string)n.Attribute("motionFile"),MotionSettings=MotionRequest.Parse((string)n.Attribute("motionSettings")) };
        }
        // Publishes a finished upscale beside the cover file and marks it as waiting for a choice.
        // Returns null when the cover was remade meanwhile; the temporary file is then left for the caller to remove.
        internal static Photo AttachUpscale(Photo photo,string revision,string temp) {
            string metadata=Path.Combine(photo.Folder,"photo.xml");
            string part=Path.Combine(photo.Folder,"meta-"+Guid.NewGuid().ToString("N")+".tmp");
            try {
                lock(SharedCoverFiles.MetadataLock) {
                    var xml=XElement.Load(metadata);
                    string cover=(string)xml.Attribute("coverFile");
                    if((string)xml.Attribute("thumb")!=revision || cover==null || !String.Equals(cover,photo.CoverFile,StringComparison.OrdinalIgnoreCase))
                        return null;
                    string published=SharedCoverFiles.Publish(temp,Path.GetDirectoryName(cover),
                        Path.GetFileNameWithoutExtension(cover)+"-upscaled",System.Threading.CancellationToken.None);
                    xml.SetAttributeValue("upscaleFile",published);
                    xml.SetAttributeValue("upscaleChoice","pending");
                    xml.Save(part);
                    File.Replace(part,metadata,null);
                }
            } finally {File.Delete(part);}
            return Read(photo.Folder,photo.Linked?photo.Original:null);
        }
        // Links a finished GIF/WebP/MP4 to the cover. Returns null (and leaves the file) when the cover was remade meanwhile.
        internal static Photo AttachMotion(Photo photo,string motionFile,MotionRequest settings=null) {
            string metadata=Path.Combine(photo.Folder,"photo.xml");
            string part=Path.Combine(photo.Folder,"meta-"+Guid.NewGuid().ToString("N")+".tmp");
            try {
                lock(SharedCoverFiles.MetadataLock) {
                    var xml=XElement.Load(metadata);
                    if((string)xml.Attribute("thumb")!=Path.GetFileName(photo.Thumb))return null;
                    xml.SetAttributeValue("motionFile",motionFile);
                    xml.SetAttributeValue("motionSettings",settings==null?null:settings.ToText());
                    xml.Save(part);
                    File.Replace(part,metadata,null);
                }
            } finally {File.Delete(part);}
            return Read(photo.Folder,photo.Linked?photo.Original:null);
        }
        // Records which of the two files the app uses. Neither file is deleted.
        internal static Photo ChooseUpscale(Photo photo,bool upscaled) {
            string metadata=Path.Combine(photo.Folder,"photo.xml");
            string part=Path.Combine(photo.Folder,"meta-"+Guid.NewGuid().ToString("N")+".tmp");
            try {
                lock(SharedCoverFiles.MetadataLock) {
                    var xml=XElement.Load(metadata);
                    if(!String.Equals((string)xml.Attribute("upscaleFile"),photo.UpscaleFile,StringComparison.OrdinalIgnoreCase))
                        return Read(photo.Folder,photo.Linked?photo.Original:null);
                    xml.SetAttributeValue("upscaleChoice",upscaled?"upscaled":"original");
                    xml.Save(part);
                    File.Replace(part,metadata,null);
                }
            } finally {File.Delete(part);}
            return Read(photo.Folder,photo.Linked?photo.Original:null);
        }
        public List<Photo> Photos(Album album,out int skipped,bool byDate=false) {
            return Photos(album,out skipped,byDate?PhotoSort.Modified:PhotoSort.Name,byDate);
        }
        internal List<Photo> Photos(Album album,out int skipped,PhotoSort sort,bool descending) {
            var all=FolderPhotos(album,out skipped);
            return SortCoverFamilies(all,sort,descending);
        }
        // Everything without a cover in the LoveMyShelf folders and all the folders inside them.
        // A folder inside two added folders counts once.
        internal List<Photo> UnfinishedCovers(out int skipped,CancellationToken token=default(CancellationToken),Action<int> found=null) {
            return ScanFolderTrees(VisibleAlbums().Select(a=>a.Source),true,out skipped,token,found);
        }
        internal List<Photo> FolderTreePhotos(string top,out int skipped,CancellationToken token,Action<int> found) {
            token.ThrowIfCancellationRequested();
            skipped=0;
            var dir=new DirectoryInfo(top);
            if(!dir.Exists)throw new DirectoryNotFoundException(top);
            if(!ShownFolder(dir) || IsFolderLink(dir))return new List<Photo>();
            return ScanFolderTrees(new[]{top},false,out skipped,token,found);
        }
        List<Photo> ScanFolderTrees(IEnumerable<string> roots,bool unfinished,out int skipped,CancellationToken token,Action<int> found) {
            skipped=0;
            var items=new List<Photo>();
            var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string coverFolder=Preferences.Load(Root).CoverDirectory;
            coverFolder=String.IsNullOrWhiteSpace(coverFolder)?null:PlainPath(coverFolder);
            var clock=System.Diagnostics.Stopwatch.StartNew();int originals=0;
            foreach(string root in roots) {
                string top=PlainPath(root);
                if(!unfinished && String.Equals(top,coverFolder,StringComparison.OrdinalIgnoreCase))continue;
                var waiting=new Stack<string>();waiting.Push(top);
                while(waiting.Count>0) {
                    token.ThrowIfCancellationRequested();
                    string path=waiting.Pop();
                    if(!seen.Add(path))continue;
                    try {foreach(string inner in FoldersToScan(path,coverFolder,token))waiting.Push(inner);}
                    catch(IOException quiet) {ErrorLog.Quiet(quiet);} catch(UnauthorizedAccessException quiet) {ErrorLog.Quiet(quiet);} catch(System.Security.SecurityException quiet) {ErrorLog.Quiet(quiet);}
                    try {
                        if(HasPictures(path,token)) {
                            int unreadable;
                            Action<Photo> progress=null;
                            if(!unfinished && found!=null) {
                                int inFolder=0;
                                progress=photo=>{
                                    if(!photo.RecordDamaged && !SharedCoverFiles.IsCoverFile(photo.Original))inFolder++;
                                    if(clock.ElapsedMilliseconds>=250){clock.Restart();found(originals+inFolder);}
                                };
                            }
                            foreach(var p in FolderPhotos(RecordsFor(path),out unreadable,token,progress)) {
                                if((unfinished && p.HasCrop) || p.RecordDamaged || SharedCoverFiles.IsCoverFile(p.Original))continue;
                                items.Add(p);if(!p.IsExtra)originals++;
                            }
                            skipped+=unreadable;
                        }
                    } catch(IOException) {if(!unfinished || path==top)skipped++;}
                    catch(UnauthorizedAccessException) {if(!unfinished || path==top)skipped++;}
                    if(found!=null && clock.ElapsedMilliseconds>=250){clock.Restart();found(originals);}
                }
            }
            return items;
        }
        static string PlainPath(string path) {
            string full=Path.GetFullPath(path);
            return full.Length>Path.GetPathRoot(full).Length?full.TrimEnd(Path.DirectorySeparatorChar):full;
        }
        // Windows' own folders hold thousands of icons, so a whole drive that was added leaves them out.
        static readonly HashSet<string> DriveSystemFolders=new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "Windows","Program Files","Program Files (x86)","ProgramData","PerfLogs"
        };
        static List<string> FoldersToScan(string path,string coverFolder,CancellationToken token) {
            var dir=new DirectoryInfo(path);
            bool driveTop=dir.Parent==null;
            var list=new List<string>();
            foreach(var inner in dir.EnumerateDirectories()) {
                token.ThrowIfCancellationRequested();
                try {
                    if(!ShownFolder(inner) || IsFolderLink(inner))continue;
                    if(driveTop && DriveSystemFolders.Contains(inner.Name))continue;
                    string full=PlainPath(inner.FullName);
                    if(coverFolder!=null && String.Equals(full,coverFolder,StringComparison.OrdinalIgnoreCase))continue;
                    list.Add(full);
                } catch(IOException quiet) {ErrorLog.Quiet(quiet);} catch(UnauthorizedAccessException quiet) {ErrorLog.Quiet(quiet);}
            }
            return list;
        }
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
        struct FindData {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Created,Accessed,Written;
            public uint SizeHigh,SizeLow,ReparseTag,Reserved;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)] public string Name;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=14)] public string ShortName;
        }
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true,EntryPoint="FindFirstFileW")]
        static extern IntPtr FindFirstFile(string path,out FindData data);
        [DllImport("kernel32.dll")] static extern bool FindClose(IntPtr handle);
        // Junctions and symbolic links can point back up and never end. OneDrive folders are marked in a similar way but are real.
        internal static bool IsFolderLink(DirectoryInfo dir) {
            if((dir.Attributes&FileAttributes.ReparsePoint)==0)return false;
            FindData data;
            IntPtr handle=FindFirstFile(dir.FullName,out data);
            if(handle==new IntPtr(-1))return true;
            FindClose(handle);
            return data.ReparseTag==0xA0000003 || data.ReparseTag==0xA000000C;
        }
        static bool HasPictures(string path,CancellationToken token=default(CancellationToken)) {
            return Directory.EnumerateFiles(path).Any(f=>{
                token.ThrowIfCancellationRequested();
                return (ImageExtensions.Contains(Path.GetExtension(f)) || VideoFrames.Supports(f)) && !SharedCoverFiles.IsBesideCover(f);
            });
        }
        // The hidden record of a folder inside an added one. Covers saved there need it to appear in the cover collection.
        // It is complete before it appears, so it never shows as a LoveMyShelf folder for a moment.
        Album RecordsFor(string path) {
            string folder=AlbumFolderOf(path),name=Path.GetFileName(path);
            if(name.Length==0)name=path;
            string albumFile=Path.Combine(folder,"album.txt"),sourceFile=Path.Combine(folder,"source.txt");
            if(!File.Exists(albumFile)) {
                if(Directory.Exists(folder)) {
                    File.WriteAllText(Path.Combine(folder,HiddenTabFile),"");
                    File.WriteAllText(sourceFile,path);File.WriteAllText(albumFile,name);
                } else {
                    string temp=Path.Combine(Root,"setup-"+Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(temp);
                    try {
                        File.WriteAllText(Path.Combine(temp,HiddenTabFile),"");
                        File.WriteAllText(Path.Combine(temp,"source.txt"),path);File.WriteAllText(Path.Combine(temp,"album.txt"),name);
                        try {Directory.Move(temp,folder);}
                        catch(IOException) {if(!File.Exists(albumFile))throw;} // another window made it first
                    } finally {if(Directory.Exists(temp))Directory.Delete(temp,true);}
                }
            }
            return new Album(folder,File.ReadAllText(albumFile),File.ReadAllText(sourceFile));
        }
        // The real folder a record folder stands for.
        internal static string SourceOfRecords(string folder) {
            string file=Path.Combine(folder,"source.txt");
            return File.Exists(file)?File.ReadAllText(file):null;
        }
        static void AppendSaved(Album album,List<Photo> items,ref int skipped) {
            foreach(string folder in Directory.GetDirectories(album.Folder,"p-*")) {
                try {
                    var photo=Read(folder);
                    if(album.Source!=null) {
                        photo.Linked=true;
                        photo.Original=Path.Combine(album.Source,Path.GetFileName(photo.Name));
                    }
                    items.Add(photo);
                } catch (Exception) { skipped++; }
            }
        }
        public List<Photo> AllCrops(out int skipped,bool byDate=false) {
            return AllCrops(out skipped,byDate?PhotoSort.Modified:PhotoSort.Name,byDate);
        }
        internal List<Photo> AllCrops(out int skipped,PhotoSort sort,bool descending) {
            return SortCoverFamilies(SavedCovers(out skipped).Where(p=>!p.ExcludedFromCollection),sort,descending);
        }
        internal List<Photo> SavedCovers(out int skipped) {
            skipped=0;
            var items=new List<Photo>();
            foreach(Album album in Albums()) {
                try { AppendSaved(album,items,ref skipped); }
                catch(IOException) { skipped++; }
                catch(UnauthorizedAccessException) { skipped++; }
            }
            return items;
        }
        static DateTime Modified(Photo photo) {
            try {
                string path=photo.Original;
                if(photo.Linked && !File.Exists(path))path=OriginalLocator.LastKnownPath(photo)??path;
                return File.GetLastWriteTimeUtc(path);
            } catch(IOException){return DateTime.MinValue;}catch(UnauthorizedAccessException){return DateTime.MinValue;}
        }
        static long Size(Photo photo) {
            try {
                string path=photo.Original;
                if(photo.Linked && !File.Exists(path))path=OriginalLocator.LastKnownPath(photo)??path;
                return new FileInfo(path).Length;
            } catch(IOException){return 0;}catch(UnauthorizedAccessException){return 0;}catch(ArgumentException){return 0;}
        }
        internal static List<Photo> SortPhotos(IEnumerable<Photo> items,bool byDate) {
            return SortPhotos(items,byDate?PhotoSort.Modified:PhotoSort.Name,byDate);
        }
        // Every sort moves the covers of one original as one group; inside it, cover 1 is always on the left.
        // By cover work order, a group's place is its newest cover (newest first) or its first cover (oldest first).
        internal static List<Photo> SortCoverFamilies(IEnumerable<Photo> items,PhotoSort sort,bool descending) {
            var families=new Dictionary<Photo,List<Photo>>();
            var workTimes=new Dictionary<Photo,long>();
            foreach(var group in items.GroupBy(p=>CoverFamily(p),StringComparer.OrdinalIgnoreCase)) {
                var covers=InCoverOrder(group);
                var lead=covers.FirstOrDefault(p=>p.HasCrop)??covers[0];
                var times=covers.Where(p=>p.HasCrop && !p.ExcludedFromCollection).Select(p=>p.CoverCreated).ToList();
                families[lead]=covers;
                workTimes[lead]=times.Count==0?lead.CoverCreated:descending?times.Max():times.Min();
            }
            return SortPhotos(families.Keys,sort,descending,p=>workTimes[p]).SelectMany(p=>families[p]).ToList();
        }
        internal static List<Photo> SortPhotos(IEnumerable<Photo> items,PhotoSort sort,bool descending,Func<Photo,long> workTime=null) {
            var time=workTime??(Func<Photo,long>)(p=>p.CoverCreated);
            var names=Comparer<Photo>.Create(CompareFolderPhotos);
            IOrderedEnumerable<Photo> ordered;
            if(sort==PhotoSort.CoverOrder) {
                // Unfinished items have no work date and follow dated covers in either direction.
                var coversFirst=items.OrderByDescending(p=>p.HasCrop);
                ordered=descending?coversFirst.ThenByDescending(time):coversFirst.ThenBy(time);
                ordered=descending?ordered.ThenByDescending(p=>p,names):ordered.ThenBy(p=>p,names);
            } else if(sort==PhotoSort.Size) {
                ordered=descending?items.OrderByDescending(Size).ThenByDescending(p=>p,names):items.OrderBy(Size).ThenBy(p=>p,names);
            } else if(sort==PhotoSort.Type) {
                Func<Photo,string> kind=p=>Path.GetExtension(p.Name).ToLowerInvariant();
                ordered=descending?items.OrderByDescending(kind,StringComparer.Ordinal).ThenByDescending(p=>p,names):
                    items.OrderBy(kind,StringComparer.Ordinal).ThenBy(p=>p,names);
            } else if(sort==PhotoSort.Modified) {
                ordered=descending?items.OrderByDescending(Modified).ThenByDescending(p=>p,names):items.OrderBy(Modified).ThenBy(p=>p,names);
            } else ordered=descending?items.OrderByDescending(p=>p,names):items.OrderBy(p=>p,names);
            return ordered.ThenBy(p=>p.Folder,StringComparer.OrdinalIgnoreCase).ToList();
        }
        // Identifies the original a cover belongs to, in any view.
        internal static string CoverFamily(Photo photo) {return Path.Combine(Path.GetDirectoryName(photo.Folder),RecordBase(photo));}
        // Automatic order: covers cut from outside pictures first (in the order they were added), then the covers made
        // here by where each scene is in the video, then by when the cover was made.
        // Until every scene time of an original is known, its covers made here keep the order they were made in.
        // Once the person has arranged the covers of an original, that order stays and newer covers follow at the end.
        // A hidden main tile (it shows the original) leads.
        internal static List<Photo> InCoverOrder(IEnumerable<Photo> covers) {
            var list=covers.ToList();
            // A hidden cover's place doesn't count (the folder view lists hidden covers too, other views don't).
            Func<Photo,bool> placed=p=>p.HasCrop && !p.ExcludedFromCollection && p.Rank.HasValue;
            bool arranged=list.Any(placed);
            bool scenes=list.Where(p=>p.HasCrop && !p.IsOutside).All(p=>p.SceneMs.HasValue);
            return list.OrderBy(p=>p.HasCrop && !p.ExcludedFromCollection?1:0)
                .ThenBy(p=>!p.HasCrop?0:arranged?(placed(p)?0:1):(p.IsOutside?0:1))
                .ThenBy(p=>arranged && placed(p)?p.Rank.Value:0)
                .ThenBy(p=>!arranged && scenes && p.HasCrop && !p.IsOutside?p.SceneMs.Value:0)
                .ThenBy(p=>p.CoverCreated).ThenBy(p=>p.Folder,StringComparer.OrdinalIgnoreCase).ToList();
        }
        // Moves a cover among the shown covers of its original and remembers the whole order.
        // steps: -1 one place forward, +1 one place back, Int32.MinValue to the very front (it becomes the first cover).
        // Returns false when the cover is already there.
        internal static bool ArrangeCover(Photo cover,int steps) {
            lock(SharedCoverFiles.MetadataLock) {
                var all=CoversOf(cover);
                var shown=InCoverOrder(all.Where(p=>p.HasCrop && !p.ExcludedFromCollection));
                int from=shown.FindIndex(p=>String.Equals(p.Folder,cover.Folder,StringComparison.OrdinalIgnoreCase));
                if(from<0)return false;
                int to=steps==Int32.MinValue?0:Math.Max(0,Math.Min(shown.Count-1,from+steps));
                if(to==from)return false;
                var moved=shown[from];shown.RemoveAt(from);shown.Insert(to,moved);
                var ranks=shown.Select((p,i)=>Tuple.Create(p.Folder,(double?)(i+1))).ToList();
                // A hidden cover forgets its place: shown again later, it follows the arranged ones instead of sharing a place.
                ranks.AddRange(all.Where(p=>p.HasCrop && p.ExcludedFromCollection && p.Rank.HasValue).Select(p=>Tuple.Create(p.Folder,(double?)null)));
                WriteRanks(ranks);
                return true;
            }
        }
        // Back to the automatic order: every cover of the original forgets the place it was given.
        internal static void ForgetArrangement(Photo cover) {
            lock(SharedCoverFiles.MetadataLock) {
                WriteRanks(CoversOf(cover).Where(p=>p.Rank.HasValue).Select(p=>Tuple.Create(p.Folder,(double?)null)).ToList());
            }
        }
        // Every record is written to a spare file first and only then swapped in, so a failure part-way
        // (a full disk, for example) leaves the old order as it was. If a swap fails (a record is locked),
        // the records already swapped are put back from their backups.
        static void WriteRanks(List<Tuple<string,double?>> ranks) {
            var parts=new List<Tuple<string,string>>();
            var swapped=new List<Tuple<string,string>>(); // backup, record
            try {
                foreach(var item in ranks) {
                    string metadata=Path.Combine(item.Item1,"photo.xml");
                    string part=Path.Combine(item.Item1,"meta-"+Guid.NewGuid().ToString("N")+".tmp");
                    var xml=XElement.Load(metadata);
                    xml.SetAttributeValue("rank",item.Item2.HasValue?Num(item.Item2.Value):null);
                    xml.Save(part);
                    parts.Add(Tuple.Create(part,metadata));
                }
                try {
                    foreach(var pair in parts) {
                        string backup=pair.Item1+".old";
                        File.Replace(pair.Item1,pair.Item2,backup);
                        swapped.Add(Tuple.Create(backup,pair.Item2));
                    }
                } catch {
                    foreach(var done in swapped)try {File.Copy(done.Item1,done.Item2,true);} catch(IOException quiet) {ErrorLog.Quiet(quiet);} catch(UnauthorizedAccessException quiet) {ErrorLog.Quiet(quiet);}
                    throw;
                }
            } finally {
                foreach(var pair in parts)try {File.Delete(pair.Item1);} catch(IOException quiet) {ErrorLog.Quiet(quiet);} catch(UnauthorizedAccessException quiet) {ErrorLog.Quiet(quiet);}
                foreach(var done in swapped)try {File.Delete(done.Item1);} catch(IOException quiet) {ErrorLog.Quiet(quiet);} catch(UnauthorizedAccessException quiet) {ErrorLog.Quiet(quiet);}
            }
        }
        // The place a record has right now on disk. A window that has been open for a while may still hold an older one.
        static double? CurrentRank(string folder,double? known) {
            try {
                string metadata=Path.Combine(folder,"photo.xml");
                lock(SharedCoverFiles.MetadataLock) {
                    if(!File.Exists(metadata))return known;
                    double? rank=(double?)XElement.Load(metadata).Attribute("rank");
                    return rank.HasValue && (Double.IsNaN(rank.Value) || Double.IsInfinity(rank.Value))?null:rank;
                }
            } catch(Exception) {return known;}
        }
        // Older covers picked from the full frame list have only a frame number. Their scene time is worked out
        // once in the background and kept in the record; the new order shows the next time the folder is read.
        // single: also for an original with one cover (when scene times are shown on the tiles).
        internal void FillSceneTimes(System.Threading.CancellationToken token,bool single=false) {
            try {
                int skipped;
                foreach(var family in SavedCovers(out skipped).GroupBy(p=>CoverFamily(p),StringComparer.OrdinalIgnoreCase)) {
                    if(family.Count()<2 && !single)continue;
                    foreach(var cover in family.Where(p=>p.HasCrop && !p.IsOutside && !p.SceneMs.HasValue)) {
                        token.ThrowIfCancellationRequested();
                        try {
                            if(File.Exists(cover.Original))SaveSceneTime(cover,MotionMaker.MomentOf(cover,cover.Original));
                        } catch(OperationCanceledException) {throw;} catch(Exception quiet) {ErrorLog.Quiet(quiet);}
                    }
                }
            } catch(Exception quiet) {ErrorLog.Quiet(quiet);}
        }
        static void SaveSceneTime(Photo cover,double ms) {
            string metadata=Path.Combine(cover.Folder,"photo.xml");
            string part=Path.Combine(cover.Folder,"meta-"+Guid.NewGuid().ToString("N")+".tmp");
            lock(SharedCoverFiles.MetadataLock) {
                try {
                    var xml=XElement.Load(metadata);
                    xml.SetAttributeValue("sceneMs",Num(ms));
                    xml.Save(part);
                    File.Replace(part,metadata,null);
                } finally {File.Delete(part);}
            }
            cover.SavedSceneMs=ms;
        }
        // Extra covers of one original live beside its main record as "p-<key>-<id>" folders.
        // The folder grid shows only the main record; the cover collection shows every record.
        internal static string RecordBase(Photo photo) {
            string name=Path.GetFileName(photo.Folder);
            return name.Length>34?name.Substring(0,34):name;
        }
        internal static Photo NewCoverRecord(Photo photo,string original) {
            string folder=Path.Combine(Path.GetDirectoryName(photo.Folder),RecordBase(photo)+"-"+Guid.NewGuid().ToString("N").Substring(0,10));
            // A cover cut from an outside picture has no scene of its own to pass on.
            return new Photo {Folder=folder,Name=photo.Name,Original=original,Linked=true,Order=photo.Order,
                FrameIndex=photo.IsOutside?0:photo.FrameIndex,FrameTime=photo.IsOutside?null:photo.FrameTime,
                FrameTimeOffset=photo.IsOutside?0:photo.FrameTimeOffset};
        }
        // When an original's first cover (its main record "p-<key>") is gone but other covers of it remain,
        // the next shown one in scene order takes the first place: its record folder "p-<key>-<id>" is renamed to "p-<key>".
        // The cover files stay where they are (the record keeps their full paths). Hidden covers are not moved up.
        // Returns the folder the promoted cover had, or null when nothing was moved up.
        internal static string PromoteExtraCover(string mainFolder,Func<string,bool> skip=null) {
            lock(SharedCoverFiles.MetadataLock) {
                if(Directory.Exists(mainFolder))return null;
                string parent=Path.GetDirectoryName(mainFolder),basis=Path.GetFileName(mainFolder);
                if(!Directory.Exists(parent))return null;
                var candidates=new List<Photo>();
                foreach(string folder in Directory.GetDirectories(parent,basis+"-*")) {
                    if(Path.GetFileName(folder).Length<=basis.Length+1 || (skip!=null && skip(folder)))continue;
                    try {
                        if(!File.Exists(Path.Combine(folder,"photo.xml")))continue;
                        var cover=Read(folder);
                        if(!cover.HasCrop || cover.ExcludedFromCollection)continue;
                        candidates.Add(cover);
                    } catch(Exception quiet) {ErrorLog.Quiet(quiet);}
                }
                if(candidates.Count==0)return null;
                var best=InCoverOrder(candidates)[0];
                Directory.Move(best.Folder,mainFolder);
                return best.Folder;
            }
        }
        // Every saved cover of the same original, including hidden ones.
        internal static List<Photo> CoversOf(Photo photo) {
            var covers=new List<Photo>();
            string parent=Path.GetDirectoryName(photo.Folder);
            if(!Directory.Exists(parent))return covers;
            foreach(string folder in Directory.GetDirectories(parent,RecordBase(photo)+"*")) {
                try {if(File.Exists(Path.Combine(folder,"photo.xml")))covers.Add(Read(folder,photo.Linked?photo.Original:null));}
                catch(Exception quiet) {ErrorLog.Quiet(quiet);}
            }
            return covers;
        }
        // Removes a cover's record after its files went to the Recycle Bin.
        internal static void RemoveRecord(Photo photo) {
            lock(SharedCoverFiles.MetadataLock) {
                if(Directory.Exists(photo.Folder))Directory.Delete(photo.Folder,true);
            }
        }
        public void SetCollectionExcluded(Photo photo,bool excluded) {
            string marker=Path.Combine(photo.Folder,ExclusionFile);
            if(excluded) {
                using(File.Open(marker,FileMode.OpenOrCreate,FileAccess.Write,FileShare.ReadWrite)) { }
            } else File.Delete(marker);
            photo.ExcludedFromCollection=excluded;
        }
        static readonly HashSet<string> ImageExtensions=new HashSet<string>(PictureTypes.Extensions,StringComparer.OrdinalIgnoreCase);
        [DllImport("shlwapi.dll",CharSet=CharSet.Unicode,ExactSpelling=true)]
        static extern int StrCmpLogicalW(string a,string b);
        static List<Photo> FolderPhotos(Album album,out int skipped,CancellationToken token=default(CancellationToken),Action<Photo> found=null) {
            skipped=0;
            var edits=new HashSet<string>(Directory.GetDirectories(album.Folder,"p-*"),StringComparer.OrdinalIgnoreCase);
            var photos=new List<Photo>();
            // Extra covers of one original live in "p-<key>-<id>" folders beside its main "p-<key>" record.
            var extraFolders=edits.Select(f=>Path.GetFileName(f)).Where(n=>n.Length>35 && n[34]=='-')
                .ToLookup(n=>n.Substring(0,34),n=>Path.Combine(album.Folder,n),StringComparer.OrdinalIgnoreCase);
            foreach(string path in Directory.EnumerateFiles(album.Source)) {
                token.ThrowIfCancellationRequested();
                if(!ImageExtensions.Contains(Path.GetExtension(path)) && !VideoFrames.Supports(path)) continue;
                if(SharedCoverFiles.IsBesideCover(path)) continue;
                string name=Path.GetFileName(path), folder=Path.Combine(album.Folder,"p-"+Key(name));
                bool added=false,damaged=false;
                if(edits.Contains(folder) && File.Exists(Path.Combine(folder,"photo.xml"))) {
                    try { photos.Add(Read(folder,path));added=true; }
                    catch(Exception) { skipped++;damaged=true; }
                }
                if(!added && extraFolders["p-"+Key(name)].Any()) {
                    // The first cover was deleted earlier while other covers remained: the earliest one moves up.
                    try {
                        if(PromoteExtraCover(folder)!=null){photos.Add(Read(folder,path));added=true;}
                    } catch(Exception quiet) {ErrorLog.Quiet(quiet);}
                }
                if(!added)photos.Add(new Photo {Folder=folder,Name=name,Original=path,Linked=true,RecordDamaged=damaged});
                if(found!=null)found(photos[photos.Count-1]);
                foreach(string extra in extraFolders["p-"+Key(name)]) {
                    try {
                        if(!File.Exists(Path.Combine(extra,"photo.xml")))continue;
                        var cover=Read(extra,path);
                        if(cover.ExcludedFromCollection || !cover.HasCrop)continue; // hidden covers show only in "Show hidden covers again"
                        cover.LeadFolder=folder;photos.Add(cover);
                    } catch(Exception) { skipped++; }
                }
            }
            return photos;
        }
        internal static int CompareFolderPhotos(Photo a,Photo b) {
            int order=StrCmpLogicalW(a.Name,b.Name);
            return order!=0?order:String.CompareOrdinal(a.Name,b.Name);
        }
        public Photo Reframe(Photo old, BitmapSource preview, Rect crop,int? frameIndex=null,double rotation=0,
            double? frameTime=null,int frameTimeOffset=0) {
            return Reframe(old,preview,crop,frameIndex,rotation,frameTime,frameTimeOffset,
                CoverOutputMode.SavedImage,1920,System.Threading.CancellationToken.None);
        }
        internal Photo Reframe(Photo old,BitmapSource preview,Rect crop,int? frameIndex,double rotation,
            double? frameTime,int frameTimeOffset,CoverOutputMode mode,int maxHeight,System.Threading.CancellationToken token,
            string coverDirectory=null,int brightness=0,CoverPlace place=CoverPlace.CoverFolder,string picture=null,string drawings=null) {
            drawings=MotionShape.Normalize(drawings??old.Drawings);
            // picture: a picture made outside LoveMyShelf that the cover is cut from, instead of the original.
            // The original stays the file the cover belongs to, so where the cover is saved and how it is found again don't change.
            if(coverDirectory!=null){mode=CoverOutputMode.Native;maxHeight=0;}
            bool native=mode==CoverOutputMode.Native;
            int selectedFrame=frameIndex??old.FrameIndex;
            double? selectedTime=frameIndex.HasValue?frameTime:old.FrameTime;
            int selectedOffset=frameIndex.HasValue?frameTimeOffset:old.FrameTimeOffset;
            var source=preview;
            string original=old.Original;
            bool originalAway=false;
            if(picture!=null) {
                selectedFrame=0;selectedTime=null;selectedOffset=0;
                // Editing such a cover works while the original's drive is unplugged: the picture is all it needs.
                try {original=OriginalLocator.Resolve(this,old,token);}
                catch(FileNotFoundException) {if(!old.HasCrop)throw;originalAway=true;}
                source=Pictures.Load(picture);
            } else if(native) {
                original=OriginalLocator.Resolve(this,old,token);
                source=AnimationFrames.Supports(original)?
                    AnimationFrames.SavedFrame(original,new Photo {FrameIndex=selectedFrame,FrameTime=selectedTime,FrameTimeOffset=selectedOffset},0,token):
                    Pictures.Load(original);
            }
            token.ThrowIfCancellationRequested();
            double? sceneMs=picture!=null || selectedTime.HasValue?null:selectedFrame==old.FrameIndex?old.SavedSceneMs:null;
            if(picture==null && !selectedTime.HasValue && !sceneMs.HasValue && selectedFrame>0 && AnimationFrames.Supports(original)) {
                try {sceneMs=MotionMaker.MomentOf(new Photo {FrameIndex=selectedFrame},original);}
                catch(OperationCanceledException) {throw;} catch(Exception quiet) {ErrorLog.Quiet(quiet);}
            }
            Directory.CreateDirectory(old.Folder);
            string id = Guid.NewGuid().ToString("N");
            string thumb = "grid-"+id+".jpg", part = Path.Combine(old.Folder,"meta-"+id+".tmp");
            string newThumb=Path.Combine(old.Folder,thumb);
            string display=Path.ChangeExtension(newThumb,".view.png");
            string coverFile=null;
            Photo previous=old;
            bool fellBack=false;
            // The outside picture is copied into the record folder once; later edits of the cover reuse that copy.
            string pictureName=null,pictureCopy=null;
            try {
                if(picture!=null) {
                    if(String.Equals(Path.GetDirectoryName(Path.GetFullPath(picture)),Path.GetFullPath(old.Folder),StringComparison.OrdinalIgnoreCase))
                        pictureName=Path.GetFileName(picture);
                    else {
                        pictureName="outside-"+id+Path.GetExtension(picture).ToLowerInvariant();
                        pictureCopy=Path.Combine(old.Folder,pictureName);
                        File.Copy(picture,pictureCopy);
                        File.SetAttributes(pictureCopy,FileAttributes.Normal);
                    }
                }
                if(coverDirectory==null) {
                    using(var output=File.Create(newThumb))Pictures.Thumbnail(preview,crop,output,rotation,brightness,drawings);
                    using(var output=File.Create(display))CoverImages.Save(source,crop,rotation,output,maxHeight,native,token,brightness,drawings);
                } else {
                    if(old.Linked && !originalAway)OriginalLocator.Remember(old,original,required:true);
                    string folderTarget=SharedCoverFiles.TargetFolder(place,coverDirectory,original,old.Linked);
                    bool besideEarlier=false;
                    if(originalAway) {
                        // The edited cover goes where the cover it replaces is, or to the cover folder.
                        string earlier=old.CoverFile==null?null:Path.GetDirectoryName(old.CoverFile);
                        bool there=earlier!=null && Directory.Exists(earlier);
                        folderTarget=there?earlier:coverDirectory;
                        // It keeps the kind of name the replaced cover had, so a cover beside its original is still known as a cover.
                        besideEarlier=there && SharedCoverFiles.IsBesideCover(old.CoverFile);
                        if(!there && place!=CoverPlace.CoverFolder)fellBack=true;
                    }
                    bool beside=originalAway?besideEarlier:folderTarget!=coverDirectory && place==CoverPlace.BesideOriginal;
                    try {
                        coverFile=SharedCoverFiles.Write(folderTarget,old.Name,old.Folder,id,source,crop,rotation,token,brightness,beside:beside,drawings:drawings);
                    } catch(Exception ex) {
                        // A phone, a read-only or an offline cloud folder: keep the cover in the cover folder instead.
                        if(folderTarget==coverDirectory || token.IsCancellationRequested ||
                            !(ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException))throw;
                        coverFile=SharedCoverFiles.Write(coverDirectory,old.Name,old.Folder,id,source,crop,rotation,token,brightness,drawings:drawings);
                        fellBack=true;
                    }
                }
                token.ThrowIfCancellationRequested();
                long coverCreated=old.CoverCreated>0?old.CoverCreated:DateTime.UtcNow.Ticks;
                WriteMeta(part,old.Name,Path.GetFileName(old.Original),thumb,crop,old.Order,coverCreated,selectedFrame,
                    rotation,selectedTime,selectedOffset,coverFile,brightness,sceneMs,pictureName,CurrentRank(old.Folder,old.Rank),
                    Covers.AspectOf(crop,(preview??source).PixelWidth,(preview??source).PixelHeight),drawings);
                string target=Path.Combine(old.Folder,"photo.xml");
                lock(SharedCoverFiles.MetadataLock) {
                    if(File.Exists(target)) {
                        if(old.HasCrop) {
                            previous=Read(old.Folder,old.Linked?original:null);
                            // Editing the JPG drawing alone leaves the separately made moving cover in place.
                            if(previous.MotionFile!=null && (previous.Drawings??"")!=drawings && previous.Crop==crop &&
                                previous.Rotation==rotation && previous.Brightness==brightness && previous.FrameIndex==selectedFrame &&
                                previous.FrameTime==selectedTime && previous.FrameTimeOffset==selectedOffset && previous.Picture==pictureName) {
                                var current=XElement.Load(target);
                                var next=XElement.Load(part);
                                next.SetAttributeValue("motionFile",(string)current.Attribute("motionFile"));
                                next.SetAttributeValue("motionSettings",(string)current.Attribute("motionSettings"));
                                next.Save(part);
                            }
                        }
                        File.Replace(part,target,null);
                    } else File.Move(part,target);
                }
            } catch {
                File.Delete(part); File.Delete(newThumb); File.Delete(display);
                if(coverFile!=null)File.Delete(coverFile);
                if(pictureCopy!=null)try {File.Delete(pictureCopy);} catch(IOException quiet) {ErrorLog.Quiet(quiet);} catch(UnauthorizedAccessException quiet) {ErrorLog.Quiet(quiet);}
                throw;
            }
            SharedCoverFiles.RemoveSuperseded(previous);
            // A cover remade from something else leaves no stray copy of the picture it used before.
            if(old.Picture!=null && !String.Equals(old.Picture,pictureName,StringComparison.OrdinalIgnoreCase)) {
                try {File.Delete(old.PictureFile);} catch(IOException quiet) {ErrorLog.Quiet(quiet);} catch(UnauthorizedAccessException quiet) {ErrorLog.Quiet(quiet);}
            }
            if(old.Thumb!=null) {
                try { File.Delete(old.Thumb);File.Delete(old.LegacyImage); }
                catch(IOException quiet) {ErrorLog.Quiet(quiet);} catch(UnauthorizedAccessException quiet) {ErrorLog.Quiet(quiet);}
            }
            if(old.Linked && coverDirectory==null) OriginalLocator.Remember(old,original);
            var result=Read(old.Folder,old.Linked?original:null);
            result.SavedToFallback=fellBack;
            return result;
        }
    }
}
