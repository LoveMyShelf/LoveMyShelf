using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;

namespace LoveMyShelf {
    internal struct MotionTrackKey {
        internal readonly double Time;
        internal readonly Rect Box;
        internal readonly bool Fixed,Lost;
        internal MotionTrackKey(double time,Rect box,bool fixedByUser,bool lost) {
            Time=time;Box=box;Fixed=fixedByUser;Lost=lost;
        }
    }

    internal sealed class MotionTrackPath {
        internal const int MaxKeys=600;
        internal const double TimeTolerance=0.0000005;
        static readonly CultureInfo Invariant=CultureInfo.InvariantCulture;
        internal readonly MotionTrackKey[] Keys;
        internal readonly bool FollowSize;

        MotionTrackPath(MotionTrackKey[] keys,bool followSize) {Keys=keys;FollowSize=followSize;}
        internal MotionTrackPath WithSize(bool size) {return new MotionTrackPath(Keys,size);}

        int IndexAt(double time) {
            int lo=0,hi=Keys.Length;
            while(lo<hi) {
                int mid=(lo+hi)/2;
                if(Keys[mid].Time<=time+TimeTolerance)lo=mid+1;
                else hi=mid;
            }
            return Math.Max(0,lo-1);
        }
        internal MotionTrackKey At(double time) {
            int i=IndexAt(time);
            var a=Keys[i];
            if(i==Keys.Length-1 || time<=a.Time)return a;
            var b=Keys[i+1];
            if(a.Lost && !b.Fixed)return a;
            return new MotionTrackKey(time,Between(a,b,time),false,a.Lost);
        }
        internal bool FixedAt(double time) {
            var key=Keys[IndexAt(time)];
            return key.Fixed && Math.Abs(key.Time-time)<=TimeTolerance;
        }
        internal IEnumerable<Tuple<int,int>> LostRanges(double step,int total) {
            for(int i=0;i<Keys.Length;i++) {
                if(!Keys[i].Lost)continue;
                int from=i==0?0:(int)Math.Ceiling((Keys[i].Time-TimeTolerance)/step);
                int end=i==Keys.Length-1?total:(int)Math.Ceiling((Keys[i+1].Time-TimeTolerance)/step);
                if(from<total && from<end)yield return Tuple.Create(from,Math.Min(total,end));
            }
        }

        internal double NextFixed(double time) {
            for(int i=IndexAt(time)+1;i<Keys.Length;i++)if(Keys[i].Fixed)return Keys[i].Time;
            return Double.PositiveInfinity;
        }
        internal MotionTrackPath Correct(double time,Rect box) {
            var keys=Keys.Where(key=>Math.Abs(key.Time-time)>TimeTolerance).ToList();
            keys.Add(new MotionTrackKey(time,box,true,false));
            return Create(keys,FollowSize);
        }
        internal MotionTrackPath ReplaceForward(double from,double to,IEnumerable<MotionTrackKey> tracked) {
            return Create(Keys.Where(k=>k.Time<from-TimeTolerance || k.Time>=to-TimeTolerance)
                .Concat(tracked),FollowSize);
        }
        internal MotionTrackPath ForPart(double shift,double length) {
            double end=shift+length;
            var keys=Keys.Where(k=>k.Time>=shift-TimeTolerance && k.Time<=end+TimeTolerance)
                .Select(k=>new MotionTrackKey(Math.Max(0,Math.Min(length,k.Time-shift)),k.Box,k.Fixed,k.Lost)).ToList();
            // Outside the tracked span At() already holds its nearest endpoint.
            if(keys.Count==0 || (shift>Keys[0].Time && keys[0].Time>TimeTolerance)) {
                var first=At(shift);
                keys.Insert(0,new MotionTrackKey(0,first.Box,false,first.Lost));
            }
            if(end<Keys[Keys.Length-1].Time && length-keys[keys.Count-1].Time>TimeTolerance) {
                var last=At(end);
                keys.Add(new MotionTrackKey(length,last.Box,false,last.Lost));
            }
            // Each interpolated boundary within the span replaced a clipped key.
            return new MotionTrackPath(keys.ToArray(),FollowSize);
        }
        // Every box moved to another frame of reference (the GIF's cut changed: MotionCrop.cs).
        internal MotionTrackPath Mapped(Func<Rect,Rect> map) {
            return new MotionTrackPath(Array.ConvertAll(Keys,k=>new MotionTrackKey(k.Time,map(k.Box),k.Fixed,k.Lost)),FollowSize);
        }
        internal MotionTrackPath Flip(bool x,bool y) {
            return new MotionTrackPath(Array.ConvertAll(Keys,k=>new MotionTrackKey(k.Time,
                new Rect(x?Math.Max(0,1-k.Box.Right):k.Box.X,y?Math.Max(0,1-k.Box.Bottom):k.Box.Y,k.Box.Width,k.Box.Height),
                k.Fixed,k.Lost)),FollowSize);
        }

        static Rect Between(MotionTrackKey a,MotionTrackKey b,double time) {
            double t=MotionShape.Clamp((time-a.Time)/(b.Time-a.Time));
            return new Rect(a.Box.X+(b.Box.X-a.Box.X)*t,a.Box.Y+(b.Box.Y-a.Box.Y)*t,
                a.Box.Width+(b.Box.Width-a.Box.Width)*t,a.Box.Height+(b.Box.Height-a.Box.Height)*t);
        }
        sealed class Span {
            internal int First,Last,Split;
            internal double Error;
        }
        static void AddSpan(List<Span> spans,MotionTrackKey[] keys,int first,int last) {
            if(last-first<2 || (keys[first].Lost && !keys[last].Fixed))return;
            var span=new Span {First=first,Last=last,Error=0.002,Split=-1};
            for(int i=first+1;i<last;i++) {
                Rect box=keys[i].Box,guess=Between(keys[first],keys[last],keys[i].Time);
                double error=Math.Max(Math.Max(Math.Abs(box.X-guess.X),Math.Abs(box.Y-guess.Y)),
                    Math.Max(Math.Abs(box.Width-guess.Width),Math.Abs(box.Height-guess.Height)));
                if(error>span.Error){span.Error=error;span.Split=i;}
            }
            if(span.Split>=0)spans.Add(span);
        }
        // Keep all manual keys and both sides of every lost transition. Use the remaining budget
        // for the largest interpolation errors first.
        internal static MotionTrackPath Create(IEnumerable<MotionTrackKey> frames,bool size,double seed=Double.NaN) {
            var keys=frames.OrderBy(k=>k.Time).ToArray();
            if(keys.Length==0)return null;
            var keep=new bool[keys.Length];
            int kept=0,previous=-1;
            var spans=new List<Span>();
            for(int i=0;i<keys.Length;i++) {
                bool anchor=Math.Abs(keys[i].Time-seed)<=TimeTolerance;
                if(i!=0 && i!=keys.Length-1 && !keys[i].Fixed && !anchor &&
                    keys[i].Lost==keys[i-1].Lost && keys[i].Lost==keys[i+1].Lost)continue;
                keep[i]=true;kept++;
                if(previous>=0)AddSpan(spans,keys,previous,i);
                previous=i;
            }
            if(kept>MaxKeys)return null;
            while(kept<MaxKeys && spans.Count>0) {
                int best=0;
                for(int i=1;i<spans.Count;i++)if(spans[i].Error>spans[best].Error)best=i;
                var span=spans[best];spans.RemoveAt(best);
                keep[span.Split]=true;kept++;
                AddSpan(spans,keys,span.First,span.Split);
                AddSpan(spans,keys,span.Split,span.Last);
            }
            return new MotionTrackPath(keys.Where((key,i)=>keep[i]).ToArray(),size);
        }

        internal void Write(StringBuilder text) {
            text.Append(",@");
            if(FollowSize)text.Append("s;");
            for(int i=0;i<Keys.Length;i++) {
                if(i>0)text.Append(';');
                var key=Keys[i];var box=key.Box;
                text.AppendFormat(Invariant,"{0:0.######}:{1:0.0000}:{2:0.0000}:{3:0.0000}:{4:0.0000}",
                    key.Time,box.X,box.Y,Math.Max(0.0001,box.Width),Math.Max(0.0001,box.Height));
                if(key.Fixed)text.Append(":*");
                if(key.Lost)text.Append(":!");
            }
        }
        internal static MotionTrackPath Read(string text) {
            var entries=text.Split(';');
            bool size=entries[0]=="s";
            int start=size?1:0;
            if(entries.Length-start<1 || entries.Length-start>MaxKeys)return null;
            var keys=new List<MotionTrackKey>();
            double previous=-1;
            for(int i=start;i<entries.Length;i++) {
                var fields=entries[i].Split(':');
                double time,x,y,w,h;
                if(fields.Length<5 || fields.Length>7 || !MotionShape.Number(fields[0],out time) || time<0 || time<=previous ||
                    !MotionShape.Number(fields[1],out x) || !MotionShape.Number(fields[2],out y) ||
                    !MotionShape.Number(fields[3],out w) || !MotionShape.Number(fields[4],out h) ||
                    x<0 || y<0 || w<=0 || h<=0 || x+w>1.00010001 || y+h>1.00010001)return null;
                bool fixedByUser=false,lost=false;
                for(int j=5;j<fields.Length;j++) {
                    if(fields[j]=="*" && !fixedByUser && !lost)fixedByUser=true;
                    else if(fields[j]=="!" && !lost)lost=true;
                    else return null;
                }
                keys.Add(new MotionTrackKey(time,new Rect(x,y,w,h),fixedByUser,lost));
                previous=time;
            }
            return new MotionTrackPath(keys.ToArray(),size);
        }
    }
}

