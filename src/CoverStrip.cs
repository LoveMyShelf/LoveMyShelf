using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LoveMyShelf {
    // The other covers of the same original, small, in the empty space beside a tall cover on a wide screen.
    // They stay in one place on the right, in scene order, so switching between them never moves anything.
    internal sealed class CoverStrip : Border {
        internal const double MinRoom=110;
        const double MinTile=72,MaxTile=140,Gap=8,SidePadding=14;
        static readonly Brush NowLine=Frozen(NoticeLook.Blue),HoverLine=Frozen(Color.FromArgb(0xB0,0xFF,0xFF,0xFF)),
            Empty=Frozen(Color.FromRgb(0x23,0x26,0x2A)),BadgeBack=Frozen(Color.FromArgb(0xCC,0,0,0));
        static Brush Frozen(Color color) {var brush=new SolidColorBrush(color);brush.Freeze();return brush;}
        static readonly ControlTemplate PlainButton=MakeTemplate();
        static ControlTemplate MakeTemplate() {
            var template=new ControlTemplate(typeof(Button)) {VisualTree=new FrameworkElementFactory(typeof(ContentPresenter))};
            template.Seal();
            return template;
        }

        sealed class Tile {
            internal Photo Cover;internal Button Button;internal Border Frame;internal Image Image;
        }
        readonly StackPanel list=new StackPanel {VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center};
        readonly ScrollViewer scroll;
        readonly List<Tile> tiles=new List<Tile>();
        readonly Dictionary<string,ImageSource> pictures=new Dictionary<string,ImageSource>(StringComparer.OrdinalIgnoreCase);
        string shownKey,current;
        double tileWidth;
        internal Action<Photo> Chosen;
        internal int Count {get {return tiles.Count;}}

        internal CoverStrip() {
            HorizontalAlignment=HorizontalAlignment.Right;Visibility=Visibility.Collapsed;
            scroll=new ScrollViewer {Content=list,VerticalScrollBarVisibility=ScrollBarVisibility.Hidden,
                HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,Focusable=false,PanningMode=PanningMode.VerticalOnly};
            Child=scroll;
        }
        internal bool Holds(Photo photo) {
            return tiles.Count>1 && tiles.Any(t=>Same(t.Cover.Folder,photo.Folder));
        }
        static bool Same(string a,string b) {return String.Equals(a,b,StringComparison.OrdinalIgnoreCase);}
        internal void Clear() {
            tiles.Clear();list.Children.Clear();shownKey=null;current=null;Visibility=Visibility.Collapsed;
        }
        // Marks the cover on screen; called at once, before the list is read again.
        internal void Mark(string folder) {
            current=folder;
            foreach(var tile in tiles)Paint(tile);
            ShowCurrent();
        }
        internal void Fill(List<Photo> covers,string folder) {
            current=folder;
            string key=String.Join("|",covers.Select(c=>c.Folder+"*"+c.Thumb+"*"+c.ActiveCoverFile));
            if(key==shownKey) {
                for(int i=0;i<tiles.Count;i++)tiles[i].Cover=covers[i];
                foreach(var tile in tiles)Paint(tile);
                ShowCurrent();
                return;
            }
            shownKey=key;tiles.Clear();list.Children.Clear();
            if(covers.Count<2)return;
            for(int i=0;i<covers.Count;i++) {
                var tile=MakeTile(covers[i],i+1,covers.Count);
                tiles.Add(tile);list.Children.Add(tile.Button);
                Size(tile);Paint(tile);LoadPicture(tile);
            }
            ShowCurrent();
        }
        // room: the free width beside the picture. height: the height the picture uses. bottom: the bar below it.
        internal void Place(double room,double height,double bottom) {
            Width=room;Margin=new Thickness(0,10,0,bottom+10);
            double usable=Math.Max(0,height-20);
            double fits=tiles.Count==0?MaxTile:(usable-Gap*(tiles.Count-1))/tiles.Count*9/16;
            double width=Math.Floor(Math.Max(MinTile,Math.Min(Math.Min(MaxTile,room-SidePadding*2),fits)));
            if(width==tileWidth)return;
            tileWidth=width;
            foreach(var tile in tiles)Size(tile);
            ShowCurrent();
        }
        void Size(Tile tile) {
            double width=tileWidth>0?tileWidth:MinTile;
            tile.Button.Width=width;tile.Button.Height=Math.Round(width*16/9);
        }
        Tile MakeTile(Photo cover,int number,int total) {
            var image=new Image {Stretch=Stretch.UniformToFill,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
            RenderOptions.SetBitmapScalingMode(image,BitmapScalingMode.HighQuality);
            var badge=new Border {Background=BadgeBack,CornerRadius=new CornerRadius(4),Padding=new Thickness(5,1,5,1),Margin=new Thickness(4),
                HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Bottom,IsHitTestVisible=false,
                Child=new TextBlock {Text=number+"/"+total,FontSize=11,Foreground=Brushes.White}};
            var body=new Grid {ClipToBounds=true};
            body.Children.Add(image);body.Children.Add(badge);
            var frame=new Border {Background=Empty,BorderThickness=new Thickness(2),CornerRadius=new CornerRadius(6),Child=body,SnapsToDevicePixels=true};
            var button=new Button {Template=PlainButton,Content=frame,Focusable=false,Cursor=Cursors.Hand,Margin=new Thickness(0,0,0,number<total?Gap:0)};
            AutomationProperties.SetName(button,L.F("Cover {0} of {1}",number,total));
            button.ToolTip=L.F("Cover {0} of {1}",number,total);
            var tile=new Tile {Cover=cover,Button=button,Frame=frame,Image=image};
            button.Click+=delegate {if(!Same(tile.Cover.Folder,current) && Chosen!=null)Chosen(tile.Cover);};
            button.MouseEnter+=delegate {Paint(tile);};
            button.MouseLeave+=delegate {Paint(tile);};
            return tile;
        }
        void Paint(Tile tile) {
            bool now=Same(tile.Cover.Folder,current);
            tile.Frame.BorderBrush=now?NowLine:tile.Button.IsMouseOver?HoverLine:Brushes.Transparent;
            tile.Frame.Opacity=now || tile.Button.IsMouseOver?1:0.72;
        }
        void ShowCurrent() {
            var tile=tiles.FirstOrDefault(t=>Same(t.Cover.Folder,current));
            if(tile==null)return;
            Dispatcher.BeginInvoke(new Action(()=>{if(tile.Button.IsLoaded)tile.Button.BringIntoView();}),System.Windows.Threading.DispatcherPriority.Loaded);
        }
        async void LoadPicture(Tile tile) {
            string path=tile.Cover.GridImage;
            ImageSource known;
            if(pictures.TryGetValue(path,out known)){tile.Image.Source=known;return;}
            try {
                var loaded=await Task.Run(()=>Small(path));
                pictures[path]=loaded;
                tile.Image.Source=loaded;
            } catch(Exception) {} // an unreadable picture stays an empty tile that can still be chosen
        }
        static ImageSource Small(string path) {
            using(var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)) {
                var image=new BitmapImage();
                image.BeginInit();
                image.CacheOption=BitmapCacheOption.OnLoad;image.DecodePixelWidth=300;image.StreamSource=input;
                image.EndInit();image.Freeze();
                return image;
            }
        }
    }
}
