using System;
using System.IO;
using System.Reflection;
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
            using(var picker=new System.Windows.Forms.FolderBrowserDialog {Description=description,ShowNewFolderButton=allowCreate}) {
                if(Directory.Exists(initial))picker.SelectedPath=initial;
                var native=new System.Windows.Forms.NativeWindow();
                native.AssignHandle(new System.Windows.Interop.WindowInteropHelper(owner).Handle);
                try{return picker.ShowDialog(native)==System.Windows.Forms.DialogResult.OK?picker.SelectedPath:null;}
                finally{native.ReleaseHandle();}
            }
        }
    }
    internal static class Program {
        [STAThread] public static void Main(string[] args) {
            string data=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string root=Path.Combine(data,"LoveMyShelf","Folders");
            L.Korean=Preferences.Load(root).Korean;
            // Background upscaling runs in a separate copy of this program, without a window.
            if(args.Length==3 && args[0]==UpscaleEngine.ChildSwitch) {
                Environment.Exit(UpscaleEngine.RunChild(args[1],args[2]));
                return;
            }
            bool first;
            using(var guard=new Mutex(true,@"Local\LoveMyShelf.Personal.v1",out first)) {
                if(!first) { MessageBox.Show(L.T("Love My Shelf is already running."),"Love My Shelf"); return; }
                try {
                    var app=new Application();
                    using(Stream stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("LoveMyShelf.Theme.xaml"))
                        app.Resources=(ResourceDictionary)XamlReader.Load(stream);
                    UI.LoadFonts(app);
                    var prefs=Preferences.Load(root);
                    Themes.Apply(prefs.Theme);
                    if(!prefs.LanguageChosen || !prefs.UsageNoticeShown) {
                        // Keep the app alive between this window and the main one.
                        app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
                        new FirstRunWindow(prefs).ShowDialog();
                        try {Directory.CreateDirectory(root);prefs.Save(root);} catch(IOException) {} catch(UnauthorizedAccessException) {}
                        app.ShutdownMode=ShutdownMode.OnLastWindowClose;
                    }
                    app.Run(new GalleryWindow(new GalleryStore(root)));
                } catch(Exception ex) { MessageBox.Show(ex.Message,L.T("Could not open Love My Shelf"),MessageBoxButton.OK,MessageBoxImage.Error); }
            }
        }
    }
}
