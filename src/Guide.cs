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
        }
        sealed class Section {public string Id,Name;public List<object> Items=new List<object>();}

        static string T(string en,string ko) {return L.Korean?ko:en;}
        static Tuple<string[],string> K(string what,params string[] keys) {return Tuple.Create(keys,what);}
        static Tuple<string,string> W(string word,string meaning) {return Tuple.Create(word,meaning);}

        static GuideWindow open;
        // One Guide & Options window at a time; asking again moves it to the requested section.
        internal static void Open(Window owner,string section) {
            if(open!=null && open.IsLoaded) {
                open.Go(section,null);
                if(open.WindowState==WindowState.Minimized)open.WindowState=WindowState.Normal;
                open.Activate();return;
            }
            open=new GuideWindow(owner,section);
            open.Closed+=delegate {open=null;};
            open.Show();
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
        Preferences Prefs {get {var g=Gallery;return g!=null?g.Settings:new Preferences();}}

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
            PreviewKeyDown+=(s,e)=>{if(e.Key==Key.Escape || e.Key==Key.F1){Close();e.Handled=true;}};
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
            if(toTop)scroller.ScrollToTop();
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
            var chip=new Border {CornerRadius=new CornerRadius(6),BorderThickness=new Thickness(1),Padding=new Thickness(7,0,7,1)};
            chip.SetResourceReference(Border.BackgroundProperty,"Panel");chip.SetResourceReference(Border.BorderBrushProperty,"StrongLine");
            var text=UI.Text(label,Math.Max(12,size-1));text.FontWeight=FontWeights.SemiBold;chip.Child=text;
            return chip;
        }
        // A key drawn like a small rounded keyboard key.
        internal static Border KeyChip(string label) {
            var key=new Border {CornerRadius=new CornerRadius(6),BorderThickness=new Thickness(1,1,1,2),Padding=new Thickness(7,0,7,1),MinWidth=26};
            key.SetResourceReference(Border.BackgroundProperty,"Raised");key.SetResourceReference(Border.BorderBrushProperty,"StrongLine");
            var text=UI.Text(label,12);text.FontWeight=FontWeights.SemiBold;text.HorizontalAlignment=HorizontalAlignment.Center;
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
            var label=UI.Text(T("⚙  Choose it right here","⚙  여기서 바로 정해요"),12);label.FontWeight=FontWeights.SemiBold;
            label.SetResourceReference(TextBlock.ForegroundProperty,"Accent");label.Margin=new Thickness(0,0,0,4);
            stack.Children.Add(label);
            stack.Children.Add(Heading(setting.Title));
            AddLines(stack,setting.Lines,7);
            var prefs=Prefs;
            int current=setting.Get(prefs);
            Panel list=setting.Columns>1?(Panel)new UniformGrid {Columns=setting.Columns}:new StackPanel();
            list.Margin=new Thickness(0,10,0,0);
            for(int i=0;i<setting.Choices.Length;i++)list.Children.Add(ChoiceTile(setting,i,i==current));
            if(current<0 && setting.Custom!=null)list.Children.Add(CustomTile(setting.Custom(prefs)));
            stack.Children.Add(list);
            if(setting.Extra!=null)stack.Children.Add(setting.Extra(this));
            AddTips(stack,setting.Tips);
            var saved=UI.Text(T("✓  Saved. It works right away.","✓  저장했어요. 바로 적용돼요."),13);
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
                badge.Child=UI.Text(T("Default","처음 설정"),11,true);
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
        // A box to type a number in, for a setting whose tiles only list the usual values. Out-of-range numbers are brought into range.
        FrameworkElement NumberBox(Setting setting,string label,Func<Preferences,double> get,Action<Preferences,double> set) {
            var row=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(0,2,0,6)};
            var caption=UI.Text(label,13);caption.VerticalAlignment=VerticalAlignment.Center;caption.Margin=new Thickness(2,0,10,0);row.Children.Add(caption);
            var box=new TextBox {Width=72,Padding=new Thickness(6,4,6,4),FontSize=13,VerticalContentAlignment=VerticalAlignment.Center,
                Text=get(Prefs).ToString("0.###",System.Globalization.CultureInfo.CurrentCulture)};
            System.Windows.Automation.AutomationProperties.SetName(box,label);
            Action apply=()=>{
                double value;
                string text=box.Text.Trim().Replace(',','.');
                if(!Double.TryParse(text,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out value) || Double.IsNaN(value) || Double.IsInfinity(value)){
                    box.Text=get(Prefs).ToString("0.###",System.Globalization.CultureInfo.CurrentCulture);return;
                }
                Apply(setting,p=>set(p,value));
            };
            var ok=UI.Btn(T("Set","정하기"),apply);ok.Margin=new Thickness(8,0,0,0);
            box.KeyDown+=(s,e)=>{if(e.Key==Key.Enter){apply();e.Handled=true;}};
            row.Children.Add(box);row.Children.Add(ok);
            return row;
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
                    var go=UI.Btn(T("Change","바꾸러 가기")+"  ›",()=>Go(sectionId,target.Id));
                    go.VerticalAlignment=VerticalAlignment.Center;go.Margin=new Thickness(10,0,0,0);
                    DockPanel.SetDock(go,Dock.Right);row.Children.Add(go);
                    var text=new StackPanel();
                    var name=Rich(setting.Title,14,false);text.Children.Add(name);
                    int value=setting.Get(prefs);
                    string now=value>=0 && value<setting.Choices.Length?setting.Choices[value].Title:setting.Custom!=null?setting.Custom(prefs):"";
                    if(setting.Id=="folder")now=String.IsNullOrEmpty(prefs.CoverDirectory)?T("Not chosen yet","아직 안 정했어요"):prefs.CoverDirectory;
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
            var path=new TextBox {Text=String.IsNullOrEmpty(folder)?T("Not chosen yet","아직 안 정했어요"):folder,IsReadOnly=true,Padding=new Thickness(8,6,8,6),FontSize=13};
            System.Windows.Automation.AutomationProperties.SetName(path,T("Cover folder","표지 폴더"));
            panel.Children.Add(path);
            var buttons=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(0,8,0,0)};
            var pick=UI.Btn(T("Choose another folder…","다른 폴더 고르기…"),()=>{
                var gallery=Gallery;if(gallery==null)return;
                string chosen=UI.PickFolder(this,L.T("Choose the folder where Love My Shelf will save your cover files."),
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

        // ---------- the words ----------

        static Setting Numbers(string id,string title,int[] values,int def,Func<Preferences,int> get,Action<Preferences,int> set,
            Func<int,string> name,Func<int,string> detail,int columns) {
            return new Setting {Id=id,Title=title,Default=Array.IndexOf(values,def),Columns=columns,
                Choices=values.Select(v=>new Choice(name(v),detail(v))).ToArray(),
                Get=p=>Array.IndexOf(values,get(p)),Set=(p,i)=>set(p,values[i]),
                Custom=p=>T("Now ","지금 ")+name(get(p))+T(" (a value set earlier)"," (예전에 직접 정한 값)")};
        }

        List<Section> BuildSections() {
            var list=new List<Section>();
            Func<string,string,Section> add=(id,name)=>{var s=new Section {Id=id,Name=name};list.Add(s);return s;};

            // ----- Getting started -----
            var start=add("start",T("Getting started","처음 시작하기"));
            start.Items.Add(new Card {Title=T("What Love My Shelf does","Love My Shelf는 이런 프로그램이에요"),
                Lines=new[]{
                    T("Love My Shelf lays out the photos, GIFs and videos in a folder as one grid of small pictures, so you can see at a glance what is in the folder without opening every file one by one.",
                      "폴더 안의 사진·GIF·영상을 작은 그림 바둑판 하나로 쭉 펼쳐 보여 줘서, 파일을 하나하나 열어 보지 않아도 이 폴더에 뭐가 있는지 한눈에 알 수 있게 해 주는 프로그램이에요."),
                    T("For any file you can save a cover: a tall picture (9 wide by 16 tall) of the part that matters, such as one moment of a video. The grid then shows covers, so files that looked alike are easy to tell apart.",
                      "어느 파일이든 표지를 만들어 둘 수 있어요. 표지는 파일에서 중요한 부분(영상이면 한 장면)만 골라 세로로 길게(가로 9, 세로 16) 만든 그림이에요. 그러면 바둑판에 표지가 보여서, 비슷해 보이던 파일도 쉽게 구별돼요."),
                    T("The photo or video you start from is called the original. Love My Shelf never changes or deletes it. A cover is always saved as a new, separate file.",
                      "표지를 만들 때 쓴 원래 사진이나 영상은 원본이라고 불러요. Love My Shelf는 원본을 절대 바꾸거나 지우지 않아요. 표지는 언제나 새 파일로 따로 만들어져요."),
                    T("Love My Shelf doesn't upload your pictures anywhere. Everything happens inside your own computer.",
                      "Love My Shelf는 사진을 인터넷 어디에도 올리지 않아요. 모든 일이 내 컴퓨터 안에서만 일어나요.")}});
            start.Items.Add(new Card {Title=T("Your first cover in 4 steps","4단계로 첫 표지 만들기"),
                Steps=new[]{
                    T("In the folder tree on the left, right-click a folder that has photos or videos and choose [[Add to Love My Shelf]] (like pinning a folder in File Explorer). You can also drag a folder onto Love My Shelf in the tree, or from File Explorer onto this window.",
                      "왼쪽 폴더 나무에서 사진이나 영상이 들어 있는 폴더를 오른쪽 클릭하고 [[Love My Shelf에 추가]]를 골라요(파일 탐색기의 '즐겨찾기에 고정'과 같아요). 폴더를 나무의 Love My Shelf 위로 끌어다 놓거나, 파일 탐색기에서 이 창으로 끌어다 놓아도 돼요."),
                    T("Small pictures (tiles) appear in rows. Click the photo you like. For a video, play it and click [[Pause]] near the moment you like.",
                      "작은 그림(칸)들이 바둑판처럼 나타나요. 마음에 드는 사진을 한 번 눌러요. 영상이면 재생하다가 마음에 드는 장면 근처에서 [[일시정지]]를 눌러요."),
                    T("Click [[Make cover]]. Drag the picture with the mouse to move it, and roll the mouse wheel to make it bigger or smaller.",
                      "[[표지 만들기]]를 눌러요. 마우스로 사진을 끌어서 옮기고, 마우스 휠(가운데 작은 바퀴)을 굴려서 크기를 맞춰요."),
                    T("Click [[Save cover]] and you're done. The new cover opens big right away.",
                      "[[표지 저장]]을 누르면 끝이에요. 만든 표지가 바로 크게 열려요.")}});
            start.Items.Add(new Card {Title=T("How to use this window","이 창을 쓰는 법"),
                Lines=new[]{
                    T("Click a topic in the list on the left to read about it on the right.","왼쪽 목록에서 궁금한 것을 누르면 오른쪽에 설명이 나와요."),
                    T("Boxes with a colored border that say “Choose it right here” are settings. Click a choice and it's saved and used at once. There's no Save button to press.",
                      "색깔 테두리에 ‘여기서 바로 정해요’라고 적힌 상자에서는 설정을 바꿀 수 있어요. 고르는 순간 저장되고 바로 적용돼요. 따로 누를 저장 버튼은 없어요."),
                    T("“Default” marks the choice Love My Shelf starts with. If you're unsure, pick that one.",
                      "‘처음 설정’이라고 적힌 것은 Love My Shelf를 처음 설치했을 때 골라져 있던 것이에요. 잘 모르겠으면 그걸 고르면 돼요."),
                    T("To see every setting in one place, open [[All settings at a glance]] in the list.",
                      "모든 설정을 한곳에서 보고 싶으면 왼쪽 목록의 [[설정 한눈에 보기]]를 눌러요."),
                    T("On any screen, press {{F1}} to open the part of this window that explains that screen.",
                      "어느 화면에서든 키보드의 {{F1}}을 누르면, 지금 보고 있는 화면에 맞는 설명이 바로 열려요.")}});

            add("all",T("All settings at a glance","설정 한눈에 보기"));

            // ----- Browsing -----
            var browse=add("browse",T("Browsing files","파일 둘러보기"));
            browse.Items.Add(new Card {Title=T("Open a folder and the tiles appear","폴더를 열면 칸들이 나와요"),
                Lines=new[]{
                    T("A folder's photos and videos line up as tiles. The top row works like File Explorer's: ← back, → forward, ↑ the folder above, ⟳ refresh, and the address bar showing where you are.",
                      "폴더의 사진과 영상이 칸으로 바둑판처럼 늘어서요. 위쪽 줄은 파일 탐색기와 같아요: ← 뒤로, → 앞으로, ↑ 위 폴더, ⟳ 새로 고침, 그리고 지금 위치를 보여 주는 주소창."),
                    T("In the address bar, click a part to go there, or the › after it to see the folders inside. Click the empty end (or press {{Ctrl}} + {{L}}) to type or paste a path; suggestions appear as you type. {{Enter}} goes, {{Esc}} stops.",
                      "주소창에서 이름을 누르면 그곳으로 가고, 그 뒤의 ›를 누르면 안의 폴더 목록이 나와요. 빈 끝을 누르면(또는 {{Ctrl}} + {{L}}) 경로를 직접 입력하거나 붙여 넣을 수 있고, 입력하는 동안 폴더 이름이 제안돼요. {{Enter}}로 이동, {{Esc}}로 취소."),
                    T("Point the mouse at a tile and its full file name appears right below it (above it on the bottom row), without covering the tile.",
                      "칸에 마우스를 올리면 그 칸 바로 아래(맨 아래 줄이면 위)에 파일 이름 전체가 나와요. 마우스를 올린 칸은 가리지 않아요."),
                    T("A photo that already has a cover shows the cover in its tile.","이미 표지를 만든 사진은 칸에 표지가 보여요."),
                    T("At the bottom left, [[All]] shows everything, [[Finished]] shows only tiles with a cover, and [[Unfinished]] shows only tiles without a cover.",
                      "아래 왼쪽의 [[전체]]는 모두, [[표지 O]]는 표지가 있는 칸만, [[표지 X]]는 아직 표지가 없는 칸만 보여 줘요. X는 오류가 아니라 '아직 표지가 없다'는 뜻이에요."),
                    T("[[Cover collection]] at the bottom middle gathers every cover from every folder. [[Unfinished cover collection]] next to it gathers what still has no cover from your Love My Shelf folders and every folder inside them.",
                      "아래 가운데 [[표지 O 모음]]은 모든 폴더의 표지를 한곳에 모아 보여 줘요. 그 옆 [[표지 X 모음]]은 Love My Shelf에 추가한 폴더와 그 안의 모든 하위 폴더에서 아직 표지가 없는 것(X)만 모아 보여 줘요."),
                    T("When the window is narrow, for example two windows side by side, the buttons at the bottom get shorter names or small pictures, like [[O set]] or [[O]], and [[New window]] and [[Guide & Options]] at the top become small pictures (⧉ ?). Point the mouse at one to see its full name.",
                      "창이 좁으면(예: 창 두 개를 나란히 띄웠을 때) 아래쪽 버튼 이름이 [[O 모음]], [[O]]처럼 짧아지거나 작은 그림으로 바뀌어요. 위쪽 [[새 창]]·[[설명서 겸 옵션]]도 그림(⧉ ?)으로 바뀌어요. 마우스를 올리면 원래 이름이 나와요."),
                    T("[[Cover size]] at the bottom right makes the tiles bigger or smaller in four steps.","아래 오른쪽 [[표지 크기]]를 누를 때마다 칸이 네 단계로 커지거나 작아져요.")}});
            browse.Items.Add(new Card {Title=T("Move around with the keyboard, like File Explorer","파일 탐색기처럼 키보드로 움직여요"),
                Lines=new[]{T("Click a tile once to choose it, and double-click it to open it big, like in File Explorer. (If you'd rather open with one click, change it in [[Large view]] under Options.) The arrow keys move between tiles without opening anything.",
                    "칸을 한 번 누르면 그 칸이 골라지고, 두 번 누르면 크게 열려요. 파일 탐색기와 같아요. (한 번만 눌러도 열리게 하고 싶으면 옵션의 [[크게 보기]]에서 바꿀 수 있어요.) 방향키로는 아무것도 열지 않고 칸 사이를 옮겨 다녀요.")},
                Keys=new[]{K(T("Move between tiles","칸 사이 이동"),"←","→","↑","↓"),K(T("Open the chosen tile big","고른 칸 크게 보기"),"Enter"),
                    K(T("First / last tile","맨 앞 칸 / 맨 끝 칸"),"Home","End"),K(T("Refresh: read the folder again and close the empty slots","새로 고침: 폴더를 다시 읽고 빈칸을 정리해요"),"F5")}});
            browse.Items.Add(new Card {Title=T("Choose several at once","여러 개를 한 번에 골라요"),
                Lines=new[]{T("Then right-click one of the chosen tiles to copy, share, hide or delete them all together.","여러 칸을 고른 다음, 고른 칸 하나를 오른쪽 클릭하면 한꺼번에 복사하기·공유하기·숨기기·휴지통으로 보내기를 할 수 있어요.")},
                Keys=new[]{K(T("Add or remove one tile","한 칸씩 더하거나 빼기"),"Ctrl+","Click"),K(T("Choose everything from the last chosen tile to this one","마지막에 고른 칸부터 이 칸까지 모두 고르기"),"Shift+","Click"),
                    K(T("Choose everything","모두 고르기"),"Ctrl+","A"),K(T("Undo the choosing","고른 것 풀기"),"Esc")}});
            browse.Items.Add(new Card {Title=T("Move files to another folder","다른 폴더로 파일 옮기기"),
                Steps=new[]{T("Press {{Ctrl}} + {{N}} or click [[New window]]. A second window opens beside the first.","{{Ctrl}} + {{N}}을 누르거나 [[새 창]]을 눌러요. 첫 창 옆에 창이 하나 더 열려요."),
                    T("In the new window, open the folder you want to move files into (its tab at the top).","새 창에서 파일을 옮겨 놓을 폴더의 탭을 눌러요."),
                    T("Choose the tiles in the first window and drag them onto the new window. The window's edge lights up and says how many files will move. Let go to move them.","첫 창에서 칸을 고른 다음 새 창으로 끌어다 놓아요. 놓을 수 있는 창은 가장자리가 밝아지고 몇 개가 옮겨지는지 알려 줘요. 놓으면 옮겨져요.")},
                Lines=new[]{T("The covers of each file go along with it, so they still open the right original. Hold {{Ctrl}} while you let go to copy instead of move.","파일에 딸린 표지도 함께 따라가서 계속 같은 원본으로 이어져요. {{Ctrl}}을 누른 채 놓으면 옮기지 않고 복사해요."),
                    T("If the other folder already has a file with that name, Love My Shelf asks: [[Replace]] (the old file goes to the Recycle Bin), [[Skip]] or [[Keep both]] (this one gets a number after its name). Nothing is overwritten without asking.","그 폴더에 같은 이름의 파일이 이미 있으면 물어봐요. [[바꾸기]](있던 파일은 휴지통으로), [[건너뛰기]], [[둘 다 두기]](옮기는 파일 이름 뒤에 번호가 붙어요) 중에서 고를 수 있어요. 묻지 않고 덮어쓰는 일은 없어요."),
                    T("Moved by mistake? Press {{Ctrl}} + {{Z}} and the files go back to where they were.","잘못 옮겼나요? {{Ctrl}} + {{Z}}를 누르면 파일이 원래 자리로 돌아가요.")},
                Keys=new[]{K(T("Open another window","창 하나 더 열기"),"Ctrl+","N"),K(T("Put the last move back","방금 옮긴 것 되돌리기"),"Ctrl+","Z")}});
            browse.Items.Add(new Card {Title=T("Rename, delete, find","이름 바꾸기, 지우기, 찾기"),
                Keys=new[]{K(T("Rename the chosen file. Its JPG and GIF covers get the same new name too.","고른 파일의 이름 바꾸기. 그 파일로 만든 JPG 표지와 GIF 표지 이름도 함께 바뀌어요."),"F2"),
                    K(T("Move to the Recycle Bin. You can bring it back from the Recycle Bin.","휴지통으로 보내기. 휴지통에서 다시 되살릴 수 있어요."),"Delete")},
                Lines=new[]{T("Click the magnifier at the top right (or press {{Ctrl}} + {{F}}) and a search box opens. Type in it and only files whose names contain those letters stay on screen. Press {{Esc}} to clear it and close the box.",
                    "오른쪽 위 돋보기를 누르면(또는 {{Ctrl}} + {{F}}) 검색 칸이 열려요. 글자를 넣으면 이름에 그 글자가 들어간 파일만 남아요. {{Esc}}를 누르면 다시 모두 보이고 칸이 닫혀요."),
                    T("Right-click a tile for more: [[Open file location]] (shows it in File Explorer), [[Open with default app]] (opens it in the program Windows usually uses) and [[Details]] (size, date and so on).",
                      "칸을 오른쪽 클릭하면 더 있어요. [[파일 위치 열기]]는 파일 탐색기에서 그 파일을 보여 주고, [[기본 앱으로 열기]]는 Windows가 평소에 쓰는 프로그램으로 열고, [[정보 보기]]는 크기·날짜 같은 정보를 보여 줘요.")}});
            browse.Items.Add(new Card {Title=T("Tiles stay where they are","칸은 저절로 움직이지 않아요"),
                Lines=new[]{T("Making, hiding or deleting covers leaves every other tile in its place, so you don't lose where you were. A deleted tile leaves an empty slot. Press {{F5}} or choose a sort to tidy up.",
                    "표지를 만들거나 숨기거나 지워도 다른 칸은 그 자리 그대로예요. 그래서 보던 곳을 잃어버리지 않아요. 지운 칸은 빈칸으로 남아요. 정리하고 싶으면 {{F5}}를 누르거나 정렬을 다시 골라요."),
                    T("Your Love My Shelf folders are in the folder tree on the left. Drag one onto another there to change their order. Right-click for [[Open in new window]] and [[Remove from Love My Shelf]]. The two collection buttons at the bottom middle always stay there.",
                      "Love My Shelf에 추가한 폴더는 왼쪽 폴더 나무에 있어요. 나무 안에서 다른 폴더 위로 끌어다 놓으면 순서가 바뀌어요. 오른쪽 클릭하면 [[새 창에서 열기]]와 [[Love My Shelf에서 빼기]]가 있어요. 아래 가운데의 두 모음 버튼은 늘 그 자리에 있어요.")}});
            browse.Items.Add(new Card {Title=T("Choose many at once: hold the right button and drag","여러 개 한 번에 고르기: 오른쪽 버튼을 누른 채 긋기"),
                Lines=new[]{T("Hold the right mouse button down and drag over the tiles: a rectangle appears and every tile it touches is chosen. You can start right on a tile, because the tiles sit close together. (This is Love My Shelf's own way; in File Explorer you'd use the left button on empty space, which works here too.)",
                    "마우스 오른쪽 버튼을 누른 채 칸 위를 그어 보세요. 사각형이 생기고 닿은 칸이 모두 골라져요. 칸끼리 붙어 있어서 칸 위에서 바로 시작해도 돼요. (Love My Shelf만의 방법이에요. 파일 탐색기처럼 빈 곳에서 왼쪽 버튼으로 그어도 돼요.)"),
                    T("Hold {{Ctrl}} while drawing to add to what's already chosen (tiles already chosen get unchosen). Drag to the top or bottom edge to scroll. {{Esc}} cancels. A right click without moving still opens the menu. The folders area works the same way.",
                      "{{Ctrl}}을 누른 채 그으면 이미 고른 것에 더해져요(이미 고른 칸은 풀려요). 위나 아래 끝으로 끌면 화면이 따라 움직여요. {{Esc}}를 누르면 취소돼요. 움직이지 않고 오른쪽을 누르면 지금처럼 메뉴가 떠요. 폴더 자리에서도 똑같이 돼요.")}});
            browse.Items.Add(new Card {Title=T("Copy, cut and paste files","파일 복사·잘라내기·붙여넣기"),
                Lines=new[]{T("{{Ctrl}} + {{C}} copies the chosen files, {{Ctrl}} + {{X}} cuts them (they fade until pasted) and {{Ctrl}} + {{V}} pastes into the folder on screen — the same as File Explorer, so files go back and forth with File Explorer and other windows. Covers go along when files move, and {{Ctrl}} + {{Z}} puts a move back.",
                    "{{Ctrl}} + {{C}}는 고른 파일 복사, {{Ctrl}} + {{X}}는 잘라내기(붙여 넣을 때까지 흐리게 보여요), {{Ctrl}} + {{V}}는 지금 보는 폴더에 붙여넣기예요. 파일 탐색기와 같아서 탐색기나 다른 창과 파일을 주고받을 수 있어요. 옮길 때는 표지도 따라가고, {{Ctrl}} + {{Z}}로 되돌릴 수 있어요.")}});
            browse.Items.Add(new Card {Title=T("The right-click menu","오른쪽 클릭 메뉴"),
                Lines=new[]{T("Right-click a file or folder: Love My Shelf's own items come first, then, below a line, the same menu File Explorer shows (Open with, Share, Compress, Properties and so on — the full one that Windows 11 keeps under \"Show more options\"). Delete, Rename, Cut, Copy and Paste there are done by Love My Shelf, so covers stay with their files. Right-click blank space for the folder's own menu, as in File Explorer: View, Sort by, Refresh, Paste, Undo move, then New, Open in Terminal, Properties and so on.",
                    "파일이나 폴더를 오른쪽 클릭하면 Love My Shelf 메뉴가 먼저, 줄 아래에 파일 탐색기와 같은 메뉴가 나와요(연결 프로그램, 공유, 압축, 속성 등 — Windows 11에서 '더 많은 옵션 표시'를 눌러야 보이는 전체 메뉴예요). 그 안의 삭제·이름 바꾸기·잘라내기·복사·붙여넣기는 Love My Shelf가 대신 해서 표지가 파일을 따라가요. 빈 곳을 오른쪽 클릭하면 파일 탐색기처럼 그 폴더의 메뉴가 나와요: 보기, 정렬 기준, 새로 고침, 붙여넣기, 이동 취소, 그 아래 새로 만들기·터미널에서 열기·속성 등.")}});
            browse.Items.Add(new Card {Title=T("The folder tree on the left, like File Explorer","파일 탐색기처럼 왼쪽 폴더 나무"),
                Lines=new[]{T("Click the button at the top left (or press {{Ctrl}} + {{T}}) to show or hide the folder tree. Love My Shelf comes first (the two collections and your folder tabs), then the same places as File Explorer: your pinned folders, This PC with its drives, and the network.",
                    "위쪽 맨 왼쪽 버튼을 누르면(또는 {{Ctrl}} + {{T}}) 폴더 나무가 열리고 닫혀요. 맨 위는 Love My Shelf(두 모음과 위쪽 폴더들), 그 아래는 파일 탐색기와 같은 곳들이에요: 즐겨찾기에 고정한 폴더, 내 PC와 드라이브, 네트워크."),
                    T("Click a folder to see it; ▸ (or a double-click) unfolds the folders inside. Any folder can be opened this way, even one that isn't a tab. Drag files onto a folder in the tree to move them there.",
                      "폴더를 누르면 그 폴더가 보이고, ▸(또는 두 번 누르기)로 안의 폴더가 펼쳐져요. 위쪽에 추가하지 않은 폴더도 이렇게 바로 열 수 있어요. 파일을 나무의 폴더 위로 끌어다 놓으면 그 폴더로 옮겨져요."),
                    T("Drag the tree's right edge to make it wider or narrower. Love My Shelf remembers whether it is open, its width and which groups you folded.",
                      "나무 오른쪽 경계를 끌면 폭이 바뀌어요. 열려 있는지, 폭, 접어 둔 칸은 다음에도 그대로예요.")}});
            browse.Items.Add(new Setting {Id="foldersPlace",SectionId="browse",Title=T("Where should the folders go?","폴더는 어디에 둘까요?"),
                Lines=new[]{T("The folders inside a folder have their own area with a line across, apart from the photos. [[View]] on that line changes how they look, like File Explorer. In a place with folders but no photos or videos, the folders open by themselves even if you folded them.",
                    "폴더 안의 폴더는 사진과 따로, 가로줄이 그어진 자기 자리에 모여요. 그 줄의 [[보기]]로 파일 탐색기처럼 모양을 바꿀 수 있어요. 사진이나 영상 없이 폴더만 있는 곳에서는 접어 뒀어도 폴더가 저절로 펼쳐져요.")},
                Choices=new[]{new Choice(T("Above the photos","사진 위"),T("Folders first, like File Explorer.","파일 탐색기처럼 폴더가 먼저예요.")),
                    new Choice(T("Below the photos","사진 아래"),T("Photos first; the folders wait at the bottom.","사진이 먼저, 폴더는 아래에 있어요."))},
                Default=0,Columns=2,Get=p=>p.FoldersBelow?1:0,Set=(p,i)=>p.FoldersBelow=i==1});
            browse.Items.Add(new Setting {Id="folderView",SectionId="browse",Title=T("How should the folders look?","폴더를 어떤 모양으로 보여 줄까요?"),
                Lines=new[]{T("The same views as File Explorer's View menu.","파일 탐색기의 [보기] 메뉴와 같은 모양들이에요.")},
                Choices=new[]{new Choice(T("Extra large icons","아주 큰 아이콘"),""),new Choice(T("Large icons","큰 아이콘"),""),new Choice(T("Medium icons","보통 아이콘"),""),
                    new Choice(T("Small icons","작은 아이콘"),""),new Choice(T("List","목록"),""),new Choice(T("Details","자세히"),T("Name, date modified and type in columns.","이름, 수정한 날짜, 유형이 칸으로 나뉘어요.")),
                    new Choice(T("Tiles","타일"),""),new Choice(T("Content","내용"),"")},
                Default=1,Columns=2,Get=p=>Math.Max(0,Math.Min(7,p.FolderView)),Set=(p,i)=>p.FolderView=i});
            browse.Items.Add(new Setting {Id="sort",SectionId="browse",Title=T("In what order should the tiles line up?","칸을 어떤 차례로 늘어놓을까요?"),
                Lines=new[]{T("The [[Sort]] button at the bottom right does the same thing, like the Sort menu in File Explorer. Choosing one reads the folder again and lines the tiles up anew, closing empty slots.",
                    "아래 오른쪽 [[정렬]] 버튼을 눌러도 똑같이 바꿀 수 있어요. 파일 탐색기의 정렬과 같아요. 하나를 고르면 폴더를 다시 읽으면서 새 차례로 늘어서고, 빈칸도 정리돼요.")},
                Choices=new[]{
                    new Choice(T("File name","파일 이름"),T("In alphabetical order. Numbers inside names count as numbers, so 2 comes before 10.","이름의 가나다·ABC 차례예요. 이름 속 숫자는 숫자 크기대로 세서, 2가 10보다 앞에 와요.")),
                    new Choice(T("Date modified","수정한 날짜"),T("By the date each file was last saved or changed, the same date File Explorer shows.","파일이 마지막으로 저장되거나 바뀐 날짜 차례예요. 파일 탐색기의 ‘수정한 날짜’와 같아요.")),
                    new Choice(T("File size","파일 크기"),T("By how big each file is (MB).","파일 용량(MB)이 작은 것과 큰 것 차례예요.")),
                    new Choice(T("File type","파일 종류"),T("Files of the same kind come together: GIFs with GIFs, JPGs with JPGs, MP4s with MP4s.","같은 종류끼리 모여요. GIF는 GIF끼리, JPG는 JPG끼리, MP4는 MP4끼리 붙어 있어요.")),
                    new Choice(T("Cover work order","표지 만든 순서"),T("By the day you made each cover. Tiles without a cover come last.","표지를 만든 때 차례예요. 아직 표지가 없는 칸은 맨 뒤에 와요."))},
                Default=0,
                Get=p=>Array.IndexOf(GalleryWindow.SortChoices,p.Sort),
                Set=(p,i)=>{var kind=GalleryWindow.SortChoices[i];if(kind!=p.Sort)p.Descending=kind==PhotoSort.Modified;p.Sort=kind;},
                Tips=new[]{T("[[Shuffle]] is at the very bottom of the [[Sort]] menu. A shuffled order is only for now: choosing a sort again or reopening the folder brings the normal order back.",
                    "[[순서 섞기]]는 [[정렬]] 메뉴 맨 아래에 있어요. 섞은 차례는 잠깐만 쓰여요. 정렬을 다시 고르거나 폴더를 다시 열면 원래 차례로 돌아와요."),
                    T("The order is the same for every folder.","정렬은 모든 폴더에 똑같이 적용돼요.")}});
            browse.Items.Add(new Setting {Id="direction",SectionId="browse",Title=T("Which end first?","어느 쪽부터 늘어놓을까요?"),
                Lines=new[]{T("The words below change to fit the order chosen above.","아래 말은 위에서 고른 차례에 맞춰 바뀌어요.")},
                Choices=new[]{new Choice(GalleryWindow.DirectionName(Prefs.Sort,false),""),new Choice(GalleryWindow.DirectionName(Prefs.Sort,true),"")},
                Default=Prefs.Sort==PhotoSort.Modified?1:0,Columns=2,
                Get=p=>p.Descending?1:0,Set=(p,i)=>p.Descending=i==1});

            // ----- Making a cover -----
            var make=add("editor",T("Making a cover","표지 만들기"));
            make.Items.Add(new Card {Title=T("Fit it inside the frame","테두리 안에 맞춰요"),
                Lines=new[]{
                    T("In the middle of the cover editor there is a tall frame. Whatever is inside the frame becomes the cover, exactly as you see it.",
                      "표지 만들기 화면 가운데에 세로로 긴 테두리가 있어요. 테두리 안에 들어간 부분이 보이는 그대로 표지가 돼요."),
                    T("Drag the picture to move it. Roll the mouse wheel to make it bigger or smaller.",
                      "사진을 끌면 사진이 움직이고, 마우스 휠을 굴리면 사진이 커지거나 작아져요."),
                    T("If you make the picture smaller than the frame, the empty part becomes black.","사진을 테두리보다 작게 줄이면 남는 곳은 검은색으로 채워져요.")},
                Keys=new[]{K(T("Move the picture","사진 옮기기"),T("Drag","끌기")),K(T("Bigger or smaller","크게·작게"),T("Wheel","휠")),
                    K(T("One step bigger / smaller","한 단계 크게 / 작게"),"+","−"),K(T("Previous / next scene (videos)","이전 장면 / 다음 장면 (영상)"),"D","F"),
                    K(T("Skip back / forward (videos)","뒤로 / 앞으로 건너뛰기 (영상)"),"←","→"),K(T("Skip one second (videos)","1초 건너뛰기 (영상)"),"Shift","←","→"),
                    K(T("Brighter / darker / back to normal","밝게 / 어둡게 / 원래대로"),"E","W","Q"),
                    K(T("Close without saving","저장하지 않고 닫기"),"Esc")}});
            make.Items.Add(new Card {Title=T("Rotation and brightness","회전과 밝기"),
                Lines=new[]{T("Move the [[Rotation]] bar to tilt the picture a little at a time (0.1° per step). For a picture lying on its side, the 90° buttons stand it up in one go.",
                    "[[회전]] 막대를 움직이면 사진이 조금씩(한 번에 0.1도) 기울어요. 옆으로 누운 사진은 90도 버튼으로 한 번에 세울 수 있어요."),
                    T("Move the [[Brightness]] bar to make the picture lighter or darker. [[Reset]] puts both back the way they were.",
                      "[[밝기]] 막대를 움직이면 사진이 밝아지거나 어두워져요. [[처음대로]]를 누르면 둘 다 처음으로 돌아가요.")}});
            make.Items.Add(new Card {Title=T("Several covers from one original","원본 하나로 표지 여러 장"),
                Lines=new[]{T("[[Add cover]] in the large view (or [[Make another cover]] when you right-click a tile) keeps the covers you already made and adds one more. While a video with a cover plays, the buttons are [[Pause]] · [[Add cover]] · [[View cover]] · [[Close]], so you can watch, add a cover and keep watching. [[Edit cover]] changes the cover you are looking at. If the cover has a GIF, you see only the button for the cover on screen: [[Edit GIF cover]] while the GIF cover plays, [[Edit JPG cover]] after [[View JPG cover]].",
                    "크게 보기의 [[표지 추가]](칸을 오른쪽 클릭하면 [[표지 추가로 만들기]])는 먼저 만든 표지를 그대로 두고 한 장을 더 만들어요. 표지가 있는 영상을 재생할 때는 버튼이 [[일시정지]] · [[표지 추가]] · [[표지 보기]] · [[닫기]] 순서라서, 보다가 표지를 더하고 이어서 볼 수 있어요. [[표지 수정하기]]는 지금 보고 있는 표지를 고쳐요. GIF가 있는 표지는 지금 보이는 표지의 버튼만 나와요. GIF 표지가 움직일 때는 [[GIF 표지 수정하기]], [[JPG 표지 보기]]를 누르면 [[JPG 표지 수정하기]]예요."),
                    T("In a folder, an original with two or more covers shows one tile for each cover, side by side, right after the original's first tile. They all belong to the same original, so Delete, Rename and moving to another folder work on the original once. [[Cover collection]] shows every cover as its own tile too.","폴더 화면에서는 표지가 두 장 이상인 원본의 칸이 표지 수만큼 나란히 늘어서요. 처음 만든 표지 바로 뒤에 새 표지가 붙어요. 이 칸들은 모두 같은 원본에서 만든 것이라, 지우기·이름 바꾸기·다른 폴더로 옮기기는 원본 하나에 한 번만 적용돼요. [[표지 O 모음]]에도 표지마다 칸이 하나씩 보여요.")}});
            make.Items.Add(new Setting {Id="snap",SectionId="editor",Title=T("Should the frame stick to the picture's edges?","테두리를 사진 가장자리에 붙일까요?"),
                Lines=new[]{T("While you drag, when an edge of the picture comes close to the frame, it can stick like a magnet so the edge lines up exactly. That stops thin uneven gaps. Keep dragging and it comes loose again.",
                    "사진을 끌다가 사진의 가장자리가 테두리 가까이 오면, 자석처럼 딱 붙어서 반듯하게 맞춰지게 할 수 있어요. 테두리 안에 가느다란 빈틈이 생기는 걸 막아 줘요. 붙은 뒤에도 계속 끌면 다시 떨어져요.")},
                Choices=new[]{new Choice(T("Stick (on)","붙이기 (켜기)"),T("An edge that comes close snaps into line.","가장자리가 가까이 오면 자석처럼 붙어요.")),
                    new Choice(T("Don't stick (off)","붙이지 않기 (끄기)"),T("The picture stops exactly where you leave it. Good for very small moves.","끈 그대로 멈춰요. 아주 조금씩 옮기고 싶을 때 좋아요."))},
                Default=0,Columns=2,Get=p=>p.SnapEdges?0:1,Set=(p,i)=>p.SnapEdges=i==0});
            make.Items.Add(Numbers("wheel",T("How much should one wheel step zoom?","휠을 한 칸 굴릴 때 얼마나 커질까요?"),new[]{3,5,7,10,15},7,
                p=>p.WheelZoomPercent,(p,v)=>p.WheelZoomPercent=v,v=>v+"%",
                v=>v==3?T("Very fine steps, for exact fitting","아주 조금씩. 세밀하게 맞출 때"):v==7?T("Enough for most pictures","대부분 이걸로 충분해요"):v==15?T("Big steps, to change size quickly","성큼성큼. 크기를 빨리 바꿀 때"):"",3));
            ((Setting)make.Items.Last()).Lines=new[]{T("Each step of the mouse wheel makes the picture this much bigger or smaller. Smaller numbers change it a little at a time; bigger numbers change it a lot at once.",
                "마우스 휠을 한 칸 굴릴 때마다 사진이 이만큼 커지거나 작아져요. 숫자가 작을수록 조금씩, 클수록 한 번에 많이 변해요."),
                T("A cover editor that is already open keeps its old setting until you open it again.","이미 열려 있는 표지 만들기 창은 다시 열 때부터 바뀌어요.")};

            make.Items.Add(new Setting {Id="pintop",SectionId="editor",Title=T("Should the wheel keep the top of the frame in place?","휠로 키울 때 테두리 윗줄을 붙잡아 둘까요?"),
                Lines=new[]{T("When this is on, rolling the wheel makes the picture bigger or smaller around the top line of the frame, so whatever you lined up with the top stays there. Dragging is always free.",
                    "켜 두면 휠로 키우고 줄여도 테두리 윗줄 자리가 그대로라서, 윗줄에 맞춰 둔 부분이 흐트러지지 않아요. 끌 때는 언제나 자유롭게 움직여요.")},
                Choices=new[]{new Choice(T("Keep the top line (on)","윗줄 붙잡기 (켜기)"),T("The wheel zooms around the top line of the frame.","휠이 테두리 윗줄을 기준으로 키우고 줄여요.")),
                    new Choice(T("Zoom around the middle (off)","가운데 기준 (끄기)"),T("The wheel zooms around the middle of the frame.","휠이 테두리 가운데를 기준으로 키우고 줄여요."))},
                Default=0,Columns=2,Get=p=>p.PinTopOnWheel?0:1,Set=(p,i)=>p.PinTopOnWheel=i==0});

            // ----- Saving covers -----
            var save=add("save",T("Saving covers","표지 저장하기"));
            save.Items.Add(new Card {Title=T("What appears when you save","저장하면 무엇이 생기나요?"),
                Lines=new[]{
                    T("When you click [[Save cover]], one new picture file (a JPG) appears on your computer. The original stays as it is; only the cover is a new file.",
                      "[[표지 저장]]을 누르면 컴퓨터에 새 사진 파일(JPG)이 하나 생겨요. 원본은 그대로 두고, 표지만 새 파일로 따로 만드는 거예요."),
                    T("The cover's name is the original's name followed by “cover” and a short tag of letters and numbers. The tag keeps names from clashing when you make several covers from one original.",
                      "표지 파일 이름은 원본 이름 뒤에 ‘cover’와 알파벳·숫자가 섞인 꼬리표가 붙어요. 꼬리표 덕분에 원본 하나로 표지를 여러 장 만들어도 이름이 겹치지 않아요."),
                    T("The cover is cut straight out of the original at full quality, so a sharp original gives a sharp cover.",
                      "표지는 원본에서 그대로 잘라 내서 가장 좋은 화질로 저장해요. 그래서 원본이 선명하면 표지도 선명해요.")}});
            save.Items.Add(new Setting {Id="place",SectionId="save",Title=T("Where should new covers go?","만든 표지를 어디에 넣어 둘까요?"),
                Lines=new[]{T("Choose one of three places for cover files. The examples are for a cover made from “Puppy.jpg” in a folder called “Photos”.",
                    "표지 파일을 넣어 둘 곳을 세 가지 중에서 골라요. 아래 예시는 ‘사진’ 폴더 안의 ‘강아지.jpg’로 표지를 만들었을 때예요.")},
                Choices=new[]{
                    new Choice(T("① Gather them all in one cover folder","① 표지 폴더 한 곳에 모으기"),
                        T("Whichever folder the photo is in, every cover goes into the one cover folder chosen below. Handy for keeping all covers in one place.\nExample: cover folder › Puppy-cover-1a2b….jpg",
                          "어느 폴더의 사진으로 만들든, 표지는 모두 아래에서 정한 표지 폴더 하나에 차곡차곡 모여요. 표지만 한곳에 모아 두고 싶을 때 좋아요.\n예) 표지 폴더 › 강아지-cover-1a2b….jpg")),
                    new Choice(T("② Put it in the same folder as the original","② 원본 사진과 같은 폴더에 넣기"),
                        T("The cover goes into the folder the original is in. Its name starts with the original's name, so when File Explorer sorts by name, the cover sits right under the original. Good if you always want the two together. In Love My Shelf, these cover files don't take tiles of their own; they show as the original's cover.\nExample: Photos › Puppy.jpg, Puppy_cover_1a2b….jpg",
                          "표지가 원본이 있는 폴더에 같이 들어가요. 표지 이름이 원본 이름으로 시작해서, 파일 탐색기에서 이름 차례로 보면 원본 바로 다음 줄에 표지가 보여요. 원본과 표지를 늘 붙여 두고 싶을 때 좋아요. Love My Shelf 화면에서는 이 표지 파일이 칸을 따로 차지하지 않고, 원본 칸에 표지로 보여요.\n예) 사진 › 강아지.jpg, 강아지_cover_1a2b….jpg")),
                    new Choice(T("③ Make a “Love My Shelf 표지” folder inside the original's folder","③ 원본 폴더 안에 ‘Love My Shelf 표지’ 폴더를 만들어 넣기"),
                        T("A folder named “Love My Shelf 표지” is made inside the original's folder, and covers go in there. Good if you want covers kept per folder but not mixed in with the photos.\nExample: Photos › Love My Shelf 표지 › Puppy-cover-1a2b….jpg",
                          "원본이 있는 폴더 안에 ‘Love My Shelf 표지’라는 폴더가 저절로 생기고, 표지는 그 안에 들어가요. 폴더마다 표지를 따로 모으고 싶지만, 원본 사진 사이에 섞이는 건 싫을 때 좋아요.\n예) 사진 › Love My Shelf 표지 › 강아지-cover-1a2b….jpg"))},
                Default=0,Get=p=>(int)p.CoverPlace,Set=(p,i)=>p.CoverPlace=(CoverPlace)i,
                Tips=new[]{T("With ② or ③, if a folder can't be written to (a connected phone or some cloud folders), the cover goes to the cover folder instead and you'll see a message.",
                    "②나 ③을 골랐는데 휴대폰을 연결한 폴더나 일부 클라우드 폴더처럼 저장할 수 없는 곳이면, 대신 표지 폴더에 저장하고 알려 드려요."),
                    T("Changing this doesn't move covers you already made. Only covers made from now on go to the new place.","바꿔도 이미 만든 표지는 원래 자리에 그대로 있어요. 바꾼 뒤에 만드는 표지부터 새 자리에 저장돼요."),
                    T("Wherever they're saved, [[Cover collection]] shows them all.","어디에 저장하든 [[표지 O 모음]]에는 모든 표지가 다 보여요.")}});
            save.Items.Add(new Setting {Id="folder",SectionId="save",Title=T("Which folder is the cover folder?","표지 폴더는 어디로 할까요?"),
                Lines=new[]{T("With ① above, covers gather here. With ② or ③, this folder is used when the original's folder can't be written to. It was chosen the first time you saved a cover.",
                    "위에서 ①을 고르면 표지가 여기에 모여요. ②나 ③을 고르면, 원본 폴더에 저장할 수 없을 때 대신 쓰는 곳이에요. 처음 표지를 저장할 때 정한 폴더가 들어 있어요.")},
                Get=p=>0,Set=(p,i)=>{},Extra=g=>g.CoverFolderPicker(),
                Tips=new[]{T("Covers already made stay where they are when you pick another folder.","다른 폴더로 바꿔도 이미 만든 표지는 옮겨지지 않아요.")}});
            save.Items.Add(new Card {Title=T("The Open cover folder button","표지 폴더 열기 버튼"),
                Lines=new[]{T("[[Open cover folder]] at the bottom right, next to [[Sort]], opens the folder with your covers in File Explorer. With ② or ③, it opens the folder you're looking at (or the “Love My Shelf 표지” folder inside it).",
                    "아래 오른쪽 [[정렬]] 옆 [[표지 폴더 열기]]를 누르면 표지가 들어 있는 폴더가 파일 탐색기로 열려요. ②나 ③을 골랐다면 지금 보고 있는 폴더(또는 그 안의 ‘Love My Shelf 표지’ 폴더)가 열려요.")}});

            // ----- Scenes from videos -----
            var video=add("video",T("Scenes from videos","영상에서 장면 고르기"));
            video.Items.Add(new Card {Title=T("Catch the exact moment","딱 그 순간을 잡아요"),
                Lines=new[]{T("A video is many pictures (scenes) shown quickly one after another. You can pick any single one of them for a cover.",
                    "영상은 그림(장면) 여러 장을 빠르게 넘겨 보여 주는 거예요. 그중 딱 한 장을 골라 표지로 만들 수 있어요.")},
                Steps=new[]{T("Click the video to play it, and click [[Pause]] near the moment you want.","영상 칸을 눌러 재생하고, 원하는 순간 근처에서 [[일시정지]]를 눌러요."),
                    T("Press {{D}} and {{F}} to step one scene back or forward while paused.","멈춘 상태에서 {{D}}와 {{F}}를 누르면 한 장면씩 뒤로·앞으로 움직여요."),
                    T("Click [[Make cover]]. The scenes around the paused moment appear as small pictures; click the best one.","[[표지 만들기]]를 누르면 멈춘 곳 앞뒤의 장면들이 작은 그림으로 나와요. 가장 마음에 드는 장면을 눌러요."),
                    T("Fit it in the frame as with a photo, and click [[Save cover]]. {{D}} and {{F}} work in the cover editor too.","사진처럼 테두리에 맞추고 [[표지 저장]]을 눌러요. 표지 만들기 화면에서도 {{D}}와 {{F}}로 이전·다음 장면으로 옮겨 갈 수 있어요.")},
                Keys=new[]{K(T("One scene back / forward","한 장면 뒤로 / 앞으로"),"D","F"),K(T("Skip back / forward","뒤로 / 앞으로 건너뛰기"),"←","→"),K(T("Skip one second","1초 건너뛰기"),"Shift","←","→")},
                Tips=new[]{T("[[All frames]] in the list shows every scene of the whole video.","장면 목록의 [[모든 장면]]을 누르면 영상 전체의 장면을 다 볼 수 있어요.")}});
            video.Items.Add(Numbers("skip",T("How far should ← and → skip?","←, → 키로 얼마나 건너뛸까요?"),new[]{3,5,10,30},5,
                p=>p.SkipSeconds,(p,v)=>p.SkipSeconds=v,v=>T(v+" seconds",v+"초"),
                v=>v==3?T("Small steps","조금씩"):v==5?T("Usual","보통"):v==10?T("Bigger steps","크게"):T("Long jumps through long videos","긴 영상에서 성큼성큼"),4));
            ((Setting)video.Items.Last()).Lines=new[]{T("While watching, press → to skip ahead and ← to skip back. Choose how many seconds one press skips.",
                "영상을 볼 때 키보드의 → 키를 누르면 앞으로, ← 키를 누르면 뒤로 건너뛰어요. 한 번 누를 때 몇 초씩 건너뛸지 골라요.")};
            video.Items.Add(Numbers("brightnessStep",T("How much should E and W change the brightness?","E, W 키로 밝기를 얼마나 바꿀까요?"),new[]{1,2,3,4,5,10},3,
                p=>p.BrightnessStep,(p,v)=>p.BrightnessStep=v,v=>v.ToString(),
                v=>v==1?T("Finest","가장 세밀하게"):v==3?T("Usual","보통"):v==10?T("Big steps","크게"):"",3));
            ((Setting)video.Items.Last()).Lines=new[]{T("While watching a video or making a cover, E makes the picture brighter and W darker. Q puts it back to normal. Choose how much one press changes it.",
                "영상을 보거나 표지를 만들 때 E를 누르면 밝아지고 W를 누르면 어두워져요. Q를 누르면 원래대로 돌아와요. 한 번 누를 때 얼마나 바뀔지 골라요.")};
            video.Items.Add(Numbers("sceneStep",T("How far apart is one scene? (scene step)","한 장면은 몇 초 간격일까요? (장면 간격)"),new[]{150,200,250,500},200,
                p=>p.SceneStepMs,(p,v)=>p.SceneStepMs=v,v=>T((v/1000.0).ToString("0.##",System.Globalization.CultureInfo.InvariantCulture)+" s",(v/1000.0).ToString("0.##",System.Globalization.CultureInfo.InvariantCulture)+"초"),
                v=>v==150?T("Closest together. Takes longer to read","가장 촘촘해요. 읽는 데 오래 걸려요"):v==200?T("A blink or a wink is rarely missed","눈 깜빡임도 윙크도 거의 안 놓쳐요"):v==500?T("Fastest. Rough","가장 빨라요. 대신 듬성듬성해요"):"",4));
            var stepSetting=(Setting)video.Items.Last();
            stepSetting.Lines=new[]{T("This is the size of one 'scene' everywhere in Love My Shelf: the scene grid, the D / F keys, the thumbnails above the seek bar and the Make GIF cover window. The smaller the number, the finer the steps, but the longer it takes to read. A video can't go finer than its own pictures, so a smaller number uses the closest it has.",
                "Love My Shelf 어디서든 '한 장면'은 이만큼이에요. 장면 고르기 바둑판, D·F 키, 재생바 위 작은 그림, GIF 표지 만들기 창이 모두 이 간격으로 움직여요. 작을수록 촘촘하지만 읽는 데 오래 걸려요. 영상 자체 그림보다 더 잘게는 나눌 수 없어서, 그보다 작게 쓰면 가장 가까운 값으로 맞춰요.")};
            stepSetting.Extra=g=>g.NumberBox(stepSetting,T("Or type your own, in seconds (0.03 to 2)","직접 쓰기 (초, 0.03 ~ 2)"),p=>p.SceneStepMs/1000.0,
                (p,seconds)=>p.SceneStepMs=(int)Math.Round(Math.Max(0.03,Math.Min(2,seconds))*1000));

            // ----- GIF covers -----
            var gif=add("gif",T("GIF covers","GIF 표지 만들기"));
            gif.Items.Add(new Card {Title=T("JPG covers and GIF covers","JPG 표지와 GIF 표지"),
                Lines=new[]{T("A still cover is a JPG cover. A moving cover is a GIF cover, whatever kind of file it is saved as (GIF, WebP or MP4).",
                    "멈춰 있는 표지는 JPG 표지, 움직이는 표지는 GIF 표지라고 불러요. 움직이는 표지는 GIF·WebP·MP4 중 무엇으로 저장하든 모두 GIF 표지라고 불러요."),
                    T("A GIF cover is made around a JPG cover from a video, GIF or WebP: a few seconds before and after the cover's moment, with the same frame, rotation and brightness.",
                      "GIF 표지는 영상·GIF·WebP로 만든 JPG 표지를 바탕으로 만들어요. 표지 장면의 앞뒤 몇 초를, JPG 표지와 똑같은 테두리·기울기·밝기로 움직이게 해요."),
                    T("You'll find [[Make GIF cover]] in the cover editor, in the large view and when you right-click a tile.","[[GIF 표지 만들기]]는 표지 만들기 화면, 크게 보기 화면, 칸을 오른쪽 클릭했을 때 나오는 메뉴에 있어요."),
                    T("In the tiles the JPG cover shows first; the GIF cover moves when you point at it (you can change this below).","칸에는 JPG 표지가 먼저 보이고, 마우스를 올리면 GIF 표지가 움직여요(아래에서 바꿀 수 있어요).")}});
            gif.Items.Add(new Card {Title=T("Choose the part on the scene strip","장면 띠에서 범위를 골라요"),
                Lines=new[]{T("At the bottom of the Make GIF cover window, scenes of the video are laid out side by side in a strip. Two blue handles on it mark where the GIF starts and ends.",
                    "GIF 표지 만들기 창 아래쪽에는 영상 장면이 가로로 쭉 늘어선 띠가 있어요. 띠 위의 파란 손잡이 두 개가 GIF가 시작하는 곳과 끝나는 곳이에요.")},
                Steps=new[]{T("Drag the two blue handles to the start and the end. They move one scene at a time (the scene step, under Scenes from videos), and so do ‹ and › under the strip.","파란 손잡이 두 개를 시작과 끝으로 끌어요. 손잡이는 한 장면씩('영상에서 장면 고르기'의 장면 간격만큼) 맞춰져 움직이고, 띠 아래 ‹ › 버튼도 한 장면씩 옮겨요."),
                    T("Drag the pictures in the strip to see other parts of the video. [[Back to the cover]] returns to the cover's moment.","띠의 그림을 끌면 영상의 다른 부분이 보여요. [[표지로 돌아가기]]를 누르면 표지 장면으로 돌아와요."),
                    T("Choose the speed and how it plays: [[Loop]] plays it over and over, [[Back and forth]] plays forward then backward, [[Backward]] plays it in reverse.","속도와 재생 방식을 골라요. [[반복]]은 같은 방향으로 계속, [[왕복]]은 앞으로 갔다가 거꾸로 돌아오고, [[거꾸로]]는 뒤에서부터 재생해요."),
                    T("The preview at the top appears at once as a quick preview while you move the handles. A moment after you stop, it is replaced by the real file, so the size shown is exact. When you like it, click [[Make]].","위의 미리 보기는 손잡이를 옮기는 동안 빠른 미리 보기로 바로 보여요. 손을 떼고 잠깐 있으면 실제로 만든 파일로 바뀌어서, 그때 보이는 크기는 정확해요. 마음에 들면 [[만들기]]를 눌러요.")},
                Keys=new[]{K(T("Move the last handle you used one scene","마지막에 잡은 손잡이를 한 장면씩 옮기기"),"←","→"),K(T("Pause or play the preview","미리보기 멈춤 / 재생"),"Space")},
                Tips=new[]{T("Making runs in the background, so you can keep using Love My Shelf. A message at the bottom right tells you when it's done.","만드는 일은 뒤에서 혼자 하니까 그동안 Love My Shelf를 계속 써도 돼요. 다 되면 오른쪽 아래에 알려 드려요.")}});
            gif.Items.Add(new Card {Title=T("GIF, WebP, MP4: what's the difference?","GIF·WebP·MP4는 무엇이 다른가요?"),
                Lines=new[]{T("A GIF cover can be saved as one of three kinds of file. All three are silent. The sizes below are for a 3-second cover at 15 pictures a second, 960 pixels tall.",
                    "GIF 표지는 세 가지 파일 중 하나로 저장할 수 있어요. 셋 다 소리는 들어가지 않아요. 아래 크기는 3초짜리를 1초에 15장, 세로 960픽셀로 만들었을 때예요.")}});
            gif.Items.Add(new Card {Title="GIF",
                Lines=new[]{T("The most widely used kind of moving picture, so it moves almost everywhere.",
                    "가장 널리 쓰이는 ‘움직이는 그림’ 형식이라 거의 모든 곳에서 움직여요."),
                    T("But each picture can use only 256 colors. Where colors change smoothly, like skin or stage lights, tiny dot patterns or steps can show. Love My Shelf mixes the dots evenly to hide this as much as it can.",
                      "대신 그림 한 장에 색을 256가지까지만 쓸 수 있어요. 얼굴이나 조명처럼 색이 부드럽게 이어지는 곳에 자잘한 점무늬나 계단 같은 줄이 생길 수 있어요. Love My Shelf는 점을 고르게 섞어서 최대한 티 나지 않게 만들어요."),
                    T("It makes the biggest files: usually 7 to 13 MB for the example above. Longer or bigger GIFs grow fast.",
                      "파일이 가장 커요. 위의 예시가 보통 7~13MB예요. 길고 크게 만들수록 금방 커져요.")},
                Tips=new[]{
                    T("It moves on Discord, X (Twitter) and most other places, but they limit how big a file can be.","디스코드, X(트위터) 같은 곳에서도 움직여요. 다만 올릴 수 있는 파일 크기에 한도가 있어요.")}});
            gif.Items.Add(new Card {Title="WebP",
                Lines=new[]{T("A newer kind of file made by Google. It moves like a GIF but keeps colors much smoother, and the file is about a tenth of the size (about 1 MB for the example).",
                    "구글이 만든 새로운 형식이에요. GIF처럼 움직이면서도 색을 훨씬 곱게 담고, 파일은 GIF의 10분의 1쯤으로 작아요(위의 예시가 약 1MB)."),
                    T("Some places can't show it yet: older programs and some apps show only the first picture, standing still, or can't open it at all.",
                      "대신 아직 못 보여 주는 곳이 있어요. 오래된 프로그램이나 일부 앱은 첫 장면만 멈춘 그림으로 보여 주거나 아예 못 열어요."),
                    T("It's the best choice for keeping and enjoying covers inside Love My Shelf: it moves smoothly in the tiles and takes little space.",
                      "Love My Shelf 안에서 모아 두고 감상하기에는 가장 좋아요. 칸에서도 부드럽게 움직이고 공간도 덜 차지해요.")},
                Tips=new[]{T("Discord: it moves like a GIF.","디스코드에서도 GIF처럼 움직여요.")}});
            gif.Items.Add(new Card {Title="MP4",
                Lines=new[]{T("A video file. Of the three it's the smallest (about 0.8 MB for the example) and the sharpest.",
                    "동영상 형식이에요. 셋 중 파일이 가장 작고(위의 예시가 약 0.8MB) 화질도 가장 좋아요."),
                    T("But it's treated as a video, not a picture. Where you post it, it may get a play button, play only when tapped, or stop after one play. Whether it repeats is up to the place you post it.",
                      "대신 그림이 아니라 동영상으로 취급돼요. 올린 곳에 따라 재생 버튼이 붙거나, 눌러야 재생되거나, 한 번만 재생하고 멈출 수 있어요. 계속 반복할지는 올린 곳이 정해요."),
                    T("It works anywhere that takes videos.","동영상을 올릴 수 있는 곳이면 어디서든 볼 수 있어요.")}});
            gif.Items.Add(new Card {Title=T("Which one should I choose?","무엇을 고르면 좋을까요?"),
                Lines=new[]{T("Not sure → GIF: it moves wherever you post it.","잘 모르겠으면 → GIF: 어디에 올려도 움직여요."),
                    T("Keeping it in Love My Shelf or posting on Discord → WebP: smooth colors, small files.","Love My Shelf 안에서 모아 보거나 디스코드에 올릴 거면 → WebP: 색이 곱고 파일이 작아요."),
                    T("Fine with it going up as a video → MP4: smallest and sharpest.","동영상으로 올라가도 괜찮다면 → MP4: 가장 작고 선명해요.")}});
            gif.Items.Add(new Setting {Id="gifFormat",SectionId="gif",Title=T("Which kind of file should a GIF cover start as?","GIF 표지를 처음에 어떤 파일로 만들까요?"),
                Lines=new[]{T("This is chosen when the Make GIF cover window opens. You can still change it each time under [[More choices]] in that window.",
                    "GIF 표지 만들기 창을 열 때 처음 골라져 있을 형식이에요. 그 창의 [[더 보기]]에서 그때그때 바꿀 수도 있어요.")},
                Choices=new[]{new Choice("GIF",T("Moves everywhere · big files","어디서나 움직여요 · 파일이 커요")),
                    new Choice("WebP",T("Smooth colors, small · some apps can't show it","색이 곱고 작아요 · 안 되는 곳도 있어요")),
                    new Choice("MP4",T("Smallest and sharpest · goes up as a video","가장 작고 선명해요 · 동영상으로 올라가요"))},
                Default=0,Get=p=>p.GifFormat,Set=(p,i)=>p.GifFormat=i});
            gif.Items.Add(new Setting {Id="gifQuality",SectionId="gif",Title=T("Picture quality","화질은 어느 정도로 할까요?"),
                Lines=new[]{T("Higher quality looks better and makes bigger files. For GIF it decides how finely the 256 colors are mixed; for WebP and MP4 it decides how much the picture is squeezed.",
                    "화질이 높을수록 보기 좋지만 파일이 커져요. GIF에서는 256가지 색을 얼마나 곱게 섞을지, WebP와 MP4에서는 그림을 얼마나 꾹 눌러 담을지를 정해요.")},
                Choices=new[]{new Choice(T("High","높게"),T("The smoothest colors. The file gets bigger.","색이 가장 고와요. 파일이 커져요.")),
                    new Choice(T("Normal","보통"),T("Enough for most covers.","대부분의 표지는 이걸로 충분해요.")),
                    new Choice(T("Smaller file","작게"),T("Smaller files. A GIF uses only 128 colors, so shadows on faces may show bands.","파일이 작아져요. GIF는 색을 128가지만 써서 얼굴 그림자에 줄무늬가 보일 수 있어요."))},
                Default=1,Get=p=>p.GifQuality,Set=(p,i)=>p.GifQuality=i});
            gif.Items.Add(Numbers("gifFps",T("How many pictures per second? (smoothness)","1초에 몇 장으로 만들까요? (부드러움)"),MotionWindow.Rates,15,
                p=>p.GifFps,(p,v)=>p.GifFps=v,v=>T(v+" a second","1초에 "+v+"장"),
                v=>v==10?T("Looks choppy · small","뚝뚝 끊겨 보여요 · 작아요"):v==15?T("Smooth enough and not too big","부드러움과 크기가 알맞아요"):v==24?T("Same as movies","영화와 같아요"):v==30?T("Very smooth · big","아주 부드러워요 · 커요"):"",3));
            ((Setting)gif.Items.Last()).Lines=new[]{T("Moving pictures are many pictures shown quickly. More pictures per second look smoother but make the file bigger: 30 a second makes a file about twice as big as 15.",
                "움직이는 그림은 그림 여러 장을 빠르게 넘기는 거예요. 1초에 넘기는 장 수가 많을수록 부드럽지만 그만큼 파일도 커져요. 1초에 30장이면 15장보다 파일이 약 2배가 돼요.")};
            gif.Items.Add(Numbers("gifHeight",T("How tall, in pixels?","세로 크기는 얼마로 할까요?"),MotionWindow.Heights,960,
                p=>p.GifHeight,(p,v)=>p.GifHeight=v,v=>T(v+" pixels",v+"픽셀"),
                v=>v==480?T("Small, for chats","채팅에서 작게 볼 때"):v==960?T("Sharp on most screens","보통 화면에서 선명해요"):v==1280?T("Very sharp · big file","아주 선명해요 · 파일이 커요"):"",3));
            ((Setting)gif.Items.Last()).Lines=new[]{T("How many pixels (dots) tall the GIF cover is. Taller is sharper but bigger: twice as tall makes a file about four times as big. It's never made bigger than the original.",
                "GIF 표지의 세로에 점(픽셀)을 몇 개 쓸지예요. 클수록 선명하지만 파일이 커져요. 세로가 2배가 되면 파일은 약 4배가 돼요. 원본보다 크게 만들지는 않아요(원본이 작으면 원본 크기로 만들어요).")};
            gif.Items.Add(new Setting {Id="gifSide",SectionId="gif",Title=T("How much before and after the cover at first?","처음에 표지 앞뒤로 몇 초를 고를까요?"),
                Lines=new[]{T("When the Make GIF cover window opens, the two blue handles sit before and after the cover's moment. This is how far each one starts from it. With 1.5 seconds: 1.5 before + 1.5 after = 3 seconds in all. You can always drag the handles.",
                    "GIF 표지 만들기 창을 열면 파란 손잡이 두 개가 표지 장면의 앞과 뒤에 놓여 있어요. 처음에 표지에서 각각 몇 초 떨어져 있을지예요. 1.5초면 앞 1.5초 + 뒤 1.5초 = 모두 3초짜리가 돼요. 창에서 손잡이를 끌어 언제든 바꿀 수 있어요.")},
                Choices=new[]{new Choice(T("0.5 seconds each (1 s in all)","앞뒤 0.5초씩 (모두 1초)"),""),new Choice(T("1 second each (2 s in all)","앞뒤 1초씩 (모두 2초)"),""),
                    new Choice(T("1.5 seconds each (3 s in all)","앞뒤 1.5초씩 (모두 3초)"),""),new Choice(T("2 seconds each (4 s in all)","앞뒤 2초씩 (모두 4초)"),""),
                    new Choice(T("3 seconds each (6 s in all)","앞뒤 3초씩 (모두 6초)"),"")},
                Default=2,Columns=2,Get=p=>Array.IndexOf(new[]{5,10,15,20,30},p.GifSideTenths),Set=(p,i)=>p.GifSideTenths=new[]{5,10,15,20,30}[i],
                Custom=p=>T("Now "+(p.GifSideTenths/10.0)+" s each","지금 앞뒤 "+(p.GifSideTenths/10.0)+"초씩")});
            gif.Items.Add(Numbers("strip",T("How much of the video should the strip show at first?","장면 띠에 처음 몇 초를 보여 줄까요?"),new[]{4,6,10,20,30},10,
                p=>p.GifStripSeconds,(p,v)=>p.GifStripSeconds=v,v=>T(v+" seconds",v+"초"),
                v=>v==4?T("Easy to aim precisely","세밀하게 맞추기 쉬워요"):v==10?T("A good balance","알맞아요"):v==30?T("See a lot around the cover","앞뒤를 넓게 봐요"):"",3));
            ((Setting)gif.Items.Last()).Lines=new[]{T("How long a stretch of video the strip shows when the window opens, with the cover's moment in the middle. Shorter makes it easier to place the handles exactly; longer shows more before and after. Drag the pictures to see the rest.",
                "GIF 표지 만들기 창을 처음 열었을 때, 띠 한 줄에 영상을 몇 초어치 보여 줄지예요. 표지 장면이 가운데에 와요. 짧을수록 손잡이를 세밀하게 옮기기 쉽고, 길수록 앞뒤를 넓게 볼 수 있어요. 그림을 끌면 안 보이던 부분도 볼 수 있어요.")};
            gif.Items.Add(Numbers("gifPreviewStep",T("How far apart should the quick preview read scenes?","빠른 미리 보기는 몇 초마다 한 장씩 읽을까요?"),new[]{50,100,200,500},100,
                p=>p.GifPreviewStepMs,(p,v)=>p.GifPreviewStepMs=v,v=>T((v/1000.0).ToString("0.###",System.Globalization.CultureInfo.InvariantCulture)+" s",(v/1000.0).ToString("0.###",System.Globalization.CultureInfo.InvariantCulture)+"초"),
                v=>v==50?T("Very smooth, takes longer to appear","아주 부드러워요. 나오는 데 더 걸려요"):v==100?T("A good balance","알맞아요"):v==200?T("Appears faster, a little choppy","더 빨리 나와요. 조금 끊겨요"):v==500?T("Appears fastest, choppy","가장 빨리 나와요. 대신 뚝뚝 끊겨요"):"",4));
            var previewSetting=(Setting)gif.Items.Last();
            previewSetting.Lines=new[]{T("Only for the quick preview in the Make GIF cover window. While you move the handles, the preview is made at once from pictures read this far apart; a moment after you stop, the real file replaces it. This doesn't change the finished GIF cover, and it's separate from the scene step under Scenes from videos.",
                "GIF 표지 만들기 창의 빠른 미리 보기에만 쓰여요. 손잡이를 옮기는 동안에는 이 간격마다 한 장씩 읽은 그림으로 미리 보기를 바로 보여 주고, 손을 떼고 잠깐 있으면 실제 파일로 바뀌어요. 다 만든 GIF 표지에는 영향이 없고, '영상에서 장면 고르기'의 장면 간격과도 상관없어요."),
                T("A video can't go finer than its own pictures. If a long part would need more than 400 pictures, that preview reads them a little further apart so the computer doesn't slow down.",
                "영상 자체 그림보다 더 잘게는 읽을 수 없어요. 고른 구간이 길어서 그림이 400장을 넘으면, 컴퓨터가 느려지지 않게 그 미리 보기만 간격을 조금 넓혀요.")};
            previewSetting.Extra=g=>g.NumberBox(previewSetting,T("Or type your own, in seconds (0.03 to 2)","직접 쓰기 (초, 0.03 ~ 2)"),p=>p.GifPreviewStepMs/1000.0,
                (p,seconds)=>p.GifPreviewStepMs=(int)Math.Round(Math.Max(0.03,Math.Min(2,seconds))*1000));
            gif.Items.Add(new Setting {Id="gridGif",SectionId="gif",Title=T("Should GIF covers move in the tiles?","칸에서 GIF 표지를 움직일까요?"),
                Lines=new[]{T("How GIF covers show while you browse. The tile shows the JPG cover first.","파일을 둘러볼 때 칸에서 GIF 표지를 어떻게 보여 줄지예요. 칸에는 먼저 JPG 표지가 보여요.")},
                Choices=new[]{new Choice(T("Don't move","움직이지 않기"),T("Tiles always show the JPG cover. Easiest on the computer.","칸에는 언제나 JPG 표지만 보여요. 컴퓨터가 가장 편해요.")),
                    new Choice(T("Move when the mouse is on it","마우스를 올렸을 때만 움직이기"),T("A GIF cover moves while you point at its tile.","칸에 마우스를 올리면 그 칸의 GIF 표지가 움직여요.")),
                    new Choice(T("Always move","언제나 움직이기"),T("Every GIF cover on the screen moves. Ones you can't see rest, so scrolling stays smooth.","화면에 보이는 GIF 표지가 모두 움직여요. 화면 밖의 것은 쉬고 있어서 스크롤은 부드러워요."))},
                Default=1,Get=p=>(int)p.GridGif,Set=(p,i)=>p.GridGif=(GifPlayback)i});

            // ----- Large view -----
            var large=add("large",T("Large view","크게 보기"));
            large.Items.Add(new Card {Title=T("The large view","크게 보기 화면"),
                Lines=new[]{T("Double-click a tile (or choose it and press Enter) and it opens big. Photos, videos, GIFs and WebPs all open here.","칸을 두 번 누르면(또는 칸을 고르고 Enter) 크게 열려요. 사진도, 영상도, GIF와 WebP도 여기서 열려요."),
                    T("Point at the playback bar of a video and a picture of that moment appears above it, with the time written on it. Click to jump there.",
                      "영상의 재생 막대에 마우스를 올리면, 그 위치의 장면이 시간과 함께 작은 그림으로 떠요. 누르면 그 위치로 가요."),
                    T("When you change the speed, brightness or volume with keys, a small note shows the new value at the top left.","키로 재생 속도, 밝기, 음량을 바꾸면 화면 왼쪽 위에 바뀐 값이 작게 나와요."),
                    T("The volume is remembered: the next video starts at the volume you left. {{↑}} {{↓}} change it by 5, and {{M}} turns the sound off and on.",
                      "음량은 기억돼요. 다음 영상도 마지막에 둔 음량으로 시작해요. {{↑}} {{↓}}로 5씩 바꾸고, {{M}}으로 소리를 껐다 켜요."),
                    T("If speech is hard to make out, press {{V}} or the Clear voice button. Small voices get louder and words stand out. Music can sound a little different, so press it again to turn it off.",
                      "말소리가 잘 안 들리면 {{V}}나 '목소리 또렷하게' 버튼을 눌러 보세요. 작은 목소리가 커지고 말이 더 잘 들려요. 음악은 느낌이 조금 달라질 수 있으니, 그럴 땐 한 번 더 눌러 끄면 돼요."),
                    T("The small ▾ next to that button picks how strong it is: Gentle, Normal or Strong. Strong lifts small voices the most, but quiet background noise can get louder too.",
                      "버튼 옆 작은 ▾를 누르면 세기를 약하게, 보통, 강하게 중에서 고를 수 있어요. 강하게는 작은 목소리를 가장 많이 키우지만, 조용한 곳의 잡음도 같이 커질 수 있어요.")},
                Keys=new[]{K(T("Previous / next file","이전 파일 / 다음 파일"),"K","L"),K(T("Play or pause","재생 / 일시정지"),"Space"),
                    K(T("Skip back / forward","뒤로 / 앞으로 건너뛰기"),"←","→"),K(T("Skip one second","1초 건너뛰기"),"Shift","←","→"),K(T("One scene back / forward","한 장면 뒤로 / 앞으로"),"D","F"),
                    K(T("Brighter / darker / back to normal","밝게 / 어둡게 / 원래대로"),"E","W","Q"),
                    K(T("0.1× faster / 0.1× slower / normal speed","0.1배 빠르게 / 0.1배 느리게 / 보통 속도"),"C","X","Z"),K(T("Full screen","전체화면"),"F11"),K(T("Close","닫기"),"Esc")}});
            large.Items.Add(new Card {Title=T("Cover or original","표지 또는 원본"),
                Lines=new[]{T("[[View original]] and [[View cover]] switch between the cover and the original.","[[원본 보기]]와 [[표지 보기]]로 표지와 원본을 서로 바꿔 볼 수 있어요."),
                    T("If a cover looks a little blurry because it's small, [[Upscale]] uses AI (a program that has learned what pictures look like) to make it 4 times bigger and sharper. It takes a while and runs in the background; a message at the bottom right says when it's done. The original cover stays, and you can compare the two and pick one.",
                      "표지가 작아서 조금 흐릿해 보이면 [[선명하게 키우기]]를 눌러 보세요. AI(그림을 많이 보고 배운 프로그램)가 표지를 4배로 키우면서 선명하게 다듬어 줘요. 시간이 조금 걸리고 뒤에서 혼자 하니까, 그동안 다른 사진을 봐도 돼요. 다 되면 오른쪽 아래에 알려 드려요. 원래 표지도 남아 있어서 둘을 비교해 보고 고를 수 있어요.")}});
            var views=new[]{new Choice(T("The cover","표지 크게 보기"),T("A tile with a cover opens on the cover. [[View original]] switches to the original.","표지가 있는 칸은 표지가 먼저 크게 열려요. [[원본 보기]]로 바꿔 볼 수 있어요.")),
                new Choice(T("The original","원본 크게 보기"),T("The original opens first. [[View cover]] switches to the cover.","원래 사진이나 영상이 먼저 열려요. [[표지 보기]]로 바꿔 볼 수 있어요."))};
            large.Items.Add(new Setting {Id="openBy",SectionId="large",Title=T("How should a tile open the large view?","칸을 어떻게 누르면 크게 열릴까요?"),
                Lines=new[]{T("This is how clicking a tile in the folder view works. Enter always opens the chosen tile.","폴더 화면에서 칸을 누르는 방법이에요. 고른 칸에서 Enter를 누르면 어느 쪽이든 크게 열려요.")},
                Choices=new[]{new Choice(T("Double-click","두 번 누르기"),T("One click chooses a tile, so you can then Shift-click to choose a range. Two clicks open it, like File Explorer.","한 번 누르면 칸이 골라지고, 이어서 Shift를 누른 채 다른 칸을 누르면 사이가 모두 골라져요. 두 번 누르면 크게 열려요. 파일 탐색기와 같아요.")),
                    new Choice(T("One click","한 번 누르기"),T("One click opens the tile big. To choose tiles, hold Ctrl or Shift while you click.","한 번 누르면 바로 크게 열려요. 칸을 고르려면 Ctrl이나 Shift를 누른 채 눌러요."))},
                Default=0,Columns=2,Get=p=>p.OpenByDoubleClick?0:1,Set=(p,i)=>p.OpenByDoubleClick=i==0});
            large.Items.Add(new Setting {Id="photoView",SectionId="large",Title=T("Clicking a photo tile opens…","사진 칸을 누르면 무엇을 크게 볼까요?"),
                Lines=new[]{T("Tiles without a cover always open the original. Right-click a tile to choose either one just this once.","표지가 없는 칸은 언제나 원본이 열려요. 칸을 오른쪽 클릭하면 이번 한 번만 둘 중 하나를 골라서 열 수도 있어요.")},
                Choices=views,Default=0,Columns=2,Get=p=>p.OpenPhotoOriginal?1:0,Set=(p,i)=>p.OpenPhotoOriginal=i==1});
            large.Items.Add(new Setting {Id="motionView",SectionId="large",Title=T("Clicking a video, GIF or WebP tile opens…","영상·GIF·WebP 칸을 누르면 무엇을 크게 볼까요?"),
                Lines=new[]{T("Choosing the original plays the video straight away.","원본을 고르면 영상이 바로 재생돼요.")},
                Choices=views,Default=0,Columns=2,Get=p=>p.OpenMotionOriginal?1:0,Set=(p,i)=>p.OpenMotionOriginal=i==1});

            // ----- Sharing -----
            var share=add("share",T("Sharing","공유하기"));
            share.Items.Add(new Card {Title=T("Send covers to a chat","표지를 채팅방에 보내요"),
                Steps=new[]{T("Right-click a tile and choose [[Copy]].","칸을 오른쪽 클릭해서 [[복사하기]]를 골라요."),
                    T("Click the message box of a chat app and press {{Ctrl}} + {{V}}. The files go in.","채팅 앱의 글 쓰는 칸을 누르고 {{Ctrl}} + {{V}}를 눌러요. 파일이 들어가요."),
                    T("Or simply drag the tile into the chat window.","또는 칸을 채팅창으로 그냥 끌어다 놓아도 돼요.")},
                Lines=new[]{T("[[Share…]] in the same menu opens the Windows share window, where you pick the app to send with.","같은 메뉴의 [[공유하기…]]를 고르면 Windows 공유 창이 열려요. 거기서 보낼 앱을 고르면 돼요.")},
                Tips=new[]{T("Love My Shelf never uploads anything by itself. Sending is always done by the app you choose.","Love My Shelf가 직접 인터넷에 올리는 일은 없어요. 보내는 건 언제나 내가 고른 앱이 해요.")}});
            share.Items.Add(new Setting {Id="share",SectionId="share",Title=T("What should Copy, Share and dragging send?","복사·공유·끌어다 놓기를 하면 무엇을 보낼까요?"),
                Lines=new[]{T("Right-click a tile to choose differently just this once.","칸을 오른쪽 클릭하면 이번 한 번만 다르게 고를 수도 있어요.")},
                Choices=new[]{new Choice(T("Cover and original","표지와 원본 함께"),T("Sends both the cover and the original photo or video.","표지와 원래 사진(영상)을 둘 다 보내요.")),
                    new Choice(T("Cover only","표지만"),T("Sends only the cover. A GIF cover made from it goes along.","만든 표지만 보내요. 그 표지로 만든 GIF 표지가 있으면 함께 가요.")),
                    new Choice(T("Original only","원본만"),T("Sends only the original photo or video.","원래 사진이나 영상만 보내요."))},
                Default=0,Get=p=>(int)p.Share,Set=(p,i)=>p.Share=(ShareContent)i});
            share.Items.Add(Numbers("shareLimit",T("Ask before sending big videos?","큰 영상은 보내기 전에 물어볼까요?"),new[]{10,20,50,100},20,
                p=>p.ShareLimitMB,(p,v)=>p.ShareLimitMB=v,v=>v+"MB",
                v=>v==10?T("For apps that take only small files","작은 파일만 받는 앱용"):v==20?T("Discord's free limit","디스코드 무료 한도"):v==50?T("For apps that take bigger files","더 큰 파일을 받는 앱용"):T("Almost never asks","거의 묻지 않아요"),4));
            ((Setting)share.Items.Last()).Lines=new[]{T("A chat app may refuse a video that is too big. For example, a free Discord account can upload files up to 20 MB. When an original video is bigger than the size below, Love My Shelf asks whether to [[Send it as it is]] or [[Only about 10 seconds around the cover]].",
                "원본 영상이 너무 크면 채팅 앱에 올라가지 않을 수 있어요. 예를 들어 디스코드는 무료 사용자가 파일 하나에 20MB까지 올릴 수 있어요. 원본 영상이 아래 크기보다 크면 [[그대로 보내기]]와 [[표지 장면 앞뒤로 10초만 보내기]] 중에서 고르라고 물어봐요.")};

            // ----- Tidying up -----
            var tidy=add("tidy",T("Tidying up","정리하기"));
            tidy.Items.Add(new Card {Title=T("Several covers of one original","원본 하나에 표지가 여러 개일 때"),
                Lines=new[]{T("When one photo or video has several covers, each cover shows a small number like 2/5. That means it is the second of five covers. They always stay together. Covers of a video line up by where their scene is in the video, whichever you made first.","사진이나 영상 하나에 표지가 여러 개 있으면 칸 오른쪽 아래에 2/5 같은 번호가 나와요. 표지 5개 중 두 번째라는 뜻이에요. 이 표지들은 항상 붙어 다니고, 영상의 표지는 어느 것을 먼저 만들었든 영상 속 장면 시간 순서로 놓여요."),
                    T("Point at a cover with the mouse, and every cover of the same original gets a colored outline.","표지에 마우스를 올리면 같은 원본의 표지들에 모두 색 테두리가 생겨요."),
                    T("In the large view, the other covers appear small in the empty space on the right when the screen has room. Click one to see it. In full screen they rest when the mouse stops, and come back when it moves.","크게 볼 때 화면 오른쪽에 여백이 있으면 같은 원본의 다른 표지들이 작게 나와요. 누르면 그 표지로 바뀌어요. 전체 화면에서는 마우스가 멈추면 숨고, 움직이면 다시 나와요.")}});
            tidy.Items.Add(new Card {Title=T("Hide or delete a cover","표지 숨기기와 지우기"),
                Lines=new[]{T("Right-click a tile:","칸을 오른쪽 클릭하면 이런 것들이 있어요."),
                    T("[[Hide this cover in Love My Shelf]] keeps the file but stops showing it in Love My Shelf. [[Show this cover again]] brings it back.","[[이 표지 프로그램에서 안 보기]]는 파일은 그대로 두고 Love My Shelf에서만 안 보이게 해요. [[이 표지 다시 보기]]로 되돌릴 수 있어요."),
                    T("[[Move this cover file to the Recycle Bin]] deletes only the cover. The original stays. If the cover also has a GIF cover, this reads [[Move the JPG and GIF cover files to the Recycle Bin]], and [[Move only the GIF cover file to the Recycle Bin]] deletes just the GIF and keeps the JPG cover. Hide and show then read [[Hide the JPG and GIF covers in Love My Shelf]] and [[Show the JPG and GIF covers again]].","[[이 표지 파일 휴지통으로 보내기]]는 표지만 지워요. 원본은 그대로예요. GIF 표지도 있으면 이 이름이 [[JPG·GIF 표지 모두 휴지통으로 보내기]]로 바뀌고, [[GIF 표지만 휴지통으로 보내기]]를 누르면 GIF만 지우고 JPG 표지는 남아요. 안 보기·다시 보기도 이때는 [[JPG·GIF 표지 모두 프로그램에서 안 보기]], [[JPG·GIF 표지 모두 다시 보기]]로 바뀌어요."),
                    T("Deleted a cover by mistake? A notice shows at the bottom right for 3 seconds. Click it and the cover comes back. Pointing at the notice holds the time.","표지를 잘못 지웠다면 오른쪽 아래에 3초 동안 나오는 알림을 누르세요. 표지가 그대로 돌아와요. 알림 위에 마우스를 올리면 시간이 멈춰요."),
                    T("[[Move original to Recycle Bin]] deletes the original photo or video itself. You can bring it back from the Recycle Bin on the desktop.","[[원본 파일 휴지통으로 보내기]]는 원래 사진이나 영상 자체를 지워요. 바탕 화면의 휴지통에서 되살릴 수 있어요.")}});
            tidy.Items.Add(new Card {Title=T("If you moved an original","원본을 다른 곳으로 옮겼다면"),
                Lines=new[]{T("Love My Shelf remembers where each original was. If you moved or renamed it in File Explorer, the tile shows a small saved preview instead.",
                    "Love My Shelf는 원본이 어디에 있었는지 기억해요. 파일 탐색기에서 원본을 옮기거나 이름을 바꾸면, 칸에는 저장해 둔 작은 미리보기가 대신 보여요."),
                    T("Open it big, click [[Locate original]], and choose the file in its new place. Everything works as before.","크게 열어서 [[원본 다시 찾기]]를 누르고, 옮긴 곳의 파일을 골라 주세요. 그러면 전처럼 쓸 수 있어요.")},
                Tips=new[]{T("Renaming inside Love My Shelf ({{F2}}) never loses anything, because Love My Shelf updates its notes at the same time.","Love My Shelf 안에서 {{F2}}로 이름을 바꾸면 Love My Shelf가 기억도 같이 바꿔서 이런 일이 생기지 않아요.")}});

            // ----- Screen, language and updates -----
            var screen=add("screen",T("Screen, language, updates","화면·언어·새 버전"));
            screen.Items.Add(new Setting {Id="theme",SectionId="screen",Title=T("Light or dark screen?","화면을 밝게 할까요, 어둡게 할까요?"),
                Lines=new[]{T("Where photos and videos are shown (the large view and the cover editor), the background stays black in every mode so pictures look right.",
                    "사진과 영상이 놓이는 곳(크게 보기, 표지 만들기)은 사진이 제 색으로 보이도록 어느 모드에서나 바탕이 검은색이에요.")},
                Choices=new[]{new Choice(T("Follow Windows","Windows 설정 따르기"),T("Light when Windows is light, dark when Windows is dark. It changes along with Windows.","Windows가 밝은 모드면 밝게, 어두운 모드면 어둡게 보여요. Windows 설정을 바꾸면 따라 바뀌어요.")),
                    new Choice(T("Light","밝게"),T("Soft light gray with dark letters. Easy to read in the daytime.","부드러운 밝은 회색 바탕에 진한 글씨예요. 낮에 읽기 편해요.")),
                    new Choice(T("Dark","어둡게"),T("Dark background with light letters. Easier on the eyes at night.","어두운 바탕에 밝은 글씨예요. 밤에 눈이 덜 부셔요."))},
                Default=0,Get=p=>(int)p.Theme,Set=(p,i)=>p.Theme=(ThemeMode)i});
            screen.Items.Add(new Setting {Id="language",SectionId="screen",Title=T("Language","어떤 말로 보여 줄까요?"),
                Choices=new[]{new Choice("한국어",""),new Choice("English","")},Columns=2,
                Get=p=>p.Korean?0:1,Set=(p,i)=>p.Korean=i==0});
            screen.Items.Add(new Setting {Id="updates",SectionId="screen",Title=T("Tell me when a new version is out?","새 버전이 나오면 알려 드릴까요?"),
                Lines=new[]{T("When on, Love My Shelf asks GitHub (the website where Love My Shelf is published) once each time it starts whether there's a newer version. It sends no photos or personal information. If there is one, a message appears at the bottom right. Click it and the update downloads and installs by itself; your covers and settings stay.",
                    "켜 두면 Love My Shelf를 켤 때마다 한 번, GitHub(Love My Shelf를 올려 두는 인터넷 사이트)에 새 버전이 있는지 물어봐요. 이때 사진이나 내 정보는 하나도 보내지 않아요. 새 버전이 있으면 오른쪽 아래에 알려 드려요. 그 알림을 누르면 알아서 내려받아 바꿔 주고, 만든 표지와 설정은 그대로 남아요.")},
                Choices=new[]{new Choice(T("Tell me","알려 주기"),""),new Choice(T("Don't tell me","알리지 않기"),T("Love My Shelf never asks the internet.","인터넷에 전혀 묻지 않아요."))},
                Default=0,Columns=2,Get=p=>p.CheckUpdates?0:1,Set=(p,i)=>p.CheckUpdates=i==0});

            // ----- All shortcuts -----
            var keys=add("keys",T("All shortcuts","단축키 모아보기"));
            keys.Items.Add(new Card {Title=T("Anywhere","어디서나"),Keys=new[]{K(T("Open this guide","설명서 겸 옵션 열기"),"F1"),K(T("Full screen","전체화면"),"F11")}});
            keys.Items.Add(new Card {Title=T("Browsing files","파일 둘러보기"),Keys=new[]{
                K(T("Move","이동"),"←","→","↑","↓"),K(T("Open big","크게 보기"),"Enter"),K(T("Rename","이름 바꾸기"),"F2"),K(T("Recycle Bin","휴지통으로 보내기"),"Delete"),
                K(T("Choose all","모두 고르기"),"Ctrl+","A"),K(T("Copy / cut / paste files","파일 복사 / 잘라내기 / 붙여넣기"),"Ctrl+","C","X","V"),K(T("Undo the choosing","고른 것 풀기"),"Esc"),K(T("Choose a folder to add","추가할 폴더 고르기"),"Ctrl+","O"),K(T("Search names","이름 검색"),"Ctrl+","F"),K(T("Back / forward","뒤로 / 앞으로"),"Alt+","←","→"),K(T("Up to the folder above","위 폴더로"),"Alt+","↑"),K(T("Type a path in the address bar","주소창에 경로 입력"),"Ctrl+","L"),K(T("Folder tree","폴더 나무"),"Ctrl+","T"),K(T("Refresh","새로 고침"),"F5")}});
            keys.Items.Add(new Card {Title=T("Large view","크게 보기"),Keys=new[]{
                K(T("Previous / next file","이전 / 다음 파일"),"K","L"),K(T("Play / pause","재생 / 일시정지"),"Space"),K(T("Skip","건너뛰기"),"←","→"),K(T("Skip one second","1초 건너뛰기"),"Shift","←","→"),
                K(T("One scene","한 장면씩"),"D","F"),K(T("Brightness","밝기"),"E","W","Q"),K(T("Speed","재생 속도"),"C","X","Z"),K(T("Volume","음량"),"↑","↓"),K(T("Sound off / on","소리 끄기 / 켜기"),"M"),K(T("Clear voice on / off","목소리 또렷하게 켜기 / 끄기"),"V"),K(T("Close","닫기"),"Esc")}});
            keys.Items.Add(new Card {Title=T("Making a cover","표지 만들기"),Keys=new[]{
                K(T("Bigger / smaller","크게 / 작게"),"+","−"),K(T("Previous / next scene","이전 / 다음 장면"),"D","F"),K(T("Skip","건너뛰기"),"←","→"),K(T("Skip one second","1초 건너뛰기"),"Shift","←","→"),
                K(T("Brightness","밝기"),"E","W","Q"),K(T("Close without saving","저장하지 않고 닫기"),"Esc")}});
            keys.Items.Add(new Card {Title=T("Making a GIF cover","GIF 표지 만들기"),Keys=new[]{
                K(T("Move a handle one scene","손잡이 한 장면씩 옮기기"),"←","→"),K(T("Pause / play the preview","미리보기 멈춤 / 재생"),"Space")}});

            // ----- Help -----
            var help=add("help",T("If something goes wrong","문제가 생겼을 때"));
            help.Items.Add(new Card {Title=T("A blue “Windows protected your PC” window appears","파란색 ‘Windows의 PC 보호’ 창이 떠요"),
                Lines=new[]{T("Windows shows this the first time you run a program it doesn't know yet. Click [[More info]], then [[Run anyway]].",
                    "처음 실행할 때 Windows가 아직 잘 모르는 프로그램이라 조심하라고 띄우는 창이에요. [[추가 정보]]를 누른 다음 [[실행]]을 누르면 돼요.")}});
            help.Items.Add(new Card {Title=T("Video scenes don't show up","영상의 장면이 안 나와요"),
                Lines=new[]{T("The Love My Shelf folder needs its tools folder. Extract the whole downloaded ZIP again so every file stays together.",
                    "Love My Shelf 폴더 안에 tools라는 폴더가 있어야 해요. 내려받은 ZIP 파일을 처음부터 다시 ‘압축 풀기’ 해서, 모든 파일이 한 폴더에 함께 있게 해 주세요.")}});
            help.Items.Add(new Card {Title=T("Videos stutter","영상이 뚝뚝 끊겨요"),
                Lines=new[]{T("For smooth playback, libmpv-2.dll must be in the Love My Shelf folder next to LoveMyShelf.exe. Extracting the whole ZIP puts it there.",
                    "부드럽게 재생하려면 LoveMyShelf.exe 옆에 libmpv-2.dll 파일이 있어야 해요. ZIP 파일 전체를 풀면 함께 들어 있어요.")}});
            help.Items.Add(new Card {Title=T("“Love My Shelf is already running”","‘Love My Shelf가 이미 실행 중이에요’라고 나와요"),
                Lines=new[]{T("Only one Love My Shelf can be open at a time. Click Love My Shelf on the taskbar at the bottom of the screen.",
                    "Love My Shelf는 한 번에 하나만 켤 수 있어요. 화면 맨 아래 작업 표시줄에서 이미 켜져 있는 Love My Shelf를 눌러 보세요.")}});
            help.Items.Add(new Card {Title=T("Text is hard to read","글씨가 잘 안 읽혀요"),
                Lines=new[]{T("Try another screen mode in [[Screen, language, updates]].","[[화면·언어·새 버전]]에서 화면 모드를 바꿔 보세요.")}});
            return list;
        }
    }
}
