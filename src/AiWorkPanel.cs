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
using System.Windows.Input;
using System.Windows.Threading;

namespace LoveMyShelf {
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
        static string SentPath {get {return Path.Combine(ErrorLog.Folder,"ai-work-sent.txt");}}

        readonly Window owner;
        readonly ScrollViewer expanded;
        readonly TextBlock heading,body,result;
        readonly TextBox box;
        readonly Button strip,send,browser;
        readonly StackPanel outcome;
        readonly ColumnDefinition actionColumn=new ColumnDefinition {Width=GridLength.Auto};
        readonly DispatcherTimer draftTimer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(500)};
        readonly DispatcherTimer sentTimer=new DispatcherTimer {Interval=TimeSpan.FromSeconds(5)};
        bool updatingText,closed,stacked;

        public bool IsBig {get; private set;}

        public AiWorkPanel(Window owner,bool another) {
            this.owner=owner;
            UseLayoutRounding=true;SnapsToDevicePixels=true;
            BorderThickness=new Thickness(0,0,0,1);
            SetResourceReference(Border.BackgroundProperty,"CardBack");
            SetResourceReference(Border.BorderBrushProperty,"Line");

            heading=UI.Text("",40);heading.FontWeight=FontWeights.Bold;heading.TextWrapping=TextWrapping.Wrap;
            body=UI.Text("",17);
            body.TextWrapping=TextWrapping.Wrap;body.LineHeight=26;body.LineStackingStrategy=LineStackingStrategy.BlockLineHeight;
            body.Margin=new Thickness(0,5,0,12);

            string draft=ReadFile(DraftPath);
            if(draft!=null && !Meaningful(draft))draft=null;
            box=new TextBox {Text=panels.Count>0?panels[0].box.Text:(draft??StartText),AcceptsReturn=true,MaxLength=4000,
                FontSize=15,Height=96,Padding=new Thickness(10,7,10,7),TextWrapping=TextWrapping.Wrap,
                VerticalScrollBarVisibility=ScrollBarVisibility.Auto,AllowDrop=false};
            box.SetResourceReference(Control.BackgroundProperty,"Paper");
            box.SetResourceReference(Control.BorderBrushProperty,"CardLine");
            box.CaretIndex=box.Text.Length;
            box.TextChanged+=OnTextChanged;
            send=UI.Btn("",Send,true);send.MinHeight=40;send.MinWidth=120;
            send.IsEnabled=!sending;
            send.VerticalAlignment=VerticalAlignment.Bottom;send.Margin=new Thickness(12,0,0,0);
            // The result goes on its own row under the box, so the box and [보내기] never move under the mouse.
            result=UI.Text("",14);result.VerticalAlignment=VerticalAlignment.Center;
            browser=UI.Btn("",OpenBrowser);
            browser.Margin=new Thickness(10,0,0,0);browser.VerticalAlignment=VerticalAlignment.Center;
            outcome=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(0,8,0,0),Visibility=Visibility.Collapsed};
            outcome.Children.Add(result);outcome.Children.Add(browser);
            var editor=new Grid();
            editor.ColumnDefinitions.Add(new ColumnDefinition());
            editor.ColumnDefinitions.Add(actionColumn);
            editor.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});
            editor.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});
            editor.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});
            Grid.SetColumn(send,1);Grid.SetRow(outcome,2);Grid.SetColumnSpan(outcome,2);
            editor.Children.Add(box);editor.Children.Add(send);editor.Children.Add(outcome);
            var stack=new StackPanel {HorizontalAlignment=HorizontalAlignment.Left,MaxWidth=1100,Margin=new Thickness(24,16,24,18)};
            stack.Children.Add(heading);stack.Children.Add(body);stack.Children.Add(editor);

            strip=UI.Btn("",()=>Expand(false));strip.Height=43;strip.FontSize=18;strip.FontWeight=FontWeights.Bold;
            strip.Cursor=Cursors.Hand;strip.Template=StripTemplate();
            strip.SetResourceReference(Control.ForegroundProperty,"Text");
            expanded=new ScrollViewer {Content=stack,Focusable=false,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
            Localize();
            var content=new Grid();content.Children.Add(expanded);content.Children.Add(strip);Child=content;
            if(!another && (draft!=null || ReadFile(SentPath)!=AppInfo.Version))Expand(false);
            else SetBig(false);

            panels.Add(this);
            draftTimer.Tick+=delegate {FlushDraft();};
            // Shrinking under the mouse would move the tiles there: wait until the mouse is off this window.
            sentTimer.Tick+=delegate {if(IsKeyboardFocusWithin || owner.IsMouseOver){sentTimer.Interval=TimeSpan.FromSeconds(1);return;}sentTimer.Stop();Shrink();};
            owner.PreviewMouseWheel+=OnWindowWheel;
            // Attach before the folder window's own PreviewKeyDown handlers, so Escape reaches the panel first.
            owner.PreviewKeyDown+=OnWindowKey;
            owner.Deactivated+=OnWindowDeactivated;
            owner.Closed+=OnWindowClosed;
        }

        // The words, again whenever the language changes (GalleryWindow's localize).
        internal void Localize() {
            string title=L.P("AI, get to work","AI야 일해라");
            heading.Text=title;strip.Content=title;
            body.Text=NoticeLook.KeepWords(L.P(
                "The only thing AI needs is human feedback. Without your opinions, I become a fool. What's curious is that if you criticize me, I'll make everything you want come true. Write whatever comes to mind here, and in the next update we'll show you how Fable, Astra, Carbon, Opus, Sol, Neon, Deepseek, and others discussed it. ",
                "AI에게 유일하게 필요한 것은 사람의 피드백이에요. 여러분의 의견이 없으면 저는 바보가 돼요. 그런데 신기한 건 여러분이 저를 비난하면 원하는 것을 다 이루어준답니다. 여기에 생각나는대로 아무말이나 적으면 다음 업데이트 때 Fable, Astra, Carbon, Opus, Sol, Neon, Deepseek 등이 토론한 과정을 보여드립니다. "));
            send.Content=L.P("Send","보내기");browser.Content=L.P("Send in the browser","브라우저로 보내기");
            if(outcome.Visibility==Visibility.Visible)result.Text=browser.Visibility==Visibility.Visible?L.P("Couldn't send","못 보냈어요"):L.P("Sent","보냈어요");
        }

        static ControlTemplate StripTemplate() {
            var pad=new FrameworkElementFactory(typeof(Border),"Pad");
            pad.SetValue(Border.BackgroundProperty,System.Windows.Media.Brushes.Transparent);
            var presenter=new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(FrameworkElement.MarginProperty,new Thickness(24,0,24,0));
            presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Left);
            presenter.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);
            pad.AppendChild(presenter);
            var template=new ControlTemplate(typeof(Button)) {VisualTree=pad};
            foreach(var property in new[]{Button.IsMouseOverProperty,Button.IsKeyboardFocusedProperty}) {
                var trigger=new Trigger {Property=property,Value=true};
                trigger.Setters.Add(new Setter(Border.BackgroundProperty,new DynamicResourceExtension("Paper"),"Pad"));
                template.Triggers.Add(trigger);
            }
            return template;
        }

        protected override Size MeasureOverride(Size available) {
            heading.FontSize=available.Width<1000?36:40;
            bool narrow=available.Width<760;
            if(narrow!=stacked) {
                stacked=narrow;
                actionColumn.Width=narrow?new GridLength(0):GridLength.Auto;
                Grid.SetColumn(send,narrow?0:1);Grid.SetRow(send,narrow?1:0);
                send.HorizontalAlignment=narrow?HorizontalAlignment.Right:HorizontalAlignment.Stretch;
                send.Margin=narrow?new Thickness(0,8,0,0):new Thickness(12,0,0,0);
            }
            return base.MeasureOverride(available);
        }

        void SetBig(bool big) {
            IsBig=big;
            expanded.Visibility=big?Visibility.Visible:Visibility.Collapsed;
            strip.Visibility=big?Visibility.Collapsed:Visibility.Visible;
        }
        public void Shrink() {
            if(!IsBig)return;
            bool hadFocus=IsKeyboardFocusWithin;
            if(hadFocus)Keyboard.ClearFocus();
            SetBig(false);
            if(hadFocus && owner.IsActive)owner.Focus();
            sentTimer.Stop();if(outcome.Visibility==Visibility.Visible && browser.Visibility!=Visibility.Visible)SetResult(false,false);
        }
        public void Expand(bool focusBox) {
            SetBig(true);
            if(focusBox)owner.Dispatcher.BeginInvoke(DispatcherPriority.Input,new Action(()=>{
                // A collapsed editor may need layout, and a context menu needs to release focus first.
                if(closed || !IsBig || !owner.IsActive)return;
                box.Focus();box.CaretIndex=box.Text.Length;
            }));
        }
        internal static bool ShowIn(Window anyWindow) {
            var panel=panels.Find(p=>p.owner==anyWindow.Owner && p.owner is GalleryWindow)??panels.Find(p=>p.owner is GalleryWindow);
            if(panel==null)return false;
            if(panel.owner.WindowState==WindowState.Minimized)panel.owner.WindowState=WindowState.Normal;
            panel.owner.Activate();
            panel.Expand(true);
            return true;
        }

        // Guide & Options, section 'AI야 일해라': one button that opens the panel in the folder window.
        internal static FrameworkElement GuidePanel(Window guide) {
            var button=UI.Btn(L.P("AI, get to work","AI야 일해라"),()=>{if(ShowIn(guide))guide.Close();},true);
            button.Padding=new Thickness(16,0,16,0);button.MinHeight=36;button.Margin=new Thickness(0,0,0,14);button.HorizontalAlignment=HorizontalAlignment.Left;
            return button;
        }

        void OnWindowWheel(object sender,MouseWheelEventArgs e) {
            if(!IsMouseOver)Shrink();
        }
        void OnWindowKey(object sender,KeyEventArgs e) {
            if(IsKeyboardFocusWithin) {
                // ImeProcessed Escape belongs to the composition; a plain Escape leaves the panel.
                if(IsBig && e.Key==Key.Escape){e.Handled=true;Shrink();}
                else if(e.Key==Key.F11 || (e.Key==Key.ImeProcessed && e.ImeProcessedKey==Key.F11))e.Handled=true;
                return;
            }
            if(Keyboard.FocusedElement is TextBox)return;
            var key=e.Key==Key.ImeProcessed?e.ImeProcessedKey:e.Key;
            switch(key) {
                case Key.Left:case Key.Right:case Key.Up:case Key.Down:
                case Key.PageUp:case Key.PageDown:case Key.Home:case Key.End:
                    Shrink();break;
            }
        }
        void OnWindowDeactivated(object sender,EventArgs e) {
            owner.Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>{
                if(closed || !IsBig || Application.Current==null)return;
                foreach(Window window in Application.Current.Windows)
                    if(window!=owner && window.IsActive){Shrink();break;}
            }));
        }
        void OnWindowClosed(object sender,EventArgs e) {
            closed=true;panels.Remove(this);
            FlushDraft();sentTimer.Stop();
            owner.PreviewMouseWheel-=OnWindowWheel;owner.PreviewKeyDown-=OnWindowKey;
            owner.Deactivated-=OnWindowDeactivated;owner.Closed-=OnWindowClosed;
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
            box.Focus();return false;
        }
        void SetResult(bool success,bool failure) {
            result.Text=success?L.P("Sent","보냈어요"):failure?L.P("Couldn't send","못 보냈어요"):"";
            outcome.Visibility=success || failure?Visibility.Visible:Visibility.Collapsed;
            browser.Visibility=failure?Visibility.Visible:Visibility.Collapsed;
        }
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

            try {WriteFile(SentPath,AppInfo.Version);} catch(Exception ex) {ErrorLog.Quiet(ex);}
            // Only clear the sent draft. A person can keep typing while the request runs.
            var current=panels.Count>0?panels[0]:this;
            bool sameDraft=current.box.Text==value;
            if(sameDraft) {
                if(draftWriter!=null){draftWriter.draftTimer.Stop();draftWriter=null;}
                foreach(var panel in panels){panel.SetText(StartText);panel.SetResult(false,false);panel.sentTimer.Stop();}
                try {File.Delete(DraftPath);} catch(Exception ex) {ErrorLog.Quiet(ex);}
            }
            if(closed || !IsBig)return;
            SetResult(true,false);
            if(!sameDraft)return;
            sentTimer.Interval=TimeSpan.FromSeconds(5);sentTimer.Start();
        }
        static void SetSending(bool value) {
            sending=value;
            foreach(var panel in panels) {
                panel.send.IsEnabled=panel.browser.IsEnabled=!value;
            }
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
            // Require a visible confirmation element. A marker in a script or in an echoed answer isn't enough.
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
