using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml.Linq;

namespace LoveMyShelf {
    // "Locked photo boxes": a locked copy of chosen photo folders, made with 7-Zip (7z, AES-256, file names locked too,
    // no compression because photos and videos don't get smaller, in 2 GB parts). The originals are never touched.
    // One box per folder; a box is made again only when something in its folder changed or the password changed.
    // The password goes to 7-Zip through its input, never on its command line, so other programs can't read it there.
    // Any 7-Zip can open a box with the same password, also without LoveMyShelf.
    internal static class PhotoBox {
        internal static string SevenZip() {
            string here=AppDomain.CurrentDomain.BaseDirectory;
            var places=new List<string> {Path.Combine(here,"tools","7z.exe"),Path.Combine(here,"7z.exe")};
            foreach(var v in new[]{"ProgramW6432","ProgramFiles","ProgramFiles(x86)"}) {
                string pf=Environment.GetEnvironmentVariable(v);
                if(!String.IsNullOrEmpty(pf))places.Add(Path.Combine(pf,"7-Zip","7z.exe"));
            }
            return places.FirstOrDefault(File.Exists);
        }
        internal static string BoxFolder(string destination) {return Path.Combine(destination,Backup.BoxFolderName);}

        // The files of a folder as a short fingerprint: names, sizes and change times. A box is made again when it differs.
        internal static string Listing(string folder,out int files,out long size,out long largest) {
            files=0;size=0;largest=0;
            var lines=new List<string>();
            foreach(string file in Directory.EnumerateFiles(folder,"*",SearchOption.AllDirectories)) {
                try {
                    var info=new FileInfo(file);
                    lines.Add(file.Substring(folder.Length).ToUpperInvariant()+"|"+info.Length+"|"+info.LastWriteTimeUtc.Ticks);
                    files++;size+=info.Length;largest=Math.Max(largest,info.Length);
                } catch(IOException) {} catch(UnauthorizedAccessException) {}
            }
            lines.Sort(StringComparer.Ordinal);
            using(var sha=SHA256.Create())
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(String.Join("\n",lines))));
        }
        internal static long Size(string folder,CancellationToken token) {
            long size=0;
            try {
                foreach(string file in Directory.EnumerateFiles(folder,"*",SearchOption.AllDirectories)) {
                    if(token.IsCancellationRequested)return -1;
                    try {size+=new FileInfo(file).Length;} catch(IOException) {} catch(UnauthorizedAccessException) {}
                }
            } catch(IOException) {return -1;} catch(UnauthorizedAccessException) {return -1;}
            return size;
        }
        // Folders inside another chosen folder are already in that folder's box.
        internal static List<string> TopFolders(IEnumerable<string> folders) {
            var list=folders.Select(f=>f.TrimEnd('\\')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return list.Where(f=>!list.Any(o=>!String.Equals(o,f,StringComparison.OrdinalIgnoreCase) && Backup.Inside(f,o))).ToList();
        }

        // Runs 7-Zip with the password on its input. Returns the exit code (0 = fine) and what it said.
        static int Run(string arguments,string workingDirectory,string password,Action<int> percent,CancellationToken token,out string said,bool keepAll=false) {
            string exe=SevenZip();
            if(exe==null)throw new FileNotFoundException(L.P("7-Zip wasn't found.","7-Zip을 찾지 못했어요."));
            var info=new ProcessStartInfo(exe,arguments) {
                UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,
                WorkingDirectory=workingDirectory??Path.GetTempPath(),
                StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8
            };
            using(var process=Process.Start(info)) {
                var output=new StringBuilder();var errors=new StringBuilder();
                var reading=new Thread(()=>{
                    var buffer=new char[4096];var number=new StringBuilder();
                    int read;
                    try {
                        while((read=process.StandardOutput.Read(buffer,0,buffer.Length))>0) {
                            for(int i=0;i<read;i++) {
                                char c=buffer[i];
                                if(Char.IsDigit(c))number.Append(c);
                                else {
                                    if(c=='%' && number.Length>0 && number.Length<4 && percent!=null)percent(Int32.Parse(number.ToString(),CultureInfo.InvariantCulture));
                                    number.Clear();
                                }
                                if(keepAll || output.Length<20000)output.Append(c);
                            }
                        }
                    } catch(Exception) {}
                }) {IsBackground=true};
                reading.Start();
                var errorReading=new Thread(()=>{try {errors.Append(process.StandardError.ReadToEnd());} catch(Exception) {}}) {IsBackground=true};
                errorReading.Start();
                try {
                    byte[] secret=new UTF8Encoding(false).GetBytes(password.Normalize(NormalizationForm.FormC)+"\n");
                    process.StandardInput.BaseStream.Write(secret,0,secret.Length);
                    process.StandardInput.BaseStream.Flush();
                    Array.Clear(secret,0,secret.Length);
                    process.StandardInput.Close();
                } catch(IOException) {}
                while(!process.WaitForExit(200)) {
                    if(token.IsCancellationRequested) {
                        try {process.Kill();} catch(Exception) {}
                        process.WaitForExit(5000);
                        token.ThrowIfCancellationRequested();
                    }
                }
                reading.Join(3000);errorReading.Join(3000);
                said=output.ToString()+"\n"+errors.ToString();
                return process.ExitCode;
            }
        }
        static string Quote(string path) {return "\""+path.Replace("\"","")+"\"";}
        static List<string> Parts(string folder,string name) {
            if(!Directory.Exists(folder))return new List<string>();
            return Directory.GetFiles(folder,name+".7z.*").Where(p=>Regex.IsMatch(Path.GetExtension(p),@"^\.\d{3}$"))
                .OrderBy(p=>p,StringComparer.OrdinalIgnoreCase).ToList();
        }
        // What a box holds, read back from the box itself: how many files and how many bytes.
        static bool Count(string firstPart,string password,CancellationToken token,out int files,out long size) {
            files=0;size=0;string said;
            if(Run("l -ba -slt -sccUTF-8 "+Quote(firstPart),null,password,null,token,out said,true)!=0)return false;
            bool folder=false;long length=0;bool inItem=false;
            foreach(string raw in said.Split('\n')) {
                string line=raw.TrimEnd('\r');
                if(line.StartsWith("Path = ",StringComparison.Ordinal)) {
                    if(inItem && !folder){files++;size+=length;}
                    inItem=true;folder=false;length=0;
                } else if(line.StartsWith("Size = ",StringComparison.Ordinal)) {
                    Int64.TryParse(line.Substring(7),NumberStyles.Integer,CultureInfo.InvariantCulture,out length);
                } else if(line.StartsWith("Attributes = ",StringComparison.Ordinal) && line.Substring(13).Contains("D")) folder=true;
                else if(line.StartsWith("Folder = +",StringComparison.Ordinal)) folder=true;
            }
            if(inItem && !folder){files++;size+=length;}
            return true;
        }

        // Makes the boxes that are new or out of date. Boxes that can't be made keep their last good copy.
        internal static void MakeAll(BackupSettings settings,string password,string epoch,XElement folders,XElement boxList,
            Backup.Result result,Action<Backup.Progress> report,CancellationToken token) {
            string boxDir=BoxFolder(settings.Destination);
            Directory.CreateDirectory(boxDir);
            var chosen=TopFolders(settings.BoxFolders);
            int n=0;
            foreach(string source in chosen) {
                token.ThrowIfCancellationRequested();
                n++;
                string name=Path.GetFileName(source.TrimEnd('\\'));
                var box=settings.Boxes.FirstOrDefault(b=>String.Equals(b.Source.TrimEnd('\\'),source,StringComparison.OrdinalIgnoreCase));
                Func<string> step=()=>L.PF("Photo box {0} of {1}: ‘{2}’","사진 상자 {1}개 중 {0}번째: ‘{2}’",n,chosen.Count,name);
                if(!Directory.Exists(source)) {
                    result.BoxProblems.Add(L.PF("‘{0}’ wasn't found, so its last box was kept.","‘{0}’ 폴더를 못 찾아서 지난번 상자를 그대로 뒀어요.",name));
                    KeepOld(boxList,box,folders,name,epoch,result);
                    continue;
                }
                if(Backup.Inside(settings.Destination,source)) {
                    result.BoxProblems.Add(L.PF("‘{0}’ holds the backup folder itself, so it can't go in a box.","‘{0}’ 폴더 안에 백업 폴더가 있어서 상자로 만들 수 없어요.",name));
                    continue;
                }
                report(new Backup.Progress {Step=step(),Detail=L.P("Looking for changes","바뀐 게 있는지 보고 있어요")});
                int files;long size,largest;
                string listing=Listing(source,out files,out size,out largest);
                if(box!=null && box.Listing==listing && box.Epoch==epoch && Parts(boxDir,"B-"+box.Id).Count==box.Parts && box.Parts>0) {
                    result.BoxesKept++;AddToList(boxList,box,folders,name);continue;
                }
                long free=FreeSpace(boxDir);
                if(free<size+256L*1024*1024) {
                    result.BoxProblems.Add(L.PF("Not enough space for ‘{0}’ (needs {1}).","‘{0}’ 상자를 만들 공간이 모자라요({1} 필요).",name,Size(size)));
                    KeepOld(boxList,box,folders,name,epoch,result);
                    continue;
                }
                string id=box!=null?box.Id:Guid.NewGuid().ToString("N").Substring(0,10);
                string fresh="B-"+id+"-new";
                foreach(string old in Parts(boxDir,fresh))File.Delete(old);
                string said;
                int code;
                try {
                code=Run("a -t7z -mx0 -mhe=on -v2g -p -sccUTF-8 -ssw -bsp1 -bb0 "+Quote(Path.Combine(boxDir,fresh+".7z"))+" *",source,password,
                    p=>report(new Backup.Progress {Step=step(),Detail=L.P("Locking a copy","잠긴 복사본을 만들고 있어요"),Fraction=p/100.0}),token,out said);
                } catch(OperationCanceledException) {foreach(string old in Parts(boxDir,fresh))TryDelete(old);throw;}
                var made=Parts(boxDir,fresh);
                if(code>1 || made.Count==0) {
                    foreach(string old in made)File.Delete(old);
                    result.BoxProblems.Add(L.PF("The box for ‘{0}’ couldn't be made.","‘{0}’ 상자를 만들지 못했어요.",name)+" "+Short(said));
                    KeepOld(boxList,box,folders,name,epoch,result);
                    continue;
                }
                report(new Backup.Progress {Step=step(),Detail=L.P("Checking that it opens","잘 열리는지 확인하고 있어요"),Fraction=0});
                int boxFiles=0;long boxSize=0;bool counted;
                try {
                    int test=Run("t -sccUTF-8 -bsp1 -bb0 "+Quote(made[0]),null,password,
                        p=>report(new Backup.Progress {Step=step(),Detail=L.P("Checking that it opens","잘 열리는지 확인하고 있어요"),Fraction=p/100.0}),token,out said);
                    counted=test==0 && Count(made[0],password,token,out boxFiles,out boxSize);
                } catch(OperationCanceledException) {foreach(string old in made)TryDelete(old);throw;}
                if(!counted || boxFiles!=files || boxSize!=size) {
                    foreach(string old in made)File.Delete(old);
                    result.BoxProblems.Add(L.PF("The box for ‘{0}’ didn't pass the check (files may have changed while it was made), so the last one was kept.",
                        "‘{0}’ 상자가 확인을 통과하지 못했어요(만드는 사이 파일이 바뀌었을 수 있어요). 지난번 상자를 그대로 뒀어요.",name));
                    KeepOld(boxList,box,folders,name,epoch,result);
                    continue;
                }
                // Checked: the old parts step aside (renamed, not deleted), the new ones take their name, and only then
                // the old ones go. If a part is held by the cloud app, everything is put back as it was.
                if(!Swap(boxDir,id,made)) {
                    result.BoxProblems.Add(L.PF("The box for ‘{0}’ was made but couldn't replace the last one (a file was in use). The last one was kept.",
                        "‘{0}’ 상자를 만들었지만 파일을 쓰고 있는 프로그램이 있어서 지난번 상자와 바꾸지 못했어요. 지난번 상자를 그대로 뒀어요.",name));
                    KeepOld(boxList,box,folders,name,epoch,result);
                    continue;
                }
                if(box==null){box=new BackupSettings.Box {Id=id,Source=source};settings.Boxes.Add(box);}
                box.Epoch=epoch;box.Listing=listing;box.Parts=made.Count;box.Files=files;box.Size=size;box.Largest=largest;
                settings.Save();
                result.BoxesMade++;result.BoxBytes+=size;
                AddToList(boxList,box,folders,name);
            }
        }
        static void TryDelete(string file) {try {File.Delete(file);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}}
        static bool Swap(string boxDir,string id,List<string> made) {
            var old=Parts(boxDir,"B-"+id);
            var aside=new List<Tuple<string,string>>();
            var placed=new List<string>();
            try {
                foreach(string part in old){string to=Path.Combine(boxDir,"B-"+id+"-old.7z"+Path.GetExtension(part));if(File.Exists(to))File.Delete(to);File.Move(part,to);aside.Add(Tuple.Create(part,to));}
                foreach(string part in made){string to=Path.Combine(boxDir,"B-"+id+".7z"+Path.GetExtension(part));File.Move(part,to);placed.Add(to);}
            } catch(IOException quiet) {
                ErrorLog.Quiet(quiet);
                foreach(string part in placed)TryDelete(part);
                foreach(var pair in aside)try {File.Move(pair.Item2,pair.Item1);} catch(Exception again) {ErrorLog.Quiet(again);}
                foreach(string part in made)TryDelete(part);
                return false;
            } catch(UnauthorizedAccessException quiet) {
                ErrorLog.Quiet(quiet);
                foreach(string part in placed)TryDelete(part);
                foreach(var pair in aside)try {File.Move(pair.Item2,pair.Item1);} catch(Exception again) {ErrorLog.Quiet(again);}
                foreach(string part in made)TryDelete(part);
                return false;
            }
            foreach(var pair in aside)TryDelete(pair.Item2);
            return true;
        }
        // The last good box stays in the list only when it was locked with this backup's password.
        static void KeepOld(XElement boxList,BackupSettings.Box box,XElement folders,string name,string epoch,Backup.Result result) {
            if(box==null)return;
            if(box.Epoch!=epoch) {
                result.BoxProblems.Add(L.PF("The last box of ‘{0}’ is locked with an older password, so this backup doesn't count on it.",
                    "‘{0}’의 지난번 상자는 예전 비밀번호로 잠겨 있어서 이번 백업에는 넣지 않았어요.",name));
                return;
            }
            AddToList(boxList,box,folders,name);
        }
        static void AddToList(XElement boxList,BackupSettings.Box box,XElement folders,string name) {
            var folder=folders.Elements("folder").FirstOrDefault(f=>String.Equals(((string)f.Attribute("source")??"").TrimEnd('\\'),box.Source.TrimEnd('\\'),StringComparison.OrdinalIgnoreCase));
            boxList.Add(new XElement("box",new XAttribute("id",box.Id),new XAttribute("folder",folder==null?"":(string)folder.Attribute("id")),
                new XAttribute("source",box.Source),new XAttribute("name",name),new XAttribute("parts",box.Parts),
                new XAttribute("files",box.Files),new XAttribute("size",box.Size),new XAttribute("largest",box.Largest)));
        }
        static string Short(string said) {
            var line=(said??"").Split('\n').Select(l=>l.Trim()).Where(l=>l.StartsWith("ERROR",StringComparison.OrdinalIgnoreCase) || l.Contains("WARNING")).FirstOrDefault();
            return line??"";
        }
        internal static long FreeSpace(string path) {
            try {return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))).AvailableFreeSpace;} catch(Exception) {return Int64.MaxValue;}
        }
        internal static string Size(long bytes) {
            if(bytes>=1L<<30)return (bytes/(double)(1L<<30)).ToString("0.0",CultureInfo.InvariantCulture)+" GB";
            if(bytes>=1L<<20)return (bytes/(double)(1L<<20)).ToString("0",CultureInfo.InvariantCulture)+" MB";
            return Math.Max(1,bytes/1024).ToString(CultureInfo.InvariantCulture)+" KB";
        }

        // ---------------- Opening boxes in a restore ----------------
        internal sealed class Found {
            internal string Id,FolderId,Source,Name;internal int Parts,Files;internal long Size,Largest;
            internal List<string> PartFiles=new List<string>();
            internal bool Complete {get {return PartFiles.Count==Parts && Parts>0;}}
        }
        static readonly Regex IdText=new Regex("^[0-9a-f]{4,32}$");
        static string SafeName(string name) {
            name=name??"";
            foreach(char bad in Path.GetInvalidFileNameChars())name=name.Replace(bad,'_');
            name=name.Trim().Trim('.');
            return name.Length==0 || name.Length>120?"Photos":name;
        }
        internal static List<Found> FindBoxes(Backup.Opened opened,string backupFolder) {
            var list=new List<Found>();
            var boxes=opened.Manifest.Element("boxes");
            if(boxes==null)return list;
            string dir=BoxFolder(backupFolder);
            foreach(var b in boxes.Elements("box")) {
                var found=new Found {Id=(string)b.Attribute("id"),FolderId=(string)b.Attribute("folder"),Source=(string)b.Attribute("source"),
                    Name=(string)b.Attribute("name"),Parts=(int?)b.Attribute("parts")??0,Files=(int?)b.Attribute("files")??0,
                    Size=(long?)b.Attribute("size")??0,Largest=(long?)b.Attribute("largest")??0};
                if(found.Id==null || !IdText.IsMatch(found.Id))continue;
                found.Name=SafeName(found.Name);
                found.PartFiles=Parts(dir,"B-"+found.Id);
                list.Add(found);
            }
            return list;
        }
        // Whether a drive can take the boxes: room for all of them plus a little, and files bigger than 4 GB need NTFS or exFAT.
        internal static string CanOpen(IEnumerable<Found> boxes,string parent,out long need,out long free) {
            need=boxes.Sum(b=>b.Size)+512L*1024*1024;
            free=FreeSpace(parent);
            if(free<need)return L.PF("This drive needs {0} more.","이 드라이브에 {0}가 더 필요해요.",Size(need-free));
            try {
                var drive=new DriveInfo(Path.GetPathRoot(Path.GetFullPath(parent)));
                if(drive.DriveFormat.StartsWith("FAT",StringComparison.OrdinalIgnoreCase) && !drive.DriveFormat.Equals("exFAT",StringComparison.OrdinalIgnoreCase)
                    && boxes.Any(b=>b.Largest>=4L*1024*1024*1024))
                    return L.P("A file is bigger than 4 GB, which this drive (FAT32) can't hold. Choose another drive.","4GB보다 큰 파일이 있어서 이 드라이브(FAT32)에는 못 풀어요. 다른 드라이브를 골라 주세요.");
            } catch(Exception) {}
            return null;
        }
        // Opens one box into a new folder inside parent. Returns that folder.
        internal static string Open(Found box,string parent,string password,Action<int> percent,CancellationToken token) {
            if(box.Id==null || !IdText.IsMatch(box.Id))throw new IOException("bad box");
            string work=Path.Combine(parent,".lms-box-"+box.Id);
            if(Directory.Exists(work))Directory.Delete(work,true);
            string said;int code;
            try {code=Run("x -sccUTF-8 -bsp1 -bb0 -y -o"+Quote(work)+" "+Quote(box.PartFiles[0]),null,password,percent,token,out said);}
            catch(OperationCanceledException) {try {if(Directory.Exists(work))Directory.Delete(work,true);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}throw;}
            if(code!=0) {
                try {if(Directory.Exists(work))Directory.Delete(work,true);} catch(Exception) {}
                throw new IOException(L.PF("The photo box for ‘{0}’ couldn't be opened.","‘{0}’ 사진 상자를 풀지 못했어요.",box.Name)+" "+Short(said));
            }
            string name=SafeName(box.Name);
            string target=Path.Combine(parent,name);
            for(int n=2;Directory.Exists(target) || File.Exists(target);n++)target=Path.Combine(parent,name+" ("+n+")");
            Directory.Move(work,target);
            return target;
        }
    }
}
