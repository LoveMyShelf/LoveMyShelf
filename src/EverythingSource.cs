using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace LoveMyShelf {
    // The whole-computer list from Everything (voidtools), when it is running: it already knows every file, so the
    // list comes in seconds instead of minutes. 2026-11-28 user: "everything 연결 돼?" … "연결해봐".
    // Nothing is installed or shipped: LoveMyShelf only asks the Everything already running on this computer, the
    // documented way (WM_COPYDATA). Drives Everything doesn't index (network drives, most USB sticks) are read the
    // plain way, and if Everything isn't there or doesn't answer, everything is read the plain way.
    internal sealed class EverythingSource : IFileListSource {
        readonly IFileListSource plain;
        internal EverythingSource(IFileListSource plain) {this.plain=plain;}
        public string Name {get {return "everything";}}

        public List<string> PictureFolders(FileListRequest request,FileListProgress progress,CancellationToken token) {
            var found=new List<string>();
            var crawl=new List<string>();
            var settings=EverythingSettings.Read();
            // Everything set to leave out files or hidden/system folders we want: it can't give the whole answer.
            if(settings==null || !settings.Usable(request))return plain.PictureFolders(request,progress,token);
            try {
                using(var everything=EverythingClient.Open()) {
                    if(everything==null)return plain.PictureFolders(request,progress,token);
                    // Only NTFS drives Everything reads whole are trusted; everything else is read the plain way.
                    var indexed=new List<string>();
                    foreach(string root in request.Roots.Distinct(StringComparer.OrdinalIgnoreCase)) {
                        int drive=root.Length>=2 && root[1]==':'?Char.ToUpperInvariant(root[0])-'A':-1;
                        if(drive>=0 && drive<26 && everything.DriveIndexed(drive))indexed.Add(root);else crawl.Add(root);
                    }
                    // Folders the person told Everything to leave out are read the plain way.
                    foreach(string left in settings.ExcludedFolders)
                        if(indexed.Any(r=>Under(left,r)) && !request.Excluded(left))crawl.Add(left);
                    if(indexed.Count>0) {
                        var folders=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        string search="ext:"+String.Join(";",PictureTypes.Extensions.Concat(VideoFrames.Extensions).Select(e=>e.TrimStart('.')).Distinct());
                        bool stopped=false;
                        try {everything.Each(search,(folder,name)=>{if(UncoveredFolders.IsPicture(name))folders.Add(folder);},token);}
                        catch(OperationCanceledException) {stopped=true;} // what came so far is still checked: [Stop] shows it
                        var verdicts=new Dictionary<string,bool>(StringComparer.OrdinalIgnoreCase);
                        foreach(string folder in folders) {
                            string root=indexed.FirstOrDefault(r=>Under(folder,r));
                            if(root==null || settings.ExcludedFolders.Any(x=>Under(folder,x)))continue;
                            progress.Folder();
                            if(Allowed(folder,root,request,verdicts,progress)) {found.Add(folder);progress.Pictures();}
                        }
                        if(stopped)return found;
                    }
                }
            } catch(OperationCanceledException) {return found;}
            catch(Exception e) {
                // Everything went away or answered something unexpected: the plain way, from the start.
                ErrorLog.Quiet(e);
                return plain.PictureFolders(request,progress,token);
            }
            if(crawl.Count>0 && !token.IsCancellationRequested) {
                var rest=new FileListRequest {Roots=crawl,Hidden=request.Hidden,SkipSystem=request.SkipSystem,Excluded=request.Excluded,
                    RootsAtOnce=request.RootsAtOnce,WorkersPerRoot=request.WorkersPerRoot};
                found.AddRange(plain.PictureFolders(rest,progress,token));
            }
            return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        static bool Under(string path,string top) {
            if(!path.StartsWith(top,StringComparison.OrdinalIgnoreCase))return false;
            return path.Length==top.Length || top.EndsWith("\\") || path[top.Length]=='\\';
        }
        // The same folders the plain crawl leaves out: each folder between the root and this one is checked once.
        static bool Allowed(string folder,string root,FileListRequest request,Dictionary<string,bool> verdicts,FileListProgress progress) {
            if(folder.Length<=root.Length)return true;
            bool known;
            if(verdicts.TryGetValue(folder,out known))return known;
            string parent=Path.GetDirectoryName(folder);
            bool ok;
            if(parent==null)ok=true;
            else if(!Allowed(UncoveredFolders.Plain(parent),root,request,verdicts,progress))ok=false;
            else {
                uint attributes,tag;
                if(!CrawlSource.Info(folder,out attributes,out tag)){progress.CouldNotRead();ok=false;}
                else ok=!CrawlSource.IsLink(attributes,tag) && !CrawlSource.Skip(Path.GetFileName(folder),attributes,parent.Length<=3,folder,request);
            }
            verdicts[folder]=ok;
            return ok;
        }
    }

    // What Everything was told to read, from its own settings file. null = not found, so Everything isn't trusted.
    internal sealed class EverythingSettings {
        bool excludeHidden,excludeSystem,filesFiltered;
        internal List<string> ExcludedFolders=new List<string>();

        internal bool Usable(FileListRequest request) {
            if(filesFiltered)return false;
            if(request.Hidden && excludeHidden)return false;
            if(!request.SkipSystem && excludeSystem)return false;
            return true;
        }
        internal static EverythingSettings Read() {
            try {
                string program=null;
                foreach(var top in new[] {Environment.SpecialFolder.ProgramFiles,Environment.SpecialFolder.ProgramFilesX86}) {
                    string p=Path.Combine(Environment.GetFolderPath(top),"Everything","Everything.ini");
                    if(File.Exists(p)){program=p;break;}
                }
                string roaming=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"Everything","Everything.ini");
                // app_data=1 in the program's file means the real settings live in AppData.
                string appData;
                bool inProgram=program!=null && Values(program).TryGetValue("app_data",out appData) && appData=="0";
                string path=inProgram?program:File.Exists(roaming)?roaming:program;
                if(path==null)return null;
                var v=Values(path);
                var s=new EverythingSettings();
                Func<string,string> get=k=>{string x;return v.TryGetValue(k,out x)?x:"";};
                bool listOn=get("exclude_list_enabled")!="0";
                s.excludeHidden=get("exclude_hidden_files_and_folders")=="1";
                s.excludeSystem=get("exclude_system_files_and_folders")=="1";
                if(listOn) {
                    s.filesFiltered=get("include_only_files")!="" || get("exclude_files")!="" || get("include_only_folders")!="";
                    foreach(string f in List(get("exclude_folders"))) {
                        // A pattern can't be read the plain way: then Everything isn't trusted at all.
                        if(f.IndexOfAny(new[] {'*','?'})>=0 || f.StartsWith("regex:",StringComparison.OrdinalIgnoreCase)){s.filesFiltered=true;continue;}
                        s.ExcludedFolders.Add(UncoveredFolders.Plain(f));
                    }
                }
                return s;
            } catch(Exception quiet) {ErrorLog.Quiet(quiet);return null;}
        }
        static Dictionary<string,string> Values(string path) {
            var d=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            foreach(string line in File.ReadAllLines(path)) {
                int eq=line.IndexOf('=');
                if(eq>0)d[line.Substring(0,eq).Trim()]=line.Substring(eq+1).Trim();
            }
            return d;
        }
        // "a","b" or a,b: Everything's list form.
        static IEnumerable<string> List(string value) {
            var items=new List<string>();
            var now=new StringBuilder();
            bool quoted=false;
            for(int i=0;i<value.Length;i++) {
                char c=value[i];
                if(c=='"'){quoted=!quoted;continue;}
                // Inside quotes a backslash is written twice ("C:\\Temp").
                if(quoted && c=='\\' && i+1<value.Length && value[i+1]=='\\')i++;
                if(c==',' && !quoted){items.Add(now.ToString());now.Clear();continue;}
                now.Append(c);
            }
            items.Add(now.ToString());
            return items.Select(x=>x.Trim()).Where(x=>x!="");
        }
    }

    // Talks to a running Everything over its documented IPC: a query goes out in WM_COPYDATA, the answer comes back
    // in WM_COPYDATA to a hidden window of ours. Everything runs on one thread: the window lives on the thread that
    // opened it, and the answer is waited for there.
    internal sealed class EverythingClient : IDisposable {
        const uint WM_COPYDATA=0x4A,WM_USER=0x400;
        const int IPC_IS_NTFS_DRIVE_INDEXED=400,IPC_IS_DB_LOADED=401,COPYDATA_QUERYW=2;
        const uint ReplyId=0x4C4D5331; // "LMS1"
        const int PageSize=50000,Overlap=1000;
        static readonly TimeSpan ReplyWait=TimeSpan.FromSeconds(60);

        readonly IntPtr everything,window;
        byte[] reply;

        EverythingClient(IntPtr everything,IntPtr window) {this.everything=everything;this.window=window;}

        // null when Everything isn't running or hasn't finished reading its database.
        internal static EverythingClient Open() {
            IntPtr found=FindWindowW("EVERYTHING_TASKBAR_NOTIFICATION",null);
            if(found==IntPtr.Zero)found=FindWindowW("EVERYTHING_TASKBAR_NOTIFICATION_(1.5a)",null);
            if(found==IntPtr.Zero)return null;
            IntPtr loaded;
            if(SendMessageTimeoutW(found,WM_USER,new IntPtr(IPC_IS_DB_LOADED),IntPtr.Zero,SMTO_ABORTIFHUNG,3000,out loaded)==IntPtr.Zero || loaded==IntPtr.Zero)return null;
            EnsureClass();
            IntPtr hwnd=CreateWindowExW(0,ClassName,"",0,0,0,0,0,IntPtr.Zero,IntPtr.Zero,GetModuleHandleW(null),IntPtr.Zero);
            if(hwnd==IntPtr.Zero)return null;
            // In case Everything runs as administrator: let its answer through.
            try {ChangeWindowMessageFilterEx(hwnd,WM_COPYDATA,1,IntPtr.Zero);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}
            var client=new EverythingClient(found,hwnd);
            lock(clients)clients[hwnd]=client;
            return client;
        }
        public void Dispose() {
            lock(clients)clients.Remove(window);
            DestroyWindow(window);
        }

        // Whether Everything reads this NTFS drive (0 = A:, 2 = C: …).
        internal bool DriveIndexed(int drive) {
            IntPtr result;
            return SendMessageTimeoutW(everything,WM_USER,new IntPtr(IPC_IS_NTFS_DRIVE_INDEXED),new IntPtr(drive),SMTO_ABORTIFHUNG,3000,out result)!=IntPtr.Zero && result!=IntPtr.Zero;
        }
        // Every matching item: its folder and its name, page by page. Pages overlap a little, so a file removed
        // between two pages never makes the next one skip an item (the folders are kept without repeats anyway).
        internal void Each(string search,Action<string,string> item,CancellationToken token) {
            int offset=0;
            while(true) {
                token.ThrowIfCancellationRequested();
                int total;
                int got=Query(search,offset,PageSize,token,item,out total);
                if(got==0 || offset+got>=total)return;
                offset+=Math.Max(1,got-Overlap);
            }
        }
        // One page: sends the query, waits for the answer, reads it. Returns how many items came back.
        int Query(string search,int offset,int max,CancellationToken token,Action<string,string> item,out int total) {
            // EVERYTHING_IPC_QUERYW: reply_hwnd, reply_copydata_message, search_flags, offset, max_results, search text.
            byte[] text=Encoding.Unicode.GetBytes(search+"\0");
            byte[] query=new byte[20+text.Length];
            Put(query,0,(uint)window.ToInt64());Put(query,4,ReplyId);Put(query,8,0);Put(query,12,(uint)offset);Put(query,16,(uint)max);
            Buffer.BlockCopy(text,0,query,20,text.Length);
            reply=null;
            var data=GCHandle.Alloc(query,GCHandleType.Pinned);
            IntPtr cds=Marshal.AllocHGlobal(Marshal.SizeOf(typeof(CopyData)));
            try {
                Marshal.StructureToPtr(new CopyData {Kind=new IntPtr(COPYDATA_QUERYW),Size=query.Length,Data=data.AddrOfPinnedObject()},cds,false);
                IntPtr accepted;
                token.ThrowIfCancellationRequested();
                if(SendMessageTimeoutW(everything,WM_COPYDATA,window,cds,SMTO_ABORTIFHUNG,3000,out accepted)==IntPtr.Zero || accepted==IntPtr.Zero)
                    throw new IOException("Everything did not take the query.");
            } finally {Marshal.FreeHGlobal(cds);data.Free();}
            // The answer comes as a sent message: it is handled while messages are looked at here.
            var until=DateTime.UtcNow+ReplyWait;
            while(reply==null) {
                token.ThrowIfCancellationRequested();
                if(DateTime.UtcNow>until)throw new TimeoutException("Everything did not answer.");
                MsgWaitForMultipleObjects(0,null,false,100,0x4FF);
                Msg msg;
                while(PeekMessageW(out msg,IntPtr.Zero,0,0,1)) {TranslateMessage(ref msg);DispatchMessageW(ref msg);}
            }
            byte[] r=reply;reply=null;
            // EVERYTHING_IPC_LISTW: totfolders, totfiles, totitems, numfolders, numfiles, numitems, offset, then items.
            if(r.Length<28)throw new IOException("Everything's answer is too short.");
            total=(int)Get(r,8);
            uint count=Get(r,20);
            // A short or odd answer is not taken as a full one: the plain way reads it instead.
            if(Get(r,24)!=(uint)offset || count>(uint)max || 28L+count*12L>r.Length)throw new IOException("Everything's answer doesn't fit.");
            if(item!=null) {
                for(int i=0;i<(int)count;i++) {
                    int at=28+i*12;
                    uint flags=Get(r,at);
                    if((flags&1)!=0)continue; // a folder
                    string name=Text(r,Get(r,at+4)),path=Text(r,Get(r,at+8));
                    if(path.Length==2 && path[1]==':')path+="\\";
                    item(path,name);
                }
            }
            return (int)count;
        }
        static void Put(byte[] b,int at,uint v) {b[at]=(byte)v;b[at+1]=(byte)(v>>8);b[at+2]=(byte)(v>>16);b[at+3]=(byte)(v>>24);}
        static uint Get(byte[] b,int at) {return (uint)(b[at]|(b[at+1]<<8)|(b[at+2]<<16)|(b[at+3]<<24));}
        static string Text(byte[] b,uint at) {
            if(at<28 || at>=b.Length || (at&1)!=0)throw new IOException("Everything's answer doesn't fit.");
            int end=(int)at;
            while(end+1<b.Length && (b[end]!=0 || b[end+1]!=0))end+=2;
            if(end+1>=b.Length)throw new IOException("Everything's answer doesn't fit."); // no end mark
            return Encoding.Unicode.GetString(b,(int)at,end-(int)at);
        }

        // ----- The hidden window that receives the answer -----
        const string ClassName="LoveMyShelf.EverythingReply";
        static readonly Dictionary<IntPtr,EverythingClient> clients=new Dictionary<IntPtr,EverythingClient>();
        static WndProc proc; // kept, so the collector never takes it while Windows still calls it
        static readonly object classGate=new object();
        static bool registered;
        static void EnsureClass() {
            lock(classGate) {
                if(registered)return;
                proc=Receive;
                var c=new WndClass {Proc=proc,Instance=GetModuleHandleW(null),ClassName=ClassName};
                if(RegisterClassW(ref c)==0 && Marshal.GetLastWin32Error()!=1410)throw new IOException("The answer window could not be made.");
                registered=true;
            }
        }
        static IntPtr Receive(IntPtr hwnd,uint msg,IntPtr wParam,IntPtr lParam) {
            if(msg==WM_COPYDATA) {
                var cd=(CopyData)Marshal.PtrToStructure(lParam,typeof(CopyData));
                if((uint)cd.Kind.ToInt64()==ReplyId) {
                    EverythingClient client;
                    lock(clients)clients.TryGetValue(hwnd,out client);
                    if(client!=null && cd.Size>0 && cd.Data!=IntPtr.Zero) {
                        var copy=new byte[cd.Size];
                        Marshal.Copy(cd.Data,copy,0,cd.Size);
                        client.reply=copy;
                    }
                    return new IntPtr(1);
                }
            }
            return DefWindowProcW(hwnd,msg,wParam,lParam);
        }

        delegate IntPtr WndProc(IntPtr hwnd,uint msg,IntPtr wParam,IntPtr lParam);
        [StructLayout(LayoutKind.Sequential)] struct CopyData {public IntPtr Kind;public int Size;public IntPtr Data;}
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
        struct WndClass {
            public uint Style;public WndProc Proc;public int ClassExtra,WindowExtra;public IntPtr Instance,Icon,Cursor,Background;
            public string MenuName,ClassName;
        }
        [StructLayout(LayoutKind.Sequential)] struct Msg {public IntPtr Hwnd;public uint Message;public IntPtr WParam,LParam;public uint Time;public int X,Y;}
        const uint SMTO_ABORTIFHUNG=2;
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr FindWindowW(string cls,string name);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr SendMessageTimeoutW(IntPtr hwnd,uint msg,IntPtr wParam,IntPtr lParam,uint flags,uint timeout,out IntPtr result);
        [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern ushort RegisterClassW(ref WndClass c);
        [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
        static extern IntPtr CreateWindowExW(uint exStyle,string cls,string name,uint style,int x,int y,int w,int h,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr param);
        [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr DefWindowProcW(IntPtr hwnd,uint msg,IntPtr wParam,IntPtr lParam);
        [DllImport("user32.dll")] static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd,uint msg,uint action,IntPtr info);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern bool PeekMessageW(out Msg msg,IntPtr hwnd,uint min,uint max,uint remove);
        [DllImport("user32.dll")] static extern bool TranslateMessage(ref Msg msg);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr DispatchMessageW(ref Msg msg);
        [DllImport("user32.dll")] static extern uint MsgWaitForMultipleObjects(uint count,IntPtr[] handles,bool all,uint ms,uint wake);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string name);
    }
}
