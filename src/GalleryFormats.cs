using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace LoveMyShelf {
    public partial class GalleryWindow {
        const string AllFormats="All types", PictureFormats="Photos", VideoFormats="Videos";
        readonly HashSet<string> chosenFormats=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly List<HashSet<string>> backFormats=new List<HashSet<string>>(),forwardFormats=new List<HashSet<string>>();
        HashSet<string> historyFormats;
        List<Photo> formatSource=new List<Photo>();
        int formatSourceVersion=-1,formatTotal;
        readonly Dictionary<string,FormatRow> availableFormats=new Dictionary<string,FormatRow>(StringComparer.OrdinalIgnoreCase);
        Popup formatPopup;
        UIElement formatPopupRoot;
        DockPanel formatList;
        ScrollViewer formatScroll;
        readonly List<Button> formatButtons=new List<Button>();
        bool formatDismissedOnButton;
        int formatVisit;
        DateTime lastHiddenFormatNotice;
        readonly HashSet<string> formatArrivals=new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        sealed class FormatRow {
            internal string Name;
            internal string Group;
            internal int Count;
        }

        internal Button FormatButton {get;private set;}
        internal ISet<string> ChosenFormats {get {return new HashSet<string>(chosenFormats,chosenFormats.Comparer);}}
        internal IList<string> FormatChoices {get {return FormatRows().Select(r=>FormatLabel(r.Name)).ToList();}}
        bool FilteringFormats {get {return !ShowingSimilar && chosenFormats.Count>0;}}
        // Only when the type filter alone empties the list: a search or the cover filter that already finds nothing keeps its own note.
        bool FormatEmpty {get {return FilteringFormats && !folderUnavailable && formatTotal>0 && formatSourceVersion==refreshVersion && !photos.Any(p=>p!=null);}}

        static string FormatName(string path) {
            string ext=Path.GetExtension(path);
            if(String.IsNullOrEmpty(ext))return null;
            switch(ext.ToUpperInvariant()) {
                case ".JPG":case ".JPEG":case ".JFIF":return "JPG";
                case ".TIF":case ".TIFF":return "TIF";
                case ".WEBP":return "WebP";
                default:return ext.Substring(1).ToUpperInvariant();
            }
        }
        static string FileFormat(Photo photo) {return FormatName(String.IsNullOrEmpty(photo.Original)?photo.Folder:photo.Original);}
        static string FormatGroup(string path) {
            // SimilarFiles.IsPicture uses the same extensions as GalleryStore's folder loader.
            return SimilarFiles.IsPicture(path)?PictureFormats:VideoFrames.Supports(path)?VideoFormats:null;
        }
        static string FormatLabel(string name) {
            switch(name) {
                case AllFormats:return L.P("All types","모든 형식");
                case PictureFormats:return L.P("Photos","사진");
                case VideoFormats:return L.P("Videos","영상");
                default:return name;
            }
        }

        void BuildFormatButton() {
            FormatButton=UI.Btn("",delegate {if(formatPopup.IsOpen)CloseFormatList();else OpenFormatList();});
            FormatButton.Padding=new Thickness(7,4,7,4);
            FormatButton.VerticalAlignment=VerticalAlignment.Center;
            formatPopup=new Popup {PlacementTarget=FormatButton,Placement=PlacementMode.Bottom,StaysOpen=false,AllowsTransparency=true};
            formatList=new DockPanel {LastChildFill=true};
            KeyboardNavigation.SetTabNavigation(formatList,KeyboardNavigationMode.Cycle);
            KeyboardNavigation.SetDirectionalNavigation(formatList,KeyboardNavigationMode.Cycle);
            var border=new Border {Child=formatList,MinWidth=200,Padding=new Thickness(4),CornerRadius=new CornerRadius(8),BorderThickness=new Thickness(1)};
            UI.Use(border,Border.BackgroundProperty,"Panel");UI.Use(border,Border.BorderBrushProperty,"Line");
            formatPopup.Child=border;
            formatPopup.Opened+=delegate {
                var source=PresentationSource.FromVisual(border);
                formatPopupRoot=source==null?null:source.RootVisual as UIElement;
                if(formatPopupRoot!=null)formatPopupRoot.PreviewKeyDown+=FormatListKeyDown;
                if(FormatButton.IsKeyboardFocused && formatButtons.Count>0)formatButtons[0].Focus();
            };
            formatPopup.Closed+=delegate {
                if(formatPopupRoot!=null){formatPopupRoot.PreviewKeyDown-=FormatListKeyDown;formatPopupRoot=null;}
            };
            var openProperty=DependencyPropertyDescriptor.FromProperty(Popup.IsOpenProperty,typeof(Popup));
            EventHandler trackDismissal=delegate {
                if(formatPopup.IsOpen || Mouse.LeftButton!=MouseButtonState.Pressed || PresentationSource.FromVisual(FormatButton)==null)return;
                var cursor=System.Windows.Forms.Cursor.Position;
                Point at=FormatButton.PointFromScreen(new Point(cursor.X,cursor.Y));
                if(!new Rect(FormatButton.RenderSize).Contains(at))return;
                // An outside down on the anchor can close the popup before WPF dispatches the same click to the button.
                formatDismissedOnButton=true;
                Dispatcher.BeginInvoke(new Action(()=>formatDismissedOnButton=false),DispatcherPriority.Input);
            };
            openProperty.AddValueChanged(formatPopup,trackDismissal);
            // Only the one click that closed the list is swallowed; a quick next click opens it again.
            FormatButton.PreviewMouseLeftButtonDown+=(s,e)=>{if(formatDismissedOnButton){formatDismissedOnButton=false;e.Handled=true;}};
            PreviewKeyDown+=FormatListKeyDown;
            SizeChanged+=delegate {if(formatPopup.IsOpen)SizeFormatList();};
            FormatButton.LayoutUpdated+=delegate {if(formatPopup.IsOpen)PlaceFormatList();};
            Closed+=delegate {CloseFormatList();openProperty.RemoveValueChanged(formatPopup,trackDismissal);};
            UpdateFormatButton(0);
        }

        void FormatListKeyDown(object sender,KeyEventArgs e) {
            if(!formatPopup.IsOpen || e.Key!=Key.Escape)return;
            CloseFormatList();FormatButton.Focus();e.Handled=true;
        }
        void CloseFormatList() {if(formatPopup!=null)formatPopup.IsOpen=false;}
        internal void OpenFormatList() {
            if(ShowingSimilar || FormatButton==null || formatPopup.IsOpen)return;
            formatDismissedOnButton=false;
            DrawFormatList();
            PlaceFormatList();
            formatPopup.IsOpen=true;
        }

        void PlaceFormatList() {
            if(PresentationSource.FromVisual(FormatButton)==null)return;
            var frame=(FrameworkElement)formatPopup.Child;
            // While open, the list's own width is used: measuring again on every layout pass would keep relaying it out.
            double listWidth=frame.ActualWidth;
            if(!(listWidth>0)){frame.Measure(new Size(Double.PositiveInfinity,Double.PositiveInfinity));listWidth=frame.DesiredSize.Width;}
            Point anchor=FormatButton.TranslatePoint(new Point(0,0),this);
            double offset=SystemParameters.MenuDropAlignment?0:Math.Min(0,ActualWidth-anchor.X-listWidth-4);
            if(Math.Abs(formatPopup.HorizontalOffset-offset)>0.5)formatPopup.HorizontalOffset=offset;
        }
        void SizeFormatList() {
            if(formatScroll!=null)formatScroll.MaxHeight=Math.Max(32,ActualHeight*0.7-44);
        }
        IEnumerable<FormatRow> FormatRows() {
            var types=availableFormats.Values.Where(r=>r.Count>0 || chosenFormats.Contains(r.Name)).ToList();
            foreach(string name in chosenFormats) {
                if(!availableFormats.ContainsKey(name))types.Add(new FormatRow {Name=name,Group=FormatGroup("."+name)});
            }
            yield return new FormatRow {Name=AllFormats,Count=formatTotal};
            bool grouped=types.Any(r=>r.Group==PictureFormats) && types.Any(r=>r.Group==VideoFormats);
            foreach(string group in new[]{PictureFormats,VideoFormats,null}) {
                var rows=types.Where(r=>r.Group==group).OrderByDescending(r=>r.Count).ThenBy(r=>r.Name,StringComparer.OrdinalIgnoreCase);
                if(grouped && group!=null)yield return new FormatRow {Name=group,Count=rows.Sum(r=>r.Count)};
                foreach(var row in rows)yield return row;
            }
        }
        void DrawFormatList() {
            string focused=formatButtons.Where(b=>b.IsKeyboardFocused).Select(b=>(string)b.Tag).FirstOrDefault();
            double offset=formatScroll==null?0:formatScroll.VerticalOffset;
            formatButtons.Clear();formatList.Children.Clear();
            var rows=FormatRows().ToList();
            var all=MakeFormatRow(rows[0]);
            DockPanel.SetDock(all,Dock.Top);formatList.Children.Add(all);
            var items=new StackPanel();
            foreach(var row in rows.Skip(1))items.Children.Add(MakeFormatRow(row));
            if(rows.Count==2 && rows[1].Count>0 && rows[1].Count==formatTotal) {
                string name=rows[1].Name;
                var note=UI.Text(String.Format(L.P("Everything here is {0}","이 폴더는 모두 {0}예요"),name),12,true);
                note.Margin=new Thickness(10,6,10,6);items.Children.Add(note);
            }
            formatScroll=new ScrollViewer {Content=items,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
            formatList.Children.Add(formatScroll);SizeFormatList();formatScroll.ScrollToVerticalOffset(offset);
            if(focused!=null) {
                var row=formatButtons.FirstOrDefault(b=>(string)b.Tag==focused)??all;
                row.Focus();
            }
        }
        Button MakeFormatRow(FormatRow row) {
            bool all=row.Name==AllFormats,group=row.Name==PictureFormats || row.Name==VideoFormats;
            string mark="";
            if(all)mark=chosenFormats.Count==0?"✓":"";
            else if(group) {
                var names=GroupFormats(row.Name);
                int picked=names.Count(chosenFormats.Contains);
                mark=picked==0?"":picked==names.Count?"✓":"–";
            } else if(chosenFormats.Contains(row.Name))mark="✓";
            var button=UI.Btn("",delegate {ClickFormat(row.Name);});
            button.Tag=row.Name;button.MinHeight=32;button.Padding=new Thickness(7,3,7,3);
            button.HorizontalContentAlignment=HorizontalAlignment.Stretch;
            button.Background=Brushes.Transparent;button.BorderBrush=Brushes.Transparent;
            var content=new DockPanel {LastChildFill=true};
            var check=UI.Text(mark,13);check.Width=22;
            UI.Use(check,TextBlock.ForegroundProperty,"Accent");DockPanel.SetDock(check,Dock.Left);content.Children.Add(check);
            var number=UI.Text(row.Count.ToString("N0"),12,true);number.Margin=new Thickness(12,0,0,0);
            number.TextAlignment=TextAlignment.Right;UI.Right(content,number);
            var label=UI.Text(FormatLabel(row.Name),13,row.Count==0 && !all);
            if(all || group)label.FontWeight=FontWeights.SemiBold;
            content.Children.Add(label);button.Content=content;
            AutomationProperties.SetName(button,String.Join(" ",new[]{label.Text,number.Text,mark}).Trim());
            formatButtons.Add(button);return button;
        }
        List<string> GroupFormats(string group) {
            return availableFormats.Values.Where(r=>r.Count>0 && r.Group==group).Select(r=>r.Name)
                .Concat(chosenFormats.Where(n=>FormatGroup("."+n)==group)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        internal void ClickFormat(string name) {
            if(ShowingSimilar || active==null)return;
            switch(name) {case "모든 형식":name=AllFormats;break;case "사진":name=PictureFormats;break;case "영상":name=VideoFormats;break;}
            var row=FormatRows().FirstOrDefault(r=>String.Equals(name,r.Name,StringComparison.OrdinalIgnoreCase) ||
                String.Equals(name,FormatLabel(r.Name),StringComparison.OrdinalIgnoreCase));
            if(row==null)return;
            if(row.Name==AllFormats)chosenFormats.Clear();
            else if(row.Name==PictureFormats || row.Name==VideoFormats) {
                var names=GroupFormats(row.Name);
                if(names.All(chosenFormats.Contains))chosenFormats.ExceptWith(names);else chosenFormats.UnionWith(names);
            } else if(!chosenFormats.Remove(row.Name))chosenFormats.Add(row.Name);
            FormatSelectionChanged();
        }

        void UpdateFormatButton(int level) {
            if(FormatButton==null)return;
            FormatButton.Visibility=ShowingSimilar?Visibility.Collapsed:Visibility.Visible;
            string type=L.P("Type","형식");
            var names=FormatRows().Where(r=>chosenFormats.Contains(r.Name)).Select(r=>r.Name).ToList();
            bool filtering=names.Count>0;
            string tooltip=type+" · "+(filtering?String.Join("·",names):FormatLabel(AllFormats));
            if(!filtering && availableFormats.Count>0)tooltip+=" ("+String.Join("·",availableFormats.Keys.OrderBy(n=>n,StringComparer.OrdinalIgnoreCase))+")";
            FormatButton.ToolTip=tooltip;AutomationProperties.SetName(FormatButton,tooltip);
            UI.Use(FormatButton,Control.ForegroundProperty,filtering?"Accent":"Text");
            UI.Use(FormatButton,Control.BorderBrushProperty,filtering?"Accent":"Line");
            if(level==2) {
                var icon=LineIcon("M 2.5,3.5 L 15.5,3.5 L 11,9 L 11,14 L 7,16 L 7,9 Z");
                if(filtering)UI.Use(icon,System.Windows.Shapes.Shape.StrokeProperty,"Accent");
                var compact=new StackPanel {Orientation=Orientation.Horizontal};compact.Children.Add(icon);
                if(filtering) {
                    var countLabel=UI.Text(names.Count.ToString("N0"),13);countLabel.Margin=new Thickness(4,0,0,0);
                    UI.Use(countLabel,TextBlock.ForegroundProperty,"Accent");compact.Children.Add(countLabel);
                }
                FormatButton.Content=compact;
            } else if(!filtering)FormatButton.Content=type+" ▾";
            else if(level==1)FormatButton.Content=type+" "+names.Count+" ▾";
            else FormatButton.Content=(names.Count<=2?String.Join("·",names):String.Format(L.P("{0} +{1}","{0} 외 {1}"),names[0],names.Count-1))+" ▾";
        }

        void ChangeFormatLocation() {
            formatVisit++;
            formatArrivals.Clear();
            CloseFormatList();chosenFormats.Clear();
            if(historyFormats!=null)chosenFormats.UnionWith(historyFormats);
            formatSource.Clear();availableFormats.Clear();formatTotal=0;formatSourceVersion=-1;
        }
        void ReadFormatSource() {
            if(ShowingSimilar)return;
            formatSource=new List<Photo>(photos);formatSourceVersion=refreshVersion;
            UpdateFormatCounts();
            photos=FilterFormatSource();
        }
        void ReorderFormatSource(List<Photo> before,List<Photo> ordered) {
            var family=new HashSet<Photo>(before);
            int next=0;
            for(int i=0;i<formatSource.Count;i++)if(family.Contains(formatSource[i]))formatSource[i]=ordered[next++];
        }
        void RemoveFromFormatSource(Func<Photo,bool> remove,IDictionary<string,Photo> promoted) {
            if(ShowingSimilar)return;
            bool changed=formatSource.RemoveAll(p=>p!=null && remove(p))>0;
            if(promoted!=null)for(int i=0;i<formatSource.Count;i++) {
                var photo=formatSource[i];Photo replacement;
                if(photo!=null && promoted.TryGetValue(photo.Folder,out replacement)){formatSource[i]=replacement;changed=true;}
            }
            if(changed){UpdateFormatCounts();SetCount(skippedCount);}
        }
        void InsertIntoFormatSource(Photo cover,Photo last,Photo next) {
            int index=formatSource.IndexOf(last);
            if(index<0)return;
            // Only use the following tile when it is part of this cover family. Shuffle can place an unrelated tile there.
            int before=next!=null && GalleryStore.CoverFamily(next)==GalleryStore.CoverFamily(cover)?formatSource.IndexOf(next):-1;
            formatSource.Insert(before<0?index+1:before,cover);
            if(!cover.IsExtra)UpdateFormatCounts();
        }
        void ReplaceInFormatSource(Photo old,Photo photo) {
            if(old==null)return;
            int index=formatSource.IndexOf(old);
            if(index<0)return;
            formatSource[index]=photo;
            if(photo==null || old.IsExtra!=photo.IsExtra || FileFormat(old)!=FileFormat(photo))UpdateFormatCounts();
        }
        bool RefreshHiddenCover(Photo photo) {
            if(!FilteringFormats || chosenFormats.Contains(FileFormat(photo)) || !formatSource.Any(p=>p!=null && SameOriginal(photo,p)))return false;
            ReloadKeepingPlace();return true;
        }
        void UpdateFormatCounts() {
            if(ShowingSimilar)return;
            availableFormats.Clear();formatTotal=0;
            foreach(var photo in formatSource) {
                if(photo==null || photo.IsExtra)continue;
                string path=String.IsNullOrEmpty(photo.Original)?photo.Folder:photo.Original;
                string name=FormatName(path);
                if(name==null)continue;
                formatTotal++;
                FormatRow row;
                if(!availableFormats.TryGetValue(name,out row))availableFormats.Add(name,row=new FormatRow {Name=name,Group=FormatGroup(path)});
                row.Count++;
            }
            if(updateTopBar!=null)updateTopBar();
            if(formatPopup.IsOpen)DrawFormatList();
        }
        List<Photo> FilterFormatSource() {
            IEnumerable<Photo> visible=formatSource;
            if(FilteringFormats)visible=visible.Where(p=>p==null || chosenFormats.Contains(FileFormat(p)));
            if(shuffledOrder!=null)visible=visible.OrderBy(p=>{
                int rank;return p!=null && shuffledOrder.TryGetValue(GalleryStore.CoverFamily(p),out rank)?rank:Int32.MaxValue;
            });
            return visible.ToList();
        }
        void FormatSelectionChanged() {
            if(updateTopBar!=null)updateTopBar();
            if(formatPopup.IsOpen)DrawFormatList();
            if(formatSourceVersion!=refreshVersion || searchTimer.IsEnabled)return;
            scrollToTop=true;photos=FilterFormatSource();ClearSelection();ShowPhotos();
        }
        string FormatCountPrefix(int visible) {
            return FilteringFormats?String.Format(L.P("{0:N0} of {1:N0} · ","{1:N0}개 중 {0:N0}개 · "),visible,formatTotal):"";
        }
        void ShowFormatEmpty() {
            emptyTitle.Text=L.P("No files of the selected types","고른 형식의 파일이 없어요");
            emptyHelp.Text=L.P("Choose other types from [Type ▾] or click [Show all types].","[형식 ▾]에서 다른 형식을 고르거나 [모든 형식 보기]를 눌러 주세요.");
            emptyHelp.TextWrapping=TextWrapping.Wrap;emptyHelp.TextAlignment=TextAlignment.Center;
            emptyAction.Content=L.P("Show all types","모든 형식 보기");emptyAction.Visibility=Visibility.Visible;
        }

        bool NoticeHiddenArrivals(TransferJob job) {
            if(!IsVisible || active==null || active.Source==null || !FilteringFormats)return false;
            string folder=active.Source;
            var paths=job.Items.Where(i=>!i.Whole && (i.Outcome==1 || i.Outcome==2 || i.Outcome==6) && !String.IsNullOrEmpty(i.Arrived))
                .Select(i=>i.Arrived).Where(path=>ShellPlaces.Same(Path.GetDirectoryName(path),folder) && FormatGroup(path)!=null &&
                    !chosenFormats.Contains(FormatName(path))).ToList();
            if(paths.Count==0)return false;
            formatArrivals.UnionWith(paths);ShowHiddenFormatNotice();
            return true;
        }
        void ShowHiddenFormatNotice() {
            if(formatArrivals.Count==0 || formatSourceVersion!=refreshVersion || searchTimer.IsEnabled)return;
            // Only count arrivals that would pass the folder's other filters and the current search.
            // Files the list doesn't have yet stay waiting: the refresh that reads them may still be coming.
            var found=formatSource.Where(p=>p!=null && !p.IsExtra && !String.IsNullOrEmpty(p.Original) && formatArrivals.Contains(p.Original)).ToList();
            formatArrivals.ExceptWith(found.Select(p=>p.Original));
            var hidden=found.Select(FileFormat).Where(name=>!chosenFormats.Contains(name)).ToList();
            if(!FilteringFormats || hidden.Count==0 || DateTime.UtcNow-lastHiddenFormatNotice<TimeSpan.FromSeconds(2))return;
            lastHiddenFormatNotice=DateTime.UtcNow;
            var visit=formatVisit;
            Notify(String.Format(L.P("{0:N0} added files are hidden by the type filter","넣은 {0:N0}개가 형식 거르기 때문에 안 보여요"),hidden.Count),
                L.P("Show them too","같이 보기"),delegate {
                    if(formatVisit!=visit || !FilteringFormats)return;
                    chosenFormats.UnionWith(hidden);FormatSelectionChanged();
                },NoticeKind.Info);
        }
    }
}
