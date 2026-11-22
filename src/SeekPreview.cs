using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LoveMyShelf {
    // Hover thumbnail above the playback bar, like PotPlayer's: just the picture, no frame or background,
    // with the time written inside it at the bottom in white letters outlined in black.
    internal sealed class SeekPreview {
        const int CacheSize=64;
        // Thumbnails are grouped by the scene step (Guide & Options), so hovering moves one scene at a time.
        static long SampleMs {get {return Math.Max(30,Scenes.StepMs);}}
        // Largest size on screen (device-independent pixels): landscape up to 320 wide, portrait up to 300 tall.
        internal const double MaxWidth=320, MaxHeight=300;
        readonly Grid host;
        readonly DragSlider slider;
        readonly Canvas layer=new Canvas {IsHitTestVisible=false,ClipToBounds=true};
        readonly Grid card=new Grid {Visibility=Visibility.Collapsed};
        readonly Image thumbnail=new Image {Stretch=Stretch.Fill};
        readonly OutlinedText time=new OutlinedText {FontSize=15,HorizontalAlignment=HorizontalAlignment.Center,
            VerticalAlignment=VerticalAlignment.Bottom,Margin=new Thickness(0,0,0,6)};
        internal FrameworkElement Card {get{return card;}}
        internal Image Picture {get{return thumbnail;}}
        internal string TimeText {get{return time.Text;}}
        // The playback brightness (E/W): the picture sits on a white or black card at 1-|level|/100,
        // the same as the video, the scene grid and saved covers. The time stays crisp.
        internal int Brightness {
            set {
                int level=Math.Max(-100,Math.Min(100,value));
                card.Background=level==0?null:level>0?Brushes.White:Brushes.Black;
                thumbnail.Opacity=1-Math.Abs(level)/100.0;
            }
        }
        // Pixels to read for a sharp picture at this display's scale.
        internal int ReadSide {get {
            var source=PresentationSource.FromVisual(host);
            double scale=source!=null && source.CompositionTarget!=null?source.CompositionTarget.TransformToDevice.M11:1;
            return (int)Math.Ceiling(Math.Max(MaxWidth,MaxHeight)*Math.Max(1,scale));
        }}
        readonly Dictionary<long,BitmapSource> cache=new Dictionary<long,BitmapSource>();
        readonly Queue<long> recent=new Queue<long>();
        CancellationTokenSource request,opening;
        Task<MpvThumbnails> thumbnails;
        string path;
        long sample=-1,shownSample=-1;
        double pointerX;

        public SeekPreview(Grid host,DragSlider slider) {
            this.host=host;this.slider=slider;
            Panel.SetZIndex(layer,1);host.Children.Add(layer);
            RenderOptions.SetBitmapScalingMode(thumbnail,BitmapScalingMode.HighQuality);
            card.Children.Add(thumbnail);card.Children.Add(time);
            slider.AddHandler(Mouse.MouseMoveEvent,new MouseEventHandler((s,e)=>Hover(e.GetPosition(slider))),true);
            slider.MouseEnter+=(s,e)=>Hover(e.GetPosition(slider));
            slider.MouseLeave+=delegate {Hide();};
            slider.PointerDragStarted+=Hide;
        }
        public void Start(string source) {
            Hide();cache.Clear();recent.Clear();path=source;
            CloseThumbnails();
        }
        // Opened on the first hover so it does not slow down starting playback.
        Task<MpvThumbnails> Thumbnails() {
            if(thumbnails==null && path!=null && VideoFrames.Supports(path) && MpvPlayer.Available) {
                opening=new CancellationTokenSource();
                var token=opening.Token;string source=path;
                thumbnails=Task.Run(()=>MpvThumbnails.TryOpen(source,token));
            }
            return thumbnails;
        }
        void CloseThumbnails() {
            if(opening!=null){opening.Cancel();opening=null;}
            var old=thumbnails;thumbnails=null;
            if(old!=null)old.ContinueWith(t=>{if(t.Status==TaskStatus.RanToCompletion && t.Result!=null)t.Result.Dispose();});
        }
        public void Hide() {
            sample=shownSample=-1;CancelRequest();HideCard();
        }
        void CancelRequest() {if(request!=null && !request.IsCancellationRequested)request.Cancel();}
        void HideCard() {card.Visibility=Visibility.Collapsed;layer.Children.Remove(card);thumbnail.Source=null;}
        public void Clear() {Start(null);}

        internal void Hover(Point point) {
            if(path==null || !slider.IsVisible || slider.IsMouseCaptured || !new Rect(slider.RenderSize).Contains(point)){Hide();return;}
            double value=slider.ValueAt(point);
            pointerX=point.X;
            long step=SampleMs;
            long target=(long)(Math.Max(0,Math.Min(value,slider.Maximum-1))/step)*step;
            if(target==sample) {
                if(card.Visibility==Visibility.Visible)PinCard();
                return;
            }
            sample=target;
            BitmapSource cached;
            if(cache.TryGetValue(target,out cached))Show(target,cached);
            else {
                if(card.Visibility==Visibility.Visible)PinCard();
                LoadLatest();
            }
        }
        async void LoadLatest() {
            if(request!=null || path==null || sample<0 || !slider.IsVisible || cache.ContainsKey(sample))return;
            string source=path;
            long target=sample;
            int side=ReadSide;
            var fast=Thumbnails();
            var pending=new CancellationTokenSource();
            request=pending;
            var token=pending.Token;
            try {
                var frame=await Task.Run(()=>{
                    if(fast!=null) {
                        // A hidden mpv instance shows the nearest keyframe in a few tens of milliseconds.
                        MpvThumbnails thumbs=null;
                        try{thumbs=fast.Result;}catch(AggregateException){}
                        if(thumbs!=null) {
                            try{return thumbs.Read(target,side,token);}
                            catch(OperationCanceledException){throw;}
                            catch(Exception){token.ThrowIfCancellationRequested();}
                        }
                    }
                    if(VideoFrames.Supports(source))return VideoFrames.PreviewAt(source,target,side,token);
                    using(var reader=new AnimationFrames(source,token))
                        return reader.Read(reader.Times.At(target),side);
                },token);
                if(token.IsCancellationRequested || sample<0 || !slider.IsVisible)return;
                if(cache.Count==CacheSize)cache.Remove(recent.Dequeue());
                cache.Add(target,frame);recent.Enqueue(target);
                if(target==sample || (shownSample>=0 && Math.Abs(target-sample)<Math.Abs(shownSample-sample)))
                    Show(target,frame);
            } catch(Exception) {
                // A thumbnail is optional and must not interrupt playback.
            } finally {
                request=null;
                bool superseded=token.IsCancellationRequested || target!=sample;
                pending.Dispose();
                // Let each decode finish, then skip to the latest requested time.
                if(superseded)LoadLatest();
            }
        }
        void Show(long target,BitmapSource frame) {
            // Fits the largest box, and never takes more than about half of a small window.
            double boxWidth=Math.Min(MaxWidth,Math.Max(120,host.ActualWidth*0.45)),boxHeight=Math.Min(MaxHeight,Math.Max(90,host.ActualHeight*0.45));
            double scale=Math.Min(boxWidth/frame.PixelWidth,boxHeight/frame.PixelHeight);
            thumbnail.Width=Math.Round(frame.PixelWidth*scale);thumbnail.Height=Math.Round(frame.PixelHeight*scale);
            card.Width=thumbnail.Width;card.Height=thumbnail.Height;
            thumbnail.Source=frame;time.Text=Clock(target);shownSample=target;
            if(card.Visibility!=Visibility.Visible) {
                layer.Children.Add(card);card.Visibility=Visibility.Visible;
            }
            PinCard();
        }
        void PinCard() {
            var position=slider.TranslatePoint(new Point(pointerX,0),host);
            card.Measure(new Size(Double.PositiveInfinity,Double.PositiveInfinity));
            Canvas.SetLeft(card,Math.Max(4,Math.Min(position.X-card.DesiredSize.Width/2,host.ActualWidth-card.DesiredSize.Width-4)));
            Canvas.SetTop(card,Math.Max(0,position.Y-card.DesiredSize.Height-8));
        }
        // 00:01:02.34 (hundredths), the same precision as the playback bar.
        internal static string Clock(double ms) {
            var t=TimeSpan.FromMilliseconds(Math.Max(0,ms));
            return ((int)t.TotalHours).ToString("00")+":"+t.Minutes.ToString("00")+":"+t.Seconds.ToString("00")+"."+(t.Milliseconds/10).ToString("00");
        }
    }

    // White text with a black outline, readable on any picture.
    internal sealed class OutlinedText : FrameworkElement {
        string text="";FormattedText formatted;
        public double FontSize=14;
        public string Text {get{return text;}set{text=value??"";formatted=null;InvalidateMeasure();InvalidateVisual();}}
        FormattedText Make(string s) {
            var family=(FontFamily)Application.Current.TryFindResource("AppFont")??new FontFamily("Segoe UI");
            double dpi=1;var source=PresentationSource.FromVisual(this);
            if(source!=null && source.CompositionTarget!=null)dpi=source.CompositionTarget.TransformToDevice.M11;
            return new FormattedText(s,System.Globalization.CultureInfo.CurrentUICulture,FlowDirection.LeftToRight,
                new Typeface(family,FontStyles.Normal,FontWeights.Bold,FontStretches.Normal),FontSize,Brushes.White,dpi);
        }
        FormattedText Formatted() {
            if(formatted!=null)return formatted;
            formatted=Make(text.Length==0?" ":text);
            return formatted;
        }
        // Every digit takes the same width, so a changing time never makes the text shimmer.
        double Advance(char c,double cell,FormattedText glyph) {return Char.IsDigit(c)?cell:glyph.WidthIncludingTrailingWhitespace;}
        protected override Size MeasureOverride(Size available) {
            double cell=Make("0").WidthIncludingTrailingWhitespace,width=0;
            foreach(char c in text)width+=Advance(c,cell,Make(c.ToString()));
            return new Size(width+4,Formatted().Height+4);
        }
        protected override void OnRender(DrawingContext dc) {
            if(text.Length==0)return;
            var outline=new Pen(new SolidColorBrush(Color.FromArgb(0xE6,0,0,0)),3.2) {LineJoin=PenLineJoin.Round};
            double cell=Make("0").WidthIncludingTrailingWhitespace,x=2;
            foreach(char c in text) {
                var glyph=Make(c.ToString());
                double advance=Advance(c,cell,glyph);
                var geometry=glyph.BuildGeometry(new Point(x+(advance-glyph.WidthIncludingTrailingWhitespace)/2,2));
                dc.DrawGeometry(null,outline,geometry);
                dc.DrawGeometry(Brushes.White,null,geometry);
                x+=advance;
            }
        }
    }
}
