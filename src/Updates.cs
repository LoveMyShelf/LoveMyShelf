using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace LoveMyShelf {
    internal static class AppInfo {
        // Keep in step with the GitHub release tag (v1.2.0 and so on).
        internal const string Version="1.0.7";
        // The last version without the "updated" notice: someone coming from it has no record of what they last saw.
        internal const string LastWithoutNotice="1.0.4";
        internal const string ReleasesPage="https://github.com/LoveMyShelf/LoveMyShelf/releases/latest";
        const string LatestApi="https://api.github.com/repos/LoveMyShelf/LoveMyShelf/releases/latest";

        // Asks GitHub once for the newest release tag. Sends nothing but a plain request;
        // returns null when offline, when GitHub can't be reached, or when this copy is current.
        internal static Task<string> NewerVersionAsync() {
            return Task.Run(()=>{
                try {
                    ServicePointManager.SecurityProtocol|=(SecurityProtocolType)3072; // TLS 1.2
                    var request=(HttpWebRequest)WebRequest.Create(LatestApi);
                    request.UserAgent="LoveMyShelf";request.Accept="application/vnd.github+json";
                    request.Timeout=8000;request.ReadWriteTimeout=8000;
                    string json;
                    using(var response=request.GetResponse())
                    using(var reader=new StreamReader(response.GetResponseStream()))json=reader.ReadToEnd();
                    var match=Regex.Match(json,"\"tag_name\"\\s*:\\s*\"v?([0-9]+(?:\\.[0-9]+){1,3})\"");
                    if(!match.Success)return null;
                    return IsNewer(match.Groups[1].Value,Version)?match.Groups[1].Value:null;
                } catch(Exception) {return null;}
            });
        }

        internal static bool IsNewer(string candidate,string current) {
            Version a,b;
            return System.Version.TryParse(candidate,out a) && System.Version.TryParse(current,out b) && a>b;
        }
    }

    internal sealed class UpdateFile {
        internal string RelativePath,Hash,Url;
        internal long Size,Download;
    }
    internal sealed class ReleaseItem {
        internal string Group,Icon,TitleKo,TitleEn,TextKo,TextEn;
        internal string[] Keys=new string[0];
    }
    // What changed in one version, in both languages. It travels inside the signed update list.
    internal sealed class ReleaseNote {
        internal string Version,SummaryKo,SummaryEn;
        internal List<ReleaseItem> Items=new List<ReleaseItem>();
    }
    internal sealed class UpdatePlan {
        internal string Version;
        internal List<UpdateFile> Files=new List<UpdateFile>();
        internal List<ReleaseNote> Notes=new List<ReleaseNote>(); // newest first, only versions this copy doesn't have
        internal byte[] Manifest,Signature;
    }

    // One-click update. A release carries a signed list of every program file. Only files that differ are
    // downloaded, each is checked against the list, and a small copy of this program swaps them in after
    // Love My Shelf closes. If the swap or the first start fails, the previous files are put back.
    // Covers and settings live outside the program folder and are never touched.
    internal static class AppUpdater {
        internal const string ApplySwitch="--apply-update";
        internal const string AppMutex=@"Local\LoveMyShelf.Personal.v1";
        internal const string ReadyEvent=@"Local\LoveMyShelf.UpdateStarted";
        internal const string PublicKey="<RSAKeyValue><Modulus>sFUj0/1LRAQsgw+WnRgj5o6mtqZ9sfkuoO3ZYZYllTdNg3OtGqGaR2nmPSyAxquOrEniWi3ccyyy6GWBwing/v+ybD/dQZK4t8pJ2t+/u2HdfNkXGZeieyjYHi594TafAT33jIDg/6hZeVTt6uyZRqeI1+p5/n59nXhdYo/c3bHUN0x56KvsQtjeq08kotn9kot5ObMaXrvelmXz2Ad+8nCkqykqB/AiOsceUsNW+GiHSjbvlsbcDzEcAEVQp5T34gNXGdLpq4LLC/OVJdmHX2TDhbpFBeDxbeIVrpO3M5WXeK+RfnlUDOxD9LRdaFo8l8y62dHQEf7VLdsr/JgH9PjL5BoD9mbDX+KciLmlFktITdvncw1CwmS2sli2T4DuP/disP5UZBlfGJy0Z0qqlfjLGlNjwooICUEDQgibxAXKMThwiLi1u/hIjbTpFfs+D1RvJ0UOMdhuzevc8LAnz3q7/5uZqw8c0uddj9xPZVrrKTE+ju6N3wCh3BchZXqh</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";
        const string DownloadPrefix="https://github.com/LoveMyShelf/LoveMyShelf/releases/download/";
        const string ManifestName="LoveMyShelf-update.xml",SignatureName="LoveMyShelf-update.sig";
        const string ExeName="LoveMyShelf.exe",HelperName="LoveMyShelf-apply.exe",FailedName="failed.txt";
        const string BackupFolder=".lovemyshelf-update-backup",NewSuffix=".lovemyshelf-new";

        static string AppFolder {get {return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);}}

        // Installed with winget (portable package): winget keeps its own record of the files and the version,
        // so this copy must not replace its own files. Updates go through "winget upgrade" instead.
        internal const string WingetId="LoveMyShelf.LoveMyShelf";
        static bool? fromWinget,wingetMachine;
        static string WingetRoot(Environment.SpecialFolder place,params string[] rest) {
            string root=Environment.GetFolderPath(place);
            if(String.IsNullOrEmpty(root))return null; // never let "WinGet\Packages" become a path relative to the working folder
            return Path.GetFullPath(Path.Combine(new[]{root}.Concat(rest).ToArray())).TrimEnd(Path.DirectorySeparatorChar);
        }
        internal static bool FromWinget {get {
            if(fromWinget==null) {
                bool found=false,machine=false;
                try {
                    string folder=Path.GetFullPath(AppFolder).TrimEnd(Path.DirectorySeparatorChar);
                    string name=Path.GetFileName(folder),parent=Path.GetDirectoryName(folder);
                    string user=WingetRoot(Environment.SpecialFolder.LocalApplicationData,"Microsoft","WinGet","Packages");
                    var machineRoots=new[]{WingetRoot(Environment.SpecialFolder.ProgramFiles,"WinGet","Packages"),
                        WingetRoot(Environment.SpecialFolder.ProgramFilesX86,"WinGet","Packages")};
                    Func<string,bool> same=root=>root!=null && String.Equals(root,parent,StringComparison.OrdinalIgnoreCase);
                    if(parent!=null && name.StartsWith(WingetId+"_",StringComparison.OrdinalIgnoreCase)) {
                        if(same(user))found=true;
                        else if(machineRoots.Any(same))found=machine=true;
                    }
                    // winget's own record: an uninstall entry with this package id whose install folder is this one.
                    // This also covers a changed portable install root (PortablePackageUserRoot / MachineRoot).
                    if(!found) {
                        int where=WingetRecord(folder);
                        if(where>0){found=true;machine=where==2;}
                    }
                    // Started through a link in winget's Links folder (the manifest avoids this, but be safe).
                    if(!found && String.Equals(folder,WingetRoot(Environment.SpecialFolder.LocalApplicationData,"Microsoft","WinGet","Links"),StringComparison.OrdinalIgnoreCase))found=true;
                } catch(Exception) {}
                fromWinget=found;wingetMachine=machine;
            }
            return fromWinget.Value;
        }}

        // 0 = no record, 1 = for this user, 2 = for all users.
        static int WingetRecord(string folder) {
            var places=new[]{Tuple.Create(Microsoft.Win32.RegistryHive.CurrentUser,1),Tuple.Create(Microsoft.Win32.RegistryHive.LocalMachine,2)};
            foreach(var place in places)
                foreach(var view in new[]{Microsoft.Win32.RegistryView.Registry64,Microsoft.Win32.RegistryView.Registry32}) {
                    try {
                        using(var root=Microsoft.Win32.RegistryKey.OpenBaseKey(place.Item1,view))
                        using(var list=root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall")) {
                            if(list==null)continue;
                            foreach(string name in list.GetSubKeyNames()) {
                                if(!name.StartsWith(WingetId+"_",StringComparison.OrdinalIgnoreCase))continue;
                                using(var entry=list.OpenSubKey(name)) {
                                    if(entry==null || !String.Equals(entry.GetValue("WinGetPackageIdentifier") as string,WingetId,StringComparison.OrdinalIgnoreCase))continue;
                                    string location=entry.GetValue("InstallLocation") as string;
                                    if(String.IsNullOrEmpty(location))continue;
                                    if(String.Equals(Path.GetFullPath(location).TrimEnd(Path.DirectorySeparatorChar),folder,StringComparison.OrdinalIgnoreCase))return place.Item2;
                                }
                            }
                        }
                    } catch(Exception) {}
                }
            return 0;
        }

        // Opens a PowerShell window that waits for this program to close, runs winget upgrade for this package,
        // and starts the program again when it worked. If it didn't, the window stays open with winget's own message.
        // Values go in as environment variables, so no path is ever pasted into the script text.
        internal static void StartWingetUpgrade() {
            bool machine=FromWinget && wingetMachine==true;
            const string script=
                "$Host.UI.RawUI.WindowTitle='Love My Shelf update';"+
                "$id=[int]$env:LMS_PID;"+
                "Wait-Process -Id $id -Timeout 120 -ErrorAction SilentlyContinue;"+
                "if(Get-Process -Id $id -ErrorAction SilentlyContinue){Write-Host 'Love My Shelf is still running. Close it and try again.';Read-Host 'Press Enter to close';exit 1}"+
                "winget upgrade --id $env:LMS_WINGET_ID --exact --source winget;"+
                "if($LASTEXITCODE -eq 0){if(Test-Path -LiteralPath $env:LMS_EXE){Start-Process -FilePath $env:LMS_EXE}}"+
                "else{Write-Host '';Read-Host 'Press Enter to close'}";
            string arguments="-NoProfile -ExecutionPolicy Bypass -EncodedCommand "+Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            string exe=Assembly.GetExecutingAssembly().Location,pid=Process.GetCurrentProcess().Id.ToString();
            if(machine) {
                // Installed for all users under Program Files: winget needs administrator rights there.
                // An elevated start can't take extra environment variables, so set them on this process first; the child inherits them.
                Environment.SetEnvironmentVariable("LMS_PID",pid);Environment.SetEnvironmentVariable("LMS_EXE",exe);
                Environment.SetEnvironmentVariable("LMS_WINGET_ID",WingetId);
                Process.Start(new ProcessStartInfo("powershell.exe",arguments) {UseShellExecute=true,Verb="runas",WorkingDirectory=Path.GetTempPath()});
                return;
            }
            var start=new ProcessStartInfo("powershell.exe",arguments) {UseShellExecute=false,WorkingDirectory=Path.GetTempPath()};
            start.EnvironmentVariables["LMS_PID"]=pid;start.EnvironmentVariables["LMS_EXE"]=exe;start.EnvironmentVariables["LMS_WINGET_ID"]=WingetId;
            Process.Start(start);
        }

        static string UpdateRoot {get {return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LoveMyShelf","Update");}}
        static string Staged(string stage,UpdateFile file) {return Path.Combine(stage,"files",file.RelativePath.Replace('/',Path.DirectorySeparatorChar));}
        static string Installed(string appDir,UpdateFile file) {return Path.Combine(appDir,file.RelativePath.Replace('/',Path.DirectorySeparatorChar));}

        // Returns null when this release can't be installed by itself; the caller then offers the download page.
        internal static Task<UpdatePlan> LoadAsync(string version) {
            return Task.Run(()=>{
                try {
                    if(FromWinget || File.Exists(Path.Combine(UpdateRoot,version,FailedName)) || !CanWrite(AppFolder))return null;
                    string baseUrl=DownloadPrefix+"v"+version+"/";
                    return Read(FetchBytes(baseUrl+ManifestName),FetchBytes(baseUrl+SignatureName),version,PublicKey);
                } catch(Exception) {return null;}
            });
        }
        static HttpWebRequest Request(string url) {
            ServicePointManager.SecurityProtocol|=(SecurityProtocolType)3072;
            var request=(HttpWebRequest)WebRequest.Create(url);
            request.UserAgent="LoveMyShelf";request.Timeout=30000;request.ReadWriteTimeout=30000;
            return request;
        }
        static byte[] FetchBytes(string url) {
            const int limit=1024*1024;
            using(var response=Request(url).GetResponse())
            using(var body=response.GetResponseStream())
            using(var memory=new MemoryStream()) {
                var buffer=new byte[16384];int read;
                while((read=body.Read(buffer,0,buffer.Length))>0) {
                    memory.Write(buffer,0,read);
                    if(memory.Length>limit)throw new InvalidDataException();
                }
                return memory.ToArray();
            }
        }
        static bool CanWrite(string folder) {
            string probe=Path.Combine(folder,".lovemyshelf-update-"+Guid.NewGuid().ToString("N")+".tmp");
            try {File.WriteAllBytes(probe,new byte[0]);File.Delete(probe);return true;}
            catch(IOException) {return false;} catch(UnauthorizedAccessException) {return false;}
        }

        // Nothing in the list is trusted until its signature checks out.
        internal static UpdatePlan Read(byte[] manifest,byte[] signature,string expectedVersion,string key) {
            try {
                using(var rsa=new RSACryptoServiceProvider()) {
                    rsa.PersistKeyInCsp=false;rsa.FromXmlString(key);
                    if(!rsa.VerifyData(manifest,"SHA256",signature))return null;
                }
                XElement root;
                using(var memory=new MemoryStream(manifest))root=XDocument.Load(memory).Root;
                if(root==null || root.Name!="update")return null;
                var plan=new UpdatePlan {Version=(string)root.Attribute("version"),Manifest=manifest,Signature=signature};
                if(plan.Version!=expectedVersion || !AppInfo.IsNewer(plan.Version,AppInfo.Version))return null;
                var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach(var item in root.Elements("file")) {
                    var file=new UpdateFile {RelativePath=(string)item.Attribute("path"),Hash=(string)item.Attribute("sha256"),
                        Url=(string)item.Attribute("url"),Size=(long)item.Attribute("size")};
                    file.Download=Math.Max(0,(long?)item.Attribute("download")??file.Size);
                    if(!SafePath(file.RelativePath) || !seen.Add(file.RelativePath) || file.Size<0 ||
                        file.Hash==null || !Regex.IsMatch(file.Hash,"^[0-9a-f]{64}$") ||
                        file.Url==null || !file.Url.StartsWith(DownloadPrefix,StringComparison.Ordinal))return null;
                    plan.Files.Add(file);
                }
                if(!seen.Contains(ExeName))return null;
                string newest=plan.Version;
                plan.Notes=ReadNotes(root,v=>AppInfo.IsNewer(v,AppInfo.Version) && !AppInfo.IsNewer(v,newest));
                return plan;
            } catch(Exception) {return null;}
        }
        // Every version up to this one, newest first, from the notes built into the program.
        internal static List<ReleaseNote> InstalledNotes(string upTo=AppInfo.Version) {
            try {
                using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("LoveMyShelf.release-notes.xml")) {
                    if(stream==null)return new List<ReleaseNote>();
                    return ReadNotes(XDocument.Load(stream).Root,v=>System.Version.Parse(v)<=System.Version.Parse(upTo));
                }
            } catch(Exception) {return new List<ReleaseNote>();}
        }
        // A problem in the notes never blocks an update; the plain notice is used instead.
        static List<ReleaseNote> ReadNotes(XElement root,Func<string,bool> wanted) {
            var notes=new List<ReleaseNote>();
            try {
                foreach(var release in root.Elements("release")) {
                    string version=(string)release.Attribute("version");
                    if(!wanted(version))continue;
                    var note=new ReleaseNote {Version=version,SummaryKo=(string)release.Attribute("summaryKo")??"",SummaryEn=(string)release.Attribute("summaryEn")??""};
                    foreach(var item in release.Elements("item")) {
                        string keys=(string)item.Attribute("keys")??"";
                        var entry=new ReleaseItem {Group=(string)item.Attribute("group")??"better",Icon=(string)item.Attribute("icon")??"",
                            TitleKo=(string)item.Attribute("titleKo")??"",TitleEn=(string)item.Attribute("titleEn")??"",
                            TextKo=(string)item.Attribute("textKo")??"",TextEn=(string)item.Attribute("textEn")??"",
                            Keys=keys.Split(new[]{' '},StringSplitOptions.RemoveEmptyEntries)};
                        if(entry.TitleKo.Length>0 && entry.TitleEn.Length>0)note.Items.Add(entry);
                    }
                    if(note.Items.Count>0)notes.Add(note);
                }
                notes.Sort((a,b)=>System.Version.Parse(b.Version).CompareTo(System.Version.Parse(a.Version)));
                return notes;
            } catch(Exception) {return new List<ReleaseNote>();}
        }
        // How much this copy really has to download: files it already has, or got earlier, don't count.
        internal static Task<long> DownloadSizeAsync(UpdatePlan plan,CancellationToken token) {
            return Task.Run(()=>{
                string stage=Path.Combine(UpdateRoot,plan.Version),appDir=AppFolder;
                long total=0;
                foreach(var file in plan.Files) {
                    token.ThrowIfCancellationRequested();
                    if(!Matches(Installed(appDir,file),file,token) && !Matches(Staged(stage,file),file,token))total+=file.Download;
                }
                return total;
            },token);
        }
        static bool SafePath(string path) {
            if(path==null || !Regex.IsMatch(path,@"^[A-Za-z0-9._ -]+(/[A-Za-z0-9._ -]+)*$"))return false;
            return path.Split('/').All(part=>part!="." && part!=".." && !part.EndsWith(".") && !part.EndsWith(" "));
        }

        // Downloads whatever differs from this copy. Returns the folder holding the checked files.
        internal static string Prepare(UpdatePlan plan,CancellationToken token) {
            string stage=Path.Combine(UpdateRoot,plan.Version),appDir=AppFolder;
            Directory.CreateDirectory(stage);
            foreach(var file in plan.Files) {
                token.ThrowIfCancellationRequested();
                string staged=Staged(stage,file);
                if(Matches(Installed(appDir,file),file,token)) {
                    if(File.Exists(staged))File.Delete(staged);
                    continue;
                }
                if(!Matches(staged,file,token))Fetch(file,staged,token);
            }
            File.WriteAllBytes(Path.Combine(stage,ManifestName),plan.Manifest);
            File.WriteAllBytes(Path.Combine(stage,SignatureName),plan.Signature);
            return stage;
        }
        static bool Matches(string path,UpdateFile file,CancellationToken token) {
            var info=new FileInfo(path);
            if(!info.Exists || info.Length!=file.Size)return false;
            using(var sha=SHA256.Create())
            using(var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)) {
                var buffer=new byte[1024*1024];int read;
                while((read=input.Read(buffer,0,buffer.Length))>0) {
                    token.ThrowIfCancellationRequested();
                    sha.TransformBlock(buffer,0,read,null,0);
                }
                sha.TransformFinalBlock(buffer,0,0);
                return Hex(sha.Hash)==file.Hash;
            }
        }
        static string Hex(byte[] bytes) {return BitConverter.ToString(bytes).Replace("-","").ToLowerInvariant();}
        static void Fetch(UpdateFile file,string target,CancellationToken token) {
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            string part=target+".part";
            try {
                using(var response=Request(file.Url).GetResponse())
                using(var body=response.GetResponseStream())
                using(var unzip=new GZipStream(body,CompressionMode.Decompress))
                using(var output=File.Create(part))
                using(var sha=SHA256.Create()) {
                    var buffer=new byte[256*1024];int read;long total=0;
                    while((read=unzip.Read(buffer,0,buffer.Length))>0) {
                        token.ThrowIfCancellationRequested();
                        total+=read;
                        if(total>file.Size)throw new InvalidDataException();
                        output.Write(buffer,0,read);sha.TransformBlock(buffer,0,read,null,0);
                    }
                    sha.TransformFinalBlock(buffer,0,0);
                    if(total!=file.Size || Hex(sha.Hash)!=file.Hash)throw new InvalidDataException();
                }
                if(File.Exists(target))File.Delete(target);
                File.Move(part,target);
            } finally {
                try {if(File.Exists(part))File.Delete(part);} catch(IOException quiet) {ErrorLog.Quiet(quiet);} catch(UnauthorizedAccessException quiet) {ErrorLog.Quiet(quiet);}
            }
        }

        // Starts the helper. The caller closes the app right after this returns.
        internal static void StartApply(string stage) {
            string helper=Path.Combine(stage,HelperName);
            File.Copy(Assembly.GetExecutingAssembly().Location,helper,true);
            Process.Start(new ProcessStartInfo(helper,ApplySwitch+" \""+stage+"\" \""+AppFolder+"\"") {UseShellExecute=false,CreateNoWindow=true});
        }
        internal static int RunApply(string stage,string appDir) {
            return Apply(stage,appDir,PublicKey,AppMutex,ReadyEvent,ExeName,120);
        }

        internal static int Apply(string stage,string appDir,string key,string mutexName,string readyName,string exeName,int readySeconds) {
            string exe=Path.Combine(appDir,exeName),backup=Path.Combine(appDir,BackupFolder);
            var swapped=new List<Tuple<string,string>>();
            var fresh=new List<string>();
            using(var guard=new Mutex(false,mutexName)) {
                // The app holds this until it has fully closed. Holding it here also stops a second start mid-swap.
                if(!Take(guard,60))return 2;
                try {
                    var plan=Read(File.ReadAllBytes(Path.Combine(stage,ManifestName)),File.ReadAllBytes(Path.Combine(stage,SignatureName)),
                        Path.GetFileName(stage.TrimEnd(Path.DirectorySeparatorChar)),key);
                    if(plan==null)throw new InvalidDataException();
                    var work=plan.Files.Where(f=>File.Exists(Staged(stage,f))).ToList();
                    foreach(var file in work)if(!Matches(Staged(stage,file),file,CancellationToken.None))throw new InvalidDataException();
                    if(Directory.Exists(backup))Directory.Delete(backup,true);
                    // Copy everything first: a full disk stops here, before any working file has moved.
                    foreach(var file in work) {
                        string target=Installed(appDir,file);
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        fresh.Add(target+NewSuffix);
                        File.Copy(Staged(stage,file),target+NewSuffix,true);
                    }
                    foreach(var file in work) {
                        string target=Installed(appDir,file),saved=null;
                        if(File.Exists(target)) {
                            saved=Path.Combine(backup,file.RelativePath.Replace('/',Path.DirectorySeparatorChar));
                            Directory.CreateDirectory(Path.GetDirectoryName(saved));
                            string from=target,to=saved;
                            Retry(()=>File.Move(from,to));
                        }
                        swapped.Add(Tuple.Create(target,saved));
                        string next=target+NewSuffix,final=target;
                        Retry(()=>File.Move(next,final));
                    }
                } catch(Exception failed) {
                    ErrorLog.Serious("Update could not be put in place",failed);
                    Undo(swapped,fresh);MarkFailed(stage);
                    // Let go of it altogether before the version before it starts: an older one takes this still being
                    // open to mean a copy is already running.
                    guard.ReleaseMutex();guard.Close();
                    Launch(exe,appDir);
                    return 1;
                }
                using(var ready=new EventWaitHandle(false,EventResetMode.ManualReset,readyName)) {
                    guard.ReleaseMutex();
                    if(StartedWell(Launch(exe,appDir),ready,readySeconds))return 0;
                }
                if(Take(guard,30)) {
                    Undo(swapped,fresh);MarkFailed(stage);
                    guard.ReleaseMutex();guard.Close();
                    Launch(exe,appDir);
                }
                return 1;
            }
        }
        static bool Take(Mutex guard,int seconds) {
            try {return guard.WaitOne(TimeSpan.FromSeconds(seconds));}
            catch(AbandonedMutexException) {return true;}
        }
        // Antivirus or a process that is just exiting can hold a file for a moment.
        static void Retry(Action action) {
            for(int attempt=0;;attempt++) {
                try {action();return;}
                catch(IOException) {if(attempt>=40)throw;}
                catch(UnauthorizedAccessException) {if(attempt>=40)throw;}
                Thread.Sleep(250);
            }
        }
        static void Undo(List<Tuple<string,string>> swapped,List<string> fresh) {
            for(int i=swapped.Count-1;i>=0;i--) {
                var pair=swapped[i];
                try {
                    Retry(()=>{if(File.Exists(pair.Item1))File.Delete(pair.Item1);});
                    if(pair.Item2!=null)Retry(()=>File.Move(pair.Item2,pair.Item1));
                } catch(Exception quiet) {ErrorLog.Quiet(quiet);}
            }
            foreach(string path in fresh)try {if(File.Exists(path))File.Delete(path);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}
        }
        static void MarkFailed(string stage) {
            try {File.WriteAllText(Path.Combine(stage,FailedName),DateTime.UtcNow.ToString("o"));} catch(Exception quiet) {ErrorLog.Quiet(quiet);}
            try {string files=Path.Combine(stage,"files");if(Directory.Exists(files))Directory.Delete(files,true);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}
        }
        static Process Launch(string exe,string appDir) {
            try {return Process.Start(new ProcessStartInfo(exe) {UseShellExecute=false,WorkingDirectory=appDir});}
            catch(Exception) {return null;}
        }
        // A new version that closes before its first window appears counts as failed.
        // One that shows nothing for a long time may be stuck, or only slow (a drive waking up, an antivirus scan):
        // from outside the two look the same, so it is never closed from here. The wait goes on five times as long
        // instead, so that when the person gives up and ends it, the version before it still comes back.
        static bool StartedWell(Process process,EventWaitHandle ready,int seconds) {
            if(process==null)return false;
            using(process) {
                var waited=Stopwatch.StartNew(); // not the clock: it jumps when the computer sleeps or the time is set
                bool noted=false;
                while(waited.Elapsed.TotalSeconds<seconds*5.0) {
                    if(ready.WaitOne(250))return true;
                    if(process.HasExited)return ready.WaitOne(0);
                    if(!noted && waited.Elapsed.TotalSeconds>=seconds) {
                        noted=true;
                        ErrorLog.Note("Update","The new version has not shown its first window after "+seconds+" seconds. Still waiting for it.");
                    }
                }
                ErrorLog.Note("Update","The new version never reported its first window. It is left running, and the files of the version before it are kept.");
                return true;
            }
        }

        // An update that was undone and has not been told about yet: its version, once. The mark itself stays, since it
        // also keeps that version from being tried again; a line added to it is what makes this happen only once.
        const string ToldMark="told";
        internal static string UndoneUpdate() {
            try {
                if(!Directory.Exists(UpdateRoot))return null;
                string found=null;
                foreach(string folder in Directory.GetDirectories(UpdateRoot)) {
                    string version=Path.GetFileName(folder),mark=Path.Combine(folder,FailedName);
                    System.Version parsed;
                    // One mark that can't be read or written must not keep quiet about another that was just written.
                    try {
                        if(!System.Version.TryParse(version,out parsed) || !AppInfo.IsNewer(version,AppInfo.Version) || !File.Exists(mark))continue;
                        if(File.ReadAllText(mark).Contains(ToldMark))continue;
                        File.AppendAllText(mark,Environment.NewLine+ToldMark);
                        if(found==null || AppInfo.IsNewer(version,found))found=version;
                    } catch(Exception quiet) {ErrorLog.Quiet(quiet);}
                }
                return found;
            } catch(Exception quiet) {ErrorLog.Quiet(quiet);return null;}
        }
        // True while the update helper is waiting to hear that this new version opened. If it can't open, closing
        // at once (no window to answer first) is what lets the helper put the version before it back.
        internal static bool HelperWaiting {
            get {
                if(reported)return false;
                try {
                    EventWaitHandle ready;
                    if(!EventWaitHandle.TryOpenExisting(ReadyEvent,out ready))return false;
                    ready.Dispose();
                    return true;
                } catch(Exception quiet) {ErrorLog.Quiet(quiet);return false;}
            }
        }
        static volatile bool reported;
        // Called once the first window is on screen: tells the helper all is well, then clears what's left.
        internal static void ReportStarted() {
            reported=true;
            try {
                EventWaitHandle ready;
                if(EventWaitHandle.TryOpenExisting(ReadyEvent,out ready))using(ready)ready.Set();
            } catch(Exception quiet) {ErrorLog.Quiet(quiet);}
            string appDir=AppFolder;
            Task.Delay(5000).ContinueWith(delegate {
                try {string backup=Path.Combine(appDir,BackupFolder);if(Directory.Exists(backup))Directory.Delete(backup,true);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}
                try {
                    if(!Directory.Exists(UpdateRoot))return;
                    foreach(string folder in Directory.GetDirectories(UpdateRoot)) {
                        System.Version parsed;
                        if(System.Version.TryParse(Path.GetFileName(folder),out parsed) && !AppInfo.IsNewer(parsed.ToString(),AppInfo.Version))
                            try {Directory.Delete(folder,true);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}
                    }
                } catch(Exception quiet) {ErrorLog.Quiet(quiet);}
            });
        }
    }
}
