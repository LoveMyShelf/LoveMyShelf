using System;
using System.IO;

namespace LoveMyShelf {
    internal static class StoreImport {
        static string Marker {get {return Path.Combine(AppInfo.DataRoot,"imported-from-zip.txt");}}

        internal static void Run() {
            string from=AppInfo.LegacyDataRoot,to=AppInfo.DataRoot;
            // If WinRT fell back to the old location, there is nothing to copy.
            if(String.Equals(from,to,StringComparison.OrdinalIgnoreCase))return;
            // The marker decides: written once the import is done, or right away when there was nothing to import,
            // so records made later with the GitHub version are never pulled in by surprise. A copy that stopped
            // part way leaves no marker and carries on at the next start (files already copied are kept as they are).
            if(File.Exists(Marker))return;
            try {
                if(!Directory.Exists(Path.Combine(from,"Folders"))) {
                    Directory.CreateDirectory(to);
                    File.WriteAllText(Marker,"none");
                    return;
                }
                if(Copy(from,to,true))File.WriteAllText(Marker,"pending");
            } catch(Exception quiet) {ErrorLog.Quiet(quiet);}
        }

        static bool Copy(string from,string to,bool top) {
            Directory.CreateDirectory(to);
            if((File.GetAttributes(to)&FileAttributes.ReparsePoint)!=0)
                throw new IOException("The Store import destination is a link: "+to);
            bool complete=true;
            foreach(string file in Directory.GetFiles(from)) {
                string name=Path.GetFileName(file);
                if(name.StartsWith("errors",StringComparison.OrdinalIgnoreCase) && name.EndsWith(".txt",StringComparison.OrdinalIgnoreCase))continue;
                try {
                    string target=Path.Combine(to,name);
                    if(!File.Exists(target))File.Copy(file,target);
                } catch(Exception quiet) {ErrorLog.Quiet(quiet);complete=false;}
            }
            foreach(string folder in Directory.GetDirectories(from)) {
                string name=Path.GetFileName(folder);
                if(top && (String.Equals(name,"Update",StringComparison.OrdinalIgnoreCase) ||
                    String.Equals(name,"backup-work",StringComparison.OrdinalIgnoreCase)))continue;
                try {
                    // Do not follow junctions out of the old data folder.
                    if((File.GetAttributes(folder)&FileAttributes.ReparsePoint)!=0)
                        throw new IOException("The Store import source is a link: "+folder);
                    if(!Copy(folder,Path.Combine(to,name),false))complete=false;
                } catch(Exception quiet) {ErrorLog.Quiet(quiet);complete=false;}
            }
            return complete;
        }

        internal static bool TakeNotice() {
            if(!AppInfo.Packaged)return false;
            try {
                string mark=Marker;
                if(!File.Exists(mark) || File.ReadAllText(mark)!="pending")return false;
                File.WriteAllText(mark,"told");
                return true;
            } catch(Exception quiet) {ErrorLog.Quiet(quiet);return false;}
        }
    }
}
