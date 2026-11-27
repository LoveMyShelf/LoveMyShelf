using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace LoveMyShelf {
    internal static class SharedCoverFiles {
        internal static readonly object MetadataLock=new object();

        // Cover files saved next to originals: "photo_cover_0123456789abcdef.jpg", optionally followed by
        // "-upscaled", a " (2)" collision number, and the extension. The grid hides these.
        static readonly Regex BesideName=new Regex(@"_cover_[0-9a-f]{16}(-upscaled)?( \(\d+\))?\.(jpg|gif|webp|mp4)$",
            RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
        internal static bool IsBesideCover(string path) {return BesideName.IsMatch(Path.GetFileName(path));}
        // Cover files in the cover folder: "photo-cover-0123456789abcdef.jpg". The grid still shows these when
        // that folder is opened, but they are finished covers, so the unfinished collection leaves them out.
        static readonly Regex CoverFolderName=new Regex(@"-cover-[0-9a-f]{16}(-upscaled)?( \(\d+\))?\.(jpg|gif|webp|mp4)$",
            RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
        internal static bool IsCoverFile(string path) {
            string name=Path.GetFileName(path);
            return BesideName.IsMatch(name) || CoverFolderName.IsMatch(name);
        }

        // The folder a new cover goes to for the chosen place. Falls back to the cover folder
        // for older imported items, which have no original folder of their own.
        internal static string TargetFolder(CoverPlace place,string coverDirectory,string original,bool linked) {
            if(!linked || place==CoverPlace.CoverFolder || String.IsNullOrEmpty(original))return coverDirectory;
            string folder=Path.GetDirectoryName(original);
            if(String.IsNullOrEmpty(folder))return coverDirectory;
            return place==CoverPlace.Subfolder?Path.Combine(folder,Preferences.SubfolderName):folder;
        }

        internal static string Stem(string name,string folder,string revision,bool beside) {
            var stem=Path.GetFileNameWithoutExtension(name);
            int max=beside?120:64;
            if(stem.Length>max)stem=stem.Substring(0,max).TrimEnd();
            string id;
            using(var hash=SHA256.Create())id=BitConverter.ToString(hash.ComputeHash(
                Encoding.UTF8.GetBytes(folder+"\n"+revision)),0,8).Replace("-","").ToLowerInvariant();
            return stem+(beside?"_cover_":"-cover-")+id;
        }

        internal static string Write(string directory,string name,string folder,string revision,BitmapSource source,
            Rect crop,double rotation,CancellationToken token,int brightness,bool trimVerticalPadding=true,bool beside=false,string drawings=null,
            AdjustValues adjust=default(AdjustValues),BitmapSource eraseAi=null) {
            if(adjust.IsIdentity && brightness!=0)adjust.Brightness=brightness;
            Directory.CreateDirectory(directory);
            string part=Path.Combine(directory,".lovemyshelf-"+Guid.NewGuid().ToString("N")+".tmp");
            try {
                // A drawing is positioned in the full editor frame, including any padding the user saw.
                var image=CoverImages.NativeCover(source,crop,rotation,token,adjust.Brightness,trimVerticalPadding && String.IsNullOrEmpty(drawings),adjust);
                image=MotionDraw.Apply(image,drawings,eraseAi);
                using(var stream=File.Create(part)) {
                    var encoder=new JpegBitmapEncoder {QualityLevel=100};
                    encoder.Frames.Add(BitmapFrame.Create(image));
                    encoder.Save(stream);
                }
                return Publish(part,directory,Stem(name,folder,revision,beside),token);
            } finally {File.Delete(part);}
        }

        // Saves the edited picture as a standalone new file beside the original ("name_편집.jpg", " (2)" on collision).
        // Never overwrites the original or any existing file.
        internal static string WriteEditedJpg(string originalPath,BitmapSource source,Rect crop,double rotation,
            AdjustValues adjust,string drawings,BitmapSource eraseAi,CancellationToken token,string fallbackDirectory=null) {
            if(String.IsNullOrEmpty(originalPath))throw new ArgumentException("originalPath");
            string directory=Path.GetDirectoryName(originalPath);
            if(String.IsNullOrEmpty(directory))directory=fallbackDirectory;
            if(String.IsNullOrEmpty(directory))throw new InvalidOperationException(L.T("The source folder could not be found."));
            string stem=EditedStem(originalPath);
            try {
                return WriteEditedJpgTo(directory,stem,originalPath,source,crop,rotation,adjust,drawings,eraseAi,token);
            } catch(Exception ex) {
                if(String.IsNullOrEmpty(fallbackDirectory) || String.Equals(directory,fallbackDirectory,StringComparison.OrdinalIgnoreCase) ||
                    token.IsCancellationRequested || !(ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException))throw;
                return WriteEditedJpgTo(fallbackDirectory,stem,originalPath,source,crop,rotation,adjust,drawings,eraseAi,token);
            }
        }

        internal static string EditedStem(string originalPath) {
            string stem=Path.GetFileNameWithoutExtension(originalPath);
            if(String.IsNullOrEmpty(stem))stem="picture";
            if(stem.Length>120)stem=stem.Substring(0,120).TrimEnd();
            return stem+L.P("_edited","_편집");
        }

        static string WriteEditedJpgTo(string directory,string stem,string originalPath,BitmapSource source,Rect crop,double rotation,
            AdjustValues adjust,string drawings,BitmapSource eraseAi,CancellationToken token) {
            Directory.CreateDirectory(directory);
            string part=Path.Combine(directory,".lovemyshelf-"+Guid.NewGuid().ToString("N")+".tmp");
            try {
                var image=CoverImages.NativeCover(source,crop,rotation,token,adjust.Brightness,String.IsNullOrEmpty(drawings),adjust);
                image=MotionDraw.Apply(image,drawings,eraseAi);
                using(var stream=File.Create(part)) {
                    var encoder=new JpegBitmapEncoder {QualityLevel=98};
                    encoder.Frames.Add(BitmapFrame.Create(image));
                    encoder.Save(stream);
                }
                for(int i=1;;i++) {
                    token.ThrowIfCancellationRequested();
                    string path=Path.Combine(directory,stem+(i==1?"":" ("+i+")")+".jpg");
                    if(String.Equals(Path.GetFullPath(path),Path.GetFullPath(originalPath),StringComparison.OrdinalIgnoreCase))continue;
                    if(File.Exists(path) || Directory.Exists(path))continue;
                    try {File.Move(part,path);return path;}
                    catch(IOException) {if(!File.Exists(path) && !Directory.Exists(path))throw;}
                }
            } finally {
                try {if(File.Exists(part))File.Delete(part);} catch(IOException quiet) {ErrorLog.Quiet(quiet);} catch(UnauthorizedAccessException quiet) {ErrorLog.Quiet(quiet);}
            }
        }

        internal static string Publish(string temp,string directory,string stem,CancellationToken token,string extension=".jpg") {
            for(int i=1;;i++) {
                token.ThrowIfCancellationRequested();
                string path=Path.Combine(directory,stem+(i==1?"":" ("+i+")")+extension);
                try {File.Move(temp,path);return path;}
                catch(IOException) {if(!File.Exists(path) && !Directory.Exists(path))throw;}
            }
        }

        internal static Photo Migrate(GalleryStore store,Photo old,string directory,CancellationToken token) {
            if(old.CoverFile!=null)return null;
            string original=OriginalLocator.Resolve(store,old,token);
            var source=AnimationFrames.Supports(original)?AnimationFrames.SavedFrame(original,old,0,token):Pictures.Load(original);
            string metadata=Path.Combine(old.Folder,"photo.xml");
            string part=Path.Combine(old.Folder,Guid.NewGuid().ToString("N")+".tmp");
            string output=null;
            try {
                output=Write(directory,old.Name,old.Folder,Path.GetFileName(old.Thumb),source,old.Crop,old.Rotation,token,old.Brightness,
                    trimVerticalPadding:false,drawings:old.Drawings,adjust:old.Adjust,eraseAi:old.LoadEraseAiPatch(false));
                lock(MetadataLock) {
                    token.ThrowIfCancellationRequested();
                    var xml=XElement.Load(metadata);
                    if((string)xml.Attribute("thumb")!=Path.GetFileName(old.Thumb) || xml.Attribute("coverFile")!=null)return null;
                    if(xml.Attribute("coverCreated")==null)xml.SetAttributeValue("coverCreated",old.CoverCreated);
                    xml.SetAttributeValue("coverFile",output);
                    xml.SetAttributeValue("coverFileTicks",File.GetLastWriteTimeUtc(output).Ticks);
                    xml.Save(part);
                    File.Replace(part,metadata,null);
                    output=null;
                }
                return GalleryStore.Read(old.Folder,old.Linked?original:null);
            } finally {
                File.Delete(part);
                if(output!=null)File.Delete(output);
            }
        }

        internal static void RemoveSuperseded(Photo photo) {
            if(photo.CoverFile==null || photo.CoverFileTicks<=0)return;
            try {
                if(File.GetLastWriteTimeUtc(photo.CoverFile).Ticks==photo.CoverFileTicks)
                    File.Delete(photo.CoverFile);
            } catch(IOException quiet) {ErrorLog.Quiet(quiet);} catch(UnauthorizedAccessException quiet) {ErrorLog.Quiet(quiet);}
        }
    }
}
