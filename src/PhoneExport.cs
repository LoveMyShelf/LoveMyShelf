using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace LoveMyShelf {
    // One tile to send to the phone, in the order shown: its cover file and its original.
    internal sealed class PhoneItem {internal string Cover,Original;}

    // "See it on your phone": copies the covers on screen (and, if chosen, their originals) into a folder that
    // Google Drive or OneDrive keeps in step, named 001_name.jpg, 002_name.jpg … so a phone app sorted by name
    // shows them in the same order. Only copies: nothing on this computer is changed. Sending again brings the
    // folder up to date: files already there are kept or only renamed, and files this app put there before that
    // are no longer wanted are removed. Files someone else put in the folder are never touched.
    internal static class PhoneExport {
        internal const string FolderName="Love My Shelf 휴대폰";
        internal sealed class Place {internal string Name,Root;}
        // Google Drive and OneDrive folders on this computer (the same search as backups).
        internal static List<Place> Places() {
            var list=new List<Place>();var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var p in Backup.CloudPlaces()) {
                string root=Path.Combine(Path.GetDirectoryName(p.Path),FolderName);
                if(seen.Add(root))list.Add(new Place {Name=p.Name,Root=root});
            }
            return list;
        }
        // A folder name Windows and the cloud apps accept.
        internal static string Clean(string name,int max) {
            var bad=new HashSet<char>(Path.GetInvalidFileNameChars());
            var b=new StringBuilder();
            string text=name??"";
            for(int i=0;i<text.Length;i++) {
                char c=text[i];
                if(Char.IsHighSurrogate(c) && i+1<text.Length && Char.IsLowSurrogate(text[i+1])){b.Append(c).Append(text[++i]);continue;}
                b.Append(bad.Contains(c) || c<' ' || Char.IsSurrogate(c)?'_':c); // a lone half of an emoji can't be written down
            }
            string s=b.ToString().Trim().TrimEnd('.',' ');
            if(s.Length>max){int cut=max;if(Char.IsLowSurrogate(s[cut]))cut--;s=s.Substring(0,cut).TrimEnd('.',' ');}
            return s.Length==0?"Love My Shelf":s;
        }

        internal sealed class Plan {
            internal readonly List<Tuple<string,string>> Files=new List<Tuple<string,string>>(); // name in the folder, source file
            internal int Covers,MissingOriginals;
            internal long Bytes;
        }
        // The names, in the order shown. With originals, each original follows its first cover as 001_name_원본.mp4
        // ("." sorts before "_", so the cover comes first). An original with several covers is sent once.
        internal static Plan MakePlan(IList<PhoneItem> items,bool originals,string originalWord) {
            var plan=new Plan();
            int width=Math.Max(3,items.Count.ToString(CultureInfo.InvariantCulture).Length);
            var sentOriginals=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var missing=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int number=0;
            foreach(var item in items) {
                if(item.Cover==null || !File.Exists(item.Cover))continue;
                number++;
                string name=Clean(Path.GetFileNameWithoutExtension(item.Original??item.Cover),60);
                string head=number.ToString(CultureInfo.InvariantCulture).PadLeft(width,'0')+"_"+name;
                plan.Files.Add(Tuple.Create(head+Path.GetExtension(item.Cover).ToLowerInvariant(),item.Cover));
                plan.Covers++;plan.Bytes+=Size(item.Cover);
                if(!originals || item.Original==null || sentOriginals.Contains(item.Original))continue;
                if(!File.Exists(item.Original)){missing.Add(item.Original);continue;}
                sentOriginals.Add(item.Original);
                plan.Files.Add(Tuple.Create(head+"_"+originalWord+Path.GetExtension(item.Original),item.Original));
                plan.Bytes+=Size(item.Original);
            }
            plan.MissingOriginals=missing.Count(m=>!sentOriginals.Contains(m));
            return plan;
        }
        static long Size(string path) {try {return new FileInfo(path).Length;} catch(Exception) {return 0;}}

        // What this app put in a folder last time: name, source, the source's size and time when it was copied.
        sealed class Record {internal string Name,Source;internal long Length,Ticks;}
        static string RecordFile(string storeRoot,string folder) {
            string key;
            using(var md5=MD5.Create())
                key=BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(folder).TrimEnd('\\').ToLowerInvariant()))).Replace("-","").Substring(0,16);
            return Path.Combine(storeRoot,"phone",key+".txt");
        }
        static List<Record> ReadRecords(string file) {
            var list=new List<Record>();
            try {
                if(!File.Exists(file))return list;
                foreach(var line in File.ReadAllLines(file,Encoding.UTF8)) {
                    var parts=line.Split('\t');if(parts.Length<4)continue;
                    long length,ticks;
                    if(!Int64.TryParse(parts[2],out length) || !Int64.TryParse(parts[3],out ticks))continue;
                    list.Add(new Record {Name=parts[0],Source=parts[1],Length=length,Ticks=ticks});
                }
            } catch(Exception ex) {
                // Starting from nothing would copy everything again beside the old copies: stop instead.
                ErrorLog.Quiet(ex);
                throw new IOException(L.P("The list of what was sent before can't be read right now. Try again in a moment.","전에 보낸 목록을 지금 읽을 수 없어요. 잠시 뒤에 다시 해 주세요."),ex);
            }
            return list;
        }
        static void WriteRecords(string file,IEnumerable<Record> records) {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string temp=file+".new";
            File.WriteAllLines(temp,records.Select(r=>r.Name+"\t"+r.Source+"\t"+r.Length.ToString(CultureInfo.InvariantCulture)+"\t"+r.Ticks.ToString(CultureInfo.InvariantCulture)),new UTF8Encoding(false));
            if(File.Exists(file))File.Replace(temp,file,null);else File.Move(temp,file);
        }

        internal sealed class Result {internal int Copied,Kept,Renamed,Removed,Covers,MissingOriginals;internal string Folder;}
        internal sealed class Progress {internal int Done,Total;internal long Bytes,TotalBytes;internal string Name;}

        // Brings the folder in line with the plan. Throws IOException when there is not enough room.
        internal static Result Send(Plan plan,string folder,string storeRoot,Action<Progress> report,CancellationToken token) {
            Directory.CreateDirectory(folder);
            string recordFile=RecordFile(storeRoot,folder);
            var old=ReadRecords(recordFile);
            // Only records whose copy is still there, unchanged, count as ours.
            old=old.Where(r=>{
                string path=Path.Combine(folder,r.Name);
                return r.Name.IndexOfAny(new[]{'\\','/'})<0 && File.Exists(path) && Size(path)==r.Length;
            }).ToList();
            var result=new Result {Folder=folder,Covers=plan.Covers,MissingOriginals=plan.MissingOriginals};
            var kept=new List<Record>();
            var free=new List<Record>(old);
            // 1. What can stay as it is, or only be renamed (a cloud app renames without uploading again).
            var steps=new List<Tuple<string,string,Record,long,long>>(); // name, source, reused record, length, ticks
            foreach(var f in plan.Files) {
                var info=new FileInfo(f.Item2);
                if(!info.Exists)continue; // gone since the window opened (a drive unplugged)
                long length=info.Length,ticks=info.LastWriteTimeUtc.Ticks;
                Func<Record,bool> same=r=>String.Equals(r.Source,f.Item2,StringComparison.OrdinalIgnoreCase) && r.Length==length && r.Ticks==ticks;
                var reuse=free.FirstOrDefault(r=>same(r) && String.Equals(r.Name,f.Item1,StringComparison.OrdinalIgnoreCase))??free.FirstOrDefault(same);
                if(reuse!=null)free.Remove(reuse);
                steps.Add(Tuple.Create(f.Item1,f.Item2,reuse,length,ticks));
            }
            // 2. Room: what still has to be copied.
            long needed=steps.Where(s=>s.Item3==null).Sum(s=>s.Item4);
            try {
                var drive=new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder)));
                if(drive.IsReady && drive.AvailableFreeSpace<needed+64L*1024*1024)
                    throw new IOException(L.PF("There is not enough room: {0} is needed and {1} is free.","공간이 모자라요. {0}가 필요한데 {1}만 남아 있어요.",DetailsWindow.Size(needed),DetailsWindow.Size(drive.AvailableFreeSpace)));
            } catch(ArgumentException) {}
            // 3. Files of ours that are no longer wanted go first, so their names are free.
            var records=new List<Record>(free.Count+steps.Count);
            foreach(var r in free) {
                token.ThrowIfCancellationRequested();
                try {File.Delete(Path.Combine(folder,r.Name));result.Removed++;}
                catch(Exception ex) {ErrorLog.Quiet(ex);records.Add(r);}
            }
            // 4. Renames go through a passing name first, so two files can swap places.
            var renames=steps.Where(s=>s.Item3!=null && !String.Equals(s.Item3.Name,s.Item1,StringComparison.Ordinal)).ToList();
            var passing=new Dictionary<Record,string>();
            records.AddRange(steps.Where(s=>s.Item3!=null).Select(s=>s.Item3));
            try {
                foreach(var s in renames) {
                    string temp="~lms-"+Guid.NewGuid().ToString("N").Substring(0,12)+".tmp";
                    File.Move(Path.Combine(folder,s.Item3.Name),Path.Combine(folder,temp));
                    passing[s.Item3]=s.Item3.Name;s.Item3.Name=temp;
                }
            } finally {
                // Where everything is now, also when a rename failed half way (the next send picks it up from here).
                WriteRecords(recordFile,records);
            }
            var progress=new Progress {Total=steps.Count,TotalBytes=needed};
            foreach(var s in steps) {
                token.ThrowIfCancellationRequested();
                progress.Name=s.Item1;report(progress);
                string name=FreeName(folder,s.Item1,s.Item3);
                string target=Path.Combine(folder,name);
                if(s.Item3!=null) {
                    if(!String.Equals(s.Item3.Name,name,StringComparison.Ordinal)) {
                        File.Move(Path.Combine(folder,s.Item3.Name),target);
                        s.Item3.Name=name;
                        if(passing.ContainsKey(s.Item3) && String.Equals(passing[s.Item3],name,StringComparison.Ordinal))result.Kept++;else result.Renamed++;
                    } else result.Kept++;
                    WriteRecords(recordFile,records);
                } else {
                    Copy(s.Item2,target,token,b=>{progress.Bytes+=b;report(progress);});
                    records.Add(new Record {Name=name,Source=s.Item2,Length=s.Item4,Ticks=s.Item5});
                    WriteRecords(recordFile,records);
                    result.Copied++;
                }
                progress.Done++;
            }
            progress.Name=null;report(progress);
            return result;
        }
        // The planned name, or "name (2)" when a file someone else put there already has it.
        static string FreeName(string folder,string name,Record own) {
            string candidate=name;
            for(int n=2;(Directory.Exists(Path.Combine(folder,candidate)) || File.Exists(Path.Combine(folder,candidate))) && (own==null || !String.Equals(own.Name,candidate,StringComparison.OrdinalIgnoreCase));n++)
                candidate=Path.GetFileNameWithoutExtension(name)+" ("+n.ToString(CultureInfo.InvariantCulture)+")"+Path.GetExtension(name);
            return candidate;
        }
        // A copy that can be stopped; a half-made copy is removed.
        static void Copy(string source,string target,CancellationToken token,Action<long> copied) {
            bool done=false,created=false;
            try {
                using(var from=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete,1<<20,FileOptions.SequentialScan))
                using(var to=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None,1<<20)) {
                    created=true;
                    var buffer=new byte[1<<20];int read;
                    while((read=from.Read(buffer,0,buffer.Length))>0) {
                        token.ThrowIfCancellationRequested();
                        to.Write(buffer,0,read);copied(read);
                    }
                }
                try {File.SetLastWriteTimeUtc(target,File.GetLastWriteTimeUtc(source));} catch(Exception) {}
                done=true;
            } finally {
                if(!done && created)try {File.Delete(target);} catch(Exception) {} // never a file that was there before
            }
        }
    }
}
