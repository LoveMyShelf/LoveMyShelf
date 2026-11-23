using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace LoveMyShelf {
    // AI upscaling with Swin2SR (x4) through ONNX Runtime. The runtime and model live in an
    // "upscale" folder beside the executable. Work runs in a child process at below-normal
    // priority so the app stays responsive and native failures cannot take the app down.
    internal static class UpscaleEngine {
        internal const int Factor=4;
        // Covers this tall or taller are already large; the button is hidden for them.
        internal const int MaxSourceHeight=1920;
        // Very large results are scaled down to this height.
        internal const int MaxOutputHeight=3840;
        internal const string ChildSwitch="--upscale";
        const int Tile=224,Pad=16;

        internal static string Folder {get {return Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"upscale");}}
        internal static bool Available {
            get {return File.Exists(Path.Combine(Folder,"onnxruntime.dll")) && File.Exists(Path.Combine(Folder,"model.onnx"));}
        }
        internal static bool Suitable(int coverHeight) {return Available && coverHeight>0 && coverHeight<MaxSourceHeight;}

        // Child process entry: LoveMyShelf.exe --upscale <input> <output>
        internal static int RunChild(string input,string output) {
            try {
                var source=new FormatConvertedBitmap(Load(input),PixelFormats.Rgb24,null,0);
                int w=source.PixelWidth,h=source.PixelHeight,stride=w*3;
                var pixels=new byte[stride*h];
                source.CopyPixels(pixels,stride,0);
                byte[] result;
                using(var ort=new OnnxSession(Path.Combine(Folder,"onnxruntime.dll"),Path.Combine(Folder,"model.onnx"),
                    Math.Max(1,Environment.ProcessorCount-2))) {
                    result=Run(ort,pixels,w,h,(done,total)=>{Console.Out.WriteLine("progress "+done+" "+total);Console.Out.Flush();});
                }
                BitmapSource image=BitmapSource.Create(w*Factor,h*Factor,96,96,PixelFormats.Rgb24,null,result,w*Factor*3);
                if(image.PixelHeight>MaxOutputHeight)image=Shrink(image,MaxOutputHeight);
                using(var stream=File.Create(output)) {
                    var encoder=new JpegBitmapEncoder {QualityLevel=100};
                    encoder.Frames.Add(BitmapFrame.Create(image));
                    encoder.Save(stream);
                }
                return 0;
            } catch(Exception ex) {
                try{Console.Error.WriteLine(ex.Message);Console.Error.Flush();}catch(IOException){}
                return 1;
            }
        }
        static BitmapSource Load(string path) {
            using(var file=File.Open(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete)) {
                var frame=BitmapFrame.Create(file,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
                frame.Freeze();return frame;
            }
        }
        static BitmapSource Shrink(BitmapSource image,int height) {
            int width=Math.Max(1,(int)Math.Round(image.PixelWidth*(double)height/image.PixelHeight));
            var visual=new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual,BitmapScalingMode.HighQuality);
            using(var dc=visual.RenderOpen())dc.DrawImage(image,new Rect(0,0,width,height));
            var target=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);
            target.Render(visual);target.Freeze();
            return target;
        }
        static int Reflect(int i,int n) {
            if(n==1)return 0;
            while(i<0 || i>=n){if(i<0)i=-i;if(i>=n)i=2*n-2-i;}
            return i;
        }
        // Runs the model on overlapping tiles of a fixed size and keeps each tile's center.
        static byte[] Run(OnnxSession ort,byte[] rgb,int w,int h,Action<int,int> progress) {
            int side=Tile+2*Pad,outW=w*Factor,outH=h*Factor;
            var output=new byte[outW*outH*3];
            var input=new float[3*side*side];
            int across=(w+Tile-1)/Tile,down=(h+Tile-1)/Tile,total=across*down,done=0;
            progress(0,total);
            for(int ty=0;ty<down;ty++) for(int tx=0;tx<across;tx++) {
                int x0=tx*Tile-Pad,y0=ty*Tile-Pad;
                for(int y=0;y<side;y++) {
                    int sy=Reflect(y0+y,h);
                    for(int x=0;x<side;x++) {
                        int s=(sy*w+Reflect(x0+x,w))*3,d=y*side+x;
                        input[d]=rgb[s]/255f;input[side*side+d]=rgb[s+1]/255f;input[2*side*side+d]=rgb[s+2]/255f;
                    }
                }
                int outSide;
                float[] result=ort.Run(input,side,out outSide);
                int keepW=Math.Min(Tile,w-tx*Tile)*Factor,keepH=Math.Min(Tile,h-ty*Tile)*Factor,offset=Pad*Factor,plane=outSide*outSide;
                for(int y=0;y<keepH;y++) {
                    int row=((ty*Tile*Factor+y)*outW+tx*Tile*Factor)*3,src=(offset+y)*outSide+offset;
                    for(int x=0;x<keepW;x++) {
                        int p=src+x,q=row+x*3;
                        output[q]=ToByte(result[p]);output[q+1]=ToByte(result[plane+p]);output[q+2]=ToByte(result[2*plane+p]);
                    }
                }
                progress(++done,total);
            }
            return output;
        }
        static byte ToByte(float v) {
            float scaled=v*255f+0.5f;
            return scaled<=0?(byte)0:scaled>=255?(byte)255:(byte)scaled;
        }
    }

    // Minimal binding to the ONNX Runtime C API (function table order from onnxruntime_c_api.h).
    sealed class OnnxSession : IDisposable {
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr LoadLibraryEx(string path,IntPtr file,uint flags);
        [DllImport("kernel32.dll",CharSet=CharSet.Ansi,ExactSpelling=true)] static extern IntPtr GetProcAddress(IntPtr module,string name);
        const uint AlteredSearchPath=0x8;
        const uint ApiVersion=17;

        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr GetApiBaseFn();
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr GetApiFn(uint version);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr ErrorMessageFn(IntPtr status);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr CreateEnvFn(int level,byte[] id,out IntPtr env);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr CreateSessionFn(IntPtr env,[MarshalAs(UnmanagedType.LPWStr)] string path,IntPtr options,out IntPtr session);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr RunFn(IntPtr session,IntPtr runOptions,IntPtr[] inputNames,IntPtr[] inputs,UIntPtr inputCount,IntPtr[] outputNames,UIntPtr outputCount,IntPtr[] outputs);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr CreateOptionsFn(out IntPtr options);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr SetThreadsFn(IntPtr options,int threads);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr CreateTensorFn(IntPtr info,IntPtr data,UIntPtr bytes,long[] shape,UIntPtr rank,int type,out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr GetDataFn(IntPtr value,out IntPtr data);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr DimCountFn(IntPtr info,out UIntPtr count);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr DimsFn(IntPtr info,long[] dims,UIntPtr count);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr ShapeFn(IntPtr value,out IntPtr info);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr CpuInfoFn(int allocator,int memType,out IntPtr info);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate void ReleaseFn(IntPtr item);

        readonly IntPtr api;
        readonly ErrorMessageFn errorMessage;
        readonly RunFn run;
        readonly CreateTensorFn createTensor;
        readonly GetDataFn getData;
        readonly DimCountFn dimCount;
        readonly DimsFn dims;
        readonly ShapeFn shape;
        readonly ReleaseFn releaseEnv,releaseStatus,releaseMemoryInfo,releaseSession,releaseValue,releaseShape,releaseOptions;
        IntPtr env,options,session,memory;
        readonly IntPtr inputName=Marshal.StringToHGlobalAnsi("pixel_values"),outputName=Marshal.StringToHGlobalAnsi("reconstruction");

        T Fn<T>(int index) where T:class {
            return (T)(object)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(api,index*IntPtr.Size),typeof(T));
        }
        internal OnnxSession(string library,string model,int threads) {
            IntPtr module=LoadLibraryEx(library,IntPtr.Zero,AlteredSearchPath);
            if(module==IntPtr.Zero)throw new IOException(L.T("Could not load the upscaling tool. Check that the Love My Shelf folder has its upscale folder.")+" ("+Marshal.GetLastWin32Error()+")");
            IntPtr getBase=GetProcAddress(module,"OrtGetApiBase");
            if(getBase==IntPtr.Zero)throw new IOException(L.T("Could not load the upscaling tool. Check that the Love My Shelf folder has its upscale folder."));
            IntPtr apiBase=((GetApiBaseFn)Marshal.GetDelegateForFunctionPointer(getBase,typeof(GetApiBaseFn)))();
            api=((GetApiFn)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(apiBase,0),typeof(GetApiFn)))(ApiVersion);
            if(api==IntPtr.Zero)throw new IOException(L.T("Could not load the upscaling tool. Check that the Love My Shelf folder has its upscale folder."));
            errorMessage=Fn<ErrorMessageFn>(2);
            run=Fn<RunFn>(9);
            createTensor=Fn<CreateTensorFn>(49);
            getData=Fn<GetDataFn>(51);
            dimCount=Fn<DimCountFn>(61);
            dims=Fn<DimsFn>(62);
            shape=Fn<ShapeFn>(65);
            releaseEnv=Fn<ReleaseFn>(92);releaseStatus=Fn<ReleaseFn>(93);releaseMemoryInfo=Fn<ReleaseFn>(94);
            releaseSession=Fn<ReleaseFn>(95);releaseValue=Fn<ReleaseFn>(96);releaseShape=Fn<ReleaseFn>(99);releaseOptions=Fn<ReleaseFn>(100);
            try {
                Check(Fn<CreateEnvFn>(3)(3,System.Text.Encoding.ASCII.GetBytes("LoveMyShelf\0"),out env));
                Check(Fn<CreateOptionsFn>(10)(out options));
                Check(Fn<SetThreadsFn>(24)(options,threads));
                Check(Fn<CreateSessionFn>(7)(env,model,options,out session));
                Check(Fn<CpuInfoFn>(69)(1,0,out memory));
            } catch {Dispose();throw;}
        }
        void Check(IntPtr status) {
            if(status==IntPtr.Zero)return;
            string message=Marshal.PtrToStringAnsi(errorMessage(status));
            releaseStatus(status);
            throw new IOException(message);
        }
        // Input: planar RGB floats (1, 3, side, side). Output: planar RGB floats of the upscaled square.
        internal float[] Run(float[] input,int side,out int outSide) {
            var handle=GCHandle.Alloc(input,GCHandleType.Pinned);
            IntPtr tensor=IntPtr.Zero,info=IntPtr.Zero;
            var outputs=new IntPtr[1];
            try {
                Check(createTensor(memory,handle.AddrOfPinnedObject(),(UIntPtr)(input.Length*4),new long[]{1,3,side,side},(UIntPtr)4,1,out tensor));
                Check(run(session,IntPtr.Zero,new[]{inputName},new[]{tensor},(UIntPtr)1,new[]{outputName},(UIntPtr)1,outputs));
                Check(shape(outputs[0],out info));
                UIntPtr count;Check(dimCount(info,out count));
                var size=new long[(int)count];Check(dims(info,size,count));
                if(size.Length!=4 || size[1]!=3 || size[2]!=size[3])throw new InvalidDataException(L.T("The result didn't come out right, so it wasn't saved."));
                outSide=(int)size[2];
                IntPtr data;Check(getData(outputs[0],out data));
                var result=new float[3*outSide*outSide];
                Marshal.Copy(data,result,0,result.Length);
                return result;
            } finally {
                if(info!=IntPtr.Zero)releaseShape(info);
                if(outputs[0]!=IntPtr.Zero)releaseValue(outputs[0]);
                if(tensor!=IntPtr.Zero)releaseValue(tensor);
                handle.Free();
            }
        }
        public void Dispose() {
            if(memory!=IntPtr.Zero){releaseMemoryInfo(memory);memory=IntPtr.Zero;}
            if(session!=IntPtr.Zero){releaseSession(session);session=IntPtr.Zero;}
            if(options!=IntPtr.Zero){releaseOptions(options);options=IntPtr.Zero;}
            if(env!=IntPtr.Zero){releaseEnv(env);env=IntPtr.Zero;}
        }
    }

    internal enum UpscaleStage {None,Queued,Running}

    // One upscale at a time, in the order requested. Results stay pending until the person chooses.
    internal sealed class UpscaleJobs {
        sealed class Job {internal Photo Photo;internal string Revision;}
        readonly List<Job> queue=new List<Job>();
        Job running;
        Process child;
        int done,total;
        bool stopped;
        internal event Action<string> Changed;          // photo folder
        internal event Action<Photo> Finished;          // updated photo with a pending choice
        internal event Action<Photo,string> Failed;     // photo, message

        internal bool IsBusy {get {return running!=null || queue.Count>0;}}
        internal UpscaleStage Stage(string folder,out int percent) {
            percent=0;
            if(running!=null && Same(running.Photo.Folder,folder)) {
                percent=total>0?(int)(100L*done/total):0;
                return UpscaleStage.Running;
            }
            return queue.Exists(j=>Same(j.Photo.Folder,folder))?UpscaleStage.Queued:UpscaleStage.None;
        }
        static bool Same(string a,string b) {return String.Equals(a,b,StringComparison.OrdinalIgnoreCase);}

        internal void Enqueue(Photo photo) {
            if(stopped || !File.Exists(photo.CoverFile))return;
            int percent;
            if(Stage(photo.Folder,out percent)!=UpscaleStage.None)return;
            queue.Add(new Job {Photo=photo,Revision=Path.GetFileName(photo.Thumb)});
            Raise(photo.Folder);
            if(running==null)Next();
        }
        void Raise(string folder) {if(Changed!=null)Changed(folder);}
        async void Next() {
            if(stopped || queue.Count==0)return;
            var job=queue[0];queue.RemoveAt(0);
            running=job;done=0;total=0;
            Raise(job.Photo.Folder);
            string temp=null;
            try {
                string directory=Path.GetDirectoryName(job.Photo.CoverFile);
                temp=Path.Combine(directory,".lovemyshelf-"+Guid.NewGuid().ToString("N")+".tmp");
                string error=await RunChild(job.Photo.CoverFile,temp);
                if(stopped)return;
                if(error!=null)throw new IOException(error);
                var updated=await Task.Run(()=>GalleryStore.AttachUpscale(job.Photo,job.Revision,temp));
                temp=null;
                if(stopped)return;
                running=null;Raise(job.Photo.Folder);
                if(updated==null) {
                    if(Failed!=null)Failed(job.Photo,L.T("The cover changed during upscaling, so the upscaled result wasn't saved."));
                } else if(Finished!=null)Finished(updated);
            } catch(Exception ex) {
                if(stopped)return;
                running=null;Raise(job.Photo.Folder);
                if(Failed!=null)Failed(job.Photo,ex.Message);
            } finally {
                if(temp!=null)try{File.Delete(temp);}catch(IOException){}catch(UnauthorizedAccessException){}
                if(running==job)running=null;
                if(!stopped)Next();
            }
        }
        Task<string> RunChild(string input,string output) {
            var done=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var start=new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,
                UpscaleEngine.ChildSwitch+" \""+input+"\" \""+output+"\"") {
                UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,
                StandardOutputEncoding=System.Text.Encoding.UTF8,StandardErrorEncoding=System.Text.Encoding.UTF8};
            var process=new Process {StartInfo=start,EnableRaisingEvents=true};
            var errors=new System.Text.StringBuilder();
            var dispatcher=Dispatcher.CurrentDispatcher;
            process.OutputDataReceived+=(s,e)=>{
                if(e.Data==null || !e.Data.StartsWith("progress "))return;
                var parts=e.Data.Split(' ');
                int a,b;
                if(parts.Length==3 && Int32.TryParse(parts[1],out a) && Int32.TryParse(parts[2],out b))
                    dispatcher.BeginInvoke(new Action(()=>{
                        if(child!=process)return;
                        this.done=a;total=b;
                        if(running!=null)Raise(running.Photo.Folder);
                    }));
            };
            process.ErrorDataReceived+=(s,e)=>{if(e.Data!=null)lock(errors)errors.AppendLine(e.Data);};
            process.Exited+=delegate {
                // Let the asynchronous readers drain before reporting.
                process.WaitForExit();
                int code=process.ExitCode;
                string message;
                lock(errors)message=errors.ToString().Trim();
                dispatcher.BeginInvoke(new Action(()=>{
                    if(child==process)child=null;
                    process.Dispose();
                    done.TrySetResult(code==0?null:(message.Length>0?message:L.T("It stopped in the middle of upscaling.")));
                }));
            };
            process.Start();
            child=process;
            try{process.PriorityClass=ProcessPriorityClass.BelowNormal;}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}
            process.BeginOutputReadLine();process.BeginErrorReadLine();
            return done.Task;
        }
        internal void Stop() {
            stopped=true;queue.Clear();
            var process=child;child=null;
            if(process!=null)try{process.Kill();}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}
        }
    }

    // A small notice in the lower-right corner of the LoveMyShelf window.
    internal sealed class UpscaleToast : Window {
        readonly DispatcherTimer timer=new DispatcherTimer {Interval=TimeSpan.FromSeconds(15)};
        internal bool IsWorking {get {return kind==NoticeKind.Busy && !closed;}}
        readonly NoticeKind kind;
        // True when the notice closed because it was clicked (not by its timer or the ✕).
        internal bool WasClicked {get;private set;}
        internal UpscaleToast(Window anchor,string title,string message,Action clicked,NoticeKind kind=NoticeKind.Done,bool keepOpen=false,double seconds=15) {
            this.kind=kind;timer.Interval=TimeSpan.FromSeconds(seconds);
            WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;ShowInTaskbar=false;Topmost=true;ShowActivated=false;
            AllowsTransparency=true;Background=Brushes.Transparent;
            SizeToContent=SizeToContent.Height;Width=380;
            FontFamily=(FontFamily)Application.Current.Resources["AppFont"];
            if(anchor!=null && anchor.IsVisible)Owner=anchor;
            TextBlock heading,detail;ContentControl icon;
            var card=NoticeLook.Card(out heading,out detail,out icon);
            heading.Text=NoticeLook.KeepWords(title);detail.Text=NoticeLook.KeepWords(message);
            if(String.IsNullOrEmpty(message))detail.Visibility=Visibility.Collapsed;
            icon.Content=NoticeLook.Icon(kind);card.Padding=new Thickness(18,14,42,15);
            var close=new Button {Content="✕",Style=(Style)Application.Current.Resources["TitleBarButton"],
                VerticalAlignment=VerticalAlignment.Top,HorizontalAlignment=HorizontalAlignment.Right,Width=26,Height=24,Margin=new Thickness(0,8,8,0)};
            close.SetResourceReference(Control.ForegroundProperty,"Subtle");
            System.Windows.Automation.AutomationProperties.SetName(close,L.T("Close"));
            close.Click+=(s,e)=>{e.Handled=true;Close();};
            var body=new Grid {Margin=new Thickness(16,12,16,20)};
            body.Children.Add(card);body.Children.Add(close);
            Content=body;
            Loaded+=delegate {NoticeLook.Enter(card);};
            if(clicked!=null) {
                Cursor=Cursors.Hand;
                MouseLeftButtonUp+=delegate {WasClicked=true;Close();clicked();};
            }
            // A "working on it" notice stays until the work ends and the caller dismisses it.
            bool stays=kind==NoticeKind.Busy || keepOpen;
            timer.Tick+=delegate {Close();};
            MouseEnter+=delegate {timer.Stop();};
            MouseLeave+=delegate {if(!stays)timer.Start();};
            Closed+=delegate {closed=true;timer.Stop();};
            Loaded+=delegate {Place(anchor);if(!stays)timer.Start();};
        }
        bool closed;
        [DllImport("user32.dll")] static extern bool EnableWindow(IntPtr window,bool enable);
        // A dialog opened with ShowDialog switches off every other window, including this notice, and then
        // a click on the ✕ only makes the system beep. The notice never takes input from the dialog, so it switches itself back on.
        protected override void OnSourceInitialized(EventArgs e) {
            base.OnSourceInitialized(e);
            var source=PresentationSource.FromVisual(this) as HwndSource;
            if(source!=null)source.AddHook(KeepEnabled);
        }
        IntPtr KeepEnabled(IntPtr window,int message,IntPtr wParam,IntPtr lParam,ref bool handled) {
            const int WM_ENABLE=0x000A;
            if(message==WM_ENABLE && wParam==IntPtr.Zero && !closed)
                Dispatcher.BeginInvoke(new Action(()=>{if(!closed)EnableWindow(window,true);}),DispatcherPriority.Send);
            return IntPtr.Zero;
        }
        // Closes the notice if it is still showing; safe to call more than once.
        internal void Dismiss() {if(!closed)Close();}
        void Place(Window anchor) {
            Rect area=SystemParameters.WorkArea;
            if(anchor!=null && anchor.IsVisible && anchor.WindowState!=WindowState.Minimized) {
                var source=PresentationSource.FromVisual(anchor);
                if(source!=null && source.CompositionTarget!=null) {
                    var toDips=source.CompositionTarget.TransformFromDevice;
                    Point corner=toDips.Transform(anchor.PointToScreen(new Point(anchor.ActualWidth,anchor.ActualHeight)));
                    Left=corner.X-ActualWidth+2;Top=corner.Y-ActualHeight-26;
                    return;
                }
            }
            Left=area.Right-ActualWidth+2;Top=area.Bottom-ActualHeight+6;
        }
    }

    // Shows the current cover and its upscaled version side by side. Wheel zoom and drag move both together.
    internal sealed class UpscaleChoiceWindow : Window {
        internal bool? ChoseUpscaled {get;private set;}
        readonly ScaleTransform zoom=new ScaleTransform();
        readonly TranslateTransform shift=new TranslateTransform();
        Point? dragFrom;

        internal UpscaleChoiceWindow(Window owner,Photo photo) {
            UI.WindowSetup(this,L.T("Choose a cover"));
            if(owner!=null && owner.IsVisible){Owner=owner;WindowStartupLocation=WindowStartupLocation.CenterOwner;}
            ShowInTaskbar=owner==null || !owner.IsVisible;
            var area=SystemParameters.WorkArea;
            Width=Math.Min(1100,area.Width);Height=Math.Min(860,area.Height);MinWidth=420;MinHeight=360;
            var transform=new TransformGroup();transform.Children.Add(zoom);transform.Children.Add(shift);
            var windowContent=new DockPanel();
            windowContent.Children.Add(new WindowTitleBar(this));
            var root=new DockPanel {Margin=new Thickness(12,8,12,12)};
            windowContent.Children.Add(root);Content=windowContent;
            var hint=UI.Text(L.T("Zoom with the wheel and drag to move. The two pictures move together. Double-click to go back to how it was.")+" "+
                L.T("The file you don't choose also stays in the cover folder."),12,true);
            hint.TextWrapping=TextWrapping.Wrap;hint.Margin=new Thickness(2,0,2,8);
            DockPanel.SetDock(hint,Dock.Top);root.Children.Add(hint);
            var footer=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,10,0,0)};
            var later=UI.Btn(L.T("Decide later"),Close);later.MinWidth=90;footer.Children.Add(later);
            DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
            var columns=new Grid();
            columns.ColumnDefinitions.Add(new ColumnDefinition());
            columns.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(10)});
            columns.ColumnDefinitions.Add(new ColumnDefinition());
            root.Children.Add(columns);
            bool upscaledNow=photo.UpscaleChoice=="upscaled";
            AddSide(columns,0,L.T("Original cover"),photo.CoverFile,transform,!upscaledNow,false);
            AddSide(columns,2,L.T("Upscaled"),photo.UpscaleFile,transform,upscaledNow,true);
            PreviewKeyDown+=(s,e)=>{if(e.Key==Key.Escape){Close();e.Handled=true;}};
        }
        void AddSide(Grid columns,int column,string title,string path,Transform transform,bool inUse,bool upscaled) {
            var side=new DockPanel();
            var header=new DockPanel {Margin=new Thickness(2,0,2,6)};
            var name=UI.Text(title,14);name.FontWeight=FontWeights.SemiBold;
            var size=UI.Text("",12,true);size.Margin=new Thickness(8,0,0,0);
            header.Children.Add(name);header.Children.Add(size);
            DockPanel.SetDock(header,Dock.Top);side.Children.Add(header);
            var choose=UI.Btn(inUse?L.T("In use · keep using it"):L.T("Use this cover"),()=>{ChoseUpscaled=upscaled;DialogResult=true;},true);
            choose.Margin=new Thickness(0,8,0,0);choose.MinHeight=30;
            DockPanel.SetDock(choose,Dock.Bottom);side.Children.Add(choose);
            var image=new Image {Stretch=Stretch.Uniform,RenderTransform=transform};
            RenderOptions.SetBitmapScalingMode(image,BitmapScalingMode.HighQuality);
            var frame=new Border {Background=Brushes.Black,ClipToBounds=true,Child=image,
                BorderThickness=new Thickness(inUse?2:1),Cursor=Cursors.SizeAll};
            frame.SetResourceReference(Border.BorderBrushProperty,inUse?"Accent":"Line");
            frame.MouseWheel+=(s,e)=>{Zoom(image,e.GetPosition(image),e.Delta>0?1.25:0.8);e.Handled=true;};
            frame.MouseLeftButtonDown+=(s,e)=>{
                if(e.ClickCount==2){zoom.ScaleX=zoom.ScaleY=1;shift.X=shift.Y=0;return;}
                dragFrom=e.GetPosition(this);frame.CaptureMouse();e.Handled=true;
            };
            frame.MouseMove+=(s,e)=>{
                if(dragFrom==null || !frame.IsMouseCaptured)return;
                Point now=e.GetPosition(this);
                shift.X+=now.X-dragFrom.Value.X;shift.Y+=now.Y-dragFrom.Value.Y;dragFrom=now;
            };
            frame.MouseLeftButtonUp+=delegate {dragFrom=null;frame.ReleaseMouseCapture();};
            side.Children.Add(frame);
            Grid.SetColumn(side,column);columns.Children.Add(side);
            LoadInto(image,size,path);
        }
        void Zoom(Image image,Point at,double factor) {
            double scale=Math.Max(1,Math.Min(10,zoom.ScaleX*factor));
            if(scale==1){zoom.ScaleX=zoom.ScaleY=1;shift.X=shift.Y=0;return;}
            shift.X+=at.X*(zoom.ScaleX-scale);shift.Y+=at.Y*(zoom.ScaleY-scale);
            zoom.ScaleX=zoom.ScaleY=scale;
        }
        async void LoadInto(Image image,TextBlock size,string path) {
            try {
                var bitmap=await Task.Run(()=>{
                    using(var file=File.Open(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete)) {
                        var frame=BitmapFrame.Create(file,BitmapCreateOptions.None,BitmapCacheOption.OnLoad);
                        frame.Freeze();return (BitmapSource)frame;
                    }
                });
                image.Source=bitmap;
                size.Text=L.F("{0} × {1} px",bitmap.PixelWidth,bitmap.PixelHeight);
            } catch(Exception ex) {size.Text=ex.Message;}
        }
    }
}
