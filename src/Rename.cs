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
        internal RenameWindow(Window owner,string name) {
            UI.WindowSetup(this,L.T("Rename"));Owner=owner;ShowInTaskbar=false;
            WindowStartupLocation=WindowStartupLocation.CenterOwner;ResizeMode=ResizeMode.NoResize;
            Width=Math.Min(460,SystemParameters.WorkArea.Width);SizeToContent=SizeToContent.Height;
            var root=new DockPanel();root.Children.Add(new WindowTitleBar(this));
            var page=new StackPanel {Margin=new Thickness(22,16,22,20)};root.Children.Add(page);Content=root;
            var title=UI.Text(L.T("New name"),14);page.Children.Add(title);
            string extension=Path.GetExtension(name),stem=Path.GetFileNameWithoutExtension(name);
            var row=new DockPanel {Margin=new Thickness(0,6,0,0)};
            var ext=UI.Text(extension,14,true);ext.Margin=new Thickness(6,0,0,0);DockPanel.SetDock(ext,Dock.Right);row.Children.Add(ext);
            var box=new TextBox {Text=stem,Padding=new Thickness(6),FontSize=14};
            System.Windows.Automation.AutomationProperties.SetName(box,L.T("New name"));
            row.Children.Add(box);page.Children.Add(row);
            var hint=UI.Text(NoticeLook.KeepWords(L.T("Its JPG covers, upscaled copies and GIF covers get the same new name.")),12,true);
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
