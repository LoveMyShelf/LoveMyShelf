using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LoveMyShelf {
    // The first set of "make a moving picture" extras (1.0.9): flip, how many times it plays, a pause at the end,
    // quality as a number, a size to stay under, and one line of text. Also "make a moving picture smaller".
    internal static class MotionLook {
        static string N(double v) {return v.ToString("0.####",CultureInfo.InvariantCulture);}
        // 1..100 from the three buttons (High, Normal, Low) when no number was typed.
        internal static int QualityOf(MotionRequest r) {return r.Q>0?Math.Max(1,Math.Min(100,r.Q)):new[]{90,75,55}[Math.Max(0,Math.Min(2,r.Quality))];}
        // GIF: how many colors and how the dots are spread. Untouched choices keep the old settings exactly.
        internal static string GifPalette(MotionRequest r) {
            string see=r.KeepAlpha?":reserve_transparent=1":"";
            if(r.Q<=0)return "palettegen=stats_mode=full"+(r.Quality==2?":max_colors=128":"")+see;
            int q=QualityOf(r);
            int colors=q>=88?256:Math.Max(16,(int)Math.Round(q*2.6));
            return "palettegen=stats_mode=full"+(colors<256?":max_colors="+colors:"")+see;
        }
        // How many times a GIF or WebP plays as made (0 = forever), so a smaller copy plays the same.
        // GIF: no loop block = once; a loop block counts the plays after the first (0 = forever). WebP: the ANIM count.
        internal static int PlaysOf(string path) {
            try {
                var bytes=File.ReadAllBytes(path);
                if(path.EndsWith(".webp",StringComparison.OrdinalIgnoreCase)) {
                    for(int i=12;i+16<=bytes.Length;i++)
                        if(bytes[i]=='A' && bytes[i+1]=='N' && bytes[i+2]=='I' && bytes[i+3]=='M')return bytes[i+12]|bytes[i+13]<<8;
                    return 0;
                }
                var mark=Encoding.ASCII.GetBytes("NETSCAPE2.0");
                for(int i=0;i+mark.Length+4<=bytes.Length;i++) {
                    bool hit=true;
                    for(int k=0;k<mark.Length && hit;k++)hit=bytes[i+k]==mark[k];
                    if(!hit)continue;
                    int at=i+mark.Length; // sub-block: size 3, id 1, count (2 bytes, little end)
                    if(bytes[at]!=3 || bytes[at+1]!=1)return 0;
                    int loops=bytes[at+2]|bytes[at+3]<<8;
                    return loops==0?0:Math.Min(100,loops+1);
                }
                return 1;
            } catch(IOException) {return 0;} catch(UnauthorizedAccessException) {return 0;}
        }
        internal static string GifDither(MotionRequest r) {
            if(r.Q<=0)return r.Quality==0?"sierra2_4a":"bayer:bayer_scale="+(r.Quality==2?5:4);
            int q=QualityOf(r);
            return q>=85?"sierra2_4a":"bayer:bayer_scale="+(q>=60?4:5);
        }
        internal static int WebpQuality(MotionRequest r) {return r.Q<=0?(r.Quality==0?90:r.Quality==2?65:82):QualityOf(r);}
        internal static int Mp4Crf(MotionRequest r) {return r.Q<=0?(r.Quality==0?17:r.Quality==2?25:20):Math.Max(14,Math.Min(40,(int)Math.Round(51-QualityOf(r)*0.34)));}
        // GIF counts the plays after the first (-1 = once); WebP counts every play (0 = forever on both).
        internal static string GifLoop(MotionRequest r) {return r.Plays<=0?"0":r.Plays==1?"-1":(r.Plays-1).ToString(CultureInfo.InvariantCulture);}
        internal static string WebpLoop(MotionRequest r) {return Math.Max(0,r.Plays).ToString(CultureInfo.InvariantCulture);}
        // The pause on the last picture, added after the play style (so a back-and-forth pauses at its end too).
        internal static string Hold(MotionRequest r) {return r.Hold>0.01?"tpad=stop_mode=clone:stop_duration="+N(Math.Min(30,r.Hold)):null;}

        // Text is drawn on the finished picture, above any drawings or flips.
        // The text and the font are copied into a folder of their own, so no path or letter in them can upset FFmpeg.
        // Where the text and the font copy go: beside the file being made, or (when that path has an apostrophe,
        // which FFmpeg's filter text can't hold) a folder of its own under ProgramData.
        internal static string WorkFolder(string part) {
            string work=part+".dress";
            return work.IndexOf((char)39)<0?work:Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"LoveMyShelf","text",Guid.NewGuid().ToString("N"));
        }
        internal static string Flips(MotionRequest r) {
            return r.FlipX?(r.FlipY?"hflip,vflip":"hflip"):(r.FlipY?"vflip":null);
        }
        internal static string Dress(MotionRequest r,int outH,string work) {
            var steps=new StringBuilder();
            string text=(r.Text??"").Replace("\r"," ").Replace("\n"," ").Trim();
            if(text.Length>0) {
                Directory.CreateDirectory(work);
                string textFile=Path.Combine(work,"text.txt"),font=TextFonts.PathOf(r.Font);
                File.WriteAllText(textFile,text,new UTF8Encoding(false));
                string fontCopy=FontCopy(font,work);
                int px=Math.Max(8,(int)Math.Round(outH*Math.Max(2,Math.Min(40,r.TextSize))/100.0));
                int border=TextLook.EdgePixels(r,px),pad=r.Band?TextLook.BandPad(px):0;
                // Room kept at the picture's edges, so the edge and the band are never cut off there.
                int keep=Math.Max(border,pad);
                string k=keep.ToString(CultureInfo.InvariantCulture);
                // Top, middle, bottom; or the middle of the text at a place of your own, kept inside the picture.
                string x="(w-text_w)/2",y=r.TextPlace==0?"max("+k+"\\,h*0.04)":r.TextPlace==1?"(h-text_h)/2":"min(h-text_h-"+k+"\\,h-text_h-h*0.04)";
                if(r.TextPlace==3) {
                    x="max("+k+"\\,min(w-text_w-"+k+"\\,w*"+N(Math.Max(0,Math.Min(100,r.TextX))/100)+"-text_w/2))";
                    y="max("+k+"\\,min(h-text_h-"+k+"\\,h*"+N(Math.Max(0,Math.Min(100,r.TextY))/100)+"-text_h/2))";
                }
                steps.Append("drawtext=fontfile=").Append(Quote(fontCopy)).Append(":textfile=").Append(Quote(textFile))
                    .Append(":expansion=none:fontsize=").Append(px).Append(":fontcolor=").Append(TextLook.Hex(r.TextColor));
                if(border>0)steps.Append(":borderw=").Append(border).Append(":bordercolor=").Append(TextLook.Hex(r.EdgeColor));
                if(r.Band)steps.Append(":box=1:boxcolor=").Append(TextLook.Hex(r.BandColor)).Append('@').Append(N(Math.Max(0,Math.Min(100,r.BandStrength))/100))
                    .Append(":boxborderw=").Append(pad);
                steps.Append(":x=").Append(x).Append(":y=").Append(y).Append(',');
            }
            return steps.Length==0?null:steps.ToString(0,steps.Length-1);
        }
        // FFmpeg's font reader can't open a path with letters outside English (a folder named "사진", a Korean user
        // name): it then quietly draws with a fallback font that has no Korean letters. So the font is copied once to
        // a folder under ProgramData, whose path is plain, and used from there. Only if that path isn't plain either,
        // the copy beside the file is used (as before).
        internal static string FontCopy(string font,string work) {
            try {
                string folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"LoveMyShelf","fonts");
                if(folder.All(ch=>ch<128 && ch!=(char)39)) {
                    var info=new FileInfo(font);
                    uint hash=2166136261;foreach(char ch in info.FullName.ToLowerInvariant()+"|"+info.Length+"|"+info.LastWriteTimeUtc.Ticks)hash=(hash^ch)*16777619; // the same every run
                    string key=hash.ToString("X8",CultureInfo.InvariantCulture);
                    string copy=Path.Combine(folder,Path.GetFileNameWithoutExtension(font).Where(ch=>ch<128 && Char.IsLetterOrDigit(ch)).Aggregate("",(s,ch)=>s+ch)+"-"+key+Path.GetExtension(font).ToLowerInvariant());
                    if(!File.Exists(copy) || new FileInfo(copy).Length!=info.Length) {
                        Directory.CreateDirectory(folder);
                        if(File.Exists(copy))File.Delete(copy); // a broken copy from before
                        string part=copy+"."+Guid.NewGuid().ToString("N")+".tmp";
                        File.Copy(font,part,true);
                        try {File.Move(part,copy);} catch(IOException) {File.Delete(part);} // another window copied it first
                    }
                    if(File.Exists(copy) && new FileInfo(copy).Length==info.Length)return copy;
                }
            } catch(Exception ex) {ErrorLog.Quiet(ex);}
            string local=Path.Combine(work,"font"+Path.GetExtension(font));
            File.Copy(font,local,true);
            return local;
        }
        // A path for a filter option: forward slashes, the drive's colon escaped, inside single quotes.
        static string Quote(string path) {
            return "'"+path.Replace('\\','/').Replace(":","\\:").Replace("'","'\\''")+"'";
        }
        internal static string FontFile() {
            string own=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"fonts","Pretendard-Bold.otf");
            if(File.Exists(own))return own;
            string windows=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"Fonts","malgunbd.ttf");
            return File.Exists(windows)?windows:Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"Fonts","arial.ttf");
        }
        internal static string Base64(string text) {return Convert.ToBase64String(Encoding.UTF8.GetBytes(text??""));}
        internal static string FromBase64(string text) {
            try {return Encoding.UTF8.GetString(Convert.FromBase64String(text??""));} catch(FormatException) {return "";}
        }

        // [−] [number] [+] unit: a number can be typed or stepped. Applied with Enter, on leaving the box, or a step.
        // fine: the smallest typed step when it is finer than the [-][+] step (0.01 for the smooth loop).
        internal static FrameworkElement Number(double value,double min,double max,double step,string unit,string name,Action<double> changed,out Func<double> read,out Action<double> write,double fine=0) {
            var row=new StackPanel {Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};
            var box=new TextBox {Width=54,MinHeight=26,VerticalContentAlignment=VerticalAlignment.Center,HorizontalContentAlignment=HorizontalAlignment.Center,Margin=new Thickness(3,0,3,0)};
            AutomationProperties.SetName(box,name);box.ToolTip=name;
            double current=value;
            double grain=fine>0?fine:step<1?0.1:1;
            string pattern=grain<0.1?"0.0#":grain<1?"0.0":"0";
            Action<double> show=v=>{box.Text=v.ToString(pattern,CultureInfo.CurrentCulture);};
            // A typed number stays as typed (to one decimal for a decimal box); only [−] [+] move by the step.
            Action<double> set=v=>{
                v=Math.Max(min,Math.Min(max,Math.Round(v/grain)*grain));
                bool differs=Math.Abs(v-current)>1e-9;current=v;show(v);
                if(differs)changed(v);
            };
            Func<bool> apply=()=>{
                double typed;
                if(Double.TryParse(box.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out typed) ||
                    Double.TryParse(box.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out typed)) {
                    if(Double.IsNaN(typed) || Double.IsInfinity(typed)){show(current);return false;}
                    set(typed);return true;
                }
                show(current);return false;
            };
            var less=UI.Btn("−",()=>set(Math.Ceiling(current/step-1e-9)*step-step));var more=UI.Btn("+",()=>set(Math.Floor(current/step+1e-9)*step+step));
            foreach(var b in new[]{less,more}){b.MinWidth=26;b.Padding=new Thickness(6,0,6,0);}
            AutomationProperties.SetName(less,name+" −");AutomationProperties.SetName(more,name+" +");
            box.KeyDown+=(s,e)=>{if(e.Key==Key.Enter){apply();e.Handled=true;}};
            // Leaving the box changes nothing unless something was typed in it.
            bool edited=false;
            box.TextChanged+=delegate {if(box.IsKeyboardFocusWithin)edited=true;};
            box.LostKeyboardFocus+=delegate {if(edited){edited=false;apply();}};
            row.Children.Add(less);row.Children.Add(box);row.Children.Add(more);
            if(!String.IsNullOrEmpty(unit)){var u=UI.Text(unit,12,true);u.Margin=new Thickness(4,0,0,0);row.Children.Add(u);}
            show(value);
            read=()=>current;write=v=>{current=Math.Max(min,Math.Min(max,v));show(current);};
            return row;
        }
    }

    // The line of text over the GIF window's preview, drawn like the file will have it (its font, colors, edge and band).
    // It can be dragged to any place (then the place is "Your own"); while the made file's own pictures play (they
    // already have the text), only a dashed box shows where it is, and it can still be dragged.
    internal sealed class TextMark : FrameworkElement {
        string text="";int place=2;double size=8,across=50,down=50;bool boxOnly;
        MotionRequest look=new MotionRequest();
        Rect box=Rect.Empty;
        Point grabbed;bool dragging,moved;
        // While dragging: the middle of the text as percents of the width and the height. Then once when let go.
        internal event Action<double,double> Moving;
        internal event Action Moved;
        internal void Set(string value,int where,double percent,double x,double y,bool onlyBox,MotionRequest style) {
            text=(value??"").Replace("\r"," ").Replace("\n"," ").Trim();place=where;size=percent;across=x;down=y;boxOnly=onlyBox;
            if(style!=null)look=style;
            InvalidateVisual();
        }
        internal TextMark() {Cursor=Cursors.SizeAll;ToolTip=L.P("Drag the text to move it","글자를 끌어서 옮겨요");}
        // The text's shape at its place, and the room kept at the picture's edges (for the edge and the band).
        Geometry Shape(out Rect bounds,out double edge,out double pad) {
            bounds=Rect.Empty;edge=pad=0;
            if(text.Length==0 || ActualHeight<=0)return null;
            double px=Math.Max(6,ActualHeight*Math.Max(2,Math.Min(40,size))/100);
            bool missing;
            Geometry shape=TextFonts.Outline(TextFonts.PathOf(look.Font),text,px,out missing);
            if(shape==null) {
                var family=Application.Current.Resources["AppFont"] as FontFamily??new FontFamily("Malgun Gothic");
                shape=new FormattedText(text,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface(family,FontStyles.Normal,FontWeights.Bold,FontStretches.Normal),px,Brushes.White,1).BuildGeometry(new Point(0,0));
            }
            var ink=shape.Bounds;
            if(ink.IsEmpty)return null;
            edge=look.EdgeSize<0?Math.Max(1,px/12):look.EdgeSize<=0?0:Math.Max(1,px*look.EdgeSize/100);
            pad=look.Band?Math.Max(2,px*0.22):0;
            double keep=Math.Max(edge,pad),x,y;
            if(place==3) {
                // The same rule as the file: the middle of the text at the place picked, kept inside the picture.
                x=Math.Max(keep,Math.Min(ActualWidth-ink.Width-keep,ActualWidth*across/100-ink.Width/2));
                y=Math.Max(keep,Math.Min(ActualHeight-ink.Height-keep,ActualHeight*down/100-ink.Height/2));
            } else {
                x=(ActualWidth-ink.Width)/2;
                y=place==0?Math.Max(keep,ActualHeight*0.04):place==1?(ActualHeight-ink.Height)/2:Math.Min(ActualHeight-ink.Height-keep,ActualHeight-ink.Height-ActualHeight*0.04);
            }
            shape=shape.Clone();shape.Transform=new TranslateTransform(x-ink.X,y-ink.Y);
            bounds=new Rect(x,y,ink.Width,ink.Height);
            return shape;
        }
        protected override void OnRender(DrawingContext dc) {
            Rect at;double edge,pad;
            var shape=Shape(out at,out edge,out pad);
            if(shape==null){box=Rect.Empty;return;}
            box=at;
            if(!boxOnly) {
                if(look.Band)dc.DrawRectangle(new SolidColorBrush(TextLook.ColorOf(look.BandColor,look.BandStrength/100)),null,Rect.Inflate(at,pad,pad));
                if(edge>0)dc.DrawGeometry(null,new Pen(new SolidColorBrush(TextLook.ColorOf(look.EdgeColor)),edge*2){LineJoin=PenLineJoin.Round},shape);
                dc.DrawGeometry(new SolidColorBrush(TextLook.ColorOf(look.TextColor)),null,shape);
            }
            // A dashed box: while the pointer is on the text, while dragging, or where the made file has its text.
            if(boxOnly || dragging || IsMouseOver) {
                var outline=Rect.Inflate(box,4+pad,3+pad);
                dc.DrawRectangle(null,new Pen(Brushes.Black,3),outline);
                dc.DrawRectangle(null,new Pen(Brushes.White,1.5){DashStyle=DashStyles.Dash},outline);
            }
        }
        protected override HitTestResult HitTestCore(PointHitTestParameters p) {
            return !box.IsEmpty && Rect.Inflate(box,8,6).Contains(p.HitPoint)?new PointHitTestResult(this,p.HitPoint):null;
        }
        protected override void OnMouseEnter(MouseEventArgs e) {base.OnMouseEnter(e);InvalidateVisual();}
        protected override void OnMouseLeave(MouseEventArgs e) {base.OnMouseLeave(e);InvalidateVisual();}
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) {
            if(box.IsEmpty)return;
            var p=e.GetPosition(this);
            grabbed=new Point(p.X-(box.X+box.Width/2),p.Y-(box.Y+box.Height/2));
            moved=false;dragging=CaptureMouse();e.Handled=true;
        }
        protected override void OnMouseMove(MouseEventArgs e) {
            if(!dragging || ActualWidth<=0 || ActualHeight<=0)return;
            var p=e.GetPosition(this);
            // Text wider (or taller) than the picture stays in the middle that way; otherwise it stays inside.
            double cx=box.Width>=ActualWidth?ActualWidth/2:Math.Max(box.Width/2,Math.Min(ActualWidth-box.Width/2,p.X-grabbed.X));
            double cy=box.Height>=ActualHeight?ActualHeight/2:Math.Max(box.Height/2,Math.Min(ActualHeight-box.Height/2,p.Y-grabbed.Y));
            moved=true;
            place=3;across=Math.Round(cx/ActualWidth*1000)/10;down=Math.Round(cy/ActualHeight*1000)/10;
            InvalidateVisual();
            if(Moving!=null)Moving(across,down);
        }
        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) {
            if(!dragging)return;
            dragging=false;ReleaseMouseCapture();e.Handled=true; // the click doesn't pause the preview
            InvalidateVisual();
            if(moved && Moved!=null)Moved(); // a click without moving changes nothing
        }
        protected override void OnLostMouseCapture(MouseEventArgs e) {base.OnLostMouseCapture(e);if(dragging){dragging=false;InvalidateVisual();if(moved && Moved!=null)Moved();}}
    }

    // Right-click a GIF or WebP → [Make this moving picture smaller…]: a copy under a size you pick, saved beside it.
    internal sealed class ShrinkWindow : Window {
        readonly string path;
        readonly StackPanel page=new StackPanel {Margin=new Thickness(24,18,24,20)};
        CancellationTokenSource work;
        internal static void Open(Window owner,string path) {
            if(String.IsNullOrEmpty(path) || !File.Exists(path)){if(owner!=null)UI.Error(owner,new FileNotFoundException(L.T("The original file is unavailable.")));return;}
            var window=new ShrinkWindow(path);
            if(owner!=null && owner.IsVisible){window.Owner=owner;window.WindowStartupLocation=WindowStartupLocation.CenterOwner;}
            window.Show();
        }
        ShrinkWindow(string path) {
            this.path=path;
            UI.WindowSetup(this,L.P("Make it smaller","움짤 용량 줄이기"));
            Width=Math.Min(500,SystemParameters.WorkArea.Width);SizeToContent=SizeToContent.Height;ResizeMode=ResizeMode.NoResize;
            var content=new DockPanel();content.Children.Add(new WindowTitleBar(this));content.Children.Add(page);Content=content;
            Closing+=delegate {if(work!=null)work.Cancel();};
            Ask();
        }
        static string Mb(long bytes) {double mb=bytes/1048576.0;return (mb<10?mb.ToString("0.0",CultureInfo.CurrentCulture):mb.ToString("0",CultureInfo.CurrentCulture))+"MB";}
        TextBlock Line(string text,double size,bool subtle,double top=0) {
            var t=UI.Text(NoticeLook.KeepWords(text),size,subtle);t.TextWrapping=TextWrapping.Wrap;t.Margin=new Thickness(0,top,0,0);page.Children.Add(t);return t;
        }
        void Ask() {
            page.Children.Clear();
            long bytes=new FileInfo(path).Length;
            var title=Line(Path.GetFileName(path),16,false);title.FontWeight=FontWeights.SemiBold;
            Line(L.P("Now ","지금 ")+Mb(bytes)+L.P(". A smaller copy is saved next to it; the file itself stays as it is.",". 같은 폴더에 작은 사본을 따로 만들어요. 원래 파일은 그대로 둬요."),13,true,4);
            double start=Math.Max(0.1,Math.Min(20,Math.Round(bytes/1048576.0*0.5,1)));
            Func<double> target,quality;Action<double> setTarget,setQuality;
            var targetRow=new DockPanel {Margin=new Thickness(0,16,0,0)};
            var targetName=UI.Text(L.P("Stay under","이 크기 안으로"),13);targetName.Width=120;DockPanel.SetDock(targetName,Dock.Left);targetRow.Children.Add(targetName);
            targetRow.Children.Add(MotionLook.Number(start,0.1,500,0.1,"MB",L.P("Size to stay under (MB)","목표 용량 (MB)"),v=>{},out target,out setTarget));
            page.Children.Add(targetRow);
            var qualityRow=new DockPanel {Margin=new Thickness(0,8,0,0)};
            var qualityName=UI.Text(L.P("Picture quality","화질"),13);qualityName.Width=120;DockPanel.SetDock(qualityName,Dock.Left);qualityRow.Children.Add(qualityName);
            qualityRow.Children.Add(MotionLook.Number(75,1,100,5,L.P("(1–100)","(1~100)"),L.P("Picture quality (1 to 100)","화질 (1~100)"),v=>{},out quality,out setQuality));
            page.Children.Add(qualityRow);
            Line(L.P("It makes the picture smaller, and fewer pictures a second if it still doesn't fit, until it fits.",
                "들어갈 때까지 크기를 줄이고, 그래도 크면 1초에 보이는 장 수를 줄여요."),12,true,10);
            var buttons=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,18,0,0)};
            var close=UI.Btn(L.T("Close"),Close);close.Margin=new Thickness(0,0,6,0);
            var go=UI.Btn(L.P("Make it smaller","줄이기"),()=>Run(target(),(int)quality()),true);go.IsDefault=true;go.MinWidth=96;
            buttons.Children.Add(close);buttons.Children.Add(go);page.Children.Add(buttons);
        }
        async void Run(double targetMb,int quality) {
            page.Children.Clear();
            var title=Line(L.P("Making it smaller…","줄이는 중이에요…"),16,false);title.FontWeight=FontWeights.SemiBold;
            var bar=new ProgressBar {Height=8,Margin=new Thickness(0,14,0,0),Maximum=1};page.Children.Add(bar);
            var stop=UI.Btn(L.T("Cancel"),()=>{if(work!=null)work.Cancel();});stop.HorizontalAlignment=HorizontalAlignment.Right;stop.Margin=new Thickness(0,16,0,0);page.Children.Add(stop);
            work=new CancellationTokenSource();var token=work.Token;
            string made=null;Exception failed=null;
            var progress=new Progress<double>(v=>bar.Value=v);
            try {made=await Task.Run(()=>MotionMaker.Shrink(path,targetMb,quality,token,p=>((IProgress<double>)progress).Report(p)),token);}
            catch(OperationCanceledException) {} catch(Exception ex) {failed=ex;}
            work=null;
            if(!IsVisible)return;
            page.Children.Clear();
            if(made==null) {
                var t=Line(failed==null?L.P("Stopped.","멈췄어요."):L.P("It couldn't be made smaller.","줄이지 못했어요."),16,false);t.FontWeight=FontWeights.SemiBold;
                if(failed!=null)Line(failed.Message,13,true,6);
            } else {
                long before=new FileInfo(path).Length,after=new FileInfo(made).Length;
                var t=Line(Mb(before)+" → "+Mb(after),18,false);t.FontWeight=FontWeights.SemiBold;
                Line(after<=targetMb*1048576?L.P("It fits. ✓","목표 안에 들어왔어요. ✓"):L.P("It's as small as it can go and still a little over. Try a lower quality, or cut it shorter in Make GIF cover.","이 이상 줄이면 알아보기 힘들어서 여기서 멈췄어요. 화질을 더 낮추거나, GIF 표지 만들기에서 짧게 잘라 보세요."),13,true,6);
                Line(Path.GetFileName(made),12,true,6);
                var open=UI.Btn(L.P("Show in folder","폴더 열기"),()=>{try {System.Diagnostics.Process.Start("explorer.exe","/select,\""+made+"\"");} catch(Exception) {}});
                open.HorizontalAlignment=HorizontalAlignment.Left;open.Margin=new Thickness(0,14,0,0);page.Children.Add(open);
                foreach(var gallery in Application.Current.Windows.OfType<GalleryWindow>())gallery.ReloadAfterChange();
            }
            var close=UI.Btn(L.T("Close"),Close,true);close.HorizontalAlignment=HorizontalAlignment.Right;close.Margin=new Thickness(0,16,0,0);page.Children.Add(close);
        }
    }
}
