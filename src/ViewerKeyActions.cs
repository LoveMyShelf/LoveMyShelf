using System;
using System.Windows;
using System.Windows.Input;

namespace LoveMyShelf {
    // What the PotPlayer-style keys do in the large view (ViewerKeys has the keys). 2026-11-27 user decisions:
    // PotPlayer's keys for what PotPlayer also does, LoveMyShelf's own keys stay one key.
    public partial class Viewer {
        // ----- Adjustments: R / T contrast, Y / U saturation, I / O color temperature, Ctrl+R sharpen, Q all back -----
        Key stepKey=Key.None;DateTime stepSince;
        // One step is the brightness step (1-10, default 3); held, it grows to 2 and then 4 steps (capped).
        void StepLook(int field,int direction,Key key,bool repeat) {
            var now=DateTime.UtcNow;
            if(!repeat || key!=stepKey){stepKey=key;stepSince=now;}
            double held=(now-stepSince).TotalMilliseconds;
            int step=(home==null?3:Math.Max(1,home.Settings.BrightnessStep))*(held<600?1:held<1500?2:4);
            int value=field==1?playbackAdjust.Contrast:field==2?playbackAdjust.Saturation:field==3?playbackAdjust.Temperature:playbackAdjust.Sharpness;
            SetPlaybackAdjust(field,value+direction*step);
            int shown=field==1?playbackAdjust.Contrast:field==2?playbackAdjust.Saturation:field==3?playbackAdjust.Temperature:playbackAdjust.Sharpness;
            string name=field==1?L.P("Contrast","대비"):field==2?L.P("Saturation","채도"):field==3?L.P("Color temp","색 온도"):L.P("Sharpness","선명하게");
            ShowCornerNote(name+" "+shown.ToString("+0;-0;0"));
        }
        // Ctrl+R, like PotPlayer's sharpen switch: off ↔ the strength used last (40 the first time).
        int lastSharpness=40;
        void ToggleSharpen() {
            if(playbackAdjust.Sharpness>0){lastSharpness=playbackAdjust.Sharpness;SetPlaybackAdjust(4,0);}
            else SetPlaybackAdjust(4,lastSharpness);
            ShowCornerNote(playbackAdjust.Sharpness>0?L.PF("Sharpen on ({0})","선명하게 켬 ({0})",playbackAdjust.Sharpness):L.P("Sharpen off","선명하게 끔"));
        }
        // Q: every adjustment back to normal; Q again brings back the look from just before (like [Reset] everywhere).
        object lookBeforeQ,lookAfterQ;
        internal bool CanUndoResetLook {get {return lookBeforeQ!=null && Equals(ReadLook(),lookAfterQ);}}
        void ToggleResetLook() {
            if(CanUndoResetLook) {
                var back=lookBeforeQ;lookBeforeQ=lookAfterQ=null;SetLook(back);
                ShowCornerNote(L.P("Adjustments back as they were","보정을 바로 전으로 되돌렸어요"));
                return;
            }
            var before=ReadLook();
            playbackBrightness=0;playbackStyle=Light.Soft;playbackAdjust=AdjustValues.None;brightnessChosen=adjustChosen=true;
            ApplyPlaybackBrightness();seekPreview.Brightness=PlaybackValue;
            var after=ReadLook();
            if(Equals(before,after)){lookBeforeQ=lookAfterQ=null;ShowCornerNote(L.P("Adjustments: normal","보정: 원래대로"));return;}
            lookBeforeQ=before;lookAfterQ=after;
            ShowCornerNote(L.P("All adjustments back to normal · Q again: undo","보정 모두 원래대로 · Q 한 번 더: 되돌리기"));
        }

        // ----- Turning and zoom -----
        // Alt+K, like PotPlayer: a quarter turn to the right each time.
        void TurnQuarter() {
            LeaveAreaView();
            if(!turning)StartTurning();
            turnSettle.Stop();turnSettle.Start();
            turn=((int)Math.Floor(turn/90.0)*90+90)%360;
            ApplyTurn();
            ShowCornerNote(turn==0?L.T("Rotation: normal"):L.F("Rotation {0}°",turn>180?turn-360:turn));
        }
        // Ctrl + number pad 4 6 8 2 move the picture by a tenth of the view; 5 puts it back in the middle.
        void PanBy(double x,double y) {
            LeaveAreaView();
            var area=ViewArea;
            if(x==0 && y==0)pan=new Vector();else pan+=new Vector(x*area.Width/10,y*area.Height/10);
            ApplyZoom();
        }

        // ----- Subtitle and sound timing (mpv sub-delay / audio-delay), for this video only -----
        double subtitleDelay,audioDelay;
        void ShiftSubtitles(double seconds) {
            if(mpv==null)return;
            subtitleDelay=seconds==0?0:Math.Round(subtitleDelay+seconds,2);
            mpv.SubtitleDelay=subtitleDelay;
            ShowCornerNote(subtitleDelay==0?L.P("Subtitle timing: normal","자막 싱크: 원래대로"):L.PF("Subtitles {0:+0.0;-0.0} s","자막 {0:+0.0;-0.0}초",subtitleDelay));
        }
        void ShiftSound(double seconds) {
            if(mpv==null)return;
            audioDelay=seconds==0?0:Math.Round(audioDelay+seconds,2);
            mpv.AudioDelay=audioDelay;
            ShowCornerNote(audioDelay==0?L.P("Sound timing: normal","소리 싱크: 원래대로"):L.PF("Sound {0:+0.0;-0.0} s","소리 {0:+0.0;-0.0}초",audioDelay));
        }
        // A new file starts with its own timing.
        void ResetTimings() {
            if(subtitleDelay!=0 || audioDelay!=0){subtitleDelay=audioDelay=0;if(mpv!=null){mpv.SubtitleDelay=0;mpv.AudioDelay=0;}}
        }

        // ----- The GIF cover part: \ repeat on / off (like PotPlayer's A-B repeat) -----
        bool partRepeat=true;
        void TogglePartRepeat() {
            double from,to;
            if(!PartIsHere || !PartFor(partFile,out from,out to)) {
                ShowCornerNote(L.P("No part is set · [ start, ] end","구간이 없어요 · [ 시작, ] 끝"));return;
            }
            partRepeat=!partRepeat;
            if(partRepeat){partInside=true;partJump.Restart();SeekTo(from);}
            ShowCornerNote(partRepeat?L.P("Part repeat on","구간 반복 켬"):L.P("Part repeat off · the part stays marked","구간 반복 끔 · 구간 표시는 그대로"));
        }

        // Ctrl+T, like PotPlayer: this window stays above the others.
        void ToggleOnTop() {
            Topmost=!Topmost;
            ShowCornerNote(Topmost?L.P("Always on top: on","항상 위: 켬"):L.P("Always on top: off","항상 위: 끔"));
        }
    }
}
