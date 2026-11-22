using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LoveMyShelf {
    internal enum ConflictChoice { Replace, Skip, KeepBoth }

    // What the person is asked when a file with the same name is already in the target folder.
    internal sealed class ConflictInfo {
        public string Name,From,To,FolderName;
        public int Number,Total; // this is the Number-th of Total same-name files in this move
        public bool Copy;
    }

    internal sealed class MoveReport {
        public readonly List<Tuple<string,string>> Moved=new List<Tuple<string,string>>(); // old path, new path
        public int Copied,Skipped,AlreadyThere;
        public readonly List<string> Failed=new List<string>();
    }

    // Moves (or copies) original files into another added folder and carries what LoveMyShelf keeps for them along:
    // the cover records (main and extra) follow the original, so its covers stay connected to it.
    // Nothing is overwritten silently: a file with the same name asks first, and a replaced file goes to the Recycle Bin.
    internal static class Mover {
        static string Plain(string path) {return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);}
        internal static bool SameFolder(string a,string b) {
            return String.Equals(Plain(a),Plain(b),StringComparison.OrdinalIgnoreCase);
        }

        // "name (2).ext", the first one that is free in both folders.
        internal static string UniqueName(string folder,string name,string otherFolder) {
            string stem=Path.GetFileNameWithoutExtension(name),extension=Path.GetExtension(name);
            for(int n=2;n<10000;n++) {
                string candidate=stem+" ("+n+")"+extension;
                if(!File.Exists(Path.Combine(folder,candidate)) && !Directory.Exists(Path.Combine(folder,candidate)) &&
                    (otherFolder==null || !File.Exists(Path.Combine(otherFolder,candidate))))return candidate;
            }
            throw new IOException(L.T("Couldn't find a free name."));
        }

        // The record folders of one original: "p-<key>" and its extra covers "p-<key>-<id>".
        static List<string> RecordsOf(string albumFolder,string name) {
            string basis="p-"+GalleryStore.KeyOf(name);
            if(!Directory.Exists(albumFolder))return new List<string>();
            return Directory.GetDirectories(albumFolder,basis+"*").Where(d=>{
                string n=Path.GetFileName(d);return n.Length==basis.Length || n[basis.Length]=='-';}).ToList();
        }

        // Renames a file where it is, with its covers if LoveMyShelf knows the folder.
        static string RenameInPlace(GalleryStore store,string path,string newName) {
            string directory=Path.GetDirectoryName(path),old=Path.GetFileName(path);
            string album=store.AlbumFolderOf(directory);
            if(File.Exists(Path.Combine(album,"source.txt")))
                return Renamer.Rename(store,new Photo {Folder=Path.Combine(album,"p-"+GalleryStore.KeyOf(old)),Name=old},newName);
            string target=Path.Combine(directory,newName);
            File.Move(path,target);
            return target;
        }

        // ask: called with a file name when the target already has one; returns the choice and whether it applies to the rest.
        internal static MoveReport Run(GalleryStore store,IList<string> originals,string targetSource,string targetAlbum,bool copy,
                Func<string,Tuple<ConflictChoice,bool>> ask,Action<string> recycle) {
            return RunDetailed(store,originals,targetSource,targetAlbum,copy,"",info=>ask(info.Name),recycle);
        }
        // The same, but the question gets both files' places and how many same-name files this move has.
        internal static MoveReport RunDetailed(GalleryStore store,IList<string> originals,string targetSource,string targetAlbum,bool copy,
                string folderName,Func<ConflictInfo,Tuple<ConflictChoice,bool>> ask,Action<string> recycle) {
            var report=new MoveReport();
            ConflictChoice? always=null;
            int total=originals.Count(f=>{
                try {return File.Exists(f) && !SameFolder(Path.GetDirectoryName(f),targetSource) && File.Exists(Path.Combine(targetSource,Path.GetFileName(f)));}
                catch(Exception) {return false;}
            }),number=0;
            foreach(string from in originals) {
                string name=Path.GetFileName(from);
                try {
                    if(!File.Exists(from)){report.Failed.Add(name+" — "+L.T("The file is no longer there."));continue;}
                    string directory=Path.GetDirectoryName(from);
                    if(SameFolder(directory,targetSource)){report.AlreadyThere++;continue;}
                    string to=Path.Combine(targetSource,name);
                    bool conflict=File.Exists(to) || Directory.Exists(to);
                    var choice=ConflictChoice.Replace;
                    if(conflict) {
                        if(Directory.Exists(to)){report.Failed.Add(name+" — "+L.T("There is a folder with that name."));continue;}
                        if(always.HasValue){choice=always.Value;number++;}
                        else {
                            var answer=ask(new ConflictInfo {Name=name,From=from,To=to,FolderName=folderName,Number=++number,Total=Math.Max(total,number),Copy=copy});choice=answer.Item1;
                            if(answer.Item2)always=choice;
                        }
                        if(choice==ConflictChoice.Skip){report.Skipped++;continue;}
                    }
                    bool replace=conflict && choice==ConflictChoice.Replace;
                    string finalName=conflict && choice==ConflictChoice.KeepBoth?UniqueName(targetSource,name,directory):name;
                    string destination=Path.Combine(targetSource,finalName);
                    if(copy) {
                        if(replace) {
                            // Copy under a hidden temporary name first; the old file goes only once the copy is there.
                            string landing=Landing(targetSource,name);
                            File.Copy(from,landing,false);
                            try {PutInPlace(landing,to,recycle);}
                            catch(Exception) {try {File.Delete(landing);} catch(Exception) {}throw;}
                        } else File.Copy(from,destination,false);
                        report.Copied++;continue;
                    }
                    string current=from;
                    if(finalName!=name)current=RenameInPlace(store,from,finalName);
                    try {
                        MoveOne(store,current,targetSource,targetAlbum,finalName,replace,recycle);
                        report.Moved.Add(Tuple.Create(from,destination));
                    } catch(Exception) {
                        if(finalName!=name) {try {RenameInPlace(store,current,name);} catch(Exception) {}}
                        throw;
                    }
                } catch(Exception ex) {report.Failed.Add(name+" — "+ex.Message);}
            }
            return report;
        }

        static void MoveOne(GalleryStore store,string current,string targetSource,string targetAlbum,string name,bool replace,Action<string> recycle) {
            string sourceAlbum=store.AlbumFolderOf(Path.GetDirectoryName(current));
            string to=Path.Combine(targetSource,name);
            var records=RecordsOf(sourceAlbum,name);
            if(replace) {
                // Nothing of the file already there is touched until the moved file has fully arrived:
                // it lands under a hidden temporary name first (across drives this is the slow, fallible copy).
                string landing=Landing(targetSource,name);
                File.Move(current,landing);
                try {PutInPlace(landing,to,recycle);}
                catch(Exception) {try {File.Move(landing,current);} catch(Exception) {}throw;}
                // The replaced file is in the Recycle Bin now; its own records no longer describe anything.
                foreach(string old in RecordsOf(targetAlbum,name))
                    try {lock(SharedCoverFiles.MetadataLock)Directory.Delete(old,true);} catch(Exception) {}
            } else File.Move(current,to);
            var moved=new List<Tuple<string,string>>();
            try {
                lock(SharedCoverFiles.MetadataLock) {
                    foreach(string record in records) {
                        string destination=Path.Combine(targetAlbum,Path.GetFileName(record));
                        if(Directory.Exists(destination))throw new IOException(L.T("Love My Shelf still has records for an older file with that name. Press F5 and try again, or pick another name."));
                        Directory.Move(record,destination);
                        moved.Add(Tuple.Create(record,destination));
                    }
                }
            } catch(Exception) {
                for(int i=moved.Count-1;i>=0;i--) {try {Directory.Move(moved[i].Item2,moved[i].Item1);} catch(Exception) {}}
                try {File.Move(to,current);} catch(Exception) {}
                throw;
            }
            // Point the remembered location of every cover record at the file's new place.
            foreach(var pair in moved) {
                try {OriginalLocator.Remember(GalleryStore.Read(pair.Item2,to),to);} catch(Exception) {}
            }
        }

        // A hidden temporary name in the target folder, for a file that will replace another.
        static string Landing(string folder,string name) {
            return Path.Combine(folder,"~LoveMyShelf-"+Guid.NewGuid().ToString("N").Substring(0,12)+Path.GetExtension(name));
        }
        // The file at landing takes the place of the one at target: the old one goes to the Recycle Bin, then the rename.
        // If the old one can't be moved to the Recycle Bin, nothing changes and the move of this file fails.
        static void PutInPlace(string landing,string target,Action<string> recycle) {
            try {File.SetAttributes(landing,File.GetAttributes(landing)|FileAttributes.Hidden);} catch(Exception) {}
            recycle(target);
            if(File.Exists(target))throw new IOException(L.T("The file already there couldn't be moved to the Recycle Bin."));
            File.Move(landing,target);
            try {File.SetAttributes(target,File.GetAttributes(target)&~FileAttributes.Hidden);} catch(Exception) {}
        }

        // Puts files back where they came from. A file renamed to keep both gets its old name back when that name is free.
        internal static MoveReport Undo(GalleryStore store,IList<Tuple<string,string>> moved,Func<string,Tuple<ConflictChoice,bool>> ask,Action<string> recycle) {
            return UndoDetailed(store,moved,"",info=>ask(info.Name),recycle);
        }
        internal static MoveReport UndoDetailed(GalleryStore store,IList<Tuple<string,string>> moved,string folderName,Func<ConflictInfo,Tuple<ConflictChoice,bool>> ask,Action<string> recycle) {
            var total=new MoveReport();
            foreach(var pair in moved) {
                string back=Path.GetDirectoryName(pair.Item1);
                string album;
                try {album=store.OpenFolder(back).Folder;} catch(Exception ex) {total.Failed.Add(Path.GetFileName(pair.Item1)+" — "+ex.Message);continue;}
                var report=RunDetailed(store,new[]{pair.Item2},back,album,false,folderName,ask,recycle);
                total.Skipped+=report.Skipped;total.AlreadyThere+=report.AlreadyThere;total.Failed.AddRange(report.Failed);
                foreach(var done in report.Moved) {
                    total.Moved.Add(done);
                    string wanted=Path.GetFileName(pair.Item1);
                    if(!String.Equals(Path.GetFileName(done.Item2),wanted,StringComparison.OrdinalIgnoreCase) && !File.Exists(pair.Item1)) {
                        try {RenameInPlace(store,done.Item2,wanted);} catch(Exception) {}
                    }
                }
            }
            return total;
        }
    }

    // Facts about one of the two same-name files, read in the background.
    internal sealed class FileFacts {
        public string Path,Folder;
        public long Bytes;
        public DateTime Modified;
        public int Width,Height;
        public double DurationMs;
        public BitmapSource Picture;
        public bool Missing;
        public long Pixels {get {return (long)Width*Height;}}
        internal static FileFacts Read(string path,int maxSide,CancellationToken token) {
            var facts=new FileFacts {Path=path};
            try {
                var info=new FileInfo(path);
                if(!info.Exists){facts.Missing=true;return facts;}
                facts.Folder=info.DirectoryName;facts.Bytes=info.Length;facts.Modified=info.LastWriteTime;
                if(VideoFrames.Supports(path)) {
                    try {
                        var size=VideoFrames.ReadOrientation(path,token);
                        int w=size.Width,h=size.Height;if(Math.Abs(size.Angle%180)==90){int t=w;w=h;h=t;}
                        facts.Width=w;facts.Height=h;
                    } catch(OperationCanceledException) {throw;} catch(Exception) {}
                    try {facts.DurationMs=VideoFrames.DurationMs(path,token);} catch(OperationCanceledException) {throw;} catch(Exception) {}
                    try {facts.Picture=VideoFrames.AtTime(path,0,0,maxSide,token);} catch(OperationCanceledException) {throw;} catch(Exception) {}
                } else {
                    try {
                        using(var stream=File.OpenRead(path)) {
                            var frame=BitmapDecoder.Create(stream,BitmapCreateOptions.DelayCreation,BitmapCacheOption.None).Frames[0];
                            facts.Width=frame.PixelWidth;facts.Height=frame.PixelHeight;
                        }
                    } catch(Exception) {}
                    try {facts.Picture=Pictures.Load(path,maxSide);} catch(Exception) {}
                }
            } catch(OperationCanceledException) {throw;} catch(Exception) {facts.Missing=true;}
            return facts;
        }
        // True when both files hold exactly the same bytes.
        internal static bool SameBytes(string a,string b,CancellationToken token) {
            try {
                if(new FileInfo(a).Length!=new FileInfo(b).Length)return false;
                using(var x=new FileStream(a,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
                using(var y=new FileStream(b,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)) {
                    var left=new byte[1<<20];var right=new byte[1<<20];
                    while(true) {
                        token.ThrowIfCancellationRequested();
                        int n=ReadFully(x,left),m=ReadFully(y,right);
                        if(n!=m)return false;
                        if(n==0)return true;
                        for(int i=0;i<n;i++)if(left[i]!=right[i])return false;
                    }
                }
            } catch(OperationCanceledException) {throw;} catch(Exception) {return false;}
        }
        static int ReadFully(Stream stream,byte[] buffer) {
            int total=0;
            while(total<buffer.Length) {
                int n=stream.Read(buffer,total,buffer.Length-total);
                if(n==0)break;
                total+=n;
            }
            return total;
        }
    }

    // A picture shown large, from the same-name question. A click or Esc closes it.
    internal sealed class LargePictureWindow : Window {
        internal LargePictureWindow(Window owner,string path) {
            UI.WindowSetup(this,System.IO.Path.GetFileName(path));Owner=owner;ShowInTaskbar=false;
            WindowStartupLocation=WindowStartupLocation.CenterOwner;
            var area=SystemParameters.WorkArea;
            Width=Math.Min(1000,area.Width*0.8);Height=Math.Min(900,area.Height*0.85);
            var root=new DockPanel();root.Children.Add(new WindowTitleBar(this));
            var image=new Image {Stretch=Stretch.Uniform,Margin=new Thickness(10),Cursor=Cursors.Hand};
            RenderOptions.SetBitmapScalingMode(image,BitmapScalingMode.HighQuality);
            var note=UI.Text(L.T("Loading…"),14,true);note.HorizontalAlignment=HorizontalAlignment.Center;note.VerticalAlignment=VerticalAlignment.Center;
            var host=new Grid();host.Children.Add(note);host.Children.Add(image);root.Children.Add(host);Content=root;
            MouseLeftButtonUp+=delegate {Close();};
            PreviewKeyDown+=(s,e)=>{if(e.Key==Key.Escape){e.Handled=true;Close();}};
            Loaded+=async delegate {
                BitmapSource picture=null;
                try {
                    picture=await Task.Run(()=>VideoFrames.Supports(path)?VideoFrames.AtTime(path,0,0,1800,CancellationToken.None):Pictures.Load(path,1800));
                } catch(Exception) {}
                if(picture!=null){image.Source=picture;note.Visibility=Visibility.Collapsed;}
                else note.Text=L.T("This picture can't be shown.");
            };
        }
    }

    // "There is already a file named …": the file being moved and the one already there, side by side,
    // so the person can see which to keep. Replaced files go to the Recycle Bin.
    internal sealed class ConflictWindow : Window {
        internal ConflictChoice Choice=ConflictChoice.Skip;
        internal bool ApplyToAll;
        readonly CancellationTokenSource closing=new CancellationTokenSource();
        readonly ConflictInfo info;
        // Filled in once both files have been read; the checks look at these.
        internal readonly List<string> Facts=new List<string>();
        internal bool Identical;
        internal Task Ready;
        readonly Border[] picture=new Border[2];
        readonly StackPanel[] details=new StackPanel[2];
        readonly TextBlock same=UI.Text("",14);
        internal ConflictWindow(Window owner,ConflictInfo info) {
            this.info=info;
            UI.WindowSetup(this,L.T("Same name"));Owner=owner;ShowInTaskbar=false;
            WindowStartupLocation=WindowStartupLocation.CenterOwner;ResizeMode=ResizeMode.NoResize;
            Width=Math.Min(780,SystemParameters.WorkArea.Width);SizeToContent=SizeToContent.Height;
            var root=new DockPanel();root.Children.Add(new WindowTitleBar(this));
            var page=new StackPanel {Margin=new Thickness(22,16,22,20)};root.Children.Add(page);Content=root;
            var title=UI.Text(NoticeLook.KeepWords(L.F("“{0}” is already in “{1}”.",info.Name,info.FolderName)),15);
            title.TextWrapping=TextWrapping.Wrap;page.Children.Add(title);
            if(info.Total>1) {
                var counter=UI.Text(L.F("{0:N0} of {1:N0} files with the same name",info.Number,info.Total),12,true);
                counter.Margin=new Thickness(0,4,0,0);page.Children.Add(counter);
            }
            var cards=new Grid {Margin=new Thickness(0,12,0,0)};
            cards.ColumnDefinitions.Add(new ColumnDefinition());
            cards.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(12)});
            cards.ColumnDefinitions.Add(new ColumnDefinition());
            var paths=new[]{info.From,info.To};
            var headings=new[]{L.T(info.Copy?"The file being copied":"The file being moved"),L.T("The file already there")};
            for(int side=0;side<2;side++) {
                var card=Card(side,headings[side],paths[side]);
                Grid.SetColumn(card,side*2);cards.Children.Add(card);
            }
            page.Children.Add(cards);
            same.TextWrapping=TextWrapping.Wrap;same.Margin=new Thickness(0,10,0,0);same.FontWeight=FontWeights.SemiBold;same.Visibility=Visibility.Collapsed;
            page.Children.Add(same);
            var hint=UI.Text(NoticeLook.KeepWords(L.T("Replace puts the file that is there into the Recycle Bin first. Keep both gives this one a number after its name.")),12,true);
            hint.TextWrapping=TextWrapping.Wrap;hint.Margin=new Thickness(0,10,0,0);page.Children.Add(hint);
            CheckBox all=null;
            if(info.Total>info.Number) {
                all=new CheckBox {Content=L.F("Do the same for the other {0:N0} too, without looking at them",info.Total-info.Number),Margin=new Thickness(0,12,0,0)};
                all.SetResourceReference(Control.ForegroundProperty,"Text");page.Children.Add(all);
            }
            var actions=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,14,0,0)};
            Action<string,ConflictChoice,bool> add=(label,choice,primary)=>{
                var button=UI.Btn(label,()=>{Choice=choice;ApplyToAll=all!=null && all.IsChecked==true;Close();},primary);
                button.FontSize=14;button.MinHeight=32;button.Margin=new Thickness(6,0,0,0);
                actions.Children.Add(button);
            };
            add(L.T(info.Copy?"Replace with the copied file":"Replace with the moved file"),ConflictChoice.Replace,false);
            add(L.T("Keep the file that is there"),ConflictChoice.Skip,false);
            add(L.T("Keep both"),ConflictChoice.KeepBoth,true);
            page.Children.Add(actions);
            PreviewKeyDown+=(s,e)=>{if(e.Key==Key.Escape){e.Handled=true;Close();}}; // Esc = keep the one that is there
            Closed+=delegate {closing.Cancel();};
            Loaded+=delegate {Ready=Compare();};
        }
        FrameworkElement Card(int side,string heading,string path) {
            var card=new Border {BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(8),Padding=new Thickness(12)};
            card.SetResourceReference(Border.BackgroundProperty,"Panel");card.SetResourceReference(Border.BorderBrushProperty,"Line");
            var stack=new StackPanel();card.Child=stack;
            var head=UI.Text(heading,13);head.FontWeight=FontWeights.SemiBold;stack.Children.Add(head);
            var frame=new Border {Height=220,Margin=new Thickness(0,8,0,8),CornerRadius=new CornerRadius(6),ClipToBounds=true,Cursor=Cursors.Hand,
                ToolTip=L.T("Click to see it large")};
            frame.SetResourceReference(Border.BackgroundProperty,"TileBand");
            frame.Child=UI.Text(L.T("Loading…"),12,true);((TextBlock)frame.Child).HorizontalAlignment=HorizontalAlignment.Center;
            ((TextBlock)frame.Child).VerticalAlignment=VerticalAlignment.Center;
            frame.MouseLeftButtonUp+=delegate {
                if(File.Exists(path))new LargePictureWindow(this,path).ShowDialog();
            };
            picture[side]=frame;stack.Children.Add(frame);
            var rows=new StackPanel();details[side]=rows;stack.Children.Add(rows);
            return card;
        }
        async Task Compare() {
            try {
                var token=closing.Token;
                var pair=await Task.Run(()=>new[]{FileFacts.Read(info.From,440,token),FileFacts.Read(info.To,440,token)},token);
                if(token.IsCancellationRequested)return;
                for(int side=0;side<2;side++)Show(side,pair[side],pair[1-side]);
                if(pair[0].Missing || pair[1].Missing)return;
                bool identical=await Task.Run(()=>FileFacts.SameBytes(info.From,info.To,token),token);
                if(token.IsCancellationRequested || !identical)return;
                Identical=true;
                same.Text=L.T("These are exactly the same file.");same.Visibility=Visibility.Visible;
                Facts.Add("same");
            } catch(OperationCanceledException) {}
            catch(Exception) {}
        }
        static string Size(long bytes) {
            if(bytes>=1024L*1024*1024)return (bytes/1073741824.0).ToString("0.##")+" GB";
            if(bytes>=1024*1024)return (bytes/1048576.0).ToString("0.#")+" MB";
            return Math.Max(1,(bytes+1023)/1024).ToString("N0")+" KB";
        }
        void Show(int side,FileFacts mine,FileFacts other) {
            var frame=picture[side];
            if(mine.Picture!=null) {
                var image=new Image {Source=mine.Picture,Stretch=Stretch.Uniform};
                RenderOptions.SetBitmapScalingMode(image,BitmapScalingMode.HighQuality);
                frame.Child=image;
            } else ((TextBlock)frame.Child).Text=L.T("This picture can't be shown.");
            var rows=details[side];rows.Children.Clear();
            var name=UI.Text(NoticeLook.KeepWords(System.IO.Path.GetFileName(mine.Path)),13);name.TextWrapping=TextWrapping.Wrap;rows.Children.Add(name);
            if(mine.Missing){rows.Children.Add(UI.Text(L.T("The file is no longer there."),12,true));return;}
            bool newer=mine.Modified>other.Modified.AddSeconds(1),larger=mine.Bytes>other.Bytes,sharper=mine.Pixels>other.Pixels && other.Pixels>0;
            Row(rows,L.T("Size"),Size(mine.Bytes),larger?L.T("Larger"):null);
            Row(rows,L.T("Date modified"),mine.Modified.ToString("g"),newer?L.T("Newer"):null);
            if(mine.Width>0)Row(rows,L.T("Resolution"),mine.Width+" × "+mine.Height+" px",sharper?L.T("Sharper"):null);
            if(mine.DurationMs>0)Row(rows,L.T("Length"),Viewer.Stamp(mine.DurationMs),null);
            Row(rows,L.T("Location"),mine.Folder,null);
            Facts.Add(side+":"+System.IO.Path.GetFileName(mine.Path)+(larger?":larger":"")+(newer?":newer":"")+(sharper?":sharper":""));
        }
        static void Row(StackPanel rows,string label,string value,string badge) {
            var line=new DockPanel {Margin=new Thickness(0,3,0,0),MinHeight=22};
            var name=UI.Text(label,12,true);name.Width=78;name.VerticalAlignment=VerticalAlignment.Top;DockPanel.SetDock(name,Dock.Left);line.Children.Add(name);
            var text=UI.Text(value,12);text.TextWrapping=TextWrapping.Wrap;text.ToolTip=value;
            var cell=new WrapPanel();cell.Children.Add(text);
            if(badge!=null) {
                var pill=new Border {CornerRadius=new CornerRadius(8),Padding=new Thickness(7,0,7,1),Margin=new Thickness(6,0,0,0),BorderThickness=new Thickness(1)};
                pill.SetResourceReference(Border.BorderBrushProperty,"Accent");
                var mark=UI.Text(badge,11);mark.SetResourceReference(TextBlock.ForegroundProperty,"Accent");pill.Child=mark;cell.Children.Add(pill);
            }
            line.Children.Add(cell);rows.Children.Add(line);
        }
    }
}
