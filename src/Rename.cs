using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Xml.Linq;

namespace LoveMyShelf {
    // Renames an original in its folder and carries everything LoveMyShelf keeps for it along:
    // the cover records (main and extra), and the cover, upscaled and GIF files, whose names start with the original's.
    internal static class Renamer {
        internal static readonly char[] Invalid=Path.GetInvalidFileNameChars();

        // Returns null when the name is fine, otherwise a friendly reason.
        internal static string Problem(string stem) {
            if(String.IsNullOrWhiteSpace(stem))return L.T("Please type a name.");
            if(stem.IndexOfAny(Invalid)>=0)return L.T("A name can't contain \\ / : * ? \" < > |");
            if(stem.EndsWith(".") || stem.EndsWith(" "))return L.T("A name can't end with a dot or a space.");
            return null;
        }

        static string SourceOf(string albumFolder) {
            string file=Path.Combine(albumFolder,"source.txt");
            return File.Exists(file)?File.ReadAllText(file):null;
        }

        sealed class Undo {
            public readonly List<Tuple<string,string,bool>> Moves=new List<Tuple<string,string,bool>>(); // from, to, directory
            public void Move(string from,string to,bool directory) {
                if(directory)Directory.Move(from,to);else File.Move(from,to);
                Moves.Add(Tuple.Create(from,to,directory));
            }
            public void Rollback() {
                for(int i=Moves.Count-1;i>=0;i--) {
                    var m=Moves[i];
                    try {if(m.Item3)Directory.Move(m.Item2,m.Item1);else File.Move(m.Item2,m.Item1);} catch(Exception) {}
                }
            }
        }

        // A cover file named after the old original gets the same rest of its name after the new stem.
        static string RenamedFile(string path,string oldName,string newName) {
            if(String.IsNullOrEmpty(path) || !File.Exists(path))return null;
            string file=Path.GetFileName(path);
            string oldStem=Path.GetFileNameWithoutExtension(oldName),newStem=Path.GetFileNameWithoutExtension(newName);
            foreach(var style in new[]{Tuple.Create("_cover_",120),Tuple.Create("-cover-",64)}) {
                string a=oldStem.Length>style.Item2?oldStem.Substring(0,style.Item2).TrimEnd():oldStem;
                if(!file.StartsWith(a+style.Item1,StringComparison.OrdinalIgnoreCase))continue;
                string b=newStem.Length>style.Item2?newStem.Substring(0,style.Item2).TrimEnd():newStem;
                string target=Path.Combine(Path.GetDirectoryName(path),b+file.Substring(a.Length));
                if(String.Equals(target,path,StringComparison.OrdinalIgnoreCase) && target==path)return null;
                return target;
            }
            return null;
        }

        // Renames photo's original to newName (with extension). Returns the new path of the original.
        internal static string Rename(GalleryStore store,Photo photo,string newName) {
            string albumFolder=Path.GetDirectoryName(photo.Folder);
            string source=SourceOf(albumFolder);
            if(source==null)throw new InvalidOperationException(L.T("Only files in an added folder can be renamed."));
            string oldName=Path.GetFileName(photo.Name);
            string from=Path.Combine(source,oldName),to=Path.Combine(source,newName);
            if(!File.Exists(from))throw new FileNotFoundException(L.T("The original is missing from this folder."),from);
            bool caseOnly=String.Equals(oldName,newName,StringComparison.OrdinalIgnoreCase);
            if(oldName==newName)return from;
            if(!caseOnly && (File.Exists(to) || Directory.Exists(to)))throw new IOException(L.F("There is already a file named “{0}” here.",newName));
            string oldBase="p-"+GalleryStore.KeyOf(oldName),newBase="p-"+GalleryStore.KeyOf(newName);
            var records=Directory.GetDirectories(albumFolder,oldBase+"*").Where(d=>Path.GetFileName(d).Length==oldBase.Length ||
                Path.GetFileName(d)[oldBase.Length]=='-').ToList();
            foreach(var record in records) {
                string target=Path.Combine(albumFolder,newBase+Path.GetFileName(record).Substring(oldBase.Length));
                if(!caseOnly && Directory.Exists(target))throw new IOException(L.T("Love My Shelf still has records for an older file with that name. Press F5 and try again, or pick another name."));
            }
            var undo=new Undo();
            try {
                if(caseOnly) {
                    string temp=Path.Combine(source,Guid.NewGuid().ToString("N")+".tmp");
                    undo.Move(from,temp,false);undo.Move(temp,to,false);
                } else undo.Move(from,to,false);
                lock(SharedCoverFiles.MetadataLock) {
                    foreach(var record in records) {
                        string moved=record;
                        if(!caseOnly) {
                            moved=Path.Combine(albumFolder,newBase+Path.GetFileName(record).Substring(oldBase.Length));
                            undo.Move(record,moved,true);
                        }
                        string metadata=Path.Combine(moved,"photo.xml");
                        if(!File.Exists(metadata))continue;
                        var xml=XElement.Load(metadata);
                        xml.SetAttributeValue("name",newName);
                        xml.SetAttributeValue("original",newName);
                        foreach(string attribute in new[]{"coverFile","upscaleFile","motionFile"}) {
                            string current=(string)xml.Attribute(attribute);
                            string renamed=RenamedFile(current,oldName,newName);
                            if(renamed==null)continue;
                            if(File.Exists(renamed) && !String.Equals(renamed,current,StringComparison.OrdinalIgnoreCase))continue; // never overwrite
                            undo.Move(current,renamed,false);
                            xml.SetAttributeValue(attribute,renamed);
                        }
                        string part=Path.Combine(moved,"meta-"+Guid.NewGuid().ToString("N")+".tmp");
                        xml.Save(part);
                        string backup=Path.Combine(moved,"meta-"+Guid.NewGuid().ToString("N")+".bak");
                        File.Replace(part,metadata,backup);
                        undoBackups.Add(Tuple.Create(backup,metadata));
                    }
                }
            } catch {
                foreach(var b in undoBackups) try {if(File.Exists(b.Item1)){File.Copy(b.Item1,b.Item2,true);}} catch(Exception) {}
                undo.Rollback();
                CleanBackups();
                throw;
            }
            CleanBackups();
            // Point the remembered location of every cover record at the renamed original.
            foreach(var record in Directory.GetDirectories(albumFolder,newBase+"*")) {
                try {OriginalLocator.Remember(GalleryStore.Read(record,to),to);} catch(Exception) {}
            }
            return to;
        }
        // ----- Folders -----
        internal static bool IsAt(string path,string folder) {
            if(String.IsNullOrEmpty(path) || String.IsNullOrEmpty(folder))return false;
            string p=path.TrimEnd('\\'),f=folder.TrimEnd('\\');
            return String.Equals(p,f,StringComparison.OrdinalIgnoreCase) || p.StartsWith(f+"\\",StringComparison.OrdinalIgnoreCase);
        }
        internal static string Moved(string path,string from,string to) {
            return to.TrimEnd('\\')+path.Substring(Math.Min(path.Length,from.TrimEnd('\\').Length));
        }
        // A drive or a network share can't be renamed as a folder.
        internal static bool CanRenameFolder(string path) {
            try {
                string full=Path.GetFullPath(path).TrimEnd('\\'),root=Path.GetPathRoot(full).TrimEnd('\\');
                return !String.Equals(full,root,StringComparison.OrdinalIgnoreCase) && Directory.Exists(full);
            } catch(Exception) {return false;}
        }
        static void WriteBack(string file,List<Tuple<string,byte[]>> saved,Action<string> write) {
            saved.Add(Tuple.Create(file,File.ReadAllBytes(file)));
            string part=Path.Combine(Path.GetDirectoryName(file),Guid.NewGuid().ToString("N")+".tmp");
            try {write(part);File.Replace(part,file,null);}
            finally {if(File.Exists(part))File.Delete(part);}
        }
        static XElement LoadOrNull(string file) {
            if(!File.Exists(file))return null;
            try {return XElement.Load(file);} catch(System.Xml.XmlException) {return null;} // a damaged record never blocks the rename
        }
        static void Repoint(string file,string[] attributes,string from,string to,List<Tuple<string,byte[]>> saved) {
            var xml=LoadOrNull(file);if(xml==null)return;
            bool changed=false;
            foreach(string attribute in attributes) {
                string value=(string)xml.Attribute(attribute);
                if(!IsAt(value,from))continue;
                xml.SetAttributeValue(attribute,Moved(value,from,to));changed=true;
            }
            if(changed)WriteBack(file,saved,part=>xml.Save(part));
        }
        // Renames a folder and carries along the records of it and of every folder inside, the saved paths of
        // covers kept in it, and the cover folder setting. If any step fails, everything goes back as it was.
        internal static string RenameFolder(GalleryStore store,string folder,string newName) {
            string from=Path.GetFullPath(folder).TrimEnd('\\');
            if(!CanRenameFolder(from))throw new DirectoryNotFoundException(L.T("The source folder could not be found."));
            string to=Path.Combine(Path.GetDirectoryName(from),newName);
            if(from==to)return from;
            bool caseOnly=String.Equals(from,to,StringComparison.OrdinalIgnoreCase);
            if(!caseOnly && (Directory.Exists(to) || File.Exists(to)))throw new IOException(L.F("There is already a folder or file named “{0}” here.",newName));
            if(IsAt(store.Root,from))throw new IOException(L.T("A file in this folder is in use. Close it and try again."));
            var albums=store.Albums().Where(a=>IsAt(a.Source,from)).ToList();
            var targets=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            var stale=new List<string>();
            foreach(var album in albums) {
                string target=store.AlbumFolderOf(Moved(album.Source,from,to));
                targets[album.Folder]=target;
                if(String.Equals(target,album.Folder,StringComparison.OrdinalIgnoreCase) || !Directory.Exists(target))continue;
                if(Directory.GetDirectories(target,"p-*").Length>0)throw new IOException(L.T("Love My Shelf still has covers for an older folder with that name. Pick another name."));
                stale.Add(target);
            }
            var undo=new Undo();
            var saved=new List<Tuple<string,byte[]>>();
            var removed=new List<string>();
            try {
                try {
                    if(caseOnly) {
                        string temp=Path.Combine(Path.GetDirectoryName(from),Guid.NewGuid().ToString("N")+".tmp");
                        undo.Move(from,temp,true);undo.Move(temp,to,true);
                    } else undo.Move(from,to,true);
                }
                catch(IOException) {throw new IOException(L.T("A file in this folder is in use. Close it and try again."));}
                catch(UnauthorizedAccessException) {throw new IOException(L.T("A file in this folder is in use. Close it and try again."));}
                lock(SharedCoverFiles.MetadataLock) {
                    foreach(string old in stale) {
                        string aside=Path.Combine(store.Root,"setup-"+Guid.NewGuid().ToString("N"));
                        undo.Move(old,aside,true);removed.Add(aside);
                    }
                    foreach(var album in albums) {
                        string record=targets[album.Folder];
                        if(!String.Equals(record,album.Folder,StringComparison.OrdinalIgnoreCase))undo.Move(album.Folder,record,true);
                        string source=Moved(album.Source,from,to);
                        WriteBack(Path.Combine(record,"source.txt"),saved,part=>File.WriteAllText(part,source));
                        if(String.Equals(album.Source.TrimEnd('\\'),from,StringComparison.OrdinalIgnoreCase))
                            WriteBack(Path.Combine(record,"album.txt"),saved,part=>File.WriteAllText(part,newName));
                    }
                    // Covers of any folder may be kept inside the renamed one.
                    foreach(var album in store.Albums())
                        foreach(string record in Directory.GetDirectories(album.Folder,"p-*")) {
                            Repoint(Path.Combine(record,"photo.xml"),new[]{"coverFile","upscaleFile","motionFile"},from,to,saved);
                            Repoint(Path.Combine(record,"origin.xml"),new[]{"path"},from,to,saved);
                        }
                    var names=targets.ToDictionary(t=>Path.GetFileName(t.Key),t=>Path.GetFileName(t.Value),StringComparer.OrdinalIgnoreCase);
                    string order=Path.Combine(store.Root,"tab-order.txt");
                    if(File.Exists(order)) {
                        var lines=File.ReadAllLines(order).Select(l=>names.ContainsKey(l)?names[l]:l).ToArray();
                        WriteBack(order,saved,part=>File.WriteAllLines(part,lines));
                    }
                    string last=Path.Combine(store.Root,"last.txt");
                    if(File.Exists(last)) {
                        string shown=File.ReadAllText(last).Trim();
                        if(names.ContainsKey(shown))WriteBack(last,saved,part=>File.WriteAllText(part,names[shown]));
                    }
                    Repoint(Path.Combine(store.Root,"options.xml"),new[]{"coverDirectory"},from,to,saved);
                }
            } catch {
                for(int i=saved.Count-1;i>=0;i--) try {File.WriteAllBytes(saved[i].Item1,saved[i].Item2);} catch(Exception) {}
                undo.Rollback();
                throw;
            }
            foreach(string aside in removed) try {Directory.Delete(aside,true);} catch(Exception) {}
            return to;
        }
        [ThreadStatic] static List<Tuple<string,string>> backups;
        static List<Tuple<string,string>> undoBackups {get {return backups??(backups=new List<Tuple<string,string>>());}}
        static void CleanBackups() {
            foreach(var b in undoBackups) try {File.Delete(b.Item1);} catch(Exception) {}
            undoBackups.Clear();
        }
    }

    // Small window to type a new name. The extension stays as it is, like File Explorer.
    internal sealed class RenameWindow : Window {
        internal string NewName {get;private set;}
        internal RenameWindow(Window owner,string name,bool folder=false) {
            UI.WindowSetup(this,L.T("Rename"));Owner=owner;ShowInTaskbar=false;
            WindowStartupLocation=WindowStartupLocation.CenterOwner;ResizeMode=ResizeMode.NoResize;
            Width=Math.Min(460,SystemParameters.WorkArea.Width);SizeToContent=SizeToContent.Height;
            var root=new DockPanel();root.Children.Add(new WindowTitleBar(this));
            var page=new StackPanel {Margin=new Thickness(22,16,22,20)};root.Children.Add(page);Content=root;
            var title=UI.Text(L.T("New name"),14);page.Children.Add(title);
            string extension=folder?"":Path.GetExtension(name),stem=folder?name:Path.GetFileNameWithoutExtension(name);
            var row=new DockPanel {Margin=new Thickness(0,6,0,0)};
            var ext=UI.Text(extension,14,true);ext.Margin=new Thickness(6,0,0,0);DockPanel.SetDock(ext,Dock.Right);row.Children.Add(ext);
            var box=new TextBox {Text=stem,Padding=new Thickness(6),FontSize=14};
            System.Windows.Automation.AutomationProperties.SetName(box,L.T("New name"));
            row.Children.Add(box);page.Children.Add(row);
            var hint=UI.Text(NoticeLook.KeepWords(L.T(folder?"Its covers stay with the files inside.":"Its JPG covers, upscaled copies and GIF covers get the same new name.")),12,true);
            hint.TextWrapping=TextWrapping.Wrap;hint.Margin=new Thickness(0,8,0,0);page.Children.Add(hint);
            var error=UI.Text("",12);error.TextWrapping=TextWrapping.Wrap;error.Margin=new Thickness(0,6,0,0);
            error.SetResourceReference(TextBlock.ForegroundProperty,"Accent");page.Children.Add(error);
            var actions=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,14,0,0)};
            var cancel=UI.Btn(L.T("Cancel"),()=>DialogResult=false);cancel.IsCancel=true;
            var ok=UI.Btn(L.T("Rename"),()=>{
                string problem=Renamer.Problem(box.Text.Trim());
                if(problem!=null){error.Text=problem;box.Focus();return;}
                NewName=box.Text.Trim()+extension;DialogResult=true;
            },true);
            ok.IsDefault=true;ok.Margin=new Thickness(6,0,0,0);
            foreach(var b in new[]{cancel,ok}){b.FontSize=14;b.MinHeight=32;}
            actions.Children.Add(cancel);actions.Children.Add(ok);page.Children.Add(actions);
            box.TextChanged+=delegate {error.Text="";};
            Loaded+=delegate {box.Focus();box.SelectAll();};
        }
    }
}
