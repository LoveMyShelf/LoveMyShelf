using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LoveMyShelf {
    internal enum SubtitleKind { Smi, Srt, Vtt, Ass }

    // One time written in the file. Saving replaces only these numbers, so tags, styles and line endings stay as they were.
    internal sealed class SubtitleSlot {
        internal int Offset,Length,Original,Ms;
        internal string Token;
    }
    internal sealed class SubtitleCue {
        internal const int LastLineHold=4000; // an SMI file has no end time for its very last line
        internal string Group="";
        internal SubtitleSlot Start,End;
        internal int TextOffset,TextLength;
        internal string Text,OriginalText,RawText,PlayerText;
        internal bool Edited;
        internal int StartMs {get {return Start.Ms;}}
        internal int EndMs {get {return End!=null?End.Ms:Start.Ms+LastLineHold;}}
    }
    // The file on disk changed after it was opened, so saving would throw away someone else's change.
    internal sealed class SubtitleChangedException : IOException {
        internal SubtitleChangedException():base("The subtitle file changed.") {}
    }

    // Reads SMI, SRT, VTT and ASS/SSA. mpv can show them but can't list their lines or write them back.
    internal sealed class SubtitleFile {
        internal static readonly string[] Extensions={".smi",".sami",".srt",".ass",".ssa",".vtt"};
        internal string Path;
        internal SubtitleKind Kind;
        internal string Raw;
        internal readonly List<SubtitleSlot> Slots=new List<SubtitleSlot>();
        internal readonly List<SubtitleCue> AllCues=new List<SubtitleCue>();
        internal readonly List<string> Groups=new List<string>();
        internal readonly Dictionary<string,string> GroupNames=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        internal string ActiveGroup="";
        Encoding encoding;
        byte[] prefix=new byte[0];
        DateTime stamp;
        long stampLength;
        readonly Stack<Tuple<int[],string[]>> history=new Stack<Tuple<int[],string[]>>();

        internal List<SubtitleCue> Cues {get {return AllCues.Where(c=>c.Group==ActiveGroup).ToList();}}
        internal bool Dirty {get {return Slots.Any(s=>s.Ms!=s.Original) || AllCues.Any(c=>c.Edited);}}
        internal bool CanUndo {get {return history.Count>0;}}
        internal string EncodingName {get {return encoding.WebName;}}

        // ----- Finding the subtitle of a video -----
        internal static string FindFor(string video) {
            try {
                string folder=System.IO.Path.GetDirectoryName(video),stem=System.IO.Path.GetFileNameWithoutExtension(video);
                foreach(string ext in Extensions) {
                    string same=System.IO.Path.Combine(folder,stem+ext);
                    if(File.Exists(same))return same;
                }
                var tagged=Directory.EnumerateFiles(folder,stem+".*")
                    .Where(f=>Extensions.Contains(System.IO.Path.GetExtension(f),StringComparer.OrdinalIgnoreCase)).ToList();
                string[] korean={"ko","kor","kr","korean","한글","한국어"};
                return tagged.OrderBy(f=>korean.Contains(System.IO.Path.GetExtension(System.IO.Path.GetFileNameWithoutExtension(f)).TrimStart('.'),StringComparer.OrdinalIgnoreCase)?0:1)
                    .ThenBy(f=>Array.FindIndex(Extensions,e=>String.Equals(e,System.IO.Path.GetExtension(f),StringComparison.OrdinalIgnoreCase)))
                    .ThenBy(f=>f,StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            } catch(IOException) {return null;} catch(UnauthorizedAccessException) {return null;} catch(ArgumentException) {return null;}
        }

        // ----- Reading -----
        internal static SubtitleFile Load(string path) {
            var file=new SubtitleFile {Path=path};
            var info=new FileInfo(path);
            if(info.Length>64L*1024*1024)throw new InvalidDataException(L.T("The subtitle file is too big to open."));
            byte[] bytes=File.ReadAllBytes(path);
            file.stamp=info.LastWriteTimeUtc;file.stampLength=bytes.Length;
            file.Decode(bytes);
            string ext=System.IO.Path.GetExtension(path).ToLowerInvariant();
            file.Kind=ext==".smi" || ext==".sami"?SubtitleKind.Smi:ext==".vtt"?SubtitleKind.Vtt:ext==".ass" || ext==".ssa"?SubtitleKind.Ass:SubtitleKind.Srt;
            if(file.Kind==SubtitleKind.Smi)file.ParseSmi();
            else if(file.Kind==SubtitleKind.Ass)file.ParseAss();
            else file.ParseTimed();
            if(file.AllCues.Count==0)throw new InvalidDataException(L.T("No subtitles were found in this file."));
            return file;
        }
        void Decode(byte[] bytes) {
            if(bytes.Length>=3 && bytes[0]==0xEF && bytes[1]==0xBB && bytes[2]==0xBF){encoding=new UTF8Encoding(false,true);prefix=bytes.Take(3).ToArray();}
            else if(bytes.Length>=2 && bytes[0]==0xFF && bytes[1]==0xFE){encoding=new UnicodeEncoding(false,false,true);prefix=bytes.Take(2).ToArray();}
            else if(bytes.Length>=2 && bytes[0]==0xFE && bytes[1]==0xFF){encoding=new UnicodeEncoding(true,false,true);prefix=bytes.Take(2).ToArray();}
            if(encoding!=null){Raw=encoding.GetString(bytes,prefix.Length,bytes.Length-prefix.Length);return;}
            try {
                encoding=new UTF8Encoding(false,true);
                Raw=encoding.GetString(bytes);
            } catch(ArgumentException) {
                // Older Korean subtitles. Saving must never replace a character it can't write with "?".
                encoding=Encoding.GetEncoding(949,new EncoderExceptionFallback(),new DecoderReplacementFallback("\uFFFD"));
                Raw=encoding.GetString(bytes);
            }
        }
        SubtitleSlot Slot(int offset,int length,int ms) {
            var slot=new SubtitleSlot {Offset=offset,Length=length,Original=ms,Ms=ms,Token=Raw.Substring(offset,length)};
            Slots.Add(slot);return slot;
        }
        void Finish() {
            foreach(string group in AllCues.Select(c=>c.Group).Distinct())Groups.Add(group);
            ActiveGroup=Groups.OrderByDescending(g=>AllCues.Where(c=>c.Group==g).Sum(c=>c.Text.Count(IsHangul))).ThenBy(g=>Groups.IndexOf(g)).FirstOrDefault()??"";
        }
        static bool IsHangul(char c) {return c>=0xAC00 && c<=0xD7A3;}

        // SRT and WebVTT share their shape: a time line, then text until a blank line.
        static readonly Regex TimeLine=new Regex(@"^[ \t]*((?:\d+:)?\d{1,2}:\d{2}[,.]\d{1,3})[ \t]*-->[ \t]*((?:\d+:)?\d{1,2}:\d{2}[,.]\d{1,3})[^\r\n]*(?:\r?\n|\z)",RegexOptions.Multiline|RegexOptions.CultureInvariant);
        void ParseTimed() {
            foreach(Match m in TimeLine.Matches(Raw)) {
                var start=Slot(m.Groups[1].Index,m.Groups[1].Length,Clock(m.Groups[1].Value));
                var end=Slot(m.Groups[2].Index,m.Groups[2].Length,Clock(m.Groups[2].Value));
                int from=m.Index+m.Length,at=from,last=from;
                while(at<Raw.Length) {
                    int stop=at;
                    while(stop<Raw.Length && Raw[stop]!='\n' && Raw[stop]!='\r')stop++;
                    if(String.IsNullOrWhiteSpace(Raw.Substring(at,stop-at)))break;
                    last=stop;
                    at=stop;
                    if(at<Raw.Length && Raw[at]=='\r')at++;
                    if(at<Raw.Length && Raw[at]=='\n')at++;
                }
                string body=Raw.Substring(from,last-from);
                string text=Plain(body);
                AllCues.Add(new SubtitleCue {Start=start,End=end,TextOffset=from,TextLength=last-from,RawText=body,Text=text,OriginalText=text,PlayerText=OwnFont(body.Replace("\r\n","\n"))});
            }
            Finish();
        }
        static readonly Regex Tag=new Regex(@"<[^>]*>",RegexOptions.CultureInvariant);
        static string Plain(string body) {
            string text=Tag.Replace(body.Replace("\r\n","\n"),"");
            text=Regex.Replace(text,@"\{\\[^}]*\}","");
            return String.Join("\n",WebUtility.HtmlDecode(text).Split('\n').Select(l=>l.Replace('\u00A0',' ').Trim()).Where(l=>l.Length>0));
        }
        static int Clock(string token) {
            var parts=token.Replace(',','.').Split(':');
            double seconds=0;
            foreach(string part in parts)seconds=seconds*60+Double.Parse(part,CultureInfo.InvariantCulture);
            return (int)Math.Round(seconds*1000);
        }

        // ASS keeps its fields in the order given by the Format line of [Events].
        static readonly Regex AssLine=new Regex(@"^Dialogue:[ \t]*([^\r\n]*)",RegexOptions.Multiline|RegexOptions.CultureInvariant);
        void ParseAss() {
            int startField=1,endField=2,textField=9;
            var events=Regex.Match(Raw,@"\[Events\][\s\S]*?^Format:[ \t]*([^\r\n]*)",RegexOptions.Multiline|RegexOptions.IgnoreCase);
            if(events.Success) {
                var names=events.Groups[1].Value.Split(',').Select(n=>n.Trim().ToLowerInvariant()).ToList();
                if(names.Contains("start") && names.Contains("end") && names.Contains("text")) {
                    startField=names.IndexOf("start");endField=names.IndexOf("end");textField=names.IndexOf("text");
                }
            }
            foreach(Match m in AssLine.Matches(Raw)) {
                int at=m.Groups[1].Index,stop=at+m.Groups[1].Length;
                var offsets=new List<int>{at};
                for(int i=at;i<stop && offsets.Count<=textField;i++)if(Raw[i]==',')offsets.Add(i+1);
                if(offsets.Count<=textField)continue;
                Func<int,Tuple<int,int>> field=n=>{
                    int begin=offsets[n],finish=n+1<offsets.Count?offsets[n+1]-1:stop;
                    while(begin<finish && Raw[begin]==' ')begin++;
                    while(finish>begin && Raw[finish-1]==' ')finish--;
                    return Tuple.Create(begin,finish-begin);
                };
                var a=field(startField);var b=field(endField);
                int startMs,endMs;
                try {startMs=Clock(Raw.Substring(a.Item1,a.Item2));endMs=Clock(Raw.Substring(b.Item1,b.Item2));}
                catch(FormatException) {continue;}
                var start=Slot(a.Item1,a.Item2,startMs);var end=Slot(b.Item1,b.Item2,endMs);
                int from=offsets[textField];
                string body=Raw.Substring(from,stop-from);
                string text=Plain(body.Replace("\\N","\n").Replace("\\n","\n").Replace("\\h"," "));
                AllCues.Add(new SubtitleCue {Start=start,End=end,TextOffset=from,TextLength=stop-from,RawText=body,Text=text,OriginalText=text});
            }
            Finish();
        }

        static readonly Regex SyncTag=new Regex(@"<SYNC\b[^>]*?\bStart\s*=\s*[""']?(-?\d+)[^>]*>",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
        static readonly Regex ParaTag=new Regex(@"<P\b([^>]*)>",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
        static readonly Regex ClassName=new Regex(@"\bClass\s*=\s*[""']?([\w-]+)",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
        static readonly Regex StyleName=new Regex(@"\.([\w-]+)\s*\{[^}]*?\bName\s*:\s*([^;}]+)",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
        sealed class SmiPart {public SubtitleSlot Slot;public int Offset,Length;public string Body;}
        void ParseSmi() {
            foreach(Match style in StyleName.Matches(Raw))GroupNames[style.Groups[1].Value]=style.Groups[2].Value.Trim();
            var syncs=SyncTag.Matches(Raw);
            int close=Raw.LastIndexOf("</BODY",StringComparison.OrdinalIgnoreCase);
            var parts=new Dictionary<string,List<SmiPart>>(StringComparer.OrdinalIgnoreCase);
            var order=new List<string>();
            for(int i=0;i<syncs.Count;i++) {
                var sync=syncs[i];
                var slot=Slot(sync.Groups[1].Index,sync.Groups[1].Length,Int32.Parse(sync.Groups[1].Value,CultureInfo.InvariantCulture));
                int from=sync.Index+sync.Length,to=i+1<syncs.Count?syncs[i+1].Index:(close>from?close:Raw.Length);
                string body=Raw.Substring(from,to-from);
                var paragraphs=ParaTag.Matches(body);
                Action<string,int,int> add=(name,offset,length)=>{
                    while(length>0 && Char.IsWhiteSpace(Raw[offset+length-1]))length--;
                    if(!parts.ContainsKey(name)){parts[name]=new List<SmiPart>();order.Add(name);}
                    parts[name].Add(new SmiPart {Slot=slot,Offset=offset,Length=length,Body=Raw.Substring(offset,length)});
                };
                if(paragraphs.Count==0){add("",from,to-from);continue;}
                for(int p=0;p<paragraphs.Count;p++) {
                    var c=ClassName.Match(paragraphs[p].Groups[1].Value);
                    int begin=from+paragraphs[p].Index+paragraphs[p].Length;
                    int finish=p+1<paragraphs.Count?from+paragraphs[p+1].Index:to;
                    add(c.Success?c.Groups[1].Value:"",begin,finish-begin);
                }
            }
            foreach(string name in order) {
                var list=parts[name];
                for(int i=0;i<list.Count;i++) {
                    string text=SmiText(list[i].Body);
                    if(text.Length==0)continue;
                    var next=list.Skip(i+1).FirstOrDefault(n=>n.Slot.Original>list[i].Slot.Original);
                    AllCues.Add(new SubtitleCue {Group=name,Start=list[i].Slot,End=next==null?null:next.Slot,TextOffset=list[i].Offset,TextLength=list[i].Length,
                        RawText=list[i].Body,Text=text,OriginalText=text,PlayerText=SmiPlayerText(list[i].Body)});
                }
            }
            Finish();
        }
        static readonly Regex BreakTag=new Regex(@"<br\s*/?>",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
        static string SmiText(string body) {return Plain(BreakTag.Replace(body.Replace("\r","").Replace("\n"," "),"\n"));}
        // Color, bold, italics and underline survive on screen; other SMI tags mean nothing to the player.
        static string SmiPlayerText(string body) {
            string text=BreakTag.Replace(body.Replace("\r","").Replace("\n"," "),"\n");
            text=Regex.Replace(text,@"</?(?!(?:font|b|i|u)\b)[a-z][^>]*>","",RegexOptions.IgnoreCase);
            return String.Join("\n",WebUtility.HtmlDecode(OwnFont(text)).Split('\n').Select(l=>l.Replace('\u00A0',' ').Trim()).Where(l=>l.Length>0));
        }
        // A font name or size written in the subtitle is left out on screen (the file keeps it), so every subtitle
        // shows in Love My Shelf's font at the size the person chose. Colors stay.
        static readonly Regex FontTag=new Regex(@"<font\b[^>]*>",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
        static readonly Regex FontLook=new Regex(@"\s+(?:face|size)\s*=\s*(?:""[^""]*""|'[^']*'|[^\s>]+)",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
        static string OwnFont(string text) {return FontTag.Replace(text,m=>FontLook.Replace(m.Value,""));}

        // ----- Changing -----
        void Remember() {
            history.Push(Tuple.Create(Slots.Select(s=>s.Ms).ToArray(),AllCues.Select(c=>c.Text).ToArray()));
        }
        // The chosen line starts at this moment of the video. Everything moves with it, or only this line and what follows.
        internal bool SyncTo(SubtitleCue cue,double videoMs,bool onlyFromHere) {
            int delta=(int)Math.Round(videoMs)-cue.StartMs;
            if(delta==0)return false;
            Remember();
            int from=cue.StartMs;
            foreach(var slot in Slots)if(!onlyFromHere || slot.Ms>=from)slot.Ms+=delta;
            return true;
        }
        internal void SetText(SubtitleCue cue,string text) {
            text=String.Join("\n",(text??"").Replace("\r\n","\n").Replace('\r','\n').Split('\n').Select(l=>l.Trim()).Where(l=>l.Length>0));
            if(text.Length==0 || text==cue.Text)return;
            Remember();
            ApplyText(cue,text);
        }
        void ApplyText(SubtitleCue cue,string text) {
            cue.Text=text;cue.Edited=text!=cue.OriginalText;
            if(!cue.Edited){cue.PlayerText=Kind==SubtitleKind.Smi?SmiPlayerText(cue.RawText):OwnFont(cue.RawText.Replace("\r\n","\n"));return;}
            cue.PlayerText=text;
        }
        string EncodedText(SubtitleCue cue) {
            var lines=cue.Text.Split('\n');
            switch(Kind) {
                case SubtitleKind.Smi:return String.Join("<br>",lines.Select(WebUtility.HtmlEncode));
                case SubtitleKind.Ass:return String.Join("\\N",lines);
                default:return String.Join(Raw.Contains("\r\n")?"\r\n":"\n",lines);
            }
        }
        internal void Undo() {
            if(history.Count==0)return;
            var saved=history.Pop();
            for(int i=0;i<Slots.Count;i++)Slots[i].Ms=saved.Item1[i];
            for(int i=0;i<AllCues.Count;i++)if(AllCues[i].Text!=saved.Item2[i])ApplyText(AllCues[i],saved.Item2[i]);
        }
        internal void Revert() {
            foreach(var slot in Slots)slot.Ms=slot.Original;
            foreach(var cue in AllCues)if(cue.Edited)ApplyText(cue,cue.OriginalText);
            history.Clear();
        }
        internal int LinesBeforeZero {get {return Cues.Count(c=>c.StartMs<0);}}

        // ----- Writing -----
        string TimeText(SubtitleSlot slot) {
            int ms=Math.Max(0,slot.Ms);
            if(Kind==SubtitleKind.Smi)return ms.ToString(CultureInfo.InvariantCulture);
            var t=TimeSpan.FromMilliseconds(ms);
            int hours=(int)t.TotalHours;
            if(Kind==SubtitleKind.Ass) {
                long centis=(long)Math.Round(ms/10.0);
                return String.Format(CultureInfo.InvariantCulture,"{0}:{1:00}:{2:00}.{3:00}",centis/360000,centis/6000%60,centis/100%60,centis%100);
            }
            char mark=slot.Token.Contains(",")?',':'.';
            bool showHours=hours>0 || slot.Token.Count(c=>c==':')==2;
            return (showHours?hours.ToString("00",CultureInfo.InvariantCulture)+":":"")+
                String.Format(CultureInfo.InvariantCulture,"{0:00}:{1:00}{2}{3:000}",showHours?t.Minutes:(int)t.TotalMinutes,t.Seconds,mark,t.Milliseconds);
        }
        // The original text with only the changed times and lines replaced.
        internal string Render() {
            var changes=new List<Tuple<int,int,string>>();
            foreach(var slot in Slots)if(slot.Ms!=slot.Original)changes.Add(Tuple.Create(slot.Offset,slot.Length,TimeText(slot)));
            foreach(var cue in AllCues)if(cue.Edited)changes.Add(Tuple.Create(cue.TextOffset,cue.TextLength,EncodedText(cue)));
            var output=new StringBuilder(Raw.Length+changes.Count*2);
            int at=0;
            foreach(var change in changes.OrderBy(c=>c.Item1)) {
                output.Append(Raw,at,change.Item1-at).Append(change.Item3);
                at=change.Item1+change.Item2;
            }
            return output.Append(Raw,at,Raw.Length-at).ToString();
        }
        // What the player shows right now, before anything is saved. It is a temporary copy with the current times.
        internal string PlayerText(out string extension) {
            if(Kind==SubtitleKind.Ass){extension=System.IO.Path.GetExtension(Path);return Render();}
            extension=".srt";
            var output=new StringBuilder();
            int number=1;
            foreach(var cue in Cues.OrderBy(c=>c.StartMs)) {
                int start=Math.Max(0,cue.StartMs),end=Math.Max(start+1,cue.EndMs);
                if(cue.EndMs<=0)continue;
                output.Append(number++).Append('\n').Append(SrtTime(start)).Append(" --> ").Append(SrtTime(end)).Append('\n')
                    .Append(cue.PlayerText).Append("\n\n");
            }
            return output.ToString();
        }
        static string SrtTime(int ms) {
            var t=TimeSpan.FromMilliseconds(ms);
            return String.Format(CultureInfo.InvariantCulture,"{0:00}:{1:00}:{2:00},{3:000}",(int)t.TotalHours,t.Minutes,t.Seconds,t.Milliseconds);
        }
        // Overwrites the subtitle. The very first version is kept in Love My Shelf's own folder, and the file is only
        // replaced once the whole new one has been written. Returns the file as it is now on disk.
        internal SubtitleFile Save(string backupFolder,bool evenIfChanged=false) {
            var info=new FileInfo(Path);
            if(!evenIfChanged && (!info.Exists || info.LastWriteTimeUtc!=stamp || info.Length!=stampLength))throw new SubtitleChangedException();
            byte[] body;
            try {body=encoding.GetBytes(Render());}
            catch(EncoderFallbackException) {throw new InvalidDataException(L.T("Some of the characters you typed can't be saved in this subtitle file."));}
            byte[] all=new byte[prefix.Length+body.Length];
            Buffer.BlockCopy(prefix,0,all,0,prefix.Length);Buffer.BlockCopy(body,0,all,prefix.Length,body.Length);
            if(info.Exists)KeepFirstVersion(backupFolder);
            string temp=Path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try {
                File.WriteAllBytes(temp,all);
                if(info.Exists)File.Replace(temp,Path,null);else File.Move(temp,Path);
            } finally {try {File.Delete(temp);} catch(IOException) {} catch(UnauthorizedAccessException) {}}
            var fresh=Load(Path);
            if(fresh.Groups.Contains(ActiveGroup))fresh.ActiveGroup=ActiveGroup;
            return fresh;
        }
        void KeepFirstVersion(string folder) {
            Directory.CreateDirectory(folder);
            string id;
            using(var sha=SHA256.Create())id=BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(Path).ToUpperInvariant())),0,8).Replace("-","");
            string name=System.IO.Path.GetFileName(Path);
            if(name.Length>120)name=name.Substring(name.Length-120);
            string backup=System.IO.Path.Combine(folder,id+"-"+name);
            if(!File.Exists(backup))File.Copy(Path,backup);
        }
    }
}
