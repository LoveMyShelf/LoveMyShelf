using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using System.Xml.Linq;

namespace LoveMyShelf {
    // The large view's shortcuts, which people can change in Guide & Options → Advanced.
    // Only the key that does each thing changes: the guide, the shortcut list, tooltips and notices keep showing
    // the default keys (a decision made on 2026-11-25: few people change them, and the Advanced page says so).
    internal static class ViewerKeys {
        internal sealed class Command {
            public string Id,En,Ko;public Key Default;
            // Shift, Ctrl or Alt that always go with this action's key (Shift + ← is a minute, like PotPlayer). Changing
            // the key in Advanced keeps them.
            public ModifierKeys Mods;
            // Other keys that do the same thing while this action still has its default key (the number pad's /).
            public Key[] Also=new Key[0];
            public string Name {get {return L.Korean?Ko:En;}}
        }
        // In the order of the shortcut list in the guide, so they are easy to find.
        // 2026-11-27 user: "단축키는 팟플레이어랑 그냥 똑같이 하고 겹치는 거 기존 거는 그냥 다른 버튼이나 오른쪽 클릭했을 때 뜨는
        // 그 설정으로 빼". Keys PotPlayer has are PotPlayer's (Shift, Ctrl and Alt allowed); keys only LoveMyShelf has are one
        // key, with Shift at most ("우리가 새로 만드는 거 Alt 컨트롤 절대 붙이지 마라"). Capture stays /, the GIF part is
        // \ repeat on/off, Backspace clear and Enter make; small turns are in the controls window.
        // Brightness style is 4 (2026-11-28 user: "단축키 숫자 4에 이거 밝기 방식 바꾸는 거 넣어줘").
        internal static readonly Command[] All={
            A("previous","Previous file","이전 파일",Key.K),
            A("next","Next file","다음 파일",Key.L),
            A("play","Play / pause","재생 / 일시정지",Key.Space),
            A("back","Skip back","뒤로 건너뛰기",Key.Left),
            A("forward","Skip forward","앞으로 건너뛰기",Key.Right),
            M("back30","30 seconds back","30초 뒤로",ModifierKeys.Control,Key.Left),
            M("forward30","30 seconds forward","30초 앞으로",ModifierKeys.Control,Key.Right),
            M("back60","1 minute back","1분 뒤로",ModifierKeys.Shift,Key.Left),
            M("forward60","1 minute forward","1분 앞으로",ModifierKeys.Shift,Key.Right),
            M("back300","5 minutes back","5분 뒤로",ModifierKeys.Control|ModifierKeys.Alt,Key.Left),
            M("forward300","5 minutes forward","5분 앞으로",ModifierKeys.Control|ModifierKeys.Alt,Key.Right),
            A("sceneBack","One scene back","한 장면 뒤로",Key.D),
            A("sceneForward","One scene forward","한 장면 앞으로",Key.F),
            A("brightnessReset","All adjustments back to normal (again: undo)","보정 모두 원래대로 (한 번 더: 되돌리기)",Key.Q),
            A("darker","Darker","어둡게",Key.W),
            A("brighter","Brighter","밝게",Key.E),
            A("contrastDown","Less contrast","대비 낮게",Key.R),
            A("contrastUp","More contrast","대비 높게",Key.T),
            A("saturationDown","Less saturation","채도 낮게",Key.Y),
            A("saturationUp","More saturation","채도 높게",Key.U),
            A("cooler","Cooler color","색 온도 차갑게",Key.I),
            A("warmer","Warmer color","색 온도 따뜻하게",Key.O),
            M("sharpen","Sharpen on / off","선명하게 켜기 / 끄기",ModifierKeys.Control,Key.R),
            A("autoAdjust","Auto adjust from this scene (again: undo)","이 장면 기준 자동 보정 (한 번 더: 되돌리기)",Key.OemPlus),
            A("brightnessStyle","Brightness style: Soft → See in the dark → Milky","밝기 방식 바꾸기: 부드럽게 → 어두운 곳 보기 → 뽀얗게",Key.D4,Key.NumPad4),
            A("speedReset","Normal speed","원래 속도",Key.Z),
            A("slower","Slower","느리게",Key.X),
            A("faster","Faster","빠르게",Key.C),
            A("double","2× speed and back","2배속 켜기 / 끄기",Key.V),
            M("turn90","Turn a quarter","90도씩 돌리기",ModifierKeys.Alt,Key.K),
            A("zoomIn","Zoom in","확대",Key.Add,Key.NumPad9),
            A("zoomOut","Zoom out","축소",Key.Subtract,Key.NumPad1),
            A("zoomReset","Zoom back to normal","확대 원래대로",Key.NumPad5),
            M("panLeft","Move the picture left","화면 왼쪽으로",ModifierKeys.Control,Key.NumPad4),
            M("panRight","Move the picture right","화면 오른쪽으로",ModifierKeys.Control,Key.NumPad6),
            M("panUp","Move the picture up","화면 위로",ModifierKeys.Control,Key.NumPad8),
            M("panDown","Move the picture down","화면 아래로",ModifierKeys.Control,Key.NumPad2),
            M("panReset","Picture back to the middle","화면 가운데로",ModifierKeys.Control,Key.NumPad5),
            A("volumeUp","Volume up","볼륨 올리기",Key.Up),
            A("volumeDown","Volume down","볼륨 내리기",Key.Down),
            A("mute","Sound off / on","소리 끄기 / 켜기",Key.M),
            A("clearVoice","Clear voice on / off","목소리 또렷하게 켜기 / 끄기",Key.N),
            A("subEarlier","Subtitles 0.5 s earlier","자막 0.5초 빠르게",Key.OemComma),
            A("subLater","Subtitles 0.5 s later","자막 0.5초 느리게",Key.OemPeriod),
            A("subReset","Subtitle timing back to normal","자막 싱크 원래대로",Key.OemSemicolon),
            M("audioEarlier","Sound 0.1 s earlier","소리 0.1초 빠르게",ModifierKeys.Shift,Key.OemComma),
            M("audioLater","Sound 0.1 s later","소리 0.1초 느리게",ModifierKeys.Shift,Key.OemPeriod),
            M("audioReset","Sound timing back to normal","소리 싱크 원래대로",ModifierKeys.Shift,Key.OemQuestion),
            A("capture","Capture this scene","지금 장면 캡처",Key.OemQuestion,Key.Divide),
            A("partStart","Start of the GIF cover part","GIF 표지 구간 시작",Key.OemOpenBrackets),
            A("partEnd","End of the GIF cover part","GIF 표지 구간 끝",Key.OemCloseBrackets),
            A("partCycle","Part: start → end (repeats) → clear","구간: 시작 → 끝(바로 반복) → 지우기",Key.Oem5,Key.OemBackslash),
            A("partClear","Clear the GIF cover part","GIF 표지 구간 지우기",Key.Back),
            A("quickGif","Make a GIF cover from the part in the background","구간을 GIF 표지로 뒤에서 바로 만들기",Key.Return),
            A("info","File information","파일 정보",Key.Tab),
            M("onTop","Always on top on / off","항상 위 켜기 / 끄기",ModifierKeys.Control,Key.T),
            A("close","Close","닫기",Key.F4),
            A("recycle","Move the original to the Recycle Bin","원본을 휴지통으로 보내기",Key.Delete),
        };
        static Command M(string id,string en,string ko,ModifierKeys mods,Key key) {return new Command {Id=id,En=en,Ko=ko,Default=key,Mods=mods};}
        static Command A(string id,string en,string ko,Key key,params Key[] also) {return new Command {Id=id,En=en,Ko=ko,Default=key,Also=also};}

        // Keys that always keep their own job in the large view, so nobody can lose the way out or the guide.
        internal static readonly Key[] Fixed={Key.Escape,Key.F1,Key.F11};
        internal static bool CanUse(Key key) {
            if(Fixed.Contains(key))return false;
            switch(key) {
                case Key.None:case Key.LeftShift:case Key.RightShift:case Key.LeftCtrl:case Key.RightCtrl:case Key.LeftAlt:case Key.RightAlt:
                case Key.LWin:case Key.RWin:case Key.System:case Key.ImeProcessed:case Key.DeadCharProcessed:case Key.Apps:
                case Key.CapsLock:case Key.NumLock:case Key.Scroll:case Key.PrintScreen:case Key.Pause:
                case Key.HangulMode:case Key.HanjaMode:case Key.ImeConvert:case Key.ImeNonConvert:
                    return false;
            }
            return true;
        }

        // The map the large view reads: key with its Shift / Ctrl / Alt → action id. Rebuilt whenever the options are
        // loaded or changed.
        static Dictionary<int,string> map=Build(new Dictionary<string,Key>());
        internal static void Use(Preferences prefs) {map=Build(prefs!=null?prefs.Shortcuts:new Dictionary<string,Key>());}
        static int Chord(Key key,ModifierKeys mods) {return (int)key | ((int)(mods&(ModifierKeys.Shift|ModifierKeys.Control|ModifierKeys.Alt))<<16);}
        // The key as pressed now (with the Shift, Ctrl and Alt held).
        internal static string ActionFor(Key key) {return ActionFor(key,Keyboard.Modifiers);}
        internal static string ActionFor(Key key,ModifierKeys mods) {string id;return map.TryGetValue(Chord(key,mods),out id)?id:null;}

        internal static Key KeyOf(Dictionary<string,Key> changed,Command action) {
            Key key;return changed!=null && changed.TryGetValue(action.Id,out key)?key:action.Default;
        }
        static Dictionary<int,string> Build(Dictionary<string,Key> changed) {
            var result=new Dictionary<int,string>();
            foreach(var action in All)result[Chord(KeyOf(changed,action),action.Mods)]=action.Id;
            // The extra keys only while the action has its own key and nothing else took them.
            foreach(var action in All)
                if(KeyOf(changed,action)==action.Default)
                    foreach(var also in action.Also)if(!result.ContainsKey(Chord(also,action.Mods)))result[Chord(also,action.Mods)]=action.Id;
            return result;
        }
        // The action that already uses this key (other than the one being changed), or null. Counts the extra keys
        // still working (the number pad's /), so taking one of them asks first too.
        internal static Command UsedBy(Dictionary<string,Key> changed,Key key,Command except) {
            string id;
            if(!Build(changed).TryGetValue(Chord(key,except.Mods),out id))return null;
            var owner=All.First(a=>a.Id==id);
            return owner==except?null:owner;
        }
        // Jobs that happen once per press. A key passed on from another window loses "held down", so those
        // windows drop the repeats of these themselves.
        static readonly HashSet<string> once=new HashSet<string>{"play","mute","double","clearVoice","recycle","info","partStart","partEnd","partClear","capture",
            "brightnessReset","brightnessStyle","sharpen","autoAdjust","turn90","zoomReset","panReset","subReset","audioReset","partCycle","quickGif","onTop","close"};
        internal static bool OncePerPress(string id) {return id!=null && once.Contains(id);}
        // True when this is one of the job's first keys (what the large view has always used).
        internal static bool IsFirstKey(string id,Key key) {
            var action=All.FirstOrDefault(a=>a.Id==id);
            return action!=null && (action.Default==key || action.Also.Contains(key));
        }
        // Gives the action a key. A key equal to the default is not stored, so the file keeps only real changes.
        internal static void Set(Dictionary<string,Key> changed,Command action,Key key) {
            if(key==action.Default)changed.Remove(action.Id);else changed[action.Id]=key;
        }

        // The key with the Shift / Ctrl / Alt that go with this action: "Ctrl+Alt+←".
        internal static string Label(Command action,Key key) {
            string mods=((action.Mods&ModifierKeys.Control)!=0?"Ctrl+":"")+((action.Mods&ModifierKeys.Alt)!=0?"Alt+":"")+((action.Mods&ModifierKeys.Shift)!=0?"Shift+":"");
            return mods+Label(key);
        }
        // How a key is written on its chip: the same signs as on the keyboard and in the guide.
        internal static string Label(Key key) {
            switch(key) {
                case Key.Left:return "←";case Key.Right:return "→";case Key.Up:return "↑";case Key.Down:return "↓";
                case Key.OemQuestion:return "/";case Key.Divide:return "Num /";case Key.Multiply:return "Num *";
                case Key.Subtract:return "Num −";case Key.Add:return "Num +";case Key.Decimal:return "Num .";
                case Key.OemOpenBrackets:return "[";case Key.OemCloseBrackets:return "]";
                case Key.Oem5:case Key.OemBackslash:return "\\";
                case Key.OemComma:return ",";case Key.OemPeriod:return ".";case Key.OemSemicolon:return ";";
                case Key.OemQuotes:return "'";case Key.OemMinus:return "-";case Key.OemPlus:return "=";case Key.OemTilde:return "`";
                case Key.Back:return "Backspace";case Key.Return:return "Enter";case Key.PageUp:return "PageUp";case Key.Next:return "PageDown";
                case Key.Insert:return "Insert";case Key.Home:return "Home";case Key.End:return "End";
                case Key.Space:return "Space";
            }
            if(key>=Key.D0 && key<=Key.D9)return ((int)(key-Key.D0)).ToString();
            if(key>=Key.NumPad0 && key<=Key.NumPad9)return "Num "+(int)(key-Key.NumPad0);
            return key.ToString();
        }

        internal static Dictionary<string,Key> Read(XElement xml) {
            var result=new Dictionary<string,Key>(StringComparer.Ordinal);
            var group=xml.Element("shortcuts");
            if(group==null)return result;
            foreach(var item in group.Elements("key")) {
                string id=(string)item.Attribute("action");Key key;
                var action=All.FirstOrDefault(a=>a.Id==id);
                if(action!=null && Enum.TryParse((string)item.Attribute("key"),out key) && Enum.IsDefined(typeof(Key),key) && CanUse(key) && key!=action.Default)result[id]=key;
            }
            // Two jobs on one key can only come from a hand-edited file: then every key goes back to the first ones.
            if(All.Select(a=>Chord(KeyOf(result,a),a.Mods)).Distinct().Count()!=All.Length)result.Clear();
            return result;
        }
        internal static XElement Xml(Dictionary<string,Key> changed) {
            if(changed==null || changed.Count==0)return null;
            return new XElement("shortcuts",All.Where(a=>changed.ContainsKey(a.Id))
                .Select(a=>new XElement("key",new XAttribute("action",a.Id),new XAttribute("key",changed[a.Id].ToString()))));
        }
    }
}
