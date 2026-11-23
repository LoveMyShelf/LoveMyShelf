using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace LoveMyShelf {
    public sealed class FramePicker : Window {
        sealed class Request {
            // Indices: missing tiles on screen. Later: missing scenes around the screen, read once those are done.
            public int[] Indices,Later;
            public int Side,Paused,Version;
        }
        // A scene picture read while the window is open, at the tile width it was read for.
        sealed class Picture {public BitmapSource Image;public int Side;public long Bytes;}
        static double preferredWidth=260;
        readonly string path;
        readonly bool snapEdges;
        readonly int wheelZoomPercent;
        readonly int previewSide;
        readonly Photo photo;
        int previous;
        readonly double? anchorTime;
        readonly bool useNearby;
        FrameTimes nearby;
        readonly CancellationTokenSource lifetime=new CancellationTokenSource();
        readonly Canvas canvas=new Canvas();
        readonly ScrollViewer scroll;
        readonly TextBlock count=UI.Text(L.T("Preparing the scene list…"),11,true);
        readonly Stopwatch indexClock=new Stopwatch();
        readonly DispatcherTimer indexTimer=new DispatcherTimer {Interval=TimeSpan.FromSeconds(1)};
        readonly Dictionary<int,Button> cells=new Dictionary<int,Button>();
        // Every scene picture read so far (scene index -> picture), so tiles that scroll back show at once.
        Dictionary<int,Picture> pictures=new Dictionary<int,Picture>();
        long pictureBytes;
        const long PictureLimit=200L*1024*1024;
        // Changes whenever the scene list is replaced; pictures read for an older list are dropped.
        int rangeVersion;
        readonly AutoResetEvent work=new AutoResetEvent(false);
        volatile Request requested;
        volatile bool closed;
        Task decoding;
        // Reading more scenes when the grid is scrolled to its top or bottom: -1 earlier, +1 later, 0 nothing asked.
        volatile int extendWanted;
        volatile bool atVideoStart,atVideoEnd;
        bool wideReady;
        // The grid never holds more than this much of the video; past it, the far end is dropped.
        const double MaxSpanMs=120000;
        bool pendingLayout,fitNeighbors=true;
        int total,columns,leadingSlots,first=-1,end=-1;
        double? replacementOffset;
        double cellWidth,rowHeight;
        // The list's height at the last layout, to tell where a scene was on screen before the window changed size.
        double shownHeight;
        internal CropWindow SavedCrop { get; private set; }
        internal bool OpenFullList { get; private set; }
        internal Func<CropWindow,Task> CommitCover;
        // Runs after a cover is saved, while the editor is still open (opens the new cover's large view on top).
        internal Func<Task> AfterCommit;
        internal Func<CropWindow,MotionRequest,Task> CommitMotion;
        // Seconds the arrow keys skip in the cover editor (the large view's setting).
        internal int SkipSeconds=5;
        internal int BrightnessStep=3;
        // The playback brightness (-100..100) the video was watched with: the scenes show with it and a cover starts from it.
        int brightness;
        readonly TextBlock brightnessNote=UI.Text("",11,true);
        internal int Brightness {
            get {return brightness;}
            set {
                brightness=Math.Max(-100,Math.Min(100,value));
                brightnessNote.Text=brightness==0?"":L.F("Brightness {0:+0;-0}",brightness);
                brightnessNote.Visibility=brightness==0?Visibility.Collapsed:Visibility.Visible;
                foreach(var tile in cells.Values)ApplyBrightness(tile);
            }
        }

        public FramePicker(Window owner,string path,Photo photo,int previous,double? anchorTime,bool snapEdges=true,int wheelZoomPercent=7,
            bool useNearby=false,bool? startFull=null,int previewSide=2200) {
            this.path=path;this.photo=photo;
            this.snapEdges=snapEdges;this.wheelZoomPercent=wheelZoomPercent;
            this.previewSide=previewSide;
            this.previous=previous;this.anchorTime=anchorTime;
            this.useNearby=useNearby && anchorTime.HasValue && VideoFrames.Supports(path);
            UI.WindowSetup(this,L.T("Choose a scene")); Owner=owner;ShowInTaskbar=false;
            WindowStartupLocation=WindowStartupLocation.CenterOwner;
            Width=Math.Min(1000,SystemParameters.WorkArea.Width);Height=Math.Min(760,SystemParameters.WorkArea.Height);
            MinWidth=360;MinHeight=300;
            var root=new DockPanel();Content=root;
            root.Children.Add(FullScreen.TitleBar(this,startFull??FullScreen.IsFull(owner)));
            var head=UI.Bar(new Thickness(6,2,6,2));
            if(this.useNearby)UI.Right(head,UI.Btn(L.T("All scenes"),()=>{OpenFullList=true;DialogResult=false;}));
            var name=UI.Text(Path.GetFileName(path),12);name.TextTrimming=TextTrimming.CharacterEllipsis;head.Children.Add(name);
            DockPanel.SetDock(head,Dock.Top);root.Children.Add(head);
            var bar=UI.Bar(new Thickness(6,2,6,2));
            UI.Right(bar,UI.Btn(L.T("Cancel"),()=>DialogResult=false));
            brightnessNote.Margin=new Thickness(0,0,10,0);brightnessNote.VerticalAlignment=VerticalAlignment.Center;
            brightnessNote.Visibility=Visibility.Collapsed;UI.Right(bar,brightnessNote);
            var label=UI.Text(L.T("Scene size"),12);DockPanel.SetDock(label,Dock.Left);bar.Children.Add(label);
            var size=new DragSlider { Minimum=100,Maximum=580,TickFrequency=20,IsSnapToTickEnabled=true,
                Value=preferredWidth,Width=110,Margin=new Thickness(5,0,10,0),VerticalAlignment=VerticalAlignment.Center };
            AutomationProperties.SetName(size,L.T("Scene size"));
            size.ValueChanged+=delegate {preferredWidth=size.Value;fitNeighbors=false;ScheduleLayout();};
            DockPanel.SetDock(size,Dock.Left);bar.Children.Add(size);count.TextTrimming=TextTrimming.CharacterEllipsis;bar.Children.Add(count);
            DockPanel.SetDock(bar,Dock.Bottom);root.Children.Add(bar);
            scroll=new ScrollViewer { Content=canvas,VerticalScrollBarVisibility=ScrollBarVisibility.Visible,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled };
            scroll.ScrollChanged+=delegate { ScheduleLayout(); };scroll.SizeChanged+=delegate { ScheduleLayout(); };
            root.Children.Add(scroll);
            indexTimer.Tick+=delegate {UpdateIndexStatus();};
            Loaded+=delegate {
                if(decoding!=null)return;
                indexClock.Start();
                indexTimer.Start();
                decoding=Task.Run((Action)Decode);
            };
            Closed+=async delegate {
                closed=true;indexTimer.Stop();lifetime.Cancel();work.Set();
                if(decoding!=null)await decoding;
                work.Dispose();lifetime.Dispose();
            };
            PreviewKeyDown+=(s,e)=>{if(e.Key==Key.Escape){DialogResult=false;e.Handled=true;}};
        }
        void UpdateIndexStatus() {
            int seconds=(int)indexClock.Elapsed.TotalSeconds;
            bool decodingFrames;
            int scanned;
            if(useNearby)
                count.Text=L.F("Loading the scenes around the paused moment… ({0}s)",seconds);
            else if(VideoFrames.TryIndexProgress(path,out decodingFrames,out scanned))
                count.Text=L.F(decodingFrames?"Preparing the scenes to show in the list… {0:N0} ({1}s)":"Reading scene times… {0:N0} ({1}s)",scanned,seconds);
            else
                count.Text=L.F("Preparing the scene list… ({0}s)",seconds);
        }
        void ScheduleLayout() {
            if(pendingLayout || closed)return;
            pendingLayout=true;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(()=>{pendingLayout=false;if(!closed)LayoutCells();}));
        }
        void LayoutCells() {
            if(total==0 || scroll.ViewportWidth<1 || scroll.ViewportHeight<1)return;
            int cols=Math.Max(1,(int)(scroll.ViewportWidth/preferredWidth));
            double w=scroll.ViewportWidth/cols,h=Math.Round(w*0.75)+20,offset=replacementOffset??scroll.VerticalOffset;
            int pad=leadingSlots%cols;
            int before=(Math.Max(0,previous-1)+pad)/cols,after=(Math.Min(total-1,Math.Max(0,previous+1))+pad)/cols;
            if(fitNeighbors && previous>=0)h=Math.Min(h,Math.Floor(scroll.ViewportHeight/(after-before+1)));
            // The paused scene left the range (it was widened far away): keep the rows as they are.
            else if(fitNeighbors && rowHeight>0 && cols==columns && Math.Abs(w-cellWidth)<0.5)h=rowHeight;
            h=Math.Max(36,h);
            bool resized=cols!=columns || Math.Abs(w-cellWidth)>0.5 || h!=rowHeight;
            if(resized || replacementOffset.HasValue) {
                bool initial=columns==0;
                bool replacing=replacementOffset.HasValue;
                // The scene that stays where it is on screen when the tiles change size: the paused scene while any of
                // it shows, otherwise the scene in the middle of the screen. Anchoring on the top row pushed the paused
                // scene off the screen whenever the tiles grew.
                int keep=Math.Max(0,previous);double place=0.5;
                if(!initial && !replacing && rowHeight>0) {
                    double seen=shownHeight>0?shownHeight:scroll.ViewportHeight;
                    int row=previous>=0?(previous+leadingSlots)/columns:-1;
                    if(row<0 || (row+1)*rowHeight<=offset || row*rowHeight>=offset+seen) {
                        row=(int)((offset+seen/2)/rowHeight);
                        keep=Math.Max(0,Math.Min(total-1,row*columns-leadingSlots+columns/2));
                        row=(keep+leadingSlots)/columns;
                    }
                    place=Math.Max(0,Math.Min(1,((row+0.5)*rowHeight-offset)/seen));
                }
                replacementOffset=null;leadingSlots=pad;
                columns=cols;cellWidth=w;rowHeight=h;
                canvas.Height=Math.Ceiling((total+pad)/(double)cols)*h;
                double top=replacing?offset:((keep+pad)/cols+0.5)*h-place*scroll.ViewportHeight;
                if(initial && previous>=0)top=(before+after+1)*h/2-scroll.ViewportHeight/2;
                offset=Math.Max(0,Math.Min(top,Math.Max(0,canvas.Height-scroll.ViewportHeight)));
                scroll.ScrollToVerticalOffset(offset);
            }
            shownHeight=scroll.ViewportHeight;
            int start=Math.Max(0,Math.Max(0,(int)(offset/h)-1)*cols-pad);
            int stop=Math.Min(total,((int)Math.Ceiling((offset+scroll.ViewportHeight)/h)+1)*cols-pad);
            // Scrolled to the top or bottom of the scenes read so far: read more that way.
            if(useNearby && wideReady && extendWanted==0) {
                if(start<=cols && !atVideoStart)RequestMore(-1);
                else if(stop>=total-cols && !atVideoEnd)RequestMore(1);
            }
            // Tiles exist one screen above and below what is shown, so a small scroll never meets an empty tile.
            int screenRows=Math.Max(1,(int)Math.Ceiling(scroll.ViewportHeight/h));
            int cellStart=Math.Max(0,start-screenRows*cols),cellStop=Math.Min(total,stop+screenRows*cols);
            if(!resized && cellStart==first && cellStop==end)return;
            first=cellStart;end=cellStop;
            foreach(int i in cells.Keys.Where(i=>resized || i<cellStart || i>=cellStop).ToArray()) {
                canvas.Children.Remove(cells[i]);cells.Remove(i);
            }
            double dpi=PresentationSource.FromVisual(this).CompositionTarget.TransformToDevice.M11;
            int side=Math.Max(1,(int)Math.Ceiling(w*dpi));
            for(int i=cellStart;i<cellStop;i++) {
                Button tile;
                if(!cells.TryGetValue(i,out tile)) {
                    tile=MakeTile(i);tile.Width=Math.Max(1,w-3);tile.Height=h-3;
                    // A picture read earlier shows at once, even one read at another tile size (until the new one comes).
                    Picture saved;
                    if(pictures.TryGetValue(i,out saved))((Image)tile.Tag).Source=saved.Image;
                    Canvas.SetLeft(tile,((i+pad)%cols)*w+1);Canvas.SetTop(tile,((i+pad)/cols)*h+1);
                    cells.Add(i,tile);canvas.Children.Add(tile);
                }
            }
            Func<int,bool> missing=i=>{Picture saved;return !pictures.TryGetValue(i,out saved) || saved.Side!=side;};
            // On screen first, then onward below it, then above it, up to four screens each way.
            int farStart=Math.Max(0,start-4*screenRows*cols),farStop=Math.Min(total,stop+4*screenRows*cols);
            var shown=Enumerable.Range(start,Math.Max(0,stop-start)).Where(missing).ToArray();
            var later=Enumerable.Range(stop,Math.Max(0,farStop-stop)).Concat(Enumerable.Range(farStart,Math.Max(0,start-farStart))).Where(missing).ToArray();
            requested=new Request {Indices=shown,Later=later,Side=side,Paused=previous,Version=rangeVersion};work.Set();
        }
        void RequestMore(int direction) {
            extendWanted=direction;
            count.Text=L.T("Loading more…");
            work.Set();
        }
        Button MakeTile(int index) {
            var tile=UI.Btn("",()=>EditFrame(index));
            tile.Padding=new Thickness(2);tile.HorizontalContentAlignment=HorizontalAlignment.Stretch;tile.VerticalContentAlignment=VerticalAlignment.Stretch;
            if(index==previous){tile.SetResourceReference(Control.BorderBrushProperty,"Accent");tile.BorderThickness=new Thickness(2);}
            var content=new DockPanel();
            // An empty tile is the same quiet band as the main grid, not a white flash.
            var holder=new Border();UI.Use(holder,Border.BackgroundProperty,"TileBand");
            string position=nearby==null?(index+1).ToString():Viewer.Stamp(nearby.Start(index));
            string label=position;if(index==previous)label+=L.T(" · paused scene");
            var number=UI.Text(label,11);number.HorizontalAlignment=HorizontalAlignment.Center;
            DockPanel.SetDock(number,Dock.Bottom);content.Children.Add(number);
            var image=new Image {Stretch=Stretch.Uniform};
            // The picture sits on a white or black sheet the size of the picture, as covers do (Pictures.DrawCover).
            var sheet=new Grid {HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
            sheet.Children.Add(image);holder.Child=sheet;content.Children.Add(holder);tile.Tag=image;tile.Content=content;
            ApplyBrightness(tile);
            AutomationProperties.SetName(tile,L.T("Scene ")+position);return tile;
        }
        void ApplyBrightness(Button tile) {
            var image=(Image)tile.Tag;var sheet=(Grid)image.Parent;
            sheet.Background=brightness==0?null:brightness>0?Brushes.White:Brushes.Black;
            image.Opacity=1-Math.Abs(brightness)/100.0;
        }
        async void EditFrame(int index) {
            if(!scroll.IsEnabled)return;
            scroll.IsEnabled=false;
            try {
                var token=lifetime.Token;
                FrameTimes current=nearby;
                int frameCount=total;
                var image=await Task.Run(()=>current==null?AnimationFrames.Still(path,index,previewSide):
                    VideoFrames.AtTime(path,current.Start(index),current.TimestampOffset(index),previewSide,token));
                if(closed)return;
                Rect? saved=photo!=null && photo.HasCrop?(Rect?)photo.Crop:null;
                // Brightness changed while watching wins; otherwise the saved cover's own brightness.
                var editor=new CropWindow(this,image,photo.Name,saved,photo.Rotation,snapEdges,wheelZoomPercent,previewSide,
                    brightness!=0?brightness:photo.Brightness);
                editor.CommitCover=CommitCover;editor.AfterCommit=AfterCommit;editor.SkipSeconds=SkipSeconds;editor.BrightnessStep=BrightnessStep;
                editor.AddFrameControls(path,index,frameCount,current);
                if(CommitMotion!=null && MotionMaker.Supports(path))
                    editor.EnableMotion(CommitMotion,()=>new MotionSource {Path=path,Moment=MotionSource.MomentOf(path,editor.FrameTime,editor.FrameIndex),
                        Crop=editor.Surface.Fraction,Rotation=editor.Surface.Rotation,Brightness=editor.Surface.Brightness,
                        Still=Pictures.RenderRotatedCover(editor.Surface.Source,editor.Surface.Fraction,editor.Surface.Rotation,540,960,editor.Surface.Brightness)});
                if(editor.ShowDialog()==true) {SavedCrop=editor;DialogResult=true;}
                else if(!editor.BackToFrames) DialogResult=false;
            } catch(Exception ex){if(!closed)UI.Error(this,ex);}
            finally {if(!closed)scroll.IsEnabled=true;}
        }
        void Decode() {
            try {
                if(useNearby){DecodeNearby();return;}
                using(var file=new AnimationFrames(path,lifetime.Token)) {
                    Dispatcher.Invoke(new Action(()=>SetRange(file.Times,false)));
                    DecodeVisible(file);
                }
            } catch(Exception ex) {
                if(!closed)Dispatcher.Invoke(new Action(()=>{
                    indexTimer.Stop();count.Text=L.T("Could not read the scenes.");count.ToolTip=ex.Message;
                }));
            }
        }

        void DecodeNearby() {
            var token=lifetime.Token;
            double center=anchorTime.Value;
            FrameTimes close=null;
            // The grid shows one scene every so often (0.2 s unless changed in the options), not every picture of the video.
            double step=Scenes.StepMs;
            try {close=VideoFrames.Nearby(path,center,token,350).Thin(step,center);}
            catch(InvalidDataException) { }
            bool shownClose=close!=null && close.Start(0)<=center;
            if(shownClose) {
                using(var file=new AnimationFrames(path,token,close)) {
                    Dispatcher.Invoke(new Action(()=>SetRange(close,true)));
                    work.WaitOne();
                    if(closed)return;
                    var view=requested;
                    if(view!=null && view.Indices.Contains(view.Paused)) {
                        // Decode adjacent frames in sequence, but show the paused thumbnail first.
                        bool hasPrevious=view.Paused>0;
                        var earlier=hasPrevious?file.Read(view.Paused-1,view.Side):null;
                        Present(view,view.Paused,file.Read(view.Paused,view.Side));
                        if(earlier!=null)Present(view,view.Paused-1,earlier);
                        if(view.Paused+1<file.Count)Present(view,view.Paused+1,file.Read(view.Paused+1,view.Side));
                    }
                }
            }
            token.ThrowIfCancellationRequested();
            FrameTimes wide;
            try {wide=VideoFrames.Nearby(path,center,token,Scenes.RadiusMs).Thin(step,center);}
            catch(InvalidDataException) {
                if(!shownClose)throw;
                wide=close;
            }
            if(wide.Count>0 && wide.Start(0)<=0.5)atVideoStart=true;
            var current=wide;
            var reader=new AnimationFrames(path,token,current);
            try {
                Dispatcher.Invoke(new Action(()=>{SetRange(current,true);wideReady=true;}));
                while(!closed) {
                    DecodeVisible(reader);
                    int direction=extendWanted;
                    if(closed || direction==0)return;
                    // Widen the range that way by the usual reach (75 scenes), keeping at most MaxSpanMs in all.
                    double from=current.Start(0),to=current.Start(current.Count-1);
                    double newFrom=direction<0?Math.Max(0,from-Scenes.RadiusMs):from;
                    double newTo=direction>0?to+Scenes.RadiusMs:to;
                    if(newTo-newFrom>MaxSpanMs){if(direction<0)newTo=newFrom+MaxSpanMs;else newFrom=newTo-MaxSpanMs;}
                    FrameTimes wider=null;
                    try {
                        // Thinned from a scene already shown, so the new list keeps the same scene grid.
                        double gridScene=direction<0?from:to;
                        wider=VideoFrames.Nearby(path,(newFrom+newTo)/2,token,(int)Math.Ceiling((newTo-newFrom)/2)+1).Thin(step,gridScene);
                    } catch(InvalidDataException) {}
                    token.ThrowIfCancellationRequested();
                    bool grew=wider!=null && wider.Count>0 &&
                        (direction<0?wider.Start(0)<from-0.5:wider.Start(wider.Count-1)>to+0.5);
                    if(!grew) {
                        // Nothing more that way: the video's start or end.
                        if(direction<0)atVideoStart=true;else atVideoEnd=true;
                        extendWanted=0;
                        var shown=current;
                        Dispatcher.Invoke(new Action(()=>{if(!closed)count.Text=RangeText(shown);}));
                        work.Set(); // finish the pictures that were waiting when reading started
                        continue;
                    }
                    if(wider.Start(0)<=0.5)atVideoStart=true;else if(direction>0)atVideoStart=false;
                    if(direction<0)atVideoEnd=false;
                    reader.Dispose();
                    current=wider;reader=new AnimationFrames(path,token,current);
                    var next=current;
                    Dispatcher.Invoke(new Action(()=>{extendWanted=0;SetRange(next,true);}));
                }
            } finally {reader.Dispose();}
        }
        static string RangeText(FrameTimes times) {
            return L.F("{0}–{1} · {2:N0} scenes",Viewer.Stamp(times.Start(0)),Viewer.Stamp(times.Start(times.Count-1)),times.Count);
        }

        void SetRange(FrameTimes times,bool local) {
            if(closed)return;
            indexTimer.Stop();
            int marked=anchorTime.HasValue?times.At(anchorTime.Value):Math.Min(times.Count-1,previous);
            // After the range moved away from the paused scene, no tile is the paused one.
            if(local && anchorTime.HasValue && (anchorTime.Value<times.Start(0)-0.5 || anchorTime.Value>times.Start(times.Count-1)+0.5))marked=-1;
            if(local && nearby!=null && columns>0) {
                int oldTop=Math.Min(total-1,Math.Max(0,(int)(scroll.VerticalOffset/rowHeight)*columns-leadingSlots));
                int newTop=FindFrame(times,nearby,oldTop);
                if(newTop<0)newTop=times.At(nearby.Start(oldTop));
                // Keep the tile at the top in the same column, so the grid doesn't shift sideways.
                int pad=((oldTop+leadingSlots-newTop)%columns+columns)%columns;
                replacementOffset=scroll.VerticalOffset+
                    ((newTop+pad)/columns-(oldTop+leadingSlots)/columns)*rowHeight;
                leadingSlots=pad;
                cells.Clear();canvas.Children.Clear();
            }
            // Pictures already read move to their place in the new list; the same scene keeps its picture.
            var moved=new Dictionary<int,Picture>();
            if(local && nearby!=null)
                foreach(var pair in pictures) {
                    int index=FindFrame(times,nearby,pair.Key);
                    if(index>=0)moved[index]=pair.Value;
                }
            pictures=moved;pictureBytes=moved.Values.Sum(p=>p.Bytes);
            rangeVersion++;
            if(local)nearby=times;
            requested=null;first=end=-1;
            previous=marked;total=times.Count;
            count.Text=local?RangeText(times):
                L.F("{0:N0} scenes",total);
            ScheduleLayout();
        }

        static int FindFrame(FrameTimes target,FrameTimes source,int index) {
            double stamp=source.Start(index);
            int last=target.At(stamp);
            if(target.Start(last)!=stamp)return -1;
            int offset=source.TimestampOffset(index),lastOffset=target.TimestampOffset(last);
            return offset<=lastOffset?last-lastOffset+offset:-1;
        }

        void DecodeVisible(AnimationFrames file) {
            // What this thread read for the previous request, so a new one does not read again a picture
            // that has not reached the window yet. (Older ones have, or were let go for memory and may be read again.)
            var read=new HashSet<int>();var before=new HashSet<int>();int readVersion=-1,readSide=-1;
            while(!closed) {
                work.WaitOne();
                if(extendWanted!=0)return; // the caller reads more scenes, then comes back
                var view=requested;
                if(closed || view==null)continue;
                if(view.Version!=readVersion || view.Side!=readSide){read.Clear();readVersion=view.Version;readSide=view.Side;}
                before=read;read=new HashSet<int>();
                IEnumerable<int> indices=view.Indices;
                if(view.Indices.Contains(view.Paused)){
                    var keyframe=file.Times.KeyframeBefore(file.Times.Start(view.Paused));
                    // Show the paused frame's group first when earlier tiles need a separate seek.
                    if(keyframe.HasValue && file.Times.Start(view.Indices[0])<keyframe.Value)
                        indices=indices.OrderBy(i=>file.Times.Start(i)<keyframe.Value);
                }
                foreach(int i in indices.Concat(view.Later)) {
                    if(closed || requested!=view || extendWanted!=0)break;
                    if(before.Contains(i) || !read.Add(i))continue;
                    Present(view,i,file.Read(i,view.Side));
                }
            }
        }

        // Hands a picture to the window without waiting, so reading goes on while the grid is being scrolled.
        void Present(Request view,int index,BitmapSource frame) {
            Dispatcher.BeginInvoke(DispatcherPriority.Render,new Action(()=>{
                if(closed || view.Version!=rangeVersion)return;
                Picture old;
                if(pictures.TryGetValue(index,out old))pictureBytes-=old.Bytes;
                var picture=new Picture {Image=frame,Side=view.Side,Bytes=(long)frame.PixelWidth*frame.PixelHeight*4};
                pictures[index]=picture;pictureBytes+=picture.Bytes;
                Button tile;
                if(cells.TryGetValue(index,out tile))((Image)tile.Tag).Source=frame;
                if(pictureBytes>PictureLimit)Trim();
            }));
        }
        // Over the memory limit: let go of the pictures farthest from the tiles on screen.
        void Trim() {
            int middle=first<0?0:(first+end)/2;
            foreach(var index in pictures.Keys.OrderByDescending(i=>Math.Abs(i-middle)).ToArray()) {
                if(pictureBytes<=PictureLimit*3/4)break;
                if(cells.ContainsKey(index))continue;
                pictureBytes-=pictures[index].Bytes;pictures.Remove(index);
            }
        }
    }
}
