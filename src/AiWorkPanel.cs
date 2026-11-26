using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace LoveMyShelf {
    // The folder tree with the writing box right under its last folder. The whole tree comes first: the box shows only
    // when the tree fits with room to spare for all of the box, and while someone is typing in it. Otherwise the tree
    // gets all the height and scrolls as before.
    internal sealed class TreeWithBox : Panel {
        readonly FrameworkElement tree;
        readonly AiWorkPanel box;
        ScrollViewer scroller;
        double extent=-1;
        internal bool BoxShown {get; private set;}
        internal double LastWhole,LastBox,LastHeight; // for checks
        internal TreeWithBox(FrameworkElement tree,AiWorkPanel box) {
            this.tree=tree;this.box=box;ClipToBounds=true;
            Children.Add(tree);Children.Add(box);
            // Opening or closing folders changes how tall the tree is: look again whenever its contents change height.
            LayoutUpdated+=delegate {
                if(scroller==null)scroller=Find(tree);
                if(scroller!=null && Math.Abs(scroller.ExtentHeight-extent)>0.5){extent=scroller.ExtentHeight;InvalidateMeasure();}
            };
            box.IsKeyboardFocusWithinChanged+=delegate {InvalidateMeasure();};
        }
        static ScrollViewer Find(DependencyObject at) {
            for(int i=0;i<VisualTreeHelper.GetChildrenCount(at);i++) {
                var child=VisualTreeHelper.GetChild(at,i);
                var found=child as ScrollViewer??Find(child);
                if(found!=null)return found;
            }
            return null;
        }
        double treeHeight;
        protected override Size MeasureOverride(Size available) {
            double width=available.Width,height=available.Height;
            var open=new Size(width,double.PositiveInfinity);
            tree.Measure(open);double whole=tree.DesiredSize.Height;
            box.Measure(open);double boxHeight=box.DesiredSize.Height;
            if(double.IsInfinity(height))height=whole+boxHeight;
            LastWhole=whole;LastBox=boxHeight;LastHeight=height;
            BoxShown=whole+boxHeight<=height+0.5 || (box.IsKeyboardFocusWithin && height-boxHeight>=60);
            treeHeight=BoxShown?Math.Min(whole,height-boxHeight):height;
            tree.Measure(new Size(width,Math.Max(0,treeHeight)));
            return new Size(double.IsInfinity(width)?Math.Max(tree.DesiredSize.Width,box.DesiredSize.Width):width,height);
        }
        protected override Size ArrangeOverride(Size final) {
            tree.Arrange(new Rect(0,0,final.Width,Math.Max(0,treeHeight)));
            // Hidden: placed below the bottom edge, where the clip keeps it out of sight and out of reach.
            box.Arrange(new Rect(0,BoxShown?treeHeight:final.Height+1,final.Width,box.DesiredSize.Height));
            box.IsHitTestVisible=BoxShown;
            return final;
        }
    }

    // The small writing box at the bottom of the folder tree: the person's own sentence, a box that starts with the
    // app version, and [보내기]. Only what is in the box goes to the Google Form, straight from here (no browser).
    internal sealed class AiWorkPanel : Border {
        const string Form="https://docs.google.com/forms/d/e/1FAIpQLSe08XYMQMcURksoFYLe0Nu-LZP_WOlPUL6godRQnYT__MUxlg";
        const string Field="entry.227508215";
        const int LongestAddress=7000;
        const string VersionLine="LoveMyShelf "+AppInfo.Version;
        static readonly string StartText=VersionLine+Environment.NewLine;
        static readonly List<AiWorkPanel> panels=new List<AiWorkPanel>();
        static AiWorkPanel draftWriter;
        static bool sending;
        static string DraftPath {get {return Path.Combine(ErrorLog.Folder,"ai-work-draft.txt");}}

        readonly Window owner;
        readonly TextBlock words,result;
        readonly TextBox box;
        readonly Button send;
        readonly Hyperlink browser;
        bool failed;
        readonly DispatcherTimer draftTimer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(500)};
        readonly DispatcherTimer sentTimer=new DispatcherTimer {Interval=TimeSpan.FromSeconds(5)};
        bool updatingText,closed;

        public AiWorkPanel(Window owner) {
            this.owner=owner;
            UseLayoutRounding=true;SnapsToDevicePixels=true;
            // No line of its own: it sits right under the last folder of the tree (TreeWithBox).
            Padding=new Thickness(12,10,12,12);

            words=UI.Text("",13);words.TextWrapping=TextWrapping.Wrap;
            words.LineHeight=20;words.LineStackingStrategy=LineStackingStrategy.BlockLineHeight;words.Margin=new Thickness(0,0,0,8);

            string draft=ReadFile(DraftPath);
            if(draft!=null && !Meaningful(draft))draft=null;
            box=new TextBox {Text=panels.Count>0?panels[0].box.Text:(draft??StartText),AcceptsReturn=true,MaxLength=4000,
                FontSize=13,Height=78,Padding=new Thickness(6,5,6,5),TextWrapping=TextWrapping.Wrap,
                VerticalScrollBarVisibility=ScrollBarVisibility.Auto,AllowDrop=false};
            box.SetResourceReference(Control.BackgroundProperty,"Paper");
            box.SetResourceReference(Control.BorderBrushProperty,"CardLine");
            box.CaretIndex=box.Text.Length;
            box.TextChanged+=OnTextChanged;
            // Esc anywhere in the box area gives the keys back to the folder window.
            PreviewKeyDown+=(s,e)=>{if(e.Key==Key.Escape){e.Handled=true;Keyboard.ClearFocus();if(owner.IsActive)owner.Focus();}};

            send=UI.Btn("",Send,true);send.MinHeight=32;send.MinWidth=88;send.Padding=new Thickness(14,0,14,0);
            send.HorizontalAlignment=HorizontalAlignment.Right;send.Margin=new Thickness(0,8,0,0);send.IsEnabled=!sending;
            // Two lines kept for the result even when empty (a narrow tree wraps the failure line), so the box keeps one height.
            // After a failure the way through the browser is a link inside it, not one more button.
            result=UI.Text("",13);result.TextWrapping=TextWrapping.Wrap;result.Margin=new Thickness(0,6,0,0);result.MinHeight=40;
            browser=new Hyperlink();browser.Click+=delegate {OpenBrowser();};
            var stack=new StackPanel();
            stack.Children.Add(words);stack.Children.Add(box);stack.Children.Add(send);stack.Children.Add(result);
            Child=stack;
            Localize();

            panels.Add(this);
            draftTimer.Tick+=delegate {FlushDraft();};
            sentTimer.Tick+=delegate {sentTimer.Stop();if(!failed)SetResult(false,false);};
            // A function key typed while writing (F11 full screen) stays out of the folder window.
            owner.PreviewKeyDown+=OnWindowKey;
            owner.Closed+=OnWindowClosed;
        }

        // The words, again whenever the language changes (GalleryWindow's localize).
        internal void Localize() {
            words.Text=NoticeLook.KeepWords(L.P(
                "Write whatever you'd like to say to the AI that made this: features you want, errors, anything. In the next update we'll show you how it was discussed.",
                "원하는 기능, 오류 등 이걸 만든 AI한테 하고 싶은 말을 적어주세요. 다음 업데이트 때 이에 대한 토론 과정을 보여드리겠습니다."));
            send.Content=L.P("Send","보내기");
            if(result.Inlines.Count>0)SetResult(!failed,failed);
        }
        // Caret at the end of the box (the folder window opens the tree first when it is closed).
        internal void FocusBox() {
            owner.Dispatcher.BeginInvoke(DispatcherPriority.Input,new Action(()=>{
                if(closed || !IsVisible)return;
                box.Focus();box.CaretIndex=box.Text.Length;
            }));
        }
        internal static bool ShowIn(Window anyWindow) {
            var gallery=(anyWindow!=null?anyWindow.Owner as GalleryWindow:null);
            if(gallery==null || !panels.Exists(p=>p.owner==gallery)) {
                var first=panels.Find(p=>p.owner is GalleryWindow);
                if(first==null)return false;
                gallery=(GalleryWindow)first.owner;
            }
            if(gallery.WindowState==WindowState.Minimized)gallery.WindowState=WindowState.Normal;
            gallery.Activate();
            gallery.ShowAiWork();
            return true;
        }
        // Guide & Options, section 'feedback': one button that opens the box in the folder window.
        internal static FrameworkElement GuidePanel(Window guide) {
            var button=UI.Btn(L.P("Write it","적으러 가기"),()=>{if(ShowIn(guide))guide.Close();},true);
            button.Padding=new Thickness(16,0,16,0);button.MinHeight=36;button.Margin=new Thickness(0,0,0,14);button.HorizontalAlignment=HorizontalAlignment.Left;
            return button;
        }

        void OnWindowKey(object sender,KeyEventArgs e) {
            if(!IsKeyboardFocusWithin)return;
            if(e.Key==Key.F11 || (e.Key==Key.ImeProcessed && e.ImeProcessedKey==Key.F11))e.Handled=true;
        }
        void OnWindowClosed(object sender,EventArgs e) {
            closed=true;panels.Remove(this);
            FlushDraft();sentTimer.Stop();
            owner.PreviewKeyDown-=OnWindowKey;owner.Closed-=OnWindowClosed;
        }

        void OnTextChanged(object sender,TextChangedEventArgs e) {
            if(updatingText)return;
            if(sentTimer.IsEnabled){sentTimer.Stop();SetResult(false,false);}
            if(draftWriter!=null)draftWriter.draftTimer.Stop();
            draftWriter=this;draftTimer.Start();
            foreach(var panel in panels)
                if(panel!=this)panel.SetText(box.Text);
        }
        void SetText(string value) {
            if(box.Text==value)return;
            updatingText=true;
            try {box.Text=value;box.CaretIndex=box.Text.Length;}
            finally {updatingText=false;}
        }
        void FlushDraft() {
            draftTimer.Stop();
            if(draftWriter!=this)return;
            draftWriter=null;
            try {
                if(!Meaningful(box.Text))File.Delete(DraftPath);
                else WriteFile(DraftPath,box.Text);
            } catch(Exception ex) {ErrorLog.Quiet(ex);}
        }
        static string ReadFile(string path) {
            try {return File.Exists(path)?File.ReadAllText(path,Encoding.UTF8):null;}
            catch(Exception ex) {ErrorLog.Quiet(ex);return null;}
        }
        static void WriteFile(string path,string value) {
            Directory.CreateDirectory(ErrorLog.Folder);
            File.WriteAllText(path,value,Encoding.UTF8);
        }

        // Something written besides the version line that was there from the start.
        static bool Meaningful(string value) {string trimmed=value.Trim();return trimmed.Length!=0 && trimmed!=VersionLine;}
        bool HasFeedback(string value) {
            if(Meaningful(value))return true;
            box.Focus();box.CaretIndex=box.Text.Length;return false;
        }
        void SetResult(bool success,bool failure) {
            failed=failure;
            result.Inlines.Clear();browser.Inlines.Clear();
            if(success)result.Inlines.Add(new Run(L.P("Sent","보냈어요")));
            if(failure) {
                result.Inlines.Add(new Run(L.P("Couldn't send · ","못 보냈어요 · ")));
                browser.Inlines.Add(new Run(L.P("Send in the browser","브라우저로 보내기")));result.Inlines.Add(browser);
            }
        }
        // Typing in the box: the folder window leaves its keys alone (buttons here behave like any other button).
        internal bool Typing {get {return box.IsKeyboardFocusWithin;}}
        async void Send() {
            if(sending)return;
            string value=box.Text;
            if(!HasFeedback(value))return;
            sentTimer.Stop();SetResult(false,false);
            if(draftWriter!=null)draftWriter.FlushDraft();
            SetSending(true);
            bool success=await Task.Run(()=>Post(value));
            SetSending(false);
            if(!success){if(!closed)SetResult(false,true);return;}
            // Only clear the sent text. A person can keep typing while the request runs.
            var current=panels.Count>0?panels[0]:this;
            if(current.box.Text!=value){if(!closed){SetResult(true,false);sentTimer.Start();}return;}
            if(draftWriter!=null){draftWriter.draftTimer.Stop();draftWriter=null;}
            foreach(var panel in panels){panel.SetText(StartText);panel.SetResult(false,false);panel.sentTimer.Stop();}
            try {File.Delete(DraftPath);} catch(Exception ex) {ErrorLog.Quiet(ex);}
            if(closed)return;
            SetResult(true,false);sentTimer.Start();
        }
        static void SetSending(bool value) {
            sending=value;
            foreach(var panel in panels){panel.send.IsEnabled=!value;panel.browser.IsEnabled=!value;}
        }
        // Checks put a stand-in here so nothing reaches the real form.
        internal static Func<string,bool> PostForChecks;
        static bool Post(string value) {
            if(PostForChecks!=null)return PostForChecks(value);
            try {
                ServicePointManager.SecurityProtocol|=(SecurityProtocolType)3072; // TLS 1.2
                byte[] data=Encoding.UTF8.GetBytes(Field+"="+Escape(value));
                var request=(HttpWebRequest)WebRequest.Create(Form+"/formResponse");
                request.Method="POST";request.ContentType="application/x-www-form-urlencoded";
                request.AllowAutoRedirect=false;
                request.Timeout=15000;request.ReadWriteTimeout=15000;request.ContentLength=data.Length;
                using(var stream=request.GetRequestStream())stream.Write(data,0,data.Length);
                using(var response=(HttpWebResponse)request.GetResponse())
                using(var reader=new StreamReader(response.GetResponseStream(),Encoding.UTF8)) {
                    return response.StatusCode==HttpStatusCode.OK && IsConfirmation(reader.ReadToEnd());
                }
            } catch(WebException ex) {
                if(ex.Response!=null)ex.Response.Dispose();
                ErrorLog.Quiet(ex);return false;
            } catch(Exception ex) {ErrorLog.Quiet(ex);return false;}
        }
        static bool IsConfirmation(string html) {
            html=Regex.Replace(html,@"<!--.*?-->|<script\b[^>]*>.*?</script\s*>","",RegexOptions.IgnoreCase|RegexOptions.Singleline);
            // Google answers 400 when the answer is refused (checked 2026-11-27 with an empty answer), so a 200 is the
            // thank-you page unless it shows the form again. The class names of that page change, so they aren't required.
            if(html.Trim().Length==0 || html.IndexOf("/forms/",StringComparison.Ordinal)<0 || html.IndexOf("closedform",StringComparison.OrdinalIgnoreCase)>=0)return false;
            return !Regex.IsMatch(html,@"<form\b[^>]*\saction\s*=\s*[""'][^""']*/formResponse(?:[?#][^""']*)?[""']",RegexOptions.IgnoreCase);
        }
        void OpenBrowser() {
            string value=box.Text;
            if(!HasFeedback(value))return;
            try {
                string prefix=Form+"/viewform?usp=pp_url&"+Field+"=";
                string escaped=Escape(value);
                if(prefix.Length+escaped.Length>LongestAddress) {
                    int low=0,high=value.Length;
                    while(low<high) {
                        int count=(low+high+1)/2;
                        if(prefix.Length+Escape(value.Substring(0,count)).Length<=LongestAddress)low=count;
                        else high=count-1;
                    }
                    escaped=Escape(value.Substring(0,low));
                }
                Process.Start(new ProcessStartInfo(prefix+escaped) {UseShellExecute=true});
            } catch(Exception ex) {ErrorLog.Quiet(ex);}
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
    }
}
