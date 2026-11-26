using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace LoveMyShelf {
    // "Send to phone": where (Google Drive, OneDrive or another folder), what (covers only, or covers and originals),
    // then the copying with a bar, then what happened. The buttons stay at the bottom; the page scrolls at 150% and 225%.
    internal sealed class PhoneWindow : Window {
        static string T(string en,string ko) {return L.P(en,ko);}
        static PhoneWindow open;
        internal static void Show(Window owner,string folderName,List<PhoneItem> items,string storeRoot) {
            if(open!=null && open.IsLoaded) {
                if(open.busy!=null){open.Activate();return;}
                open.Close();
            }
            open=new PhoneWindow(owner,folderName,items,storeRoot);
            open.Closed+=delegate {open=null;};
            open.ChoosePage();
            open.Show();
        }

        readonly StackPanel page=new StackPanel {Margin=new Thickness(28,20,28,20)};
        readonly ScrollViewer scroller=new ScrollViewer {VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
        readonly DockPanel bar=new DockPanel {LastChildFill=false,Margin=new Thickness(28,10,28,16)};
        readonly string folderName,storeRoot;
        readonly List<PhoneItem> items;
        CancellationTokenSource busy;
        string chosenRoot;bool originals;

        PhoneWindow(Window owner,string folderName,List<PhoneItem> items,string storeRoot) {
            this.folderName=folderName;this.items=items;this.storeRoot=storeRoot;
            UI.WindowSetup(this,T("See it on your phone","휴대폰에서 보기"));
            if(owner!=null && owner.IsVisible){Owner=owner;WindowStartupLocation=WindowStartupLocation.CenterOwner;}
            ShowInTaskbar=true;
            var area=SystemParameters.WorkArea;
            Width=Math.Min(620,area.Width);Height=Math.Min(700,area.Height);
            MinWidth=Math.Min(440,area.Width);MinHeight=Math.Min(400,area.Height);
            var root=new DockPanel();
            root.Children.Add(new WindowTitleBar(this));
            var bottom=new Border {BorderThickness=new Thickness(0,1,0,0),Child=bar};
            bottom.SetResourceReference(Border.BorderBrushProperty,"Line");
            DockPanel.SetDock(bottom,Dock.Bottom);root.Children.Add(bottom);
            scroller.Content=page;root.Children.Add(scroller);
            Content=root;
            Closing+=(s,e)=>{
                if(busy==null)return;
                var answer=MessageBox.Show(this,T("Stop sending? What was already sent stays in the folder.","보내기를 멈출까요? 이미 보낸 파일은 폴더에 그대로 있어요."),
                    Title,MessageBoxButton.YesNo,MessageBoxImage.Question);
                if(answer!=MessageBoxResult.Yes){e.Cancel=true;return;}
                var cts=busy;if(cts!=null)cts.Cancel();
            };
            PreviewKeyDown+=(s,e)=>{if(e.Key==Key.Escape && busy==null){Close();e.Handled=true;}};
            var saved=ReadChoice();
            chosenRoot=saved.Item1;originals=saved.Item2;
        }

        // ---------------- Building blocks (the same look as the backup window) ----------------
        void NewPage(string title) {
            page.Children.Clear();bar.Children.Clear();
            var heading=UI.Text(NoticeLook.KeepWords(title),21);heading.FontWeight=FontWeights.Bold;heading.TextWrapping=TextWrapping.Wrap;
            heading.Margin=new Thickness(0,0,0,10);page.Children.Add(heading);
            scroller.ScrollToTop();
        }
        TextBlock Para(string text,bool subtle=false,double size=14,double top=4) {
            var t=UI.Text(NoticeLook.KeepWords(text),size,subtle);t.TextWrapping=TextWrapping.Wrap;
            t.LineHeight=size+8;t.LineStackingStrategy=LineStackingStrategy.BlockLineHeight;
            t.Margin=new Thickness(0,top,0,0);page.Children.Add(t);return t;
        }
        void Sub(string text) {
            var t=UI.Text(NoticeLook.KeepWords(text),16);t.FontWeight=FontWeights.SemiBold;t.TextWrapping=TextWrapping.Wrap;
            t.Margin=new Thickness(0,20,0,6);page.Children.Add(t);
        }
        static TextBlock Line(string text,bool subtle,double size=13) {
            var t=UI.Text(NoticeLook.KeepWords(text),size,subtle);t.TextWrapping=TextWrapping.Wrap;return t;
        }
        void Note(string text,bool warning) {
            var box=new Border {CornerRadius=new CornerRadius(10),Padding=new Thickness(14,10,14,11),Margin=new Thickness(0,10,0,0),BorderThickness=new Thickness(1)};
            box.SetResourceReference(Border.BackgroundProperty,"Paper");
            box.BorderBrush=new SolidColorBrush(warning?NoticeLook.Amber:Color.FromArgb(0x60,NoticeLook.Blue.R,NoticeLook.Blue.G,NoticeLook.Blue.B));
            box.Child=Line(text,false,13);
            page.Children.Add(box);
        }
        Button AtLeft(string label,Action action) {var b=UI.Btn(label,action);b.MinWidth=96;b.MinHeight=34;b.Padding=new Thickness(14,0,14,0);DockPanel.SetDock(b,Dock.Left);bar.Children.Add(b);return b;}
        Button AtRight(string label,Action action,bool primary=false) {
            var b=UI.Btn(label,action,primary);b.MinWidth=110;b.MinHeight=34;b.Padding=new Thickness(16,0,16,0);b.Margin=new Thickness(8,0,0,0);
            DockPanel.SetDock(b,Dock.Right);bar.Children.Add(b);return b;
        }
        // Cards chosen like radio buttons: a title, a line under it, and an optional part shown while chosen.
        void Choices(string[] titles,string[] details,int selected,Action<int> chose,FrameworkElement[] extras=null) {
            var boxes=new List<Action<bool>>();
            for(int i=0;i<titles.Length;i++) {
                int index=i;
                var box=new Border {CornerRadius=new CornerRadius(10),BorderThickness=new Thickness(1.5),Padding=new Thickness(14,10,14,11),
                    Margin=new Thickness(0,0,0,8),Cursor=Cursors.Hand,Focusable=true};
                box.SetResourceReference(Border.BackgroundProperty,"Panel");
                var stack=new StackPanel();var top=new DockPanel();
                var dot=new Border {Width=16,Height=16,CornerRadius=new CornerRadius(8),BorderThickness=new Thickness(2),Margin=new Thickness(0,2,10,0),VerticalAlignment=VerticalAlignment.Top};
                DockPanel.SetDock(dot,Dock.Left);top.Children.Add(dot);
                var title=Line(titles[i],false,14.5);title.FontWeight=FontWeights.SemiBold;top.Children.Add(title);
                stack.Children.Add(top);
                if(details!=null && details[i]!=null){var d=Line(details[i],true,13);d.Margin=new Thickness(26,3,0,0);stack.Children.Add(d);}
                FrameworkElement extra=extras!=null && i<extras.Length?extras[i]:null;
                if(extra!=null){extra.Margin=new Thickness(26,8,0,0);stack.Children.Add(extra);}
                box.Child=stack;
                Action<bool> look=on=>{
                    box.BorderBrush=on?(Brush)FindResource("Accent"):(Brush)FindResource("Line");
                    dot.BorderBrush=on?(Brush)FindResource("Accent"):(Brush)FindResource("Subtle");
                    dot.Background=on?(Brush)FindResource("Accent"):Brushes.Transparent;
                    if(extra!=null)extra.Visibility=on?Visibility.Visible:Visibility.Collapsed;
                };
                boxes.Add(look);
                Action pick=()=>{for(int k=0;k<boxes.Count;k++)boxes[k](k==index);chose(index);};
                box.MouseLeftButtonUp+=(s,e)=>{
                    for(var node=e.OriginalSource as DependencyObject;node!=null && node!=box;node=VisualTreeHelper.GetParent(node))
                        if(node is Button)return;
                    pick();
                };
                box.KeyDown+=(s,e)=>{if(e.Key==Key.Space || e.Key==Key.Enter){pick();e.Handled=true;}};
                page.Children.Add(box);
            }
            for(int k=0;k<boxes.Count;k++)boxes[k](k==selected);
        }

        // ---------------- Choosing ----------------
        string Target(string root) {return Path.Combine(root,PhoneExport.Clean(folderName,80));}
        void ChoosePage() {
            NewPage(T("See it on your phone","휴대폰에서 보기"));
            int covers=items.Count(i=>i.Cover!=null && File.Exists(i.Cover));
            if(covers==0) {
                Para(T("There are no covers on this screen yet. Make covers first, then send them.","이 화면에는 아직 표지가 없어요. 표지를 만든 뒤에 보내 주세요."));
                AtRight(T("Close","닫기"),Close,true);
                return;
            }
            Para(L.PF("The {0} covers of ‘{1}’ are copied into a Google Drive or OneDrive folder, in the order shown now. Open that folder in the Google Drive or OneDrive app on your phone and sort it by name: you see them in the same order.",
                "‘{1}’의 표지 {0}개를 지금 보이는 순서 그대로 구글 드라이브나 OneDrive 폴더에 복사해요. 휴대폰의 구글 드라이브·OneDrive 앱에서 그 폴더를 열고 이름순으로 보면 같은 순서로 보여요.",covers,folderName));

            Sub(T("Where should they go?","어디로 보낼까요?"));
            var places=PhoneExport.Places();
            if(chosenRoot==null && places.Count>0)chosenRoot=places[0].Root;
            var titles=new List<string>();var details=new List<string>();var extras=new List<FrameworkElement>();
            foreach(var p in places){titles.Add(p.Name);details.Add(Target(p.Root));extras.Add(null);}
            bool otherChosen=chosenRoot!=null && !places.Any(p=>String.Equals(p.Root,chosenRoot,StringComparison.OrdinalIgnoreCase));
            string other=otherChosen?chosenRoot:null;
            var otherPath=Line(otherChosen?Target(chosenRoot):T("No folder chosen yet","아직 고른 폴더가 없어요"),true,13);
            var otherPanel=new StackPanel();otherPanel.Children.Add(otherPath);
            var pickOther=UI.Btn(T("Choose a folder…","폴더 고르기…"),()=>{
                string chosen;
                if(FolderDialog.TryPick(new WindowInteropHelper(this).Handle,T("Where should they go?","어디로 보낼까요?"),chosenRoot,out chosen) && chosen!=null) {
                    other=chosenRoot=chosen;otherPath.Text=Target(chosen);
                }
            });
            pickOther.HorizontalAlignment=HorizontalAlignment.Left;pickOther.Margin=new Thickness(0,6,0,0);pickOther.Padding=new Thickness(12,0,12,0);
            otherPanel.Children.Add(pickOther);
            titles.Add(T("Another folder","다른 폴더"));
            details.Add(T("For example a folder that Dropbox or another cloud app keeps in step.","예를 들어 드롭박스 같은 다른 클라우드 앱의 폴더요."));
            extras.Add(otherPanel);
            int selected=otherChosen || chosenRoot==null?places.Count:places.FindIndex(p=>String.Equals(p.Root,chosenRoot,StringComparison.OrdinalIgnoreCase));
            Choices(titles.ToArray(),details.ToArray(),selected,i=>{chosenRoot=i<places.Count?places[i].Root:other;},extras.ToArray());
            if(places.Count==0)
                Note(T("No Google Drive or OneDrive folder was found on this computer. Install the Google Drive or OneDrive app and sign in, and it shows up here.",
                    "이 컴퓨터에서 구글 드라이브나 OneDrive 폴더를 찾지 못했어요. 구글 드라이브나 OneDrive 앱을 설치하고 로그인하면 여기에 나와요."),false);

            Sub(T("What should be sent?","무엇을 보낼까요?"));
            var plainPlan=PhoneExport.MakePlan(items,false,OriginalWord);
            var fullPlan=PhoneExport.MakePlan(items,true,OriginalWord);
            Choices(new[]{T("Covers only (recommended)","표지만(추천)"),T("Covers and originals","표지와 원본 함께")},
                new[]{L.PF("Light: about {0}. You see the covers on the phone, in order.","가벼워요. 약 {0}예요. 휴대폰에서 표지를 순서대로 볼 수 있어요.",DetailsWindow.Size(plainPlan.Bytes)),
                    L.PF("About {0}. Each original comes right after its cover, so you can open the photo or video itself on the phone. It can use up the free space of the cloud quickly.",
                        "약 {0}예요. 원본이 표지 바로 뒤에 들어가서 휴대폰에서 사진·영상 자체도 열어 볼 수 있어요. 클라우드 무료 용량을 금방 쓸 수 있어요.",DetailsWindow.Size(fullPlan.Bytes))},
                originals?1:0,i=>originals=i==1);

            Note(T("These are copies. Deleting them on the phone leaves the originals and covers on this computer as they are. Send again after you change the order or add covers: files already there are kept or only renamed, and only new ones are copied.",
                "복사본이라 휴대폰에서 지워도 이 컴퓨터의 원본과 표지는 그대로예요. 순서를 바꾸거나 표지를 더 만든 뒤 다시 보내면, 이미 올라간 파일은 그대로 두거나 이름만 바꾸고 새것만 복사해요."),false);
            Note(T("They go up to the cloud. Don't send photos that nobody else should see; keep those in a locked photo box (Guide & Options → Backup and restore).",
                "클라우드에 올라가요. 남에게 보이면 안 되는 사진은 보내지 말고 ‘잠긴 사진 상자’에 넣어 두세요(설명서 겸 옵션 → 백업·복원)."),true);

            AtLeft(T("Close","닫기"),Close);
            AtRight(T("Send","보내기"),()=>{
                if(chosenRoot==null){MessageBox.Show(this,T("Choose where they should go first.","먼저 보낼 곳을 골라 주세요."),Title);return;}
                SaveChoice();
                SendPage(PhoneExport.MakePlan(items,originals,OriginalWord),Target(chosenRoot));
            },true);
        }
        static string OriginalWord {get {return T("original","원본");}}

        // ---------------- Sending ----------------
        void SendPage(PhoneExport.Plan plan,string folder) {
            NewPage(T("Sending…","보내는 중…"));
            var line=Para("",false,14);
            var progressBar=new ProgressBar {Height=8,Margin=new Thickness(0,14,0,0),Minimum=0,Maximum=1};
            progressBar.SetResourceReference(Control.ForegroundProperty,"Accent");progressBar.SetResourceReference(Control.BackgroundProperty,"Panel");
            progressBar.BorderThickness=new Thickness(0);
            page.Children.Add(progressBar);
            var detail=Para("",true,13,10);
            Para(T("You can keep using Love My Shelf meanwhile.","그동안 Love My Shelf를 계속 써도 돼요."),true,13,14);
            var cts=new CancellationTokenSource();busy=cts;
            AtRight(T("Stop","멈추기"),()=>cts.Cancel());
            PhoneExport.Progress latest=null;
            var tick=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(200)};
            tick.Tick+=delegate {
                var p=latest;if(p==null)return;
                line.Text=NoticeLook.KeepWords(L.PF("{0} of {1}","{1}개 중 {0}개",Math.Min(p.Done+1,p.Total),p.Total));
                double part=p.TotalBytes>0?(double)p.Bytes/p.TotalBytes:(p.Total>0?(double)p.Done/p.Total:1);
                progressBar.Value=Math.Max(0,Math.Min(1,part));
                detail.Text=p.Name??"";
            };
            tick.Start();
            Task.Run(()=>PhoneExport.Send(plan,folder,storeRoot,p=>latest=p,cts.Token)).ContinueWith(t=>{
                tick.Stop();busy=null;
                if(!IsLoaded)return;
                if(t.IsFaulted){var ex=t.Exception.GetBaseException();if(ex is OperationCanceledException)StoppedPage(folder);else FailedPage(ex,folder);}
                else if(t.IsCanceled)StoppedPage(folder);
                else DonePage(t.Result);
            },TaskScheduler.FromCurrentSynchronizationContext());
        }
        void OpenFolder(string folder) {
            try {if(Directory.Exists(folder))Process.Start(new ProcessStartInfo(folder) {UseShellExecute=true});} catch(Exception ex) {UI.Error(this,ex);}
        }
        void DonePage(PhoneExport.Result r) {
            NewPage(T("Sent","다 보냈어요"));
            var parts=new List<string>();
            if(r.Copied>0)parts.Add(L.PF("{0} copied","새로 복사 {0}개",r.Copied));
            if(r.Kept>0)parts.Add(L.PF("{0} already there","그대로 {0}개",r.Kept));
            if(r.Renamed>0)parts.Add(L.PF("{0} renamed","이름만 바꿈 {0}개",r.Renamed));
            if(r.Removed>0)parts.Add(L.PF("{0} no longer needed, removed","필요 없어져서 지움 {0}개",r.Removed));
            Para(L.PF("{0} covers are in ‘{1}’.","표지 {0}개가 ‘{1}’에 들어 있어요.",r.Covers,r.Folder));
            if(parts.Count>0)Para(String.Join(" · ",parts),true,13,6);
            if(r.MissingOriginals>0)
                Note(L.PF("{0} originals weren't found, so only their covers were sent. The drive they are on may be unplugged.",
                    "원본을 찾지 못한 {0}개는 표지만 보냈어요. 원본이 있는 외장 하드가 빠져 있을 수 있어요.",r.MissingOriginals),true);
            Note(T("Whether it has finished uploading is shown by the Google Drive or OneDrive app on this computer. On the phone, open the ‘Love My Shelf 휴대폰’ folder in the same app and sort it by name.",
                "업로드가 끝났는지는 이 컴퓨터의 구글 드라이브나 OneDrive 앱에서 확인해 주세요. 휴대폰에서는 같은 앱에서 ‘Love My Shelf 휴대폰’ 폴더를 열고 이름순으로 보면 돼요."),false);
            AtLeft(T("Open the folder","폴더 열기"),()=>OpenFolder(r.Folder));
            AtRight(T("Close","닫기"),Close,true);
        }
        void StoppedPage(string folder) {
            NewPage(T("Stopped","멈췄어요"));
            Para(T("What was already sent stays in the folder. Send again and it carries on from there.","이미 보낸 파일은 폴더에 그대로 있어요. 다시 보내면 거기서부터 이어서 보내요."));
            AtLeft(T("Open the folder","폴더 열기"),()=>OpenFolder(folder));
            AtRight(T("Close","닫기"),Close,true);
        }
        void FailedPage(Exception ex,string folder) {
            NewPage(T("Couldn't finish sending","다 보내지 못했어요"));
            Para(ex.Message);
            Para(T("What was already sent stays in the folder. Send again and it carries on from there.","이미 보낸 파일은 폴더에 그대로 있어요. 다시 보내면 거기서부터 이어서 보내요."),true,13,10);
            ErrorLog.Quiet(ex);
            AtLeft(T("Open the folder","폴더 열기"),()=>OpenFolder(folder));
            AtRight(T("Close","닫기"),Close,true);
        }

        // ---------------- The last choice (where, and whether originals go too) ----------------
        string ChoiceFile {get {return Path.Combine(storeRoot,"phone","choice.txt");}}
        Tuple<string,bool> ReadChoice() {
            try {
                if(File.Exists(ChoiceFile)) {
                    var lines=File.ReadAllLines(ChoiceFile,Encoding.UTF8);
                    string root=lines.Length>0 && lines[0].Length>0?lines[0]:null;
                    return Tuple.Create(root,lines.Length>1 && lines[1]=="originals");
                }
            } catch(Exception ex) {ErrorLog.Quiet(ex);}
            return Tuple.Create((string)null,false);
        }
        void SaveChoice() {
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(ChoiceFile));
                File.WriteAllLines(ChoiceFile,new[]{chosenRoot??"",originals?"originals":"covers"},new UTF8Encoding(false));
            } catch(Exception ex) {ErrorLog.Quiet(ex);}
        }
    }
}
