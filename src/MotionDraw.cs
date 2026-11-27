using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LoveMyShelf {
    internal sealed class MotionShape {
        internal const int MaxShapes=50,MaxPoints=400,MaxMosaics=20;
        internal const int EraseStaticCSharp=24,EraseStaticAi=25,ErasePerFrame=26;
        internal const string Kinds="rbelaphmx";
        static readonly CultureInfo Invariant=CultureInfo.InvariantCulture;
        internal readonly char Kind;
        internal readonly int Color,Thickness,FillColor,Opacity,Block;
        internal bool Fill {get {return FillColor>=0;}}
        internal readonly Point[] Points;
        internal readonly MotionTrackPath Track;

        internal MotionShape(char kind,int color,int thickness,int fillColor,int opacity,int block,Point[] points,MotionTrackPath track=null) {
            Kind=kind;Color=color;Thickness=thickness;FillColor=CanFill(kind) || kind=='l'?fillColor:-1;
            Opacity=opacity;Block=block;Points=points;Track=HasCorners?track:null;
        }
        internal bool HasCorners {get {return Kind=='r' || Kind=='b' || Kind=='e' || Kind=='m';}}
        internal bool IsMosaic {get {return Kind=='m';}}
        internal bool IsErase {get {return Kind=='x';}}
        internal bool IsPerFrameErase {get {return Kind=='x' && Block==ErasePerFrame;}}
        internal bool IsAiErase {get {return Kind=='x' && Block==EraseStaticAi;}}
        internal int EraseSceneMs {get {return Kind=='x'?Color:0;}}
        internal static bool IsPen(char kind) {return kind=='p' || kind=='h' || kind=='x';}
        internal static bool CanFill(char kind) {return kind=='r' || kind=='b' || kind=='e' || kind=='a';}
        internal MotionShape WithPoints(Point[] points) {return new MotionShape(Kind,Color,Thickness,FillColor,Opacity,Block,points,Track);}
        internal MotionShape WithTrack(MotionTrackPath track) {return new MotionShape(Kind,Color,Thickness,FillColor,Opacity,Block,Points,track);}
        internal MotionShape WithEraseMode(int block,int? sceneMs=null) {return new MotionShape(Kind,sceneMs??Color,Thickness,FillColor,Opacity,block,Points,Track);}
        internal static string WithoutAiFlag(string value) {
            var list=Read(value);
            bool changed=false;
            for(int i=0;i<list.Count;i++)
                if(list[i].IsAiErase){list[i]=list[i].WithEraseMode(EraseStaticCSharp);changed=true;}
            return changed?Save(list):Normalize(value);
        }
        internal MotionShape At(double time) {return Track==null?this:WithBox(Track.At(time).Box);}
        internal Rect Box {get {return new Rect(Points[0],Points[1]);}}
        internal MotionShape WithBox(Rect box) {return WithPoints(new[]{box.TopLeft,box.BottomRight});}
        internal static double Clamp(double value) {return Math.Max(0,Math.Min(1,value));}
        internal static string Normalize(string value) {return Save(Read(value));}
        internal static List<MotionShape> Read(string value) {
            var shapes=new List<MotionShape>();
            if(String.IsNullOrEmpty(value))return shapes;
            var entries=value.Split('|');
            if(entries[0]!="v1")return shapes;
            int mosaics=0;
            for(int i=1;i<entries.Length && shapes.Count<MaxShapes;i++) {
                var fields=entries[i].Split(',');
                MotionTrackPath track=null;
                if(fields.Length==9 && fields[8].StartsWith("@",StringComparison.Ordinal)) {
                    track=MotionTrackPath.Read(fields[8].Substring(1));
                    Array.Resize(ref fields,8);
                }
                if(fields.Length<8 || fields.Length%2!=0 || fields[0].Length!=1)continue;
                char kind=fields[0][0];
                if(Kinds.IndexOf(kind)<0 || (!IsPen(kind) && fields.Length!=8))continue;
                int color,thickness;
                if(fields[1].Length!=6 || !Int32.TryParse(fields[1],NumberStyles.AllowHexSpecifier,Invariant,out color) ||
                    !Int32.TryParse(fields[2],NumberStyles.Integer,Invariant,out thickness) || thickness<1 || thickness>50)continue;
                var style=fields[3].Split(':');
                if((style.Length!=1 && style.Length!=4) || (style[0]!="0" && style[0]!="1"))continue;
                int fill=style[0]=="1"?color:-1,opacity=100,block=24;
                if(style.Length==4) {
                    if(style[1]!="-" && (style[1].Length!=6 || !Int32.TryParse(style[1],NumberStyles.AllowHexSpecifier,Invariant,out fill)))continue;
                    if(style[1]=="-")fill=-1;
                    if(!Int32.TryParse(style[2],NumberStyles.Integer,Invariant,out opacity) || opacity<10 || opacity>100 ||
                        !Int32.TryParse(style[3],NumberStyles.Integer,Invariant,out block) || block<1 || block>100)continue;
                }
                if(kind=='m' && mosaics==MaxMosaics)continue;
                int count=(fields.Length-4)/2;
                var points=new List<Point>(count);
                for(int j=4;j<fields.Length;j+=2) {
                    double x,y;
                    if(!Number(fields[j],out x) || !Number(fields[j+1],out y))break;
                    points.Add(new Point(Clamp(x),Clamp(y)));
                }
                if(points.Count!=count)continue;
                shapes.Add(new MotionShape(kind,color,thickness,fill,opacity,block,count>MaxPoints?Simplify(points):points.ToArray(),track));
                if(kind=='m')mosaics++;
            }
            return shapes;
        }
        internal static bool Number(string text,out double value) {
            return Double.TryParse(text,NumberStyles.Float,Invariant,out value) && !Double.IsNaN(value) && !Double.IsInfinity(value);
        }
        internal static string Save(List<MotionShape> shapes) {
            if(shapes.Count==0)return "";
            var text=new StringBuilder("v1");
            foreach(var shape in shapes) {
                text.Append('|').Append(shape.Kind).Append(',').Append(shape.Color.ToString("X6",Invariant))
                    .Append(',').Append(shape.Thickness.ToString(Invariant)).Append(shape.Fill?",1":",0");
                if((shape.Fill && shape.FillColor!=shape.Color) || shape.Opacity!=100 || shape.Block!=24)
                    text.Append(':').Append(shape.Fill?shape.FillColor.ToString("X6",Invariant):"-")
                        .Append(':').Append(shape.Opacity.ToString(Invariant)).Append(':').Append(shape.Block.ToString(Invariant));
                foreach(var point in shape.Points)
                    text.Append(',').Append(Clamp(point.X).ToString("0.####",Invariant))
                        .Append(',').Append(Clamp(point.Y).ToString("0.####",Invariant));
                if(shape.Track!=null)shape.Track.Write(text);
            }
            return text.ToString();
        }
        // Keep endpoints and the points that bend the stroke away from the straight segments.
        internal static Point[] Simplify(List<Point> points) {
            int last=points.Count-1;
            if(last<2)return points.ToArray();
            var keep=new bool[points.Count];keep[0]=keep[last]=true;
            var spans=new Stack<Tuple<int,int>>();spans.Push(Tuple.Create(0,last));
            while(spans.Count>0) {
                var span=spans.Pop();int first=span.Item1,end=span.Item2,split=-1;
                Point a=points[first];Vector axis=points[end]-a;
                double length=axis.LengthSquared,farthest=0.002*0.002;
                for(int i=first+1;i<end;i++) {
                    Vector delta=points[i]-a;
                    double t=length==0?0:Clamp((delta*axis)/length);
                    double distance=(delta-axis*t).LengthSquared;
                    if(distance>farthest){farthest=distance;split=i;}
                }
                if(split<0)continue;
                keep[split]=true;spans.Push(Tuple.Create(first,split));spans.Push(Tuple.Create(split,end));
            }
            var result=points.Where((point,i)=>keep[i]).ToArray();
            if(result.Length<=MaxPoints)return result;
            var capped=new Point[MaxPoints];
            for(int i=0;i<capped.Length;i++)capped[i]=result[i*(result.Length-1)/(capped.Length-1)];
            return capped;
        }
    }

    internal static class MotionDraw {
        internal static Point At(Point p,double width,double height) {return new Point(p.X*width,p.Y*height);}
        internal static double Pixels(MotionShape shape,double height) {return Math.Max(1.5,shape.Thickness*height/1000);}
        internal static Pen Stroke(Brush brush,double thickness) {
            return new Pen(brush,thickness) {StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round,LineJoin=PenLineJoin.Round};
        }
        internal static Geometry GeometryOf(MotionShape shape,double width,double height,double px) {
            Point a=At(shape.Points[0],width,height),b=At(shape.Points[1],width,height);
            if(shape.Kind=='r' || shape.Kind=='b' || shape.IsMosaic) {
                var bounds=new Rect(a,b);
                double corner=shape.Kind=='b'?Math.Min(bounds.Width,bounds.Height)*0.18:0;
                return new RectangleGeometry(bounds,corner,corner);
            }
            if(shape.Kind=='e')return new EllipseGeometry(new Rect(a,b));
            if(shape.Kind=='l')return new LineGeometry(a,b);
            var geometry=new StreamGeometry();
            using(var path=geometry.Open()) {
                if(MotionShape.IsPen(shape.Kind)) {
                    path.BeginFigure(a,false,false);
                    if(shape.Points.Length==2 && (b-a).LengthSquared<1e-8)
                        path.LineTo(new Point(a.X+0.05,a.Y),true,false);
                    else for(int i=1;i<shape.Points.Length;i++)path.LineTo(At(shape.Points[i],width,height),true,false);
                } else {
                    Vector axis=b-a;
                    double length=axis.Length;
                    if(length<0.001)return new LineGeometry(a,b);
                    axis/=length;
                    double head=Math.Min(length,Math.Max(8,3.5*px));
                    Point back=b-axis*head;
                    Vector wing=new Vector(-axis.Y,axis.X)*(head*0.45);
                    path.BeginFigure(a,false,false);path.LineTo(shape.Fill?back:b,true,false);
                    path.BeginFigure(back+wing,shape.Fill,shape.Fill);
                    path.LineTo(b,true,false);path.LineTo(back-wing,true,false);
                }
            }
            return geometry;
        }
        // Both callers draw in output pixels. The preview scales the drawing context to the picture on screen.
        internal static void Render(DrawingContext dc,MotionShape shape,double width,double height) {
            if(shape.IsMosaic || shape.IsErase)return;
            double px=Pixels(shape,height);
            var brush=new SolidColorBrush(TextLook.ColorOf(shape.Color));
            dc.PushOpacity(shape.Opacity/100.0);
            dc.DrawGeometry(shape.Fill?new SolidColorBrush(TextLook.ColorOf(shape.FillColor)):null,
                Stroke(brush,px),GeometryOf(shape,width,height,px));
            dc.Pop();
        }

        internal static Int32Rect MosaicBounds(MotionShape shape,int width,int height) {
            if(width<4 || height<4)return Int32Rect.Empty;
            var rect=new Rect(At(shape.Points[0],width,height),At(shape.Points[1],width,height));
            int x=Math.Min((width-4)&~1,(int)rect.Left&~1),y=Math.Min((height-4)&~1,(int)rect.Top&~1);
            int right=Math.Max(x+4,Math.Min(width&~1,(int)Math.Ceiling(rect.Right/2)*2));
            int bottom=Math.Max(y+4,Math.Min(height&~1,(int)Math.Ceiling(rect.Bottom/2)*2));
            return new Int32Rect(x,y,right-x,bottom-y);
        }

        internal static int MosaicBlock(MotionShape shape,int height) {
            return Math.Max(1,(int)Math.Round(shape.Block*height/1000.0));
        }

        internal static string AppendMosaics(StringBuilder graph,string current,string value,int width,int height,bool keepAlpha) {
            return AppendMosaics(graph,current,MotionShape.Read(value),width,height,keepAlpha,0);
        }
        internal static string AppendMosaics(StringBuilder graph,string current,IEnumerable<MotionShape> shapes,int width,int height,bool keepAlpha,int number) {
            foreach(var shape in shapes.Where(shape=>shape.IsMosaic)) {
                var box=MosaicBounds(shape,width,height);
                if(box.IsEmpty)continue;
                string id=(++number).ToString(CultureInfo.InvariantCulture);
                int block=MosaicBlock(shape,height);
                graph.AppendFormat(CultureInfo.InvariantCulture,
                    @";{0}split[base{1}][box{1}];[box{1}]crop={2}:{3}:{4}:{5},scale=max(1\,trunc(iw/{6})):max(1\,trunc(ih/{6})),scale={2}:{3}:flags=neighbor[tile{1}];[base{1}][tile{1}]overlay={4}:{5}:format={7}[mosaic{1}]",
                    current,id,box.Width,box.Height,box.X,box.Y,block,keepAlpha?"rgb":"auto");
                current="[mosaic"+id+"]";
            }
            return current;
        }

        internal static BitmapSource Apply(BitmapSource picture,string value,BitmapSource aiErasePatch=null) {
            return Apply(picture,MotionShape.Read(value),aiErasePatch);
        }
        internal static BitmapSource Apply(BitmapSource picture,IList<MotionShape> shapes,BitmapSource aiErasePatch=null) {
            if(picture==null || shapes==null || shapes.Count==0)return picture;
            if(shapes.Any(shape=>shape.IsErase))
                picture=aiErasePatch!=null?EraseStore.Composite(picture,aiErasePatch):CoverInpaint.ApplyHybrid(picture,shapes);
            picture=ApplyMosaics(picture,shapes);
            if(!shapes.Any(shape=>!shape.IsMosaic && !shape.IsErase))return picture;
            int w=picture.PixelWidth,h=picture.PixelHeight;
            var visual=new DrawingVisual();
            using(var dc=visual.RenderOpen()) {
                dc.DrawImage(picture,new Rect(0,0,w,h));
                foreach(var shape in shapes)Render(dc,shape,w,h);
            }
            var result=new RenderTargetBitmap(w,h,96,96,PixelFormats.Pbgra32);
            result.Render(visual);result.Freeze();
            return result;
        }


        // Sample each box into a small grid, then repeat those samples at the box's full size.
        // Working on one WPF pixel buffer lets overlapping boxes sample the earlier boxes.
        internal static BitmapSource ApplyMosaics(BitmapSource picture,IEnumerable<MotionShape> shapes) {
            int w=picture.PixelWidth,h=picture.PixelHeight;
            if(w<4 || h<4)return picture;
            int[] pixels=null;
            foreach(var shape in shapes.Where(shape=>shape.IsMosaic)) {
                var box=MosaicBounds(shape,w,h);
                int block=MosaicBlock(shape,h);
                if(block==1)continue;
                if(pixels==null) {
                    var source=picture.Format==PixelFormats.Pbgra32?picture:new FormatConvertedBitmap(picture,PixelFormats.Pbgra32,null,0);
                    pixels=new int[checked(w*h)];
                    source.CopyPixels(pixels,checked(w*4),0);
                }
                int smallW=Math.Max(1,box.Width/block),smallH=Math.Max(1,box.Height/block);
                var small=new int[smallW*smallH];
                for(int y=0;y<smallH;y++) {
                    int row=(box.Y+Nearest(y,box.Height,smallH))*w+box.X;
                    for(int x=0;x<smallW;x++)small[y*smallW+x]=pixels[row+Nearest(x,box.Width,smallW)];
                }
                var columns=new int[box.Width];
                for(int x=0;x<columns.Length;x++)columns[x]=Nearest(x,smallW,box.Width);
                for(int y=0;y<box.Height;y++) {
                    int row=(box.Y+y)*w+box.X,smallRow=Nearest(y,smallH,box.Height)*smallW;
                    for(int x=0;x<columns.Length;x++)pixels[row+x]=small[smallRow+columns[x]];
                }
            }
            if(pixels==null)return picture;
            var bitmap=BitmapSource.Create(w,h,96,96,PixelFormats.Pbgra32,null,pixels,checked(w*4));
            bitmap.Freeze();
            return bitmap;
        }

        static int Nearest(int index,int sourceSize,int targetSize) {
            return (int)((2L*index+1)*sourceSize/(2L*targetSize));
        }

        internal static void MosaicPlaceholder(DrawingContext dc,Rect rect) {
            if(rect.Width<=0 || rect.Height<=0)return;
            dc.PushClip(new RectangleGeometry(rect));
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(150,0,0,0)),null,rect);
            var stripe=new Pen(new SolidColorBrush(Color.FromArgb(150,255,255,255)),1);
            for(double x=rect.Left-rect.Height;x<rect.Right;x+=8)
                dc.DrawLine(stripe,new Point(x,rect.Bottom),new Point(x+rect.Height,rect.Top));
            var label=new FormattedText(L.P("Mosaic","모자이크"),CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,new Typeface("Segoe UI"),12,Brushes.White,1);
            dc.DrawRectangle(Brushes.Black,null,new Rect(rect.X+3,rect.Y+3,label.Width+6,label.Height+2));
            dc.DrawText(label,new Point(rect.X+6,rect.Y+4));
            dc.Pop();
        }

        internal static string WritePng(string value,int width,int height,string folder) {
            var shapes=MotionShape.Read(value).Where(shape=>!shape.IsMosaic && !shape.IsErase).ToList();
            if(shapes.Count==0)return null;
            return WritePng(shapes,width,height,folder,"shapes.png",width,height);
        }

        internal static string WritePng(IEnumerable<MotionShape> shapes,int width,int height,string folder,string name,
            int bitmapWidth,int bitmapHeight) {
            string path=Path.Combine(folder,name);
            Action write=()=>{
                Directory.CreateDirectory(folder);
                var visual=new DrawingVisual();
                using(var dc=visual.RenderOpen())
                    foreach(var shape in shapes)Render(dc,shape,width,height);
                var bitmap=new RenderTargetBitmap(bitmapWidth,bitmapHeight,96,96,PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var straight=new FormatConvertedBitmap(bitmap,PixelFormats.Bgra32,null,0);
                var png=new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(straight));
                using(var stream=File.Create(path))png.Save(stream);
            };
            RunSta(write);
            return path;
        }
        internal static void RunSta(Action work) {
            if(Thread.CurrentThread.GetApartmentState()==ApartmentState.STA){work();return;}
            ExceptionDispatchInfo failure=null;
            var thread=new Thread(()=>{
                try {work();} catch(Exception ex) {failure=ExceptionDispatchInfo.Capture(ex);}
            }) {IsBackground=true};
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();thread.Join();
            if(failure!=null)failure.Throw();
        }
    }

    // The image and both layers share the same centered rectangle inside the preview frame.
    internal sealed class MotionPicture : Panel {
        readonly Image picture;
        internal MotionPicture(Image picture,FrameworkElement drawing,FrameworkElement text) {
            this.picture=picture;
            Children.Add(picture);Children.Add(drawing);Children.Add(text);
        }
        protected override Size MeasureOverride(Size available) {
            foreach(UIElement child in Children)child.Measure(available);
            return new Size();
        }
        protected override Size ArrangeOverride(Size size) {
            double width=size.Width,height=size.Height;
            var source=picture.Source;
            if(source!=null && source.Width>0 && source.Height>0) {
                double scale=Math.Min(width/source.Width,height/source.Height);
                width=source.Width*scale;height=source.Height*scale;
            }
            var rect=new Rect((size.Width-width)/2,(size.Height-height)/2,width,height);
            foreach(UIElement child in Children)child.Arrange(rect);
            return size;
        }
    }


    // Like TextStylePanel, hold the last choice in memory across windows. Save it as well for the next run.
    internal sealed class DrawPreferences {
        static readonly string PathName=Path.Combine(ErrorLog.Folder,"draw-style.txt");
        internal static readonly DrawPreferences Last=Load();
        internal bool Open;
        internal char Tool='r';
        internal int Color=0xFFFFFF,Fill=-1,Thickness=6,Opacity=100,Block=24,MarkerThickness=20,MarkerOpacity=40,EraseThickness=24;
        static DrawPreferences Load() {
            var result=new DrawPreferences();
            try {
                if(!File.Exists(PathName))return result;
                var parts=File.ReadAllText(PathName).Split('|');
                if((parts.Length!=9 && parts.Length!=10) || parts[1].Length!=1 || MotionShape.Kinds.IndexOf(parts[1][0])<0)return result;
                int[] values=new int[parts.Length-2];
                for(int i=0;i<values.Length;i++)
                    if(!Int32.TryParse(parts[i+2],NumberStyles.Integer,CultureInfo.InvariantCulture,out values[i]))return result;
                result.Open=parts[0]=="1";result.Tool=parts[1][0];
                result.Color=Limit(values[0],0,0xFFFFFF);result.Fill=Limit(values[1],-1,0xFFFFFF);
                result.Thickness=Limit(values[2],1,50);result.Opacity=Limit(values[3],10,100);result.Block=Limit(values[4],1,100);
                result.MarkerThickness=Limit(values[5],1,50);result.MarkerOpacity=Limit(values[6],10,100);
                if(values.Length>=8)result.EraseThickness=Limit(values[7],1,50);
            } catch(IOException ex) {ErrorLog.Quiet(ex);}
            catch(UnauthorizedAccessException ex) {ErrorLog.Quiet(ex);}
            return result;
        }
        static int Limit(int value,int low,int high) {return Math.Max(low,Math.Min(high,value));}
        internal void Save() {
            try {
                Directory.CreateDirectory(ErrorLog.Folder);
                File.WriteAllText(PathName,String.Format(CultureInfo.InvariantCulture,
                    "{0}|{1}|{2}|{3}|{4}|{5}|{6}|{7}|{8}|{9}",Open?1:0,Tool,Color,Fill,Thickness,Opacity,Block,MarkerThickness,MarkerOpacity,EraseThickness));
            } catch(IOException ex) {ErrorLog.Quiet(ex);}
            catch(UnauthorizedAccessException ex) {ErrorLog.Quiet(ex);}
        }
    }

    internal sealed class MotionDrawPanel : StackPanel {
        readonly MotionDrawLayer layer;
        readonly Button opener,undo,redo,delete,front,back,clear,none,aiButton;
        readonly StackPanel body=new StackPanel {Margin=new Thickness(0,6,0,4)};
        readonly StackPanel aiBusyPanel;
        readonly Button[] normalThicknessPresets,eraseThicknessPresets;
        readonly RadioButton[] tools=new RadioButton[MotionShape.Kinds.Length];
        readonly FrameworkElement strokeField,fillField,thicknessField,opacityField,blockField,gifEraseField;
        readonly RadioButton gifStaticChoice,gifPerFrameChoice;
        readonly TextBlock gifPerFrameNote;
        readonly Action<int> showColor,showFill;
        readonly Action<double> showThickness,showOpacity,showBlock;
        readonly bool isGif;
        bool syncing,aiRunning;
        internal Func<Task> OnRunAiErase;
        internal Action OnCancelAiErase;
        internal event Action OpenChanged,VisibilityChanged;
        internal bool IsOpen {get {return body.Visibility==Visibility.Visible;}}
        internal bool IsEditing {get {return body.IsVisible;}}
        internal double ControlsHeight {set {scroller.MaxHeight=Math.Max(0,value);}}
        // The [Draw ▾] button, which a window may take out and put beside the [Adjust ▾] button.
        internal Button Opener {get {return opener;}}
        readonly ScrollViewer scroller;

        internal MotionDrawPanel(MotionDrawLayer layer,FrameworkElement tracking=null) {
            this.layer=layer;
            isGif=tracking!=null;
            opener=UI.Btn("",Toggle);
            opener.HorizontalAlignment=HorizontalAlignment.Left;opener.Margin=new Thickness(0,2,0,2);
            AutomationProperties.SetName(opener,L.P("Draw","그림 그리기"));
            scroller=new ScrollViewer {Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,Focusable=false};
            Children.Add(opener);Children.Add(scroller);
            var toolRow=new WrapPanel();
            string[] names={L.P("Rectangle","네모"),L.P("Rounded rectangle","둥근 네모"),L.P("Ellipse","동그라미"),
                L.P("Line","직선"),L.P("Arrow","화살표"),L.P("Pen","펜"),L.P("Highlighter","형광펜"),L.P("Mosaic box","모자이크"),
                L.P("Erase","지우기")};
            string group="draw-"+Guid.NewGuid().ToString("N");
            for(int i=0;i<names.Length;i++) {
                char kind=MotionShape.Kinds[i];
                string label=(i+1).ToString(CultureInfo.InvariantCulture)+"  "+names[i];
                var tool=new RadioButton {Content=label,ToolTip=kind=='x'?
                    L.P("9  Erase: paint over unwanted things in the picture, such as date letters or passersby","9  지우기: 날짜 글씨나 지나가는 사람처럼 사진에 있는 필요 없는 것을 칠해서 지워요"):label,
                    GroupName=group,Style=(Style)Application.Current.FindResource("SegmentChoice")};
                tool.Click+=delegate {layer.SelectTool(kind);};
                tools[i]=tool;toolRow.Children.Add(tool);
            }
            body.Children.Add(toolRow);
            string stroke=L.P("Stroke color","선 색"),fill=L.P("Fill color","채우기 색");
            strokeField=Field(stroke,TextStylePanel.Colors(stroke,()=>layer.Color,v=>layer.SetStyle(rgb:v),out showColor));
            var fills=(Panel)TextStylePanel.Colors(fill,()=>layer.FillColor,v=>layer.SetStyle(fill:v),out showFill);
            none=UI.Btn(L.P("None","없음"),()=>layer.SetStyle(fill:-1));
            none.Margin=new Thickness(0,0,5,0);none.MinHeight=24;fills.Children.Insert(0,none);
            fillField=Field(fill,fills);

            Func<double> readThick;
            var thicknessRow=new WrapPanel {VerticalAlignment=VerticalAlignment.Center};
            thicknessRow.Children.Add(MotionLook.Number(layer.Thickness,1,50,1,"",L.P("Thickness","굵기"),
                v=>{if(!syncing)layer.SetStyle(width:(int)v);},out readThick,out showThickness));
            normalThicknessPresets=MakePresetButtons(thicknessRow,L.P("Thickness","굵기"),"",new[]{3,6,12},n=>layer.SetStyle(width:n));
            eraseThicknessPresets=MakePresetButtons(thicknessRow,L.P("Thickness","굵기"),"",new[]{12,24,48},n=>layer.SetStyle(width:n));
            aiButton=UI.Btn(L.P("Fill with AI","AI로 메우기"),RunAiClicked,true);
            aiButton.Margin=new Thickness(8,0,0,2);
            aiButton.ToolTip=L.P("Fills the painted spots again with the AI on this PC. It takes about 2-3 seconds. Press Ctrl+Z once to undo.",
                "칠한 곳을 내 PC 안의 AI로 다시 메워요. 2~3초쯤 걸려요. Ctrl+Z를 한 번 누르면 AI 적용 전으로 돌아가요.");
            thicknessRow.Children.Add(aiButton);
            aiBusyPanel=new StackPanel {Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,0,0,2),Visibility=Visibility.Collapsed};
            var busyText=UI.Text(L.P("Refining…","다듬는 중…"),12,true);busyText.Margin=new Thickness(0,0,6,0);
            var cancelAi=UI.Btn(L.T("Cancel"),()=>{if(OnCancelAiErase!=null)OnCancelAiErase();});
            cancelAi.MinHeight=25;cancelAi.Padding=new Thickness(8,1,8,1);
            aiBusyPanel.Children.Add(busyText);aiBusyPanel.Children.Add(cancelAi);
            thicknessRow.Children.Add(aiBusyPanel);
            thicknessField=Field(L.P("Thickness","굵기"),thicknessRow);

            opacityField=Field(L.P("Opacity","투명도"),Number(layer.ShapeOpacity,10,100,"%",L.P("Opacity","투명도"),
                v=>layer.SetStyle(opacity:v),new[]{40,70,100},out showOpacity));
            string block=L.P("Block size","칸 크기");
            blockField=Field(block,Number(layer.Block,1,100,"px",block,v=>layer.SetStyle(block:v),new[]{12,24,48},out showBlock));
            blockField.ToolTip=L.P("Pixels when the cover is 1000 px tall.","표지 높이가 1000px일 때의 칸 크기예요.");
            foreach(var field in new[]{strokeField,fillField,thicknessField,opacityField,blockField})body.Children.Add(field);

            if(isGif) {
                var gifEraseBox=new StackPanel();
                var modeRow=new WrapPanel {VerticalAlignment=VerticalAlignment.Center};
                string eraseGroup="gif-erase-"+Guid.NewGuid().ToString("N");
                gifStaticChoice=new RadioButton {Content=L.P("Cover with this scene's patch","이 장면 조각으로 덮기"),GroupName=eraseGroup,
                    Style=(Style)Application.Current.FindResource("SegmentChoice"),IsChecked=true};
                gifPerFrameChoice=new RadioButton {Content=L.P("Fill each scene separately","장면마다 따로 메우기"),GroupName=eraseGroup,
                    Style=(Style)Application.Current.FindResource("SegmentChoice")};
                gifStaticChoice.Click+=delegate {if(!syncing)layer.SetEraseMode(0);};
                gifPerFrameChoice.Click+=delegate {if(!syncing)layer.SetEraseMode(1);};
                modeRow.Children.Add(gifStaticChoice);modeRow.Children.Add(gifPerFrameChoice);
                gifEraseBox.Children.Add(modeRow);
                gifPerFrameNote=UI.Text(L.P("Moving backgrounds are filled quickly scene by scene","움직이는 배경은 장면마다 빠르게 메워요"),12,true);
                gifPerFrameNote.TextWrapping=TextWrapping.Wrap;gifPerFrameNote.Margin=new Thickness(1,3,0,0);
                gifEraseBox.Children.Add(gifPerFrameNote);
                gifEraseField=Field(L.P("Fill mode","메우기"),gifEraseBox);
                body.Children.Add(gifEraseField);
            }

            var actions=new WrapPanel {Margin=new Thickness(0,5,0,4)};
            delete=UI.Btn(L.P("Delete","지우기"),layer.DeleteSelected);
            front=UI.Btn(L.P("Bring to front","맨 앞으로"),()=>layer.Reorder(true));
            back=UI.Btn(L.P("Send to back","맨 뒤로"),()=>layer.Reorder(false));
            undo=UI.Btn(L.P("Undo Ctrl+Z","되돌리기 Ctrl+Z"),layer.Undo);
            redo=UI.Btn(L.P("Redo Ctrl+Y","다시 하기 Ctrl+Y"),layer.Redo);
            clear=UI.Btn(L.P("Clear all","모두 지우기"),layer.Clear);
            foreach(var button in new[]{delete,front,back,undo,redo,clear}) {
                button.Margin=new Thickness(0,0,5,4);actions.Children.Add(button);
            }
            body.Children.Add(actions);
            if(tracking!=null)body.Children.Add(tracking);
            body.IsVisibleChanged+=delegate {layer.SetActive(IsEditing);if(VisibilityChanged!=null)VisibilityChanged();};
            layer.Updated+=Sync;
            ShowOpener(DrawPreferences.Last.Open);Sync();
        }
        static Button[] MakePresetButtons(Panel row,string name,string unit,int[] presets,Action<int> change) {
            var made=new Button[presets.Length];
            for(int i=0;i<presets.Length;i++) {
                int number=presets[i];
                var button=UI.Btn(number.ToString(CultureInfo.InvariantCulture),()=>change(number));
                button.Margin=new Thickness(4,0,0,2);button.MinWidth=30;
                AutomationProperties.SetName(button,name+" "+number+unit);
                made[i]=button;row.Children.Add(button);
            }
            return made;
        }
        async void RunAiClicked() {
            if(aiRunning || OnRunAiErase==null)return;
            await OnRunAiErase();
        }
        internal void SetAiRunning(bool running) {
            if(aiRunning==running)return;
            aiRunning=running;Sync();
        }
        internal void SetOpen(bool open) {
            if(IsOpen==open)return;
            DrawPreferences.Last.Open=open;DrawPreferences.Last.Save();
            ShowOpener(open);
            if(OpenChanged!=null)OpenChanged();
        }
        FrameworkElement Number(int value,int min,int max,string unit,string name,Action<int> change,int[] presets,out Action<double> show) {
            Func<double> read;
            var row=new WrapPanel();
            row.Children.Add(MotionLook.Number(value,min,max,1,unit,name,v=>{if(!syncing)change((int)v);},out read,out show));
            MakePresetButtons(row,name,unit,presets,change);
            return row;
        }
        static FrameworkElement Field(string label,FrameworkElement control) {
            var row=new DockPanel {Margin=new Thickness(0,4,0,0)};
            var name=UI.Text(label,12);name.Width=72;name.TextWrapping=TextWrapping.Wrap;name.VerticalAlignment=VerticalAlignment.Center;
            row.Children.Add(name);row.Children.Add(control);
            return row;
        }
        void Toggle() { SetOpen(!IsOpen); }
        void ShowOpener(bool open) {
            opener.Content=L.P("Draw","그림 그리기")+(open?"  ▴":"  ▾");
            body.Visibility=scroller.Visibility=open?Visibility.Visible:Visibility.Collapsed;
        }
        void Sync() {
            syncing=true;
            for(int i=0;i<tools.Length;i++)tools[i].IsChecked=MotionShape.Kinds[i]==layer.Tool;
            showColor(layer.Color);showFill(layer.FillColor);showThickness(layer.Thickness);showOpacity(layer.ShapeOpacity);showBlock(layer.Block);
            none.FontWeight=layer.FillColor<0?FontWeights.Bold:FontWeights.Normal;
            bool erase=layer.IsErase;
            strokeField.Visibility=opacityField.Visibility=(layer.IsMosaic || erase)?Visibility.Collapsed:Visibility.Visible;
            thicknessField.Visibility=layer.IsMosaic?Visibility.Collapsed:Visibility.Visible;
            fillField.Visibility=layer.CanFill?Visibility.Visible:Visibility.Collapsed;
            blockField.Visibility=layer.IsMosaic?Visibility.Visible:Visibility.Collapsed;
            foreach(var b in normalThicknessPresets)b.Visibility=erase?Visibility.Collapsed:Visibility.Visible;
            foreach(var b in eraseThicknessPresets)b.Visibility=erase?Visibility.Visible:Visibility.Collapsed;
            bool showAi=(erase || layer.HasEraseShapes) && layer.HasEraseShapes && !layer.IsMosaic && EraseEngine.Available;
            if(!erase)showAi=false;
            aiBusyPanel.Visibility=showAi && aiRunning?Visibility.Visible:Visibility.Collapsed;
            aiButton.Visibility=showAi && !aiRunning?Visibility.Visible:Visibility.Collapsed;
            aiButton.IsEnabled=isGif?layer.EraseMode==0:layer.AiPatch==null;
            if(gifEraseField!=null) {
                gifEraseField.Visibility=(erase || layer.HasEraseShapes)?Visibility.Visible:Visibility.Collapsed;
                gifStaticChoice.IsChecked=layer.EraseMode==0;
                gifPerFrameChoice.IsChecked=layer.EraseMode==1;
                gifPerFrameNote.Visibility=layer.EraseMode==1?Visibility.Visible:Visibility.Collapsed;
            }
            undo.IsEnabled=layer.CanUndo;redo.IsEnabled=layer.CanRedo;delete.IsEnabled=layer.HasSelection;
            front.IsEnabled=layer.CanReorder(true);back.IsEnabled=layer.CanReorder(false);clear.IsEnabled=layer.HasShapes;
            syncing=false;
        }
        internal bool HandleKey(KeyEventArgs e,bool allowX) {
            if(e.Key==Key.Escape && layer.CancelDrag())return true;
            if(!IsEditing || Keyboard.FocusedElement is TextBox)return false;
            Key key=e.Key==Key.ImeProcessed?e.ImeProcessedKey:e.Key;
            if(Keyboard.Modifiers==ModifierKeys.Control) {
                if(key==Key.Z){layer.Undo();return true;}
                if(key==Key.Y){layer.Redo();return true;}
            }
            if(Keyboard.Modifiers!=ModifierKeys.None)return false;
            int index=key>=Key.D1 && key<=Key.D9?key-Key.D1:key>=Key.NumPad1 && key<=Key.NumPad9?key-Key.NumPad1:-1;
            if(index>=0){if(!e.IsRepeat)layer.SelectTool(MotionShape.Kinds[index]);return true;}
            if((key==Key.Delete || (allowX && key==Key.X)) && layer.HasSelection) {
                if(!e.IsRepeat)layer.DeleteSelected();
                return true;
            }
            return false;
        }
    }

    internal sealed class MotionDrawLayer : FrameworkElement {
        const double DragDistance=4,HandleRadius=4;
        const int MaxUndo=50;
        sealed class Edit {
            internal readonly List<MotionShape> Shapes;
            internal readonly int Selected;
            internal readonly EraseAiPatch AiPatch;
            internal readonly int EraseMode;
            internal Edit(List<MotionShape> shapes,int selected,EraseAiPatch aiPatch,int eraseMode) {
                Shapes=new List<MotionShape>(shapes);Selected=selected;AiPatch=aiPatch;EraseMode=eraseMode;
            }
        }
        readonly List<Edit> undo=new List<Edit>(),redo=new List<Edit>();
        readonly List<Point> points=new List<Point>();
        List<MotionShape> shapes=new List<MotionShape>();
        EraseAiPatch aiPatch;
        int eraseMode;
        int selected=-1,handle=-1;
        bool active,onlySelection,dragging,drawing;
        double pixelHeight=1000;
        Point start,anchor;
        MotionShape pending,dragShape;
        double sceneTime;
        int trackCount;
        internal Func<double> FixedSceneTime;
        internal Func<BitmapSource> ErasePicture;
        internal Func<IList<MotionShape>,BitmapSource> StaticErasePatch;
        internal Func<int> CurrentEraseSceneMs;
        internal event Action BeforePointer,PointerStarted,TrackLimit,EraseStrokesChanged;
        internal MotionShape SelectedShape {get {return HasSelection?shapes[selected]:null;}}
        internal EraseAiPatch AiPatch {get {return aiPatch;}}
        internal int EraseMode {get {return eraseMode;}}
        internal IList<MotionShape> Shapes {get {return shapes;}}
        internal bool HasEraseShapes {get {return shapes.Any(shape=>shape.IsErase);}}
        internal bool HasTracks {get {return trackCount!=0;}}
        internal IEnumerable<MotionTrackPath> Tracks {get {return shapes.Where(shape=>shape.Track!=null).Select(shape=>shape.Track);}}

        internal void SetSceneTime(double time) {
            if(sceneTime==time)return;
            sceneTime=time;
            if(HasTracks)InvalidateVisual();
        }
        internal bool ReplaceShape(MotionShape old,MotionShape replacement) {
            int index=shapes.IndexOf(old);
            if(index<0)return false;
            CancelDrag();Remember();shapes[index]=replacement;SaveChange();
            return true;
        }
        internal void TransformTracks(Func<MotionTrackPath,MotionTrackPath> transform) {
            TransformTracks(shapes,transform);
            foreach(var edit in undo.Concat(redo))TransformTracks(edit.Shapes,transform);
            SaveValue();Update();
        }
        static void TransformTracks(List<MotionShape> list,Func<MotionTrackPath,MotionTrackPath> transform) {
            for(int i=0;i<list.Count;i++)if(list[i].Track!=null)list[i]=list[i].WithTrack(transform(list[i].Track));
        }
        internal char Tool {get;private set;}
        internal Func<BitmapSource> MosaicPicture;
        char Kind {get {return HasSelection?shapes[selected].Kind:Tool;}}
        DrawPreferences Defaults {get {return DrawPreferences.Last;}}
        int DefaultThickness {get {return Tool=='h'?Defaults.MarkerThickness:Tool=='x'?Defaults.EraseThickness:Defaults.Thickness;}}
        int DefaultOpacity {get {return Tool=='h'?Defaults.MarkerOpacity:Defaults.Opacity;}}
        internal string Value {get;private set;}
        internal bool IsDragging {get {return dragging;}}
        internal bool HasSelection {get {return selected>=0;}}
        internal bool HasShapes {get {return shapes.Count>0;}}
        internal bool CanUndo {get {return undo.Count>0;}}
        internal bool CanRedo {get {return redo.Count>0;}}
        internal int Color {get {return HasSelection?shapes[selected].Color:Defaults.Color;}}
        internal int Thickness {get {return HasSelection?shapes[selected].Thickness:DefaultThickness;}}
        internal int FillColor {get {return HasSelection?shapes[selected].FillColor:Defaults.Fill;}}
        internal int ShapeOpacity {get {return HasSelection?shapes[selected].Opacity:DefaultOpacity;}}
        internal int Block {get {return HasSelection?shapes[selected].Block:Defaults.Block;}}
        internal bool CanFill {get {return MotionShape.CanFill(Kind);}}
        internal bool IsMosaic {get {return Kind=='m';}}
        internal bool IsErase {get {return Kind=='x';}}
        internal event Action Updated,Changed,Editing;

        internal MotionDrawLayer() {
            Value="";Tool=Defaults.Tool;
            Cursor=Cursors.Cross;Focusable=true;IsHitTestVisible=false;ClipToBounds=true;
            KeyboardNavigation.SetIsTabStop(this,false);
        }
        internal void Load(string value,EraseAiPatch initialAi=null) {
            CancelDrag();selected=-1;undo.Clear();redo.Clear();
            shapes=MotionShape.Read(value);
            eraseMode=shapes.Any(shape=>shape.IsPerFrameErase)?1:0;
            aiPatch=initialAi;
            if(aiPatch==null) {
                for(int i=0;i<shapes.Count;i++)
                    if(shapes[i].IsAiErase)shapes[i]=shapes[i].WithEraseMode(MotionShape.EraseStaticCSharp);
            }
            SaveValue();Update();
        }
        internal void SetInitialAiPatch(EraseAiPatch patch) {
            aiPatch=patch;
            if(patch==null) {
                for(int i=0;i<shapes.Count;i++)
                    if(shapes[i].IsAiErase)shapes[i]=shapes[i].WithEraseMode(MotionShape.EraseStaticCSharp);
                SaveValue();
            }
            Update();
        }
        internal void ApplyAiPatch(EraseAiPatch patch) {
            if(patch==null || !HasEraseShapes)return;
            if(aiPatch!=null && !String.IsNullOrEmpty(patch.CacheKey) && aiPatch.CacheKey==patch.CacheKey)return;
            CancelDrag();Remember();
            eraseMode=0;
            int sceneMs=(int)Math.Max(0,Math.Min(0xFFFFFF,Math.Round(patch.SceneMs)));
            for(int i=0;i<shapes.Count;i++)
                if(shapes[i].IsErase)shapes[i]=shapes[i].WithEraseMode(MotionShape.EraseStaticAi,sceneMs);
            aiPatch=patch;
            SaveChange(true);
        }
        internal void InvalidateAiPatch() {
            if(aiPatch==null && !shapes.Any(s=>s.IsAiErase))return;
            aiPatch=null;
            for(int i=0;i<shapes.Count;i++)
                if(shapes[i].IsAiErase)shapes[i]=shapes[i].WithEraseMode(MotionShape.EraseStaticCSharp);
            SaveValue();Update();
        }
        internal void SetEraseMode(int mode) {
            mode=mode==1?1:0;
            if(eraseMode==mode)return;
            CancelDrag();
            if(HasEraseShapes) {
                Remember();
                eraseMode=mode;
                aiPatch=null;
                int block=mode==1?MotionShape.ErasePerFrame:MotionShape.EraseStaticCSharp;
                int sceneMs=mode==0 && CurrentEraseSceneMs!=null?CurrentEraseSceneMs():0;
                for(int i=0;i<shapes.Count;i++)
                    if(shapes[i].IsErase)shapes[i]=shapes[i].WithEraseMode(block,sceneMs);
                SaveChange();
            } else {
                eraseMode=mode;Update();
            }
        }
        internal void SelectTool(char kind) {
            CancelDrag();selected=-1;Tool=Defaults.Tool=kind;Defaults.Save();Update();
        }
        internal void SetActive(bool value) {
            active=value;IsHitTestVisible=value;
            if(!active)CancelDrag();
            InvalidateVisual();
        }
        internal void SetLook(bool showingOwn,double height) {
            if(onlySelection==showingOwn && pixelHeight==height)return;
            onlySelection=showingOwn;pixelHeight=height;
            InvalidateVisual();
        }
        void Update() {
            InvalidateVisual();
            if(Updated!=null)Updated();
        }
        void SaveValue() {
            Value=MotionShape.Save(shapes);
            trackCount=shapes.Count(shape=>shape.Track!=null);
        }
        void SaveChange(bool keepAi=false) {
            if(!keepAi && aiPatch!=null && EraseStore.CanonicalEraseStrokes(shapes)!=aiPatch.StrokesSpec) {
                aiPatch=null;
                for(int i=0;i<shapes.Count;i++)
                    if(shapes[i].IsAiErase)shapes[i]=shapes[i].WithEraseMode(eraseMode==1?MotionShape.ErasePerFrame:MotionShape.EraseStaticCSharp);
                if(EraseStrokesChanged!=null)EraseStrokesChanged();
            } else if(!keepAi && EraseStrokesChanged!=null) {
                EraseStrokesChanged();
            }
            SaveValue();
            Update();
            if(Changed!=null)Changed();
        }

        void Snapshot(List<Edit> history) {
            if(history.Count==MaxUndo)history.RemoveAt(0);
            history.Add(new Edit(shapes,selected,aiPatch,eraseMode));
        }
        void Remember() {Snapshot(undo);redo.Clear();}
        internal void Undo() {Restore(undo,redo);}
        internal void Redo() {Restore(redo,undo);}
        void Restore(List<Edit> from,List<Edit> to) {
            CancelDrag();
            if(from.Count==0)return;
            Snapshot(to);
            var edit=from[from.Count-1];from.RemoveAt(from.Count-1);
            shapes=edit.Shapes;selected=edit.Selected;aiPatch=edit.AiPatch;eraseMode=edit.EraseMode;
            SaveChange(true);
        }
        internal void Clear() {
            CancelDrag();
            if(!HasShapes)return;
            Remember();shapes.Clear();selected=-1;SaveChange();
        }
        internal void DeleteSelected() {DeleteAt(selected);}
        void DeleteAt(int index) {
            CancelDrag();
            if(index<0)return;
            Remember();shapes.RemoveAt(index);selected=-1;SaveChange();
        }
        int OrderTarget(bool front) {
            return front?shapes.FindLastIndex(shape=>shape.IsMosaic==IsMosaic && shape.IsErase==IsErase):
                shapes.FindIndex(shape=>shape.IsMosaic==IsMosaic && shape.IsErase==IsErase);
        }
        internal bool CanReorder(bool front) {return HasSelection && !IsErase && selected!=OrderTarget(front);}
        internal void Reorder(bool front) {
            CancelDrag();
            if(!CanReorder(front))return;
            Remember();
            int target=OrderTarget(front);
            var shape=shapes[selected];shapes.RemoveAt(selected);
            selected=target;shapes.Insert(selected,shape);SaveChange();
        }
        internal void SetStyle(int? rgb=null,int? width=null,int? fill=null,int? opacity=null,int? block=null) {
            if(rgb.HasValue)Defaults.Color=rgb.Value;
            if(fill.HasValue)Defaults.Fill=fill.Value;
            if(block.HasValue)Defaults.Block=block.Value;
            if(Kind=='h') {
                if(width.HasValue)Defaults.MarkerThickness=width.Value;
                if(opacity.HasValue)Defaults.MarkerOpacity=opacity.Value;
            } else if(Kind=='x') {
                if(width.HasValue)Defaults.EraseThickness=width.Value;
            } else {
                if(width.HasValue)Defaults.Thickness=width.Value;
                if(opacity.HasValue)Defaults.Opacity=opacity.Value;
            }
            Defaults.Save();
            if(HasSelection) {
                var shape=shapes[selected];
                int nextColor=shape.IsErase?shape.Color:(rgb??shape.Color);
                int nextFill=shape.IsErase?-1:(fill??shape.FillColor);
                int nextOpacity=shape.IsErase?100:(opacity??shape.Opacity);
                int nextBlock=shape.IsErase?shape.Block:(block??shape.Block);
                var next=new MotionShape(shape.Kind,nextColor,width??shape.Thickness,nextFill,
                    nextOpacity,nextBlock,shape.Points,shape.Track);
                if(next.Color!=shape.Color || next.Thickness!=shape.Thickness || next.FillColor!=shape.FillColor ||
                    next.Opacity!=shape.Opacity || next.Block!=shape.Block) {
                    CancelDrag();Remember();shapes[selected]=next;SaveChange();return;
                }
            }
            Update();
        }
        internal bool CancelDrag() {
            if(!dragging)return false;
            EndDrag();
            InvalidateVisual();
            return true;
        }
        void EndDrag() {
            dragging=false;pending=null;points.Clear();
            if(IsMouseCaptured)ReleaseMouseCapture();
        }
        Point ToPicture(Point p) {
            return new Point(MotionShape.Clamp(p.X/ActualWidth),MotionShape.Clamp(p.Y/ActualHeight));
        }
        Point ToScreen(Point p) {return MotionDraw.At(p,ActualWidth,ActualHeight);}
        double Scale {get {return ActualHeight/pixelHeight;}}
        Geometry ScreenGeometry(MotionShape shape) {
            return MotionDraw.GeometryOf(shape,ActualWidth,ActualHeight,MotionDraw.Pixels(shape,pixelHeight)*Scale);
        }
        static Point[] Handles(MotionShape shape) {
            Point a=shape.Points[0],b=shape.Points[1];
            if(MotionShape.IsPen(shape.Kind)) {
                var p=shape.Points;
                a=new Point(p.Min(at=>at.X),p.Min(at=>at.Y));b=new Point(p.Max(at=>at.X),p.Max(at=>at.Y));
            } else if(!shape.HasCorners)return shape.Points;
            return new[]{a,new Point(b.X,a.Y),b,new Point(a.X,b.Y)};
        }
        protected override void OnRender(DrawingContext dc) {
            if(ActualWidth<=0 || ActualHeight<=0)return;
            // Give the layer hit-test bounds even before the first shape exists.
            if(active)dc.DrawRectangle(Brushes.Transparent,null,new Rect(0,0,ActualWidth,ActualHeight));
            MotionShape liveErase=null;
            if(!onlySelection) {
                var current=shapes.ConvertAll(shape=>shape.At(sceneTime));
                if(pending!=null) {
                    if(drawing) {
                        if(pending.IsErase)liveErase=pending;
                        else current.Add(pending);
                    } else current[selected]=pending;
                }
                BitmapSource workingPic=null;
                bool anyErase=current.Any(shape=>shape.IsErase);
                bool anyMosaic=current.Any(shape=>shape.IsMosaic);
                if(anyErase) {
                    if(StaticErasePatch!=null && eraseMode==0) {
                        var patch=aiPatch!=null && aiPatch.Patch!=null && pending==null?aiPatch.Patch:StaticErasePatch(current);
                        if(patch!=null)dc.DrawImage(patch,new Rect(0,0,ActualWidth,ActualHeight));
                    } else {
                        var basePic=ErasePicture!=null?ErasePicture():(MosaicPicture!=null?MosaicPicture():null);
                        if(basePic!=null) {
                            workingPic=aiPatch!=null && aiPatch.Patch!=null && pending==null?
                                EraseStore.Composite(basePic,aiPatch.Patch):CoverInpaint.ApplyHybrid(basePic,current);
                            if(!anyMosaic)dc.DrawImage(workingPic,new Rect(0,0,ActualWidth,ActualHeight));
                        }
                    }
                }
                if(anyMosaic) {
                    var picture=workingPic??(MosaicPicture==null?null:MosaicPicture());
                    if(picture!=null)dc.DrawImage(MotionDraw.ApplyMosaics(picture,current),new Rect(0,0,ActualWidth,ActualHeight));
                    else foreach(var shape in current.Where(shape=>shape.IsMosaic))
                        MotionDraw.MosaicPlaceholder(dc,new Rect(ToScreen(shape.Points[0]),ToScreen(shape.Points[1])));
                }
                double width=ActualWidth/Scale;
                dc.PushTransform(new ScaleTransform(Scale,Scale));
                foreach(var shape in current)MotionDraw.Render(dc,shape,width,pixelHeight);
                dc.Pop();
                if(liveErase!=null) {
                    double brushPx=Math.Max(2,MotionDraw.Pixels(liveErase,pixelHeight)*Scale);
                    var liveGeom=MotionDraw.GeometryOf(liveErase,ActualWidth,ActualHeight,brushPx);
                    dc.DrawGeometry(null,MotionDraw.Stroke(new SolidColorBrush(System.Windows.Media.Color.FromArgb(145,255,90,90)),brushPx),liveGeom);
                    dc.DrawGeometry(null,MotionDraw.Stroke(new SolidColorBrush(System.Windows.Media.Color.FromArgb(190,255,255,255)),Math.Max(1.5,brushPx*0.24)),liveGeom);
                }
            }
            if(HasTracks) {
                var lostPen=MotionDraw.Stroke(Brushes.Red,2);
                lostPen.DashStyle=DashStyles.Dash;
                for(int i=0;i<shapes.Count;i++) {
                    var shape=shapes[i];
                    if(i==selected && (pending!=null || active) || shape.Track==null)continue;
                    var key=shape.Track.At(sceneTime);
                    if(key.Lost)dc.DrawGeometry(null,lostPen,ScreenGeometry(shape.WithBox(key.Box)));
                }
            }
            if(liveErase!=null)return;
            var outlined=pending??(HasSelection?shapes[selected].At(sceneTime):null);
            if(!active || outlined==null)return;
            var geometry=ScreenGeometry(outlined);
            dc.DrawGeometry(null,MotionDraw.Stroke(Brushes.Black,2.5),geometry);
            var dash=MotionDraw.Stroke(Brushes.White,1);dash.DashStyle=DashStyles.Dash;
            if(!HasSelection || pending!=null || shapes[selected].Track==null || !shapes[selected].Track.At(sceneTime).Lost)
                dc.DrawGeometry(null,dash,geometry);
            else {
                var red=MotionDraw.Stroke(Brushes.Red,2);red.DashStyle=DashStyles.Dash;
                dc.DrawGeometry(null,red,geometry);
            }
            if(drawing && pending!=null)return;
            foreach(var point in Handles(outlined)) {
                Point at=ToScreen(point);
                dc.DrawRectangle(Brushes.White,new Pen(Brushes.Black,1),new Rect(at.X-HandleRadius,at.Y-HandleRadius,2*HandleRadius,2*HandleRadius));
            }
        }
        protected override HitTestResult HitTestCore(PointHitTestParameters e) {
            return active && ActualWidth>0 && ActualHeight>0 &&
                new Rect(0,0,ActualWidth,ActualHeight).Contains(e.HitPoint)?new PointHitTestResult(this,e.HitPoint):null;
        }
        int ShapeAt(Point p) {
            for(int i=shapes.Count-1;i>=0;i--) {
                var shape=shapes[i].At(sceneTime);var geometry=ScreenGeometry(shape);
                var hit=MotionDraw.Stroke(Brushes.Transparent,MotionDraw.Pixels(shape,pixelHeight)*Scale+12);
                if(!shape.IsMosaic && ((shape.Fill && geometry.FillContains(p)) || geometry.StrokeContains(hit,p)))return i;
            }
            for(int i=shapes.Count-1;i>=0;i--)
                if(shapes[i].IsMosaic && ScreenGeometry(shapes[i].At(sceneTime)).FillContains(p))return i;
            return -1;
        }
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) {
            if(!active)return;
            Focus();
            if(BeforePointer!=null)BeforePointer();
            start=e.GetPosition(this);anchor=ToPicture(start);handle=-1;
            if(HasSelection) {
                var handles=Handles(shapes[selected].At(sceneTime));
                for(int i=0;i<handles.Length;i++)
                    if((ToScreen(handles[i])-start).Length<=HandleRadius+3){handle=i;break;}
            }
            if(handle<0)selected=Tool=='x'?-1:ShapeAt(start);
            drawing=!HasSelection;
            dragShape=drawing?null:shapes[selected].At(sceneTime);
            if(PointerStarted!=null)PointerStarted();
            dragging=(!drawing || (shapes.Count<MotionShape.MaxShapes &&
                (Tool!='m' || shapes.Count(shape=>shape.IsMosaic)<MotionShape.MaxMosaics))) && CaptureMouse();
            Update();e.Handled=true;
        }
        protected override void OnMouseMove(MouseEventArgs e) {
            if(!dragging || ActualWidth<=0 || ActualHeight<=0)return;
            DragTo(e.GetPosition(this));
        }
        MotionShape NewEraseShape(Point[] strokePoints) {
            int eraseColor=eraseMode==0 && CurrentEraseSceneMs!=null?CurrentEraseSceneMs():0;
            int eraseBlock=eraseMode==1?MotionShape.ErasePerFrame:MotionShape.EraseStaticCSharp;
            return new MotionShape('x',eraseColor,DefaultThickness,-1,100,eraseBlock,strokePoints);
        }
        void DragTo(Point mouse) {
            bool first=pending==null;
            if(first && (mouse-start).Length<=DragDistance)return;
            Point p=ToPicture(mouse);
            if(drawing) {
                if(MotionShape.IsPen(Tool)) {
                    if(points.Count==0)points.Add(anchor);
                    if((p-points[points.Count-1]).LengthSquared>=0.00000025)points.Add(p);
                    if(points.Count>MotionShape.MaxPoints*2) {
                        var simplified=MotionShape.Simplify(points);points.Clear();points.AddRange(simplified);
                    }
                    if(points.Count<2)return;
                }
                pending=Tool=='x'?NewEraseShape(points.ToArray()):
                    new MotionShape(Tool,Defaults.Color,DefaultThickness,MotionShape.CanFill(Tool)?Defaults.Fill:-1,DefaultOpacity,Defaults.Block,
                        MotionShape.IsPen(Tool)?points.ToArray():new[]{anchor,Constrain(Tool,anchor,p)});
            } else {
                var original=dragShape;
                Point[] moved=handle>=0?Resize(original,p):Move(original,mouse-start);
                pending=original.WithPoints(moved);
            }
            InvalidateVisual();
            if(first && Editing!=null)Editing();
        }

        Point Constrain(char kind,Point fixedPoint,Point p) {
            if((Keyboard.Modifiers & ModifierKeys.Shift)==0)return p;
            Vector delta=ToScreen(p)-ToScreen(fixedPoint);
            if(kind=='a' || kind=='l') {
                if(delta.LengthSquared<0.0001)return p;
                double direction=Math.Round(Math.Atan2(delta.Y,delta.X)/(Math.PI/4))*Math.PI/4;
                var axis=new Vector(Math.Cos(direction),Math.Sin(direction));
                delta=axis*(delta*axis);
            } else if(!MotionShape.IsPen(kind)) {
                double side=Math.Max(Math.Abs(delta.X),Math.Abs(delta.Y));
                delta=new Vector(delta.X<0?-side:side,delta.Y<0?-side:side);
            } else return p;
            Point edge=ToScreen(fixedPoint);
            double limit=1;
            if(Math.Abs(delta.X)>0.0001)limit=Math.Min(limit,(delta.X>0?ActualWidth-edge.X:-edge.X)/delta.X);
            if(Math.Abs(delta.Y)>0.0001)limit=Math.Min(limit,(delta.Y>0?ActualHeight-edge.Y:-edge.Y)/delta.Y);
            return ToPicture(edge+delta*limit);
        }
        Point[] Resize(MotionShape shape,Point p) {
            var handles=Handles(shape);
            if(shape.HasCorners) {
                Point opposite=handles[(handle+2)%4];
                return new[]{opposite,Constrain(shape.Kind,opposite,p)};
            }
            if(!MotionShape.IsPen(shape.Kind)) {
                var ends=(Point[])shape.Points.Clone();
                ends[handle]=Constrain(shape.Kind,ends[1-handle],p);
                return ends;
            }
            Point anchor=handles[(handle+2)%4],corner=handles[handle];
            double x=Math.Abs(corner.X-anchor.X)<0.0001?1:(p.X-anchor.X)/(corner.X-anchor.X);
            double y=Math.Abs(corner.Y-anchor.Y)<0.0001?1:(p.Y-anchor.Y)/(corner.Y-anchor.Y);
            return Array.ConvertAll(shape.Points,at=>new Point(anchor.X+(at.X-anchor.X)*x,anchor.Y+(at.Y-anchor.Y)*y));
        }
        Point[] Move(MotionShape shape,Vector distance) {
            var p=shape.Points;
            double x=Math.Max(-p.Min(at=>at.X),Math.Min(1-p.Max(at=>at.X),distance.X/ActualWidth));
            double y=Math.Max(-p.Min(at=>at.Y),Math.Min(1-p.Max(at=>at.Y),distance.Y/ActualHeight));
            return Array.ConvertAll(p,at=>new Point(at.X+x,at.Y+y));
        }
        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) {
            if(!active)return;
            e.Handled=true;
            if(!dragging)return;
            if(ActualWidth>0 && ActualHeight>0)DragTo(e.GetPosition(this));
            var final=pending;
            if(final!=null && MotionShape.IsPen(final.Kind) && drawing)
                final=final.WithPoints(MotionShape.Simplify(points));
            EndDrag();
            if(final==null && drawing && Tool=='x') {
                int hit=ShapeAt(start);
                if(hit>=0){selected=hit;Update();return;}
                if(shapes.Count<MotionShape.MaxShapes)final=NewEraseShape(new[]{anchor,anchor});
            }
            if(final==null){InvalidateVisual();return;}
            var rounded=Array.ConvertAll(final.Points,p=>new Point(Math.Round(MotionShape.Clamp(p.X),4),Math.Round(MotionShape.Clamp(p.Y),4)));
            final=final.WithPoints(rounded);
            if(!drawing && final.Points.SequenceEqual(dragShape.Points)){InvalidateVisual();return;}
            if(!drawing && final.Track!=null && FixedSceneTime!=null) {
                if(final.Box.Width<=0 || final.Box.Height<=0){InvalidateVisual();return;}
                var corrected=final.Track.Correct(FixedSceneTime(),final.Box);
                if(corrected==null){if(TrackLimit!=null)TrackLimit();InvalidateVisual();return;}
                final=shapes[selected].WithTrack(corrected);
            }
            Remember();
            if(drawing) {
                if(final.IsErase && eraseMode==0 && CurrentEraseSceneMs!=null) {
                    int sceneMs=final.EraseSceneMs;
                    for(int i=0;i<shapes.Count;i++)
                        if(shapes[i].IsErase)shapes[i]=shapes[i].WithEraseMode(MotionShape.EraseStaticCSharp,sceneMs);
                }
                selected=final.IsErase?-1:shapes.Count;
                shapes.Add(final);
            } else shapes[selected]=final;
            SaveChange();
        }
        protected override void OnMouseRightButtonDown(MouseButtonEventArgs e) {
            if(!active || dragging)return;
            int index=ShapeAt(e.GetPosition(this));
            if(index<0)return;
            DeleteAt(index);e.Handled=true;
        }
        protected override void OnLostMouseCapture(MouseEventArgs e) {
            base.OnLostMouseCapture(e);CancelDrag();
        }
    }
}
