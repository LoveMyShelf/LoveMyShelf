using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LoveMyShelf {
    // One face found in a picture: where it is (as fractions of the picture's width and height) and the 128 numbers that describe it.
    internal sealed class FoundFace {
        internal float X,Y,W,H,Score;
        internal float[] Vector;
    }
    // Finds faces (YuNet) and turns each into 128 numbers (SFace) through the ONNX Runtime that upscaling already uses.
    // The two models live in a "faces" folder beside the executable. Pictures never leave this computer.
    internal static class FaceEngine {
        internal const string ChildSwitch="--faces";
        const string DetectorFile="face_detection_yunet_2023mar.onnx",RecognizerFile="face_recognition_sface_2021dec.onnx";
        const int Side=640;             // the detector's fixed input
        const float MinScore=0.8f,Overlap=0.3f;
        const int MinFace=24;           // smaller faces (in the picture's own pixels) are left out
        const int Threads=4;            // leaves the rest of the computer free
        static readonly string[] DetectorOutputs={"cls_8","cls_16","cls_32","obj_8","obj_16","obj_32","bbox_8","bbox_16","bbox_32","kps_8","kps_16","kps_32"};
        // Where the eyes, nose and mouth corners sit in the 112x112 face the recognizer expects.
        static readonly float[] Mark={38.2946f,51.6963f,73.5318f,51.5014f,56.0252f,71.7366f,41.5493f,92.3655f,70.7299f,92.2041f};

        internal static string Folder {get {return Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"faces");}}
        static string Runtime {get {return Path.Combine(UpscaleEngine.Folder,"onnxruntime.dll");}}
        internal static bool Available {
            get {return File.Exists(Runtime) && File.Exists(Path.Combine(Folder,DetectorFile)) && File.Exists(Path.Combine(Folder,RecognizerFile));}
        }

        // Child process entry: LoveMyShelf.exe --faces <list> <output>
        // list: one picture path per line (UTF-8). output: for each picture "picture <index> <faces|-1>" and then one line per face
        // "face x y w h score v1 ... v128", closed by "end <index>". Progress goes to standard output as "progress <done> <total>".
        internal static int RunChild(string list,string output) {
            try {
                var paths=File.ReadAllLines(list,Encoding.UTF8);
                var inv=CultureInfo.InvariantCulture;
                using(var detector=new OnnxSession(Runtime,Path.Combine(Folder,DetectorFile),Threads,"input",DetectorOutputs))
                using(var recognizer=new OnnxSession(Runtime,Path.Combine(Folder,RecognizerFile),Threads,"data",new[]{"fc1"}))
                using(var writer=new StreamWriter(output,false,new UTF8Encoding(false))) {
                    var text=new StringBuilder();
                    for(int i=0;i<paths.Length;i++) {
                        List<FoundFace> faces=null;
                        try {faces=Find(detector,recognizer,paths[i]);} catch(Exception) {}
                        text.Length=0;
                        text.Append("picture ").Append(i).Append(' ').Append(faces==null?-1:faces.Count).Append('\n');
                        if(faces!=null)foreach(var f in faces) {
                            text.Append("face ").Append(f.X.ToString("R",inv)).Append(' ').Append(f.Y.ToString("R",inv)).Append(' ')
                                .Append(f.W.ToString("R",inv)).Append(' ').Append(f.H.ToString("R",inv)).Append(' ').Append(f.Score.ToString("R",inv));
                            foreach(float v in f.Vector)text.Append(' ').Append(v.ToString("R",inv));
                            text.Append('\n');
                        }
                        // The closing line tells the reader this picture's lines are whole (the program may be stopped mid-line).
                        text.Append("end ").Append(i).Append('\n');
                        writer.Write(text.ToString());
                        if(i%8==7 || i==paths.Length-1){writer.Flush();Console.Out.WriteLine("progress "+(i+1)+" "+paths.Length);Console.Out.Flush();}
                    }
                }
                return 0;
            } catch(Exception ex) {
                try{Console.Error.WriteLine(ex.Message);Console.Error.Flush();}catch(IOException){}
                return 1;
            }
        }
        static List<FoundFace> Find(OnnxSession detector,OnnxSession recognizer,string path) {
            int w,h;
            byte[] bgr=LoadBgr(path,out w,out h);
            var found=new List<FoundFace>();
            foreach(float[] row in Detect(detector,bgr,w,h)) {
                if(Math.Min(row[2],row[3])<MinFace)continue;
                float[] vector=Describe(recognizer,bgr,w,h,row);
                if(vector==null)continue;
                found.Add(new FoundFace {X=row[0]/w,Y=row[1]/h,W=row[2]/w,H=row[3]/h,Score=row[14],Vector=vector}); // the box as fractions of the picture
            }
            return found;
        }
        static byte[] LoadBgr(string path,out int w,out int h) {
            using(var file=File.Open(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete)) {
                BitmapSource frame=PictureTypes.UsesFfmpeg(path)?Pictures.Load(path):BitmapFrame.Create(file,BitmapCreateOptions.IgnoreColorProfile,BitmapCacheOption.OnLoad);
                var source=new FormatConvertedBitmap(frame,PixelFormats.Bgr24,null,0);
                w=source.PixelWidth;h=source.PixelHeight;
                if(w<1 || h<1 || (long)w*h>120000000)throw new InvalidDataException("picture size");
                var pixels=new byte[w*3*h];
                source.CopyPixels(pixels,w*3,0);
                return pixels;
            }
        }
        // Rows of 15 numbers in the picture's pixels: x, y, width, height, five points (x, y each), score.
        static List<float[]> Detect(OnnxSession detector,byte[] bgr,int w,int h) {
            double scale=Math.Min(1.0,(double)Side/Math.Max(w,h));
            int sw=Math.Max(1,(int)Math.Round(w*scale)),sh=Math.Max(1,(int)Math.Round(h*scale));
            var input=new float[3*Side*Side];
            Shrink(bgr,w,h,input,sw,sh);
            float[][] o=detector.RunAll(input,new long[]{1,3,Side,Side});
            float kx=(float)w/sw,ky=(float)h/sh;
            var rows=new List<float[]>();
            int[] strides={8,16,32};
            for(int s=0;s<3;s++) {
                int stride=strides[s],cols=Side/stride,rowsCount=Side/stride;
                float[] cls=o[s],obj=o[3+s],box=o[6+s],kps=o[9+s];
                for(int r=0;r<rowsCount;r++) for(int c=0;c<cols;c++) {
                    int i=r*cols+c;
                    float score=(float)Math.Sqrt(Clamp(cls[i])*Clamp(obj[i]));
                    if(score<MinScore)continue;
                    float cx=(c+box[i*4])*stride,cy=(r+box[i*4+1])*stride;
                    float bw=(float)Math.Exp(box[i*4+2])*stride,bh=(float)Math.Exp(box[i*4+3])*stride;
                    var row=new float[15];
                    row[0]=(cx-bw/2)*kx;row[1]=(cy-bh/2)*ky;row[2]=bw*kx;row[3]=bh*ky;
                    for(int n=0;n<5;n++) {
                        row[4+2*n]=(kps[i*10+2*n]+c)*stride*kx;
                        row[5+2*n]=(kps[i*10+2*n+1]+r)*stride*ky;
                    }
                    row[14]=score;
                    rows.Add(row);
                }
            }
            // Keep the best box of each face: a box that overlaps a better one is the same face again.
            rows.Sort((a,b)=>b[14].CompareTo(a[14]));
            var kept=new List<float[]>();
            foreach(float[] row in rows) {
                bool again=false;
                foreach(float[] k in kept) if(Shared(row,k)>Overlap){again=true;break;}
                if(!again)kept.Add(row);
            }
            return kept;
        }
        static float Clamp(float v) {return v<0?0:v>1?1:v;}
        static float Shared(float[] a,float[] b) {
            float x1=Math.Max(a[0],b[0]),y1=Math.Max(a[1],b[1]),x2=Math.Min(a[0]+a[2],b[0]+b[2]),y2=Math.Min(a[1]+a[3],b[1]+b[3]);
            float inter=Math.Max(0,x2-x1)*Math.Max(0,y2-y1),union=a[2]*a[3]+b[2]*b[3]-inter;
            return union<=0?0:inter/union;
        }
        // Shrinks the picture into the top-left corner of the detector's square (the rest stays black).
        // Each small pixel is the average of the picture's pixels it covers. Planes: blue, green, red, values 0 to 255.
        static void Shrink(byte[] bgr,int w,int h,float[] input,int sw,int sh) {
            int plane=Side*Side;
            for(int y=0;y<sh;y++) {
                int y0=(int)((long)y*h/sh),y1=Math.Max(y0+1,(int)((long)(y+1)*h/sh));
                for(int x=0;x<sw;x++) {
                    int x0=(int)((long)x*w/sw),x1=Math.Max(x0+1,(int)((long)(x+1)*w/sw));
                    long b=0,g=0,r=0;
                    for(int yy=y0;yy<y1;yy++) {
                        int p=(yy*w+x0)*3;
                        for(int xx=x0;xx<x1;xx++,p+=3){b+=bgr[p];g+=bgr[p+1];r+=bgr[p+2];}
                    }
                    int count=(y1-y0)*(x1-x0),d=y*Side+x;
                    input[d]=(float)b/count;input[plane+d]=(float)g/count;input[2*plane+d]=(float)r/count;
                }
            }
        }
        // Turns the face upright at 112x112 from its five points, then asks the recognizer for its 128 numbers (length 1).
        static float[] Describe(OnnxSession recognizer,byte[] bgr,int w,int h,float[] row) {
            // The closest turn, size and shift that moves the face's five points onto the expected places.
            double sx=0,sy=0,dx=0,dy=0;
            for(int n=0;n<5;n++){sx+=row[4+2*n];sy+=row[5+2*n];dx+=Mark[2*n];dy+=Mark[2*n+1];}
            sx/=5;sy/=5;dx/=5;dy/=5;
            double a=0,b=0,spread=0;
            for(int n=0;n<5;n++) {
                double px=row[4+2*n]-sx,py=row[5+2*n]-sy,qx=Mark[2*n]-dx,qy=Mark[2*n+1]-dy;
                a+=px*qx+py*qy;b+=px*qy-py*qx;spread+=px*px+py*py;
            }
            if(spread<=0)return null;
            a/=spread;b/=spread;
            double det=a*a+b*b;
            if(det<=1e-12)return null;
            // Picture point for each face pixel: the reverse of x' = a x - b y + tx, y' = b x + a y + ty.
            double tx=dx-(a*sx-b*sy),ty=dy-(b*sx+a*sy);
            const int size=112;
            int plane=size*size;
            var input=new float[3*plane];
            for(int y=0;y<size;y++) for(int x=0;x<size;x++) {
                double ux=x-tx,uy=y-ty;
                double px=(a*ux+b*uy)/det,py=(-b*ux+a*uy)/det;
                int x0=(int)Math.Floor(px),y0=(int)Math.Floor(py);
                double fx=px-x0,fy=py-y0;
                int d=y*size+x;
                for(int c=0;c<3;c++) {
                    double v=Pixel(bgr,w,h,x0,y0,c)*(1-fx)*(1-fy)+Pixel(bgr,w,h,x0+1,y0,c)*fx*(1-fy)
                        +Pixel(bgr,w,h,x0,y0+1,c)*(1-fx)*fy+Pixel(bgr,w,h,x0+1,y0+1,c)*fx*fy;
                    input[(2-c)*plane+d]=(float)v;   // the recognizer reads red, green, blue
                }
            }
            float[] vector=recognizer.RunAll(input,new long[]{1,3,size,size})[0];
            if(vector.Length!=128)return null;
            double length=0;
            foreach(float v in vector)length+=v*v;
            length=Math.Sqrt(length);
            if(length<=0 || Double.IsNaN(length) || Double.IsInfinity(length))return null;
            for(int i=0;i<vector.Length;i++)vector[i]=(float)(vector[i]/length);
            return vector;
        }
        static int Pixel(byte[] bgr,int w,int h,int x,int y,int channel) {
            if(x<0 || y<0 || x>=w || y>=h)return 0;
            return bgr[(y*w+x)*3+channel];
        }
    }
}
