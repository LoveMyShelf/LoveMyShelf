using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace LoveMyShelf {
    public static class Pictures {
        public static BitmapSource Load(string path, int maxSide = 0) {
            if(AnimationFrames.Supports(path)) return AnimationFrames.Still(path,0,maxSide);
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
            if(brightness!=0) {
                dc.DrawRectangle(brightness>0?Brushes.White:Brushes.Black,null,bounds);
                dc.PushOpacity(1-Math.Abs(brightness)/100.0);
            }
            dc.DrawImage(source,bounds);
            if(brightness!=0)dc.Pop();
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
            return photo.Rotation==0?TrimPreview(thumb,photo.Crop):thumb;
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
        internal static void Thumbnail(BitmapSource source, Rect fraction, Stream output,double rotation=0,int brightness=0) {
            var encoder = new JpegBitmapEncoder { QualityLevel = 88 };
            encoder.Frames.Add(BitmapFrame.Create(rotation==0 && brightness==0?RenderCrop(source,fraction,252,448):
                RenderRotatedCover(source,fraction,rotation,252,448,brightness)));
            encoder.Save(output);
        }
    }
    public class Album {
        public readonly string Folder, Name, Source;
        public Album(string folder,string name,string source=null) { Folder = folder; Name = name; Source=source; }
    }
    public class Photo {
        public string Folder, Original, Thumb, Name;
        internal string CoverFile;
        internal long CoverFileTicks;
        // Set only on the result of a save that could not write to the chosen place.
        internal bool SavedToFallback;
        // An optional GIF (or WebP/MP4) made from this cover with Make GIF.
        internal string MotionFile;
        // The part and choices the GIF was made with (null for one made before these were saved).
        internal MotionRequest MotionSettings;
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
        public double Rotation;
        internal int Brightness;
        public bool Linked, ExcludedFromCollection;
        public int FrameIndex;
        internal double? FrameTime;
        internal int FrameTimeOffset;
        public bool HasCrop { get { return !Crop.IsEmpty; } }
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
        const string HiddenTabFile = "hide-folder-tab";
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public GalleryStore(string root) { Root = root; Directory.CreateDirectory(root); }
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
        static string Num(double n) { return n.ToString("R",Inv); }
        static void WriteMeta(string path, string name, string original, string thumb, Rect crop, long order,long coverCreated,
            int frameIndex,double rotation,double? frameTime,int frameTimeOffset,string coverFile,int brightness) {
            new XElement("photo",new XAttribute("name",name),new XAttribute("original",original),
                new XAttribute("thumb",thumb), new XAttribute("order",order),new XAttribute("coverCreated",coverCreated),new XAttribute("frame",frameIndex),
                new XAttribute("x",Num(crop.X)),new XAttribute("y",Num(crop.Y)),
                new XAttribute("w",Num(crop.Width)),new XAttribute("h",Num(crop.Height)),
                rotation==0?null:new XAttribute("rotation",Num(rotation)),
                brightness==0?null:new XAttribute("brightness",brightness),
                frameTime.HasValue?new XAttribute("frameTimeMs",Num(frameTime.Value)):null,
                frameTimeOffset>0?new XAttribute("frameTimeOffset",frameTimeOffset):null,
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
                throw new InvalidDataException(L.T("The saved cover coordinates are invalid."));
            if(Double.IsNaN(rotation) || Double.IsInfinity(rotation))
                throw new InvalidDataException(L.T("The saved cover rotation is invalid."));
            if(frameTime.HasValue && (Double.IsNaN(frameTime.Value) || Double.IsInfinity(frameTime.Value) || frameTime.Value<0 || frameTimeOffset<0))
                throw new InvalidDataException(L.T("Could not read video frame times."));
            return new Photo { Folder=folder,Name=(string)n.Attribute("name"), Crop=r,Order=(long)n.Attribute("order"),
                CoverCreated=coverCreated,
                Rotation=CropSurface.NormalizeRotation(rotation),
                Brightness=Math.Max(-100,Math.Min(100,(int?)n.Attribute("brightness")??0)),
                FrameIndex=(int?)n.Attribute("frame")??0,
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
            var sorted=SortPhotos(all.Where(p=>!p.IsExtra),sort,descending);
            // Extra covers follow the main record of their original, oldest first.
            var extras=all.Where(p=>p.IsExtra).GroupBy(p=>p.LeadFolder,StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g=>g.Key,g=>g.OrderBy(p=>p.CoverCreated).ThenBy(p=>p.Folder,StringComparer.OrdinalIgnoreCase).ToList(),StringComparer.OrdinalIgnoreCase);
            if(extras.Count==0)return sorted;
            var result=new List<Photo>(sorted.Count+all.Count-sorted.Count);
            foreach(var photo in sorted) {
                result.Add(photo);
                List<Photo> more;
                if(extras.TryGetValue(photo.Folder,out more))result.AddRange(more);
            }
            return result;
        }
        internal List<Photo> UnfinishedCovers(out int skipped) {
            skipped=0;
            var items=new List<Photo>();
            foreach(var album in VisibleAlbums()) {
                try {
                    int unreadable;
                    items.AddRange(FolderPhotos(album,out unreadable).Where(p=>!p.HasCrop));
                    skipped+=unreadable;
                } catch(IOException) {skipped++;}
                catch(UnauthorizedAccessException) {skipped++;}
            }
            return items;
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
            return SortPhotos(SavedCovers(out skipped).Where(p=>!p.ExcludedFromCollection),sort,descending);
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
        internal static List<Photo> SortPhotos(IEnumerable<Photo> items,PhotoSort sort,bool descending) {
            var names=Comparer<Photo>.Create(CompareFolderPhotos);
            IOrderedEnumerable<Photo> ordered;
            if(sort==PhotoSort.CoverOrder) {
                // Unfinished items have no work date and follow dated covers in either direction.
                var coversFirst=items.OrderByDescending(p=>p.HasCrop);
                ordered=descending?coversFirst.ThenByDescending(p=>p.CoverCreated):coversFirst.ThenBy(p=>p.CoverCreated);
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
        // Extra covers of one original live beside its main record as "p-<key>-<id>" folders.
        // The folder grid shows only the main record; the cover collection shows every record.
        internal static string RecordBase(Photo photo) {
            string name=Path.GetFileName(photo.Folder);
            return name.Length>34?name.Substring(0,34):name;
        }
        internal static Photo NewCoverRecord(Photo photo,string original) {
            string folder=Path.Combine(Path.GetDirectoryName(photo.Folder),RecordBase(photo)+"-"+Guid.NewGuid().ToString("N").Substring(0,10));
            return new Photo {Folder=folder,Name=photo.Name,Original=original,Linked=true,Order=photo.Order,
                FrameIndex=photo.FrameIndex,FrameTime=photo.FrameTime,FrameTimeOffset=photo.FrameTimeOffset};
        }
        // When an original's first cover (its main record "p-<key>") is gone but other covers of it remain,
        // the earliest shown one takes the first place: its record folder "p-<key>-<id>" is renamed to "p-<key>".
        // The cover files stay where they are (the record keeps their full paths). Hidden covers are not moved up.
        // Returns the folder the promoted cover had, or null when nothing was moved up.
        internal static string PromoteExtraCover(string mainFolder,Func<string,bool> skip=null) {
            lock(SharedCoverFiles.MetadataLock) {
                if(Directory.Exists(mainFolder))return null;
                string parent=Path.GetDirectoryName(mainFolder),basis=Path.GetFileName(mainFolder);
                if(!Directory.Exists(parent))return null;
                Photo best=null;
                foreach(string folder in Directory.GetDirectories(parent,basis+"-*")) {
                    if(Path.GetFileName(folder).Length<=basis.Length+1 || (skip!=null && skip(folder)))continue;
                    try {
                        if(!File.Exists(Path.Combine(folder,"photo.xml")))continue;
                        var cover=Read(folder);
                        if(!cover.HasCrop || cover.ExcludedFromCollection)continue;
                        if(best==null || cover.CoverCreated<best.CoverCreated)best=cover;
                    } catch(Exception) {}
                }
                if(best==null)return null;
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
                catch(Exception) {}
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
        static readonly HashSet<string> ImageExtensions=new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            ".jpg",".jpeg",".jfif",".png",".bmp",".tif",".tiff",".gif",".webp"
        };
        [DllImport("shlwapi.dll",CharSet=CharSet.Unicode,ExactSpelling=true)]
        static extern int StrCmpLogicalW(string a,string b);
        static List<Photo> FolderPhotos(Album album,out int skipped) {
            skipped=0;
            var edits=new HashSet<string>(Directory.GetDirectories(album.Folder,"p-*"),StringComparer.OrdinalIgnoreCase);
            var photos=new List<Photo>();
            // Extra covers of one original live in "p-<key>-<id>" folders beside its main "p-<key>" record.
            var extraFolders=edits.Select(f=>Path.GetFileName(f)).Where(n=>n.Length>35 && n[34]=='-')
                .ToLookup(n=>n.Substring(0,34),n=>Path.Combine(album.Folder,n),StringComparer.OrdinalIgnoreCase);
            foreach(string path in Directory.EnumerateFiles(album.Source)) {
                if(!ImageExtensions.Contains(Path.GetExtension(path)) && !VideoFrames.Supports(path)) continue;
                if(SharedCoverFiles.IsBesideCover(path)) continue;
                string name=Path.GetFileName(path), folder=Path.Combine(album.Folder,"p-"+Key(name));
                bool added=false;
                if(edits.Contains(folder) && File.Exists(Path.Combine(folder,"photo.xml"))) {
                    try { photos.Add(Read(folder,path));added=true; }
                    catch(Exception) { skipped++; }
                }
                if(!added && extraFolders["p-"+Key(name)].Any()) {
                    // The first cover was deleted earlier while other covers remained: the earliest one moves up.
                    try {
                        if(PromoteExtraCover(folder)!=null){photos.Add(Read(folder,path));added=true;}
                    } catch(Exception) {}
                }
                if(!added)photos.Add(new Photo {Folder=folder,Name=name,Original=path,Linked=true});
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
            string coverDirectory=null,int brightness=0,CoverPlace place=CoverPlace.CoverFolder) {
            if(coverDirectory!=null){mode=CoverOutputMode.Native;maxHeight=0;}
            bool native=mode==CoverOutputMode.Native;
            int selectedFrame=frameIndex??old.FrameIndex;
            double? selectedTime=frameIndex.HasValue?frameTime:old.FrameTime;
            int selectedOffset=frameIndex.HasValue?frameTimeOffset:old.FrameTimeOffset;
            var source=preview;
            string original=old.Original;
            if(native) {
                original=OriginalLocator.Resolve(this,old,token);
                source=AnimationFrames.Supports(original)?
                    AnimationFrames.SavedFrame(original,new Photo {FrameIndex=selectedFrame,FrameTime=selectedTime,FrameTimeOffset=selectedOffset},0,token):
                    Pictures.Load(original);
            }
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(old.Folder);
            string id = Guid.NewGuid().ToString("N");
            string thumb = "grid-"+id+".jpg", part = Path.Combine(old.Folder,"meta-"+id+".tmp");
            string newThumb=Path.Combine(old.Folder,thumb);
            string display=Path.ChangeExtension(newThumb,".view.png");
            string coverFile=null;
            Photo previous=old;
            bool fellBack=false;
            try {
                if(coverDirectory==null) {
                    Pictures.Thumbnail(preview,crop,newThumb,rotation,brightness);
                    using(var output=File.Create(display))CoverImages.Save(source,crop,rotation,output,maxHeight,native,token,brightness);
                } else {
                    if(old.Linked)OriginalLocator.Remember(old,original,required:true);
                    string folderTarget=SharedCoverFiles.TargetFolder(place,coverDirectory,original,old.Linked);
                    bool beside=folderTarget!=coverDirectory && place==CoverPlace.BesideOriginal;
                    try {
                        coverFile=SharedCoverFiles.Write(folderTarget,old.Name,old.Folder,id,source,crop,rotation,token,brightness,beside:beside);
                    } catch(Exception ex) {
                        // A phone, a read-only or an offline cloud folder: keep the cover in the cover folder instead.
                        if(folderTarget==coverDirectory || token.IsCancellationRequested ||
                            !(ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException))throw;
                        coverFile=SharedCoverFiles.Write(coverDirectory,old.Name,old.Folder,id,source,crop,rotation,token,brightness);
                        fellBack=true;
                    }
                }
                token.ThrowIfCancellationRequested();
                long coverCreated=old.CoverCreated>0?old.CoverCreated:DateTime.UtcNow.Ticks;
                WriteMeta(part,old.Name,Path.GetFileName(old.Original),thumb,crop,old.Order,coverCreated,selectedFrame,
                    rotation,selectedTime,selectedOffset,coverFile,brightness);
                string target=Path.Combine(old.Folder,"photo.xml");
                lock(SharedCoverFiles.MetadataLock) {
                    if(File.Exists(target)) {
                        if(old.HasCrop)previous=Read(old.Folder,old.Linked?original:null);
                        File.Replace(part,target,null);
                    } else File.Move(part,target);
                }
            } catch {
                File.Delete(part); File.Delete(newThumb); File.Delete(display);
                if(coverFile!=null)File.Delete(coverFile);
                throw;
            }
            SharedCoverFiles.RemoveSuperseded(previous);
            if(old.Thumb!=null) {
                try { File.Delete(old.Thumb);File.Delete(old.LegacyImage); }
                catch(IOException) { } catch(UnauthorizedAccessException) { }
            }
            if(old.Linked && coverDirectory==null) OriginalLocator.Remember(old,original);
            var result=Read(old.Folder,old.Linked?original:null);
            result.SavedToFallback=fellBack;
            return result;
        }
    }
}
