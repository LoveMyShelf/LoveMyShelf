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
    // File Explorer's address bar: the way to the folder on screen as clickable parts ("› This PC › I: › A › Memes"),
    // a › after each part that lists the folders inside it, « for the front parts when it is too long, and a click on
    // the empty end turns it into a text box to type or paste a path (with folder suggestions, Enter to go, Esc to stop).
    internal sealed class AddressBar : Border {
        readonly GalleryWindow owner;
        readonly StackPanel crumbs=new StackPanel {Orientation=Orientation.Horizontal,ClipToBounds=true,VerticalAlignment=VerticalAlignment.Center};
        readonly TextBox edit=new TextBox {Visibility=Visibility.Collapsed,BorderThickness=new Thickness(0),Background=Brushes.Transparent,
            VerticalContentAlignment=VerticalAlignment.Center,Padding=new Thickness(6,0,6,0),FontSize=12.5};
        readonly Popup suggestPopup=new Popup {Placement=PlacementMode.Bottom,StaysOpen=false,AllowsTransparency=true};
        readonly ListBox suggestions=new ListBox {MaxHeight=280,MinWidth=260,BorderThickness=new Thickness(1)};
        // One part: its words, its folder (null for "This PC" and "LoveMyShelf") and, for the cover collections, the album.
        sealed class Part {internal string Label,Path;internal Album Album;internal string Kind;}
        readonly List<Part> parts=new List<Part>();
        internal AddressBar(GalleryWindow owner) {
            this.owner=owner;
            CornerRadius=new CornerRadius(8);BorderThickness=new Thickness(1);MinHeight=28;Padding=new Thickness(2,0,2,0);
            VerticalAlignment=VerticalAlignment.Center;Cursor=Cursors.IBeam;
            SetResourceReference(BackgroundProperty,"Panel");SetResourceReference(BorderBrushProperty,"Line");
            edit.SetResourceReference(Control.ForegroundProperty,"Text");edit.SetResourceReference(TextBox.CaretBrushProperty,"Text");
            var holder=new Grid();holder.Children.Add(crumbs);holder.Children.Add(edit);Child=holder;
            // The empty end of the bar starts typing, like File Explorer.
            MouseLeftButtonDown+=(s,e)=>{if(!(e.OriginalSource is DependencyObject) || Within<Button>((DependencyObject)e.OriginalSource)==null){BeginEdit();e.Handled=true;}};
            SizeChanged+=(s,e)=>{if(e.WidthChanged)Layout();};
            edit.PreviewKeyDown+=EditKeys;
            edit.LostKeyboardFocus+=(s,e)=>{
                var to=e.NewFocus as DependencyObject;
                if(to!=null && (Within<ListBox>(to)==suggestions))return;
                EndEdit();
            };
            edit.TextChanged+=delegate {if(edit.IsKeyboardFocusWithin && !filling)Suggest();};
            suggestions.SetResourceReference(Control.BackgroundProperty,"Panel");suggestions.SetResourceReference(Control.BorderBrushProperty,"Line");
            suggestions.SetResourceReference(Control.ForegroundProperty,"Text");
            suggestions.MouseUp+=(s,e)=>{var item=suggestions.SelectedItem as string;if(item!=null){Go(item);}};
            suggestPopup.Child=suggestions;suggestPopup.PlacementTarget=this;
        }
        static T Within<T>(DependencyObject node) where T:DependencyObject {
            for(;node!=null;node=node is Visual?VisualTreeHelper.GetParent(node):LogicalTreeHelper.GetParent(node))if(node is T)return (T)node;
            return null;
        }
        internal bool Editing {get {return edit.Visibility==Visibility.Visible;}}
        // ----- Showing where you are -----
        internal void Show(Album active) {
            parts.Clear();
            if(active==null){Layout();return;}
            if(active.Source==null) {
                parts.Add(new Part {Label=Covers.AppName,Kind="lms"});
                parts.Add(new Part {Label=active.Name,Album=active});
            } else {
                parts.Add(new Part {Label=L.T("This PC"),Kind="pc"});
                var chain=new List<string>();
                for(string at=active.Source;at!=null;at=Path.GetDirectoryName(at))chain.Insert(0,at);
                foreach(string at in chain) {
                    string name=Path.GetFileName(at.TrimEnd('\\'));
                    parts.Add(new Part {Label=String.IsNullOrEmpty(name)?at.TrimEnd('\\'):name,Path=at});
                }
            }
            if(!Editing)Layout();
        }
        Button Flat(string text,double size,bool strong) {
            var button=new Button {Content=text,Padding=new Thickness(5,1,5,1),MinHeight=22,BorderThickness=new Thickness(0),
                Background=Brushes.Transparent,VerticalAlignment=VerticalAlignment.Center,FontSize=size,Cursor=Cursors.Arrow};
            button.SetResourceReference(Control.ForegroundProperty,strong?"Text":"Subtle");
            return button;
        }
        void Layout() {
            crumbs.Children.Clear();
            if(parts.Count==0)return;
            var infinite=new Size(double.PositiveInfinity,double.PositiveInfinity);
            var made=new List<Tuple<FrameworkElement,FrameworkElement>>();
            for(int i=0;i<parts.Count;i++) {
                var part=parts[i];bool last=i==parts.Count-1;
                var label=Flat(part.Label,12.5,true);
                if(!last && (part.Path!=null || part.Album!=null)){var p=part;label.Click+=delegate {Open(p);};}
                else if(last){label.Click+=delegate {BeginEdit();};}
                var more=Flat("›",13,false);more.Padding=new Thickness(3,1,3,1);
                { var p=part;more.Click+=delegate {Inside(p,more);}; }
                label.Measure(infinite);more.Measure(infinite);
                made.Add(Tuple.Create((FrameworkElement)label,(FrameworkElement)more));
            }
            double room=ActualWidth>0?ActualWidth-40:double.PositiveInfinity; // keep an empty end to click for typing
            var fold=Flat("«",13,false);fold.Measure(infinite);
            int hidden=0;
            for(;hidden<parts.Count-1;hidden++) {
                double width=hidden>0?fold.DesiredSize.Width:0;
                for(int i=hidden;i<made.Count;i++)width+=made[i].Item1.DesiredSize.Width+made[i].Item2.DesiredSize.Width;
                if(width<=room)break;
            }
            if(hidden>0) {
                var menu=new ContextMenu {PlacementTarget=fold,Placement=PlacementMode.Bottom};
                for(int i=hidden-1;i>=0;i--) {
                    var p=parts[i];var item=new MenuItem {Header=p.Label,IsEnabled=p.Path!=null || p.Album!=null};
                    item.Click+=delegate {Open(p);};menu.Items.Add(item);
                }
                fold.Click+=delegate {menu.IsOpen=true;};fold.ToolTip=L.T("Show the folders above");
                crumbs.Children.Add(fold);
            }
            for(int i=hidden;i<made.Count;i++){crumbs.Children.Add(made[i].Item1);crumbs.Children.Add(made[i].Item2);}
        }
        void Open(Part part) {
            if(part.Album!=null){owner.ShowAlbum(part.Album);return;}
            if(part.Path!=null)owner.NavigateTo(part.Path);
        }
        // The list under a ›: the folders inside that part (the drives for This PC, the collections and folders for LoveMyShelf).
        void Inside(Part part,Button at) {
            var menu=new ContextMenu {PlacementTarget=at,Placement=PlacementMode.Bottom};
            if(part.Kind=="lms") {
                foreach(var album in new[]{owner.AllCropsAlbum,owner.UnfinishedAlbum}){var a=album;var item=new MenuItem {Header=a.Name};item.Click+=delegate {owner.ShowAlbum(a);};menu.Items.Add(item);}
                menu.Items.Add(new Separator());
                foreach(var album in owner.Store.VisibleAlbums()){var a=album;var item=new MenuItem {Header=a.Name,ToolTip=a.Source};item.Click+=delegate {owner.NavigateTo(a.Source);};menu.Items.Add(item);}
            } else if(part.Kind=="pc") {
                foreach(var drive in ShellPlaces.Drives()){var d=drive;var item=new MenuItem {Header=d.Name};item.Click+=delegate {owner.NavigateTo(d.Path);};menu.Items.Add(item);}
            } else if(part.Path!=null) {
                string next=parts.IndexOf(part)+1<parts.Count?parts[parts.IndexOf(part)+1].Path:null;
                foreach(var path in GalleryStore.ChildFolders(part.Path)) {
                    var p=path;var item=new MenuItem {Header=Path.GetFileName(p),IsChecked=next!=null && ShellPlaces.Same(next,p)};
                    item.Click+=delegate {owner.NavigateTo(p);};menu.Items.Add(item);
                }
            }
            if(menu.Items.Count==0)menu.Items.Add(new MenuItem {Header=L.T("No folders inside"),IsEnabled=false});
            menu.IsOpen=true;
        }
        // ----- Typing a path -----
        internal void BeginEdit() {
            var last=parts.LastOrDefault();
            edit.Text=last==null?"":last.Path??(last.Album!=null?Covers.AppName+"\\"+last.Label:"");
            crumbs.Visibility=Visibility.Collapsed;edit.Visibility=Visibility.Visible;
            SetResourceReference(BorderBrushProperty,"Accent");
            Dispatcher.BeginInvoke(new Action(()=>{edit.Focus();Keyboard.Focus(edit);edit.SelectAll();}),System.Windows.Threading.DispatcherPriority.Input);
        }
        void EndEdit() {
            suggestPopup.IsOpen=false;
            if(!Editing)return;
            bool hadFocus=edit.IsKeyboardFocusWithin;
            edit.Visibility=Visibility.Collapsed;crumbs.Visibility=Visibility.Visible;
            // A hidden box would keep the keyboard, and every key (Ctrl+Z, Delete, arrows) would go nowhere.
            if(hadFocus)owner.FocusTiles();
            SetResourceReference(BorderBrushProperty,"Line");
            Layout();
        }
        void Go(string text) {
            EndEdit();
            owner.GoToTyped(text);
        }
        bool filling;
        void EditKeys(object sender,KeyEventArgs e) {
            if(e.Key==Key.Escape){EndEdit();owner.FocusTiles();e.Handled=true;}
            else if(e.Key==Key.Enter){Go(edit.Text);e.Handled=true;}
            else if((e.Key==Key.Down || e.Key==Key.Up) && suggestPopup.IsOpen && suggestions.Items.Count>0) {
                int at=suggestions.SelectedIndex+(e.Key==Key.Down?1:-1);
                at=Math.Max(0,Math.Min(suggestions.Items.Count-1,at));
                suggestions.SelectedIndex=at;
                filling=true;edit.Text=(string)suggestions.SelectedItem;edit.CaretIndex=edit.Text.Length;filling=false;
                e.Handled=true;
            }
            else if(e.Key==Key.Tab && suggestPopup.IsOpen && suggestions.Items.Count>0) {
                filling=true;edit.Text=(string)(suggestions.SelectedItem??suggestions.Items[0])+"\\";edit.CaretIndex=edit.Text.Length;filling=false;
                Suggest();e.Handled=true;
            }
        }
        // Folder names that start with what is typed after the last "\", like File Explorer's suggestions.
        void Suggest() {
            suggestions.Items.Clear();
            try {
                string text=Environment.ExpandEnvironmentVariables(edit.Text.Trim('"'));
                int cut=text.LastIndexOf('\\');
                if(cut>=0) {
                    string parent=text.Substring(0,cut+1),start=text.Substring(cut+1);
                    if(Directory.Exists(parent))
                        foreach(var path in GalleryStore.ChildFolders(parent).Where(p=>Path.GetFileName(p).StartsWith(start,StringComparison.OrdinalIgnoreCase)).Take(12))
                            suggestions.Items.Add(path);
                }
            } catch(Exception) {}
            suggestions.MinWidth=Math.Max(260,ActualWidth);
            suggestPopup.IsOpen=suggestions.Items.Count>0;
        }
    }
}
