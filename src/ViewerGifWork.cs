using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace LoveMyShelf {
    // GIF covers made in the background while watching (Enter). Nothing covers the video (2026-11-27 user: "다시는 동영상
    // 가리지마라", "모서리에 투명하게만 적어", "잠깐 뜨고 바로 사라져"): a short see-through corner note when it starts and
    // when it is done, and how far it is only in the player bar, which shows when the mouse goes to the bottom.
    public partial class Viewer {
        TextBlock gifBarText;
        readonly Dictionary<object,KeyValuePair<string,double>> gifJobs=new Dictionary<object,KeyValuePair<string,double>>();

        // The folder window tells the large view about its GIF work while this window is showing.
        internal bool CanShowGifWork {get {return IsVisible && WindowState!=WindowState.Minimized;}}
        internal void GifWorkStarted(object id,string name) {
            if(gifBarText==null) {
                gifBarText=UI.Text("",12,true);gifBarText.VerticalAlignment=VerticalAlignment.Center;gifBarText.Margin=new Thickness(10,0,4,0);
                gifBarText.Visibility=Visibility.Collapsed;
                controls.Children.Add(gifBarText);
            }
            gifJobs[id]=new KeyValuePair<string,double>(name,0);
            ShowGifWork();
            ShowCornerNote(L.P("Making GIF","GIF 만드는 중"));
        }
        internal void GifWorkProgress(object id,double share) {
            KeyValuePair<string,double> job;
            if(!gifJobs.TryGetValue(id,out job))return;
            gifJobs[id]=new KeyValuePair<string,double>(job.Key,Math.Max(0,Math.Min(1,share)));
            ShowGifWork();
        }
        // done: made (a short corner note says so); otherwise the folder window says what went wrong.
        internal void GifWorkEnded(object id,bool done) {
            KeyValuePair<string,double> job;
            if(!gifJobs.TryGetValue(id,out job))return;
            gifJobs.Remove(id);
            ShowGifWork();
            if(done)ShowCornerNote(L.P("GIF cover ready","GIF 표지 다 됐어요"));
        }
        // "GIF 40%" in the player bar, the names on hover.
        void ShowGifWork() {
            if(gifBarText==null)return;
            if(gifJobs.Count==0){gifBarText.Visibility=Visibility.Collapsed;return;}
            int percent=(int)Math.Round(gifJobs.Values.Average(j=>j.Value)*100);
            gifBarText.Text=(gifJobs.Count==1?"GIF":L.PF("GIF ×{0}","GIF {0}개",gifJobs.Count))+" "+percent+"%";
            gifBarText.ToolTip=String.Join(Environment.NewLine,gifJobs.Values.Select(j=>j.Key))+Environment.NewLine+
                L.P("Made in the background; this goes away when it's done.","뒤에서 만들고 있어요. 다 되면 사라져요.");
            gifBarText.Visibility=Visibility.Visible;
        }
    }
}
