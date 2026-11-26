using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace LoveMyShelf {
    // "Tell us what's inconvenient": the person writes here, sees everything that would go along, and Love My Shelf opens
    // a Google Form in the browser with it filled in. Nothing is sent by Love My Shelf itself: the person presses [Submit]
    // on the form. No sign-in is needed (the form has no file question and no one-response limit).
    internal sealed class FeedbackWindow : Window {
        static string T(string en,string ko) {return L.P(en,ko);}

        // The form of the Love My Shelf Google account (lovemyshelf.app@outlook.com). The numbers are the form's fields;
        // they stay the same as long as those questions are not deleted and made again.
        internal const string FormPage="https://docs.google.com/forms/d/e/1FAIpQLSe08XYMQMcURksoFYLe0Nu-LZP_WOlPUL6godRQnYT__MUxlg/viewform";
        const string KindField="entry.774059150",TextField="entry.227508215",VersionField="entry.1968224065",
            WindowsField="entry.1493050765",ErrorsField="entry.565975869",MailField="entry.581102624";
        // The form's own choices: always Korean, whatever language the program shows.
        static readonly string[] KindValues={"안 돼요 (오류)","불편해요","이런 게 있으면 좋겠어요","기타"};
        // Google answers 400 when the address is longer than about 8,100 bytes (measured 2026-11-26). Some room is kept.
        const int LongestAddress=7000;
        internal const int LongestText=600;

        static FeedbackWindow open;
        internal static void Show(Window owner) {
            if(open!=null && open.IsLoaded){if(open.WindowState==WindowState.Minimized)open.WindowState=WindowState.Normal;open.Activate();return;}
            open=new FeedbackWindow(owner);
            open.Closed+=delegate {open=null;};
            open.WritePage();
            open.Show();
        }

        readonly StackPanel page=new StackPanel {Margin=new Thickness(28,20,28,20)};
        readonly ScrollViewer scroller=new ScrollViewer {VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
        readonly DockPanel bar=new DockPanel {LastChildFill=false,Margin=new Thickness(28,10,28,16)};
        // Kept across the pages, so going back keeps what was written.
        int kind=1;
        string text="",mail="",errors;
        bool withVersion=true,withErrors,opened;
        readonly string versionLine,windowsLine,errorsFound;

        FeedbackWindow(Window owner) {
            UI.WindowSetup(this,T("Tell us what's inconvenient","불편한 점 알려 주기"));
            if(owner!=null && owner.IsVisible){Owner=owner;WindowStartupLocation=WindowStartupLocation.CenterOwner;}
            ShowInTaskbar=true;
            var area=SystemParameters.WorkArea;
            Width=Math.Min(640,area.Width);Height=Math.Min(760,area.Height);
            MinWidth=Math.Min(440,area.Width);MinHeight=Math.Min(400,area.Height);
            var root=new DockPanel();
            root.Children.Add(new WindowTitleBar(this));
            var bottom=new Border {BorderThickness=new Thickness(0,1,0,0),Child=bar};
            bottom.SetResourceReference(Border.BorderBrushProperty,"Line");
            DockPanel.SetDock(bottom,Dock.Bottom);root.Children.Add(bottom);
            scroller.Content=page;root.Children.Add(scroller);
            Content=root;
            PreviewKeyDown+=(s,e)=>{if(e.Key==Key.Escape){Close();e.Handled=true;}};
            // Written but not yet taken to the form: closing would lose it, so ask first.
            Closing+=(s,e)=>{
                if(opened || text.Trim().Length==0)return;
                var answer=MessageBox.Show(this,T("What you wrote hasn't been sent yet and will be lost. Close anyway?","쓴 내용이 아직 보내지지 않았고, 닫으면 사라져요. 그래도 닫을까요?"),
                    Title,MessageBoxButton.YesNo,MessageBoxImage.Question);
                if(answer!=MessageBoxResult.Yes)e.Cancel=true;
            };
            versionLine="Love My Shelf "+AppInfo.Version;
            windowsLine=WindowsName();
            errorsFound=RecentErrors();
            errors=errorsFound;
        }

        // The part of Guide & Options under "Tell us what's inconvenient": one line and the button.
        internal static FrameworkElement GuidePanel(Window guide) {
            var box=new Border {CornerRadius=new CornerRadius(14),BorderThickness=new Thickness(1),Padding=new Thickness(20,16,20,18),Margin=new Thickness(0,0,0,14)};
            box.SetResourceReference(Border.BackgroundProperty,"CardBack");box.SetResourceReference(Border.BorderBrushProperty,"CardLine");
            var stack=new StackPanel();box.Child=stack;
            stack.Children.Add(Line(T("Write it in Love My Shelf, then press [Submit] once on the form that opens. No sign-in is needed.",
                "Love My Shelf에서 쓰고, 열리는 설문지에서 [제출]만 한 번 누르면 돼요. 로그인하지 않아도 돼요."),false,14));
            var button=UI.Btn(T("Tell us what's inconvenient…","불편한 점 알려 주기…"),()=>Show(Application.Current.Windows.OfType<GalleryWindow>().FirstOrDefault()??guide),true);
            button.Padding=new Thickness(16,0,16,0);button.MinHeight=36;button.Margin=new Thickness(0,12,0,0);button.HorizontalAlignment=HorizontalAlignment.Left;
            stack.Children.Add(button);
            return box;
        }

        // ---------------- Building blocks (the same look as the phone and backup windows) ----------------
        void NewPage(string title) {
            page.Children.Clear();bar.Children.Clear();
            var heading=UI.Text(NoticeLook.KeepWords(title),21);heading.FontWeight=FontWeights.Bold;heading.TextWrapping=TextWrapping.Wrap;
            heading.Margin=new Thickness(0,0,0,10);page.Children.Add(heading);
            scroller.ScrollToTop();
        }
        TextBlock Para(string value,bool subtle=false,double size=14,double top=4) {
            var t=UI.Text(NoticeLook.KeepWords(value),size,subtle);t.TextWrapping=TextWrapping.Wrap;
            t.LineHeight=size+8;t.LineStackingStrategy=LineStackingStrategy.BlockLineHeight;
            t.Margin=new Thickness(0,top,0,0);page.Children.Add(t);return t;
        }
        void Sub(string value) {
            var t=UI.Text(NoticeLook.KeepWords(value),16);t.FontWeight=FontWeights.SemiBold;t.TextWrapping=TextWrapping.Wrap;
            t.Margin=new Thickness(0,20,0,6);page.Children.Add(t);
        }
        static TextBlock Line(string value,bool subtle,double size=13) {
            var t=UI.Text(NoticeLook.KeepWords(value),size,subtle);t.TextWrapping=TextWrapping.Wrap;return t;
        }
        Border Note(string value,bool warning) {
            var box=new Border {CornerRadius=new CornerRadius(10),Padding=new Thickness(14,10,14,11),Margin=new Thickness(0,10,0,0),BorderThickness=new Thickness(1)};
            box.SetResourceReference(Border.BackgroundProperty,"Paper");
            box.BorderBrush=new SolidColorBrush(warning?NoticeLook.Amber:Color.FromArgb(0x60,NoticeLook.Blue.R,NoticeLook.Blue.G,NoticeLook.Blue.B));
            box.Child=Line(value,false,13);
            page.Children.Add(box);return box;
        }
        Button AtLeft(string label,Action action) {var b=UI.Btn(label,action);b.MinWidth=96;b.MinHeight=34;b.Padding=new Thickness(14,0,14,0);DockPanel.SetDock(b,Dock.Left);bar.Children.Add(b);return b;}
        Button AtRight(string label,Action action,bool primary=false) {
            var b=UI.Btn(label,action,primary);b.MinWidth=110;b.MinHeight=34;b.Padding=new Thickness(16,0,16,0);b.Margin=new Thickness(8,0,0,0);
            DockPanel.SetDock(b,Dock.Right);bar.Children.Add(b);return b;
        }
        static TextBox Box(string value,bool many,double height) {
            var box=new TextBox {Text=value??"",FontSize=14,Padding=new Thickness(10,8,10,8),BorderThickness=new Thickness(1),
                TextWrapping=many?TextWrapping.Wrap:TextWrapping.NoWrap,AcceptsReturn=many,
                VerticalScrollBarVisibility=many?ScrollBarVisibility.Auto:ScrollBarVisibility.Disabled};
            if(many)box.Height=height;
            box.SetResourceReference(Control.BorderBrushProperty,"Line");
            return box;
        }
        CheckBox Tick(string value,string detail,bool on) {
            var panel=new StackPanel();
            var title=Line(value,false,14);title.FontWeight=FontWeights.SemiBold;panel.Children.Add(title);
            if(detail!=null){var d=Line(detail,true,13);d.Margin=new Thickness(0,2,0,0);panel.Children.Add(d);}
            var tick=new CheckBox {Content=panel,IsChecked=on,Margin=new Thickness(0,4,0,4),VerticalContentAlignment=VerticalAlignment.Top};
            tick.SetResourceReference(Control.ForegroundProperty,"Text");
            page.Children.Add(tick);return tick;
        }

        // ---------------- Writing ----------------
        void WritePage() {
            NewPage(T("Tell us what's inconvenient","불편한 점 알려 주기"));
            Para(T("Something that doesn't work, something awkward, or something you wish it had: write it freely. Every message is read, and it helps make Love My Shelf better.",
                "안 되는 것, 불편한 것, 있었으면 하는 기능을 편하게 적어 주세요. 하나하나 다 읽어 보고 Love My Shelf를 고치는 데 써요."));

            Sub(T("What is it about?","어떤 이야기인가요?"));
            var kinds=new WrapPanel();
            var labels=new[]{T("It doesn't work (an error)","안 돼요 (오류)"),T("It's inconvenient","불편해요"),T("I wish it had this","이런 게 있으면 좋겠어요"),T("Something else","기타")};
            for(int i=0;i<labels.Length;i++) {
                int index=i;
                var choice=new RadioButton {Content=labels[i],GroupName="kind",IsChecked=i==kind,Style=(Style)Application.Current.FindResource("FolderFilterChoice"),
                    FontSize=14,Padding=new Thickness(16,8,16,8),Margin=new Thickness(0,0,8,8)};
                choice.Checked+=delegate {kind=index;};
                kinds.Children.Add(choice);
            }
            page.Children.Add(kinds);

            var count=UI.Text("",13,true);
            var head=new DockPanel {Margin=new Thickness(0,14,0,6)};
            var name=UI.Text(T("What happened?","내용"),16);name.FontWeight=FontWeights.SemiBold;
            DockPanel.SetDock(count,Dock.Right);head.Children.Add(count);head.Children.Add(name);
            page.Children.Add(head);
            var write=Box(text,true,170);write.MaxLength=LongestText;
            Action counted=()=>{count.Text=L.PF("{0} / {1} characters","{0} / {1}자",write.Text.Length,LongestText);};
            write.TextChanged+=delegate {text=write.Text;counted();};
            counted();
            page.Children.Add(write);
            Para(T("Where were you and what were you doing? With that, it's much easier to fix.","어디에서 무엇을 하다가 그랬는지 적어 주시면 고치기 훨씬 쉬워요."),true,13,6);

            Sub(T("What goes along with it","함께 보낼 정보"));
            var version=Tick(T("Version","버전"),versionLine+" · "+windowsLine,withVersion);
            version.Checked+=delegate {withVersion=true;};version.Unchecked+=delegate {withVersion=false;};
            bool any=errorsFound.Length>0;
            var errorTick=Tick(T("Recent error records","최근 오류 기록"),
                any?T("Folders, file names and your Windows user name are hidden as far as Love My Shelf can tell. Check it below and delete anything you'd rather not send.",
                    "폴더 경로, 파일 이름, Windows 사용자 이름은 최대한 가렸어요. 아래에서 확인하고 보내기 싫은 부분은 지워도 돼요.")
                   :T("Love My Shelf hasn't recorded any errors.","Love My Shelf가 적어 둔 오류가 없어요."),any && withErrors);
            errorTick.IsEnabled=any;
            var errorBox=Box(errors,true,150);errorBox.FontSize=12;errorBox.FontFamily=new FontFamily("Consolas, Malgun Gothic");errorBox.MaxLength=12000;
            errorBox.Margin=new Thickness(24,4,0,0);errorBox.Visibility=any && withErrors?Visibility.Visible:Visibility.Collapsed;
            errorBox.TextChanged+=delegate {errors=errorBox.Text;};
            errorTick.Checked+=delegate {withErrors=true;errorBox.Visibility=Visibility.Visible;};
            errorTick.Unchecked+=delegate {withErrors=false;errorBox.Visibility=Visibility.Collapsed;};
            page.Children.Add(errorBox);

            Sub(T("Email for a reply (optional)","답장 받을 이메일 (안 적어도 돼요)"));
            var mailBox=Box(mail,false,0);mailBox.MaxLength=254;
            mailBox.TextChanged+=delegate {mail=mailBox.Text.Trim();};
            page.Children.Add(mailBox);
            Para(T("Only if you'd like an answer. If a picture of the screen would help, we'll ask for it in the reply.","답장이 필요할 때만 적어 주세요. 화면 사진이 필요하면 답장으로 부탁드릴게요."),true,13,6);

            Note(T("When you press [Open the sending page], a Google Form opens in your browser with all of this filled in. It isn't sent yet: press [Submit] at the bottom of the form once. No sign-in is needed. Photos and videos themselves are never sent.",
                "[보내기 화면 열기]를 누르면 브라우저에 구글 설문지가 이 내용 그대로 채워져서 열려요. 아직 보내진 건 아니에요. 설문지 맨 아래 [제출]을 한 번 눌러야 보내져요. 로그인하지 않아도 돼요. 사진·영상 파일 자체는 보내지 않아요."),false);

            AtLeft(T("Close","닫기"),Close);
            AtRight(T("Open the sending page","보내기 화면 열기"),()=>{
                if(text.Trim().Length==0){MessageBox.Show(this,T("Write a line or two first.","먼저 한두 줄 적어 주세요."),Title);write.Focus();return;}
                bool cut;
                string address=Address(out cut);
                if(address==null){MessageBox.Show(this,T("It's too long to go in the form. Please make it a little shorter.","설문지에 넣기에는 너무 길어요. 조금만 줄여 주세요."),Title);return;}
                opened=true;
                if(!OpenInBrowser(address)){OpenFailedPage(address);return;}
                OpenedPage(address,cut);
            },true);
            write.Focus();
        }

        // The form's address with the answers in it, kept short enough for Google. The error records give way first:
        // the oldest record goes first, then the end of the last one. Null when even without them it is too long.
        string Address(out bool cut) {
            cut=false;
            var records=new List<string>();
            if(withErrors) {
                foreach(string raw in (errors??"").Replace("\r\n","\n").Split('\n')) {
                    if(records.Count==0 || Regex.IsMatch(raw,@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d"))records.Add(raw);
                    else records[records.Count-1]+="\n"+raw;
                }
                records=records.Select(r=>r.Trim()).Where(r=>r.Length>0).ToList();
            }
            while(true) {
                string log=String.Join("\n",records);
                var parts=new List<string> {"usp=pp_url"};
                Action<string,string> put=(field,value)=>{if(!String.IsNullOrEmpty(value))parts.Add(field+"="+Escape(value));};
                put(KindField,KindValues[Math.Max(0,Math.Min(KindValues.Length-1,kind))]);
                put(TextField,text.Trim());
                if(withVersion){put(VersionField,versionLine);put(WindowsField,windowsLine);}
                put(ErrorsField,log);
                put(MailField,mail);
                // After escaping only ASCII is left, so the length is the number of bytes.
                string address=FormPage+"?"+String.Join("&",parts);
                if(address.Length<=LongestAddress)return address;
                if(records.Count==0)return null;
                cut=true;
                if(records.Count>1){records.RemoveAt(0);continue;}
                // One record left: keep its beginning (the place and the message), drop the end of it.
                string only=records[0];
                int keep=only.Length-Math.Max(40,(address.Length-LongestAddress)/3+20);
                if(keep<80){records.Clear();continue;}
                if(Char.IsHighSurrogate(only[keep-1]))keep--;
                records[0]=only.Substring(0,keep)+" …";
            }
        }
        // Uri.EscapeDataString stops at a half emoji (a lone surrogate); those are dropped instead.
        static string Escape(string value) {
            var clean=new StringBuilder(value.Length);
            for(int i=0;i<value.Length;i++) {
                char c=value[i];
                if(Char.IsHighSurrogate(c)){if(i+1<value.Length && Char.IsLowSurrogate(value[i+1])){clean.Append(c).Append(value[i+1]);i++;}continue;}
                if(Char.IsLowSurrogate(c))continue;
                clean.Append(c);
            }
            return Uri.EscapeDataString(clean.ToString());
        }
        bool OpenInBrowser(string address) {
            try {Process.Start(new ProcessStartInfo(address) {UseShellExecute=true});return true;}
            catch(Exception ex) {ErrorLog.Quiet(ex);return false;}
        }
        void OpenedPage(string address,bool cut) {
            NewPage(T("One more step: press [Submit]","한 번만 더: [제출]을 눌러 주세요"));
            Para(T("Look at your browser. The Google Form should be open there with what you wrote.","브라우저를 봐 주세요. 쓰신 내용이 채워진 구글 설문지가 열려 있을 거예요."));
            Note(T("It isn't sent yet. Press [Submit] at the very bottom of the form, and then it's sent.","아직 보내지지 않았어요. 설문지 맨 아래의 [제출]을 눌러야 보내져요."),true);
            if(cut)Note(T("The error records were long, so only the newest part went in.","오류 기록이 길어서 가장 최근 부분만 넣었어요."),false);
            Para(T("Thank you for taking the time. It really helps.","시간 내 주셔서 고마워요. 정말 큰 도움이 돼요."),true,14,14);
            Para(T("If no browser opened, press [Copy the address], open Chrome or Edge, right-click the address bar at the top and choose [Paste].","브라우저가 열리지 않았다면 [주소 복사]를 누르고, 크롬이나 엣지를 열어 맨 위 주소 칸을 오른쪽 클릭한 뒤 [붙여넣기]를 눌러 주세요."),true,13,10);
            AtLeft(T("Back","뒤로"),WritePage);
            AtRight(T("Close","닫기"),Close);
            AtRight(T("Copy the address","주소 복사"),()=>CopyText(address));
        }
        void OpenFailedPage(string address) {
            NewPage(T("The browser didn't open","브라우저가 열리지 않았어요"));
            Para(T("Press [Copy the address], open Chrome or Edge, right-click the address bar at the top and choose [Paste]. The form opens with what you wrote; press [Submit] at the bottom of it.",
                "[주소 복사]를 누른 뒤 크롬이나 엣지를 열어 맨 위 주소 칸을 오른쪽 클릭하고 [붙여넣기]를 눌러 주세요. 쓰신 내용이 채워진 설문지가 열리면 맨 아래 [제출]을 눌러 주세요."));
            AtLeft(T("Back","뒤로"),WritePage);
            AtRight(T("Copy the address","주소 복사"),()=>CopyText(address),true);
        }
        void CopyText(string value) {
            try {Clipboard.SetText(value);MessageBox.Show(this,T("Copied.","복사했어요."),Title);}
            catch(Exception ex) {ErrorLog.Quiet(ex);UI.Error(this,ex);}
        }

        // ---------------- What goes along ----------------
        // "Windows 11 Home 24H2 (26100)", from the registry; only names and numbers, nothing about the person.
        static string WindowsName() {
            try {
                using(var key=Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion")) {
                    if(key!=null) {
                        string product=key.GetValue("ProductName") as string??"Windows";
                        string release=key.GetValue("DisplayVersion") as string??key.GetValue("ReleaseId") as string??"";
                        string build=key.GetValue("CurrentBuildNumber") as string??"";
                        int number;
                        // Windows 11 still calls itself "Windows 10" in ProductName; the build tells them apart.
                        if(Int32.TryParse(build,out number) && number>=22000)product=product.Replace("Windows 10","Windows 11");
                        string text=product+(release.Length>0?" "+release:"")+(build.Length>0?" ("+build+")":"");
                        return text+(Environment.Is64BitOperatingSystem?" 64‑bit":" 32‑bit");
                    }
                }
            } catch(Exception ex) {ErrorLog.Quiet(ex);}
            return Environment.OSVersion.VersionString;
        }

        // The last few problems from errors.txt, a few lines each, with anything personal hidden.
        internal static string RecentErrors() {
            try {
                string path=ErrorLog.Path;
                if(!File.Exists(path))return "";
                string all;
                using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
                using(var reader=new StreamReader(file,Encoding.UTF8)) {
                    if(file.Length>64*1024)file.Seek(-64*1024,SeekOrigin.End);
                    all=reader.ReadToEnd();
                }
                var entries=new List<List<string>>();
                foreach(string raw in all.Replace("\r\n","\n").Split('\n')) {
                    if(raw.Length==0)continue;
                    if(!raw.StartsWith(" ") && Regex.IsMatch(raw,@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d"))entries.Add(new List<string>());
                    if(entries.Count==0)continue;
                    var entry=entries[entries.Count-1];
                    if(entry.Count<7)entry.Add(raw.TrimEnd());
                }
                var text=new StringBuilder();
                foreach(var entry in entries.Skip(Math.Max(0,entries.Count-3))) {
                    // Hidden before it is shortened, so a cut never leaves half a file name that the hiding misses.
                    foreach(string line in entry){string safe=Hide(line);int cut=safe.Length>300 && Char.IsHighSurrogate(safe[299])?299:300;text.Append(safe.Length>300?safe.Substring(0,cut)+" …":safe).Append(Environment.NewLine);}
                }
                return text.ToString().Trim();
            } catch(Exception ex) {ErrorLog.Quiet(ex);return "";}
        }

        // Hides folders, file names, the Windows user name and the computer's name. Too much hidden is better than too little.
        internal static string Hide(string value) {
            if(String.IsNullOrEmpty(value))return "";
            // A mark no name can contain while replacing; the real word goes in at the end.
            const string hidden="\u0001";
            string result=value;
            // Paths: C:\…, \\server\…, file:///…, up to a quote or the end of the line.
            result=Regex.Replace(result,@"file:/{2,3}[^""\r\n<>|]*",hidden,RegexOptions.IgnoreCase);
            result=Regex.Replace(result,@"(?<![A-Za-z])[A-Za-z]:[\\/][^""\r\n<>|]*",hidden);
            // \\server\share\… and \folder\folder\… (a path without its drive letter).
            result=Regex.Replace(result,@"(?<![\w:\\])\\{1,2}[^\\\s""]+\\[^""\r\n<>|]*",hidden);
            // Anything else with a backslash between names (a phone's "This PC\Galaxy\DCIM\…"), and phone folders written with /.
            result=Regex.Replace(result,@"[^\s\\/:*?<>|""]{1,60}\\[^\r\n""<>|]+",hidden);
            result=Regex.Replace(result,@"/(?:storage|sdcard|DCIM|Users|home|mnt|media)/[^\r\n'""<>|]*",hidden,RegexOptions.IgnoreCase);
            // A file name on its own, as in 'IMG_0001.jpg' or "clip.mp4", spaces included.
            result=Regex.Replace(result,@"(['""“‘])[^'""“”‘’\r\n]{1,200}\.[A-Za-z0-9]{2,10}(['""”’])","$1"+hidden+"$2");
            result=Regex.Replace(result,@"[^\r\n'""\\/:*?<>|]{1,80}\.(jpe?g|png|gif|webp|bmp|heic|heif|avif|jxl|tiff?|raw|dng|cr2|cr3|nef|arw|orf|rw2|psd|mp4|mkv|avi|mov|wmv|webm|m4v|3gp|ts|flv|mpe?g|smi|srt|ass|vtt|txt|xml|zip|7z|rar|db|lmsbackup)(?![.\w])",hidden,RegexOptions.IgnoreCase);
            // IP addresses.
            result=Regex.Replace(result,@"\b(?:\d{1,3}\.){3}\d{1,3}\b",hidden);
            string profile="";
            try {profile=Path.GetFileName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\'));} catch(Exception) {}
            foreach(string name in new[]{Environment.UserName,Environment.MachineName,Environment.UserDomainName,profile}) {
                if(String.IsNullOrEmpty(name) || name.Length<2)continue;
                result=Regex.Replace(result,Regex.Escape(name),hidden,RegexOptions.IgnoreCase);
            }
            // Email addresses.
            result=Regex.Replace(result,@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}",hidden);
            return result.Replace(hidden,T("(hidden)","(가림)"));
        }
    }
}
