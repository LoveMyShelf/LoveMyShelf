using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace LoveMyShelf {
    // File Explorer's views, in its own order.
    internal enum FolderView { ExtraLarge, Large, Medium, Small, List, Details, Tiles, Content }

    // The folders inside the folder on screen, in their own area apart from the photo tiles, like File Explorer:
    // a group header ("Folders (7)", a fold arrow, a line across and the View menu) that also divides them from the photos,
    // then the folders in the chosen view. The area sits above or below the photos (Options) and never takes more than
    // about half the height; the rest scrolls inside it.
    internal sealed class FolderPane : DockPanel {
        readonly GalleryWindow owner;
        readonly ScrollViewer scroller=new ScrollViewer {VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,Focusable=false};
        readonly TextBlock title=new TextBlock {FontSize=12,VerticalAlignment=VerticalAlignment.Center};
        readonly System.Windows.Shapes.Path arrow=new System.Windows.Shapes.Path {Data=Geometry.Parse("M 0,0 L 3.5,3.5 L 0,7"),StrokeThickness=1.3,Width=8,Height=8,Stretch=Stretch.None,
            VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center,RenderTransformOrigin=new Point(0.5,0.5)};
        readonly Button viewButton;
        IList<Tuple<string,int>> folders=new List<Tuple<string,int>>();
        // The folders chosen in the area (one click, or a range drawn with the right button).
        readonly HashSet<string> chosen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal IList<string> Chosen {get {return chosen.ToList();}}
        bool folded;
        // A folder with no photos or videos at all: the folders are all there is to see, so they open by themselves and take
        // the whole area. This never changes the remembered fold; folding them here lasts only while this folder is open.
        bool onlyFolders,foldedHere;
        string place;
        internal bool IsFolded {get {return onlyFolders?foldedHere:folded;}}
        internal bool FillsArea {get {return onlyFolders && !foldedHere && folders.Count>0;}}
        void SetFolded(bool value) {
            if(onlyFolders)foldedHere=value;
            else {folded=value;owner.SaveFoldersFolded(value);}
            owner.PlaceFolderPane();Fill();owner.ShowEmptyNote();
        }
        internal void Unfold() {SetFolded(false);}
        internal FolderPane(GalleryWindow owner) {
            this.owner=owner;LastChildFill=true;folded=owner.Settings.FoldersFolded; // remembered, so people who don't want them keep one line
            arrow.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,"Subtle");title.SetResourceReference(TextBlock.ForegroundProperty,"Subtle");
            // The header: ▸ Folders (7) ———— [View ▾]
            var header=new DockPanel {Margin=new Thickness(8,4,8,4),LastChildFill=true};
            var fold=new Button {Background=Brushes.Transparent,BorderThickness=new Thickness(0),Padding=new Thickness(4,2,8,2),MinHeight=24,Cursor=Cursors.Hand};
            var label=new StackPanel {Orientation=Orientation.Horizontal};
            var arrowBox=new Border {Width=14,Child=arrow};label.Children.Add(arrowBox);title.Margin=new Thickness(4,0,0,0);label.Children.Add(title);
            fold.Content=label;fold.Click+=delegate {SetFolded(!IsFolded);};
            DockPanel.SetDock(fold,Dock.Left);header.Children.Add(fold);
            viewButton=new Button {Padding=new Thickness(8,2,8,2),MinHeight=24,FontSize=12};
            viewButton.Click+=delegate {ViewMenu().IsOpen=true;};
            DockPanel.SetDock(viewButton,Dock.Right);header.Children.Add(viewButton);
            var line=new Border {Height=1,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(4,0,10,0)};
            line.SetResourceReference(Border.BackgroundProperty,"Line");header.Children.Add(line);
            DockPanel.SetDock(header,Dock.Top);Children.Add(header);
            // A background (even a see-through one) makes the empty space take the mouse, so a range can start there.
            scroller.Padding=new Thickness(6,0,6,6);scroller.Background=Brushes.Transparent;
            var holder=new Grid();holder.Children.Add(scroller);SetupRange(holder);Children.Add(holder);
            Visibility=Visibility.Collapsed;
        }
        FolderView View {get {return (FolderView)Math.Max(0,Math.Min(7,owner.Settings.FolderView));}}
        // Shows these folders (none: the area hides).
        internal void Show(IList<Tuple<string,int>> list,bool noFiles,string where) {
            folders=list??new List<Tuple<string,int>>();
            if(!String.Equals(place,where,StringComparison.OrdinalIgnoreCase)){place=where;foldedHere=false;}
            onlyFolders=noFiles;
            chosen.RemoveWhere(c=>!folders.Any(f=>ShellPlaces.Same(f.Item1,c)));
            owner.PlaceFolderPane();
            Fill();
        }
        internal void Fill() {
            Visibility=folders.Count>0?Visibility.Visible:Visibility.Collapsed;
            title.Text=L.F("Folders ({0:N0})",folders.Count);
            viewButton.Content=L.T("View")+"  ▾";viewButton.ToolTip=L.T("Change how the folders look");
            arrow.RenderTransform=new RotateTransform(IsFolded?0:90);
            scroller.Visibility=IsFolded?Visibility.Collapsed:Visibility.Visible;
            if(IsFolded || folders.Count==0){scroller.Content=null;return;}
            var view=View;
            scroller.HorizontalScrollBarVisibility=view==FolderView.List?ScrollBarVisibility.Auto:ScrollBarVisibility.Disabled;
            scroller.VerticalScrollBarVisibility=view==FolderView.List?ScrollBarVisibility.Disabled:ScrollBarVisibility.Auto;
            if(view==FolderView.Details) {
                var stack=new StackPanel();
                stack.Children.Add(DetailsHeader());
                foreach(var f in folders)stack.Children.Add(Item(f,view));
                scroller.Content=stack;
            } else if(view==FolderView.Content) {
                var stack=new StackPanel();
                foreach(var f in folders)stack.Children.Add(Item(f,view));
                scroller.Content=stack;
            } else {
                var wrap=new WrapPanel {Orientation=view==FolderView.List?Orientation.Vertical:Orientation.Horizontal};
                // List fills columns from top to bottom, only as tall as needed (at most the area's height).
                if(view==FolderView.List) {
                    int fit=Math.Max(1,(int)((double.IsInfinity(MaxHeight)?400:MaxHeight-48)/28));
                    wrap.Height=Math.Min(fit,folders.Count)*28;
                }
                foreach(var f in folders)wrap.Children.Add(Item(f,view));
                scroller.Content=wrap;
            }
        }
        // ----- One folder -----
        static readonly string FolderDrawing="M 1.5,4 L 1.5,14.5 L 16.5,14.5 L 16.5,6 L 8.5,6 L 6.8,4 Z";
        static FrameworkElement FolderIcon(double size) {
            var path=new System.Windows.Shapes.Path {Data=Geometry.Parse(FolderDrawing),Width=size,Height=size*0.84,Stretch=Stretch.Uniform,
                StrokeThickness=Math.Max(1.3,size/26),StrokeLineJoin=PenLineJoin.Round,SnapsToDevicePixels=true,IsHitTestVisible=false};
            path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,"Subtle");
            return new Border {Width=size,Height=size,Child=path,IsHitTestVisible=false};
        }
        static TextBlock Text(string text,double size,bool subtle,TextAlignment align=TextAlignment.Left,int lines=1) {
            var block=new TextBlock {Text=text,FontSize=size,TextTrimming=TextTrimming.CharacterEllipsis,TextAlignment=align,
                VerticalAlignment=VerticalAlignment.Center,IsHitTestVisible=false};
            if(lines>1){block.TextWrapping=TextWrapping.WrapWithOverflow;block.LineStackingStrategy=LineStackingStrategy.BlockLineHeight;
                block.LineHeight=Math.Round(size*1.35);block.MaxHeight=block.LineHeight*lines+2;}
            block.SetResourceReference(TextBlock.ForegroundProperty,subtle?"Subtle":"Text");
            return block;
        }
        static string Date(string path) {
            try {return Directory.GetLastWriteTime(path).ToString("yyyy-MM-dd tt h:mm",System.Globalization.CultureInfo.CurrentCulture);} catch(Exception) {return "";}
        }
        // Column widths of the Details view (name takes the rest).
        const double DateWidth=170,TypeWidth=110,SizeWidth=80;
        FrameworkElement DetailsHeader() {
            var grid=Columns();grid.Margin=new Thickness(0,0,0,2);
            var names=new[]{L.T("Name"),L.T("Date modified"),L.T("Type"),L.T("Size")};
            for(int i=0;i<4;i++) {
                int column=i;
                var head=new Button {Content=names[i],HorizontalContentAlignment=HorizontalAlignment.Left,Background=Brushes.Transparent,
                    BorderThickness=new Thickness(0),Padding=new Thickness(i==0?30:8,3,8,3),FontSize=12,MinHeight=24};
                head.SetResourceReference(Control.ForegroundProperty,"Subtle");
                // Like File Explorer: a click sorts by that column, a second click turns the order around.
                if(i<2)head.Click+=delegate {
                    var sort=column==0?PhotoSort.Name:PhotoSort.Modified;
                    owner.ChooseSort(sort,owner.Settings.Sort==sort?!owner.Settings.Descending:sort==PhotoSort.Modified);
                };
                Grid.SetColumn(head,i);grid.Children.Add(head);
            }
            var under=new Border {Height=1,VerticalAlignment=VerticalAlignment.Bottom};under.SetResourceReference(Border.BackgroundProperty,"Line");
            Grid.SetColumnSpan(under,4);grid.Children.Add(under);
            return grid;
        }
        static Grid Columns() {
            var grid=new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(1,GridUnitType.Star),MinWidth=120});
            grid.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(DateWidth)});
            grid.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(TypeWidth)});
            grid.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(SizeWidth)});
            return grid;
        }
        FrameworkElement Item(Tuple<string,int> folder,FolderView view) {
            string path=folder.Item1,name=System.IO.Path.GetFileName(path);
            FrameworkElement content;double width=double.NaN,height=double.NaN;
            switch(view) {
                case FolderView.ExtraLarge: case FolderView.Large: case FolderView.Medium: {
                    double icon=view==FolderView.ExtraLarge?96:view==FolderView.Large?64:40;
                    width=view==FolderView.ExtraLarge?150:view==FolderView.Large?112:88;
                    var stack=new StackPanel {Margin=new Thickness(4,6,4,6)};
                    var picture=FolderIcon(icon);picture.HorizontalAlignment=HorizontalAlignment.Center;stack.Children.Add(picture);
                    var words=Text(name,12.5,false,TextAlignment.Center,2);words.Margin=new Thickness(0,4,0,0);stack.Children.Add(words);
                    content=stack;break;
                }
                case FolderView.Small: case FolderView.List: {
                    width=view==FolderView.Small?190:220;height=26;
                    var row=new DockPanel {Margin=new Thickness(6,0,6,0)};
                    var picture=FolderIcon(16);picture.Margin=new Thickness(0,0,8,0);DockPanel.SetDock(picture,Dock.Left);row.Children.Add(picture);
                    row.Children.Add(Text(name,12.5,false));content=row;break;
                }
                case FolderView.Details: {
                    height=26;var grid=Columns();
                    var first=new DockPanel {Margin=new Thickness(6,0,8,0)};
                    var picture=FolderIcon(16);picture.Margin=new Thickness(0,0,8,0);DockPanel.SetDock(picture,Dock.Left);first.Children.Add(picture);
                    first.Children.Add(Text(name,12.5,false));grid.Children.Add(first);
                    var date=Text(Date(path),12,true);date.Margin=new Thickness(8,0,8,0);Grid.SetColumn(date,1);grid.Children.Add(date);
                    var type=Text(L.T("File folder"),12,true);type.Margin=new Thickness(8,0,8,0);Grid.SetColumn(type,2);grid.Children.Add(type);
                    content=grid;break;
                }
                case FolderView.Tiles: {
                    width=260;height=64;
                    var row=new DockPanel {Margin=new Thickness(8,0,8,0)};
                    var picture=FolderIcon(40);picture.Margin=new Thickness(0,0,10,0);DockPanel.SetDock(picture,Dock.Left);row.Children.Add(picture);
                    var words=new StackPanel {VerticalAlignment=VerticalAlignment.Center};
                    words.Children.Add(Text(name,12.5,false));words.Children.Add(Text(L.T("File folder"),11.5,true));
                    row.Children.Add(words);content=row;break;
                }
                default: { // Content
                    height=56;
                    var row=new DockPanel {Margin=new Thickness(8,0,8,0)};
                    var picture=FolderIcon(32);picture.Margin=new Thickness(0,0,12,0);DockPanel.SetDock(picture,Dock.Left);row.Children.Add(picture);
                    var date=new StackPanel {VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(12,0,0,0)};
                    date.Children.Add(Text(L.T("Date modified")+": "+Date(path),11.5,true));
                    DockPanel.SetDock(date,Dock.Right);row.Children.Add(date);
                    var words=new StackPanel {VerticalAlignment=VerticalAlignment.Center};
                    words.Children.Add(Text(name,12.5,false));words.Children.Add(Text(L.T("File folder"),11.5,true));
                    row.Children.Add(words);content=row;
                    var line=new Border {Height=1,VerticalAlignment=VerticalAlignment.Bottom,Opacity=0.6};line.SetResourceReference(Border.BackgroundProperty,"Line");
                    var holder=new Grid();holder.Children.Add(row);holder.Children.Add(line);content=holder;
                    break;
                }
            }
            var button=new Button {Content=content,Tag=new FolderTarget(path,true),ToolTip=path,Padding=new Thickness(0),Margin=new Thickness(1),
                HorizontalContentAlignment=HorizontalAlignment.Stretch,VerticalContentAlignment=VerticalAlignment.Center,
                Background=Brushes.Transparent,BorderBrush=Brushes.Transparent,Template=Look()};
            if(!double.IsNaN(width))button.Width=width;
            if(!double.IsNaN(height))button.Height=height;
            ToolTipService.SetInitialShowDelay(button,900);
            System.Windows.Automation.AutomationProperties.SetName(button,name);
            if(chosen.Contains(path))button.SetResourceReference(Control.BackgroundProperty,"Raised");
            // Like File Explorer: one click chooses, two clicks open (or one click opens, per Options > Large view).
            button.PreviewMouseLeftButtonDown+=(s,e)=>{
                if(e.ClickCount==2){e.Handled=true;owner.EnterFolder(path);return;}
                if(owner.Settings.OpenByDoubleClick) {
                    if((Keyboard.Modifiers&ModifierKeys.Control)==0)chosen.Clear();
                    if(!chosen.Add(path))chosen.Remove(path);
                    Paint();
                }
            };
            button.Click+=delegate {if(!owner.Settings.OpenByDoubleClick)owner.EnterFolder(path);};
            button.KeyDown+=(s,e)=>{if(e.Key==Key.Enter){owner.EnterFolder(path);e.Handled=true;}};
            button.ContextMenu=Menu(path);
            // Ours, then File Explorer's own menu (for every chosen folder when this one is chosen).
            button.ContextMenuOpening+=(s,e)=>{
                e.Handled=true;
                var folders=chosen.Contains(path) && chosen.Count>1?chosen.ToList():new List<string>{path};
                owner.ShowFolderMenu(button.ContextMenu,folders,button,e);
            };
            return button;
        }
        internal string OnlyChosen {get {return chosen.Count==1?chosen.First():null;}}
        IEnumerable<Button> Items() {
            var panel=scroller.Content as Panel;
            return panel==null?Enumerable.Empty<Button>():panel.Children.OfType<Button>();
        }
        void Paint() {
            foreach(var item in Items()) {
                var target=item.Tag as FolderTarget;
                if(target!=null && chosen.Contains(target.Path))item.SetResourceReference(Control.BackgroundProperty,"Raised");else item.ClearValue(Control.BackgroundProperty);
            }
        }
        // ----- Range selection, as over the photo tiles: the right button anywhere, the left one from empty space -----
        bool armed,drawing;MouseButton drawButton;Point drawStart;HashSet<string> drawBase=new HashSet<string>();
        readonly Canvas layer=new Canvas {IsHitTestVisible=false,ClipToBounds=true};
        readonly Grid box=new Grid {Visibility=Visibility.Collapsed};
        void SetupRange(Grid holder) {
            var fill=new Border {Opacity=0.16};fill.SetResourceReference(Border.BackgroundProperty,"Accent");
            var edge=new Border {BorderThickness=new Thickness(1)};edge.SetResourceReference(Border.BorderBrushProperty,"Accent");
            box.Children.Add(fill);box.Children.Add(edge);layer.Children.Add(box);holder.Children.Add(layer);
            scroller.PreviewMouseRightButtonDown+=(s,e)=>Arm(e,MouseButton.Right);
            scroller.PreviewMouseLeftButtonDown+=(s,e)=>{
                if(ItemAt(e.OriginalSource)!=null)return;
                if((Keyboard.Modifiers&ModifierKeys.Control)==0){chosen.Clear();Paint();}
                Arm(e,MouseButton.Left);
            };
            scroller.PreviewMouseMove+=(s,e)=>{
                if(!armed && !drawing)return;
                var pressed=drawButton==MouseButton.Right?e.RightButton:e.LeftButton;
                if(pressed!=MouseButtonState.Pressed){armed=false;if(drawing)Finish();return;}
                var at=e.GetPosition(layer);
                if(!drawing) {
                    if(Math.Abs(at.X-drawStart.X)<4 && Math.Abs(at.Y-drawStart.Y)<4)return;
                    // Ready before taking the mouse: taking it sends a move event right away (back into this handler).
                    drawBase=(Keyboard.Modifiers&ModifierKeys.Control)!=0?new HashSet<string>(chosen,StringComparer.OrdinalIgnoreCase):new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    drawing=true;armed=false;scroller.CaptureMouse();
                }
                var rect=new Rect(drawStart,at);
                Canvas.SetLeft(box,rect.X);Canvas.SetTop(box,rect.Y);box.Width=Math.Max(1,rect.Width);box.Height=Math.Max(1,rect.Height);box.Visibility=Visibility.Visible;
                chosen.Clear();
                foreach(var f in drawBase)chosen.Add(f);
                foreach(var item in Items()) {
                    var place=new Rect(item.TranslatePoint(new Point(),layer),new Size(item.ActualWidth,item.ActualHeight));
                    var target=item.Tag as FolderTarget;
                    if(target!=null && place.IntersectsWith(rect)){if(!chosen.Add(target.Path))chosen.Remove(target.Path);}
                }
                Paint();e.Handled=true;
            };
            MouseButtonEventHandler up=(s,e)=>{
                bool drew=drawing && e.ChangedButton==drawButton;
                if(e.ChangedButton==drawButton){armed=false;if(drawing){Finish();e.Handled=true;}} // no menu after drawing
                // A right click on blank space in the area: File Explorer's blank-space menu.
                if(!drew && e.ChangedButton==MouseButton.Right && ItemAt(e.OriginalSource)==null){e.Handled=true;owner.ShowBackgroundMenu(scroller,null);}
            };
            scroller.PreviewMouseRightButtonUp+=up;scroller.PreviewMouseLeftButtonUp+=up;
            scroller.LostMouseCapture+=delegate {if(drawing)Finish();};
        }
        void Arm(MouseButtonEventArgs e,MouseButton button) {armed=true;drawButton=button;drawStart=e.GetPosition(layer);}
        void Finish() {drawing=false;box.Visibility=Visibility.Collapsed;if(scroller.IsMouseCaptured)scroller.ReleaseMouseCapture();}
        static Button ItemAt(object source) {
            for(var node=source as DependencyObject;node!=null;node=node is Visual?VisualTreeHelper.GetParent(node):LogicalTreeHelper.GetParent(node))
                if(node is Button && ((Button)node).Tag is FolderTarget)return (Button)node;
            return null;
        }
        ContextMenu Menu(string path) {
            var menu=new ContextMenu();
            var open=new MenuItem();open.Click+=delegate {owner.EnterFolder(path);};
            var openNew=new MenuItem();openNew.Click+=delegate {owner.OpenFolderInNewWindow(path);};
            var shelf=new MenuItem();shelf.Click+=delegate {if(owner.OnShelf(path))owner.RemoveFromShelf(path);else owner.AddToShelf(path);};
            var explorer=new MenuItem();explorer.Click+=delegate {
                try {System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) {UseShellExecute=true});} catch(Exception ex) {UI.Error(owner,ex);}
            };
            menu.Items.Add(open);menu.Items.Add(openNew);menu.Items.Add(new Separator());menu.Items.Add(shelf);menu.Items.Add(new Separator());menu.Items.Add(explorer);
            menu.Opened+=delegate {
                open.Header=L.T("Open");openNew.Header=L.T("Open in new window");explorer.Header=L.T("Open in File Explorer");
                shelf.Header=L.T(owner.OnShelf(path)?"Remove from Love My Shelf":"Add to Love My Shelf");
            };
            return menu;
        }
        static ControlTemplate look;
        // A folder in the area: no card, just a soft background under the pointer, like File Explorer.
        static ControlTemplate Look() {
            if(look!=null)return look;
            var box=new FrameworkElementFactory(typeof(Border),"Box");
            box.SetValue(Border.CornerRadiusProperty,new CornerRadius(5));
            box.SetValue(Border.BorderThicknessProperty,new Thickness(1));
            box.SetValue(Border.BackgroundProperty,new TemplateBindingExtension(Control.BackgroundProperty));
            box.SetValue(Border.BorderBrushProperty,new TemplateBindingExtension(Control.BorderBrushProperty));
            box.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
            look=new ControlTemplate(typeof(Button)) {VisualTree=box};
            var hover=new Trigger {Property=UIElement.IsMouseOverProperty,Value=true};
            hover.Setters.Add(new Setter(Border.BackgroundProperty,new DynamicResourceExtension("Panel"),"Box"));
            look.Triggers.Add(hover);
            return look;
        }
        // ----- The View menu, as in File Explorer -----
        ContextMenu ViewMenu() {
            var menu=new ContextMenu {PlacementTarget=viewButton,Placement=PlacementMode.Bottom};
            var names=new[]{"Extra large icons","Large icons","Medium icons","Small icons","List","Details view","Tiles","Content"};
            for(int i=0;i<names.Length;i++) {
                int choice=i;
                var item=new MenuItem {Header=L.T(names[i]),IsChecked=(int)View==i};
                item.Click+=delegate {owner.SaveFolderView(choice,null);};
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());
            foreach(bool below in new[]{false,true}) {
                bool value=below;
                var item=new MenuItem {Header=L.T(below?"Folders below the photos":"Folders above the photos"),IsChecked=owner.Settings.FoldersBelow==below};
                item.Click+=delegate {owner.SaveFolderView(null,value);};
                menu.Items.Add(item);
            }
            return menu;
        }
    }
}
