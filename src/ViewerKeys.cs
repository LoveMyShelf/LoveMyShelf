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
            // Other keys that do the same thing while this action still has its default key (the number pad's /).
            public Key[] Also=new Key[0];
            public string Name {get {return L.Korean?Ko:En;}}
        }
        // In the order of the shortcut list in the guide, so they are easy to find.
        internal static readonly Command[] All={
            A("previous","Previous file","이전 파일",Key.K),
            A("next","Next file","다음 파일",Key.L),
            A("play","Play / pause","재생 / 일시정지",Key.Space),
            A("back","Skip back (with Shift, one second)","뒤로 건너뛰기 (Shift와 함께 누르면 1초)",Key.Left),
            A("forward","Skip forward (with Shift, one second)","앞으로 건너뛰기 (Shift와 함께 누르면 1초)",Key.Right),
            A("sceneBack","One scene back","한 장면 뒤로",Key.D),
            A("sceneForward","One scene forward","한 장면 앞으로",Key.F),
            A("brightnessReset","Brightness back to normal","밝기 원래대로",Key.Q),
            A("darker","Darker","어둡게",Key.W),
            A("brighter","Brighter","밝게",Key.E),
            A("brightnessStyle","Brightness style","밝기 방식 바꾸기",Key.R),
            A("speedReset","Normal speed","원래 속도",Key.Z),
            A("slower","Slower","느리게",Key.X),
            A("faster","Faster","빠르게",Key.C),
            A("double","2× speed and back","2배속 켜기 / 끄기",Key.V),
            A("turnReset","Rotation back to normal","회전 원래대로",Key.I),
            A("turnLeft","Turn left","왼쪽으로 돌리기",Key.O),
            A("turnRight","Turn right","오른쪽으로 돌리기",Key.P),
            A("volumeUp","Volume up","볼륨 올리기",Key.Up),
            A("volumeDown","Volume down","볼륨 내리기",Key.Down),
            A("mute","Sound off / on","소리 끄기 / 켜기",Key.M),
            A("clearVoice","Clear voice on / off","목소리 또렷하게 켜기 / 끄기",Key.N),
            A("capture","Capture this scene","지금 장면 캡처",Key.OemQuestion,Key.Divide),
            A("partStart","Start of the GIF cover part","GIF 표지 구간 시작",Key.OemOpenBrackets),
            A("partEnd","End of the GIF cover part","GIF 표지 구간 끝",Key.OemCloseBrackets),
            A("partClear","Clear the GIF cover part","GIF 표지 구간 지우기",Key.Oem5,Key.OemBackslash),
            A("quickGif","Make a GIF cover from the part in the background","구간을 GIF 표지로 뒤에서 바로 만들기",Key.G),
            A("info","File information","파일 정보",Key.Tab),
            A("recycle","Move the original to the Recycle Bin","원본을 휴지통으로 보내기",Key.Delete),
        };
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

        // The map the large view reads: key → action id. Rebuilt whenever the options are loaded or changed.
        static Dictionary<Key,string> map=Build(new Dictionary<string,Key>());
        internal static void Use(Preferences prefs) {map=Build(prefs!=null?prefs.Shortcuts:new Dictionary<string,Key>());}
        internal static string ActionFor(Key key) {string id;return map.TryGetValue(key,out id)?id:null;}

        internal static Key KeyOf(Dictionary<string,Key> changed,Command action) {
            Key key;return changed!=null && changed.TryGetValue(action.Id,out key)?key:action.Default;
        }
        static Dictionary<Key,string> Build(Dictionary<string,Key> changed) {
            var result=new Dictionary<Key,string>();
            foreach(var action in All)result[KeyOf(changed,action)]=action.Id;
            // The extra keys only while the action has its own key and nothing else took them.
            foreach(var action in All)
                if(KeyOf(changed,action)==action.Default)
                    foreach(var also in action.Also)if(!result.ContainsKey(also))result[also]=action.Id;
            return result;
        }
        // The action that already uses this key (other than the one being changed), or null. Counts the extra keys
        // still working (the number pad's /), so taking one of them asks first too.
        internal static Command UsedBy(Dictionary<string,Key> changed,Key key,Command except) {
            string id;
            if(!Build(changed).TryGetValue(key,out id))return null;
            var owner=All.First(a=>a.Id==id);
            return owner==except?null:owner;
        }
        // Jobs that happen once per press. A key passed on from another window loses "held down", so those
        // windows drop the repeats of these themselves.
        static readonly HashSet<string> once=new HashSet<string>{"play","mute","double","clearVoice","turnReset","recycle","info","partStart","partEnd","partClear","capture"};
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
            if(All.Select(a=>KeyOf(result,a)).Distinct().Count()!=All.Length)result.Clear();
            return result;
        }
        internal static XElement Xml(Dictionary<string,Key> changed) {
            if(changed==null || changed.Count==0)return null;
            return new XElement("shortcuts",All.Where(a=>changed.ContainsKey(a.Id))
                .Select(a=>new XElement("key",new XAttribute("action",a.Id),new XAttribute("key",changed[a.Id].ToString()))));
        }
    }
}
