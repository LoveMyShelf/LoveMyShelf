using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
        // Covers this long (on their long side) or longer are already large; the button is hidden for them.
        internal const int MaxSourceHeight=1920;
        // Very large results are scaled down to this long side.
        internal const int MaxOutputHeight=3840;
        internal const string ChildSwitch="--upscale";
        const int Tile=224,Pad=16;

        internal static string Folder {get {return Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"upscale");}}
        internal static bool Available {
            get {return File.Exists(Path.Combine(Folder,"onnxruntime.dll")) && File.Exists(Path.Combine(Folder,"model.onnx"));}
        }
        internal static bool Suitable(int coverLongSide) {return Available && coverLongSide>0 && coverLongSide<MaxSourceHeight;}

        // Set from the options: true runs upscaling on the graphics card (DirectML), false on the processor.
        internal static bool UseGpu;
        internal const string GpuSwitch="gpu";
        internal static bool GpuAvailable {get {return File.Exists(Path.Combine(Folder,"DirectML.dll"));}}

        // Child process entry: LoveMyShelf.exe --upscale <input> <output> [gpu]
        internal static int RunChild(string input,string output,bool gpu=false) {
            try {
                var source=new FormatConvertedBitmap(Load(input),PixelFormats.Rgb24,null,0);
                int w=source.PixelWidth,h=source.PixelHeight,stride=w*3;
                var pixels=new byte[stride*h];
                source.CopyPixels(pixels,stride,0);
                byte[] result;
                using(var device=new Device(gpu && GpuAvailable)) {
                    result=Run(device,pixels,w,h,(done,total)=>{Console.Out.WriteLine("progress "+done+" "+total);Console.Out.Flush();});
                    Console.Out.WriteLine("device "+(device.OnGpu?"gpu":"cpu"));Console.Out.Flush();
                }
                BitmapSource image=BitmapSource.Create(w*Factor,h*Factor,96,96,PixelFormats.Rgb24,null,result,w*Factor*3);
                int longSide=Math.Max(image.PixelWidth,image.PixelHeight);
                if(longSide>MaxOutputHeight)image=Shrink(image,Math.Max(1,(int)Math.Round(image.PixelHeight*(double)MaxOutputHeight/longSide)));
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
        static byte[] Run(Device ort,byte[] rgb,int w,int h,Action<int,int> progress) {
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
        // Runs the model on the graphics card when asked and it works, and otherwise on the processor. Some graphics
        // drivers give wrong pictures (on an Intel Iris Xe tested in 2026, nearly black or blue ones with the usual graph
        // optimizations), so every tile from the graphics card is checked, and at the first failure or wrong tile the
        // rest is done on the processor, starting again from that tile.
        sealed class Device : IDisposable {
            OnnxSession session;
            internal bool OnGpu {get;private set;}
            internal Device(bool gpu) {
                if(gpu) {
                    try {session=Open(true);OnGpu=true;}
                    catch(Exception){session=null;}
                }
                if(session==null)session=Open(false);
            }
            static OnnxSession Open(bool gpu) {
                return new OnnxSession(Path.Combine(Folder,"onnxruntime.dll"),Path.Combine(Folder,"model.onnx"),
                    gpu?2:Math.Max(1,Environment.ProcessorCount-2),gpu:gpu);
            }
            internal float[] Run(float[] input,int side,out int outSide) {
                if(OnGpu) {
                    try {
                        float[] result=session.Run(input,side,out outSide);
                        if(Plausible(input,side,result,outSide))return result;
                    } catch(Exception) {}
                    session.Dispose();session=null;OnGpu=false;
                    session=Open(false);
                }
                return session.Run(input,side,out outSide);
            }
            // The enlarged tile must look like the tile: no missing numbers, and the average of each 4x4 block of the
            // result (cut to 0..1 as it is saved) close to the pixel it came from. A right result measured about 0.002-0.004
            // on average (2026-11-26); black, blue, striped or shuffled results are far above 0.04.
            static bool Plausible(float[] input,int side,float[] output,int outSide) {
                if(outSide!=side*Factor || output.Length!=3*outSide*outSide)return false;
                int inPlane=side*side,outPlane=outSide*outSide;
                double error=0;
                for(int c=0;c<3;c++)
                    for(int y=0;y<side;y++)
                        for(int x=0;x<side;x++) {
                            double sum=0;
                            for(int dy=0;dy<Factor;dy++) {
                                int row=c*outPlane+(y*Factor+dy)*outSide+x*Factor;
                                for(int dx=0;dx<Factor;dx++) {
                                    float v=output[row+dx];
                                    if(Single.IsNaN(v) || Single.IsInfinity(v))return false;
                                    sum+=v<0?0:v>1?1:v;
                                }
                            }
                            error+=Math.Abs(sum/(Factor*Factor)-input[c*inPlane+y*side+x]);
                        }
                return error/(3.0*inPlane)<=0.04;
            }
            public void Dispose() {if(session!=null){session.Dispose();session=null;}}
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
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr OptionsFn(IntPtr options);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr AppendDmlFn(IntPtr options,int device);

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
        readonly IntPtr inputName,outputName;
        readonly IntPtr[] outputNames;

        T Fn<T>(int index) where T:class {
            return (T)(object)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(api,index*IntPtr.Size),typeof(T));
        }
        // input and outputs: the names in the model (the upscaling model's when left out).
        // gpu: run on the graphics card through DirectML (the onnxruntime-directml build), with the settings DirectML needs.
        internal OnnxSession(string library,string model,int threads,string input="pixel_values",string[] outputs=null,bool gpu=false) {
            inputName=Marshal.StringToHGlobalAnsi(input);
            outputs=outputs??new[]{"reconstruction"};
            outputNames=new IntPtr[outputs.Length];
            for(int i=0;i<outputs.Length;i++)outputNames[i]=Marshal.StringToHGlobalAnsi(outputs[i]);
            outputName=outputNames[0];
            IntPtr module=LoadLibraryEx(library,IntPtr.Zero,AlteredSearchPath);
            if(module==IntPtr.Zero)throw new IOException(L.T("Could not load the upscaling tool. Check that the LoveMyShelf folder has its upscale folder.")+" ("+Marshal.GetLastWin32Error()+")");
            IntPtr getBase=GetProcAddress(module,"OrtGetApiBase");
            if(getBase==IntPtr.Zero)throw new IOException(L.T("Could not load the upscaling tool. Check that the LoveMyShelf folder has its upscale folder."));
            IntPtr apiBase=((GetApiBaseFn)Marshal.GetDelegateForFunctionPointer(getBase,typeof(GetApiBaseFn)))();
            api=((GetApiFn)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(apiBase,0),typeof(GetApiFn)))(ApiVersion);
            if(api==IntPtr.Zero)throw new IOException(L.T("Could not load the upscaling tool. Check that the LoveMyShelf folder has its upscale folder."));
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
                if(gpu) {
                    // Basic graph optimization only: with all of them an Intel Iris Xe gave wrong pictures, with basic the
                    // same as the processor. DirectML also needs memory patterns off and one operation at a time.
                    Check(Fn<SetThreadsFn>(23)(options,1));
                    Check(Fn<OptionsFn>(17)(options));
                    Check(Fn<SetThreadsFn>(13)(options,0));
                    // ONNX Runtime loads DirectML.dll only now, by name, and Windows has an older one of its own:
                    // loading the one beside onnxruntime.dll first makes sure that one is used.
                    if(LoadLibraryEx(Path.Combine(Path.GetDirectoryName(library),"DirectML.dll"),IntPtr.Zero,AlteredSearchPath)==IntPtr.Zero)
                        throw new IOException("DirectML");
                    IntPtr append=GetProcAddress(module,"OrtSessionOptionsAppendExecutionProvider_DML");
                    if(append==IntPtr.Zero)throw new IOException("DirectML");
                    Check(((AppendDmlFn)Marshal.GetDelegateForFunctionPointer(append,typeof(AppendDmlFn)))(options,GraphicsCards.Fastest()));
                }
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
        // Any input shape, every output of the model as flat floats in the order the names were given.
        internal float[][] RunAll(float[] input,long[] inputShape) {
            var handle=GCHandle.Alloc(input,GCHandleType.Pinned);
            IntPtr tensor=IntPtr.Zero;
            var outputs=new IntPtr[outputNames.Length];
            try {
                Check(createTensor(memory,handle.AddrOfPinnedObject(),(UIntPtr)(input.Length*4),inputShape,(UIntPtr)inputShape.Length,1,out tensor));
                Check(run(session,IntPtr.Zero,new[]{inputName},new[]{tensor},(UIntPtr)1,outputNames,(UIntPtr)outputNames.Length,outputs));
                var results=new float[outputs.Length][];
                for(int i=0;i<outputs.Length;i++) {
                    IntPtr info=IntPtr.Zero;
                    try {
                        Check(shape(outputs[i],out info));
                        UIntPtr count;Check(dimCount(info,out count));
                        var size=new long[(int)count];Check(dims(info,size,count));
                        long total=1;foreach(long d in size)total*=d;
                        if(total<=0 || total>Int32.MaxValue/4)throw new InvalidDataException("output size");
                        IntPtr data;Check(getData(outputs[i],out data));
                        results[i]=new float[(int)total];
                        Marshal.Copy(data,results[i],0,(int)total);
                    } finally {if(info!=IntPtr.Zero)releaseShape(info);}
                }
                return results;
            } finally {
                foreach(IntPtr o in outputs)if(o!=IntPtr.Zero)releaseValue(o);
                if(tensor!=IntPtr.Zero)releaseValue(tensor);
                handle.Free();
            }
        }
        public void Dispose() {
            if(memory!=IntPtr.Zero){releaseMemoryInfo(memory);memory=IntPtr.Zero;}
            if(session!=IntPtr.Zero){releaseSession(session);session=IntPtr.Zero;}
            if(options!=IntPtr.Zero){releaseOptions(options);options=IntPtr.Zero;}
            if(env!=IntPtr.Zero){releaseEnv(env);env=IntPtr.Zero;}
            if(!namesFreed) {
                namesFreed=true;
                Marshal.FreeHGlobal(inputName);
                foreach(IntPtr name in outputNames)Marshal.FreeHGlobal(name);
            }
        }
        bool namesFreed;
    }

    // Picks the graphics card for DirectML by its number in DXGI's list (the order ONNX Runtime counts in). On a laptop
    // with both built-in graphics and a separate card, number 0 is usually the built-in one that drives the screen, so
    // the card with the most memory of its own is taken; software renderers are skipped. 0 when anything goes wrong.
    static class GraphicsCards {
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
        struct AdapterDesc {
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string Description;
            public uint VendorId,DeviceId,SubSysId,Revision;
            public UIntPtr DedicatedVideoMemory,DedicatedSystemMemory,SharedSystemMemory;
            public uint LuidLow;public int LuidHigh;
            public uint Flags;
        }
        [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(ref Guid id,out IntPtr factory);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int EnumAdapters1Fn(IntPtr factory,uint index,out IntPtr adapter);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetDesc1Fn(IntPtr adapter,out AdapterDesc desc);
        const int NotFound=unchecked((int)0x887A0002);
        const uint Software=2;
        internal static int Fastest() {
            IntPtr factory=IntPtr.Zero;
            try {
                var id=new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
                if(CreateDXGIFactory1(ref id,out factory)<0 || factory==IntPtr.Zero)return 0;
                var enumerate=(EnumAdapters1Fn)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(factory),12*IntPtr.Size),typeof(EnumAdapters1Fn));
                int best=0;ulong most=0;
                for(uint i=0;i<16;i++) {
                    IntPtr adapter;
                    int hr=enumerate(factory,i,out adapter);
                    if(hr==NotFound || hr<0 || adapter==IntPtr.Zero)break;
                    try {
                        var describe=(GetDesc1Fn)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(adapter),10*IntPtr.Size),typeof(GetDesc1Fn));
                        AdapterDesc desc;
                        if(describe(adapter,out desc)<0 || (desc.Flags&Software)!=0)continue;
                        ulong memory=desc.DedicatedVideoMemory.ToUInt64();
                        if(memory>most){most=memory;best=(int)i;}
                    } finally {Marshal.Release(adapter);}
                }
                return best;
            } catch(Exception) {return 0;}
            finally {if(factory!=IntPtr.Zero)Marshal.Release(factory);}
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
        // The cover being upscaled now (the folder window's bottom bar shows it with the other background work).
        internal string RunningFolder {get {var job=running;return job==null?null:job.Photo.Folder;}}
        internal string RunningName {get {var job=running;return job==null?null:job.Photo.Name;}}
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
                bool gpu=UpscaleEngine.UseGpu && UpscaleEngine.GpuAvailable;
                string error=await RunChild(job.Photo.CoverFile,temp,gpu);
                if(stopped)return;
                // The graphics card can take the whole upscaling program down (a driver crash), which nothing inside it
                // can catch, so a failed run on the graphics card is done again on the processor.
                if(error!=null && gpu) {
                    done=0;total=0;Raise(job.Photo.Folder);
                    error=await RunChild(job.Photo.CoverFile,temp,false);
                    if(stopped)return;
                }
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
        Task<string> RunChild(string input,string output,bool gpu) {
            var done=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var start=new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,
                UpscaleEngine.ChildSwitch+" \""+input+"\" \""+output+"\""+(gpu?" "+UpscaleEngine.GpuSwitch:"")) {
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

    // A small notice in the lower-right corner of the LoveMyShelf window. Over a large view (a video playing, often in
    // full screen) it goes to the top right instead, so it never covers the play bar. A notice that can open something
    // has a button for it; a click anywhere else on the card does nothing, so a click meant for the window under it
    // can't open a cover by mistake.
    internal sealed class UpscaleToast : Window {
        readonly DispatcherTimer timer=new DispatcherTimer {Interval=TimeSpan.FromSeconds(15)};
        internal bool IsWorking {get {return kind==NoticeKind.Busy && !closed;}}
        readonly NoticeKind kind;
        // True when the notice closed because it was clicked (not by its timer or the ✕).
        internal bool WasClicked {get;private set;}
        internal UpscaleToast(Window anchor,string title,string message,Action clicked,NoticeKind kind=NoticeKind.Done,bool keepOpen=false,double seconds=15,string button=null) {
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
                var act=UI.Btn(String.IsNullOrEmpty(button)?L.P("Open","열기"):button,()=>{if(closed)return;WasClicked=true;Close();clicked();},true);
                act.HorizontalAlignment=HorizontalAlignment.Left;act.Margin=new Thickness(0,10,0,0);act.MinHeight=30;act.Padding=new Thickness(14,2,14,2);
                var words=detail.Parent as Panel;
                if(words!=null)words.Children.Add(act);
            }
            // Every notice goes by itself (2026-11-27 user: "걍 잠깐 뜨고 바로 사라져", no notice that waits for ✕): 2 s,
            // or a little longer when it has a button to press. Pointing at it holds it. A "working on it" notice only
            // hides: it stays open out of sight until the work ends, so closing LoveMyShelf still waits for that work.
            timer.Interval=TimeSpan.FromSeconds(clicked==null?2:Math.Max(2,Math.Min(seconds,6)));
            timer.Tick+=delegate {timer.Stop();if(kind==NoticeKind.Busy)Hide();else Close();};
            MouseEnter+=delegate {timer.Stop();};
            MouseLeave+=delegate {timer.Start();};
            Closed+=delegate {closed=true;timer.Stop();};
            Loaded+=delegate {Place(anchor);timer.Start();};
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
        // Where a window is on the screen, in the same units as Left and Top (also when maximized or full screen).
        static Rect OnScreen(Window window) {
            var source=PresentationSource.FromVisual(window);
            if(source==null || source.CompositionTarget==null)return Rect.Empty;
            var toDips=source.CompositionTarget.TransformFromDevice;
            return new Rect(toDips.Transform(window.PointToScreen(new Point(0,0))),toDips.Transform(window.PointToScreen(new Point(window.ActualWidth,window.ActualHeight))));
        }
        void Place(Window anchor) {
            Rect area=SystemParameters.WorkArea;
            // A large view under the corner where the notice would go: the notice goes to that view's top right,
            // under its title bar, away from the play bar and the pause button.
            try {
                Rect corner=Rect.Empty;
                if(anchor!=null && anchor.IsVisible && anchor.WindowState!=WindowState.Minimized) {
                    var a=OnScreen(anchor);
                    if(!a.IsEmpty)corner=new Rect(a.Right-ActualWidth,a.Bottom-ActualHeight-26,ActualWidth,ActualHeight);
                }
                if(corner.IsEmpty)corner=new Rect(area.Right-ActualWidth,area.Bottom-ActualHeight,ActualWidth,ActualHeight);
                var views=Application.Current.Windows.OfType<Viewer>().Where(v=>v.IsVisible && v.WindowState!=WindowState.Minimized).ToList();
                var view=anchor as Viewer;
                if(view==null || !view.IsVisible || view.WindowState==WindowState.Minimized)
                    view=views.FirstOrDefault(v=>{var r=OnScreen(v);return !r.IsEmpty && r.IntersectsWith(corner);});
                if(view!=null) {
                    var r=OnScreen(view);
                    if(!r.IsEmpty){Left=r.Right-ActualWidth+2;Top=r.Top+40;return;}
                }
            } catch(Exception) {}
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

    // Shows the current cover and its upscaled version next to each other, as big as the screen allows: the two
    // pictures fill the window from top to bottom and touch in the middle, and the names and buttons stand beside
    // them instead of above and below. Wide covers are stacked instead, when that shows them bigger.
    // Wheel zoom and drag move both together. It follows its owner into full screen, where nothing at all is
    // above or below the pictures (the title bar only shows while the mouse is at the top).
    internal sealed class UpscaleChoiceWindow : Window {
        internal bool? ChoseUpscaled {get;private set;}
        readonly ScaleTransform zoom=new ScaleTransform();
        readonly TranslateTransform shift=new TranslateTransform();
        Point? dragFrom;
        const double PanelWidth=210,Gap=4,TitleHeight=30;
        readonly Grid stage=new Grid();
        readonly Border[] frames=new Border[2];
        readonly StackPanel[] panels=new StackPanel[2];
        readonly WindowTitleBar titleBar;
        double aspect=9/16.0; // width over height; the covers' own once the first one is read
        bool full,stacked,fitted;
        Rect work;

        internal UpscaleChoiceWindow(Window owner,Photo photo) {
            UI.WindowSetup(this,L.T("Choose a cover"));
            bool owned=owner!=null && owner.IsVisible;
            if(owned)Owner=owner;
            ShowInTaskbar=!owned;
            MinWidth=520;MinHeight=360;
            // As tall as the work area of the monitor the owner is on.
            work=SystemParameters.WorkArea;
            if(owned) {
                try {
                    var screen=System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(owner).Handle).WorkingArea;
                    var source=PresentationSource.FromVisual(owner);
                    Matrix toUnits=source!=null && source.CompositionTarget!=null?source.CompositionTarget.TransformFromDevice:Matrix.Identity;
                    var corner=toUnits.Transform(new Point(screen.Left,screen.Top));var size=toUnits.Transform(new Vector(screen.Width,screen.Height));
                    if(size.X>200 && size.Y>200)work=new Rect(corner.X,corner.Y,size.X,size.Y);
                } catch(Exception) {}
            }
            WindowStartupLocation=WindowStartupLocation.Manual;
            Place();
            var transform=new TransformGroup();transform.Children.Add(zoom);transform.Children.Add(shift);
            var windowContent=new Grid();
            windowContent.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});
            windowContent.RowDefinitions.Add(new RowDefinition());
            titleBar=FullScreen.TitleBar(this,owned && FullScreen.IsFull(owner),escapeExits:false,changed:SetFull);
            titleBar.VerticalAlignment=VerticalAlignment.Top;Panel.SetZIndex(titleBar,1);
            Grid.SetRow(stage,1);windowContent.Children.Add(stage);windowContent.Children.Add(titleBar);
            Content=windowContent;
            stage.SetResourceReference(Panel.BackgroundProperty,"Paper");
            // The pictures sit in the middle column(s) and rows; the stars around them share what is left.
            stage.ColumnDefinitions.Add(new ColumnDefinition());
            stage.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
            stage.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(Gap)});
            stage.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
            stage.ColumnDefinitions.Add(new ColumnDefinition());
            stage.RowDefinitions.Add(new RowDefinition());
            stage.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});
            stage.RowDefinitions.Add(new RowDefinition {Height=new GridLength(Gap)});
            stage.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});
            stage.RowDefinitions.Add(new RowDefinition());
            bool upscaledNow=photo.UpscaleChoice=="upscaled";
            AddSide(0,L.T("Original cover"),photo.CoverFile,transform,!upscaledNow,false);
            AddSide(1,L.T("Upscaled"),photo.UpscaleFile,transform,upscaledNow,true);
            // How to move the pictures, and the way out without choosing, under the second name.
            var hint=UI.Text(NoticeLook.KeepWords(L.T("Zoom with the wheel and drag to move. The two pictures move together. Double-click to go back to how it was.")+" "+
                L.T("The file you don't choose also stays in the cover folder.")),12,true);
            hint.TextWrapping=TextWrapping.Wrap;hint.Margin=new Thickness(0,26,0,0);hint.LineHeight=18;
            panels[1].Children.Add(hint);
            var later=UI.Btn(L.T("Decide later"),Close);later.MinHeight=30;later.HorizontalAlignment=HorizontalAlignment.Left;later.Margin=new Thickness(0,10,0,0);
            panels[1].Children.Add(later);
            stage.SizeChanged+=delegate {Arrange();};
            PreviewKeyDown+=(s,e)=>{if(e.Key==Key.Escape){Close();e.Handled=true;}};
            // In full screen the title bar stays out of the way until the mouse reaches the top edge.
            PreviewMouseMove+=(s,e)=>{
                if(!full)return;
                bool near=e.GetPosition(this).Y<(titleBar.Visibility==Visibility.Visible?TitleHeight+8:6);
                titleBar.Visibility=near || titleBar.IsMouseCaptureWithin?Visibility.Visible:Visibility.Collapsed;
            };
            SetFull(false);
        }
        // Windowed: the title bar has its own row. Full screen: the pictures take the whole window and the bar floats over them.
        void SetFull(bool now) {
            full=now;
            Grid.SetRow(stage,full?0:1);Grid.SetRowSpan(stage,full?2:1);
            titleBar.Visibility=full?Visibility.Collapsed:Visibility.Visible;
        }
        // The window's place when it is not full screen: the whole height of the work area, and wide enough for
        // the two pictures at that height with a panel on each side.
        void Place() {
            double room=Math.Max(200,work.Height-TitleHeight);
            double side=2*room*aspect+Gap+2*PanelWidth,stack=(room-Gap)/2*aspect+2*PanelWidth;
            double wanted=Math.Min(room,(work.Width-2*PanelWidth-Gap)/2/aspect)>=Math.Min((room-Gap)/2,(work.Width-2*PanelWidth)/aspect)?side:stack;
            Height=work.Height;Width=Math.Max(Math.Min(MinWidth,work.Width),Math.Min(work.Width,Math.Ceiling(wanted)));
            Left=work.Left+(work.Width-Width)/2;Top=work.Top;
        }
        void AddSide(int index,string title,string path,Transform transform,bool inUse,bool upscaled) {
            var panel=new StackPanel {Width=PanelWidth-28,VerticalAlignment=VerticalAlignment.Center};
            var name=UI.Text(title,16);name.FontWeight=FontWeights.SemiBold;name.TextWrapping=TextWrapping.Wrap;
            var size=UI.Text("",12,true);size.Margin=new Thickness(0,3,0,0);size.TextWrapping=TextWrapping.Wrap;
            var choose=UI.Btn(inUse?L.T("In use · keep using it"):L.T("Use this cover"),()=>{ChoseUpscaled=upscaled;DialogResult=true;},true);
            choose.Margin=new Thickness(0,12,0,0);choose.MinHeight=34;choose.HorizontalAlignment=HorizontalAlignment.Left;
            panel.Children.Add(name);panel.Children.Add(size);panel.Children.Add(choose);
            panels[index]=panel;stage.Children.Add(panel);
            var image=new Image {Stretch=Stretch.Uniform,RenderTransform=transform};
            RenderOptions.SetBitmapScalingMode(image,BitmapScalingMode.HighQuality);
            var frame=new Border {Background=Brushes.Black,ClipToBounds=true,Child=image,
                BorderThickness=new Thickness(inUse?2:0),Cursor=Cursors.SizeAll,VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center};
            frame.SetResourceReference(Border.BorderBrushProperty,"Accent");
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
            frames[index]=frame;stage.Children.Add(frame);
            LoadInto(image,size,path,index==0);
        }
        // Side by side or one above the other, whichever shows the pictures bigger; then each picture gets exactly
        // its own shape, so nothing is empty above or below it when the height is what limits it.
        void Arrange() {
            double width=stage.ActualWidth,height=stage.ActualHeight;
            if(width<=0 || height<=0 || frames[1]==null)return;
            double beside=Math.Min(height,(width-2*PanelWidth-Gap)/2/aspect);
            double above=Math.Min((height-Gap)/2,(width-2*PanelWidth)/aspect);
            stacked=above>beside+1;
            double tall=Math.Max(40,Math.Floor(stacked?above:beside));
            foreach(var frame in frames){frame.Height=tall;frame.Width=Math.Max(40,Math.Floor(tall*aspect));}
            if(stacked) {
                // Pictures in the middle, one over the other; each name stands to the right of its own picture.
                Put(frames[0],1,1,1,3);Put(frames[1],3,1,1,3);
                Put(panels[0],1,4,1,1);Put(panels[1],3,4,1,1);
                foreach(var panel in panels){panel.HorizontalAlignment=HorizontalAlignment.Left;panel.Margin=new Thickness(14,0,14,0);}
                Align(panels[0],TextAlignment.Left);
            } else {
                // Original on the left with its name to its left; upscaled on the right with its name to its right.
                Put(frames[0],0,1,5,1);Put(frames[1],0,3,5,1);
                Put(panels[0],0,0,5,1);Put(panels[1],0,4,5,1);
                panels[0].HorizontalAlignment=HorizontalAlignment.Right;panels[0].Margin=new Thickness(14,0,14,0);
                panels[1].HorizontalAlignment=HorizontalAlignment.Left;panels[1].Margin=new Thickness(14,0,14,0);
                Align(panels[0],TextAlignment.Right);
            }
        }
        static void Put(FrameworkElement element,int row,int column,int rows,int columns) {
            Grid.SetRow(element,row);Grid.SetColumn(element,column);Grid.SetRowSpan(element,rows);Grid.SetColumnSpan(element,columns);
        }
        // The left panel's words and button lean toward its picture.
        static void Align(StackPanel panel,TextAlignment side) {
            foreach(var child in panel.Children) {
                var text=child as TextBlock;if(text!=null)text.TextAlignment=side;
                var button=child as Button;if(button!=null)button.HorizontalAlignment=side==TextAlignment.Right?HorizontalAlignment.Right:HorizontalAlignment.Left;
            }
        }
        void Zoom(Image image,Point at,double factor) {
            double scale=Math.Max(1,Math.Min(10,zoom.ScaleX*factor));
            if(scale==1){zoom.ScaleX=zoom.ScaleY=1;shift.X=shift.Y=0;return;}
            shift.X+=at.X*(zoom.ScaleX-scale);shift.Y+=at.Y*(zoom.ScaleY-scale);
            zoom.ScaleX=zoom.ScaleY=scale;
        }
        async void LoadInto(Image image,TextBlock size,string path,bool shapes) {
            try {
                var bitmap=await Task.Run(()=>{
                    using(var file=File.Open(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete)) {
                        var frame=BitmapFrame.Create(file,BitmapCreateOptions.None,BitmapCacheOption.OnLoad);
                        frame.Freeze();return (BitmapSource)frame;
                    }
                });
                image.Source=bitmap;
                size.Text=L.F("{0} × {1} px",bitmap.PixelWidth,bitmap.PixelHeight);
                // The original cover's shape decides the layout (the upscaled one has the same shape).
                if(shapes && bitmap.PixelWidth>0 && bitmap.PixelHeight>0) {
                    aspect=bitmap.PixelWidth/(double)bitmap.PixelHeight;
                    if(!fitted && !full && WindowState==WindowState.Normal){fitted=true;Place();}
                    Arrange();
                }
            } catch(Exception ex) {size.Text=ex.Message;}
        }
    }
}
