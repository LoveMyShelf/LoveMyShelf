using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Data;
using System.Windows.Automation;
using System.Windows.Threading;


[assembly: InternalsVisibleTo("Exercise")]
[assembly: InternalsVisibleTo("ColdStart")]

namespace LoveMyShelf {
    public class PhotoRow {
        public GalleryWindow Owner; public List<Photo> Photos; public int Columns,Number; public double Height;
        public event Action<int> Changed;
        public void Replace(int index,Photo photo) {
            Photos[index]=photo;
            if(Changed!=null) Changed(index);
        }
    }
    public class TileRow : UniformGrid {
        CancellationTokenSource pending;
        PhotoRow displayed;
        static readonly ContextMenu MenuStandIn=new ContextMenu();
        public TileRow() {
            Rows=1;
            // Tiles are made as soon as a row gets its data (not only once it is shown), so a list that just changed
            // never paints a blank frame before its tiles appear; Loaded only refills a row that was emptied when it left the screen.
            DataContextChanged+=(s,e)=>{ Fill(e.NewValue as PhotoRow); };
            Loaded+=delegate { var row=DataContext as PhotoRow; if(row!=null && displayed!=row) Fill(row); };
            Unloaded+=delegate { Fill(null); };
        }
        // The file a tile shows: the cover, or the original when the cover is hidden or missing.
        internal static string ShownImage(Photo photo) {
            return photo.HasCrop && !photo.ExcludedFromCollection?photo.GridImage:photo.Original;
        }
        static async void LoadTile(Button tile,Image image,Photo photo,PhotoRow row,CancellationToken token,Action loaded=null) {
            try {
                // Read at the tile's own size on screen, so large tiles stay sharp and small ones stay light.
                var source=PresentationSource.FromVisual(row.Owner);
                double scale=source!=null && source.CompositionTarget!=null?source.CompositionTarget.TransformToDevice.M22:1;
                int side=Math.Max(64,(int)Math.Ceiling(row.Height*scale));
                var owner=row.Owner;
                var pending=TileImages.Load(photo,side,row.Number,()=>owner.CenterRow,token);
                // Pictures already in memory appear in the same frame as the tile.
                BitmapSource bitmap=pending.IsCompleted?pending.Result:await pending;
                if(bitmap==null)throw new InvalidDataException();
                if(!token.IsCancellationRequested) {image.Source=bitmap;if(loaded!=null)loaded();}
            } catch(OperationCanceledException) { }
            catch(Exception) {
                if(!token.IsCancellationRequested) {
                    var name=UI.Text(photo.Name,12,true);name.TextWrapping=TextWrapping.Wrap;
                    tile.Content=name;
                }
            }
        }
        void Fill(PhotoRow row) {
            if(displayed!=null) displayed.Changed-=UpdateCell;
            displayed=row;
            if(pending!=null) {pending.Cancel();pending.Dispose();pending=null;}
            Children.Clear(); if(row==null) return;
            pending=new CancellationTokenSource();
            Columns=row.Columns; Height=row.Height;
            for(int column=0;column<row.Photos.Count;column++) Children.Add(MakeTile(row,row.Photos[column],column));
            row.Changed+=UpdateCell;
        }
        void UpdateCell(int index) {
            Children.RemoveAt(index);
            Children.Insert(index,MakeTile(displayed,displayed.Photos[index],index,false));
        }
        FrameworkElement MakeTile(PhotoRow row,Photo photo,int column,bool allowReflow=true) {
                if(photo==null) return new Border { IsHitTestVisible=false };
                var image=new Image { Stretch=Stretch.Uniform,SnapsToDevicePixels=true };
                RenderOptions.SetBitmapScalingMode(image,BitmapScalingMode.HighQuality);
                // The full name shows in a card beside the tile on hover (GalleryWindow.ShowName), never over the tile.
                var tile=new Button { Content=image,BorderThickness=new Thickness(0),
                    Padding=new Thickness(0),Margin=new Thickness(0.5),ToolTip=photo.Name,
                    Template=TileTemplate(), HorizontalContentAlignment=HorizontalAlignment.Stretch,VerticalContentAlignment=VerticalAlignment.Stretch };
                ToolTipService.SetIsEnabled(tile,false);
                UI.Use(tile,Control.BackgroundProperty,"TileBand");
                AutomationProperties.SetName(tile,photo.Name);
                row.Owner.TrackTile(tile,photo);
                tile.PreviewMouseLeftButtonDown+=(s,e)=>row.Owner.NoteClickCount(e.ClickCount);
                tile.Click+=delegate {row.Owner.TileClicked(photo);};
                // The menu is built the first time it opens, not for every tile that scrolls by.
                Func<ContextMenu> buildMenu=()=>{
                var menu=new ContextMenu();
                if(photo.HasCrop) {
                    foreach(bool cover in new[]{true,false}) {
                        var view=new MenuItem {Header=L.T(cover?"View cover large":"View original large")};
                        view.Click+=delegate {menu.IsOpen=false;row.Owner.OpenPhoto(photo,cover);};
                        menu.Items.Add(view);
                    }
                    menu.Items.Add(new Separator());
                }
                var edit=new MenuItem { Header=photo.HasCrop?L.T("Make another cover"):L.T("Make cover") }; edit.Click+=delegate { row.Owner.Edit(photo); };
                menu.Items.Add(edit);
                var openDefault=new MenuItem {Header=L.T("Open with default app")};
                openDefault.Click+=delegate {row.Owner.OpenInDefaultPlayer(photo);};
                menu.Items.Add(openDefault);
                if(photo.Linked) {
                    var rename=new MenuItem {Header=L.T("Rename"),InputGestureText="F2"};
                    rename.Click+=delegate {row.Owner.RenameOriginal(photo);};
                    menu.Items.Add(rename);
                }
                var location=new MenuItem {Header=L.T("Open file location")};
                location.Click+=delegate {row.Owner.ShowInExplorer(photo,false);};
                menu.Items.Add(location);
                if(photo.HasCrop) {
                    var coverLocation=new MenuItem {Header=L.T("Open cover file location")};
                    coverLocation.Click+=delegate {row.Owner.ShowInExplorer(photo,true);};
                    menu.Items.Add(coverLocation);
                }
                var info=new MenuItem {Header=L.T("Details")};
                info.Click+=delegate {row.Owner.ShowDetails(photo);};
                menu.Items.Add(info);
                ShareTiles.AddMenu(menu,row.Owner,photo);
                if(photo.HasCrop && MotionMaker.Supports(photo.Original)) {
                    var gif=new MenuItem {Header=L.T("Make GIF cover")};
                    gif.Click+=delegate {row.Owner.ShowMakeGif(row.Owner,photo);};
                    menu.Items.Add(gif);
                }
                if(photo.HasCrop) {
                    menu.Items.Add(new Separator());
                    // With a GIF cover the names say JPG and GIF, so each choice is clear about what it touches.
                    bool both=photo.HasMotion;
                    var collection=new MenuItem { Header=photo.ExcludedFromCollection?L.T(both?"Show the JPG and GIF covers again":"Show this cover again"):
                        L.T(both?"Hide the JPG and GIF covers in Love My Shelf":"Hide this cover in Love My Shelf") };
                    collection.Click+=delegate { row.Owner.ChangeCollection(photo); };
                    menu.Items.Add(collection);
                    if(both) {
                        var recycleGif=new MenuItem { Header=L.T("Move only the GIF cover file to the Recycle Bin") };
                        recycleGif.Click+=delegate { row.Owner.RecycleMotion(photo); };
                        menu.Items.Add(recycleGif);
                    }
                    var recycleCover=new MenuItem { Header=L.T(both?"Move the JPG and GIF cover files to the Recycle Bin":"Move this cover file to the Recycle Bin") };
                    recycleCover.Click+=delegate { row.Owner.RecycleCover(photo); };
                    menu.Items.Add(recycleCover);
                }
                if(!row.Owner.ShowingAllCrops && photo.HasCrop) {
                    // Offered when other covers of this original are hidden; checked when the menu opens.
                    var unhide=new MenuItem {Header=L.F("Show hidden covers again ({0})",0),Visibility=Visibility.Collapsed};
                    unhide.Click+=delegate { row.Owner.ShowHiddenCovers(photo); };
                    menu.Items.Add(unhide);
                    menu.Opened+=delegate {
                        int hidden=0;
                        try {hidden=GalleryStore.CoversOf(photo).Count(p=>p.ExcludedFromCollection && p.Folder!=photo.Folder);} catch(Exception) {}
                        unhide.Header=L.F("Show hidden covers again ({0})",hidden);
                        unhide.Visibility=hidden>0?Visibility.Visible:Visibility.Collapsed;
                    };
                }
                if(!row.Owner.ShowingAllCrops) {
                    menu.Items.Add(new Separator());
                    var remove=new MenuItem { Header=L.T("Move original to Recycle Bin"),InputGestureText="Delete" };
                    remove.Click+=delegate { row.Owner.RecycleOriginal(photo); };
                    menu.Items.Add(remove);
                }
                return menu;
                };
                // A shared empty stand-in lets Windows raise the opening event; it is swapped for the real menu there.
                tile.ContextMenu=MenuStandIn;
                ContextMenu single=null;
                // With several tiles selected, right-clicking one of them acts on all of them.
                tile.ContextMenuOpening+=(s,e)=>{if(single==null)single=buildMenu();tile.ContextMenu=row.Owner.MenuFor(photo,single);};
                Action loaded=null;
                var mode=row.Owner.Settings.GridGif;
                if(photo.HasMotion && mode!=GifPlayback.Never)loaded=AttachMotion(tile,image,photo,mode,row.Height,pending.Token);
                ShareTiles.AttachDrag(tile,row.Owner,photo);
                var reflow=allowReflow?row.Owner.CurrentReflow:null;
                var kept=reflow!=null && !photo.HasMotion?reflow.ImageFor(photo):null;
                if(kept!=null){image.Source=kept;if(loaded!=null)loaded();}
                else LoadTile(tile,image,photo,row,pending.Token,loaded);
                if(reflow!=null)reflow.Animate(tile,photo,row,column);
                return tile;
        }
        // Moves GIF covers in the grid: on hover, or always while the tile is on screen.
        // Returns what to run once the still has loaded.
        static Action AttachMotion(Button tile,Image image,Photo photo,GifPlayback mode,double height,CancellationToken token) {
            var player=new FramePlayer(image);
            bool hovering=false,ready=false;
            int side=(int)Math.Max(160,Math.Min(720,height*1.5));
            Action start=async ()=>{
                if(player.Playing || !ready)return;
                try {
                    var frames=await MotionFrames.LoadAsync(photo.MotionFile,side,token);
                    if(token.IsCancellationRequested || !tile.IsLoaded || (mode==GifPlayback.Hover && !hovering))return;
                    player.Start(frames);
                } catch(Exception) {}
            };
            if(mode==GifPlayback.Hover) {
                tile.MouseEnter+=delegate {hovering=true;start();};
                tile.MouseLeave+=delegate {hovering=false;player.Stop();};
            }
            tile.Unloaded+=delegate {player.Stop();};
            return ()=>{ready=true;if(mode==GifPlayback.Always || hovering)start();};
        }
        static ControlTemplate tileTemplate;
        // A tile: the picture and a selection outline drawn from the button's own border settings.
        static ControlTemplate TileTemplate() {
            if(tileTemplate!=null) return tileTemplate;
            var root=new FrameworkElementFactory(typeof(Grid));
            var border=new FrameworkElementFactory(typeof(Border));
            border.SetResourceReference(Border.BackgroundProperty,"TileBand");
            border.SetValue(Border.ClipToBoundsProperty,true);
            border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
            root.AppendChild(border);
            var outline=new FrameworkElementFactory(typeof(Border));
            outline.SetValue(UIElement.IsHitTestVisibleProperty,false);
            outline.SetValue(Border.BorderBrushProperty,new TemplateBindingExtension(Control.BorderBrushProperty));
            outline.SetValue(Border.BorderThicknessProperty,new TemplateBindingExtension(Control.BorderThicknessProperty));
            root.AppendChild(outline);
            tileTemplate=new ControlTemplate(typeof(Button)) { VisualTree=root };
            return tileTemplate;
        }
    }
    // Where the tiles were before the list changed (a file was deleted, moved or a cover was added), so the tiles
    // that stay can slide from their old place to the new one instead of jumping, and keep their pictures.
    internal sealed class Reflow {
        internal readonly Dictionary<string,Point> Old=new Dictionary<string,Point>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string,Tuple<string,ImageSource>> images=new Dictionary<string,Tuple<string,ImageSource>>(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> Fresh=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal List<string> OldFolders=new List<string>();
        internal int FirstIndex;
        internal double Fraction,OriginX,OriginY,Cell,NewOffset,Viewport;
        internal bool Calibrated;
        internal static readonly TimeSpan Duration=TimeSpan.FromMilliseconds(220);
        internal void KeepImage(Photo photo,ImageSource source) {
            images[photo.Folder]=Tuple.Create(TileRow.ShownImage(photo),source);
        }
        // A tile whose record folder changed (a cover moved up to the first place) keeps its place and picture.
        internal void Rename(string from,string to) {
            Point point;Tuple<string,ImageSource> image;
            if(Old.TryGetValue(from,out point)){Old.Remove(from);Old[to]=point;} else Old.Remove(to);
            if(images.TryGetValue(from,out image)){images.Remove(from);images[to]=image;} else images.Remove(to);
        }
        internal ImageSource ImageFor(Photo photo) {
            Tuple<string,ImageSource> kept;
            return images.TryGetValue(photo.Folder,out kept) && kept.Item1==TileRow.ShownImage(photo)?kept.Item2:null;
        }
        // Slides a tile that was already on screen from its old place; a new tile fades in.
        internal void Animate(FrameworkElement tile,Photo photo,PhotoRow row,int column) {
            Point old;
            double x=OriginX+column*Cell,y=OriginY+row.Number*row.Height-NewOffset;
            if(y<-row.Height || y>Viewport+row.Height)return; // not in view: nothing to animate
            if(Old.TryGetValue(photo.Folder,out old)) {
                double dx=old.X-x,dy=old.Y-y;
                if(Math.Abs(dx)<0.5 && Math.Abs(dy)<0.5)return;
                var move=new TranslateTransform(dx,dy);tile.RenderTransform=move;
                var ease=new CubicEase {EasingMode=EasingMode.EaseOut};
                move.BeginAnimation(TranslateTransform.XProperty,new DoubleAnimation(dx,0,Duration) {EasingFunction=ease});
                move.BeginAnimation(TranslateTransform.YProperty,new DoubleAnimation(dy,0,Duration) {EasingFunction=ease});
            } else if(Fresh.Contains(photo.Folder)) {
                tile.BeginAnimation(UIElement.OpacityProperty,new DoubleAnimation(0,1,Duration));
            }
        }
    }
    public class GalleryWindow : Window {
        readonly GalleryStore store;
        readonly Action<string,IntPtr> moveToRecycleBin;
        // A second window opened beside the first one (to drag originals between two folders).
        readonly bool another;
        internal GalleryStore Store {get{return store;}}
        readonly CancellationTokenSource lifetime=new CancellationTokenSource();
        internal string PlaybackDirectory { get { return Path.Combine(store.Root,"Playback"); } }
        readonly ListBox grid=new ListBox();
        readonly TextBlock count=UI.Text("",11,true), emptyTitle=UI.Text("",20);
        readonly TextBlock emptyHelp=UI.Text("",14,true);
        readonly Button allCropsButton,unfinishedButton,emptyAction,sortButton;
        bool canShuffle;
        readonly DockPanel footerStatus=new DockPanel();
        readonly StackPanel footerControls=new StackPanel {Orientation=Orientation.Horizontal,
            HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Center};
        readonly StackPanel filter=new StackPanel {Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,8,0)};
        readonly RadioButton[] filterButtons=new RadioButton[3];
        readonly Dictionary<string,int> folderFilters=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        readonly Action localize;
        readonly Action updateFooter;
        Preferences settings;
        readonly Func<Window,string> pickCoverFolder;
        bool coverSetupShown,migratingCovers,migrationRequested;
        readonly Random random=new Random();
        Dictionary<string,int> shuffledOrder;
        const string CollectionKey="@all-crops";
        const string UnfinishedKey="@unfinished-covers";
        Album allCrops,unfinished;
        public bool ShowingAllCrops { get { return active==allCrops; } }
        bool ShowingUnfinished { get { return active==unfinished; } }
        internal Preferences Settings { get { return settings; } }
        int folderFilter;
        bool ShowingPending { get { return ShowingUnfinished || (active!=null && active.Source!=null && folderFilter==1); } }
        int refreshVersion, skippedCount;
        readonly StackPanel tabs=new StackPanel { Orientation=Orientation.Horizontal };
        readonly StackPanel empty=new StackPanel { HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center };
        readonly DispatcherTimer resizeTimer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(100) };
        Album active;
        // Removed tiles keep their slots until the current view is reloaded.
        List<Photo> photos=new List<Photo>();
        List<PhotoRow> rows;
        int columns;
        bool folderUnavailable;
        int density=120;
        public GalleryWindow(GalleryStore store) : this(store,RecycleBin.MoveFile) { }
        internal GalleryWindow(GalleryStore store,Action<string,IntPtr> moveToRecycleBin,Func<Window,string> pickCoverFolder=null,bool another=false) {
            this.store=store;this.another=another;
            this.pickCoverFolder=pickCoverFolder??(owner=>UI.PickFolder(owner,
                L.T("Choose the folder where Love My Shelf will save your cover files."),Preferences.SuggestedCoverParent,true));
            settings=Preferences.Load(store.Root);L.Korean=settings.Korean;MotionWindow.UseDefaults(settings);
            if(!another)Task.Run(()=>MotionMaker.SweepPreviews()); // GIF preview files left behind by a sudden exit
            allCrops=new Album(CollectionKey,L.T("Cover collection"));
            unfinished=new Album(UnfinishedKey,L.T("Unfinished cover collection"));
            this.moveToRecycleBin=moveToRecycleBin;
            UI.WindowSetup(this,"Love My Shelf");
            Width=Math.Min(1120,SystemParameters.WorkArea.Width);Height=Math.Min(800,SystemParameters.WorkArea.Height); MinWidth=440; MinHeight=340;
            var root=new DockPanel(); Content=root;
            var nav=UI.Bar(new Thickness(5,2,5,2));
            allCropsButton=UI.Btn(allCrops.Name,()=>Select(allCrops));
            allCropsButton.Margin=new Thickness(0,0,4,0);DockPanel.SetDock(allCropsButton,Dock.Left);nav.Children.Add(allCropsButton);
            unfinishedButton=UI.Btn(unfinished.Name,()=>Select(unfinished));
            unfinishedButton.Margin=new Thickness(0,0,4,0);nav.Children.Add(unfinishedButton);
            // The two collections can't be hidden, so their right-click menu only opens them in a new window.
            foreach(var pair in new[]{Tuple.Create(allCropsButton,CollectionKey),Tuple.Create(unfinishedButton,UnfinishedKey)}) {
                string key=pair.Item2;
                var openNew=new MenuItem();
                openNew.Click+=delegate {OpenAnotherWindow(key);};
                var menu=new ContextMenu();menu.Items.Add(openNew);
                menu.Opened+=delegate {openNew.Header=L.T("Open in new window");};
                pair.Item1.ContextMenu=menu;
            }
            // One button for the guide and every setting: read how something works, then choose it right there.
            var guide=UI.Btn("",()=>ShowGuide("start"));UI.Right(nav,guide);
            var open=UI.Btn("",PickFolder,true);UI.Right(nav,open);
            var newWindow=UI.Btn("",OpenAnotherWindow);newWindow.Margin=new Thickness(0,0,6,0);UI.Right(nav,newWindow);
            var searchHost=SearchBox();searchHost.Margin=new Thickness(0,0,6,0);UI.Right(nav,searchHost);
            var scroller=new ScrollViewer { Content=tabs,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto };
            nav.Children.Add(scroller);DockPanel.SetDock(nav,Dock.Top);root.Children.Add(nav);
            var line=UI.Use(new Border { Height=1 },Border.BackgroundProperty,"Line"); DockPanel.SetDock(line,Dock.Top);root.Children.Add(line);
            var modeFile=Path.Combine(store.Root,"windowed");
            // Explorer keys run before the fullscreen keys so Esc first clears a selection.
            PreviewKeyDown+=ExplorerKeys;
            var titleBar=FullScreen.TitleBar(this,!another && !File.Exists(modeFile),full=>{
                if(another)return;
                try { if(full) File.Delete(modeFile); else File.WriteAllText(modeFile,""); }
                catch(IOException) { } catch(UnauthorizedAccessException) { }
            });
            root.Children.Insert(0,titleBar);
            var sizing=UI.Btn("",()=>{density=density==120?88:(density==88?220:(density==220?164:120)); LayoutRows();});
            // Sort works like File Explorer: one button, the choices open upward, Shuffle is the last one.
            sortButton=UI.Btn("",OpenSortMenu);
            foreach(var button in new[]{sortButton,sizing}) {
                button.Margin=new Thickness(3,0,0,0);footerControls.Children.Add(button);
            }
            footerStatus.Children.Add(filter);
            // Shown as All, Finished (cover O), Unfinished (cover X); the numbers stay All=0, Unfinished=1, Finished=2.
            foreach(int i in new[]{0,2,1}) {
                int choice=i;
                var button=new RadioButton {Style=(Style)Application.Current.Resources["FolderFilterChoice"]};
                button.Checked+=delegate {ChangeFolderFilter(choice);};
                filterButtons[i]=button;filter.Children.Add(button);
            }
            count.TextTrimming=TextTrimming.CharacterEllipsis;footerStatus.Children.Add(count);
            var footer=new Grid {Margin=new Thickness(5,1,5,2)};
            footer.ColumnDefinitions.Add(new ColumnDefinition());
            footer.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
            footer.ColumnDefinitions.Add(new ColumnDefinition());
            footer.Children.Add(footerStatus);
            var openCoverFolder=UI.Btn("",OpenCoverFolder);
            openCoverFolder.HorizontalAlignment=HorizontalAlignment.Center;
            openCoverFolder.VerticalAlignment=VerticalAlignment.Center;
            Grid.SetColumn(openCoverFolder,1);footer.Children.Add(openCoverFolder);
            Grid.SetColumn(footerControls,2);footer.Children.Add(footerControls);
            DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
            updateFooter=delegate {
                double width=ActualWidth>0?ActualWidth:Width;
                bool compact=width<710;
                string sortLabel=shuffledOrder!=null?L.T("Sort: shuffled"):L.F("Sort: {0}",SortName(settings.Sort));
                sortButton.Content=(compact?"⇅":sortLabel)+"  ▴";
                sortButton.ToolTip=L.T("Sort");AutomationProperties.SetName(sortButton,L.T("Sort"));
                FooterLabel(sizing,"Cover size","▦",compact);
            };
            SizeChanged+=delegate {updateFooter();};
            var host=new Grid();root.Children.Add(host);
            grid.BorderThickness=new Thickness(0);grid.Padding=new Thickness(0);grid.Margin=new Thickness(2,0,1,0);UI.Use(grid,Control.BackgroundProperty,"Paper");
            var style=new Style(typeof(ListBoxItem));
            style.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(0)));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty,HorizontalAlignment.Stretch));
            style.Setters.Add(new Setter(Control.FocusableProperty,false));
            style.Setters.Add(new Setter(Control.TemplateProperty,new ControlTemplate(typeof(ListBoxItem)) { VisualTree=new FrameworkElementFactory(typeof(ContentPresenter)) }));
            grid.ItemContainerStyle=style;
            grid.ItemTemplate=new DataTemplate { VisualTree=new FrameworkElementFactory(typeof(TileRow)) };
            ScrollViewer.SetHorizontalScrollBarVisibility(grid,ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(grid,ScrollBarVisibility.Visible);
            // Pressing on empty space clears the selection, like File Explorer.
            grid.PreviewMouseLeftButtonDown+=(s,e)=>{
                if((Keyboard.Modifiers&(ModifierKeys.Control|ModifierKeys.Shift))!=0 || selected.Count==0)return;
                var node=e.OriginalSource as DependencyObject;
                while(node!=null && !(node is ButtonBase) && !(node is ScrollBar)) {
                    node=(node is Visual || node is System.Windows.Media.Media3D.Visual3D)?VisualTreeHelper.GetParent(node):LogicalTreeHelper.GetParent(node);
                }
                if(node==null)ClearSelection();
            };
            VirtualizingPanel.SetIsVirtualizing(grid,true);
            VirtualizingPanel.SetVirtualizationMode(grid,VirtualizationMode.Recycling);
            VirtualizingPanel.SetScrollUnit(grid,ScrollUnit.Pixel);
            // Tile pictures load from the middle of the screen outward (TileQueue reads this from its threads).
            grid.AddHandler(ScrollViewer.ScrollChangedEvent,new ScrollChangedEventHandler((s,e)=>{
                if(rowHeight>0)centerRow=(int)((e.VerticalOffset+e.ViewportHeight/2)/rowHeight);
            }));
            host.Children.Add(grid);
            SetupNameCard(host);
            SetupDropHint(host);
            emptyTitle.HorizontalAlignment=HorizontalAlignment.Center;empty.Children.Add(emptyTitle);
            emptyHelp.Margin=new Thickness(0,8,0,22);empty.Children.Add(emptyHelp);
            emptyAction=UI.Btn("",PickFolder,true);emptyAction.HorizontalAlignment=HorizontalAlignment.Center;empty.Children.Add(emptyAction);host.Children.Add(empty);
            resizeTimer.Tick+=delegate {resizeTimer.Stop();LayoutRows();};
            reflowTimer.Tick+=delegate {reflowTimer.Stop();CurrentReflow=null;};
            grid.SizeChanged+=delegate {resizeTimer.Stop();resizeTimer.Start();};
            Upscales.Finished+=UpscaleFinished;
            Upscales.Failed+=(photo,message)=>Notify(L.T("Upscaling failed"),photo.Name+Environment.NewLine+message,null,NoticeKind.Warning);
            Closed+=delegate {
                resizeTimer.Stop();refreshVersion++;lifetime.Cancel();Upscales.Stop();
                FilesChanged-=ReloadAfterFileMove;
                // Large views are windows of their own now; they close with the folder window they came from.
                foreach(var view in Application.Current.Windows.OfType<Viewer>().Where(v=>v.Home==this).ToList())view.Close();
            };
            FilesChanged+=ReloadAfterFileMove;
            Activated+=delegate {TakeOverSettings();};
            localize=delegate {
                open.Content=emptyAction.Content=L.T("Add folder");
                newWindow.Content=L.T("New window");newWindow.ToolTip=L.T("Open another Love My Shelf window to drag files between two folders")+" (Ctrl+N)";
                AutomationProperties.SetName(newWindow,L.T("New window"));
                guide.Content=L.T("Guide & Options");guide.ToolTip=L.T("Guide & Options")+" (F1)";
                searchHint.Text=L.T("Search names");AutomationProperties.SetName(search,L.T("Search names"));
                openCoverFolder.Content=L.T("Open cover folder");
                allCropsButton.Content=L.T("Cover collection");unfinishedButton.Content=L.T("Unfinished cover collection");
                var labels=new[]{"All","Unfinished","Finished"};
                var names=new[]{"All items","Unfinished covers only","Finished covers only"};
                for(int i=0;i<filterButtons.Length;i++) {
                    filterButtons[i].Content=L.T(labels[i]);
                    filterButtons[i].ToolTip=L.T(names[i]);
                    AutomationProperties.SetName(filterButtons[i],L.T(names[i]));
                }
                updateFooter();
                titleBar.Update();
            };
            localize();
            SetupTabDrop();
            AllowDrop=true;
            PreviewDragOver+=(s,e)=>{
                if(e.Data.GetDataPresent(TabDragFormat))return; // a folder tab being rearranged
                if(e.Data.GetDataPresent(MoveFormat)){MoveDragOver(e);return;}
                var paths=e.Data.GetData(DataFormats.FileDrop) as string[];
                e.Effects=paths!=null && paths.Length==1 && Directory.Exists(paths[0])?DragDropEffects.Link:DragDropEffects.None;
                e.Handled=true;
            };
            Drop+=(s,e)=>{
                if(e.Data.GetDataPresent(MoveFormat)){MoveDrop(e);return;}
                var paths=e.Data.GetData(DataFormats.FileDrop) as string[];
                if(paths!=null && paths.Length==1 && Directory.Exists(paths[0]))OpenFolder(paths[0]);
            };
            PreviewKeyDown+=(s,e)=>{
                if(e.Key==Key.F5) {Refresh();StartCoverMigration();e.Handled=true;}
                else if(Keyboard.Modifiers==ModifierKeys.Control && e.Key==Key.O) {
                    PickFolder(); e.Handled=true;
                }
                else if(Keyboard.Modifiers==ModifierKeys.Control && e.Key==Key.N && !search.IsKeyboardFocusWithin) {
                    OpenAnotherWindow(); e.Handled=true;
                }
            };
            var albums=store.VisibleAlbums(); string lastFile=Path.Combine(store.Root,"last.txt");
            string last=File.Exists(lastFile)?File.ReadAllText(lastFile):"";
            var initial=last==CollectionKey?allCrops:last==UnfinishedKey?unfinished:(albums.FirstOrDefault(a=>Path.GetFileName(a.Folder)==last)??albums.FirstOrDefault()??
                allCrops);
            // On cold start the constructor runs before WPF installs its UI synchronization context.
            Loaded+=delegate {
                if(active!=null)return;
                // A window opened with "Open in new window" starts on that folder.
                Album start=startFolder==null?null:startFolder==CollectionKey?allCrops:startFolder==UnfinishedKey?unfinished:
                    store.VisibleAlbums().FirstOrDefault(a=>String.Equals(a.Folder,startFolder,StringComparison.OrdinalIgnoreCase));
                Select(start??initial);
            };
            ContentRendered+=delegate {
                if(coverSetupShown || another)return; // the first window already did this
                coverSetupShown=true;
                if(String.IsNullOrWhiteSpace(settings.CoverDirectory))EnsureCoverFolder(this);
                else StartCoverMigration();
                if(settings.CheckUpdates)CheckForUpdate();
            };
        }
        async void CheckForUpdate() {
            string newer=await AppInfo.NewerVersionAsync();
            if(newer==null || lifetime.IsCancellationRequested || !settings.CheckUpdates)return;
            Notify(L.F("Version {0} is out",newer),
                L.T("Click here to open the download page. Unzip the new ZIP over your Love My Shelf folder. Your covers and settings stay as they are."),
                ()=>{
                    try {System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppInfo.ReleasesPage) {UseShellExecute=true});}
                    catch(Exception ex) {UI.Error(this,ex);}
                },NoticeKind.Info);
        }
        void ChangeFolderFilter(int choice) {
            if(active==null || active.Source==null || folderFilter==choice)return;
            folderFilters[active.Folder]=folderFilter=choice;
            shuffledOrder=null;Refresh();
        }
        static void FooterLabel(Button button,string label,string compactLabel,bool compact) {
            string localized=L.T(label);
            button.Content=compact?compactLabel:localized;
            button.ToolTip=localized;
            AutomationProperties.SetName(button,localized);
        }
        void PickFolder() {
            string path=UI.PickFolder(this,L.T("Choose a folder containing photos or videos."),active==null?null:active.Source,false);
            if(path!=null)OpenFolder(path);
        }
        bool EnsureCoverFolder(Window owner) {
            if(!String.IsNullOrWhiteSpace(settings.CoverDirectory))return true;
            string path=pickCoverFolder(owner);
            if(path==null)return false;
            try {
                var updated=settings.Copy();updated.CoverDirectory=path;
                updated.Save(store.Root);settings=updated;
                StartCoverMigration();return true;
            } catch(Exception ex) {UI.Error(owner,ex);return false;}
        }
        void OpenCoverFolder() {
            if(!EnsureCoverFolder(this))return;
            try {
                // With covers saved beside originals, open the folder tab being viewed.
                string folder=settings.CoverDirectory;
                if(settings.CoverPlace!=CoverPlace.CoverFolder && active!=null && active.Source!=null && Directory.Exists(active.Source)) {
                    string sub=Path.Combine(active.Source,Preferences.SubfolderName);
                    folder=settings.CoverPlace==CoverPlace.Subfolder && Directory.Exists(sub)?sub:active.Source;
                }
                Directory.CreateDirectory(folder);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) {UseShellExecute=true});
            } catch(Exception ex) {UI.Error(this,ex);}
        }
        async void StartCoverMigration() {
            if(String.IsNullOrWhiteSpace(settings.CoverDirectory))return;
            if(migratingCovers){migrationRequested=true;return;}
            migratingCovers=true;
            string directory=settings.CoverDirectory;
            try {
                var covers=await Task.Run(()=>{int skipped;return store.SavedCovers(out skipped);},lifetime.Token);
                foreach(var photo in covers.Where(p=>p.CoverFile==null)) {
                    if(lifetime.IsCancellationRequested || settings.CoverDirectory!=directory)break;
                    await MigrateCover(photo,directory);
                }
            } catch(OperationCanceledException) {} catch(IOException) {} catch(UnauthorizedAccessException) {}
            finally {
                migratingCovers=false;
                if(migrationRequested && !lifetime.IsCancellationRequested) {
                    migrationRequested=false;StartCoverMigration();
                }
            }
        }
        internal Task MigrateLocatedCover(Photo photo) {
            return String.IsNullOrWhiteSpace(settings.CoverDirectory) || photo.CoverFile!=null?
                Task.FromResult(0):MigrateCover(photo,settings.CoverDirectory);
        }
        async Task MigrateCover(Photo photo,string directory) {
            try {
                var updated=await Task.Run(()=>SharedCoverFiles.Migrate(store,photo,directory,lifetime.Token),lifetime.Token);
                if(updated==null || lifetime.IsCancellationRequested)return;
                photo.CoverFile=updated.CoverFile;photo.CoverFileTicks=updated.CoverFileTicks;
                int index=photos.FindIndex(p=>p!=null && p.Folder==photo.Folder && p.Thumb==photo.Thumb);
                if(index>=0) {
                    photos[index].CoverFile=updated.CoverFile;photos[index].CoverFileTicks=updated.CoverFileTicks;
                    UpdatePhoto(index,photos[index]);
                }
            } catch(OperationCanceledException) {} catch(Exception ex) {
                // Individual unavailable originals keep their existing previews and can be retried.
                System.Diagnostics.Trace.WriteLine(ex);
            }
        }
        public void OpenFolder(string path) {
            try {
                if(!String.IsNullOrEmpty(settings.CoverDirectory) &&
                    String.Equals(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar),
                        Path.GetFullPath(settings.CoverDirectory).TrimEnd(Path.DirectorySeparatorChar),StringComparison.OrdinalIgnoreCase))
                    Select(allCrops);
                else Select(store.OpenFolder(path));
            } catch(Exception ex) {UI.Error(this,ex);}
        }
        void Select(Album album) {
            if(active==null || active.Folder!=album.Folder){shuffledOrder=null;searchTimer.Stop();search.Text="";}
            active=album; tabs.Children.Clear();scrollToTop=true;
            // The folder name leads the title, so side-by-side windows, the taskbar and Alt+Tab tell folders apart.
            Title=album.Name+" — Love My Shelf";
            UI.Use(allCropsButton,Control.ForegroundProperty,ShowingAllCrops?"Accent":"Text");
            UI.Use(allCropsButton,Control.BorderBrushProperty,ShowingAllCrops?"Accent":"Line");
            UI.Use(unfinishedButton,Control.ForegroundProperty,ShowingUnfinished?"Accent":"Text");
            UI.Use(unfinishedButton,Control.BorderBrushProperty,ShowingUnfinished?"Accent":"Line");
            filter.Visibility=active.Source==null?Visibility.Collapsed:Visibility.Visible;
            int view;
            folderFilter=folderFilters.TryGetValue(active.Folder,out view)?view:0;
            filterButtons[folderFilter].IsChecked=true;
            BuildTabs();
            try { File.WriteAllText(Path.Combine(store.Root,"last.txt"),Path.GetFileName(album.Folder)); } catch(IOException) { }
            Refresh();
            if(grid.Items.Count>0) grid.ScrollIntoView(grid.Items[0]);
        }
        const string TabDragFormat="LoveMyShelf.FolderTab";
        // Folder tabs, in the order the person arranged. Rebuilding them never touches the grid.
        void BuildTabs() {
            tabs.Children.Clear();
            var albums=store.VisibleAlbums();
            for(int position=0;position<albums.Count;position++) {
                Album item=albums[position];int place=position;
                bool selected=active.Folder==item.Folder;
                var button=UI.Btn(item.Name,()=>Select(item)); button.Margin=new Thickness(0,0,5,0);
                button.Tag=item;
                if(item.Source!=null) {
                    button.ToolTip=item.Source;
                    var hide=new MenuItem {Header=L.T("Hide folder tab")};
                    hide.Click+=delegate {
                        try{
                            store.HideFolder(item);
                            if(active.Folder==item.Folder)Select(allCrops);
                            else {
                                tabs.Children.Remove(button);
                                // That folder's tiles leave the list and the others slide forward.
                                if(ShowingUnfinished)
                                    RemoveTiles(p=>String.Equals(Path.GetDirectoryName(p.Folder),item.Folder,StringComparison.OrdinalIgnoreCase));
                            }
                        }
                        catch(Exception ex){UI.Error(this,ex);}
                    };
                    // The order is changed by dragging the tab sideways, so the menu has no move items.
                    var openNew=new MenuItem {Header=L.T("Open in new window")};
                    openNew.Click+=delegate {OpenAnotherWindow(item.Folder);};
                    button.ContextMenu=new ContextMenu();
                    button.ContextMenu.Items.Add(openNew);
                    button.ContextMenu.Items.Add(new Separator());button.ContextMenu.Items.Add(hide);
                    AttachTabDrag(button,item);
                }
                if(selected){UI.Use(button,Control.ForegroundProperty,"Accent");UI.Use(button,Control.BorderBrushProperty,"Accent");}
                tabs.Children.Add(button); if(selected) Dispatcher.BeginInvoke(new Action(()=>button.BringIntoView()));
            }
        }
        internal void MoveTab(Album album,int position) {
            try {store.MoveTab(album,position);BuildTabs();}
            catch(Exception ex){UI.Error(this,ex);}
        }
        // Drag a folder tab sideways to put it somewhere else. The collection buttons stay on the left.
        void AttachTabDrag(Button button,Album item) {
            Point? down=null;
            button.PreviewMouseLeftButtonDown+=(s,e)=>{down=e.GetPosition(button);};
            button.PreviewMouseLeftButtonUp+=delegate {down=null;};
            button.PreviewMouseMove+=(s,e)=>{
                if(down==null || e.LeftButton!=MouseButtonState.Pressed)return;
                if(Math.Abs((e.GetPosition(button)-down.Value).X)<SystemParameters.MinimumHorizontalDragDistance*2)return;
                down=null;button.ReleaseMouseCapture();e.Handled=true;
                button.Opacity=0.5;
                try {DragDrop.DoDragDrop(button,new DataObject(TabDragFormat,item.Folder),DragDropEffects.Move);}
                catch(System.Runtime.InteropServices.COMException) {}
                finally {button.Opacity=1;tabDropMarker.Visibility=Visibility.Collapsed;}
            };
        }
        // Where a dragged tab would land: the gap nearest the pointer.
        int TabDropIndex(Point point) {
            int index=0;
            foreach(UIElement child in tabs.Children) {
                var element=child as FrameworkElement;
                if(element==null || element==tabDropMarker)continue;
                double middle=element.TranslatePoint(new Point(element.ActualWidth/2,0),tabs).X;
                if(point.X>middle)index++;
            }
            return index;
        }
        readonly Border tabDropMarker=new Border {Width=2,Margin=new Thickness(-4,3,2,3),Visibility=Visibility.Collapsed,IsHitTestVisible=false};
        void SetupTabDrop() {
            tabDropMarker.SetResourceReference(Border.BackgroundProperty,"Accent");
            tabs.AllowDrop=true;tabs.Background=Brushes.Transparent;
            tabs.DragOver+=(s,e)=>{
                if(!e.Data.GetDataPresent(TabDragFormat))return;
                e.Effects=DragDropEffects.Move;e.Handled=true;
                int index=TabDropIndex(e.GetPosition(tabs));
                tabs.Children.Remove(tabDropMarker);
                tabDropMarker.Visibility=Visibility.Visible;
                tabs.Children.Insert(Math.Min(index,tabs.Children.Count),tabDropMarker);
            };
            tabs.DragLeave+=delegate {tabDropMarker.Visibility=Visibility.Collapsed;};
            tabs.Drop+=(s,e)=>{
                if(!e.Data.GetDataPresent(TabDragFormat))return;
                e.Handled=true;
                string folder=(string)e.Data.GetData(TabDragFormat);
                int index=TabDropIndex(e.GetPosition(tabs));
                tabs.Children.Remove(tabDropMarker);
                var albums=store.VisibleAlbums();
                int from=albums.FindIndex(a=>a.Folder==folder);
                if(from<0)return;
                // Dropping after its own position shifts the target left by one once it is removed.
                MoveTab(albums[from],index>from?index-1:index);
            };
        }
        // Saves and applies settings changed in the Guide & Options window. Changes take effect at once.
        internal bool ApplySettings(Preferences updated) {
            bool changedSort=settings.Sort!=updated.Sort || settings.Descending!=updated.Descending;
            bool changedLanguage=settings.Korean!=updated.Korean;
            bool changedTheme=settings.Theme!=updated.Theme;
            bool changedStorage=settings.CoverDirectory!=updated.CoverDirectory;
            bool changedGrid=settings.GridGif!=updated.GridGif;
            updated.LanguageChosen=true;
            try {updated.Save(store.Root);} catch(Exception ex) {UI.Error(this,ex);return false;}
            settings=updated;MotionWindow.UseDefaults(settings);
            if(changedTheme)Themes.Apply(settings.Theme);
            if(changedLanguage) {
                L.Korean=settings.Korean;localize();
                bool collection=ShowingAllCrops,pending=ShowingUnfinished;
                allCrops=new Album(CollectionKey,L.T("Cover collection"));
                unfinished=new Album(UnfinishedKey,L.T("Unfinished cover collection"));
                if(collection)active=allCrops;else if(pending)active=unfinished;
            }
            if(changedSort)shuffledOrder=null;
            if(changedSort || changedLanguage)Select(active);
            else if(changedGrid)LayoutRows();
            if(changedStorage) {
                if(String.IsNullOrWhiteSpace(settings.CoverDirectory))EnsureCoverFolder(this);
                else StartCoverMigration();
            }
            SettingsChanged();
            return true;
        }
        // The folder whose tiles are on screen (see Refresh).
        string shownAlbum;
        async void Refresh() {
            int version=++refreshVersion;
            int skipped=0; folderUnavailable=false;
            try {
                if(ShowingAllCrops || ShowingUnfinished) {
                    bool pending=ShowingUnfinished;
                    photos=new List<Photo>(); LayoutRows();
                    canShuffle=false;
                    emptyTitle.Text=L.T("Loading covers…");emptyHelp.Text="";empty.Visibility=Visibility.Visible;
                    count.Text="";count.ToolTip=null;
                    var sort=settings.Sort;
                    bool descending=settings.Descending;
                    var saved=await Task.Run(()=>pending?GalleryStore.SortPhotos(store.UnfinishedCovers(out skipped),sort,descending):
                        store.AllCrops(out skipped,sort,descending));
                    if(version!=refreshVersion) return;
                    photos=saved;
                } else {
                    // A folder with thousands of files is read off the UI thread so the window never freezes.
                    var album=active;var sort=settings.Sort;bool descending=settings.Descending;int filterNow=folderFilter;
                    var reading=Task.Run(()=>{
                        int unreadable;
                        var list=store.Photos(album,out unreadable,sort,descending);
                        if(filterNow>0)list=list.Where(p=>p.HasCrop==(filterNow==2)).ToList();
                        return Tuple.Create(list,unreadable);
                    });
                    // Opening another folder clears the old tiles if reading takes a moment; F5 and search keep them until the new list is ready.
                    if(await Task.WhenAny(reading,Task.Delay(150))!=reading && version==refreshVersion && shownAlbum!=album.Folder) {
                        photos=new List<Photo>();LayoutRows();
                        emptyTitle.Text=L.T("Loading…");emptyHelp.Text="";empty.Visibility=Visibility.Visible;
                        count.Text="";count.ToolTip=null;
                    }
                    var read=await reading;
                    if(version!=refreshVersion) return;
                    photos=read.Item1;skipped=read.Item2;
                }
                shownAlbum=active.Folder;
            }
            catch(IOException) { if(version!=refreshVersion)return; folderUnavailable=true; photos=new List<Photo>(); }
            catch(UnauthorizedAccessException) { if(version!=refreshVersion)return; folderUnavailable=true; photos=new List<Photo>(); }
            string query=search.Text.Trim();
            if(query.Length>0)photos=photos.Where(p=>p!=null && p.Name.IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0).ToList();
            ClearSelection();
            // Extra covers keep following their original's main tile (the sort is stable).
            if(shuffledOrder!=null) photos=photos.OrderBy(p=>{
                int rank; return shuffledOrder.TryGetValue(p.IsExtra?p.LeadFolder:p.Folder,out rank)?rank:Int32.MaxValue;
            }).ToList();
            skippedCount=skipped;
            var reflow=pendingReflow;pendingReflow=null;
            ShowPhotos(reflow);
        }
        void ShowPhotos(Reflow reflow=null) {
            SetCount(skippedCount);
            bool pending=ShowingPending;
            bool coversOnly=ShowingAllCrops || (active.Source!=null && folderFilter==2);
            emptyTitle.Text=folderUnavailable?L.T("This folder could not be opened"):
                L.T(pending?"No unfinished covers here":coversOnly?"No covers yet":"No photos or videos in this folder");
            emptyHelp.Text=folderUnavailable?L.T("Check the folder location and permissions, then press F5."):
                L.T(ShowingUnfinished?"Unfinished items from visible folder tabs appear here.":
                    pending?"Change the folder filter to see all items.":
                    coversOnly?"Open a local folder and make a cover to see it here.":"Choose another folder or add files and press F5.");
            empty.Visibility=photos.Count==0?Visibility.Visible:Visibility.Collapsed;
            if(reflow!=null)ApplyReflow(reflow);else LayoutRows();
        }
        void SetCount(int skipped) {
            // Counts are of originals: an original's extra cover tiles are not counted again.
            int total=photos.Count(p=>p!=null && !p.IsExtra);
            int covers=photos.Count(p=>p!=null && !p.IsExtra && p.HasCrop);
            canShuffle=total>1 || photos.Contains(null) || (ShowingPending && covers>0);
            count.ToolTip=null;
            if(ShowingAllCrops)count.Text=L.F("{0:N0} covers",covers);
            else if(ShowingPending)count.Text=L.F("{0:N0} unfinished",total-covers);
            else if(folderFilter==2)count.Text=L.F("{0:N0} finished",covers);
            else count.Text=L.F("{1:N0} of {0:N0} finished",total,covers);
            if(skipped>0)count.Text+="   ·   "+L.F("{0:N0} unreadable",skipped);
        }
        // Set before a layout that should start at the top (a different view, Shuffle).
        bool scrollToTop=true;
        volatile int centerRow;
        internal int CenterRow {get {return centerRow;}}
        double rowHeight;
        double cellWidth;
        // Tiles that are sliding to new places right now (see Reflow). MakeTile reads it while the rows are rebuilt.
        internal Reflow CurrentReflow {get;private set;}
        Reflow pendingReflow;
        readonly DispatcherTimer reflowTimer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(500)};
        void LayoutRows(Reflow reflow=null,int firstTileOverride=-1) {
            updateFooter();
            // Re-laying out (window resized, Cover size, F5, a changed tile) keeps the first visible tile in view.
            var scroller=FindScroller(grid);
            int firstTile=-1;
            if(!scrollToTop && scroller!=null && rowHeight>0 && columns>0 && scroller.VerticalOffset>0)
                firstTile=(int)Math.Floor(scroller.VerticalOffset/rowHeight)*columns;
            scrollToTop=false;
            double width=Math.Max(200,grid.ActualWidth-SystemParameters.VerticalScrollBarWidth-2);
            columns=Math.Max(2,(int)Math.Floor(width/density));
            double height=(width/columns-1)*16/9+1;
            rowHeight=height;cellWidth=width/columns;
            rows=new List<PhotoRow>();
            for(int i=0;i<photos.Count;i+=columns) rows.Add(MakeRow(i,height));
            if(reflow!=null && scroller!=null) {
                // Keep the same tile in view (to the pixel), build the rows at once, then let the tiles slide.
                double target=firstTileOverride>=0?Math.Floor(firstTileOverride/(double)columns)*height+reflow.Fraction:0;
                reflow.Cell=cellWidth;reflow.Viewport=scroller.ViewportHeight;
                reflow.NewOffset=Math.Max(0,Math.Min(target,Math.Max(0,rows.Count*height-scroller.ViewportHeight)));
                CurrentReflow=reflow;
                grid.ItemsSource=rows;grid.UpdateLayout();
                scroller.ScrollToVerticalOffset(reflow.NewOffset);grid.UpdateLayout();
                reflowTimer.Stop();reflowTimer.Start();
                return;
            }
            grid.ItemsSource=rows;
            if(firstTile>0 && scroller!=null) {
                double offset=Math.Floor(firstTile/(double)columns)*height;
                Dispatcher.BeginInvoke(new Action(()=>scroller.ScrollToVerticalOffset(offset)),DispatcherPriority.Loaded);
            }
        }
        static ScrollViewer FindScroller(DependencyObject parent) {
            for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++) {
                var child=VisualTreeHelper.GetChild(parent,i);
                var found=child as ScrollViewer??FindScroller(child);
                if(found!=null)return found;
            }
            return null;
        }
        void Shuffle() {
            bool pending=ShowingPending;
            var ordered=photos.Where(p=>p!=null && !p.IsExtra && (!pending || !p.HasCrop)).ToList();
            if(ordered.Count<2 && ordered.Count==photos.Count)return;
            bool changed=false;
            for(int i=ordered.Count-1;i>0;i--) {
                int j=random.Next(i+1);
                if(j==i) continue;
                Photo item=ordered[i]; ordered[i]=ordered[j]; ordered[j]=item; changed=true;
            }
            if(!changed) ordered.Reverse();
            shuffledOrder=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
            for(int i=0;i<ordered.Count;i++) shuffledOrder[ordered[i].Folder]=i;
            // Extra covers stay right after their original's main tile.
            var extras=photos.Where(p=>p!=null && p.IsExtra).ToLookup(p=>p.LeadFolder,StringComparer.OrdinalIgnoreCase);
            photos=ordered.SelectMany(p=>new[]{p}.Concat(extras[p.Folder])).ToList();scrollToTop=true;ShowPhotos();
            if(rows.Count>0)grid.ScrollIntoView(rows[0]);
        }
        internal static string SortName(PhotoSort sort) {
            return L.T(sort==PhotoSort.Modified?"Date modified":sort==PhotoSort.Size?"File size":sort==PhotoSort.Type?"File type":
                sort==PhotoSort.CoverOrder?"Cover work order":"File name");
        }
        // Direction in words that fit the criterion, instead of "ascending / descending".
        internal static string DirectionName(PhotoSort sort,bool descending) {
            switch(sort) {
                case PhotoSort.Modified:return L.T(descending?"Newest first":"Oldest first");
                case PhotoSort.Size:return L.T(descending?"Largest first":"Smallest first");
                case PhotoSort.CoverOrder:return L.T(descending?"Latest cover first":"First cover first");
                case PhotoSort.Type:return L.T(descending?"Type Z → A":"Type A → Z");
                default:return L.T(descending?"Z → A":"A → Z");
            }
        }
        internal static readonly PhotoSort[] SortChoices={PhotoSort.Name,PhotoSort.Modified,PhotoSort.Size,PhotoSort.Type,PhotoSort.CoverOrder};
        // The menu that opens above the Sort button. Built fresh each time so the checks match the current state.
        internal ContextMenu SortMenu() {
            var menu=new ContextMenu {PlacementTarget=sortButton,Placement=PlacementMode.Top};
            foreach(var choice in SortChoices) {
                var kind=choice;
                var item=new MenuItem {Header=SortName(kind),IsChecked=shuffledOrder==null && settings.Sort==kind};
                // A new criterion starts in its natural direction: newest first for dates, otherwise A → Z / smallest first.
                item.Click+=delegate {ChooseSort(kind,kind==settings.Sort?settings.Descending:kind==PhotoSort.Modified);};
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());
            foreach(bool descending in new[]{false,true}) {
                bool value=descending;
                var item=new MenuItem {Header=DirectionName(settings.Sort,value),IsChecked=shuffledOrder==null && settings.Descending==value};
                item.Click+=delegate {ChooseSort(settings.Sort,value);};
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());
            var shuffle=new MenuItem {Header=L.T("Shuffle"),IsChecked=shuffledOrder!=null,IsEnabled=canShuffle};
            shuffle.Click+=delegate {Shuffle();};
            menu.Items.Add(shuffle);
            return menu;
        }
        void OpenSortMenu() {
            var menu=SortMenu();
            sortButton.ContextMenu=menu;menu.IsOpen=true;
        }
        // Picking a criterion or direction saves it and reloads the view (empty slots are filled, like F5).
        internal void ChooseSort(PhotoSort sort,bool descending) {
            var updated=settings.Copy();updated.Sort=sort;updated.Descending=descending;
            try {updated.Save(store.Root);} catch(Exception ex) {UI.Error(this,ex);return;}
            settings=updated;shuffledOrder=null;scrollToTop=true;
            Select(active);SettingsChanged();
        }
        // Lets an open Guide & Options window show the new value.
        internal event Action SettingsChangedEvent;
        void SettingsChanged() {updateFooter();var handler=SettingsChangedEvent;if(handler!=null)handler();}
        PhotoRow MakeRow(int start,double height) {
            return new PhotoRow { Owner=this,Columns=columns,Height=height,Number=start/columns,Photos=photos.GetRange(start,Math.Min(photos.Count-start,columns)) };
        }
        internal bool EditFrames(Viewer playback) {
            if(!EnsureCoverFolder(playback))return false;
            Photo photo=playback.CurrentPhoto;
            string path=photo.Original;
            try {
                var picker=CreateFramePicker(playback,path,photo,playback.CropNearby);
                bool? saved=picker.ShowDialog();
                if(picker.OpenFullList) {
                    picker=CreateFramePicker(playback,path,photo,false);
                    saved=picker.ShowDialog();
                }
                return saved==true;
            } catch(Exception ex){if(!lifetime.IsCancellationRequested)UI.Error(playback,ex);return false;}
        }
        FramePicker CreateFramePicker(Viewer playback,string path,Photo photo,bool nearby) {
            var picker=new FramePicker(playback,path,photo,playback.CropFrame,playback.CropTime,
                settings.SnapEdges,settings.WheelZoomPercent,nearby,FullScreen.IsFull(this),Preferences.EditorPreviewSide);
            picker.Brightness=playback.PlaybackBrightness;picker.SkipSeconds=settings.SkipSeconds;
            var target=AddTarget(photo,path);
            picker.CommitCover=editor=>SaveCrop(target,editor,editor.FrameIndex);
            // The new cover's large view opens over the editor, the scene grid and the video before they close.
            picker.AfterCommit=()=>OpenSavedOver(playback,playback);
            picker.CommitMotion=async (editor,request)=>{
                var updated=await SaveCrop(target,editor,editor.FrameIndex,null,false);
                MakeMotion(updated,request);
            };
            return picker;
        }
        CropWindow CreateCropEditor(Window owner,Photo photo,BitmapSource preview) {
            return new CropWindow(owner,preview,photo.Name,photo.HasCrop?(Rect?)photo.Crop:null,photo.Rotation,
                settings.SnapEdges,settings.WheelZoomPercent,brightness:photo.Brightness);
        }
        internal async Task<Photo> EditSavedCover(Viewer viewer,Photo photo) {
            if(!EnsureCoverFolder(viewer))return null;
            try {
                var preview=await Task.Run(()=>{
                    var token=lifetime.Token;
                    string path=OriginalLocator.Resolve(store,photo,token);
                    return AnimationFrames.Supports(path)?AnimationFrames.SavedFrame(path,photo,Preferences.EditorPreviewSide,token):
                        Pictures.Load(path,Preferences.EditorPreviewSide);
                },lifetime.Token);
                if(!viewer.IsVisible || viewer.CurrentPhoto!=photo || lifetime.IsCancellationRequested)return null;
                var dialog=CreateCropEditor(viewer,photo,preview);
                dialog.Title=L.T("Edit cover");
                Rect startingCrop=dialog.Surface.Fraction;
                Photo updated=null;
                dialog.CommitCover=async editor=>{
                    // Keep the saved coordinates if the preview size introduced rounding and the crop did not move.
                    Rect crop=editor.Surface.Fraction==startingCrop?photo.Crop:editor.Surface.Fraction;
                    updated=await SaveCrop(photo,editor,null,crop,showAfter:false);
                };
                if(MotionMaker.Supports(photo.Original))
                    dialog.EnableMotion(async (editor,request)=>{
                        Rect crop=editor.Surface.Fraction==startingCrop?photo.Crop:editor.Surface.Fraction;
                        updated=await SaveCrop(photo,editor,null,crop,false,false);
                        MakeMotion(updated,request);
                    },()=>new MotionSource {Path=photo.Original,Moment=MotionSource.MomentOf(photo.Original,photo.FrameTime,photo.FrameIndex),
                        Crop=dialog.Surface.Fraction,Rotation=dialog.Surface.Rotation,Brightness=dialog.Surface.Brightness,
                        Still=Pictures.RenderRotatedCover(dialog.Surface.Source,dialog.Surface.Fraction,dialog.Surface.Rotation,540,960,dialog.Surface.Brightness)});
                return dialog.ShowDialog()==true?updated:null;
            } catch(OperationCanceledException){return null;}
            catch(Exception ex){if(viewer.IsVisible && viewer.CurrentPhoto==photo)UI.Error(viewer,ex);return null;}
        }
        public async void Edit(Photo photo) {
            try {
                if(!EnsureCoverFolder(this))return;
                if(AnimationFrames.Supports(photo.Original)) {
                    new Viewer(this,new[]{photo},0,true).ShowDialog();
                    ShowJustSaved();
                    return;
                }
                if(photo.Linked && photo.HasCrop) {
                    photo.Original=await Task.Run(()=>OriginalLocator.Resolve(store,photo,lifetime.Token));
                    if(lifetime.IsCancellationRequested)return;
                }
                var preview=Pictures.Load(photo.Original,Preferences.EditorPreviewSide);
                var dialog=CreateCropEditor(this,photo,preview);
                // A photo that already has a cover gets another one; the editor starts from the existing composition.
                var target=AddTarget(photo);
                dialog.CommitCover=editor=>SaveCrop(target,editor,photo.FrameIndex);
                dialog.AfterCommit=()=>OpenSavedOver(null,dialog);
                dialog.ShowDialog();
                ShowJustSaved();
            } catch(OperationCanceledException){} catch(Exception ex){if(!lifetime.IsCancellationRequested)UI.Error(this,ex);}
        }
        // After a cover is saved from the editor, its large view opens as soon as the editor closes.
        // The automated checks turn this off except where they test it.
        internal static bool OpenCoverAfterSave=true;
        Photo justSaved;
        // offerMotion: a video/GIF/WebP cover can offer Make GIF. showAfter: open the large view afterwards
        // (off when editing from a large view, which then shows the new cover itself).
        async Task<Photo> SaveCrop(Photo photo,CropWindow dialog,int? frame,Rect? savedCrop=null,bool offerMotion=true,bool showAfter=true) {
            var preview=dialog.Surface.Source;
            var crop=savedCrop??dialog.Surface.Fraction;
            double rotation=dialog.Surface.Rotation;
            double? frameTime=dialog.FrameTime;
            int offset=dialog.FrameTimeOffset;
            string directory=settings.CoverDirectory;
            int brightness=dialog.Surface.Brightness;
            var place=settings.CoverPlace;
            Photo updated=await Task.Run(()=>store.Reframe(photo,preview,crop,frame,rotation,frameTime,offset,
                CoverOutputMode.Native,0,lifetime.Token,directory,brightness,place));
            bool added=!photo.HasCrop && GalleryStore.RecordBase(photo)!=Path.GetFileName(photo.Folder);
            bool motion=offerMotion && MotionMaker.Supports(updated.Original);
            Action makeGif=motion?(Action)(()=>ShowMakeGif(ActiveAppWindow(),updated)):null;
            string gifLine=motion?" "+L.T("Click here to make a GIF cover from this JPG cover."):"";
            if(added)
                Notify(L.T("Added another cover"),L.T("Both covers are kept. The new cover now sits next to the first one.")+gifLine,makeGif);
            else if(motion && !(showAfter && OpenCoverAfterSave))
                // The large view has its own Make GIF button; the notice is for when it doesn't open.
                Notify(L.T("Cover saved"),gifLine.Trim(),makeGif);
            if(showAfter && OpenCoverAfterSave)justSaved=updated;
            if(updated.SavedToFallback)
                Notify(L.T("Saved in the cover folder instead"),
                    L.T("Love My Shelf couldn’t write next to the original, so this cover went to your cover folder. This can happen with phones and some cloud folders."),
                    null,NoticeKind.Warning);
            int index=photos.FindIndex(p=>p!=null && p.Folder==photo.Folder);
            if(index>=0)UpdatePhoto(index,updated);
            else if(added)InsertCover(updated);
            return updated;
        }
        // Make GIF for a cover that is already saved: same crop, rotation and brightness.
        internal void ShowMakeGif(Window owner,Photo photo) {
            if(!photo.HasCrop || !MotionMaker.Supports(photo.Original))return;
            try {
                string original=photo.Original;
                if(photo.Linked && !File.Exists(original))original=OriginalLocator.Resolve(store,photo,lifetime.Token);
                var source=new MotionSource {Path=original,Moment=MotionSource.MomentOf(original,photo.FrameTime,photo.FrameIndex),
                    Crop=photo.Crop,Rotation=photo.Rotation,Brightness=photo.Brightness};
                try {if(photo.CoverFile!=null && File.Exists(photo.CoverFile))source.Still=Pictures.Load(photo.CoverFile,960);} catch(Exception) {}
                // A cover that already has a GIF: Edit GIF cover, starting from the part and choices it was made with.
                var dialog=new MotionWindow(owner!=null && owner.IsVisible?owner:this,source,false,photo.HasMotion,photo.HasMotion?photo.MotionSettings:null);
                if(dialog.ShowDialog()==true)MakeMotion(photo,dialog.Value);
            } catch(Exception ex){Notify(L.T("Something went wrong"),ex.Message,null,NoticeKind.Warning);}
        }
        // Makes the GIF (or WebP/MP4) in the background and links it to the cover when done.
        internal async void MakeMotion(Photo photo,MotionRequest request) {
            // An edited GIF cover replaces the old file: it goes to the Recycle Bin and the new one takes its name.
            string old=photo.HasMotion?photo.MotionFile:null;
            var working=Notify(L.T("Making the GIF cover"),photo.Name+Environment.NewLine+
                L.T("It runs in the background, so you can keep using Love My Shelf. We’ll let you know here when it’s done."),null,NoticeKind.Busy);
            try {
                var place=settings.CoverPlace;string coverDirectory=settings.CoverDirectory;
                var result=await Task.Run(()=>{
                    string original=OriginalLocator.Resolve(store,photo,lifetime.Token);
                    string target=SharedCoverFiles.TargetFolder(place,coverDirectory,original,photo.Linked);
                    MotionResult made;
                    try {
                        made=MotionMaker.Make(photo,original,request,target,target!=coverDirectory && place==CoverPlace.BesideOriginal,lifetime.Token);
                    } catch(Exception ex) {
                        if(target==coverDirectory || lifetime.IsCancellationRequested || !(ex is IOException || ex is UnauthorizedAccessException) ||
                            ex is FileNotFoundException)throw;
                        made=MotionMaker.Make(photo,original,request,coverDirectory,false,lifetime.Token);
                    }
                    var linked=GalleryStore.AttachMotion(photo,made.File,request);
                    return Tuple.Create(made,linked);
                },lifetime.Token);
                if(lifetime.IsCancellationRequested)return;
                working.Dismiss();
                var ready=result.Item2;
                if(ready!=null && old!=null)ready=ReplaceOldMotion(ready,old,result.Item1.File,request)??ready;
                if(ready!=null)ReplaceShown(ready);
                // Tell the person when the original ran out before or after the cover's moment.
                string shorter="";
                if(result.Item1.Before<request.Before-0.05)shorter+=L.F("Only {0:0.#} s was available before the cover.",result.Item1.Before)+" ";
                if(result.Item1.After<request.After-0.05)shorter+=L.F("Only {0:0.#} s was available after the cover.",result.Item1.After)+" ";
                Notify(L.T("The GIF cover is ready"),shorter+L.T("Click here to go to the GIF cover’s large view."),
                    ready==null?(Action)null:()=>OpenReadyCover(ready));
            } catch(OperationCanceledException) {}
            catch(Exception ex) {
                working.Dismiss();
                if(!lifetime.IsCancellationRequested)
                    Notify(L.T("Couldn’t make the GIF cover"),photo.Name+Environment.NewLine+ex.Message,null,NoticeKind.Warning);
            }
            // The Make GIF window's ready-made file has been copied (or wasn't usable); either way it's done.
            finally {working.Dismiss();MotionMaker.DeleteLater(request.Prepared);}
        }
        // Where Make another cover saves: a new record for originals that already have one.
        Photo AddTarget(Photo photo,string resolved=null) {
            if(!photo.HasCrop || !photo.Linked)return photo;
            return GalleryStore.NewCoverRecord(photo,resolved??photo.Original);
        }
        internal void ShowHiddenCovers(Photo photo) {
            try {
                foreach(var cover in GalleryStore.CoversOf(photo).Where(p=>p.ExcludedFromCollection && p.Folder!=photo.Folder))
                    store.SetCollectionExcluded(cover,false);
                Notify(L.T("Hidden covers are back"),L.T("You can see them in Cover collection."),null);
                if(!ShowingAllCrops)ReloadKeepingPlace();
            } catch(Exception ex){UI.Error(this,ex);}
        }
        // Sends the cover's files (still, upscaled copy, GIF) to the Recycle Bin and forgets the cover.
        // The original stays where it is. In a folder the tile goes back to showing the original.
        internal void RecycleCover(Photo photo) {RecycleCovers(new[]{photo});}
        internal void RecycleCovers(IEnumerable<Photo> covers) {
            var done=new List<Photo>();
            foreach(var photo in covers.ToList()) {
                try {
                    var files=new[]{photo.CoverFile,photo.UpscaleFile,photo.MotionFile}
                        .Where(f=>!String.IsNullOrEmpty(f) && File.Exists(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    var handle=new System.Windows.Interop.WindowInteropHelper(this).Handle;
                    foreach(string file in files)moveToRecycleBin(file,handle);
                    GalleryStore.RemoveRecord(photo);
                    done.Add(photo);
                } catch(Exception ex){
                    Notify(L.T("Couldn’t move the cover to the Recycle Bin"),ex.Message,null,NoticeKind.Warning);
                }
            }
            if(done.Count==0)return;
            // The collection and extra cover tiles lose the tile; a main tile goes back to its original.
            // With "Cover O" chosen, an original without any cover left has nothing to show either.
            var gone=new HashSet<string>(done.Where(p=>ShowingAllCrops || p.IsExtra).Select(p=>p.Folder),StringComparer.OrdinalIgnoreCase);
            // A deleted first cover with other covers left: the earliest of them moves up to the first place,
            // so its tile simply goes away and the rest slide forward (no bare original tile is left behind).
            var promoted=new Dictionary<string,Photo>(StringComparer.OrdinalIgnoreCase);
            foreach(var photo in done.Where(p=>GalleryStore.RecordBase(p).Length==Path.GetFileName(p.Folder).Length)) {
                try {
                    string from=GalleryStore.PromoteExtraCover(photo.Folder,f=>{int percent;return Upscales.Stage(f,out percent)!=UpscaleStage.None;});
                    if(from==null)continue;
                    var now=GalleryStore.Read(photo.Folder,photo.Linked?photo.Original:null);
                    promoted[from]=now;gone.Add(photo.Folder);
                } catch(Exception) {}
            }
            foreach(var photo in done.Where(p=>!ShowingAllCrops && !p.IsExtra && !gone.Contains(p.Folder))) {
                bool otherCovers=photos.Any(p=>p!=null && p.IsExtra && String.Equals(p.LeadFolder,photo.Folder,StringComparison.OrdinalIgnoreCase) && !done.Any(d=>d.Folder==p.Folder));
                if(active.Source!=null && folderFilter==2 && !otherCovers)gone.Add(photo.Folder);
                else {
                    int index=photos.FindIndex(p=>p!=null && p.Folder==photo.Folder);
                    if(index>=0)UpdatePhoto(index,new Photo {Folder=photo.Folder,Name=photo.Name,Original=photo.Original,Linked=true});
                }
            }
            if(gone.Count>0)RemoveTiles(p=>gone.Contains(p.Folder),promoted);
            Notify(L.T("Moved the cover to the Recycle Bin"),L.T("The original is still in its folder. You can restore the cover file from the Recycle Bin."),null);
        }
        // After an edited GIF cover is made: the old file goes to the Recycle Bin and the new one takes its name
        // (when it's the same kind of file in the same folder). If anything fails, both files simply stay.
        Photo ReplaceOldMotion(Photo linked,string old,string made,MotionRequest request) {
            try {
                if(String.Equals(Path.GetFullPath(old),Path.GetFullPath(made),StringComparison.OrdinalIgnoreCase) || !File.Exists(old))return null;
                moveToRecycleBin(old,new System.Windows.Interop.WindowInteropHelper(this).Handle);
                bool sameKind=String.Equals(Path.GetDirectoryName(old),Path.GetDirectoryName(made),StringComparison.OrdinalIgnoreCase) &&
                    String.Equals(Path.GetExtension(old),Path.GetExtension(made),StringComparison.OrdinalIgnoreCase);
                if(!sameKind || File.Exists(old))return null;
                File.Move(made,old);
                var renamed=GalleryStore.AttachMotion(linked,old,request);
                if(renamed==null){File.Move(old,made);return null;}
                return renamed;
            } catch(Exception) {return null;}
        }
        // Sends only the GIF cover file to the Recycle Bin. The JPG cover stays; the record keeps the GIF's path,
        // so restoring the file from the Recycle Bin brings the GIF cover back.
        internal void RecycleMotion(Photo photo) {
            try {
                if(!photo.HasMotion)return;
                moveToRecycleBin(photo.MotionFile,new System.Windows.Interop.WindowInteropHelper(this).Handle);
                var updated=GalleryStore.Read(photo.Folder,photo.Linked?photo.Original:null);
                updated.ExcludedFromCollection=photo.ExcludedFromCollection;
                ReplaceShown(updated);
                Notify(L.T("Moved the GIF cover to the Recycle Bin"),L.T("The JPG cover is still here. You can restore the GIF file from the Recycle Bin."),null);
            } catch(Exception ex){
                Notify(L.T("Couldn’t move the cover to the Recycle Bin"),ex.Message,null,NoticeKind.Warning);
            }
        }
        public void ChangeCollection(Photo photo) {ChangeCollections(new[]{photo});}
        internal void ChangeCollections(IEnumerable<Photo> targets) {
            var gone=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try {
                foreach(var photo in targets.ToList()) {
                    int index=photos.IndexOf(photo); if(index<0) continue;
                    bool hiding=!photo.ExcludedFromCollection;
                    store.SetCollectionExcluded(photo,hiding);
                    // The collection never lists hidden covers, and the folder lists only the main tile of a hidden one.
                    if(ShowingAllCrops || (photo.IsExtra && hiding))gone.Add(photo.Folder);
                    else UpdatePhoto(index,photo);
                }
            } catch(Exception ex){UI.Error(this,ex);}
            if(gone.Count>0)RemoveTiles(p=>gone.Contains(p.Folder));
        }
        public void RecycleOriginal(Photo photo) {RecycleOriginals(new[]{photo});}
        // Sends originals to the Recycle Bin. Every tile of an original (its extra covers too) leaves the list at once.
        internal void RecycleOriginals(IEnumerable<Photo> targets) {
            if(ShowingAllCrops)return;
            var removed=new List<Photo>();
            foreach(var photo in targets.Where(p=>p.Linked && photos.Contains(p)).ToList()) {
                if(removed.Any(r=>SameOriginal(r,photo)))continue; // its main tile was already handled
                try {
                    // Operate only on the entry in the displayed folder, even if the viewer
                    // has previously located a same-named original somewhere else.
                    string source=active.Source;
                    if(ShowingUnfinished) {
                        var album=store.VisibleAlbums().FirstOrDefault(a=>String.Equals(a.Folder,Path.GetDirectoryName(photo.Folder),StringComparison.OrdinalIgnoreCase));
                        if(album==null)continue;
                        source=album.Source;
                    }
                    string path=Path.Combine(source,Path.GetFileName(photo.Name));
                    if(!File.Exists(path))throw new FileNotFoundException(L.T("The original is missing from this folder."),path);
                    if(photo.HasCrop && !OriginalLocator.MatchesOriginal(photo,path,lifetime.Token))
                        throw new IOException(L.T("This file does not match the saved original. Check it in File Explorer."));
                    moveToRecycleBin(path,new System.Windows.Interop.WindowInteropHelper(this).Handle);
                    removed.Add(photo);
                }catch(Exception ex){
                    count.Text=L.T("Recycle failed · ")+ex.Message;count.ToolTip=photo.Name+Environment.NewLine+ex.Message;
                }
            }
            if(removed.Count>0)RemoveTiles(p=>removed.Any(r=>SameOriginal(r,p)));
        }
        // ---------- Removing and adding tiles in place ----------
        // Notes where every tile on screen is now, so the tiles that stay can slide to their new places afterwards.
        Reflow CaptureReflow() {
            var reflow=new Reflow();
            var scroller=FindScroller(grid);
            double offset=scroller!=null?scroller.VerticalOffset:0;
            reflow.OldFolders=photos.Select(p=>p==null?"":p.Folder).ToList();
            var index=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
            for(int i=0;i<photos.Count;i++)if(photos[i]!=null && !index.ContainsKey(photos[i].Folder))index[photos[i].Folder]=i;
            if(rowHeight>0 && columns>0) {
                int firstRow=(int)Math.Floor(offset/rowHeight);
                reflow.FirstIndex=firstRow*columns;reflow.Fraction=offset-firstRow*rowHeight;
            }
            reflow.Cell=cellWidth;
            foreach(var pair in shownTiles.ToList()) {
                var tile=pair.Value;int at;
                if(!tile.IsVisible || !index.TryGetValue(pair.Key,out at) || columns<=0)continue;
                Point point;
                try {point=tile.TranslatePoint(new Point(),grid);} catch(InvalidOperationException) {continue;}
                reflow.Old[pair.Key]=point;
                if(!reflow.Calibrated) {
                    reflow.OriginX=point.X-(at%columns)*cellWidth;
                    reflow.OriginY=point.Y-((at/columns)*rowHeight-offset);
                    reflow.Calibrated=true;
                }
                var image=tile.Content as Image;
                if(image!=null && image.Source!=null && photos[at]!=null)reflow.KeepImage(photos[at],image.Source);
            }
            return reflow;
        }
        // Builds the rows for the new list, keeping the tile that was at the top of the view at the top.
        void ApplyReflow(Reflow reflow) {
            int first=-1;
            if(reflow.Calibrated) {
                var now=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
                for(int i=0;i<photos.Count;i++)if(photos[i]!=null && !now.ContainsKey(photos[i].Folder))now[photos[i].Folder]=i;
                first=photos.Count;
                for(int i=reflow.FirstIndex;i<reflow.OldFolders.Count;i++) {
                    int at;
                    if(now.TryGetValue(reflow.OldFolders[i],out at)){first=at;break;}
                }
            }
            LayoutRows(reflow,first);
        }
        void ReloadKeepingPlace() {
            if(lifetime.IsCancellationRequested)return;
            pendingReflow=CaptureReflow();Refresh();
        }
        // Takes tiles out of the list right away; the ones behind them slide forward.
        // promoted: tiles that stay but now stand for another record (old folder → new photo), such as a cover moved up.
        void RemoveTiles(Func<Photo,bool> remove,IDictionary<string,Photo> promoted=null) {
            if(photos.Count==0 || rows==null)return;
            var reflow=CaptureReflow();
            var focused=focusIndex>=0 && focusIndex<photos.Count && photos[focusIndex]!=null?photos[focusIndex].Folder:null;
            var kept=photos.Where(p=>p!=null && !remove(p)).ToList();
            bool replaced=false;
            if(promoted!=null) {
                for(int i=0;i<kept.Count;i++) {
                    Photo now;
                    if(!promoted.TryGetValue(kept[i].Folder,out now))continue;
                    string old=kept[i].Folder;
                    reflow.Rename(old,now.Folder);
                    if(String.Equals(focused,old,StringComparison.OrdinalIgnoreCase))focused=now.Folder;
                    if(selected.Remove(old))selected.Add(now.Folder);
                    kept[i]=now;replaced=true;
                }
            }
            if(kept.Count==photos.Count && !replaced)return;
            photos=kept;
            var present=new HashSet<string>(kept.Select(p=>p.Folder),StringComparer.OrdinalIgnoreCase);
            selected.RemoveWhere(f=>!present.Contains(f));
            focusIndex=focused==null?-1:kept.FindIndex(p=>String.Equals(p.Folder,focused,StringComparison.OrdinalIgnoreCase));
            anchorIndex=focusIndex;
            ShowPhotos(reflow);
            RefreshSelection();
        }
        // A new extra cover appears right after the last tile of its original.
        void InsertCover(Photo cover) {
            if(rows==null || active==null || ShowingUnfinished || (active.Source!=null && folderFilter==1))return;
            string folder=Path.GetDirectoryName(cover.Folder),recordBase=GalleryStore.RecordBase(cover);
            int last=-1;
            for(int i=0;i<photos.Count;i++) {
                var p=photos[i];
                if(p!=null && String.Equals(Path.GetDirectoryName(p.Folder),folder,StringComparison.OrdinalIgnoreCase) &&
                    String.Equals(GalleryStore.RecordBase(p),recordBase,StringComparison.OrdinalIgnoreCase))last=i;
            }
            if(last<0)return; // that original isn't shown here; the cover appears when the view is loaded again
            if(active.Source!=null)cover.LeadFolder=photos[last].LeadFolder??photos[last].Folder;
            var reflow=CaptureReflow();
            reflow.Fresh.Add(cover.Folder);
            photos.Insert(last+1,cover);
            ShowPhotos(reflow);
        }
        static bool SameOriginal(Photo a,Photo b) {
            return String.Equals(a.Name,b.Name,StringComparison.OrdinalIgnoreCase) &&
                String.Equals(Path.GetDirectoryName(a.Folder),Path.GetDirectoryName(b.Folder),StringComparison.OrdinalIgnoreCase);
        }
        void UpdatePhoto(int index,Photo photo) {
            // A replaced tile (a new picture, a GIF) stays an extra tile of the same original.
            if(photo!=null && photo.LeadFolder==null && photos[index]!=null && photos[index].LeadFolder!=null && photo.Folder==photos[index].Folder)
                photo.LeadFolder=photos[index].LeadFolder;
            photos[index]=photo;
            rows[index/columns].Replace(index%columns,photo);
            SetCount(skippedCount);
        }
        public void OpenPhoto(Photo photo) {
            OpenPhoto(photo,null);
        }
        // ---------- A second window and moving originals between folders ----------
        internal const string MoveFormat="LoveMyShelf.Originals";
        // Raised after files were moved or copied between folders, so every open window reads its folder again.
        static event Action FilesChanged;
        // The last moves, newest last. Ctrl+Z puts the newest one back.
        static readonly List<List<Tuple<string,string>>> moveHistory=new List<List<Tuple<string,string>>>();
        void ReloadAfterFileMove() {ReloadKeepingPlace();}
        internal static bool AnotherWindowOpen {get {return Application.Current!=null && Application.Current.Windows.OfType<GalleryWindow>().Count(w=>w.IsVisible)>1;}}

        // Opens a second window beside this one. A full-screen or maximized window is first made smaller so both can be seen.
        internal void OpenAnotherWindow() {OpenAnotherWindow(null);}
        // folder: the tab the new window starts on (a folder, or the key of a collection); null starts where it usually would.
        string startFolder;
        internal void OpenAnotherWindow(string folder) {
            try {
                var other=new GalleryWindow(store,moveToRecycleBin,pickCoverFolder,true);
                other.startFolder=folder;
                bool shared=FullScreen.IsFull(this) || WindowState==WindowState.Maximized;
                if(FullScreen.IsFull(this))FullScreen.Leave(this);
                if(WindowState!=WindowState.Normal)WindowState=WindowState.Normal;
                other.WindowStartupLocation=WindowStartupLocation.Manual;
                if(shared) {
                    var handle=new System.Windows.Interop.WindowInteropHelper(this).Handle;
                    var pixels=System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
                    var source=PresentationSource.FromVisual(this);
                    var toDips=source!=null?source.CompositionTarget.TransformFromDevice:Matrix.Identity;
                    var area=new Rect(toDips.Transform(new Point(pixels.Left,pixels.Top)),toDips.Transform(new Point(pixels.Right,pixels.Bottom)));
                    double half=Math.Floor(area.Width/2);
                    Left=area.Left;Top=area.Top;Width=half;Height=area.Height;
                    other.Left=area.Left+half;other.Top=area.Top;other.Width=area.Width-half;other.Height=area.Height;
                } else {
                    other.Left=Left+40;other.Top=Top+40;other.Width=Width;other.Height=Height;
                }
                other.Show();
                other.Activate();
            } catch(Exception ex) {UI.Error(this,ex);}
        }

        // Another window may have saved options (language, theme, sort, …). Take them over when this window is used again.
        void TakeOverSettings() {
            try {
                var fresh=Preferences.Load(store.Root);
                if(fresh.SameAs(settings))return;
                bool sortChanged=fresh.Sort!=settings.Sort || fresh.Descending!=settings.Descending,languageChanged=fresh.Korean!=settings.Korean;
                bool themeChanged=fresh.Theme!=settings.Theme,gridChanged=fresh.GridGif!=settings.GridGif;
                fresh.LanguageChosen=settings.LanguageChosen || fresh.LanguageChosen;
                settings=fresh;MotionWindow.UseDefaults(settings);
                if(themeChanged)Themes.Apply(settings.Theme);
                if(languageChanged) {
                    L.Korean=settings.Korean;localize();
                    bool collection=ShowingAllCrops,pending=ShowingUnfinished;
                    allCrops=new Album(CollectionKey,L.T("Cover collection"));
                    unfinished=new Album(UnfinishedKey,L.T("Unfinished cover collection"));
                    if(collection)active=allCrops;else if(pending)active=unfinished;
                }
                if(sortChanged)shuffledOrder=null;
                if(sortChanged || languageChanged)Select(active);
                else if(gridChanged)LayoutRows();
                SettingsChanged();
            } catch(Exception) {}
        }

        // The outline and sentence shown over the grid while files from another window are dragged over it.
        readonly Border dropHint=new Border {BorderThickness=new Thickness(3),Visibility=Visibility.Collapsed,IsHitTestVisible=false};
        readonly TextBlock dropHintText=new TextBlock {FontSize=14,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap,TextAlignment=TextAlignment.Center};
        readonly DispatcherTimer dropHintTimer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(300)};
        void SetupDropHint(Grid host) {
            dropHint.SetResourceReference(Border.BorderBrushProperty,"Accent");
            var label=new Border {CornerRadius=new CornerRadius(12),Padding=new Thickness(16,9,16,10),Margin=new Thickness(12),MaxWidth=520,
                HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Bottom};
            label.SetResourceReference(Border.BackgroundProperty,"CardBack");label.SetResourceReference(Border.BorderBrushProperty,"Accent");label.BorderThickness=new Thickness(1);
            dropHintText.SetResourceReference(TextBlock.ForegroundProperty,"Text");
            label.Child=dropHintText;dropHint.Child=label;
            Panel.SetZIndex(dropHint,6);host.Children.Add(dropHint);
            dropHintTimer.Tick+=delegate {HideDropHint();};
        }
        void ShowDropHint(string text) {
            dropHintText.Text=NoticeLook.KeepWords(text);dropHint.Visibility=Visibility.Visible;
            dropHintTimer.Stop();dropHintTimer.Start(); // DragOver keeps coming while the pointer is here
        }
        void HideDropHint() {dropHintTimer.Stop();dropHint.Visibility=Visibility.Collapsed;}

        string[] DraggedOriginals(IDataObject data) {
            var text=data.GetData(MoveFormat) as string;
            return String.IsNullOrEmpty(text)?new string[0]:text.Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries);
        }
        // The folder tab being shown is where dropped files go; the cover collections have no folder to drop into.
        bool CanDropMoves(string[] originals) {
            return originals.Length>0 && active!=null && active.Source!=null && Directory.Exists(active.Source)
                && originals.Any(p=>!Mover.SameFolder(Path.GetDirectoryName(p),active.Source));
        }
        void MoveDragOver(DragEventArgs e) {
            var originals=DraggedOriginals(e.Data);
            e.Handled=true;
            if(!CanDropMoves(originals)){e.Effects=DragDropEffects.None;HideDropHint();return;}
            bool copy=(e.KeyStates&DragDropKeyStates.ControlKey)!=0;
            e.Effects=copy?DragDropEffects.Copy:DragDropEffects.Link; // Link is this app's own "move" (the allowed effects keep other programs from moving the files)
            ShowDropHint(L.F(copy?"Let go to copy {0:N0} to “{1}”.":"Let go to move {0:N0} to “{1}”. Hold Ctrl to copy instead.",originals.Length,active.Name));
        }
        void MoveDrop(DragEventArgs e) {
            e.Handled=true;HideDropHint();
            var originals=DraggedOriginals(e.Data);
            if(!CanDropMoves(originals))return;
            bool copy=(e.KeyStates&DragDropKeyStates.ControlKey)!=0;
            e.Effects=copy?DragDropEffects.Copy:DragDropEffects.Link;
            var target=active;
            // Start after the drag ends, so a question or notice doesn't open in the middle of the drag.
            Dispatcher.BeginInvoke(new Action(()=>MoveOriginals(originals,copy,target)),DispatcherPriority.ApplicationIdle);
        }
        Tuple<ConflictChoice,bool> AskConflict(ConflictInfo info) {
            Tuple<ConflictChoice,bool> answer=Tuple.Create(ConflictChoice.Skip,false);
            Dispatcher.Invoke(new Action(()=>{
                var dialog=new ConflictWindow(ActiveAppWindow(),info);dialog.ShowDialog();
                answer=Tuple.Create(dialog.Choice,dialog.ApplyToAll);
            }));
            return answer;
        }
        void RecycleForMove(string path) {
            Dispatcher.Invoke(new Action(()=>moveToRecycleBin(path,new System.Windows.Interop.WindowInteropHelper(this).Handle)));
        }
        async void MoveOriginals(string[] originals,bool copy,Album target) {
            string folderName=target.Name;
            var busy=Notify(L.T(copy?"Copying files…":"Moving files…"),L.F("{0:N0} files are going to “{1}”.",originals.Length,folderName),null,NoticeKind.Busy);
            MoveReport report=null;Exception error=null;
            try {
                report=await Task.Run(()=>Mover.RunDetailed(store,originals,target.Source,target.Folder,copy,folderName,AskConflict,RecycleForMove));
            } catch(Exception ex) {error=ex;}
            finally {busy.Dismiss();}
            if(error!=null){Notify(L.T("Couldn’t move the files"),error.Message,null,NoticeKind.Warning);return;}
            if(report.Moved.Count>0)lock(moveHistory)moveHistory.Add(report.Moved);
            if(report.Moved.Count+report.Copied>0 && FilesChanged!=null)FilesChanged();
            string done=copy?L.F("Copied {0:N0} to “{1}”.",report.Copied,folderName):L.F("Moved {0:N0} to “{1}”.",report.Moved.Count,folderName);
            var lines=new List<string>();
            if(report.Moved.Count>0)lines.Add(L.T("Press Ctrl+Z to put them back."));
            if(report.Skipped>0)lines.Add(L.F("{0:N0} skipped because of the same name.",report.Skipped));
            if(report.AlreadyThere>0)lines.Add(L.F("{0:N0} were already in this folder.",report.AlreadyThere));
            if(report.Failed.Count==0) {
                if(report.Moved.Count+report.Copied>0)Notify(copy?L.T("Copied"):L.T("Moved"),done+(lines.Count>0?Environment.NewLine+String.Join(" ",lines):""),null);
                else if(lines.Count>0)Notify(L.T("Nothing was moved"),String.Join(" ",lines),null,NoticeKind.Info);
            } else {
                Notify(L.T("Some files couldn’t be moved"),
                    String.Join(Environment.NewLine,report.Failed.Take(3))+(report.Failed.Count>3?Environment.NewLine+L.F("and {0:N0} more",report.Failed.Count-3):"")+
                    (report.Moved.Count+report.Copied>0?Environment.NewLine+done:""),null,NoticeKind.Warning);
            }
        }
        // Ctrl+Z: puts the files of the latest move back where they were.
        async void UndoMove() {
            List<Tuple<string,string>> last;
            lock(moveHistory) {
                if(moveHistory.Count==0)return;
                last=moveHistory[moveHistory.Count-1];moveHistory.RemoveAt(moveHistory.Count-1);
            }
            var busy=Notify(L.T("Putting the files back…"),"",null,NoticeKind.Busy);
            MoveReport report=null;Exception error=null;
            try {
                report=await Task.Run(()=>Mover.UndoDetailed(store,last,L.T("the old folder"),AskConflict,RecycleForMove));
            } catch(Exception ex) {error=ex;}
            finally {busy.Dismiss();}
            if(error!=null){Notify(L.T("Couldn’t put the files back"),error.Message,null,NoticeKind.Warning);return;}
            if(report.Moved.Count>0 && FilesChanged!=null)FilesChanged();
            if(report.Failed.Count>0)Notify(L.T("Some files couldn’t be put back"),String.Join(Environment.NewLine,report.Failed.Take(3)),null,NoticeKind.Warning);
            else Notify(L.T("Put back"),L.F("{0:N0} files are back where they were.",report.Moved.Count),null);
        }
        // What a drag from this tile carries: every chosen tile when the dragged one is chosen, otherwise just that one.
        internal List<Photo> DragTargets(Photo photo) {
            if(selected.Contains(photo.Folder)) {
                var chosen=SelectedPhotos.ToList();
                if(chosen.Count>0)return chosen;
            }
            return new List<Photo>{photo};
        }

        // ---------- Explorer state: selection, keys, search, rename, details ----------
        readonly HashSet<string> selected=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int focusIndex=-1,anchorIndex=-1;
        readonly Dictionary<string,Button> shownTiles=new Dictionary<string,Button>(StringComparer.OrdinalIgnoreCase);
        readonly TextBox search=new TextBox {Width=170,Padding=new Thickness(8,4,8,4),VerticalContentAlignment=VerticalAlignment.Center};
        readonly TextBlock searchHint=UI.Text("",12,true);
        readonly DispatcherTimer searchTimer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(250)};
        internal IEnumerable<Photo> SelectedPhotos {get {return photos.Where(p=>p!=null && selected.Contains(p.Folder));}}
        FrameworkElement SearchBox() {
            var host=new Grid {VerticalAlignment=VerticalAlignment.Center};
            search.SetResourceReference(Control.BorderBrushProperty,"Line");
            searchHint.IsHitTestVisible=false;searchHint.Margin=new Thickness(10,0,0,0);
            host.Children.Add(search);host.Children.Add(searchHint);
            search.TextChanged+=delegate {
                searchHint.Visibility=search.Text.Length==0?Visibility.Visible:Visibility.Collapsed;
                searchTimer.Stop();searchTimer.Start();
            };
            searchTimer.Tick+=delegate {searchTimer.Stop();Refresh();};
            search.PreviewKeyDown+=(s,e)=>{
                if(e.Key==Key.Escape && search.Text.Length>0){search.Text="";e.Handled=true;}
                else if(e.Key==Key.Enter || e.Key==Key.Down){grid.Focus();if(focusIndex<0)MoveFocus(0,false);e.Handled=true;}
            };
            return host;
        }
        internal void TrackTile(Button tile,Photo photo) {
            shownTiles[photo.Folder]=tile;
            tile.MouseEnter+=delegate {ShowNameSoon(tile,photo.Name);};
            tile.MouseLeave+=delegate {if(nameTile==tile)HideName();};
            tile.ContextMenuOpening+=delegate {HideName();};
            tile.Unloaded+=delegate {Button current;if(shownTiles.TryGetValue(photo.Folder,out current) && current==tile)shownTiles.Remove(photo.Folder);};
            ShowSelected(tile,selected.Contains(photo.Folder),focusIndex>=0 && focusIndex<photos.Count && photos[focusIndex]==photo);
        }
        // Hover name card: the whole file name, just below the tile (above it on the last visible row),
        // covering the neighbouring tile but never the one it names. It waits a moment so sweeping the
        // mouse across the grid doesn't flash names everywhere.
        readonly Canvas nameLayer=new Canvas {IsHitTestVisible=false,ClipToBounds=true};
        readonly Border nameCard=new Border {CornerRadius=new CornerRadius(8),BorderThickness=new Thickness(1),Padding=new Thickness(10,6,10,7),
            Visibility=Visibility.Collapsed,SnapsToDevicePixels=true};
        readonly TextBlock nameText=new TextBlock {FontSize=13,TextWrapping=TextWrapping.Wrap,TextAlignment=TextAlignment.Center,
            LineHeight=19,LineStackingStrategy=LineStackingStrategy.BlockLineHeight};
        readonly DispatcherTimer nameTimer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(250)};
        Button nameTile;
        internal string ShownName {get{return nameCard.Visibility==Visibility.Visible?nameText.Text.Replace(((char)0x2060).ToString(),""):null;}}
        internal Rect ShownNameBounds {get{return new Rect(Canvas.GetLeft(nameCard),Canvas.GetTop(nameCard),nameCard.ActualWidth,nameCard.ActualHeight);}}
        void SetupNameCard(Grid host) {
            nameCard.SetResourceReference(Border.BackgroundProperty,"CardBack");nameCard.SetResourceReference(Border.BorderBrushProperty,"CardLine");
            nameCard.Effect=new System.Windows.Media.Effects.DropShadowEffect {BlurRadius=14,ShadowDepth=3,Direction=270,Opacity=0.28,Color=Colors.Black};
            nameText.SetResourceReference(TextBlock.ForegroundProperty,"Text");
            TextOptions.SetTextFormattingMode(nameText,TextFormattingMode.Ideal);
            nameCard.Child=nameText;nameLayer.Children.Add(nameCard);
            Panel.SetZIndex(nameLayer,5);host.Children.Add(nameLayer);
            nameTimer.Tick+=delegate {nameTimer.Stop();PlaceName();};
            grid.AddHandler(ScrollViewer.ScrollChangedEvent,new ScrollChangedEventHandler((s,e)=>{if(e.VerticalChange!=0)HideName();}));
            Deactivated+=delegate {HideName();};
        }
        internal void ShowNameSoon(Button tile,string name) {
            // Korean words stay whole when the name wraps onto a second line.
            nameTile=tile;nameText.Text=NoticeLook.KeepWords(name);
            nameTimer.Stop();
            if(nameCard.Visibility==Visibility.Visible)PlaceName(); // already showing a name: move straight to this tile
            else nameTimer.Start();
        }
        internal void HideName() {
            nameTimer.Stop();nameTile=null;
            nameCard.BeginAnimation(OpacityProperty,null);nameCard.Visibility=Visibility.Collapsed;
        }
        internal void PlaceName() {
            var tile=nameTile;
            if(tile==null || !tile.IsVisible || !tile.IsMouseOver && !forceNameForChecks || nameLayer.ActualWidth<=0){HideName();return;}
            double hostWidth=nameLayer.ActualWidth,hostHeight=nameLayer.ActualHeight;
            var corner=tile.TranslatePoint(new Point(),nameLayer);
            double width=tile.ActualWidth,height=tile.ActualHeight;
            double widest=Math.Max(80,Math.Min(hostWidth-8,Math.Max(width*2,300)));
            nameCard.MaxWidth=widest;nameCard.MinWidth=Math.Min(width,widest);
            // A collapsed card measures as zero, which let the first card run off the right edge.
            // Show it fully transparent first, then measure, place and fade it in.
            bool appearing=nameCard.Visibility!=Visibility.Visible;
            if(appearing){nameCard.BeginAnimation(OpacityProperty,null);nameCard.Opacity=0;nameCard.Visibility=Visibility.Visible;}
            nameCard.Measure(new Size(widest,Double.PositiveInfinity));
            var size=nameCard.DesiredSize;
            double left=Math.Max(4,Math.Min(corner.X+width/2-size.Width/2,hostWidth-size.Width-4));
            double below=corner.Y+height+3,above=corner.Y-size.Height-3,top;
            if(below+size.Height<=hostHeight-2)top=below;
            else if(above>=2)top=above;
            else top=hostHeight-(corner.Y+height)>corner.Y?Math.Min(below,hostHeight-size.Height-2):Math.Max(2,above);
            Canvas.SetLeft(nameCard,left);Canvas.SetTop(nameCard,top);
            if(appearing)nameCard.BeginAnimation(OpacityProperty,new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(120)));
        }
        // The checks can't move the real mouse, so they ask for the card directly.
        internal static bool forceNameForChecks;
        static void ShowSelected(Button tile,bool on,bool focused) {
            if(on){tile.SetResourceReference(Control.BorderBrushProperty,"Accent");tile.BorderThickness=new Thickness(3);}
            else if(focused){tile.SetResourceReference(Control.BorderBrushProperty,"Accent");tile.BorderThickness=new Thickness(1);}
            else {tile.BorderThickness=new Thickness(0);}
        }
        void RefreshSelection() {
            foreach(var pair in shownTiles) {
                int index=photos.FindIndex(p=>p!=null && String.Equals(p.Folder,pair.Key,StringComparison.OrdinalIgnoreCase));
                ShowSelected(pair.Value,selected.Contains(pair.Key),index>=0 && index==focusIndex);
            }
            if(selected.Count>1)count.Text=L.F("{0:N0} selected",selected.Count);
            else SetCount(skippedCount);
        }
        internal void ClearSelection() {
            selected.Clear();focusIndex=-1;anchorIndex=-1;
            if(shownTiles.Count>0)RefreshSelection();
        }
        // A plain click selects the tile and a double click (or Enter) opens the large view, like File Explorer;
        // with "One click" chosen in the options a plain click opens it. Ctrl adds or removes, Shift selects a range.
        int lastClickCount=1;
        internal void NoteClickCount(int count) {lastClickCount=count;}
        internal void TileClicked(Photo photo) {
            int index=photos.IndexOf(photo);if(index<0)return;
            int clicks=lastClickCount;lastClickCount=1;
            var modifiers=Keyboard.Modifiers;
            if((modifiers&ModifierKeys.Control)!=0) {
                if(!selected.Remove(photo.Folder))selected.Add(photo.Folder);
                focusIndex=anchorIndex=index;RefreshSelection();return;
            }
            if((modifiers&ModifierKeys.Shift)!=0 && anchorIndex>=0) {
                SelectRange(anchorIndex,index);focusIndex=index;RefreshSelection();return;
            }
            selected.Clear();focusIndex=anchorIndex=index;
            if(settings.OpenByDoubleClick) {
                selected.Add(photo.Folder);RefreshSelection();
                if(clicks>=2)OpenPhoto(photo);
                return;
            }
            // One-click mode only remembers where you are (for the arrow keys); it doesn't leave a selection behind.
            RefreshSelection();
            OpenPhoto(photo);
        }
        void SelectRange(int from,int to) {
            selected.Clear();
            for(int i=Math.Min(from,to);i<=Math.Max(from,to);i++)if(photos[i]!=null)selected.Add(photos[i].Folder);
        }
        void MoveFocus(int target,bool extend) {
            if(photos.Count==0)return;
            target=Math.Max(0,Math.Min(photos.Count-1,target));
            // Skip empty slots in the direction of travel.
            int step=target>=focusIndex?1:-1;
            while(target>=0 && target<photos.Count && photos[target]==null)target+=step;
            if(target<0 || target>=photos.Count)return;
            focusIndex=target;
            if(extend && anchorIndex>=0)SelectRange(anchorIndex,target);
            else {selected.Clear();selected.Add(photos[target].Folder);anchorIndex=target;}
            if(rows!=null && columns>0)grid.ScrollIntoView(rows[target/columns]);
            RefreshSelection();
        }
        void ExplorerKeys(object sender,KeyEventArgs e) {
            if(e.Handled || search.IsKeyboardFocusWithin || !IsActiveWindowForKeys)return;
            var key=e.Key==Key.ImeProcessed?e.ImeProcessedKey:e.Key;
            bool shift=(Keyboard.Modifiers&ModifierKeys.Shift)!=0,control=(Keyboard.Modifiers&ModifierKeys.Control)!=0;
            switch(key) {
                case Key.Escape:
                    if(selected.Count==0)return; // nothing selected: Esc keeps its usual job (leaving full screen)
                    ClearSelection();break;
                case Key.Right:MoveFocus(focusIndex<0?0:focusIndex+1,shift);break;
                case Key.Left:MoveFocus(focusIndex<0?0:focusIndex-1,shift);break;
                case Key.Down:MoveFocus(focusIndex<0?0:focusIndex+columns,shift);break;
                case Key.Up:MoveFocus(focusIndex<0?0:focusIndex-columns,shift);break;
                case Key.Home:MoveFocus(0,shift);break;
                case Key.End:MoveFocus(photos.Count-1,shift);break;
                case Key.Enter:
                    if(focusIndex<0 || photos[focusIndex]==null)return;
                    OpenPhoto(photos[focusIndex]);break;
                case Key.A:
                    if(!control)return;
                    selected.Clear();foreach(var p in photos)if(p!=null)selected.Add(p.Folder);
                    anchorIndex=0;RefreshSelection();break;
                case Key.Z:
                    if(!control)return;
                    UndoMove();break;
                case Key.F2:
                    var one=FocusedOrOnly();if(one==null || !one.Linked)return;
                    RenameOriginal(one);break;
                case Key.Delete:
                    DeleteSelected();break;
                case Key.F1:
                    ShowGuide("browse");break;
                default:return;
            }
            e.Handled=true;
        }
        bool IsActiveWindowForKeys {get {return IsActive || Application.Current.Windows.OfType<Window>().All(w=>w==this || !w.IsVisible || w is UpscaleToast || w is VideoOverlay);}}
        Photo FocusedOrOnly() {
            if(selected.Count==1)return SelectedPhotos.FirstOrDefault();
            return focusIndex>=0 && focusIndex<photos.Count?photos[focusIndex]:null;
        }
        List<Photo> Targets(Photo clicked) {
            if(clicked!=null && !selected.Contains(clicked.Folder))return new List<Photo>{clicked};
            var list=SelectedPhotos.ToList();
            if(list.Count==0 && clicked!=null)list.Add(clicked);
            return list;
        }
        // In the folder grid Delete sends originals to the Recycle Bin; in Cover collection it sends cover files.
        void DeleteSelected() {
            var targets=Targets(FocusedOrOnly());
            if(targets.Count==0)return;
            bool covers=ShowingAllCrops;
            // An original with several cover tiles is one original.
            int items=covers?targets.Count:targets.Where(p=>p.Linked).GroupBy(p=>Path.GetDirectoryName(p.Folder)+"|"+p.Name,StringComparer.OrdinalIgnoreCase).Count();
            if(items>1 && MessageBox.Show(this,L.F(covers?"Move {0:N0} cover files to the Recycle Bin?":"Move {0:N0} originals to the Recycle Bin?",items),
                "Love My Shelf",MessageBoxButton.OKCancel,MessageBoxImage.Question)!=MessageBoxResult.OK)return;
            int first=targets.Min(p=>photos.IndexOf(p));
            if(covers)RecycleCovers(targets);
            else RecycleOriginals(targets);
            ClearSelection();
            // Like File Explorer: the tile that moved into the empty place is chosen.
            if(first>=0 && photos.Count>0)MoveFocus(Math.Min(first,photos.Count-1),false);
        }
        // The right-click menu for several selected tiles.
        internal ContextMenu MenuFor(Photo photo,ContextMenu single) {
            var targets=Targets(photo);
            if(targets.Count<2)return single;
            var menu=new ContextMenu();
            var copy=new MenuItem {Header=L.F("Copy {0:N0} items",targets.Count)};
            copy.Click+=delegate {ShareTiles.RunMany(this,targets,false);};
            var share=new MenuItem {Header=L.F("Share {0:N0} items…",targets.Count)};
            share.Click+=delegate {ShareTiles.RunMany(this,targets,true);};
            menu.Items.Add(copy);menu.Items.Add(share);
            var withCovers=targets.Where(p=>p.HasCrop && !p.ExcludedFromCollection).ToList();
            if(withCovers.Count>0) {
                var hide=new MenuItem {Header=L.F(withCovers.Any(p=>p.HasMotion)?"Hide {0:N0} covers (JPG and GIF) in Love My Shelf":"Hide {0:N0} covers in Love My Shelf",withCovers.Count)};
                hide.Click+=delegate {ChangeCollections(withCovers);ClearSelection();};
                menu.Items.Add(hide);
            }
            menu.Items.Add(new Separator());
            int originals=ShowingAllCrops?targets.Count:targets.GroupBy(p=>Path.GetDirectoryName(p.Folder)+"|"+p.Name,StringComparer.OrdinalIgnoreCase).Count();
            var delete=new MenuItem {Header=L.F(ShowingAllCrops?"Move {0:N0} cover files to the Recycle Bin":"Move {0:N0} originals to the Recycle Bin",originals),InputGestureText="Delete"};
            delete.Click+=delegate {DeleteSelected();};
            menu.Items.Add(delete);
            var clear=new MenuItem {Header=L.T("Clear selection"),InputGestureText="Esc"};
            clear.Click+=delegate {ClearSelection();};
            menu.Items.Add(clear);
            return menu;
        }
        // The guide, opened on the section for where you are.
        internal void ShowGuide(string section,Window owner=null) {GuideWindow.Open(owner??this,section);}
        internal void ShowInExplorer(Photo photo,bool cover) {
            try {
                string path=cover?photo.ActiveCoverFile:photo.Original;
                if(!cover && photo.Linked && !File.Exists(path))path=OriginalLocator.Resolve(store,photo,lifetime.Token);
                if(String.IsNullOrEmpty(path) || !File.Exists(path))throw new FileNotFoundException(L.T("The original file is unavailable."),path);
                System.Diagnostics.Process.Start("explorer.exe","/select,\""+path+"\"");
            } catch(Exception ex){Notify(L.T("Something went wrong"),ex.Message,null,NoticeKind.Warning);}
        }
        internal void ShowDetails(Photo photo) {
            new DetailsWindow(this,photo).ShowDialog();
        }
        // Renames the original and its covers; every tile of it keeps its slot with the new name.
        internal async void RenameOriginal(Photo photo) {
            if(!photo.Linked)return;
            var dialog=new RenameWindow(this,photo.Name);
            if(dialog.ShowDialog()!=true || dialog.NewName==photo.Name)return;
            string newName=dialog.NewName;
            string albumFolder=Path.GetDirectoryName(photo.Folder),oldBase=GalleryStore.RecordBase(photo);
            string newBase="p-"+GalleryStore.KeyOf(newName);
            try {
                string newPath=await Task.Run(()=>Renamer.Rename(store,photo,newName));
                if(lifetime.IsCancellationRequested)return;
                for(int i=0;i<photos.Count;i++) {
                    var p=photos[i];
                    if(p==null || !String.Equals(Path.GetDirectoryName(p.Folder),albumFolder,StringComparison.OrdinalIgnoreCase) ||
                        !String.Equals(GalleryStore.RecordBase(p),oldBase,StringComparison.OrdinalIgnoreCase))continue;
                    string folder=Path.Combine(albumFolder,newBase+Path.GetFileName(p.Folder).Substring(oldBase.Length));
                    bool wasSelected=selected.Remove(p.Folder);
                    var renamed=File.Exists(Path.Combine(folder,"photo.xml"))?GalleryStore.Read(folder,newPath):
                        new Photo {Folder=folder,Name=newName,Original=newPath,Linked=true};
                    if(wasSelected)selected.Add(folder);
                    UpdatePhoto(i,renamed);
                }
                Notify(L.T("Renamed"),L.F("“{0}” is now “{1}”.",photo.Name,newName),null);
            } catch(Exception ex) {
                Notify(L.T("Couldn’t rename"),ex.Message,null,NoticeKind.Warning);
            }
        }
        internal readonly UpscaleJobs Upscales=new UpscaleJobs();
        // Raised after a saved cover changes outside the editor, so an open viewer can follow.
        internal event Action<Photo> CoverReplaced;
        bool choosingUpscale;
        Window ActiveAppWindow() {
            var windows=Application.Current.Windows.OfType<Window>()
                .Where(w=>w.IsVisible && !(w is UpscaleToast) && !(w is VideoOverlay)).ToList();
            return windows.LastOrDefault(w=>w.IsEnabled && w.IsActive)??windows.LastOrDefault(w=>w.IsEnabled)??this;
        }
        internal UpscaleToast Notify(string title,string message,Action clicked,NoticeKind kind=NoticeKind.Done) {
            var toast=new UpscaleToast(ActiveAppWindow(),title,message,clicked==null?null:(Action)(()=>{
                // A notice can be clicked while another window (the cover editor, Make GIF cover, a question) is open.
                // Opening something on top of it would stack windows, so that window is brought forward instead.
                var busy=BlockingWindow();
                if(busy!=null){busy.Activate();Notify(L.T("Close this window first"),L.T("Finish or close the window that is open, then click the notice again."),null,NoticeKind.Info);return;}
                clicked();
            }),kind);
            toast.Show();
            return toast;
        }
        static Window BlockingWindow() {
            return Application.Current.Windows.OfType<Window>().FirstOrDefault(w=>w.IsVisible &&
                !(w is GalleryWindow) && !(w is UpscaleToast) && !(w is Viewer) && !(w is GuideWindow) && !(w is VideoOverlay));
        }
        void UpscaleFinished(Photo updated) {
            ReplaceShown(updated);
            Notify(L.T("Upscaling finished"),updated.Name+Environment.NewLine+L.T("Click to compare and choose."),
                ()=>ShowPendingChoice(updated,ActiveAppWindow()));
        }
        void ReplaceShown(Photo updated) {
            int index=photos.FindIndex(p=>p!=null && String.Equals(p.Folder,updated.Folder,StringComparison.OrdinalIgnoreCase));
            if(index>=0 && rows!=null)UpdatePhoto(index,updated);
            if(CoverReplaced!=null)CoverReplaced(updated);
        }
        // Shows the two versions side by side. Returns the photo as recorded after the choice.
        internal async Task<Photo> ShowUpscaleChoice(Window owner,Photo photo) {
            if(choosingUpscale)return photo;
            choosingUpscale=true;
            try {
                Photo current=await Task.Run(()=>GalleryStore.Read(photo.Folder,photo.Linked?photo.Original:null));
                if(lifetime.IsCancellationRequested)return photo;
                if(!current.HasUpscale)return current;
                var dialog=new UpscaleChoiceWindow(owner!=null && owner.IsVisible?owner:this,current);
                if(dialog.ShowDialog()!=true || dialog.ChoseUpscaled==null)return current;
                bool upscaled=dialog.ChoseUpscaled.Value;
                var chosen=await Task.Run(()=>GalleryStore.ChooseUpscale(current,upscaled));
                if(!lifetime.IsCancellationRequested)ReplaceShown(chosen);
                return chosen;
            } catch(Exception ex){if(!lifetime.IsCancellationRequested)UI.Error(owner??this,ex);return photo;}
            finally{choosingUpscale=false;}
        }
        async void ShowPendingChoice(Photo photo,Window owner=null) {await ShowUpscaleChoice(owner??this,photo);}
        internal void OpenPhoto(Photo photo,bool? cover) {
            // The first click after an upscale finishes shows the comparison instead.
            if(cover==null && photo.UpscalePending){ShowPendingChoice(photo);return;}
            var items=photos.Where(p=>p!=null).ToList();
            int index=items.IndexOf(photo);if(index<0)return;
            OpenViewer(items,index,cover);
        }
        // Opens a large view as a window of its own, like a video player opened from File Explorer: any number
        // can be open, and the folder window stays usable. A new one takes the place, size and full screen of
        // the large view opened last. Make cover from it closes it and opens the editor here.
        Viewer OpenViewer(IList<Photo> items,int index,bool? cover,Window placeLike=null) {
            var viewer=new Viewer(this,items,index,false,cover);
            var like=placeLike??LastViewer();
            if(like!=null)viewer.PlaceLike(like);
            viewer.EditLater=p=>Edit(p);
            viewer.Closed+=delegate {ShowJustSaved();};
            viewer.Show();
            return viewer;
        }
        Viewer LastViewer() {
            return Application.Current.Windows.OfType<Viewer>().LastOrDefault(v=>v.IsVisible && !v.ForCrop && v.WindowState!=WindowState.Minimized);
        }
        // The "GIF cover is ready" notice: shows that cover large, playing its GIF, in a new large view.
        // Skips it when the cover was removed or remade meanwhile.
        void OpenReadyCover(Photo ready) {
            try {
                if(lifetime.IsCancellationRequested || !IsVisible)return;
                var current=GalleryStore.Read(ready.Folder,ready.Linked?ready.Original:null);
                if(!current.HasMotion){Notify(L.T("The GIF cover is gone"),L.T("The GIF file was moved or deleted."),null,NoticeKind.Warning);return;}
                var items=photos.Where(p=>p!=null).ToList();
                int index=items.FindIndex(p=>String.Equals(p.Folder,current.Folder,StringComparison.OrdinalIgnoreCase));
                if(index<0){items=new List<Photo>{current};index=0;}
                else items[index]=current;
                OpenViewer(items,index,true);
            } catch(Exception ex){UI.Error(this,ex);}
        }
        // Opens the saved cover's large view on top of the still-open editor and waits for its first picture,
        // so when the editor closes the large view is already there and the folder never shows in between.
        // placeLike: the window whose place, size and full screen the large view takes (the video's large view).
        // closesLast: the last of the windows underneath to close; the large view gets the keyboard back after it.
        async Task OpenSavedOver(Window placeLike=null,Window closesLast=null) {
            var saved=justSaved;
            if(saved==null || lifetime.IsCancellationRequested || !IsVisible)return;
            try {
                justSaved=null;
                var items=photos.Where(p=>p!=null).ToList();
                int index=items.FindIndex(p=>String.Equals(p.Folder,saved.Folder,StringComparison.OrdinalIgnoreCase));
                if(index<0){items=new List<Photo>{saved};index=0;}
                else items[index]=saved;
                var viewer=new Viewer(this,items,index,false,true);
                if(placeLike!=null)viewer.PlaceLike(placeLike);
                viewer.EditLater=p=>Edit(p);
                viewer.Closed+=delegate {ShowJustSaved();};
                if(closesLast!=null)
                    // When the windows underneath close, Windows gives the focus to the folder window; hand it back.
                    closesLast.Closed+=delegate {
                        Dispatcher.BeginInvoke(new Action(()=>{if(viewer.IsVisible)viewer.Activate();}),DispatcherPriority.Background);
                    };
                viewer.Show();
                await Task.WhenAny(viewer.FirstPicture,Task.Delay(2000));
            } catch(Exception) {justSaved=saved;}
        }
        // Opens the large cover view of the cover saved last, among the grid's items when it is one of them.
        void ShowJustSaved() {
            var saved=justSaved;justSaved=null;
            if(saved==null || lifetime.IsCancellationRequested || !IsVisible)return;
            var items=photos.Where(p=>p!=null).ToList();
            int index=items.FindIndex(p=>String.Equals(p.Folder,saved.Folder,StringComparison.OrdinalIgnoreCase));
            if(index<0){items=new List<Photo>{saved};index=0;}
            else items[index]=saved;
            OpenViewer(items,index,true);
        }
        internal async void OpenInDefaultPlayer(Photo photo) {
            try {
                string path=photo.Original;
                if(photo.Linked && photo.HasCrop)
                    path=await Task.Run(()=>OriginalLocator.Resolve(store,photo,lifetime.Token));
                if(lifetime.IsCancellationRequested)return;
                if(!File.Exists(path))throw new FileNotFoundException(L.T("The original file is unavailable."),path);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) {UseShellExecute=true});
            } catch(OperationCanceledException){} catch(Exception ex){if(!lifetime.IsCancellationRequested)UI.Error(this,ex);}
        }
    }
}
