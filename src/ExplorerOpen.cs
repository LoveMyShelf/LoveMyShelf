using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace LoveMyShelf {
    // "Open with Love My Shelf" from File Explorer: the path in the command line, a second start that hands the
    // path to the running program, and the per-user Windows entries (no administrator needed) that put it in the menu.
    internal static class ExplorerOpen {
        const string AppFile="LoveMyShelf.exe",VerbName="LoveMyShelf";
        static readonly string[] ImageTypes={".jpg",".jpeg",".jfif",".png",".bmp",".tif",".tiff",".gif",".webp"};
        static string[] Types {get {return ImageTypes.Concat(VideoFrames.Extensions).ToArray();}}

        // The first argument that names an existing file or folder (switches like --apply-update are skipped).
        internal static string PathFrom(string[] args) {
            foreach(string arg in args) {
                if(String.IsNullOrWhiteSpace(arg) || arg.StartsWith("--") || arg.StartsWith("/"))continue;
                try {
                    string full=Path.GetFullPath(arg);
                    if(File.Exists(full) || Directory.Exists(full))return full;
                } catch(Exception) {}
            }
            return null;
        }

        // ---- handing the path to the program that is already running ----
        static string PipeName {get {
            string user;
            try {user=System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;} catch(Exception) {user=Environment.UserName;}
            return "LoveMyShelf.Open."+user;
        }}
        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);

        internal static bool Send(string path) {
            try {
                AllowSetForegroundWindow(-1); // so the running program may come to the front
                using(var client=new NamedPipeClientStream(".",PipeName,PipeDirection.Out,PipeOptions.None)) {
                    client.Connect(4000);
                    byte[] bytes=new UTF8Encoding(false).GetBytes(path+"\n");
                    client.Write(bytes,0,bytes.Length);client.Flush();
                }
                return true;
            } catch(Exception) {return false;}
        }

        internal static void Listen(Action<string> received) {
            var thread=new Thread(()=>{
                while(true) {
                    try {
                        // Only this Windows account may connect.
                        var security=new PipeSecurity();
                        security.AddAccessRule(new PipeAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User,PipeAccessRights.ReadWrite,System.Security.AccessControl.AccessControlType.Allow));
                        using(var server=new NamedPipeServerStream(PipeName,PipeDirection.In,1,PipeTransmissionMode.Byte,PipeOptions.None,0,0,security)) {
                            server.WaitForConnection();
                            using(var reader=new StreamReader(server,new UTF8Encoding(false))) {
                                string line=reader.ReadLine();
                                if(!String.IsNullOrWhiteSpace(line))received(line.Trim());
                            }
                        }
                    } catch(IOException) {Thread.Sleep(200);}
                    catch(UnauthorizedAccessException) {return;}
                    catch(Exception) {Thread.Sleep(500);}
                }
            }) {IsBackground=true,Name="LoveMyShelf open-with listener"};
            thread.Start();
        }

        // ---- Windows entries (current user only) ----
        static string AppKey {get {return @"Software\Classes\Applications\"+AppFile;}}
        static string VerbKey(string type) {return @"Software\Classes\SystemFileAssociations\"+type+@"\shell\"+VerbName;}
        static string Label {get {return L.Korean?"Love My Shelf로 열기":"Open with Love My Shelf";}}
        static string ExePath {get {return Assembly.GetExecutingAssembly().Location;}}

        // Puts the entries in when asked for, takes them out when not, and keeps the program's place current.
        internal static void Sync(Preferences prefs) {
            if(prefs.ExplorerMenu)Register();else Unregister();
        }

        static string Read(string key,string name) {
            using(var k=Registry.CurrentUser.OpenSubKey(key)) {return k==null?null:k.GetValue(name) as string;}
        }
        static void Write(string key,string name,string value) {
            using(var k=Registry.CurrentUser.CreateSubKey(key)) {k.SetValue(name,value);}
        }

        static void Register() {
            string exe=ExePath,command="\""+exe+"\" \"%1\"",label=Label;
            // Nothing to do when it is already written for this place and language.
            if(Read(AppKey+@"\shell\open\command",null)==command && Read(VerbKey(".jpg"),null)==label && Read(VerbKey(".jpg")+@"\command",null)==command)return;
            Write(AppKey,"FriendlyAppName","Love My Shelf");
            Write(AppKey+@"\DefaultIcon",null,"\""+exe+"\",0");
            Write(AppKey+@"\shell\open",null,"");
            Write(AppKey+@"\shell\open\command",null,command);
            foreach(string type in Types) {
                Write(AppKey+@"\SupportedTypes",type,"");
                Write(VerbKey(type),null,label);
                Write(VerbKey(type),"Icon","\""+exe+"\",0");
                Write(VerbKey(type)+@"\command",null,command);
            }
        }

        static void Unregister() {
            using(var classes=Registry.CurrentUser.OpenSubKey(@"Software\Classes",true)) {
                if(classes==null)return;
                classes.DeleteSubKeyTree(@"Applications\"+AppFile,false);
                foreach(string type in Types)classes.DeleteSubKeyTree(@"SystemFileAssociations\"+type+@"\shell\"+VerbName,false);
            }
        }
    }
}
