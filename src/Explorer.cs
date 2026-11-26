using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace LoveMyShelf {
    // "Details" for one item: the original and, when there is one, its cover.
    internal sealed class DetailsWindow : Window {
        readonly StackPanel list=new StackPanel();
        internal DetailsWindow(GalleryWindow owner,Photo photo) {
            UI.WindowSetup(this,L.T("Details"));Owner=owner;ShowInTaskbar=false;
            WindowStartupLocation=WindowStartupLocation.CenterOwner;ResizeMode=ResizeMode.NoResize;
            Width=Math.Min(480,SystemParameters.WorkArea.Width);SizeToContent=SizeToContent.Height;
            var root=new DockPanel();root.Children.Add(new WindowTitleBar(this));
            var page=new StackPanel {Margin=new Thickness(22,14,22,18)};root.Children.Add(page);Content=root;
            var title=UI.Text(photo.Name,16);title.FontWeight=FontWeights.SemiBold;title.TextWrapping=TextWrapping.Wrap;page.Children.Add(title);
            list.Margin=new Thickness(0,10,0,0);page.Children.Add(list);
            Row(L.T("Loading…"),"");
            var close=UI.Btn(L.T("Close"),Close,true);close.IsCancel=true;close.IsDefault=true;close.MinWidth=80;close.MinHeight=32;
            close.HorizontalAlignment=HorizontalAlignment.Right;close.Margin=new Thickness(0,14,0,0);page.Children.Add(close);
            Loaded+=async delegate {
                List<Tuple<string,string>> rows;
                try {rows=await Task.Run(()=>Read(owner.Store,photo));}
                catch(Exception ex){rows=new List<Tuple<string,string>>{Tuple.Create(L.T("Something went wrong"),ex.Message)};}
                list.Children.Clear();
                foreach(var row in rows)Row(row.Item1,row.Item2);
            };
        }
        void Row(string label,string value) {
            var line=new DockPanel {Margin=new Thickness(0,3,0,3)};
            var name=UI.Text(label,13,true);name.Width=120;name.VerticalAlignment=VerticalAlignment.Top;DockPanel.SetDock(name,Dock.Left);
            var text=new TextBox {Text=value,IsReadOnly=true,BorderThickness=new Thickness(0),Background=null,FontSize=13,TextWrapping=TextWrapping.Wrap,Padding=new Thickness(0)};
            text.SetResourceReference(Control.ForegroundProperty,"Text");
            line.Children.Add(name);line.Children.Add(text);list.Children.Add(line);
        }
        internal static string Size(long bytes) {
            if(bytes>=1024L*1024*1024)return (bytes/1073741824.0).ToString("0.##")+" GB";
            if(bytes>=1024*1024)return (bytes/1048576.0).ToString("0.#")+" MB";
            return Math.Max(1,(bytes+1023)/1024).ToString("N0")+" KB";
        }
        static string Pixels(string path) {
            try {
                if(VideoFrames.Supports(path)) {
                    var size=VideoFrames.ReadOrientation(path,CancellationToken.None);
                    int w=size.Width,h=size.Height;if(Math.Abs(size.Angle%180)==90){int t=w;w=h;h=t;}
                    return w>0?w+" × "+h+" px":"";
                }
                int pw,ph;
                if(PictureTypes.UsesFfmpeg(path)) {var upright=Pictures.Load(path);pw=upright.PixelWidth;ph=upright.PixelHeight;}
                else PictureTypes.ReadSize(path,out pw,out ph);
                return pw+" × "+ph+" px";
            } catch(Exception) {return "";}
        }
        static List<Tuple<string,string>> Read(GalleryStore store,Photo photo) {
            var rows=new List<Tuple<string,string>>();
            string path=photo.Original;
            if(photo.Linked && !File.Exists(path))try{path=OriginalLocator.Resolve(store,photo,CancellationToken.None);}catch(Exception){}
            if(File.Exists(path)) {
                var info=new FileInfo(path);
                rows.Add(Tuple.Create(L.T("Location"),info.DirectoryName));
                rows.Add(Tuple.Create(L.T("File type"),info.Extension.TrimStart('.').ToUpperInvariant()));
                rows.Add(Tuple.Create(L.T("Size"),Size(info.Length)));
                string pixels=Pixels(path);if(pixels.Length>0)rows.Add(Tuple.Create(L.T("Resolution"),pixels));
                if(VideoFrames.Supports(path)) {
                    double ms=VideoFrames.DurationMs(path,CancellationToken.None);
                    if(ms>0)rows.Add(Tuple.Create(L.T("Length"),Viewer.Stamp(ms)));
                }
                rows.Add(Tuple.Create(L.T("Date modified"),info.LastWriteTime.ToString("g")));
            } else rows.Add(Tuple.Create(L.T("Location"),L.T("The original file is unavailable.")));
            if(photo.HasCrop) {
                int covers=GalleryStore.CoversOf(photo).Count;
                rows.Add(Tuple.Create(L.T("Covers"),L.F("{0:N0}",covers)));
                if(File.Exists(photo.ActiveCoverFile)) {
                    var cover=new FileInfo(photo.ActiveCoverFile);
                    rows.Add(Tuple.Create(L.T("JPG cover file"),cover.Name));
                    rows.Add(Tuple.Create(L.T("JPG cover size"),Pixels(cover.FullName)+" · "+Size(cover.Length)));
                }
                if(photo.HasMotion)rows.Add(Tuple.Create(L.T("GIF cover file"),Path.GetFileName(photo.MotionFile)));
            }
            return rows;
        }
    }
}
