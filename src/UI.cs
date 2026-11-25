using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

[assembly: AssemblyTitle("Love My Shelf")]
[assembly: AssemblyProduct("Love My Shelf")]
[assembly: AssemblyDescription("Create portrait covers from local photos and video frames.")]
[assembly: AssemblyVersion(LoveMyShelf.AppInfo.Version+".0")]
[assembly: AssemblyFileVersion(LoveMyShelf.AppInfo.Version+".0")]

namespace LoveMyShelf {
    internal static class UI {
        public static Brush B(string key) { return (Brush)Application.Current.Resources[key]; }
        public static Button Btn(string label, Action action, bool primary=false) {
            var b = new Button { Content=label, MinHeight=27 };
            if(primary) b.Style=(Style)Application.Current.Resources["Primary"];
            b.Click += delegate { action(); };
            return b;
        }
        public static TextBlock Text(string text,double size, bool muted=false) {
            var block=new TextBlock { Text=text,FontSize=size,VerticalAlignment=VerticalAlignment.Center };
            block.SetResourceReference(TextBlock.ForegroundProperty,muted?"Subtle":"Text");
            return block;
        }
        // Follows the light/dark color named by key, also when the theme changes later.
        public static T Use<T>(T element,DependencyProperty property,string key) where T:FrameworkElement {
            element.SetResourceReference(property,key);return element;
        }
        // Use the bundled Pretendard font (fonts folder beside the exe) for every window.
        // Without the folder the theme keeps its default, Segoe UI with Malgun Gothic for Korean.
        public static void LoadFonts(Application app) {
            try {
                string folder=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"fonts");
                if(!File.Exists(Path.Combine(folder,"Pretendard-Regular.otf")))return;
                var baseUri=new Uri(folder.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar);
                app.Resources["AppFont"]=new FontFamily(baseUri,"./#Pretendard, Segoe UI, Malgun Gothic");
            } catch(Exception) {}
        }
        // Set by the automated checks: open windows on the primary display instead of the one
        // under the mouse, so checks never appear on the monitor the person is using.
        internal static bool PrimaryScreenOnly;
        public static void WindowSetup(Window w,string title) {
            w.Title=title; w.Style=(Style)Application.Current.Resources[typeof(Window)];
            w.WindowStartupLocation=WindowStartupLocation.CenterScreen;
            if(PrimaryScreenOnly) w.SourceInitialized+=delegate {
                if(w.WindowStartupLocation!=WindowStartupLocation.CenterScreen)return;
                Rect area=SystemParameters.WorkArea;
                double width=Double.IsNaN(w.Width)?w.ActualWidth:w.Width,height=Double.IsNaN(w.Height)?w.ActualHeight:w.Height;
                w.WindowStartupLocation=WindowStartupLocation.Manual;
                w.Left=area.Left+Math.Max(0,(area.Width-width)/2);w.Top=area.Top+Math.Max(0,(area.Height-height)/2);
            };
        }
        public static void Error(Window w,Exception ex) {
            MessageBox.Show(w,ex.Message,"Love My Shelf",MessageBoxButton.OK,MessageBoxImage.Warning);
        }
        public static DockPanel Bar(Thickness margin) { return new DockPanel { Margin=margin,LastChildFill=true }; }
        public static void Right(DockPanel p, UIElement e) { DockPanel.SetDock(e,Dock.Right); p.Children.Add(e); }
        public static string PickFolder(Window owner,string description,string initial,bool allowCreate) {
            // The Explorer-style folder window (address bar, Quick access, search). The old tree window is only a fallback.
            IntPtr handle=new System.Windows.Interop.WindowInteropHelper(owner).Handle;
            string chosen;
            if(FolderDialog.TryPick(handle,description,initial,out chosen))return chosen;
            using(var picker=new System.Windows.Forms.FolderBrowserDialog {Description=description,ShowNewFolderButton=allowCreate}) {
                if(Directory.Exists(initial))picker.SelectedPath=initial;
                var native=new System.Windows.Forms.NativeWindow();
                native.AssignHandle(new System.Windows.Interop.WindowInteropHelper(owner).Handle);
                try{return picker.ShowDialog(native)==System.Windows.Forms.DialogResult.OK?picker.SelectedPath:null;}
                finally{native.ReleaseHandle();}
            }
        }
    }
    // Windows' own Explorer-style "Select folder" window (IFileOpenDialog with FOS_PICKFOLDERS).
    internal static class FolderDialog {
        const uint PickFolders=0x20,ForceFileSystem=0x40,NoChangeDir=0x8,PathMustExist=0x800;
        const int Cancelled=unchecked((int)0x800704C7);
        const uint FileSystemPath=0x80058000;
        // Returns false only when the window could not be shown at all; a cancel returns true with null.
        public static bool TryPick(IntPtr owner,string title,string initial,out string chosen) {
            chosen=null;
            IFileDialog dialog=null;
            try { dialog=(IFileDialog)new FileOpenDialogClass(); }
            catch(Exception) { return false; }
            try {
                uint options;dialog.GetOptions(out options);
                dialog.SetOptions(options|PickFolders|ForceFileSystem|NoChangeDir|PathMustExist);
                if(!string.IsNullOrEmpty(title))dialog.SetTitle(title);
                if(!string.IsNullOrEmpty(initial) && Directory.Exists(initial)) {
                    IShellItem start;
                    if(SHCreateItemFromParsingName(initial,IntPtr.Zero,typeof(IShellItem).GUID,out start)==0 && start!=null) {
                        try{dialog.SetFolder(start);}finally{Marshal.ReleaseComObject(start);}
                    }
                }
                int result=dialog.Show(owner);
                if(result==Cancelled)return true;
                if(result!=0)return false;
                IShellItem item;dialog.GetResult(out item);
                try {
                    IntPtr text;item.GetDisplayName(FileSystemPath,out text);
                    try{chosen=Marshal.PtrToStringUni(text);}finally{Marshal.FreeCoTaskMem(text);}
                }
                finally{Marshal.ReleaseComObject(item);}
                return true;
            }
            catch(Exception) { chosen=null;return false; }
            finally{Marshal.ReleaseComObject(dialog);}
        }
        [DllImport("shell32.dll",CharSet=CharSet.Unicode,PreserveSig=true)]
        static extern int SHCreateItemFromParsingName(string path,IntPtr bindContext,[MarshalAs(UnmanagedType.LPStruct)] Guid riid,out IShellItem item);
        [ComImport,Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")] class FileOpenDialogClass {}
        [ComImport,Guid("42f85136-db7e-439c-85f1-e4075d135fc8"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileDialog {
            [PreserveSig] int Show(IntPtr owner);
            void SetFileTypes(uint count,IntPtr specs);
            void SetFileTypeIndex(uint index);
            void GetFileTypeIndex(out uint index);
            void Advise(IntPtr events,out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint options);
            void GetOptions(out uint options);
            void SetDefaultFolder(IShellItem item);
            void SetFolder(IShellItem item);
            void GetFolder(out IShellItem item);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem item);
        }
        [ComImport,Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem {
            void BindToHandler(IntPtr bindContext,ref Guid handler,ref Guid riid,out IntPtr result);
            void GetParent(out IShellItem parent);
            void GetDisplayName(uint form,out IntPtr name);
            void GetAttributes(uint mask,out uint attributes);
            void Compare(IShellItem other,uint hint,out int order);
        }
    }
    internal static class Program {
        // A file sent from File Explorer before a folder window could show it.
        internal static volatile string PendingPath;
        // Called from the listening thread: the first folder window opens the file's folder and its large view.
        internal static void OpenFromOutside(string path) {
            var app=Application.Current;
            if(app==null){PendingPath=path;return;}
            app.Dispatcher.BeginInvoke(new Action(()=>{
                var window=app.Windows.OfType<GalleryWindow>().FirstOrDefault(w=>w.IsVisible);
                if(window==null){PendingPath=path;return;}
                window.OpenExternalFile(path);
            }));
        }
        [STAThread] public static void Main(string[] args) {
            // From here on, whatever ends the program without warning leaves its reason in errors.txt (the helpers too).
            ErrorLog.WatchCrashes();
            // The update helper: a copy of this program that swaps the files after the app has closed.
            if(args.Length==3 && args[0]==AppUpdater.ApplySwitch) {
                Environment.Exit(AppUpdater.RunApply(args[1],args[2]));
                return;
            }
            string data=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string root=Path.Combine(data,"LoveMyShelf","Folders");
            L.Korean=Preferences.Load(root).Korean;
            // Background upscaling runs in a separate copy of this program, without a window.
            if(args.Length==3 && args[0]==UpscaleEngine.ChildSwitch) {
                Environment.Exit(UpscaleEngine.RunChild(args[1],args[2]));
                return;
            }
            // Finding faces runs the same way.
            if(args.Length==3 && args[0]==FaceEngine.ChildSwitch) {
                Environment.Exit(FaceEngine.RunChild(args[1],args[2]));
                return;
            }
            string openPath=ExplorerOpen.PathFrom(args); // a file or folder from "Open with Love My Shelf"
            // Asked by taking it, not by whether it was already there: the update helper keeps it open, without holding
            // it, while it waits for this new version to start, and that must not look like a second copy running.
            using(var guard=new Mutex(false,AppUpdater.AppMutex)) {
                bool first;
                try {first=guard.WaitOne(0);} catch(AbandonedMutexException) {first=true;}
                if(!first) {
                    // Hand the file to the program that is already running instead of complaining.
                    if(openPath!=null && ExplorerOpen.Send(openPath))return;
                    MessageBox.Show(L.T("Love My Shelf is already running."),"Love My Shelf"); return;
                }
                // Never let go of here: it is let go when this thread ends, as late as can be, and the update helper waits for that.
                PendingPath=openPath;
                ExplorerOpen.Listen(OpenFromOutside);
                bool running=false;
                // Ending on a problem: the file queue stops where it is and is kept, as it is when the program is closed.
                // It waits five seconds at most, since the thread that failed may be the one it would wait for.
                ErrorLog.BeforeCrash=TransferQueue.StopForCrash;
                try {
                    var app=new Application();
                    using(Stream stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("LoveMyShelf.Theme.xaml"))
                        app.Resources=(ResourceDictionary)XamlReader.Load(stream);
                    UI.LoadFonts(app);
                    var prefs=Preferences.Load(root);
                    // Keeps the File Explorer menu entry pointing at this copy (the folder may have moved).
                    if(prefs.ExplorerMenu) {try {ExplorerOpen.Sync(prefs);} catch(Exception) {}}
                    Themes.Apply(prefs.Theme);
                    if(!prefs.LanguageChosen || !prefs.UsageNoticeShown) {
                        // Keep the app alive between this window and the main one.
                        app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
                        new FirstRunWindow(prefs).ShowDialog();
                        prefs.SeenVersion=AppInfo.Version; // a new person has nothing to be told about an update
                        try {Directory.CreateDirectory(root);prefs.Save(root);} catch(IOException) {} catch(UnauthorizedAccessException) {}
                        app.ShutdownMode=ShutdownMode.OnLastWindowClose;
                    }
                    // Windows is shutting down or the person is signing out: the file queue stops where it is and is kept.
                    app.SessionEnding+=delegate {try {TransferQueue.Shutdown();} catch(Exception quiet) {ErrorLog.Quiet(quiet);}};
                    var firstWindow=new GalleryWindow(new GalleryStore(root));
                    running=true;
                    app.Run(firstWindow);
                } catch(Exception ex) {
                    if(AppUpdater.HelperWaiting) {
                        // Just updated, and this version can't open: no window to answer, so the one before it comes back now.
                        ErrorLog.Serious("The new version could not open",ex);
                        if(running)ErrorLog.RunBeforeCrash();
                        return;
                    }
                    if(!running) {
                        ErrorLog.Serious("Could not open",ex);
                        MessageBox.Show(ex.Message,L.T("Could not open Love My Shelf"),MessageBoxButton.OK,MessageBoxImage.Error);
                    } else {
                        // A problem nothing was ready for, in the middle of use.
                        ErrorLog.Serious("Crash",ex);
                        ErrorLog.RunBeforeCrash();
                        // The windows are still there, in whatever state the problem left them: this window may be cut short by them.
                        try {
                            MessageBox.Show(L.PF("Love My Shelf ran into a problem and has to close.\n\n{0}\n\nWhat happened is written in this file:\n{1}",
                                "문제가 생겨서 Love My Shelf를 닫아야 해요.\n\n{0}\n\n무슨 일이 있었는지 이 파일에 적어 두었어요:\n{1}",ex.Message,ErrorLog.Path),
                                "Love My Shelf",MessageBoxButton.OK,MessageBoxImage.Error);
                        } catch(Exception again) {ErrorLog.Serious("While telling about the crash",again);}
                    }
                }
            }
        }
    }
}
