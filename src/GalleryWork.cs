using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace LoveMyShelf {
    // Work that runs in the background (a GIF cover being made, a new file being saved, …): one list for the whole app.
    // 2026-11-27 user: "gif 만들기 같은 작업을 하면 창 어디에라도 하단에라도 % 표시해. 언제까지 어떻게 기다려야 하는데?"
    //   var job=BackgroundWork.Start(L.P("Making GIF","GIF 만드는 중"),file name);  job.Progress(0..1);  job.End();
    internal static class BackgroundWork {
        internal sealed class Job {
            internal string What,Name;
            internal double Share;
            internal readonly Stopwatch Clock=Stopwatch.StartNew();
            internal void Progress(double share) {
                share=Math.Max(0,Math.Min(1,share));
                if(share<=Share)return;
                // Listeners hear about whole-percent steps only, so a fast worker cannot flood the screen thread.
                bool step=Math.Floor(share*100)>Math.Floor(Share*100) || share>=1;
                Share=share;if(step)Raise();
            }
            internal void End() {
                lock(jobs){if(!jobs.Remove(this))return;}
                Raise();
            }
            // Time left from the speed so far; null while too early to tell.
            internal TimeSpan? Left {get {
                double seconds=Clock.Elapsed.TotalSeconds;
                if(Share<0.03 || seconds<2)return null;
                return TimeSpan.FromSeconds(Math.Max(1,seconds/Share*(1-Share)));
            }}
        }
        static readonly List<Job> jobs=new List<Job>();
        // Raised on any thread; listeners go to their own thread.
        internal static event Action Changed;
        static void Raise() {var handler=Changed;if(handler!=null)handler();}
        internal static Job Start(string what,string name) {
            var job=new Job {What=what,Name=name};
            lock(jobs)jobs.Add(job);
            Raise();
            return job;
        }
        internal static List<Job> Now {get {lock(jobs)return jobs.ToList();}}
        internal static string LeftText(TimeSpan left) {
            return left.TotalSeconds<60?L.PF("about {0} s left","약 {0}초 남음",(int)Math.Ceiling(left.TotalSeconds))
                :L.PF("about {0} min left","약 {0}분 남음",(int)Math.Ceiling(left.TotalMinutes));
        }
    }

    // The folder window's bottom bar shows that work, in the group at its right (where the file queue's button is),
    // only while there is some: "GIF 만드는 중 45% · 약 30초 남음", or "작업 2개 · 45%"; a click lists them.
    // Upscaling in this window counts too. Nothing covers the pictures and no row is added.
    public partial class GalleryWindow {
        Button workButton;
        readonly DispatcherTimer workTick=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(700)};

        void SetupWork(Panel footerRight) {
            workButton=UI.Btn("",OpenWorkList);workButton.Visibility=Visibility.Collapsed;
            workButton.Margin=new Thickness(3,0,0,0);
            footerRight.Children.Insert(0,workButton);
            Action later=()=>{if(!Dispatcher.HasShutdownStarted)Dispatcher.BeginInvoke(new Action(ShowWork));};
            Action changed=later;
            BackgroundWork.Changed+=changed;
            Action<string> upscale=folder=>later();
            Upscales.Changed+=upscale;
            workTick.Tick+=delegate {ShowWork();};
            Closed+=delegate {workClosed=true;BackgroundWork.Changed-=changed;Upscales.Changed-=upscale;workTick.Stop();};
            // Work that started before this window opened shows at once.
            Loaded+=delegate {ShowWork();};
        }
        bool workClosed;
        // What is running now, as lines: (what, file, share, time left).
        List<Tuple<string,string,double,TimeSpan?>> WorkNow() {
            var list=BackgroundWork.Now.Select(j=>Tuple.Create(j.What,j.Name,j.Share,j.Left)).ToList();
            string folder=Upscales.RunningFolder;
            if(folder!=null) {
                int percent;Upscales.Stage(folder,out percent);
                list.Add(Tuple.Create(L.P("Upscaling","업스케일링"),Upscales.RunningName??"",percent/100.0,(TimeSpan?)null));
            }
            return list;
        }
        internal string WorkText {get {return workButton!=null && workButton.Visibility==Visibility.Visible?workButton.Content as string:null;}}
        void ShowWork() {
            if(workButton==null || workClosed)return;
            var now=WorkNow();
            var was=workButton.Visibility;
            if(now.Count==0) {
                workButton.Visibility=Visibility.Collapsed;workTick.Stop();
            } else {
                int percent=(int)Math.Floor(now.Average(w=>w.Item3)*100);
                string text;
                if(now.Count==1) {
                    var one=now[0];
                    text=one.Item1+" "+percent+"%"+(one.Item4.HasValue?" · "+BackgroundWork.LeftText(one.Item4.Value):"");
                } else text=L.PF("{0} jobs","작업 {0}개",now.Count)+" · "+percent+"%";
                workButton.Content=text;
                workButton.ToolTip=String.Join(Environment.NewLine,now.Select(w=>w.Item1+" · "+w.Item2+" · "+(int)Math.Floor(w.Item3*100)+"%"))
                    +Environment.NewLine+L.P("Click to see the list.","누르면 목록이 나와요.");
                workButton.Visibility=Visibility.Visible;
                if(!workTick.IsEnabled)workTick.Start();
            }
            if(was!=workButton.Visibility && updateFooter!=null)updateFooter();
        }
        void OpenWorkList() {
            var menu=new ContextMenu {PlacementTarget=workButton,Placement=System.Windows.Controls.Primitives.PlacementMode.Top};
            foreach(var w in WorkNow())
                menu.Items.Add(new MenuItem {Header=(w.Item1+" · "+w.Item2+" · "+(int)Math.Floor(w.Item3*100)+"%"+(w.Item4.HasValue?" · "+BackgroundWork.LeftText(w.Item4.Value):"")).Replace("_","__"),IsEnabled=false});
            if(menu.Items.Count==0)return;
            menu.IsOpen=true;
        }
    }
}
