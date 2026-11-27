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
        // Slots whose tile is a wide cover taking this slot and the next one (the next slot is then empty).
        internal bool[] Wide;
        internal bool IsWide(int slot) {return Wide!=null && slot>=0 && slot<Wide.Length && Wide[slot];}
        // Set on the rows of folder tiles (full path, photos and videos inside); those rows have no Photos.
        internal List<Tuple<string,int>> Folders;
        public event Action<int> Changed;
        public void Replace(int index,Photo photo) {
            Photos[index]=photo;
            if(Changed!=null) Changed(index);
        }
    }
    // One row of the grid: equal cells side by side, where a wide cover takes two cells (see Covers).
    public class TileRow : Panel {
        CancellationTokenSource pending;
        PhotoRow displayed;
        static readonly ContextMenu MenuStandIn=new ContextMenu();
        public int Columns {get;set;}
        protected override Size MeasureOverride(Size available) {
            double width=Double.IsInfinity(available.Width)?0:available.Width;
            int columns=Math.Max(1,Columns);double cell=width/columns;
            double height=Double.IsInfinity(available.Height)?0:available.Height;
            for(int i=0;i<InternalChildren.Count;i++)
                InternalChildren[i].Measure(new Size(cell*(displayed!=null && displayed.IsWide(i)?2:1),height));
            return new Size(width,height);
        }
        protected override Size ArrangeOverride(Size final) {
            int columns=Math.Max(1,Columns);double cell=final.Width/columns;
            for(int i=0;i<InternalChildren.Count;i++) {
                // The slot after a wide tile is covered by it: its stand-in gets no room.
                bool covered=i>0 && displayed!=null && displayed.IsWide(i-1);
                int span=displayed!=null && displayed.IsWide(i)?2:1;
                InternalChildren[i].Arrange(covered?new Rect(i*cell,0,0,0):new Rect(i*cell,0,cell*span,final.Height));
            }
            return final;
        }
        public TileRow() {
            // Tiles are made as soon as a row gets its data (not only once it is shown), so a list that just changed
            // never paints a blank frame before its tiles appear; Loaded only refills a row that was emptied when it left the screen.
            DataContextChanged+=(s,e)=>{ Fill(e.NewValue as PhotoRow); };
            Loaded+=delegate { var row=DataContext as PhotoRow; if(row!=null && displayed!=row) Fill(row); };
            Unloaded+=delegate { Fill(null); };
        }
        // The file a tile shows: the cover, or the original when the cover is hidden or missing.
        internal static string ShownImage(Photo photo) {
            return photo.ShowsCover?photo.GridImage:photo.Original;
        }
        static async void LoadTile(Button tile,Image image,Photo photo,PhotoRow row,CancellationToken token,Action loaded=null) {
            try {
                // Read at the tile's own size on screen, so large tiles stay sharp and small ones stay light.
                var source=PresentationSource.FromVisual(row.Owner);
                double scale=source!=null && source.CompositionTarget!=null?source.CompositionTarget.TransformToDevice.M22:1;
                // The long side: the tile's height, or its width when it is wider (a wide cover across two cells).
                int span=row.IsWide(row.Photos.IndexOf(photo))?2:1;
                int side=Math.Max(64,(int)Math.Ceiling(Math.Max(row.Height-row.Owner.TileNameHeight,row.Owner.CellWidth*span)*scale));
                var owner=row.Owner;
                BitmapSource bitmap=null;
                // A file another program is writing, a drive still waking up or a network hiccup usually passes in a moment.
                for(int attempt=0;bitmap==null && attempt<3;attempt++) {
                    if(attempt>0)await Task.Delay(attempt==1?700:2000,token);
                    try {
                        var pending=TileImages.Load(photo,side,row.Number,()=>owner.CenterRow,token);
                        // Pictures already in memory appear in the same frame as the tile.
                        bitmap=pending.IsCompleted?pending.Result:await pending;
                    }
                    catch(OperationCanceledException) {throw;}
                    catch(Exception) {}
                }
                // Still no luck: the small copy saved with the cover is better than an empty tile.
                if(bitmap==null && photo.ShowsCover && File.Exists(photo.Thumb))
                    bitmap=await TileImages.LoadSavedCopy(photo,side,row.Number,()=>owner.CenterRow,token);
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
        // While every folder window is minimized the tiles let go of their pictures, and take them back afterwards.
        internal void Rest() {Fill(null);}
        internal void Wake() {var row=DataContext as PhotoRow;if(row!=null && displayed!=row)Fill(row);}
        void Fill(PhotoRow row) {
            if(displayed!=null) displayed.Changed-=UpdateCell;
            displayed=row;
            if(pending!=null) {pending.Cancel();pending.Dispose();pending=null;}
            Children.Clear(); if(row==null) return;
            pending=new CancellationTokenSource();
            Columns=row.Columns; Height=row.Height;
            if(row.Folders!=null) {
                // Each cell holds three folder cards, one above another; a last cell with fewer keeps them at the top.
                for(int at=0;at<row.Folders.Count;at+=GalleryWindow.FoldersPerCell) {
                    var cell=new Grid {Margin=new Thickness(3,3,3,3)};
                    for(int k=0;k<GalleryWindow.FoldersPerCell;k++)cell.RowDefinitions.Add(new RowDefinition());
                    for(int k=0;k<GalleryWindow.FoldersPerCell && at+k<row.Folders.Count;k++) {
                        var card=MakeFolderTile(row,row.Folders[at+k]);Grid.SetRow(card,k);cell.Children.Add(card);
                    }
                    Children.Add(cell);
                }
                return;
            }
            for(int column=0;column<row.Photos.Count;column++)
                Children.Add(column>0 && row.IsWide(column-1)?new Border {IsHitTestVisible=false,Visibility=Visibility.Collapsed}:MakeTile(row,row.Photos[column],column));
            InvalidateMeasure();
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
                if(row.Owner.TileNameHeight>0) {
                    tile.SetValue(TileMark.NameProperty,NoticeLook.KeepWords(photo.Name));
                    tile.SetValue(TileMark.NameHeightProperty,row.Owner.TileNameHeight-GalleryWindow.TileNamePadding);
                }
                UI.Use(tile,Control.BackgroundProperty,"TileBand");
                if(photo.SimilarLabel!=null)tile.SetValue(TileMark.GroupProperty,photo.SimilarLabel);
                if(photo.SimilarKeep)tile.SetValue(TileMark.KeepProperty,L.P("Suggested to keep","남기기 추천"));
                if(photo.SimilarInfo!=null){tile.SetValue(TileMark.InfoProperty,photo.SimilarInfo);tile.SetValue(TileMark.HasInfoProperty,true);}
                AutomationProperties.SetName(tile,photo.Name);
                row.Owner.TrackTile(tile,photo);
                tile.PreviewMouseLeftButtonDown+=(s,e)=>row.Owner.NoteClickCount(e.ClickCount);
                tile.Click+=delegate {row.Owner.TileClicked(photo);};
                // The menu is built the first time it opens, not for every tile that scrolls by.
                Func<ContextMenu> buildMenu=()=>{
                var menu=new ContextMenu();
                row.Owner.AddSimilarItems(menu,photo);
                if(photo.HasCrop) {
                    // With a GIF cover the names say JPG and GIF, so each choice is clear about what it touches.
                    bool both=photo.HasMotion;
                    var collection=new MenuItem { Header=photo.ExcludedFromCollection?L.T(both?"Show both the JPG and GIF covers again":"Show this cover again"):
                        L.T(both?"Hide both the JPG and GIF covers":"Hide this cover") };
                    collection.Click+=delegate { row.Owner.ChangeCollection(photo); };
                    menu.Items.Add(collection);
                    if(both) {
                        var recycleGif=new MenuItem { Header=L.T("Move only the GIF cover to the Recycle Bin") };
                        recycleGif.Click+=delegate { row.Owner.RecycleMotion(photo); };
                        menu.Items.Add(recycleGif);
                    }
                    var recycleCover=new MenuItem { Header=L.T(both?"Move both the JPG and GIF covers to the Recycle Bin":"Move this cover file to the Recycle Bin") };
                    recycleCover.Click+=delegate { row.Owner.RecycleCover(photo); };
                    menu.Items.Add(recycleCover);
                    if(!row.Owner.ShowingAllCrops) {
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
                    menu.Items.Add(new Separator());
                    foreach(bool cover in new[]{true,false}) {
                        var view=new MenuItem {Header=L.T(cover?"View cover large":"View original large")};
                        view.Click+=delegate {menu.IsOpen=false;row.Owner.OpenPhoto(photo,cover);};
                        menu.Items.Add(view);
                    }
                    menu.Items.Add(new Separator());
                }
                var edit=new MenuItem { Header=photo.HasCrop?L.T("Make one more cover"):L.T("Make cover") }; edit.Click+=delegate { row.Owner.Edit(photo); };
                menu.Items.Add(edit);
                // A GIF or WebP: a smaller copy beside it, under a size you pick.
                string kind=Path.GetExtension(photo.Original??"").ToLowerInvariant();
                if((kind==".gif" || kind==".webp") && AnimationFrames.Supports(photo.Original)) {
                    var shrink=new MenuItem {Header=L.P("Make this moving picture smaller…","움짤 용량 줄이기…")};
                    shrink.Click+=delegate {ShrinkWindow.Open(row.Owner,photo.Original);};
                    menu.Items.Add(shrink);
                }
                if(photo.Linked) {
                    var outside=new MenuItem {Header=L.T("Use a picture made elsewhere as a cover…")};
                    outside.Click+=delegate {row.Owner.AddOutsideCover(photo);};
                    menu.Items.Add(outside);
                    row.Owner.AddTagItems(menu,new List<Photo>{photo});
                }
                if(photo.HasCrop && !photo.ExcludedFromCollection) {
                    // Offered when this original has two or more shown covers; checked when the menu opens.
                    var order=new MenuItem {Header=L.T("Cover order"),Visibility=Visibility.Collapsed};
                    var forward=new MenuItem {Header=L.T("Move forward")};forward.Click+=delegate {row.Owner.MoveCover(photo,-1);};
                    var back=new MenuItem {Header=L.T("Move back")};back.Click+=delegate {row.Owner.MoveCover(photo,1);};
                    var front=new MenuItem {Header=L.T("Move to the very front (the first cover)")};front.Click+=delegate {row.Owner.MoveCover(photo,Int32.MinValue);};
                    var automatic=new MenuItem {Header=L.T("Back to the automatic order")};automatic.Click+=delegate {row.Owner.MoveCover(photo,0);};
                    order.Items.Add(forward);order.Items.Add(back);order.Items.Add(front);order.Items.Add(new Separator());order.Items.Add(automatic);
                    menu.Items.Add(order);
                    menu.Opened+=delegate {
                        int at=-1,total=0;bool arranged=false;
                        try {
                            var all=GalleryStore.CoversOf(photo);
                            arranged=all.Any(p=>p.HasCrop && !p.ExcludedFromCollection && p.Rank.HasValue);
                            var shown=GalleryStore.InCoverOrder(all.Where(p=>p.HasCrop && !p.ExcludedFromCollection));
                            total=shown.Count;
                            at=shown.FindIndex(p=>String.Equals(p.Folder,photo.Folder,StringComparison.OrdinalIgnoreCase));
                        } catch(Exception) {}
                        order.Visibility=total>1 && at>=0?Visibility.Visible:Visibility.Collapsed;
                        forward.IsEnabled=front.IsEnabled=at>0;back.IsEnabled=at>=0 && at<total-1;
                        automatic.IsEnabled=arranged;
                    };
                }
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
                if(photo.HasCrop && photo.HasMotion) {
                    // A cover with a moving cover too: one line for each file, so the GIF is found where it is.
                    var gifLocation=new MenuItem {Header=L.P("Open GIF cover location","GIF 표지 위치 열기")};
                    gifLocation.Click+=delegate {row.Owner.ShowInExplorer(photo,true,true);};
                    menu.Items.Add(gifLocation);
                    var jpgLocation=new MenuItem {Header=L.P("Open JPG cover location","JPG 표지 위치 열기")};
                    jpgLocation.Click+=delegate {row.Owner.ShowInExplorer(photo,true,false);};
                    menu.Items.Add(jpgLocation);
                } else if(photo.HasCrop) {
                    var coverLocation=new MenuItem {Header=L.T("Open cover file location")};
                    coverLocation.Click+=delegate {row.Owner.ShowInExplorer(photo,true);};
                    menu.Items.Add(coverLocation);
                }
                var info=new MenuItem {Header=L.T("Details")};
                info.Click+=delegate {row.Owner.ShowDetails(photo);};
                menu.Items.Add(info);
                ShareTiles.AddMenu(menu,row.Owner,photo);
                if(photo.HasCrop && !photo.IsOutside && MotionMaker.Supports(photo.Original)) {
                    var gif=new MenuItem {Header=L.T("Make GIF cover")};
                    gif.Click+=delegate {row.Owner.ShowMakeGif(row.Owner,photo);};
                    menu.Items.Add(gif);
                }
                if(!row.Owner.ShowingAllCrops) {
                    menu.Items.Add(new Separator());
                    var remove=new MenuItem { Header=L.T("Move the original file to the Recycle Bin"),InputGestureText="Delete" };
                    remove.Click+=delegate { row.Owner.RecycleOriginal(photo); };
                    menu.Items.Add(remove);
                }
                return menu;
                };
                // A shared empty stand-in lets Windows raise the opening event; it is swapped for the real menu there.
                tile.ContextMenu=MenuStandIn;
                ContextMenu single=null;
                // With several tiles selected, right-clicking one of them acts on all of them.
                // LoveMyShelf's items, a line, then File Explorer's own menu for the file (see ShellMenu).
                tile.ContextMenuOpening+=(s,e)=>{
                    if(single==null)single=buildMenu();
                    e.Handled=true;
                    row.Owner.ShowFileMenu(photo,row.Owner.MenuFor(photo,single),tile,e);
                };
                Action loaded=null;
                var mode=row.Owner.Settings.GridGif;
                if(photo.HasMotion && !photo.ShowOriginal && mode!=GifPlayback.Never)loaded=AttachMotion(tile,image,photo,mode,row.Height,pending.Token,row.Owner);
                ShareTiles.AttachDrag(tile,row.Owner,photo);
                var reflow=allowReflow?row.Owner.CurrentReflow:null;
                var kept=reflow!=null && !photo.HasMotion?reflow.ImageFor(photo):null;
                if(kept!=null){image.Source=kept;if(loaded!=null)loaded();}
                else LoadTile(tile,image,photo,row,pending.Token,loaded);
                if(reflow!=null)reflow.Animate(tile,photo,row,column);
                return tile;
        }
        // A folder inside the folder: a quiet card in the button colors with a line-drawn folder, its name and how many
        // photos and videos are right inside. Double-click (or one click, per Options) goes in; files dropped on it move in.
        static FrameworkElement MakeFolderTile(PhotoRow row,Tuple<string,int> folder) {
            string path=folder.Item1,name=System.IO.Path.GetFileName(path);
            var owner=row.Owner;
            // A card on its side: the folder drawing on the left, the name (two lines at most) and the count on the right.
            var icon=new System.Windows.Shapes.Path {Data=Geometry.Parse("M 1.5,4 L 1.5,17.5 L 22.5,17.5 L 22.5,6.5 L 11.5,6.5 L 9,4 Z"),Width=24,Height=20,
                StrokeThickness=1.4,StrokeLineJoin=PenLineJoin.Round,Stretch=Stretch.None,VerticalAlignment=VerticalAlignment.Center,
                Margin=new Thickness(10,0,8,0),SnapsToDevicePixels=true};
            icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,"Subtle");
            var title=new TextBlock {Text=name,FontSize=12.5,TextWrapping=TextWrapping.WrapWithOverflow,TextTrimming=TextTrimming.CharacterEllipsis,
                LineStackingStrategy=LineStackingStrategy.BlockLineHeight,LineHeight=16,MaxHeight=32}; // two lines at most, then "…"
            title.SetResourceReference(TextBlock.ForegroundProperty,"Text");
            var words=new StackPanel {VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,4,8,4)};
            words.Children.Add(title);
            if(folder.Item2>=0) {
                var number=new TextBlock {Text=L.F("{0:N0} items",folder.Item2),FontSize=11,Margin=new Thickness(0,2,0,0)};
                number.SetResourceReference(TextBlock.ForegroundProperty,"Subtle");
                words.Children.Add(number);
            }
            var stack=new DockPanel {LastChildFill=true,ClipToBounds=true};
            DockPanel.SetDock(icon,Dock.Left);stack.Children.Add(icon);stack.Children.Add(words);
            var tile=new Button {Content=stack,Margin=new Thickness(1,3,1,3),Padding=new Thickness(0),Tag=new FolderTarget(path),
                HorizontalContentAlignment=HorizontalAlignment.Stretch,VerticalContentAlignment=VerticalAlignment.Center,ToolTip=path};
            tile.Template=FolderTemplate();
            tile.SetResourceReference(Control.BackgroundProperty,"Panel");tile.SetResourceReference(Control.BorderBrushProperty,"Line");
            AutomationProperties.SetName(tile,name);
            tile.Click+=delegate {if(!owner.Settings.OpenByDoubleClick)owner.EnterFolder(path);};
            tile.PreviewMouseLeftButtonDown+=(s,e)=>{if(e.ClickCount==2 && owner.Settings.OpenByDoubleClick){e.Handled=true;owner.EnterFolder(path);}};
            var menu=new ContextMenu();
            var open=new MenuItem();open.Click+=delegate {owner.EnterFolder(path);};
            var openNew=new MenuItem();openNew.Click+=delegate {owner.OpenFolderInNewWindow(path);};
            var explorer=new MenuItem();explorer.Click+=delegate {
                try {System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) {UseShellExecute=true});} catch(Exception ex) {UI.Error(owner,ex);}
            };
            menu.Items.Add(open);menu.Items.Add(openNew);menu.Items.Add(new Separator());menu.Items.Add(explorer);
            menu.Opened+=delegate {open.Header=L.T("Open");openNew.Header=L.T("Open in new window");explorer.Header=L.T("Open in File Explorer");};
            tile.ContextMenu=menu;
            return tile;
        }
        static ControlTemplate folderTemplate;
        static ControlTemplate FolderTemplate() {
            if(folderTemplate!=null)return folderTemplate;
            var box=new FrameworkElementFactory(typeof(Border),"Box");
            box.SetValue(Border.CornerRadiusProperty,new CornerRadius(8));
            box.SetValue(Border.BorderThicknessProperty,new Thickness(1));
            box.SetValue(Border.BackgroundProperty,new TemplateBindingExtension(Control.BackgroundProperty));
            box.SetValue(Border.BorderBrushProperty,new TemplateBindingExtension(Control.BorderBrushProperty));
            box.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
            folderTemplate=new ControlTemplate(typeof(Button)) {VisualTree=box};
            var hover=new Trigger {Property=UIElement.IsMouseOverProperty,Value=true};
            hover.Setters.Add(new Setter(Border.BorderBrushProperty,new DynamicResourceExtension("Accent"),"Box"));
            folderTemplate.Triggers.Add(hover);
            var pressed=new Trigger {Property=ButtonBase.IsPressedProperty,Value=true};
            pressed.Setters.Add(new Setter(UIElement.OpacityProperty,0.8,"Box"));
            folderTemplate.Triggers.Add(pressed);
            return folderTemplate;
        }
        // Moves GIF covers in the grid: on hover, or always while the tile is on screen.
        // Returns what to run once the still has loaded.
        static Action AttachMotion(Button tile,Image image,Photo photo,GifPlayback mode,double height,CancellationToken token,GalleryWindow owner) {
            var player=new FramePlayer(image);
            bool hovering=false,ready=false,pressed=false,unplayable=false;
            int side=(int)Math.Max(160,Math.Min(720,height*1.5));
            Action start=async ()=>{
                if(player.Playing || !ready || TileSound.PlayingFor(tile))return;
                try {
                    var frames=await MotionFrames.LoadCoverAsync(photo,side,token);
                    if(token.IsCancellationRequested || !tile.IsLoaded || (mode==GifPlayback.Hover && !hovering))return;
                    // The video took over meanwhile: the pictures start when it stops (see below).
                    if(TileSound.PlayingFor(tile) || player.Playing)return;
                    player.Start(frames);
                } catch(Exception) {}
            };
            // An MP4 cover plays as a video while the mouse is on its tile: its sound (an option, on unless turned off)
            // stays with the picture, and it plays to the end however long it is. GIF and WebP covers, and MP4 covers in
            // tiles the mouse isn't on ([Always move]), are shown picture by picture as before.
            bool video=TileSound.IsVideo(photo),hasSound=TileSound.Has(photo);
            Action play=()=>{
                if(!video || unplayable || !ready || !hovering || pressed || TileSound.PlayingFor(tile) || !File.Exists(photo.MotionFile))return;
                // A video playing in a large view keeps the speakers to itself.
                bool silent=!hasSound || !owner.Settings.GifHoverSound ||
                    Application.Current.Windows.OfType<Viewer>().Any(v=>v.IsVisible && v.WindowState!=WindowState.Minimized);
                double top=0,bottom=1;
                CoverImages.VisibleRows(photo.Crop,photo.Rotation,photo.Aspect,out top,out bottom);
                ImageSource still=null;bool showing=false;
                TileSound.Play(tile,photo.MotionFile,owner.Settings.Volume,silent,top,bottom,
                    moving=>{
                        // The mouse left before the file opened: nothing to show, and the file is let go.
                        if(!hovering || !tile.IsLoaded){TileSound.Stop(tile);return;}
                        player.Hold=true; // the picture-by-picture player (if it runs) waits while the video shows
                        still=image.Source;showing=true;image.Source=moving;
                    },
                    ()=>{
                        if(showing)image.Source=still;
                        showing=false;player.Hold=false;
                        // [Always move]: the pictures go on (or begin) once the video is out of the way.
                        if(mode==GifPlayback.Always && tile.IsLoaded && !player.Playing)tile.Dispatcher.BeginInvoke(new Action(()=>start()));
                    },
                    ()=>{unplayable=true;if(hovering)start();},photo.Aspect); // Windows can't play it: picture by picture, without sound
            };
            if(video) {
                // A click may be about to rename, move or delete the file (also from the menu it opens): the video lets go
                // of the file and the tile stays still until the mouse comes back to it.
                tile.PreviewMouseDown+=delegate {pressed=true;TileSound.Stop(tile);};
                tile.MouseEnter+=delegate {pressed=false;hovering=true;play();};
                tile.MouseLeave+=delegate {hovering=false;TileSound.Stop(tile);};
                tile.Unloaded+=delegate {TileSound.Stop(tile);};
            }
            if(mode==GifPlayback.Hover) {
                tile.MouseEnter+=delegate {hovering=true;if(!video || unplayable)start();};
                tile.MouseLeave+=delegate {hovering=false;player.Stop();};
            }
            tile.Unloaded+=delegate {player.Stop();};
            return ()=>{
                ready=true;
                if(video && !unplayable && hovering)play();
                if(mode==GifPlayback.Always || (hovering && (!video || unplayable)))start();
            };
        }
        static ControlTemplate tileTemplate;
        // A tile: the picture and a selection outline drawn from the button's own border settings.
        static ControlTemplate TileTemplate() {
            if(tileTemplate!=null) return tileTemplate;
            var root=new FrameworkElementFactory(typeof(Grid));
            // The picture on top; the file name (when the option is on) in its own strip below, never over the picture.
            var pictureRow=new FrameworkElementFactory(typeof(RowDefinition));
            pictureRow.SetValue(RowDefinition.HeightProperty,new GridLength(1,GridUnitType.Star));
            root.AppendChild(pictureRow);
            var nameRow=new FrameworkElementFactory(typeof(RowDefinition));
            nameRow.SetValue(RowDefinition.HeightProperty,GridLength.Auto);
            root.AppendChild(nameRow);
            var border=new FrameworkElementFactory(typeof(Border));
            border.SetResourceReference(Border.BackgroundProperty,"TileBand");
            border.SetValue(Border.ClipToBoundsProperty,true);
            border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
            root.AppendChild(border);
            var fileName=new FrameworkElementFactory(typeof(TextBlock),"FileName");
            fileName.SetValue(Grid.RowProperty,1);
            fileName.SetValue(TextBlock.BackgroundProperty,Brushes.Transparent);
            fileName.SetValue(FrameworkElement.MarginProperty,new Thickness(3,2,3,3));
            fileName.SetValue(TextBlock.FontSizeProperty,GalleryWindow.TileNameFont);
            fileName.SetValue(TextBlock.LineHeightProperty,GalleryWindow.TileNameLine);
            fileName.SetValue(TextBlock.LineStackingStrategyProperty,LineStackingStrategy.BlockLineHeight);
            fileName.SetValue(TextBlock.TextAlignmentProperty,TextAlignment.Center);
            fileName.SetValue(TextBlock.TextWrappingProperty,TextWrapping.Wrap);
            fileName.SetValue(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis);
            fileName.SetResourceReference(TextBlock.ForegroundProperty,"Text");
            fileName.SetBinding(TextBlock.TextProperty,new System.Windows.Data.Binding {Path=new PropertyPath(TileMark.NameProperty),
                RelativeSource=System.Windows.Data.RelativeSource.TemplatedParent});
            foreach(var size in new[]{FrameworkElement.HeightProperty,FrameworkElement.MaxHeightProperty})
                fileName.SetBinding(size,new System.Windows.Data.Binding {Path=new PropertyPath(TileMark.NameHeightProperty),
                    RelativeSource=System.Windows.Data.RelativeSource.TemplatedParent});
            root.AppendChild(fileName);
            // "2/5": this is the second of the original's five covers. Dark behind white reads on any picture, in both themes.
            var number=new FrameworkElementFactory(typeof(Border),"CoverNumber");
            number.SetValue(UIElement.IsHitTestVisibleProperty,false);
            number.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Right);
            number.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Bottom);
            number.SetValue(FrameworkElement.MarginProperty,new Thickness(0,0,5,5));
            number.SetValue(Border.PaddingProperty,new Thickness(5,1,5,2));
            number.SetValue(Border.CornerRadiusProperty,new CornerRadius(6));
            number.SetValue(Border.BackgroundProperty,TileMark.NumberBack);
            var digits=new FrameworkElementFactory(typeof(TextBlock));
            digits.SetValue(TextBlock.FontSizeProperty,11.0);
            digits.SetValue(TextBlock.FontWeightProperty,FontWeights.SemiBold);
            digits.SetValue(TextBlock.ForegroundProperty,Brushes.White);
            digits.SetBinding(TextBlock.TextProperty,new System.Windows.Data.Binding {Path=new PropertyPath(TileMark.NumberProperty),
                RelativeSource=System.Windows.Data.RelativeSource.TemplatedParent});
            number.AppendChild(digits);
            root.AppendChild(number);
            // "12:34": where this cover's scene is in the video (an option, off unless chosen). Bottom left, away from the number.
            var time=new FrameworkElementFactory(typeof(Border),"CoverTime");
            time.SetValue(UIElement.IsHitTestVisibleProperty,false);
            time.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Left);
            time.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Bottom);
            time.SetValue(FrameworkElement.MarginProperty,new Thickness(5,0,0,5));
            time.SetValue(Border.PaddingProperty,new Thickness(5,1,5,2));
            time.SetValue(Border.CornerRadiusProperty,new CornerRadius(6));
            time.SetValue(Border.BackgroundProperty,TileMark.NumberBack);
            var clock=new FrameworkElementFactory(typeof(TextBlock));
            clock.SetValue(TextBlock.FontSizeProperty,11.0);
            clock.SetValue(TextBlock.FontWeightProperty,FontWeights.SemiBold);
            clock.SetValue(TextBlock.ForegroundProperty,Brushes.White);
            clock.SetBinding(TextBlock.TextProperty,new System.Windows.Data.Binding {Path=new PropertyPath(TileMark.TimeProperty),
                RelativeSource=System.Windows.Data.RelativeSource.TemplatedParent});
            time.AppendChild(clock);
            root.AppendChild(time);
            // "Similar photos": the group's label on its first tile, and "suggested to keep", top left.
            var marks=new FrameworkElementFactory(typeof(StackPanel),"SimilarMarks");
            marks.SetValue(UIElement.IsHitTestVisibleProperty,false);
            marks.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Left);
            marks.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Top);
            marks.SetValue(FrameworkElement.MarginProperty,new Thickness(5,5,5,0));
            foreach(var mark in new[]{Tuple.Create("SimilarGroup",TileMark.GroupProperty,true),Tuple.Create("SimilarKeep",TileMark.KeepProperty,false)}) {
                var box=new FrameworkElementFactory(typeof(Border),mark.Item1);
                box.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Left);
                box.SetValue(FrameworkElement.MarginProperty,new Thickness(0,0,0,3));
                box.SetValue(Border.PaddingProperty,new Thickness(6,1,6,2));
                box.SetValue(Border.CornerRadiusProperty,new CornerRadius(6));
                if(mark.Item3)box.SetResourceReference(Border.BackgroundProperty,"Accent");else box.SetValue(Border.BackgroundProperty,TileMark.NumberBack);
                var words=new FrameworkElementFactory(typeof(TextBlock));
                words.SetValue(TextBlock.FontSizeProperty,11.0);
                words.SetValue(TextBlock.FontWeightProperty,FontWeights.SemiBold);
                words.SetValue(TextBlock.TextWrappingProperty,TextWrapping.Wrap); // a narrow tile shows the label on two lines
                if(mark.Item3)words.SetResourceReference(TextBlock.ForegroundProperty,"Paper");else words.SetValue(TextBlock.ForegroundProperty,Brushes.White);
                words.SetBinding(TextBlock.TextProperty,new System.Windows.Data.Binding {Path=new PropertyPath(mark.Item2),
                    RelativeSource=System.Windows.Data.RelativeSource.TemplatedParent});
                box.AppendChild(words);
                marks.AppendChild(box);
            }
            root.AppendChild(marks);
            // "Similar photos": what is known about the file (size, type, date, how it differs), over the bottom of the picture.
            var info=new FrameworkElementFactory(typeof(Border),"SimilarInfo");
            info.SetValue(UIElement.IsHitTestVisibleProperty,false);
            info.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Bottom);
            info.SetValue(Border.PaddingProperty,new Thickness(5,2,5,3));
            info.SetValue(Border.BackgroundProperty,TileMark.InfoBack);
            var facts=new FrameworkElementFactory(typeof(TextBlock));
            facts.SetValue(TextBlock.FontSizeProperty,10.5);
            facts.SetValue(TextBlock.ForegroundProperty,Brushes.White);
            facts.SetValue(TextBlock.TextWrappingProperty,TextWrapping.Wrap);
            facts.SetValue(TextBlock.LineHeightProperty,14.0);
            facts.SetValue(TextBlock.LineStackingStrategyProperty,LineStackingStrategy.BlockLineHeight);
            facts.SetBinding(TextBlock.TextProperty,new System.Windows.Data.Binding {Path=new PropertyPath(TileMark.InfoProperty),
                RelativeSource=System.Windows.Data.RelativeSource.TemplatedParent});
            info.AppendChild(facts);
            root.AppendChild(info);
            // Every cover of the original under the mouse gets this outline.
            var family=new FrameworkElementFactory(typeof(Border),"CoverFamily");
            family.SetValue(UIElement.IsHitTestVisibleProperty,false);
            family.SetValue(UIElement.VisibilityProperty,Visibility.Collapsed);
            family.SetValue(Grid.RowSpanProperty,2);
            family.SetValue(Border.BorderThicknessProperty,new Thickness(2));
            family.SetResourceReference(Border.BorderBrushProperty,"Accent");
            root.AppendChild(family);
            var outline=new FrameworkElementFactory(typeof(Border));
            outline.SetValue(UIElement.IsHitTestVisibleProperty,false);
            outline.SetValue(Grid.RowSpanProperty,2);
            outline.SetValue(Border.BorderBrushProperty,new TemplateBindingExtension(Control.BorderBrushProperty));
            outline.SetValue(Border.BorderThicknessProperty,new TemplateBindingExtension(Control.BorderThicknessProperty));
            root.AppendChild(outline);
            tileTemplate=new ControlTemplate(typeof(Button)) { VisualTree=root };
            var noNumber=new Trigger {Property=TileMark.NumberProperty,Value=null};
            noNumber.Setters.Add(new Setter(UIElement.VisibilityProperty,Visibility.Collapsed,"CoverNumber"));
            tileTemplate.Triggers.Add(noNumber);
            var noTime=new Trigger {Property=TileMark.TimeProperty,Value=null};
            noTime.Setters.Add(new Setter(UIElement.VisibilityProperty,Visibility.Collapsed,"CoverTime"));
            tileTemplate.Triggers.Add(noTime);
            var noGroup=new Trigger {Property=TileMark.GroupProperty,Value=null};
            noGroup.Setters.Add(new Setter(UIElement.VisibilityProperty,Visibility.Collapsed,"SimilarGroup"));
            tileTemplate.Triggers.Add(noGroup);
            var noKeep=new Trigger {Property=TileMark.KeepProperty,Value=null};
            noKeep.Setters.Add(new Setter(UIElement.VisibilityProperty,Visibility.Collapsed,"SimilarKeep"));
            tileTemplate.Triggers.Add(noKeep);
            var noInfo=new Trigger {Property=TileMark.InfoProperty,Value=null};
            noInfo.Setters.Add(new Setter(UIElement.VisibilityProperty,Visibility.Collapsed,"SimilarInfo"));
            tileTemplate.Triggers.Add(noInfo);
            // The facts band takes the bottom of the tile: the cover number and time give way (the band says how many covers).
            var hasInfo=new Trigger {Property=TileMark.HasInfoProperty,Value=true};
            hasInfo.Setters.Add(new Setter(UIElement.VisibilityProperty,Visibility.Collapsed,"CoverNumber"));
            hasInfo.Setters.Add(new Setter(UIElement.VisibilityProperty,Visibility.Collapsed,"CoverTime"));
            tileTemplate.Triggers.Add(hasInfo);
            var noName=new Trigger {Property=TileMark.NameProperty,Value=null};
            noName.Setters.Add(new Setter(UIElement.VisibilityProperty,Visibility.Collapsed,"FileName"));
            tileTemplate.Triggers.Add(noName);
            var lit=new Trigger {Property=TileMark.LitProperty,Value=true};
            lit.Setters.Add(new Setter(UIElement.VisibilityProperty,Visibility.Visible,"CoverFamily"));
            tileTemplate.Triggers.Add(lit);
            return tileTemplate;
        }
    }
    // What a photo tile shows about the other covers of its original (drawn by the tile's template).
    internal static class TileMark {
        internal static readonly Brush NumberBack=Frozen(Color.FromArgb(0xCC,0,0,0));
        static Brush Frozen(Color color) {var brush=new SolidColorBrush(color);brush.Freeze();return brush;}
        internal static readonly DependencyProperty NumberProperty=DependencyProperty.RegisterAttached("Number",typeof(string),typeof(TileMark));
        internal static readonly DependencyProperty TimeProperty=DependencyProperty.RegisterAttached("Time",typeof(string),typeof(TileMark));
        internal static readonly DependencyProperty GroupProperty=DependencyProperty.RegisterAttached("Group",typeof(string),typeof(TileMark));
        internal static readonly DependencyProperty KeepProperty=DependencyProperty.RegisterAttached("Keep",typeof(string),typeof(TileMark));
        internal static readonly DependencyProperty InfoProperty=DependencyProperty.RegisterAttached("Info",typeof(string),typeof(TileMark));
        internal static readonly DependencyProperty HasInfoProperty=DependencyProperty.RegisterAttached("HasInfo",typeof(bool),typeof(TileMark));
        internal static readonly Brush InfoBack=Frozen(Color.FromArgb(0xB8,0,0,0));
        internal static readonly DependencyProperty LitProperty=DependencyProperty.RegisterAttached("Lit",typeof(bool),typeof(TileMark));
        internal static readonly DependencyProperty NameProperty=DependencyProperty.RegisterAttached("Name",typeof(string),typeof(TileMark));
        internal static readonly DependencyProperty NameHeightProperty=DependencyProperty.RegisterAttached("NameHeight",typeof(double),typeof(TileMark),new PropertyMetadata(0.0));
    }
    // Marks a folder tile, so a drop can tell it moves files into that folder.
    internal sealed class FolderTarget {
        internal readonly string Path;internal readonly bool Plain; // Plain: a folder in the folders area (no card look)
        internal FolderTarget(string path,bool plain=false){Path=path;Plain=plain;}
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
    public partial class GalleryWindow : Window {
        readonly GalleryStore store;
        readonly Action<string,IntPtr> moveToRecycleBin;
        // A second window opened beside the first one (to drag originals between two folders).
        readonly bool another;
        internal GalleryStore Store {get{return store;}}
        readonly CancellationTokenSource lifetime=new CancellationTokenSource();
        internal string PlaybackDirectory { get { return Path.Combine(store.Root,"Playback"); } }
        readonly ListBox grid=new ListBox();
        // The small writing box at the bottom of the folder tree (AiWorkPanel.cs).
        AiWorkPanel aiWork;
        readonly TextBlock count=UI.Text("",11,true), emptyTitle=UI.Text("",20), marqueeHint=UI.Text("",11.5,true);
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
        readonly ToggleButton selectionToggle=new ToggleButton();
        bool SelectingByClick {get {return selectionToggle.IsChecked==true;}}
        readonly Action updateTopBar;
        readonly Button folderList;
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
        // A tag's view: every original that carries the tag, from all folders (like the unfinished collection, they live in their own folders).
        internal const string TagKey="@tag:";
        bool ShowingTag {get {return active!=null && active.Source==null && active.Folder.StartsWith(TagKey,StringComparison.Ordinal);}}
        // One tag, or several shown together (their names joined by a tab, which a tag can't contain).
        string ShownTag {get {var tags=ShownTags;return tags.Count==1?tags[0]:null;}}
        List<string> ShownTags {get {return ShowingTag?SplitTags(active.Folder.Substring(TagKey.Length)):new List<string>();}}
        static Album TagAlbum(string tag) {return TagAlbum(SplitTags(tag));}
        const string ScopeKey="@scope:";
        bool ShowingScope {get {return active!=null && active.Source==null && active.Folder.StartsWith(ScopeKey,StringComparison.Ordinal);}}
        string ScopeFolder {get {return ShowingScope?active.Folder.Substring(ScopeKey.Length):null;}}
        static Album ScopeAlbum(string path) {return new Album(ScopeKey+path,FolderName(path));}
        Button tagsButton;
        internal Preferences Settings { get { return settings; } }
        internal void RememberShareCovers(ShareCoverKind kind) {
            if(settings.ShareCovers==kind)return;
            var updated=settings.Copy();updated.ShareCovers=kind;
            try {updated.Save(store.Root);} catch(Exception ex) {UI.Error(this,ex);return;}
            settings=updated;
            foreach(var window in Application.Current.Windows.OfType<GalleryWindow>())
                if(String.Equals(window.store.Root,store.Root,StringComparison.OrdinalIgnoreCase))window.settings.ShareCovers=kind;
        }
        // The large view's volume and Clear voice, kept for the next video (like PotPlayer). Every window learns
        // them, so another window's later save doesn't put the old values back.
        // The volume belongs to the speaker it was set on; an empty speaker is the Windows default.
        internal void SaveAudio(int value,bool clearVoice,int clearVoiceLevel,string speaker) {
            value=Math.Max(0,Math.Min(100,value));
            var windows=Application.Current.Windows.OfType<GalleryWindow>().ToList();
            if(settings.VolumeFor(speaker)==value && settings.ClearVoice==clearVoice && settings.ClearVoiceLevel==clearVoiceLevel)return;
            foreach(var w in windows) {
                if(String.IsNullOrEmpty(speaker))w.settings.Volume=value;else w.settings.SpeakerVolumes[speaker]=value;
                w.settings.ClearVoice=clearVoice;w.settings.ClearVoiceLevel=clearVoiceLevel;
            }
            var updated=settings.Copy();
            try {updated.Save(store.Root);} catch(Exception) {}
        }
        // The subtitle size chosen in a large view, for every window and the next video.
        internal void SaveSubtitleSize(int percent) {
            if(settings.SubtitleScale==percent)return;
            foreach(var w in Application.Current.Windows.OfType<GalleryWindow>())w.settings.SubtitleScale=percent;
            try {settings.Copy().Save(store.Root);} catch(Exception) {}
        }

        // ----- Memory given back while the folder windows are minimized -----
        // Watching a video with the folder window down is like File Explorer closed and a video player open: the
        // tiles' pictures aren't needed, so they are let go a few seconds after the last folder window goes down and
        // read again when one comes back. Nothing is let go while a folder window is on screen, and nothing is said about it.
        static DispatcherTimer restTimer; // made by the first folder window, on its own thread
        static bool resting,minimizeTipShown;
        static DispatcherTimer MakeRestTimer() {
            var timer=new DispatcherTimer {Interval=TimeSpan.FromSeconds(5)};
            timer.Tick+=delegate {timer.Stop();Rest();};
            return timer;
        }
        void WatchRest() {
            if(restTimer==null)restTimer=MakeRestTimer();
            StateChanged+=delegate {
                restTimer.Stop();
                if(WindowState==WindowState.Minimized)restTimer.Start();else WakeUp();
            };
        }
        static void Rest() {
            if(resting || Application.Current==null)return;
            var windows=Application.Current.Windows.OfType<GalleryWindow>().Where(w=>w.IsVisible).ToList();
            if(windows.Count==0 || windows.Any(w=>w.WindowState!=WindowState.Minimized))return;
            // A window working on a cover (the editor, Make GIF cover and so on) still uses the pictures.
            if(BlockingWindow()!=null)return;
            foreach(var window in windows) {
                window.CurrentReflow=null;
                EachTileRow(window.grid,row=>row.Rest());
            }
            TileCache.Clear();MotionFrames.ClearCache();
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode=System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
            resting=true;
        }
        // Quietly: the tiles on screen just take their pictures back.
        static void WakeUp() {
            if(!resting)return;
            resting=false;
            foreach(var window in Application.Current.Windows.OfType<GalleryWindow>())EachTileRow(window.grid,row=>row.Wake());
        }
        static void EachTileRow(DependencyObject parent,Action<TileRow> act) {
            for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++) {
                var child=VisualTreeHelper.GetChild(parent,i);
                var row=child as TileRow;
                if(row!=null)act(row);else EachTileRow(child,act);
            }
        }
        internal static void MinimizeAll() {
            foreach(var window in Application.Current.Windows.OfType<GalleryWindow>().Where(w=>w.IsVisible).ToList())window.WindowState=WindowState.Minimized;
        }
        // Said once, to someone who used LoveMyShelf before the GIF cover defaults changed (Preferences.MoveToNewGifDefaults).
        // The options are saved right away, so the file carries the new mark and this is not said again.
        void TellGifDefaultsChanged() {
            if(!settings.GifDefaultsChanged)return;
            foreach(var window in Application.Current.Windows.OfType<GalleryWindow>())window.settings.GifDefaultsChanged=false;
            try {settings.Copy().Save(store.Root);} catch(Exception) {return;}
            Dispatcher.BeginInvoke(new Action(()=>Notify(L.T("The GIF cover defaults have changed"),
                L.P("GIF covers are now made as WebP, at high picture quality, 30 pictures a second and 1280 pixels tall. They are much smoother and sharper than before, so try making one. If you don't like it, you can change it any time, and you can type in the numbers you want. [See the settings] opens them.","이제 GIF 표지를 WebP · 화질 높게 · 1초에 30장 · 세로 1280픽셀로 만들어요. 전보다 훨씬 부드럽고 선명하니 한 번 만들어 보세요. 마음에 안 들면 언제든 바꿀 수 있고, 원하는 숫자를 직접 넣을 수도 있어요. [설정 보기]를 누르면 그 설정이 열려요."),
                ()=>GuideWindow.Open(this,"gif","gifFormat"),NoticeKind.Info,true,15,L.P("See the settings","설정 보기"))),DispatcherPriority.ApplicationIdle);
        }
        // Said once each time LoveMyShelf runs, when a video opens while a folder window is still up.
        internal void TipMinimize() {
            if(minimizeTipShown || !IsVisible || WindowState==WindowState.Minimized)return;
            minimizeTipShown=true;
            Notify(L.T("Watching only the video?"),L.P("Minimize the folder window and LoveMyShelf uses less memory.","폴더 창을 최소화해 두면 LoveMyShelf가 메모리를 덜 써요."),
                MinimizeAll,NoticeKind.Info,false,8,L.P("Minimize it now","지금 최소화하기"));
        }
        // Where the subtitle explorer was left, for every window.
        internal void SaveSubtitleBounds(string bounds) {
            if(settings.SubtitleBounds==bounds)return;
            foreach(var w in Application.Current.Windows.OfType<GalleryWindow>())w.settings.SubtitleBounds=bounds;
            try {settings.Copy().Save(store.Root);} catch(Exception) {}
        }
        // Where the large view closed last was, for every window.
        internal void SaveViewerBounds(string bounds) {
            if(settings.ViewerBounds==bounds)return;
            foreach(var w in Application.Current.Windows.OfType<GalleryWindow>())w.settings.ViewerBounds=bounds;
            try {settings.Copy().Save(store.Root);} catch(Exception) {}
        }
        internal Album AllCropsAlbum {get {return allCrops;}}
        internal Album UnfinishedAlbum {get {return unfinished;}}
        int folderFilter;
        bool ShowingPending { get { return ShowingUnfinished || (active!=null && (active.Source!=null || ShowingScope) && folderFilter==1); } }
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
                L.T("Choose the folder where cover files will be saved."),Preferences.SuggestedCoverParent,true));
            settings=Preferences.Load(store.Root);L.Korean=settings.Korean;MotionWindow.UseDefaults(settings);ViewerKeys.Use(settings);
            if(!another)Task.Run(()=>MotionMaker.SweepPreviews()); // GIF preview files left behind by a sudden exit
            allCrops=new Album(CollectionKey,L.T("Cover collection"));
            unfinished=new Album(UnfinishedKey,L.T("Unfinished cover collection"));
            this.moveToRecycleBin=moveToRecycleBin;
            UI.WindowSetup(this,"LoveMyShelf");
            Width=Math.Min(1120,SystemParameters.WorkArea.Width);Height=Math.Min(800,SystemParameters.WorkArea.Height); MinWidth=440; MinHeight=340;
            var root=new DockPanel(); Content=root;
            var nav=UI.Bar(new Thickness(5,2,5,2));
            // The two collections sit in the middle of the bottom bar, so the folder tabs get the whole top bar
            // (two windows side by side on one monitor is the usual way to sort files).
            allCropsButton=UI.Btn(allCrops.Name,()=>Select(allCrops));
            unfinishedButton=UI.Btn(unfinished.Name,()=>Select(unfinished));
            unfinishedButton.Margin=new Thickness(4,0,0,0);
            var collections=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
            selectionToggle.Style=(Style)Application.Current.Resources["SelectionToggle"];
            selectionToggle.MinHeight=27;
            selectionToggle.Margin=new Thickness(0,0,4,0);
            var selectionCount=UI.Text("",12,true);
            selectionCount.Margin=new Thickness(2,0,4,0);
            collections.Children.Add(selectionToggle);collections.Children.Add(selectionCount);
            collections.Children.Add(allCropsButton);collections.Children.Add(unfinishedButton);
            tagsButton=UI.Btn(L.T("Tags"),()=>OpenTagMenu());tagsButton.Margin=new Thickness(4,0,0,0);
            collections.Children.Add(tagsButton);
            // A tag's view and a search (which also finds tags) show what the tags say right now.
            Action tagsChanged=()=>Dispatcher.BeginInvoke(new Action(()=>{if(ShowingTag || search.Text.Trim().Length>0)Refresh();}));
            Tags.Changed+=tagsChanged;Closed+=delegate {Tags.Changed-=tagsChanged;};
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
            var guide=UI.Btn("",()=>ShowGuide("start"));guide.Margin=new Thickness(4,0,0,0);UI.Right(nav,guide);
            // Folders are added from the folder tree now (right-click → Add to LoveMyShelf, like pinning in File Explorer);
            // the old Add folder button stays only as Ctrl+O and the link on the empty screen.
            var open=UI.Btn("",PickFolder,true);open.Visibility=Visibility.Collapsed;UI.Right(nav,open);
            var newWindow=UI.Btn("",OpenAnotherWindow);newWindow.Margin=new Thickness(0,0,6,0);UI.Right(nav,newWindow);
            var searchHost=SearchBox();searchHost.Margin=new Thickness(6,0,6,0);UI.Right(nav,searchHost);
            BuildFormatButton();UI.Right(nav,FormatButton);
            // Every folder in one list, like a browser's tab list; it shows only when some tabs don't fit.
            folderList=UI.Btn("▾",()=>OpenFolderList());folderList.Padding=new Thickness(10,4,10,4);folderList.Margin=new Thickness(5,0,0,0);
            folderList.Visibility=Visibility.Collapsed;UI.Right(nav,folderList);
            var scroller=new ScrollViewer { Content=tabs,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled,HorizontalScrollBarVisibility=ScrollBarVisibility.Hidden };
            // No scroll bar, like a browser's tab strip: the mouse wheel, dragging, the ▾ list and the tab of the folder
            // on screen scrolling into view do the job. A soft fade at an edge says more tabs are hidden there.
            scroller.PreviewMouseWheel+=(s,e)=>{
                if(scroller.ScrollableWidth<=0)return;
                scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset-e.Delta*0.6);e.Handled=true;
            };
            // A sliver of a tab (when the search box is open in a very narrow window) looks broken, so it hides instead.
            // When the window gets narrower, the tab of the folder on screen stays in view.
            scroller.SizeChanged+=delegate {
                scroller.Visibility=scroller.ActualWidth<60?Visibility.Hidden:Visibility.Visible;
                var current=tabs.Children.OfType<Button>().FirstOrDefault(b=>trail.Count>0 && b.Tag is Album && ((Album)b.Tag).Folder==trail[0].Folder);
                if(current!=null)Dispatcher.BeginInvoke(new Action(()=>current.BringIntoView()),DispatcherPriority.Loaded);
            };
            // The list button appears when the tabs overflow and leaves once they all fit again (counting its own room).
            scroller.ScrollChanged+=(s,e)=>{
                double viewport=scroller.ViewportWidth,offset=scroller.HorizontalOffset,end=scroller.ScrollableWidth;
                if(viewport<=0 || end<=0.5)scroller.OpacityMask=null;
                else {
                    double f=Math.Min(0.3,28/viewport);
                    var mask=new LinearGradientBrush {StartPoint=new Point(0,0.5),EndPoint=new Point(1,0.5)};
                    mask.GradientStops.Add(new GradientStop(offset>0.5?Colors.Transparent:Colors.Black,0));
                    mask.GradientStops.Add(new GradientStop(Colors.Black,offset>0.5?f:0));
                    mask.GradientStops.Add(new GradientStop(Colors.Black,offset<end-0.5?1-f:1));
                    mask.GradientStops.Add(new GradientStop(offset<end-0.5?Colors.Transparent:Colors.Black,1));
                    scroller.OpacityMask=mask;
                }
                if(e.ExtentWidthChange==0 && e.ViewportWidthChange==0)return;
                bool shown=folderList.Visibility==Visibility.Visible;
                double room=scroller.ViewportWidth+(shown?folderList.ActualWidth+folderList.Margin.Left:0);
                folderList.Visibility=scroller.ExtentWidth>room+0.5?Visibility.Visible:Visibility.Collapsed;
                // The first time ever that tabs are hidden, say once how to reach them (the wheel can't be seen).
                if(folderList.Visibility==Visibility.Visible && !settings.TabScrollTipShown && IsLoaded) {
                    var updated=settings.Copy();updated.TabScrollTipShown=true;
                    try {updated.Save(store.Root);settings=updated;} catch(Exception) {return;}
                    Dispatcher.BeginInvoke(new Action(()=>Notify(L.T("Not all folder tabs are showing"),
                        L.T("Roll the mouse wheel over the tabs and they move sideways. Click ▾ to see every folder at once."),
                        ()=>OpenFolderList(),NoticeKind.Info,false,15,L.P("See every folder","폴더 모두 보기"))),DispatcherPriority.ApplicationIdle);
                }
            };
            // The faded edge is a button too: a click there slides the tabs that way.
            scroller.PreviewMouseLeftButtonDown+=(s,e)=>{
                double x=e.GetPosition(scroller).X,viewport=scroller.ViewportWidth,edge=Math.Min(0.3*viewport,28);
                if(scroller.ScrollableWidth<=0.5)return;
                if(x>viewport-edge && scroller.HorizontalOffset<scroller.ScrollableWidth-0.5){SlideTabs(scroller,viewport*0.6);e.Handled=true;}
                else if(x<edge && scroller.HorizontalOffset>0.5){SlideTabs(scroller,-viewport*0.6);e.Handled=true;}
            };
            // The folder tree button sits first, where File Explorer's navigation pane begins.
            treeButton=UI.Btn("",ToggleTree);treeButton.Content=LineIcon("M 2.5,3.5 L 15.5,3.5 L 15.5,14.5 L 2.5,14.5 Z M 7,3.5 L 7,14.5");
            treeButton.Padding=new Thickness(7,4,7,4);treeButton.VerticalAlignment=VerticalAlignment.Center;treeButton.Margin=new Thickness(0,0,6,0);
            DockPanel.SetDock(treeButton,Dock.Left);nav.Children.Add(treeButton);
            // Like File Explorer's top row: ← → ↑ ⟳ and the address bar. The folder tabs are gone (the tree's
            // LoveMyShelf group does their job); the tab strip is kept only out of sight.
            folderList.Visibility=Visibility.Collapsed;
            foreach(var made in new[]{
                Tuple.Create("M 15,9 L 3.5,9 M 8,4 L 3,9 L 8,14","Back","Alt+←",(Action)(()=>GoBack())),
                Tuple.Create("M 3,9 L 14.5,9 M 10,4 L 15,9 L 10,14","Forward","Alt+→",(Action)(()=>GoForward())),
                Tuple.Create("M 9,15 L 9,3.5 M 4,8 L 9,3 L 14,8","Up to the folder above","Alt+↑",(Action)(()=>GoUpFolder())),
                Tuple.Create("M 14.5,9 A 5.5,5.5 0 1 1 12.8,5 M 13,1.8 L 13,5.3 L 9.5,5.3","Refresh","F5",(Action)(()=>{Refresh();StartCoverMigration();DrivesChangedSoon(0.3);}))}) {
                var action=made.Item4;var button=UI.Btn("",()=>action());button.Content=LineIcon(made.Item1);
                button.Padding=new Thickness(7,4,7,4);button.VerticalAlignment=VerticalAlignment.Center;button.Margin=new Thickness(0,0,4,0);
                button.BorderThickness=new Thickness(0);button.Background=Brushes.Transparent;
                button.Tag=made.Item2+"|"+made.Item3;
                DockPanel.SetDock(button,Dock.Left);nav.Children.Add(button);navButtons.Add(button);
            }
            address=new AddressBar(this);address.Margin=new Thickness(4,0,0,0);
            nav.Children.Add(address);DockPanel.SetDock(nav,Dock.Top);root.Children.Add(nav);
            // Narrow window: New window, Add folder and Guide & Options become small pictures (their names on hover),
            // so the folder tabs keep at least about 400 px or all of their own width.
            var newWindowIcon=LineIcon("M 6,2.5 L 15.5,2.5 L 15.5,11 L 6,11 Z M 2.5,6 L 2.5,15.5 L 11.5,15.5 L 11.5,13");
            var addIcon=LineIcon("M 9,3 L 9,15 M 3,9 L 15,9");
            var guideIcon=new TextBlock {Text="?",FontSize=14,FontWeight=FontWeights.SemiBold,IsHitTestVisible=false,
                Width=18,TextAlignment=TextAlignment.Center,LineHeight=18,LineStackingStrategy=LineStackingStrategy.BlockLineHeight};
            // The top bar buttons keep the height of a folder tab instead of stretching with the bar,
            // and the picture versions are small squares.
            foreach(var button in new[]{guide,open,newWindow,folderList,searchButton})button.VerticalAlignment=VerticalAlignment.Center;
            searchHost.VerticalAlignment=VerticalAlignment.Center;
            Action<Button,bool> square=(button,on)=>{if(on)button.Padding=new Thickness(7,4,7,4);else button.ClearValue(Control.PaddingProperty);};
            updateTopBar=delegate {
                double width=nav.ActualWidth;if(width<=0)return;
                var infinite=new Size(double.PositiveInfinity,double.PositiveInfinity);
                newWindow.Content=L.T("New window");open.Content=L.T("Add folder");guide.Content=L.T("Guide & Options");
                foreach(var button in new[]{newWindow,open,guide})square(button,false);
                UpdateFormatButton(0);
                Func<double> rightWidth=delegate {
                    double right=0;
                    foreach(var part in new FrameworkElement[]{newWindow,open,guide,searchHost,FormatButton,folderList}) {
                        if(part.Visibility==Visibility.Collapsed)continue;
                        InvalidateTree(part);part.Measure(infinite);right+=part.DesiredSize.Width;
                    }
                    return right;
                };
                if(width-rightWidth()>=520)return; // the address bar keeps about 360 px
                newWindow.Content=newWindowIcon;open.Content=addIcon;guide.Content=guideIcon;
                foreach(var button in new[]{newWindow,open,guide})square(button,true);
                if(FormatButton.Visibility==Visibility.Collapsed || width-rightWidth()>=520)return;
                UpdateFormatButton(1);
                if(width-rightWidth()<520)UpdateFormatButton(2);
            };
            nav.SizeChanged+=delegate {updateTopBar();};
            var line=UI.Use(new Border { Height=1 },Border.BackgroundProperty,"Line"); DockPanel.SetDock(line,Dock.Top);root.Children.Add(line);
            // Inside a folder opened from a folder tile: [↑] and the way down from the tab, each part clickable.
            pathRow.Margin=new Thickness(5,3,5,3);
            DockPanel.SetDock(pathRow,Dock.Top);root.Children.Add(pathRow);
            pathRow.SizeChanged+=(s,e)=>{if(e.WidthChanged)LayoutPath();};
            var modeFile=Path.Combine(store.Root,"windowed");
            // Explorer keys run before the fullscreen keys so Esc first clears a selection.
            PreviewKeyDown+=ExplorerKeys;
            // A tile's sound never outlives the moment: any key, leaving the window or closing it stops it.
            // (Also for keys another handler already used up, such as Ctrl+Z or Delete: those are the ones that touch files.)
            AddHandler(Keyboard.PreviewKeyDownEvent,new KeyEventHandler((s,e)=>TileSound.Stop()),true);
            Deactivated+=delegate {TileSound.Stop();};Closed+=delegate {TileSound.Stop();};
            var titleBar=FullScreen.TitleBar(this,!another && !File.Exists(modeFile),full=>{
                if(another)return;
                try { if(full) File.Delete(modeFile); else File.WriteAllText(modeFile,""); }
                catch(IOException) { } catch(UnauthorizedAccessException) { }
            });
            root.Children.Insert(0,titleBar);
            Button sizing=null;sizing=UI.Btn("",()=>ToggleSizeList(sizing)); // opens the sizes to choose from (GalleryTileSize.cs)
            // Sort works like File Explorer: one button, the choices open upward, Shuffle is the last one.
            sortButton=UI.Btn("",OpenSortMenu);
            var openCoverFolder=UI.Btn("",OpenCoverFolder);
            queueButton=UI.Btn("",()=>TransferWindow.ShowQueue(true));queueButton.Visibility=Visibility.Collapsed;
            AutomationProperties.SetName(queueButton,L.P("File queue","파일 대기열"));
            foreach(var button in new[]{queueButton,openCoverFolder,sortButton,sizing}) {
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
            count.TextTrimming=TextTrimming.CharacterEllipsis;
            footerStatus.Children.Add(count);
            var footer=new Grid {Margin=new Thickness(5,1,5,2)};
            footer.ColumnDefinitions.Add(new ColumnDefinition());
            footer.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
            footer.ColumnDefinitions.Add(new ColumnDefinition());
            footer.Children.Add(footerStatus);
            collections.Margin=new Thickness(8,0,8,0);
            Grid.SetColumn(collections,1);footer.Children.Add(collections);
            Grid.SetColumn(footerControls,2);footer.Children.Add(footerControls);
            DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
            var coverFolderIcon=LineIcon("M 1.5,3.5 L 1.5,13.5 L 16.5,13.5 L 16.5,5.5 L 8.5,5.5 L 6.8,3.5 Z");
            // Labels shrink step by step only as far as the window needs, so nothing in the bottom bar overlaps:
            // 1 Sort and Cover size become symbols, 2 the collections and filters drop the word "cover", 3 Open cover folder becomes an icon.
            updateFooter=delegate {
                selectionCount.Text=SelectingByClick?L.PF("{0:N0} selected","{0:N0}개 골랐어요",selected.Count):"";
                selectionCount.Visibility=SelectingByClick?Visibility.Visible:Visibility.Collapsed;
                double width=footer.ActualWidth>0?footer.ActualWidth:(ActualWidth>0?ActualWidth:Width)-10;
                string sortLabel=shuffledOrder!=null?L.T("Sort: shuffled"):L.F("Sort: {0}",SortName(settings.Sort));
                var infinite=new Size(double.PositiveInfinity,double.PositiveInfinity);
                double filterWidth=0,side=0;bool fits=false;
                for(int level=0;level<=3 && !fits;level++) {
                    sortButton.Content=(level>=1?"⇅":sortLabel)+"  ▴";
                    FooterLabel(sizing,"Cover size","▦",level>=1);
                    allCropsButton.Content=L.T(level>=2?"O set":"Cover collection");
                    unfinishedButton.Content=L.T(level>=2?"X set":"Unfinished cover collection");
                    tagsButton.Content=level>=2?"#":L.T("Tags");
                    filterButtons[1].Content=level>=2?"X":L.T("Unfinished");
                    filterButtons[2].Content=level>=2?"O":L.T("Finished");
                    filterButtons[1].MinWidth=filterButtons[2].MinWidth=level>=2?38:58;
                    if(level>=3)openCoverFolder.Content=coverFolderIcon;else openCoverFolder.Content=L.T("Open cover folder");
                    filterWidth=filter.Visibility==Visibility.Visible?RowWidth(filter):0;
                    side=Math.Max(filterWidth+(level<2?70:0),RowWidth(footerControls)); // room for a short count while labels are long
                    fits=RowWidth(collections)+2*side<=width;
                }
                // Narrower than even the shortest labels allow: both sides take what they need and the collections
                // stay centered in the space between them instead of sliding under the filters.
                var star=new GridLength(1,GridUnitType.Star);
                footer.ColumnDefinitions[0].Width=fits?star:GridLength.Auto;
                footer.ColumnDefinitions[1].Width=fits?GridLength.Auto:star;
                footer.ColumnDefinitions[2].Width=fits?star:GridLength.Auto;
                // The count shows only when it fits whole (a long message may still trim); a lone "…" says nothing.
                count.Visibility=Visibility.Visible;count.InvalidateMeasure();count.Measure(infinite);
                double room=(fits?(width-RowWidth(collections))/2:0)-filterWidth;
                count.Visibility=room>=count.DesiredSize.Width || room>=160?Visibility.Visible:Visibility.Collapsed;
                ShowRangeHint();
                sortButton.ToolTip=L.T("Sort");AutomationProperties.SetName(sortButton,L.T("Sort"));
                allCropsButton.ToolTip=L.T("Cover collection");AutomationProperties.SetName(allCropsButton,L.T("Cover collection"));
                unfinishedButton.ToolTip=L.T("Unfinished cover collection");AutomationProperties.SetName(unfinishedButton,L.T("Unfinished cover collection"));
                openCoverFolder.ToolTip=L.T("Open cover folder");AutomationProperties.SetName(openCoverFolder,L.T("Open cover folder"));
                tagsButton.ToolTip=ShowingTag?"#"+String.Join(" · #",ShownTags):L.T("Tags");AutomationProperties.SetName(tagsButton,L.T("Tags"));
            };
            selectionToggle.Checked+=delegate {RefreshSelection();};
            selectionToggle.Unchecked+=delegate {RefreshSelection();};
            footer.SizeChanged+=delegate {updateFooter();};
            // The folder tree on the left, with an edge that can be dragged to make it wider or narrower.
            tree=new FolderTree(this);
            var treeEdge=new System.Windows.Controls.Primitives.Thumb {Width=5,Cursor=Cursors.SizeWE,Opacity=0};
            var edgeLook=new FrameworkElementFactory(typeof(Border));edgeLook.SetValue(Border.BackgroundProperty,Brushes.Transparent);
            treeEdge.Template=new ControlTemplate(typeof(System.Windows.Controls.Primitives.Thumb)) {VisualTree=edgeLook};
            treeEdge.DragDelta+=(s,e)=>{treePanel.Width=Math.Max(160,Math.Min(400,treePanel.Width+e.HorizontalChange));};
            treeEdge.DragCompleted+=delegate {SaveTree(null,(int)Math.Round(treePanel.Width),null);};
            var treeLine=UI.Use(new Border {Width=1,HorizontalAlignment=HorizontalAlignment.Right},Border.BackgroundProperty,"Line");
            // Right under the last folder, the writing box, when the whole tree fits with room for it (TreeWithBox).
            aiWork=new AiWorkPanel(this);aiWork.Margin=new Thickness(0,0,1,0);
            var treeGrid=new Grid();treeGrid.Children.Add(new TreeWithBox(tree,aiWork));treeGrid.Children.Add(treeLine);
            treeEdge.HorizontalAlignment=HorizontalAlignment.Right;treeEdge.Margin=new Thickness(0,0,-2,0);treeGrid.Children.Add(treeEdge);
            treePanel.Child=treeGrid;treePanel.Width=settings.TreeWidth;
            DockPanel.SetDock(treePanel,Dock.Left);root.Children.Add(treePanel);
            // The folder tabs moved into the tree: the first start after that opens the tree once (it was closed by default),
            // so the folders are not suddenly nowhere to be seen. After that it stays as the user leaves it.
            bool treeIntro=!settings.TreeIntroShown;
            if(treeIntro) {
                var updated=settings.Copy();updated.TreeIntroShown=true;updated.TreeOpen=true;
                try {updated.Save(store.Root);settings=updated;} catch(Exception) {treeIntro=false;}
            }
            ShowTree(settings.TreeOpen || treeIntro,false);
            if(treeIntro && !another)
                ContentRendered+=delegate {
                    Dispatcher.BeginInvoke(new Action(()=>Notify(L.T("The folder list moved to the folder tree on the left"),
                        L.T("Your LoveMyShelf folders, This PC and Quick access are all there. It is the same as File Explorer. Open and close it with the button at the very top left (Ctrl+T)."),
                        null,NoticeKind.Info)),DispatcherPriority.ApplicationIdle);
                };
            // The folders area and the photos share the rest, one above the other (which one is on top is an option).
            var area=new DockPanel {LastChildFill=true};root.Children.Add(area);folderArea=area;
            folderPane=new FolderPane(this);area.Children.Add(folderPane);
            area.SizeChanged+=delegate {PlaceFolderPane();};
            PlaceFolderPane();
            SetupSimilar(area);
            SetupUncovered(area);
            var host=new Grid();area.Children.Add(host);
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
                var node=e.OriginalSource as DependencyObject;
                while(node!=null && !(node is ButtonBase) && !(node is ScrollBar)) {
                    node=(node is Visual || node is System.Windows.Media.Media3D.Visual3D)?VisualTreeHelper.GetParent(node):LogicalTreeHelper.GetParent(node);
                }
                if(node!=null)return;
                // Empty space: a left-button drag draws the range rectangle there, like File Explorer.
                MarqueeArm(e,MouseButton.Left);
                if(!SelectingByClick && (Keyboard.Modifiers&(ModifierKeys.Control|ModifierKeys.Shift))==0 && selected.Count>0)ClearSelection();
            };
            // The right button held down and dragged draws the range rectangle anywhere, even starting on a tile
            // (the tiles touch, so there is little empty space); a right click that doesn't move still opens the menu.
            grid.PreviewMouseRightButtonDown+=(s,e)=>{
                var node=e.OriginalSource as DependencyObject;
                while(node!=null && !(node is ScrollBar))node=(node is Visual || node is System.Windows.Media.Media3D.Visual3D)?VisualTreeHelper.GetParent(node):LogicalTreeHelper.GetParent(node);
                if(node==null)MarqueeArm(e,MouseButton.Right);
            };
            grid.PreviewMouseMove+=(s,e)=>MarqueeMove(e);
            grid.PreviewMouseLeftButtonUp+=(s,e)=>MarqueeUp(e,MouseButton.Left);
            grid.PreviewMouseRightButtonUp+=(s,e)=>MarqueeUp(e,MouseButton.Right);
            grid.LostMouseCapture+=delegate {if(marqueeActive)MarqueeEnd(false);};
            // A right click on empty space (not a tile, nothing drawn): File Explorer's blank-space menu.
            grid.PreviewMouseRightButtonUp+=(s,e)=>{
                if(e.Handled || IsOnTileOrBar(e.OriginalSource))return;
                e.Handled=true;ShowBackgroundMenu(grid,null);
            };
            VirtualizingPanel.SetIsVirtualizing(grid,true);
            VirtualizingPanel.SetVirtualizationMode(grid,VirtualizationMode.Recycling);
            VirtualizingPanel.SetScrollUnit(grid,ScrollUnit.Pixel);
            // Tile pictures load from the middle of the screen outward (TileQueue reads this from its threads).
            grid.AddHandler(ScrollViewer.ScrollChangedEvent,new ScrollChangedEventHandler((s,e)=>{
                if(rowHeight>0)centerRow=(int)((e.VerticalOffset+e.ViewportHeight/2)/rowHeight);
            }));
            host.Children.Add(grid);
            SetupMarquee(host);
            SetupNameCard(host);
            SetupDropHint(host);
            emptyTitle.HorizontalAlignment=HorizontalAlignment.Center;empty.Children.Add(emptyTitle);
            emptyHelp.Margin=new Thickness(0,8,0,22);empty.Children.Add(emptyHelp);
            emptyAction=UI.Btn("",()=>{if(FormatEmpty)ClickFormat(AllFormats);else if(FoldersFoldedAway)folderPane.Unfold();else PickFolder();});emptyAction.HorizontalAlignment=HorizontalAlignment.Center;empty.Children.Add(emptyAction);host.Children.Add(empty);
            resizeTimer.Tick+=delegate {resizeTimer.Stop();LayoutRows();};
            reflowTimer.Tick+=delegate {reflowTimer.Stop();CurrentReflow=null;};
            grid.SizeChanged+=delegate {resizeTimer.Stop();resizeTimer.Start();};
            Upscales.Finished+=UpscaleFinished;
            Upscales.Failed+=(photo,message)=>Notify(L.T("Upscaling failed"),photo.Name+Environment.NewLine+message,null,NoticeKind.Warning);
            // A cover started from a large view of this window saves through this window, so it has to finish first.
            Closing+=(s,e)=>{
                // The last folder window ends the program, and with it whatever the file queue is still sending.
                if(!CoverWork.BelongsTo(this) && TransferQueue.Busy && !Application.Current.Windows.OfType<GalleryWindow>().Any(w=>w!=this && w.IsVisible)) {
                    TransferWindow.ShowQueue(false);
                    var answer=MessageBox.Show(this,L.P("Files are still being sent, or are waiting to be sent.\n\nIf you close now, the queue stops where it is and is kept. The next time you start LoveMyShelf, the unfinished jobs are in the file queue, and [Go on] continues them from where they stopped. Every file that has not been sent yet stays whole where it is.\n\nClose now?",
                        "아직 보내고 있거나 기다리는 파일이 있어요.\n\n지금 닫으면 대기열을 그 자리에서 멈춰 두고 보관해요. 다음에 LoveMyShelf를 켜면 끝내지 못한 작업이 파일 대기열에 남아 있고, [이어서]를 누르면 멈춘 곳부터 계속해요. 아직 보내지 않은 파일은 원래 자리에 온전히 남아 있어요.\n\n지금 닫을까요?"),
                        "LoveMyShelf",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No);
                    if(answer!=MessageBoxResult.Yes){e.Cancel=true;return;}
                    TransferQueue.Shutdown();
                }
                if(!CoverWork.BelongsTo(this))return;
                e.Cancel=true;CoverWork.Front();
                Notify(L.T("Please finish the cover first"),L.T("A cover you started from this window is still open. Save or close it, then close this window."),null,NoticeKind.Info);
            };
            Closed+=delegate {
                FinishPendingCovers();
                resizeTimer.Stop();refreshVersion++;lifetime.Cancel();Upscales.Stop();
                FilesChanged-=ReloadAfterFileMove;
                TransferQueue.Tick-=ShowQueueButton;
                if(!Application.Current.Windows.OfType<GalleryWindow>().Any(w=>w!=this && w.IsVisible))TransferWindow.CloseQueue();
                // Large views are windows of their own now; they close with the folder window they came from.
                foreach(var view in Application.Current.Windows.OfType<Viewer>().Where(v=>v.Home==this).ToList())view.Close();
            };
            FilesChanged+=ReloadAfterFileMove;
            var recycleBin=moveToRecycleBin;
            TransferQueue.Start(store.Root,job=>BindTransfer(job,store,recycleBin));
            TransferWindow.ClosedWhileWorking-=TellQueueGoesOn;TransferWindow.ClosedWhileWorking+=TellQueueGoesOn;
            if(!another)Loaded+=delegate {TellLeftoverQueue();};
            TransferQueue.Hold=()=>relinking;
            TransferQueue.FilesMoved-=TellFilesMoved;TransferQueue.FilesMoved+=TellFilesMoved;
            TransferQueue.Tick+=ShowQueueButton;
            WatchRest();
            Activated+=delegate {TakeOverSettings();};
            localize=delegate {
                if(aiWork!=null)aiWork.Localize();
                open.Content=L.T("Add folder");emptyAction.Content=L.T("Select a folder…");
                newWindow.Content=L.T("New window");newWindow.ToolTip=L.T("Open one more window and you can drag files between two folders")+" (Ctrl+N)";
                AutomationProperties.SetName(newWindow,L.T("New window"));
                guide.Content=L.T("Guide & Options");guide.ToolTip=L.T("Guide & Options")+" (F1)";
                AutomationProperties.SetName(guide,L.T("Guide & Options"));AutomationProperties.SetName(open,L.T("Add folder"));
                open.ToolTip=L.T("Add folder")+" (Ctrl+O)";
                folderList.ToolTip=L.T("All folders · rolling the wheel over the tabs also moves them sideways");AutomationProperties.SetName(folderList,L.T("All folders"));
                updateTopBar();
                searchHint.Text=L.T("Search names");AutomationProperties.SetName(search,L.T("Search names"));
                treeButton.ToolTip=L.T("Folder tree")+" (Ctrl+T)";AutomationProperties.SetName(treeButton,L.T("Folder tree"));
                searchButton.ToolTip=L.T("Search names")+" (Ctrl+F)";AutomationProperties.SetName(searchButton,L.T("Search names"));
                openCoverFolder.Content=L.T("Open cover folder");
                selectionToggle.Content=L.P("Select","선택하기");
                allCropsButton.Content=L.T("Cover collection");unfinishedButton.Content=L.T("Unfinished cover collection");
                var labels=new[]{"All","Unfinished","Finished"};
                var names=new[]{"All items","Only files without a cover","Only files with a cover"};
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
                bool folder=paths!=null && paths.Length==1 && Directory.Exists(paths[0]);
                e.Effects=folder?DragDropEffects.Link:DragDropEffects.None;
                if(folder && FolderTree.IsShelfRow(e.OriginalSource)) {
                    var over=FolderTree.EntryAt(e.OriginalSource);
                    if(OnShelf(paths[0]) && over!=null && over.Kind=="tab")ShowDropHint(L.T("Let go and it moves to this place in the list."));
                    else ShowDropHint(L.F("Let go and the “{0}” folder is added to LoveMyShelf.",Path.GetFileName(paths[0].TrimEnd('\\'))));
                }
                e.Handled=true;
            };
            Drop+=(s,e)=>{
                if(e.Data.GetDataPresent(MoveFormat)){MoveDrop(e);return;}
                var paths=e.Data.GetData(DataFormats.FileDrop) as string[];
                if(paths!=null && paths.Length==1 && Directory.Exists(paths[0])) {
                    if(FolderTree.IsShelfRow(e.OriginalSource)) {
                        HideDropHint();
                        // A LoveMyShelf folder dropped on another one moves there in the list (like Quick access in File Explorer).
                        var over=FolderTree.EntryAt(e.OriginalSource);
                        if(OnShelf(paths[0]) && over!=null && over.Kind=="tab")ReorderShelf(paths[0],over.Album);
                        else AddToShelf(paths[0]);
                    }
                    else OpenFolder(paths[0]);
                }
            };
            PreviewKeyDown+=(s,e)=>{
                if(aiWork!=null && aiWork.Typing)return; // typing in the writing box: Backspace, F5 and the rest stay there
                if(e.Key==Key.F5) {Refresh();StartCoverMigration();DrivesChangedSoon(0.3);e.Handled=true;} // F5 also looks again for files renamed or moved outside
                else if(Keyboard.Modifiers==ModifierKeys.Control && e.Key==Key.O) {
                    PickFolder(); e.Handled=true;
                }
                // Back up out of a folder opened from a folder tile, like File Explorer.
                // File Explorer's keys: Backspace or Alt+← back, Alt+→ forward, Alt+↑ up, Ctrl+L / Alt+D / F4 the address bar.
                else if(((e.Key==Key.Back && Keyboard.Modifiers==ModifierKeys.None) || (e.SystemKey==Key.Left && Keyboard.Modifiers==ModifierKeys.Alt))
                    && !search.IsKeyboardFocusWithin && !address.Editing) {
                    GoBack(); e.Handled=true;
                }
                else if(e.SystemKey==Key.Right && Keyboard.Modifiers==ModifierKeys.Alt && !address.Editing){GoForward();e.Handled=true;}
                else if(e.SystemKey==Key.Up && Keyboard.Modifiers==ModifierKeys.Alt && !address.Editing){GoUpFolder();e.Handled=true;}
                else if((Keyboard.Modifiers==ModifierKeys.Control && e.Key==Key.L) || (Keyboard.Modifiers==ModifierKeys.Alt && e.SystemKey==Key.D) ||
                    (e.Key==Key.F4 && Keyboard.Modifiers==ModifierKeys.None)) {address.BeginEdit();e.Handled=true;}
                else if(Keyboard.Modifiers==ModifierKeys.Control && e.Key==Key.T) {
                    ToggleTree(); e.Handled=true;
                }
                else if(Keyboard.Modifiers==ModifierKeys.Control && e.Key==Key.F) {
                    OpenSearch(); e.Handled=true;
                }
                else if(Keyboard.Modifiers==ModifierKeys.Control && e.Key==Key.N && !search.IsKeyboardFocusWithin) {
                    OpenAnotherWindow(); e.Handled=true;
                }
            };
            WatchDrives();
            // The mouse's back button goes up too.
            PreviewMouseDown+=(s,e)=>{
                if(e.ChangedButton==MouseButton.XButton1){GoBack();e.Handled=true;}
                else if(e.ChangedButton==MouseButton.XButton2){GoForward();e.Handled=true;}
            };
            var albums=store.VisibleAlbums(); string lastFile=Path.Combine(store.Root,"last.txt");
            string last=File.Exists(lastFile)?File.ReadAllText(lastFile):"";
            var initial=last==CollectionKey?allCrops:last==UnfinishedKey?unfinished:
                last.StartsWith(ScopeKey,StringComparison.Ordinal) && CanGatherFolder(last.Substring(ScopeKey.Length))?ScopeAlbum(last.Substring(ScopeKey.Length)):
                last.StartsWith(TagKey,StringComparison.Ordinal) && last.Length>TagKey.Length?TagAlbum(last.Substring(TagKey.Length)):(albums.FirstOrDefault(a=>Path.GetFileName(a.Folder)==last)??albums.FirstOrDefault()??
                allCrops);
            // On cold start the constructor runs before WPF installs its UI synchronization context.
            Loaded+=delegate {
                if(active!=null)return;
                // A window opened with "Open in new window" starts on that folder.
                Album start=startFolder==null?null:startFolder==CollectionKey?allCrops:startFolder==UnfinishedKey?unfinished:
                    startFolder.StartsWith(ScopeKey,StringComparison.Ordinal) && CanGatherFolder(startFolder.Substring(ScopeKey.Length))?ScopeAlbum(startFolder.Substring(ScopeKey.Length)):
                    startFolder.StartsWith(TagKey,StringComparison.Ordinal) && startFolder.Length>TagKey.Length?TagAlbum(startFolder.Substring(TagKey.Length)):
                    store.Albums().FirstOrDefault(a=>a.Source!=null && String.Equals(a.Folder,startFolder,StringComparison.OrdinalIgnoreCase));
                Select(start??initial);
            };
            ContentRendered+=delegate {
                if(coverSetupShown || another)return; // the first window already did this
                coverSetupShown=true;
                AppUpdater.ReportStarted();
                // Started with a file from File Explorer's "Open with LoveMyShelf".
                string outside=Program.PendingPath;Program.PendingPath=null;
                if(outside!=null)OpenExternalFile(outside);
                var sceneStore=store;var sceneToken=lifetime.Token;
                bool sceneSingles=settings.CoverTimes;
                Task.Run(()=>sceneStore.FillSceneTimes(sceneToken,sceneSingles));
                DrivesChangedSoon(0.3); // a drive may have a new letter since last time
                TellIfUpdated();
                TellIfUpdateUndone();
                BackupWindow.RemindIfDue(this);
                TellGifDefaultsChanged();
                if(StoreImport.TakeNotice())
                    Notify("LoveMyShelf",L.P("Your covers and records from the GitHub version were brought over. From now on the Store version keeps its own.",
                        "GitHub판에서 만든 표지와 기록을 가져왔어요. 이제부터 스토어판 기록은 따로 저장돼요."),null);
                // The first start with range selection: tell about it once, a little after the window appears.
                if(!settings.MarqueeTipShown && settings.MarqueeUses==0) {
                    var updated=settings.Copy();updated.MarqueeTipShown=true;
                    try {updated.Save(store.Root);settings=updated;} catch(Exception) {}
                    var later=new DispatcherTimer {Interval=TimeSpan.FromSeconds(treeIntro?9:3)}; // after the tree notice
                    later.Tick+=delegate {later.Stop();MarqueeTip();};later.Start();
                }
                if(String.IsNullOrWhiteSpace(settings.CoverDirectory))EnsureCoverFolder(this);
                else StartCoverMigration();
                if(settings.CheckUpdates)CheckForUpdate();
            };
        }
        // The first start of a newer version: one small notice that leads to what changed.
        void TellIfUpdated() {
            if(settings.SeenVersion==AppInfo.Version)return;
            string before=String.IsNullOrEmpty(settings.SeenVersion)?AppInfo.LastWithoutNotice:settings.SeenVersion;
            var updated=settings.Copy();updated.SeenVersion=AppInfo.Version;
            try {updated.Save(store.Root);settings=updated;} catch(Exception) {return;}
            if(!AppInfo.IsNewer(AppInfo.Version,before))return;
            Notify(L.F("Updated to version {0}",AppInfo.Version),L.P("See what changed in this version.","이번 버전에서 바뀐 내용을 볼 수 있어요."),
                ()=>GuideWindow.Open(this,"news"),NoticeKind.Done,false,8,L.P("What's new","새 소식 보기"));
        }
        // An update that could not be finished put the version before it back. Said once, at the next start,
        // with the way that still works: the ZIP from the download page.
        static string undoneUpdate; // one for all the folder windows
        void TellIfUpdateUndone() {
            if(AppInfo.Packaged)return;
            undoneUpdate=AppUpdater.UndoneUpdate();
            if(undoneUpdate==null)return;
            Notify(L.PF("Couldn’t update to the new version ({0})","새 버전({0})으로 업데이트하지 못했어요",undoneUpdate),
                L.P("The update couldn’t be finished, so the version you were using was put back. Your covers and settings are as they were.",
                    "업데이트를 마치지 못해서 쓰던 버전으로 되돌려 두었어요. 만든 표지와 설정은 그대로예요.")+Environment.NewLine+
                L.P("[Download page] opens the page. Get the new ZIP there, close LoveMyShelf, then unzip it over the folder you use now.",
                    "[다운로드 페이지]를 누르면 페이지가 열려요. 거기서 새 ZIP을 받은 뒤 LoveMyShelf를 끄고, 지금 쓰는 폴더에 덮어쓰기로 압축을 풀면 돼요."),
                OpenReleasesPage,NoticeKind.Warning,true,15,L.P("Download page","다운로드 페이지"));
        }
        async void CheckForUpdate() {
            if(AppInfo.Packaged)return;
            string newer=await AppInfo.NewerVersionAsync();
            if(newer==null || lifetime.IsCancellationRequested || !settings.CheckUpdates)return;
            var plan=await AppUpdater.LoadAsync(newer);
            if(lifetime.IsCancellationRequested || !settings.CheckUpdates)return;
            if(plan==null && AppUpdater.FromWinget) {
                Notify(L.F("Version {0} is out",newer),
                    L.P("[Update now] closes LoveMyShelf, winget updates it, and it starts again. Your covers and settings stay as they are.",
                        "[지금 업데이트]를 누르면 LoveMyShelf가 닫히고 winget이 새 버전으로 바꾼 뒤 다시 켜요. 만든 표지와 설정은 그대로예요."),
                    UpdateWithWinget,NoticeKind.Info,true,15,L.P("Update now","지금 업데이트"));
                return;
            }
            if(plan==null) {
                if(newer==undoneUpdate)return; // just told, with the same way out
                Notify(L.F("Version {0} is out",newer),
                    L.P("[Download page] opens the page. Get the new ZIP there, close LoveMyShelf, then unzip it over the folder you use now. Your covers and settings stay as they are.",
                        "[다운로드 페이지]를 누르면 페이지가 열려요. 거기서 새 ZIP을 받은 뒤 LoveMyShelf를 끄고, 지금 쓰는 폴더에 덮어쓰기로 압축을 풀면 돼요. 만든 표지와 설정은 그대로 남아요."),
                    OpenReleasesPage,NoticeKind.Info,false,15,L.P("Download page","다운로드 페이지"));
                return;
            }
            Notify(L.F("Version {0} is out",newer),
                plan.Notes.Count>0?L.P("See what changed in the new version, then update from there.","새 버전에서 바뀐 내용을 보고, 거기서 바로 업데이트할 수 있어요."):
                    L.P("[Update now] starts the update. You can keep using LoveMyShelf while it downloads, and your covers and settings stay as they are.","[지금 업데이트]를 누르면 업데이트를 시작해요. 다운로드하는 동안에도 계속 쓸 수 있고, 만든 표지와 설정은 그대로 남아요."),
                ()=>ShowUpdateNews(plan),NoticeKind.Info,true,15,plan.Notes.Count>0?L.P("What's new","새 소식 보기"):L.P("Update now","지금 업데이트"));
        }
        void ShowUpdateNews(UpdatePlan plan) {
            if(plan.Notes.Count==0){DownloadUpdate(plan);return;}
            var news=new UpdateNewsWindow(ActiveAppWindow(),plan,lifetime.Token);
            if(news.ShowDialog()==true)DownloadUpdate(plan);
        }
        void OpenReleasesPage() {
            try {System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppInfo.ReleasesPage) {UseShellExecute=true});}
            catch(Exception ex) {UI.Error(this,ex);}
        }
        async void DownloadUpdate(UpdatePlan plan) {
            var working=Notify(L.T("Downloading the update…"),L.T("You can keep using it meanwhile. We’ll let you know here when it’s ready."),null,NoticeKind.Busy);
            try {
                string stage=await Task.Run(()=>AppUpdater.Prepare(plan,lifetime.Token),lifetime.Token);
                working.Dismiss();
                if(!lifetime.IsCancellationRequested)OfferRestart(stage);
            } catch(OperationCanceledException) {working.Dismiss();}
            catch(Exception) {
                working.Dismiss();
                if(!lifetime.IsCancellationRequested)
                    Notify(L.T("Couldn’t get the update"),L.P("The program is as it was. You can get the new version from the download page.","프로그램은 그대로예요. 다운로드 페이지에서 새 버전을 받을 수 있어요."),OpenReleasesPage,NoticeKind.Warning,false,15,L.P("Download page","다운로드 페이지"));
            }
        }
        void OfferRestart(string stage) {
            Notify(L.T("The update is ready"),L.P("[Restart now] closes LoveMyShelf for a moment, then it starts again as the new version.","[지금 다시 켜기]를 누르면 LoveMyShelf가 잠깐 꺼졌다가 새 버전으로 다시 켜져요."),
                ()=>RestartForUpdate(stage),NoticeKind.Done,true,15,L.P("Restart now","지금 다시 켜기"));
        }
        static bool wingetStarted; // one PowerShell window, even if the notice is clicked twice or in two windows
        void UpdateWithWinget() {
            if(wingetStarted)return;
            var windows=Application.Current.Windows.OfType<Window>().ToList();
            if(TransferQueue.Busy || CoverWork.Top!=null || windows.OfType<GalleryWindow>().Any(w=>w.Upscales.IsBusy) || windows.OfType<UpscaleToast>().Any(t=>t.IsWorking)) {
                if(TransferQueue.Busy)TransferWindow.ShowQueue(false);
                Notify(L.T("Some work is still in progress"),L.P("So that the work isn't cut off, press the button again after it is done.","작업이 끊기지 않도록, 끝난 뒤에 버튼을 다시 눌러 주세요."),
                    UpdateWithWinget,NoticeKind.Info,true,15,L.P("Update now","지금 업데이트"));
                return;
            }
            foreach(var window in windows.OfType<GalleryWindow>())window.FinishPendingCovers();
            try {AppUpdater.StartWingetUpgrade();wingetStarted=true;}
            catch(Exception) {
                Notify(L.T("Couldn’t get the update"),L.P("The program is as it was. You can get the new version from the download page.","프로그램은 그대로예요. 다운로드 페이지에서 새 버전을 받을 수 있어요."),OpenReleasesPage,NoticeKind.Warning,false,15,L.P("Download page","다운로드 페이지"));
                return;
            }
            Application.Current.Shutdown();
        }
        void RestartForUpdate(string stage) {
            var windows=Application.Current.Windows.OfType<Window>().ToList();
            if(TransferQueue.Busy || windows.OfType<GalleryWindow>().Any(w=>w.Upscales.IsBusy) || windows.OfType<UpscaleToast>().Any(t=>t.IsWorking)) {
                if(TransferQueue.Busy)TransferWindow.ShowQueue(false);
                Notify(L.T("Some work is still in progress"),L.P("So that the work isn't cut off, press the button again after it is done.","작업이 끊기지 않도록, 끝난 뒤에 버튼을 다시 눌러 주세요."),
                    ()=>RestartForUpdate(stage),NoticeKind.Info,true,15,L.P("Restart now","지금 다시 켜기"));
                return;
            }
            foreach(var window in windows.OfType<GalleryWindow>())window.FinishPendingCovers();
            try {AppUpdater.StartApply(stage);}
            catch(Exception) {
                Notify(L.T("Couldn’t get the update"),L.P("The program is as it was. You can get the new version from the download page.","프로그램은 그대로예요. 다운로드 페이지에서 새 버전을 받을 수 있어요."),OpenReleasesPage,NoticeKind.Warning,false,15,L.P("Download page","다운로드 페이지"));
                return;
            }
            Application.Current.Shutdown();
        }
        void ChangeFolderFilter(int choice) {
            if(active==null || (active.Source==null && !ShowingScope) || folderFilter==choice)return;
            folderFilters[active.Folder]=folderFilter=choice;
            shuffledOrder=null;Refresh(ShowingScope);
        }
        // The width a row of buttons needs right now. Each child is measured on its own: a panel keeps its old
        // size until the next layout pass, even after a button's label changed.
        static double RowWidth(StackPanel row) {
            double width=row.Margin.Left+row.Margin.Right;
            foreach(UIElement child in row.Children) {
                if(child.Visibility==Visibility.Collapsed)continue;
                InvalidateTree(child);
                child.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));width+=child.DesiredSize.Width;
            }
            return width;
        }
        static void InvalidateTree(DependencyObject node) {
            var element=node as UIElement;if(element!=null)element.InvalidateMeasure();
            for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++)InvalidateTree(VisualTreeHelper.GetChild(node,i));
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
                else {
                    bool isNew=!OnShelf(path);
                    Select(store.OpenFolder(path));
                    if(isNew) {
                        if(tree!=null && treePanel.Visibility==Visibility.Visible){tree.Build();tree.UnfoldShelfFolder(path);TreeSyncSoon();}
                        foreach(var other in Application.Current.Windows.OfType<GalleryWindow>().Where(w=>w!=this))other.FoldersChanged();
                    }
                }
            } catch(Exception ex) {UI.Error(this,ex);}
        }
        void Select(Album album) {
            if(active==null || active.Folder!=album.Folder){shuffledOrder=null;searchTimer.Stop();search.Text="";}
            // The way down from the tab to the folder on screen: going back up keeps the part above it,
            // anything else (a tab, a collection) starts over from that folder.
            // Back / Forward remember each place, like File Explorer (a refresh of the same place doesn't count).
            int inTrail=trail.FindIndex(a=>a.Folder==album.Folder);
            if(inTrail>=0)trail.RemoveRange(inTrail+1,trail.Count-inTrail-1);else{trail.Clear();trail.Add(album);trailFromPc=false;}
            // A folder reached from the tree or the path row gets its records the first time it is opened.
            if(album.Source!=null && !Directory.Exists(album.Folder)) {
                try {album=store.OpenSubfolder(album.Source);trail[trail.Count-1]=album;}
                catch(Exception ex) {UI.Error(this,ex);return;}
            }
            bool changed=active==null || !String.Equals(LocationKey(active),LocationKey(album),StringComparison.OrdinalIgnoreCase);
            if(changed && active!=null && !historyMove) {
                backStack.Add(LocationKey(active));backFormats.Add(new HashSet<string>(chosenFormats,chosenFormats.Comparer));
                forwardStack.Clear();forwardFormats.Clear();
            }
            if(changed || historyMove)ChangeFormatLocation();
            active=album; tabs.Children.Clear();scrollToTop=true;
            // Only the folder name, like File Explorer: side-by-side windows, the taskbar and Alt+Tab tell folders apart.
            Title=album.Name;
            if(updateTopBar!=null)updateTopBar(); // the [Type] button: hidden in Similar photos, back to "Type" in a new place
            BuildPath();
            TreeSyncSoon();
            UI.Use(allCropsButton,Control.ForegroundProperty,ShowingAllCrops?"Accent":"Text");
            UI.Use(allCropsButton,Control.BorderBrushProperty,ShowingAllCrops?"Accent":"Line");
            UI.Use(unfinishedButton,Control.ForegroundProperty,ShowingUnfinished?"Accent":"Text");
            UI.Use(unfinishedButton,Control.BorderBrushProperty,ShowingUnfinished?"Accent":"Line");
            UI.Use(tagsButton,Control.ForegroundProperty,ShowingTag?"Accent":"Text");
            UI.Use(tagsButton,Control.BorderBrushProperty,ShowingTag?"Accent":"Line");
            filter.Visibility=active.Source!=null || ShowingScope || ShowingUnfinished?Visibility.Visible:Visibility.Collapsed;
            ShowSimilarBar();
            ShowUncoveredBar();
            if(updateFooter!=null)updateFooter();
            int view;
            folderFilter=folderFilters.TryGetValue(active.Folder,out view)?view:0;
            filterButtons[folderFilter].IsChecked=true;
            BuildTabs();
            try { File.WriteAllText(Path.Combine(store.Root,"last.txt"),trail[0].Source==null?trail[0].Folder:Path.GetFileName(trail[0].Folder)); } catch(IOException) { }
            Refresh();
            if(grid.Items.Count>0) grid.ScrollIntoView(grid.Items[0]);
        }
        // ----- Folders inside a folder (Options: Show the folders inside a folder) -----
        readonly List<Album> trail=new List<Album>();
        readonly StackPanel pathRow=new StackPanel {Orientation=Orientation.Horizontal,Visibility=Visibility.Collapsed,ClipToBounds=true};
        // The folder tiles on screen: full path and how many photos and videos are right inside (-1: can't be read).
        List<Tuple<string,int>> subfolders=new List<Tuple<string,int>>();
        int folderRows;
        internal bool InsideSubfolder {get {return trail.Count>1;}}
        internal const int FoldersPerCell=3;
        internal IList<Tuple<string,int>> FolderTiles {get {return subfolders;}}
        internal void EnterFolder(string path) {
            Album album;
            try {album=store.OpenSubfolder(path);} catch(Exception ex) {UI.Error(this,ex);return;}
            if(!trail.Any(a=>a.Folder==album.Folder))trail.Add(album);
            Select(album);
        }
        internal bool GoUp() {return GoUpFolder();}
        // ----- Back, Forward, Up and the address bar (File Explorer's top row) -----
        AddressBar address;
        readonly List<Button> navButtons=new List<Button>();
        readonly List<string> backStack=new List<string>(),forwardStack=new List<string>();
        bool historyMove;
        static string LocationKey(Album album) {return album.Source??("@"+album.Folder);}
        void GoToLocation(string key,HashSet<string> formats) {
            historyMove=true;historyFormats=formats;
            try {
                if(key.StartsWith("@"+TagKey,StringComparison.Ordinal))Select(TagAlbum(key.Substring(1+TagKey.Length)));
                else if(key.StartsWith("@"+ScopeKey,StringComparison.Ordinal))Select(ScopeAlbum(key.Substring(1+ScopeKey.Length)));
                else if(key=="@"+SimilarKey)Select(SimilarAlbum);
                else if(key.StartsWith("@"))Select(key.Substring(1)==UnfinishedKey?unfinished:allCrops);
                else if(Directory.Exists(key))NavigateTo(key);
            } finally {historyMove=false;historyFormats=null;}
        }
        internal void GoBack() {MoveInHistory(backStack,backFormats,forwardStack,forwardFormats);}
        internal void GoForward() {MoveInHistory(forwardStack,forwardFormats,backStack,backFormats);}
        void MoveInHistory(List<string> from,List<HashSet<string>> fromFormats,List<string> to,List<HashSet<string>> toFormats) {
            if(from.Count==0 || active==null)return;
            int last=from.Count-1;string key=from[last];var formats=fromFormats[last];
            from.RemoveAt(last);fromFormats.RemoveAt(last);
            to.Add(LocationKey(active));toFormats.Add(new HashSet<string>(chosenFormats,chosenFormats.Comparer));
            GoToLocation(key,formats);
        }
        // Up goes to the real folder above, even above a LoveMyShelf folder (like File Explorer).
        internal bool GoUpFolder() {
            if(active==null || active.Source==null)return false;
            string parent=Path.GetDirectoryName(active.Source.TrimEnd('\\'));
            if(String.IsNullOrEmpty(parent) || ShellPlaces.Same(parent,active.Source))return false;
            NavigateTo(parent);return true;
        }
        void UpdateNavButtons() {
            foreach(var button in navButtons) {
                var words=((string)button.Tag).Split('|');
                button.ToolTip=L.T(words[0])+" ("+words[1]+")";AutomationProperties.SetName(button,L.T(words[0]));
                bool can=words[0]=="Back"?backStack.Count>0:words[0]=="Forward"?forwardStack.Count>0:
                    words[0]=="Up to the folder above"?(active!=null && active.Source!=null && !String.IsNullOrEmpty(Path.GetDirectoryName(active.Source.TrimEnd('\\')))):true;
                button.IsEnabled=can;
            }
        }
        internal void ShowAlbum(Album album) {Select(album);}
        internal void FocusTiles() {grid.Focus();Keyboard.Focus(grid);}
        // A path typed or pasted into the address bar.
        internal void GoToTyped(string text) {
            string path=Environment.ExpandEnvironmentVariables((text??"").Trim().Trim('"'));
            if(path.Length==0)return;
            // The name as the address bar shows it, in either language, or written the old way with spaces.
            string named=new[]{"LoveMyShelf","러브마이셀프","Love My Shelf"}.FirstOrDefault(n=>path.StartsWith(n,StringComparison.OrdinalIgnoreCase));
            if(named!=null) {
                string rest=path.Substring(named.Length).Trim('\\');
                if(rest.Length==0 || String.Equals(rest,allCrops.Name,StringComparison.OrdinalIgnoreCase)){Select(allCrops);return;}
                if(String.Equals(rest,unfinished.Name,StringComparison.OrdinalIgnoreCase)){Select(unfinished);return;}
            }
            try {
                if(Directory.Exists(path)){NavigateTo(path);return;}
                if(File.Exists(path)){NavigateTo(Path.GetDirectoryName(path));return;}
            } catch(Exception) {}
            Notify(L.T("Can’t find that place"),L.F("Can’t find the location “{0}”. Check the address and type it again.",text.Trim()),null,NoticeKind.Warning);
        }
        // ----- The folder tree (File Explorer's navigation pane) -----
        readonly Border treePanel=new Border();
        FolderTree tree;
        readonly Button treeButton;
        // True when the folder on screen was opened from This PC in the tree: the path row then shows the whole way from the drive.
        bool trailFromPc;
        internal IEnumerable<string> PathWords() {
            return pathRow.Children.OfType<FrameworkElement>().Select(e=>e is TextBlock?((TextBlock)e).Text:e is Button && ((Button)e).Content is string?(string)((Button)e).Content:"[↑]");
        }
        internal void ToggleTree() {ShowTree(treePanel.Visibility!=Visibility.Visible,true);}
        // Opens the folder tree if it is closed and puts the caret in the writing box under it.
        internal void ShowAiWork() {
            if(treePanel.Visibility!=Visibility.Visible)ShowTree(true,true);
            aiWork.FocusBox();
        }
        void ShowTree(bool open,bool remember) {
            treePanel.Visibility=open?Visibility.Visible:Visibility.Collapsed;
            UI.Use(treeButton,Control.BorderBrushProperty,open?"Accent":"Line");
            if(open){tree.Build();TreeSyncSoon();}
            if(remember)SaveTree(open,null,null);
        }
        internal void SaveTree(bool? open,int? width,string folded) {
            var updated=settings.Copy();
            if(open.HasValue)updated.TreeOpen=open.Value;
            if(width.HasValue)updated.TreeWidth=width.Value;
            if(folded!=null)updated.TreeFolded=folded;
            try {updated.Save(store.Root);settings=updated;} catch(Exception) {}
        }
        // Like pinning a folder to Quick access in File Explorer: the folder gets a tab and a row under LoveMyShelf.
        internal void AddToShelf(string path) {
            try {store.OpenFolder(path);} catch(Exception ex) {UI.Error(this,ex);return;}
            BuildTabs();if(tree!=null && treePanel.Visibility==Visibility.Visible){tree.Build();tree.UnfoldShelfFolder(path);TreeSyncSoon();}
            foreach(var other in Application.Current.Windows.OfType<GalleryWindow>().Where(w=>w!=this))other.FoldersChanged();
            if(ShowingUnfinished || ShowingScope)Refresh();
            else if(photos.Count==0 && active!=null && active.Source==null)ShowPhotos();
        }
        internal void RemoveFromShelf(string path) {
            var album=store.VisibleAlbums().FirstOrDefault(a=>ShellPlaces.Same(a.Source,path));if(album==null)return;
            try {store.HideFolder(album);} catch(Exception ex) {UI.Error(this,ex);return;}
            if(trail.Count>0 && trail[0].Folder==album.Folder)Select(allCrops);else BuildTabs();
            if(tree!=null && treePanel.Visibility==Visibility.Visible){tree.Build();TreeSyncSoon();}
            foreach(var other in Application.Current.Windows.OfType<GalleryWindow>().Where(w=>w!=this))other.FoldersChanged();
            if(ShowingUnfinished || ShowingScope)Refresh();
        }
        void ReorderShelf(string path,Album place) {
            var albums=store.VisibleAlbums();
            var moving=albums.FirstOrDefault(a=>ShellPlaces.Same(a.Source,path));if(moving==null || place==null)return;
            int position=albums.FindIndex(a=>a.Folder==place.Folder);if(position<0)return;
            try {store.MoveTab(moving,position);} catch(Exception ex) {UI.Error(this,ex);return;}
            FoldersChanged();
            foreach(var other in Application.Current.Windows.OfType<GalleryWindow>().Where(w=>w!=this))other.FoldersChanged();
        }
        internal bool OnShelf(string path) {return store.VisibleAlbums().Any(a=>ShellPlaces.Same(a.Source,path));}
        internal bool CanGatherFolder(string path) {return !String.IsNullOrEmpty(path) && store.VisibleAlbums().Any(a=>Renamer.IsAt(path,a.Source));}
        internal void GatherFolder(string path) {
            if(CanGatherFolder(path))Select(ScopeAlbum(path));
        }
        void FoldersChanged() {BuildTabs();if(tree!=null && treePanel.Visibility==Visibility.Visible){tree.Build();TreeSyncSoon();}if(ShowingUnfinished || ShowingScope)Refresh();}
        FolderPane folderPane;
        DockPanel folderArea;
        internal void PlaceFolderPane() {
            // With no photos to share the space, the folders start at the top and may use the whole height.
            bool fills=folderPane.FillsArea,below=settings.FoldersBelow && !fills;
            DockPanel.SetDock(folderPane,below?Dock.Bottom:Dock.Top);
            // A thin line between the photos and the folders when the folders are below (the header line is on top of them).
            folderPane.Margin=new Thickness(0,below?2:0,0,below?0:2);
            if(folderArea.ActualHeight>0)folderPane.MaxHeight=Math.Max(90,folderArea.ActualHeight*(fills?1:0.45));
        }
        // The View menu of the folders area and Options both come here.
        internal void SaveFolderView(int? view,bool? below) {
            var updated=settings.Copy();
            if(view.HasValue)updated.FolderView=view.Value;
            if(below.HasValue)updated.FoldersBelow=below.Value;
            try {updated.Save(store.Root);settings=updated;} catch(Exception ex) {UI.Error(this,ex);return;}
            PlaceFolderPane();folderPane.Fill();SettingsChanged();
        }
        internal void SaveFoldersFolded(bool folded) {
            var updated=settings.Copy();updated.FoldersFolded=folded;
            try {updated.Save(store.Root);settings=updated;} catch(Exception) {}
        }
        internal void TreeSyncSoon() {
            if(tree==null || treePanel.Visibility!=Visibility.Visible)return;
            Dispatcher.BeginInvoke(new Action(()=>{
                if(!ShowingScope){tree.ShowCurrent(active,trail,trailFromPc);return;}
                bool fromPc;var path=FolderPathTrail(ScopeFolder,out fromPc);
                tree.ShowCurrent(path[path.Count-1],path,fromPc);
            }),DispatcherPriority.Background);
        }
        internal void OpenFromTree(TreeEntry entry) {
            if(entry.Album!=null && (entry.Album.Source==null || entry.Kind=="tab")) {Select(entry.Album);return;}
            if(entry.Path!=null)NavigateTo(entry.Path);
        }
        // Opens any folder, as from File Explorer's tree: inside a folder tab the way starts at that tab, otherwise at the drive.
        internal void NavigateTo(string path) {
            string full;
            try {full=Path.GetFullPath(path);} catch(Exception ex) {UI.Error(this,ex);return;}
            if(full.Length>Path.GetPathRoot(full).Length)full=full.TrimEnd(Path.DirectorySeparatorChar);
            if(!Directory.Exists(full)){UI.Error(this,new DirectoryNotFoundException(L.T("The source folder could not be found.")));return;}
            bool fromPc;var pathTrail=FolderPathTrail(full,out fromPc);
            trail.Clear();trail.AddRange(pathTrail);trailFromPc=fromPc;
            Select(trail[trail.Count-1]);
        }
        List<Album> FolderPathTrail(string full,out bool fromPc) {
            Album tab=null;
            foreach(var album in store.VisibleAlbums())
                if(Renamer.IsAt(full,album.Source) && (tab==null || album.Source.Length>tab.Source.Length))tab=album;
            var chain=new List<string>();
            for(string at=full;at!=null;at=Path.GetDirectoryName(at)) {
                chain.Insert(0,at);
                if(tab!=null && ShellPlaces.Same(at,tab.Source))break;
            }
            fromPc=tab==null;
            return chain.Select(at=>tab!=null && ShellPlaces.Same(at,tab.Source)?tab:new Album(store.AlbumFolderOf(at),FolderName(at),at)).ToList();
        }
        static string FolderName(string path) {
            string name=Path.GetFileName(path.TrimEnd('\\'));
            return String.IsNullOrEmpty(name)?path.TrimEnd('\\'):name; // a drive is shown as "C:"
        }
        // Slides the folder tabs sideways over about a sixth of a second.
        DispatcherTimer tabSlide;
        void SlideTabs(ScrollViewer scroller,double by) {
            if(tabSlide!=null)tabSlide.Stop();
            double from=scroller.HorizontalOffset,to=Math.Max(0,Math.Min(scroller.ScrollableWidth,from+by));
            var started=DateTime.UtcNow;
            tabSlide=new DispatcherTimer(DispatcherPriority.Render) {Interval=TimeSpan.FromMilliseconds(15)};
            tabSlide.Tick+=delegate {
                double t=Math.Min(1,(DateTime.UtcNow-started).TotalMilliseconds/170),eased=1-Math.Pow(1-t,3);
                scroller.ScrollToHorizontalOffset(from+(to-from)*eased);
                if(t>=1)tabSlide.Stop();
            };
            tabSlide.Start();
        }
        // The way from the tab (or from This PC) to the folder on screen, like File Explorer's address bar: when it is
        // too long for the window, the parts at the front fold into « (a click lists them), and the folder itself always shows.
        readonly List<Tuple<string,Album>> pathParts=new List<Tuple<string,Album>>();
        void BuildPath() {
            // The way is shown in the address bar now, like File Explorer; the old path row stays hidden.
            pathParts.Clear();pathRow.Visibility=Visibility.Collapsed;
            if(address!=null) {
                address.Show(ShowingScope?new Album(active.Folder,active.Name+" · "+L.T("Including subfolders")):active);
                address.ToolTip=ScopeFolder;
            }
            UpdateNavButtons();
            return;
#pragma warning disable 162
            bool show=trail.Count>1 || trailFromPc;
            pathRow.Visibility=show?Visibility.Visible:Visibility.Collapsed;
            if(!show){pathRow.Children.Clear();return;}
            if(trailFromPc)pathParts.Add(Tuple.Create(L.T("This PC"),(Album)null));
            foreach(var album in trail)pathParts.Add(Tuple.Create(album.Name,album));
            LayoutPath();
#pragma warning restore 162
        }
        FrameworkElement PathPart(Tuple<string,Album> part,bool last) {
            var button=new Button {Content=part.Item1,Padding=new Thickness(6,2,6,2),MinHeight=24,BorderThickness=new Thickness(0),
                Background=Brushes.Transparent,VerticalAlignment=VerticalAlignment.Center,FontSize=12.5};
            if(part.Item2!=null)button.ToolTip=part.Item2.Source;
            UI.Use(button,Control.ForegroundProperty,last?"Text":"Subtle");
            if(last)button.FontWeight=FontWeights.SemiBold;
            else if(part.Item2!=null){var target=part.Item2;button.Click+=delegate {Select(target);};}
            else button.IsHitTestVisible=false; // "This PC" itself is not a folder to open
            return button;
        }
        static FrameworkElement PathArrow() {
            var arrow=UI.Text("›",13,true);arrow.Margin=new Thickness(2,0,2,0);arrow.VerticalAlignment=VerticalAlignment.Center;
            return arrow;
        }
        void LayoutPath() {
            pathRow.Children.Clear();
            if(pathParts.Count==0)return;
            var up=UI.Btn("",()=>GoUp());up.Content=LineIcon("M 9,15 L 9,3.5 M 4,8 L 9,3 L 14,8");
            up.Padding=new Thickness(5,2,5,2);up.MinHeight=24;up.Margin=new Thickness(0,0,8,0);up.VerticalAlignment=VerticalAlignment.Center;
            up.ToolTip=L.T("Up one folder")+" (Backspace)";AutomationProperties.SetName(up,L.T("Up one folder"));
            up.IsEnabled=trail.Count>1;
            var infinite=new Size(double.PositiveInfinity,double.PositiveInfinity);
            var parts=pathParts.Select((p,i)=>PathPart(p,i==pathParts.Count-1)).ToList();
            var arrows=pathParts.Select(p=>PathArrow()).ToList();
            up.Measure(infinite);foreach(var e in parts.Concat(arrows))e.Measure(infinite);
            double room=pathRow.ActualWidth>0?pathRow.ActualWidth-4:double.PositiveInfinity;
            var more=new Button {Content="«",Padding=new Thickness(6,2,6,2),MinHeight=24,BorderThickness=new Thickness(0),
                Background=Brushes.Transparent,VerticalAlignment=VerticalAlignment.Center,FontSize=13,ToolTip=L.T("Show the folders above")};
            UI.Use(more,Control.ForegroundProperty,"Subtle");more.Measure(infinite);
            int hidden=0;
            for(;hidden<pathParts.Count-1;hidden++) {
                double width=up.DesiredSize.Width+(hidden>0?more.DesiredSize.Width+arrows[0].DesiredSize.Width:0);
                for(int i=hidden;i<parts.Count;i++)width+=parts[i].DesiredSize.Width+(i>hidden?arrows[i].DesiredSize.Width:0);
                if(width<=room)break;
            }
            pathRow.Children.Add(up);
            if(hidden>0) {
                // The folded parts, nearest first, like File Explorer's « list.
                var menu=new ContextMenu {PlacementTarget=more,Placement=PlacementMode.Bottom};
                for(int i=hidden-1;i>=0;i--) {
                    var part=pathParts[i];var item=new MenuItem {Header=part.Item1,IsEnabled=part.Item2!=null};
                    if(part.Item2!=null){var target=part.Item2;item.Click+=delegate {Select(target);};}
                    menu.Items.Add(item);
                }
                more.Click+=delegate {menu.IsOpen=true;};
                pathRow.Children.Add(more);pathRow.Children.Add(arrows[0]);
            }
            for(int i=hidden;i<parts.Count;i++) {
                if(i>hidden)pathRow.Children.Add(arrows[i]);
                pathRow.Children.Add(parts[i]);
            }
        }
        const string TabDragFormat="LoveMyShelf.FolderTab";
        // Folder tabs, in the order the person arranged. Rebuilding them never touches the grid.
        void BuildTabs() {
            tabs.Children.Clear();
            var albums=store.VisibleAlbums();
            for(int position=0;position<albums.Count;position++) {
                Album item=albums[position];int place=position;
                bool selected=trail.Count>0 && trail[0].Folder==item.Folder;
                var button=UI.Btn(item.Name,()=>Select(item)); button.Margin=new Thickness(0,0,5,0);
                button.Tag=item;
                if(item.Source!=null) {
                    button.ToolTip=item.Source;
                    var hide=new MenuItem {Header=L.T("Remove from LoveMyShelf")};
                    hide.Click+=delegate {
                        try{
                            store.HideFolder(item);
                            if(trail.Count>0 && trail[0].Folder==item.Folder)Select(allCrops);
                            else {
                                tabs.Children.Remove(button);
                                // The folders inside it may leave too, unless another added folder still holds them.
                                if(ShowingUnfinished || ShowingScope)Refresh();
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
            if(updateTopBar!=null)updateTopBar();
        }
        // The ▾ button's list: both collections, then every folder tab, with the one on screen ticked.
        internal ContextMenu OpenFolderList() {
            var menu=new ContextMenu {PlacementTarget=folderList,Placement=PlacementMode.Bottom};
            Action<Album> add=album=>{
                var item=new MenuItem {Header=album.Name,IsCheckable=false,IsChecked=active!=null && active.Folder==album.Folder};
                if(album.Source!=null)item.ToolTip=album.Source;
                item.Click+=delegate {Select(album);};
                menu.Items.Add(item);
            };
            add(allCrops);add(unfinished);menu.Items.Add(new Separator());
            foreach(var album in store.VisibleAlbums())add(album);
            menu.IsOpen=true;
            return menu;
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
            bool changedFolders=settings.ShowSubfolders!=updated.ShowSubfolders;
            bool changedNames=settings.TileNames!=updated.TileNames;
            bool changedTimes=settings.CoverTimes!=updated.CoverTimes;
            bool changedShape=settings.TileShape!=updated.TileShape || settings.WideCovers!=updated.WideCovers;
            bool changedLayout=changedShape || settings.FillWideGaps!=updated.FillWideGaps;
            bool changedExplorer=settings.ExplorerMenu!=updated.ExplorerMenu;
            updated.LanguageChosen=true;
            try {updated.Save(store.Root);} catch(Exception ex) {UI.Error(this,ex);return false;}
            settings=updated;MotionWindow.UseDefaults(settings);ViewerKeys.Use(settings);
            if(changedExplorer || (updated.ExplorerMenu && changedLanguage)) {
                try {ExplorerOpen.Sync(updated);} catch(Exception ex) {UI.Error(this,ex);}
            }
            // Every open window shows names the same way and remembers where frames are saved.
            foreach(var other in Application.Current.Windows.OfType<GalleryWindow>().Where(w=>w!=this)) {
                other.settings.CaptureDirectory=updated.CaptureDirectory;other.settings.CaptureAsShown=updated.CaptureAsShown;other.settings.CaptureJpg=updated.CaptureJpg;
                other.settings.MonitorSpeakers=new Dictionary<string,SpeakerChoice>(updated.MonitorSpeakers,StringComparer.OrdinalIgnoreCase);
                other.settings.SubtitleScale=updated.SubtitleScale;other.settings.GifAutoMake=updated.GifAutoMake;other.settings.GifMakeDelayMs=updated.GifMakeDelayMs;
                other.settings.WebpEffort=updated.WebpEffort;other.settings.UpscaleGpu=updated.UpscaleGpu;
                other.settings.Shortcuts=new Dictionary<string,System.Windows.Input.Key>(updated.Shortcuts,StringComparer.Ordinal);
                if(changedNames)other.settings.TileNames=updated.TileNames;
                if(changedTimes){other.settings.CoverTimes=updated.CoverTimes;other.CoverMarksSoon();}
                if(changedLayout) {
                    other.settings.TileShape=updated.TileShape;other.settings.WideCovers=updated.WideCovers;
                    other.settings.FillWideGaps=updated.FillWideGaps;
                }
                if(changedNames || changedLayout)other.LayoutRows();
            }
            if(changedNames || changedLayout)LayoutRows();
            if(changedShape){var shapeHandler=TileShapeChanged;if(shapeHandler!=null)shapeHandler();}
            if(changedTimes)CoverMarksSoon();
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
            else if(changedFolders)Refresh();
            if(folderPane!=null){PlaceFolderPane();folderPane.Fill();}
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
        CancellationTokenSource collectionScan;
        List<Photo> scopePhotos;
        async void Refresh(bool reuseScope=false) {
            if(reuseScope && ShowingScope && scopePhotos==null && collectionScan!=null)return;
            int version=++refreshVersion;
            if(collectionScan!=null){collectionScan.Cancel();collectionScan=null;}
            if(!reuseScope || !ShowingScope)scopePhotos=null;
            int skipped=0; folderUnavailable=false;
            var folders=new List<Tuple<string,int>>();
            try {
                if(ShowingSimilar) {
                    canShuffle=false;
                    if(similarShown==null){photos=new List<Photo>();subfolders=new List<Tuple<string,int>>();LayoutRows();} // not the last place's tiles meanwhile
                    var built=await BuildSimilar(search.Text.Trim());
                    if(version!=refreshVersion) return;
                    similarShown=built;
                    photos=PadSimilar(Math.Max(2,columns));
                } else if(ShowingScope && scopePhotos!=null) {
                    skipped=skippedCount;photos=FilterScopePhotos();
                } else if(ShowingAllCrops || ShowingUnfinished || ShowingTag || ShowingScope) {
                    bool pending=ShowingUnfinished;
                    string scope=ScopeFolder;
                    var tag=ShowingTag?ShownTags:null;
                    photos=new List<Photo>(); subfolders=new List<Tuple<string,int>>();LayoutRows();
                    canShuffle=false;
                    emptyTitle.Text=L.T(tag!=null || scope!=null?"Loading…":"Loading covers…");emptyHelp.Text="";empty.Visibility=Visibility.Visible;
                    emptyAction.Visibility=Visibility.Collapsed;
                    count.Text="";count.ToolTip=null;
                    var sort=settings.Sort;
                    bool descending=settings.Descending;
                    // Reading every folder inside can take a while: the count keeps moving, and leaving stops it.
                    var scan=pending || scope!=null?CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token):null;
                    if(scan!=null)collectionScan=scan;
                    var scanToken=scan==null?CancellationToken.None:scan.Token;
                    Action<int> foundSoFar=n=>Dispatcher.BeginInvoke(new Action(()=>{
                        if(version==refreshVersion && collectionScan==scan)
                            emptyHelp.Text=L.F(scope!=null?"{0:N0} files found. Still looking.":"{0:N0} files without a cover found. Still looking.",n);
                    }));
                    List<Photo> saved;
                    try {
                        if(pending && UncoveredRangeNow==UncoveredRange.Computer)
                            saved=await UncoveredOnComputer(scanToken,sort,descending,foundSoFar,n=>skipped=n);
                        else saved=await Task.Run(()=>{
                            if(scope!=null) {
                                var gathered=store.FolderTreePhotos(scope,out skipped,scanToken,foundSoFar);
                                scanToken.ThrowIfCancellationRequested();
                                return GalleryStore.SortCoverFamilies(gathered,sort,descending);
                            }
                            if(pending)return GalleryStore.SortPhotos(store.UnfinishedCovers(out skipped,scanToken,foundSoFar),sort,descending);
                            return tag!=null?store.Tagged(tag,out skipped,sort,descending):store.AllCrops(out skipped,sort,descending);
                        },scanToken);
                    } finally {
                        if(scan!=null) {
                            if(collectionScan==scan)collectionScan=null;
                            scan.Dispose();
                        }
                    }
                    if(version!=refreshVersion) return;
                    if(scope!=null){scopePhotos=saved;photos=FilterScopePhotos();}
                    else photos=WithoutPending(saved);
                } else {
                    // A folder with thousands of files is read off the UI thread so the window never freezes.
                    var album=active;var sort=settings.Sort;bool descending=settings.Descending;int filterNow=folderFilter;
                    bool withFolders=true; // the folders inside always show, like File Explorer (their area can be folded or put below the photos)
                    var reading=Task.Run(()=>{
                        int unreadable;
                        var list=store.Photos(album,out unreadable,sort,descending);
                        if(filterNow>0)list=list.Where(p=>p.HasCrop==(filterNow==2)).ToList();
                        var inside=new List<Tuple<string,int>>();
                        if(withFolders && album.Source!=null)
                            try {inside=GalleryStore.Subfolders(album.Source,sort,descending);} catch(IOException) {} catch(UnauthorizedAccessException) {}
                        return Tuple.Create(list,unreadable,inside);
                    });
                    // Opening another folder clears the old tiles if reading takes a moment; F5 and search keep them until the new list is ready.
                    if(await Task.WhenAny(reading,Task.Delay(150))!=reading && version==refreshVersion && shownAlbum!=album.Folder) {
                        photos=new List<Photo>();subfolders=new List<Tuple<string,int>>();LayoutRows();
                        emptyTitle.Text=L.T("Loading…");emptyHelp.Text="";empty.Visibility=Visibility.Visible;
                        emptyAction.Visibility=Visibility.Collapsed;
                        count.Text="";count.ToolTip=null;
                    }
                    var read=await reading;
                    if(version!=refreshVersion) return;
                    photos=WithoutPending(read.Item1);skipped=read.Item2;folders=read.Item3;
                }
                shownAlbum=active.Folder;
            }
            catch(OperationCanceledException) {return;}
            catch(IOException) { if(version!=refreshVersion)return; folderUnavailable=true; photos=new List<Photo>(); }
            catch(UnauthorizedAccessException) { if(version!=refreshVersion)return; folderUnavailable=true; photos=new List<Photo>(); }
            string query=search.Text.Trim();
            // The search box finds file names and tags alike; "#word" looks at tags only.
            if(query.Length>0 && !ShowingSimilar) {
                bool tagsOnly=query.StartsWith("#");
                string word=query.TrimStart('#').Trim();
                bool anyTags=Tags.Any;
                photos=photos.Where(p=>p!=null && ((!tagsOnly && p.Name.IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0) ||
                    (anyTags && word.Length>0 && p.Linked && Tags.Of(p).Any(t=>t.IndexOf(word,StringComparison.OrdinalIgnoreCase)>=0)))).ToList();
            }
            if(query.Length>0)folders=folders.Where(f=>Path.GetFileName(f.Item1).IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0).ToList();
            subfolders=folders;
            ReadFormatSource();
            ClearSelection();
            skippedCount=skipped;
            var reflow=pendingReflow;pendingReflow=null;
            ShowPhotos(reflow);
            ShowHiddenFormatNotice();
        }
        List<Photo> FilterScopePhotos() {
            var list=WithoutPending(scopePhotos);
            return folderFilter==0?list.ToList():list.Where(p=>p.HasCrop==(folderFilter==2)).ToList();
        }
        void ShowPhotos(Reflow reflow=null) {
            UpdateCoverMarks();
            SetCount(skippedCount);
            // No filter and no search, and still nothing: this folder really has no photos or videos.
            bool noFiles=photos.Count==0 && !FilteringFormats && !folderUnavailable && active.Source!=null && folderFilter==0 && search.Text.Trim().Length==0;
            folderPane.Show(subfolders,noFiles,active.Folder);
            ShowEmptyNote();
            if(reflow!=null)ApplyReflow(reflow);else LayoutRows();
        }
        // Nothing on screen because the folders here are folded: say so, so it never looks frozen.
        bool FoldersFoldedAway {get {return photos.Count==0 && subfolders.Count>0 && folderPane.IsFolded;}}
        internal void ShowEmptyNote() {
            emptyHelp.ClearValue(TextBlock.TextWrappingProperty);emptyHelp.ClearValue(TextBlock.TextAlignmentProperty);
            if(ShowingSimilar){SimilarEmptyNote();return;}
            if(FormatEmpty){ShowFormatEmpty();empty.Visibility=Visibility.Visible;return;}
            bool pending=ShowingPending,searching=search.Text.Trim().Length>0,foldedAway=FoldersFoldedAway;
            bool coversOnly=ShowingAllCrops || ((active.Source!=null || ShowingScope) && folderFilter==2);
            string source=active.Source??ScopeFolder;
            bool driveAway=folderUnavailable && source!=null && DriveLinks.DriveAway(source);
            emptyTitle.Text=driveAway?L.T("This folder's drive isn't connected"):folderUnavailable?L.T("This folder could not be opened"):
                L.T(searching?"No search results":ShowingScope && scopePhotos!=null && scopePhotos.Count==0?"No photos or videos in this folder or its subfolders.":ShowingTag?"No files with this tag can be shown":pending?"Every file has a cover":coversOnly?"No covers yet":"No photos or videos in this folder");
            emptyHelp.Text=driveAway && !ShowingScope?L.T("Connect the drive again and this folder comes back by itself, together with its covers. It's fine even if the drive letter changes."):
                folderUnavailable?L.T("Check that the folder is still in its place, then press F5. If you unplugged a phone or a USB drive, connect it again."):
                L.T(searching?"Search with another name, or clear the search box.":
                    ShowingTag?"Files on a drive that isn't connected come back when the drive is connected again. To put this tag on a file, right-click the file and click [Tags…].":
                    ShowingUnfinished && UncoveredRangeNow==UncoveredRange.Computer?"Photos and videos on this computer that have no cover yet gather here.":
                    ShowingUnfinished?"Photos and videos that have no cover yet, from the folders you added to LoveMyShelf and their subfolders, gather here.":
                    ShowingScope?(folderFilter!=0 && scopePhotos!=null && scopePhotos.Count>0?"Click [All] at the bottom left to see everything.":""):
                    pending?"Click [All] at the bottom left to see everything.":
                    coversOnly?"Open a folder and make a cover, and it gathers here.":"Choose another folder, or put photos or videos in this folder and press F5.");
            // Nothing added yet: point to the folder tree, where folders are added (and open it).
            bool noFolders=!ShowingScope && store.VisibleAlbums().Count==0;
            if(foldedAway) {
                emptyHelp.Text=L.F("There are {0:N0} folders inside, but they are folded right now.",subfolders.Count);
                emptyAction.Content=L.F("Show folders ({0:N0})",subfolders.Count);
            } else {
                emptyAction.Content=L.T("Select a folder…");
                if(noFolders && !folderUnavailable) {
                    emptyTitle.Text=L.T("Add a folder to LoveMyShelf");
                    emptyHelp.Text=L.T("In the folder tree on the left, right-click a folder and click [Add to LoveMyShelf]. You can also drag a folder from File Explorer onto this window.");
                    if(treePanel.Visibility!=Visibility.Visible)ShowTree(true,false);
                }
            }
            emptyAction.Visibility=noFolders||foldedAway?Visibility.Visible:Visibility.Collapsed;
            empty.Visibility=photos.Count==0 && (subfolders.Count==0||foldedAway)?Visibility.Visible:Visibility.Collapsed;
        }
        void SetCount(int skipped) {
            if(ShowingSimilar){SimilarCount();updateFooter();return;}
            // Counts are of originals: an original's extra cover tiles are not counted again.
            int total=photos.Count(p=>p!=null && !p.IsExtra);
            int covers=photos.Count(p=>p!=null && !p.IsExtra && p.HasCrop);
            canShuffle=total>1 || photos.Contains(null) || (ShowingPending && covers>0);
            count.ToolTip=null;
            if(ShowingAllCrops)count.Text=L.F("{0:N0} covers",covers);
            else if(ShowingPending)count.Text=L.F("{0:N0} unfinished",total-covers);
            else if(folderFilter==2)count.Text=L.F("{0:N0} finished",covers);
            // While types are picked, "16 of 11" already says how many show, so "All 11" is not said again.
            else if(FilteringFormats)count.Text=FormatCountPrefix(total)+L.F("{0:N0} finished",covers);
            else count.Text=L.F("{1:N0} of {0:N0} finished",total,covers);
            if(skipped>0)count.Text+="   ·   "+L.F("{0:N0} unreadable",skipped);
            if(FilteringFormats && (ShowingAllCrops || ShowingPending || folderFilter==2))count.Text=FormatCountPrefix(total)+count.Text;
            updateFooter();
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
                firstTile=Math.Max(0,(int)Math.Floor(scroller.VerticalOffset/rowHeight)-folderRows)*columns;
            // Wide covers make empty slots in the list (see PadWide), and those move when the cells change. The places
            // kept across the new layout are remembered by the tile, not by the number of its slot.
            Photo firstPhoto=TileAtOrAfter(laidOut,firstTile),overridePhoto=TileAtOrAfter(photos,firstTileOverride);
            Photo focusPhoto=focusIndex>=0 && focusIndex<photos.Count?photos[focusIndex]:null;
            Photo anchorPhoto=anchorIndex>=0 && anchorIndex<photos.Count?photos[anchorIndex]:null;
            scrollToTop=false;
            double width=Math.Max(200,grid.ActualWidth-SystemParameters.VerticalScrollBarWidth-2);
            // The cells have the shape picked in the options (9:16, 3:4 or 1:1). Wider cells are made a little wider too,
            // so a tile keeps about the same size on screen whatever its shape.
            double shape=Covers.Shape(settings.TileShape);
            columns=Math.Max(2,(int)Math.Floor(width/(density*Math.Sqrt(shape/Covers.Tall))));
            TileNameHeight=NameStripHeight(width/columns);
            double height=(width/columns-1)/shape+1+TileNameHeight; // the picture keeps the cell's shape; the name strip is added below it
            rowHeight=height;cellWidth=width/columns;
            if(ShowingSimilar && similarShown!=null){photos=PadSimilar(columns);wideSlots=new HashSet<int>();} // each group starts a row of its own
            else photos=PadWide(PhotosInSourceOrder,shape);
            // Found again by record (F5 reads new Photo objects for the same files).
            if(firstTile>=0)firstTile=firstPhoto==null?firstTile:Math.Max(0,IndexOfRecord(firstPhoto));
            if(firstTileOverride>=0 && overridePhoto!=null)firstTileOverride=Math.Max(0,IndexOfRecord(overridePhoto));
            if(focusIndex>=0)focusIndex=focusPhoto==null?-1:IndexOfRecord(focusPhoto);
            if(anchorIndex>=0)anchorIndex=anchorPhoto==null?-1:IndexOfRecord(anchorPhoto);
            laidOut=new List<Photo>(photos);
            rows=new List<PhotoRow>();
            // Folder tiles come first, in rows of their own; the file rows follow (their numbers count the folder rows).
            // Three folder cards are stacked in each cell, so a row holds three times as many folders as tiles.
            // Folders now have their own area (FolderPane) above or below the photos; the photo grid has none.
            var gridFolders=new List<Tuple<string,int>>();
            int perRow=columns*FoldersPerCell;
            folderRows=(gridFolders.Count+perRow-1)/perRow;
            for(int i=0;i<gridFolders.Count;i+=perRow)
                rows.Add(new PhotoRow {Owner=this,Columns=columns,Height=height,Number=i/perRow,Photos=new List<Photo>(),
                    Folders=gridFolders.GetRange(i,Math.Min(gridFolders.Count-i,perRow))});
            for(int i=0;i<photos.Count;i+=columns) rows.Add(MakeRow(i,height));
            if(reflow!=null && scroller!=null) {
                // Keep the same tile in view (to the pixel), build the rows at once, then let the tiles slide.
                double target=firstTileOverride>=0?(Math.Floor(firstTileOverride/(double)columns)+folderRows)*height+reflow.Fraction:0;
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
                double offset=(Math.Floor(firstTile/(double)columns)+folderRows)*height;
                Dispatcher.BeginInvoke(new Action(()=>scroller.ScrollToVerticalOffset(offset)),DispatcherPriority.Loaded);
            }
        }
        // ---------- File names under the tiles ----------
        internal const double TileNameFont=11.5,TileNameLine=16,TileNamePadding=5;
        const int MostNameLines=12; // one very long name must not make every row a screen tall
        internal double TileNameHeight {get;private set;}
        internal double CellWidth {get {return cellWidth;}}
        bool wholeNamesFit;
        double NameStripHeight(double cell) {
            wholeNamesFit=false;
            int mode=settings.TileNames;
            if(mode<=0)return 0;
            int lines=mode<3?mode:LongestNameLines(cell);
            return lines*TileNameLine+TileNamePadding;
        }
        // Rows must all be the same height, so "whole name" makes room for the longest name on the list.
        int LongestNameLines(double cell) {
            var probe=new TextBlock {FontSize=TileNameFont,FontFamily=FontFamily,LineHeight=TileNameLine,
                LineStackingStrategy=LineStackingStrategy.BlockLineHeight,TextWrapping=TextWrapping.Wrap};
            double room=Math.Max(20,cell-1-6);
            int most=1;
            // The longest names are the ones that need the most lines; measuring a few hundred stays instant in huge folders.
            foreach(string name in photos.Where(p=>p!=null).Select(p=>p.Name).OrderByDescending(n=>n.Length).Take(200)) {
                probe.Text=NoticeLook.KeepWords(name);
                probe.Measure(new Size(room,Double.PositiveInfinity));
                most=Math.Max(most,(int)Math.Ceiling(probe.DesiredSize.Height/TileNameLine-0.01));
            }
            wholeNamesFit=most<=MostNameLines;
            return Math.Min(most,MostNameLines);
        }
        // ---------- External drives coming and going ----------
        static DispatcherTimer driveTimer;
        static bool relinking,driveEvent;
        void WatchDrives() {
            SourceInitialized+=delegate {
                var source=System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle);
                if(source!=null)source.AddHook(DeviceMessage);
            };
        }
        static IntPtr DeviceMessage(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam,ref bool handled) {
            const int DeviceChange=0x0219,Arrived=0x8000,Removed=0x8004;
            if(message==DeviceChange) {
                long kind=wParam.ToInt64();
                if(kind==Arrived || kind==Removed){driveEvent=true;DrivesChangedSoon(1.5);} // Windows needs a moment to finish mounting
            }
            return IntPtr.Zero;
        }
        static void DrivesChangedSoon(double seconds) {
            if(driveTimer==null) {
                driveTimer=new DispatcherTimer();
                driveTimer.Tick+=delegate {driveTimer.Stop();RelinkDrives();};
            }
            driveTimer.Stop();driveTimer.Interval=TimeSpan.FromSeconds(seconds);driveTimer.Start();
        }
        // Records must stay put while something is working on them.
        static bool RecordsInUse() {
            var windows=Application.Current.Windows.OfType<Window>().ToList();
            return renamingFolder || TransferQueue.Running || windows.OfType<GalleryWindow>().Any(w=>w.Upscales.IsBusy || w.migratingCovers || w.pendingFolders.Count>0) ||
                windows.OfType<UpscaleToast>().Any(t=>t.IsWorking) ||
                windows.Any(w=>w.IsVisible && !(w is GalleryWindow) && !(w is UpscaleToast) && !(w is VideoOverlay) && !(w is GuideWindow) && !(w is TransferWindow));
        }
        static async void RelinkDrives() {
            if(relinking || Application.Current==null){if(relinking)DrivesChangedSoon(3);return;}
            var galleries=Application.Current.Windows.OfType<GalleryWindow>().ToList();
            if(galleries.Count==0)return;
            if(RecordsInUse()){DrivesChangedSoon(3);return;} // tried again once the video or editor is closed
            bool device=driveEvent;driveEvent=false;
            var records=galleries[0].store;
            relinking=true;
            try {
                var result=await Task.Run(()=>DriveLinks.Run(records,DriveLayout.Current(),CancellationToken.None));
                galleries=Application.Current.Windows.OfType<GalleryWindow>().ToList();
                if(result.Changed || device)foreach(var gallery in galleries)gallery.DrivesChanged(result);
                if(result.Records+result.Files>0 && galleries.Count>0)
                    galleries[0].Notify(L.T("Covers reconnected"),L.F("The drive letter or a file's location changed, so {0:N0} covers were reconnected. Your photo and video files were not touched at all.",Math.Max(result.Records,result.Files)),null,NoticeKind.Done,false,6);
            } catch(Exception) {/* The records stay as they were and the next drive change tries again. */}
            finally {relinking=false;}
        }
        void DrivesChanged(RelinkResult result) {
            if(lifetime.IsCancellationRequested)return;
            if(result.CoverDirectory!=null)settings.CoverDirectory=result.CoverDirectory;
            Func<Album,Album> follow=a=>{
                Tuple<string,string> moved;
                return a!=null && a.Source!=null && result.Albums.TryGetValue(a.Folder,out moved)?new Album(moved.Item1,a.Name,moved.Item2):a;
            };
            var sources=result.Albums.Where(p=>File.Exists(Path.Combine(p.Value.Item1,"source.txt"))).ToList();
            foreach(var stack in new[]{backStack,forwardStack})
                for(int i=0;i<stack.Count;i++) {
                    if(stack[i].StartsWith("@"))continue;
                    string recordFolder=store.AlbumFolderOf(stack[i]);
                    var hit=sources.FirstOrDefault(p=>String.Equals(p.Key,recordFolder,StringComparison.OrdinalIgnoreCase));
                    if(hit.Value!=null)stack[i]=hit.Value.Item2;
                }
            if(active!=null && active.Source!=null && result.Albums.ContainsKey(active.Folder)) {
                int view;bool hadView=folderFilters.TryGetValue(active.Folder,out view);
                for(int i=0;i<trail.Count;i++)trail[i]=follow(trail[i]);
                active=follow(active);
                if(hadView)folderFilters[active.Folder]=view;
                BuildPath();
            }
            if(result.Albums.Count>0)BuildTabs();
            if(tree!=null && treePanel.Visibility==Visibility.Visible) {
                if(result.Albums.Count>0){tree.Build();TreeSyncSoon();}
                else tree.RefreshDrives();
            }
            ReloadKeepingPlace();
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
            // Whole originals are shuffled: their covers stay together, cover 1 on the left.
            var ordered=PhotosInSourceOrder.Where(p=>p!=null).GroupBy(p=>GalleryStore.CoverFamily(p),StringComparer.OrdinalIgnoreCase)
                .Select(g=>GalleryStore.InCoverOrder(g)).Where(f=>!pending || f.Any(p=>!p.IsExtra && !p.HasCrop)).ToList();
            if(ordered.Count<2 && ordered.Sum(f=>f.Count)==photos.Count(p=>p!=null))return;
            bool changed=false;
            for(int i=ordered.Count-1;i>0;i--) {
                int j=random.Next(i+1);
                if(j==i) continue;
                var item=ordered[i]; ordered[i]=ordered[j]; ordered[j]=item; changed=true;
            }
            if(!changed) ordered.Reverse();
            shuffledOrder=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
            for(int i=0;i<ordered.Count;i++) shuffledOrder[GalleryStore.CoverFamily(ordered[i][0])]=i;
            photos=ordered.SelectMany(f=>f).ToList();
            scrollToTop=true;ShowPhotos();
            if(rows.Count>0)grid.ScrollIntoView(rows[0]);
        }
        internal static string SortName(PhotoSort sort) {
            return L.T(sort==PhotoSort.Modified?"Date modified":sort==PhotoSort.Size?"File size":sort==PhotoSort.Type?"File type":
                sort==PhotoSort.CoverOrder?"Cover creation order":"File name");
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
        // ---------- The shape of the grid's cells ----------
        // Told to open cover editors, so their "How it shows in your grid" follows a change.
        internal static event Action TileShapeChanged;
        // Picks the cells' shape for every folder window (saved in the options). Covers stay as they are; only the cells change.
        internal static void ChangeTileShape(int shape) {ChangeGrid(p=>p.TileShape=shape);}
        internal static void ChangeWideCovers(bool on) {ChangeGrid(p=>p.WideCovers=on);}
        // The cover editor's [Keep this shape], kept for next time (nothing in the grid changes).
        internal static void RememberCropLock(bool on) {RememberEditor(p=>p.CropLock=on);}
        internal static void RememberCropZoom(bool on) {RememberEditor(p=>p.CropZoom=on);}
        internal static void RememberGifStripHeight(int height) {RememberEditor(p=>p.GifStripHeight=height);}
        internal static void RememberCropShape(double shape) {
            if(shape>=Covers.Narrowest && shape<=Covers.Widest && !Double.IsNaN(shape))RememberEditor(p=>p.CropShape=shape);
        }
        static void RememberEditor(Action<Preferences> change) {
            var windows=Application.Current.Windows.OfType<GalleryWindow>().ToList();
            if(windows.Count==0)return;
            var updated=windows[0].settings.Copy();change(updated);
            try {updated.Save(windows[0].store.Root);} catch(Exception quiet) {ErrorLog.Quiet(quiet);return;}
            foreach(var window in windows) {if(window==windows[0])window.settings=updated;else change(window.settings);}
        }
        static void ChangeGrid(Action<Preferences> change) {
            var windows=Application.Current.Windows.OfType<GalleryWindow>().ToList();
            if(windows.Count==0)return;
            var first=windows[0];
            var updated=first.settings.Copy();change(updated);
            try {updated.Save(first.store.Root);} catch(Exception ex) {UI.Error(first,ex);return;}
            foreach(var window in windows) {
                if(window==first)window.settings=updated;else change(window.settings);
                window.LayoutRows();window.SettingsChanged();
            }
            var handler=TileShapeChanged;if(handler!=null)handler();
        }
        // Lets an open Guide & Options window show the new value.
        internal event Action SettingsChangedEvent;
        void SettingsChanged() {updateFooter();var handler=SettingsChangedEvent;if(handler!=null)handler();}
        PhotoRow MakeRow(int start,double height) {
            var row=new PhotoRow { Owner=this,Columns=columns,Height=height,Number=folderRows+start/columns,Photos=photos.GetRange(start,Math.Min(photos.Count-start,columns)) };
            row.Wide=new bool[row.Photos.Count];
            for(int k=0;k<row.Photos.Count;k++)row.Wide[k]=wideSlots.Contains(start+k);
            return row;
        }
        // ---------- Wide covers taking two cells ----------
        // The slots that hold a wide tile; the slot after each is left empty for it.
        HashSet<int> wideSlots=new HashSet<int>();
        // A cover clearly wider than a cell takes two cells (an option). Files without a cover keep one cell.
        bool TakesTwoCells(Photo photo,double shape) {
            return photo!=null && settings.WideCovers && !ShowingSimilar && columns>=3 && photo.ShowsCover && Covers.TakesTwoCells(photo.Aspect,shape);
        }
        // Keep the source order across redraws, resizes and option changes. Navigation uses the padded order.
        List<Photo> gridSource, paddedGrid;
        List<Photo> PhotosInSourceOrder {get {return photos==paddedGrid?gridSource:photos;}}
        List<Photo> PadWide(List<Photo> list,double shape) {
            gridSource=list.Where(p=>p!=null).ToList();
            paddedGrid=PadForTest(gridSource,columns,p=>TakesTwoCells(p,shape),settings.FillWideGaps,out wideSlots);
            return paddedGrid;
        }
        internal static List<Photo> PadForTest(List<Photo> list,int columns,Func<Photo,bool> wide,bool fill,out HashSet<int> wideSlots) {
            wideSlots=new HashSet<int>();
            if(columns<3 || !list.Any(p=>p!=null && wide(p)))return list;
            var padded=new List<Photo>(list.Count+16);
            Dictionary<string,int> familyCounts=null;
            for(int i=0;i<list.Count;i++) {
                var photo=list[i];
                bool isWide=photo!=null && wide(photo);
                if(isWide && padded.Count%columns==columns-1) {
                    Photo next=fill && i+1<list.Count?list[i+1]:null;
                    Photo filler=null;
                    if(next!=null && !wide(next)) {
                        if(familyCounts==null)familyCounts=list.Where(p=>p!=null)
                            .GroupBy(GalleryStore.CoverFamily,StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(g=>g.Key,g=>g.Count(),StringComparer.OrdinalIgnoreCase);
                        var previous=i>0?list[i-1]:null;
                        bool splitsFamily=previous!=null && String.Equals(GalleryStore.CoverFamily(previous),
                            GalleryStore.CoverFamily(photo),StringComparison.OrdinalIgnoreCase);
                        if(!splitsFamily && familyCounts[GalleryStore.CoverFamily(next)]==1) {
                            filler=next;
                            i++;
                        }
                    }
                    padded.Add(filler);
                }
                if(isWide)wideSlots.Add(padded.Count);
                padded.Add(photo);
                if(isWide)padded.Add(null);
            }
            return padded;
        }
        // The empty slot right after a wide tile (it belongs to that tile).
        bool IsWideTail(int index) {return index>0 && wideSlots.Contains(index-1);}
        // The list as the rows on screen were last laid out (with its empty slots), so a scroll place can be read back.
        List<Photo> laidOut=new List<Photo>();
        static Photo TileAtOrAfter(List<Photo> list,int index) {
            if(index<0)return null;
            for(int i=index;i<list.Count;i++)if(list[i]!=null)return list[i];
            return null;
        }
        int IndexOfRecord(Photo photo) {
            int at=photos.IndexOf(photo);
            return at>=0?at:photos.FindIndex(p=>p!=null && String.Equals(p.Folder,photo.Folder,StringComparison.OrdinalIgnoreCase));
        }
        // One cover at a time: asking for another brings the open one forward.
        internal bool CoverWorkFree(Window from) {
            if(CoverWork.CanStart(from))return true;
            CoverWork.Front();
            Notify(L.T("You’re already making a cover"),L.T("You can make one cover at a time. Please finish the cover that’s open first."),null,NoticeKind.Info);
            return false;
        }
        void TellFileInCoverWork() {
            CoverWork.Front();
            Notify(L.T("This file is being used for a cover"),L.T("Save or close the cover first, then try again."),null,NoticeKind.Info);
        }
        // Leaves out the original the open cover is being made from, and says so.
        List<Photo> WithoutCoverWork(IEnumerable<Photo> targets) {
            var all=targets.ToList();
            var free=all.Where(p=>!CoverWork.Holds(p)).ToList();
            if(free.Count<all.Count)TellFileInCoverWork();
            return free;
        }
        internal bool EditFrames(Viewer playback) {
            if(!CoverWorkFree(playback))return false;
            FinishPendingCovers();
            if(!EnsureCoverFolder(playback))return false;
            Photo photo=playback.CurrentPhoto;
            string path=photo.Original;
            try {
                var picker=CreateFramePicker(playback,path,photo,playback.CropNearby);
                bool? saved=CoverWork.Show(picker,photo,this,path);
                if(picker.OpenFullList) {
                    picker=CreateFramePicker(playback,path,photo,false);
                    saved=CoverWork.Show(picker,photo,this,path);
                }
                if(!String.IsNullOrEmpty(picker.SavedEditedPath)) {
                    if(playback.ForCrop && playback.IsVisible)playback.Close();
                    RevealSavedFile(picker.SavedEditedPath);
                }
                return saved==true;
            } catch(Exception ex){if(!lifetime.IsCancellationRequested)UI.Error(playback,ex);return false;}
        }
        FramePicker CreateFramePicker(Viewer playback,string path,Photo photo,bool nearby) {
            var picker=new FramePicker(playback,path,photo,playback.CropFrame,playback.CropTime,
                settings.SnapEdges,settings.WheelZoomPercent,nearby,FullScreen.IsFull(this),Preferences.EditorPreviewSide);
            picker.Brightness=playback.PlaybackBrightness;picker.BrightnessChosen=playback.BrightnessChosen;picker.SkipSeconds=settings.SkipSeconds;picker.BrightnessStep=settings.BrightnessStep;
            picker.Framing=playback.CurrentFraming;picker.Area=playback.CoverArea;
            var target=AddTarget(photo,path);
            // While watching, the video stays open behind the editor and is simply there again after saving.
            // A video opened only to make a cover closes and shows the new cover instead.
            bool staysOpen=!playback.ForCrop;
            picker.CommitCover=async editor=>{
                var updated=await SaveCrop(target,editor,editor.FrameIndex,null,true,!staysOpen);
                if(staysOpen)playback.CoverSaved(updated);
            };
            if(!staysOpen)picker.AfterCommit=()=>OpenSavedOver(playback,playback);
            picker.CommitMotion=async (editor,request)=>{
                var updated=await SaveCrop(target,editor,editor.FrameIndex,null,false,!staysOpen);
                if(staysOpen)playback.CoverSaved(updated);
                MakeMotion(updated,request);
            };
            picker.SaveEditedCopy=editor=>SaveEditedJpgFromCrop(target,editor,editor.FrameIndex,null,null);
            picker.SaveEditedMotion=(editor,request,tok,prog)=>SaveEditedMotionFromSource(
                new MotionSource {Path=path,Moment=MotionSource.MomentOf(path,editor.FrameTime,editor.FrameIndex),
                    Crop=editor.Surface.Fraction,Rotation=editor.Surface.Rotation,Brightness=editor.Surface.Brightness,
                    Adjust=editor.Surface.Adjust,Aspect=editor.Surface.Aspect},request,tok,prog);
            return picker;
        }
        // fresh: start with no framing, rotation or brightness of the photo's cover (it was cut from a different picture).
        CropWindow CreateCropEditor(Window owner,Photo photo,BitmapSource preview,bool fresh=false) {
            var editor=new CropWindow(owner,preview,photo.Name,photo.HasCrop && !fresh?(Rect?)photo.Crop:null,fresh?0:photo.Rotation,
                settings.SnapEdges,settings.WheelZoomPercent,brightness:fresh?0:photo.Brightness,drawings:fresh?null:photo.Drawings,
                adjust:fresh?AdjustValues.None:photo.Adjust,initialEraseAi:fresh?null:photo.LoadEraseRecord(false)) {BrightnessStep=settings.BrightnessStep};
            editor.SourceIdentityProvider=()=>EraseStore.SourceIdentity(photo.Original,editor.FrameIndex,editor.FrameTime,fresh?null:photo.PictureFile);
            return editor;
        }
        // A cover cut from a picture made somewhere else (a poster, a picture edited in another program).
        // It belongs to the original like any other cover; a copy of the picture is kept so the cover can be edited later.
        internal async void AddOutsideCover(Photo photo) {
            if(!photo.Linked || !CoverWorkFree(this))return;
            FinishPendingCovers();
            try {
                if(!EnsureCoverFolder(this))return;
                var pick=new Microsoft.Win32.OpenFileDialog {Title=L.T("Choose a picture to use as a cover"),CheckFileExists=true,
                    Filter=L.T("Pictures")+" (JPG, PNG, WebP, BMP)|*.jpg;*.jpeg;*.jfif;*.png;*.webp;*.bmp"};
                if(pick.ShowDialog(this)!=true)return;
                string picture=pick.FileName;
                var preview=await Task.Run(()=>Pictures.Load(picture,Preferences.EditorPreviewSide),lifetime.Token);
                if(lifetime.IsCancellationRequested || !CoverWorkFree(this))return;
                // The original may have been moved since its covers were made (Cover collection lists it at its old place).
                if(photo.HasCrop) {
                    photo.Original=await Task.Run(()=>OriginalLocator.Resolve(store,photo,lifetime.Token));
                    if(lifetime.IsCancellationRequested || !CoverWorkFree(this))return;
                }
                var dialog=new CropWindow(this,preview,photo.Name,null,0,settings.SnapEdges,settings.WheelZoomPercent) {BrightnessStep=settings.BrightnessStep};
                dialog.Title=L.T("Cover from a picture made elsewhere");
                dialog.SourceIdentityProvider=()=>EraseStore.SourceIdentity(photo.Original,0,null,picture);
                Photo target=OutsideTarget(photo);
                dialog.CommitCover=editor=>SaveCrop(target,editor,0,picture:picture);
                dialog.EnableSaveEditedCopy(editor=>SaveEditedJpgFromCrop(target,editor,0,null,picture));
                dialog.AfterCommit=()=>OpenSavedOver(null,dialog);
                CoverWork.Show(dialog,photo,this);
                if(!String.IsNullOrEmpty(dialog.SavedEditedPath))RevealSavedFile(dialog.SavedEditedPath);
                ShowJustSaved();
            } catch(OperationCanceledException){}
            catch(Exception ex){if(!lifetime.IsCancellationRequested)Notify(L.T("Couldn’t use this picture as a cover"),ex.Message,null,NoticeKind.Warning);}
        }
        // Where the cover from an outside picture is saved: the original's own record when it has no cover yet, a new record otherwise.
        Photo OutsideTarget(Photo photo) {
            if(!photo.HasCrop)return photo;
            string folder=Path.Combine(Path.GetDirectoryName(photo.Folder),GalleryStore.RecordBase(photo)+"-"+Guid.NewGuid().ToString("N").Substring(0,10));
            return new Photo {Folder=folder,Name=photo.Name,Original=photo.Original,Linked=true,Order=photo.Order};
        }
        // Moves a cover among the covers of its original. steps: -1 forward, 1 back, Int32.MinValue to the front, 0 back to the automatic order.
        internal void MoveCover(Photo photo,int steps) {
            if(CoverWork.Holds(photo)){TellFileInCoverWork();return;}
            FinishPendingCovers();
            try {
                if(steps==0)GalleryStore.ForgetArrangement(photo);
                else if(!GalleryStore.ArrangeCover(photo,steps))return;
                // Every window showing these covers puts them in the new order in place; nothing is read again from the folders.
                foreach(var window in Application.Current.Windows.OfType<GalleryWindow>())window.ApplyCoverOrder(photo);
                foreach(var viewer in Application.Current.Windows.OfType<Viewer>())viewer.CoverOrderChanged();
            } catch(Exception ex){Notify(L.T("Couldn’t change the cover order"),ex.Message,null,NoticeKind.Warning);}
        }
        void ApplyCoverOrder(Photo moved) {
            if(ShowingScope && Renamer.IsAt(moved.Original,ScopeFolder)){ReloadKeepingPlace();return;}
            if(RefreshHiddenCover(moved))return;
            if(rows==null || active==null || photos.Count==0)return;
            string family=GalleryStore.CoverFamily(moved);
            var slots=new List<int>();
            for(int i=0;i<photos.Count;i++)
                if(photos[i]!=null && String.Equals(GalleryStore.CoverFamily(photos[i]),family,StringComparison.OrdinalIgnoreCase))slots.Add(i);
            if(slots.Count<2)return;
            Dictionary<string,double?> ranks;
            try {ranks=GalleryStore.CoversOf(moved).ToDictionary(p=>p.Folder,p=>p.Rank,StringComparer.OrdinalIgnoreCase);}
            catch(Exception) {return;}
            var before=slots.Select(i=>photos[i]).ToList();
            foreach(var p in before){double? rank;p.Rank=ranks.TryGetValue(p.Folder,out rank)?rank:null;}
            var ordered=GalleryStore.InCoverOrder(before);
            if(ordered.SequenceEqual(before)){CoverMarksSoon();return;}
            string focused=focusIndex>=0 && focusIndex<photos.Count && photos[focusIndex]!=null?photos[focusIndex].Folder:null;
            string anchored=anchorIndex>=0 && anchorIndex<photos.Count && photos[anchorIndex]!=null?photos[anchorIndex].Folder:null;
            var reflow=CaptureReflow();
            var source=PhotosInSourceOrder;
            if(source!=photos) {
                var members=new HashSet<Photo>(before);
                int next=0;
                for(int i=0;i<source.Count;i++)if(members.Contains(source[i]))source[i]=ordered[next++];
            }
            for(int k=0;k<slots.Count;k++)photos[slots[k]]=ordered[k];
            ReorderFormatSource(before,ordered);
            if(focused!=null)focusIndex=photos.FindIndex(p=>p!=null && String.Equals(p.Folder,focused,StringComparison.OrdinalIgnoreCase));
            if(anchored!=null)anchorIndex=photos.FindIndex(p=>p!=null && String.Equals(p.Folder,anchored,StringComparison.OrdinalIgnoreCase));
            ShowPhotos(reflow);
            RefreshSelection();
        }
        internal async Task<Photo> EditSavedCover(Viewer viewer,Photo photo) {
            if(!CoverWorkFree(viewer))return null;
            if(!EnsureCoverFolder(viewer))return null;
            try {
                var preview=await Task.Run(()=>{
                    var token=lifetime.Token;
                    // A cover cut from an outside picture is edited from the kept copy of that picture, not from the original.
                    if(photo.IsOutside) {
                        if(!File.Exists(photo.PictureFile))throw new FileNotFoundException(L.T("The picture this cover was made from can’t be found, so the cover can’t be edited. You can add the picture again as a new cover."),photo.PictureFile);
                        return Pictures.Load(photo.PictureFile,Preferences.EditorPreviewSide);
                    }
                    string path=OriginalLocator.Resolve(store,photo,token);
                    return AnimationFrames.Supports(path)?AnimationFrames.SavedFrame(path,photo,Preferences.EditorPreviewSide,token):
                        Pictures.Load(path,Preferences.EditorPreviewSide);
                },lifetime.Token);
                if(!viewer.IsVisible || viewer.CurrentPhoto!=photo || lifetime.IsCancellationRequested)return null;
                if(!CoverWorkFree(viewer))return null; // another cover was opened while this one was loading
                var dialog=CreateCropEditor(viewer,photo,preview);
                dialog.Title=L.T("Edit cover");
                dialog.KeepsSavedShape=true; // editing keeps the cover's own shape even with Keep this shape on
                Rect startingCrop=dialog.Surface.Fraction;
                Photo updated=null;
                dialog.CommitCover=async editor=>{
                    // Keep the saved coordinates if the preview size introduced rounding and the crop did not move.
                    Rect crop=editor.Surface.Fraction==startingCrop?photo.Crop:editor.Surface.Fraction;
                    updated=await SaveCrop(photo,editor,null,crop,showAfter:false,picture:photo.PictureFile);
                };
                dialog.EnableSaveEditedCopy(editor=>{
                    Rect crop=editor.Surface.Fraction==startingCrop?photo.Crop:editor.Surface.Fraction;
                    return SaveEditedJpgFromCrop(photo,editor,null,crop,photo.PictureFile);
                });
                if(!photo.IsOutside && MotionMaker.Supports(photo.Original))
                    dialog.EnableMotion(async (editor,request)=>{
                        Rect crop=editor.Surface.Fraction==startingCrop?photo.Crop:editor.Surface.Fraction;
                        updated=await SaveCrop(photo,editor,null,crop,false,false);
                        MakeMotion(updated,request);
                    },()=>new MotionSource {Path=photo.Original,Moment=MotionSource.MomentOf(photo.Original,photo.FrameTime,photo.FrameIndex),
                        Crop=dialog.Surface.Fraction,Rotation=dialog.Surface.Rotation,Brightness=dialog.Surface.Brightness,
                        Adjust=dialog.Surface.Adjust,RecordFolder=photo.Folder,Aspect=dialog.Surface.Aspect,Still=dialog.Surface.Still()},
                    (request,tok,prog)=>SaveEditedMotionFromSource(new MotionSource {Path=photo.Original,
                        Moment=MotionSource.MomentOf(photo.Original,photo.FrameTime,photo.FrameIndex),
                        Crop=dialog.Surface.Fraction,Rotation=dialog.Surface.Rotation,Brightness=dialog.Surface.Brightness,
                        Adjust=dialog.Surface.Adjust,RecordFolder=photo.Folder,Aspect=dialog.Surface.Aspect},request,tok,prog));
                bool? ok=CoverWork.Show(dialog,photo,this);
                if(!String.IsNullOrEmpty(dialog.SavedEditedPath))RevealSavedFile(dialog.SavedEditedPath);
                return ok==true?updated:null;
            } catch(OperationCanceledException){return null;}
            catch(Exception ex){if(viewer.IsVisible && viewer.CurrentPhoto==photo)UI.Error(viewer,ex);return null;}
        }
        public async void Edit(Photo photo) {
            if(!CoverWorkFree(this))return;
            FinishPendingCovers();
            try {
                if(!EnsureCoverFolder(this))return;
                if(AnimationFrames.Supports(photo.Original)) {
                    var animViewer=new Viewer(this,new[]{photo},0,true);
                    CoverWork.Show(animViewer,photo,this);
                    ShowJustSaved();
                    return;
                }
                if(photo.Linked && photo.HasCrop) {
                    photo.Original=await Task.Run(()=>OriginalLocator.Resolve(store,photo,lifetime.Token));
                    if(lifetime.IsCancellationRequested)return;
                    if(!CoverWorkFree(this))return; // another cover was opened while this one was loading
                }
                var preview=Pictures.Load(photo.Original,Preferences.EditorPreviewSide);
                var dialog=CreateCropEditor(this,photo,preview,photo.IsOutside);
                // A photo that already has a cover gets another one; the editor starts from the existing composition.
                var target=AddTarget(photo);
                dialog.CommitCover=editor=>SaveCrop(target,editor,photo.FrameIndex);
                dialog.EnableSaveEditedCopy(editor=>SaveEditedJpgFromCrop(target,editor,photo.FrameIndex,null,null));
                dialog.AfterCommit=()=>OpenSavedOver(null,dialog);
                CoverWork.Show(dialog,photo,this);
                if(!String.IsNullOrEmpty(dialog.SavedEditedPath))RevealSavedFile(dialog.SavedEditedPath);
                ShowJustSaved();
            } catch(OperationCanceledException){} catch(Exception ex){if(!lifetime.IsCancellationRequested)UI.Error(this,ex);}
        }
        // After a cover is saved from the editor, its large view opens as soon as the editor closes.
        // The automated checks turn this off except where they test it.
        internal static bool OpenCoverAfterSave=true;
        Photo justSaved;
        // offerMotion: a video/GIF/WebP cover can offer Make GIF. showAfter: open the large view afterwards
        // (off when editing from a large view, which then shows the new cover itself).
        // picture: the outside picture the cover is cut from (null for a cover made from the original).
        async Task<Photo> SaveCrop(Photo photo,CropWindow dialog,int? frame,Rect? savedCrop=null,bool offerMotion=true,bool showAfter=true,string picture=null) {
            var preview=dialog.Surface.Source;
            var crop=savedCrop??dialog.Surface.Fraction;
            double rotation=dialog.Surface.Rotation;
            double? frameTime=dialog.FrameTime;
            int offset=dialog.FrameTimeOffset;
            string directory=settings.CoverDirectory;
            int brightness=dialog.Surface.Brightness;
            AdjustValues adjust=dialog.Surface.Adjust;
            var place=settings.CoverPlace;
            string drawings=dialog.Drawings;
            EraseAiPatch eraseAi=dialog.EraseAi;
            Photo updated=await Task.Run(()=>store.Reframe(photo,preview,crop,frame,rotation,frameTime,offset,
                CoverOutputMode.Native,0,lifetime.Token,directory,brightness,place,picture,drawings,adjust,eraseAi));
            bool added=!photo.HasCrop && GalleryStore.RecordBase(photo)!=Path.GetFileName(photo.Folder);
            bool motion=offerMotion && !updated.HasMotion && picture==null && MotionMaker.Supports(updated.Original);
            Action makeGif=motion?(Action)(()=>ShowMakeGif(ActiveAppWindow(),updated)):null;
            string gifLine=motion?" "+L.P("[Make GIF cover] makes a moving cover from this JPG cover.","[GIF 표지 만들기]를 누르면 이 JPG 표지로 움직이는 표지를 만들어요."):"";
            if(picture!=null && !photo.HasCrop)
                Notify(L.T("Added the picture as a cover"),added?L.T("The earlier covers are still there. To change the order, right-click a cover and choose Cover order."):L.T("To cut it differently later, open the cover and click Edit cover."),null);
            else if(added)
                Notify(L.T("Made one more cover"),L.T("The earlier cover is still there. The new cover goes in the place that fits the order of the video’s scenes.")+gifLine,makeGif,NoticeKind.Done,false,15,L.T("Make GIF cover"));
            else if(motion && !(showAfter && OpenCoverAfterSave))
                // The large view has its own Make GIF button; the notice is for when it doesn't open.
                Notify(L.T("Cover saved"),gifLine.Trim(),makeGif,NoticeKind.Done,false,15,L.T("Make GIF cover"));
            if(showAfter && OpenCoverAfterSave)justSaved=updated;
            if(updated.SavedToFallback)
                Notify(L.T("Saved in the cover folder instead"),
                    L.T("This cover couldn't be saved where the original is, so it was saved in the cover folder. This can happen with phones and some cloud folders."),
                    null,NoticeKind.Warning);
            int index=photos.FindIndex(p=>p!=null && p.Folder==photo.Folder);
            if(ShowingScope && Renamer.IsAt(updated.Original,ScopeFolder) && (added || folderFilter!=0 || index<0))ReloadKeepingPlace();
            else if(index>=0)UpdatePhoto(index,updated);
            else if(!RefreshHiddenCover(updated) && added)InsertCover(updated);
            return updated;
        }
        async Task<string> SaveEditedJpgFromCrop(Photo photo,CropWindow dialog,int? frame,Rect? savedCrop,string picture) {
            var crop=savedCrop??dialog.Surface.Fraction;
            double rotation=dialog.Surface.Rotation;
            AdjustValues adjust=dialog.Surface.Adjust;
            string drawings=dialog.Drawings;
            BitmapSource eraseAi=dialog.EraseAi!=null?dialog.EraseAi.Patch:null;
            int selectedFrame=frame??photo.FrameIndex;
            double? selectedTime=frame.HasValue?dialog.FrameTime:photo.FrameTime;
            int selectedOffset=frame.HasValue?dialog.FrameTimeOffset:photo.FrameTimeOffset;
            string fallbackDir=settings.CoverDirectory;
            return await Task.Run(()=>{
                string original=photo.Original;
                BitmapSource source;
                if(picture!=null) {
                    try {if(photo.Linked)original=OriginalLocator.Resolve(store,photo,lifetime.Token);} catch(FileNotFoundException) {}
                    source=Pictures.Load(picture);
                } else {
                    if(photo.Linked)original=OriginalLocator.Resolve(store,photo,lifetime.Token);
                    source=AnimationFrames.Supports(original)?
                        AnimationFrames.SavedFrame(original,new Photo {FrameIndex=selectedFrame,FrameTime=selectedTime,FrameTimeOffset=selectedOffset},0,lifetime.Token):
                        Pictures.Load(original);
                }
                return SharedCoverFiles.WriteEditedJpg(original,source,crop,rotation,adjust,drawings,eraseAi,lifetime.Token,fallbackDir);
            },lifetime.Token);
        }
        Task<string> SaveEditedMotionFromSource(MotionSource source,MotionRequest request,CancellationToken token,Action<double> progress) {
            string fallbackDir=settings.CoverDirectory;
            return Task.Run(()=>MotionMaker.SaveEditedCopy(source,request,token,fallbackDir,progress),token);
        }
        internal void RevealSavedFile(string path) {
            if(String.IsNullOrEmpty(path) || lifetime.IsCancellationRequested)return;
            try {
                string full=Path.GetFullPath(path);
                if(!File.Exists(full))return;
                string folder=Path.GetDirectoryName(full);
                string albumKey=store.AlbumFolderOf(folder);
                int curFilter;
                if(folderFilters.TryGetValue(albumKey,out curFilter) && curFilter==2)folderFilters[albumKey]=0;
                string fmt=FormatName(full);
                if(chosenFormats.Count>0 && fmt!=null && !chosenFormats.Contains(fmt))chosenFormats.Clear();
                if(search.Text.Trim().Length>0) {searchTimer.Stop();search.Text="";}
                if(WindowState==WindowState.Minimized)WindowState=WindowState.Normal;
                Activate();
                if(active!=null && active.Source!=null && ShellPlaces.Same(active.Source,folder)) {
                    if(folderFilter==2){folderFilters[active.Folder]=folderFilter=0;filterButtons[0].IsChecked=true;}
                    ReloadKeepingPlace();
                } else {
                    NavigateTo(folder);
                }
                int tries=0;
                var wait=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(80)};
                wait.Tick+=delegate {
                    tries++;
                    int idx=photos.FindIndex(p=>p!=null && p.Original!=null && String.Equals(p.Original,full,StringComparison.OrdinalIgnoreCase));
                    if(idx>=0) {
                        wait.Stop();
                        var hit=photos[idx];
                        selected.Clear();selected.Add(hit.Folder);
                        focusIndex=anchorIndex=idx;
                        RefreshSelection();
                        int rowIdx=folderRows+idx/Math.Max(1,columns);
                        if(rowIdx>=0 && rowIdx<grid.Items.Count)grid.ScrollIntoView(grid.Items[rowIdx]);
                        FocusTiles();
                    } else if(tries>60 || lifetime.IsCancellationRequested)wait.Stop();
                };
                wait.Start();
                Notify(L.P("Saved as a new file","새 파일로 저장했어요"),Path.GetFileName(full),()=>{
                    var hit=photos.FirstOrDefault(p=>p!=null && p.Original!=null && String.Equals(p.Original,full,StringComparison.OrdinalIgnoreCase));
                    if(hit!=null)OpenPhoto(hit,false);
                },NoticeKind.Done,false,10,L.P("Open","열기"));
            } catch(Exception ex) {UI.Error(this,ex);}
        }
        // Make GIF for a cover that is already saved: same crop, rotation and adjustments.
        internal void ShowMakeGif(Window owner,Photo photo) {
            if(!photo.HasCrop || photo.IsOutside || !MotionMaker.Supports(photo.Original))return;
            if(!CoverWorkFree(owner))return;
            try {
                string original=photo.Original;
                if(photo.Linked && !File.Exists(original))original=OriginalLocator.Resolve(store,photo,lifetime.Token);
                var source=new MotionSource {Path=original,Moment=MotionSource.MomentOf(original,photo.FrameTime,photo.FrameIndex),
                    Crop=photo.Crop,Rotation=photo.Rotation,Brightness=photo.Brightness,Adjust=photo.Adjust,
                    RecordFolder=photo.Folder,InitialEraseAi=photo.HasMotion?photo.LoadEraseRecord(true):null,Aspect=photo.Aspect};
                try {if(String.IsNullOrEmpty(photo.Drawings) && photo.CoverFile!=null && File.Exists(photo.CoverFile))source.Still=Pictures.Load(photo.CoverFile,960);} catch(Exception) {}
                // A cover that already has a GIF: Edit GIF cover, starting from the part and choices it was made with.
                var dialog=new MotionWindow(owner!=null && owner.IsVisible?owner:this,source,false,photo.HasMotion,photo.HasMotion?photo.MotionSettings:null);
                dialog.SaveEditedFile=(req,tok,prog)=>SaveEditedMotionFromSource(source,req,tok,prog);
                if(CoverWork.Show(dialog,photo,this,original)==true)MakeMotion(photo,dialog.Value);
                else if(!String.IsNullOrEmpty(dialog.SavedEditedPath))RevealSavedFile(dialog.SavedEditedPath);
            } catch(Exception ex){Notify(L.T("Something went wrong"),ex.Message,null,NoticeKind.Warning);}
        }
        // Makes the GIF (or WebP/MP4) in the background and links it to the cover when done.
        // watcher: the large view it was asked from (Enter): while it shows, only a small line on the video says how far
        // it is and a short note when done, instead of the notice cards (2026-11-27 user: the card was far too big).
        internal async void MakeMotion(Photo photo,MotionRequest request,Viewer watcher=null) {
            // An edited GIF cover replaces the old file: it goes to the Recycle Bin and the new one takes its name.
            string old=photo.HasMotion?photo.MotionFile:null;
            var job=new object();
            bool quiet=watcher!=null && watcher.CanShowGifWork;
            // Just the title and the file: the line about working in the background made the card three lines taller.
            var working=quiet?null:Notify(L.T("Making the GIF cover"),photo.Name,null,NoticeKind.Busy);
            if(quiet)watcher.GifWorkStarted(job,photo.Name);
            Action<double> progress=null;
            if(quiet){var last=DateTime.MinValue;progress=share=>{var now=DateTime.UtcNow;if((now-last).TotalMilliseconds<250 && share<1)return;last=now;Dispatcher.BeginInvoke(new Action(()=>watcher.GifWorkProgress(job,share)));};}
            try {
                var place=settings.CoverPlace;string coverDirectory=settings.CoverDirectory;
                var result=await Task.Run(()=>{
                    string original=OriginalLocator.Resolve(store,photo,lifetime.Token);
                    var shapes=MotionShape.Read(request.Shapes);
                    if(request.TransientAiPatch!=null && shapes.Any(s=>s.IsAiErase)) {
                        EraseStore.SaveToRecord(photo.Folder,request.TransientAiPatch,true);
                    } else if(!shapes.Any(s=>s.IsAiErase)) {
                        EraseStore.SaveToRecord(photo.Folder,null,true);
                    }
                    string target=SharedCoverFiles.TargetFolder(place,coverDirectory,original,photo.Linked);
                    MotionResult made;
                    try {
                        made=MotionMaker.Make(photo,original,request,target,target!=coverDirectory && place==CoverPlace.BesideOriginal,lifetime.Token,progress);
                    } catch(Exception ex) {
                        if(target==coverDirectory || lifetime.IsCancellationRequested || !(ex is IOException || ex is UnauthorizedAccessException) ||
                            ex is FileNotFoundException)throw;
                        made=MotionMaker.Make(photo,original,request,coverDirectory,false,lifetime.Token,progress);
                    }
                    var linked=GalleryStore.AttachMotion(photo,made.File,request);
                    return Tuple.Create(made,linked);
                },lifetime.Token);
                if(lifetime.IsCancellationRequested)return;
                if(working!=null)working.Dismiss();
                var ready=result.Item2;
                if(ready!=null && old!=null)ready=ReplaceOldMotion(ready,old,result.Item1.File,request)??ready;
                if(ready!=null)ReplaceShown(ready);
                // Tell the person when the original ran out before or after the cover's moment.
                string shorter="";
                if(result.Item1.Before<request.Before-0.05)shorter+=L.F("The original had only {0:0.#} s before the cover, so only that much went in.",result.Item1.Before)+" ";
                if(result.Item1.After<request.After-0.05)shorter+=L.F("The original had only {0:0.#} s after the cover, so only that much went in.",result.Item1.After)+" ";
                // Still watching: a short note on the video that fades by itself. Otherwise the usual card.
                if(quiet && watcher.CanShowGifWork && shorter.Length==0)watcher.GifWorkEnded(job,true);
                else {
                    if(quiet)watcher.GifWorkEnded(job,false);
                    Notify(L.T("The GIF cover is ready"),photo.Name+(shorter.Length>0?Environment.NewLine+shorter.Trim():""),
                        ready==null?(Action)null:()=>OpenReadyCover(ready),NoticeKind.Done,false,8,L.P("Open","열기"));
                }
            } catch(OperationCanceledException) {if(quiet)watcher.GifWorkEnded(job,false);}
            catch(Exception ex) {
                if(working!=null)working.Dismiss();
                if(quiet)watcher.GifWorkEnded(job,false);
                if(!lifetime.IsCancellationRequested)
                    Notify(L.T("Couldn’t make the GIF cover"),photo.Name+Environment.NewLine+ex.Message,null,NoticeKind.Warning);
            }
            // The Make GIF window's ready-made file has been copied (or wasn't usable); either way it's done.
            finally {if(working!=null)working.Dismiss();MotionMaker.DeleteLater(request.Prepared);}
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
                Notify(L.T("Hidden covers show again"),L.T("You can see them in Cover collection."),null);
                if(!ShowingAllCrops)ReloadKeepingPlace();
            } catch(Exception ex){UI.Error(this,ex);}
        }
        // Sends the cover's files (still, upscaled copy, GIF) to the Recycle Bin and forgets the cover.
        // The original stays where it is. The tile leaves at once, but nothing on disk changes for 3 seconds,
        // so the notice can bring the cover back exactly as it was. Pointing at the notice holds the time.
        sealed class PendingCovers {internal List<Photo> Covers;internal bool Done;}
        readonly List<PendingCovers> pendingDeletes=new List<PendingCovers>();
        readonly HashSet<string> pendingFolders=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const double UndoSeconds=3;
        internal void RecycleCover(Photo photo) {RecycleCovers(new[]{photo});}
        internal void RecycleCovers(IEnumerable<Photo> covers) {
            var chosen=WithoutCoverWork(covers).Where(p=>p.HasCrop && pendingFolders.Add(p.Folder)).ToList();
            if(chosen.Count==0)return;
            var pending=new PendingCovers {Covers=chosen};
            pendingDeletes.Add(pending);
            if(ShowingScope)Refresh(true);else ShowWithoutPending();
            var toast=new UpscaleToast(ActiveAppWindow(),
                chosen.Count==1?L.T("Moved the cover to the Recycle Bin"):L.F("Moved {0:N0} covers to the Recycle Bin",chosen.Count),
                L.P("[Undo] within 3 seconds puts it back. The original is still there.","3초 안에 [되돌리기]를 누르면 돌아와요. 원본은 그대로 있어요."),
                ()=>UndoCovers(pending),NoticeKind.Info,false,UndoSeconds,L.P("Undo","되돌리기"));
            toast.Closed+=delegate {if(!toast.WasClicked)FinishCovers(pending);};
            toast.Show();
        }
        void UndoCovers(PendingCovers pending) {
            if(pending.Done)return;
            pending.Done=true;pendingDeletes.Remove(pending);
            foreach(var photo in pending.Covers)pendingFolders.Remove(photo.Folder);
            ReloadKeepingPlace();
        }
        void FinishCovers(PendingCovers pending) {
            TileSound.Stop(); // a cover file that is about to go must not be held by its own sound
            if(pending.Done)return;
            pending.Done=true;pendingDeletes.Remove(pending);
            bool open=!lifetime.IsCancellationRequested && IsVisible,reload=false;
            var done=new List<Photo>();
            foreach(var photo in pending.Covers) {
                pendingFolders.Remove(photo.Folder);
                try {
                    var files=new[]{photo.CoverFile,photo.UpscaleFile,photo.MotionFile}
                        .Where(f=>!String.IsNullOrEmpty(f) && File.Exists(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    var handle=new System.Windows.Interop.WindowInteropHelper(this).Handle;
                    foreach(string file in files)moveToRecycleBin(file,handle);
                    GalleryStore.RemoveRecord(photo);
                    done.Add(photo);
                } catch(Exception ex){
                    reload=true; // it couldn't go: its tile comes back
                    if(open)Notify(L.T("Couldn’t move the cover to the Recycle Bin"),ex.Message,null,NoticeKind.Warning);
                }
            }
            // A deleted first cover with other covers left: the next of them moves up to the first place.
            // A cover still waiting out its own 3 seconds is never moved up.
            foreach(var photo in done.Where(p=>GalleryStore.RecordBase(p).Length==Path.GetFileName(p.Folder).Length)) {
                try {
                    if(GalleryStore.PromoteExtraCover(photo.Folder,f=>{int percent;return pendingFolders.Contains(f) || Upscales.Stage(f,out percent)!=UpscaleStage.None;})!=null)
                        reload=true;
                } catch(Exception) {}
            }
            if(open && ShowingScope) {
                var album=active;
                Dispatcher.BeginInvoke(new Action(()=>{if(active==album)ReloadKeepingPlace();}),DispatcherPriority.Background);
            } else if(open && reload)ReloadKeepingPlace();
        }
        // Before anything else touches the records (renaming, moving, a new cover, closing), waiting covers finish first.
        void FinishPendingCovers() {
            foreach(var pending in pendingDeletes.ToList())FinishCovers(pending);
        }
        // The list as it looks while covers wait: their tiles are gone, and an original left with no cover shows itself again.
        List<Photo> WithoutPending(List<Photo> list) {
            if(pendingFolders.Count==0)return list;
            var result=new List<Photo>(list.Count);
            foreach(var photo in list) {
                if(photo==null || !pendingFolders.Contains(photo.Folder)){result.Add(photo);continue;}
                if(ShowingAllCrops || photo.IsExtra)continue;
                bool otherCovers=list.Any(p=>p!=null && p.IsExtra && String.Equals(p.LeadFolder,photo.Folder,StringComparison.OrdinalIgnoreCase) && !pendingFolders.Contains(p.Folder));
                if(otherCovers || (active.Source!=null && folderFilter==2))continue;
                result.Add(new Photo {Folder=photo.Folder,Name=photo.Name,Original=photo.Original,Linked=true});
            }
            return result;
        }
        void ShowWithoutPending() {
            var now=WithoutPending(photos);
            var stays=new HashSet<string>(now.Where(p=>p!=null).Select(p=>p.Folder),StringComparer.OrdinalIgnoreCase);
            foreach(var bare in now.Where(p=>p!=null && !p.HasCrop && pendingFolders.Contains(p.Folder))) {
                int index=photos.FindIndex(p=>p!=null && p.Folder==bare.Folder);
                if(index>=0 && photos[index].HasCrop)UpdatePhoto(index,bare);
            }
            var removed=new HashSet<string>(photos.Where(p=>p!=null && !stays.Contains(p.Folder)).Select(p=>p.Folder),stays.Comparer);
            if(removed.Count>0)RemoveTiles(p=>removed.Contains(p.Folder));
        }
        // After an edited GIF cover is made: the old file goes to the Recycle Bin and the new one takes its name
        // (when it's the same kind of file in the same folder). If anything fails, both files simply stay.
        Photo ReplaceOldMotion(Photo linked,string old,string made,MotionRequest request) {
            TileSound.Stop(); // the old file may be playing in its tile right now
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
            if(CoverWork.Holds(photo)){TellFileInCoverWork();return;}
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
            if(ShowingSimilar){SimilarRecycle(targets.ToList());return;}
            if(ShowingAllCrops)return;
            targets=WithoutCoverWork(targets);
            FinishPendingCovers();
            var removed=new List<Photo>();
            foreach(var photo in targets.Where(p=>p.Linked && photos.Contains(p)).ToList()) {
                if(removed.Any(r=>SameOriginal(r,photo)))continue; // its main tile was already handled
                try {
                    // Operate only on the entry in the displayed folder, even if the viewer
                    // has previously located a same-named original somewhere else.
                    string source=active.Source;
                    if(ShowingUnfinished || ShowingTag || ShowingScope) {
                        // The tile may come from a folder inside an added one: its own record says where it lives.
                        source=GalleryStore.SourceOfRecords(Path.GetDirectoryName(photo.Folder));
                        if(source==null)continue;
                    }
                    string path=Path.Combine(source,Path.GetFileName(photo.Name));
                    if(!File.Exists(path))throw new FileNotFoundException(L.T("The original is missing from this folder."),path);
                    if(photo.HasCrop && !OriginalLocator.MatchesOriginal(photo,path,lifetime.Token))
                        throw new IOException(L.T("The file in this place is different from the original. Check it in File Explorer."));
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
                    reflow.OriginY=point.Y-((folderRows+at/columns)*rowHeight-offset);
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
        // A file was added next to the shown ones (a smaller copy of a moving picture): show it, keeping the place.
        internal void ReloadAfterChange() {if(active!=null && (active.Source!=null || ShowingScope))ReloadKeepingPlace();}
        void ReloadKeepingPlace() {
            if(lifetime.IsCancellationRequested)return;
            pendingReflow=CaptureReflow();Refresh();
        }
        // Takes tiles out of the list right away; the ones behind them slide forward.
        // promoted: tiles that stay but now stand for another record (old folder → new photo), such as a cover moved up.
        void RemoveTiles(Func<Photo,bool> remove,IDictionary<string,Photo> promoted=null) {
            if(scopePhotos!=null)scopePhotos.RemoveAll(p=>remove(p));
            RemoveFromFormatSource(remove,promoted);
            if(photos.Count==0 || rows==null)return;
            var reflow=CaptureReflow();
            var focused=focusIndex>=0 && focusIndex<photos.Count && photos[focusIndex]!=null?photos[focusIndex].Folder:null;
            var kept=PhotosInSourceOrder.Where(p=>p!=null && !remove(p)).ToList();
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
        // A new extra cover takes its place among the tiles of its original, in scene order unless the person arranged them.
        void InsertCover(Photo cover) {
            if(rows==null || active==null)return;
            string folder=Path.GetDirectoryName(cover.Folder),recordBase=GalleryStore.RecordBase(cover);
            int last=-1;
            var family=new List<Photo>();
            for(int i=0;i<photos.Count;i++) {
                var p=photos[i];
                if(p!=null && String.Equals(Path.GetDirectoryName(p.Folder),folder,StringComparison.OrdinalIgnoreCase) &&
                    String.Equals(GalleryStore.RecordBase(p),recordBase,StringComparison.OrdinalIgnoreCase)){last=i;family.Add(p);}
            }
            if(last<0)return; // that original isn't shown here; the cover appears when the view is loaded again
            if(active.Source!=null || ShowingUnfinished || ShowingTag || ShowingScope)cover.LeadFolder=photos[last].LeadFolder??photos[last].Folder;
            var order=GalleryStore.InCoverOrder(family.Concat(new[]{cover}));
            int at=order.IndexOf(cover);
            int place=at+1<order.Count?photos.IndexOf(order[at+1]):last+1;
            if(place<0)place=last+1;
            var reflow=CaptureReflow();
            reflow.Fresh.Add(cover.Folder);
            InsertIntoFormatSource(cover,photos[last],place<photos.Count?photos[place]:null);
            var source=PhotosInSourceOrder;
            if(source!=photos) {
                int sourcePlace=at+1<order.Count?source.IndexOf(order[at+1]):source.IndexOf(photos[last])+1;
                if(sourcePlace<0)sourcePlace=source.IndexOf(photos[last])+1; // never expected (the family is in both lists), but never a bad index
                if(sourcePlace<=0)sourcePlace=source.Count;
                source.Insert(sourcePlace,cover);
            }
            photos.Insert(place,cover);
            if(focusIndex>=place)focusIndex++;
            if(anchorIndex>=place)anchorIndex++;
            ShowPhotos(reflow);
        }
        // Delete in a large view: the file that view shows goes to the Recycle Bin, and its tiles leave every
        // folder window. It works after this window has moved on to another folder too. Returns what went wrong, or null.
        internal string RecycleFromViewer(Photo photo,IntPtr owner) {
            if(CoverWork.Holds(photo))return L.T("This file is being used for a cover");
            try {
                FinishPendingCovers();
                string path=photo.Original;
                if(String.IsNullOrEmpty(path) || !File.Exists(path))return L.T("The original file is unavailable.");
                if(photo.HasCrop && !OriginalLocator.MatchesOriginal(photo,path,lifetime.Token))
                    return L.T("The file in this place is different from the original. Check it in File Explorer.");
                moveToRecycleBin(path,owner);
            } catch(Exception ex) {return ex.Message;}
            foreach(var window in Application.Current.Windows.OfType<GalleryWindow>().ToList())
                try {if(window.rows!=null)window.RemoveTiles(p=>SameOriginal(photo,p));} catch(Exception) {}
            return null;
        }
        internal static bool SameOriginal(Photo a,Photo b) {
            return String.Equals(a.Name,b.Name,StringComparison.OrdinalIgnoreCase) &&
                String.Equals(Path.GetDirectoryName(a.Folder),Path.GetDirectoryName(b.Folder),StringComparison.OrdinalIgnoreCase);
        }
        void UpdatePhoto(int index,Photo photo) {
            // A replaced tile (a new picture, a GIF) stays an extra tile of the same original.
            if(photo!=null && photo.LeadFolder==null && photos[index]!=null && photos[index].LeadFolder!=null && photo.Folder==photos[index].Folder)
                photo.LeadFolder=photos[index].LeadFolder;
            if(scopePhotos!=null && photo!=null) {
                int at=scopePhotos.FindIndex(p=>String.Equals(p.Folder,photos[index].Folder,StringComparison.OrdinalIgnoreCase));
                if(at>=0)scopePhotos[at]=photo;
            }
            double shape=Covers.Shape(settings.TileShape);
            // Compared with the slot as it is laid out (the same Photo may already have changed, such as a cover hidden).
            bool relayout=wideSlots.Contains(index)!=TakesTwoCells(photo,shape);
            ReplaceInFormatSource(photos[index],photo);
            var source=PhotosInSourceOrder;
            if(source!=photos) {
                int sourceIndex=source.IndexOf(photos[index]);
                if(sourceIndex>=0)source[sourceIndex]=photo;
            }
            photos[index]=photo;
            CoverMarksSoon();
            // A cover that became wide (or stopped being wide) needs the rows laid out again; others change in place.
            if(relayout)LayoutRows();
            else {
                rows[folderRows+index/columns].Replace(index%columns,photo);
                if(index<laidOut.Count)laidOut[index]=photo; // so the place on screen is still found after a resize
            }
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
        // The move Ctrl+Z has taken out of the history but not yet handed to the file queue (it is still being
        // got ready). While it is set, another Ctrl+Z waits; if the getting ready is cut short, it goes back.
        static List<Tuple<string,string>> undoPending;
        // For a move that took folders along: the folders (old place, new place), so that undoing it tidies them too.
        static readonly Dictionary<List<Tuple<string,string>>,FolderTrail> moveTrails=new Dictionary<List<Tuple<string,string>>,FolderTrail>();
        static FolderTrail undoPendingTrail;
        sealed class FolderTrail {
            public List<Tuple<string,string>> Roots=new List<Tuple<string,string>>(),Empty=new List<Tuple<string,string>>();
            public HashSet<string> Created=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // New places of folders that moved in one step (so an undo knows them as folders without looking).
            public HashSet<string> Whole=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        static void TellFilesMoved() {var handler=FilesChanged;if(handler!=null)handler();}
        // The small button in the bottom bar that shows how far the file queue is; it is there only while the queue has work.
        Button queueButton;
        void ShowQueueButton() {
            if(queueButton==null)return;
            var jobs=TransferQueue.Jobs.Where(j=>j.Active).ToList();
            var was=queueButton.Visibility;
            if(jobs.Count==0)queueButton.Visibility=Visibility.Collapsed;
            else {
                long total=jobs.Sum(j=>j.TotalBytes),sent=jobs.Sum(j=>j.DoneBytes);
                bool working=jobs.Any(j=>j.State==TransferState.Running || j.State==TransferState.Waiting);
                // The word "Queue" is there whenever the window is wide enough for it.
                string word=ActualWidth>=1000?L.P("Queue","대기열")+" ":"";
                queueButton.Content=(working?"⇄ ":"Ⅱ ")+word+(total>0?Math.Floor(100.0*sent/total).ToString("0")+"%":jobs.Count.ToString());
                queueButton.ToolTip=(working?L.P("Files are being sent.","파일을 보내고 있어요."):L.P("The file queue is paused.","파일 대기열을 멈춰 뒀어요."))+" "
                    +L.P("Click to open the file queue.","누르면 파일 대기열이 열려요.");
                queueButton.Visibility=Visibility.Visible;
            }
            if(was!=queueButton.Visibility)updateFooter();
        }
        internal static bool AnotherWindowOpen {get {return Application.Current!=null && Application.Current.Windows.OfType<GalleryWindow>().Count(w=>w.IsVisible)>1;}}

        // Opens a second window beside this one. A full-screen or maximized window is first made smaller so both can be seen.
        internal void OpenAnotherWindow() {OpenAnotherWindow(null);}
        // folder: the tab the new window starts on (a folder, or the key of a collection); null starts where it usually would.
        string startFolder;
        // A folder tile's "Open in new window": that folder becomes the new window's starting place.
        internal void OpenFolderInNewWindow(string path) {
            try {OpenAnotherWindow(store.OpenSubfolder(path).Folder);} catch(Exception ex) {UI.Error(this,ex);}
        }
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
        internal void TakeOverSettings() {
            try {
                var fresh=Preferences.Load(store.Root);
                if(fresh.SameAs(settings))return;
                bool sortChanged=fresh.Sort!=settings.Sort || fresh.Descending!=settings.Descending,languageChanged=fresh.Korean!=settings.Korean;
                bool themeChanged=fresh.Theme!=settings.Theme,gridChanged=fresh.GridGif!=settings.GridGif ||
                    fresh.TileShape!=settings.TileShape || fresh.WideCovers!=settings.WideCovers || fresh.FillWideGaps!=settings.FillWideGaps;
                fresh.LanguageChosen=settings.LanguageChosen || fresh.LanguageChosen;
                settings=fresh;MotionWindow.UseDefaults(settings);ViewerKeys.Use(settings);
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
        void HideDropHint() {dropHintTimer.Stop();dropHint.Visibility=Visibility.Collapsed;MarkDropFolder(null);}

        string[] DraggedOriginals(IDataObject data) {
            var text=data.GetData(MoveFormat) as string;
            return String.IsNullOrEmpty(text)?new string[0]:text.Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries);
        }
        // The folder tab being shown is where dropped files go; the cover collections have no folder to drop into.
        bool CanDropMoves(string[] originals,string target) {
            return originals.Length>0 && target!=null && Directory.Exists(target)
                && originals.Any(p=>!Mover.SameFolder(Path.GetDirectoryName(p),target))
                && !originals.Any(p=>Renamer.IsAt(target,p)); // a folder is never dropped into itself
        }
        // A folder tile under the pointer takes the files into that folder; anywhere else they go to the folder on screen.
        // A folder tile or a folder row of the tree under the pointer, or null.
        static Control FolderTileAt(DragEventArgs e) {
            var row=FolderTree.FolderRowAt(e.OriginalSource);
            if(row!=null)return row;
            var node=e.OriginalSource as DependencyObject;
            while(node!=null) {
                var button=node as Button;
                if(button!=null && button.Tag is FolderTarget)return button;
                node=(node is Visual || node is System.Windows.Media.Media3D.Visual3D)?VisualTreeHelper.GetParent(node):LogicalTreeHelper.GetParent(node);
            }
            return null;
        }
        static string DropPath(Control target) {
            if(target==null)return null;
            var tile=target.Tag as FolderTarget;if(tile!=null)return tile.Path;
            var entry=target.Tag as TreeEntry;return entry==null?null:entry.Path;
        }
        Control dropFolderTile;
        void MarkDropFolder(Control tile) {
            if(dropFolderTile==tile)return;
            if(dropFolderTile is TreeViewItem)dropFolderTile.ClearValue(Control.BackgroundProperty);
            else if(dropFolderTile!=null && ((FolderTarget)dropFolderTile.Tag).Plain){dropFolderTile.ClearValue(Control.BorderBrushProperty);dropFolderTile.ClearValue(Control.BackgroundProperty);}
            else if(dropFolderTile!=null){dropFolderTile.SetResourceReference(Control.BorderBrushProperty,"Line");dropFolderTile.SetResourceReference(Control.BackgroundProperty,"Panel");}
            dropFolderTile=tile;
            if(tile is TreeViewItem)tile.SetResourceReference(Control.BackgroundProperty,"Raised");
            else if(tile!=null){tile.SetResourceReference(Control.BorderBrushProperty,"Accent");tile.SetResourceReference(Control.BackgroundProperty,"Raised");}
        }
        // Like File Explorer: files move within the same drive and are copied to another drive;
        // Ctrl always copies and Shift always moves.
        // Between drives the person's own choice in the options comes first: ask each time (the start), copy or move.
        // Returns true to copy, false to move, null to ask once the files are let go.
        bool? CopiesOnDrop(DragEventArgs e,string[] originals,string target) {
            if((e.KeyStates&DragDropKeyStates.ControlKey)!=0)return true;
            if((e.KeyStates&DragDropKeyStates.ShiftKey)!=0)return false;
            try {
                string root=Path.GetPathRoot(Path.GetFullPath(target));
                bool across=originals.Any(p=>!String.Equals(Path.GetPathRoot(Path.GetFullPath(p)),root,StringComparison.OrdinalIgnoreCase));
                if(!across)return false;
                return settings.DropAcross==1?true:settings.DropAcross==2?(bool?)false:null;
            } catch(Exception) {return false;}
        }
        // Like File Explorer, holding dragged files over a folded folder in the tree for a moment unfolds it,
        // and holding them near the tree's top or bottom edge scrolls it.
        TreeViewItem hoverRow;DateTime hoverSince;
        void HoverUnfold(TreeViewItem row) {
            if(row!=hoverRow){hoverRow=row;hoverSince=DateTime.UtcNow;return;}
            if(!row.IsExpanded && row.HasItems && (DateTime.UtcNow-hoverSince).TotalMilliseconds>800)row.IsExpanded=true;
        }
        void ScrollTreeNearEdge(DragEventArgs e) {
            if(tree==null || treePanel.Visibility!=Visibility.Visible)return;
            var scroller=FindScroller(tree);if(scroller==null)return;
            double y=e.GetPosition(scroller).Y,x=e.GetPosition(scroller).X;
            if(x<0 || x>scroller.ActualWidth)return;
            if(y>=0 && y<28)scroller.ScrollToVerticalOffset(scroller.VerticalOffset-8);
            else if(y>scroller.ActualHeight-28 && y<=scroller.ActualHeight)scroller.ScrollToVerticalOffset(scroller.VerticalOffset+8);
        }
        void MoveDragOver(DragEventArgs e) {
            var originals=DraggedOriginals(e.Data);
            e.Handled=true;
            var tile=FolderTileAt(e);
            string target=tile!=null?DropPath(tile):active==null?null:active.Source;
            if(!CanDropMoves(originals,target)){e.Effects=DragDropEffects.None;HideDropHint();MarkDropFolder(null);return;}
            bool? choice=CopiesOnDrop(e,originals,target);
            bool copy=choice!=false;
            e.Effects=copy?DragDropEffects.Copy:DragDropEffects.Link; // Link is this app's own "move" (the allowed effects keep other programs from moving the files)
            MarkDropFolder(tile);
            if(tile is TreeViewItem)HoverUnfold((TreeViewItem)tile);
            ScrollTreeNearEdge(e);
            if(choice==null)ShowDropHint(L.PF("Let go and you are asked whether to copy or move {0:N0} to “{1}”. Hold Ctrl to copy, Shift to move.",
                "지금 놓으면 {0:N0}개를 ‘{1}’ 폴더로 복사할지 옮길지 물어봐요. Ctrl을 누른 채 놓으면 복사, Shift를 누른 채 놓으면 옮기기예요.",originals.Length,tile!=null?Path.GetFileName(target):active.Name));
            else if(tile!=null)ShowDropHint(L.F(copy?"Let go and {0:N0} are copied into the “{1}” folder.":"Let go and {0:N0} move into the “{1}” folder.",originals.Length,Path.GetFileName(target)));
            else ShowDropHint(L.F(copy?"Let go to copy {0:N0} to “{1}”.":"Let go to move {0:N0} to “{1}”. Hold Ctrl to copy instead.",originals.Length,active.Name));
            if(!(tile is TreeViewItem))hoverRow=null;
        }
        void MoveDrop(DragEventArgs e) {
            e.Handled=true;HideDropHint();MarkDropFolder(null);
            var originals=DraggedOriginals(e.Data);
            var tile=FolderTileAt(e);
            string targetPath=tile!=null?DropPath(tile):active==null?null:active.Source;
            if(!CanDropMoves(originals,targetPath))return;
            bool? choice=CopiesOnDrop(e,originals,targetPath);
            e.Effects=choice!=false?DragDropEffects.Copy:DragDropEffects.Link;
            Album target;
            if(tile==null)target=active;
            else try {target=store.OpenSubfolder(targetPath);} catch(Exception ex) {UI.Error(this,ex);return;}
            // Start after the drag ends, so a question or notice doesn't open in the middle of the drag.
            Dispatcher.BeginInvoke(new Action(()=>{
                if(choice==null) {
                    var dialog=new TransferChoiceWindow(ActiveAppWindow(),originals.Length,target.Name);
                    dialog.ShowDialog();
                    if(dialog.CopyChosen==null)return;
                    choice=dialog.CopyChosen;
                    if(dialog.Remember) {
                        var updated=settings.Copy();updated.DropAcross=choice==true?1:2;
                        try {updated.Save(store.Root);settings=updated;SettingsChanged();} catch(Exception) {}
                    }
                }
                MoveOriginals(originals,choice==true,target);
            }),DispatcherPriority.ApplicationIdle);
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
        // originals: files, and folders too (a folder goes with everything inside it).
        void MoveOriginals(string[] originals,bool copy,Album target) {
            TakeOverSettings(); // an option changed in another window (check every copy) counts here too
            if(!copy && originals.Any(p=>CoverWork.HoldsPath(p) || CoverWork.HoldsUnder(p))) {
                originals=originals.Where(p=>!CoverWork.HoldsPath(p) && !CoverWork.HoldsUnder(p)).ToArray();
                TellFileInCoverWork();
                if(originals.Length==0)return;
            }
            FinishPendingCovers();
            var items=new List<TransferItem>();
            string from=null;
            try {
                var folders=originals.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                from=folders.Count==1?folders[0]:L.PF("{0:N0} folders","폴더 {0:N0}곳",folders.Count);
            } catch(Exception) {}
            QueueTransfer(new TransferJob {Copy=copy,FolderName=target.Name,FromLabel=from??"",ToLabel=target.Source},items,originals,target);
        }
        // Reads the sizes, asks about every same-name file now (so the queue never stops in the night to ask), then
        // puts the job in the file queue. The queue sends one job at a time.
        async void QueueTransfer(TransferJob job,List<TransferItem> items,string[] chosen=null,Album target=null) {
            job.Store=store;
            var busy=Notify(L.P("Getting the files ready…","보낼 파일을 살펴보고 있어요…"),"",null,NoticeKind.Busy);
            try {
                await Task.Run(()=>{
                    // Folders are opened up into the files inside them (this can take a while on a hard drive).
                    if(chosen!=null)Mover.Expand(store,chosen,target.Source,target.Folder,job,items);
                    foreach(var item in items) {
                        if(item.Whole)continue;
                        try {
                            var info=new FileInfo(item.From);
                            if(!info.Exists)continue;
                            item.Size=info.Length;item.Stamp=info.LastWriteTimeUtc;
                            var there=new FileInfo(Path.Combine(item.TargetSource,info.Name));
                            item.Conflict=!Mover.SameFolder(info.DirectoryName,item.TargetSource) && there.Exists && !Mover.SamePlace(info.DirectoryName,item.TargetSource);
                            if(item.Conflict){item.SeenLength=there.Length;item.SeenStamp=there.LastWriteTimeUtc;}
                        } catch(Exception) {}
                    }
                });
            } finally {busy.Dismiss();}
            if(lifetime.IsCancellationRequested) { // this window was closed meanwhile: nothing is queued
                if(job.Undo)lock(moveHistory) {
                    if(undoPending!=null){moveHistory.Add(undoPending);if(undoPendingTrail!=null)moveTrails[undoPending]=undoPendingTrail;}
                    undoPending=null;undoPendingTrail=null;
                }
                return;
            }
            if(job.Rejected.Count>0)Notify(L.P("Some couldn’t be taken","일부는 보낼 수 없어요"),
                String.Join(Environment.NewLine,job.Rejected.Take(3))+(job.Rejected.Count>3?Environment.NewLine+L.F("and {0:N0} more",job.Rejected.Count-3):""),null,NoticeKind.Warning);
            if(items.Count==0 && job.EmptyDirs.Count==0) {
                if(job.Undo)lock(moveHistory){undoPending=null;undoPendingTrail=null;}
                int there;lock(job.Report)there=job.Report.AlreadyThere;
                if(there>0)Notify(L.T("Nothing was moved"),L.F("{0:N0} were already in this folder.",there),null,NoticeKind.Info);
                return;
            }
            var conflicts=items.Where(i=>i.Conflict).ToList();
            ConflictChoice? always=null;
            for(int i=0;i<conflicts.Count;i++) {
                var item=conflicts[i];
                if(always.HasValue){item.Decided=always;continue;}
                var dialog=new ConflictWindow(ActiveAppWindow(),new ConflictInfo {Name=Path.GetFileName(item.From),From=item.From,
                    To=Path.Combine(item.TargetSource,Path.GetFileName(item.From)),FolderName=job.Undo?L.T("the original folder"):job.FolderName,
                    Number=i+1,Total=conflicts.Count,Copy=job.Copy});
                dialog.ShowDialog();
                item.Decided=dialog.Choice;
                if(dialog.ApplyToAll)always=dialog.Choice;
            }
            job.Items.AddRange(items);
            job.Verify=settings.VerifyCopies;
            BindTransfer(job,store,moveToRecycleBin);
            bool others=TransferQueue.Busy;
            TransferQueue.Add(job);
            if(job.Undo)lock(moveHistory){undoPending=null;undoPendingTrail=null;}
            // A job that is done in a blink (a move inside one drive) needs no window; anything longer shows the queue.
            if(others){TransferWindow.ShowQueue(false);TellQueueButton();}
            else {
                var soon=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(700)};
                soon.Tick+=delegate {
                    soon.Stop();
                    if(job.Active && Application.Current.Windows.OfType<GalleryWindow>().Any(w=>w.IsVisible)){TransferWindow.ShowQueue(false);TellQueueButton();}
                };
                soon.Start();
            }
        }
        // What a job needs from the running program: where the records are, how a replaced file goes to the Recycle Bin, and the notice at its end.
        static void BindTransfer(TransferJob job,GalleryStore store,Action<string,IntPtr> bin) {
            job.Store=store;
            job.Recycle=path=>Application.Current.Dispatcher.Invoke(new Action(()=>{
                var window=Application.Current.Windows.OfType<GalleryWindow>().FirstOrDefault(w=>w.IsVisible);
                bin(path,window==null?IntPtr.Zero:new System.Windows.Interop.WindowInteropHelper(window).Handle);
            }));
            job.Finished=TransferEnded;
        }
        // The first few times the file queue opens by itself: where to find it again.
        void TellQueueButton() {
            TakeOverSettings();
            if(settings.QueueTips>=3)return;
            var updated=settings.Copy();updated.QueueTips++;
            try {updated.Save(store.Root);settings=updated;} catch(Exception) {}
            Notify(L.P("It is in the file queue","파일 대기열에 넣었어요"),
                L.P("The file queue shows how far it is. You can pause there, cancel, or change the order. If you close that window, the [⇄ Queue] button in the bottom bar of this window opens it again.",
                    "파일 대기열 창에서 얼마나 보냈는지 볼 수 있고, 일시정지·취소·순서 바꾸기도 거기서 해요. 그 창을 닫아도 이 창 아래 줄의 [⇄ 대기열] 버튼을 누르면 다시 열려요."),
                ()=>TransferWindow.ShowQueue(true),NoticeKind.Info,false,15,L.P("Open the file queue","대기열 열기"));
        }
        // The file queue window was closed while files are still being sent.
        static void TellQueueGoesOn() {
            var window=Application.Current.Windows.OfType<GalleryWindow>().FirstOrDefault(w=>w.IsVisible);
            if(window==null)return;
            window.Notify(L.P("Still sending in the background","뒤에서 계속 보내고 있어요"),
                L.P("The [⇄ Queue] button in the bottom bar, or [Open the file queue] here, opens it again.","아래 줄의 [⇄ 대기열] 버튼이나 여기의 [대기열 열기]를 누르면 파일 대기열이 다시 열려요."),
                ()=>TransferWindow.ShowQueue(true),NoticeKind.Info,false,8,L.P("Open the file queue","대기열 열기"));
        }
        // Jobs the last run left unfinished are back in the queue, paused.
        static bool leftoverTold;
        void TellLeftoverQueue() {
            if(leftoverTold)return;
            leftoverTold=true;
            int left=TransferQueue.Jobs.Count(j=>j.Active);
            if(left==0)return;
            ShowQueueButton();
            Notify(L.P("Unfinished jobs are in the file queue","끝내지 못한 작업이 파일 대기열에 있어요"),
                L.PF("{0:N0} jobs from the last time are waiting, paused. [Open the file queue] opens it: [Go on] continues one from where it stopped, [Cancel] drops it.",
                    "지난번에 보내던 작업 {0:N0}개가 멈춘 채 기다리고 있어요. [대기열 열기]를 누르면 파일 대기열이 열려요. [이어서]를 누르면 멈춘 곳부터 계속하고, [취소]를 누르면 그만둬요.",left),
                ()=>TransferWindow.ShowQueue(true),NoticeKind.Info,true,15,L.P("Open the file queue","대기열 열기"));
        }
        // A job of the file queue has ended (done or cancelled): the notice, and what Ctrl+Z can put back.
        static void TransferEnded(TransferJob job) {
            var report=job.Report;
            List<Tuple<string,string>> moved;List<string> failed;int copied,skipped,already;
            lock(report){moved=report.Moved.ToList();failed=report.Failed.ToList();copied=report.Copied;skipped=report.Skipped;already=report.AlreadyThere;}
            if(job.Undo) {
                // Files the job never got to (it was cancelled) can still be put back with Ctrl+Z.
                // Ones that were skipped or failed are not offered again: they would only come up every time.
                var left=job.Items.Skip(job.Next)
                    .Select(i=>Tuple.Create(Path.Combine(i.TargetSource,i.RestoreName??Path.GetFileName(i.From)),i.From)).ToList();
                if(left.Count>0)lock(moveHistory) {
                    moveHistory.Add(left);
                    // The rest of the same move: its folders are tidied when the rest is put back too.
                    if(job.Roots.Count+job.EmptyDirs.Count>0 || job.Items.Any(i=>i.Whole)) {
                        var rest=new FolderTrail();
                        foreach(var root in job.Roots)rest.Roots.Add(Tuple.Create(root.Item2,root.Item1));
                        foreach(var empty in job.EmptyDirs)rest.Empty.Add(Tuple.Create(empty.Item2,empty.Item1));
                        if(job.OnlyThese!=null)rest.Created=job.OnlyThese;
                        foreach(var item in job.Items.Skip(job.Next))if(item.Whole)rest.Whole.Add(item.From);
                        moveTrails[left]=rest;
                    }
                }
            } else {
                bool folders=job.Roots.Count+job.EmptyDirs.Count>0 || job.Items.Any(i=>i.Whole);
                // A move of nothing but empty folders has no file to list, but it can be undone all the same.
                if(moved.Count>0 || (folders && !job.Copy && job.Created.Count>0))lock(moveHistory) {
                    moveHistory.Add(moved);
                    if(folders) {
                        var trail=new FolderTrail();
                        trail.Roots.AddRange(job.Roots);trail.Empty.AddRange(job.EmptyDirs);
                        lock(job.Created)foreach(string made in job.Created)trail.Created.Add(made);
                        var whole=new HashSet<string>(job.Items.Where(i=>i.Whole).Select(i=>i.From),StringComparer.OrdinalIgnoreCase);
                        foreach(var pair in moved)if(whole.Contains(pair.Item1))trail.Whole.Add(pair.Item2);
                        moveTrails[moved]=trail;
                    }
                }
            }
            // A folder that is at a new place now (in one step, or file by file until the old one was empty and
            // went): windows that were showing it or something inside it follow it, and so does the cover folder setting.
            var gone=new List<Tuple<string,string>>();
            var steps=new HashSet<string>(job.Items.Where(i=>i.Whole).Select(i=>i.From),StringComparer.OrdinalIgnoreCase);
            foreach(var pair in moved)if(steps.Contains(pair.Item1))gone.Add(pair);
            if(!job.Copy)foreach(var root in job.Roots) {
                try {if(!Directory.Exists(root.Item1) && Directory.Exists(root.Item2))gone.Add(root);} catch(Exception) {}
            }
            foreach(var pair in gone) {
                foreach(var gallery in Application.Current.Windows.OfType<GalleryWindow>().ToList()) {
                    if(gallery.lifetime.IsCancellationRequested)continue;
                    try {
                        bool covers=Renamer.IsAt(gallery.settings.CoverDirectory,pair.Item1);
                        gallery.FolderRenamed(pair.Item1,pair.Item2);
                        if(covers) {try {gallery.settings.Copy().Save(gallery.store.Root);} catch(Exception) {}}
                    } catch(Exception) {}
                }
            }
            var window=Application.Current.Windows.OfType<GalleryWindow>().FirstOrDefault(w=>w.IsVisible);
            if(window==null)return;
            bool cancelled=job.State==TransferState.Cancelled;
            // Every window showing the folder the files landed in says so once if its type filter hides them (put back too).
            foreach(var gallery in Application.Current.Windows.OfType<GalleryWindow>().ToList())gallery.NoticeHiddenArrivals(job);
            if(job.Undo) {
                if(failed.Count>0)window.Notify(L.T("Some files couldn’t be put back"),String.Join(Environment.NewLine,failed.Take(3)),null,NoticeKind.Warning);
                else if(moved.Count>0)window.Notify(L.T("Put back"),L.F("{0:N0} files were put back in their original place.",moved.Count),null);
                return;
            }
            bool copy=job.Copy;string folderName=job.FolderName;
            string done=copy?L.F("Copied {0:N0} to “{1}”.",copied,folderName):L.F("Moved {0:N0} to “{1}”.",moved.Count,folderName);
            var lines=new List<string>();
            if(cancelled)lines.Add(L.P("The rest was cancelled and is where it was.","나머지는 취소해서 원래 자리에 그대로 있어요."));
            if(moved.Count>0)lines.Add(L.T("You can undo with Ctrl+Z."));
            if(skipped>0)lines.Add(L.F("{0:N0} were skipped because the same name was there.",skipped));
            if(already>0)lines.Add(L.F("{0:N0} were already in this folder.",already));
            if(failed.Count==0) {
                if(moved.Count+copied==0 && job.EmptyDirs.Count>0 && !cancelled)
                    window.Notify(copy?L.T("Copied"):L.T("Moved"),L.PF("{0:N0} empty folders went to “{1}”.","빈 폴더 {0:N0}개를 ‘{1}’ 폴더로 보냈어요.",job.EmptyDirs.Count,folderName),null);
                else if(moved.Count+copied>0)window.Notify(copy?L.T("Copied"):L.T("Moved"),done+(lines.Count>0?Environment.NewLine+String.Join(" ",lines):""),null);
                else if(lines.Count>0 && !cancelled)window.Notify(L.T("Nothing was moved"),String.Join(" ",lines),null,NoticeKind.Info);
            } else {
                window.Notify(L.T("Some files couldn’t be moved"),
                    String.Join(Environment.NewLine,failed.Take(3))+(failed.Count>3?Environment.NewLine+L.F("and {0:N0} more",failed.Count-3):"")+
                    (moved.Count+copied>0?Environment.NewLine+done:""),()=>TransferWindow.ShowQueue(true),NoticeKind.Warning);
            }
        }
        // Ctrl+Z: puts the files of the latest move back where they were. It waits its turn in the file queue too,
        // so it never touches a file another job is sending.
        void UndoMove() {
            // One undo at a time: a second one started before the first has ended would put things back out of order.
            bool preparing;lock(moveHistory)preparing=undoPending!=null;
            if(preparing)return;
            if(TransferQueue.Undoing) {
                TransferWindow.ShowQueue(true);
                Notify(L.P("Still putting files back","아직 되돌리는 중이에요"),L.P("Press Ctrl+Z again when the one in the file queue has ended.","파일 대기열에 있는 되돌리기가 끝난 뒤에 Ctrl+Z를 다시 눌러 주세요."),null,NoticeKind.Info);
                return;
            }
            List<Tuple<string,string>> last;FolderTrail trail=null;
            lock(moveHistory) {
                if(moveHistory.Count==0)return;
                last=moveHistory[moveHistory.Count-1];
                if(last.Any(m=>CoverWork.HoldsPath(m.Item1) || CoverWork.HoldsPath(m.Item2) || CoverWork.HoldsUnder(m.Item2)))last=null; // stays in the history for later
                else {
                    moveHistory.RemoveAt(moveHistory.Count-1);undoPending=last;
                    if(moveTrails.TryGetValue(last,out trail))moveTrails.Remove(last);
                    undoPendingTrail=trail;
                }
            }
            if(last==null){TellFileInCoverWork();return;}
            var items=new List<TransferItem>();var failed=new List<string>();
            // The folder a file goes back into may be gone (it was moved away with the file): it is made again.
            var albums=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            foreach(var pair in last) {
                string back=Path.GetDirectoryName(pair.Item1);
                try {
                    bool whole=trail!=null && trail.Whole.Contains(pair.Item2);
                    string album;
                    // OpenSubfolder: a folder that was not on the shelf is not put on it by an undo.
                    if(!albums.TryGetValue(back,out album))albums[back]=album=Directory.Exists(back)?store.OpenSubfolder(back).Folder:store.AlbumFolderOf(back);
                    items.Add(new TransferItem {From=pair.Item2,TargetSource=back,TargetAlbum=album,RestoreName=whole?null:Path.GetFileName(pair.Item1),Whole=whole,MakeFolder=!whole});
                }
                catch(Exception ex) {failed.Add(Path.GetFileName(pair.Item1)+" — "+ex.Message);}
            }
            if(failed.Count>0)Notify(L.T("Some files couldn’t be put back"),String.Join(Environment.NewLine,failed.Take(3)),null,NoticeKind.Warning);
            bool folderWork=trail!=null && trail.Roots.Count+trail.Empty.Count>0;
            if(items.Count==0 && !folderWork){lock(moveHistory){undoPending=null;undoPendingTrail=null;}return;}
            string to=null;
            try {
                var folders=items.Select(i=>i.TargetSource).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                to=folders.Count==1?folders[0]:L.PF("{0:N0} folders","폴더 {0:N0}곳",folders.Count);
            } catch(Exception) {}
            var undo=new TransferJob {Undo=true,FolderName=L.T("the original folder"),
                FromLabel=items.Count>0?Path.GetDirectoryName(items[0].From):trail.Roots.Count>0?trail.Roots[0].Item2:"",ToLabel=to??(trail!=null && trail.Roots.Count>0?trail.Roots[0].Item1:"")};
            // The folders the files came back out of are empty afterwards and go, but only the ones the move itself
            // had made; a folder that was there before the move stays. Folders that were empty go back as well.
            if(trail!=null) {
                foreach(var root in trail.Roots)undo.Roots.Add(Tuple.Create(root.Item2,root.Item1));
                foreach(var empty in trail.Empty)undo.EmptyDirs.Add(Tuple.Create(empty.Item2,empty.Item1));
                undo.OnlyThese=trail.Created;
            }
            QueueTransfer(undo,items);
        }
        // What a drag from this tile carries: every chosen tile when the dragged one is chosen, otherwise just that one.
        internal List<Photo> DragTargets(Photo photo) {
            if(selected.Contains(photo.Folder)) {
                var chosen=SelectedPhotos.ToList();
                if(chosen.Count>0)return chosen;
            }
            return new List<Photo>{photo};
        }

        // ---------- The right-click menu: ours, then File Explorer's ----------
        internal static Point MenuPoint(FrameworkElement at,ContextMenuEventArgs e) {
            if(e!=null && e.CursorLeft>=0){var p=System.Windows.Forms.Cursor.Position;return new Point(p.X,p.Y);}
            return at.PointToScreen(new Point(at.ActualWidth/2,at.ActualHeight/2)); // opened from the keyboard
        }
        internal string RangeHint {get {return L.T("Select many: hold the right button and drag");}}
        internal void ShowFileMenu(Photo photo,ContextMenu own,FrameworkElement at,ContextMenuEventArgs e) {
            var targets=Targets(photo);
            var take=new ShellMenu.Takeovers {
                Delete=()=>{
                    if(targets.Count>1){DeleteSelected();return;}
                    if(ShowingAllCrops)RecycleCover(photo);else RecycleOriginal(photo);
                },
                Rename=()=>{if(targets.Count==1 && photo.Linked)RenameOriginal(photo);},
                Cut=()=>CopyToClipboard(true,targets),Copy=()=>CopyToClipboard(false,targets),Paste=()=>PasteFromClipboard(null)
            };
            ShellMenu.Show(this,own,ClipboardFiles(targets),MenuPoint(at,e),take,RefreshSoon,RangeHint);
        }
        // For a folder (in the folders area or the tree): Windows does what it does to folders, except Rename
        // and Paste, which LoveMyShelf does so covers follow.
        internal void ShowFolderMenu(ContextMenu own,IList<string> folders,FrameworkElement at,ContextMenuEventArgs e) {
            string folder=folders.Count==1?folders[0]:null;
            var rename=new MenuItem {Header=L.T("Rename"),InputGestureText="F2",IsEnabled=folder!=null && Renamer.CanRenameFolder(folder)};
            rename.Click+=delegate {RenameFolder(folder);};
            var take=new ShellMenu.Takeovers {HideRename=true,LiftDelete=true,
                Paste=folder!=null?(Action)(()=>PasteFromClipboard(folder)):null};
            own.Items.Insert(0,rename);
            try {
                ShellMenu.Show(this,own,folders,MenuPoint(at,e),take,()=>{
                    // The folder on screen was just deleted: go to the nearest folder still there, like File Explorer.
                    if(active!=null && active.Source!=null && !Directory.Exists(active.Source)) {
                        string up=Path.GetDirectoryName(active.Source.TrimEnd(Path.DirectorySeparatorChar));
                        while(up!=null && !Directory.Exists(up))up=Path.GetDirectoryName(up);
                        if(up!=null)NavigateTo(up);
                    }
                    RefreshSoon();if(tree!=null && treePanel.Visibility==Visibility.Visible){tree.Build();TreeSyncSoon();}
                });
            } finally {own.Items.Remove(rename);}
        }
        DispatcherTimer refreshSoon;
        // After Windows ran something from its menu (it may have changed files), read the folder again shortly.
        internal void RefreshSoon() {
            if(refreshSoon==null){refreshSoon=new DispatcherTimer {Interval=TimeSpan.FromSeconds(1.2)};refreshSoon.Tick+=delegate {refreshSoon.Stop();Refresh();};}
            refreshSoon.Stop();refreshSoon.Start();
        }

        static bool IsOnTileOrBar(object source) {
            for(var node=source as DependencyObject;node!=null;node=(node is Visual || node is System.Windows.Media.Media3D.Visual3D)?VisualTreeHelper.GetParent(node):LogicalTreeHelper.GetParent(node))
                if(node is ButtonBase || node is ScrollBar)return true;
            return false;
        }
        // LoveMyShelf's part of the blank-space menu: View (tile size, the folders' view and place), Sort by, Refresh;
        // then Windows' own part for the folder on screen (New, Paste, Properties, ...). Paste is done by LoveMyShelf.
        internal void ShowBackgroundMenu(FrameworkElement at,ContextMenuEventArgs e) {
            var own=new ContextMenu();
            var view=new MenuItem {Header=L.T("View")};
            foreach(var size in new[]{Tuple.Create(88,"Small tiles"),Tuple.Create(120,"Medium tiles"),Tuple.Create(164,"Large tiles"),Tuple.Create(220,"Extra large tiles")}) {
                int value=size.Item1;var item=new MenuItem {Header=L.T(size.Item2),IsChecked=density==value};
                item.Click+=delegate {density=value;LayoutRows();};view.Items.Add(item);
            }
            view.Items.Add(new Separator());
            // The cells' shape, and whether wide covers take two cells.
            var cells=new MenuItem {Header=L.P("Shape of the cells","칸 모양")};
            for(int i=0;i<Covers.Shapes.Length;i++) {
                int shape=i;var item=new MenuItem {Header=Covers.ShapeLabel(i),IsChecked=settings.TileShape==i};
                item.Click+=delegate {ChangeTileShape(shape);};cells.Items.Add(item);
            }
            cells.Items.Add(new Separator());
            var wide=new MenuItem {Header=L.P("Wide covers take two cells","가로로 긴 표지는 칸 두 개에"),IsChecked=settings.WideCovers};
            wide.Click+=delegate {ChangeWideCovers(!settings.WideCovers);};cells.Items.Add(wide);
            view.Items.Add(cells);
            view.Items.Add(new Separator());
            var folders=new MenuItem {Header=L.T("Folders")};
            var names=new[]{"Extra large icons","Large icons","Medium icons","Small icons","List","Details view","Tiles","Content"};
            for(int i=0;i<names.Length;i++){int choice=i;var item=new MenuItem {Header=L.T(names[i]),IsChecked=settings.FolderView==i};item.Click+=delegate {SaveFolderView(choice,null);};folders.Items.Add(item);}
            folders.Items.Add(new Separator());
            foreach(bool below in new[]{false,true}){bool value=below;var item=new MenuItem {Header=L.T(below?"Folders below the photos":"Folders above the photos"),IsChecked=settings.FoldersBelow==below};item.Click+=delegate {SaveFolderView(null,value);};folders.Items.Add(item);}
            view.Items.Add(folders);
            own.Items.Add(view);
            var sortBy=new MenuItem {Header=L.T("Sort by")};
            var sortMenu=SortMenu();
            foreach(var item in sortMenu.Items.Cast<object>().ToList()){sortMenu.Items.Remove(item);sortBy.Items.Add(item);}
            own.Items.Add(sortBy);
            var refresh=new MenuItem {Header=L.T("Refresh"),InputGestureText="F5"};refresh.Click+=delegate {Refresh();StartCoverMigration();DrivesChangedSoon(0.3);};
            own.Items.Add(refresh);
            var phone=new MenuItem {Header=L.P("Send to phone…","휴대폰으로 보내기…")};phone.Click+=delegate {SendToPhone();};
            own.Items.Add(phone);
            var tell=new MenuItem {Header=L.P("Write to the AI that made this…","이걸 만든 AI한테 적기…")};tell.Click+=delegate {ShowAiWork();};
            own.Items.Add(tell);
            // Paste and Undo as in File Explorer's blank-space menu (Windows' part outside File Explorer has neither).
            own.Items.Add(new Separator());
            bool canPaste=false;try {canPaste=active!=null && active.Source!=null && Clipboard.ContainsFileDropList();} catch(Exception) {}
            var paste=new MenuItem {Header=L.T("Paste"),InputGestureText="Ctrl+V",IsEnabled=canPaste};paste.Click+=delegate {PasteFromClipboard(null);};
            own.Items.Add(paste);
            bool canUndo;lock(moveHistory)canUndo=moveHistory.Count>0;
            var undo=new MenuItem {Header=L.T("Undo move"),InputGestureText="Ctrl+Z",IsEnabled=canUndo};undo.Click+=delegate {UndoMove();};
            own.Items.Add(undo);
            string folder=active!=null?active.Source:null;
            if(folder==null){ShellMenu.Show(this,own,new string[0],MenuPoint(at,e),null,null);return;}
            var take=new ShellMenu.Takeovers {Paste=()=>PasteFromClipboard(null)};
            ISet<string> before=null;
            try {before=new HashSet<string>(Directory.EnumerateDirectories(folder).Select(Path.GetFileName),StringComparer.OrdinalIgnoreCase);}
            catch(IOException) {} catch(UnauthorizedAccessException) {}
            ShellMenu.ShowBackground(this,own,folder,MenuPoint(at,e),take,()=>{
                RefreshSoon();if(tree!=null && treePanel.Visibility==Visibility.Visible){tree.Build();TreeSyncSoon();}
                StartRenameOfNewFolder(folder,before);
            });
        }
        internal async void StartRenameOfNewFolder(string parent,ISet<string> before) {
            if(before==null || lifetime.IsCancellationRequested)return;
            var token=lifetime.Token;
            var timeout=Task.Delay(2000,token);
            // Windows can return from the menu command before the folder exists.
            try {
                do {
                    var reading=Task.Run(()=>Directory.EnumerateDirectories(parent)
                        .Where(path=>!before.Contains(Path.GetFileName(path)) && !before.Contains(path)).Take(2).ToArray());
                    if(await Task.WhenAny(reading,timeout)!=reading || token.IsCancellationRequested)return;
                    var added=await reading;
                    if(added.Length>1)return;
                    if(added.Length==1) {
                        if(active!=null && ShellPlaces.Same(active.Source,parent))ReloadKeepingPlace();
                        RenameFolder(added[0]);
                        return;
                    }
                    await Task.WhenAny(Task.Delay(100,token),timeout);
                } while(!timeout.IsCompleted);
            } catch(IOException) {} catch(UnauthorizedAccessException) {}
            catch(Exception quiet) {ErrorLog.Quiet(quiet);} // never ends the program over a name box that didn't open
        }

        // ---------- Copy, cut and paste (Ctrl+C, Ctrl+X, Ctrl+V), shared with File Explorer and other programs ----------
        // Shared by all windows: cutting in one and pasting in another clears the faded look everywhere, like File Explorer.
        static readonly HashSet<string> cutFolders=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static void ShowCutLookEverywhere() {foreach(var w in Application.Current.Windows.OfType<GalleryWindow>())w.ShowCutLook();}
        internal string OriginalForTransfer(Photo photo) {
            if(!ShowingScope)return photo.Original;
            // The viewer may have resolved another path. Transfers use the entry in the gathered folder.
            try {
                string source=GalleryStore.SourceOfRecords(Path.GetDirectoryName(photo.Folder));
                return Renamer.IsAt(source,ScopeFolder)?Path.Combine(source,Path.GetFileName(photo.Name)):null;
            } catch(IOException) {return null;} catch(UnauthorizedAccessException) {return null;}
        }
        // The files of these tiles: originals in a folder, the cover files in Cover collection.
        List<string> ClipboardFiles(IList<Photo> targets) {
            var files=ShowingAllCrops?targets.Select(p=>p.ActiveCoverFile):targets.Where(p=>p.Linked).Select(OriginalForTransfer);
            return files.Where(f=>f!=null && File.Exists(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        internal void CopyToClipboard(bool cut,IList<Photo> targets=null) {
            targets=targets??Targets(FocusedOrOnly());
            var files=ClipboardFiles(targets);if(files.Count==0)return;
            if(ShowingAllCrops)cut=false; // a cover file is copied; covers leave only with their original
            var list=new System.Collections.Specialized.StringCollection();list.AddRange(files.ToArray());
            var data=new DataObject();data.SetFileDropList(list);
            // The same mark File Explorer reads: 2 = move (cut), 5 = copy.
            data.SetData("Preferred DropEffect",new MemoryStream(BitConverter.GetBytes(cut?2:5)));
            for(int tries=0;;tries++) {
                try {Clipboard.SetDataObject(data,true);break;}
                catch(System.Runtime.InteropServices.COMException) {if(tries>=4)return;Thread.Sleep(60);}
            }
            cutFolders.Clear();
            if(cut)foreach(var p in targets){cutFolders.Add(p.Folder);if(p.LeadFolder!=null)cutFolders.Add(p.LeadFolder);}
            ShowCutLookEverywhere();
        }
        // Cut tiles look faded until they are pasted, like File Explorer.
        void ShowCutLook() {foreach(var pair in shownTiles)pair.Value.Opacity=cutFolders.Contains(pair.Key)?0.45:1;}
        // Pastes the files on the clipboard into this folder (or into the given folder).
        internal void PasteFromClipboard(string folder) {
            Album target;
            if(folder!=null){try {target=store.OpenSubfolder(folder);} catch(Exception ex) {UI.Error(this,ex);return;}}
            else target=active;
            if(target==null || target.Source==null) {
                Notify(L.T("Nothing can be pasted here"),L.T("Open a folder first. The cover collections aren't folders."),null,NoticeKind.Info);return;
            }
            System.Collections.Specialized.StringCollection list;int effect=5;
            try {
                if(!Clipboard.ContainsFileDropList())return;
                list=Clipboard.GetFileDropList();
                var mark=Clipboard.GetData("Preferred DropEffect") as MemoryStream;
                if(mark!=null && mark.Length>=4){var bytes=new byte[4];mark.Position=0;mark.Read(bytes,0,4);effect=BitConverter.ToInt32(bytes,0);}
            } catch(Exception) {return;}
            bool move=(effect&2)!=0 && (effect&1)==0;
            // Folders are pasted too, with everything inside them.
            var files=list.Cast<string>().Where(p=>File.Exists(p) || Directory.Exists(p)).ToArray();
            if(files.Length==0)return;
            MoveOriginals(files,!move,target);
            if(move){try {Clipboard.Clear();} catch(Exception) {}cutFolders.Clear();ShowCutLookEverywhere();}
        }

        // ---------- Range selection (a rectangle drawn with the right button, or the left one from empty space) ----------
        readonly Canvas marqueeLayer=new Canvas {IsHitTestVisible=false,ClipToBounds=true};
        readonly Grid marqueeBox=new Grid {Visibility=Visibility.Collapsed};
        readonly TextBlock marqueeCount=new TextBlock {FontSize=12,FontWeight=FontWeights.SemiBold,Padding=new Thickness(6,2,6,2)};
        readonly Border marqueeLabel=new Border {CornerRadius=new CornerRadius(4),Visibility=Visibility.Collapsed};
        readonly DispatcherTimer marqueeScroll=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(30)};
        bool marqueePending,marqueeActive;MouseButton marqueeButton;Point marqueeStart;HashSet<string> marqueeBase=new HashSet<string>();
        void SetupMarquee(Grid host) {
            var fill=new Border {Opacity=0.16};fill.SetResourceReference(Border.BackgroundProperty,"Accent");
            var edge=new Border {BorderThickness=new Thickness(1)};edge.SetResourceReference(Border.BorderBrushProperty,"Accent");
            marqueeBox.Children.Add(fill);marqueeBox.Children.Add(edge);marqueeLayer.Children.Add(marqueeBox);
            marqueeLabel.SetResourceReference(Border.BackgroundProperty,"CardBack");marqueeCount.SetResourceReference(TextBlock.ForegroundProperty,"Text");
            marqueeLabel.Child=marqueeCount;marqueeLayer.Children.Add(marqueeLabel);
            Panel.SetZIndex(marqueeLayer,5);host.Children.Add(marqueeLayer);
            // Until range selection has been used a few times, a small note in the corner of the photos keeps reminding
            // of it (it fits even in half a screen). × hides it until the next start.
            var close=new Button {Content="×",Padding=new Thickness(6,0,2,0),BorderThickness=new Thickness(0),Background=Brushes.Transparent,
                FontSize=13,MinHeight=0,VerticalAlignment=VerticalAlignment.Center,Cursor=Cursors.Hand};
            close.SetResourceReference(Control.ForegroundProperty,"Subtle");
            close.Click+=delegate {rangeHintClosed=true;ShowRangeHint();};
            var line=new StackPanel {Orientation=Orientation.Horizontal};
            marqueeHint.VerticalAlignment=VerticalAlignment.Center;line.Children.Add(marqueeHint);line.Children.Add(close);
            rangeHint.Child=line;rangeHint.SetResourceReference(Border.BackgroundProperty,"CardBack");rangeHint.SetResourceReference(Border.BorderBrushProperty,"Line");
            Panel.SetZIndex(rangeHint,4);host.Children.Add(rangeHint);
            marqueeScroll.Tick+=delegate {
                var scroller=FindScroller(grid);if(scroller==null || !marqueeActive)return;
                var at=Mouse.GetPosition(scroller);
                if(at.Y<24)scroller.ScrollToVerticalOffset(scroller.VerticalOffset-Math.Max(6,24-at.Y));
                else if(at.Y>scroller.ViewportHeight-24)scroller.ScrollToVerticalOffset(scroller.VerticalOffset+Math.Max(6,at.Y-scroller.ViewportHeight+24));
                else return;
                MarqueeDraw(at);
            };
        }
        // Where a point is on the whole (scrolled) list of tiles.
        static Point MarqueeContent(Point onScroller,ScrollViewer scroller) {return new Point(onScroller.X,onScroller.Y+scroller.VerticalOffset);}
        void MarqueeArm(MouseButtonEventArgs e,MouseButton button) {
            var scroller=FindScroller(grid);if(scroller==null || rows==null || columns<=0)return;
            var at=e.GetPosition(scroller);
            if(at.X<0 || at.Y<0 || at.X>scroller.ViewportWidth || at.Y>scroller.ViewportHeight)return;
            marqueePending=true;marqueeButton=button;marqueeStart=MarqueeContent(at,scroller);
        }
        void MarqueeMove(MouseEventArgs e) {
            if(!marqueePending && !marqueeActive)return;
            var pressed=marqueeButton==MouseButton.Right?e.RightButton:e.LeftButton;
            if(pressed!=MouseButtonState.Pressed){marqueePending=false;if(marqueeActive)MarqueeEnd(false);return;}
            var scroller=FindScroller(grid);if(scroller==null)return;
            var at=e.GetPosition(scroller);
            if(!marqueeActive) {
                var now=MarqueeContent(at,scroller);
                if(Math.Abs(now.X-marqueeStart.X)<4 && Math.Abs(now.Y-marqueeStart.Y)<4)return;
                marqueeActive=true;marqueePending=false;
                marqueeBase=(Keyboard.Modifiers&ModifierKeys.Control)!=0?new HashSet<string>(selected,StringComparer.OrdinalIgnoreCase):new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                grid.CaptureMouse();marqueeScroll.Start();HideName();
            }
            MarqueeDraw(at);e.Handled=true;
        }
        void MarqueeDraw(Point at) {
            var scroller=FindScroller(grid);if(scroller==null || columns<=0 || rowHeight<=0)return;
            var now=MarqueeContent(at,scroller);
            double left=Math.Min(marqueeStart.X,now.X),right=Math.Max(marqueeStart.X,now.X),top=Math.Min(marqueeStart.Y,now.Y),bottom=Math.Max(marqueeStart.Y,now.Y);
            left=Math.Max(0,left);right=Math.Min(scroller.ViewportWidth,right);top=Math.Max(0,top);
            // Tiles touched by the rectangle, worked out from their places (tiles off screen count too).
            double cell=scroller.ViewportWidth/columns;
            var hits=new HashSet<string>(StringComparer.OrdinalIgnoreCase);int lastHit=-1;
            int firstRow=(int)Math.Floor(top/rowHeight),lastRow=(int)Math.Floor(Math.Max(top,bottom-0.01)/rowHeight);
            int firstColumn=Math.Max(0,(int)Math.Floor(left/cell)),lastColumn=Math.Min(columns-1,(int)Math.Floor(Math.Max(left,right-0.01)/cell));
            for(int r=Math.Max(folderRows,firstRow);r<=lastRow;r++)
                for(int c=firstColumn;c<=lastColumn;c++) {
                    int index=(r-folderRows)*columns+c;
                    if(IsWideTail(index))index--;
                    if(index<0 || index>=photos.Count || photos[index]==null)continue;
                    hits.Add(photos[index].Folder);lastHit=index;
                }
            selected.Clear();
            foreach(var f in marqueeBase)if(!hits.Contains(f))selected.Add(f);
            foreach(var f in hits)if(!marqueeBase.Contains(f))selected.Add(f);
            if(lastHit>=0){focusIndex=lastHit;anchorIndex=lastHit;}
            RefreshSelection();
            // The rectangle on screen (in the layer over the tiles).
            var a=scroller.TranslatePoint(new Point(left,top-scroller.VerticalOffset),marqueeLayer);
            var b=scroller.TranslatePoint(new Point(right,bottom-scroller.VerticalOffset),marqueeLayer);
            Canvas.SetLeft(marqueeBox,a.X);Canvas.SetTop(marqueeBox,a.Y);
            marqueeBox.Width=Math.Max(1,b.X-a.X);marqueeBox.Height=Math.Max(1,b.Y-a.Y);marqueeBox.Visibility=Visibility.Visible;
            marqueeCount.Text=L.F("{0:N0} selected",selected.Count);marqueeLabel.Visibility=Visibility.Visible;
            var pointer=scroller.TranslatePoint(at,marqueeLayer);
            Canvas.SetLeft(marqueeLabel,pointer.X+14);Canvas.SetTop(marqueeLabel,pointer.Y+16);
        }
        void MarqueeUp(MouseButtonEventArgs e,MouseButton button) {
            if(button!=marqueeButton)return;
            marqueePending=false;
            if(!marqueeActive)return;
            MarqueeEnd(false);e.Handled=true; // the right button's menu doesn't open after drawing
        }
        void MarqueeEnd(bool cancel) {
            if(!marqueeActive)return;
            marqueeActive=false;marqueeScroll.Stop();
            if(grid.IsMouseCaptured)grid.ReleaseMouseCapture();
            marqueeBox.Visibility=marqueeLabel.Visibility=Visibility.Collapsed;
            if(cancel){selected.Clear();foreach(var f in marqueeBase)selected.Add(f);RefreshSelection();return;}
            if(marqueeButton==MouseButton.Right && settings.MarqueeUses<1000) {
                var updated=settings.Copy();updated.MarqueeUses++;
                try {updated.Save(store.Root);settings=updated;} catch(Exception) {}
                updateFooter();
            }
        }
        // ----- Telling people about it, often enough (it isn't the usual way) -----
        bool marqueeTipThisRun,rangeHintClosed;DateTime lastDragTip=DateTime.MinValue;
        readonly Border rangeHint=new Border {CornerRadius=new CornerRadius(12),Padding=new Thickness(12,5,6,5),BorderThickness=new Thickness(1),
            HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Bottom,Margin=new Thickness(12,12,24,12),Visibility=Visibility.Collapsed};
        void ShowRangeHint() {
            marqueeHint.Text=L.T("Hold the right button and drag to select many");
            rangeHint.Visibility=settings.MarqueeUses<3 && !rangeHintClosed && photos.Count>1?Visibility.Visible:Visibility.Collapsed;
        }
        void MarqueeTip() {
            if(settings.MarqueeUses>=3)return;
            Notify(L.T("To select many at once"),L.T("Hold the right mouse button and drag over the tiles. Every tile the rectangle touches is selected. (Starting from an empty spot, the left button works too.)"),null,NoticeKind.Info);
        }
        // A tile was dragged with the left button and let go where nothing takes it: probably meant to choose a range.
        internal void NoteDragWithoutDrop() {
            if(settings.MarqueeUses>=3 || (DateTime.UtcNow-lastDragTip).TotalSeconds<60)return;
            lastDragTip=DateTime.UtcNow;
            Notify(L.T("To choose a range, use the right button"),L.T("Hold the right mouse button and drag over the tiles. Dragging with the left button moves the files."),null,NoticeKind.Info);
        }

        // ---------- Explorer state: selection, keys, search, rename, details ----------
        readonly HashSet<string> selected=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int focusIndex=-1,anchorIndex=-1;
        readonly Dictionary<string,Button> shownTiles=new Dictionary<string,Button>(StringComparer.OrdinalIgnoreCase);
        readonly TextBox search=new TextBox {Width=170,Padding=new Thickness(8,4,8,4),VerticalContentAlignment=VerticalAlignment.Center};
        readonly TextBlock searchHint=UI.Text("",12,true);
        readonly DispatcherTimer searchTimer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(250)};
        internal IEnumerable<Photo> SelectedPhotos {get {return photos.Where(p=>p!=null && selected.Contains(p.Folder));}}
        readonly Button searchButton=new Button {MinHeight=27,Padding=new Thickness(7,4,7,4)};
        readonly Grid searchField=new Grid {VerticalAlignment=VerticalAlignment.Center,Visibility=Visibility.Collapsed};
        internal bool SearchOpen {get {return searchField.Visibility==Visibility.Visible;}}
        // Name search is a magnifier button; it opens into a box only while it is being used, so the folder tabs keep the room.
        FrameworkElement SearchBox() {
            var host=new Grid {VerticalAlignment=VerticalAlignment.Stretch};
            searchButton.Content=LineIcon("M 7.5,2 A 5.5,5.5 0 1 1 7.49,2 Z M 11.4,11.4 L 16,16");
            searchButton.Click+=delegate {OpenSearch();};
            // Rounded like the buttons around it; the edge lights up while typing.
            search.BorderThickness=new Thickness(0);search.Background=Brushes.Transparent;
            searchHint.IsHitTestVisible=false;searchHint.Margin=new Thickness(10,0,0,0);searchHint.VerticalAlignment=VerticalAlignment.Center;
            var frame=new Border {CornerRadius=new CornerRadius(8),BorderThickness=new Thickness(1),MinHeight=27,Padding=new Thickness(0,1,2,1)};
            frame.SetResourceReference(Border.BackgroundProperty,"Panel");frame.SetResourceReference(Border.BorderBrushProperty,"Line");
            search.IsKeyboardFocusWithinChanged+=delegate {frame.SetResourceReference(Border.BorderBrushProperty,search.IsKeyboardFocusWithin?"Accent":"Line");};
            searchField.VerticalAlignment=VerticalAlignment.Stretch;
            frame.Child=search;searchField.Children.Add(frame);searchField.Children.Add(searchHint);
            host.Children.Add(searchButton);host.Children.Add(searchField);
            search.TextChanged+=delegate {
                CloseFormatList();
                searchHint.Visibility=search.Text.Length==0?Visibility.Visible:Visibility.Collapsed;
                searchTimer.Stop();searchTimer.Start();
                // A search typed in stays open, so a hidden word never quietly empties the list.
                if(search.Text.Length>0 && !SearchOpen)ShowSearchField(true);
                else if(search.Text.Length==0 && !search.IsKeyboardFocusWithin)ShowSearchField(false);
            };
            searchTimer.Tick+=delegate {searchTimer.Stop();Refresh();};
            search.PreviewKeyDown+=(s,e)=>{
                if(e.Key==Key.Escape){search.Text="";ShowSearchField(false);grid.Focus();e.Handled=true;}
                else if(e.Key==Key.Enter || e.Key==Key.Down){grid.Focus();if(focusIndex<0)MoveFocus(0,false);e.Handled=true;}
            };
            search.LostKeyboardFocus+=(s,e)=>{
                if(e.NewFocus is MenuItem || e.NewFocus is ContextMenu)return; // right-click menu of the box (Paste)
                if(search.Text.Length==0)ShowSearchField(false);
            };
            // Clicking anywhere else while the box is still empty folds it back, like the focus moving away.
            PreviewMouseDown+=(s,e)=>{
                if(!SearchOpen || search.Text.Length>0)return;
                var node=e.OriginalSource as DependencyObject;
                while(node!=null && node!=host)node=(node is Visual || node is System.Windows.Media.Media3D.Visual3D)?VisualTreeHelper.GetParent(node):LogicalTreeHelper.GetParent(node);
                if(node==null)ShowSearchField(false);
            };
            return host;
        }
        void ShowSearchField(bool open) {
            searchField.Visibility=open?Visibility.Visible:Visibility.Collapsed;
            searchButton.Visibility=open?Visibility.Collapsed:Visibility.Visible;
            if(updateTopBar!=null)updateTopBar();
        }
        internal void OpenSearch() {
            ShowSearchField(true);
            Dispatcher.BeginInvoke(new Action(()=>{search.Focus();Keyboard.Focus(search);search.SelectAll();}),DispatcherPriority.Input);
        }
        // A small line drawing in the text color (works in both themes).
        static System.Windows.Shapes.Path LineIcon(string drawing) {
            var path=new System.Windows.Shapes.Path {Data=Geometry.Parse(drawing),Width=18,Height=18,StrokeThickness=1.6,
                StrokeLineJoin=PenLineJoin.Round,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,
                Stretch=Stretch.None,IsHitTestVisible=false,SnapsToDevicePixels=true};
            path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,"Text");
            return path;
        }
        internal void TrackTile(Button tile,Photo photo) {
            shownTiles[photo.Folder]=tile;
            if(cutFolders.Contains(photo.Folder))tile.Opacity=0.45;
            tile.MouseEnter+=delegate {
                if(photo.SimilarFile!=null && photo.Similar!=null)ShowNameSoon(tile,SimilarInfo.Card(photo.Similar,photo.SimilarFile,photo.SimilarCovers),true);
                else if(ShowingScope) {
                    string top=ScopeFolder.TrimEnd('\\'),folder=Path.GetDirectoryName(photo.Original);
                    string relative=ShellPlaces.Same(folder,top)?L.T("In the top folder"):
                        Renamer.IsAt(folder,top)?folder.Substring(top.Length+1):folder;
                    ShowNameSoon(tile,photo.Name+Environment.NewLine+relative,true);
                }
                else if(!(settings.TileNames==3 && wholeNamesFit))ShowNameSoon(tile,photo.Name); // already written in full under the tile
                CoverSibling mark;LightCoverFamily(coverMarks.TryGetValue(photo.Folder,out mark)?mark.Family:null);
            };
            tile.MouseLeave+=delegate {if(nameTile==tile)HideName();LightCoverFamily(null);};
            tile.ContextMenuOpening+=delegate {HideName();};
            tile.Unloaded+=delegate {Button current;if(shownTiles.TryGetValue(photo.Folder,out current) && current==tile)shownTiles.Remove(photo.Folder);};
            ShowSelected(tile,selected.Contains(photo.Folder),focusIndex>=0 && focusIndex<photos.Count && photos[focusIndex]==photo);
            PaintCoverMark(tile,photo.Folder);
        }
        // ---------- Covers of the same original: their numbers and the outline on hover ----------
        sealed class CoverSibling {internal string Family;internal int Number,Total;}
        readonly Dictionary<string,CoverSibling> coverMarks=new Dictionary<string,CoverSibling>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string,List<Photo>> coverFamilies=new Dictionary<string,List<Photo>>(StringComparer.OrdinalIgnoreCase);
        string litFamily;
        bool coverMarksQueued;
        readonly Dictionary<string,string> coverTimes=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        internal static string SceneClock(double ms) {
            long seconds=(long)Math.Floor(Math.Max(0,ms)/1000);
            return seconds>=3600?String.Format("{0}:{1:00}:{2:00}",seconds/3600,seconds/60%60,seconds%60):String.Format("{0}:{1:00}",seconds/60,seconds%60);
        }
        void UpdateCoverMarks() {
            coverMarks.Clear();coverFamilies.Clear();coverTimes.Clear();
            if(settings.CoverTimes) {
                // Only covers made here from a video have a scene time worth showing; a cover from an outside picture has none.
                foreach(var p in photos.Where(p=>p!=null && p.HasCrop && !p.ExcludedFromCollection && !p.IsOutside && p.SceneMs.HasValue && VideoFrames.Supports(p.Original)))
                    coverTimes[p.Folder]=SceneClock(p.SceneMs.Value);
            }
            foreach(var group in photos.Where(p=>p!=null && p.HasCrop && !p.ExcludedFromCollection).GroupBy(p=>GalleryStore.CoverFamily(p),StringComparer.OrdinalIgnoreCase)) {
                var covers=GalleryStore.InCoverOrder(group);
                if(covers.Count<2)continue;
                coverFamilies[group.Key]=covers;
                for(int i=0;i<covers.Count;i++)coverMarks[covers[i].Folder]=new CoverSibling {Family=group.Key,Number=i+1,Total=covers.Count};
            }
            if(litFamily!=null && !coverFamilies.ContainsKey(litFamily))litFamily=null;
            PaintCoverMarks();
        }
        // Several tiles can change in one go (hiding covers, for example); the numbers are worked out once afterwards.
        void CoverMarksSoon() {
            if(coverMarksQueued)return;
            coverMarksQueued=true;
            Dispatcher.BeginInvoke(new Action(()=>{coverMarksQueued=false;UpdateCoverMarks();}),DispatcherPriority.Background);
        }
        void PaintCoverMarks() {foreach(var pair in shownTiles)PaintCoverMark(pair.Value,pair.Key);}
        void PaintCoverMark(Button tile,string folder) {
            CoverSibling mark;
            bool has=coverMarks.TryGetValue(folder,out mark);
            tile.SetValue(TileMark.NumberProperty,has?mark.Number+"/"+mark.Total:null);
            string clockText;
            tile.SetValue(TileMark.TimeProperty,coverTimes.TryGetValue(folder,out clockText)?clockText:null);
            tile.SetValue(TileMark.LitProperty,has && String.Equals(mark.Family,litFamily,StringComparison.OrdinalIgnoreCase));
        }
        void LightCoverFamily(string family) {
            if(String.Equals(litFamily,family,StringComparison.OrdinalIgnoreCase))return;
            litFamily=family;PaintCoverMarks();
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
            grid.MouseLeave+=delegate {LightCoverFamily(null);};
            Deactivated+=delegate {LightCoverFamily(null);};
            Panel.SetZIndex(nameLayer,5);host.Children.Add(nameLayer);
            nameTimer.Tick+=delegate {nameTimer.Stop();PlaceName();};
            grid.AddHandler(ScrollViewer.ScrollChangedEvent,new ScrollChangedEventHandler((s,e)=>{if(e.VerticalChange!=0)HideName();}));
            Deactivated+=delegate {HideName();};
        }
        internal void ShowNameSoon(Button tile,string name,bool details=false) {
            // Korean words stay whole when the name wraps onto a second line.
            nameTile=tile;nameText.Text=NoticeLook.KeepWords(name);
            nameText.TextAlignment=details?TextAlignment.Left:TextAlignment.Center; // the "Similar photos" card lists facts line by line
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
            if(!SelectingByClick && selected.Count>1){count.Text=L.F("{0:N0} selected",selected.Count);updateFooter();}
            else SetCount(skippedCount);
            // Choosing several tiles one by one: say once per run that the right button can draw a range.
            if(selected.Count>=2 && !marqueeActive && !marqueeTipThisRun && settings.MarqueeUses<3){marqueeTipThisRun=true;MarqueeTip();}
        }
        internal void ClearSelection() {
            selected.Clear();focusIndex=-1;anchorIndex=-1;
            if(shownTiles.Count>0 || SelectingByClick)RefreshSelection();
        }
        // A plain click selects the tile and a double click (or Enter) opens the large view, like File Explorer;
        // with "One click" chosen in the options a plain click opens it. Ctrl adds or removes, Shift selects a range.
        int lastClickCount=1;
        internal void NoteClickCount(int count) {lastClickCount=count;}
        internal void TileClicked(Photo photo) {
            int index=photos.IndexOf(photo);if(index<0)return;
            int clicks=lastClickCount;lastClickCount=1;
            var modifiers=Keyboard.Modifiers;
            bool control=(modifiers&ModifierKeys.Control)!=0,shift=(modifiers&ModifierKeys.Shift)!=0;
            if(SelectingByClick && !control && !shift && clicks>=2) {
                if(!SimilarOpen(photo))OpenPhoto(photo);
                return;
            }
            if(control || (SelectingByClick && !shift)) {
                if(!selected.Remove(photo.Folder))selected.Add(photo.Folder);
                focusIndex=anchorIndex=index;RefreshSelection();return;
            }
            if(shift && anchorIndex>=0) {
                SelectRange(anchorIndex,index);focusIndex=index;RefreshSelection();return;
            }
            selected.Clear();focusIndex=anchorIndex=index;
            if(settings.OpenByDoubleClick) {
                selected.Add(photo.Folder);RefreshSelection();
                if(clicks>=2 && !SimilarOpen(photo))OpenPhoto(photo);
                return;
            }
            // One-click mode only remembers where you are (for the arrow keys); it doesn't leave a selection behind.
            RefreshSelection();
            if(!SimilarOpen(photo))OpenPhoto(photo);
        }
        void SelectRange(int from,int to) {
            selected.Clear();
            for(int i=Math.Min(from,to);i<=Math.Max(from,to);i++)if(photos[i]!=null)selected.Add(photos[i].Folder);
        }
        void MoveFocus(int target,bool extend) {
            if(photos.Count==0)return;
            target=Math.Max(0,Math.Min(photos.Count-1,target));
            // Up or down onto the second cell of a wide tile: that tile. Onto the empty cell left at the end of a row
            // before a wide tile: the nearest tile in that row. Left or right steps over them (below).
            if(Math.Abs(target-focusIndex)>1 && columns>0 && photos[target]==null) {
                if(IsWideTail(target))target--;
                else {
                    int start=target-target%columns,end=Math.Min(photos.Count-1,start+columns-1),best=-1;
                    for(int d=1;d<columns && best<0;d++) {
                        if(target-d>=start && photos[target-d]!=null)best=target-d;
                        else if(target+d<=end && photos[target+d]!=null)best=target+d;
                    }
                    if(best>=0)target=best;
                }
            }
            // Skip empty slots in the direction of travel.
            int step=target>=focusIndex?1:-1;
            while(target>=0 && target<photos.Count && photos[target]==null)target+=step;
            if(target<0 || target>=photos.Count)return;
            focusIndex=target;
            if(extend && anchorIndex>=0)SelectRange(anchorIndex,target);
            else {selected.Clear();selected.Add(photos[target].Folder);anchorIndex=target;}
            if(rows!=null && columns>0)grid.ScrollIntoView(rows[folderRows+target/columns]);
            RefreshSelection();
        }
        void ExplorerKeys(object sender,KeyEventArgs e) {
            // Typing in a text box (search, the address bar, renaming): its keys stay there, never on the tiles.
            if(e.Handled || search.IsKeyboardFocusWithin || (aiWork!=null && aiWork.Typing) || Keyboard.FocusedElement is TextBox || (address!=null && address.Editing) || !IsActiveWindowForKeys)return;
            var key=e.Key==Key.ImeProcessed?e.ImeProcessedKey:e.Key;
            bool shift=(Keyboard.Modifiers&ModifierKeys.Shift)!=0,control=(Keyboard.Modifiers&ModifierKeys.Control)!=0;
            switch(key) {
                case Key.Escape:
                    if(SelectingByClick) {
                        if(marqueeActive)MarqueeEnd(true);
                        selectionToggle.IsChecked=false;break;
                    }
                    if(marqueeActive){MarqueeEnd(true);break;}
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
                    if(!SimilarOpen(photos[focusIndex]))OpenPhoto(photos[focusIndex]);break;
                // In the similar photos, double-click and Enter compare; Space opens the large view.
                case Key.Space:
                    if(!ShowingSimilar || focusIndex<0 || photos[focusIndex]==null)return;
                    OpenPhoto(photos[focusIndex]);break;
                case Key.A:
                    if(!control)return;
                    selected.Clear();foreach(var p in photos)if(p!=null)selected.Add(p.Folder);
                    anchorIndex=0;RefreshSelection();break;
                case Key.Z:
                    if(!control)return;
                    UndoMove();break;
                // Copy, cut and paste files through the Windows clipboard, like File Explorer.
                case Key.C:
                    if(!control)return;
                    CopyToClipboard(false);break;
                case Key.X:
                    if(!control)return;
                    CopyToClipboard(true);break;
                case Key.V:
                    if(!control)return;
                    PasteFromClipboard(null);break;
                case Key.F2:
                    string chosenFolder=FolderForRename();
                    if(chosenFolder!=null){RenameFolder(chosenFolder);break;}
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
            if(ShowingSimilar){SimilarRecycle(targets);return;}
            bool covers=ShowingAllCrops;
            // An original with several cover tiles is one original.
            int items=covers?targets.Count:targets.Where(p=>p.Linked).GroupBy(p=>Path.GetDirectoryName(p.Folder)+"|"+p.Name,StringComparer.OrdinalIgnoreCase).Count();
            if(items>1 && MessageBox.Show(this,L.F(covers?"Move {0:N0} cover files to the Recycle Bin?":"Move {0:N0} originals to the Recycle Bin?",items),
                "LoveMyShelf",MessageBoxButton.OKCancel,MessageBoxImage.Question)!=MessageBoxResult.OK)return;
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
            var withCovers=targets.Where(p=>p.HasCrop && !p.ExcludedFromCollection).ToList();
            if(withCovers.Count>0) {
                var hide=new MenuItem {Header=L.F(withCovers.Any(p=>p.HasMotion)?"Hide {0:N0} covers (both JPG and GIF)":"Hide {0:N0} covers",withCovers.Count)};
                hide.Click+=delegate {ChangeCollections(withCovers);ClearSelection();};
                menu.Items.Add(hide);
                menu.Items.Add(new Separator());
            }
            var copy=new MenuItem {Header=L.F("Copy {0:N0} items",targets.Count)};
            copy.Click+=delegate {ShareTiles.RunMany(this,targets,false);};
            var share=new MenuItem {Header=L.F("Share {0:N0} items…",targets.Count)};
            share.Click+=delegate {ShareTiles.RunMany(this,targets,true);};
            menu.Items.Add(copy);menu.Items.Add(share);
            if(settings.Share!=ShareContent.OriginalOnly && targets.Any(p=>p.HasCrop && p.HasMotion))
                menu.Items.Add(ShareTiles.CoverKindsMenu(menu,this));
            AddTagItems(menu,targets.Where(p=>p.Linked).ToList());
            menu.Items.Add(new Separator()); // the Recycle Bin stays at the bottom: here it can mean the originals
            int originals=ShowingAllCrops?targets.Count:targets.GroupBy(p=>Path.GetDirectoryName(p.Folder)+"|"+p.Name,StringComparer.OrdinalIgnoreCase).Count();
            var delete=new MenuItem {Header=L.F(ShowingAllCrops?"Move {0:N0} cover files to the Recycle Bin":"Move {0:N0} originals to the Recycle Bin",originals),InputGestureText="Delete"};
            delete.Click+=delegate {DeleteSelected();};
            menu.Items.Add(delete);
            var clear=new MenuItem {Header=L.T("Clear selection"),InputGestureText="Esc"};
            clear.Click+=delegate {ClearSelection();};
            menu.Items.Add(clear);
            return menu;
        }
        // ----- Tags -----
        // [Tags…] in a tile's right-click menu, and in a tag's view also [Take the tag off].
        internal void AddTagItems(ContextMenu menu,List<Photo> targets) {
            if(targets.Count==0)return;
            var tags=new MenuItem {Header=L.T("Tags…")};
            tags.Click+=delegate {EditTags(targets);};
            menu.Items.Add(tags);
            string shown=ShownTag;
            if(shown==null)return;
            var off=new MenuItem {Header=L.F("Take the tag “{0}” off",shown).Replace("_","__")};
            off.Click+=delegate {
                try {Tags.Change(targets,null,new[]{shown});ClearSelection();} catch(Exception ex) {UI.Error(this,ex);}
            };
            menu.Items.Add(off);
        }
        internal void EditTags(List<Photo> targets) {
            if(targets.Count==0)return;
            new TagWindow(this,targets).ShowDialog();
        }
        void RenameShownTag() {
            string tag=ShownTag;if(tag==null)return;
            var ask=new TagNameWindow(this,L.T("Rename tag"),L.T("New name of the tag"),tag,L.T("Rename"));
            if(ask.ShowDialog()!=true || ask.Value==tag)return;
            try {
                Tags.Rename(tag,ask.Value);FaceNames.Rename(store,tag,ask.Value);
                historyMove=true;
                try {Select(TagAlbum(ask.Value));} finally {historyMove=false;}
            } catch(Exception ex) {UI.Error(this,ex);}
        }
        void DeleteShownTag() {
            string tag=ShownTag;if(tag==null)return;
            int files=Tags.FilesWith(tag).Sum(f=>f.Value.Count);
            if(MessageBox.Show(this,L.F("Take the tag “{0}” off {1:N0} files and delete it?\nThe files themselves stay where they are.",tag,files),
                "LoveMyShelf",MessageBoxButton.OKCancel,MessageBoxImage.Question)!=MessageBoxResult.OK)return;
            try {Tags.Delete(tag);FaceNames.Rename(store,tag,null);Refresh();} catch(Exception ex) {UI.Error(this,ex);}
        }
        // The guide, opened on the section for where you are.
        internal void ShowGuide(string section,Window owner=null) {GuideWindow.Open(owner??this,section);}
        // moving: the GIF cover's file instead of the JPG cover's (2026-11-27 user: "gif jpg 구분을 해라").
        // A GIF asked for but gone: its own path, so the person is told it is missing instead of being shown the JPG (DeepSeek).
        internal static string LocationOf(Photo photo,bool cover,bool moving) {return !cover?photo.Original:moving && photo.MotionFile!=null?photo.MotionFile:photo.ActiveCoverFile;}
        internal void ShowInExplorer(Photo photo,bool cover,bool moving=false) {
            try {
                string path=LocationOf(photo,cover,moving);
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
            if(CoverWork.Holds(photo)){TellFileInCoverWork();return;}
            var dialog=new RenameWindow(this,photo.Name);
            if(dialog.ShowDialog()!=true || dialog.NewName==photo.Name)return;
            FinishPendingCovers();
            string newName=dialog.NewName;
            string albumFolder=Path.GetDirectoryName(photo.Folder),oldBase=GalleryStore.RecordBase(photo);
            string newBase="p-"+GalleryStore.KeyOf(newName);
            try {
                string newPath=await Task.Run(()=>Renamer.Rename(store,photo,newName));
                if(lifetime.IsCancellationRequested)return;
                if(ShowingScope && Renamer.IsAt(newPath,ScopeFolder))ReloadKeepingPlace();
                else for(int i=0;i<photos.Count;i++) {
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
                Notify(L.T("Renamed"),L.F("“{0}” → “{1}”",photo.Name,newName),null);
            } catch(Exception ex) {
                Notify(L.T("Couldn’t rename"),ex.Message,null,NoticeKind.Warning);
            }
        }
        // The folder F2 means: the row chosen in the tree or the one folder chosen in the folders area, when the keyboard is there.
        string FolderForRename() {
            string path=null;
            if(tree!=null && tree.IsKeyboardFocusWithin && tree.Current!=null)path=tree.Current.Path;
            else if(folderPane!=null && folderPane.IsKeyboardFocusWithin)path=folderPane.OnlyChosen;
            return path!=null && Renamer.CanRenameFolder(path)?path:null;
        }
        static bool renamingFolder;
        internal async void RenameFolder(string path) {
            if(renamingFolder || !Renamer.CanRenameFolder(path))return;
            string from=Path.GetFullPath(path).TrimEnd('\\'),oldName=Path.GetFileName(from);
            var windows=Application.Current.Windows.OfType<Window>().ToList();
            if(TransferQueue.Busy || windows.OfType<GalleryWindow>().Any(w=>w.Upscales.IsBusy || w.migratingCovers) || windows.OfType<UpscaleToast>().Any(t=>t.IsWorking)) {
                if(TransferQueue.Busy)TransferWindow.ShowQueue(false);
                Notify(L.T("Some work is still in progress"),L.T("Rename the folder after the work in progress is done."),null,NoticeKind.Info);return;
            }
            // An open picture, video or editor is holding on to the records inside.
            var open=windows.FirstOrDefault(w=>w.IsVisible && !(w is GalleryWindow) && !(w is UpscaleToast) && !(w is VideoOverlay) && !(w is GuideWindow) && !(w is TransferWindow));
            if(open!=null) {
                open.Activate();
                Notify(L.T("Close this window first"),L.T("Close the open picture or video window, then rename the folder."),null,NoticeKind.Info);return;
            }
            var dialog=new RenameWindow(this,oldName,true);
            if(dialog.ShowDialog()!=true || dialog.NewName==oldName)return;
            string newName=dialog.NewName;
            var galleries=Application.Current.Windows.OfType<GalleryWindow>().ToList();
            foreach(var gallery in galleries)gallery.FinishPendingCovers();
            renamingFolder=true;
            try {
                string to=await Task.Run(()=>Renamer.RenameFolder(store,from,newName));
                foreach(var gallery in galleries)if(!gallery.lifetime.IsCancellationRequested)gallery.FolderRenamed(from,to);
                Notify(L.T("Renamed"),L.F("“{0}” → “{1}”",oldName,newName),null);
            } catch(Exception ex) {
                Notify(L.T("Couldn’t rename"),ex.Message,null,NoticeKind.Warning);
            } finally {renamingFolder=false;}
        }
        // Every window follows the new name without moving: same folder on screen, same place in the list.
        void FolderRenamed(string from,string to) {
            if(Renamer.IsAt(settings.CoverDirectory,from))settings.CoverDirectory=Renamer.Moved(settings.CoverDirectory,from,to);
            foreach(var stack in new[]{backStack,forwardStack})
                for(int i=0;i<stack.Count;i++) {
                    string key=stack[i];
                    if(key.StartsWith("@"+ScopeKey,StringComparison.Ordinal) && Renamer.IsAt(key.Substring(1+ScopeKey.Length),from))
                        stack[i]="@"+ScopeKey+Renamer.Moved(key.Substring(1+ScopeKey.Length),from,to);
                    else if(!key.StartsWith("@") && Renamer.IsAt(key,from))stack[i]=Renamer.Moved(key,from,to);
                }
            bool inside=active!=null && Renamer.IsAt(active.Source??ScopeFolder,from);
            if(inside || ShowingAllCrops)ClearSelection();
            if(inside) {
                string name=Path.GetFileName(to);
                Func<Album,Album> follow=a=>{
                    if(ShowingScope && a.Folder==active.Folder)return ScopeAlbum(Renamer.Moved(ScopeFolder,from,to));
                    if(a.Source==null || !Renamer.IsAt(a.Source,from))return a;
                    string source=Renamer.Moved(a.Source,from,to);
                    return new Album(store.AlbumFolderOf(source),ShellPlaces.Same(a.Source,from)?name:a.Name,source);
                };
                int view;bool hadView=folderFilters.TryGetValue(active.Folder,out view);
                for(int i=0;i<trail.Count;i++)trail[i]=follow(trail[i]);
                var now=follow(active);
                if(hadView)folderFilters[now.Folder]=view;
                if(ShowingScope)try {
                    string lastFile=Path.Combine(store.Root,"last.txt");
                    if(File.Exists(lastFile) && File.ReadAllText(lastFile)==active.Folder)File.WriteAllText(lastFile,now.Folder);
                } catch(IOException) {}
                active=now;Title=now.Name;
                BuildPath();
            }
            BuildTabs();
            if(tree!=null && treePanel.Visibility==Visibility.Visible){tree.FolderRenamed(from,to);TreeSyncSoon();}
            ReloadKeepingPlace();
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
        internal UpscaleToast Notify(string title,string message,Action clicked,NoticeKind kind=NoticeKind.Done,bool keepOpen=false,double seconds=15,string button=null) {
            var toast=new UpscaleToast(ActiveAppWindow(),title,message,clicked==null?null:(Action)(()=>{
                // A notice can be clicked while another window (the cover editor, Make GIF cover, a question) is open.
                // Opening something on top of it would stack windows, so that window is brought forward instead.
                var busy=BlockingWindow();
                if(busy!=null){busy.Activate();Notify(L.T("Close this window first"),L.P("Finish the work in the open window or close it, then press the button on the notice again.","열린 창의 작업을 끝내거나 창을 닫은 뒤, 알림의 버튼을 다시 눌러 주세요."),null,NoticeKind.Info);return;}
                clicked();
            }),kind,keepOpen,seconds,button);
            // Watching in a large view: no card over the video, just the title as its corner text (the notice is not shown;
            // Dismiss still works on it).
            var showing=Application.Current.Windows.OfType<Viewer>().Where(v=>v.IsVisible && v.WindowState!=WindowState.Minimized).ToList();
            var watching=showing.FirstOrDefault(v=>v.IsActive)??showing.FirstOrDefault();
            if(watching!=null){watching.Say(title);return toast;}
            toast.Show();
            return toast;
        }
        static Window BlockingWindow() {
            if(CoverWork.Top!=null)return CoverWork.Top;
            return Application.Current.Windows.OfType<Window>().FirstOrDefault(w=>w.IsVisible &&
                !(w is GalleryWindow) && !(w is UpscaleToast) && !(w is Viewer) && !(w is GuideWindow) && !(w is VideoOverlay) && !(w is TransferWindow));
        }
        void UpscaleFinished(Photo updated) {
            ReplaceShown(updated);
            Notify(L.T("Upscaling finished"),updated.Name+Environment.NewLine+L.P("[Compare] shows it side by side with the original cover, so you can choose the one you like.","[비교해 보기]를 누르면 원래 표지와 나란히 비교해서 마음에 드는 쪽을 고를 수 있어요."),
                ()=>ShowPendingChoice(updated,ActiveAppWindow()),NoticeKind.Done,false,15,L.P("Compare","비교해 보기"));
        }
        void ReplaceShown(Photo updated) {
            int index=photos.FindIndex(p=>p!=null && String.Equals(p.Folder,updated.Folder,StringComparison.OrdinalIgnoreCase));
            if(index>=0 && rows!=null)UpdatePhoto(index,updated);
            else if(ShowingScope && Renamer.IsAt(updated.Original,ScopeFolder))ReloadKeepingPlace();
            else RefreshHiddenCover(updated);
            if(CoverReplaced!=null)CoverReplaced(updated);
        }
        // Shows the two versions side by side. Returns the photo as recorded after the choice.
        internal async Task<Photo> ShowUpscaleChoice(Window owner,Photo photo) {
            if(choosingUpscale || !CoverWorkFree(owner??this))return photo;
            choosingUpscale=true;
            try {
                Photo current=await Task.Run(()=>GalleryStore.Read(photo.Folder,photo.Linked?photo.Original:null));
                if(lifetime.IsCancellationRequested)return photo;
                if(!current.HasUpscale)return current;
                if(!CoverWorkFree(owner??this))return current; // another cover was opened while this one was loading
                Window from=owner!=null && owner.IsVisible?owner:this;
                var dialog=new UpscaleChoiceWindow(from,current);
                if(CoverWork.Show(dialog,current,this,null,from)!=true || dialog.ChoseUpscaled==null)return current;
                bool upscaled=dialog.ChoseUpscaled.Value;
                var chosen=await Task.Run(()=>GalleryStore.ChooseUpscale(current,upscaled));
                if(!lifetime.IsCancellationRequested)ReplaceShown(chosen);
                return chosen;
            } catch(Exception ex){if(!lifetime.IsCancellationRequested)UI.Error(owner??this,ex);return photo;}
            finally{choosingUpscale=false;}
        }
        async void ShowPendingChoice(Photo photo,Window owner=null) {await ShowUpscaleChoice(owner??this,photo);}
        // "Open with LoveMyShelf" from File Explorer: shows the file's folder, then opens the file large, like a
        // photo viewer or video player does. A folder is just shown.
        internal void OpenExternalFile(string path) {
            try {
                string full=Path.GetFullPath(path);
                if(WindowState==WindowState.Minimized)WindowState=WindowState.Normal;
                Activate();
                if(Directory.Exists(full)){NavigateTo(full);return;}
                if(!File.Exists(full))return;
                NavigateTo(Path.GetDirectoryName(full));
                // The folder is read in the background; open the file as soon as its tile is there.
                int tries=0;
                var wait=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(150)};
                wait.Tick+=delegate {
                    tries++;
                    var found=photos.FirstOrDefault(p=>p!=null && p.Original!=null && String.Equals(p.Original,full,StringComparison.OrdinalIgnoreCase));
                    if(found!=null){wait.Stop();OpenPhoto(found);}
                    else if(tries>100 || lifetime.IsCancellationRequested)wait.Stop();
                };
                wait.Start();
            } catch(Exception ex) {UI.Error(this,ex);}
        }
        internal void OpenPhoto(Photo photo,bool? cover) {
            // The first click after an upscale finishes shows the comparison instead.
            if(cover==null && photo.UpscalePending){ShowPendingChoice(photo);return;}
            var items=photos.Where(p=>p!=null).ToList();
            int index=items.IndexOf(photo);if(index<0)return;
            OpenViewer(items,index,cover);
        }
        // Opens a large view as a window of its own, like a video player opened from File Explorer: any number
        // can be open, and the folder window stays usable. The first one opens where the last large view was left,
        // and one opened while others show opens beside them (see ViewerPlacement), so they don't pile up.
        // Make cover from it closes it and opens the editor here.
        Viewer OpenViewer(IList<Photo> items,int index,bool? cover,Window placeLike=null) {
            var viewer=new Viewer(this,items,index,false,cover);
            if(placeLike!=null)viewer.PlaceLike(placeLike);
            else try {ViewerPlacement.Place(viewer,this,settings.ViewerBounds);} catch(Exception) {}
            viewer.EditLater=p=>Edit(p);
            viewer.Closed+=delegate {ShowJustSaved();};
            viewer.Show();
            return viewer;
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
        // Files sent to the Recycle Bin from the similar photos: folder windows showing their folder read it again.
        internal void FilesRecycled(IList<string> paths) {
            var folders=paths.Select(p=>Path.GetDirectoryName(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach(var window in Application.Current.Windows.OfType<GalleryWindow>().ToList()) {
                if(window.active==null)continue;
                string scope=window.ScopeFolder;
                if(folders.Any(f=>scope!=null?Renamer.IsAt(f,scope):ShellPlaces.Same(f,window.active.Source)))window.ReloadKeepingPlace();
            }
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
