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
        // MaxShapes 300 (was 50): a long erase drag is kept as several pieces of up to MaxPoints points each, never thinned
        // out (2026-11-27 user: a rubbed-over spot came out as a few thin lines). MaxThickness: up to the picture's height.
        internal const int MaxShapes=300,MaxPoints=400,MaxMosaics=20,MaxAreas=8,MaxThickness=1000;
        internal const int EraseStaticCSharp=24,EraseStaticAi=25,ErasePerFrame=26;
        internal const string Kinds="rbelaphmx";
        // Parts adjusted on their own (AdjustAreas.cs): box, ellipse, painted. One tool [0] with a shape choice.
        internal const string AreaKinds="goq",AllKinds=Kinds+AreaKinds;
        static readonly CultureInfo Invariant=CultureInfo.InvariantCulture;
        internal readonly char Kind;
        internal readonly int Color,Thickness,FillColor,Opacity,Block;
        internal bool Fill {get {return FillColor>=0;}}
        internal readonly Point[] Points;
        internal readonly MotionTrackPath Track;
        // A part's own adjustment (brightness kept as Light's encoded value, soft style) and its soft edge 0..100.
        internal readonly AdjustValues Area;
        internal readonly int Feather;

        internal MotionShape(char kind,int color,int thickness,int fillColor,int opacity,int block,Point[] points,MotionTrackPath track=null,
            AdjustValues area=default(AdjustValues),int feather=0) {
            Kind=kind;Color=color;Thickness=thickness;FillColor=CanFill(kind) || kind=='l'?fillColor:-1;
            Opacity=opacity;Block=block;Points=points;Track=CanTrack?track:null;
            Area=IsArea?area.Clamped():default(AdjustValues);Feather=IsArea?Math.Max(0,Math.Min(100,feather)):0;
        }
        internal bool HasCorners {get {return Kind=='r' || Kind=='b' || Kind=='e' || Kind=='m' || Kind=='g' || Kind=='o';}}
        internal bool CanTrack {get {return Kind=='r' || Kind=='b' || Kind=='e' || Kind=='m';}}
        internal bool IsArea {get {return AreaKinds.IndexOf(Kind)>=0;}}
        internal bool IsMosaic {get {return Kind=='m';}}
        internal bool IsErase {get {return Kind=='x';}}
        internal bool IsPerFrameErase {get {return Kind=='x' && Block==ErasePerFrame;}}
        internal bool IsAiErase {get {return Kind=='x' && Block==EraseStaticAi;}}
        internal int EraseSceneMs {get {return Kind=='x'?Color:0;}}
        internal static bool IsPen(char kind) {return kind=='p' || kind=='h' || kind=='x' || kind=='q';}
        internal static bool CanFill(char kind) {return kind=='r' || kind=='b' || kind=='e' || kind=='a';}
        internal MotionShape WithPoints(Point[] points) {return new MotionShape(Kind,Color,Thickness,FillColor,Opacity,Block,points,Track,Area,Feather);}
        internal MotionShape WithTrack(MotionTrackPath track) {return new MotionShape(Kind,Color,Thickness,FillColor,Opacity,Block,Points,track,Area,Feather);}
        internal MotionShape WithEraseMode(int block,int? sceneMs=null) {return new MotionShape(Kind,sceneMs??Color,Thickness,FillColor,Opacity,block,Points,Track,Area,Feather);}
        internal MotionShape WithArea(AdjustValues area,int feather) {return new MotionShape(Kind,Color,Thickness,FillColor,Opacity,Block,Points,Track,area,feather);}
        internal MotionShape WithKind(char kind) {return new MotionShape(kind,Color,Thickness,FillColor,Opacity,Block,Points,Track,Area,Feather);}
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
            int mosaics=0,areas=0;
            for(int i=1;i<entries.Length && shapes.Count<MaxShapes;i++) {
                var fields=entries[i].Split(',');
                MotionTrackPath track=null;
                // A part's adjustment rides at the end: ~brightness:contrast:saturation:temperature:sharpness:soft edge.
                AdjustValues area=default(AdjustValues);int feather=0;
                if(fields.Length>1 && fields[fields.Length-1].StartsWith("~",StringComparison.Ordinal)) {
                    if(!ReadArea(fields[fields.Length-1].Substring(1),out area,out feather))continue;
                    Array.Resize(ref fields,fields.Length-1);
                }
                if(fields.Length==9 && fields[8].StartsWith("@",StringComparison.Ordinal)) {
                    track=MotionTrackPath.Read(fields[8].Substring(1));
                    Array.Resize(ref fields,8);
                }
                if(fields.Length<8 || fields.Length%2!=0 || fields[0].Length!=1)continue;
                char kind=fields[0][0];
                if(AllKinds.IndexOf(kind)<0 || (!IsPen(kind) && fields.Length!=8))continue;
                int color,thickness;
                if(fields[1].Length!=6 || !Int32.TryParse(fields[1],NumberStyles.AllowHexSpecifier,Invariant,out color) ||
                    !Int32.TryParse(fields[2],NumberStyles.Integer,Invariant,out thickness) || thickness<1 || thickness>MaxThickness)continue;
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
                if(AreaKinds.IndexOf(kind)>=0 && areas==MaxAreas)continue;
                int count=(fields.Length-4)/2;
                var points=new List<Point>(count);
                for(int j=4;j<fields.Length;j+=2) {
                    double x,y;
                    if(!Number(fields[j],out x) || !Number(fields[j+1],out y))break;
                    points.Add(new Point(Clamp(x),Clamp(y)));
                }
                if(points.Count!=count)continue;
                shapes.Add(new MotionShape(kind,color,thickness,fill,opacity,block,count>MaxPoints?Simplify(points):points.ToArray(),track,area,feather));
                if(kind=='m')mosaics++;
                if(AreaKinds.IndexOf(kind)>=0)areas++;
            }
            return shapes;
        }
        static bool ReadArea(string text,out AdjustValues area,out int feather) {
            area=default(AdjustValues);feather=0;
            var parts=text.Split(':');
            if(parts.Length!=6)return false;
            var n=new int[6];
            for(int i=0;i<6;i++)if(!Int32.TryParse(parts[i],NumberStyles.Integer,Invariant,out n[i]))return false;
            area=new AdjustValues {Brightness=Light.Encode(Math.Max(-100,Math.Min(100,n[0])),Light.Soft),Contrast=n[1],Saturation=n[2],Temperature=n[3],Sharpness=n[4]}.Clamped();
            feather=Math.Max(0,Math.Min(100,n[5]));
            return true;
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
                if(shape.IsArea)
                    text.Append(",~").Append(Light.Level(shape.Area.Brightness).ToString(Invariant)).Append(':').Append(shape.Area.Contrast.ToString(Invariant))
                        .Append(':').Append(shape.Area.Saturation.ToString(Invariant)).Append(':').Append(shape.Area.Temperature.ToString(Invariant))
                        .Append(':').Append(shape.Area.Sharpness.ToString(Invariant)).Append(':').Append(shape.Feather.ToString(Invariant));
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
        // A painted part uses a wider brush (up to a fifth of the height) than lines do.
        internal static double Pixels(MotionShape shape,double height) {return Math.Max(1.5,shape.Thickness*height/(shape.Kind=='q'?250.0:1000.0));}
        internal static Pen Stroke(Brush brush,double thickness) {
            return new Pen(brush,thickness) {StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round,LineJoin=PenLineJoin.Round};
        }
        internal static Geometry GeometryOf(MotionShape shape,double width,double height,double px) {
            Point a=At(shape.Points[0],width,height),b=At(shape.Points[1],width,height);
            if(shape.Kind=='r' || shape.Kind=='b' || shape.Kind=='g' || shape.IsMosaic) {
                var bounds=new Rect(a,b);
                double corner=shape.Kind=='b'?Math.Min(bounds.Width,bounds.Height)*0.18:0;
                return new RectangleGeometry(bounds,corner,corner);
            }
            if(shape.Kind=='e' || shape.Kind=='o')return new EllipseGeometry(new Rect(a,b));
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
            if(shape.IsMosaic || shape.IsErase || shape.IsArea)return;
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
            picture=AdjustAreas.Apply(picture,shapes);
            picture=ApplyMosaics(picture,shapes);
            if(!shapes.Any(shape=>!shape.IsMosaic && !shape.IsErase && !shape.IsArea))return picture;
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
            var shapes=MotionShape.Read(value).Where(shape=>!shape.IsMosaic && !shape.IsErase && !shape.IsArea).ToList();
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
        internal int Color=0xFFFFFF,Fill=-1,Thickness=6,Opacity=100,Block=24,MarkerThickness=20,MarkerOpacity=40,EraseThickness=40;
        // Parts (kept for this run only): the shape last picked, the next part's values (a little brighter, so it shows
        // at once), its soft edge and the painting brush.
        internal char AreaKind='g';
        internal AdjustValues AreaValues=new AdjustValues {Brightness=Light.Encode(20,Light.Soft)};
        internal int AreaFeather=30,AreaThickness=24;
        static DrawPreferences Load() {
            var result=new DrawPreferences();
            try {
                if(!File.Exists(PathName))return result;
                var parts=File.ReadAllText(PathName).Split('|');
                if((parts.Length!=9 && parts.Length!=10) || parts[1].Length!=1 || MotionShape.AllKinds.IndexOf(parts[1][0])<0)return result;
                int[] values=new int[parts.Length-2];
                for(int i=0;i<values.Length;i++)
                    if(!Int32.TryParse(parts[i+2],NumberStyles.Integer,CultureInfo.InvariantCulture,out values[i]))return result;
                result.Open=parts[0]=="1";result.Tool=parts[1][0];
                if(MotionShape.AreaKinds.IndexOf(result.Tool)>=0)result.AreaKind=result.Tool;
                result.Color=Limit(values[0],0,0xFFFFFF);result.Fill=Limit(values[1],-1,0xFFFFFF);
                result.Thickness=Limit(values[2],1,MotionShape.MaxThickness);result.Opacity=Limit(values[3],10,100);result.Block=Limit(values[4],1,100);
                result.MarkerThickness=Limit(values[5],1,MotionShape.MaxThickness);result.MarkerOpacity=Limit(values[6],10,100);
                if(values.Length>=8)result.EraseThickness=Limit(values[7],1,MotionShape.MaxThickness);
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
        readonly Button opener,eraseOpener,undo,redo,delete,front,back,clear,none,aiButton,againButton,fillAllButton;
        // [Fill every scene] (or Enter, or ▶ Play): with [Each scene], every scene is filled only when asked, so the
        // painted spot can be fixed first (2026-11-28 user: "I didn't like what I painted, but it started as soon as I let go").
        internal event Action FillAllRequested;
        readonly TextBlock thicknessName;
        readonly RadioButton fillOnce,fillEach;
        // The [Erase ▾] button: erasing is its own tool, not one of the drawing tools (2026-11-27 user).
        internal Button EraseOpener {get {return eraseOpener;}}
        char lastDrawTool='r';
        readonly TextBlock aiNote;
        // Which try the window should give the AI: 0 the painted spots as they are, 1..3 a different try (EraseAgain).
        internal int AiVariant {get;private set;}
        readonly StackPanel body=new StackPanel {Margin=new Thickness(0,6,0,4)};
        readonly StackPanel aiBusyPanel;
        readonly RadioButton[] tools=new RadioButton[MotionShape.Kinds.Length];
        readonly FrameworkElement strokeField,fillField,thicknessField,opacityField,blockField,gifEraseField;
        readonly RadioButton areaTool;
        readonly RadioButton[] areaShapes=new RadioButton[3];
        readonly StackPanel areaBox;
        readonly AdjustRows areaRows;
        readonly AdjustRow featherRow;
        readonly TextBlock gifModeText,gifPauseNote;
        // The GIF window decides how erased spots are filled; the person can still switch with [Change].
        internal event Action EraseModeSwitched;
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
            opener.HorizontalAlignment=HorizontalAlignment.Left;opener.Margin=new Thickness(0,2,6,2);
            AutomationProperties.SetName(opener,L.P("Draw","그림 그리기"));
            eraseOpener=UI.Btn("",ToggleErase);
            eraseOpener.HorizontalAlignment=HorizontalAlignment.Left;eraseOpener.Margin=new Thickness(0,2,0,2);
            eraseOpener.ToolTip=L.P("Paint over unwanted things, like date letters or passersby, and they are filled in from around them (9)",
                "날짜 글씨나 지나가는 사람처럼 필요 없는 것을 칠하면 주변으로 메워요 (9)");
            AutomationProperties.SetName(eraseOpener,L.P("Erase","지우기"));
            scroller=new ScrollViewer {Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,Focusable=false};
            var openers=new WrapPanel();openers.Children.Add(opener);openers.Children.Add(eraseOpener);
            Children.Add(openers);Children.Add(scroller);
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
                if(kind=='x')tool.Visibility=Visibility.Collapsed; // erasing has its own [Erase] button
            }
            areaTool=new RadioButton {Content="0  "+L.P("Adjust part","부분 보정"),GroupName=group,Style=(Style)Application.Current.FindResource("SegmentChoice"),
                ToolTip=L.P("0  Adjust part: drag over a part of the picture and change only its brightness, contrast, saturation, color temperature and sharpness",
                    "0  부분 보정: 사진에서 한 부분을 끌어서 고르고 그곳만 밝기·대비·채도·색 온도·선명하게를 바꿔요")};
            areaTool.Click+=delegate {layer.SelectTool(DrawPreferences.Last.AreaKind);};
            toolRow.Children.Add(areaTool);
            body.Children.Add(toolRow);
            this.toolRow=toolRow;
            // Shown only for [0 Adjust part]: the shape, then that part's own five values and its soft edge.
            areaBox=new StackPanel {Margin=new Thickness(0,4,0,0)};
            var shapeRow=new WrapPanel();
            string shapeGroup="area-"+Guid.NewGuid().ToString("N");
            string[] shapeNames={L.P("Box","네모"),L.P("Ellipse","동그라미"),L.P("Paint","칠하기")};
            for(int i=0;i<3;i++) {
                char kind=MotionShape.AreaKinds[i];
                var choice=new RadioButton {Content=shapeNames[i],GroupName=shapeGroup,Style=(Style)Application.Current.FindResource("SegmentChoice")};
                choice.Click+=delegate {layer.SelectAreaKind(kind);};
                areaShapes[i]=choice;shapeRow.Children.Add(choice);
            }
            areaBox.Children.Add(shapeRow);
            var areaNote=UI.Text(L.P("Drag over the picture to pick a part. Only that part changes with the numbers below; click a part to change it again.",
                "사진에서 끌어서 고칠 곳을 골라요. 고른 곳만 아래 숫자대로 바뀌고, 다시 고치려면 그곳을 눌러요."),12,true);
            areaNote.TextWrapping=TextWrapping.Wrap;areaNote.Margin=new Thickness(2,4,0,2);
            areaBox.Children.Add(areaNote);
            areaRows=new AdjustRows((field,v)=>{
                var next=layer.AreaValues;
                if(field==0)next.Brightness=Light.Encode(v,Light.Soft);
                else if(field==1)next.Contrast=v;
                else if(field==2)next.Saturation=v;
                else if(field==3)next.Temperature=v;
                else next.Sharpness=v;
                layer.SetArea(next,layer.AreaFeather);
            });
            featherRow=new AdjustRow(L.P("Soft edge","가장자리 부드럽게"),0,100,v=>layer.SetArea(layer.AreaValues,v));
            areaRows.Children.Insert(areaRows.Children.Count-1,featherRow);
            areaBox.Children.Add(areaRows);
            body.Children.Add(areaBox);
            string stroke=L.P("Stroke color","선 색"),fill=L.P("Fill color","채우기 색");
            strokeField=Field(stroke,TextStylePanel.Colors(stroke,()=>layer.Color,v=>layer.SetStyle(rgb:v),out showColor));
            var fills=(Panel)TextStylePanel.Colors(fill,()=>layer.FillColor,v=>layer.SetStyle(fill:v),out showFill);
            none=UI.Btn(L.P("None","없음"),()=>layer.SetStyle(fill:-1));
            none.Margin=new Thickness(0,0,5,0);none.MinHeight=24;fills.Children.Insert(0,none);
            fillField=Field(fill,fills);

            Func<double> readThick;
            var thicknessRow=new WrapPanel {VerticalAlignment=VerticalAlignment.Center};
            // No preset buttons and no low ceiling (2026-11-27 user: "12 24 48 is weird", "why max 50, the text is bigger").
            // 1000 = the picture's height. The brush shows as a circle over the preview (see MotionDrawLayer).
            thicknessRow.Children.Add(MotionLook.Number(layer.Thickness,1,MotionShape.MaxThickness,1,"",L.P("Thickness (1000 = the picture's height)","굵기 (1000이면 그림 높이만큼)"),
                v=>{if(!syncing){layer.SetStyle(width:(int)v);layer.ShowBrush();}},out readThick,out showThickness));
            aiButton=UI.Btn(L.P("Fill with AI","AI로 메우기"),RunAiClicked,true);
            aiButton.Margin=new Thickness(8,0,0,2);
            aiButton.ToolTip=L.P("Fills the painted spots again with the AI on this PC. It takes about 2-3 seconds. Press Ctrl+Z once to undo.",
                "칠한 곳을 내 PC 안의 AI로 다시 메워요. 2~3초쯤 걸려요. Ctrl+Z를 한 번 누르면 AI 적용 전으로 돌아가요.");
            thicknessRow.Children.Add(aiButton);
            againButton=UI.Btn(L.P("Fill again","다시 메우기"),RunAgainClicked);
            againButton.Margin=new Thickness(8,0,0,2);
            againButton.ToolTip=L.P("Fills the painted spots again a little differently each time. Ctrl+Z goes back to the result before.",
                "누를 때마다 조금 다른 방법으로 다시 메워요. Ctrl+Z를 누르면 앞 결과로 돌아가요.");
            thicknessRow.Children.Add(againButton);
            aiBusyPanel=new StackPanel {Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,0,0,2),Visibility=Visibility.Collapsed};
            var busyText=UI.Text(L.P("Refining…","다듬는 중…"),12,true);busyText.Margin=new Thickness(0,0,6,0);
            var cancelAi=UI.Btn(L.T("Cancel"),()=>{if(OnCancelAiErase!=null)OnCancelAiErase();});
            cancelAi.MinHeight=25;cancelAi.Padding=new Thickness(8,1,8,1);
            aiBusyPanel.Children.Add(busyText);aiBusyPanel.Children.Add(cancelAi);
            thicknessRow.Children.Add(aiBusyPanel);
            thicknessField=Field(L.P("Thickness","굵기"),thicknessRow);
            thicknessName=(TextBlock)((DockPanel)thicknessField).Children[0];
            aiNote=UI.Text(L.P("If one spot looks odd, paint over just that spot and press [Fill with AI]: only the new spot is filled again.",
                "한 곳만 이상하면 그 위만 다시 칠하고 [AI로 메우기]를 눌러요. 새로 칠한 곳만 다시 메워요."),12,true);
            aiNote.TextWrapping=TextWrapping.Wrap;aiNote.Margin=new Thickness(2,2,0,2);

            opacityField=Field(L.P("Opacity","투명도"),Number(layer.ShapeOpacity,10,100,"%",L.P("Opacity","투명도"),
                v=>layer.SetStyle(opacity:v),new[]{40,70,100},out showOpacity));
            string block=L.P("Block size","칸 크기");
            blockField=Field(block,Number(layer.Block,1,100,"px",block,v=>layer.SetStyle(block:v),new[]{12,24,48},out showBlock));
            blockField.ToolTip=L.P("Pixels when the cover is 1000 px tall.","표지 높이가 1000px일 때의 칸 크기예요.");
            foreach(var field in new[]{strokeField,fillField,thicknessField,opacityField,blockField})body.Children.Add(field);
            body.Children.Insert(body.Children.IndexOf(thicknessField)+1,aiNote);

            if(isGif) {
                // GIF erase (2026-11-27 user: "nothing stops the screen, I don't know what 'fill' means"): picking
                // [9 Erase] stops the preview; one line says what to do; how the spots are filled is chosen by looking
                // at the background, said in plain words, with [Change] for the other way.
                // Three short steps instead of sentences (2026-11-27 user: "reading these sentences takes forever; give it a
                // name and explain on mouse-over", "after this, what do I do?"). The details are on the tooltip.
                gifPauseNote=UI.Text("",12,true);
                gifPauseNote.TextWrapping=TextWrapping.Wrap;gifPauseNote.Margin=new Thickness(2,4,0,2);
                gifPauseNote.ToolTip=L.P("The preview stops while you erase. The painted spot is filled from around it at once on this scene; the other scenes are filled in the background, then ▶ Play shows them all. To paint on another scene, click it on the strip. Ctrl+Z undoes a stroke.",
                    "지우는 동안 미리보기가 멈춰요. 칠한 곳은 이 장면에서 바로 주변으로 메워지고, 다른 장면은 뒤에서 메운 뒤 ▶ 재생으로 모두 볼 수 있어요. 다른 장면에서 칠하려면 장면 띠에서 그 장면을 눌러요. Ctrl+Z로 한 번 칠한 것을 되돌려요.");
                body.Children.Insert(body.Children.IndexOf(areaBox),gifPauseNote);
                // How the spots are filled: picked by itself from the background, two short choices to change it.
                var fillRow=new WrapPanel();
                string fillGroup="fill-"+Guid.NewGuid().ToString("N");
                fillOnce=new RadioButton {Content=L.P("Same for all scenes","모든 장면 같게"),GroupName=fillGroup,Style=(Style)Application.Current.FindResource("SegmentChoice"),
                    ToolTip=L.P("For a background that stays still: the fill made on one scene is used on every scene.","배경이 멈춰 있을 때: 한 장면에서 메운 모습을 모든 장면에 써요.")};
                fillEach=new RadioButton {Content=L.P("Each scene","장면마다"),GroupName=fillGroup,Style=(Style)Application.Current.FindResource("SegmentChoice"),
                    ToolTip=L.P("For a moving background: every scene is filled on its own.","배경이 움직일 때: 장면마다 따로 메워요.")};
                RoutedEventHandler picked=(s,e)=>{
                    if(syncing)return;
                    int mode=fillEach.IsChecked==true?1:0;
                    if(mode==layer.EraseMode)return;
                    if(EraseModeSwitched!=null)EraseModeSwitched();
                    layer.SetEraseMode(mode);
                };
                fillOnce.Checked+=picked;fillEach.Checked+=picked;
                fillRow.Children.Add(fillOnce);fillRow.Children.Add(fillEach);
                gifModeText=UI.Text("",12,true);gifModeText.Margin=new Thickness(8,0,0,0);gifModeText.VerticalAlignment=VerticalAlignment.Center;
                fillRow.Children.Add(gifModeText);
                gifEraseField=Field(L.P("Fill","메우기"),fillRow);
                fillAllButton=UI.Btn(L.P("Fill every scene (Enter)","모든 장면 메우기 (Enter)"),()=>{if(FillAllRequested!=null)FillAllRequested();},true);
                fillAllButton.HorizontalAlignment=HorizontalAlignment.Left;fillAllButton.Margin=new Thickness(72,6,0,2);
                fillAllButton.ToolTip=L.P("Fills the painted spots on every scene, in the background. Fix the painted spots first if you like; ▶ Play starts it too.",
                    "칠한 곳을 모든 장면에서 뒤에서 메워요. 칠한 곳을 먼저 다듬어도 돼요. ▶ 재생을 눌러도 시작해요.");
                ((FrameworkElement)gifEraseField).ToolTip=L.P("Picked by itself from whether the background moves. Change it if the erased spot looks wrong when it plays.",
                    "배경이 움직이는지 보고 저절로 골라요. 재생할 때 지운 곳이 어색하면 바꿔 봐요.");
                body.Children.Add(gifEraseField);
                body.Children.Add(fillAllButton);
            }

            var actions=new WrapPanel {Margin=new Thickness(0,5,0,4)};
            delete=UI.Btn(L.P("Delete","지우기"),layer.DeleteSelected);
            front=UI.Btn(L.P("Bring to front","맨 앞으로"),()=>layer.Reorder(true));
            back=UI.Btn(L.P("Send to back","맨 뒤로"),()=>layer.Reorder(false));
            undo=UI.Btn(L.P("Undo Ctrl+Z","되돌리기 Ctrl+Z"),layer.Undo);
            redo=UI.Btn(L.P("Redo Ctrl+Y","다시 하기 Ctrl+Y"),layer.Redo);
            clear=UI.Btn(L.P("Clear all","모두 지우기"),()=>{if(layer.IsErase)layer.ClearErase();else layer.Clear();});
            foreach(var button in new[]{delete,front,back,undo,redo,clear}) {
                button.Margin=new Thickness(0,0,5,4);actions.Children.Add(button);
            }
            body.Children.Add(actions);
            if(tracking!=null)body.Children.Add(tracking);
            body.IsVisibleChanged+=delegate {layer.SetActive(IsEditing);if(VisibilityChanged!=null)VisibilityChanged();};
            layer.Updated+=Sync;
            ShowOpener(DrawPreferences.Last.Open);Sync();
        }
        readonly WrapPanel toolRow;
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
            AiVariant=0;
            await OnRunAiErase();
        }
        async void RunAgainClicked() {
            if(aiRunning || OnRunAiErase==null)return;
            AiVariant=EraseAgain.Next(AiVariant);
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
        // [Draw ▾] and [Erase ▾] open the same controls: Draw with the drawing tools, Erase with only what erasing needs.
        // Pressing the open one again folds them.
        void Toggle() {
            if(IsOpen && !layer.IsErase){SetOpen(false);return;}
            if(layer.IsErase)layer.SelectTool(lastDrawTool);
            if(!IsOpen)SetOpen(true);else ShowOpener(true);
        }
        void ToggleErase() {
            if(IsOpen && layer.IsErase){SetOpen(false);return;}
            if(!layer.IsErase)layer.SelectTool('x');
            if(!IsOpen)SetOpen(true);else ShowOpener(true);
        }
        void ShowOpener(bool open) {
            bool erase=layer.IsErase;
            opener.Content=L.P("Draw","그림 그리기")+(open && !erase?"  ▴":"  ▾");
            eraseOpener.Content=L.P("Erase","지우기")+(open && erase?"  ▴":"  ▾");
            body.Visibility=scroller.Visibility=open?Visibility.Visible:Visibility.Collapsed;
        }
        void Sync() {
            syncing=true;
            for(int i=0;i<tools.Length;i++)tools[i].IsChecked=MotionShape.Kinds[i]==layer.Tool;
            bool area=layer.IsArea;
            areaTool.IsChecked=MotionShape.AreaKinds.IndexOf(layer.Tool)>=0;
            for(int i=0;i<3;i++)areaShapes[i].IsChecked=MotionShape.AreaKinds[i]==layer.AreaKind;
            areaBox.Visibility=area?Visibility.Visible:Visibility.Collapsed;
            if(area){areaRows.Show(layer.AreaValues);featherRow.Show(layer.AreaFeather);}
            showColor(layer.Color);showFill(layer.FillColor);showThickness(layer.Thickness);showOpacity(layer.ShapeOpacity);showBlock(layer.Block);
            none.FontWeight=layer.FillColor<0?FontWeights.Bold:FontWeights.Normal;
            bool erase=layer.IsErase;
            if(!erase && MotionShape.Kinds.IndexOf(layer.Tool)>=0)lastDrawTool=layer.Tool;
            toolRow.Visibility=erase?Visibility.Collapsed:Visibility.Visible;
            ShowOpener(IsOpen);
            thicknessName.Text=erase?L.P("Brush","붓 크기"):L.P("Thickness","굵기");
            strokeField.Visibility=opacityField.Visibility=(layer.IsMosaic || erase || area)?Visibility.Collapsed:Visibility.Visible;
            thicknessField.Visibility=layer.IsMosaic || (area && layer.AreaKind!='q')?Visibility.Collapsed:Visibility.Visible;
            fillField.Visibility=layer.CanFill?Visibility.Visible:Visibility.Collapsed;
            blockField.Visibility=layer.IsMosaic?Visibility.Visible:Visibility.Collapsed;
            bool showAi=(erase || layer.HasEraseShapes) && layer.HasEraseShapes && !layer.IsMosaic && EraseEngine.Available;
            if(!erase)showAi=false;
            aiBusyPanel.Visibility=showAi && aiRunning?Visibility.Visible:Visibility.Collapsed;
            aiButton.Visibility=showAi && !aiRunning?Visibility.Visible:Visibility.Collapsed;
            aiButton.IsEnabled=isGif?layer.EraseMode==0 && layer.AiPatch==null:layer.AiPatch==null;
            bool filled=layer.AiPatch!=null && (!isGif || layer.EraseMode==0);
            againButton.Visibility=showAi && !aiRunning && filled?Visibility.Visible:Visibility.Collapsed;
            aiButton.Visibility=showAi && !aiRunning && !filled?Visibility.Visible:Visibility.Collapsed;
            aiNote.Visibility=showAi && filled?Visibility.Visible:Visibility.Collapsed;
            if(gifEraseField!=null) {
                gifEraseField.Visibility=(erase || layer.HasEraseShapes)?Visibility.Visible:Visibility.Collapsed;
                fillOnce.IsChecked=layer.EraseMode!=1;fillEach.IsChecked=layer.EraseMode==1;
                gifModeText.Text=layer.HasEraseShapes?(layer.EraseMode==1?L.P("(background moves)","(배경이 움직여요)"):L.P("(background still)","(배경이 멈춰 있어요)")):"";
                gifPauseNote.Visibility=erase?Visibility.Visible:Visibility.Collapsed;
                bool each=layer.EraseMode==1;
                gifPauseNote.Text=each?L.P("1 Paint over it   2 Check this scene   3 [Fill every scene] or ▶ Play",
                    "1 지울 곳 칠하기   2 이 장면 확인   3 [모든 장면 메우기] 또는 ▶ 재생"):
                    L.P("1 Paint over it   2 The result shows   3 ▶ Play to check every scene","1 지울 곳 칠하기   2 결과가 바로 보여요   3 ▶ 재생으로 모든 장면 확인");
                fillAllButton.Visibility=erase && each && layer.HasEraseShapes?Visibility.Visible:Visibility.Collapsed;
            }
            foreach(var b in new[]{delete,front,back})b.Visibility=erase?Visibility.Collapsed:Visibility.Visible;
            undo.IsEnabled=layer.CanUndo;redo.IsEnabled=layer.CanRedo;delete.IsEnabled=layer.HasSelection;
            front.IsEnabled=layer.CanReorder(true);back.IsEnabled=layer.CanReorder(false);clear.IsEnabled=erase?layer.HasEraseShapes:layer.HasShapes;
            clear.Content=erase?L.P("Clear erased spots","지운 곳 모두 없애기"):L.P("Clear all","모두 지우기");
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
            if(key==Key.D0 || key==Key.NumPad0){if(!e.IsRepeat)layer.SelectTool(DrawPreferences.Last.AreaKind);return true;}
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
        // A long erase drag: the pieces already full (MaxPoints each), kept whole; added with the last piece as one undo step.
        readonly List<MotionShape> erasePieces=new List<MotionShape>();
        internal event Action ShapeLimit;
        bool limitTold;
        List<MotionShape> shapes=new List<MotionShape>();
        EraseAiPatch aiPatch;
        int eraseMode;
        int selected=-1,handle=-1;
        bool active,onlySelection,dragging,drawing;
        double pixelHeight=1000;
        Point start,anchor;
        MotionShape pending,dragShape;
        double sceneTime;
        int trackCount,pickOnClick=-1;
        internal Func<double> FixedSceneTime;
        internal Func<BitmapSource> ErasePicture;
        internal Func<IList<MotionShape>,BitmapSource> StaticErasePatch;
        internal Func<int> CurrentEraseSceneMs;
        internal event Action BeforePointer,PointerStarted,TrackLimit,EraseStrokesChanged;
        internal MotionShape SelectedShape {get {return HasSelection?shapes[selected]:null;}}
        internal EraseAiPatch AiPatch {get {return aiPatch;}}
        // The last AI fill, kept after new strokes drop it, so only the new strokes need filling on top of it.
        EraseAiPatch earlierAi;
        internal EraseAiPatch EarlierAi {get {return earlierAi;}}
        internal int EraseMode {get {return eraseMode;}}
        internal IList<MotionShape> Shapes {get {return shapes;}}
        internal bool HasEraseShapes {get {return shapes.Any(shape=>shape.IsErase);}}
        internal bool HasTracks {get {return trackCount!=0;}}
        // Parts that change the picture: the preview under them has to be redrawn with each scene.
        internal bool HasLiveAreas {get {return shapes.Any(AdjustAreas.Active);}}
        internal IEnumerable<MotionTrackPath> Tracks {get {return shapes.Where(shape=>shape.Track!=null).Select(shape=>shape.Track);}}

        internal void SetSceneTime(double time) {
            if(sceneTime==time)return;
            sceneTime=time;
            if(HasTracks || HasLiveAreas)InvalidateVisual();
        }
        internal bool ReplaceShape(MotionShape old,MotionShape replacement) {
            int index=shapes.IndexOf(old);
            if(index<0)return false;
            CancelDrag();Remember();shapes[index]=replacement;SaveChange();
            return true;
        }
        // Every drawing (and its followed path) moved to another frame of reference: the GIF's cut changed, and each
        // drawing stays on the same spot of the video. One undo step.
        internal void RemapAll(Func<Point,Point> point) {
            if(shapes.Count==0)return;
            CancelDrag();Remember();
            Func<Rect,Rect> box=r=>new Rect(point(r.TopLeft),point(r.BottomRight));
            for(int i=0;i<shapes.Count;i++) {
                var moved=shapes[i].WithPoints(Array.ConvertAll(shapes[i].Points,p=>{var q=point(p);return new Point(MotionShape.Clamp(q.X),MotionShape.Clamp(q.Y));}));
                if(moved.Track!=null)moved=moved.WithTrack(moved.Track.Mapped(box));
                shapes[i]=moved;
            }
            SaveChange();
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
        int DefaultThickness {get {return Tool=='h'?Defaults.MarkerThickness:Tool=='x'?Defaults.EraseThickness:Tool=='q'?Defaults.AreaThickness:Defaults.Thickness;}}
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
        internal bool IsArea {get {return MotionShape.AreaKinds.IndexOf(Kind)>=0;}}
        internal char AreaKind {get {return IsArea?Kind:Defaults.AreaKind;}}
        bool AreaSelected {get {return HasSelection && shapes[selected].IsArea;}}
        internal AdjustValues AreaValues {get {return AreaSelected?shapes[selected].Area:Defaults.AreaValues;}}
        internal int AreaFeather {get {return AreaSelected?shapes[selected].Feather:Defaults.AreaFeather;}}
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
            aiPatch=earlierAi=initialAi;
            if(aiPatch==null) {
                for(int i=0;i<shapes.Count;i++)
                    if(shapes[i].IsAiErase)shapes[i]=shapes[i].WithEraseMode(MotionShape.EraseStaticCSharp);
            }
            SaveValue();Update();
        }
        internal void SetInitialAiPatch(EraseAiPatch patch) {
            aiPatch=earlierAi=patch;
            if(patch==null) {
                for(int i=0;i<shapes.Count;i++)
                    if(shapes[i].IsAiErase)shapes[i]=shapes[i].WithEraseMode(MotionShape.EraseStaticCSharp);
                SaveValue();
            }
            Update();
        }
        internal void ApplyAiPatch(EraseAiPatch patch) {
            if(patch==null || !HasEraseShapes)return;
            if(aiPatch!=null && !String.IsNullOrEmpty(patch.CacheKey) && aiPatch.CacheKey==patch.CacheKey && aiPatch.Patch==patch.Patch)return;
            CancelDrag();Remember();
            eraseMode=0;
            int sceneMs=(int)Math.Max(0,Math.Min(0xFFFFFF,Math.Round(patch.SceneMs)));
            for(int i=0;i<shapes.Count;i++)
                if(shapes[i].IsErase)shapes[i]=shapes[i].WithEraseMode(MotionShape.EraseStaticAi,sceneMs);
            aiPatch=earlierAi=patch;
            SaveChange(true);
        }
        internal void InvalidateAiPatch() {
            earlierAi=null; // the picture under it changed
            if(aiPatch==null && !shapes.Any(s=>s.IsAiErase))return;
            aiPatch=null;
            for(int i=0;i<shapes.Count;i++)
                if(shapes[i].IsAiErase)shapes[i]=shapes[i].WithEraseMode(MotionShape.EraseStaticCSharp);
            SaveValue();Update();
        }
        internal void SetEraseMode(int mode,bool remember=true) {
            mode=mode==1?1:0;
            if(eraseMode==mode)return;
            CancelDrag();
            if(HasEraseShapes) {
                if(remember)Remember();
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
        // [0] Adjust part: box, ellipse or painted. A chosen box or ellipse changes into the other; otherwise the next part.
        internal void SelectAreaKind(char kind) {
            Defaults.AreaKind=kind;
            if(AreaSelected && kind!='q' && shapes[selected].Kind!='q') {
                if(shapes[selected].Kind==kind)return;
                CancelDrag();Remember();shapes[selected]=shapes[selected].WithKind(kind);
                Tool=Defaults.Tool=kind;Defaults.Save();SaveChange();return;
            }
            SelectTool(kind);
        }
        // A part's values. Holding a key changes them many times a second, so changes to the same part within a
        // second share one undo step.
        DateTime lastAreaEdit;int lastAreaIndex=-1;
        internal void SetArea(AdjustValues values,int feather) {
            values=values.Clamped();feather=Math.Max(0,Math.Min(100,feather));
            Defaults.AreaValues=values;Defaults.AreaFeather=feather;
            if(AreaSelected) {
                var shape=shapes[selected];
                if(shape.Area==values && shape.Feather==feather)return;
                CancelDrag();
                var now=DateTime.UtcNow;
                if(lastAreaIndex!=selected || (now-lastAreaEdit).TotalMilliseconds>1000 || undo.Count==0)Remember();else redo.Clear();
                lastAreaEdit=now;lastAreaIndex=selected;
                shapes[selected]=shape.WithArea(values,feather);SaveChange();return;
            }
            Update();
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
            var edit=from[from.Count-1];from.RemoveAt(from.Count-1);lastAreaIndex=-1;
            shapes=edit.Shapes;selected=edit.Selected;aiPatch=edit.AiPatch;eraseMode=edit.EraseMode;
            if(aiPatch!=null)earlierAi=aiPatch;
            SaveChange(true);
        }
        internal void Clear() {
            CancelDrag();
            if(!HasShapes)return;
            Remember();shapes.Clear();selected=-1;SaveChange();
        }
        // [Clear] while erasing: only the erased spots; drawings, mosaics and parts stay (Codex).
        internal void ClearErase() {
            CancelDrag();
            if(!HasEraseShapes)return;
            Remember();shapes.RemoveAll(shape=>shape.IsErase);selected=-1;SaveChange();
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
            } else if(Kind=='q') {
                if(width.HasValue)Defaults.AreaThickness=width.Value;
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
                    nextOpacity,nextBlock,shape.Points,shape.Track,shape.Area,shape.Feather);
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
            dragging=false;pending=null;points.Clear();erasePieces.Clear();limitTold=false;
            if(IsMouseCaptured)ReleaseMouseCapture();
        }
        MotionShape Rounded(MotionShape shape) {
            return shape.WithPoints(Array.ConvertAll(shape.Points,p=>new Point(Math.Round(MotionShape.Clamp(p.X),4),Math.Round(MotionShape.Clamp(p.Y),4))));
        }
        // The brush: a circle of its real size follows the mouse over the picture while erasing; changing the size
        // shows it in the middle for a moment (2026-11-27 user: "I can't see the size until I click").
        Point? hover;
        DateTime brushUntil=DateTime.MinValue;
        System.Windows.Threading.DispatcherTimer brushTimer;
        internal void ShowBrush() {
            if(!IsErase)return;
            brushUntil=DateTime.UtcNow.AddMilliseconds(900);InvalidateVisual();
            if(brushTimer==null) {
                brushTimer=new System.Windows.Threading.DispatcherTimer {Interval=TimeSpan.FromMilliseconds(950)};
                brushTimer.Tick+=delegate {brushTimer.Stop();InvalidateVisual();};
            }
            brushTimer.Stop();brushTimer.Start();
        }
        protected override void OnMouseLeave(MouseEventArgs e) {base.OnMouseLeave(e);if(hover!=null){hover=null;InvalidateVisual();}}
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
                bool anyArea=current.Any(AdjustAreas.Active);
                if(anyErase) {
                    if(StaticErasePatch!=null && eraseMode==0) {
                        var patch=aiPatch!=null && aiPatch.Patch!=null && pending==null?aiPatch.Patch:StaticErasePatch(current);
                        if(patch!=null)dc.DrawImage(patch,new Rect(0,0,ActualWidth,ActualHeight));
                        // Parts and mosaics need the erased picture under them.
                        if(patch!=null && (anyMosaic || anyArea) && MosaicPicture!=null) {
                            var under=MosaicPicture();
                            if(under!=null)workingPic=EraseStore.Composite(under,patch);
                        }
                    } else {
                        var basePic=ErasePicture!=null?ErasePicture():(MosaicPicture!=null?MosaicPicture():null);
                        if(basePic!=null) {
                            workingPic=aiPatch!=null && aiPatch.Patch!=null && pending==null?
                                EraseStore.Composite(basePic,aiPatch.Patch):CoverInpaint.ApplyHybrid(basePic,current);
                            if(!anyMosaic && !anyArea)dc.DrawImage(workingPic,new Rect(0,0,ActualWidth,ActualHeight));
                        }
                    }
                }
                if(anyMosaic || anyArea) {
                    var picture=workingPic??(MosaicPicture==null?null:MosaicPicture());
                    if(picture!=null)dc.DrawImage(MotionDraw.ApplyMosaics(AdjustAreas.Apply(picture,current),current),new Rect(0,0,ActualWidth,ActualHeight));
                    else foreach(var shape in current.Where(shape=>shape.IsMosaic))
                        MotionDraw.MosaicPlaceholder(dc,new Rect(ToScreen(shape.Points[0]),ToScreen(shape.Points[1])));
                }
                // Parts have no line of their own; while drawing is open a thin dashed line shows where each one is.
                if(active) {
                    var partLine=MotionDraw.Stroke(new SolidColorBrush(System.Windows.Media.Color.FromArgb(170,255,255,255)),1);
                    partLine.DashStyle=DashStyles.Dot;
                    foreach(var shape in current.Where(shape=>shape.IsArea)) {
                        if(shape.Kind=='q') {
                            double brush=MotionDraw.Pixels(shape,pixelHeight)*Scale;
                            dc.DrawGeometry(null,MotionDraw.Stroke(new SolidColorBrush(System.Windows.Media.Color.FromArgb(40,255,255,255)),brush),
                                MotionDraw.GeometryOf(shape,ActualWidth,ActualHeight,brush));
                        } else dc.DrawGeometry(null,partLine,ScreenGeometry(shape));
                    }
                }
                double width=ActualWidth/Scale;
                dc.PushTransform(new ScaleTransform(Scale,Scale));
                foreach(var shape in current)MotionDraw.Render(dc,shape,width,pixelHeight);
                dc.Pop();
                // What gets filled, as one flat see-through color with no line in the middle (2026-11-27 user: "is only the
                // white line erased, or the area around it?"). Drawn as one layer so overlapping strokes don't get darker.
                var painted=new List<MotionShape>();
                if(active && IsErase)painted.AddRange(current.Where(shape=>shape.IsErase));
                painted.AddRange(erasePieces);
                if(liveErase!=null)painted.Add(liveErase);
                if(painted.Count>0) {
                    dc.PushOpacity(liveErase!=null?0.55:0.22);
                    var tint=new SolidColorBrush(System.Windows.Media.Color.FromRgb(255,80,80));
                    foreach(var shape in painted) {
                        double brushPx=Math.Max(2,MotionDraw.Pixels(shape,pixelHeight)*Scale);
                        dc.DrawGeometry(null,MotionDraw.Stroke(tint,brushPx),MotionDraw.GeometryOf(shape,ActualWidth,ActualHeight,brushPx));
                    }
                    dc.Pop();
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
            if(active && IsErase) {
                double size=Math.Max(2,Math.Max(1.5,DefaultThickness*pixelHeight/1000.0)*Scale);
                var ring=new Pen(Brushes.White,1.5);var edge=new Pen(new SolidColorBrush(System.Windows.Media.Color.FromArgb(160,0,0,0)),3.5);
                Point? at=hover??(DateTime.UtcNow<brushUntil?new Point(ActualWidth/2,ActualHeight/2):(Point?)null);
                if(at!=null) {
                    dc.DrawEllipse(null,edge,at.Value,size/2,size/2);
                    dc.DrawEllipse(null,ring,at.Value,size/2,size/2);
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
                if(!shape.IsMosaic && !(shape.IsArea && shape.Kind!='q') && ((shape.Fill && geometry.FillContains(p)) || geometry.StrokeContains(hit,p)))return i;
            }
            for(int i=shapes.Count-1;i>=0;i--)
                if((shapes[i].IsMosaic || shapes[i].Kind=='g' || shapes[i].Kind=='o') && ScreenGeometry(shapes[i].At(sceneTime)).FillContains(p))return i;
            return -1;
        }
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) {
            if(!active)return;
            Focus();
            BeginDrag(e.GetPosition(this));
            e.Handled=true;
        }
        // A whole stroke given as points on the layer, as a mouse would draw it (the checks use this).
        internal void StrokeForCheck(IList<Point> path) {
            if(!active || path.Count==0)return;
            BeginDrag(path[0],false);
            for(int i=1;i<path.Count;i++)if(dragging)DragTo(path[i]);
            FinishDrag(null);
        }
        void BeginDrag(Point at,bool capture=true) {
            if(BeforePointer!=null)BeforePointer();
            start=at;anchor=ToPicture(start);handle=-1;
            if(HasSelection) {
                var handles=Handles(shapes[selected].At(sceneTime));
                for(int i=0;i<handles.Length;i++)
                    if((ToScreen(handles[i])-start).Length<=HandleRadius+3){handle=i;break;}
            }
            pickOnClick=-1;
            if(handle<0) {
                int hit=Tool=='x'?-1:ShapeAt(start);
                if(MotionShape.AreaKinds.IndexOf(Tool)>=0 && hit>=0 && hit!=selected && shapes[hit].IsArea){pickOnClick=hit;hit=-1;}
                selected=hit;
            }
            drawing=!HasSelection;
            dragShape=drawing?null:shapes[selected].At(sceneTime);
            if(PointerStarted!=null)PointerStarted();
            dragging=(!drawing || (shapes.Count<MotionShape.MaxShapes &&
                (Tool!='m' || shapes.Count(shape=>shape.IsMosaic)<MotionShape.MaxMosaics) &&
                (MotionShape.AreaKinds.IndexOf(Tool)<0 || shapes.Count(shape=>shape.IsArea)<MotionShape.MaxAreas))) && (!capture || CaptureMouse());
            Update();
        }
        protected override void OnMouseMove(MouseEventArgs e) {
            if(active && IsErase){hover=e.GetPosition(this);if(!dragging)InvalidateVisual();}
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
                    if(Tool=='x' && points.Count>=MotionShape.MaxPoints) {
                        // Erasing is never thinned out: a full piece is kept as it is and the drag goes on in a new one
                        // from the same point. At the limit of pieces the drag stops and says so.
                        if(shapes.Count+erasePieces.Count+2>MotionShape.MaxShapes) {
                            // Full: this piece stays at its size and the rest of the drag is not painted (said once per drag).
                            points.RemoveAt(points.Count-1);
                            if(!limitTold){limitTold=true;if(ShapeLimit!=null)ShapeLimit();}
                        } else {
                            erasePieces.Add(Rounded(NewEraseShape(points.ToArray())));
                            var last=points[points.Count-1];points.Clear();points.Add(last);
                        }
                    } else if(points.Count>MotionShape.MaxPoints*2) {
                        var simplified=MotionShape.Simplify(points);points.Clear();points.AddRange(simplified);
                    }
                    if(points.Count<2 && erasePieces.Count==0)return;
                    if(points.Count<2)points.Add(points[0]);
                }
                pending=Tool=='x'?NewEraseShape(points.ToArray()):
                    new MotionShape(Tool,Defaults.Color,DefaultThickness,MotionShape.CanFill(Tool)?Defaults.Fill:-1,DefaultOpacity,Defaults.Block,
                        MotionShape.IsPen(Tool)?points.ToArray():new[]{anchor,Constrain(Tool,anchor,p)},null,Defaults.AreaValues,Defaults.AreaFeather);
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
            FinishDrag(e.GetPosition(this));
        }
        void FinishDrag(Point? last) {
            if(!dragging)return;
            if(last!=null && ActualWidth>0 && ActualHeight>0)DragTo(last.Value);
            var final=pending;
            if(final!=null && MotionShape.IsPen(final.Kind) && drawing && !final.IsErase)
                final=final.WithPoints(MotionShape.Simplify(points));
            var pieces=drawing?erasePieces.ToList():new List<MotionShape>();
            EndDrag();
            if(final==null && drawing && pickOnClick>=0){selected=pickOnClick;pickOnClick=-1;Update();return;}
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
                foreach(var piece in pieces)shapes.Add(final.IsErase && eraseMode==0?piece.WithEraseMode(final.Block,final.EraseSceneMs):piece);
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
