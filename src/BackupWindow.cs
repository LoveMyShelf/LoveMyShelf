using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace LoveMyShelf {
    // The backup window: setting up, making a backup, and putting one back. One page at a time,
    // with the buttons always at the bottom (the page scrolls above them at 150% and 225%).
    internal sealed class BackupWindow : Window {
        static string T(string en,string ko) {return L.P(en,ko);}
        static BackupWindow open;
        internal static void Show(Window owner,string what) {
            if(open==null && Thread.VolatileRead(ref Backup.Working)>0) {
                MessageBox.Show(T("The last backup work is still being put away. Try again in a moment.","앞서 하던 백업 일을 아직 정리하고 있어요. 잠시 뒤에 다시 눌러 주세요."),T("Backup","백업"));
                return;
            }
            if(open!=null && open.IsLoaded && open.busy==null) {open.Activate();open.Start(what);return;}
            if(open!=null && open.IsLoaded){open.Activate();return;}
            open=new BackupWindow(owner);
            open.Closed+=delegate {open=null;};
            open.Start(what);
            open.Show();
        }

        readonly StackPanel page=new StackPanel {Margin=new Thickness(28,20,28,20)};
        readonly ScrollViewer scroller=new ScrollViewer {VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
        readonly DockPanel bar=new DockPanel {LastChildFill=false,Margin=new Thickness(28,10,28,16)};
        readonly Window owner;
        BackupSettings settings=BackupSettings.Load();
        CancellationTokenSource busy;

        BackupWindow(Window owner) {
            this.owner=owner;
            UI.WindowSetup(this,T("Backup","백업"));
            if(owner!=null && owner.IsVisible){Owner=owner;WindowStartupLocation=WindowStartupLocation.CenterOwner;}
            ShowInTaskbar=true;
            var area=SystemParameters.WorkArea;
            Width=Math.Min(640,area.Width);Height=Math.Min(700,area.Height);
            MinWidth=Math.Min(460,area.Width);MinHeight=Math.Min(420,area.Height);
            var root=new DockPanel();
            root.Children.Add(new WindowTitleBar(this));
            var bottom=new Border {BorderThickness=new Thickness(0,1,0,0),Child=bar};
            bottom.SetResourceReference(Border.BorderBrushProperty,"Line");
            DockPanel.SetDock(bottom,Dock.Bottom);root.Children.Add(bottom);
            scroller.Content=page;root.Children.Add(scroller);
            Content=root;
            Closing+=(s,e)=>{
                if(busy==null)return;
                var answer=MessageBox.Show(this,T("Stop what is being done now? Your last backup stays as it is.","지금 하던 일을 멈출까요? 지난 백업은 그대로 있어요."),
                    Title,MessageBoxButton.YesNo,MessageBoxImage.Question);
                if(answer!=MessageBoxResult.Yes){e.Cancel=true;return;}
                var cts=busy;if(cts!=null)cts.Cancel();
            };
            PreviewKeyDown+=(s,e)=>{if(e.Key==Key.Escape && busy==null){Close();e.Handled=true;}};
        }
        void Start(string what) {
            settings=BackupSettings.Load();
            if(what=="restore")RestoreStart();
            else if(what=="setup" || !settings.Configured)SetupPage(what!="setup");
            else PasswordPage();
        }

        // ---------------- Building blocks ----------------
        int pageNumber;
        void NewPage(string title) {
            pageNumber++;
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
        TextBlock Sub(string text) {
            var t=UI.Text(NoticeLook.KeepWords(text),16);t.FontWeight=FontWeights.SemiBold;t.TextWrapping=TextWrapping.Wrap;
            t.Margin=new Thickness(0,20,0,6);page.Children.Add(t);return t;
        }
        static TextBlock Line(string text,bool subtle,double size=13) {
            var t=UI.Text(NoticeLook.KeepWords(text),size,subtle);t.TextWrapping=TextWrapping.Wrap;return t;
        }
        Border Note(string text,bool warning) {
            var box=new Border {CornerRadius=new CornerRadius(10),Padding=new Thickness(14,10,14,11),Margin=new Thickness(0,10,0,0),BorderThickness=new Thickness(1)};
            box.SetResourceReference(Border.BackgroundProperty,"Paper");
            box.BorderBrush=new SolidColorBrush(warning?NoticeLook.Amber:Color.FromArgb(0x60,NoticeLook.Blue.R,NoticeLook.Blue.G,NoticeLook.Blue.B));
            box.Child=Line(text,false,13);
            page.Children.Add(box);return box;
        }
        Button AtLeft(string label,Action action) {var b=UI.Btn(label,action);b.MinWidth=96;b.MinHeight=34;b.Padding=new Thickness(14,0,14,0);DockPanel.SetDock(b,Dock.Left);bar.Children.Add(b);return b;}
        Button AtRight(string label,Action action,bool primary=false) {
            var b=UI.Btn(label,action,primary);b.MinWidth=110;b.MinHeight=34;b.Padding=new Thickness(16,0,16,0);b.Margin=new Thickness(8,0,0,0);
            DockPanel.SetDock(b,Dock.Right);bar.Children.Add(b);return b;
        }
        // A card that can be chosen (like a radio button): title, a line under it, and an optional part shown when chosen.
        sealed class Choice {internal Border Box;internal bool On;internal Action Changed;internal FrameworkElement Extra;}
        List<Choice> Choices(string[] titles,string[] details,int selected,Action<int> chose,FrameworkElement[] extras=null) {
            var list=new List<Choice>();
            for(int i=0;i<titles.Length;i++) {
                int index=i;
                var box=new Border {CornerRadius=new CornerRadius(10),BorderThickness=new Thickness(1.5),Padding=new Thickness(14,10,14,11),
                    Margin=new Thickness(0,0,0,8),Cursor=Cursors.Hand,Focusable=true};
                box.SetResourceReference(Border.BackgroundProperty,"Panel");
                var stack=new StackPanel();
                var top=new DockPanel();
                var dot=new Border {Width=16,Height=16,CornerRadius=new CornerRadius(8),BorderThickness=new Thickness(2),Margin=new Thickness(0,2,10,0),VerticalAlignment=VerticalAlignment.Top};
                DockPanel.SetDock(dot,Dock.Left);top.Children.Add(dot);
                var title=Line(titles[i],false,14.5);title.FontWeight=FontWeights.SemiBold;top.Children.Add(title);
                stack.Children.Add(top);
                if(details!=null && details[i]!=null){var d=Line(details[i],true,13);d.Margin=new Thickness(26,3,0,0);stack.Children.Add(d);}
                FrameworkElement extra=extras!=null && i<extras.Length?extras[i]:null;
                if(extra!=null){extra.Margin=new Thickness(26,8,0,0);stack.Children.Add(extra);}
                box.Child=stack;
                var choice=new Choice {Box=box,Extra=extra};
                choice.Changed=()=>{
                    box.BorderBrush=choice.On?(Brush)FindResource("Accent"):(Brush)FindResource("Line");
                    dot.BorderBrush=choice.On?(Brush)FindResource("Accent"):(Brush)FindResource("Subtle");
                    dot.Background=choice.On?(Brush)FindResource("Accent"):Brushes.Transparent;
                    if(extra!=null)extra.Visibility=choice.On?Visibility.Visible:Visibility.Collapsed;
                };
                Action pick=()=>{foreach(var c in list){c.On=c==choice;c.Changed();}chose(index);};
                box.MouseLeftButtonUp+=(s,e)=>{
                    // Clicks on buttons and boxes inside the shown part are theirs.
                    for(var node=e.OriginalSource as DependencyObject;node!=null && node!=box;node=VisualTreeHelper.GetParent(node))
                        if(node is Button || node is CheckBox || node is TextBox)return;
                    pick();
                };
                box.KeyDown+=(s,e)=>{if(e.Key==Key.Space || e.Key==Key.Enter){pick();e.Handled=true;}};
                list.Add(choice);page.Children.Add(box);
            }
            for(int i=0;i<list.Count;i++){list[i].On=i==selected;list[i].Changed();}
            return list;
        }
        CheckBox Check(string text,bool on) {
            var c=new CheckBox {IsChecked=on,Margin=new Thickness(0,4,0,4),VerticalContentAlignment=VerticalAlignment.Center};
            var label=Line(text,false,13.5);c.Content=label;
            c.SetResourceReference(Control.ForegroundProperty,"Text");
            return c;
        }
        PasswordBox Secret() {
            var box=new PasswordBox {FontSize=16,Padding=new Thickness(10,7,10,7),Margin=new Thickness(0,4,0,0),BorderThickness=new Thickness(1)};
            box.SetResourceReference(Control.BackgroundProperty,"Panel");box.SetResourceReference(Control.ForegroundProperty,"Text");
            box.SetResourceReference(Control.BorderBrushProperty,"StrongLine");box.SetResourceReference(PasswordBox.CaretBrushProperty,"Text");
            return box;
        }
        // Under password boxes: what the keys type right now. A password box takes no Korean (Windows turns the Korean
        // input off there), so letters always go in as English, even with the Korean/English key on Korean.
        TextBlock KeyLine(params PasswordBox[] boxes) {
            var line=Para("",false,13,6);
            var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(300)};
            int here=pageNumber;
            Action update=()=>{
                if(pageNumber!=here){timer.Stop();return;}
                bool caps=Keyboard.IsKeyToggled(Key.CapsLock);
                line.Text=NoticeLook.KeepWords("⌨  "+T("Goes in as English letters (this box takes no Korean)","영어로 들어가요(이 칸에는 한글이 안 들어가요)")+"  ·  "
                    +(caps?T("Caps Lock is ON: capital letters","Caps Lock 켜짐: 대문자로 들어가요"):T("Caps Lock off","Caps Lock 꺼짐")));
                if(caps)line.Foreground=new SolidColorBrush(NoticeLook.Amber);else line.SetResourceReference(TextBlock.ForegroundProperty,"Subtle");
            };
            timer.Tick+=delegate {update();};
            foreach(var box in boxes) {
                box.GotKeyboardFocus+=delegate {update();timer.Start();};
                box.LostKeyboardFocus+=delegate {update();};
                box.KeyUp+=delegate {update();};
            }
            Closed+=delegate {timer.Stop();};
            update();
            return line;
        }
        sealed class Bar {internal Border Track,Fill;internal TextBlock Step,Detail;}
        Bar Progress() {
            var p=new Bar();
            p.Step=Para("",false,15,6);p.Step.FontWeight=FontWeights.SemiBold;
            p.Detail=Para("",true,13,2);
            p.Track=new Border {Height=8,CornerRadius=new CornerRadius(4),Margin=new Thickness(0,14,0,0)};
            p.Track.SetResourceReference(Border.BackgroundProperty,"Raised");
            p.Fill=new Border {Height=8,CornerRadius=new CornerRadius(4),HorizontalAlignment=HorizontalAlignment.Left,Width=0};
            p.Fill.SetResourceReference(Border.BackgroundProperty,"Accent");
            p.Track.Child=p.Fill;page.Children.Add(p.Track);
            return p;
        }
        DispatcherTimer sweep;
        void Show(Bar p,Backup.Progress what) {
            if(what.Step!=null)p.Step.Text=NoticeLook.KeepWords(what.Step);
            p.Detail.Text=NoticeLook.KeepWords(what.Detail??"");
            double width=p.Track.ActualWidth;
            if(what.Fraction>=0) {
                if(sweep!=null){sweep.Stop();sweep=null;}
                p.Fill.HorizontalAlignment=HorizontalAlignment.Left;p.Fill.Margin=new Thickness(0);
                p.Fill.Width=Math.Max(0,Math.Min(1,what.Fraction))*width;
            } else if(sweep==null) {
                // Not known how long: a short piece moves back and forth.
                double x=0,step=6;
                sweep=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(30)};
                sweep.Tick+=delegate {
                    double w=p.Track.ActualWidth,piece=Math.Max(30,w/5);
                    x+=step;if(x<0 || x>w-piece){step=-step;x=Math.Max(0,Math.Min(w-piece,x));}
                    p.Fill.Width=piece;p.Fill.Margin=new Thickness(x,0,0,0);
                };
                sweep.Start();
            }
        }
        void StopSweep() {if(sweep!=null){sweep.Stop();sweep=null;}}

        [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);
        // Runs work away from the screen, keeps the computer awake meanwhile, and brings progress back to the screen.
        void Run<TResult>(Bar bar,Func<Action<Backup.Progress>,CancellationToken,TResult> work,Action<TResult> done,Action<Exception> failed) {
            busy=new CancellationTokenSource();
            var token=busy.Token;
            Interlocked.Increment(ref Backup.Working);
            Backup.Progress latest=null;
            var tick=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(150)};
            tick.Tick+=delegate {var l=latest;if(l!=null && bar!=null)Show(bar,l);};
            tick.Start();
            Task.Run(()=>{
                SetThreadExecutionState(0x80000001); // ES_CONTINUOUS | ES_SYSTEM_REQUIRED
                try {return work(p=>latest=p,token);}
                finally {SetThreadExecutionState(0x80000000);Interlocked.Decrement(ref Backup.Working);}
            }).ContinueWith(t=>{
                tick.Stop();StopSweep();busy=null;
                if(!IsLoaded)return;
                if(t.IsFaulted)failed(t.Exception.GetBaseException());
                else if(t.IsCanceled)failed(new OperationCanceledException());
                else done(t.Result);
            },TaskScheduler.FromCurrentSynchronizationContext());
        }

        // ---------------- Setting up ----------------
        string chosenPlace;
        void SetupPage(bool thenBackup) {
            NewPage(T("Where should backups go?","백업을 어디에 둘까요?"));
            Para(T("Your covers, tags and records are locked with your password on this computer first, and only the locked file goes there.",
                "표지·태그·기록을 이 컴퓨터에서 먼저 비밀번호로 잠그고, 잠긴 파일만 그곳에 넣어요."),true);
            var places=Backup.CloudPlaces();
            chosenPlace=settings.Destination??(places.Count>0?places[0].Path:null);
            var titles=new List<string>();var details=new List<string>();var extras=new List<FrameworkElement>();
            foreach(var p in places){titles.Add(p.Name);details.Add(p.Path);extras.Add(null);}
            bool otherChosen=chosenPlace!=null && !places.Any(p=>String.Equals(p.Path,chosenPlace,StringComparison.OrdinalIgnoreCase));
            string other=otherChosen?chosenPlace:null;
            var otherPath=Line(otherChosen?chosenPlace:T("No folder chosen yet","아직 고른 폴더가 없어요"),true,13);
            var otherPanel=new StackPanel();otherPanel.Children.Add(otherPath);
            var pickOther=UI.Btn(T("Choose a folder…","폴더 고르기…"),()=>{
                string chosen;
                if(FolderDialog.TryPick(new WindowInteropHelper(this).Handle,T("Where should backups go?","백업을 어디에 둘까요?"),chosenPlace,out chosen) && chosen!=null) {
                    other=chosenPlace=chosen;otherPath.Text=chosen;
                }
            });
            pickOther.HorizontalAlignment=HorizontalAlignment.Left;pickOther.Margin=new Thickness(0,6,0,0);pickOther.Padding=new Thickness(12,0,12,0);
            otherPanel.Children.Add(pickOther);
            titles.Add(T("Another folder (an external drive and so on)","다른 폴더(외장 하드 등)"));details.Add(null);extras.Add(otherPanel);
            int selected=otherChosen || chosenPlace==null?places.Count:places.FindIndex(p=>String.Equals(p.Path,chosenPlace,StringComparison.OrdinalIgnoreCase));
            Choices(titles.ToArray(),details.ToArray(),selected,i=>{
                if(i<places.Count)chosenPlace=places[i].Path;
                else chosenPlace=other;
            },extras.ToArray());
            if(places.Any(p=>p.Name=="Google Drive"))
                Note(T("If Google Drive is set to ‘mirror files’, what is in it also takes room on this computer.",
                    "구글 드라이브가 ‘파일 미러링’으로 되어 있으면, 그 안에 넣은 만큼 이 컴퓨터 공간도 써요."),false);

            Sub(T("How many backups should stay?","백업을 얼마나 남겨 둘까요?"));
            var keeps=new[]{"1","2","month","3months"};
            int keep=Math.Max(0,Array.IndexOf(keeps,settings.Keep));
            Choices(new[]{T("Only the newest one","가장 최근 1개만"),T("The newest two","최근 2개"),T("A month of them (recommended)","한 달 치(추천)"),T("Three months of them","3달 치")},
                new[]{T("The one before is removed after the new one is checked.","새 백업이 잘 열리는지 확인한 뒤 앞의 것을 지워요."),
                    T("One spare stays, in case the newest one has a problem.","새 백업에 문제가 있을 때를 위해 하나를 더 남겨요."),
                    T("A tag or cover removed by mistake can come back within a month.","실수로 지운 태그나 표지도 한 달 안에는 되찾을 수 있어요."),
                    T("Takes more room in the backup folder.","백업 폴더 공간을 더 써요.")},
                keep,i=>keep=i);
            Note(T("An older backup that still has covers the newer ones are missing (a drive was unplugged) is never removed. In a cloud folder an old backup is removed only a day later, after the new one has had time to upload.",
                "새 백업에 빠진 표지가 들어 있는 지난 백업(외장 하드가 빠져 있었을 때)은 지우지 않아요. 클라우드 폴더에서는 새 백업이 올라갈 시간을 주려고 하루 뒤에 지워요."),false);

            Sub(T("How often should I remind you?","얼마나 자주 알려 드릴까요?"));
            var days=new[]{7,14,30};
            int interval=Math.Max(0,Array.IndexOf(days,settings.IntervalDays));
            Choices(new[]{T("Every week (recommended)","1주일마다(추천)"),T("Every two weeks","2주일마다"),T("Every month","한 달마다")},null,interval,i=>interval=i);

            Sub(T("Lock the original photos and videos too?","원본 사진·영상도 잠가 둘까요?"));
            bool sevenZip=PhotoBox.SevenZip()!=null;
            var folderList=new StackPanel();
            var boxChecks=new List<Tuple<CheckBox,string>>();
            var picked=new HashSet<string>(settings.BoxFolders,StringComparer.OrdinalIgnoreCase);
            var store=new GalleryStore(Backup.StoreRoot);
            var sizes=new CancellationTokenSource();
            Closed+=delegate {sizes.Cancel();};
            foreach(var album in store.VisibleAlbums()) {
                if(album.Source==null)continue;
                var c=Check(album.Name,picked.Contains(album.Source));
                c.ToolTip=album.Source;
                folderList.Children.Add(c);boxChecks.Add(Tuple.Create(c,album.Source));
                var label=(TextBlock)c.Content;string source=album.Source;string name=album.Name;
                Task.Run(()=>PhotoBox.Size(source,sizes.Token)).ContinueWith(t=>{
                    if(t.Result>=0)label.Text=NoticeLook.KeepWords(name+"  ·  "+PhotoBox.Size(t.Result));
                },TaskScheduler.FromCurrentSynchronizationContext());
            }
            if(boxChecks.Count==0)folderList.Children.Add(Line(T("Add folders to LoveMyShelf first.","먼저 LoveMyShelf에 폴더를 넣어 주세요."),true));
            var boxNote=Line(T("Each chosen folder becomes a locked box with the same password, in a folder beside the backups. Only folders that changed are made again. A box takes as much room as the photos in it.",
                "고른 폴더마다 같은 비밀번호로 잠긴 상자를 백업 옆 폴더에 만들어요. 다음부터는 바뀐 폴더만 다시 만들어요. 상자는 안의 사진만큼 공간을 써요."),true,13);
            boxNote.Margin=new Thickness(0,0,0,6);
            folderList.Children.Insert(0,boxNote);
            int box=settings.BoxOn && sevenZip?1:0;
            Choices(new[]{T("Covers and records only (recommended)","표지와 기록만(추천)"),T("Also make locked photo boxes","잠긴 사진 상자도 만들기")},
                new[]{T("Small and quick. The originals stay where they are.","작고 빨라요. 원본은 지금 있는 곳에 그대로 있어요."),
                    sevenZip?T("A locked copy of the originals, for when this computer breaks.","컴퓨터가 고장 났을 때를 위한 원본의 잠긴 복사본이에요.")
                        :T("7-Zip is needed for this and wasn't found.","이 기능에는 7-Zip이 필요한데 찾지 못했어요.")},
                box,i=>box=i,new FrameworkElement[]{null,folderList});

            AtLeft(T("Cancel","취소"),Close);
            AtRight(T("Next","다음"),()=>{
                if(String.IsNullOrEmpty(chosenPlace)){MessageBox.Show(this,T("Choose where backups go.","백업을 둘 곳을 골라 주세요."),Title);return;}
                if(box==1 && !sevenZip){MessageBox.Show(this,T("7-Zip wasn't found, so photo boxes can't be made.","7-Zip을 찾지 못해서 사진 상자를 만들 수 없어요."),Title);return;}
                settings.Destination=chosenPlace;settings.Keep=keeps[keep];settings.IntervalDays=days[interval];
                settings.BoxOn=box==1;
                settings.BoxFolders=boxChecks.Where(c=>c.Item1.IsChecked==true).Select(c=>c.Item2).ToList();
                if(settings.BoxOn && settings.BoxFolders.Count==0){MessageBox.Show(this,T("Choose at least one folder for the photo boxes.","사진 상자에 넣을 폴더를 하나 이상 골라 주세요."),Title);return;}
                NotesPage(thenBackup);
            },true);
        }
        void NotesPage(bool thenBackup) {
            NewPage(T("Good to know before you start","시작하기 전에 꼭 알아 두세요"));
            Para("1.  "+T("Photos and covers that are already in a cloud folder can still be seen there as they are. Only the backup and the photo boxes are locked.",
                "이미 클라우드 폴더에 올라가 있는 사진과 표지는 지금처럼 보여요. 잠기는 건 백업 파일과 사진 상자예요."));
            Para("2.  "+T("Covers on a drive that isn't plugged in can't go in. After each backup you are told how many were left out.",
                "꽂혀 있지 않은 외장 하드에 있는 표지는 못 담아요. 백업이 끝나면 몇 장이 빠졌는지 알려 드려요."),false,14,8);
            Para("3.  "+T("The password is kept nowhere. If you forget it, nobody can open the backup, not even us.",
                "비밀번호는 어디에도 저장하지 않아요. 잊어버리면 아무도(저희도) 백업을 열 수 없어요."),false,14,8);
            var ok=Check(T("I've read this","확인했어요"),false);ok.Margin=new Thickness(0,16,0,0);page.Children.Add(ok);
            AtLeft(T("Back","뒤로"),()=>SetupPage(thenBackup));
            Button go=null,save=null;
            go=AtRight(T("Save and back up now","저장하고 지금 백업하기"),()=>{settings.Save();PasswordPage();},true);
            save=AtRight(T("Save settings only","설정만 저장"),()=>{settings.Save();Close();});
            go.IsEnabled=save.IsEnabled=false;
            ok.Checked+=delegate {go.IsEnabled=save.IsEnabled=true;};ok.Unchecked+=delegate {go.IsEnabled=save.IsEnabled=false;};
        }

        // ---------------- Making a backup ----------------
        static bool Easy(string password) {
            if(password.Distinct().Count()<=2)return true;
            string lower=password.ToLowerInvariant();
            foreach(string common in new[]{"12345678","123456789","1234567890","password","qwerty","asdfgh","abcd1234","11111111","00000000","iloveyou"})
                if(lower.Contains(common))return true;
            return false;
        }
        void PasswordPage() {
            NewPage(T("Your backup password","백업을 잠글 비밀번호를 넣어 주세요"));
            Para(T("Use 8 characters or more.","8자 이상으로 넣어 주세요."),true);
            if(settings.BoxOn)Para(T("The photo boxes are locked with the same password, so a restore asks for it only once.",
                "사진 상자도 같은 비밀번호로 잠가요. 그래서 복원할 때 한 번만 넣으면 돼요."),true);
            var first=Secret();page.Children.Add(first);
            var againLabel=Para(T("Type it once more","한 번 더 넣어 주세요"),false,14,14);
            var again=Secret();page.Children.Add(again);
            againLabel.Visibility=again.Visibility=Visibility.Collapsed;
            KeyLine(first,again);
            var message=Para("",false,13.5,10);message.Foreground=new SolidColorBrush(NoticeLook.Amber);message.Visibility=Visibility.Collapsed;
            Note(T("If you forget the password, nobody can open the backup. Write it down in a notebook or a memo on your phone.",
                "비밀번호를 잊으면 아무도 백업을 열 수 없어요. 수첩이나 휴대폰 메모에 적어 두세요."),false);
            bool easyTold=false,asking=false;
            AtLeft(T("Cancel","취소"),Close);
            Button settingsButton=AtLeft(T("Settings…","설정 바꾸기…"),()=>SetupPage(false));settingsButton.Margin=new Thickness(8,0,0,0);
            Button start=null;
            int here=pageNumber;
            Action tell=null;
            tell=()=>{message.Visibility=Visibility.Visible;};
            start=AtRight(T("Start the backup","백업 시작하기"),()=>{
                string password=first.Password;
                if(password.Length<8){message.Text=T("Use 8 characters or more.","8자 이상으로 넣어 주세요.");tell();return;}
                if(again.Visibility==Visibility.Visible) {
                    if(again.Password!=password){message.Text=T("The two don't match. Please type them again.","두 칸이 달라요. 다시 넣어 주세요.");tell();return;}
                    if(Easy(password) && !easyTold){easyTold=true;message.Text=T("This one is easy for others to guess. Press once more to use it anyway.","남이 맞추기 쉬운 비밀번호예요. 그래도 쓰려면 한 번 더 눌러 주세요.");tell();return;}
                }
                if(asking)return;
                asking=true;start.IsEnabled=false;settingsButton.IsEnabled=false;
                bool confirmed=again.Visibility==Visibility.Visible;
                var check=settings;
                Task.Run(()=>check.SamePassword(password)).ContinueWith(t=>{
                    asking=false;start.IsEnabled=true;settingsButton.IsEnabled=true;
                    if(!IsLoaded || pageNumber!=here)return;
                    if(t.Result==true || confirmed){MakeBackup(password,t.Result==true);return;}
                    againLabel.Visibility=again.Visibility=Visibility.Visible;
                    message.Text=t.Result==null
                        ?T("This is your first backup password. Type it once more so a typing mistake doesn't lock you out.","처음 정하는 비밀번호예요. 오타가 없게 한 번 더 넣어 주세요.")
                        :T("This isn't the password of your last backup. To use a new one, type it once more. Older backups still open only with the old one.","지난번 백업과 다른 비밀번호예요. 새로 바꾸려면 한 번 더 넣어 주세요. 지난 백업은 예전 비밀번호로 열어야 해요.")
                            +(settings.BoxOn && settings.Boxes.Count>0?" "+T("The photo boxes are then locked again with the new one, which takes a while.","사진 상자도 새 비밀번호로 다시 잠가서 시간이 걸려요."):"");
                    tell();again.Focus();
                },TaskScheduler.FromCurrentSynchronizationContext());
            },true);
            start.IsDefault=true;
            Dispatcher.BeginInvoke(new Action(()=>first.Focus()),DispatcherPriority.Loaded);
        }
        void MakeBackup(string password,bool samePassword) {
            NewPage(T("Making the backup","백업을 만들고 있어요"));
            var progress=Progress();
            Para(T("You can keep using LoveMyShelf meanwhile. Your last backup stays as it is until this one is checked.",
                "그동안 LoveMyShelf를 계속 써도 돼요. 이번 백업이 확인될 때까지 지난 백업은 그대로 있어요."),true,13,18);
            Button stop=null;
            stop=AtRight(T("Stop","멈추기"),()=>{if(busy!=null){busy.Cancel();stop.IsEnabled=false;}});
            var s=settings;
            Run(progress,(report,token)=>Backup.Make(s,password,samePassword,report,token),Done,e=>Failed(e,false));
        }
        void Done(Backup.Result r) {
            NewPage(T("The backup is done ✓","백업이 잘 끝났어요 ✓"));
            Para(L.PF("{0:N0} folders · {1:N0} covers · {2:N0} tags · {3}","폴더 {0:N0}개 · 표지 {1:N0}장 · 태그 {2:N0}개 · {3}",r.Folders,r.Covers,r.Tags,PhotoBox.Size(r.Bytes)));
            var where=Para(r.File,true,13,4);where.TextWrapping=TextWrapping.Wrap;
            if(r.Missing.Count>0) {
                Note(L.PF("{0:N0} covers couldn't go in: their drive isn't plugged in, or the file is gone.","표지 {0:N0}장은 못 담았어요. 외장 하드가 빠져 있거나 파일이 없어요.",r.Missing.Count),true);
                var list=UI.Btn(T("See which ones","빠진 표지 보기"),()=>MessageBox.Show(this,String.Join(Environment.NewLine,r.Missing.Take(40))+(r.Missing.Count>40?Environment.NewLine+"…":""),Title));
                list.HorizontalAlignment=HorizontalAlignment.Left;list.Margin=new Thickness(0,8,0,0);list.Padding=new Thickness(12,0,12,0);page.Children.Add(list);
            }
            if(r.BoxesMade+r.BoxesKept>0)
                Para(L.PF("Photo boxes: {0} made now ({1}), {2} unchanged.","사진 상자: 새로 만든 것 {0}개({1}), 그대로 둔 것 {2}개.",r.BoxesMade,PhotoBox.Size(r.BoxBytes),r.BoxesKept),false,14,12);
            foreach(string problem in r.BoxProblems)Note(problem,true);
            if(r.Cloud)Note(T("It is saved in the cloud folder on this computer. Whether it has finished uploading is shown by the Google Drive or OneDrive app.",
                "이 컴퓨터의 클라우드 폴더에 저장했어요. 업로드가 끝났는지는 구글 드라이브나 OneDrive 앱에서 확인해 주세요."),false);
            if(r.Removed>0)Para(L.PF("{0} older backups were removed.","지난 백업 {0}개를 정리했어요.",r.Removed),true,13,10);
            if(r.ProtectedOld>0)Para(L.PF("{0} older backups with covers this one is missing were kept.","이번에 빠진 표지가 들어 있는 지난 백업 {0}개는 지우지 않고 남겨 뒀어요.",r.ProtectedOld),true,13,6);
            AtLeft(T("Open the backup folder","백업 폴더 열기"),()=>{try {Process.Start("explorer.exe","/select,\""+r.File+"\"");} catch(Exception) {}});
            AtRight(T("Close","닫기"),Close,true).IsDefault=true;
        }
        void Failed(Exception e,bool restoring) {
            bool stopped=e is OperationCanceledException;
            NewPage(stopped?T("Stopped","멈췄어요"):restoring?T("The restore couldn't be finished","복원을 마치지 못했어요"):T("The backup couldn't be finished","백업을 마치지 못했어요"));
            if(!stopped)Para(Why(e),false);
            Para(restoring?T("What was already put back stays; doing the same restore again adds the rest without doubling anything.",
                    "이미 넣은 기록은 그대로 있어요. 같은 백업으로 다시 복원하면 나머지만 더해지고 두 번 들어가지 않아요.")
                :T("Your last backup is as it was.","지난 백업은 그대로 있어요."),true,13,10);
            if(!stopped)ErrorLog.Quiet(e);
            AtLeft(T("Close","닫기"),Close);
            AtRight(T("Try again","다시 해 보기"),()=>Start(restoring?"restore":"backup"),true);
        }
        static string Why(Exception e) {
            if(e is BackupFile.WrongPasswordException)return T("The password is wrong, or the start of the backup file is damaged.","비밀번호가 맞지 않거나 백업 파일 앞부분이 손상됐어요.");
            if(e is BackupFile.DamagedException)return T("The backup file couldn't be checked to the end. If it is in a cloud folder, make sure it has finished downloading, or choose another day's backup.",
                "백업 파일을 끝까지 확인하지 못했어요. 클라우드 폴더라면 다 내려받아졌는지 보거나 다른 날짜의 백업을 골라 주세요.");
            if(e is UnauthorizedAccessException)return T("Windows didn't allow writing there. Choose another folder.","그곳에는 Windows가 쓰기를 허락하지 않았어요. 다른 폴더를 골라 주세요.");
            if(e is IOException && (e.HResult&0xFFFF)==112)return T("The drive is full.","드라이브 공간이 가득 찼어요.");
            if(e is IOException && ((e.HResult&0xFFFF)==32 || (e.HResult&0xFFFF)==33))
                return T("Another program (often the cloud app) was using the file. Wait a moment and try again.","다른 프로그램(대개 클라우드 앱)이 파일을 쓰고 있었어요. 잠시 뒤 다시 해 보세요.");
            return e.Message;
        }

        // ---------------- Restoring ----------------
        Backup.Opened opened;
        string boxParent;
        int boxAnswer=-1;
        HashSet<string> boxesToOpen=new HashSet<string>();
        static string NewestBackup(string folder) {
            try {
                if(String.IsNullOrEmpty(folder) || !Directory.Exists(folder))return null;
                return new DirectoryInfo(folder).GetFiles("*"+BackupFile.Extension).OrderByDescending(f=>f.LastWriteTimeUtc).Select(f=>f.FullName).FirstOrDefault();
            } catch(Exception) {return null;}
        }
        void RestoreStart() {RestoreStart(null,null);}
        void RestoreStart(string keepFile,string problem) {
            opened=null;boxesToOpen.Clear();filter=null;boxAnswer=-1;
            NewPage(T("Restore from a backup","백업에서 복원하기"));
            Para(T("Covers, tags and records from the backup are added to what this computer has. Nothing here is overwritten.",
                "백업의 표지·태그·기록을 이 컴퓨터에 있는 것과 합쳐요. 지금 있는 것은 덮어쓰지 않아요."),true);
            string file=keepFile??NewestBackup(settings.Destination)??Backup.CloudPlaces().Select(p=>NewestBackup(p.Path)).FirstOrDefault(f=>f!=null);
            Sub(T("Backup file","백업 파일"));
            var name=Para(file??T("No backup file chosen yet","아직 고른 백업 파일이 없어요"),file==null,13.5,0);
            var choose=UI.Btn(T("Choose another backup file…","다른 백업 파일 고르기…"),()=>{
                var dialog=new Microsoft.Win32.OpenFileDialog {Filter="LoveMyShelf backup (*.lmsbackup)|*"+BackupFile.Extension,
                    InitialDirectory=file!=null?Path.GetDirectoryName(file):settings.Destination};
                if(dialog.ShowDialog(this)==true){file=dialog.FileName;name.Text=file;name.SetResourceReference(TextBlock.ForegroundProperty,"Text");}
            });
            choose.HorizontalAlignment=HorizontalAlignment.Left;choose.Margin=new Thickness(0,8,0,0);choose.Padding=new Thickness(12,0,12,0);
            page.Children.Add(choose);
            Sub(T("Password","비밀번호"));
            var secret=Secret();page.Children.Add(secret);
            KeyLine(secret);
            var message=Para(problem??"",false,13.5,10);message.Foreground=new SolidColorBrush(NoticeLook.Amber);
            message.Visibility=problem==null?Visibility.Collapsed:Visibility.Visible;
            AtLeft(T("Cancel","취소"),Close);
            var go=AtRight(T("Open the backup","백업 열기"),()=>{
                if(file==null || !File.Exists(file)){message.Text=T("Choose a backup file.","백업 파일을 골라 주세요.");message.Visibility=Visibility.Visible;return;}
                string password=secret.Password,path=file;
                NewPage(T("Opening the backup","백업을 열고 있어요"));
                var progress=Progress();
                if(Backup.OnlyInCloud(path))Para(T("This file is still only in the cloud, so it is downloaded first.","이 파일은 아직 클라우드에만 있어서 먼저 내려받아요."),true,13,12);
                Run(progress,(report,token)=>{
                    report(new Backup.Progress {Step=T("Checking the password and the whole file","비밀번호와 파일 전체를 확인하고 있어요"),Fraction=-1});
                    return Backup.Open(path,password,token,f=>report(new Backup.Progress {Step=T("Checking the password and the whole file","비밀번호와 파일 전체를 확인하고 있어요"),Fraction=f}));
                },o=>{opened=o;if(o.Boxes.Count>0)BoxPage();else FoldersPage();},e=>{
                    if(!(e is OperationCanceledException))ErrorLog.Quiet(e);
                    RestoreStart(path,e is OperationCanceledException?null:Why(e));
                });
            },true);
            go.IsDefault=true;
            Dispatcher.BeginInvoke(new Action(()=>secret.Focus()),DispatcherPriority.Loaded);
        }
        void BoxPage() {
            NewPage(T("Locked photo boxes were found. Open them too?","잠긴 사진 상자를 찾았어요. 같이 풀까요?"));
            var usable=opened.Boxes.Where(b=>b.Complete).ToList();
            var broken=opened.Boxes.Where(b=>!b.Complete).ToList();
            int found=opened.Folders.Count(f=>f.State==Backup.Match.Found);
            int answer=boxAnswer>=0?boxAnswer:usable.Count==0 || found*2>=opened.Folders.Count?1:0;
            var detail=new StackPanel();
            var space=Line("",false,13.5);space.Margin=new Thickness(0,8,0,0);
            var where=Line("",true,13);
            Button next=null;
            Action measure=()=>{
                var chosen=usable.Where(b=>boxesToOpen.Contains(b.Id)).ToList();
                if(boxParent==null){where.Text=T("No folder chosen yet","아직 고른 폴더가 없어요");space.Text="";if(next!=null)next.IsEnabled=answer==1;return;}
                where.Text=boxParent;
                long need,free;
                string problem=PhotoBox.CanOpen(chosen,boxParent,out need,out free);
                space.Text=NoticeLook.KeepWords(L.PF("Needed: {0} · Free on this drive: {1}","필요한 공간 {0} · 이 드라이브에 남은 공간 {1}",PhotoBox.Size(need),PhotoBox.Size(free))
                    +"  "+(problem??T("Enough room ✓","공간이 넉넉해요 ✓")));
                space.Foreground=problem!=null?new SolidColorBrush(NoticeLook.Amber):(Brush)FindResource("Text");
                if(next!=null)next.IsEnabled=answer==1 || (problem==null && chosen.Count>0);
            };
            if(boxAnswer<0 && boxesToOpen.Count==0)foreach(var b in usable){var plan=opened.Folders.FirstOrDefault(f=>f.Id==b.FolderId);if(plan==null || plan.State!=Backup.Match.Found)boxesToOpen.Add(b.Id);}
            foreach(var b in usable) {
                var c=Check(L.PF("{0}  ·  {1:N0} files · {2}","{0}  ·  파일 {1:N0}개 · {2}",b.Name,b.Files,PhotoBox.Size(b.Size)),boxesToOpen.Contains(b.Id));
                string id=b.Id;
                c.Checked+=delegate {boxesToOpen.Add(id);measure();};c.Unchecked+=delegate {boxesToOpen.Remove(id);measure();};
                detail.Children.Add(c);
            }
            var pick=UI.Btn(T("Choose where to open them…","풀 곳 고르기…"),()=>{
                string chosen;
                if(FolderDialog.TryPick(new WindowInteropHelper(this).Handle,T("Where should the photos go?","사진을 어디에 풀까요?"),boxParent,out chosen) && chosen!=null){boxParent=chosen;measure();}
            });
            pick.HorizontalAlignment=HorizontalAlignment.Left;pick.Margin=new Thickness(0,10,0,4);pick.Padding=new Thickness(12,0,12,0);
            detail.Children.Add(pick);detail.Children.Add(where);detail.Children.Add(space);
            var after=Line(T("Opened photos are a normal folder: they open without a password.","풀어 놓은 사진은 보통 폴더라서 비밀번호 없이 열려요."),true,12.5);after.Margin=new Thickness(0,6,0,0);
            detail.Children.Add(after);
            Choices(new[]{T("Yes, open the photo boxes and connect the originals right away","예, 사진 상자도 풀어서 원본까지 바로 연결할게요"),
                    T("No, only covers and records","아니요, 표지와 기록만 가져올게요")},
                new[]{T("When the original photos aren't on this computer.","이 컴퓨터에 원본 사진이 없을 때 골라요."),
                    T("When the originals are already on this computer or an external drive.","원본이 이미 이 컴퓨터나 외장 하드에 있을 때 골라요.")},
                answer,i=>{answer=i;boxAnswer=i;measure();},new FrameworkElement[]{usable.Count>0?detail:null,null});
            if(usable.Count==0)Para(T("No photo box can be opened right now, so only covers and records are brought back.","지금은 열 수 있는 사진 상자가 없어서 표지와 기록만 가져와요."),true,13,8);
            foreach(var b in broken)
                Note(L.PF("The box ‘{0}’ is missing parts ({1} of {2} found). If it is in a cloud folder, wait until it has finished syncing.",
                    "‘{0}’ 상자는 조각이 모자라요({2}개 중 {1}개). 클라우드 폴더라면 동기화가 끝날 때까지 기다려 주세요.",b.Name,b.PartFiles.Count,b.Parts),true);
            AtLeft(T("Back","뒤로"),RestoreStart);
            next=AtRight(T("Next","다음"),()=>{
                boxAnswer=answer;
                foreach(var plan in opened.Folders){plan.FromBox=false;plan.BoxId=null;plan.BoxRelative=null;}
                if(answer==0)foreach(var b in usable.Where(b=>boxesToOpen.Contains(b.Id))) {
                    // The box's own folder and the folders inside it (a folder inside a chosen one is in its box).
                    foreach(var plan in opened.Folders) {
                        string rel=null;
                        if(plan.Id==b.FolderId)rel="";
                        else if(plan.OldSource!=null && b.Source!=null && Backup.Inside(plan.OldSource,b.Source))
                            rel=Backup.Below(plan.OldSource,b.Source);
                        if(rel==null)continue;
                        plan.FromBox=true;plan.BoxId=b.Id;plan.BoxRelative=rel;plan.Later=false;
                    }
                }
                FoldersPage();
            },true);
            measure();
        }
        string filter;
        void FoldersPage() {
            NewPage(T("Finding the folders you used","예전에 보던 폴더를 찾아요"));
            Para(T("Folders found on this computer are connected. For one that wasn't found, show where it is now: folders near it are found too.",
                "이 컴퓨터에서 찾은 폴더는 바로 이어요. 못 찾은 폴더는 지금 있는 곳을 알려 주시면 그 근처 폴더도 같이 찾아요."),true);
            var plans=opened.Folders;
            Func<Backup.FolderPlan,bool> needs=p=>!p.FromBox && (p.State==Backup.Match.Missing || p.State==Backup.Match.Maybe);
            if(filter==null)filter=plans.Any(needs)?"need":"all";
            var tabs=new WrapPanel {Margin=new Thickness(0,12,0,8)};page.Children.Add(tabs);
            var news=Para("",false,13.5,0);news.Foreground=(Brush)FindResource("Accent");news.Visibility=Visibility.Collapsed;
            var list=new StackPanel();page.Children.Add(list);
            Action draw=null;
            Action<string,string,int> tab=(id,label,count)=>{
                var b=UI.Btn(label+" "+count,()=>{filter=id;draw();});
                b.Padding=new Thickness(12,0,12,0);b.Margin=new Thickness(0,0,6,6);b.MinHeight=30;
                if(filter==id)b.Style=(Style)Application.Current.Resources["Primary"];
                tabs.Children.Add(b);
            };
            draw=()=>{
                tabs.Children.Clear();list.Children.Clear();
                tab("need",T("Need finding","찾아야 해요"),plans.Count(needs));
                tab("found",T("Found","찾았어요"),plans.Count(p=>!needs(p)));
                tab("all",T("All","전체"),plans.Count);
                var shown=plans.Where(p=>filter=="all" || (filter=="need")==needs(p)).OrderByDescending(p=>p.Tab).ThenBy(p=>p.Album,StringComparer.CurrentCultureIgnoreCase).ToList();
                if(shown.Count==0)list.Children.Add(Line(filter=="need"?T("Every folder was found ✓","모든 폴더를 찾았어요 ✓"):T("Nothing here.","여기는 비어 있어요."),true,13.5));
                foreach(var plan in shown.Take(400))list.Children.Add(Card(plan,plans,news,draw));
                if(shown.Count>400)list.Children.Add(Line(L.PF("And {0} more","그리고 {0}개 더",shown.Count-400),true));
            };
            draw();
            AtLeft(T("Back","뒤로"),()=>{if(opened.Boxes.Count>0)BoxPage();else RestoreStart();});
            AtRight(T("Next","다음"),ConfirmPage,true);
        }
        Border Card(Backup.FolderPlan plan,List<Backup.FolderPlan> plans,TextBlock news,Action redraw) {
            var box=new Border {CornerRadius=new CornerRadius(10),BorderThickness=new Thickness(1),Padding=new Thickness(14,9,14,10),Margin=new Thickness(0,0,0,6)};
            box.SetResourceReference(Border.BackgroundProperty,"Panel");box.SetResourceReference(Border.BorderBrushProperty,"Line");
            var stack=new StackPanel();box.Child=stack;
            var top=new DockPanel();
            string badge=plan.FromBox?T("From the photo box","사진 상자에서 연결"):plan.State==Backup.Match.Found?T("Found ✓","찾았어요 ✓"):
                plan.Later?T("Later","나중에"):plan.State==Backup.Match.Maybe?T("Is this it?","이 폴더가 맞나요?"):T("Not found","못 찾았어요");
            var mark=UI.Text(badge,12.5);mark.FontWeight=FontWeights.SemiBold;mark.Margin=new Thickness(10,0,0,0);
            mark.Foreground=plan.State==Backup.Match.Found || plan.FromBox?new SolidColorBrush(NoticeLook.Green):
                plan.Later?(Brush)FindResource("Subtle"):new SolidColorBrush(NoticeLook.Amber);
            DockPanel.SetDock(mark,Dock.Right);top.Children.Add(mark);
            var title=Line(String.IsNullOrEmpty(plan.Album)?Path.GetFileName(plan.OldSource??""):plan.Album,false,14.5);title.FontWeight=FontWeights.SemiBold;
            top.Children.Add(title);stack.Children.Add(top);
            stack.Children.Add(Line(L.PF("Before: {0}","예전 위치: {0}",plan.OldSource),true,12.5));
            if(plan.NewSource!=null && plan.State!=Backup.Match.Missing && !plan.FromBox)stack.Children.Add(Line(L.PF("Now: {0}","지금 위치: {0}",plan.NewSource),false,12.5));
            stack.Children.Add(Line(L.PF("{0:N0} cover records · {1:N0} tags","표지 기록 {0:N0}개 · 태그 {1:N0}개",plan.Records,plan.Tags),true,12));
            var buttons=new WrapPanel {Margin=new Thickness(0,6,0,0)};
            Action<string,Action,bool> button=(label,act,primary)=>{
                var b=UI.Btn(label,act,primary);b.Padding=new Thickness(12,0,12,0);b.Margin=new Thickness(0,0,6,0);b.MinHeight=28;buttons.Children.Add(b);
            };
            Action pick=()=>{
                string chosen;
                string start=plan.NewSource??(Directory.Exists(Path.GetDirectoryName(plan.OldSource??"")??"")?Path.GetDirectoryName(plan.OldSource):null);
                if(!FolderDialog.TryPick(new WindowInteropHelper(this).Handle,L.PF("Where is ‘{0}’ now?","‘{0}’ 폴더는 지금 어디에 있나요?",plan.Album),start,out chosen) || chosen==null)return;
                int more=Backup.Pick(plans,plan,chosen);
                redraw();
                if(more>0){news.Text=L.PF("{0} more folders were found nearby!","근처에서 폴더 {0}개를 더 찾았어요!",more);news.Visibility=Visibility.Visible;}
            };
            if(plan.FromBox){}
            else if(plan.State==Backup.Match.Found)button(T("Change","바꾸기"),pick,false);
            else if(plan.State==Backup.Match.Maybe && !plan.Later) {
                button(T("Yes, this one","맞아요"),()=>{Backup.Pick(plans,plan,plan.NewSource);redraw();},true);
                button(T("Another folder…","다른 폴더…"),pick,false);
                button(T("Later","나중에 하기"),()=>{plan.Later=true;redraw();},false);
            } else {
                button(T("Find the folder…","폴더 찾기…"),pick,true);
                if(!plan.Later)button(T("Later","나중에 하기"),()=>{plan.Later=true;redraw();},false);
            }
            if(buttons.Children.Count>0)stack.Children.Add(buttons);
            return box;
        }
        void ConfirmPage() {
            var go=opened.Folders.Where(p=>(p.State==Backup.Match.Found || p.FromBox || p.State==Backup.Match.Maybe) && !p.Later).ToList();
            int later=opened.Folders.Count-go.Count;
            var boxes=boxAnswer==0?opened.Boxes.Where(b=>b.Complete && boxesToOpen.Contains(b.Id)).ToList():new List<PhotoBox.Found>();
            var gallery=Application.Current.Windows.OfType<GalleryWindow>().FirstOrDefault();
            string covers=Backup.CoverFolder(gallery!=null?gallery.Settings:null);
            NewPage(T("Restore these records?","이 기록을 가져올까요?"));
            Para(L.PF("{0:N0} folders · {1:N0} cover records · {2:N0} tags","폴더 {0:N0}개 · 표지 기록 {1:N0}개 · 태그 {2:N0}개",go.Count,go.Sum(p=>p.Records),go.Sum(p=>p.Tags)));
            if(later>0)Para(L.PF("{0} folders are left for later. Restoring the same backup again connects them then.","나중에 연결할 폴더 {0}개는 같은 백업으로 다시 복원하면 그때 이어져요.",later),true,13,8);
            if(boxes.Count>0)Para(L.PF("{0} photo boxes are opened into {1} ({2}).","사진 상자 {0}개를 {1}에 풀어요({2}).",boxes.Count,boxParent,PhotoBox.Size(boxes.Sum(b=>b.Size))),false,14,10);
            Note(T("Records already on this computer are kept as they are and the backup's are added beside them. Cover pictures that aren't here yet go into: ",
                "이 컴퓨터에 이미 있는 기록은 그대로 두고 백업의 기록을 옆에 합쳐요. 이 컴퓨터에 없는 표지 그림은 여기에 넣어요: ")+covers,false);
            AtLeft(T("Back","뒤로"),FoldersPage);
            AtRight(T("Restore","복원하기"),()=>{
                if(go.Count==0 && boxes.Count==0){MessageBox.Show(this,T("No folder is connected yet.","아직 연결된 폴더가 없어요."),Title);return;}
                RestoreNow(boxes,covers);
            },true).IsDefault=true;
        }
        void RestoreNow(List<PhotoBox.Found> boxes,string covers) {
            NewPage(T("Restoring","복원하고 있어요"));
            var progress=Progress();
            Button stop=null;
            stop=AtRight(T("Stop","멈추기"),()=>{if(busy!=null){busy.Cancel();stop.IsEnabled=false;}});
            var o=opened;string parent=boxParent;
            Run(progress,(report,token)=>{
                int n=0;
                foreach(var b in boxes) {
                    n++;
                    var box=b;string step=L.PF("Opening photo box {0} of {1}: ‘{2}’","사진 상자 {1}개 중 {0}번째를 풀고 있어요: ‘{2}’",n,boxes.Count,box.Name);
                    report(new Backup.Progress {Step=step,Fraction=0});
                    string folder=PhotoBox.Open(box,parent,o.Password,p=>report(new Backup.Progress {Step=step,Fraction=p/100.0}),token);
                    bool any=false;
                    foreach(var plan in o.Folders.Where(f=>f.FromBox && f.BoxId==box.Id)) {
                        plan.NewSource=String.IsNullOrEmpty(plan.BoxRelative)?folder:Path.Combine(folder,plan.BoxRelative);
                        any|=plan.Id==box.FolderId;
                    }
                    // A box without records of its own still becomes a folder in LoveMyShelf.
                    if(!any)try {new GalleryStore(Backup.StoreRoot).OpenFolder(folder);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}
                }
                return Backup.Apply(o,covers,report,token);
            },RestoreDone,e=>Failed(e,true));
        }
        void RestoreDone(Backup.Restored r) {
            NewPage(T("Restored ✓","복원했어요 ✓"));
            Para(L.PF("{0:N0} folders · {1:N0} cover records · {2:N0} cover pictures · {3:N0} tags added","폴더 {0:N0}개 · 표지 기록 {1:N0}개 · 표지 그림 {2:N0}장 · 태그 {3:N0}개를 넣었어요",
                r.Folders,r.Records,r.Covers,r.Tags));
            if(r.Skipped>0)Para(L.PF("{0} records were already here and kept as they are.","기록 {0}개는 이미 있어서 그대로 뒀어요.",r.Skipped),true,13,8);
            if(r.Later>0)Para(L.PF("{0} folders are left for later: restore the same backup again when you find them.","나중에 연결할 폴더가 {0}개 있어요. 찾으면 같은 백업으로 다시 복원해 주세요.",r.Later),true,13,8);
            Note(T("If a folder doesn't show in the folder tree yet, close LoveMyShelf and open it again.","새 폴더가 왼쪽 폴더 트리에 아직 안 보이면 LoveMyShelf를 껐다 다시 켜 주세요."),false);
            foreach(var w in Application.Current.Windows.OfType<GalleryWindow>())w.RefreshSoon();
            AtRight(T("Close","닫기"),Close,true).IsDefault=true;
        }

        // The buttons on the "Backup and restore" page of Guide & Options.
        internal static FrameworkElement GuidePanel(Window guide) {
            var s=BackupSettings.Load();
            var box=new Border {CornerRadius=new CornerRadius(14),BorderThickness=new Thickness(1),Padding=new Thickness(20,16,20,18),Margin=new Thickness(0,0,0,14)};
            box.SetResourceReference(Border.BackgroundProperty,"CardBack");box.SetResourceReference(Border.BorderBrushProperty,"CardLine");
            var stack=new StackPanel();box.Child=stack;
            string state=!s.Configured?T("Backups aren't set up yet.","아직 백업을 설정하지 않았어요.")
                :s.LastBackup==DateTime.MinValue?L.PF("Goes to {0}. No backup made yet.","저장할 곳: {0}. 아직 만든 백업이 없어요.",s.Destination)
                :L.PF("Last backup: {0} · Goes to {1}","마지막 백업: {0} · 저장할 곳: {1}",s.LastBackup.ToLocalTime().ToString("g",CultureInfo.CurrentCulture),s.Destination);
            var text=Line(state,false,14);stack.Children.Add(text);
            var row=new WrapPanel {Margin=new Thickness(0,12,0,0)};stack.Children.Add(row);
            Action<string,string,bool> add=(label,what,primary)=>{
                var b=UI.Btn(label,()=>BackupWindow.Show(Application.Current.Windows.OfType<GalleryWindow>().FirstOrDefault()??guide,what),primary);
                b.Padding=new Thickness(14,0,14,0);b.MinHeight=34;b.Margin=new Thickness(0,0,8,8);row.Children.Add(b);
            };
            add(T("Back up now","지금 백업하기"),"backup",true);
            add(T("Restore from a backup","백업에서 복원하기"),"restore",false);
            add(T("Backup settings","백업 설정 바꾸기"),"setup",false);
            return box;
        }

        // ---------------- The reminder ----------------
        static bool reminded;
        // At start, once: a big friendly window when a backup is due. "Tomorrow" is the only way to put it off.
        internal static void RemindIfDue(Window owner) {
            if(reminded)return;
            reminded=true;
            var later=new DispatcherTimer {Interval=TimeSpan.FromSeconds(6)};
            later.Tick+=delegate {
                later.Stop();
                if(open!=null)return;
                Task.Run(()=>{
                    Backup.ClearLeftovers();
                    var s=BackupSettings.Load();
                    if(s.Configured)Backup.CleanUpLater(s);
                    return s;
                }).ContinueWith(t=>{
                    var s=t.Result;
                    if(!s.Due || open!=null || owner==null || !owner.IsVisible)return;
                    // Someone who never set backups up is asked after a week of use, not on the first day.
                    if(!s.Configured) {
                        if(s.RemindAfter==DateTime.MinValue){s.RemindAfter=DateTime.UtcNow.AddDays(7);s.Save();return;}
                    }
                    new ReminderWindow(owner,s).Show();
                },TaskScheduler.FromCurrentSynchronizationContext());
            };
            later.Start();
        }
        sealed class ReminderWindow : Window {
            internal ReminderWindow(Window owner,BackupSettings s) {
                UI.WindowSetup(this,T("Time for a backup","백업할 때가 됐어요"));
                Owner=owner;WindowStartupLocation=WindowStartupLocation.CenterOwner;
                Width=Math.Min(520,SystemParameters.WorkArea.Width);SizeToContent=SizeToContent.Height;ResizeMode=ResizeMode.NoResize;
                var root=new DockPanel();root.Children.Add(new WindowTitleBar(this));
                var stack=new StackPanel {Margin=new Thickness(30,22,30,24)};root.Children.Add(stack);Content=root;
                var title=UI.Text(T("Time for a backup!","백업할 때가 됐어요!"),22);title.FontWeight=FontWeights.Bold;stack.Children.Add(title);
                string body=s.LastBackup==DateTime.MinValue
                    ?T("You haven't made a backup yet. Keep your covers, tags and records safe now: it takes about a minute.","아직 한 번도 백업하지 않았어요. 1분이면 돼요. 표지·태그·기록을 지금 안전하게 남겨 두세요.")
                    :L.PF("It has been {0} days since your last backup. Keep your covers and tags safe now, so nothing is lost if this computer breaks.",
                        "마지막으로 백업한 지 {0}일이 지났어요. 컴퓨터가 고장 나도 잃지 않게 지금 표지와 태그를 남겨 두세요.",(int)(DateTime.UtcNow-s.LastBackup).TotalDays);
                var text=UI.Text(NoticeLook.KeepWords(body),15);text.TextWrapping=TextWrapping.Wrap;text.Margin=new Thickness(0,10,0,0);
                text.LineHeight=24;text.LineStackingStrategy=LineStackingStrategy.BlockLineHeight;stack.Children.Add(text);
                if(s.Configured){var where=UI.Text(NoticeLook.KeepWords(L.PF("Goes to: {0}","저장할 곳: {0}",s.Destination)),13,true);where.TextWrapping=TextWrapping.Wrap;where.Margin=new Thickness(0,8,0,0);stack.Children.Add(where);}
                bool chose=false;
                var now=UI.Btn(T("Back up now","지금 백업하기"),()=>{chose=true;Close();BackupWindow.Show(owner,"backup");},true);
                now.FontSize=17;now.MinHeight=52;now.Margin=new Thickness(0,20,0,0);now.IsDefault=true;
                now.Background=new SolidColorBrush(NoticeLook.Blue);now.Foreground=Brushes.White;now.BorderThickness=new Thickness(0);
                stack.Children.Add(now);
                var tomorrow=new Button {Content=T("Remind me tomorrow","내일 다시 알려 주기"),HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(0,12,0,0),
                    Background=Brushes.Transparent,BorderThickness=new Thickness(0),Cursor=Cursors.Hand,FontSize=13,Padding=new Thickness(8,4,8,4)};
                tomorrow.SetResourceReference(Control.ForegroundProperty,"Subtle");
                tomorrow.Click+=delegate {Close();};
                stack.Children.Add(tomorrow);
                var interval=new Button {Content=T("Change how often","알림 간격 바꾸기"),HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,4,0,0),
                    Background=Brushes.Transparent,BorderThickness=new Thickness(0),Cursor=Cursors.Hand,FontSize=11.5,Padding=new Thickness(4,2,4,2)};
                interval.SetResourceReference(Control.ForegroundProperty,"Subtle");
                interval.Click+=delegate {chose=true;Close();BackupWindow.Show(owner,"setup");};
                stack.Children.Add(interval);
                // Closing or Esc is "tomorrow"; there is no "don't ask again".
                Closed+=delegate {
                    if(chose)return;
                    var fresh=BackupSettings.Load();
                    fresh.RemindAfter=DateTime.Today.AddDays(1).ToUniversalTime();
                    try {fresh.Save();} catch(Exception quiet) {ErrorLog.Quiet(quiet);}
                };
                PreviewKeyDown+=(o,e)=>{if(e.Key==Key.Escape){Close();e.Handled=true;}};
            }
        }
    }
}
