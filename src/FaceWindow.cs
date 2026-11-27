using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace LoveMyShelf {
    // Find people by face: reads the covers for faces, shows faces that look alike together, and puts the name
    // you give a group on the originals of those covers as a tag. Nothing is tagged without the person saying so.
    internal sealed class FaceWindow : Window {
        const double DefaultLikeness=0.36;
        const int FaceSize=72,FirstFaces=14,MoreFaces=120,FirstGroups=40;
        static FaceWindow open;
        readonly GalleryStore store;
        readonly StackPanel list=new StackPanel();
        readonly TextBlock status=UI.Text("",13,true),likenessText=UI.Text("",15);
        readonly Slider likeness=new Slider {Minimum=0.20,Maximum=0.70,SmallChange=0.01,LargeChange=0.05,TickFrequency=0.01,IsSnapToTickEnabled=true,MinWidth=160};
        readonly Button start,stop,forget;
        readonly DispatcherTimer regroupSoon=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(350)};
        readonly HashSet<Face> selected=new HashSet<Face>();
        readonly Dictionary<Face,Border> tiles=new Dictionary<Face,Border>();
        readonly SemaphoreSlim pictureGate=new SemaphoreSlim(3),groupGate=new SemaphoreSlim(1);
        List<Photo> covers=new List<Photo>(),unread=new List<Photo>();
        Dictionary<string,List<Face>> byCover=new Dictionary<string,List<Face>>(StringComparer.OrdinalIgnoreCase);
        FaceScan scan;
        int version,readSoFar,foundSoFar;
        bool closed,loading=true,grouping,ownChange;

        internal static void Open(Window owner,GalleryStore store) {
            if(open!=null){if(open.WindowState==WindowState.Minimized)open.WindowState=WindowState.Normal;open.Activate();return;}
            if(!FaceEngine.Available) {
                MessageBox.Show(owner,L.T("The files for finding faces are missing. Check that the LoveMyShelf folder has its faces folder and its upscale folder."),
                    "LoveMyShelf",MessageBoxButton.OK,MessageBoxImage.Information);
                return;
            }
            open=new FaceWindow(store,owner);
            open.Show();
        }
        string LikenessFile {get {return Path.Combine(store.Root,"face-likeness.txt");}}
        internal FaceWindow(GalleryStore store,Window owner=null) {
            this.store=store;
            UI.WindowSetup(this,L.T("Find people by face"));ShowInTaskbar=false;
            WindowStartupLocation=WindowStartupLocation.CenterOwner;
            Owner=owner;
            var area=SystemParameters.WorkArea.Size;
            var source=owner==null?null:PresentationSource.FromVisual(owner);
            if(source!=null && source.CompositionTarget!=null) {
                var work=System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(owner).Handle).WorkingArea;
                var size=source.CompositionTarget.TransformFromDevice.Transform(new Vector(work.Width,work.Height));
                area=new Size(size.X,size.Y);
            }
            Width=Math.Min(900,area.Width);Height=Math.Min(650,area.Height);MinWidth=Math.Min(420,Width);MinHeight=Math.Min(280,Height);
            var root=new DockPanel();var bar=new WindowTitleBar(this);root.Children.Add(bar);Content=root;
            var top=new StackPanel {Margin=new Thickness(18,10,18,8)};DockPanel.SetDock(top,Dock.Top);root.Children.Add(top);
            var about=UI.Text(NoticeLook.KeepWords(L.T("LoveMyShelf looks for faces on your covers and puts faces that look alike together. Give a group a name, and that name becomes a tag on the originals of those covers. The pictures never leave this computer.")),13,true);
            about.TextWrapping=TextWrapping.Wrap;about.Margin=new Thickness(0,0,0,10);
            var actions=new WrapPanel();
            start=UI.Btn(L.T("Find faces"),StartScan,true);start.FontSize=14;start.MinHeight=32;start.Padding=new Thickness(16,0,16,0);start.Margin=new Thickness(0,0,6,4);
            stop=UI.Btn(L.T("Stop"),()=>{if(scan!=null)scan.Stop();});stop.FontSize=14;stop.MinHeight=32;stop.Padding=new Thickness(16,0,16,0);stop.Margin=new Thickness(0,0,10,4);
            stop.Visibility=Visibility.Collapsed;
            status.TextWrapping=TextWrapping.Wrap;status.Margin=new Thickness(0,0,0,4);
            actions.Children.Add(start);actions.Children.Add(stop);actions.Children.Add(status);top.Children.Add(actions);
            var row=new Grid {Margin=new Thickness(0,8,0,0)};
            row.ColumnDefinitions.Add(new ColumnDefinition {MaxWidth=260});
            row.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(2,GridUnitType.Star),MinWidth=likeness.MinWidth});
            row.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
            row.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
            var question=UI.Text(L.T("How alike must two faces be to count as one person?"),13);question.Margin=new Thickness(0,0,10,0);
            question.TextWrapping=TextWrapping.Wrap;
            row.Children.Add(question);
            likeness.VerticalAlignment=VerticalAlignment.Center;
            System.Windows.Automation.AutomationProperties.SetName(likeness,L.T("How alike must two faces be to count as one person?"));
            Grid.SetColumn(likeness,1);row.Children.Add(likeness);
            likenessText.MinWidth=44;likenessText.TextAlignment=TextAlignment.Center;likenessText.Margin=new Thickness(8,0,0,0);
            Grid.SetColumn(likenessText,2);row.Children.Add(likenessText);
            // [Reset] works both ways (ResetToggle): pressed again it brings back the likeness from just before.
            var reset=UI.Btn(L.T("Reset"),()=>{});reset.Margin=new Thickness(8,0,0,0);
            var resetToggle=ResetToggle.Attach(reset,()=>likeness.Value,v=>{likeness.Value=(double)v;},()=>{likeness.Value=DefaultLikeness;});
            likeness.ValueChanged+=delegate {resetToggle.Changed();};
            Grid.SetColumn(reset,3);row.Children.Add(reset);top.Children.Add(row);
            var ends=new DockPanel {Margin=new Thickness(0,2,0,0)};
            var tight=UI.Text(L.T("Strict (splits more) →"),12,true);DockPanel.SetDock(tight,Dock.Right);ends.Children.Add(tight);
            ends.Children.Add(UI.Text(L.T("← Loose (joins more)"),12,true));top.Children.Add(ends);
            var help=UI.Text(NoticeLook.KeepWords(L.T("If one person is split into several groups, move it left. If different people are in one group, move it right. Click faces to choose them, then say who they are or aren't.")),12,true);
            help.TextWrapping=TextWrapping.Wrap;help.Margin=new Thickness(0,0,0,10);
            forget=UI.Btn(L.T("Delete all face information…"),ForgetAll);forget.HorizontalAlignment=HorizontalAlignment.Left;forget.Margin=new Thickness(0,4,0,18);
            // Only the buttons and the slider stay in place; the explanations scroll with the faces, so a short screen keeps its room for them.
            var page=new StackPanel();
            page.Children.Add(about);page.Children.Add(help);page.Children.Add(list);page.Children.Add(forget);
            var scroller=new ScrollViewer {Content=page,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,
                Padding=new Thickness(18,0,12,0)};
            root.Children.Add(scroller);

            double saved;
            try {
                if(File.Exists(LikenessFile) && Double.TryParse(File.ReadAllText(LikenessFile).Trim(),NumberStyles.Float,CultureInfo.InvariantCulture,out saved) && saved>=0.2 && saved<=0.7)
                    likeness.Value=saved;
                else likeness.Value=DefaultLikeness;
            } catch(IOException) {likeness.Value=DefaultLikeness;} catch(UnauthorizedAccessException) {likeness.Value=DefaultLikeness;}
            likenessText.Text=likeness.Value.ToString("0.00",CultureInfo.InvariantCulture);
            likeness.ValueChanged+=delegate {
                likenessText.Text=likeness.Value.ToString("0.00",CultureInfo.InvariantCulture);
                regroupSoon.Stop();regroupSoon.Start(); // grouping waits until the slider rests
            };
            regroupSoon.Tick+=delegate {
                regroupSoon.Stop();
                try {File.WriteAllText(LikenessFile,likeness.Value.ToString("0.00",CultureInfo.InvariantCulture));} catch(IOException) {} catch(UnauthorizedAccessException) {}
                Regroup();
            };
            // A tag renamed or deleted in the main window renames or forgets the person here too.
            Action tagsChanged=()=>{
                if(ownChange)return;
                Dispatcher.BeginInvoke(new Action(()=>{if(!closed && scan==null && !loading)LoadAll();}));
            };
            Tags.Changed+=tagsChanged;
            Closed+=delegate {
                Tags.Changed-=tagsChanged;
                closed=true;open=null;regroupSoon.Stop();
                if(scan!=null)scan.Stop();
            };
            Loaded+=delegate {LoadAll();};
        }
        // ----- Reading what is already known -----
        async void LoadAll() {
            loading=true;ShowStatus();
            Tuple<List<Photo>,Dictionary<string,List<Face>>,List<Photo>> read;
            try {
            read=await Task.Run(()=>{
                var all=FaceFiles.Covers(store);
                var known=new Dictionary<string,List<Face>>(StringComparer.OrdinalIgnoreCase);
                var waiting=new List<Photo>();
                foreach(var cover in all) {
                    var faces=FaceFiles.Read(cover);
                    if(faces==null)waiting.Add(cover);else known[cover.Folder]=faces;
                }
                return Tuple.Create(all,known,waiting);
            });
            } catch(Exception ex) {
                if(closed)return;
                loading=false;ShowStatus();status.Text=ex.Message;return;
            }
            if(closed)return;
            covers=read.Item1;byCover=read.Item2;unread=read.Item3;
            loading=false;
            ShowStatus();Regroup();
        }
        List<Face> AllFaces() {return byCover.Values.SelectMany(f=>f).ToList();}
        void ShowStatus() {
            bool busy=scan!=null;
            start.Visibility=busy?Visibility.Collapsed:Visibility.Visible;
            stop.Visibility=busy?Visibility.Visible:Visibility.Collapsed;
            start.IsEnabled=!loading && unread.Count>0;
            forget.IsEnabled=!busy && !loading && byCover.Count>0;
            int faces=byCover.Values.Sum(f=>f.Count);
            if(loading)status.Text=L.T("Loading…");
            else if(busy)status.Text=L.F("Looking for faces: {0:N0} of {1:N0} covers · {2:N0} faces so far. You can keep using LoveMyShelf meanwhile.",readSoFar,scanTotal,foundSoFar);
            else if(covers.Count==0)status.Text=L.T("There are no covers yet. Make some covers first.");
            else if(unread.Count==0)status.Text=L.F("All {0:N0} covers were read · {1:N0} faces",covers.Count,faces);
            else if(byCover.Count==0)status.Text=L.F("{0:N0} covers haven't been read yet. Click [Find faces] to start.",unread.Count);
            else status.Text=L.F("{0:N0} new covers haven't been read yet · {1:N0} faces from the others",unread.Count,faces);
            start.Content=byCover.Count==0?L.T("Find faces"):L.T("Find faces on new covers");
        }
        int scanTotal;
        // ----- Looking for faces -----
        async void StartScan() {
            if(scan!=null || unread.Count==0)return;
            var job=scan=new FaceScan();
            var todo=unread.ToList();
            scanTotal=todo.Count;readSoFar=0;foundSoFar=0;
            ShowStatus();
            string error=null;
            try {
                error=await job.Run(todo,(done,found)=>{if(!closed && scan==job){readSoFar=done;foundSoFar=found;ShowStatus();}});
            } catch(Exception ex) {error=ex.Message;}
            if(scan==job)scan=null;
            if(closed)return;
            if(error!=null)MessageBox.Show(this,error,"LoveMyShelf",MessageBoxButton.OK,MessageBoxImage.Warning);
            LoadAll();
        }
        void ForgetAll() {
            if(MessageBox.Show(this,L.T("Delete everything LoveMyShelf worked out about faces?\nThe tags you already gave stay. The covers and originals are not touched."),
                "LoveMyShelf",MessageBoxButton.OKCancel,MessageBoxImage.Question)!=MessageBoxResult.OK)return;
            FaceFiles.ClearAll(store);
            LoadAll();
        }
        // ----- Groups -----
        async void Regroup() {
            int mine=++version;
            var faces=AllFaces();
            double threshold=likeness.Value;
            List<Face> singles=null;
            List<FaceGroup> groups=null;
            // One grouping at a time: moving the slider again waits for the one that is running, and only the newest is worked out.
            await groupGate.WaitAsync();
            try {
                if(closed || mine!=version)return;
                // While the faces are being grouped in the background nothing may change them (see Busy).
                grouping=true;
                groups=await Task.Run(()=>{
                    try {return FaceGroups.Build(faces,threshold,out singles);}
                    catch(Exception) {return null;}
                });
            } finally {grouping=false;groupGate.Release();}
            if(closed || mine!=version)return;
            if(groups==null){status.Text=L.T("The faces couldn't be grouped this time. Move the slider a little and it tries again.");return;}
            selected.Clear();tiles.Clear();list.Children.Clear();
            if(faces.Count==0) {
                if(!loading && byCover.Count>0) {
                    var none=UI.Text(L.T("No faces were found on the covers that were read."),14,true);none.Margin=new Thickness(0,10,0,10);list.Children.Add(none);
                }
                return;
            }
            int shown=0,number=0;
            foreach(var group in groups) {
                if(group.Name==null)number++;
                if(shown++>=FirstGroups)continue;
                list.Children.Add(Card(group,number));
            }
            if(groups.Count>FirstGroups) {
                int from=FirstGroups;
                Button more=null;
                more=UI.Btn(L.F("Show {0:N0} more groups",groups.Count-FirstGroups),()=>{
                    int at=list.Children.IndexOf(more),n=groups.Take(from).Count(g=>g.Name==null);
                    list.Children.Remove(more);
                    foreach(var group in groups.Skip(from)) {
                        if(group.Name==null)n++;
                        list.Children.Insert(at++,Card(group,n));
                    }
                });
                more.HorizontalAlignment=HorizontalAlignment.Left;more.Margin=new Thickness(0,0,0,10);
                list.Children.Add(more);
            }
            if(singles.Count>0)list.Children.Add(SinglesCard(singles));
        }
        Border CardFrame(out StackPanel body) {
            body=new StackPanel();
            var card=new Border {CornerRadius=new CornerRadius(9),BorderThickness=new Thickness(1),Padding=new Thickness(12,10,12,10),Margin=new Thickness(0,0,0,10),Child=body};
            card.SetResourceReference(Border.BackgroundProperty,"Panel");card.SetResourceReference(Border.BorderBrushProperty,"Line");
            return card;
        }
        static TextBlock Header(string title,string detail) {
            var text=new TextBlock {FontSize=15,TextTrimming=TextTrimming.CharacterEllipsis,Margin=new Thickness(0,0,0,8)};
            text.SetResourceReference(TextBlock.ForegroundProperty,"Text");
            text.Inlines.Add(new System.Windows.Documents.Run(title) {FontWeight=FontWeights.SemiBold});
            var rest=new System.Windows.Documents.Run("  ·  "+detail);
            rest.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty,"Subtle");
            text.Inlines.Add(rest);
            return text;
        }
        Border Card(FaceGroup group,int number) {
            StackPanel body;
            var card=CardFrame(out body);
            if(group.Name!=null) {
                string name=group.Name;
                body.Children.Add(Header(name,L.F("{0:N0} faces",group.Faces.Count)));
                body.Children.Add(FacePanel(group.Faces,FirstFaces));
                var buttons=new WrapPanel {Margin=new Thickness(0,8,0,0)};
                if(group.New.Count>0) {
                    var label=UI.Text(L.F("{0:N0} faces that look like this person were found",group.New.Count),13,true);label.Margin=new Thickness(0,10,0,6);
                    body.Children.Add(label);
                    body.Children.Add(FacePanel(group.New,MoreFaces));
                    var yes=UI.Btn(L.F("Yes, they are {0}",name),()=>{
                        var chosen=Chosen(group.New);
                        NameFaces(chosen.Count>0?chosen:group.New,name);
                    },true);
                    yes.Margin=new Thickness(0,0,6,4);yes.ToolTip=L.T("Chosen faces only, or all of the new ones when none is chosen.");
                    buttons.Children.Add(yes);
                }
                var no=UI.Btn(L.F("The chosen faces are not {0}",name),()=>{
                    var chosen=Chosen(group.Faces.Concat(group.New));
                    if(chosen.Count==0){Hint(L.T("Click the faces first to choose them."));return;}
                    NotThisPerson(chosen,name);
                });
                no.Margin=new Thickness(0,0,6,4);buttons.Children.Add(no);
                body.Children.Add(buttons);
            } else {
                body.Children.Add(Header(L.F("Person {0}",number),L.F("{0:N0} faces",group.Faces.Count)));
                body.Children.Add(FacePanel(group.Faces,FirstFaces));
                body.Children.Add(NameRow(group.Faces,true));
            }
            return card;
        }
        Border SinglesCard(List<Face> singles) {
            StackPanel body;
            var card=CardFrame(out body);
            var inside=new StackPanel {Visibility=Visibility.Collapsed};
            bool built=false;
            var unfold=UI.Btn(L.F("Faces that appear only once: {0:N0} (click to see)",singles.Count),()=>{
                if(!built) {
                    built=true;
                    inside.Children.Add(FacePanel(singles,MoreFaces));
                    inside.Children.Add(NameRow(singles,false));
                }
                inside.Visibility=inside.Visibility==Visibility.Visible?Visibility.Collapsed:Visibility.Visible;
            });
            unfold.HorizontalAlignment=HorizontalAlignment.Left;
            body.Children.Add(unfold);body.Children.Add(inside);
            return card;
        }
        // A name box with its buttons. whole: with no face chosen the name goes on every face of the group.
        FrameworkElement NameRow(List<Face> faces,bool whole) {
            var row=new WrapPanel {Margin=new Thickness(0,10,0,0)};
            var box=new TextBox {Padding=new Thickness(6,4,6,4),FontSize=14,Width=190,MaxLength=Tags.MaxLength,Margin=new Thickness(0,0,6,4),VerticalContentAlignment=VerticalAlignment.Center};
            string prompt=L.T("Name of this person");
            System.Windows.Automation.AutomationProperties.SetName(box,prompt);box.ToolTip=prompt;
            var hint=UI.Text(prompt,14,true);hint.Margin=new Thickness(9,0,0,4);hint.IsHitTestVisible=false;
            var field=new Grid();field.Children.Add(box);field.Children.Add(hint);
            box.TextChanged+=delegate {hint.Visibility=box.Text.Length==0?Visibility.Visible:Visibility.Collapsed;};
            Action give=()=>{
                string name=Tags.Clean(box.Text);
                if(name==null){box.Focus();return;}
                var chosen=Chosen(faces);
                if(chosen.Count==0 && !whole){Hint(L.T("Click the faces first to choose them."));return;}
                NameFaces(chosen.Count>0?chosen:faces,name);
            };
            box.KeyDown+=(s,e)=>{if(e.Key==Key.Enter){give();e.Handled=true;}};
            var ok=UI.Btn(L.T(whole?"Name them and tag":"Name the chosen faces and tag"),give,true);ok.Margin=new Thickness(0,0,6,4);
            ok.ToolTip=whole?L.T("Chosen faces only, or the whole group when none is chosen."):null;
            row.Children.Add(field);row.Children.Add(ok);
            if(whole) {
                var apart=UI.Btn(L.T("Take the chosen faces out"),()=>{
                    var chosen=Chosen(faces);
                    if(chosen.Count==0){Hint(L.T("Click the faces first to choose them."));return;}
                    if(grouping)return;
                    foreach(var face in chosen)if(!face.Alone)face.Not.Add("*");
                    SaveFaces(chosen);Regroup();
                });
                apart.Margin=new Thickness(0,0,6,4);apart.ToolTip=L.T("A face taken out stays on its own and joins no group.");
                row.Children.Add(apart);
            }
            return row;
        }
        void Hint(string text) {MessageBox.Show(this,text,"LoveMyShelf",MessageBoxButton.OK,MessageBoxImage.Information);}
        List<Face> Chosen(IEnumerable<Face> faces) {return faces.Where(f=>selected.Contains(f)).ToList();}
        // ----- Face pictures -----
        FrameworkElement FacePanel(List<Face> faces,int first) {
            var panel=new WrapPanel();
            int shown=Math.Min(first,faces.Count);
            for(int i=0;i<shown;i++)panel.Children.Add(Tile(faces[i]));
            if(faces.Count>shown) {
                Button more=null;
                more=UI.Btn("+"+(faces.Count-shown).ToString("N0"),()=>{
                    int at=panel.Children.IndexOf(more),until=Math.Min(faces.Count,shown+MoreFaces);
                    for(int i=shown;i<until;i++)panel.Children.Insert(at++,Tile(faces[i]));
                    shown=until;
                    if(shown>=faces.Count)panel.Children.Remove(more);else more.Content="+"+(faces.Count-shown).ToString("N0");
                });
                more.Width=FaceSize;more.Height=FaceSize;more.Margin=new Thickness(0,0,6,6);more.ToolTip=L.T("Show more faces");
                panel.Children.Add(more);
            }
            return panel;
        }
        Border Tile(Face face) {
            var image=new Image {Stretch=Stretch.UniformToFill,Width=FaceSize,Height=FaceSize};
            RenderOptions.SetBitmapScalingMode(image,BitmapScalingMode.HighQuality);
            var tile=new Border {Width=FaceSize+4,Height=FaceSize+4,BorderThickness=new Thickness(2),CornerRadius=new CornerRadius(6),Margin=new Thickness(0,0,4,4),
                Child=image,Cursor=Cursors.Hand,ToolTip=NoticeLook.KeepWords(Path.GetFileName(face.Cover.Name)),Background=Brushes.Black,Focusable=true};
            tile.SetResourceReference(Border.BorderBrushProperty,"Line");
            System.Windows.Automation.AutomationProperties.SetName(tile,Path.GetFileName(face.Cover.Name));
            Action toggle=()=>{
                if(!selected.Remove(face))selected.Add(face);
                tile.SetResourceReference(Border.BorderBrushProperty,selected.Contains(face)?"FocusLine":"Line");
                tile.Opacity=1;
            };
            tile.MouseLeftButtonUp+=delegate {toggle();};
            tile.KeyDown+=(s,e)=>{if(e.Key==Key.Space || e.Key==Key.Enter){toggle();e.Handled=true;}};
            tiles[face]=tile;
            LoadPicture(face,image,version);
            return tile;
        }
        async void LoadPicture(Face face,Image image,int mine) {
            try {
                await pictureGate.WaitAsync();
                BitmapSource picture=null;
                try {
                    if(closed || mine!=version)return;
                    picture=await Task.Run(()=>CutFace(face));
                } finally {pictureGate.Release();}
                if(!closed && mine==version && picture!=null)image.Source=picture;
            } catch(Exception) {}
        }
        // The face with some room around it, cut from the cover at a small size.
        static BitmapSource CutFace(Face face) {
            try {
                string path=face.Cover.GridImage;
                if(!File.Exists(path))return null;
                // Decode the cover only as large as the face needs to fill its tile sharply.
                int height=(int)Math.Max(160,Math.Min(1920,2.2*FaceSize/Math.Max(0.02,1.5*face.H)));
                var bitmap=new BitmapImage();
                using(var file=File.Open(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete)) {
                    bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.CreateOptions=BitmapCreateOptions.IgnoreColorProfile;
                    bitmap.DecodePixelHeight=height;bitmap.StreamSource=file;bitmap.EndInit();
                }
                int w=bitmap.PixelWidth,h=bitmap.PixelHeight;
                double side=Math.Max(face.W*w,face.H*h)*1.5,cx=(face.X+face.W/2)*w,cy=(face.Y+face.H/2)*h;
                side=Math.Min(side,Math.Min(w,h));
                int size=Math.Max(1,(int)Math.Round(side));
                int x=(int)Math.Round(Math.Max(0,Math.Min(w-size,cx-side/2))),y=(int)Math.Round(Math.Max(0,Math.Min(h-size,cy-side/2)));
                size=Math.Min(size,Math.Min(w-x,h-y));
                if(size<1)return null;
                var cut=new CroppedBitmap(bitmap,new Int32Rect(x,y,size,size));
                cut.Freeze();
                return cut;
            } catch(Exception) {return null;}
        }
        // ----- What the person decides -----
        void SaveFaces(IEnumerable<Face> changed) {
            foreach(var cover in changed.Select(f=>f.Cover).Distinct()) {
                List<Face> all;
                if(!byCover.TryGetValue(cover.Folder,out all))continue;
                try {FaceFiles.Write(cover,all);} catch(IOException) {} catch(UnauthorizedAccessException) {}
            }
        }
        // These faces are this person: remembered on each face, and the name becomes a tag on the originals of their covers.
        void NameFaces(List<Face> faces,string name) {
            if(faces.Count==0 || grouping)return;
            try {
                ownChange=true;
                foreach(var face in faces) {
                    face.Name=name;
                    face.Not.RemoveAll(n=>n=="*" || String.Equals(n,name,StringComparison.OrdinalIgnoreCase));
                }
                SaveFaces(faces);
                Tags.Change(faces.Select(f=>f.Cover),new[]{name},null);
            } catch(Exception ex) {UI.Error(this,ex);}
            finally {ownChange=false;}
            Regroup();
        }
        // Hidden covers aren't shown in this window, but a face named on one still says the person is in that original.
        static bool OnHiddenCover(Photo cover,string name) {
            try {
                foreach(var other in GalleryStore.CoversOf(cover)) {
                    if(!other.ExcludedFromCollection)continue;
                    var faces=FaceFiles.Read(other);
                    if(faces!=null && faces.Any(f=>String.Equals(f.Name,name,StringComparison.OrdinalIgnoreCase)))return true;
                }
            } catch(Exception) {}
            return false;
        }
        // These faces are not this person. The tag comes off an original only when none of its covers shows that person any more.
        void NotThisPerson(List<Face> faces,string name) {
            if(grouping)return;
            try {
                ownChange=true;
                var touched=new List<Photo>();
                foreach(var face in faces) {
                    if(String.Equals(face.Name,name,StringComparison.OrdinalIgnoreCase)){face.Name="";touched.Add(face.Cover);}
                    if(!face.Not.Contains(name,StringComparer.OrdinalIgnoreCase))face.Not.Add(name);
                }
                SaveFaces(faces);
                var still=new HashSet<string>(AllFaces().Where(f=>String.Equals(f.Name,name,StringComparison.OrdinalIgnoreCase))
                    .Select(f=>Tags.AlbumOf(f.Cover)+"|"+Tags.NameOf(f.Cover)),StringComparer.OrdinalIgnoreCase);
                var untag=touched.Where(c=>!still.Contains(Tags.AlbumOf(c)+"|"+Tags.NameOf(c)) && !OnHiddenCover(c,name)).ToList();
                if(untag.Count>0)Tags.Change(untag,null,new[]{name});
            } catch(Exception ex) {UI.Error(this,ex);}
            finally {ownChange=false;}
            Regroup();
        }
    }
}
