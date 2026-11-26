using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace LoveMyShelf {
    // Guide & Options: one window that explains everything in plain words and lets people choose each
    // setting right after reading what it does. A choice is saved and applied the moment it is clicked.
    // Opened from the Guide & Options button or F1 (on the section for the screen in use).
    //
    // Writing rule for the Korean text: friendly and concrete even when long, so that a primary-school
    // child or an older person who has never used a computer much can follow it. Written in Korean
    // first, not translated from the English.
    internal sealed class GuideWindow : Window {
        // Text may contain [[button name]] (drawn like the real button) and {{key}} (drawn like a keyboard key).
        sealed class Card {
            public string Title;public string[] Lines=new string[0];public string[] Steps=new string[0];
            public Tuple<string[],string>[] Keys=new Tuple<string[],string>[0];
            public Tuple<string,string>[] Terms=new Tuple<string,string>[0];
            public string[] Example=new string[0];public string[] Tips=new string[0];
        }
        sealed class Choice {
            public string Title,Detail;
            public Choice(string title,string detail){Title=title;Detail=detail;}
        }
        sealed class Setting {
            public string Id,Title,SectionId;public string[] Lines=new string[0];public string[] Tips=new string[0];
            public Choice[] Choices=new Choice[0];public int Default=-1;public int Columns=1;
            public Func<Preferences,int> Get;public Action<Preferences,int> Set;
            // Shown as the current value when it isn't one of the choices (a number typed in an older version).
            public Func<Preferences,string> Custom;
            public Func<GuideWindow,FrameworkElement> Extra;
            // A setting that is a number: how it is read, written and named ("0.07 s"), and (Entry) the card to type one in.
            public Func<Preferences,int> Number;public Action<Preferences,int> SetNumber;public Func<int,string> NumberName;
            public NumberEntry Entry;
        }
        // The card under a number setting's choices: a box to type any value in, with − and + to step it by mouse.
        // Min, Max and Step are in the unit shown; Scale is how many stored units make one of them (1000 for a
        // setting kept in milliseconds and shown in seconds).
        sealed class NumberEntry {public string Unit;public double Min,Max,Step;public int Scale=1;}
        sealed class Section {public string Id,Name;public List<object> Items=new List<object>();}

        static string T(string en,string ko) {return L.Korean?ko:en;}
        static Tuple<string[],string> K(string what,params string[] keys) {return Tuple.Create(keys,what);}
        static Tuple<string,string> W(string word,string meaning) {return Tuple.Create(word,meaning);}

        static GuideWindow open;
        // One Guide & Options window at a time; asking again moves it to the requested section.
        // setting: the id of a setting on that page to scroll to.
        internal static void Open(Window owner,string section,string setting=null) {
            if(open!=null && open.IsLoaded) {
                open.Go(section,setting);
                if(open.WindowState==WindowState.Minimized)open.WindowState=WindowState.Normal;
                open.Activate();return;
            }
            open=new GuideWindow(owner,section);
            open.Closed+=delegate {open=null;};
            open.Show();
            if(setting!=null) {
                var shown=open;
                shown.Dispatcher.BeginInvoke(new Action(()=>{if(shown.IsLoaded)shown.Go(section,setting);}),DispatcherPriority.Loaded);
            }
        }
        internal static GuideWindow Current {get{return open;}}

        readonly ListBox contents=new ListBox();
        readonly StackPanel cards=new StackPanel {Margin=new Thickness(28,20,28,28),MaxWidth=680};
        readonly ScrollViewer scroller=new ScrollViewer {VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        readonly DispatcherTimer savedTimer=new DispatcherTimer {Interval=TimeSpan.FromSeconds(2.5)};
        List<Section> sections;
        List<Setting> settings;
        string savedId;
        TextBlock savedNote;
        bool rebuilding;

        GalleryWindow Gallery {get {return Application.Current.Windows.OfType<GalleryWindow>().FirstOrDefault();}}
        Preferences Prefs {get {var g=Gallery;return g!=null?g.Settings:PrefsForChecks??new Preferences();}}
        // The checks draw the pages without a folder window: the options they show.
        internal static Preferences PrefsForChecks;

        internal GuideWindow(Window owner,string start) {
            UI.WindowSetup(this,T("Guide & Options","설명서 겸 옵션"));
            if(owner!=null && owner.IsVisible){Owner=owner;WindowStartupLocation=WindowStartupLocation.CenterOwner;}
            ShowInTaskbar=owner==null;
            var area=SystemParameters.WorkArea;
            Width=Math.Min(980,area.Width);Height=Math.Min(760,area.Height);MinWidth=Math.Min(600,area.Width);MinHeight=Math.Min(440,area.Height);
            var root=new DockPanel();root.Children.Add(new WindowTitleBar(this));
            var side=new Border {Width=228,BorderThickness=new Thickness(0,0,1,0)};
            side.SetResourceReference(Border.BorderBrushProperty,"Line");side.SetResourceReference(Border.BackgroundProperty,"Panel");
            contents.BorderThickness=new Thickness(0);contents.Background=Brushes.Transparent;contents.Margin=new Thickness(8,12,8,12);
            contents.ItemContainerStyle=ContentsItem();
            ScrollViewer.SetHorizontalScrollBarVisibility(contents,ScrollBarVisibility.Disabled);
            side.Child=contents;DockPanel.SetDock(side,Dock.Left);root.Children.Add(side);
            scroller.Content=cards;scroller.SetResourceReference(Control.BackgroundProperty,"Paper");root.Children.Add(scroller);
            Content=root;
            contents.SelectionChanged+=delegate {if(!rebuilding)Show(contents.SelectedIndex,true);};
            PreviewKeyDown+=(s,e)=>{
                if(waitingFor!=null){TakeKey(e);return;}
                // Held Esc after cancelling a key: the repeats don't close the window.
                if(e.IsRepeat && e.Key==Key.Escape){e.Handled=true;return;}
                // Esc while "swap them?" is asked closes only that question.
                if(e.Key==Key.Escape && clashFor!=null){clashFor=null;clashWith=null;Redraw();e.Handled=true;return;}
                if(e.Key==Key.Escape || e.Key==Key.F1){Close();e.Handled=true;}
            };
            savedTimer.Tick+=delegate {savedTimer.Stop();savedId=null;if(savedNote!=null)savedNote.Visibility=Visibility.Collapsed;};
            Action changed=()=>Rebuild();
            var gallery=Gallery;
            if(gallery!=null){gallery.SettingsChangedEvent+=changed;Closed+=delegate {gallery.SettingsChangedEvent-=changed;};}
            Build();
            Go(start,null);
        }

        // Rebuilds the text (the language may have changed) and redraws the open page where it was.
        void Rebuild() {
            int index=Math.Max(0,contents.SelectedIndex);double offset=scroller.VerticalOffset;
            Title=T("Guide & Options","설명서 겸 옵션");
            Build();
            rebuilding=true;contents.SelectedIndex=index;rebuilding=false;
            Show(index,false);
            // Lay the redrawn page out now and go back to the same place (waiting for an idle moment can take long
            // while other windows animate).
            UpdateLayout();scroller.ScrollToVerticalOffset(offset);
        }
        void Build() {
            sections=BuildSections();
            settings=sections.SelectMany(s=>s.Items.OfType<Setting>()).ToList();
            rebuilding=true;contents.Items.Clear();
            foreach(var section in sections)contents.Items.Add(section.Name);
            rebuilding=false;
        }
        internal void Go(string sectionId,string settingId) {
            int index=Math.Max(0,sections.FindIndex(s=>s.Id==sectionId));
            rebuilding=true;contents.SelectedIndex=index;rebuilding=false;
            Show(index,true);
            if(settingId!=null) {
                // Lay the new page out first, so a card far down a long page can be reached.
                UpdateLayout();
                var target=cards.Children.OfType<FrameworkElement>().FirstOrDefault(c=>Equals(c.Tag,settingId));
                if(target!=null) {
                    var top=target.TranslatePoint(new Point(),cards).Y;
                    scroller.ScrollToVerticalOffset(Math.Max(0,top-12));
                }
            }
        }
        internal string SectionName {get {return contents.SelectedIndex>=0?sections[contents.SelectedIndex].Name:null;}}
        internal IEnumerable<string> SectionIds {get {return sections.Select(s=>s.Id);}}

        static Style ContentsItem() {
            var style=new Style(typeof(ListBoxItem));
            style.Setters.Add(new Setter(Control.FontSizeProperty,14.0));
            style.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(12,9,12,9)));
            style.Setters.Add(new Setter(Control.CursorProperty,Cursors.Hand));
            var border=new FrameworkElementFactory(typeof(Border));border.Name="Box";
            border.SetValue(Border.CornerRadiusProperty,new CornerRadius(8));
            border.SetValue(Border.PaddingProperty,new TemplateBindingExtension(Control.PaddingProperty));
            border.SetValue(Border.BackgroundProperty,Brushes.Transparent);
            var text=new FrameworkElementFactory(typeof(ContentPresenter));border.AppendChild(text);
            var template=new ControlTemplate(typeof(ListBoxItem)) {VisualTree=border};
            var hover=new Trigger {Property=UIElement.IsMouseOverProperty,Value=true};
            hover.Setters.Add(new Setter(Border.BackgroundProperty,new DynamicResourceExtension("Raised"),"Box"));
            var picked=new Trigger {Property=ListBoxItem.IsSelectedProperty,Value=true};
            picked.Setters.Add(new Setter(Border.BackgroundProperty,new DynamicResourceExtension("Raised"),"Box"));
            picked.Setters.Add(new Setter(Control.FontWeightProperty,FontWeights.SemiBold));
            template.Triggers.Add(hover);template.Triggers.Add(picked);
            style.Setters.Add(new Setter(Control.TemplateProperty,template));
            style.Setters.Add(new Setter(Control.ForegroundProperty,new DynamicResourceExtension("Text")));
            return style;
        }

        void Show(int index,bool toTop) {
            if(index<0 || index>=sections.Count)return;
            cards.Children.Clear();savedNote=null;
            var heading=UI.Text(sections[index].Name,24);heading.FontWeight=FontWeights.Bold;heading.Margin=new Thickness(2,0,0,14);
            cards.Children.Add(heading);
            foreach(var item in sections[index].Items) {
                var card=item as Card;
                cards.Children.Add(card!=null?Draw(card):DrawSetting((Setting)item));
            }
            if(sections[index].Id=="all")DrawOverview();
            if(sections[index].Id=="news")DrawNews();
            if(sections[index].Id=="backup")cards.Children.Add(BackupWindow.GuidePanel(this));
            if(sections[index].Id=="feedback")cards.Children.Add(AiWorkPanel.GuidePanel(this));
            if(sections[index].Id=="advanced")DrawAdvanced();
            else {waitingFor=null;clashFor=null;keyNote=null;}
            if(toTop)scroller.ScrollToTop();
        }
        // The version in use, then what each version brought, newest first.
        void DrawNews() {
            var now=Box(false);var top=new StackPanel();now.Child=top;
            var label=UI.Text(T("The version you're using now","지금 쓰고 있는 버전"),13,true);top.Children.Add(label);
            var number=UI.Text("LoveMyShelf "+AppInfo.Version,22);number.FontWeight=FontWeights.Bold;number.Margin=new Thickness(0,3,0,0);
            number.SetResourceReference(TextBlock.ForegroundProperty,"Accent");top.Children.Add(number);
            cards.Children.Add(now);
            foreach(var note in AppUpdater.InstalledNotes(NewsVersionForChecks??AppInfo.Version)) {
                var box=Box(false);var stack=new StackPanel();box.Child=stack;
                var version=UI.Text(T("Version ","버전 ")+note.Version,21);version.FontWeight=FontWeights.Bold;stack.Children.Add(version);
                string summary=T(note.SummaryEn,note.SummaryKo);
                if(summary.Length>0){var line=Rich(summary,16,false);line.Margin=new Thickness(0,6,0,0);stack.Children.Add(line);}
                UpdateNewsWindow.AddItems(stack,note);
                cards.Children.Add(box);
            }
        }
        // The checks use this to see a drafted version before it is released.
        internal static string NewsVersionForChecks;

        // ---------- Advanced: the large view's keys ----------
        // Only for the few who want it. The guide, the shortcut list, tooltips and notices keep the default keys;
        // the first card says so.
        ViewerKeys.Command waitingFor;            // the row waiting for a key to be pressed
        ViewerKeys.Command clashFor,clashWith;    // a key that is already used: the row, and the job that has the key
        Key clashKey;
        string keyNote;                           // a short line under the row waiting for a key (a key that can't be used)

        void DrawAdvanced() {
            var prefs=Prefs;
            var changed=prefs.Shortcuts;
            var intro=Box(false);var top=new StackPanel();intro.Child=top;
            top.Children.Add(Heading(T("Change the large view's shortcuts","크게 보기 단축키 바꾸기")));
            AddLines(top,new[]{
                T("You can change the keys used in the large view to the keys you like. Use it when you want them to match another program your hands are used to.",
                  "크게 보기에서 쓰는 단축키를 원하는 키로 바꿀 수 있어요. 손에 익은 다른 프로그램의 키와 맞추고 싶을 때 써 보세요."),
                T("The guide, All shortcuts and the button tooltips always show the keys LoveMyShelf started with. If you change a key here, press your new key instead of the one written there.",
                  "설명서, 단축키 모아 보기, 버튼 설명에는 언제나 처음 정해진 키로 적혀 있어요. 여기서 키를 바꿨다면 거기 적힌 키 대신 바꾼 키를 눌러 주세요."),
                T("Click a key on the right, then press the key you want to use.",
                  "오른쪽의 키를 누른 다음, 쓰고 싶은 키를 키보드에서 누르면 돼요.")},7);
            AddTips(top,new[]{
                T("A change applies right away, also to large views that are already open.","바꾼 키는 바로 적용돼요. 이미 열려 있는 크게 보기 창에도요."),
                T("{{Esc}} (close), {{F1}} (guide) and {{F11}} (full screen) can't be changed, so you can always find your way back.",
                  "{{Esc}}(닫기), {{F1}}(설명서), {{F11}}(전체 화면)은 바꿀 수 없어요. 어떻게 바꿔도 돌아갈 길이 남아 있게요."),
                T("Only single keys can be used, not keys pressed together with Ctrl, Alt or Shift.","Ctrl·Alt·Shift와 함께 누르는 키는 쓸 수 없고, 키 하나만 쓸 수 있어요."),
                T("If you get mixed up, [[Put all keys back]] at the bottom brings back the keys LoveMyShelf started with.",
                  "헷갈리면 맨 아래 [[모두 원래대로]]를 누르세요. 처음 정해진 키로 한 번에 돌아가요.")});
            cards.Children.Add(intro);

            var list=Box(true);list.Tag="keys";var rows=new StackPanel();list.Child=rows;
            var label=UI.Text(T("⚙  Set it right here","⚙  여기서 바로 설정해요"),12);label.FontWeight=FontWeights.SemiBold;
            label.SetResourceReference(TextBlock.ForegroundProperty,"Accent");label.Margin=new Thickness(0,0,0,4);
            rows.Children.Add(label);
            rows.Children.Add(Heading(T("Large view keys","크게 보기 키")));
            foreach(var command in ViewerKeys.All)rows.Children.Add(KeyRow(command,changed));
            var bottom=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(0,16,0,0)};
            var resetAll=UI.Btn(T("Put all keys back","모두 원래대로"),()=>{waitingFor=null;clashFor=null;keyNote=null;ChangeKeys(k=>k.Clear());});
            resetAll.FontSize=13;resetAll.IsEnabled=changed.Count>0;bottom.Children.Add(resetAll);
            var count=UI.Text(changed.Count>0?T(changed.Count+" changed",changed.Count+"개 바꿨어요"):T("All keys are the defaults now","지금은 모두 처음 키예요"),13,true);
            count.VerticalAlignment=VerticalAlignment.Center;count.Margin=new Thickness(12,0,0,0);bottom.Children.Add(count);
            rows.Children.Add(bottom);
            var saved=UI.Text(T("✓  Saved. It applies right away.","✓  저장됐어요. 바로 적용돼요."),13);
            saved.FontWeight=FontWeights.SemiBold;saved.SetResourceReference(TextBlock.ForegroundProperty,"Accent");
            saved.Margin=new Thickness(0,10,0,0);saved.Visibility=savedId=="keys"?Visibility.Visible:Visibility.Collapsed;
            if(savedId=="keys")savedNote=saved;
            rows.Children.Add(saved);
            cards.Children.Add(list);
        }
        FrameworkElement KeyRow(ViewerKeys.Command command,Dictionary<string,Key> changed) {
            var holder=new StackPanel {Margin=new Thickness(0,6,0,0)};
            var row=new DockPanel {LastChildFill=true};
            Key now=ViewerKeys.KeyOf(changed,command);
            bool isChanged=now!=command.Default,waiting=waitingFor==command;
            var right=new StackPanel {Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};
            if(isChanged && !waiting) {
                var back=UI.Btn(T("Reset","원래대로"),()=>{waitingFor=null;keyNote=null;Give(command,command.Default);});
                back.FontSize=12;back.Margin=new Thickness(0,0,8,0);
                back.ToolTip=T("Back to the first key: "+ViewerKeys.Label(command.Default),"처음 키로 되돌려요: "+ViewerKeys.Label(command.Default));
                right.Children.Add(back);
            }
            // The key itself is the button: drawn like a keyboard key, with the accent border when it was changed.
            var chip=new Button {Template=ChoiceTemplate(),Cursor=Cursors.Hand,MinWidth=96,Padding=new Thickness(10,5,10,6),
                BorderThickness=new Thickness(isChanged || waiting?2:1),HorizontalContentAlignment=HorizontalAlignment.Center};
            chip.SetResourceReference(Control.BackgroundProperty,waiting?"Raised":"Paper");
            chip.SetResourceReference(Control.BorderBrushProperty,isChanged || waiting?"Accent":"StrongLine");
            var chipText=UI.Text(waiting?T("Press a key…","키를 눌러 주세요…"):ViewerKeys.Label(now),13);
            chipText.FontWeight=FontWeights.SemiBold;chipText.HorizontalAlignment=HorizontalAlignment.Center;
            if(waiting || isChanged)chipText.SetResourceReference(TextBlock.ForegroundProperty,"Accent");
            chip.Content=chipText;
            System.Windows.Automation.AutomationProperties.SetName(chip,command.Name+": "+ViewerKeys.Label(now));
            chip.ToolTip=waiting?T("Press Esc to cancel","Esc를 누르면 취소돼요"):T("Click, then press the new key","누른 다음 새 키를 눌러 주세요");
            chip.Click+=delegate {
                clashFor=null;keyNote=null;
                waitingFor=waiting?null:command;
                Redraw();
            };
            right.Children.Add(chip);
            DockPanel.SetDock(right,Dock.Right);row.Children.Add(right);
            var name=UI.Text(command.Name,14);name.TextWrapping=TextWrapping.Wrap;name.VerticalAlignment=VerticalAlignment.Center;
            name.Margin=new Thickness(0,0,12,0);
            if(isChanged) {
                var both=new StackPanel {VerticalAlignment=VerticalAlignment.Center};both.Children.Add(name);
                var was=UI.Text(T("Default: ","처음 키: ")+ViewerKeys.Label(command.Default),12,true);was.Margin=new Thickness(0,1,12,0);
                both.Children.Add(was);row.Children.Add(both);
            } else row.Children.Add(name);
            holder.Children.Add(row);
            if(waiting) {
                var line=UI.Text(keyNote??T("Press the key you want to use. Esc cancels.","쓰고 싶은 키를 눌러 주세요. Esc를 누르면 취소돼요."),13,keyNote==null);
                line.TextWrapping=TextWrapping.Wrap;line.Margin=new Thickness(0,6,0,0);
                if(keyNote!=null)line.Foreground=new SolidColorBrush(NoticeLook.Amber);
                holder.Children.Add(line);
            }
            if(clashFor==command && clashWith!=null) {
                // The key is used by another job: swap the two, or keep things as they were.
                var box=new Border {CornerRadius=new CornerRadius(10),Padding=new Thickness(14,10,14,11),Margin=new Thickness(0,8,0,4),BorderThickness=new Thickness(1)};
                box.SetResourceReference(Border.BackgroundProperty,"Paper");box.BorderBrush=new SolidColorBrush(NoticeLook.Amber);
                var stack=new StackPanel();box.Child=stack;
                Key mine=ViewerKeys.KeyOf(changed,command);
                var other=clashWith;var key=clashKey;
                // The number pad's / is an extra key of Capture: taking it leaves Capture its own / key.
                Key theirs=ViewerKeys.KeyOf(changed,other);
                bool extra=theirs!=key;
                var text=UI.Text(extra?
                    T(ViewerKeys.Label(key)+" also works for “"+other.Name+"” now. Use it here instead? “"+other.Name+"” keeps "+ViewerKeys.Label(theirs)+".",
                      ViewerKeys.Label(key)+" 키는 지금 ‘"+other.Name+"’에도 쓰고 있어요. 이쪽으로 가져올까요? ‘"+other.Name+"’은 "+ViewerKeys.Label(theirs)+" 키로 계속 쓸 수 있어요."):
                    T(ViewerKeys.Label(key)+" is already used for “"+other.Name+"”. Swap them? Then “"+other.Name+"” moves to "+ViewerKeys.Label(mine)+".",
                      ViewerKeys.Label(key)+" 키는 지금 ‘"+other.Name+"’에 쓰고 있어요. 서로 맞바꿀까요? 맞바꾸면 ‘"+other.Name+"’에는 "+ViewerKeys.Label(mine)+" 키가 들어가요."),13);
                text.TextWrapping=TextWrapping.Wrap;stack.Children.Add(text);
                var buttons=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(0,8,0,0)};
                var swap=UI.Btn(extra?T("Use it here","가져오기"):T("Swap them","맞바꾸기"),()=>{
                    clashFor=null;clashWith=null;
                    ChangeKeys(k=>{if(!extra)ViewerKeys.Set(k,other,mine);ViewerKeys.Set(k,command,key);});
                },true);
                swap.FontSize=13;buttons.Children.Add(swap);
                var cancel=UI.Btn(T("Cancel","취소"),()=>{clashFor=null;clashWith=null;Redraw();});
                cancel.FontSize=13;cancel.Margin=new Thickness(6,0,0,0);buttons.Children.Add(cancel);
                stack.Children.Add(buttons);
                holder.Children.Add(box);
            }
            var line2=new Border {Height=1,Margin=new Thickness(0,6,0,0)};line2.SetResourceReference(Border.BackgroundProperty,"Line");
            holder.Children.Add(line2);
            return holder;
        }
        // A key pressed while a row waits for one.
        void TakeKey(KeyEventArgs e) {
            e.Handled=true;
            var key=e.Key==Key.ImeProcessed?e.ImeProcessedKey:e.Key==Key.System?e.SystemKey:e.Key;
            var command=waitingFor;
            switch(key) {
                case Key.LeftShift:case Key.RightShift:case Key.LeftCtrl:case Key.RightCtrl:case Key.LeftAlt:case Key.RightAlt:case Key.LWin:case Key.RWin:
                case Key.HangulMode:case Key.HanjaMode:case Key.CapsLock:case Key.NumLock:
                    return; // wait for the real key (switching 한/영 on the way is fine)
            }
            if(key==Key.Escape){waitingFor=null;keyNote=null;Redraw();return;}
            if(e.IsRepeat)return;
            if(Keyboard.Modifiers!=ModifierKeys.None) {
                keyNote=T("Keys pressed together with Ctrl, Alt or Shift can't be used. Press one key on its own.","Ctrl·Alt·Shift와 함께 누른 키는 쓸 수 없어요. 키 하나만 눌러 주세요.");
                Redraw();return;
            }
            if(!ViewerKeys.CanUse(key)) {
                keyNote=key==Key.F1 || key==Key.F11?T(ViewerKeys.Label(key)+" always opens the "+(key==Key.F1?"guide":"full screen")+", so it can't be used. Press another key.",
                        ViewerKeys.Label(key)+" 키는 언제나 "+(key==Key.F1?"설명서":"전체 화면")+"에 쓰여서 고를 수 없어요. 다른 키를 눌러 주세요."):
                    T("This key can't be used. Press another key.","이 키는 쓸 수 없어요. 다른 키를 눌러 주세요.");
                Redraw();return;
            }
            waitingFor=null;keyNote=null;
            Give(command,key);
        }
        // Gives a job a key, or first asks to swap when another job uses it (also when a row goes back to its first key).
        void Give(ViewerKeys.Command command,Key key) {
            clashFor=null;clashWith=null;
            var changed=Prefs.Shortcuts;
            if(key==ViewerKeys.KeyOf(changed,command)){Redraw();return;}
            var other=ViewerKeys.UsedBy(changed,key,command);
            if(other!=null){clashFor=command;clashWith=other;clashKey=key;Redraw();return;}
            ChangeKeys(k=>ViewerKeys.Set(k,command,key));
        }
        void ChangeKeys(Action<Dictionary<string,Key>> change) {
            var gallery=Gallery;
            if(gallery==null)return;
            gallery.TakeOverSettings(); // another folder window may have saved newer options since
            var updated=gallery.Settings.Copy();change(updated.Shortcuts);
            savedId="keys";savedTimer.Stop();savedTimer.Start();
            if(!gallery.ApplySettings(updated))savedId=null; // not saved: no "Saved" line
            Redraw(); // also when the folder window this page listens to is gone
        }
        // Draws the page again in the same place (a row started or stopped waiting for a key).
        void Redraw() {
            double offset=scroller.VerticalOffset;
            Show(Math.Max(0,contents.SelectedIndex),false);
            UpdateLayout();scroller.ScrollToVerticalOffset(offset);
        }

        // ---------- drawing ----------

        static TextBlock Rich(string text,double size,bool muted) {
            var block=UI.Text("",size,muted);block.TextWrapping=TextWrapping.Wrap;
            block.LineHeight=Math.Round(size*1.6);block.LineStackingStrategy=LineStackingStrategy.BlockLineHeight;
            int at=0;
            while(at<text.Length) {
                int button=text.IndexOf("[[",at,StringComparison.Ordinal),key=text.IndexOf("{{",at,StringComparison.Ordinal);
                int next=button<0?key:key<0?button:Math.Min(button,key);
                if(next<0){block.Inlines.Add(new Run(NoticeLook.KeepWords(text.Substring(at))));break;}
                if(next>at)block.Inlines.Add(new Run(NoticeLook.KeepWords(text.Substring(at,next-at))));
                bool isKey=next==key;
                int close=text.IndexOf(isKey?"}}":"]]",next+2,StringComparison.Ordinal);
                if(close<0){block.Inlines.Add(new Run(text.Substring(next)));break;}
                string label=text.Substring(next+2,close-next-2);
                var chip=isKey?KeyChip(label):ButtonChip(label,size);
                chip.Margin=new Thickness(1,0,1,0);
                block.Inlines.Add(new InlineUIContainer(chip) {BaselineAlignment=BaselineAlignment.Center});
                at=close+2;
            }
            return block;
        }
        // A button name drawn like the button itself, so people know what to look for on the screen.
        static Border ButtonChip(string label,double size) {
            var chip=new Border {CornerRadius=new CornerRadius(6),BorderThickness=new Thickness(1),Padding=new Thickness(7,1,7,1)};
            chip.SetResourceReference(Border.BackgroundProperty,"Panel");chip.SetResourceReference(Border.BorderBrushProperty,"StrongLine");
            var text=ChipWords(label,Math.Max(12,size-1));chip.Child=text;
            return chip;
        }
        // The words in a chip. Pretendard's own line is about 1.7 times the letters, which made a chip 26 tall in a
        // sentence line of 22, and the line shaved its border off. A line of 1.3 times keeps the chip inside the
        // sentence line (21 at most), so nothing is cut and the sentence spacing stays even.
        // gallery-checks\layout-check\run.ps1 fails when a chip is taller than its line again.
        static TextBlock ChipWords(string label,double size) {
            var text=UI.Text(label,size);text.FontWeight=FontWeights.SemiBold;
            text.LineHeight=Math.Round(size*1.3);text.LineStackingStrategy=LineStackingStrategy.BlockLineHeight;
            return text;
        }
        // A key drawn like a small rounded keyboard key.
        internal static Border KeyChip(string label) {
            var key=new Border {CornerRadius=new CornerRadius(6),BorderThickness=new Thickness(1,1,1,2),Padding=new Thickness(7,0,7,1),MinWidth=26};
            key.SetResourceReference(Border.BackgroundProperty,"Raised");key.SetResourceReference(Border.BorderBrushProperty,"StrongLine");
            var text=ChipWords(label,12);text.HorizontalAlignment=HorizontalAlignment.Center;
            key.Child=text;return key;
        }
        static Border Box(bool setting) {
            var box=new Border {CornerRadius=new CornerRadius(14),Padding=new Thickness(22,18,22,18),Margin=new Thickness(0,0,0,14),
                BorderThickness=new Thickness(setting?1.5:1)};
            box.SetResourceReference(Border.BackgroundProperty,"Panel");box.SetResourceReference(Border.BorderBrushProperty,setting?"Accent":"Line");
            return box;
        }
        static TextBlock Heading(string text) {
            var title=UI.Text(NoticeLook.KeepWords(text),17);title.FontWeight=FontWeights.SemiBold;title.TextWrapping=TextWrapping.Wrap;
            return title;
        }
        static void AddLines(StackPanel stack,string[] lines,double top) {
            foreach(var line in lines) {
                var text=Rich(line,14,false);text.Margin=new Thickness(0,top,0,0);stack.Children.Add(text);
            }
        }
        static void AddTips(StackPanel stack,string[] tips) {
            if(tips.Length==0)return;
            var box=new Border {CornerRadius=new CornerRadius(10),Padding=new Thickness(14,10,14,11),Margin=new Thickness(0,12,0,0)};
            box.SetResourceReference(Border.BackgroundProperty,"Paper");
            var list=new StackPanel();box.Child=list;
            foreach(var tip in tips) {
                var row=new DockPanel {Margin=new Thickness(0,2,0,2)};
                var dot=UI.Text("•",13,true);dot.VerticalAlignment=VerticalAlignment.Top;dot.Margin=new Thickness(0,0,8,0);
                DockPanel.SetDock(dot,Dock.Left);row.Children.Add(dot);row.Children.Add(Rich(tip,13,true));list.Children.Add(row);
            }
            stack.Children.Add(box);
        }

        static FrameworkElement Draw(Card card) {
            var box=Box(false);
            var stack=new StackPanel();box.Child=stack;
            stack.Children.Add(Heading(card.Title));
            AddLines(stack,card.Lines,7);
            for(int i=0;i<card.Steps.Length;i++) {
                var row=new DockPanel {Margin=new Thickness(0,9,0,0)};
                var number=new Border {Width=22,Height=22,CornerRadius=new CornerRadius(11),Margin=new Thickness(0,1,10,0),VerticalAlignment=VerticalAlignment.Top};
                number.SetResourceReference(Border.BackgroundProperty,"Raised");
                var digit=UI.Text((i+1).ToString(),12);digit.HorizontalAlignment=HorizontalAlignment.Center;digit.FontWeight=FontWeights.SemiBold;
                number.Child=digit;DockPanel.SetDock(number,Dock.Left);row.Children.Add(number);
                row.Children.Add(Rich(card.Steps[i],14,false));
                stack.Children.Add(row);
            }
            foreach(var pair in card.Keys) {
                var row=new DockPanel {Margin=new Thickness(0,9,0,0)};
                var keys=new StackPanel {Orientation=Orientation.Horizontal,MinWidth=170,VerticalAlignment=VerticalAlignment.Top};
                for(int i=0;i<pair.Item1.Length;i++) {
                    // "Ctrl+" then "A" draws Ctrl + A; other neighbours are alternatives shown side by side.
                    if(i>0){bool plus=pair.Item1[i-1].EndsWith("+");var gap=UI.Text(plus?"+":"",12,true);gap.Margin=new Thickness(plus?4:2,0,plus?4:2,0);keys.Children.Add(gap);}
                    keys.Children.Add(KeyChip(pair.Item1[i].TrimEnd('+')));
                }
                DockPanel.SetDock(keys,Dock.Left);row.Children.Add(keys);
                var text=Rich(pair.Item2,14,false);text.Margin=new Thickness(10,0,0,0);
                row.Children.Add(text);stack.Children.Add(row);
            }
            foreach(var term in card.Terms) {
                var row=new StackPanel {Margin=new Thickness(0,10,0,0)};
                var word=UI.Text(term.Item1,14);word.FontWeight=FontWeights.SemiBold;row.Children.Add(word);
                var meaning=Rich(term.Item2,14,true);meaning.Margin=new Thickness(0,2,0,0);row.Children.Add(meaning);
                stack.Children.Add(row);
            }
            if(card.Example.Length>0) {
                var example=new Border {CornerRadius=new CornerRadius(10),Padding=new Thickness(14,10,14,11),Margin=new Thickness(0,12,0,0)};
                example.SetResourceReference(Border.BackgroundProperty,"Paper");
                var lines=new StackPanel();example.Child=lines;
                foreach(var line in card.Example) {
                    var text=UI.Text(line,13,!line.StartsWith(" "));text.TextWrapping=TextWrapping.Wrap;text.Margin=new Thickness(0,1,0,1);
                    lines.Children.Add(text);
                }
                stack.Children.Add(example);
            }
            AddTips(stack,card.Tips);
            return box;
        }

        static ControlTemplate choiceTemplate;
        static ControlTemplate ChoiceTemplate() {
            if(choiceTemplate!=null)return choiceTemplate;
            var border=new FrameworkElementFactory(typeof(Border));border.Name="Box";
            border.SetValue(Border.CornerRadiusProperty,new CornerRadius(10));
            border.SetValue(Border.PaddingProperty,new TemplateBindingExtension(Control.PaddingProperty));
            border.SetValue(Border.BackgroundProperty,new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty,new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty,new TemplateBindingExtension(Control.BorderThicknessProperty));
            var content=new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Stretch);
            border.AppendChild(content);
            choiceTemplate=new ControlTemplate(typeof(Button)) {VisualTree=border};
            var hover=new Trigger {Property=UIElement.IsMouseOverProperty,Value=true};
            hover.Setters.Add(new Setter(Border.BackgroundProperty,new DynamicResourceExtension("Raised"),"Box"));
            var focus=new Trigger {Property=UIElement.IsKeyboardFocusedProperty,Value=true};
            focus.Setters.Add(new Setter(Border.BorderBrushProperty,new DynamicResourceExtension("FocusLine"),"Box"));
            choiceTemplate.Triggers.Add(hover);choiceTemplate.Triggers.Add(focus);
            return choiceTemplate;
        }

        FrameworkElement DrawSetting(Setting setting) {
            var box=Box(true);box.Tag=setting.Id;
            var stack=new StackPanel();box.Child=stack;
            var label=UI.Text(T("⚙  Set it right here","⚙  여기서 바로 설정해요"),12);label.FontWeight=FontWeights.SemiBold;
            label.SetResourceReference(TextBlock.ForegroundProperty,"Accent");label.Margin=new Thickness(0,0,0,4);
            stack.Children.Add(label);
            stack.Children.Add(Heading(setting.Title));
            AddLines(stack,setting.Lines,7);
            var prefs=Prefs;
            int current=setting.Get(prefs);
            Panel list=setting.Columns>1?(Panel)new UniformGrid {Columns=setting.Columns}:new StackPanel();
            list.Margin=new Thickness(0,10,0,0);
            for(int i=0;i<setting.Choices.Length;i++)list.Children.Add(ChoiceTile(setting,i,i==current));
            if(current<0 && setting.Custom!=null && setting.Entry==null)list.Children.Add(CustomTile(setting.Custom(prefs)));
            stack.Children.Add(list);
            if(setting.Entry!=null)stack.Children.Add(EntryTile(setting,current<0));
            if(setting.Extra!=null)stack.Children.Add(setting.Extra(this));
            AddTips(stack,setting.Tips);
            var saved=UI.Text(T("✓  Saved. It applies right away.","✓  저장됐어요. 바로 적용돼요."),13);
            saved.FontWeight=FontWeights.SemiBold;saved.SetResourceReference(TextBlock.ForegroundProperty,"Accent");
            saved.Margin=new Thickness(0,10,0,0);saved.Visibility=savedId==setting.Id?Visibility.Visible:Visibility.Collapsed;
            if(savedId==setting.Id)savedNote=saved;
            stack.Children.Add(saved);
            return box;
        }
        Button ChoiceTile(Setting setting,int index,bool chosen) {
            var choice=setting.Choices[index];
            var tile=new Button {Template=ChoiceTemplate(),HorizontalContentAlignment=HorizontalAlignment.Stretch,
                Margin=new Thickness(0,0,setting.Columns>1?8:0,8),Cursor=Cursors.Hand,
                BorderThickness=new Thickness(chosen?2:1),Padding=chosen?new Thickness(13,9,13,10):new Thickness(14,10,14,11)};
            tile.SetResourceReference(Control.BackgroundProperty,"Paper");
            tile.SetResourceReference(Control.BorderBrushProperty,chosen?"Accent":"Line");
            var row=new DockPanel();
            if(index==setting.Default) {
                var badge=new Border {CornerRadius=new CornerRadius(6),Padding=new Thickness(7,1,7,2),Margin=new Thickness(8,0,0,0),
                    VerticalAlignment=VerticalAlignment.Top};
                badge.SetResourceReference(Border.BackgroundProperty,"Raised");
                badge.Child=UI.Text(T("Default","기본값"),11,true);
                DockPanel.SetDock(badge,Dock.Right);row.Children.Add(badge);
            }
            var text=new StackPanel();
            var title=UI.Text((chosen?"✓  ":"")+NoticeLook.KeepWords(choice.Title),14);title.FontWeight=FontWeights.SemiBold;title.TextWrapping=TextWrapping.Wrap;
            if(chosen)title.SetResourceReference(TextBlock.ForegroundProperty,"Accent");
            text.Children.Add(title);
            if(!String.IsNullOrEmpty(choice.Detail))
                foreach(var part in choice.Detail.Split('\n')) {
                    var detail=Rich(part,13,true);detail.Margin=new Thickness(0,3,0,0);text.Children.Add(detail);
                }
            row.Children.Add(text);tile.Content=row;
            System.Windows.Automation.AutomationProperties.SetName(tile,choice.Title);
            System.Windows.Automation.AutomationProperties.SetItemStatus(tile,chosen?"chosen":"");
            tile.Click+=delegate {Choose(setting,index);};
            return tile;
        }
        // Said in the card once, right after a typed number had to be brought into range.
        string entryNoteId,entryNote;
        static ControlTemplate entryBoxTemplate;
        // A rounded box in the look of the choice tiles; its border takes the accent color while it is typed in.
        static ControlTemplate EntryBoxTemplate() {
            if(entryBoxTemplate!=null)return entryBoxTemplate;
            var border=new FrameworkElementFactory(typeof(Border));border.Name="Box";
            border.SetValue(Border.CornerRadiusProperty,new CornerRadius(8));
            border.SetValue(Border.BorderThicknessProperty,new Thickness(1));
            border.SetValue(Border.BackgroundProperty,new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty,new TemplateBindingExtension(Control.BorderBrushProperty));
            var host=new FrameworkElementFactory(typeof(ScrollViewer));host.Name="PART_ContentHost";
            host.SetValue(FrameworkElement.MarginProperty,new TemplateBindingExtension(Control.PaddingProperty));
            host.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);
            border.AppendChild(host);
            entryBoxTemplate=new ControlTemplate(typeof(TextBox)) {VisualTree=border};
            var focus=new Trigger {Property=UIElement.IsKeyboardFocusedProperty,Value=true};
            focus.Setters.Add(new Setter(Border.BorderBrushProperty,new DynamicResourceExtension("Accent"),"Box"));
            entryBoxTemplate.Triggers.Add(focus);
            return entryBoxTemplate;
        }
        // "Type your own": the last card of a number setting. chosen: the value in use is not one of the listed choices.
        FrameworkElement EntryTile(Setting setting,bool chosen) {
            var entry=setting.Entry;
            var name=setting.NumberName;
            int stored=setting.Number(Prefs);
            Func<double,string> text=v=>v.ToString("0.###",System.Globalization.CultureInfo.CurrentCulture);
            Func<double,int> toStored=v=>(int)Math.Round(v*entry.Scale);
            double local=stored/(double)entry.Scale; // what the box shows while − or + is held, before it is saved
            string own=T("Type your own","직접 입력");
            var tile=new Border {CornerRadius=new CornerRadius(10),BorderThickness=new Thickness(chosen?2:1),
                Padding=chosen?new Thickness(13,9,13,10):new Thickness(14,10,14,11),Margin=new Thickness(0,0,0,8)};
            tile.SetResourceReference(Border.BackgroundProperty,"Paper");tile.SetResourceReference(Border.BorderBrushProperty,chosen?"Accent":"Line");
            var stack=new StackPanel();tile.Child=stack;
            var title=UI.Text(chosen?"✓  "+own+"  ·  "+T("now ","지금 ")+name(stored):own,14);
            title.FontWeight=FontWeights.SemiBold;
            if(chosen)title.SetResourceReference(TextBlock.ForegroundProperty,"Accent");
            stack.Children.Add(title);
            var row=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(0,8,0,0)};
            var box=new TextBox {Template=EntryBoxTemplate(),Width=88,MinHeight=34,Padding=new Thickness(8,0,8,0),FontSize=15,FontWeight=FontWeights.SemiBold,
                TextAlignment=TextAlignment.Center,VerticalContentAlignment=VerticalAlignment.Center,Text=text(local)};
            box.SetResourceReference(Control.BackgroundProperty,"Panel");box.SetResourceReference(Control.BorderBrushProperty,"Line");
            System.Windows.Automation.AutomationProperties.SetName(box,own);
            string lowest=name(toStored(entry.Min)),highest=name(toStored(entry.Max));
            string range=T("You can enter from "+lowest+" to "+highest+".",lowest+"부터 "+highest+"까지 넣을 수 있어요.");
            var hint=UI.Text(entryNoteId==setting.Id?entryNote:range,13,true);hint.TextWrapping=TextWrapping.Wrap;hint.Margin=new Thickness(0,8,0,0);
            entryNoteId=null; // said once
            Action<double> commit=value=>{
                int next=toStored(Math.Max(entry.Min,Math.Min(entry.Max,value)));
                string note=value>entry.Max+1e-9?T("Nothing bigger than "+name(next)+" can be entered, so it was set to "+name(next)+".",name(next)+"보다 큰 값은 넣을 수 없어서 "+name(next)+"에 맞췄어요."):
                    value<entry.Min-1e-9?T("Nothing smaller than "+name(next)+" can be entered, so it was set to "+name(next)+".",name(next)+"보다 작은 값은 넣을 수 없어서 "+name(next)+"에 맞췄어요."):null;
                local=next/(double)entry.Scale;box.Text=text(local);
                hint.Text=note??range;hint.SetResourceReference(TextBlock.ForegroundProperty,"Subtle");
                if(next==setting.Number(Prefs))return;
                if(note!=null){entryNoteId=setting.Id;entryNote=note;}
                // Saving redraws the page, so this card is replaced by a new one showing the saved value.
                Apply(setting,p=>setting.SetNumber(p,next));
            };
            Action apply=()=>{
                double value;
                string typed=box.Text.Trim().Replace(',','.');
                if(!Double.TryParse(typed,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out value) || Double.IsNaN(value) || Double.IsInfinity(value)) {
                    hint.Text=T("Enter numbers only.","숫자만 넣어 주세요.");hint.Foreground=new SolidColorBrush(NoticeLook.Amber);return;
                }
                commit(value);
            };
            box.KeyDown+=(s,e)=>{if(e.Key==Key.Enter){apply();e.Handled=true;}};
            // − and + move to the next step on the grid (0.07 → 0.08; 20 MB → 25 MB) and stay inside the range.
            Func<int,double> stepped=direction=>{
                double at=local/entry.Step;
                double next=direction>0?Math.Floor(at+1e-6)+1:Math.Ceiling(at-1e-6)-1;
                return Math.Max(entry.Min,Math.Min(entry.Max,Math.Round(next*entry.Step,6)));
            };
            var hold=new DispatcherTimer();
            Func<string,int,string,Button> stepper=(glyph,direction,label)=>{
                var button=new Button {Content=glyph,Width=34,MinHeight=34,MinWidth=0,Padding=new Thickness(0),FontSize=17};
                System.Windows.Automation.AutomationProperties.SetName(button,label);button.ToolTip=label;
                bool byMouse=false,pending=false;
                // Held with the mouse: the number keeps going in the box and is saved once, when the button is let go.
                button.PreviewMouseLeftButtonDown+=delegate {
                    byMouse=true;pending=true;
                    local=stepped(direction);box.Text=text(local);
                    hold.Stop();hold.Interval=TimeSpan.FromMilliseconds(420);
                    hold.Tag=(Action)(()=>{if(button.IsPressed){local=stepped(direction);box.Text=text(local);}});
                    hold.Start();
                };
                button.LostMouseCapture+=delegate {
                    hold.Stop();
                    if(pending){pending=false;commit(local);}
                };
                // The keyboard (Space or Enter on the button) steps once.
                button.Click+=delegate {
                    if(byMouse){byMouse=false;return;}
                    commit(stepped(direction));
                };
                return button;
            };
            hold.Tick+=delegate {
                hold.Interval=TimeSpan.FromMilliseconds(90);
                var act=hold.Tag as Action;
                if(act!=null)act();
            };
            tile.Unloaded+=delegate {hold.Stop();};
            var minus=stepper("−",-1,T("One step smaller","한 단계 줄이기"));
            var plus=stepper("+",1,T("One step bigger","한 단계 늘리기"));
            box.Margin=new Thickness(6,0,0,0);plus.Margin=new Thickness(6,0,0,0);
            row.Children.Add(minus);row.Children.Add(box);
            if(!String.IsNullOrEmpty(entry.Unit)) {
                var unit=UI.Text(entry.Unit,14,true);unit.Margin=new Thickness(6,0,0,0);row.Children.Add(unit);
            }
            row.Children.Add(plus);
            var ok=UI.Btn(T("Apply","적용"),apply);ok.MinHeight=34;ok.Margin=new Thickness(12,0,0,0);row.Children.Add(ok);
            stack.Children.Add(row);stack.Children.Add(hint);
            return tile;
        }
        bool Apply(Setting setting,Action<Preferences> change) {
            var gallery=Gallery;
            if(gallery==null)return false;
            var updated=gallery.Settings.Copy();change(updated);
            savedId=setting.Id;savedTimer.Stop();savedTimer.Start();
            return gallery.ApplySettings(updated);
        }
        static Border CustomTile(string text) {
            var tile=new Border {CornerRadius=new CornerRadius(10),BorderThickness=new Thickness(2),Padding=new Thickness(13,9,13,10),Margin=new Thickness(0,0,0,8)};
            tile.SetResourceReference(Border.BorderBrushProperty,"Accent");tile.SetResourceReference(Border.BackgroundProperty,"Paper");
            var title=UI.Text("✓  "+text,14);title.FontWeight=FontWeights.SemiBold;title.SetResourceReference(TextBlock.ForegroundProperty,"Accent");
            tile.Child=title;return tile;
        }
        // Saves the choice at once. The main window applies it and asks this window to redraw.
        internal bool Choose(string settingId,int index) {
            var setting=settings.FirstOrDefault(s=>s.Id==settingId);
            return setting!=null && Choose(setting,index);
        }
        bool Choose(Setting setting,int index) {
            var gallery=Gallery;
            if(gallery==null)return false;
            var updated=gallery.Settings.Copy();setting.Set(updated,index);
            savedId=setting.Id;savedTimer.Stop();savedTimer.Start();
            return gallery.ApplySettings(updated);
        }
        internal int Chosen(string settingId) {
            var setting=settings.First(s=>s.Id==settingId);return setting.Get(Prefs);
        }
        internal IEnumerable<string> SettingIds {get {return settings.Select(s=>s.Id);}}

        // "All settings at a glance": every setting, what it is now, and a button to its explanation.
        void DrawOverview() {
            var prefs=Prefs;
            foreach(var group in sections.Where(s=>s.Id!="all" && s.Items.OfType<Setting>().Any())) {
                var box=Box(false);var stack=new StackPanel();box.Child=stack;
                stack.Children.Add(Heading(group.Name));
                foreach(var setting in group.Items.OfType<Setting>()) {
                    var row=new DockPanel {Margin=new Thickness(0,10,0,0)};
                    var target=setting;var sectionId=group.Id;
                    var go=UI.Btn(T("Go to change it","바꾸러 가기")+"  ›",()=>Go(sectionId,target.Id));
                    go.VerticalAlignment=VerticalAlignment.Center;go.Margin=new Thickness(10,0,0,0);
                    DockPanel.SetDock(go,Dock.Right);row.Children.Add(go);
                    var text=new StackPanel();
                    var name=Rich(setting.Title,14,false);text.Children.Add(name);
                    int value=setting.Get(prefs);
                    string now=value>=0 && value<setting.Choices.Length?setting.Choices[value].Title:setting.Custom!=null?setting.Custom(prefs):"";
                    if(setting.Id=="folder")now=String.IsNullOrEmpty(prefs.CoverDirectory)?T("Not chosen yet","아직 정하지 않았어요"):prefs.CoverDirectory;
                    if(setting.Id=="capture")now=prefs.CaptureFolder;
                    var current=UI.Text(T("Now: ","지금: ")+now,13);current.TextWrapping=TextWrapping.Wrap;
                    current.SetResourceReference(TextBlock.ForegroundProperty,"Accent");current.Margin=new Thickness(0,2,0,0);
                    text.Children.Add(current);row.Children.Add(text);
                    stack.Children.Add(row);
                }
                cards.Children.Add(box);
            }
        }

        FrameworkElement CoverFolderPicker() {
            var panel=new StackPanel {Margin=new Thickness(0,4,0,0)};
            string folder=Prefs.CoverDirectory;
            var path=new TextBox {Text=String.IsNullOrEmpty(folder)?T("Not chosen yet","아직 정하지 않았어요"):folder,IsReadOnly=true,Padding=new Thickness(8,6,8,6),FontSize=13};
            System.Windows.Automation.AutomationProperties.SetName(path,T("Cover folder","표지 폴더"));
            panel.Children.Add(path);
            var buttons=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(0,8,0,0)};
            var pick=UI.Btn(T("Select another folder…","다른 폴더 선택…"),()=>{
                var gallery=Gallery;if(gallery==null)return;
                string chosen=UI.PickFolder(this,L.T("Choose the folder where cover files will be saved."),
                    System.IO.Directory.Exists(folder)?folder:Preferences.SuggestedCoverParent,true);
                if(chosen==null)return;
                var updated=gallery.Settings.Copy();updated.CoverDirectory=chosen;
                savedId="folder";savedTimer.Stop();savedTimer.Start();
                gallery.ApplySettings(updated);
            },true);
            pick.FontSize=13;buttons.Children.Add(pick);
            var show=UI.Btn(T("Open this folder","이 폴더 열기"),()=>{
                try{if(System.IO.Directory.Exists(folder))System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) {UseShellExecute=true});}
                catch(Exception ex){UI.Error(this,ex);}
            });
            show.FontSize=13;show.Margin=new Thickness(6,0,0,0);show.IsEnabled=System.IO.Directory.Exists(folder);buttons.Children.Add(show);
            panel.Children.Add(buttons);
            return panel;
        }

        FrameworkElement CaptureFolderPicker() {
            var panel=new StackPanel {Margin=new Thickness(0,4,0,0)};
            string folder=Prefs.CaptureFolder;
            var path=new TextBox {Text=folder,IsReadOnly=true,Padding=new Thickness(8,6,8,6),FontSize=13};
            System.Windows.Automation.AutomationProperties.SetName(path,T("Folder for captured pictures","캡처한 사진을 저장할 폴더"));
            panel.Children.Add(path);
            var buttons=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(0,8,0,0)};
            Action<string> use=chosen=>{
                var gallery=Gallery;if(gallery==null)return;
                var updated=gallery.Settings.Copy();updated.CaptureDirectory=chosen;
                savedId="capture";savedTimer.Stop();savedTimer.Start();
                gallery.ApplySettings(updated);
            };
            var pick=UI.Btn(T("Select another folder…","다른 폴더 선택…"),()=>{
                string chosen=UI.PickFolder(this,T("Choose the folder where captured pictures will be saved.","캡처한 사진을 저장할 폴더를 골라 주세요."),
                    System.IO.Directory.Exists(folder)?folder:Preferences.SuggestedCoverParent,true);
                if(chosen!=null)use(chosen);
            },true);
            pick.FontSize=13;buttons.Children.Add(pick);
            var show=UI.Btn(T("Open this folder","이 폴더 열기"),()=>{
                try{
                    System.IO.Directory.CreateDirectory(folder);
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) {UseShellExecute=true});
                }catch(Exception ex){UI.Error(this,ex);}
            });
            show.FontSize=13;show.Margin=new Thickness(6,0,0,0);buttons.Children.Add(show);
            if(!String.IsNullOrWhiteSpace(Prefs.CaptureDirectory)) {
                var reset=UI.Btn(T("Back to the default folder","기본 폴더로 되돌리기"),()=>use(""));
                reset.FontSize=13;reset.Margin=new Thickness(6,0,0,0);buttons.Children.Add(reset);
            }
            panel.Children.Add(buttons);
            return panel;
        }

        // ---------- the words ----------

        static Setting Numbers(string id,string title,int[] values,int def,Func<Preferences,int> get,Action<Preferences,int> set,
            Func<int,string> name,Func<int,string> detail,int columns) {
            return new Setting {Id=id,Title=title,Default=Array.IndexOf(values,def),Columns=columns,
                Number=get,SetNumber=set,NumberName=name,
                Choices=values.Select(v=>new Choice(name(v),detail(v))).ToArray(),
                Get=p=>Array.IndexOf(values,get(p)),Set=(p,i)=>set(p,values[i]),
                Custom=p=>T("Now ","지금 ")+name(get(p))+T(" (a value you typed in earlier)"," (예전에 직접 입력한 값)")};
        }

        // Gives a number setting its "Type your own" card. The range and step are in the unit shown beside the box.
        static Setting Typed(Setting setting,string unit,double min,double max,double step,int scale=1) {
            setting.Entry=new NumberEntry {Unit=unit,Min=min,Max=max,Step=step,Scale=scale};
            var name=setting.NumberName;var get=setting.Number;
            setting.Custom=p=>name(get(p))+T(" (typed in)"," (직접 입력한 값)");
            return setting;
        }

        // One setting per connected monitor: which speaker its videos play on.
        static void AddSpeakerSettings(Section large) {
            var speakers=MpvPlayer.Speakers();
            if(speakers.Count==0)return;
            large.Items.Add(new Card {Title=T("A speaker for each monitor","모니터마다 스피커 정하기"),
                Lines=new[]{T("You can choose which speaker each monitor's sound comes from. For example, set the left monitor to a headset and the right monitor to speakers, and you can listen to two videos in different places.",
                    "모니터마다 소리가 나올 스피커를 정할 수 있어요. 예를 들어 왼쪽 모니터는 헤드셋, 오른쪽 모니터는 스피커로 정해 두면 영상 두 개를 각각 다른 곳에서 들을 수 있어요."),
                    T("Drag a video window to another monitor and, about a second later, its sound moves to that monitor's speaker too. The volume is remembered separately for each speaker.",
                    "영상 창을 다른 모니터로 끌어서 옮기면 1초쯤 뒤에 소리도 그 모니터의 스피커로 따라가요. 볼륨은 스피커마다 따로 기억해요.")},
                Tips=new[]{T("If the chosen speaker is off or unplugged, the sound comes from the Windows default speaker. Connect it again and the sound goes back to it.",
                    "정해 둔 스피커가 꺼져 있거나 빠져 있으면 Windows 기본 스피커로 소리가 나와요. 다시 연결하면 정해 둔 스피커로 돌아가요."),
                    T("In rare cases a video opens with the spare Windows player, and then its sound comes only from the Windows default speaker.",
                    "아주 드물게 영상이 Windows 예비 재생기로 열리면, 그 영상의 소리는 Windows 기본 스피커로만 나와요.")}});
            var choices=new[]{new Choice(T("Windows default speaker","Windows 기본 스피커"),T("The sound comes from the speaker set as the default in Windows.","Windows에서 기본으로 정한 스피커로 소리가 나와요."))}
                .Concat(speakers.Select(s=>new Choice(s.Item2,""))).ToArray();
            var screens=SpeakerRouting.Screens();
            for(int n=0;n<screens.Count;n++) {
                string key=screens[n].Key;
                large.Items.Add(new Setting {Id="speaker"+n,SectionId="large",
                    Title=T("Where should the sound of the "+screens[n].Name+" go?",screens[n].Name+"의 소리는 어디로 내보낼까요?"),
                    Choices=choices,Default=0,
                    Get=p=>{
                        SpeakerChoice chosen;
                        if(!p.MonitorSpeakers.TryGetValue(key,out chosen))return 0;
                        int at=speakers.FindIndex(s=>s.Item1==chosen.Device);
                        return at<0?-1:at+1;
                    },
                    Set=(p,i)=>{
                        if(i==0)p.MonitorSpeakers.Remove(key);
                        else p.MonitorSpeakers[key]=new SpeakerChoice {Device=speakers[i-1].Item1,Name=speakers[i-1].Item2};
                    },
                    Custom=p=>{
                        SpeakerChoice chosen;
                        string name=p.MonitorSpeakers.TryGetValue(key,out chosen)?chosen.Name:"";
                        return name+T(" (not connected now, so the Windows default speaker plays)"," (지금은 연결되어 있지 않아서 Windows 기본 스피커로 나와요)");
                    }});
            }
        }

        List<Section> BuildSections() {
            var list=new List<Section>();
            Func<string,string,Section> add=(id,name)=>{var s=new Section {Id=id,Name=name};list.Add(s);return s;};

            // ----- Getting started -----
            var start=add("start",T("Getting started","처음 시작하기"));
            start.Items.Add(new Card {Title=T("What kind of program LoveMyShelf is","LoveMyShelf는 이런 프로그램이에요"),
                Lines=new[]{
                    T("LoveMyShelf lays out the photos, GIFs and videos in a folder as small pictures in a grid. You can see at a glance what is in the folder without opening the files one by one.",
                      "폴더 안의 사진·GIF·영상을 작은 그림으로 바둑판처럼 펼쳐 보여 주는 프로그램이에요. 파일을 하나하나 열어 보지 않아도 폴더에 뭐가 있는지 한눈에 볼 수 있어요."),
                    T("You can make a cover for any file. A cover is a picture cut from the part of the file you like, or one scene if it is a video, in any shape: tall, square or wide. Once you make covers, the tiles show them, so files that looked alike are easy to tell apart.",
                      "어떤 파일이든 표지를 만들어 둘 수 있어요. 표지는 파일에서 마음에 드는 부분(영상이면 한 장면)만 골라 잘라 낸 그림이고, 세로·정사각형·가로 어떤 모양이든 돼요. 표지를 만들어 두면 칸에 표지가 보여서, 비슷비슷하던 파일도 쉽게 구별돼요."),
                    T("The photo or video a cover is made from is called the original. LoveMyShelf never changes or deletes the original. A cover is always saved as a new, separate file.",
                      "표지를 만들 때 쓴 원래 사진이나 영상을 원본이라고 불러요. LoveMyShelf는 원본을 절대 바꾸거나 지우지 않아요. 표지는 언제나 새 파일로 따로 저장돼요."),
                    T("It doesn't upload your photos or videos anywhere on the internet. All the work happens only inside your computer.",
                      "사진이나 영상을 인터넷 어디에도 올리지 않아요. 모든 작업은 내 컴퓨터 안에서만 이루어져요.")}});
            start.Items.Add(new Card {Title=T("Your first cover in 4 steps","4단계로 첫 표지 만들기"),
                Steps=new[]{
                    T("In the folder tree on the left, right-click a folder that has photos or videos and click [[Add to LoveMyShelf]] (it is like ‘Pin to Quick access’ in File Explorer). You can also drag the folder onto LoveMyShelf in the tree, or drag it from File Explorer onto this window.",
                      "왼쪽 폴더 트리에서 사진이나 영상이 들어 있는 폴더를 오른쪽 클릭하고 [[LoveMyShelf에 추가]]를 눌러요(파일 탐색기의 ‘즐겨찾기에 고정’과 비슷해요). 폴더를 트리의 LoveMyShelf 위로 끌어다 놓거나, 파일 탐색기에서 이 창으로 끌어다 놓아도 돼요."),
                    T("Photos and videos appear as small pictures (tiles) in a grid. Click a photo you like once. For a video, play it and click [[Pause]] near the scene you like.",
                      "사진과 영상이 작은 그림(칸)으로 바둑판처럼 나타나요. 마음에 드는 사진을 한 번 클릭해요. 영상이면 재생하다가 마음에 드는 장면 근처에서 [[일시정지]]를 눌러요."),
                    T("Click [[Make cover]]. Drag the picture with the mouse to set its position, and roll the wheel to set its size.",
                      "[[표지 만들기]]를 눌러요. 사진을 마우스로 끌어서 위치를 맞추고, 휠을 굴려서 크기를 맞춰요."),
                    T("Click [[Save cover]] and you're done. The cover you made opens large right away.",
                      "[[표지 저장]]을 누르면 끝이에요. 만든 표지가 바로 크게 열려요.")}});
            start.Items.Add(new Card {Title=T("How to use this window","이 창 사용법"),
                Lines=new[]{
                    T("Click an item you're curious about in the list on the left, and its explanation appears on the right.","왼쪽 목록에서 궁금한 항목을 누르면 오른쪽에 설명이 나와요."),
                    T("A box with a colored border that says ‘Set it right here’ is a setting. The moment you choose, it is saved and applied right away. There is no need to press a separate Save button.",
                      "색 테두리에 ‘여기서 바로 설정해요’라고 적힌 상자는 설정이에요. 고르는 순간 저장되고 바로 적용돼요. 저장 버튼을 따로 누를 필요가 없어요."),
                    T("The one marked ‘Default’ is the setting that was chosen from the start. If you're not sure, just leave it at the default.",
                      "‘기본값’이라고 적힌 것은 처음부터 골라져 있던 설정이에요. 잘 모르겠으면 기본값 그대로 두면 돼요."),
                    T("To see every setting in one place, click [[All settings at a glance]] in the list on the left.",
                      "모든 설정을 한곳에서 보려면 왼쪽 목록의 [[설정 한눈에 보기]]를 눌러요."),
                    T("On any screen, press {{F1}} and the explanation for the screen you're looking at opens right away.",
                      "어느 화면에서든 {{F1}}을 누르면 지금 보고 있는 화면에 맞는 설명이 바로 열려요.")}});

            add("all",T("All settings at a glance","설정 한눈에 보기"));

            // ----- Browsing -----
            var browse=add("browse",T("Browsing files","파일 둘러보기"));
            browse.Items.Add(new Card {Title=T("Open a folder and the tiles appear","폴더를 열면 칸이 나타나요"),
                Lines=new[]{
                    T("A folder's photos and videos line up as tiles in a grid. The top row is the same as File Explorer: ← back, → forward, ↑ the folder above and ⟳ refresh, and the address bar shows where you are now.",
                      "폴더의 사진과 영상이 칸으로 바둑판처럼 늘어서요. 위쪽 줄은 파일 탐색기와 같아요. ← 뒤로, → 앞으로, ↑ 위 폴더, ⟳ 새로 고침이 있고, 주소창에는 지금 위치가 보여요."),
                    T("In the address bar, click a folder name to go to that folder, or click the › after a name to see the list of folders inside it. Click the empty part of the address bar (or press {{Ctrl}} + {{L}}) to type or paste a path; folder names are suggested as you type. {{Enter}} goes there and {{Esc}} cancels.",
                      "주소창에서 폴더 이름을 누르면 그 폴더로 가고, 이름 뒤의 ›를 누르면 그 안의 폴더 목록이 나와요. 주소창의 빈 곳을 누르면(또는 {{Ctrl}} + {{L}}) 경로를 직접 입력하거나 붙여 넣을 수 있고, 입력하는 동안 폴더 이름이 자동으로 추천돼요. {{Enter}}를 누르면 이동하고 {{Esc}}를 누르면 취소돼요."),
                    T("Point the mouse at a tile and the whole file name appears right below it (above it on the bottom row). It doesn't cover the tile you're pointing at.",
                      "칸에 마우스를 올리면 그 칸 바로 아래(맨 아랫줄이면 위)에 파일 이름 전체가 나와요. 마우스를 올린 칸은 가리지 않아요."),
                    T("A file with a cover shows the cover in its tile.","표지를 만든 파일은 칸에 표지가 보여요."),
                    T("At the bottom left, [[All]] shows every tile, [[Finished]] shows only tiles with a cover, and [[Unfinished]] shows only tiles that have no cover yet.",
                      "왼쪽 아래의 [[전체]]는 모든 칸을, [[표지 O]]는 표지가 있는 칸만, [[표지 X]]는 아직 표지가 없는 칸만 보여 줘요. 여기서 X는 오류가 아니라 ‘아직 표지가 없다’는 뜻이에요."),
                    T("[[Cover collection]] at the bottom middle gathers the covers of every folder in one place. [[Unfinished cover collection]] next to it gathers only the files that have no cover yet, from the folders you added to LoveMyShelf and their subfolders.",
                      "아래쪽 가운데의 [[표지 O 모음]]은 모든 폴더의 표지를 한곳에 모아 보여 줘요. 그 옆의 [[표지 X 모음]]은 LoveMyShelf에 추가한 폴더와 그 하위 폴더에서 아직 표지가 없는 파일만 모아 보여 줘요."),
                    T("When the window gets narrow, as when two windows are side by side, the names of the buttons at the bottom get shorter, like [[O set]] or [[O]], or turn into icons. [[New window]] and [[Guide & Options]] at the top turn into icons (⧉ ?) too. Point the mouse at one to see its full name.",
                      "창 두 개를 나란히 띄울 때처럼 창이 좁아지면 아래쪽 버튼 이름이 [[O 모음]], [[O]]처럼 짧아지거나 아이콘으로 바뀌어요. 위쪽의 [[새 창]]과 [[설명서 겸 옵션]]도 아이콘(⧉ ?)으로 바뀌어요. 마우스를 올리면 원래 이름이 나와요."),
                    T("Each time you click [[Cover size]] at the bottom right, the tile size changes through 4 steps.","오른쪽 아래의 [[표지 크기]]를 누를 때마다 칸 크기가 4단계로 바뀌어요.")}});
            browse.Items.Add(new Card {Title=T("Move around with the keyboard, like File Explorer","파일 탐색기처럼 키보드로 움직여요"),
                Lines=new[]{T("Click a tile once to select it, and double-click to open it large. It is the same as File Explorer. (To open with a single click, change it in [[Large view]] under Options.) The arrow keys move between tiles without opening anything.",
                    "칸을 한 번 클릭하면 선택되고, 더블클릭하면 크게 열려요. 파일 탐색기와 같아요. (한 번만 클릭해도 열리게 하려면 옵션의 [[크게 보기]]에서 바꿀 수 있어요.) 방향키를 누르면 아무것도 열지 않고 칸 사이를 이동해요.")},
                Keys=new[]{K(T("Move between tiles","칸 사이 이동"),"←","→","↑","↓"),K(T("Open the selected tile large","선택한 칸 크게 보기"),"Enter"),
                    K(T("First / last tile","맨 앞 칸 / 맨 끝 칸"),"Home","End"),K(T("Refresh: reads the folder again and tidies up empty slots","새로 고침: 폴더를 다시 읽고 빈칸을 정리해요"),"F5")}});
            browse.Items.Add(new Card {Title=T("Select several at once","여러 개를 한 번에 선택해요"),
                Lines=new[]{T("Select several tiles, then right-click one of them to copy, share, hide or move them to the Recycle Bin all at once.","여러 칸을 선택한 다음 그중 하나를 오른쪽 클릭하면 복사, 공유, 숨기기, 휴지통으로 보내기를 한꺼번에 할 수 있어요.")},
                Keys=new[]{K(T("Add or remove one tile at a time","한 칸씩 추가하거나 빼기"),"Ctrl+","Click"),K(T("Select everything from the last selected tile to this one","마지막에 선택한 칸부터 이 칸까지 모두 선택"),"Shift+","Click"),
                    K(T("Select all","모두 선택"),"Ctrl+","A"),K(T("Clear selection","선택 해제"),"Esc")}});
            browse.Items.Add(new Card {Title=T("Move files to another folder","다른 폴더로 파일 옮기기"),
                Steps=new[]{T("Press {{Ctrl}} + {{N}} or click [[New window]]. One more window opens next to this one.","{{Ctrl}} + {{N}}을 누르거나 [[새 창]]을 눌러요. 지금 창 옆에 창이 하나 더 열려요."),
                    T("In the new window, open the folder you want to move the files into (click its folder tab at the top).","새 창에서 파일을 옮겨 넣을 폴더를 열어요(위쪽의 폴더 탭을 누르면 돼요)."),
                    T("In the first window, select tiles and drag them onto the new window. A window you can drop on lights up at its edge and tells you how many will move. Let go of the mouse and they move.","처음 창에서 칸을 선택해 새 창으로 끌어다 놓아요. 놓을 수 있는 창은 가장자리가 밝아지면서 몇 개가 옮겨지는지 알려 줘요. 마우스를 놓으면 옮겨져요.")},
                Lines=new[]{T("The covers made from a file go along with it, so their link to the original stays as it is. Hold {{Ctrl}} as you let go to copy instead of move.","파일로 만든 표지도 함께 따라가서 원본과의 연결이 그대로 유지돼요. {{Ctrl}}을 누른 채 놓으면 옮기지 않고 복사해요."),
                    T("If the folder already has a file with the same name, LoveMyShelf asks what to do. You can choose [[Replace]] (the existing file goes to the Recycle Bin), [[Skip]] or [[Keep both]] (a number is added after the name of the file being moved). Nothing is ever overwritten without asking.","옮길 폴더에 같은 이름의 파일이 이미 있으면 어떻게 할지 물어봐요. [[바꾸기]](기존 파일은 휴지통으로 가요), [[건너뛰기]], [[둘 다 남기기]](옮기는 파일 이름 뒤에 번호가 붙어요) 중에서 고를 수 있어요. 묻지 않고 덮어쓰는 일은 없어요."),
                    T("If you moved something by mistake, press {{Ctrl}} + {{Z}}. The files go back to where they were.","잘못 옮겼다면 {{Ctrl}} + {{Z}}를 누르세요. 파일이 원래 자리로 돌아가요.")},
                Keys=new[]{K(T("Open one more window","창 하나 더 열기"),"Ctrl+","N"),K(T("Undo the last move","방금 옮긴 것 되돌리기"),"Ctrl+","Z")}});
            browse.Items.Add(new Card {Title=T("The file queue: sending a lot to another drive","파일 대기열: 다른 드라이브로 많이 보낼 때"),
                Lines=new[]{T("Every move and copy lines up in the file queue and is sent one job at a time. Two jobs on the same hard drive at once only make both slow, so the queue never does that. While something is being sent, a [[⇄ Queue]] button with a percentage shows in the bottom bar. Click it and the file queue opens. If you close LoveMyShelf in the middle, the queue is kept: the next time you start it, the unfinished jobs are waiting there, paused, and [[Go on]] continues them from where they stopped.",
                      "옮기기와 복사는 모두 파일 대기열에 줄을 서고, 한 번에 한 작업씩 보내요. 같은 하드에 두 작업을 동시에 걸면 둘 다 느려지기만 해서, 대기열은 그렇게 하지 않아요. 보내는 동안에는 아래 줄에 퍼센트가 적힌 [[⇄ 대기열]] 버튼이 보여요. 그 버튼을 누르면 파일 대기열이 열려요. 중간에 LoveMyShelf를 닫아도 대기열은 남아 있어요. 다음에 켜면 끝내지 못한 작업이 멈춘 채로 기다리고 있고, [[이어서]]를 누르면 멈춘 곳부터 계속해요."),
                    T("In the file queue you can see how far each job is, how fast it is going and about how long is left. [[Pause]] stops a job where it is and lets go of the file, so the drive can be removed safely; [[Go on]] continues from the same place. [[↑]], [[↓]] and [[To the front]] change the order of jobs that are waiting. A job that has not started can be changed between copy and move.",
                      "파일 대기열에서는 작업마다 얼마나 보냈는지, 속도가 얼마인지, 대략 얼마나 남았는지 볼 수 있어요. [[일시정지]]를 누르면 그 자리에서 멈추고 파일을 놓아 두어서 드라이브를 안전하게 뺄 수 있고, [[이어서]]를 누르면 같은 곳부터 계속해요. [[↑]], [[↓]], [[맨 앞으로]]로 기다리는 작업의 순서를 바꿔요. 아직 시작하지 않은 작업은 복사와 옮기기를 서로 바꿀 수 있어요."),
                    T("Folders go too, with everything inside them. Drag a folder tile onto another window, a folder tile or the folder tree, or right-click a folder, choose [[Cut]] or [[Copy]], and press {{Ctrl}} + {{V}} in the folder it should go to. If that folder already has a folder with the same name, the two are joined, and you are asked only about files with the same name. The covers and tags of the files inside follow them, and a folder you had added to LoveMyShelf stays added at its new place.",
                      "폴더도 안에 든 것까지 통째로 보낼 수 있어요. 폴더 칸을 다른 창이나 폴더 칸, 폴더 트리 위로 끌어다 놓거나, 폴더를 오른쪽 클릭해 [[잘라내기]]나 [[복사]]를 고른 다음 보낼 폴더에서 {{Ctrl}} + {{V}}를 눌러요. 보낼 곳에 같은 이름의 폴더가 이미 있으면 두 폴더를 합치고, 이름이 같은 파일만 어떻게 할지 물어봐요. 안에 든 파일의 표지와 태그도 함께 따라가고, LoveMyShelf에 추가해 둔 폴더는 새 위치에서도 추가된 채로 있어요."),
                    T("When a file is moved to another drive, the original is deleted only after the whole file has arrived and its covers and tags have followed. If the drive comes loose or the program closes in the middle, the original is still whole in its old place.",
                      "다른 드라이브로 옮길 때는 파일이 통째로 도착하고 표지와 태그까지 따라간 뒤에야 원본을 지워요. 중간에 드라이브가 빠지거나 프로그램이 꺼져도 원본은 원래 자리에 온전히 남아 있어요.")}});
            browse.Items.Add(new Setting {Id="dropAcross",SectionId="browse",Title=T("When files are dragged to another drive, copy or move?","다른 드라이브로 끌어다 놓으면 복사할까요, 옮길까요?"),
                Lines=new[]{T("This is for dragging without a key held. Within one drive, files always move. {{Ctrl}} held always copies and {{Shift}} held always moves, whatever is chosen here.",
                      "아무 키도 누르지 않고 끌어다 놓을 때의 설정이에요. 같은 드라이브 안에서는 언제나 옮겨요. 여기서 무엇을 고르든 {{Ctrl}}을 누른 채 놓으면 복사, {{Shift}}를 누른 채 놓으면 옮기기예요.")},
                Choices=new[]{
                    new Choice(T("Ask each time","놓을 때마다 물어보기"),T("A small window asks [[Copy]] or [[Move]] when you let go.","놓는 순간 작은 창이 [[복사]]할지 [[옮기기]]할지 물어봐요.")),
                    new Choice(T("Copy","복사"),T("The originals stay where they are. This is what File Explorer does.","원본을 그 자리에 그대로 둬요. 파일 탐색기와 같은 방식이에요.")),
                    new Choice(T("Move","옮기기"),T("Each original is deleted after it has arrived whole, and its covers and tags go with it.","다 도착한 파일부터 원본을 지우고, 표지와 태그도 함께 따라가요."))},
                Default=0,Get=p=>p.DropAcross,Set=(p,i)=>p.DropAcross=i});
            browse.Items.Add(new Setting {Id="verifyCopies",SectionId="browse",Title=T("Check that each copy is the same as the original?","보낸 파일이 원본과 똑같은지 확인할까요?"),
                Lines=new[]{T("When this is on, every file sent through the file queue is read back from the drive it went to and compared with the original, byte by byte. A file that is moved is deleted from its old place only after it has passed. If a copy is not the same, it is thrown away, the original stays, and you are told.",
                      "켜 두면 파일 대기열로 보낸 파일을 도착한 드라이브에서 다시 읽어, 원본과 한 바이트씩 비교해요. 옮기는 파일은 이 확인을 통과한 뒤에야 원래 자리에서 지워요. 복사본이 원본과 다르면 복사본을 버리고 원본은 그대로 두고 알려 드려요."),
                    T("Even with this off, a file is counted as sent only after it has been written all the way onto the drive and its size has been checked.",
                      "꺼 두어도, 파일을 드라이브에 끝까지 다 쓰고 크기까지 확인한 뒤에야 보낸 것으로 쳐요.")},
                Choices=new[]{
                    new Choice(T("Don't check","확인하지 않기"),T("Files are sent at the usual speed.","평소 속도로 보내요.")),
                    new Choice(T("Check every copy","하나하나 확인하기"),T("Sending takes about twice as long. Good for an old drive, a loose cable, or files you can't afford to lose.","보내는 시간이 두 배쯤 걸려요. 오래된 드라이브나 헐거운 케이블이 걱정될 때, 잃으면 안 되는 파일을 보낼 때 좋아요."))},
                Default=0,Columns=2,Get=p=>p.VerifyCopies?1:0,Set=(p,i)=>p.VerifyCopies=i==1});
            browse.Items.Add(new Card {Title=T("Rename, delete, search","이름 바꾸기, 삭제, 검색"),
                Keys=new[]{K(T("Renames the selected file. The JPG and GIF covers made from it are renamed along with it.","선택한 파일의 이름을 바꿔요. 그 파일로 만든 JPG 표지와 GIF 표지의 이름도 함께 바뀌어요."),"F2"),
                    K(T("Moves it to the Recycle Bin. You can restore it from the Recycle Bin.","휴지통으로 보내요. 휴지통에서 복원할 수 있어요."),"Delete")},
                Lines=new[]{T("Click the magnifier at the top right (or press {{Ctrl}} + {{F}}) and the search box opens. Type something and only the files whose names contain it stay. Press {{Esc}} to clear the search and close the box.",
                    "오른쪽 위의 돋보기를 누르면(또는 {{Ctrl}} + {{F}}) 검색창이 열려요. 글자를 입력하면 이름에 그 글자가 들어간 파일만 남아요. {{Esc}}를 누르면 검색이 지워지고 검색창이 닫혀요."),
                    T("Just left of the magnifier is [[Type ▾]]. Click it to see the file types in this folder and how many of each (JPG, PNG, MP4 …). Click a type and only that type shows; click another to add it, and click it again to take it out. [[All types]] shows everything again. Opening another folder starts with all types.",
                      "돋보기 바로 왼쪽에 [[형식 ▾]]이 있어요. 누르면 이 폴더에 있는 파일 형식(JPG, PNG, MP4 …)과 개수가 보여요. 형식 하나를 누르면 그 형식만 보이고, 다른 형식을 누르면 더해지고, 다시 누르면 빠져요. [[모든 형식]]을 누르면 다시 전부 보여요. 다른 폴더를 열면 모든 형식으로 돌아와요."),
                    T("Right-click a tile for these menu items too: [[Open file location]] shows the file in File Explorer, [[Open with default app]] opens it in the program set as the default in Windows, and [[Details]] shows information such as its size and date.",
                      "칸을 오른쪽 클릭하면 이런 메뉴도 있어요. [[파일 위치 열기]]는 파일 탐색기에서 그 파일을 보여 주고, [[기본 앱으로 열기]]는 Windows에서 기본으로 지정된 프로그램으로 열고, [[정보 보기]]는 크기·날짜 같은 정보를 보여 줘요.")}});
            browse.Items.Add(new Card {Title=T("Tile positions don't change by themselves","칸 위치는 저절로 바뀌지 않아요"),
                Lines=new[]{T("Even when you make, hide or delete a cover, the other tiles stay right where they are, so you don't lose your place. A deleted tile stays as an empty slot. To tidy up empty slots, press {{F5}} or choose a sort again.",
                    "표지를 만들거나 숨기거나 지워도 다른 칸은 제자리에 그대로 있어요. 그래서 보던 위치를 놓치지 않아요. 지운 칸은 빈칸으로 남아요. 빈칸을 정리하려면 {{F5}}를 누르거나 정렬을 다시 고르면 돼요."),
                    T("The folders you added to LoveMyShelf are in the folder tree on the left. Drag a folder onto another one in the tree to change their order. Right-click for [[Open in new window]] and [[Remove from LoveMyShelf]]. The two collection buttons at the bottom middle are always there.",
                      "LoveMyShelf에 추가한 폴더는 왼쪽 폴더 트리에 있어요. 트리 안에서 폴더를 다른 폴더 위로 끌어다 놓으면 순서가 바뀌어요. 오른쪽 클릭하면 [[새 창에서 열기]]와 [[LoveMyShelf에서 제거]]가 있어요. 아래쪽 가운데의 모음 버튼 두 개는 항상 그 자리에 있어요.")}});
            browse.Items.Add(new Card {Title=T("Select many at once: hold the right button and drag","여러 개 한 번에 선택하기: 오른쪽 버튼을 누른 채 끌기"),
                Lines=new[]{T("Hold the right mouse button and drag over the tiles. A rectangle appears, and every tile it touches is selected. The tiles sit close together, so you can start right on a tile. (This is LoveMyShelf's own way. Dragging with the left button from an empty spot, as in File Explorer, works too.)",
                    "마우스 오른쪽 버튼을 누른 채 칸 위로 끌어 보세요. 사각형이 생기고, 사각형에 닿은 칸이 모두 선택돼요. 칸끼리 붙어 있어서 칸 위에서 바로 시작해도 돼요. (LoveMyShelf만의 방식이에요. 파일 탐색기처럼 빈 곳에서 왼쪽 버튼으로 끌어도 돼요.)"),
                    T("Hold {{Ctrl}} while dragging to add to what is already selected (tiles already selected get unselected). Drag to the top or bottom edge and the screen scrolls along. {{Esc}} cancels. A right-click without dragging opens the menu as usual. It works the same way in the folders area.",
                      "{{Ctrl}}을 누른 채 끌면 이미 선택한 것에 추가돼요(이미 선택된 칸은 해제돼요). 위쪽이나 아래쪽 끝으로 끌면 화면이 따라서 스크롤돼요. {{Esc}}를 누르면 취소돼요. 끌지 않고 오른쪽 클릭만 하면 평소처럼 메뉴가 나와요. 폴더 영역에서도 똑같이 쓸 수 있어요.")}});
            browse.Items.Add(new Card {Title=T("Copy, cut and paste files","파일 복사·잘라내기·붙여넣기"),
                Lines=new[]{T("{{Ctrl}} + {{C}} copies the selected files, {{Ctrl}} + {{X}} cuts them (they look faded until pasted), and {{Ctrl}} + {{V}} pastes into the folder you're looking at. It works the same way as File Explorer, so you can pass files back and forth with File Explorer and other windows. When files move, their covers go along, and {{Ctrl}} + {{Z}} undoes it.",
                    "{{Ctrl}} + {{C}}는 선택한 파일 복사, {{Ctrl}} + {{X}}는 잘라내기(붙여 넣을 때까지 흐리게 보여요), {{Ctrl}} + {{V}}는 지금 보고 있는 폴더에 붙여넣기예요. 파일 탐색기와 방식이 같아서 탐색기나 다른 창과도 파일을 주고받을 수 있어요. 파일을 옮기면 표지도 따라가고, {{Ctrl}} + {{Z}}로 되돌릴 수 있어요.")}});
            browse.Items.Add(new Card {Title=T("The right-click menu","오른쪽 클릭 메뉴"),
                Lines=new[]{T("Right-click a file or folder and LoveMyShelf's menu comes first, then, below a dividing line, the same menu as File Explorer (Open with, Share, Compress, Properties and so on. It is the full menu that Windows 11 shows only after you click ‘Show more options’). Delete, Rename, Cut, Copy and Paste in it are handled by LoveMyShelf, so covers go along with their files. Right-click an empty spot and the folder's menu appears, as in File Explorer: View, Sort by, Refresh, Paste and Undo move, and below them New, Open in Terminal, Properties and so on.",
                    "파일이나 폴더를 오른쪽 클릭하면 LoveMyShelf 메뉴가 먼저 나오고, 구분선 아래에 파일 탐색기와 같은 메뉴가 나와요(연결 프로그램, 공유, 압축, 속성 등. Windows 11에서 ‘더 많은 옵션 표시’를 눌러야 나오는 전체 메뉴예요). 그 안의 삭제·이름 바꾸기·잘라내기·복사·붙여넣기는 LoveMyShelf가 대신 처리해서 표지가 파일을 따라가요. 빈 곳을 오른쪽 클릭하면 파일 탐색기처럼 그 폴더의 메뉴가 나와요. 보기, 정렬 기준, 새로 고침, 붙여넣기, 이동 취소가 있고, 그 아래에 새로 만들기, 터미널에서 열기, 속성 등이 있어요.")}});
            browse.Items.Add(new Card {Title=T("A folder tree on the left, like File Explorer","파일 탐색기 같은 왼쪽 폴더 트리"),
                Lines=new[]{T("Click the button at the very top left (or press {{Ctrl}} + {{T}}) to open and close the folder tree. LoveMyShelf is at the top (the two collections and the folders you added), and below it is the same as File Explorer: your pinned folders, This PC with its drives, and the network.",
                    "맨 왼쪽 위 버튼을 누르면(또는 {{Ctrl}} + {{T}}) 폴더 트리가 열리고 닫혀요. 맨 위에는 LoveMyShelf(모음 두 개와 추가한 폴더들)가 있고, 그 아래는 파일 탐색기와 같아요. 즐겨찾기에 고정한 폴더, 내 PC와 드라이브, 네트워크가 있어요."),
                    T("Click a folder to open it, and click ▸ or double-click to unfold the folders inside. Folders you haven't added to LoveMyShelf can be opened this way too. Drag files onto a folder in the tree and they move to that folder.",
                      "폴더를 누르면 그 폴더가 열리고, ▸를 누르거나 더블클릭하면 안의 폴더가 펼쳐져요. LoveMyShelf에 추가하지 않은 폴더도 이렇게 바로 열 수 있어요. 파일을 트리의 폴더 위로 끌어다 놓으면 그 폴더로 옮겨져요."),
                    T("Drag the right border of the tree to change its width. Whether it is open, its width and the groups you folded stay the same the next time you start.",
                      "트리의 오른쪽 경계선을 끌면 너비가 바뀌어요. 열어 뒀는지, 너비, 접어 둔 항목은 다음에 켜도 그대로예요.")}});
            browse.Items.Add(new Setting {Id="foldersPlace",SectionId="browse",Title=T("Where should the folders be shown?","폴더는 어디에 보여 줄까요?"),
                Lines=new[]{T("The subfolders in a folder are not mixed with the photos; they gather in their own folders area, set apart by a horizontal line. Click [[View]] on that line to change how they look, as in File Explorer. In a place with only folders and no photos or videos, the folders unfold by themselves even if you folded them.",
                    "폴더 안의 하위 폴더는 사진과 섞이지 않고, 가로줄로 구분된 폴더 영역에 따로 모여요. 그 줄의 [[보기]]를 누르면 파일 탐색기처럼 보기 방식을 바꿀 수 있어요. 사진이나 영상 없이 폴더만 있는 곳에서는 접어 뒀더라도 폴더가 저절로 펼쳐져요.")},
                Choices=new[]{new Choice(T("Above the photos","사진 위"),T("Folders come first, like File Explorer.","파일 탐색기처럼 폴더가 먼저 나와요.")),
                    new Choice(T("Below the photos","사진 아래"),T("Photos come first, and the folders are below.","사진이 먼저 나오고, 폴더는 아래에 있어요."))},
                Default=0,Columns=2,Get=p=>p.FoldersBelow?1:0,Set=(p,i)=>p.FoldersBelow=i==1});
            browse.Items.Add(new Setting {Id="folderView",SectionId="browse",Title=T("In what view should the folders be shown?","폴더를 어떤 방식으로 보여 줄까요?"),
                Lines=new[]{T("These are the same views as File Explorer's [View] menu.","파일 탐색기의 [보기] 메뉴와 같은 방식들이에요.")},
                Choices=new[]{new Choice(T("Extra large icons","아주 큰 아이콘"),""),new Choice(T("Large icons","큰 아이콘"),""),new Choice(T("Medium icons","보통 아이콘"),""),
                    new Choice(T("Small icons","작은 아이콘"),""),new Choice(T("List","목록"),""),new Choice(T("Details","자세히"),T("Name, date modified and type show in columns.","이름, 수정한 날짜, 유형이 열로 나뉘어 보여요.")),
                    new Choice(T("Tiles","타일"),""),new Choice(T("Content","내용"),"")},
                Default=1,Columns=2,Get=p=>Math.Max(0,Math.Min(7,p.FolderView)),Set=(p,i)=>p.FolderView=i});
            browse.Items.Add(new Setting {Id="sort",SectionId="browse",Title=T("In what order should the tiles be sorted?","칸을 어떤 순서로 정렬할까요?"),
                Lines=new[]{T("The [[Sort]] button at the bottom right changes it in the same way. It is the same as sorting in File Explorer. Choose one and the folder is read again and sorted in the new order, and empty slots are tidied up too.",
                    "오른쪽 아래의 [[정렬]] 버튼으로도 똑같이 바꿀 수 있어요. 파일 탐색기의 정렬과 같아요. 하나를 고르면 폴더를 다시 읽어서 새 순서로 정렬하고, 빈칸도 정리돼요.")},
                Choices=new[]{
                    new Choice(T("File name","파일 이름"),T("In alphabetical order of the names. Numbers in a name are compared by size, so 2 comes before 10.","이름의 가나다·ABC 순서예요. 이름 속 숫자는 크기대로 비교해서 2가 10보다 앞에 와요.")),
                    new Choice(T("Date modified","수정한 날짜"),T("In order of the date each file was last saved or changed. It is the same as ‘Date modified’ in File Explorer.","파일이 마지막으로 저장되거나 수정된 날짜 순서예요. 파일 탐색기의 ‘수정한 날짜’와 같아요.")),
                    new Choice(T("File size","파일 크기"),T("In order of file size (MB).","파일 용량(MB) 순서예요.")),
                    new Choice(T("File type","파일 종류"),T("Files of the same kind gather together: GIFs with GIFs, JPGs with JPGs, MP4s with MP4s.","같은 종류끼리 모여요. GIF는 GIF끼리, JPG는 JPG끼리, MP4는 MP4끼리 모여 있어요.")),
                    new Choice(T("Cover creation order","표지 만든 순서"),T("In order of the time each cover was made. Tiles that have no cover yet come last.","표지를 만든 시간 순서예요. 아직 표지가 없는 칸은 맨 뒤에 와요."))},
                Default=0,
                Get=p=>Array.IndexOf(GalleryWindow.SortChoices,p.Sort),
                Set=(p,i)=>{var kind=GalleryWindow.SortChoices[i];if(kind!=p.Sort)p.Descending=kind==PhotoSort.Modified;p.Sort=kind;},
                Tips=new[]{T("[[Shuffle]] is at the very bottom of the [[Sort]] menu. A shuffled order is temporary: choose a sort again or reopen the folder and the original order comes back.",
                    "[[순서 섞기]]는 [[정렬]] 메뉴 맨 아래에 있어요. 섞은 순서는 임시예요. 정렬을 다시 고르거나 폴더를 다시 열면 원래 순서로 돌아와요."),
                    T("The sort applies to every folder in the same way.","정렬은 모든 폴더에 똑같이 적용돼요.")}});
            browse.Items.Add(new Setting {Id="direction",SectionId="browse",Title=T("Which end should come first?","어느 쪽부터 보여 줄까요?"),
                Lines=new[]{T("The choices below change to fit the sort chosen above.","아래 선택지는 위에서 고른 정렬 기준에 맞춰 바뀌어요.")},
                Choices=new[]{new Choice(GalleryWindow.DirectionName(Prefs.Sort,false),""),new Choice(GalleryWindow.DirectionName(Prefs.Sort,true),"")},
                Default=Prefs.Sort==PhotoSort.Modified?1:0,Columns=2,
                Get=p=>p.Descending?1:0,Set=(p,i)=>p.Descending=i==1});
            browse.Items.Add(new Setting {Id="tileNames",SectionId="browse",Title=T("Show the file name under each tile?","칸 아래에 파일 이름을 보여 줄까요?"),
                Lines=new[]{T("The name appears on a separate line under the picture, so it doesn't cover the cover.","이름은 그림 아래의 별도 줄에 나와서 표지를 가리지 않아요.")},
                Choices=new[]{new Choice(T("No name","이름 없음"),T("Only the pictures show. Point at a tile and its name appears.","그림만 보여요. 칸에 마우스를 올리면 이름이 나와요.")),
                    new Choice(T("One line","한 줄"),T("A long name is shortened with ‘…’ at the end. Point at the tile to see the whole name.","긴 이름은 뒷부분이 ‘…’로 줄어요. 마우스를 올리면 전체 이름이 보여요.")),
                    new Choice(T("Two lines","두 줄"),T("Most names fit. Longer ones are shortened with ‘…’.","웬만한 이름은 다 들어가요. 더 길면 ‘…’로 줄어요.")),
                    new Choice(T("Whole name","전체"),T("The name shows to the end. Every tile's name area gets the same height, fitted to the longest name in the folder, up to 12 lines.","이름을 끝까지 보여 줘요. 폴더에서 가장 긴 이름에 맞춰 모든 칸의 이름 높이가 같아지고, 최대 12줄까지 보여요."))},
                Default=0,Columns=2,Get=p=>Math.Max(0,Math.Min(3,p.TileNames)),Set=(p,i)=>p.TileNames=i});

            browse.Items.Add(new Setting {Id="tileShape",SectionId="browse",Title=T("What shape should the grid's cells be?","바둑판 칸은 어떤 모양으로 할까요?"),
                Lines=new[]{T("Covers and photos are shown whole inside the cells, so changing the shape never cuts them. The covers you made stay as they are.",
                    "표지와 사진은 칸 안에 잘리지 않고 다 보여서, 칸 모양을 바꿔도 잘리지 않아요. 만든 표지도 그대로예요."),
                    T("The same choice is in the blank-space menu: right-click an empty spot → [[View]] → [[Shape of the cells]].",
                    "빈 곳을 오른쪽 클릭 → [[보기]] → [[칸 모양]]에서도 바꿀 수 있어요.")},
                Choices=new[]{new Choice(T("9:16 tall","9:16 세로"),T("Tall phone videos and pictures show big. Wide ones show small.","세로로 찍은 휴대폰 영상·짤이 크게 보여요. 가로 사진은 작게 보여요.")),
                    new Choice(T("3:4 tall","3:4 세로"),T("Easy-going when tall and wide ones are mixed. The same shape as an Instagram profile.","세로·가로가 섞여 있을 때 무난해요. 인스타그램 프로필 칸과 같은 모양이에요.")),
                    new Choice(T("1:1 square","1:1 정사각형"),T("Tall and wide ones show at about the same size.","세로·가로가 비슷한 크기로 보여요."))},
                Default=Covers.DefaultShape,Columns=3,Get=p=>Math.Max(0,Math.Min(2,p.TileShape)),Set=(p,i)=>p.TileShape=i});
            browse.Items.Add(new Setting {Id="wideCovers",SectionId="browse",Title=T("Should wide covers take two cells?","가로로 긴 표지는 칸 두 개에 보여 줄까요?"),
                Lines=new[]{T("A cover clearly wider than a cell can take two cells side by side, so it shows big instead of as a thin strip.",
                    "칸보다 확실히 가로로 긴 표지는 칸 두 개를 나란히 차지해서, 가는 띠처럼 작게 보이지 않고 크게 보이게 할 수 있어요.")},
                Choices=new[]{new Choice(T("Two cells (on)","칸 두 개 (켜기)"),T("Wide covers show big across two cells.","가로로 긴 표지가 칸 두 개에 크게 보여요.")),
                    new Choice(T("One cell (off)","칸 하나 (끄기)"),T("Every file takes one cell; wide covers show smaller.","모든 파일이 칸 하나씩이에요. 가로로 긴 표지는 작게 보여요."))},
                Default=0,Columns=2,Get=p=>p.WideCovers?0:1,Set=(p,i)=>p.WideCovers=i==0});
            browse.Items.Add(new Setting {Id="wideGaps",SectionId="browse",Title=T("When a wide cover leaves a cell empty at the end of a row?","가로 표지 때문에 줄 끝이 비면 어떻게 할까요?"),
                Lines=new[]{T("A wide cover takes two cells, so at the end of a row it moves to the next row and can leave that cell empty.",
                    "가로 표지는 칸 두 개를 차지해서, 줄 끝에 걸리면 다음 줄로 넘어가고 그 자리가 빌 수 있어요.")},
                Choices=new[]{new Choice(T("Fill it with the next one","뒤 표지로 채우기"),T("The next one-cell file goes into the empty cell. The order changes by just one place.","바로 뒤의 한 칸짜리 파일을 빈칸에 놓아요. 순서는 딱 한 칸만 바뀌어요.")),
                    new Choice(T("Leave it empty","빈칸 그대로"),T("The order stays exactly as it is and the cell stays empty.","순서를 그대로 지키고 줄 끝을 비워 둬요."))},
                Default=0,Columns=2,Get=p=>p.FillWideGaps?0:1,Set=(p,i)=>p.FillWideGaps=i==0});

            // ----- Making a cover -----
            var make=add("editor",T("Making a cover","표지 만들기"));
            make.Items.Add(new Card {Title=T("Fit it inside the frame","테두리 안에 맞춰요"),
                Lines=new[]{
                    T("In the middle of the Make cover screen there is a frame. At first the whole picture is inside it. The part you see inside the frame becomes the cover just as it is.",
                      "표지 만들기 화면 가운데에 테두리가 있어요. 처음에는 사진 전체가 들어 있어요. 테두리 안에 보이는 부분이 그대로 표지가 돼요."),
                    T("Drag the blue handles on the frame's corners and edges to give it any shape. [[Fill the grid's cell]] gives it the shape of the grid's cells, so it fills its cell with no empty bands. For a wide cover, [[Fill two cells side by side]] gives it the shape of two cells next to each other. [[Whole picture]] goes back to the whole picture, and you can also type a width and height.",
                      "테두리의 모서리나 변의 파란 손잡이를 끌면 아무 모양으로나 바뀌어요. [[칸에 꽉 맞추기]]를 누르면 바둑판 칸과 같은 모양이 돼서 칸에 빈 띠 없이 꽉 차요. 가로로 긴 표지는 [[칸 두 개에 꽉 맞추기]]를 누르면 칸 두 개를 나란히 붙인 모양이 돼요. [[원본 그대로]]는 사진 전체로 돌아가고, 가로·세로 숫자를 직접 넣어도 돼요."),
                    T("The picture stays still while you drag the frame; only the frame moves and changes size, and the outside is dimmed. [[Fit to the window]] shows the whole picture and the frame. Turn on [[🔍 Zoom the picture with the frame]] to have the picture zoom with the frame as before.",
                      "틀을 끌어도 사진은 가만히 있고 틀만 움직이고 커지고 작아져요. 틀 바깥은 어둡게 보여요. [[화면에 맞추기]]를 누르면 사진과 틀이 다 보여요. 예전처럼 사진이 틀을 따라 확대·축소되게 하려면 [[🔍 틀 따라 사진 확대·축소]]를 켜요."),
                    T("Always cut the same shape, like 9:16? Pick it and turn on [[🔒 Keep this shape]]. While it stays on, every new cover starts in that shape, so there is nothing to click next time. Editing a saved cover keeps that cover's own shape.",
                      "늘 같은 모양(예: 9:16)으로만 자른다면 그 모양을 고르고 [[🔒 비율 고정]]을 켜 두세요. 켜 두는 동안 새 표지는 처음부터 그 모양으로 시작해서 다음부터는 누를 게 없어요. 이미 만든 표지를 고칠 때는 그 표지의 모양 그대로예요."),
                    T("[[How it shows in your grid]] on the right shows how the cover sits in your grid's cells. A wide cover takes two cells side by side.",
                      "오른쪽 [[내 바둑판에서는 이렇게 보여요]]에 표지가 바둑판 칸에 어떻게 들어가는지 보여요. 가로로 긴 표지는 칸 두 개를 나란히 차지해요."),
                    T("Drag the picture to change its position, and roll the wheel to change its size.",
                      "사진을 끌면 위치가 바뀌고, 휠을 굴리면 크기가 바뀌어요."),
                    T("If you make the picture smaller than the frame, the part left over is filled with black.","사진을 테두리보다 작게 줄이면 남는 부분은 검은색으로 채워져요.")},
                Keys=new[]{K(T("Move the picture","사진 옮기기"),T("Drag","끌기")),K(T("Zoom in / out","확대 / 축소"),T("Wheel","휠")),
                    K(T("One step in / out","한 단계 확대 / 축소"),"+","−"),K(T("Previous / next scene (videos)","이전 장면 / 다음 장면 (영상)"),"D","F"),
                    K(T("Skip back / forward (videos)","뒤로 / 앞으로 건너뛰기 (영상)"),"←","→"),K(T("Skip one second (videos)","1초 건너뛰기 (영상)"),"Shift","←","→"),
                    K(T("Back to normal / darker / brighter","원래대로 / 어둡게 / 밝게"),"Q","W","E"),K(T("Brightness style: Soft / See in the dark / Milky","밝기 방식: 부드럽게 / 어두운 곳 보기 / 뽀얗게"),"R"),
                    K(T("Close without saving","저장하지 않고 닫기"),"Esc")}});
            make.Items.Add(new Card {Title=T("Rotation and brightness","회전과 밝기"),
                Lines=new[]{T("Move the [[Rotation]] slider and the picture tilts 0.1° at a time. A picture lying on its side can be stood up in one go with the 90° buttons.",
                    "[[회전]] 슬라이더를 움직이면 사진이 0.1도씩 기울어요. 옆으로 누운 사진은 90도 버튼으로 한 번에 세울 수 있어요."),
                    T("Move the [[Brightness]] slider and the picture gets brighter or darker. Click [[Reset]] and both rotation and brightness go back to how they started.",
                      "[[밝기]] 슬라이더를 움직이면 사진이 밝아지거나 어두워져요. [[원래대로]]를 누르면 회전과 밝기가 모두 처음 상태로 돌아가요.")}});
            make.Items.Add(new Card {Title=T("Several covers from one original","원본 하나로 표지 여러 장"),
                Lines=new[]{T("[[Add cover]] in the large view (or [[Make one more cover]] when you right-click a tile) keeps the covers you made before and makes one more. When a video with a cover plays, the buttons are in the order [[Pause]] · [[Add cover]] · [[View cover]] · [[Close]], so you can add a cover while watching and keep watching. [[Edit cover]] changes the cover you're looking at. If there is a GIF cover, only the button for the cover on screen appears: [[Edit GIF cover]] while the GIF cover plays, and [[Edit JPG cover]] after you click [[View JPG cover]].",
                    "크게 보기의 [[표지 추가]](칸을 오른쪽 클릭하면 [[표지 하나 더 만들기]])를 누르면 먼저 만든 표지는 그대로 두고 표지를 한 장 더 만들어요. 표지가 있는 영상을 재생할 때는 버튼이 [[일시정지]] · [[표지 추가]] · [[표지 보기]] · [[닫기]] 순서로 놓여 있어서, 영상을 보다가 표지를 추가하고 이어서 볼 수 있어요. [[표지 수정]]은 지금 보고 있는 표지를 고쳐요. GIF 표지가 있는 경우에는 지금 보이는 표지에 맞는 버튼만 나와요. GIF 표지가 재생 중일 때는 [[GIF 표지 수정]]이, [[JPG 표지 보기]]를 누른 뒤에는 [[JPG 표지 수정]]이 나와요."),
                    T("In the folder view, an original with two or more covers shows as that many tiles side by side. These tiles are all made from the same original, so Delete, Rename and moving to another folder apply once to the one original. [[Cover collection]] also shows one tile for each cover.","폴더 화면에서는 표지가 두 장 이상인 원본이 표지 수만큼의 칸으로 나란히 보여요. 이 칸들은 모두 같은 원본에서 만든 것이라서, 삭제·이름 바꾸기·다른 폴더로 옮기기는 원본 하나에 한 번만 적용돼요. [[표지 O 모음]]에도 표지마다 칸이 하나씩 보여요.")}});
            make.Items.Add(new Card {Title=T("A cover from a picture made elsewhere","밖에서 만든 그림으로 표지 만들기"),
                Lines=new[]{T("Right-click a tile and choose [[Use a picture made elsewhere as a cover…]], then pick a picture file (JPG, PNG, WebP or BMP). The same Make cover screen opens with that picture, so you can cut, turn and brighten it in the same way. A poster or a picture you edited in another program can become the cover of a video or photo like this.",
                    "칸을 오른쪽 클릭하고 [[밖에서 만든 그림을 표지로…]]를 누른 다음 그림 파일(JPG, PNG, WebP, BMP)을 골라요. 그 그림으로 평소와 같은 표지 만들기 화면이 열려서, 똑같이 자르고 돌리고 밝기를 바꿀 수 있어요. 포스터나 다른 프로그램에서 손본 그림을 영상이나 사진의 표지로 쓸 수 있어요."),
                    T("LoveMyShelf keeps its own copy of the picture you picked. You can move or delete your picture file afterwards, and [[Edit cover]] still works. When you move the cover to the Recycle Bin, the finished cover file goes to the Recycle Bin and the kept copy is removed with it.",
                      "고른 그림은 LoveMyShelf가 사본을 따로 보관해요. 그래서 원래 그림 파일을 옮기거나 지워도 [[표지 수정]]을 쓸 수 있어요. 표지를 휴지통으로 보내면 완성된 표지 파일은 휴지통으로 가고, 보관해 둔 사본은 함께 지워져요."),
                    T("A cover made this way has no scene in the video, so [[Make GIF cover]] doesn't appear for it. Upscaling, sharing and the large view work as usual.",
                      "이렇게 만든 표지는 영상의 어느 장면인지 정보가 없어서 [[GIF 표지 만들기]]가 나오지 않아요. 업스케일, 공유, 크게 보기는 평소처럼 돼요.")}});
            make.Items.Add(new Card {Title=T("Changing the order of covers","표지 순서 바꾸기"),
                Lines=new[]{T("When one original has two or more covers, right-click a cover and open [[Cover order]]: [[Move forward]], [[Move back]], [[Move to the very front (the first cover)]] and [[Back to the automatic order]].",
                    "원본 하나에 표지가 두 장 이상 있으면, 표지를 오른쪽 클릭하고 [[표지 순서]]를 열어요. [[앞으로]], [[뒤로]], [[맨 앞으로 (첫 번째 표지)]], [[자동 순서로 되돌리기]]가 있어요."),
                    T("The automatic order puts covers from outside pictures first, in the order you added them, and then the covers made here in the time order of their scenes in the video.",
                      "자동 순서는 밖에서 만든 그림으로 만든 표지를 넣은 순서대로 먼저 놓고, 그 뒤에 여기서 만든 표지를 영상 속 장면 시간 순서로 놓아요."),
                    T("Once you move a cover yourself, that original keeps your order, and a cover you make later goes to the end. [[Back to the automatic order]] undoes it.",
                      "한 번이라도 직접 옮기면 그 원본은 정한 순서를 그대로 지키고, 나중에 만든 표지는 맨 뒤에 붙어요. [[자동 순서로 되돌리기]]를 누르면 원래대로 돌아가요.")}});
            make.Items.Add(new Setting {Id="coverTimes",SectionId="editor",Title=T("Show the scene time on covers?","표지에 장면 시간을 보여 줄까요?"),
                Lines=new[]{T("A cover made here from a video can show where its scene is in the video, like 12:34, at the bottom left of its tile. A cover from a picture made elsewhere and a cover of a photo show no time.",
                    "여기서 영상으로 만든 표지의 칸 왼쪽 아래에, 그 장면이 영상의 어디쯤인지 12:34처럼 보여 줄 수 있어요. 밖에서 만든 그림으로 만든 표지와 사진의 표지에는 시간이 나오지 않아요.")},
                Choices=new[]{new Choice(T("Don't show (off)","보이지 않기 (끄기)"),T("Only the pictures show.","그림만 보여요.")),
                    new Choice(T("Show (on)","보이기 (켜기)"),T("The scene time shows on each cover made from a video.","영상으로 만든 표지마다 장면 시간이 보여요."))},
                Default=0,Columns=2,Get=p=>p.CoverTimes?1:0,Set=(p,i)=>p.CoverTimes=i==1});
            make.Items.Add(new Setting {Id="snap",SectionId="editor",Title=T("Should the frame stick to the picture's edges?","테두리를 사진 가장자리에 붙일까요?"),
                Lines=new[]{T("While you drag, when an edge of the picture comes close to the frame, it can stick like a magnet so that it lines up straight. This prevents a thin gap inside the frame. Keep dragging after it sticks and it comes loose again.",
                    "사진을 끌다가 사진 가장자리가 테두리 가까이 오면 자석처럼 딱 붙어서 반듯하게 맞춰지게 할 수 있어요. 테두리 안쪽에 가느다란 빈틈이 생기는 걸 막아 줘요. 붙은 뒤에도 계속 끌면 다시 떨어져요.")},
                Choices=new[]{new Choice(T("Stick (on)","붙이기 (켜기)"),T("When an edge comes close, it sticks like a magnet.","가장자리가 가까이 오면 자석처럼 붙어요.")),
                    new Choice(T("Don't stick (off)","붙이지 않기 (끄기)"),T("It stops exactly where you drag it. Good when you want to move it a tiny bit at a time.","끌어다 놓은 위치에 그대로 멈춰요. 아주 조금씩 옮기고 싶을 때 좋아요."))},
                Default=0,Columns=2,Get=p=>p.SnapEdges?0:1,Set=(p,i)=>p.SnapEdges=i==0});
            make.Items.Add(Numbers("wheel",T("How much should one wheel step zoom?","휠을 한 칸 굴릴 때 얼마나 확대할까요?"),new[]{3,5,7,10,15},7,
                p=>p.WheelZoomPercent,(p,v)=>p.WheelZoomPercent=v,v=>v+"%",
                v=>v==3?T("Very little at a time. For fitting precisely","아주 조금씩. 세밀하게 맞출 때"):v==7?T("This is enough for most","대부분 이걸로 충분해요"):v==15?T("In big steps. For changing the size quickly","큼직하게. 크기를 빨리 바꿀 때"):"",3));
            ((Setting)make.Items.Last()).Lines=new[]{T("Each step of the mouse wheel makes the picture this much bigger or smaller. The smaller the number, the less it changes at a time; the bigger, the more.",
                "마우스 휠을 한 칸 굴릴 때마다 사진이 이만큼씩 커지거나 작아져요. 숫자가 작을수록 조금씩, 클수록 한 번에 많이 바뀌어요."),
                T("A Make cover screen that is already open uses it from the next time it opens.","이미 열려 있는 표지 만들기 화면에는 다시 열 때부터 적용돼요.")};
            Typed((Setting)make.Items.Last(),"%",1,50,1);

            make.Items.Add(new Setting {Id="pintop",SectionId="editor",Title=T("Should the top line of the frame stay fixed when you zoom with the wheel?","휠로 확대할 때 테두리 윗줄을 고정할까요?"),
                Lines=new[]{T("When this is on, the part touching the top line of the frame stays in place even when you zoom with the wheel, so what you lined up with the top line isn't disturbed. Dragging always moves freely.",
                    "켜 두면 휠로 확대·축소해도 테두리 윗줄에 닿은 부분이 그 자리에 그대로 있어서, 윗줄에 맞춰 둔 부분이 흐트러지지 않아요. 끌 때는 언제나 자유롭게 움직여요.")},
                Choices=new[]{new Choice(T("Fix the top line (on)","윗줄 고정 (켜기)"),T("The top line of the frame is the base when you zoom with the wheel.","휠로 확대·축소할 때 테두리 윗줄이 기준이 돼요.")),
                    new Choice(T("Middle as the base (off)","가운데 기준 (끄기)"),T("The middle of the frame is the base when you zoom with the wheel.","휠로 확대·축소할 때 테두리 가운데가 기준이 돼요."))},
                Default=0,Columns=2,Get=p=>p.PinTopOnWheel?0:1,Set=(p,i)=>p.PinTopOnWheel=i==0});

            // ----- Saving covers -----
            var save=add("save",T("Saving covers","표지 저장하기"));
            save.Items.Add(new Card {Title=T("What file appears when you save?","저장하면 어떤 파일이 생기나요?"),
                Lines=new[]{
                    T("Click [[Save cover]] and one new picture file (JPG) appears on your computer. The original is left as it is, and only the cover is made as a new, separate file.",
                      "[[표지 저장]]을 누르면 컴퓨터에 새 사진 파일(JPG)이 하나 생겨요. 원본은 그대로 두고 표지만 새 파일로 따로 만드는 거예요."),
                    T("A cover file's name is the original's name followed by ‘cover’ and a short tag of letters and numbers. Thanks to the tag, names don't clash even when you make several covers from one original.",
                      "표지 파일 이름은 원본 이름 뒤에 ‘cover’와 영문·숫자로 된 짧은 꼬리표가 붙어요. 꼬리표 덕분에 원본 하나로 표지를 여러 장 만들어도 이름이 겹치지 않아요."),
                    T("The cover is cut straight from the original and saved at the best quality. If the original is sharp, the cover is sharp too.",
                      "표지는 원본에서 그대로 잘라 내어 가장 좋은 화질로 저장해요. 원본이 선명하면 표지도 선명해요.")}});
            save.Items.Add(new Setting {Id="place",SectionId="save",Title=T("Where should the covers you make be saved?","만든 표지를 어디에 저장할까요?"),
                Lines=new[]{T("Choose one of three places to save cover files. The examples below are for a cover made from ‘Puppy.jpg’ in a folder called ‘Photos’.",
                    "표지 파일을 저장할 곳을 세 가지 중에서 골라요. 아래 예시는 ‘사진’ 폴더 안의 ‘강아지.jpg’로 표지를 만들었을 때예요.")},
                Choices=new[]{
                    new Choice(T("① Gather them in one cover folder","① 표지 폴더 한 곳에 모으기"),
                        T("Whichever folder's photo it is made from, every cover gathers in the one cover folder chosen below. Good when you want to keep just the covers in one place.\nExample: cover folder › Puppy-cover-1a2b….jpg",
                          "어느 폴더의 사진으로 만들든 표지는 모두 아래에서 정한 표지 폴더 한 곳에 모여요. 표지만 한곳에 모아 두고 싶을 때 좋아요.\n예) 표지 폴더 › 강아지-cover-1a2b….jpg")),
                    new Choice(T("② Save it in the same folder as the original","② 원본과 같은 폴더에 저장하기"),
                        T("The cover is saved together in the folder where the original is. The cover's name starts with the original's name, so when you sort by name in File Explorer, the cover comes right after the original. Good when you always want the original and the cover together. In LoveMyShelf, this cover file doesn't take a tile of its own; it shows as the cover in the original's tile.\nExample: Photos › Puppy.jpg, Puppy_cover_1a2b….jpg",
                          "표지가 원본이 있는 폴더에 함께 저장돼요. 표지 이름이 원본 이름으로 시작해서, 파일 탐색기에서 이름순으로 보면 원본 바로 다음에 표지가 나와요. 원본과 표지를 항상 붙여 두고 싶을 때 좋아요. LoveMyShelf에서는 이 표지 파일이 칸을 따로 차지하지 않고, 원본 칸에 표지로 보여요.\n예) 사진 › 강아지.jpg, 강아지_cover_1a2b….jpg")),
                    new Choice(T("③ Make a ‘Love My Shelf 표지’ folder inside the original's folder and save it there","③ 원본 폴더 안에 ‘Love My Shelf 표지’ 폴더를 만들어 저장하기"),
                        T("A folder named ‘Love My Shelf 표지’ is made automatically inside the original's folder, and covers are saved in it. Good when you want covers gathered per folder but don't want them mixed in among the originals.\nExample: Photos › Love My Shelf 표지 › Puppy-cover-1a2b….jpg",
                          "원본이 있는 폴더 안에 ‘Love My Shelf 표지’라는 폴더가 자동으로 생기고, 표지는 그 안에 저장돼요. 폴더별로 표지를 따로 모으고 싶지만 원본 사이에 섞이는 건 싫을 때 좋아요.\n예) 사진 › Love My Shelf 표지 › 강아지-cover-1a2b….jpg"))},
                Default=0,Get=p=>(int)p.CoverPlace,Set=(p,i)=>p.CoverPlace=(CoverPlace)i,
                Tips=new[]{T("Even with ② or ③, if the place can't be saved to, such as a connected phone or some cloud folders, the cover is saved in the cover folder instead and you get a notice.",
                    "②나 ③을 골랐더라도 연결한 휴대폰이나 일부 클라우드 폴더처럼 저장할 수 없는 곳이면, 표지 폴더에 대신 저장하고 알려 드려요."),
                    T("Even if you change this setting, covers already made stay where they are. Covers made after the change are saved in the new place.","설정을 바꿔도 이미 만든 표지는 원래 자리에 그대로 있어요. 바꾼 뒤에 만드는 표지부터 새 위치에 저장돼요."),
                    T("Wherever they are saved, you can see every cover in [[Cover collection]].","어디에 저장하든 [[표지 O 모음]]에서는 모든 표지를 볼 수 있어요.")}});
            save.Items.Add(new Setting {Id="folder",SectionId="save",Title=T("Which folder is the cover folder?","표지 폴더는 어디로 할까요?"),
                Lines=new[]{T("With ① above, covers gather here. With ② or ③, this is the folder used instead when the original's folder can't be saved to. The folder you chose when you first saved a cover is set here.",
                    "위에서 ①을 고르면 표지가 여기에 모여요. ②나 ③을 고르면 원본 폴더에 저장할 수 없을 때 대신 쓰는 폴더예요. 처음 표지를 저장할 때 정한 폴더가 지정돼 있어요.")},
                Get=p=>0,Set=(p,i)=>{},Extra=g=>g.CoverFolderPicker(),
                Tips=new[]{T("Even if you change to another folder, covers already made are not moved.","다른 폴더로 바꿔도 이미 만든 표지는 옮겨지지 않아요.")}});
            save.Items.Add(new Card {Title=T("The Open cover folder button","표지 폴더 열기 버튼"),
                Lines=new[]{T("Click [[Open cover folder]] next to [[Sort]] at the bottom right and the folder with your covers opens in File Explorer. If you chose ② or ③, the folder you're looking at (or the ‘Love My Shelf 표지’ folder inside it) opens.",
                    "오른쪽 아래 [[정렬]] 옆의 [[표지 폴더 열기]]를 누르면 표지가 들어 있는 폴더가 파일 탐색기에서 열려요. ②나 ③을 골랐다면 지금 보고 있는 폴더(또는 그 안의 ‘Love My Shelf 표지’ 폴더)가 열려요.")}});

            // ----- Scenes from videos -----
            var video=add("video",T("Scenes from videos","영상에서 장면 고르기"));
            video.Items.Add(new Card {Title=T("Pick exactly the moment you want","원하는 순간을 정확히 골라요"),
                Lines=new[]{T("A video is a great many scenes following one another quickly. You can pick exactly one of them and make it a cover.",
                    "영상은 수많은 장면이 빠르게 이어지는 거예요. 그중 딱 한 장면을 골라 표지로 만들 수 있어요.")},
                Steps=new[]{T("Open a video tile, play it, and click [[Pause]] near the moment you want.","영상 칸을 열어 재생하다가 원하는 순간 근처에서 [[일시정지]]를 눌러요."),
                    T("While paused, press {{D}} and {{F}} to move one scene back or forward.","일시정지한 상태에서 {{D}}와 {{F}}를 누르면 한 장면씩 뒤로·앞으로 이동해요."),
                    T("Click [[Make cover]] and the scenes before and after the paused spot appear as small pictures. Click the scene you like best.","[[표지 만들기]]를 누르면 멈춘 곳 앞뒤의 장면들이 작은 그림으로 나와요. 가장 마음에 드는 장면을 눌러요."),
                    T("Fit it in the frame just as with a photo, and click [[Save cover]]. In the Make cover screen too, {{D}} and {{F}} go to the previous and next scene.","사진과 똑같이 테두리에 맞추고 [[표지 저장]]을 눌러요. 표지 만들기 화면에서도 {{D}}와 {{F}}로 이전·다음 장면으로 넘어갈 수 있어요.")},
                Keys=new[]{K(T("One scene back / forward","한 장면 뒤로 / 앞으로"),"D","F"),K(T("Skip back / forward","뒤로 / 앞으로 건너뛰기"),"←","→"),K(T("Skip one second","1초 건너뛰기"),"Shift","←","→")},
                Tips=new[]{T("Click [[All scenes]] in the scene list to see every scene of the whole video.","장면 목록에서 [[모든 장면]]을 누르면 영상 전체의 장면을 다 볼 수 있어요.")}});
            video.Items.Add(new Card {Title=T("Draw where to cut while watching","보면서 자를 곳을 네모로 그려 둬요"),
                Lines=new[]{T("You can draw the part you want as a box over the playing video, GIF or WebP. Make cover then starts from that box.",
                    "재생 중인 영상·GIF·WebP 위에 원하는 부분을 네모로 그려 둘 수 있어요. 그러면 표지 만들기가 그 네모로 시작해요.")},
                Steps=new[]{T("Click [[▭ Where to cut ▾]] at the bottom and drag over the picture. It is also under [[Where to cut]] in the right-click controls.","아래쪽의 [[▭ 자를 곳 ▾]]를 누르고 화면을 끌어요. 오른쪽 클릭 조작 창의 [[자를 곳]]에도 있어요."),
                    T("Drag inside the box to move it, and drag a corner to resize it. To type its place and size as a % of the picture, use the boxes that open from [[▭ Where to cut ▾]].","네모 안쪽을 끌면 옮겨지고, 모서리를 끌면 크기가 바뀌어요. 위치와 크기를 사진의 몇 %인지 숫자로 적으려면 [[▭ 자를 곳 ▾]]를 누르면 나오는 칸에 적어요."),
                    T("In the same place, turn on [[🔒 Keep this shape]] to keep the shape while drawing; pick it with [[Fill the grid's cell]] and [[Fill two cells side by side]] or type width:height.","같은 곳에서 [[🔒 비율 고정]]을 켜면 그려도 모양이 그대로예요. 모양은 [[칸에 꽉 맞추기]]·[[칸 두 개에 꽉 맞추기]]나 가로:세로 숫자로 정해요."),
                    T("[[⛶ View cut area large]] shows only the box, as big as the window, while the video keeps playing. Click it again or press Esc to go back.","[[⛶ 자를 곳 크게 보기]]를 누르면 네모 부분만 창 가득 크게 나오고 영상은 계속 재생돼요. 한 번 더 누르거나 Esc를 누르면 원래대로 돌아와요."),
                    T("Click [[Make a cover from this box]] (or [[Make cover]]). Choose a scene shows only the inside of the box, the Make cover screen starts from it, and a GIF cover made there uses the same place.","[[이 네모로 표지 만들기]](또는 [[표지 만들기]])를 눌러요. 장면 고르기에도 네모 안쪽만 보이고, 표지 만들기 화면이 이 네모로 시작하고, 거기서 만드는 GIF 표지도 같은 자리를 써요.")},
                Keys=new[]{K(T("Done drawing (the box stays)","다 그렸어요(네모는 남아요)"),"Enter"),K(T("Clear the box","네모 지우기"),"Esc"),K(T("Bring back the cleared box","지운 네모 되살리기"),"Ctrl+Z")},
                Tips=new[]{T("Esc does one thing at a time: it closes the open choices, then leaves View cut area large, then clears the box. With no box, Esc closes the window as before. The box is apart from zooming and turning: zoom in with the wheel and it stays on the same part of the picture. A double-click still plays or pauses, and right-clicking the playback bar still repeats a part. The box goes when you move to another file.",
                    "Esc는 한 번에 하나씩 해요. 펼친 창을 닫고, 그다음 자를 곳 크게 보기를 풀고, 그다음 네모를 지워요. 네모가 없으면 예전처럼 창이 닫혀요. 네모는 확대·회전과 따로라서 휠로 확대해도 사진의 같은 곳에 그대로 있어요. 그리는 동안에도 더블클릭은 일시정지·재생, 재생바 오른쪽 클릭은 구간 반복 그대로예요. 다른 파일로 넘어가면 네모는 사라져요.")}});
            video.Items.Add(Numbers("skip",T("How far should the ← and → keys skip?","←, → 키로 얼마나 건너뛸까요?"),new[]{3,5,10,30},5,
                p=>p.SkipSeconds,(p,v)=>p.SkipSeconds=v,v=>T(v+" seconds",v+"초"),
                v=>v==3?T("A little at a time","조금씩"):v==5?T("Usual","보통"):v==10?T("Longer","길게"):T("For getting through long videos quickly","긴 영상을 빨리 넘길 때"),4));
            ((Setting)video.Items.Last()).Lines=new[]{T("While watching a video, the → key skips forward and the ← key skips back. Choose how many seconds one press skips.",
                "영상을 볼 때 → 키를 누르면 앞으로, ← 키를 누르면 뒤로 건너뛰어요. 한 번 누를 때 몇 초씩 건너뛸지 골라요.")};
            Typed((Setting)video.Items.Last(),T("seconds","초"),1,300,1);
            video.Items.Add(Numbers("brightnessStep",T("How much should E and W change the brightness?","E, W 키로 밝기를 얼마나 바꿀까요?"),new[]{1,2,3,4,5,10},3,
                p=>p.BrightnessStep,(p,v)=>p.BrightnessStep=v,v=>v.ToString(),
                v=>v==1?T("Finest","가장 세밀하게"):v==3?T("Usual","보통"):v==10?T("Big","크게"):"",3));
            ((Setting)video.Items.Last()).Lines=new[]{T("While watching a video or making a cover, E makes it brighter and W makes it darker. Each press of R moves to the next style. Soft (the usual one) brightens faces gently and brightly, and See in the dark lifts only the dark parts a lot, so things are easy to tell apart even in a very dark video. Milky brightens the whole screen as if laying a white veil over it. At +100 it turns all white. Q returns it to the original brightness, and moving to another file returns both to the start. Choose how much one press changes it.",
                "영상을 보거나 표지를 만들 때 E를 누르면 밝아지고 W를 누르면 어두워져요. R을 누를 때마다 방식이 차례로 바뀌어요. 부드럽게(기본)는 얼굴을 은은하고 환하게 밝혀 주고, 어두운 곳 보기는 어두운 부분만 크게 들어 올려서 아주 어두운 영상에서도 물체가 잘 구분돼요. 뽀얗게는 화면 전체에 흰 막을 씌우듯 밝혀요. +100이면 하얗게 돼요. Q를 누르면 원래 밝기로 돌아오고, 다른 파일로 넘어가면 밝기와 방식이 모두 처음으로 돌아와요. 한 번 누를 때 얼마나 바뀔지 골라요.")};
            Typed((Setting)video.Items.Last(),"",1,20,1);
            video.Items.Add(Numbers("rotationStep",T("How far should O and P turn the picture?","O, P 키로 얼마나 회전할까요?"),new[]{1,5,10,15,30,45},10,
                p=>p.RotationStep,(p,v)=>p.RotationStep=v,v=>T(v+"°",v+"도"),
                v=>v==1?T("Finest. But it can stutter","가장 세밀해요. 대신 버벅일 수 있어요"):v==10?T("Usual","보통"):v==45?T("Big","크게"):"",3));
            ((Setting)video.Items.Last()).Lines=new[]{T("In the large view, P turns the picture to the right and O to the left. I puts it back. Choose how many degrees one press turns it. Hold the key and it keeps turning, and it rests a moment at every 90°, so it is easy to stand the picture straight.",
                "크게 보기에서 P를 누르면 오른쪽으로, O를 누르면 왼쪽으로 회전해요. I를 누르면 원래대로 돌아와요. 한 번 누를 때 몇 도씩 회전할지 골라요. 누르고 있으면 계속 돌고, 90도마다 잠깐 멈춰서 똑바로 세우기 쉬워요."),
                T("It is for getting a rough feel of how the picture would look turned, before you make a cover. A video rests as a still picture while it turns, and plays on from the same scene once you stop.",
                "표지를 만들기 전에 돌리면 어떻게 보일지 대략 감을 잡는 용도예요. 영상은 돌리는 동안 그림으로 멈춰 있다가, 손을 떼면 같은 장면부터 다시 재생돼요.")};
            Typed((Setting)video.Items.Last(),T("degrees","도"),1,45,1);
            video.Items.Add(Numbers("sceneStep",T("How many seconds apart should one scene be? (scene step)","한 장면의 간격은 몇 초로 할까요? (장면 간격)"),new[]{150,200,250,500},200,
                p=>p.SceneStepMs,(p,v)=>p.SceneStepMs=v,v=>T((v/1000.0).ToString("0.###",System.Globalization.CultureInfo.InvariantCulture)+" s",(v/1000.0).ToString("0.###",System.Globalization.CultureInfo.InvariantCulture)+"초"),
                v=>v==150?T("The closest together. Takes long to load","가장 촘촘해요. 불러오는 데 오래 걸려요"):v==200?T("Hardly misses even a blink or a wink","눈 깜빡임이나 윙크도 거의 놓치지 않아요"):v==500?T("The fastest. But the scenes are far apart","가장 빨라요. 대신 듬성듬성해요"):"",4));
            var stepSetting=(Setting)video.Items.Last();
            stepSetting.Lines=new[]{T("In LoveMyShelf, ‘one scene’ is this far apart everywhere. The scene list, the D and F keys, the preview pictures above the playback bar and the Make GIF cover window all move by this step. The smaller it is, the closer together the scenes are, but the longer they take to load. A video can't be divided more finely than the frames it has, so a smaller value is set to the closest one possible.",
                "LoveMyShelf에서 ‘한 장면’은 어디서든 이 간격이에요. 장면 고르기 목록, D·F 키, 재생바 위의 미리보기 그림, GIF 표지 만들기 창이 모두 이 간격으로 움직여요. 작을수록 촘촘하지만 불러오는 데 오래 걸려요. 영상이 원래 가진 프레임보다 더 잘게 나눌 수는 없어서, 그보다 작은 값을 넣으면 가장 가까운 값으로 맞춰져요.")};
            Typed(stepSetting,T("seconds","초"),0.03,2,0.01,1000);
            video.Items.Add(new Setting {Id="capture",SectionId="video",Title=T("Where should pictures captured with the / key be saved?","/ 키로 캡처한 사진은 어디에 저장할까요?"),
                Lines=new[]{T("While watching a video, press the {{/}} key near the arrow keys, or right-click the screen and click [[Capture scene]], and the scene you see is saved as a picture right away. The file name includes the original's name and the time in the video. The original stays as it is.",
                    "영상을 보다가 방향키 근처의 {{/}} 키를 누르거나, 화면을 오른쪽 클릭하고 [[장면 캡처]]를 누르면 지금 보이는 장면이 바로 사진으로 저장돼요. 파일 이름에는 원본 이름과 영상 속 시간이 들어가요. 원본은 그대로예요.")},
                Get=p=>0,Set=(p,i)=>{},Extra=g=>g.CaptureFolderPicker()});
            video.Items.Add(new Setting {Id="captureLook",SectionId="video",Title=T("What should a captured picture look like?","캡처할 때 어떤 모습으로 저장할까요?"),
                Choices=new[]{new Choice(T("The video's original frame","영상 원본 프레임"),T("Saved at the video's original size. Brightness changed with E and W is not included.","영상 원래 크기로 저장해요. E, W로 바꾼 밝기는 들어가지 않아요.")),
                    new Choice(T("Just as it looks on screen now","지금 화면에 보이는 그대로"),T("Saved at the size and brightness you see now. Buttons, black bars and subtitles are not included.","지금 보이는 크기와 밝기로 저장해요. 버튼과 검은 여백, 자막은 들어가지 않아요."))},
                Default=0,Get=p=>p.CaptureAsShown?1:0,Set=(p,i)=>p.CaptureAsShown=i==1});
            video.Items.Add(new Setting {Id="captureFormat",SectionId="video",Title=T("What kind of file should a captured picture be?","캡처한 사진은 어떤 파일로 저장할까요?"),
                Choices=new[]{new Choice("PNG",T("The picture quality stays as it is. The file is big.","화질이 그대로예요. 파일이 커요.")),
                    new Choice("JPG",T("The file is small. The picture quality drops a tiny bit.","파일이 작아요. 화질은 아주 조금 줄어요."))},
                Default=0,Get=p=>p.CaptureJpg?1:0,Set=(p,i)=>p.CaptureJpg=i==1});

            // ----- GIF covers -----
            var gif=add("gif",T("Making GIF covers","GIF 표지 만들기"));
            gif.Items.Add(new Card {Title=T("JPG covers and GIF covers","JPG 표지와 GIF 표지"),
                Lines=new[]{T("A still cover is called a JPG cover, and a moving cover is called a GIF cover. A moving cover is called a GIF cover whichever file it is saved as: GIF, WebP or MP4.",
                    "멈춰 있는 표지는 JPG 표지, 움직이는 표지는 GIF 표지라고 불러요. 움직이는 표지는 GIF·WebP·MP4 중 어떤 파일로 저장하든 모두 GIF 표지라고 불러요."),
                    T("A GIF cover is made from a JPG cover that came from a video, GIF or WebP. It is the few seconds before and after the cover's scene, moving with the same frame, rotation and brightness as the JPG cover.",
                      "GIF 표지는 영상·GIF·WebP로 만든 JPG 표지를 바탕으로 만들어요. 표지 장면의 앞뒤 몇 초를 JPG 표지와 똑같은 테두리·회전·밝기로 움직이게 한 거예요."),
                    T("[[Make GIF cover]] is in the Make cover screen, the large view, and a tile's right-click menu.","[[GIF 표지 만들기]]는 표지 만들기 화면, 크게 보기 화면, 칸의 오른쪽 클릭 메뉴에 있어요."),
                    T("A tile shows the JPG cover first, and the GIF cover moves when you point at it (you can change this below).","칸에는 JPG 표지가 먼저 보이고, 마우스를 올리면 GIF 표지가 움직여요(아래에서 바꿀 수 있어요)."),
                    T("You can also set the part while watching the video: press {{[}} where it starts and {{]}} where it ends. The playback bar shows the part as a pale band and only that part plays again and again. Then make a cover as usual from a scene inside the part, and Make GIF cover opens on that part. A part can be up to 60 seconds. {{\\}} clears it. With the mouse: right-click two places on the playback bar and the part between them repeats (in either order); a third right-click starts a new part, and Shift + right-click clears it.",
                      "영상을 보면서 구간을 미리 정할 수도 있어요. 시작할 곳에서 {{[}}, 끝낼 곳에서 {{]}}를 눌러요. 재생바에 구간이 옅은 띠로 표시되고 그 구간만 되풀이해서 재생돼요. 그다음 평소처럼 구간 안의 장면으로 표지를 만들면 GIF 표지 만들기 창이 그 구간으로 열려요. 구간은 60초까지 정할 수 있어요. {{\\}}를 누르면 구간이 지워져요. 마우스로는 재생바의 두 곳을 오른쪽 클릭하면 그 사이가 반복돼요(순서는 상관없어요). 세 번째로 오른쪽 클릭하면 새 구간을 시작하고, Shift+오른쪽 클릭이면 지워져요.")}});
            gif.Items.Add(new Card {Title=T("Set the part on the scene strip","장면 띠에서 구간을 정해요"),
                Lines=new[]{T("At the bottom of the Make GIF cover window is a strip with the video's scenes laid out in a long row. Over the pictures runs a thin bar like a slider: its colored part is what goes into the GIF, and the two round handles at the ends of that part are the start and the end. The small card on that bar is the JPG cover's scene, and the round dot moving under them is where the preview is playing now.",
                    "GIF 표지 만들기 창 아래쪽에는 영상의 장면이 가로로 길게 늘어선 띠가 있어요. 그림 위쪽에 슬라이더처럼 생긴 얇은 줄이 있어요. 색이 칠해진 부분이 GIF에 들어갈 구간이고, 그 양 끝의 동그란 손잡이 두 개가 시작과 끝이에요. 그 줄 위의 작은 카드 표시는 JPG 표지 장면이고, 그림 아래쪽에서 움직이는 동그란 점은 미리보기가 지금 재생 중인 곳이에요.")},
                Steps=new[]{T("Drag the two handles to the start and the end. The handles move one scene at a time (by the scene step under ‘Scenes from videos’), and the ‹ › buttons under the strip also move them one scene at a time.","손잡이 두 개를 시작과 끝 위치로 끌어요. 손잡이는 한 장면씩(‘영상에서 장면 고르기’의 장면 간격만큼) 움직이고, 띠 아래의 ‹ › 버튼도 한 장면씩 옮겨 줘요."),
                    T("Drag the pictures in the strip, or roll the mouse wheel over the strip, to see other parts of the video. Each notch of the wheel moves a quarter of the strip: down goes later, up goes earlier. The handles stay where they are. Click [[Back to the cover]] to return to the cover's scene.","띠의 그림을 끌거나 띠 위에서 마우스 휠을 굴리면 영상의 다른 부분을 볼 수 있어요. 휠 한 칸에 띠의 4분의 1씩 움직이고, 아래로 굴리면 뒤쪽, 위로 굴리면 앞쪽으로 가요. 손잡이는 그 자리에 그대로 있어요. [[표지로 돌아가기]]를 누르면 표지 장면으로 돌아와요."),
                    T("Double-click outside the part and the part grows to there: to the left of the part the start comes there, to the right of it the end. It is quicker than dragging when the place is far. Double-click inside the part and an end comes in to there: before the cover's scene the start, after it the end.","구간 바깥을 더블클릭하면 구간이 그곳까지 늘어나요. 구간 왼쪽이면 시작이, 오른쪽이면 끝이 그곳으로 와요. 멀리 옮길 때는 끄는 것보다 빨라요. 구간 안쪽을 더블클릭하면 끝 하나가 그곳으로 당겨져요. 표지 장면보다 앞이면 시작이, 뒤면 끝이 와요."),
                    T("Choose the speed and the play style. The speed changes by 0.1× at a time with [[−]] and [[+]] (or {{X}} and {{C}}), from 0.2× to 4×; hold one and it keeps changing, and {{Z}} returns it to 1×. {{V}} goes straight to 2×, and pressing it again returns to 1×. [[Loop]] keeps repeating from start to end, [[Back and forth]] goes to the end and then comes back in reverse, and [[Backward]] plays in reverse from the end.","속도와 재생 방식을 골라요. 속도는 [[−]]와 [[+]](또는 {{X}}, {{C}})로 0.1배씩 바꿔요. 0.2배부터 4배까지 되고, 누르고 있으면 계속 바뀌어요. {{Z}}를 누르면 1배로 돌아와요. {{V}}를 누르면 바로 2배가 되고, 한 번 더 누르면 1배로 돌아와요. [[반복]]은 처음부터 끝까지를 계속 반복하고, [[왕복]]은 끝까지 갔다가 거꾸로 돌아오고, [[거꾸로]]는 끝에서부터 거꾸로 재생해요."),
                    T("When you change the part, the preview shows at the top right away. If you like it, click [[Make]]. The window closes right away and the real file is made in the background. To see how it will be saved first, click [[See how the real file looks]] on the left. (If you choose ‘By itself in the background’ in the settings below, the real file is made by itself while you wait and the preview changes to it.)","구간을 바꾸면 위쪽에 미리보기가 바로 나와요. 마음에 들면 [[만들기]]를 눌러요. 창은 바로 닫히고 실제 파일은 뒤에서 만들어져요. 저장될 모습을 먼저 보고 싶으면 왼쪽의 [[실제 모습 미리 보기]]를 눌러요. (아래 설정에서 ‘뒤에서 알아서 만들기’를 골라 두면, 기다리는 동안 실제 파일이 저절로 만들어지고 미리보기가 그 파일로 바뀌어요.)")},
                Keys=new[]{K(T("Move the handle you last held one scene at a time","마지막에 잡은 손잡이를 한 장면씩 옮기기"),"←","→"),K(T("See earlier and later scenes","앞뒤 장면 보기"),T("Wheel","휠")),K(T("Bring the start or the end to a place outside the part","구간 바깥으로 시작이나 끝 옮기기"),T("Double-click","더블클릭")),K(T("Original speed / 0.1× slower / 0.1× faster / 2×","원래 속도 / 0.1배 느리게 / 0.1배 빠르게 / 2배"),"Z","X","C","V"),K(T("Pause / play the preview","미리보기 일시정지 / 재생"),"Space")},
                Tips=new[]{T("The making runs by itself in the background, so you can keep using LoveMyShelf meanwhile. When it's done, a notice appears at the bottom right.","만드는 작업은 뒤에서 알아서 진행되니까 그동안 LoveMyShelf를 계속 써도 돼요. 다 되면 오른쪽 아래에 알려 드려요.")}});
            gif.Items.Add(new Card {Title=T("GIF, WebP, MP4: what's the difference?","GIF·WebP·MP4는 무엇이 다른가요?"),
                Lines=new[]{T("A GIF cover can be saved in one of three file formats. None of the three has sound. The sizes below are for a cover 3 seconds long, at 15 pictures a second and 960 pixels tall.",
                    "GIF 표지는 세 가지 파일 형식 중 하나로 저장할 수 있어요. 셋 다 소리는 들어가지 않아요. 아래 용량은 3초 길이, 1초에 15장, 세로 960픽셀로 만들었을 때 기준이에요.")}});
            gif.Items.Add(new Card {Title="GIF",
                Lines=new[]{T("The most widely used format for moving pictures, so it moves almost everywhere.",
                    "가장 널리 쓰이는 움직이는 그림 형식이라 거의 모든 곳에서 움직여요."),
                    T("But each picture can use only up to 256 colors. So where colors change smoothly, as on faces or under stage lights, tiny dot patterns or step-like lines can appear. LoveMyShelf mixes the dots evenly so that it shows as little as possible.",
                      "대신 그림 한 장에 색을 256가지까지만 쓸 수 있어요. 그래서 얼굴이나 조명처럼 색이 부드럽게 이어지는 부분에 자잘한 점무늬나 계단 같은 줄이 생길 수 있어요. LoveMyShelf는 점을 고르게 섞어서 최대한 티가 나지 않게 만들어요."),
                    T("Its files are the biggest of the three: usually 7 to 13 MB for the example above. The longer and bigger you make it, the faster the size grows.",
                      "셋 중 용량이 가장 커요. 위 기준으로 보통 7~13MB예요. 길고 크게 만들수록 용량이 빠르게 늘어요.")},
                Tips=new[]{
                    T("It moves on Discord, X (Twitter) and similar places too. But there is a limit on the size of file you can upload.","디스코드, X(트위터) 같은 곳에서도 움직여요. 다만 올릴 수 있는 파일 용량에 제한이 있어요.")}});
            gif.Items.Add(new Card {Title="WebP",
                Lines=new[]{T("A fairly new format made by Google. It moves like a GIF but holds colors much more smoothly, and its size is about a tenth of a GIF (about 1 MB for the example above).",
                    "구글이 만든 비교적 새로운 형식이에요. GIF처럼 움직이면서도 색을 훨씬 곱게 담고, 용량은 GIF의 10분의 1 정도예요(위 기준으로 약 1MB)."),
                    T("But some places don't support it yet. In older programs and some apps, only the first scene shows as a still picture, or it doesn't open at all.",
                      "대신 아직 지원하지 않는 곳이 있어요. 오래된 프로그램이나 일부 앱에서는 첫 장면만 멈춘 그림으로 보이거나 아예 열리지 않아요."),
                    T("It is the best for keeping and viewing inside LoveMyShelf. It moves smoothly in the tiles and takes up little space.",
                      "LoveMyShelf 안에서 모아 두고 보기에는 가장 좋아요. 칸에서도 부드럽게 움직이고 용량도 적게 차지해요.")},
                Tips=new[]{T("It moves like a GIF on Discord too.","디스코드에서도 GIF처럼 움직여요.")}});
            gif.Items.Add(new Card {Title="MP4",
                Lines=new[]{T("A video format. Of the three, its size is the smallest (about 0.8 MB for the example above) and its picture quality is the best.",
                    "동영상 형식이에요. 셋 중 용량이 가장 작고(위 기준으로 약 0.8MB) 화질도 가장 좋아요."),
                    T("But it is treated as a video, not a picture. Depending on where you post it, it may get a play button, play only when clicked, or play once and stop. Whether it keeps repeating depends on where you post it.",
                      "대신 그림이 아니라 동영상으로 취급돼요. 올리는 곳에 따라 재생 버튼이 붙거나, 눌러야 재생되거나, 한 번만 재생되고 멈출 수 있어요. 계속 반복 재생될지는 올리는 곳에 따라 달라요."),
                    T("It can be viewed anywhere that videos can be posted.","동영상을 올릴 수 있는 곳이라면 어디서든 볼 수 있어요.")}});
            gif.Items.Add(new Card {Title=T("Which one is good to choose?","어떤 걸 고르면 좋을까요?"),
                Lines=new[]{T("Not sure → WebP: smooth colors, and the best for keeping and viewing in LoveMyShelf. It moves on Discord too.","잘 모르겠다면 → WebP: 색이 곱고, LoveMyShelf 안에서 모아 보기에 가장 좋아요. 디스코드에서도 움직여요."),
                    T("Posting it where WebP doesn't move → GIF: it moves wherever you post it.","WebP가 움직이지 않는 곳에 올릴 거라면 → GIF: 어디에 올려도 움직여요."),
                    T("Fine with it going up as a video → MP4: the smallest size and sharp.","동영상으로 올라가도 괜찮다면 → MP4: 용량이 가장 작고 선명해요.")}});
            gif.Items.Add(new Setting {Id="gifFormat",SectionId="gif",Title=T("Which format should a GIF cover start as?","GIF 표지를 처음에 어떤 형식으로 만들까요?"),
                Lines=new[]{T("This is the format selected at first when the Make GIF cover window opens. You can also change it each time in that window.",
                    "GIF 표지 만들기 창을 열었을 때 처음 선택돼 있을 형식이에요. 그 창에서 그때그때 바꿀 수도 있어요.")},
                Choices=new[]{new Choice("GIF",T("Moves everywhere · big size","어디서나 움직여요 · 용량이 커요")),
                    new Choice("WebP",T("Smooth colors, small size · doesn't work in some places","색이 곱고 용량이 작아요 · 안 되는 곳도 있어요")),
                    new Choice("MP4",T("Smallest size and sharp · goes up as a video","용량이 가장 작고 선명해요 · 동영상으로 올라가요"))},
                Default=1,Get=p=>p.GifFormat,Set=(p,i)=>p.GifFormat=i});
            gif.Items.Add(new Setting {Id="gifQuality",SectionId="gif",Title=T("How high should the picture quality be?","화질은 어느 정도로 할까요?"),
                Lines=new[]{T("The higher the picture quality, the better it looks, but the bigger the size gets. For GIF it sets how finely the 256 colors are mixed; for WebP and MP4 it sets how much the picture is compressed.",
                    "화질이 높을수록 보기 좋지만 용량이 커져요. GIF에서는 256가지 색을 얼마나 곱게 섞을지, WebP와 MP4에서는 그림을 얼마나 압축할지를 정해요.")},
                Choices=new[]{new Choice(T("High","높게"),T("The smoothest colors.","색이 가장 고와요.")),
                    new Choice(T("Normal","보통"),T("This is enough for most covers.","대부분의 표지는 이걸로 충분해요.")),
                    new Choice(T("Low","낮게"),T("The size gets smaller. A GIF uses only 128 colors, so stripes may show in shadows on faces.","용량이 작아져요. GIF는 색을 128가지만 써서 얼굴 그림자에 줄무늬가 보일 수 있어요."))},
                Default=0,Get=p=>p.GifQuality,Set=(p,i)=>p.GifQuality=i});
            var gifPrefs=Prefs;
            var gifRates=MotionWindow.Presets(true,gifPrefs.GifHighFps);
            var gifSizes=MotionWindow.Presets(false,gifPrefs.GifLargeSize);
            gif.Items.Add(Numbers("gifFps",T("How many pictures per second? (smoothness)","1초에 몇 장으로 만들까요? (부드러움)"),gifRates,30,
                p=>p.GifFps,(p,v)=>p.GifFps=v,v=>T(v+" a second","1초에 "+v+"장"),
                v=>v==10?T("Looks choppy · small size","뚝뚝 끊겨 보여요 · 용량이 작아요"):v==15?T("A good balance of smoothness and size","부드러움과 용량이 적당해요"):v==24?T("Same as movies","영화와 같아요"):v==30?T("Very smooth","아주 부드러워요"):"",3));
            ((Setting)gif.Items.Last()).Lines=new[]{T("A moving picture is many pictures shown quickly one after another. The more pictures per second, the smoother it is, but the bigger the size gets. At 30 a second the size is about twice that at 15.",
                "움직이는 그림은 그림 여러 장을 빠르게 넘겨서 보여 주는 거예요. 1초에 넘기는 장수가 많을수록 부드럽지만 그만큼 용량도 커져요. 1초에 30장이면 15장일 때보다 용량이 약 2배가 돼요.")};
            Typed((Setting)gif.Items.Last(),T("pictures","장"),1,MotionWindow.FpsLimit(gifPrefs.GifHighFps),1);
            gif.Items.Add(Numbers("gifHeight",L.P("What size should it start at?","크기는 얼마로 시작할까요?"),gifSizes,1280,
                p=>p.GifHeight,(p,v)=>p.GifHeight=v,v=>T(v+" pixels",v+"픽셀"),
                v=>v==480?T("For viewing small in a chat","채팅에 올려 작게 볼 때"):v==960?T("Sharp on most screens","대부분의 화면에서 선명해요"):v==1280?T("Very sharp","아주 선명해요"):"",3));
            ((Setting)gif.Items.Last()).Lines=new[]{T("The size is the width of a wide cover or the height of a tall cover. The bigger, the sharper, but the bigger the size gets. Twice as tall makes the size about four times as big. It is never made bigger than the original (if the original is smaller, it is made at the original's size).",
                "가로로 긴 표지는 가로 크기, 세로로 긴 표지는 세로 크기예요. 클수록 선명하지만 용량이 커져요. 세로가 2배가 되면 용량은 약 4배가 돼요. 원본보다 크게 만들지는 않아요(원본이 더 작으면 원본 크기로 만들어요).")};
            Typed((Setting)gif.Items.Last(),T("pixels","픽셀"),240,MotionWindow.SizeLimit(gifPrefs.GifLargeSize),10);
            gif.Items.Add(new Card {Title=L.P("Advanced","고급")});
            var gifSwitches=new[]{new Choice(L.P("Off","끄기"),""),new Choice(L.P("On","켜기"),"")};
            gif.Items.Add(new Setting {Id="gifHighFps",SectionId="gif",Title=L.P("Allow high frame rates (up to 60 a second)","높은 프레임 허용 (1초에 60장까지)"),
                Lines=new[]{L.P("Keeps the motion of 60 fps video. Files get bigger, and making them and previewing them take longer.",
                    "60fps 영상의 움직임을 그대로 살려요. 대신 파일이 커지고 만들기와 미리보기가 느려져요.")},
                Choices=gifSwitches,Columns=2,Default=0,Get=p=>p.GifHighFps?1:0,Set=(p,i)=>p.GifHighFps=i==1});
            gif.Items.Add(new Setting {Id="gifLargeSize",SectionId="gif",Title=L.P("Allow large sizes (up to 3840 px, 4K)","큰 크기 허용 (3840px, 4K까지)"),
                Lines=new[]{L.P("Keeps 4K originals sharp. It takes much more memory and time, so it suits a PC with a graphics card or a fast PC.",
                    "4K 원본을 선명하게 살려요. 메모리와 시간이 크게 늘어서, 그래픽카드가 있거나 빠른 PC에 맞아요.")},
                Choices=gifSwitches,Columns=2,Default=0,Get=p=>p.GifLargeSize?1:0,Set=(p,i)=>p.GifLargeSize=i==1,
                Extra=w=>UI.Text(L.P("Graphics card ","그래픽카드 ")+(Preferences.GifGraphicsCard??L.P("Unknown","이름을 확인하지 못했어요")),13,true)});
            gif.Items.Add(new Setting {Id="gifLargeScenes",SectionId="gif",Title=L.P("Read scenes at a larger size too","장면 고르기도 큰 화질로 읽기"),
                Lines=new[]{L.P("Choose a scene and Make cover read at 3840 px instead of 2200 px. Moving between scenes may be slower.",
                    "장면 고르기와 표지 만들기 화면이 2200px 대신 3840px로 읽어요. 장면을 넘길 때 느려질 수 있어요.")},
                Choices=gifSwitches,Columns=2,Default=0,Get=p=>p.GifLargeScenes?1:0,Set=(p,i)=>p.GifLargeScenes=i==1});
            gif.Items.Add(new Setting {Id="gifSide",SectionId="gif",Title=T("How many seconds before and after the cover at first?","처음에 표지 앞뒤로 몇 초를 잡을까요?"),
                Lines=new[]{T("When the Make GIF cover window opens, the two handles sit before and after the cover's scene. This sets how many seconds from the cover's scene each one sits at first. With 1.5 seconds, it is 1.5 before + 1.5 after, 3 seconds in all. You can change it any time by dragging the handles in the window.",
                    "GIF 표지 만들기 창을 열면 손잡이 두 개가 표지 장면의 앞과 뒤에 놓여 있어요. 처음에 표지 장면에서 각각 몇 초 떨어진 곳에 놓을지 정해요. 1.5초면 앞 1.5초 + 뒤 1.5초로 모두 3초짜리가 돼요. 창에서 손잡이를 끌어 언제든 바꿀 수 있어요.")},
                Choices=new[]{new Choice(T("0.5 seconds each (1 s in all)","앞뒤 0.5초씩 (모두 1초)"),""),new Choice(T("1 second each (2 s in all)","앞뒤 1초씩 (모두 2초)"),""),
                    new Choice(T("1.5 seconds each (3 s in all)","앞뒤 1.5초씩 (모두 3초)"),""),new Choice(T("2 seconds each (4 s in all)","앞뒤 2초씩 (모두 4초)"),""),
                    new Choice(T("3 seconds each (6 s in all)","앞뒤 3초씩 (모두 6초)"),"")},
                Default=1,Columns=2,Get=p=>Array.IndexOf(new[]{5,10,15,20,30},p.GifSideTenths),Set=(p,i)=>p.GifSideTenths=new[]{5,10,15,20,30}[i],
                Number=p=>p.GifSideTenths,SetNumber=(p,v)=>p.GifSideTenths=v,
                NumberName=v=>T((v/10.0).ToString("0.#",System.Globalization.CultureInfo.InvariantCulture)+" s",(v/10.0).ToString("0.#",System.Globalization.CultureInfo.InvariantCulture)+"초")});
            Typed((Setting)gif.Items.Last(),T("seconds","초"),0.1,30,0.1,10);
            gif.Items.Add(Numbers("strip",T("How many seconds should the scene strip show at first?","장면 띠에 처음 몇 초를 보여 줄까요?"),new[]{4,6,10,20,30},4,
                p=>p.GifStripSeconds,(p,v)=>p.GifStripSeconds=v,v=>T(v+" seconds",v+"초"),
                v=>v==4?T("Easy to set precisely","세밀하게 맞추기 쉬워요"):v==10?T("About right","적당해요"):v==30?T("You can see far before and after","앞뒤를 넓게 볼 수 있어요"):"",3));
            ((Setting)gif.Items.Last()).Lines=new[]{T("This sets how many seconds of the video one row of the strip shows when the Make GIF cover window first opens. The cover's scene comes in the middle. The shorter it is, the easier it is to move the handles precisely; the longer, the further before and after you can see. Drag the pictures in the strip to see the parts that weren't showing.",
                "GIF 표지 만들기 창을 처음 열었을 때 띠 한 줄에 영상을 몇 초 분량 보여 줄지 정해요. 표지 장면이 가운데에 와요. 짧을수록 손잡이를 세밀하게 옮기기 쉽고, 길수록 앞뒤를 넓게 볼 수 있어요. 띠의 그림을 끌면 안 보이던 부분도 볼 수 있어요.")};
            Typed((Setting)gif.Items.Last(),T("seconds","초"),2,120,1);
            // How the preview in the Make GIF cover window works: each person picks.
            gif.Items.Add(new Card {Title=T("Flip, plays, a pause, text and a size limit","뒤집기·재생 횟수·멈춤·글자·목표 용량"),
                Lines=new[]{T("Open [[More choices]] in the Make GIF cover window (in a wide window they are on the right).","GIF 표지 만들기 창의 [[더 보기]]를 열면 있어요(창이 넓으면 오른쪽에 바로 보여요)."),
                    T("[[Flip]]: turn the picture left ↔ right or up ↕ down. [[Plays]]: how many times it plays, 0 means forever (an MP4 always plays as a video). [[Pause at the end]]: how many seconds the last picture stays before it starts again.",
                      "[[뒤집기]]: 좌우나 위아래로 뒤집어요. [[재생 횟수]]: 몇 번 재생할지예요. 0이면 계속 반복해요(MP4는 영상이라 해당 없어요). [[끝에서 멈춤]]: 마지막 장면에서 몇 초 멈췄다가 다시 시작할지예요."),
                    T("[[Text]]: one line of text in white with a black edge, at the top, middle or bottom. [[Text size]] is a percent of the picture's height (8 is a good start).",
                      "[[글자]]: 흰 글자에 검은 테두리로 한 줄을 넣어요. 위·가운데·아래 중에 고르고, [[글자 크기]]는 그림 높이의 몇 %인지예요(8이 무난해요)."),
                    T("To put the text exactly where you want, drag it on the preview with the mouse, or type the place across and down (%) in [[Exact place]]; the place becomes [[Your own]]. The text never goes outside the picture.",
                      "글자를 원하는 자리에 정확히 놓으려면 미리보기 위의 글자를 마우스로 끌거나, [[정확한 자리]]에 가로·세로 위치(%)를 넣어요. 그러면 자리가 [[직접]]으로 바뀌어요. 글자는 그림 밖으로 나가지 않아요."),
                    T("[[Picture quality]]: besides High, Normal and Low you can type any number from 1 to 100. Higher is sharper and bigger.",
                      "[[화질]]: 높게·보통·낮게 말고도 1~100 사이 숫자를 직접 넣을 수 있어요. 높을수록 선명하고 용량이 커요."),
                    T("[[Size limit]]: tick [[Fit under]] and type a size in MB. If the file comes out bigger, it is made again smaller (the picture first, then fewer pictures a second) until it fits.",
                      "[[목표 용량]]: [[이 크기 안으로]]를 체크하고 MB를 넣어요. 만든 파일이 더 크면 알아서 다시 작게 만들어요(먼저 크기를, 그래도 크면 1초에 보이는 장 수를 줄여요).")}});
            gif.Items.Add(new Card {Title=T("Make a moving picture you already have smaller","이미 있는 움짤 용량 줄이기"),
                Lines=new[]{T("Right-click a GIF or WebP tile and choose [[Make this moving picture smaller…]]. Type the size to stay under and the quality, then press [[Make it smaller]].",
                      "GIF나 WebP 칸을 오른쪽 클릭하고 [[움짤 용량 줄이기…]]를 눌러요. 목표 용량과 화질을 넣고 [[줄이기]]를 누르면 돼요."),
                    T("A smaller copy is saved next to it as ‘name (smaller)’. The file itself stays as it is.",
                      "같은 폴더에 ‘이름 (작게)’로 작은 사본이 생겨요. 원래 파일은 그대로 둬요.")}});
            gif.Items.Add(new Card {Title=T("The preview and the real file","미리보기와 실제 파일"),
                Lines=new[]{T("The top of the Make GIF cover window shows the preview first. The preview is scenes taken from the video at wide intervals and shown right away, so it looks a little choppier and less sharp than the real file. The real file is the very file that will be saved. When the real file is made, it plays as it is in the preview's place, and the size is shown exactly.",
                    "GIF 표지 만들기 창 위쪽에는 먼저 미리보기가 나와요. 미리보기는 영상에서 장면을 듬성듬성 뽑아 바로 보여 주는 것이라, 실제 파일보다 조금 끊겨 보이고 덜 선명해요. 실제 파일은 저장될 파일 그 자체예요. 실제 파일이 만들어지면 미리보기 자리에 그 파일이 그대로 재생되고, 용량도 정확하게 표시돼요."),
                    T("Making the real file takes time. For 3 seconds long, 30 pictures a second and 1080 pixels tall, MP4 takes about 1 second, GIF about 2 to 3 seconds, and WebP about 5 seconds (it depends on the computer and the video). Even if you change the part only a little, the file is made again from the start, because every picture in it changes.",
                    "실제 파일을 만드는 데는 시간이 걸려요. 길이 3초, 1초에 30장, 세로 1080픽셀 기준으로 MP4는 약 1초, GIF는 약 2~3초, WebP는 약 5초예요(컴퓨터와 영상에 따라 달라요). 구간을 조금만 바꿔도 파일 속 그림이 전부 달라지기 때문에 처음부터 다시 만들어요."),
                    T("The four settings below choose how these two are shown. In the Make GIF cover window, [[Change how the preview works]] brings you straight here.",
                    "이 둘을 어떻게 보여 줄지는 아래 설정 네 개로 정해요. GIF 표지 만들기 창에서 [[미리보기 방식 바꾸기]]를 누르면 바로 여기로 와요.")}});
            gif.Items.Add(new Setting {Id="gifReal",SectionId="gif",Title=T("When should the real file be made?","실제 파일은 언제 만들까요?"),
                Choices=new[]{new Choice(T("By itself in the background once the part is set","구간을 정하면 뒤에서 알아서 만들기"),
                        T("Let go of the handle and wait a moment, and it starts being made in the background. When it is done the preview changes to the real file, and clicking [[Make]] finishes right away.","손잡이를 놓고 잠시 기다리면 뒤에서 만들기 시작해요. 다 되면 미리보기가 실제 파일로 바뀌고, [[만들기]]를 누르면 바로 끝나요.")),
                    new Choice(T("Only when ‘Make’ is clicked","‘만들기’를 눌렀을 때만 만들기"),
                        T("The window shows only the preview, and the size is shown as a rough value like ‘about 3 MB’. Click [[Make]] and the window closes and the file starts being made in the background.\nWhen you want to check how it will be saved first, click [[See how the real file looks]] on the left of the window.","창에서는 미리보기만 나오고, 용량은 ‘약 3MB’처럼 어림값으로 표시돼요. [[만들기]]를 누르면 창이 닫히고 뒤에서 만들기 시작해요.\n저장될 모습을 먼저 확인하고 싶을 때는 창 왼쪽의 [[실제 모습 미리 보기]]를 누르면 돼요."))},
                Default=1,Get=p=>p.GifAutoMake?0:1,Set=(p,i)=>p.GifAutoMake=i==0});
            gif.Items.Add(Numbers("gifMakeDelay",T("How long after you let go should the real file start being made?","손잡이를 놓은 뒤 얼마나 기다렸다가 만들기 시작할까요?"),new[]{500,700,1000,1500,3000},700,
                p=>p.GifMakeDelayMs,(p,v)=>p.GifMakeDelayMs=v,v=>T((v/1000.0).ToString("0.###",System.Globalization.CultureInfo.InvariantCulture)+" s",(v/1000.0).ToString("0.###",System.Globalization.CultureInfo.InvariantCulture)+"초"),
                v=>v==500?T("Starts the soonest","가장 빨리 시작해요"):v==700?T("About right","적당해요"):v==1500?T("Good when you adjust little by little","조금씩 여러 번 고칠 때 좋아요"):v==3000?T("For choosing slowly","천천히 고를 때"):"",3));
            ((Setting)gif.Items.Last()).Lines=new[]{T("After you change the part or a setting, the real file starts being made if nothing changes for this long. Change something again before that and the wait starts over. A short wait brings the real file sooner, but when you adjust little by little, half-made work gets thrown away more often. This value is used only when ‘By itself in the background’ is chosen above.",
                "구간이나 설정을 바꾼 뒤 이 시간 동안 아무것도 바꾸지 않으면 실제 파일을 만들기 시작해요. 그 전에 다시 바꾸면 처음부터 다시 기다려요. 짧으면 실제 파일이 빨리 나오지만, 조금씩 여러 번 고칠 때는 만들다 만 작업이 자주 버려져요. 위에서 ‘뒤에서 알아서 만들기’를 골랐을 때만 쓰는 값이에요.")};
            Typed((Setting)gif.Items.Last(),T("seconds","초"),0.2,10,0.1,1000);
            gif.Items.Add(Numbers("gifPreviewStep",T("How many seconds apart should the preview read scenes?","미리보기는 몇 초 간격으로 장면을 읽을까요?"),new[]{50,100,200,500},50,
                p=>p.GifPreviewStepMs,(p,v)=>p.GifPreviewStepMs=v,v=>T((v/1000.0).ToString("0.###",System.Globalization.CultureInfo.InvariantCulture)+" s",(v/1000.0).ToString("0.###",System.Globalization.CultureInfo.InvariantCulture)+"초"),
                v=>v==50?T("Very smooth","아주 부드러워요"):v==100?T("A little less smooth · uses less memory","조금 덜 부드러워요 · 메모리를 덜 써요"):v==200?T("A little choppy","조금 끊겨요"):v==500?T("Choppy · uses the least memory","뚝뚝 끊겨요 · 메모리를 가장 덜 써요"):"",4));
            var previewSetting=(Setting)gif.Items.Last();
            previewSetting.Lines=new[]{T("This value is used only for the preview in the Make GIF cover window. When you change the part, scenes read this far apart are shown right away, and when the real file is made it changes to that file. It has no effect on the finished GIF cover and has nothing to do with the scene step under ‘Scenes from videos’.",
                "GIF 표지 만들기 창의 미리보기에만 쓰는 값이에요. 구간을 바꾸면 이 간격으로 읽은 장면을 바로 보여 주고, 실제 파일이 만들어지면 그 파일로 바뀌어요. 완성된 GIF 표지에는 영향이 없고, ‘영상에서 장면 고르기’의 장면 간격과도 관계없어요."),
                T("For a video, a wider spacing does not make the preview appear sooner: the video's scenes all have to be read either way. It can't read more finely than the frames the video has. If the part you chose is long and would need more than 400 scenes, only the preview widens the spacing a little so that the computer doesn't slow down.",
                "영상은 간격을 넓혀도 미리보기가 더 빨리 나오지는 않아요. 어느 쪽이든 그 구간의 장면을 전부 읽어야 하기 때문이에요. 영상이 원래 가진 프레임보다 더 잘게 읽을 수는 없어요. 고른 구간이 길어서 장면이 400장을 넘으면, 컴퓨터가 느려지지 않도록 미리보기만 간격을 조금 넓혀요.")};
            Typed(previewSetting,T("seconds","초"),0.03,2,0.01,1000);
            gif.Items.Add(new Setting {Id="webpEffort",SectionId="gif",Title=T("How carefully should WebP be compressed?","WebP는 얼마나 꼼꼼하게 압축할까요?"),
                Lines=new[]{T("WebP has a small size but takes the longest of the three formats to make. Compressing it less carefully makes it sooner, and the size gets a little bigger. The picture quality setting stays the same. It has no effect on GIF and MP4.",
                    "WebP는 용량이 작은 대신 세 형식 중 만드는 데 시간이 가장 오래 걸려요. 덜 꼼꼼하게 압축하면 더 빨리 만들어지고 용량이 조금 커져요. 화질 설정은 그대로예요. GIF와 MP4에는 영향이 없어요."),
                    T("The numbers below were measured for 3 seconds long, 30 pictures a second and 1080 pixels tall. They depend on the computer and the video.",
                    "아래 숫자는 길이 3초, 1초에 30장, 세로 1080픽셀로 만들었을 때 잰 값이에요. 컴퓨터와 영상에 따라 달라요.")},
                Choices=new[]{new Choice(T("Carefully (the smallest size)","꼼꼼하게 (용량이 가장 작아요)"),T("About 5 seconds · 1.9 MB","약 5초 · 1.9MB")),
                    new Choice(T("Quickly","빠르게"),T("About 3 seconds · 2.0 MB","약 3초 · 2.0MB")),
                    new Choice(T("Very quickly","아주 빠르게"),T("About 2 seconds · 2.6 MB","약 2초 · 2.6MB"))},
                Default=0,Get=p=>Math.Max(0,Math.Min(2,p.WebpEffort)),Set=(p,i)=>p.WebpEffort=i});
            gif.Items.Add(new Setting {Id="gridGif",SectionId="gif",Title=T("Should GIF covers move in the tiles?","칸에서 GIF 표지를 움직일까요?"),
                Lines=new[]{T("This sets how GIF covers show in the tiles while you browse files. A tile shows the JPG cover first.","파일을 둘러볼 때 칸에서 GIF 표지를 어떻게 보여 줄지 정해요. 칸에는 JPG 표지가 먼저 보여요.")},
                Choices=new[]{new Choice(T("Don't move","움직이지 않기"),T("Tiles always show only the JPG cover. The lightest on the computer.","칸에는 항상 JPG 표지만 보여요. 컴퓨터에 부담이 가장 적어요.")),
                    new Choice(T("Move when the mouse is on it","마우스를 올렸을 때만 움직이기"),T("Point at a tile and that tile's GIF cover moves.","칸에 마우스를 올리면 그 칸의 GIF 표지가 움직여요.")),
                    new Choice(T("Always move","항상 움직이기"),T("Every GIF cover on screen moves. The ones off screen stay still, so scrolling doesn't stutter.","화면에 보이는 GIF 표지가 모두 움직여요. 화면 밖에 있는 것은 멈춰 있어서 스크롤이 버벅이지 않아요."))},
                Default=1,Get=p=>(int)p.GridGif,Set=(p,i)=>p.GridGif=(GifPlayback)i});
            gif.Items.Add(new Setting {Id="gifHoverSound",SectionId="gif",Title=T("Should a tile play its sound when the mouse is on it?","칸에 마우스를 올리면 소리도 낼까요?"),
                Lines=new[]{T("This is for MP4 GIF covers made with [Put the sound in]. Only the tile under the mouse plays, and it stops when the mouse leaves. With [Don't move] above there is no sound either.","[소리 넣기]로 만든 MP4 GIF 표지에만 해당돼요. 마우스를 올린 칸 하나만 소리가 나고, 마우스가 벗어나면 멈춰요. 위에서 [움직이지 않기]를 골랐다면 소리도 나지 않아요.")},
                Choices=new[]{new Choice(T("Play the sound","소리 내기"),T("The cover moves with its sound, at the volume you use in the large view.","표지가 소리와 함께 움직여요. 소리 크기는 크게 보기에서 쓰는 볼륨을 따라요.")),
                    new Choice(T("No sound","소리 내지 않기"),T("Covers move silently in the tiles.","칸에서는 표지가 소리 없이 움직여요."))},
                Default=0,Columns=2,Get=p=>p.GifHoverSound?0:1,Set=(p,i)=>p.GifHoverSound=i==0});


            // ----- Large view -----
            var large=add("large",T("Large view","크게 보기"));
            large.Items.Add(new Card {Title=T("The large view","크게 보기 화면"),
                Lines=new[]{T("Double-click a tile (or select it and press Enter) and it opens large. Photos, videos, GIFs and WebPs all open here.","칸을 더블클릭하면(또는 칸을 선택하고 Enter) 크게 열려요. 사진, 영상, GIF, WebP 모두 여기서 열려요."),
                    T("Point at a video's playback bar and the scene at that spot appears as a small picture with its time. Click to go to that spot.",
                      "영상의 재생바에 마우스를 올리면 그 위치의 장면이 시간과 함께 작은 그림으로 나와요. 누르면 그 위치로 이동해요."),
                    T("When you change the playback speed, brightness or volume with keys, the new value shows briefly at the top left of the screen.","키로 재생 속도, 밝기, 볼륨을 바꾸면 화면 왼쪽 위에 바뀐 값이 잠깐 표시돼요."),
                    T("The volume is remembered: the next video starts at the volume you last set. {{↑}} {{↓}} change it by 5, and {{M}} turns the sound off and on.",
                      "볼륨은 기억돼요. 다음 영상도 마지막에 맞춘 볼륨으로 시작해요. {{↑}} {{↓}}로 5씩 조절하고, {{M}}으로 소리를 껐다 켜요."),
                    T("If speech is hard to hear, press {{N}} (Clear voice). Quiet voices get louder and words sound clearer. Music may sound a little different, so in that case press it once more to turn it off.",
                      "말소리가 잘 안 들리면 {{N}}(목소리 또렷하게)을 눌러 보세요. 작은 목소리가 커지고 말이 더 또렷하게 들려요. 음악은 느낌이 조금 달라질 수 있으니, 그럴 때는 한 번 더 눌러서 끄면 돼요."),
                    T("Right-click the screen and, in the controls window that opens, you can choose the strength: Gentle, Normal or Strong. ‘Strong’ raises quiet voices the most, but noise in quiet scenes can get louder too.",
                      "화면을 오른쪽 클릭하면 나오는 조작 창에서 세기를 약하게·보통·강하게 중에서 고를 수 있어요. ‘강하게’는 작은 목소리를 가장 많이 키워 주지만 조용한 장면의 잡음도 함께 커질 수 있어요."),
                    T("Right-click the screen and the controls window opens. Every control is listed with its key, such as moving by scene (D, F), brightness (Q, W, E, R) and rotation (I, O, P), so you can look here when you can't remember a key. Clicking a button with the mouse does the same thing, and holding it down keeps it going.",
                      "화면을 오른쪽 클릭하면 조작 창이 열려요. 장면 이동(D, F), 밝기(Q, W, E, R), 회전(I, O, P)처럼 모든 조작이 키와 함께 적혀 있어서, 키가 기억나지 않을 때 보면 돼요. 버튼을 마우스로 눌러도 똑같이 동작하고, 누르고 있으면 계속 이어져요."),
                    T("{{P}} turns the picture to the right and {{O}} to the left, a little at a time, and {{I}} puts it back. When you start turning, a video stops on that scene and becomes a small picture that never touches the edges of the screen. A moment after you stop, it plays on from the same scene at that angle. Then roll the mouse wheel to zoom in (up to 8×) or out, and drag to move the part you see. Both stay only while you look at that file. Click Make cover like this, and the seek-bar thumbnail, the scene list and the cover all show it the same way.",
                      "{{P}}를 누르면 오른쪽으로, {{O}}를 누르면 왼쪽으로 조금씩 회전하고, {{I}}를 누르면 원래대로 돌아와요. 회전을 시작하면 영상이 그 장면에서 멈추고, 돌려도 화면 끝에 닿지 않게 작은 그림이 돼요. 손을 떼고 잠시 뒤에는 그 각도로 같은 장면부터 다시 재생돼요. 그다음 마우스 휠로 확대하거나(최대 8배) 축소할 수 있고, 끌면 보이는 부분을 옮길 수 있어요. 둘 다 그 파일을 보는 동안만 유지돼요. 이 상태로 표지 만들기를 누르면 재생바 썸네일, 장면 고르기, 표지까지 똑같은 모습으로 이어져요."),
                    T("Press {{Tab}} and the file information shows at the top left: the file name, which drive and folder it is in, its format and size, and for a video its picture size, fps and so on. Press it again to hide it.",
                      "{{Tab}}을 누르면 화면 왼쪽 위에 파일 정보가 나와요. 파일 이름, 어느 드라이브의 어느 폴더에 있는지, 형식과 용량, 영상이면 화면 크기와 fps 같은 것들이에요. 한 번 더 누르면 사라져요."),
                    T("Press {{Delete}} and the original file you are looking at goes to the Recycle Bin and the next file opens. There is no confirmation window, and you can restore it from the Windows Recycle Bin. A large view opened from ‘Cover collection’ doesn't delete originals.",
                      "{{Delete}}를 누르면 지금 보는 원본 파일이 휴지통으로 가고 다음 파일이 열려요. 확인 창은 뜨지 않고, Windows 휴지통에서 되살릴 수 있어요. ‘표지 O 모음’에서 연 크게 보기에서는 원본을 지우지 않아요."),
                    T("In full screen too, the minimize and maximize buttons at the top right can be clicked right away. Minimize it and bring it back, and it returns in full screen as it was. Maximize leaves full screen and becomes a maximized window. This is the same in every LoveMyShelf window.",
                      "전체 화면에서도 오른쪽 위의 최소화·최대화 버튼을 바로 누를 수 있어요. 최소화했다가 다시 올리면 전체 화면 그대로 돌아와요. 최대화를 누르면 전체 화면이 끝나고 최대화된 창이 돼요. LoveMyShelf의 모든 창이 같아요.")},
                Keys=new[]{K(T("Previous / next file","이전 파일 / 다음 파일"),"K","L"),K(T("Play or pause","재생 / 일시정지"),"Space"),
                    K(T("Skip back / forward","뒤로 / 앞으로 건너뛰기"),"←","→"),K(T("Skip one second","1초 건너뛰기"),"Shift","←","→"),K(T("One scene back / forward","한 장면 뒤로 / 앞으로"),"D","F"),
                    K(T("Back to normal / darker / brighter","원래대로 / 어둡게 / 밝게"),"Q","W","E"),K(T("Brightness style: Soft / See in the dark / Milky","밝기 방식: 부드럽게 / 어두운 곳 보기 / 뽀얗게"),"R"),
                    K(T("Original speed / 0.1× slower / 0.1× faster / 2×","원래 속도 / 0.1배 느리게 / 0.1배 빠르게 / 2배"),"Z","X","C","V"),
                    K(T("Rotation back to normal / turn left / turn right","회전 원래대로 / 왼쪽으로 회전 / 오른쪽으로 회전"),"I","O","P"),K(T("Zoom in / out","확대 / 축소"),T("Wheel","휠")),
                    K(T("Capture this scene","지금 장면 캡처"),"/"),K(T("GIF cover part: start / end / clear","GIF 표지 구간: 시작 / 끝 / 지우기"),"[","]","\\"),K(T("Show / hide the file information","파일 정보 보기 / 숨기기"),"Tab"),
                    K(T("Move the original file to the Recycle Bin","원본 파일을 휴지통으로 보내기"),"Delete"),K(T("Open the controls window","조작 창 열기"),T("Right-click","오른쪽 클릭")),
                    K(T("Full screen","전체 화면"),"F11"),K(T("Close","닫기"),"Esc")}});
            large.Items.Add(new Card {Title=T("Subtitles and sync","자막과 싱크 맞추기"),
                Lines=new[]{T("If there is a subtitle file (SMI, SRT, ASS, VTT) with the same name as the video, it shows automatically. If one file has both Korean and English, Korean shows first.",
                    "영상과 이름이 같은 자막 파일(SMI, SRT, ASS, VTT)이 있으면 자동으로 표시돼요. 한 파일에 한국어와 영어가 함께 들어 있으면 한국어가 먼저 표시돼요."),
                    T("Subtitles show in the same font as LoveMyShelf. A font or size written inside an SMI or SRT file is not followed (colors are kept). ASS subtitles show in the fonts their maker chose.",
                    "자막은 LoveMyShelf와 같은 글꼴로 표시돼요. SMI·SRT 파일 안에 적힌 글꼴이나 크기는 따르지 않아요(색은 그대로예요). ASS 자막은 만든 사람이 정해 둔 글꼴 그대로 나와요.")},
                Steps=new[]{T("Right-click the video screen and click [[Open the subtitle explorer]].","영상 화면을 오른쪽 클릭하고 [[자막 탐색기 열기]]를 눌러요."),
                    T("Pause at the moment a line of speech starts.","대사가 시작되는 순간에 일시정지해요."),
                    T("Click the subtitle line for that speech once. One click only selects the line, and the video doesn't move.","그 대사에 해당하는 자막 줄을 한 번 클릭해요. 한 번 클릭하면 줄만 선택되고 영상은 움직이지 않아요."),
                    T("Click [[Sync to the selected line]] and the sync of the whole subtitle is set.","[[선택된 위치로 싱크 설정]]을 누르면 자막 전체의 싱크가 맞춰져요."),
                    T("If it fits, click [[Save]] to write it over the subtitle file.","잘 맞으면 [[저장]]을 눌러 자막 파일에 덮어써요.")},
                Tips=new[]{T("Double-click a subtitle line and the video goes to that spot.","자막 줄을 더블클릭하면 영상이 그 위치로 이동해요."),
                    T("If only the later part is off, choose [[Move only this line and after]] from the ▾ next to the sync button.","뒷부분만 어긋난다면 싱크 버튼 옆의 ▾에서 [[이 줄부터 뒤만 옮기기]]를 골라요."),
                    T("Saving changes only the times that moved and the sentences you fixed. The subtitle's first state is kept separately in LoveMyShelf's records folder.","저장하면 바뀐 시간과 고친 문장만 달라져요. 자막의 처음 상태는 LoveMyShelf 기록 폴더에 따로 보관돼요."),
                    T("Subtitles inside a video file also show as usual, but only separate subtitle files can be listed and synced.","영상 파일 안에 들어 있는 자막도 그대로 표시되지만, 목록으로 보고 싱크를 맞추는 건 별도의 자막 파일만 돼요.")}});
            large.Items.Add(new Card {Title=T("Cover or original","표지 또는 원본"),
                Lines=new[]{T("[[View original]] and [[View cover]] switch between the cover and the original.","[[원본 보기]]와 [[표지 보기]]로 표지와 원본을 서로 바꿔 볼 수 있어요."),
                    T("If a cover is small and looks a little blurry, try [[Upscale]]. AI enlarges the cover 4 times and makes it sharper. It takes a little time but runs by itself in the background, so you can look at other photos meanwhile. When it's done, a notice appears at the bottom right. The original cover stays too, so you can compare the two and choose.",
                      "표지가 작아서 조금 흐릿해 보이면 [[업스케일링]]을 눌러 보세요. AI가 표지를 4배로 키우면서 선명하게 다듬어 줘요. 시간이 조금 걸리지만 뒤에서 알아서 진행되니까 그동안 다른 사진을 봐도 돼요. 다 되면 오른쪽 아래에 알려 드려요. 원래 표지도 남아 있어서 둘을 비교해 보고 고를 수 있어요.")}});
            large.Items.Add(new Setting {Id="upscaleDevice",SectionId="large",Title=T("What should do the upscaling?","업스케일링은 무엇으로 할까요?"),
                Lines=new[]{T("Which one is faster depends on the computer. Graphics built into a laptop (such as Intel's) can be even slower than the processor. With a separate graphics card (such as NVIDIA or AMD), the graphics card can be much faster.",
                    "컴퓨터마다 어느 쪽이 빠른지 달라요. 노트북에 들어 있는 그래픽(인텔 내장 그래픽 등)은 오히려 CPU보다 느릴 수 있어요. 따로 꽂힌 그래픽카드(NVIDIA·AMD 등)가 있으면 그래픽카드가 훨씬 빠를 수 있어요."),
                    T("If the graphics card doesn't work or its result looks wrong, LoveMyShelf switches to the processor by itself and finishes the cover.",
                    "그래픽카드로 하다가 안 되거나 결과가 이상하면, 알아서 CPU로 바꿔서 끝까지 만들어요.")},
                Choices=new[]{new Choice(T("Processor (CPU)","CPU (컴퓨터의 두뇌)"),T("The starting choice. It works the same on every computer. The computer gets a little busy while it runs.","처음 설정이에요. 어느 컴퓨터에서나 똑같이 잘 돼요. 하는 동안 컴퓨터가 조금 바빠져요.")),
                    new Choice(T("Graphics card","그래픽카드"),T("Good for a computer with a separate graphics card. The processor stays less busy while it runs.","따로 꽂힌 그래픽카드가 있는 컴퓨터에 좋아요. 하는 동안 CPU가 덜 바빠져요."))},
                Default=0,Columns=2,Get=p=>p.UpscaleGpu?1:0,Set=(p,i)=>p.UpscaleGpu=i==1});
            var views=new[]{new Choice(T("View cover large","표지 크게 보기"),T("A tile with a cover opens the cover large first. Click [[View original]] to switch to the original.","표지가 있는 칸은 표지가 먼저 크게 열려요. [[원본 보기]]를 누르면 원본으로 바꿔 볼 수 있어요.")),
                new Choice(T("View original large","원본 크게 보기"),T("The original photo or video opens first. Click [[View cover]] to switch to the cover.","원본 사진이나 영상이 먼저 열려요. [[표지 보기]]를 누르면 표지로 바꿔 볼 수 있어요."))};
            large.Items.Add(new Setting {Id="openBy",SectionId="large",Title=T("How should clicking a tile open the large view?","칸을 어떻게 클릭하면 크게 열리게 할까요?"),
                Lines=new[]{T("This is how a tile opens in the folder view. Whichever you choose, pressing Enter on the selected tile opens it large.","폴더 화면에서 칸을 여는 방법이에요. 어느 쪽을 고르든 선택한 칸에서 Enter를 누르면 크게 열려요.")},
                Choices=new[]{new Choice(T("Double-click","더블클릭"),T("One click selects a tile, and then Shift-clicking another tile selects everything in between. A double-click opens it large. It is the same as File Explorer.","한 번 클릭하면 칸이 선택되고, 이어서 Shift를 누른 채 다른 칸을 클릭하면 그 사이가 모두 선택돼요. 더블클릭하면 크게 열려요. 파일 탐색기와 같아요.")),
                    new Choice(T("One click","한 번 클릭"),T("One click opens it large right away. To select tiles, click while holding Ctrl or Shift.","한 번 클릭하면 바로 크게 열려요. 칸을 선택하려면 Ctrl이나 Shift를 누른 채 클릭해요."))},
                Default=0,Columns=2,Get=p=>p.OpenByDoubleClick?0:1,Set=(p,i)=>p.OpenByDoubleClick=i==0});
            large.Items.Add(new Setting {Id="photoView",SectionId="large",Title=T("What should open large from a photo tile?","사진 칸을 열면 무엇을 크게 볼까요?"),
                Lines=new[]{T("A tile without a cover always opens the original. Right-click a tile to choose the cover or the original just this once.","표지가 없는 칸은 항상 원본이 열려요. 칸을 오른쪽 클릭하면 이번 한 번만 표지나 원본 중 하나를 골라서 열 수도 있어요.")},
                Choices=views,Default=0,Columns=2,Get=p=>p.OpenPhotoOriginal?1:0,Set=(p,i)=>p.OpenPhotoOriginal=i==1});
            large.Items.Add(new Setting {Id="motionView",SectionId="large",Title=T("What should open large from a video, GIF or WebP tile?","영상·GIF·WebP 칸을 열면 무엇을 크게 볼까요?"),
                Lines=new[]{T("If you choose the original, the video plays right away.","원본을 고르면 영상이 바로 재생돼요.")},
                Choices=views,Default=0,Columns=2,Get=p=>p.OpenMotionOriginal?1:0,Set=(p,i)=>p.OpenMotionOriginal=i==1});
            large.Items.Add(Numbers("subtitleSize",T("How big should subtitles be?","자막을 얼마나 크게 보여 줄까요?"),new[]{70,85,100,120,150,200},100,
                p=>p.SubtitleScale,(p,v)=>p.SubtitleScale=v,v=>v+"%",
                v=>v==70?T("Small","작게"):v==100?T("The usual size","원래 크기"):v==150?T("Big","크게"):v==200?T("Twice as big","2배"):"",3));
            var subtitleSetting=(Setting)large.Items.Last();
            subtitleSetting.Lines=new[]{T("This is the size of the subtitles on a video. While watching, you can also right-click the screen and change it by 10% at a time with [[Subtitle size]] in the controls window. A size changed there is saved here too and is used for the next video.",
                "영상 위에 나오는 자막의 크기예요. 영상을 보다가 화면을 오른쪽 클릭하면 나오는 조작 창의 [[자막 크기]]에서 10%씩 바꿀 수도 있어요. 거기서 바꾼 크기도 여기에 저장되고, 다음 영상에도 그대로 쓰여요.")};
            Typed(subtitleSetting,"%",50,300,5);
            large.Items.Add(new Card {Title=T("When you watch only a video, minimize the folder window","영상만 볼 때는 폴더 창을 최소화해 보세요"),
                Lines=new[]{T("About 5 seconds after the folder window is minimized, it lets go of the pictures it had read to show in the tiles, so LoveMyShelf uses less memory. The video window keeps playing as it is.",
                    "폴더 창을 최소화하면 5초쯤 뒤에, 칸에 보여 주려고 읽어 둔 그림들을 메모리에서 내려놓아요. 그만큼 LoveMyShelf가 메모리를 덜 써요. 영상 창은 그대로 재생돼요."),
                    T("Bring the folder window back and only the pictures of the tiles on screen are read again.",
                    "폴더 창을 다시 올리면 화면에 보이는 칸의 그림만 새로 읽어요."),
                    T("While watching a video, right-click the screen and click [[Minimize the folder window]]. If several folder windows are open, the memory is let go once all of them are minimized.",
                    "영상을 보는 중이라면 화면을 오른쪽 클릭하고 [[폴더 창 최소화]]를 누르면 돼요. 폴더 창이 여러 개 열려 있으면 모두 최소화했을 때 메모리를 내려놓아요.")}});
            AddSpeakerSettings(large);

            // ----- Sharing -----
            var share=add("share",T("Sharing","공유하기"));
            share.Items.Add(new Card {Title=T("Send a cover to a chat","표지를 채팅방에 보내기"),
                Steps=new[]{T("Right-click a tile and click [[Copy]].","칸을 오른쪽 클릭하고 [[복사]]를 눌러요."),
                    T("Click the message box of a chat app and press {{Ctrl}} + {{V}}. The files are attached.","채팅 앱의 메시지 입력창을 클릭하고 {{Ctrl}} + {{V}}를 눌러요. 파일이 첨부돼요."),
                    T("You can also just drag the tile onto the chat window.","칸을 채팅창으로 바로 끌어다 놓아도 돼요.")},
                Lines=new[]{T("Click [[Share…]] in the same menu and the Windows share window opens. Choose the app to send with there.","같은 메뉴의 [[공유…]]를 누르면 Windows 공유 창이 열려요. 거기서 보낼 앱을 고르면 돼요.")},
                Tips=new[]{T("LoveMyShelf never uploads anything to the internet itself. Sending is always done by the app you chose.","LoveMyShelf가 직접 인터넷에 올리는 일은 없어요. 보내는 건 언제나 내가 고른 앱이 해요.")}});
            share.Items.Add(new Setting {Id="share",SectionId="share",Title=T("What should Copy, Share and dragging send?","복사·공유·끌어다 놓기를 하면 무엇을 보낼까요?"),
                Lines=new[]{T("Right-click a tile to choose differently just this once.","칸을 오른쪽 클릭하면 이번 한 번만 다르게 골라 보낼 수도 있어요.")},
                Choices=new[]{new Choice(T("Cover and original","표지와 원본 함께"),T("Sends both the cover and the original photo or video.","표지와 원본 사진(영상)을 모두 보내요.")),
                    new Choice(T("Cover only","표지만"),T("Sends only the cover. If there is a GIF cover made from it, that is sent along.","표지만 보내요. 그 표지로 만든 GIF 표지가 있으면 함께 보내요.")),
                    new Choice(T("Original only","원본만"),T("Sends only the original photo or video.","원본 사진이나 영상만 보내요."))},
                Default=0,Get=p=>(int)p.Share,Set=(p,i)=>p.Share=(ShareContent)i});
            share.Items.Add(Numbers("shareLimit",T("Ask before sending big videos?","큰 영상은 보내기 전에 물어볼까요?"),new[]{10,20,50,100},20,
                p=>p.ShareLimitMB,(p,v)=>p.ShareLimitMB=v,v=>v+"MB",
                v=>v==10?T("For apps that take only small files","작은 파일만 받는 앱용"):v==20?T("Discord's limit for free users","디스코드 무료 사용자 제한"):v==50?T("For apps that take bigger files","더 큰 파일을 받는 앱용"):T("Almost never asks","거의 묻지 않아요"),4));
            ((Setting)share.Items.Last()).Lines=new[]{T("If an original video is too big, a chat app may not accept it. For example, a free Discord user can upload up to 20 MB per file. When an original video is bigger than the size chosen below, LoveMyShelf asks whether to [[Send it as it is]] or [[Send only 10 seconds around the cover scene]].",
                "원본 영상이 너무 크면 채팅 앱에 올라가지 않을 수 있어요. 예를 들어 디스코드 무료 사용자는 파일 하나에 20MB까지 올릴 수 있어요. 원본 영상이 아래에서 고른 용량보다 크면 [[그대로 보내기]]와 [[표지 장면 앞뒤로 10초만 보내기]] 중 어느 쪽으로 할지 물어봐요.")};
            Typed((Setting)share.Items.Last(),"MB",1,2000,5);

            // ----- Tidying up -----
            var tidy=add("tidy",T("Tidying up","정리하기"));
            tidy.Items.Add(new Card {Title=T("When one original has several covers","원본 하나에 표지가 여러 장일 때"),
                Lines=new[]{T("When one photo or video has several covers, a number like 2/5 shows at the bottom right of the tile. It means the second of 5 covers. These covers always stay together. A video's covers are placed in the time order of their scenes in the video, regardless of the order you made them.","사진이나 영상 하나에 표지가 여러 장 있으면 칸 오른쪽 아래에 2/5 같은 숫자가 표시돼요. 표지 5장 중 두 번째라는 뜻이에요. 이 표지들은 항상 붙어 다녀요. 영상의 표지는 만든 순서와 상관없이 영상 속 장면의 시간 순서대로 놓여요."),
                    T("Point at a cover and all the covers made from the same original get a colored outline.","표지에 마우스를 올리면 같은 원본으로 만든 표지 전부에 색 테두리가 생겨요."),
                    T("In the large view, when there is empty space on the right of the screen, the other covers of the same original appear small. Click one to switch to it. In full screen they hide when the mouse stays still and come back when it moves.","크게 볼 때 화면 오른쪽에 여백이 있으면 같은 원본의 다른 표지들이 작게 나와요. 누르면 그 표지로 바뀌어요. 전체 화면에서는 마우스를 가만히 두면 숨고, 움직이면 다시 나와요.")}});
            tidy.Items.Add(new Card {Title=T("Hiding and deleting covers","표지 숨기기와 삭제"),
                Lines=new[]{T("Right-click a tile and these menu items are there.","칸을 오른쪽 클릭하면 이런 메뉴가 있어요."),
                    T("[[Hide this cover]] leaves the file as it is and only stops showing it in LoveMyShelf. [[Show this cover again]] brings it back.","[[이 표지 숨기기]]는 파일은 그대로 두고 LoveMyShelf에서만 보이지 않게 해요. [[이 표지 다시 표시]]로 되돌릴 수 있어요."),
                    T("[[Move this cover file to the Recycle Bin]] deletes only the cover. The original stays as it is. If there is a GIF cover too, this item changes to [[Move both the JPG and GIF covers to the Recycle Bin]], and [[Move only the GIF cover to the Recycle Bin]] deletes only the GIF cover and keeps the JPG cover. Hide and show then change to [[Hide both the JPG and GIF covers]] and [[Show both the JPG and GIF covers again]].","[[이 표지 파일 휴지통으로 보내기]]는 표지만 지워요. 원본은 그대로예요. GIF 표지도 있으면 이 메뉴가 [[JPG·GIF 표지 모두 휴지통으로 보내기]]로 바뀌고, [[GIF 표지만 휴지통으로 보내기]]를 누르면 GIF 표지만 지우고 JPG 표지는 남겨 둬요. 숨기기와 다시 표시도 이때는 [[JPG·GIF 표지 모두 숨기기]], [[JPG·GIF 표지 모두 다시 표시]]로 바뀌어요."),
                    T("If you deleted a cover by mistake, press [Undo] on the notice that shows at the bottom right for 3 seconds. The cover comes right back. Keep the mouse over the notice and it waits without disappearing.","표지를 실수로 지웠다면 오른쪽 아래에 3초 동안 나오는 알림의 [되돌리기]를 누르세요. 표지가 그대로 돌아와요. 알림 위에 마우스를 올려 두면 알림이 사라지지 않고 기다려요."),
                    T("[[Move the original file to the Recycle Bin]] deletes the original photo or video itself. You can restore it from the Recycle Bin.","[[원본 파일 휴지통으로 보내기]]는 원본 사진이나 영상 자체를 지워요. 휴지통에서 복원할 수 있어요.")}});
            tidy.Items.Add(new Card {Title=T("If you moved an original somewhere else","원본을 다른 곳으로 옮겼다면"),
                Lines=new[]{T("LoveMyShelf remembers where each original was. An original that you renamed in File Explorer or moved within the same drive is usually found again by itself. When it can't be found, the tile shows the small saved preview instead.",
                    "LoveMyShelf는 원본이 어디에 있었는지 기억해요. 파일 탐색기에서 이름을 바꾸거나 같은 드라이브 안에서 옮긴 원본은 대부분 저절로 다시 찾아요. 찾지 못했을 때는 칸에 저장해 둔 작은 미리보기가 대신 보여요."),
                    T("In that case, open it large, click [[Find the original again]] and choose the file in its new place. Then it works as before.","그럴 때는 크게 열어서 [[원본 다시 찾기]]를 누르고, 옮긴 위치의 파일을 골라 주세요. 그러면 전처럼 쓸 수 있어요.")},
                Tips=new[]{T("If you rename with {{F2}} inside LoveMyShelf, its records change along with it, so this doesn't happen.","LoveMyShelf 안에서 {{F2}}로 이름을 바꾸면 기록도 함께 바뀌어서 이런 일이 생기지 않아요.")}});

            // ----- Backup and restore -----
            var backup=add("backup",T("Backup and restore","백업·복원"));
            backup.Items.Add(new Card {Title=T("Keeping your covers and tags","표지와 태그를 남겨 두기"),
                Lines=new[]{T("A backup keeps your covers, tags and records in one file locked with your password. Put it in Google Drive, OneDrive or on an external drive, and if this computer breaks you can bring everything back on another one.",
                    "백업은 표지·태그·기록을 비밀번호로 잠근 파일 하나에 담아요. 구글 드라이브, OneDrive, 외장 하드에 넣어 두면 컴퓨터가 고장 나도 다른 컴퓨터에서 그대로 되살릴 수 있어요."),
                    T("Even in Google Drive or OneDrive, Google and Microsoft can't see what's inside. All they see is the file name, its size and when it was saved.",
                        "구글 드라이브나 OneDrive에 두어도 구글과 마이크로소프트는 안의 내용을 볼 수 없어요. 보이는 건 파일 이름과 크기, 저장한 시간뿐이에요."),
                    T("The password is kept nowhere. If you forget it, nobody can open the backup. We made it and we can't either.","비밀번호는 어디에도 저장하지 않아요. 잊어버리면 아무도 열 수 없어요. 만든 저희도 못 열어요."),
                    T("If you like, the original photos and videos can be kept too, as locked photo boxes with the same password. Any 7-Zip opens them with that password.",
                        "원하면 원본 사진·영상도 같은 비밀번호로 ‘잠긴 사진 상자’에 넣어 둘 수 있어요. 7-Zip으로 그 비밀번호를 넣으면 열려요."),
                    T("When it's time, LoveMyShelf reminds you when it starts (every week unless you change it).","백업할 때가 되면 LoveMyShelf를 켤 때 알려 드려요(바꾸지 않으면 1주일마다).")}});

            backup.Items.Add(new Card {Title=T("See your covers on your phone","휴대폰에서 표지 보기"),
                Lines=new[]{T("Right-click a blank spot in a folder and choose [[Send to phone…]]. The covers on screen are copied into a Google Drive or OneDrive folder in the order shown, as 001_name.jpg, 002_name.jpg … Open that folder in the same app on your phone and sort it by name.",
                    "폴더의 빈 곳을 오른쪽 클릭하고 [[휴대폰으로 보내기…]]를 누르세요. 화면의 표지가 보이는 순서 그대로 001_이름.jpg, 002_이름.jpg …처럼 구글 드라이브나 OneDrive 폴더에 복사돼요. 휴대폰의 같은 앱에서 그 폴더를 열고 이름순으로 보면 돼요."),
                    T("Covers only is the default. If you choose covers and originals, each photo or video comes right after its cover, so you can open it on the phone too.","기본은 표지만이에요. 표지와 원본 함께를 고르면 사진·영상이 표지 바로 뒤에 들어가서 휴대폰에서 원본도 열어 볼 수 있어요."),
                    T("They are copies, so deleting them on the phone changes nothing on this computer. They do go up to the cloud: keep private photos in a locked photo box instead.","복사본이라 휴대폰에서 지워도 이 컴퓨터는 그대로예요. 다만 클라우드에 올라가니 남에게 보이면 안 되는 사진은 잠긴 사진 상자에 넣어 두세요.")}});

            // ----- Screen, language and updates -----
            var screen=add("screen",T("Screen, language, updates","화면·언어·새 버전"));
            screen.Items.Add(new Setting {Id="theme",SectionId="screen",Title=T("Light or dark screen?","화면을 밝게 할까요, 어둡게 할까요?"),
                Lines=new[]{T("Where photos and videos are shown (the large view and Make cover), the background is black in every mode so that colors look right.",
                    "사진과 영상이 표시되는 곳(크게 보기, 표지 만들기)은 색이 제대로 보이도록 어느 모드에서든 배경이 검은색이에요.")},
                Choices=new[]{new Choice(T("Follow Windows","Windows 설정 따르기"),T("Light when Windows is in light mode, dark when it is in dark mode. It changes along when you change the Windows setting.","Windows가 밝은 모드면 밝게, 어두운 모드면 어둡게 보여요. Windows 설정을 바꾸면 따라서 바뀌어요.")),
                    new Choice(T("Light","밝게"),T("Dark letters on a light gray background. Comfortable to look at in the daytime.","밝은 회색 배경에 진한 글씨예요. 낮에 보기 편해요.")),
                    new Choice(T("Dark","어둡게"),T("Light letters on a dark background. Easier on the eyes at night.","어두운 배경에 밝은 글씨예요. 밤에 눈이 덜 피로해요."))},
                Default=0,Get=p=>(int)p.Theme,Set=(p,i)=>p.Theme=(ThemeMode)i});
            screen.Items.Add(new Setting {Id="language",SectionId="screen",Title=T("Which language should it show in?","어떤 언어로 표시할까요?"),
                Choices=new[]{new Choice("한국어",""),new Choice("English","")},Columns=2,
                Get=p=>p.Korean?0:1,Set=(p,i)=>p.Korean=i==0});
            screen.Items.Add(new Setting {Id="updates",SectionId="screen",Title=T("Should we tell you when a new version is out?","새 버전이 나오면 알려 드릴까요?"),
                Lines=new[]{T("When this is on, LoveMyShelf checks GitHub for a new version once each time it starts. No photos or personal information are sent. If there is a new version, a notice appears at the bottom right. Press the button on the notice and it downloads and updates automatically, and your covers and settings stay as they are.",
                    "켜 두면 LoveMyShelf를 켤 때마다 한 번씩 GitHub에 새 버전이 있는지 확인해요. 이때 사진이나 개인 정보는 전혀 보내지 않아요. 새 버전이 있으면 오른쪽 아래에 알려 드려요. 알림의 버튼을 누르면 자동으로 다운로드해서 업데이트하고, 만든 표지와 설정은 그대로 남아요.")},
                Choices=new[]{new Choice(T("Get notices","알림 받기"),""),new Choice(T("Turn notices off","알림 끄기"),T("It never connects to the internet.","인터넷에 전혀 접속하지 않아요."))},
                Default=0,Columns=2,Get=p=>p.CheckUpdates?0:1,Set=(p,i)=>p.CheckUpdates=i==0});
            screen.Items.Add(new Setting {Id="explorerMenu",SectionId="screen",Title=T("Add LoveMyShelf to File Explorer's right-click menu?","파일 탐색기 오른쪽 클릭 메뉴에 넣을까요?"),
                Lines=new[]{T("Right-click a photo or video in File Explorer and choose [[Open with LoveMyShelf]]. The file opens large, and its folder opens behind it. On Windows 11, click [[Show more options]] first (or press {{Shift+F10}}). It is also in [[Open with]].",
                    "파일 탐색기에서 사진이나 영상을 오른쪽 클릭하고 [[LoveMyShelf로 열기]]를 고르세요. 파일이 크게 열리고, 그 폴더가 뒤에 열려요. Windows 11에서는 먼저 [[더 많은 옵션 표시]]를 누르거나 {{Shift+F10}}을 누르세요. [[연결 프로그램]]에도 들어 있어요."),
                    T("It is added only for your Windows account and needs no administrator permission. Turn it off and it is removed right away. If you move the LoveMyShelf folder, start LoveMyShelf once so the menu finds it again.",
                    "지금 쓰는 Windows 계정에만 추가되고, 관리자 권한은 필요 없어요. 끄면 바로 지워져요. LoveMyShelf 폴더를 옮겼다면 LoveMyShelf를 한 번 켜야 메뉴가 새 위치를 찾아요.")},
                Choices=new[]{new Choice(T("Add it","넣기"),""),new Choice(T("Don't add it","넣지 않기"),"")},
                Default=0,Columns=2,Get=p=>p.ExplorerMenu?0:1,Set=(p,i)=>p.ExplorerMenu=i==0});

            // ----- Version and what's new (drawn by DrawNews) -----
            add("news",T("Version and what's new","버전과 새 소식"));

            // ----- All shortcuts -----
            var keys=add("keys",T("All shortcuts","단축키 모아 보기"));
            keys.Items.Add(new Card {Title=T("Anywhere","어디서나"),Keys=new[]{K(T("Open Guide & Options","설명서 겸 옵션 열기"),"F1"),K(T("Full screen","전체 화면"),"F11")}});
            keys.Items.Add(new Card {Title=T("Browsing files","파일 둘러보기"),Keys=new[]{
                K(T("Move","이동"),"←","→","↑","↓"),K(T("Open big","크게 보기"),"Enter"),K(T("Rename","이름 바꾸기"),"F2"),K(T("Move to the Recycle Bin","휴지통으로 보내기"),"Delete"),
                K(T("Select all","모두 선택"),"Ctrl+","A"),K(T("Copy / cut / paste files","파일 복사 / 잘라내기 / 붙여넣기"),"Ctrl+","C","X","V"),K(T("Clear selection","선택 해제"),"Esc"),K(T("Select a folder to add","추가할 폴더 선택"),"Ctrl+","O"),K(T("Search names","이름 검색"),"Ctrl+","F"),K(T("Back / forward","뒤로 / 앞으로"),"Alt+","←","→"),K(T("Up to the folder above","위 폴더로"),"Alt+","↑"),K(T("Type a path in the address bar","주소창에 경로 입력"),"Ctrl+","L"),K(T("Folder tree","폴더 트리"),"Ctrl+","T"),K(T("Refresh","새로 고침"),"F5")}});
            keys.Items.Add(new Card {Title=T("Large view","크게 보기"),Keys=new[]{
                K(T("Previous / next file","이전 / 다음 파일"),"K","L"),K(T("Play / pause","재생 / 일시정지"),"Space"),K(T("Skip","건너뛰기"),"←","→"),K(T("Skip one second","1초 건너뛰기"),"Shift","←","→"),
                K(T("One scene at a time","한 장면씩"),"D","F"),K(T("Brightness","밝기"),"Q","W","E"),K(T("Brightness style","밝기 방식"),"R"),K(T("Playback speed","재생 속도"),"Z","X","C","V"),K(T("Rotation","회전"),"I","O","P"),K(T("Zoom in / out","확대 / 축소"),T("Wheel","휠")),K(T("Volume","볼륨"),"↑","↓"),K(T("Sound off / on","소리 끄기 / 켜기"),"M"),K(T("Clear voice on / off","목소리 또렷하게 켜기 / 끄기"),"N"),K(T("Capture this scene","지금 장면 캡처"),"/"),K(T("GIF cover part","GIF 표지 구간"),"[","]","\\"),K(T("File information","파일 정보"),"Tab"),K(T("Move the original to the Recycle Bin","원본을 휴지통으로 보내기"),"Delete"),K(T("Controls window","조작 창"),T("Right-click","오른쪽 클릭")),K(T("Close","닫기"),"Esc")}});
            keys.Items.Add(new Card {Title=T("Making a cover","표지 만들기"),Keys=new[]{
                K(T("Zoom in / out","확대 / 축소"),"+","−"),K(T("Previous / next scene","이전 / 다음 장면"),"D","F"),K(T("Skip","건너뛰기"),"←","→"),K(T("Skip one second","1초 건너뛰기"),"Shift","←","→"),
                K(T("Brightness","밝기"),"Q","W","E"),K(T("Brightness style","밝기 방식"),"R"),K(T("Close without saving","저장하지 않고 닫기"),"Esc")}});
            keys.Items.Add(new Card {Title=T("Making a GIF cover","GIF 표지 만들기"),Keys=new[]{
                K(T("Move a handle one scene at a time","손잡이를 한 장면씩 옮기기"),"←","→"),K(T("Speed","속도"),"Z","X","C","V"),K(T("Pause / play the preview","미리보기 일시정지 / 재생"),"Space")}});

            // ----- Writing to the AI (the button is drawn by AiWorkPanel.GuidePanel) -----
            var feedback=add("feedback",T("Write to the AI","AI한테 적기"));
            feedback.Items.Add(new Card {Title=T("Write to the AI","AI한테 적기"),
                Lines=new[]{T("At the bottom of the folder tree on the left, write whatever you like and press [[Send]].",
                    "왼쪽 폴더 트리 맨 아래 칸에 하고 싶은 말을 적고 [[보내기]]를 누르면 돼요.")},
                Tips=new[]{T("You can also right-click a blank spot in a folder and choose [[Write to the AI that made this…]].","폴더의 빈 곳을 오른쪽 클릭하고 [[이걸 만든 AI한테 적기…]]를 눌러도 돼요.")}});

            // ----- Help -----
            var help=add("help",T("If something goes wrong","문제가 생겼을 때"));
            help.Items.Add(new Card {Title=T("A blue ‘Windows protected your PC’ window pops up","파란색 ‘Windows의 PC 보호’ 창이 떠요"),
                Lines=new[]{T("This is a window Windows shows the first time you run a program it doesn't know well yet. Click [[More info]], then [[Run anyway]].",
                    "Windows가 아직 잘 모르는 프로그램을 처음 실행할 때 띄우는 창이에요. [[추가 정보]]를 누른 다음 [[실행]]을 누르면 돼요.")}});
            help.Items.Add(new Card {Title=T("Video scenes don't show up","영상의 장면이 안 나와요"),
                Lines=new[]{T("The LoveMyShelf folder needs the tools folder inside it. Unzip the downloaded ZIP file again from the start so that all the files are together in one folder.",
                    "LoveMyShelf 폴더 안에 tools 폴더가 있어야 해요. 다운로드한 ZIP 파일의 압축을 처음부터 다시 풀어서 모든 파일이 한 폴더에 함께 있게 해 주세요.")}});
            help.Items.Add(new Card {Title=T("Videos stutter","영상이 끊겨요"),
                Lines=new[]{T("For smooth playback, the libmpv-2.dll file must be in the same folder as LoveMyShelf.exe. It is included when you unzip the whole ZIP file.",
                    "부드럽게 재생하려면 LoveMyShelf.exe와 같은 폴더에 libmpv-2.dll 파일이 있어야 해요. ZIP 파일 전체의 압축을 풀면 함께 들어 있어요.")}});
            help.Items.Add(new Card {Title=T("‘LoveMyShelf is already running’ appears","‘LoveMyShelf가 이미 실행 중이에요’라고 나와요"),
                Lines=new[]{T("Only one LoveMyShelf can run at a time. Click the LoveMyShelf that is already running on the taskbar at the bottom of the screen.",
                    "LoveMyShelf는 한 번에 하나만 실행할 수 있어요. 화면 아래 작업 표시줄에서 이미 실행 중인 LoveMyShelf를 눌러 보세요.")}});
            help.Items.Add(new Card {Title=T("Text is hard to read","글씨가 잘 안 보여요"),
                Lines=new[]{T("Try changing the screen mode in [[Screen, language, updates]].","[[화면·언어·새 버전]]에서 화면 모드를 바꿔 보세요.")}});

            // ----- Advanced (drawn by DrawAdvanced): changing the large view's keys -----
            add("advanced",T("Advanced","고급"));
            return list;
        }
    }
}
