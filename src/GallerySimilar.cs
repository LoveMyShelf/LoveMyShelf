using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LoveMyShelf {
    // The "Similar photos" collection in the folder window: groups of the same pictures, each group starting on a new row,
    // shown with the same tiles as any folder, so covers and originals can be looked at while choosing what to keep.
    public partial class GalleryWindow {
        internal const string SimilarKey="@similar";
        Album similarAlbum;
        internal Album SimilarAlbum {get {return similarAlbum??(similarAlbum=new Album(SimilarKey,L.P("Similar photos","비슷한 사진 모음")));}}
        bool ShowingSimilar {get {return active!=null && active.Folder==SimilarKey;}}
        List<List<Photo>> similarShown;
        DockPanel similarBar;
        Button similarFolders,similarFind,similarStop,similarKeepAll;
        TextBlock similarStatus,similarRule;
        RadioButton similarExact,similarNear,similarCovers,similarOriginals;
        readonly RadioButton[] similarLevels=new RadioButton[3];
        FrameworkElement similarLevelRow;
        bool similarUpdating;

        static string S(string english,string korean) {return L.P(english,korean);}
        static RadioButton Choice(string group,Action picked) {
            var button=new RadioButton {GroupName=group,Style=(Style)Application.Current.FindResource("SegmentChoice")};
            button.Checked+=delegate {picked();};
            return button;
        }
        static Border Segment(params UIElement[] buttons) {
            var row=new StackPanel {Orientation=Orientation.Horizontal};
            foreach(var b in buttons)row.Children.Add(b);
            var box=new Border {Child=row,CornerRadius=new CornerRadius(8),BorderThickness=new Thickness(1),Padding=new Thickness(2),
                VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,10,4)};
            box.SetResourceReference(Border.BackgroundProperty,"Panel");box.SetResourceReference(Border.BorderBrushProperty,"Line");
            return box;
        }
        // The bar above the tiles, shown only in this collection: which folders, Find/Stop, what is going on,
        // which list, how alike, covers or originals.
        void SetupSimilar(DockPanel area) {
            SimilarSearch.Use(store);
            similarBar=new DockPanel {Visibility=Visibility.Collapsed,Margin=new Thickness(10,6,10,4)};
            DockPanel.SetDock(similarBar,Dock.Top);
            var lines=new StackPanel();similarBar.Children.Add(lines);
            var first=new WrapPanel();
            var label=UI.Text(S("Look in:","찾을 폴더:"),13);label.Margin=new Thickness(0,0,6,4);
            similarFolders=UI.Btn("",OpenSimilarFolders);similarFolders.Margin=new Thickness(0,0,8,4);
            similarFind=UI.Btn(S("Find","찾기"),()=>{if(SimilarSearch.Folders.Count==0){OpenSimilarFolders();return;}SimilarSearch.Start(false);},true);
            similarFind.Margin=new Thickness(0,0,6,4);similarFind.Padding=new Thickness(14,0,14,0);
            similarStop=UI.Btn(S("Stop","멈추기"),()=>{if(SimilarSearch.Working){SimilarSearch.StopWork=true;SimilarSearch.Changed();}else SimilarSearch.Stop();});similarStop.Margin=new Thickness(0,0,8,4);
            similarKeepAll=UI.Btn(S("Tidy up…","정리하기…"),TidyExact);similarKeepAll.Margin=new Thickness(0,0,8,4);
            similarKeepAll.ToolTip=S("Tidy up exactly the same files: choose the folder for each photo, or let it be done automatically. Right-click to change how.",
                "똑같은 파일을 정리해요. 사진마다 남길 폴더를 고르거나, 알아서 정리하게 할 수 있어요. 오른쪽 클릭하면 방식을 바꿔요.");
            similarKeepAll.MouseRightButtonUp+=(s,e)=>{e.Handled=true;TidyWayMenu();};
            similarStatus=UI.Text("",12.5,true);similarStatus.TextWrapping=TextWrapping.Wrap;similarStatus.Margin=new Thickness(0,0,0,4);similarStatus.MaxWidth=700;
            first.Children.Add(label);first.Children.Add(similarFolders);first.Children.Add(similarFind);first.Children.Add(similarStop);first.Children.Add(similarKeepAll);first.Children.Add(similarStatus);
            lines.Children.Add(first);
            var second=new WrapPanel {Margin=new Thickness(0,2,0,0)};
            string list="similar-list-"+Guid.NewGuid().ToString("N"),look="similar-look-"+Guid.NewGuid().ToString("N"),alike="similar-alike-"+Guid.NewGuid().ToString("N");
            similarExact=Choice(list,()=>{if(similarUpdating || !SimilarSearch.ShowNear)return;SimilarSearch.ShowNear=false;SimilarSearch.ViewChanged();});
            similarNear=Choice(list,()=>{if(similarUpdating || SimilarSearch.ShowNear)return;SimilarSearch.ShowNear=true;SimilarSearch.ViewChanged();});
            similarExact.FontSize=13;similarNear.FontSize=13;
            second.Children.Add(Segment(similarExact,similarNear));
            string[] names={S("Strict","엄격하게"),S("Normal","보통"),S("Loose","넓게")};
            var levels=new UIElement[3];
            for(int i=0;i<3;i++) {
                int at=i;
                similarLevels[i]=Choice(alike,()=>{if(!similarUpdating)SimilarSearch.SetLevel(at);});
                similarLevels[i].Content=names[i];levels[i]=similarLevels[i];
            }
            var levelPanel=new StackPanel {Orientation=Orientation.Horizontal};
            var levelLabel=UI.Text(S("How alike:","얼마나 같아야:"),12,true);levelLabel.Margin=new Thickness(0,0,6,4);levelLabel.VerticalAlignment=VerticalAlignment.Center;
            levelPanel.Children.Add(levelLabel);levelPanel.Children.Add(Segment(levels));
            levelPanel.ToolTip=S("Strict finds fewer, surer groups. Loose finds more, but different pictures may slip in.","엄격하게는 확실한 것만 적게, 넓게는 더 많이 찾지만 다른 사진이 섞일 수 있어요.");
            similarLevelRow=levelPanel;second.Children.Add(levelPanel);
            similarCovers=Choice(look,()=>SimilarLook(false));similarCovers.Content=S("Show covers","표지로 보기");
            similarOriginals=Choice(look,()=>SimilarLook(true));similarOriginals.Content=S("Show originals","원본으로 보기");
            second.Children.Add(Segment(similarCovers,similarOriginals));
            // How "Suggested to keep" is picked: always on screen (also in a narrow window), the whole rule on hover.
            similarRule=UI.Text(S("ⓘ How is “Suggested to keep” picked?","ⓘ ‘남기기 추천’은 어떻게 고르나요?"),12.5);
            similarRule.Margin=new Thickness(0,0,12,4);similarRule.Cursor=System.Windows.Input.Cursors.Help;
            similarRule.TextDecorations=TextDecorations.Underline;
            ToolTipService.SetShowDuration(similarRule,120000);ToolTipService.SetInitialShowDelay(similarRule,150);
            second.Children.Add(similarRule);
            var hint=UI.Text(S("Double-click a tile to compare side by side; Space opens the large view. Rest the mouse on a tile to see its details. Delete sends the chosen files to the Recycle Bin.",
                "칸을 더블클릭하면 나란히 비교, Space는 크게 보기예요. 칸에 마우스를 올리면 자세한 정보가 나와요. Delete 키는 고른 파일을 휴지통으로 보내요."),12,true);
            hint.TextWrapping=TextWrapping.Wrap;hint.Margin=new Thickness(0,0,0,4);hint.MaxWidth=520;
            second.Children.Add(hint);
            // A narrow window (two side by side) keeps its height for the tiles: the hint goes into the tooltips instead.
            similarBar.SizeChanged+=delegate {hint.Visibility=similarBar.ActualWidth<900?Visibility.Collapsed:Visibility.Visible;};
            similarExact.ToolTip=similarNear.ToolTip=hint.Text;
            lines.Children.Add(second);
            area.Children.Add(similarBar);
            Action state=()=>{if(ShowingSimilar)UpdateSimilarBar();};
            // During a search the tiles stay as they are (no jumping); they change when it ends, or at once while empty.
            Action groups=()=>{if(ShowingSimilar && (!SimilarSearch.Busy || photos.Count==0))ReloadKeepingPlace();};
            Action view=()=>{if(ShowingSimilar){scrollToTop=true;UpdateSimilarBar();Refresh();}};
            SimilarSearch.State+=state;SimilarSearch.Groups+=groups;SimilarSearch.View+=view;
            Closed+=delegate {SimilarSearch.State-=state;SimilarSearch.Groups-=groups;SimilarSearch.View-=view;};
        }
        void SimilarLook(bool originals) {
            if(similarUpdating || SimilarSearch.ShowOriginals==originals)return;
            SimilarSearch.ShowOriginals=originals;SimilarSearch.ViewChanged();
        }
        // Shows or hides the bar for the collection on screen; opening it the first time shows last time's groups.
        void ShowSimilarBar() {
            if(similarBar==null)return;
            similarShown=null; // what was shown on an earlier visit is read again (files may have gone meanwhile)
            similarBar.Visibility=ShowingSimilar?Visibility.Visible:Visibility.Collapsed;
            if(!ShowingSimilar)return;
            if(!SimilarSearch.HasResult && !SimilarSearch.Busy && SimilarSearch.Folders.Count>0)SimilarSearch.Start(true);
            UpdateSimilarBar();
        }
        void UpdateSimilarBar() {
            similarUpdating=true;
            try {
                int count=SimilarSearch.Folders.Count;
                similarFolders.Content=count==0?S("Choose folders ▾","폴더 고르기 ▾"):
                    count==1?SimilarFolderName(SimilarSearch.Folders[0])+" ▾":L.PF("{0} and {1:N0} more ▾","{0} 외 {1:N0}개 ▾",SimilarFolderName(SimilarSearch.Folders[0]),count-1);
                similarFolders.ToolTip=count==0?null:String.Join("\n",SimilarSearch.Folders);
                bool busy=SimilarSearch.Busy,working=SimilarSearch.Working;
                similarFind.Visibility=busy || working?Visibility.Collapsed:Visibility.Visible;
                similarStop.Visibility=busy || working?Visibility.Visible:Visibility.Collapsed;
                similarStop.IsEnabled=!(working && SimilarSearch.StopWork);
                similarKeepAll.Visibility=!busy && !working && !SimilarSearch.ShowNear && SimilarSearch.Exact.Count>0?Visibility.Visible:Visibility.Collapsed;
                similarFind.Content=SimilarSearch.HasResult?S("Find again","다시 찾기"):S("Find","찾기");
                similarStatus.Text=count==0?S("Choose the folders to look through, then click [Find].","찾아볼 폴더를 고른 뒤 [찾기]를 눌러요."):SimilarSearch.Phase;
                similarExact.Content=S("Exactly the same files","똑같은 파일")+"  "+SimilarSearch.Exact.Count.ToString("N0");
                similarNear.Content=S("Nearly the same photos","거의 같은 사진")+"  "+SimilarSearch.Near.Count.ToString("N0");
                similarExact.IsChecked=!SimilarSearch.ShowNear;similarNear.IsChecked=SimilarSearch.ShowNear;
                similarLevelRow.Visibility=SimilarSearch.ShowNear?Visibility.Visible:Visibility.Collapsed;
                similarLevels[SimilarSearch.Level].IsChecked=true;
                similarRule.ToolTip=new TextBlock {Text=SimilarInfo.Rule(!SimilarSearch.ShowNear),TextWrapping=TextWrapping.Wrap,MaxWidth=460,FontSize=13,LineHeight=20};
                similarCovers.IsChecked=!SimilarSearch.ShowOriginals;similarOriginals.IsChecked=SimilarSearch.ShowOriginals;
            } finally {similarUpdating=false;}
        }
        static string SimilarFolderName(string path) {string name=Path.GetFileName(path.TrimEnd('\\'));return String.IsNullOrEmpty(name)?path:name;}
        void OpenSimilarFolders() {
            var menu=new ContextMenu {PlacementTarget=similarFolders,Placement=System.Windows.Controls.Primitives.PlacementMode.Bottom};
            bool busy=SimilarSearch.Busy;
            foreach(string folder in SimilarSearch.Folders.ToList()) {
                string f=folder;
                var item=new MenuItem {Header=L.PF("Take out “{0}”","‘{0}’ 빼기",SimilarFolderName(f)).Replace("_","__"),ToolTip=f,IsEnabled=!busy};
                item.Click+=delegate {SimilarSearch.RemoveFolder(f);};
                menu.Items.Add(item);
            }
            if(SimilarSearch.Folders.Count>0)menu.Items.Add(new Separator());
            var add=new MenuItem {Header=S("Add a folder…","폴더 추가…"),IsEnabled=!busy};
            add.Click+=delegate {
                string path=UI.PickFolder(this,S("Choose a folder to look through.","찾아볼 폴더를 골라요."),SimilarSearch.Folders.LastOrDefault(),false);
                if(path!=null)SimilarSearch.AddFolder(path);
            };
            menu.Items.Add(add);
            var shelf=store.VisibleAlbums().Where(a=>a.Source!=null).ToList();
            if(shelf.Count>0) {
                var all=new MenuItem {Header=S("Add every LoveMyShelf folder","LoveMyShelf 폴더 모두 넣기"),IsEnabled=!busy};
                all.Click+=delegate {foreach(var a in shelf)SimilarSearch.AddFolder(a.Source);};
                menu.Items.Add(all);
            }
            menu.Items.Add(new Separator());
            var inside=new MenuItem {Header=S("Include the folders inside","안에 있는 폴더도 함께"),IsCheckable=true,IsChecked=SimilarSearch.Inside,IsEnabled=!busy};
            inside.Click+=delegate {SimilarSearch.Inside=inside.IsChecked;SimilarSearch.SaveSettings();};
            menu.Items.Add(inside);
            menu.IsOpen=true;
        }
        // From a folder's right-click menu: this folder is looked through and the collection opens.
        internal void FindSimilar(string folder) {
            if(folder!=null && Directory.Exists(folder) && !SimilarSearch.Busy && !SimilarSearch.Working) {
                SimilarSearch.AddFolder(folder);
                SimilarSearch.Start(false); // before the collection opens, so it doesn't show last time's result instead
            }
            Select(SimilarAlbum);
        }
        // ----- The tiles -----
        // The groups of the list on screen as tiles (read off the UI thread: each file's cover record is read).
        Task<List<List<Photo>>> BuildSimilar(string query) {
            var groups=(SimilarSearch.ShowNear?SimilarSearch.Near:SimilarSearch.Exact).ToList();
            bool originals=SimilarSearch.ShowOriginals;
            return Task.Run(()=>{
                if(query.Length>0) {
                    bool tagsOnly=query.StartsWith("#");string word=query.TrimStart('#').Trim();
                    groups=groups.Where(g=>g.Files.Any(f=>(!tagsOnly && f.Name.IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0) ||
                        (word.Length>0 && Tags.Of(store.AlbumFolderOf(f.Folder),f.Name).Any(t=>t.IndexOf(word,StringComparison.OrdinalIgnoreCase)>=0)))).ToList();
                }
                // Folders get their records the first time, as when they are opened (covers made from here need them).
                foreach(string dir in groups.SelectMany(g=>g.Files).Select(f=>f.Folder).Distinct(StringComparer.OrdinalIgnoreCase)) {
                    try {if(Directory.Exists(dir) && !File.Exists(Path.Combine(store.AlbumFolderOf(dir),"source.txt")))store.OpenSubfolder(dir);} catch(Exception) {}
                }
                var result=new List<List<Photo>>();
                int number=0;
                foreach(var group in groups) {
                    number++;
                    var tiles=new List<Photo>();
                    foreach(var file in group.Files) {
                        string record=Path.Combine(store.AlbumFolderOf(file.Folder),"p-"+GalleryStore.KeyOf(file.Name));
                        Photo photo=null;
                        if(File.Exists(Path.Combine(record,"photo.xml"))) {try {photo=GalleryStore.Read(record,file.Path);} catch(Exception) {}}
                        if(photo==null)photo=new Photo {Folder=record,Name=file.Name,Original=file.Path,Linked=true};
                        photo.Similar=group;photo.SimilarFile=file;photo.ShowOriginal=originals;
                        photo.SimilarKeep=file==group.Keep;
                        SimilarInfo.ReadDates(file);
                        photo.SimilarCovers=SimilarSearch.CoverCount(store,file);
                        photo.SimilarInfo=SimilarInfo.TileLines(group,file,photo.SimilarCovers);
                        tiles.Add(photo);
                    }
                    tiles[0].SimilarLabel=L.PF("Group {0:N0} · {1:N0} files","묶음 {0:N0} · {1:N0}개",number,group.Files.Count);
                    result.Add(tiles);
                }
                return result;
            });
        }
        // Each group starts on a new row: the rest of its last row stays empty.
        List<Photo> PadSimilar(int perRow) {
            var list=new List<Photo>();
            if(similarShown==null)return list;
            foreach(var group in similarShown) {
                list.AddRange(group);
                int rest=group.Count%Math.Max(1,perRow);
                if(rest>0)for(int i=rest;i<perRow;i++)list.Add(null);
            }
            return list;
        }
        void SimilarCount() {
            int groups=similarShown==null?0:similarShown.Count,files=similarShown==null?0:similarShown.Sum(g=>g.Count);
            long saving=similarShown==null?0:similarShown.Sum(g=>g.Count>0 && g[0].Similar!=null?g[0].Similar.Saving:0);
            count.Text=groups==0?"":L.PF("{0:N0} groups · {1:N0} files · keeping one of each frees up to {2}","묶음 {0:N0}개 · 파일 {1:N0}개 · 하나씩만 남기면 최대 {2} 비워져요",groups,files,Bytes(saving));
            count.ToolTip=null;canShuffle=false;
        }
        void SimilarEmptyNote() {
            bool searching=search.Text.Trim().Length>0;
            emptyTitle.Text=searching?L.T("No search results"):SimilarSearch.Busy?S("Looking…","찾는 중이에요…"):
                SimilarSearch.Folders.Count==0?S("Choose the folders to look through","찾아볼 폴더를 골라 주세요"):
                SimilarSearch.HasResult?(SimilarSearch.ShowNear?S("No photos are nearly the same","거의 같은 사진이 없어요"):S("No files are exactly the same","똑같은 파일이 없어요")):S("Not looked yet","아직 찾지 않았어요");
            emptyHelp.Text=searching?L.T("Search with another name, or clear the search box."):
                SimilarSearch.Folders.Count==0?S("Click [Choose folders] above, add folders, then click [Find].","위쪽 [폴더 고르기]에서 폴더를 넣고 [찾기]를 눌러요."):
                SimilarSearch.Busy?S("Groups appear here as they are found.","찾은 묶음이 여기에 나와요."):
                SimilarSearch.ShowNear && SimilarSearch.HasResult?S("Try [Loose], or look at [Exactly the same files].","[넓게]를 고르거나 [똑같은 파일]도 봐 주세요."):
                S("Click [Find] above.","위쪽 [찾기]를 눌러요.");
            emptyAction.Visibility=Visibility.Collapsed;
            empty.Visibility=photos.Count==0?Visibility.Visible:Visibility.Collapsed;
        }
        internal static string Bytes(long bytes) {
            var inv=CultureInfo.InvariantCulture;
            if(bytes>=1L<<30)return (bytes/(double)(1L<<30)).ToString("0.0",inv)+" GB";
            if(bytes>=1L<<20)return (bytes/(double)(1L<<20)).ToString("0.0",inv)+" MB";
            if(bytes>=1L<<10)return (bytes/(double)(1L<<10)).ToString("0",inv)+" KB";
            return bytes.ToString(inv)+" B";
        }
        // ----- Right-click items on a tile of this collection -----
        internal void AddSimilarItems(ContextMenu menu,Photo photo) {
            if(!ShowingSimilar || photo.Similar==null)return;
            var group=photo.Similar;var file=photo.SimilarFile;
            if(group.Exact) {
                var keep=new MenuItem {Header=S("Keep only this one, tidy up the copies…","이 파일만 남기고 사본 정리…"),
                    ToolTip=S("The covers and tags of the copies move to this file, then the copies go to the Recycle Bin.","사본에 있던 표지와 태그를 이 파일로 옮긴 뒤 사본을 휴지통으로 보내요.")};
                keep.Click+=delegate {KeepOnly(photo);};
                menu.Items.Add(keep);
            }
            if(group.Exact || (file.IsPicture && group.Files.Count(f=>f.IsPicture)>1)) {
                var compare=new MenuItem {Header=group.Exact?S("Choose the one to keep… (double-click)","남길 파일 고르기… (더블클릭)"):S("Compare side by side… (double-click)","나란히 비교… (더블클릭)"),
                    ToolTip=group.Exact?S("Shows the picture and every copy with its folder, to choose the one that stays.","그림과 사본 목록을 폴더와 함께 보여 줘요. 남길 파일을 골라요.")
                        :S("Two photos of this group big, side by side. Press 1 or 2 for the one to keep: the other goes to the Recycle Bin and the next one comes.",
                        "이 묶음의 사진 두 장을 크게 나란히 보여 줘요. 남길 사진을 1이나 2로 고르면 다른 사진은 휴지통으로 가고 다음 사진이 와요.")};
                compare.Click+=delegate {CompareSimilar(photo);};
                menu.Items.Add(compare);
            }
            var large=new MenuItem {Header=S("Large view (Space)","크게 보기 (Space)")};
            large.Click+=delegate {OpenPhoto(photo);};
            menu.Items.Add(large);
            var others=new MenuItem {Header=S("Choose the others of this group","이 묶음에서 나머지 고르기")};
            others.Click+=delegate {
                selected.Clear();
                foreach(var p in photos)if(p!=null && p.Similar==group && p.SimilarFile!=file)selected.Add(p.Folder);
                RefreshSelection();
            };
            menu.Items.Add(others);
            if(!group.Exact) {
                var apart=new MenuItem {Header=S("This one is a different photo","이 사진은 다른 사진이에요")};
                apart.Click+=delegate {SimilarSearch.NotSame(group,file);};
                var split=new MenuItem {Header=S("These aren't the same photo","이 묶음은 같은 사진이 아니에요")};
                split.Click+=delegate {SimilarSearch.NotSame(group,null);};
                menu.Items.Add(apart);menu.Items.Add(split);
            }
            menu.Items.Add(new Separator());
        }
        // ----- To the Recycle Bin -----
        bool SimilarReady() {
            if(SimilarSearch.Busy){Notify(S("Still looking","아직 찾는 중이에요"),S("This can be done when the search has finished.","찾기가 끝나면 할 수 있어요."),null,NoticeKind.Info);return false;}
            return !SimilarSearch.Working;
        }
        // The chosen originals (Delete, or the menu) go to the Recycle Bin. Covers are not moved here (see KeepOnly).
        async void SimilarRecycle(List<Photo> targets) {
            // Each file is judged in the group it is shown in: in "exactly the same files" a byte-for-byte copy must stay.
            var shown=targets.Where(p=>p!=null && p.SimilarFile!=null && p.Similar!=null).GroupBy(p=>p.SimilarFile).Select(g=>g.First()).ToList();
            var picked=shown.Select(p=>p.SimilarFile).ToList();
            var groupOf=shown.ToDictionary(p=>p.SimilarFile,p=>p.Similar);
            if(picked.Count==0 || !SimilarReady())return;
            if(groupOf.Values.Distinct().Any(g=>g.Files.All(o=>picked.Contains(o) || !File.Exists(o.Path)))) {
                MessageBox.Show(this,S("Every file of a group is chosen. Leave at least one file of each group.","한 묶음의 파일이 모두 골라져 있어요. 묶음마다 적어도 하나는 남겨 주세요."),
                    "LoveMyShelf",MessageBoxButton.OK,MessageBoxImage.Information);
                return;
            }
            string question=L.PF("Move the {0:N0} chosen files ({1}) to the Recycle Bin?\nYou can get them back from the Recycle Bin.",
                "고른 파일 {0:N0}개({1})를 휴지통으로 보낼까요?\n휴지통에서 다시 되살릴 수 있어요.",picked.Count,Bytes(picked.Sum(f=>f.Size)));
            int covers=picked.Count(f=>f.HasCover);
            if(covers>0) {
                question+="\n\n"+L.PF("{0:N0} of them have a cover. Their covers stay, but show “original missing”.","그중 {0:N0}개는 표지가 있어요. 표지는 남지만 ‘원본 없음’으로 보여요.",covers);
                if(picked.Any(f=>f.HasCover && groupOf[f].Exact))
                    question+="\n"+S("To move the covers of exact copies to the file that stays, right-click that file and use [Keep only this one].",
                        "똑같은 파일의 표지를 남는 파일로 옮기려면, 남길 파일을 오른쪽 클릭해서 [이 파일만 남기고 사본 정리]를 써 주세요.");
            }
            if(picked.Any(f=>!groupOf[f].Exact))
                question+="\n\n"+S("Some come from “nearly the same” groups. Those can differ a little from the one you keep, so check them once more.",
                    "‘거의 같은 사진’에서 고른 파일도 있어요. 남기는 사진과 조금 다를 수 있으니 한 번 더 확인해 주세요.");
            if(MessageBox.Show(this,question,"LoveMyShelf",MessageBoxButton.OKCancel,MessageBoxImage.Question)!=MessageBoxResult.OK)return;
            if(!SimilarReady() || lifetime.IsCancellationRequested)return; // another window started something while the question was open
            Begin();
            int skipped=0;string lastError=null;
            var gone=new List<SimilarFile>();
            var handle=new System.Windows.Interop.WindowInteropHelper(this).Handle;
            try {
                foreach(var file in picked) {
                    if(lifetime.IsCancellationRequested)break;
                    var groups=new List<SimilarGroup>{groupOf[file]};
                    SimilarSearch.Hold hold=null;
                    try {hold=await Task.Run(()=>SimilarSearch.Lock(file,groups,picked));} catch(Exception ex) {lastError=ex.Message;}
                    if(hold==null){skipped++;continue;}
                    using(hold) {
                        if(CoverWork.HoldsPath(file.Path) || !hold.StillAt(file.Path)){skipped++;continue;}
                        try {RecycleBin.MoveFile(file.Path,handle);gone.Add(file);}
                        catch(Exception ex) {skipped++;lastError=ex.Message;}
                    }
                }
            } finally {End();}
            if(lifetime.IsCancellationRequested)return;
            SimilarDone(gone,skipped,lastError,0,new List<SimilarFile>());
        }
        void SimilarDone(List<SimilarFile> gone,int skipped,string lastError,int coversMoved,List<SimilarFile> kept) {
            var changed=gone.Select(f=>f.Path).ToList();
            changed.AddRange(kept.Select(f=>f.Path));
            if(changed.Count>0)try {FilesRecycled(changed);} catch(Exception) {}
            SimilarSearch.Removed(gone);
            ClearSelection();
            string text=L.PF("{0:N0} files went to the Recycle Bin.","파일 {0:N0}개를 휴지통으로 보냈어요.",gone.Count);
            if(coversMoved>0)text+=" "+L.PF("{0:N0} covers moved to the file that stays.","표지 {0:N0}장을 남는 파일로 옮겼어요.",coversMoved);
            if(skipped>0)text+=" "+L.PF("{0:N0} were left alone because they changed, are gone or are in use.","{0:N0}개는 바뀌었거나 없거나 쓰는 중이라 그대로 두었어요.",skipped);
            if(lastError!=null && gone.Count==0)text+=" "+lastError;
            Notify(S("Similar photos","비슷한 사진 모음"),text,null,skipped>0?NoticeKind.Warning:NoticeKind.Done);
        }
        // Exactly the same files: this one stays; the covers (main and extra, with their GIF covers and upscales) and tags
        // of every copy move to it, then the copies go to the Recycle Bin. A copy that can't go gets its covers back.
        async void KeepOnly(Photo photo) {
            var group=photo.Similar;var keeper=photo.SimilarFile;
            if(group==null || !group.Exact || keeper==null || !SimilarReady())return;
            var copies=group.Files.Where(f=>f!=keeper).ToList();
            int covers=copies.Sum(f=>SimilarSearch.CoverCount(store,f));
            var tags=copies.SelectMany(f=>Tags.Of(store.AlbumFolderOf(f.Folder),f.Name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string question=L.PF("Keep only “{0}” ({1})\nand move its {2:N0} copies to the Recycle Bin?","‘{0}’({1})만 남기고\n사본 {2:N0}개를 휴지통으로 보낼까요?",
                keeper.Name,SimilarFolderName(keeper.Folder),copies.Count);
            if(covers>0 || tags.Count>0)
                question+="\n\n"+L.PF("{0:N0} covers and {1:N0} tags of the copies move to the file that stays.","사본에 있던 표지 {0:N0}장과 태그 {1:N0}개는 남는 파일로 옮겨요.",covers,tags.Count)
                    +"\n"+S("If you get a copy back from the Recycle Bin later, its covers stay with this file.","나중에 휴지통에서 사본을 되살려도 표지는 이 파일에 남아 있어요.");
            if(MessageBox.Show(this,question,"LoveMyShelf",MessageBoxButton.OKCancel,MessageBoxImage.Question)!=MessageBoxResult.OK)return;
            if(!SimilarReady())return;
            var result=new Tidied();
            Begin();
            try {await Tidy(group,keeper,group.Files.Where(f=>f!=keeper).ToList(),result);} finally {End();}
            if(lifetime.IsCancellationRequested)return;
            SimilarDone(result.Gone,result.Skipped,result.LastError,result.Moved,result.Kept);
        }
        // Every group of exactly the same files at once: each keeps its suggested file (the one with covers, the biggest,
        // the oldest), the copies' covers and tags move to it, and the copies go to the Recycle Bin. One question for all.
        // [Tidy up…]: one group at a time (the compare window asks the folder for each photo) or automatically (as suggested).
        void TidyExact() {
            if(SimilarSearch.ShowNear || !SimilarReady())return;
            int way=SimilarSearch.TidyWay!=0?SimilarSearch.TidyWay:AskTidyWay();
            if(way==1) {
                var first=SimilarSearch.Exact.FirstOrDefault(g=>g.Files.Count(f=>File.Exists(f.Path))>1);
                if(first!=null)new SimilarCompareWindow(this,first,null,null).Show();
            } else if(way==2)KeepAllSuggested();
        }
        void TidyWayMenu() {
            var menu=new ContextMenu {PlacementTarget=similarKeepAll};
            Action<string,int> item=(text,value)=>{
                var m=new MenuItem {Header=text,IsCheckable=true,IsChecked=SimilarSearch.TidyWay==value};
                m.Click+=delegate {SimilarSearch.TidyWay=value;SimilarSearch.SaveSettings();};
                menu.Items.Add(m);
            };
            item(S("Ask each time","매번 물어보기"),0);
            item(S("Choose the folder for each photo","사진마다 남길 폴더 고르기"),1);
            item(S("Tidy up automatically","알아서 정리"),2);
            menu.IsOpen=true;
        }
        int AskTidyWay() {
            int result=0;
            var ask=new Window {SizeToContent=SizeToContent.WidthAndHeight,ResizeMode=ResizeMode.NoResize,ShowInTaskbar=false};
            UI.WindowSetup(ask,S("Tidy up","정리하기"));ask.Owner=this;ask.WindowStartupLocation=WindowStartupLocation.CenterOwner;
            var root=new DockPanel();root.Children.Add(new WindowTitleBar(ask));UI.Use(root,Panel.BackgroundProperty,"Paper");
            var body=new StackPanel {Margin=new Thickness(22,10,22,18),MaxWidth=520};
            var head=UI.Text(L.PF("{0:N0} groups of exactly the same files. How should they be tidied up?","똑같은 파일 묶음이 {0:N0}개예요. 어떻게 정리할까요?",SimilarSearch.Exact.Count),15);
            head.FontWeight=FontWeights.SemiBold;head.TextWrapping=TextWrapping.Wrap;head.Margin=new Thickness(0,0,0,14);
            body.Children.Add(head);
            Func<string,string,int,bool,Button> choice=(title,detail,value,primary)=>{
                var face=new StackPanel {Margin=new Thickness(4,2,4,3)};
                face.Children.Add(new TextBlock {Text=title,FontSize=14,FontWeight=FontWeights.SemiBold});
                face.Children.Add(new TextBlock {Text=detail,FontSize=12.5,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,2,0,0),Opacity=0.85});
                var b=new Button {Content=face,HorizontalContentAlignment=HorizontalAlignment.Left,Padding=new Thickness(12,6,12,6),Margin=new Thickness(0,0,0,8),MinHeight=48};
                if(primary){b.Style=(Style)Application.Current.Resources["Primary"];b.IsDefault=true;}
                b.Click+=delegate {result=value;ask.Close();};
                return b;
            };
            body.Children.Add(choice(S("Choose the folder for each photo","사진마다 남길 폴더 고르기"),
                S("Group by group, it asks which folder the photo stays in. Number key or double-click, then Space for the next.",
                  "묶음마다 이 사진을 어느 폴더에 남길지 물어요. 숫자 키나 더블클릭으로 고르고 Space로 다음 묶음이에요."),1,true));
            body.Children.Add(choice(S("Tidy up automatically","알아서 정리"),
                S("One question, then all groups at once: the copies in Downloads, Desktop and temporary folders go (one with a cover always stays).",
                  "한 번만 확인하고 모든 묶음을 한꺼번에 정리해요. 다운로드·바탕 화면·임시 폴더의 사본을 지우는 쪽으로 해요(표지 있는 파일은 늘 남겨요)."),2,false));
            var remember=new CheckBox {Content=S("Don't ask next time (right-click [Tidy up…] to change)","다음부터 묻지 않기 ([정리하기…]를 오른쪽 클릭하면 바꿀 수 있어요)"),Margin=new Thickness(2,4,0,12)};
            remember.SetResourceReference(Control.ForegroundProperty,"Text");
            body.Children.Add(remember);
            var cancel=UI.Btn(S("Cancel  Esc","취소  Esc"),()=>{result=0;ask.Close();});
            cancel.IsCancel=true;cancel.HorizontalAlignment=HorizontalAlignment.Right;cancel.Padding=new Thickness(12,2,12,2);
            body.Children.Add(cancel);
            root.Children.Add(body);ask.Content=root;
            ask.ShowDialog();
            if(result!=0 && remember.IsChecked==true){SimilarSearch.TidyWay=result;SimilarSearch.SaveSettings();}
            return result;
        }
        async void KeepAllSuggested() {
            if(SimilarSearch.ShowNear || !SimilarReady())return;
            // What is tidied is fixed now (copies of the lists), so nothing changing meanwhile can disturb it.
            var groups=SimilarSearch.Exact.Where(g=>g.Keep!=null && g.Files.Count>1)
                .Select(g=>Tuple.Create(g,g.Keep,g.Files.Where(f=>f!=g.Keep).ToList())).ToList();
            if(groups.Count==0)return;
            int copies=groups.Sum(g=>g.Item3.Count);
            long bytes=groups.Sum(g=>g.Item1.Saving);
            int covers;
            Begin(); // counting reads the records: nothing else starts meanwhile
            try {covers=await Task.Run(()=>groups.Sum(g=>g.Item3.Sum(f=>SimilarSearch.CoverCount(store,f))));}
            finally {End();}
            if(lifetime.IsCancellationRequested)return;
            string question=L.PF("Tidy up all {0:N0} groups of exactly the same files?\n\nEach group keeps the file marked “Suggested to keep”, and {1:N0} copies ({2}) go to the Recycle Bin. You can get them back from the Recycle Bin.",
                "똑같은 파일 묶음 {0:N0}개를 모두 정리할까요?\n\n묶음마다 ‘남기기 추천’ 파일만 남기고, 사본 {1:N0}개({2})를 휴지통으로 보내요. 휴지통에서 다시 되살릴 수 있어요.",
                groups.Count,copies,Bytes(bytes));
            if(covers>0)question+="\n"+L.PF("{0:N0} covers of the copies move to the files that stay.","사본에 있던 표지 {0:N0}장은 남는 파일로 옮겨요.",covers);
            question+="\n\n"+S("Which file stays: one with a cover, then one outside Downloads, Desktop and temporary folders, then the oldest (the files are byte for byte the same, so only the folder and name differ).",
                "남는 파일: 표지가 있는 파일 → 다운로드·바탕 화면·임시 폴더가 아닌 곳의 파일 → 수정한 날짜가 가장 오래된 파일이에요(내용은 바이트까지 같아서 폴더·이름만 달라요).");
            // Where the copies go from and where the kept files are, by folder, so a folder that matters to the person stands out.
            Func<IEnumerable<SimilarFile>,string> byFolder=files=>{
                var counted=files.GroupBy(f=>f.Folder,StringComparer.OrdinalIgnoreCase).Select(g=>Tuple.Create(g.Key,g.Count())).OrderByDescending(t=>t.Item2).ToList();
                string text=String.Join("\n",counted.Take(5).Select(t=>"  · "+L.PF("{0} — {1:N0}","{0} — {1:N0}개",t.Item1,t.Item2)));
                if(counted.Count>5)text+="\n  · "+L.PF("{0:N0} more folders","그 밖의 폴더 {0:N0}개",counted.Count-5);
                return text;
            };
            question+="\n\n"+S("Copies go to the Recycle Bin from:","휴지통으로 가는 사본이 있는 폴더:")+"\n"+byFolder(groups.SelectMany(g=>g.Item3));
            question+="\n\n"+S("Files stay in:","남는 파일이 있는 폴더:")+"\n"+byFolder(groups.Select(g=>g.Item2));
            question+="\n\n"+S("To keep another file in a group, tidy up that group first with right-click [Keep only this one].",
                "다른 파일을 남기고 싶은 묶음은 먼저 그 파일을 오른쪽 클릭해서 [이 파일만 남기고 사본 정리]로 정리해 주세요.");
            if(MessageBox.Show(this,question,"LoveMyShelf",MessageBoxButton.OKCancel,MessageBoxImage.Question)!=MessageBoxResult.OK)return;
            if(!SimilarReady() || lifetime.IsCancellationRequested)return;
            // Another window may have tidied some of them while the question was open: those are left out.
            groups=groups.Where(g=>SimilarSearch.Exact.Contains(g.Item1) && File.Exists(g.Item2.Path)).ToList();
            var result=new Tidied();
            int tidied=0,reached=0;
            Begin();
            try {
                foreach(var group in groups) {
                    if(SimilarSearch.StopWork || lifetime.IsCancellationRequested)break;
                    reached++;
                    SimilarSearch.Phase=L.PF("Tidying up… {0:N0} of {1:N0} groups","정리하는 중… 묶음 {1:N0}개 중 {0:N0}개",reached,groups.Count);
                    SimilarSearch.Changed();
                    int before=result.Gone.Count;
                    await Tidy(group.Item1,group.Item2,group.Item3,result);
                    if(result.Gone.Count>before)tidied++;
                }
            } finally {End();}
            if(lifetime.IsCancellationRequested)return;
            int left=groups.Count-reached;
            SimilarSearch.Phase=L.PF("Tidied up {0:N0} groups: {1:N0} copies went to the Recycle Bin.","묶음 {0:N0}개를 정리했어요. 사본 {1:N0}개를 휴지통으로 보냈어요.",tidied,result.Gone.Count)
                +(left>0?" "+L.PF("{0:N0} groups were left as they are (stopped).","묶음 {0:N0}개는 멈춰서 그대로예요.",left):"")
                +(result.Skipped>0?" "+L.PF("{0:N0} copies were left alone.","사본 {0:N0}개는 그대로 두었어요.",result.Skipped):"");
            SimilarDone(result.Gone,result.Skipped,result.LastError,result.Moved,result.Kept);
        }
        // Only one tidying or recycling at a time in the whole program; every window's bar shows it.
        static void Begin() {SimilarSearch.Working=true;SimilarSearch.StopWork=false;SimilarSearch.Changed();}
        static void End() {SimilarSearch.Working=false;SimilarSearch.StopWork=false;SimilarSearch.Changed();}
        sealed class Tidied {
            internal int Skipped,Moved;internal string LastError;
            internal readonly List<SimilarFile> Gone=new List<SimilarFile>(),Kept=new List<SimilarFile>();
        }
        // One group: the keeper stays; for each copy, covers and tags move to the keeper, then the copy goes to the Recycle Bin.
        async Task Tidy(SimilarGroup group,SimilarFile keeper,List<SimilarFile> copies,Tidied result) {
            var handle=new System.Windows.Interop.WindowInteropHelper(this).Handle;
            string keeperAlbum=store.AlbumFolderOf(keeper.Folder);
            int movedHere=0;bool any=false;
            foreach(var copy in copies) {
                if(lifetime.IsCancellationRequested || SimilarSearch.StopWork)break;
                SimilarSearch.Hold hold=null;
                // The copy may go only while this very file stays (and is byte for byte the same).
                var only=new List<SimilarGroup>{new SimilarGroup {Exact=true,Files=new List<SimilarFile>{copy,keeper},Keep=keeper}};
                bool own=false;
                try {
                    hold=await Task.Run(()=>SimilarSearch.Lock(copy,only,new List<SimilarFile>{copy}));
                    // Covers that can't be shown to be this copy's own (an earlier file had its name) are not moved: the copy stays.
                    own=hold!=null && await Task.Run(()=>SimilarSearch.CoversBelongTo(store,copy));
                } catch(Exception ex) {result.LastError=ex.Message;}
                if(hold==null || !own || lifetime.IsCancellationRequested){if(hold!=null)hold.Dispose();if(!lifetime.IsCancellationRequested)result.Skipped++;continue;}
                using(hold) {
                    if(CoverWork.HoldsPath(copy.Path) || CoverWork.HoldsPath(keeper.Path) || !hold.StillAt(copy.Path)){result.Skipped++;continue;}
                    List<SimilarSearch.Moved> records=null;
                    string problem=null;
                    // The records move and the new place is remembered (it reads the file) off the UI thread.
                    try {
                        problem=await Task.Run(()=>{
                            records=SimilarSearch.MoveCovers(store,copy,keeper);
                            try {SimilarSearch.Settle(records,keeper);return null;}
                            catch(Exception ex) {SimilarSearch.PutBack(records);SimilarSearch.Unsettle(records,copy);return ex.Message;}
                        });
                    } catch(Exception ex) {problem=ex.Message;}
                    if(problem!=null){result.Skipped++;result.LastError=problem;continue;}
                    string copyAlbum=store.AlbumFolderOf(copy.Folder);
                    var copyTags=Tags.Of(copyAlbum,copy.Name);
                    try {RecycleBin.MoveFile(copy.Path,handle);}
                    catch(Exception ex) {SimilarSearch.PutBack(records);SimilarSearch.Unsettle(records,copy);result.Skipped++;result.LastError=ex.Message;continue;}
                    try {
                        if(copyTags.Count>0)Tags.Change(new[]{Tuple.Create(keeperAlbum,keeper.Name)},copyTags,null);
                        Tags.Forget(copyAlbum,new[]{copy.Name});
                    } catch(Exception quiet) {ErrorLog.Quiet(quiet);}
                    movedHere+=records.Count;result.Gone.Add(copy);any=true;
                }
            }
            if(movedHere>0)keeper.HasCover=true;
            result.Moved+=movedHere;
            if(any)result.Kept.Add(keeper);
        }
    }
}
