using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace LoveMyShelf {
    // One place in the Windows shell (a pinned Quick access folder, a drive, a network computer).
    internal sealed class ShellEntry {
        internal string Name,Path;internal bool Pinned;
        internal ShellEntry(string name,string path,bool pinned=false){Name=name;Path=path;Pinned=pinned;}
    }
    // Reads what File Explorer shows on its left: the pinned folders, the drives under This PC and the network.
    // The shell is asked on a short-lived STA thread with a time limit, so a slow network never freezes the window.
    internal static class ShellPlaces {
        // The checks put fixed lists here, so their pictures never show the person's own folders.
        internal static Func<List<ShellEntry>> PinsForChecks,DrivesForChecks,NetworkForChecks;
        internal static Func<string> OneDriveForChecks;
        const string QuickAccess="shell:::{679f85cb-0220-4080-b29b-5540cc05aab6}";
        internal static List<ShellEntry> Pinned() {
            if(PinsForChecks!=null)return PinsForChecks();
            var all=Read(QuickAccess,true);
            // Windows 11 marks the folders the person pinned; without that mark (older Windows) every folder shows, as there.
            bool marked=all.Any(e=>e.Pinned);
            return all.Where(e=>!marked || e.Pinned).ToList();
        }
        internal static List<ShellEntry> Drives() {
            if(DrivesForChecks!=null)return DrivesForChecks();
            var list=Read(17,false).Where(e=>e.Path!=null && e.Path.Length<=3 && e.Path.EndsWith(":\\")).ToList();
            if(list.Count==0) // the shell couldn't be asked: plain drive letters
                foreach(var drive in DriveInfo.GetDrives())list.Add(new ShellEntry(drive.Name.TrimEnd('\\'),drive.Name));
            return list;
        }
        internal static List<ShellEntry> Network() {return NetworkForChecks!=null?NetworkForChecks():Read(18,false);}
        // The shares of a network computer (\\name), which plain folder listing can't show.
        internal static List<ShellEntry> Shares(string computer) {return Read(computer,false).Where(e=>e.Path!=null).ToList();}
        internal static string OneDrive() {
            if(OneDriveForChecks!=null)return OneDriveForChecks();
            foreach(var name in new[]{"OneDriveConsumer","OneDrive"}) {
                string path=Environment.GetEnvironmentVariable(name);
                if(!String.IsNullOrEmpty(path) && Directory.Exists(path))return path;
            }
            return null;
        }
        static object Get(object o,string name){return o.GetType().InvokeMember(name,BindingFlags.GetProperty,null,o,null);}
        static object Call(object o,string name,params object[] args){return o.GetType().InvokeMember(name,BindingFlags.InvokeMethod,null,o,args);}
        static List<ShellEntry> Read(object space,bool pinnedMark) {
            var list=new List<ShellEntry>();
            var thread=new Thread(()=>{
                try {
                    var shell=Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
                    var folder=Call(shell,"NameSpace",space);if(folder==null)return;
                    var items=Call(folder,"Items");
                    int count=(int)Get(items,"Count");
                    for(int i=0;i<count;i++) {
                        try {
                            var item=Call(items,"Item",i);
                            if(!(bool)Get(item,"IsFolder"))continue;
                            string path=Get(item,"Path") as string,name=Get(item,"Name") as string;
                            bool pinned=false;
                            if(pinnedMark) {
                                if(path==null || !Directory.Exists(path))continue;
                                try {var mark=Call(item,"ExtendedProperty","System.Home.IsPinned");pinned=mark is bool && (bool)mark;} catch(Exception) {}
                            }
                            lock(list)list.Add(new ShellEntry(name,path,pinned));
                        } catch(Exception) {}
                    }
                } catch(Exception) {}
            });
            thread.SetApartmentState(ApartmentState.STA);thread.IsBackground=true;thread.Start();
            thread.Join(TimeSpan.FromSeconds(5));
            lock(list)return list.ToList();
        }
        [DllImport("shell32.dll",CharSet=CharSet.Unicode)]
        static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id,uint flags,IntPtr token,out IntPtr path);
        static string downloads;
        static string Downloads() {
            if(downloads!=null)return downloads;
            IntPtr raw;
            try {
                if(SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"),0,IntPtr.Zero,out raw)==0) {
                    downloads=Marshal.PtrToStringUni(raw);Marshal.FreeCoTaskMem(raw);
                }
            } catch(Exception) {}
            if(downloads==null)downloads=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Downloads");
            return downloads;
        }
        // Which picture a folder gets: the Windows folders keep their own, like File Explorer.
        internal static string KindOf(string path) {
            if(path==null)return "folder";
            if(path.Length<=3 && path.EndsWith(":\\"))return "drive";
            if(path.StartsWith(@"\\") && path.TrimEnd('\\').IndexOf('\\',2)<0)return "computer";
            var known=new[]{Tuple.Create(Environment.SpecialFolder.DesktopDirectory,"desktop"),Tuple.Create(Environment.SpecialFolder.MyDocuments,"documents"),
                Tuple.Create(Environment.SpecialFolder.MyPictures,"pictures"),Tuple.Create(Environment.SpecialFolder.MyVideos,"videos"),
                Tuple.Create(Environment.SpecialFolder.MyMusic,"music")};
            foreach(var k in known)if(Same(path,Environment.GetFolderPath(k.Item1)))return k.Item2;
            if(Same(path,Downloads()))return "downloads";
            string onedrive=OneDrive();if(onedrive!=null && Same(path,onedrive))return "onedrive";
            return "folder";
        }
        internal static bool Same(string a,string b) {
            if(String.IsNullOrEmpty(a) || String.IsNullOrEmpty(b))return false;
            return String.Equals(a.TrimEnd('\\'),b.TrimEnd('\\'),StringComparison.OrdinalIgnoreCase);
        }
    }

    // One row of the folder tree.
    internal sealed class TreeEntry {
        internal string Path,Kind,Label,Group;
        internal Album Album;   // a cover collection or a folder tab (opened as that tab)
        internal bool Pinned;
    }

    // The folder tree on the left of a folder window, made like File Explorer's navigation pane: Love My Shelf first
    // (the cover collections and the added folders), then Windows 11's order — OneDrive, the pinned folders,
    // This PC and Network. Folders are read only when they are unfolded. Pictures are this app's own line drawings.
    internal sealed class FolderTree : TreeView {
        static readonly object Placeholder=new object();
        readonly GalleryWindow owner;
        bool syncing;
        readonly DispatcherTimer openSoon=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(140)};
        internal FolderTree(GalleryWindow owner) {
            this.owner=owner;
            Background=Brushes.Transparent;BorderThickness=new Thickness(0);Padding=new Thickness(4,4,4,4);
            ScrollViewer.SetHorizontalScrollBarVisibility(this,ScrollBarVisibility.Disabled);
            ItemContainerStyle=(Style)Application.Current.Resources["FolderTreeItem"];
            openSoon.Tick+=delegate {openSoon.Stop();OpenSelected();};
            // Choosing a row opens it (after a moment, so walking with the arrow keys stays smooth), like File Explorer.
            // With the mouse it opens on release (so pressing to drag a folder doesn't open it), like File Explorer.
            // Folding a row around the open folder moves the mark onto that row; that is not a choice to open it.
            SelectedItemChanged+=(s,e)=>{
                if(syncing)return;
                var now=e.NewValue as TreeViewItem;var was=e.OldValue as TreeViewItem;
                if(now!=null && was!=null && !now.IsExpanded && IsUnder(was,now)){openSoon.Stop();return;}
                if(Mouse.LeftButton!=MouseButtonState.Pressed){openSoon.Stop();openSoon.Start();}
            };
            // The fold arrow only folds and unfolds.
            PreviewMouseLeftButtonUp+=(s,e)=>{if(!OnArrow(e.OriginalSource) && SelectedItem!=null && ItemAt(e.OriginalSource)==SelectedItem){openSoon.Stop();OpenSelected();}};
            AddHandler(TreeViewItem.ExpandedEvent,new RoutedEventHandler(Unfolded));
            // A folder row can be dragged like in File Explorer: onto Love My Shelf to add it, or out to another program.
            Point? down=null;TreeViewItem pressed=null;
            PreviewMouseLeftButtonDown+=(s,e)=>{pressed=FolderRowAt(e.OriginalSource);down=pressed==null?(Point?)null:e.GetPosition(this);};
            PreviewMouseLeftButtonUp+=delegate {down=null;};
            PreviewMouseMove+=(s,e)=>{
                if(down==null || e.LeftButton!=MouseButtonState.Pressed)return;
                var moved=e.GetPosition(this)-down.Value;
                if(Math.Abs(moved.X)<SystemParameters.MinimumHorizontalDragDistance && Math.Abs(moved.Y)<SystemParameters.MinimumVerticalDragDistance)return;
                down=null;var entry=pressed.Tag as TreeEntry;if(entry==null || entry.Path==null)return;
                try {DragDrop.DoDragDrop(pressed,new DataObject(DataFormats.FileDrop,new[]{entry.Path}),DragDropEffects.Copy|DragDropEffects.Link);} catch(COMException) {}
            };
            AddHandler(TreeViewItem.CollapsedEvent,new RoutedEventHandler(Folded));
        }
        static TreeViewItem ItemAt(object source) {
            var node=source as DependencyObject;
            while(node!=null && !(node is TreeViewItem))node=node is Visual?VisualTreeHelper.GetParent(node):LogicalTreeHelper.GetParent(node);
            return node as TreeViewItem;
        }
        static bool OnArrow(object source) {
            var node=source as DependencyObject;
            while(node!=null && !(node is TreeViewItem)) {
                if(node is System.Windows.Controls.Primitives.ToggleButton)return true;
                node=node is Visual?VisualTreeHelper.GetParent(node):LogicalTreeHelper.GetParent(node);
            }
            return false;
        }
        static TreeViewItem ParentRow(TreeViewItem row) {return ItemsControl.ItemsControlFromItemContainer(row) as TreeViewItem;}
        static bool IsUnder(TreeViewItem row,TreeViewItem above) {
            for(var at=ParentRow(row);at!=null;at=ParentRow(at))if(at==above)return true;
            return false;
        }
        bool InTree(TreeViewItem row) {
            ItemsControl at=row;
            while(at is TreeViewItem)at=ItemsControl.ItemsControlFromItemContainer(at);
            return at==this;
        }
        // The row of the open folder, or the outermost folded row hiding it.
        static TreeViewItem VisibleRow(TreeViewItem row) {
            var shown=row;
            for(var at=ParentRow(row);at!=null;at=ParentRow(at))if(!at.IsExpanded)shown=at;
            return shown;
        }
        // The folder of a tree row under the pointer (for dropping files), or null.
        internal static TreeViewItem FolderRowAt(object source) {
            var item=ItemAt(source);
            var entry=item==null?null:item.Tag as TreeEntry;
            return entry!=null && entry.Path!=null && Directory.Exists(entry.Path)?item:null;
        }
        // A row in the Love My Shelf part of the tree (where a dropped folder is added, like File Explorer's Quick access).
        internal static bool IsShelfRow(object source) {
            for(var item=ItemAt(source);item!=null;item=ItemsControl.ItemsControlFromItemContainer(item) as TreeViewItem) {
                var entry=item.Tag as TreeEntry;
                if(entry!=null && entry.Kind=="lms")return true;
            }
            return false;
        }
        internal static TreeEntry EntryAt(object source) {var item=ItemAt(source);return item==null?null:item.Tag as TreeEntry;}
        internal TreeEntry Current {get {var item=SelectedItem as TreeViewItem;return item==null?null:item.Tag as TreeEntry;}}
        void OpenSelected() {
            var entry=Current;if(entry==null)return;
            if(entry.Kind=="pc" || entry.Kind=="network" || entry.Kind=="lms" || (entry.Kind=="computer"))return; // groups only fold
            owner.OpenFromTree(entry);
        }
        // ----- Building -----
        internal void Build() {
            Items.Clear();
            var folded=new HashSet<string>((owner.Settings.TreeFolded??"").Split(new[]{' '},StringSplitOptions.RemoveEmptyEntries));
            var lms=Row(new TreeEntry {Kind="lms",Label="Love My Shelf",Group="lms"},false);
            lms.Items.Add(Row(new TreeEntry {Kind="coverO",Label=owner.AllCropsAlbum.Name,Album=owner.AllCropsAlbum},false));
            lms.Items.Add(Row(new TreeEntry {Kind="coverX",Label=owner.UnfinishedAlbum.Name,Album=owner.UnfinishedAlbum},false));
            lms.Items.Add(Row(new TreeEntry {Kind="similar",Label=owner.SimilarAlbum.Name,Album=owner.SimilarAlbum},false));
            foreach(var album in owner.Store.VisibleAlbums())
                lms.Items.Add(Row(new TreeEntry {Kind="tab",Label=album.Name,Album=album,Path=album.Source},true));
            lms.IsExpanded=!folded.Contains("lms");
            Items.Add(lms);
            // The shell lists come from another thread; the rows appear as soon as they are read.
            var gap=new Thickness(0,10,0,0);
            string onedrive=ShellPlaces.OneDrive();
            if(onedrive!=null) {
                var row=Row(new TreeEntry {Kind="onedrive",Label="OneDrive",Path=onedrive,Group="onedrive"},true);row.Margin=gap;
                Items.Add(row);
            }
            var pinsAt=Items.Count;
            var pc=Row(new TreeEntry {Kind="pc",Label=L.T("This PC"),Group="pc"},true);pc.Margin=gap;Items.Add(pc);
            var network=Row(new TreeEntry {Kind="network",Label=L.T("Network"),Group="network"},true);network.Visibility=Visibility.Collapsed;Items.Add(network);
            Task.Run(()=>ShellPlaces.Pinned()).ContinueWith(t=>{
                if(t.Result==null)return;
                int at=pinsAt;bool first=true;
                foreach(var pin in t.Result) {
                    var row=Row(new TreeEntry {Kind=ShellPlaces.KindOf(pin.Path),Label=pin.Name,Path=pin.Path,Pinned=true},true);
                    if(first){row.Margin=gap;first=false;}
                    Items.Insert(at++,row);
                }
                if(!folded.Contains("pc"))pc.IsExpanded=true;
                owner.TreeSyncSoon();
            },TaskScheduler.FromCurrentSynchronizationContext());
            Task.Run(()=>ShellPlaces.Network()).ContinueWith(t=>{
                if(t.Result!=null && t.Result.Count>0){network.Margin=gap;network.Visibility=Visibility.Visible;if(!folded.Contains("network"))network.IsExpanded=true;}
            },TaskScheduler.FromCurrentSynchronizationContext());
        }
        // A drive was plugged in or out: only the drive rows change, so everything unfolded stays unfolded.
        internal void RefreshDrives() {
            var pc=Items.OfType<TreeViewItem>().FirstOrDefault(r=>r.Tag is TreeEntry && ((TreeEntry)r.Tag).Kind=="pc");
            if(pc==null || (pc.Items.Count==1 && pc.Items[0]==Placeholder))return;
            Task.Run(()=>ShellPlaces.Drives()).ContinueWith(t=>{
                if(t.IsFaulted || t.Result==null)return;
                syncing=true;
                try {
                    foreach(var row in pc.Items.OfType<TreeViewItem>().ToList()) {
                        var entry=(TreeEntry)row.Tag;
                        if(!t.Result.Any(d=>ShellPlaces.Same(d.Path,entry.Path) && d.Name==entry.Label))pc.Items.Remove(row);
                    }
                    int at=0;
                    foreach(var drive in t.Result) {
                        var there=pc.Items.OfType<TreeViewItem>().FirstOrDefault(r=>ShellPlaces.Same(((TreeEntry)r.Tag).Path,drive.Path));
                        if(there==null)pc.Items.Insert(Math.Min(at,pc.Items.Count),Row(new TreeEntry {Kind="drive",Label=drive.Name,Path=drive.Path},true));
                        at++;
                    }
                } finally {syncing=false;}
                owner.TreeSyncSoon();
            },TaskScheduler.FromCurrentSynchronizationContext());
        }
        TreeViewItem Row(TreeEntry entry,bool unfolds) {
            var icon=Icon(entry.Kind);
            var text=new TextBlock {Text=entry.Label,TextTrimming=TextTrimming.CharacterEllipsis,VerticalAlignment=VerticalAlignment.Center};
            var header=new DockPanel {LastChildFill=true};
            if(entry.Pinned) {
                var pin=Line(Drawings["pin"],12);pin.Margin=new Thickness(6,0,4,0);pin.Opacity=0.7;
                DockPanel.SetDock(pin,Dock.Right);header.Children.Add(pin);
            }
            DockPanel.SetDock(icon,Dock.Left);header.Children.Add(icon);header.Children.Add(text);
            // Rows inside rows are made here too, so each gets the look itself (a style on the tree reaches only the top rows).
            var item=new TreeViewItem {Header=header,Tag=entry,ToolTip=entry.Path,Style=(Style)Application.Current.Resources["FolderTreeItem"]};
            ToolTipService.SetInitialShowDelay(item,900);
            System.Windows.Automation.AutomationProperties.SetName(item,entry.Label);
            if(unfolds)item.Items.Add(Placeholder);
            if(entry.Path!=null && entry.Kind!="computer") {
                item.ContextMenu=Menu(entry);
                item.ContextMenuOpening+=(s,e)=>{
                    if(e.Handled)return; // a row inside already showed its menu
                    e.Handled=true;owner.ShowFolderMenu(item.ContextMenu,new List<string>{entry.Path},item,e);
                };
            }
            return item;
        }
        ContextMenu Menu(TreeEntry entry) {
            var menu=new ContextMenu();
            var open=new MenuItem();open.Click+=delegate {owner.OpenFromTree(entry);};
            var openNew=new MenuItem();openNew.Click+=delegate {owner.OpenFolderInNewWindow(entry.Path);};
            var explorer=new MenuItem();explorer.Click+=delegate {
                try {System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(entry.Path) {UseShellExecute=true});} catch(Exception ex) {UI.Error(owner,ex);}
            };
            // Adding to Love My Shelf is the app's "Pin to Quick access".
            var shelf=new MenuItem();shelf.Click+=delegate {
                if(owner.OnShelf(entry.Path))owner.RemoveFromShelf(entry.Path);else owner.AddToShelf(entry.Path);
            };
            var similar=new MenuItem();similar.Click+=delegate {owner.FindSimilar(entry.Path);};
            menu.Items.Add(open);menu.Items.Add(openNew);menu.Items.Add(new Separator());menu.Items.Add(shelf);menu.Items.Add(similar);menu.Items.Add(new Separator());menu.Items.Add(explorer);
            menu.Opened+=delegate {
                open.Header=L.T("Open");openNew.Header=L.T("Open in new window");explorer.Header=L.T("Open in File Explorer");
                similar.Header=L.P("Find similar photos…","비슷한 사진 찾기…");
                shelf.Header=L.T(owner.OnShelf(entry.Path)?"Remove from Love My Shelf":"Add to Love My Shelf");
            };
            return menu;
        }
        // A folder just added shows the folders inside it right away.
        internal void UnfoldShelfFolder(string path) {
            if(Items.Count==0)return;
            var shelf=Items[0] as TreeViewItem;if(shelf==null)return;
            shelf.IsExpanded=true;
            foreach(var row in shelf.Items.OfType<TreeViewItem>()) {
                var entry=row.Tag as TreeEntry;
                if(entry==null || entry.Kind!="tab" || !ShellPlaces.Same(entry.Path,path))continue;
                if(row.Items.Count==1 && row.Items[0]==Placeholder)Load(row,false);
                row.IsExpanded=true;
                return;
            }
        }
        // ----- Unfolding: read the folders inside only now -----
        void Unfolded(object sender,RoutedEventArgs e) {
            var item=e.OriginalSource as TreeViewItem;if(item==null)return;
            RememberFold(item,false);
            if(item.Items.Count==1 && item.Items[0]==Placeholder)Load(item,false);
            // Unfolding around the open folder puts the mark back on it.
            if(!syncing && shownRow!=null && IsUnder(shownRow,item)) {
                var row=VisibleRow(shownRow);
                if(row!=SelectedItem) {
                    syncing=true;
                    try {row.IsSelected=true;} finally {syncing=false;}
                }
            }
        }
        void Folded(object sender,RoutedEventArgs e) {var item=e.OriginalSource as TreeViewItem;if(item!=null)RememberFold(item,true);}
        void RememberFold(TreeViewItem item,bool folded) {
            var entry=item.Tag as TreeEntry;
            if(syncing || entry==null || entry.Group==null)return;
            var set=new HashSet<string>((owner.Settings.TreeFolded??"").Split(new[]{' '},StringSplitOptions.RemoveEmptyEntries));
            if(folded)set.Add(entry.Group);else set.Remove(entry.Group);
            owner.SaveTree(null,null,String.Join(" ",set));
        }
        List<TreeEntry> Children(TreeEntry entry) {
            var list=new List<TreeEntry>();
            if(entry.Kind=="pc") {
                foreach(var drive in ShellPlaces.Drives())list.Add(new TreeEntry {Kind="drive",Label=drive.Name,Path=drive.Path});
            } else if(entry.Kind=="network") {
                foreach(var place in ShellPlaces.Network())list.Add(new TreeEntry {Kind="computer",Label=place.Name,Path=place.Path});
            } else if(entry.Kind=="computer") {
                foreach(var share in ShellPlaces.Shares(entry.Path))list.Add(new TreeEntry {Kind="folder",Label=share.Name,Path=share.Path});
            } else if(entry.Path!=null) {
                foreach(var path in GalleryStore.ChildFolders(entry.Path))
                    list.Add(new TreeEntry {Kind=ShellPlaces.KindOf(path),Label=System.IO.Path.GetFileName(path),Path=path});
            }
            return list;
        }
        void Fill(TreeViewItem item,List<TreeEntry> children) {
            item.Items.Clear();
            foreach(var child in children)item.Items.Add(Row(child,child.Kind!="file"));
        }
        void Load(TreeViewItem item,bool now,Action then=null) {
            var entry=(TreeEntry)item.Tag;
            if(now){Fill(item,Children(entry));return;}
            Task.Run(()=>Children(entry)).ContinueWith(t=>{
                if(item.Items.Count==1 && item.Items[0]==Placeholder)Fill(item,(t.IsFaulted?null:t.Result)??new List<TreeEntry>());
                if(then!=null)then();
            },TaskScheduler.FromCurrentSynchronizationContext());
        }
        // ----- A folder got a new name: its rows follow, and what is unfolded stays unfolded -----
        [DllImport("shlwapi.dll",CharSet=CharSet.Unicode)] static extern int StrCmpLogicalW(string a,string b);
        internal void FolderRenamed(string from,string to) {
            syncing=true;
            try {foreach(var row in Items.OfType<TreeViewItem>().ToList())FollowRename(row,from,to);}
            finally {syncing=false;}
        }
        void FollowRename(TreeViewItem row,string from,string to) {
            var entry=row.Tag as TreeEntry;
            bool renamed=false;
            if(entry!=null && Renamer.IsAt(entry.Path,from)) {
                renamed=ShellPlaces.Same(entry.Path,from);
                entry.Path=Renamer.Moved(entry.Path,from,to);row.ToolTip=entry.Path;
                if(renamed) {
                    entry.Label=System.IO.Path.GetFileName(to);
                    var header=row.Header as DockPanel;
                    var text=header==null?null:header.Children.OfType<TextBlock>().FirstOrDefault();
                    if(text!=null)text.Text=entry.Label;
                    System.Windows.Automation.AutomationProperties.SetName(row,entry.Label);
                }
                if(entry.Album!=null)entry.Album=new Album(owner.Store.AlbumFolderOf(entry.Path),renamed?entry.Label:entry.Album.Name,entry.Path);
            }
            foreach(var child in row.Items.OfType<TreeViewItem>().ToList())FollowRename(child,from,to);
            var parent=ParentRow(row);
            var above=parent==null?null:parent.Tag as TreeEntry;
            if(!renamed || above==null || above.Kind=="lms" || above.Kind=="pc" || above.Kind=="network")return;
            // Back into name order among the folders beside it.
            int now=parent.Items.IndexOf(row),wanted=0;
            foreach(var other in parent.Items.OfType<TreeViewItem>()) {
                if(other==row)continue;
                if(StrCmpLogicalW(((TreeEntry)other.Tag).Label,entry.Label)>0)break;
                wanted++;
            }
            if(wanted==now)return;
            bool marked=row.IsSelected;
            parent.Items.RemoveAt(now);parent.Items.Insert(wanted,row);
            if(marked)row.IsSelected=true;
        }
        // ----- Following the folder on screen -----
        // Marks the row of what the window shows, unfolding the way down to it (as File Explorer does).
        TreeViewItem shownRow;
        string shownKey;
        internal void ShowCurrent(Album active,IList<Album> trail,bool fromPc) {
            if(active==null)return;
            // Still the same folder (a refresh, say): whatever the person folded stays folded.
            string key=active.Folder+"|"+fromPc+"|"+(trail.Count>0?trail[0].Folder:"");
            if(shownRow!=null && shownKey==key && InTree(shownRow))return;
            shownRow=null;
            TreeViewItem found=null;
            syncing=true;
            try {
                var lms=Items.OfType<TreeViewItem>().FirstOrDefault(i=>((TreeEntry)i.Tag).Kind=="lms");
                if(active.Source==null) {
                    if(lms!=null){lms.IsExpanded=true;found=lms.Items.OfType<TreeViewItem>().FirstOrDefault(i=>((TreeEntry)i.Tag).Album!=null && ((TreeEntry)i.Tag).Album.Folder==active.Folder);}
                } else if(!fromPc && trail.Count>0 && lms!=null) {
                    var tab=lms.Items.OfType<TreeViewItem>().FirstOrDefault(i=>((TreeEntry)i.Tag).Album!=null && ((TreeEntry)i.Tag).Album.Folder==trail[0].Folder);
                    if(tab!=null){lms.IsExpanded=true;found=Walk(tab,trail.Skip(1).Select(a=>a.Source));}
                } else {
                    var pc=Items.OfType<TreeViewItem>().FirstOrDefault(i=>((TreeEntry)i.Tag).Kind=="pc");
                    if(pc!=null) {
                        // The drives are read in the background (a sleeping external or network drive can take seconds);
                        // the screen stays usable, and the row is marked once they are there.
                        if(pc.Items.Count==1 && pc.Items[0]==Placeholder){Load(pc,false,()=>owner.TreeSyncSoon());return;}
                        pc.IsExpanded=true;
                        string root=System.IO.Path.GetPathRoot(active.Source);
                        var drive=pc.Items.OfType<TreeViewItem>().FirstOrDefault(i=>ShellPlaces.Same(((TreeEntry)i.Tag).Path,root));
                        if(drive!=null)found=Walk(drive,trail.Select(a=>a.Source).Where(p=>!ShellPlaces.Same(p,root)));
                    }
                }
                if(found!=null) {
                    found.IsSelected=true;found.BringIntoView();
                    // A row short of the folder (not listed yet) is tried again next time.
                    if(active.Source==null || ShellPlaces.Same(((TreeEntry)found.Tag).Path,active.Source)){shownRow=found;shownKey=key;}
                }
                else if(SelectedItem is TreeViewItem)((TreeViewItem)SelectedItem).IsSelected=false;
            } finally {syncing=false;}
        }
        TreeViewItem Walk(TreeViewItem from,IEnumerable<string> paths) {
            var at=from;
            foreach(var path in paths) {
                if(at.Items.Count==1 && at.Items[0]==Placeholder)Load(at,true);
                at.IsExpanded=true;
                var next=at.Items.OfType<TreeViewItem>().FirstOrDefault(i=>ShellPlaces.Same(((TreeEntry)i.Tag).Path,path));
                if(next==null)break;
                at=next;
            }
            return at;
        }
        // ----- Pictures: one line-drawing style for every kind, in the quiet text color -----
        static readonly Dictionary<string,string> Drawings=new Dictionary<string,string> {
            {"folder","M 1.5,4 L 1.5,14.5 L 16.5,14.5 L 16.5,6 L 8.5,6 L 6.8,4 Z"},
            {"tab","M 1.5,4 L 1.5,14.5 L 16.5,14.5 L 16.5,6 L 8.5,6 L 6.8,4 Z"},
            {"drive","M 2,6 L 16,6 L 16,13 L 2,13 Z M 12.5,9.5 L 13.5,9.5"},
            {"pc","M 2,3 L 16,3 L 16,12 L 2,12 Z M 6,15.5 L 12,15.5 M 9,12 L 9,15.5"},
            {"desktop","M 2,3.5 L 16,3.5 L 16,14.5 L 2,14.5 Z M 2,11.5 L 16,11.5"},
            {"downloads","M 9,2.5 L 9,11.5 M 5,7.8 L 9,11.8 L 13,7.8 M 3,15 L 15,15"},
            {"documents","M 4,2 L 11,2 L 14,5 L 14,16 L 4,16 Z M 11,2 L 11,5 L 14,5 M 6.5,9 L 11.5,9 M 6.5,12 L 11.5,12"},
            {"pictures","M 2,3.5 L 16,3.5 L 16,14.5 L 2,14.5 Z M 2.5,13 L 7,8.5 L 10,11.5 L 12,9.5 L 15.5,13 M 11.5,6.5 L 12.5,6.5"},
            {"videos","M 2,4 L 16,4 L 16,14 L 2,14 Z M 7.5,6.8 L 11.8,9 L 7.5,11.2 Z"},
            {"music","M 7,13.5 L 7,3.8 L 15,2.3 L 15,12 M 7,13.5 A 2,1.6 0 1 1 3,13.5 A 2,1.6 0 1 1 7,13.5 Z M 15,12 A 2,1.6 0 1 1 11,12 A 2,1.6 0 1 1 15,12 Z"},
            {"onedrive","M 5,14 L 13.5,14 A 3,3 0 0 0 13.5,8 A 4.5,4.5 0 0 0 5,9 A 2.5,2.5 0 0 0 5,14 Z"},
            {"network","M 7,1.5 L 11,1.5 L 11,5 L 7,5 Z M 9,5 L 9,9.5 M 4,12.5 L 4,9.5 L 14,9.5 L 14,12.5 M 2,12.5 L 6,12.5 L 6,16 L 2,16 Z M 12,12.5 L 16,12.5 L 16,16 L 12,16 Z"},
            {"computer","M 2,3 L 16,3 L 16,12 L 2,12 Z M 6,15.5 L 12,15.5 M 9,12 L 9,15.5"},
            {"pin","M 6.5,2.5 L 11.5,2.5 L 10.5,7 L 13,9.5 L 5,9.5 L 7.5,7 Z M 9,9.5 L 9,15.5"},
            {"coverO","M 5.5,1.5 L 12.5,1.5 L 12.5,16.5 L 5.5,16.5 Z M 9,7 A 2,2 0 1 1 8.99,7 Z"},
            {"coverX","M 5.5,1.5 L 12.5,1.5 L 12.5,16.5 L 5.5,16.5 Z M 7.3,7.3 L 10.7,10.7 M 10.7,7.3 L 7.3,10.7"},
            {"similar","M 2.5,4.5 L 10.5,4.5 L 10.5,15.5 L 2.5,15.5 Z M 7.5,2.5 L 15.5,2.5 L 15.5,13.5 L 10.5,13.5"},
        };
        static FrameworkElement Line(string drawing,double size) {
            var path=new System.Windows.Shapes.Path {Data=Geometry.Parse(drawing),Width=18,Height=18,StrokeThickness=1.3,
                StrokeLineJoin=PenLineJoin.Round,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,
                Stretch=Stretch.None,SnapsToDevicePixels=true,IsHitTestVisible=false,VerticalAlignment=VerticalAlignment.Center};
            path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,"Subtle");
            if(size!=18)path.LayoutTransform=new ScaleTransform(size/18,size/18);
            return path;
        }
        static ImageSource appIcon;
        static FrameworkElement Icon(string kind) {
            FrameworkElement picture;
            if(kind=="lms") {
                try {
                    if(appIcon==null)
                        using(var icon=System.Drawing.Icon.ExtractAssociatedIcon((Assembly.GetEntryAssembly()??Assembly.GetExecutingAssembly()).Location))
                            appIcon=System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(icon.Handle,Int32Rect.Empty,BitmapSizeOptions.FromWidthAndHeight(32,32));
                } catch(Exception) {}
                picture=appIcon!=null?(FrameworkElement)new Image {Source=appIcon,Width=16,Height=16,VerticalAlignment=VerticalAlignment.Center}:Line(Drawings["folder"],18);
            } else picture=Line(Drawings.ContainsKey(kind)?Drawings[kind]:Drawings["folder"],18);
            picture.Margin=new Thickness(0,0,8,0);
            return picture;
        }
    }
}
