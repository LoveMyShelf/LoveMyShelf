using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LoveMyShelf {
    // What went wrong, written to a small text file on this computer so a problem can be found afterwards.
    // Nothing is sent anywhere. Writing never throws and never holds the program up for long.
    internal static class ErrorLog {
        const long Limit=512*1024;   // then the file becomes errors-old.txt and a new one starts
        const int PerPlace=5;        // the same place is written this many times per run, no more
        const int PerRun=400;        // and this many lines per run in all
        const int Longest=4000;      // characters of one problem
        static readonly object Gate=new object();
        static readonly Dictionary<string,int> Seen=new Dictionary<string,int>(StringComparer.Ordinal);
        static int written;
        // Set only by the checks (gallery-checks\safety), so what they break on purpose stays out of the real file.
        internal static string FolderForChecks;
        internal static string Folder {
            get {if(FolderForChecks!=null)return FolderForChecks;return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LoveMyShelf");}
        }
        internal static string Path {get {return System.IO.Path.Combine(Folder,"errors.txt");}}

        // A problem the program carried on from without telling anyone. The place is filled in by the compiler.
        internal static void Quiet(Exception problem,[CallerMemberName]string member="",[CallerLineNumber]int line=0) {
            Write(member+":"+line.ToString(CultureInfo.InvariantCulture),problem,false);
        }
        // A problem that ends the program, or one worth its whole story.
        internal static void Serious(string where,Exception problem) {Write(where,problem,true);}
        internal static void Note(string where,string message) {Append(where,where,message??"",false);}

        // The program is about to end on its own: called once, from whichever thread it happened on.
        internal static Action BeforeCrash;
        static bool watching;
        internal static void WatchCrashes() {
            if(watching)return;
            watching=true;
            AppDomain.CurrentDomain.UnhandledException+=(s,e)=>{
                Serious("Crash (background)",e.ExceptionObject as Exception);
                RunBeforeCrash();
            };
            // A background task whose failure nobody looked at. The program carries on; the reason is kept.
            TaskScheduler.UnobservedTaskException+=(s,e)=>{Write("Background task",e.Exception,false);};
        }
        static int crashed;
        internal static void RunBeforeCrash() {
            if(Interlocked.Exchange(ref crashed,1)!=0)return;
            var todo=BeforeCrash;
            if(todo==null)return;
            try {todo();} catch(Exception problem) {Serious("While closing after a crash",problem);}
        }

        static void Write(string where,Exception problem,bool whole) {
            try {
                if(problem==null){Append(where,where,"(no details)",whole);return;}
                var inner=problem as AggregateException;
                if(inner!=null && inner.InnerExceptions.Count==1)problem=inner.InnerExceptions[0];
                // The same place failing in a different way is a different problem.
                string key=where+"|"+problem.GetType().Name;
                // Asked first: a place that fails thousands of times in a row costs next to nothing after the first few.
                if(!whole)lock(Gate) {
                    int count;
                    Seen.TryGetValue(key,out count);
                    if(count>=PerPlace || written>=PerRun)return;
                }
                string text=problem.ToString();
                if(!whole && text.Length>Longest)text=text.Substring(0,Longest)+" …";
                Append(key,where,text,whole);
            } catch(Exception) {}
        }
        // What ends the program (always) is written whatever was written before it.
        static void Append(string key,string where,string text,bool always) {
            try {
                lock(Gate) {
                    int count;
                    Seen.TryGetValue(key,out count);
                    if(!always && (count>=PerPlace || written>=PerRun))return;
                    Seen[key]=count+1;
                    written++;
                    var line=new StringBuilder();
                    line.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture)).Append("  ").Append(AppInfo.Version)
                        .Append("  ").Append(where).Append(Environment.NewLine).Append("    ")
                        .Append(text.Replace("\r\n","\n").Replace("\n",Environment.NewLine+"    ")).Append(Environment.NewLine);
                    if(!always && count+1==PerPlace)line.Append("    (the same problem is not written again until Love My Shelf is restarted)").Append(Environment.NewLine);
                    if(written==PerRun)line.Append("    (too many problems: nothing more is written until Love My Shelf is restarted)").Append(Environment.NewLine);
                    Directory.CreateDirectory(Folder);
                    string path=Path;
                    // The upscaling, face-finding and update helpers are copies of this program and may write at the same
                    // moment: one at a time. A copy that died while writing must not stop the others, so the wait is short.
                    Mutex turn=null;
                    bool mine=false;
                    try {
                        turn=new Mutex(false,"Local\\LoveMyShelfErrorLog");
                        try {mine=turn.WaitOne(300);} catch(AbandonedMutexException) {mine=true;}
                    } catch(Exception) {}
                    try {
                    try {
                        var info=new FileInfo(path);
                        if(info.Exists && info.Length>Limit) {
                            string old=System.IO.Path.Combine(Folder,"errors-old.txt");
                            if(File.Exists(old))File.Delete(old);
                            File.Move(path,old);
                        }
                    } catch(Exception) {} // another copy of the program may be writing: the file just grows a little more
                    byte[] bytes=new UTF8Encoding(false).GetBytes(line.ToString());
                    using(var file=new FileStream(path,FileMode.Append,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete))
                        file.Write(bytes,0,bytes.Length);
                    } finally {
                        if(turn!=null) {
                            if(mine) {try {turn.ReleaseMutex();} catch(Exception) {}}
                            turn.Dispose();
                        }
                    }
                }
            } catch(Exception) {}
        }
    }
}
