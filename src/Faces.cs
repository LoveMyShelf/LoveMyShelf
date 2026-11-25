using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace LoveMyShelf {
    // One face on one cover. The box is kept as fractions of the cover picture, so it fits the picture at any size.
    internal sealed class Face {
        internal Photo Cover;
        internal double X,Y,W,H;
        internal float Score;
        internal float[] Vector;
        // The person the user said this face is ("" while nobody said so).
        internal string Name="";
        // People the user said this face is not. "*" keeps the face on its own: it joins no group.
        internal List<string> Not=new List<string>();
        internal bool Alone {get {return Not.Contains("*");}}
    }
    // What was found on each cover is kept beside the cover's record as "faces.txt", so a cover is read only once.
    // It is worked out again when the cover is remade, and it goes away with the cover.
    // The numbers describe a face; they stay in Love My Shelf's records on this computer.
    internal static class FaceFiles {
        const string FileName="faces.txt",Version="faces1";
        static readonly CultureInfo Inv=CultureInfo.InvariantCulture;
        static string Revision(Photo cover) {return Path.GetFileName(cover.Thumb)??"";}
        // Null when this cover hasn't been read yet (or was remade since).
        internal static List<Face> Read(Photo cover) {
            string path=Path.Combine(cover.Folder,FileName);
            try {
                if(!File.Exists(path))return null;
                var lines=File.ReadAllLines(path,Encoding.UTF8);
                if(lines.Length==0)return null;
                var head=lines[0].Split('\t');
                if(head.Length<2 || head[0]!=Version || head[1]!=Revision(cover))return null;
                var faces=new List<Face>();
                for(int i=1;i<lines.Length;i++) {
                    var p=lines[i].Split('\t');
                    if(p.Length<8)continue;
                    var bytes=Convert.FromBase64String(p[7]);
                    if(bytes.Length!=128*4)continue;
                    var vector=new float[128];
                    Buffer.BlockCopy(bytes,0,vector,0,bytes.Length);
                    faces.Add(new Face {Cover=cover,X=Double.Parse(p[0],Inv),Y=Double.Parse(p[1],Inv),W=Double.Parse(p[2],Inv),H=Double.Parse(p[3],Inv),
                        Score=Single.Parse(p[4],Inv),Name=p[5],Not=p[6].Split(new[]{'|'},StringSplitOptions.RemoveEmptyEntries).ToList(),Vector=vector});
                }
                return faces;
            } catch(IOException) {return null;} catch(UnauthorizedAccessException) {return null;} catch(FormatException) {return null;}
        }
        internal static void Write(Photo cover,List<Face> faces) {
            if(!Directory.Exists(cover.Folder))return; // the cover was removed meanwhile
            var text=new StringBuilder();
            text.Append(Version).Append('\t').Append(Revision(cover)).Append('\n');
            foreach(var f in faces) {
                var bytes=new byte[128*4];
                Buffer.BlockCopy(f.Vector,0,bytes,0,bytes.Length);
                text.Append(f.X.ToString("R",Inv)).Append('\t').Append(f.Y.ToString("R",Inv)).Append('\t').Append(f.W.ToString("R",Inv)).Append('\t')
                    .Append(f.H.ToString("R",Inv)).Append('\t').Append(f.Score.ToString("R",Inv)).Append('\t').Append(Plain(f.Name)).Append('\t')
                    .Append(String.Join("|",f.Not.Select(Plain))).Append('\t').Append(Convert.ToBase64String(bytes)).Append('\n');
            }
            string path=Path.Combine(cover.Folder,FileName),part=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try {
                File.WriteAllText(part,text.ToString(),new UTF8Encoding(false));
                if(File.Exists(path))File.Replace(part,path,null);else File.Move(part,path);
            } finally {try {File.Delete(part);} catch(Exception) {}}
        }
        // Renames a person (newName null: forgets the name) in one record folder's file, whatever cover it belongs to.
        internal static void RenameIn(string record,string name,string newName) {
            string path=Path.Combine(record,FileName);
            if(!File.Exists(path))return;
            var lines=File.ReadAllLines(path,Encoding.UTF8);
            bool changed=false;
            for(int i=1;i<lines.Length;i++) {
                var p=lines[i].Split('\t');
                if(p.Length<8)continue;
                if(String.Equals(p[5],name,StringComparison.OrdinalIgnoreCase)){p[5]=Plain(newName);changed=true;}
                var not=p[6].Split(new[]{'|'},StringSplitOptions.RemoveEmptyEntries).ToList();
                int at=not.FindIndex(n=>String.Equals(n,name,StringComparison.OrdinalIgnoreCase));
                if(at>=0) {
                    not.RemoveAt(at);
                    if(newName!=null && !not.Contains(Plain(newName),StringComparer.OrdinalIgnoreCase))not.Insert(at,Plain(newName));
                    p[6]=String.Join("|",not);changed=true;
                }
                lines[i]=String.Join("\t",p);
            }
            if(!changed)return;
            string part=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try {
                File.WriteAllText(part,String.Join("\n",lines)+"\n",new UTF8Encoding(false));
                File.Replace(part,path,null);
            } finally {try {File.Delete(part);} catch(Exception) {}}
        }
        static string Plain(string name) {return (name??"").Replace('\t',' ').Replace('|',' ').Replace('\n',' ').Replace('\r',' ');}
        // The covers people can see (hidden ones are left alone), each with the picture that is looked at.
        internal static List<Photo> Covers(GalleryStore store) {
            int skipped;
            return store.SavedCovers(out skipped).Where(p=>p.HasCrop && !p.ExcludedFromCollection && p.Linked && File.Exists(p.GridImage)).ToList();
        }
        // Removes everything that was worked out about faces. Tags that were given stay.
        internal static int ClearAll(GalleryStore store) {
            int removed=0;
            foreach(var album in store.Albums()) {
                try {
                    foreach(string record in Directory.GetDirectories(album.Folder,"p-*")) {
                        string path=Path.Combine(record,FileName);
                        try {if(File.Exists(path)){File.Delete(path);removed++;}} catch(IOException) {} catch(UnauthorizedAccessException) {}
                    }
                } catch(IOException) {} catch(UnauthorizedAccessException) {}
            }
            return removed;
        }
    }
    // Keeps the names given to faces in step with the tags: a renamed tag renames the person, a deleted tag forgets the person.
    internal static class FaceNames {
        internal static void Rename(GalleryStore store,string name,string newName) {
            foreach(var album in store.Albums()) {
                try {
                    foreach(string record in Directory.GetDirectories(album.Folder,"p-*")) {
                        try {FaceFiles.RenameIn(record,name,newName);} catch(IOException) {} catch(UnauthorizedAccessException) {}
                    }
                } catch(IOException) {} catch(UnauthorizedAccessException) {}
            }
        }
    }
    // Reads covers for faces in a separate copy of the program (below-normal priority; its memory is given back when it ends).
    internal sealed class FaceScan {
        const int Batch=120;      // results are saved after each batch, so stopping loses little
        Process child;
        bool stopped;
        internal void Stop() {
            stopped=true;
            var process=child;child=null;
            if(process!=null)try{process.Kill();}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}
        }
        // Reads the given covers and saves what was found beside each. progress: covers done so far, faces found so far.
        // Returns an error message, or null when it finished or was stopped.
        internal async Task<string> Run(List<Photo> covers,Action<int,int> progress) {
            int done=0,found=0;
            string folder=Path.Combine(Path.GetTempPath(),"LoveMyShelf-faces-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try {
                for(int start=0;start<covers.Count && !stopped;start+=Batch) {
                    var batch=covers.Skip(start).Take(Batch).ToList();
                    string list=Path.Combine(folder,"list.txt"),output=Path.Combine(folder,"found.txt");
                    File.WriteAllLines(list,batch.Select(c=>c.GridImage),new UTF8Encoding(false));
                    try {File.Delete(output);} catch(IOException) {}
                    int before=done;
                    string error=await RunChild(list,output,n=>progress(before+n,found));
                    var saved=await Task.Run(()=>Keep(batch,output));
                    done=before+saved.Item1;found+=saved.Item2;
                    progress(done,found);
                    if(error!=null && !stopped)return error;
                }
                return null;
            } finally {try {Directory.Delete(folder,true);} catch(Exception) {}}
        }
        // Saves each cover's faces. A cover the child didn't get to (it was stopped) stays unread.
        static Tuple<int,int> Keep(List<Photo> batch,string output) {
            int covers=0,faces=0;
            if(!File.Exists(output))return Tuple.Create(0,0);
            var inv=CultureInfo.InvariantCulture;
            Photo cover=null;List<Face> list=null;int expected=0;
            foreach(string line in File.ReadLines(output,Encoding.UTF8)) {
                var p=line.Split(' ');
                try {
                    if(p[0]=="picture" && p.Length>=3) {
                        int index=Int32.Parse(p[1],inv);expected=Int32.Parse(p[2],inv);
                        // A picture that couldn't be read (-1) stays unread, so the next search tries it again.
                        cover=expected>=0 && index>=0 && index<batch.Count?batch[index]:null;
                        list=new List<Face>();
                    } else if(p[0]=="face" && p.Length==6+128 && list!=null) {
                        var vector=new float[128];
                        for(int i=0;i<128;i++)vector[i]=Single.Parse(p[6+i],inv);
                        list.Add(new Face {Cover=cover,X=Double.Parse(p[1],inv),Y=Double.Parse(p[2],inv),W=Double.Parse(p[3],inv),H=Double.Parse(p[4],inv),
                            Score=Single.Parse(p[5],inv),Vector=vector});
                    } else if(p[0]=="end") {
                        // Only a picture whose closing line arrived is kept: a line cut short by stopping can't slip in.
                        if(cover!=null && list!=null && list.Count==expected) {
                            try {FaceFiles.Write(cover,list);covers++;faces+=list.Count;} catch(IOException) {} catch(UnauthorizedAccessException) {}
                        }
                        cover=null;list=null;
                    }
                } catch(FormatException) {cover=null;list=null;} catch(OverflowException) {cover=null;list=null;}
            }
            return Tuple.Create(covers,faces);
        }
        Task<string> RunChild(string list,string output,Action<int> progress) {
            var done=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var start=new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,
                FaceEngine.ChildSwitch+" \""+list+"\" \""+output+"\"") {
                UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,
                StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
            var process=new Process {StartInfo=start,EnableRaisingEvents=true};
            var errors=new StringBuilder();
            var dispatcher=System.Windows.Threading.Dispatcher.CurrentDispatcher;
            process.OutputDataReceived+=(s,e)=>{
                if(e.Data==null || !e.Data.StartsWith("progress "))return;
                var parts=e.Data.Split(' ');
                int n;
                if(parts.Length==3 && Int32.TryParse(parts[1],out n))
                    dispatcher.BeginInvoke(new Action(()=>{if(child==process)progress(n);}));
            };
            process.ErrorDataReceived+=(s,e)=>{if(e.Data!=null)lock(errors)errors.AppendLine(e.Data);};
            process.Exited+=delegate {
                process.WaitForExit();
                int code=process.ExitCode;
                string message;
                lock(errors)message=errors.ToString().Trim();
                dispatcher.BeginInvoke(new Action(()=>{
                    if(child==process)child=null;
                    process.Dispose();
                    done.TrySetResult(code==0?null:(message.Length>0?message:L.T("It stopped in the middle of looking for faces.")));
                }));
            };
            process.Start();
            child=process;
            try{process.PriorityClass=ProcessPriorityClass.BelowNormal;}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}
            process.BeginOutputReadLine();process.BeginErrorReadLine();
            return done.Task;
        }
    }
    // A person shown in the window: faces the user named, and unnamed faces that look like them ("new").
    internal sealed class FaceGroup {
        internal string Name;                       // null for a group nobody named yet
        internal List<Face> Faces=new List<Face>(); // named faces, or the faces of an unnamed group
        internal List<Face> New=new List<Face>();   // unnamed faces that look like this named person
    }
    internal static class FaceGroups {
        const int MaxGroups=9000; // about 320 MB for the table at most
        // 1) Faces the user named stay with their person. 2) An unnamed face that looks like a named person (on average,
        // against all of that person's named faces) is offered to that person, unless the user said it isn't them.
        // 3) The rest are grouped among themselves. Result: named people (most faces first), unnamed groups, then single faces.
        internal static List<FaceGroup> Build(List<Face> faces,double threshold,out List<Face> singles) {
            var people=new Dictionary<string,FaceGroup>(StringComparer.OrdinalIgnoreCase);
            var sums=new Dictionary<string,double[]>(StringComparer.OrdinalIgnoreCase);
            foreach(var face in faces.Where(f=>f.Name.Length>0)) {
                FaceGroup group;
                if(!people.TryGetValue(face.Name,out group)){people[face.Name]=group=new FaceGroup {Name=face.Name};sums[face.Name]=new double[128];}
                group.Faces.Add(face);
                var sum=sums[face.Name];
                for(int i=0;i<128;i++)sum[i]+=face.Vector[i];
            }
            var rest=new List<Face>();
            singles=new List<Face>();
            foreach(var face in faces.Where(f=>f.Name.Length==0)) {
                if(face.Alone){singles.Add(face);continue;}
                string best=null;double bestScore=threshold;
                foreach(var person in people) {
                    if(face.Not.Contains(person.Key,StringComparer.OrdinalIgnoreCase))continue;
                    var sum=sums[person.Key];
                    double score=0;
                    for(int i=0;i<128;i++)score+=sum[i]*face.Vector[i];
                    score/=person.Value.Faces.Count;
                    if(score>=bestScore){bestScore=score;best=person.Key;}
                }
                if(best!=null)people[best].New.Add(face);else rest.Add(face);
            }
            var result=people.Values.OrderByDescending(g=>g.Faces.Count+g.New.Count).ThenBy(g=>g.Name,StringComparer.OrdinalIgnoreCase).ToList();
            foreach(var members in Cluster(rest,threshold)) {
                if(members.Count==1)singles.Add(members[0]);
                else result.Add(new FaceGroup {Faces=members});
            }
            return result;
        }
        // Groups by how alike two groups' faces are on average over every pair (comparing two "average faces" instead
        // makes different people look far more alike than they are). The most alike pair of groups joins first.
        static List<List<Face>> Cluster(List<Face> faces,double threshold) {
            // A first pass in order joins only faces that are almost surely the same, so the order of the pictures matters little.
            double first=Math.Min(0.95,threshold+0.15);
            var members=new List<List<Face>>();
            var sums=new List<double[]>();
            foreach(var face in faces) {
                int best=-1;double bestScore=first;
                for(int g=0;g<members.Count;g++) {
                    double score=0;var sum=sums[g];
                    for(int i=0;i<128;i++)score+=sum[i]*face.Vector[i];
                    score/=members[g].Count;
                    if(score>=bestScore){bestScore=score;best=g;}
                }
                if(best<0) {
                    members.Add(new List<Face>{face});
                    var sum=new double[128];for(int i=0;i<128;i++)sum[i]=face.Vector[i];
                    sums.Add(sum);
                } else {
                    members[best].Add(face);
                    var sum=sums[best];for(int i=0;i<128;i++)sum[i]+=face.Vector[i];
                }
            }
            int count=members.Count;
            if(count<2)return members;
            // The table below takes count x count numbers. Far more groups than this would need gigabytes,
            // so past the limit the groups from the first pass are shown as they are.
            if(count>MaxGroups)return members.OrderByDescending(g=>g.Count).ToList();
            // pair[i*count+j]: the sum of likeness over every pair of faces between groups i and j.
            var pair=new float[(long)count*count];
            for(int i=0;i<count;i++) for(int j=i+1;j<count;j++) {
                double score=0;var a=sums[i];var b=sums[j];
                for(int k=0;k<128;k++)score+=a[k]*b[k];
                pair[(long)i*count+j]=pair[(long)j*count+i]=(float)score;
            }
            var size=members.Select(m=>m.Count).ToArray();
            var alive=new bool[count];for(int i=0;i<count;i++)alive[i]=true;
            var bestOf=new int[count];var bestValue=new double[count];
            Func<int,int,double> average=(i,j)=>pair[(long)i*count+j]/((double)size[i]*size[j]);
            Action<int> refresh=i=>{
                int b=-1;double value=Double.NegativeInfinity;
                for(int j=0;j<count;j++) if(j!=i && alive[j]){double a=average(i,j);if(a>value){value=a;b=j;}}
                bestOf[i]=b;bestValue[i]=value;
            };
            for(int i=0;i<count;i++)refresh(i);
            for(;;) {
                int at=-1;double top=Double.NegativeInfinity;
                for(int k=0;k<count;k++) if(alive[k] && bestOf[k]>=0 && bestValue[k]>top){top=bestValue[k];at=k;}
                if(at<0 || top<threshold)break;
                int other=bestOf[at];
                for(int k=0;k<count;k++) if(alive[k] && k!=at && k!=other)
                    pair[(long)at*count+k]=pair[(long)k*count+at]=pair[(long)at*count+k]+pair[(long)other*count+k];
                size[at]+=size[other];members[at].AddRange(members[other]);alive[other]=false;
                refresh(at);
                for(int k=0;k<count;k++) {
                    if(!alive[k] || k==at)continue;
                    if(bestOf[k]==at || bestOf[k]==other)refresh(k);
                    else {double a=average(k,at);if(a>bestValue[k]){bestValue[k]=a;bestOf[k]=at;}}
                }
            }
            var groups=new List<List<Face>>();
            for(int i=0;i<count;i++)if(alive[i])groups.Add(members[i]);
            return groups.OrderByDescending(g=>g.Count).ToList();
        }
    }
}
