using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace LoveMyShelf {
    // The bar above the tiles of the Unfinished cover collection: where it looks (the folders added to LoveMyShelf, or
    // the whole computer), what the reading is doing, [Stop] and [Check again]. The first visit shows one hint line.
    public partial class GalleryWindow {
        DockPanel uncoveredBar;
        Button uncoveredRange,uncoveredStop,uncoveredAgain;
        TextBlock uncoveredStatus;
        FrameworkElement uncoveredHint;
        DispatcherTimer uncoveredTimer;
        bool uncoveredReadAgain;

        static string U(string english,string korean) {return L.P(english,korean);}
        int AddedFolderCount {get {return store.VisibleAlbums().Count;}}
        internal UncoveredRange UncoveredRangeNow {get {return UncoveredOptions.Current.RangeFor(AddedFolderCount);}}

        void SetupUncovered(DockPanel area) {
            uncoveredBar=new DockPanel {Visibility=Visibility.Collapsed,Margin=new Thickness(10,6,10,4)};
            DockPanel.SetDock(uncoveredBar,Dock.Top);
            var lines=new StackPanel();uncoveredBar.Children.Add(lines);
            // Shown once: the choice is made without a question window, and this line says it can be changed.
            var hint=new WrapPanel {Margin=new Thickness(0,0,0,6)};
            var hintText=UI.Text("",13,true);hintText.TextWrapping=TextWrapping.Wrap;hintText.MaxWidth=640;hintText.Margin=new Thickness(0,0,10,4);
            hintText.VerticalAlignment=VerticalAlignment.Center;
            var toComputer=UI.Btn("",()=>{UncoveredOptions.Change(o=>{o.Range=UncoveredRange.Computer;o.HintDone=true;});},true);
            toComputer.Margin=new Thickness(0,0,6,4);toComputer.Padding=new Thickness(12,0,12,0);
            var keep=UI.Btn("",()=>{UncoveredOptions.Change(o=>{o.Range=UncoveredRange.Added;o.HintDone=true;});});
            keep.Margin=new Thickness(0,0,6,4);
            hint.Children.Add(hintText);hint.Children.Add(toComputer);hint.Children.Add(keep);
            hint.Tag=new object[]{hintText,toComputer,keep};
            uncoveredHint=hint;lines.Children.Add(hint);
            var row=new WrapPanel();
            uncoveredRange=UI.Btn("",OpenUncoveredMenu);uncoveredRange.Margin=new Thickness(0,0,8,4);
            uncoveredStop=UI.Btn("",()=>UncoveredFolders.Stop());uncoveredStop.Margin=new Thickness(0,0,8,4);
            uncoveredAgain=UI.Btn("",()=>{uncoveredReadAgain=true;Refresh();});uncoveredAgain.Margin=new Thickness(0,0,8,4);
            uncoveredStatus=UI.Text("",12.5,true);uncoveredStatus.TextWrapping=TextWrapping.Wrap;uncoveredStatus.MaxWidth=700;
            uncoveredStatus.Margin=new Thickness(0,0,0,4);uncoveredStatus.VerticalAlignment=VerticalAlignment.Center;
            row.Children.Add(uncoveredRange);row.Children.Add(uncoveredStop);row.Children.Add(uncoveredAgain);row.Children.Add(uncoveredStatus);
            lines.Children.Add(row);
            area.Children.Add(uncoveredBar);
            uncoveredTimer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(300)};
            uncoveredTimer.Tick+=delegate {
                if(!ShowingUnfinished || !UncoveredFolders.Busy)uncoveredTimer.Stop();
                UpdateUncoveredBar();
                if(ShowingUnfinished && UncoveredFolders.Busy && empty.Visibility==Visibility.Visible && photos.Count==0)emptyHelp.Text=UncoveredProgressText();
            };
            Action changed=()=>Dispatcher.BeginInvoke(new Action(()=>{if(ShowingUnfinished){ShowUncoveredBar();Refresh();}}));
            UncoveredOptions.Changed+=changed;
            Closed+=delegate {UncoveredOptions.Changed-=changed;uncoveredTimer.Stop();};
        }
        void ShowUncoveredBar() {
            if(uncoveredBar==null)return;
            uncoveredBar.Visibility=ShowingUnfinished?Visibility.Visible:Visibility.Collapsed;
            if(ShowingUnfinished)UpdateUncoveredBar();
        }
        void UpdateUncoveredBar() {
            if(uncoveredBar==null || !ShowingUnfinished)return;
            var o=UncoveredOptions.Current;
            int added=AddedFolderCount;
            bool computer=o.RangeFor(added)==UncoveredRange.Computer,busy=computer && UncoveredFolders.Busy;
            uncoveredRange.Content=(computer?U("Whole computer","컴퓨터 전체"):U("Added folders only","추가한 폴더만"))+" ▾";
            uncoveredRange.ToolTip=U("Choose where the files without a cover are looked for.","표지가 없는 파일을 어디서 찾을지 골라요.");
            uncoveredStop.Content=U("Stop","멈추기");
            uncoveredStop.Visibility=busy?Visibility.Visible:Visibility.Collapsed;
            uncoveredAgain.Content=U("Check again","다시 확인");
            uncoveredAgain.Visibility=computer && !busy?Visibility.Visible:Visibility.Collapsed;
            uncoveredAgain.ToolTip=U("Looks through every drive again, so new folders show too.","모든 드라이브를 처음부터 다시 훑어서 새로 생긴 폴더도 찾아요.");
            if(busy)uncoveredStatus.Text=UncoveredProgressText();
            else if(computer) {
                string text=UncoveredFolders.Partial?U("Stopped: what was found until then is shown. [Check again] looks through everything.","멈췄어요. 그때까지 찾은 것만 보여요. [다시 확인]을 누르면 처음부터 끝까지 찾아요."):
                    UncoveredFolders.HasList?L.PF("Looked through the whole computer at {0:t}.","{0:t}에 컴퓨터 전체를 훑었어요.",UncoveredFolders.ReadAt):"";
                if(UncoveredFolders.HasList && UncoveredFolders.Unreadable>0)
                    text+=" "+L.PF("{0:N0} folders could not be opened and were skipped.","열 수 없는 폴더 {0:N0}개는 건너뛰었어요.",UncoveredFolders.Unreadable);
                uncoveredStatus.Text=text.Trim();
            } else uncoveredStatus.Text=L.PF("Looks in the {0:N0} folders you added and the folders inside them.","추가한 폴더 {0:N0}개와 그 안의 폴더에서 찾아요.",added);
            var parts=(object[])uncoveredHint.Tag;
            ((TextBlock)parts[0]).Text=U("Only the folders you added show now. Finished collects the files you made covers for; Unfinished looks for files without a cover in the range you choose.",
                "지금은 추가한 폴더만 보여요. 표지 O에는 표지를 만든 파일이 모이고, 표지 X는 고른 범위에서 표지가 없는 파일을 찾아요.");
            ((Button)parts[1]).Content=U("Switch to the whole computer","컴퓨터 전체로 바꾸기");
            ((Button)parts[2]).Content=U("Keep it","그대로 두기");
            uncoveredHint.Visibility=!o.HintDone && !o.Range.HasValue && added>0?Visibility.Visible:Visibility.Collapsed;
            if(busy && !uncoveredTimer.IsEnabled)uncoveredTimer.Start();
        }
        static string UncoveredProgressText() {
            var p=UncoveredFolders.Progress;
            if(p==null)return U("Looking through the whole computer…","컴퓨터 전체를 훑는 중이에요…");
            return L.PF("Looking through the whole computer: {0:N0} folders checked, {1:N0} with photos or videos.",
                "컴퓨터 전체를 훑는 중이에요. 폴더 {0:N0}개를 봤고, 사진·영상이 있는 폴더는 {1:N0}개예요.",p.Folders,p.PictureFolders);
        }
        void OpenUncoveredMenu() {
            var o=UncoveredOptions.Current;
            bool computer=UncoveredRangeNow==UncoveredRange.Computer;
            var menu=new ContextMenu {PlacementTarget=uncoveredRange,Placement=System.Windows.Controls.Primitives.PlacementMode.Bottom};
            var added=new MenuItem {Header=U("Added folders only","추가한 폴더만"),IsCheckable=true,IsChecked=!computer,
                ToolTip=U("The folders you added to LoveMyShelf and the folders inside them. Quick.","LoveMyShelf에 추가한 폴더와 그 안의 폴더예요. 빨라요.")};
            added.Click+=delegate {UncoveredOptions.Change(x=>{x.Range=UncoveredRange.Added;x.HintDone=true;});};
            var whole=new MenuItem {Header=U("Whole computer","컴퓨터 전체"),IsCheckable=true,IsChecked=computer,
                ToolTip=U("Every drive this Windows account can open. The first look can take a few minutes; it goes on in the background.","이 Windows 계정이 열 수 있는 모든 드라이브예요. 처음 훑을 때는 몇 분 걸릴 수 있고, 뒤에서 계속 진행돼요.")};
            whole.Click+=delegate {UncoveredOptions.Change(x=>{x.Range=UncoveredRange.Computer;x.HintDone=true;});};
            menu.Items.Add(added);menu.Items.Add(whole);
            menu.Items.Add(new Separator());
            // Folders left out: one list for every range, also changed from a folder's right-click menu.
            var leftOut=new MenuItem {Header=L.PF("Folders left out ({0:N0})","빼 둔 폴더 ({0:N0}개)",o.Excluded.Count)};
            foreach(string folder in o.Excluded.ToList()) {
                string f=folder;
                var back=new MenuItem {Header=L.PF("Put “{0}” back","‘{0}’ 다시 넣기",FolderLabel(f)).Replace("_","__"),ToolTip=f};
                back.Click+=delegate {UncoveredOptions.Change(x=>x.Excluded.RemoveAll(e=>String.Equals(e,f,StringComparison.OrdinalIgnoreCase)));};
                leftOut.Items.Add(back);
            }
            if(o.Excluded.Count>0)leftOut.Items.Add(new Separator());
            var addOut=new MenuItem {Header=U("Leave out a folder…","뺄 폴더 고르기…")};
            addOut.Click+=delegate {
                string path=UI.PickFolder(this,U("Choose a folder to leave out of the Unfinished cover collection.","표지 X 모음에서 뺄 폴더를 골라요."),o.Excluded.LastOrDefault(),false);
                if(path!=null)LeaveOutOfUncovered(path,true);
            };
            leftOut.Items.Add(addOut);
            menu.Items.Add(leftOut);
            Func<string,string,bool,Action<UncoveredOptions,bool>,MenuItem> check=(en,ko,on,set)=>{
                var item=new MenuItem {Header=U(en,ko),IsCheckable=true,IsChecked=on,IsEnabled=computer};
                item.Click+=delegate {bool value=item.IsChecked;UncoveredOptions.Change(x=>set(x,value));};
                return item;
            };
            menu.Items.Add(check("Include external drives","외장 드라이브도 찾기",o.Removable,(x,v)=>x.Removable=v));
            menu.Items.Add(check("Include network drives","네트워크 드라이브도 찾기",o.Network,(x,v)=>x.Network=v));
            menu.Items.Add(check("Include hidden folders","숨김 폴더도 찾기",o.Hidden,(x,v)=>x.Hidden=v));
            menu.Items.Add(check("Leave out Windows and program folders","Windows·프로그램 폴더 빼기",o.SkipSystem,(x,v)=>x.SkipSystem=v));
            menu.Items.Add(new Separator());
            var more=new MenuItem {Header=U("More settings…","자세한 설정…")};
            more.Click+=delegate {GuideWindow.Open(this,"uncovered");};
            menu.Items.Add(more);
            menu.IsOpen=true;
        }
        static string FolderLabel(string path) {string name=Path.GetFileName(path.TrimEnd('\\'));return String.IsNullOrEmpty(name)?path:name;}
        internal bool LeftOutOfUncovered(string path) {return path!=null && UncoveredOptions.Current.Excluded.Any(e=>String.Equals(e.TrimEnd('\\'),path.TrimEnd('\\'),StringComparison.OrdinalIgnoreCase));}
        // From a folder's right-click menu, or the list above: the folder and everything inside it stay out (or come back).
        internal void LeaveOutOfUncovered(string path,bool leaveOut) {
            if(String.IsNullOrWhiteSpace(path))return;
            string full;
            try {full=Path.GetFullPath(path);} catch(Exception) {return;}
            if(full.Length>3)full=full.TrimEnd('\\');
            UncoveredOptions.Change(x=>{
                x.Excluded.RemoveAll(e=>String.Equals(e.TrimEnd('\\'),full.TrimEnd('\\'),StringComparison.OrdinalIgnoreCase));
                if(leaveOut)x.Excluded.Add(full);
            });
        }
        // The whole-computer list for Refresh: the folder list (read in the background, kept while the app runs),
        // then the files without a cover in those folders.
        async Task<List<Photo>> UncoveredOnComputer(CancellationToken leave,PhotoSort sort,bool descending,Action<int> found,Action<int> setSkipped) {
            bool again=uncoveredReadAgain;uncoveredReadAgain=false;
            string coverFolder=null;
            try {if(!String.IsNullOrWhiteSpace(settings.CoverDirectory))coverFolder=Path.GetFullPath(settings.CoverDirectory).TrimEnd('\\');} catch(Exception quiet) {ErrorLog.Quiet(quiet);}
            var listing=UncoveredFolders.Start(store.VisibleAlbums().Select(a=>a.Source).ToList(),coverFolder,again);
            UpdateUncoveredBar();
            if(!listing.IsCompleted) {
                emptyHelp.Text=UncoveredProgressText();
                var left=new TaskCompletionSource<bool>();
                using(leave.Register(()=>left.TrySetResult(true)))await Task.WhenAny(listing,left.Task);
                leave.ThrowIfCancellationRequested(); // leaving only stops waiting: the reading goes on and is ready next time
            }
            var folders=await listing;
            UpdateUncoveredBar();
            long minBytes=UncoveredOptions.Current.MinSizeKb*1024L;
            return await Task.Run(()=>{
                int skipped;
                var list=store.UncoveredInFolders(folders,minBytes,out skipped,leave,found);
                setSkipped(skipped);
                return GalleryStore.SortPhotos(list,sort,descending);
            },leave);
        }
    }
}
