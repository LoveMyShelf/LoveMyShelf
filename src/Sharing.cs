using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace LoveMyShelf {
    // Copy, Share and drag-out. LoveMyShelf only hands files to Windows; it never uploads anything itself.
    internal static class ShareTiles {
        internal static void AddMenu(ContextMenu menu,GalleryWindow owner,Photo photo) {
            var copy=new MenuItem {Header=L.T("Copy")};
            var share=new MenuItem {Header=L.T("Share…")};
            foreach(var pair in new[]{Tuple.Create(copy,false),Tuple.Create(share,true)}) {
                var parent=pair.Item1;bool sheet=pair.Item2;
                var choices=new List<ShareContent>{ShareContent.CoverAndOriginal,ShareContent.CoverOnly,ShareContent.OriginalOnly};
                if(!photo.HasCrop)choices=new List<ShareContent>{ShareContent.OriginalOnly};
                foreach(var choice in choices) {
                    var content=choice;
                    var item=new MenuItem {Header=Label(content,choices.Count==1)};
                    if(choices.Count>1 && content==owner.Settings.Share)item.FontWeight=FontWeights.SemiBold;
                    item.Click+=delegate {menu.IsOpen=false;Run(owner,photo,content,sheet);};
                    parent.Items.Add(item);
                }
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(copy);menu.Items.Add(share);
        }
        static string Label(ShareContent content,bool only) {
            if(only)return L.T("Original");
            return L.T(content==ShareContent.CoverAndOriginal?"Cover and original":content==ShareContent.CoverOnly?"Cover only":"Original only");
        }

        static async void Run(GalleryWindow owner,Photo photo,ShareContent content,bool sheet) {
            try {
                var files=await ShareFiles.Collect(owner,photo,content,true);
                if(files==null || files.Count==0)return;
                if(sheet)ShareSheet.Show(owner,files);
                else {
                    var list=new StringCollection();list.AddRange(files.ToArray());
                    System.Windows.Clipboard.SetFileDropList(list);
                    owner.Notify(L.T("Copied"),L.T("Press Ctrl+V in a chat window or a folder to paste."),null,NoticeKind.Done);
                }
            } catch(OperationCanceledException) {}
            catch(Exception ex) {owner.Notify(L.T("Something went wrong"),ex.Message,null,NoticeKind.Warning);}
        }

        // Copy or Share for several selected tiles, using the default from Options → Sharing.
        // Large videos are asked about once for all of them.
        internal static async void RunMany(GalleryWindow owner,List<Photo> photos,bool sheet) {
            try {
                var files=new List<string>();
                bool? clip=null;
                foreach(var photo in photos) {
                    var content=photo.HasCrop?owner.Settings.Share:ShareContent.OriginalOnly;
                    files.AddRange(await ShareFiles.Collect(owner,photo,content,true,()=>{
                        if(clip==null) {
                            var dialog=new LargeVideoWindow(owner,L.F("{0:N0} items",photos.Count),0);
                            if(dialog.ShowDialog()!=true)throw new OperationCanceledException();
                            clip=dialog.WantsClip;
                        }
                        return clip.Value;
                    }));
                }
                files=files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if(files.Count==0)return;
                if(sheet)ShareSheet.Show(owner,files);
                else {
                    var list=new StringCollection();list.AddRange(files.ToArray());
                    System.Windows.Clipboard.SetFileDropList(list);
                    owner.Notify(L.T("Copied"),L.T("Press Ctrl+V in a chat window or a folder to paste."),null,NoticeKind.Done);
                }
            } catch(OperationCanceledException) {}
            catch(Exception ex) {owner.Notify(L.T("Something went wrong"),ex.Message,null,NoticeKind.Warning);}
        }

        // Dragging a tile out of the window hands over the files right away, so large videos go as they are.
        internal static void AttachDrag(Button tile,GalleryWindow owner,Photo photo) {
            Point? down=null;
            tile.PreviewMouseLeftButtonDown+=(s,e)=>{down=e.GetPosition(tile);};
            tile.PreviewMouseLeftButtonUp+=delegate {down=null;};
            tile.PreviewMouseMove+=(s,e)=>{
                if(down==null || e.LeftButton!=MouseButtonState.Pressed)return;
                var moved=e.GetPosition(tile)-down.Value;
                if(Math.Abs(moved.X)<SystemParameters.MinimumHorizontalDragDistance*2 &&
                    Math.Abs(moved.Y)<SystemParameters.MinimumVerticalDragDistance*2)return;
                down=null;
                // Dragging a chosen tile takes every chosen tile with it, like File Explorer.
                var targets=owner.DragTargets(photo);
                var files=new List<string>();
                foreach(var target in targets)
                    files.AddRange(ShareFiles.Immediate(target,target.HasCrop?owner.Settings.Share:ShareContent.OriginalOnly));
                files=files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if(files.Count==0)return;
                tile.ReleaseMouseCapture();
                e.Handled=true;
                var data=new DataObject(DataFormats.FileDrop,files.ToArray());
                // Love My Shelf itself (the folder tree, folder tiles, another window) also gets the originals, to move them
                // into a folder. Its "move" is the Link effect, which File Explorer and chat apps don't pick on their own
                // (they still copy), so it is always offered when there are originals.
                var originals=targets.Where(t=>t.Linked && File.Exists(t.Original)).Select(t=>t.Original).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var allowed=DragDropEffects.Copy;
                if(originals.Count>0) {
                    data.SetData(GalleryWindow.MoveFormat,String.Join("\n",originals));
                    allowed|=DragDropEffects.Link;
                }
                try {
                    var done=DragDrop.DoDragDrop(tile,data,allowed);
                    // Let go where nothing took it (over its own window): maybe a range was meant — say how.
                    if(done==DragDropEffects.None && owner.IsMouseOver)owner.NoteDragWithoutDrop();
                } catch(COMException) {}
            };
        }
    }

    internal static class ShareFiles {
        // Files that exist right now, without asking anything. Used for dragging.
        internal static List<string> Immediate(Photo photo,ShareContent content) {
            var files=new List<string>();
            if(content!=ShareContent.OriginalOnly)files.AddRange(Covers(photo));
            if(content!=ShareContent.CoverOnly && File.Exists(photo.Original))files.Add(photo.Original);
            return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        static IEnumerable<string> Covers(Photo photo) {
            if(!photo.HasCrop)yield break;
            if(File.Exists(photo.ActiveCoverFile))yield return photo.ActiveCoverFile;
            if(photo.HasMotion)yield return photo.MotionFile;
        }

        // Covers and/or the original. A video larger than the limit asks: send as is, or a short clip around the cover.
        internal static async Task<List<string>> Collect(GalleryWindow owner,Photo photo,ShareContent content,bool ask,Func<bool> decide=null) {
            if(!photo.HasCrop)content=ShareContent.OriginalOnly;
            var files=new List<string>();
            if(content!=ShareContent.OriginalOnly) {
                files.AddRange(Covers(photo));
                if(files.Count==0 && content==ShareContent.CoverOnly)throw new FileNotFoundException(L.T("The saved cover file can't be found."));
            }
            if(content!=ShareContent.CoverOnly) {
                string original=photo.Original;
                if(photo.Linked && photo.HasCrop)
                    original=await Task.Run(()=>OriginalLocator.Resolve(owner.Store,photo,CancellationToken.None));
                if(!File.Exists(original))throw new FileNotFoundException(L.T("The original file is unavailable."),original);
                long limit=(long)owner.Settings.ShareLimitMB*1024*1024;
                long size=new FileInfo(original).Length;
                if(ask && VideoFrames.Supports(original) && size>limit) {
                    bool wantsClip;
                    if(decide!=null)wantsClip=decide();
                    else {
                        var dialog=new LargeVideoWindow(owner,Path.GetFileName(original),size);
                        if(dialog.ShowDialog()!=true)throw new OperationCanceledException();
                        wantsClip=dialog.WantsClip;
                    }
                    if(wantsClip) {
                        double center=photo.FrameTime??0;
                        string folder=Path.Combine(owner.Store.Root,"Share");
                        var working=owner.Notify(L.T("Making a short clip…"),L.T("This takes a few seconds."),null,NoticeKind.Busy);
                        try {original=await Task.Run(()=>Clip(original,center,folder));}
                        finally {working.Dismiss();}
                    }
                }
                files.Add(original);
            }
            return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        // About ten seconds around the cover's moment, re-encoded so it starts exactly there.
        internal static string Clip(string original,double centerMs,string folder) {
            Directory.CreateDirectory(folder);
            foreach(var old in Directory.GetFiles(folder))
                try {if(File.GetLastWriteTimeUtc(old)<DateTime.UtcNow.AddDays(-2))File.Delete(old);} catch(IOException) {} catch(UnauthorizedAccessException) {}
            string name=Path.GetFileNameWithoutExtension(original);
            if(name.Length>80)name=name.Substring(0,80).TrimEnd();
            string target=Path.Combine(folder,name+" (clip).mp4");
            for(int i=2;File.Exists(target);i++)target=Path.Combine(folder,name+" (clip "+i+").mp4");
            double start=Math.Max(0,centerMs-5000);
            using(var p=VideoFrames.Run("ffmpeg","-v error -nostdin -y -ss "+VideoFrames.Seconds(start)+" -i "+VideoFrames.Quote(original)+
                " -t 10 -map 0:V:0 -map 0:a:0? -vf \"pad=ceil(iw/2)*2:ceil(ih/2)*2\" -c:v libx264 -preset veryfast -crf 22 -pix_fmt yuv420p -c:a aac -movflags +faststart "+
                VideoFrames.Quote(target))) {
                p.WaitForExit();
                if(p.ExitCode!=0 || !File.Exists(target))throw new InvalidDataException(L.T("FFmpeg could not make the file."));
            }
            return target;
        }
    }

    // Asked when an original video is bigger than the limit in Options → Sharing.
    internal sealed class LargeVideoWindow : Window {
        internal bool WantsClip {get;private set;}
        internal LargeVideoWindow(Window owner,string name,long bytes) {
            UI.WindowSetup(this,L.T("Large video"));Owner=owner;ShowInTaskbar=false;
            WindowStartupLocation=WindowStartupLocation.CenterOwner;ResizeMode=ResizeMode.NoResize;
            Width=Math.Min(440,SystemParameters.WorkArea.Width);SizeToContent=SizeToContent.Height;
            var root=new DockPanel();root.Children.Add(new WindowTitleBar(this));
            var page=new StackPanel {Margin=new Thickness(22,16,22,20)};root.Children.Add(page);Content=root;
            var title=UI.Text(NoticeLook.KeepWords(bytes>0?L.F("This video is {0:0.#} MB. How should it be sent?",bytes/1048576.0):
                L.F("Some videos are bigger than {0} MB. How should they be sent?",((GalleryWindow)owner).Settings.ShareLimitMB)),16);
            title.FontWeight=FontWeights.SemiBold;title.TextWrapping=TextWrapping.Wrap;page.Children.Add(title);
            var file=UI.Text(name,12,true);file.TextTrimming=TextTrimming.CharacterEllipsis;file.Margin=new Thickness(0,4,0,16);page.Children.Add(file);
            var whole=UI.Btn(L.T("Send it as it is"),()=>{WantsClip=false;DialogResult=true;});
            var clip=UI.Btn(L.T("Send only 10 seconds around the cover scene"),()=>{WantsClip=true;DialogResult=true;},true);
            foreach(var button in new[]{clip,whole}) {
                button.FontSize=14;button.MinHeight=38;button.Margin=new Thickness(0,0,0,8);
                button.HorizontalContentAlignment=HorizontalAlignment.Left;page.Children.Add(button);
            }
            clip.IsDefault=true;
            var cancel=UI.Btn(L.T("Cancel"),()=>DialogResult=false);cancel.IsCancel=true;
            cancel.HorizontalAlignment=HorizontalAlignment.Right;cancel.Margin=new Thickness(0,6,0,0);page.Children.Add(cancel);
        }
    }

    // The Windows share window (the same one File Explorer's Share opens).
    internal static class ShareSheet {
        [ComImport, Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDataTransferManagerInterop {
            IntPtr GetForWindow(IntPtr appWindow,[In] ref Guid riid);
            void ShowShareUIForWindow(IntPtr appWindow);
        }
        static readonly Guid ManagerId=new Guid("a5caee9b-8708-49d1-8d36-67d25a8da00c");
        static List<string> pending;
        static readonly HashSet<IntPtr> hooked=new HashSet<IntPtr>();

        internal static void Show(Window owner,List<string> files) {
            IntPtr hwnd=new WindowInteropHelper(owner).Handle;
            var interop=(IDataTransferManagerInterop)WindowsRuntimeMarshal.GetActivationFactory(typeof(DataTransferManager));
            Guid id=ManagerId;
            if(!hooked.Contains(hwnd)) {
                var manager=(DataTransferManager)Marshal.GetObjectForIUnknown(interop.GetForWindow(hwnd,ref id));
                manager.DataRequested+=Requested;
                hooked.Add(hwnd);
            }
            pending=files;
            interop.ShowShareUIForWindow(hwnd);
        }

        static void Requested(DataTransferManager sender,DataRequestedEventArgs args) {
            var files=pending;
            if(files==null || files.Count==0)return;
            var request=args.Request;
            request.Data.Properties.Title=files.Count==1?Path.GetFileName(files[0]):L.F("{0} files from Love My Shelf",files.Count);
            var deferral=request.GetDeferral();
            var items=new IStorageItem[files.Count];
            int left=files.Count;
            for(int i=0;i<files.Count;i++) {
                int slot=i;
                var operation=StorageFile.GetFileFromPathAsync(files[i]);
                operation.Completed=(op,state)=>{
                    try {if(state==Windows.Foundation.AsyncStatus.Completed)items[slot]=op.GetResults();} catch(Exception) {}
                    if(Interlocked.Decrement(ref left)==0) {
                        try {request.Data.SetStorageItems(items.Where(item=>item!=null).ToList());}
                        catch(Exception) {}
                        finally {deferral.Complete();}
                    }
                };
            }
        }
    }
}
