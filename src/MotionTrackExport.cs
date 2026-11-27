using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;

namespace LoveMyShelf {
    internal sealed class MotionTrackExport {
        static readonly CultureInfo Invariant=CultureInfo.InvariantCulture;
        readonly List<MotionShape> shapes;
        readonly List<Overlay> overlays=new List<Overlay>();
        readonly MotionRequest request;
        readonly int width,height;
        readonly string folder;
        string inputs="";
        sealed class Overlay {
            internal int Input;
            internal MotionShape Shape;
            internal int Pad;
        }

        internal MotionTrackExport(List<MotionShape> shapes,MotionRequest request,int width,int height,string folder,CancellationToken token) {
            this.shapes=shapes;this.request=request;this.width=width;this.height=height;this.folder=folder;
            Directory.CreateDirectory(folder);
            var pending=new List<MotionShape>();
            foreach(var shape in shapes.Where(s=>!s.IsMosaic)) {
                if(shape.Track==null){pending.Add(shape);continue;}
                FlushStatic(pending);
                token.ThrowIfCancellationRequested();
                AddShape(shape);
            }
            FlushStatic(pending);
        }
        internal int NextInput {get {return overlays.Count+1;}}
        internal string Inputs {get {return inputs;}}

        void Add(string path,Overlay overlay) {
            overlay.Input=NextInput;overlays.Add(overlay);
            inputs+="-i "+VideoFrames.Quote(path)+" ";
        }
        void FlushStatic(List<MotionShape> pending) {
            if(pending.Count==0)return;
            string name="shapes-"+NextInput.ToString(Invariant)+".png";
            Add(MotionDraw.WritePng(pending,width,height,folder,name,width,height),new Overlay());
            pending.Clear();
        }
        void AddShape(MotionShape shape) {
            var path=shape.Track;
            var box=path.Keys[0].Box;
            // Keep a stroke-width margin around the texture, including at edges of the picture.
            int pad=(int)Math.Ceiling(MotionDraw.Pixels(shape,height)/2)+2;
            int boxW=Math.Max(1,(int)Math.Ceiling(box.Width*width)),boxH=Math.Max(1,(int)Math.Ceiling(box.Height*height));
            string name="shape-"+NextInput.ToString(Invariant)+".png";
            var local=shape.WithBox(new Rect(pad/(double)width,pad/(double)height,box.Width,box.Height));
            string png=MotionDraw.WritePng(new[]{local},width,height,folder,name,boxW+2*pad,boxH+2*pad);
            Add(png,new Overlay {Shape=shape,Pad=pad});
        }

        internal string Append(StringBuilder graph,string current,CancellationToken token) {
            int index=0;
            foreach(var shape in shapes.Where(s=>s.IsMosaic)) {
                string id=(++index).ToString(Invariant);
                token.ThrowIfCancellationRequested();
                current=shape.Track==null?MotionDraw.AppendMosaics(graph,current,new[]{shape},width,height,request.KeepAlpha,index-1):
                    AppendMosaic(graph,current,shape,id);
            }
            foreach(var item in overlays) {
                token.ThrowIfCancellationRequested();
                string id=item.Input.ToString(Invariant),input="["+id+":v]",output="[trackdraw"+id+"]";
                if(item.Shape==null) {
                    graph.Append(';').Append(current).Append(input).Append("overlay=0:0:eof_action=repeat:format=rgb").Append(output);
                } else {
                    var path=item.Shape.Track;
                    string x=Axis(path,b=>b.X,width),y=Axis(path,b=>b.Y,height);
                    graph.Append(';').Append(current).Append(input).Append("overlay=x='round(").Append(x).Append('-').Append(item.Pad)
                        .Append(")':y='round(").Append(y).Append('-').Append(item.Pad)
                        .Append(")':eval=frame:format=rgb:eof_action=repeat").Append(output);
                }
                current=output;
            }
            return current;
        }

        string AppendMosaic(StringBuilder graph,string current,MotionShape shape,string id) {
            if(width<4 || height<4)return current;
            var path=shape.Track;
            var first=path.Keys[0].Box;
            string w=F(EvenSize(first.Width,width)),h=F(EvenSize(first.Height,height));
            string name="crop@track"+id;
            graph.Append(';').Append(current).Append("split[trackbase").Append(id).Append("][trackbox").Append(id).Append("];[trackbox").Append(id).Append(']');
            string x=Position(path,b=>b.X,width,w),y=Position(path,b=>b.Y,height,h);
            string block=F(MotionDraw.MosaicBlock(shape,height));
            graph.Append(name).Append('=').Append(w).Append(':').Append(h).Append(":x='").Append(x).Append("':y='").Append(y)
                .Append("':exact=1,scale=w='max(1,trunc(iw/").Append(block).Append("))':h='max(1,trunc(ih/").Append(block).Append("))'");
            graph.Append(",scale=w=").Append(w).Append(":h=").Append(h).Append(":flags=neighbor");
            string output="[mosaic"+id+"]";
            graph.Append("[tracktile").Append(id).Append("];[trackbase").Append(id).Append("][tracktile").Append(id)
                .Append("]overlay=x='").Append(x).Append("':y='").Append(y).Append("':eval=frame:format=")
                .Append(request.KeepAlpha?"rgb":"auto").Append(output);
            return output;
        }

        internal static bool Variable(MotionTrackPath path) {
            var first=path.Keys[0].Box;
            return path.Keys.Any(k=>k.Box.Width!=first.Width || k.Box.Height!=first.Height);
        }
        static int EvenSize(double value,int side) {return Math.Max(4,Math.Min(side&~1,2*(int)Math.Floor(value*side/2+0.5000001)));}
        string Position(MotionTrackPath path,Func<Rect,double> get,int side,string size) {
            return "max(0,min("+F(side)+"-"+size+",2*floor(("+Axis(path,get,side)+")/2)))";
        }
        string Axis(MotionTrackPath path,Func<Rect,double> get,int pixels) {
            var keys=path.Keys;
            if(keys.All(k=>get(k.Box)==get(keys[0].Box)))return F(get(keys[0].Box)*pixels);
            // The lookup uses output PTS, but track keys are in forward source seconds.
            var times=Array.ConvertAll(keys,k=>k.Time/request.Speed);
            var values=Array.ConvertAll(keys,k=>get(k.Box)*pixels);
            return "if(lt(t,"+F(times[0])+"),"+F(values[0])+","+Expression(keys,times,values,0,keys.Length-1)+")";
        }
        static string Expression(MotionTrackKey[] keys,double[] times,double[] values,int a,int b) {
            if(a==b)return F(values[a]);
            if(b==a+1) {
                string first=F(values[a]),last=F(values[b]),time=F(times[b]-MotionTrackPath.TimeTolerance);
                string before=(keys[a].Lost && !keys[b].Fixed) || first==last?first:
                    first+"+("+last+"-"+first+")*(t-"+F(times[a])+")/"+F(times[b]-times[a]);
                return "if(lt(t,"+time+"),"+before+","+last+")";
            }
            int mid=(a+b)/2;
            return "if(lt(t,"+F(times[mid]-MotionTrackPath.TimeTolerance)+"),"+Expression(keys,times,values,a,mid)+
                ","+Expression(keys,times,values,mid,b)+")";
        }
        static string F(double n) {return n.ToString("0.#########",Invariant);}
    }
}
