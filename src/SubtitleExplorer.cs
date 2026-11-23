using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace LoveMyShelf {
    // The usual way to fix a sync: pause where a line is spoken, click that line, press the sync button.
    // So one click only chooses a line and never moves the video; a double-click goes to it.
    internal sealed class SubtitleExplorer : Window {
        internal sealed class Row : INotifyPropertyChanged {
            internal readonly SubtitleCue Cue;
            bool now;
            public event PropertyChangedEventHandler PropertyChanged;
            internal Row(SubtitleCue cue) {Cue=cue;}
            public string Time {get {return Stamp(Cue.StartMs)+" ~ "+Stamp(Cue.EndMs);}}
            public string Text {get {return Cue.Text.Replace("\n"," / ");}}
            public bool Now {get {return now;} set {if(now==value)return;now=value;Tell("Now");}}
            internal void Refresh() {Tell("Time");Tell("Text");}
            void Tell(string name) {var handler=PropertyChanged;if(handler!=null)handler(this,new PropertyChangedEventArgs(name));}
        }
        static string Stamp(int ms) {
            string sign=ms<0?"-":"";
            var t=TimeSpan.FromMilliseconds(Math.Abs(ms));
            return sign+String.Format(CultureInfo.InvariantCulture,"{0:00}:{1:00}:{2:00}.{3:000}",(int)t.TotalHours,t.Minutes,t.Seconds,t.Milliseconds);
        }

        readonly Viewer viewer;
        readonly ListBox list=new ListBox {SelectionMode=SelectionMode.Single,BorderThickness=new Thickness(0),Background=Brushes.Transparent};
        readonly TextBox search=new TextBox {Padding=new Thickness(8,5,8,5),FontSize=13,VerticalContentAlignment=VerticalAlignment.Center};
        readonly TextBlock searchHint=UI.Text("",13,true);
        readonly TextBlock info=UI.Text("",12,true);
        readonly TextBlock empty=UI.Text("",14,true);
        readonly Button syncButton,syncMenu,undo,save,edit,visible,language;
        readonly List<Row> rows=new List<Row>();
        Row nowRow;
        DateTime followAfter=DateTime.MinValue;
        internal bool Leaving;

        internal SubtitleExplorer(Viewer viewer) {
            this.viewer=viewer;
            UI.WindowSetup(this,L.T("Subtitle explorer"));
            WindowStartupLocation=WindowStartupLocation.Manual;
            ShowInTaskbar=false;ShowActivated=true;Owner=viewer;
            MinWidth=Math.Min(480,SystemParameters.WorkArea.Width);MinHeight=Math.Min(320,SystemParameters.WorkArea.Height);
            Place();

            var root=new DockPanel();Content=root;
            root.SetResourceReference(Panel.BackgroundProperty,"Paper");
            var title=new WindowTitleBar(this);DockPanel.SetDock(title,Dock.Top);root.Children.Add(title);

            var bar=new WrapPanel {Margin=new Thickness(10,8,10,4)};
            DockPanel.SetDock(bar,Dock.Top);root.Children.Add(bar);
            syncButton=UI.Btn(L.T("Sync to the selected line"),()=>Sync(false),true);
            syncButton.ToolTip=L.T("Moves the whole subtitle so the selected line starts at the scene where you paused.");
            bar.Children.Add(syncButton);
            syncMenu=UI.Btn("▾",ShowSyncMenu);
            syncMenu.MinWidth=0;syncMenu.Width=26;syncMenu.Padding=new Thickness(0);syncMenu.Margin=new Thickness(2,0,6,0);
            syncMenu.ToolTip=L.T("More ways to sync");
            System.Windows.Automation.AutomationProperties.SetName(syncMenu,L.T("More ways to sync"));
            bar.Children.Add(syncMenu);
            undo=Tool(bar,L.T("Undo"),()=>{viewer.Subtitle.Undo();viewer.RefreshSubtitle();Redraw();});
            save=Tool(bar,L.T("Save"),()=>{if(viewer.SaveSubtitle())Fill(true);});
            save.ToolTip=L.T("Writes your changes over the subtitle file.");
            edit=Tool(bar,L.T("Edit line"),EditLine);
            visible=Tool(bar,"",()=>{viewer.SubtitlesVisible=!viewer.SubtitlesVisible;Redraw();});
            Tool(bar,L.T("Open subtitle file…"),()=>{if(viewer.ChooseSubtitleFile(this))Fill(false);});
            language=Tool(bar,"",ShowLanguageMenu);language.Visibility=Visibility.Collapsed;

            var searchBox=new Grid {Margin=new Thickness(10,4,10,6)};
            DockPanel.SetDock(searchBox,Dock.Top);root.Children.Add(searchBox);
            System.Windows.Automation.AutomationProperties.SetName(search,L.T("Search subtitles"));
            searchBox.Children.Add(search);
            searchHint.Text=L.T("Search subtitles");searchHint.IsHitTestVisible=false;searchHint.Margin=new Thickness(11,0,0,0);searchHint.VerticalAlignment=VerticalAlignment.Center;
            searchBox.Children.Add(searchHint);
            search.TextChanged+=delegate {searchHint.Visibility=search.Text.Length==0?Visibility.Visible:Visibility.Collapsed;ShowRows();};

            info.Margin=new Thickness(12,4,12,7);info.TextTrimming=TextTrimming.CharacterEllipsis;
            DockPanel.SetDock(info,Dock.Bottom);root.Children.Add(info);

            var body=new Grid();root.Children.Add(body);
            list.ItemContainerStyle=ItemStyle();list.ItemTemplate=ItemTemplate();
            ScrollViewer.SetHorizontalScrollBarVisibility(list,ScrollBarVisibility.Disabled);
            VirtualizingPanel.SetIsVirtualizing(list,true);VirtualizingPanel.SetVirtualizationMode(list,VirtualizationMode.Recycling);
            body.Children.Add(list);
            empty.TextWrapping=TextWrapping.Wrap;empty.TextAlignment=TextAlignment.Center;empty.Margin=new Thickness(28);
            empty.HorizontalAlignment=HorizontalAlignment.Center;empty.VerticalAlignment=VerticalAlignment.Center;
            body.Children.Add(empty);

            list.SelectionChanged+=delegate {Buttons();followAfter=DateTime.UtcNow.AddSeconds(3);};
            list.PreviewMouseWheel+=delegate {followAfter=DateTime.UtcNow.AddSeconds(4);};
            list.AddHandler(Thumb.DragStartedEvent,new DragStartedEventHandler((s,e)=>followAfter=DateTime.UtcNow.AddSeconds(4)));
            list.MouseDoubleClick+=(s,e)=>{
                var item=ItemsControl.ContainerFromElement(list,e.OriginalSource as DependencyObject) as ListBoxItem;
                var row=item==null?null:item.DataContext as Row;
                if(row!=null){viewer.SeekSubtitle(row.Cue.StartMs);e.Handled=true;}
            };
            PreviewKeyDown+=ForwardKeys;
            Activated+=delegate {Float();};Deactivated+=delegate {FloatSoon();};
            viewer.Activated+=ViewerFocus;viewer.Deactivated+=ViewerFocus;
            Closing+=(s,e)=>{if(!Leaving && !viewer.ConfirmSubtitles(true))e.Cancel=true;};
            Closed+=delegate {viewer.Activated-=ViewerFocus;viewer.Deactivated-=ViewerFocus;viewer.ExplorerClosed(this);};
            Loaded+=delegate {Float();};
            Fill(false);
        }
        static Button Tool(Panel bar,string label,Action click) {
            var button=UI.Btn(label,click);button.Margin=new Thickness(0,0,4,0);bar.Children.Add(button);return button;
        }
        void Place() {
            double width=620,height=640;
            var saved=viewer.Home==null?null:viewer.Home.Settings.SubtitleBounds;
            var parts=(saved??"").Split(',');
            double l,t,w,h;
            if(parts.Length==4 && Number(parts[0],out l) && Number(parts[1],out t) && Number(parts[2],out w) && Number(parts[3],out h) && OnScreen(l,t,w,h)) {
                Left=l;Top=t;Width=Math.Max(MinWidth,w);Height=Math.Max(MinHeight,h);return;
            }
            var area=SystemParameters.WorkArea;
            Width=Math.Min(width,area.Width);Height=Math.Min(height,area.Height);
            bool hasRoom=viewer.WindowState==WindowState.Normal;
            Left=hasRoom?Math.Max(area.Left,Math.Min(viewer.Left+viewer.ActualWidth-Width-16,area.Right-Width)):area.Right-Width-16;
            Top=hasRoom?Math.Max(area.Top,viewer.Top+48):area.Top+48;
        }
        static bool Number(string text,out double value) {return Double.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out value);}
        // A monitor that was unplugged must not leave the window where nobody can reach it.
        static bool OnScreen(double l,double t,double w,double h) {
            if(w<100 || h<100)return false;
            var box=new System.Drawing.Rectangle((int)l,(int)t,(int)w,40);
            return System.Windows.Forms.Screen.AllScreens.Any(s=>s.WorkingArea.IntersectsWith(box));
        }
        internal string Bounds {get {
            var box=WindowState==WindowState.Normal?new Rect(Left,Top,ActualWidth,ActualHeight):RestoreBounds;
            return String.Join(",",new[]{box.Left,box.Top,box.Width,box.Height}.Select(v=>Math.Round(v).ToString(CultureInfo.InvariantCulture)));
        }}

        // Stays above the video while the video or this window is in use, but not above other programs.
        void ViewerFocus(object sender,EventArgs e) {FloatSoon();}
        void FloatSoon() {Dispatcher.BeginInvoke(new Action(Float),DispatcherPriority.Background);}
        void Float() {
            if(!IsLoaded)return;
            bool ours=IsActive || viewer.IsActive || Application.Current.Windows.OfType<VideoOverlay>().Any(w=>w.IsActive && w.Owner==viewer);
            if(Topmost!=ours)Topmost=ours;
        }
        // The list has focus most of the time, and these keys still belong to the video.
        void ForwardKeys(object sender,KeyEventArgs e) {
            if(Keyboard.FocusedElement is TextBox)return;
            var key=e.Key==Key.ImeProcessed?e.ImeProcessedKey:e.Key;
            if(key!=Key.Space && key!=Key.D && key!=Key.F && key!=Key.Left && key!=Key.Right)return;
            var source=PresentationSource.FromVisual(viewer);
            if(source==null)return;
            viewer.RaiseEvent(new KeyEventArgs(e.KeyboardDevice,source,e.Timestamp,key) {RoutedEvent=Keyboard.PreviewKeyDownEvent});
            e.Handled=true;
        }

        // ----- The list -----
        internal void Fill(bool keepChoice) {
            int chosen=keepChoice?list.SelectedIndex:-1;
            double offset=0;
            var scroller=Scroller(list);
            if(keepChoice && scroller!=null)offset=scroller.VerticalOffset;
            rows.Clear();nowRow=null;
            var file=viewer.Subtitle;
            if(file!=null)rows.AddRange(file.Cues.Select(c=>new Row(c)));
            if(file!=null && file.Groups.Count>1) {
                language.Content=LanguageName(file,file.ActiveGroup)+"  ▾";
                language.Visibility=Visibility.Visible;
            } else language.Visibility=Visibility.Collapsed;
            ShowRows();
            if(chosen>=0 && chosen<list.Items.Count)list.SelectedIndex=chosen;
            if(keepChoice && scroller!=null) {
                var target=Scroller(list);
                if(target!=null){list.UpdateLayout();target.ScrollToVerticalOffset(offset);}
            }
            Redraw();
        }
        static string LanguageName(SubtitleFile file,string group) {
            string name;
            if(!file.GroupNames.TryGetValue(group,out name))name=group;
            string both=(name+" "+group).ToUpperInvariant();
            if(both.Contains("KOREAN") || both.Contains("KRCC") || both.Contains("KOCC"))return L.T("Korean");
            if(both.Contains("ENGLISH") || both.Contains("ENCC") || both.Contains("EGCC"))return L.T("English");
            return name.Length==0?L.T("Subtitles"):name;
        }
        void ShowLanguageMenu() {
            var file=viewer.Subtitle;
            if(file==null)return;
            var menu=new ContextMenu {PlacementTarget=language,Placement=PlacementMode.Bottom};
            foreach(string group in file.Groups) {
                string choice=group;
                var item=new MenuItem {Header=LanguageName(file,group),IsChecked=group==file.ActiveGroup};
                item.Click+=delegate {
                    if(viewer.Subtitle!=file || file.ActiveGroup==choice)return;
                    file.ActiveGroup=choice;viewer.RefreshSubtitle();Fill(false);
                };
                menu.Items.Add(item);
            }
            menu.IsOpen=true;
        }
        void ShowRows() {
            string word=search.Text.Trim();
            list.ItemsSource=word.Length==0?rows:rows.Where(r=>r.Cue.Text.IndexOf(word,StringComparison.CurrentCultureIgnoreCase)>=0).ToList();
            bool none=list.Items.Count==0;
            empty.Visibility=none?Visibility.Visible:Visibility.Collapsed;
            empty.Text=viewer.Subtitle==null?(viewer.HasOwnSubtitle?L.T("This video has subtitles inside it. They show as usual during playback, but only subtitle files can be listed and synced here."):
                L.T("There is no subtitle file with the same name as this video. Choose one with [Open subtitle file] above.")):L.T("No subtitle line contains what you searched for.");
            Buttons();
        }
        void Redraw() {
            foreach(var row in rows)row.Refresh();
            Buttons();
        }
        void Buttons() {
            var file=viewer.Subtitle;
            bool chosen=list.SelectedItem!=null,has=file!=null;
            syncButton.IsEnabled=syncMenu.IsEnabled=has && chosen && viewer.CanSyncSubtitle;
            edit.IsEnabled=has && chosen;
            undo.IsEnabled=has && file.CanUndo;
            save.IsEnabled=has && file.Dirty;
            visible.Content=L.T(viewer.SubtitlesVisible?"Hide subtitles":"Show subtitles");
            visible.IsEnabled=viewer.CanSyncSubtitle;
            if(!has){info.Text="";Title=L.T("Subtitle explorer");return;}
            string name=System.IO.Path.GetFileName(file.Path);
            string moved="";
            var first=file.Cues.FirstOrDefault();
            if(first!=null && first.Start.Ms!=first.Start.Original) {
                double seconds=(first.Start.Ms-first.Start.Original)/1000.0;
                moved=" · "+L.F("Moved {0:+0.000;-0.000} s",seconds);
            }
            info.Text=name+" · "+L.F("{0:N0} lines",rows.Count)+moved+(file.Dirty?" · "+L.T("Not saved yet"):"");
            info.ToolTip=file.Path;
            Title=(file.Dirty?"• ":"")+L.T("Subtitle explorer");
        }
        // Called by the video as it plays. While paused the list stays still, so the line you're about to click doesn't run away.
        internal void ShowTime(double ms,bool playing) {
            Row current=null;
            int low=0,high=rows.Count-1;
            while(low<=high) {
                int middle=(low+high)/2;
                if(rows[middle].Cue.StartMs<=ms)low=middle+1;else high=middle-1;
            }
            for(int i=high;i>=0 && i>=high-3;i--)if(rows[i].Cue.StartMs<=ms && rows[i].Cue.EndMs>ms){current=rows[i];break;}
            if(current!=nowRow) {
                if(nowRow!=null)nowRow.Now=false;
                nowRow=current;
                if(nowRow!=null)nowRow.Now=true;
            }
            if(playing && current!=null && DateTime.UtcNow>=followAfter && search.Text.Length==0)list.ScrollIntoView(current);
        }
        internal void PlaybackChanged() {Buttons();}

        void Sync(bool onlyFromHere) {
            var row=list.SelectedItem as Row;
            if(row==null || viewer.Subtitle==null || !viewer.CanSyncSubtitle)return;
            if(!viewer.Subtitle.SyncTo(row.Cue,viewer.SubtitleClock,onlyFromHere))return;
            viewer.RefreshSubtitle();
            Redraw();
        }
        void ShowSyncMenu() {
            var menu=new ContextMenu {PlacementTarget=syncMenu,Placement=PlacementMode.Bottom};
            var all=new MenuItem {Header=L.T("Move the whole subtitle")};all.Click+=delegate {Sync(false);};menu.Items.Add(all);
            var after=new MenuItem {Header=L.T("Move only this line and after")};after.Click+=delegate {Sync(true);};menu.Items.Add(after);
            menu.IsOpen=true;
        }
        void EditLine() {
            var row=list.SelectedItem as Row;
            if(row==null || viewer.Subtitle==null)return;
            var dialog=new SubtitleLineWindow(this,row.Cue.Text);
            if(dialog.ShowDialog()!=true)return;
            viewer.Subtitle.SetText(row.Cue,dialog.Text);
            viewer.RefreshSubtitle();
            Redraw();
        }
        static ScrollViewer Scroller(DependencyObject parent) {
            for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++) {
                var child=VisualTreeHelper.GetChild(parent,i);
                var found=child as ScrollViewer??Scroller(child);
                if(found!=null)return found;
            }
            return null;
        }
        static Style ItemStyle() {
            var style=new Style(typeof(ListBoxItem));
            style.Setters.Add(new Setter(Control.ForegroundProperty,new DynamicResourceExtension("Subtle")));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty,HorizontalAlignment.Stretch));
            style.Setters.Add(new Setter(Control.FocusVisualStyleProperty,null));
            var box=new FrameworkElementFactory(typeof(Border),"Box");
            box.SetValue(Border.PaddingProperty,new Thickness(10,6,10,6));
            box.SetValue(Border.BorderThicknessProperty,new Thickness(2,0,0,0));
            box.SetValue(Border.BorderBrushProperty,Brushes.Transparent);
            box.SetValue(Border.BackgroundProperty,Brushes.Transparent);
            box.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
            var template=new ControlTemplate(typeof(ListBoxItem)) {VisualTree=box};
            var hover=new Trigger {Property=UIElement.IsMouseOverProperty,Value=true};
            hover.Setters.Add(new Setter(Border.BackgroundProperty,new DynamicResourceExtension("Panel"),"Box"));
            template.Triggers.Add(hover);
            // The line you chose: a filled row with a blue edge. The line playing now: blue, bold text.
            var picked=new Trigger {Property=ListBoxItem.IsSelectedProperty,Value=true};
            picked.Setters.Add(new Setter(Border.BackgroundProperty,new DynamicResourceExtension("Raised"),"Box"));
            picked.Setters.Add(new Setter(Border.BorderBrushProperty,new DynamicResourceExtension("Accent"),"Box"));
            picked.Setters.Add(new Setter(Control.ForegroundProperty,new DynamicResourceExtension("Text")));
            template.Triggers.Add(picked);
            style.Setters.Add(new Setter(Control.TemplateProperty,template));
            var playing=new DataTrigger {Binding=new Binding("Now"),Value=true};
            playing.Setters.Add(new Setter(Control.ForegroundProperty,new DynamicResourceExtension("Accent")));
            playing.Setters.Add(new Setter(Control.FontWeightProperty,FontWeights.SemiBold));
            style.Triggers.Add(playing);
            return style;
        }
        static DataTemplate ItemTemplate() {
            var grid=new FrameworkElementFactory(typeof(Grid));
            var timeColumn=new FrameworkElementFactory(typeof(ColumnDefinition));timeColumn.SetValue(ColumnDefinition.WidthProperty,new GridLength(196));
            var textColumn=new FrameworkElementFactory(typeof(ColumnDefinition));textColumn.SetValue(ColumnDefinition.WidthProperty,new GridLength(1,GridUnitType.Star));
            grid.AppendChild(timeColumn);grid.AppendChild(textColumn);
            var time=new FrameworkElementFactory(typeof(TextBlock));
            time.SetBinding(TextBlock.TextProperty,new Binding("Time"));
            time.SetValue(TextBlock.FontSizeProperty,12.5);
            time.SetValue(System.Windows.Documents.Typography.NumeralAlignmentProperty,FontNumeralAlignment.Tabular);
            time.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);
            grid.AppendChild(time);
            var text=new FrameworkElementFactory(typeof(TextBlock));
            text.SetBinding(TextBlock.TextProperty,new Binding("Text"));
            text.SetBinding(FrameworkElement.ToolTipProperty,new Binding("Text"));
            text.SetValue(Grid.ColumnProperty,1);
            text.SetValue(TextBlock.FontSizeProperty,14.0);
            text.SetValue(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis);
            text.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);
            grid.AppendChild(text);
            return new DataTemplate {VisualTree=grid};
        }
    }

    // A small window to fix the words of one line.
    internal sealed class SubtitleLineWindow : Window {
        readonly TextBox box;
        internal string Text {get {return box.Text;}}
        internal SubtitleLineWindow(Window owner,string text) {
            UI.WindowSetup(this,L.T("Edit line"));Owner=owner;ShowInTaskbar=false;
            WindowStartupLocation=WindowStartupLocation.CenterOwner;ResizeMode=ResizeMode.NoResize;
            Width=Math.Min(520,SystemParameters.WorkArea.Width);SizeToContent=SizeToContent.Height;
            var root=new DockPanel();root.Children.Add(new WindowTitleBar(this));
            var page=new StackPanel {Margin=new Thickness(20,12,20,18)};root.Children.Add(page);Content=root;
            box=new TextBox {Text=text.Replace("\n",Environment.NewLine),AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MinHeight=84,Padding=new Thickness(8),FontSize=14,
                VerticalScrollBarVisibility=ScrollBarVisibility.Auto,MaxHeight=240};
            System.Windows.Automation.AutomationProperties.SetName(box,L.T("Edit line"));
            page.Children.Add(box);
            var actions=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,12,0,0)};
            var cancel=UI.Btn(L.T("Cancel"),()=>DialogResult=false);cancel.IsCancel=true;
            var ok=UI.Btn(L.T("Apply"),()=>DialogResult=true,true);ok.Margin=new Thickness(6,0,0,0);
            actions.Children.Add(cancel);actions.Children.Add(ok);page.Children.Add(actions);
            Loaded+=delegate {box.Focus();box.SelectAll();};
        }
    }
}
