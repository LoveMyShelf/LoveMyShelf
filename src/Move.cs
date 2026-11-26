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
using System.Xml.Linq;

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
            throw new IOException(L.T("Couldn't find a name that can be used."));
        }

        // The record folders of one original: "p-<key>" and its extra covers "p-<key>-<id>".
        internal static List<string> RecordsOf(string albumFolder,string name) {
            string basis="p-"+GalleryStore.KeyOf(name);
            if(!Directory.Exists(albumFolder))return new List<string>();
            return Directory.GetDirectories(albumFolder,basis+"*").Where(d=>{
                string n=Path.GetFileName(d);return n.Length==basis.Length || n[basis.Length]=='-';}).ToList();
        }

        // Renames a file where it is, with its covers if LoveMyShelf knows the folder.
        internal static string RenameInPlace(GalleryStore store,string path,string newName) {
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
            TileSound.Stop(); // a cover whose sound is playing would be "in use"
            var report=new MoveReport();
            ConflictChoice? always=null;
            int total=originals.Count(f=>{
                try {return File.Exists(f) && !SameFolder(Path.GetDirectoryName(f),targetSource) && File.Exists(Path.Combine(targetSource,Path.GetFileName(f)));}
                catch(Exception) {return false;}
            }),number=0;
            foreach(string from in originals) {
                string name=Path.GetFileName(from);
                try {
                    if(!File.Exists(from)){report.Failed.Add(name+" — "+L.T("The file is not in that place."));continue;}
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
                            catch(Exception) {try {File.Delete(landing);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}throw;}
                            // The file that was there is in the Recycle Bin now; its tags don't describe the copy.
                            Tags.Forget(targetAlbum,new[]{name});
                        } else File.Copy(from,destination,false);
                        report.Copied++;continue;
                    }
                    string current=from;
                    if(finalName!=name)current=RenameInPlace(store,from,finalName);
                    try {
                        MoveOne(store,current,targetSource,targetAlbum,finalName,replace,recycle);
                        report.Moved.Add(Tuple.Create(from,destination));
                    } catch(Exception) {
                        if(finalName!=name) {try {RenameInPlace(store,current,name);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}}
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
                catch(Exception) {try {File.Move(landing,current);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}throw;}
                // The replaced file is in the Recycle Bin now; its own records no longer describe anything.
                foreach(string old in RecordsOf(targetAlbum,name))
                    try {lock(SharedCoverFiles.MetadataLock)Directory.Delete(old,true);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}
            } else File.Move(current,to);
            var moved=new List<Tuple<string,string>>();
            try {
                lock(SharedCoverFiles.MetadataLock) {
                    foreach(string record in records) {
                        string destination=Path.Combine(targetAlbum,Path.GetFileName(record));
                        if(Directory.Exists(destination))throw new IOException(L.T("Records of an older file with that name are still left. Press F5 and try again, or use another name."));
                        Directory.Move(record,destination);
                        moved.Add(Tuple.Create(record,destination));
                    }
                }
            } catch(Exception) {
                for(int i=moved.Count-1;i>=0;i--) {try {Directory.Move(moved[i].Item2,moved[i].Item1);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}}
                try {File.Move(to,current);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}
                throw;
            }
            // A tagged file's tags need the target folder's record folder, even when that folder has no covers yet.
            if(Tags.Of(sourceAlbum,name).Count>0 && !Directory.Exists(targetAlbum)) {try {store.OpenSubfolder(targetSource);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}}
            Tags.Moved(sourceAlbum,name,targetAlbum,name,replace);
            // Point the remembered location of every cover record at the file's new place.
            foreach(var pair in moved) {
                try {OriginalLocator.Remember(GalleryStore.Read(pair.Item2,to),to);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}
            }
        }

        // ---------- Folders in the file queue ----------
        // Turns what was chosen (files and folders) into the queue's list of single files.
        // A folder that moves within its own drive to a place where no folder has its name goes in one step (Whole);
        // any other folder is walked, and each file inside goes like any file, into the same folder under the target.
        internal static void Expand(GalleryStore store,IList<string> paths,string targetSource,string targetAlbum,TransferJob job,List<TransferItem> items) {
            foreach(string path in paths) {
                string shown=Path.GetFileName(path.TrimEnd('\\'));
                try {
                    if(File.Exists(path)){items.Add(new TransferItem {From=path,TargetSource=targetSource,TargetAlbum=targetAlbum});continue;}
                    if(!Directory.Exists(path))continue;
                    string from=Path.GetFullPath(path).TrimEnd('\\'),name=Path.GetFileName(from),to=Path.Combine(targetSource,name);
                    // A link to a folder somewhere else is never opened up: the files behind it belong to that other folder.
                    if(GalleryStore.IsFolderLink(new DirectoryInfo(from))){job.Rejected.Add(name+" — "+LinkedFolder());continue;}
                    if(!Renamer.CanRenameFolder(from)){job.Rejected.Add(from+" — "+L.P("A whole drive can't be sent as a folder.","드라이브를 통째로 폴더처럼 보낼 수는 없어요."));continue;}
                    if(Renamer.IsAt(Path.GetFullPath(targetSource),from)){job.Rejected.Add(name+" — "+L.P("A folder can't be sent into itself.","폴더를 자기 안으로 보낼 수는 없어요."));continue;}
                    string parent=Path.GetDirectoryName(from);
                    if(SameFolder(parent,targetSource) || SamePlace(parent,targetSource)){lock(job.Report)job.Report.AlreadyThere++;continue;}
                    if(File.Exists(to)){job.Rejected.Add(name+" — "+L.P("There is a file with that name.","그 이름의 파일이 있어요."));continue;}
                    if(Renamer.IsAt(store.Root,from)){job.Rejected.Add(name+" — "+L.P("LoveMyShelf keeps its own records in this folder.","러브마이셀프가 기록을 보관하는 폴더예요."));continue;}
                    job.FolderCount++;
                    if(!job.Copy && SameDrive(from,targetSource) && !Directory.Exists(to)) {
                        items.Add(new TransferItem {From=from,TargetSource=targetSource,TargetAlbum=targetAlbum,Whole=true});continue;
                    }
                    job.Roots.Add(Tuple.Create(from,to));
                    Walk(store,new DirectoryInfo(from),to,job,items);
                } catch(Exception ex) {job.Rejected.Add(shown+" — "+ex.Message);}
            }
        }
        static void Walk(GalleryStore store,DirectoryInfo folder,string to,TransferJob job,List<TransferItem> items) {
            bool any=false;
            string album=store.AlbumFolderOf(to);
            foreach(var file in folder.GetFiles()) {
                any=true;
                items.Add(new TransferItem {From=file.FullName,TargetSource=to,TargetAlbum=album,MakeFolder=true});
            }
            foreach(var inner in folder.GetDirectories()) {
                any=true;
                // A link to a folder somewhere else is not followed: what is behind it is not part of this folder.
                // (OneDrive folders carry a similar mark but are real folders; IsFolderLink tells them apart.)
                if(GalleryStore.IsFolderLink(inner)){job.Rejected.Add(inner.FullName+" — "+LinkedFolder());continue;}
                Walk(store,inner,Path.Combine(to,inner.Name),job,items);
            }
            if(!any)job.EmptyDirs.Add(Tuple.Create(folder.FullName,to));
        }
        static string LinkedFolder() {
            return L.P("This is a link to another folder. It isn't followed and stays where it is.","다른 폴더로 이어지는 연결 폴더예요. 따라가지 않고 그 자리에 둬요.");
        }
        // After the files of a folder job. whole: the job ran to its end (it was not cancelled).
        // Folders that were empty to begin with are made at the new place (only when the job ran to its end).
        // For a move, a folder goes only if this job took files out of it (or it was one of those empty ones) and it
        // holds nothing now; then the folders above it, up to the folder that was sent. Nothing else is touched:
        // not a folder the job never reached, not one that was at the new place before (OnlyThese, for an undo).
        // Folders that were tabs stay tabs at the new place, and covers that moved as plain files are known by
        // their new paths.
        internal static void FinishFolders(TransferJob job,bool whole) {
            var store=job.Store;
            if(whole)foreach(var empty in job.EmptyDirs) {try {MakeFolders(job,empty.Item2);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}}
            if(job.Copy)return;
            var candidates=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Action<string> rise=folder=>{
                foreach(var root in job.Roots) {
                    if(!Renamer.IsAt(folder,root.Item1))continue;
                    for(string f=folder.TrimEnd('\\');f!=null && Renamer.IsAt(f,root.Item1);f=Path.GetDirectoryName(f))candidates.Add(f);
                    break;
                }
            };
            List<string> touched;lock(job.Touched)touched=job.Touched.ToList();
            foreach(string folder in touched)rise(folder);
            if(whole)foreach(var empty in job.EmptyDirs)rise(empty.Item1);
            foreach(string folder in candidates.OrderByDescending(f=>f.Length)) {
                try {
                    if(job.OnlyThese!=null && !job.OnlyThese.Contains(folder))continue;
                    if(!Directory.Exists(folder) || Directory.EnumerateFileSystemEntries(folder).Any())continue;
                    if(GalleryStore.IsFolderLink(new DirectoryInfo(folder)))continue;
                    var root=job.Roots.First(r=>Renamer.IsAt(folder,r.Item1));
                    string twin=Renamer.Moved(folder,root.Item1,root.Item2);
                    MakeFolders(job,twin);
                    Directory.Delete(folder);
                    Followed(store,folder,twin);
                } catch(Exception quiet) {ErrorLog.Quiet(quiet);}
            }
            List<Tuple<string,string>> moved;lock(job.Report)moved=job.Report.Moved.ToList();
            try {RepointCovers(store,moved);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}
        }
        // Makes a folder and remembers every folder that was not there before, so an undo can take exactly those away.
        internal static void MakeFolders(TransferJob job,string folder) {
            var missing=new List<string>();
            for(string f=folder.TrimEnd('\\');!String.IsNullOrEmpty(f) && !Directory.Exists(f);f=Path.GetDirectoryName(f))missing.Add(f);
            if(missing.Count==0)return;
            Directory.CreateDirectory(folder);
            lock(job.Created)job.Created.AddRange(missing);
            TransferQueue.LogCreated(job,missing);
        }
        // The folder is gone from its old place: a folder that was on the shelf (a tab) is on the shelf at its new
        // place, and its old record folder goes once nothing is kept in it (no cover records, no tags). If the new
        // tab couldn't be made, the old records stay.
        static void Followed(GalleryStore store,string from,string to) {
            string album=store.AlbumFolderOf(from);
            if(!Directory.Exists(album))return;
            lock(SharedCoverFiles.MetadataLock) {
                bool tab=File.Exists(Path.Combine(album,"album.txt")) && !File.Exists(Path.Combine(album,GalleryStore.HiddenTabFile));
                if(tab) {try {store.OpenFolder(to);} catch(Exception) {return;}}
                if(Directory.GetDirectories(album,"p-*").Length==0 && !File.Exists(Path.Combine(album,"tags.xml"))) {try {Directory.Delete(album,true);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}}
            }
        }
        // A cover saved beside its original, or in the cover folder inside the original's folder, moved as a plain
        // file. The records that name it are told where exactly that file went (moved: old path, new path).
        static void RepointCovers(GalleryStore store,List<Tuple<string,string>> moved) {
            var went=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            foreach(var pair in moved)went[pair.Item1]=pair.Item2;
            if(went.Count==0)return;
            var names=new[]{"coverFile","upscaleFile","motionFile"};
            lock(SharedCoverFiles.MetadataLock) {
                foreach(var album in store.Albums()) {
                    string[] records;
                    try {records=Directory.GetDirectories(album.Folder,"p-*");} catch(Exception) {continue;}
                    foreach(string record in records) {
                        string file=Path.Combine(record,"photo.xml");
                        try {
                            if(!File.Exists(file))continue;
                            var xml=XElement.Load(file);
                            bool changed=false;
                            foreach(string attribute in names) {
                                string value=(string)xml.Attribute(attribute),now;
                                if(String.IsNullOrEmpty(value) || !went.TryGetValue(value,out now))continue;
                                if(File.Exists(value) || !File.Exists(now))continue;
                                xml.SetAttributeValue(attribute,now);changed=true;
                            }
                            if(!changed)continue;
                            string part=Path.Combine(record,Guid.NewGuid().ToString("N")+".tmp");
                            try {xml.Save(part);File.Replace(part,file,null);}
                            finally {if(File.Exists(part))File.Delete(part);}
                        } catch(Exception quiet) {ErrorLog.Quiet(quiet);}
                    }
                }
            }
        }

        internal enum TransferResult { Moved, Copied, Skipped, AlreadyThere }
        internal static bool SameDrive(string a,string b) {
            try {return String.Equals(Path.GetPathRoot(Path.GetFullPath(a)),Path.GetPathRoot(Path.GetFullPath(b)),StringComparison.OrdinalIgnoreCase);}
            catch(Exception) {return false;}
        }
        static string AppearedLater() {
            return L.P("A file with the same name appeared in that folder after this was queued. Nothing was changed.","대기열에 넣은 뒤에 그 폴더에 같은 이름의 파일이 생겼어요. 아무것도 바꾸지 않았어요.");
        }
        // One file of a queued job (TransferQueue). Like one round of RunDetailed, but the answer to a same-name
        // question was given before the job started, and a copy to another drive goes through the queue's own
        // copier, which shows progress and can be paused or cancelled.
        // decided: null when no file with that name was there when the job was queued.
        // warning: the file arrived but something small was left undone (the original couldn't be deleted).
        internal static TransferResult TransferOne(GalleryStore store,string from,string targetSource,string targetAlbum,bool copy,
                ConflictChoice? decided,Tuple<long,DateTime> seen,Action<string> recycle,TransferCopier copier,out string destination,out string warning,out string renamed) {
            destination=null;warning=null;renamed=null;
            string name=Path.GetFileName(from);
            if(!File.Exists(from))throw new IOException(L.T("The file is not in that place."));
            string directory=Path.GetDirectoryName(from);
            if(SameFolder(directory,targetSource))return TransferResult.AlreadyThere;
            if(!Directory.Exists(targetSource))throw new IOException(L.P("The folder to send it to is not there.","보낼 폴더가 그 자리에 없어요."));
            // The same folder reached by another drive letter (a mapped or substituted drive): the file is already there.
            if(SamePlace(directory,targetSource))return TransferResult.AlreadyThere;
            string to=Path.Combine(targetSource,name);
            if(Directory.Exists(to))throw new IOException(L.T("There is a folder with that name."));
            bool conflict=File.Exists(to);
            var choice=ConflictChoice.Replace;
            if(conflict) {
                // Nobody was asked about this one: it appeared while the job waited. It is left alone and reported.
                if(!decided.HasValue)throw new IOException(AppearedLater());
                choice=decided.Value;
                if(choice==ConflictChoice.Skip)return TransferResult.Skipped;
            }
            bool replace=conflict && choice==ConflictChoice.Replace;
            if(replace && seen!=null) {
                // [Replace] was the answer for the file that was there then, not for another one that took its place since.
                var there=new FileInfo(to);
                if(there.Length!=seen.Item1 || there.LastWriteTimeUtc!=seen.Item2)
                    throw new IOException(L.P("The file with the same name in that folder changed after you answered. Nothing was changed.","답한 뒤에 그 폴더의 같은 이름 파일이 바뀌었어요. 아무것도 바꾸지 않았어요."));
            }
            string finalName=conflict && choice==ConflictChoice.KeepBoth?UniqueName(targetSource,name,directory):name;
            destination=Path.Combine(targetSource,finalName);
            if(copy) {
                // The copy lands under a hidden temporary name; nothing in the folder changes until it has fully arrived.
                string landing=copier.Copy(from,targetSource,from);
                try {
                    if(replace) {
                        PutInPlace(landing,to,recycle);
                        // The replaced file is in the Recycle Bin: its tags and cover records don't describe the copy.
                        Tags.Forget(targetAlbum,new[]{name});
                        foreach(string old in RecordsOf(targetAlbum,name))
                            try {lock(SharedCoverFiles.MetadataLock)Directory.Delete(old,true);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}
                    } else {
                        if(File.Exists(destination) || Directory.Exists(destination))throw new IOException(AppearedLater());
                        File.Move(landing,destination);
                    }
                } catch(Exception) {TransferQueue.DeleteTemp(landing);throw;}
                TransferQueue.ForgetTemp(landing);
                Arrived(destination,from);
                return TransferResult.Copied;
            }
            if(CoverWork.HoldsPath(from))throw new IOException(L.P("A cover is being made from this file right now.","지금 이 파일로 표지를 만들고 있어요."));
            string current=from;
            if(finalName!=name){current=RenameInPlace(store,from,finalName);copier.Renamed(current);}
            try {
                if(SameDrive(current,targetSource))MoveOne(store,current,targetSource,targetAlbum,finalName,replace,recycle);
                else warning=MoveAcross(store,current,from,targetSource,targetAlbum,finalName,replace,recycle,copier);
            } catch(Exception) {
                // The file gets its old name back. If even that can't be done (its drive came loose), the caller
                // is told the name it has now, so the job can go on with it later.
                if(finalName!=name) {
                    try {RenameInPlace(store,current,name);copier.NameBack(from);} catch(Exception) {renamed=current;}
                }
                throw;
            }
            // The original that couldn't be deleted is no longer followed by any record: it just gets its old name back.
            if(warning!=null && finalName!=name) {try {if(!File.Exists(from))File.Move(current,from);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}}
            return TransferResult.Moved;
        }
        // True when two folder paths lead to the very same folder (one of them through a mapped or substituted
        // drive letter). Found out by putting an empty marker file in one and looking for it in the other.
        static readonly Dictionary<string,bool> samePlaces=new Dictionary<string,bool>(StringComparer.OrdinalIgnoreCase);
        internal static bool SamePlace(string a,string b) {
            if(SameDrive(a,b))return SameFolder(a,b);
            string key=Plain(a)+"|"+Plain(b);
            lock(samePlaces) {
                bool known;
                if(samePlaces.TryGetValue(key,out known))return known;
            }
            bool same=false;
            string marker="~LoveMyShelf-same-"+Guid.NewGuid().ToString("N")+".tmp";
            string made=Path.Combine(b,marker);
            try {
                using(new FileStream(made,FileMode.CreateNew,FileAccess.Write,FileShare.None,1,FileOptions.DeleteOnClose)) {
                    try {File.SetAttributes(made,FileAttributes.Hidden|FileAttributes.Temporary);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}
                    same=File.Exists(Path.Combine(a,marker));
                }
            } catch(Exception) {return false;} // not known: asked again next time
            finally {try {if(File.Exists(made))File.Delete(made);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}}
            lock(samePlaces)samePlaces[key]=same;
            return same;
        }
        // For a file that had arrived when the last run was cut short: cover records and tags that had not
        // followed it yet follow it now.
        internal static void Recover(GalleryStore store,string from,string to,string targetAlbum) {
            string name=Path.GetFileName(from),sourceAlbum=store.AlbumFolderOf(Path.GetDirectoryName(from));
            if(!String.Equals(name,Path.GetFileName(to),StringComparison.OrdinalIgnoreCase))return;
            var records=RecordsOf(sourceAlbum,name);
            var tags=Tags.Of(sourceAlbum,name);
            if(records.Count==0 && tags.Count==0)return;
            if(!Directory.Exists(targetAlbum)) {try {store.OpenSubfolder(Path.GetDirectoryName(to));} catch(Exception quiet) {ErrorLog.Quiet(quiet);}}
            lock(SharedCoverFiles.MetadataLock) {
                foreach(string record in records) {
                    string destination=Path.Combine(targetAlbum,Path.GetFileName(record));
                    if(Directory.Exists(destination))continue;
                    try {
                        Directory.Move(record,destination);
                        try {OriginalLocator.Remember(GalleryStore.Read(destination,to),to);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}
                    } catch(Exception quiet) {ErrorLog.Quiet(quiet);}
                }
            }
            if(tags.Count>0)Tags.Moved(sourceAlbum,name,targetAlbum,name);
        }
        // The arrived file shows again (it was hidden while it landed) and keeps the original's read-only mark.
        static void Arrived(string path,string original) {
            try {
                var kept=FileAttributes.ReadOnly|FileAttributes.Hidden|FileAttributes.Archive;
                File.SetAttributes(path,(File.GetAttributes(path)&~kept)|(File.GetAttributes(original)&kept));
            } catch(Exception quiet) {ErrorLog.Quiet(quiet);}
        }
        // A move to another drive. The order keeps everything safe if the program stops in the middle:
        // copy under a temporary name → the file takes its place → the cover records and tags follow → only then
        // the original is deleted. Until that last step the original is whole and where it was.
        // Returns a sentence when the original couldn't be deleted at the end (the copy is complete), otherwise null.
        static string MoveAcross(GalleryStore store,string current,string key,string targetSource,string targetAlbum,string name,bool replace,
                Action<string> recycle,TransferCopier copier) {
            string sourceAlbum=store.AlbumFolderOf(Path.GetDirectoryName(current));
            string to=Path.Combine(targetSource,name);
            string landing=copier.Copy(current,targetSource,key);
            try {
                if(replace)PutInPlace(landing,to,recycle);
                else {
                    if(File.Exists(to) || Directory.Exists(to))throw new IOException(AppearedLater());
                    File.Move(landing,to);
                }
            } catch(Exception) {TransferQueue.DeleteTemp(landing);throw;}
            TransferQueue.ForgetTemp(landing);
            var moved=new List<Tuple<string,string>>();
            var aside=new List<Tuple<string,string>>();
            try {
                lock(SharedCoverFiles.MetadataLock) {
                    // Read only now: a cover made or deleted while the long copy ran is counted too.
                    var records=RecordsOf(sourceAlbum,name);
                    // The records of the file that was replaced step aside first, so they can come back if this fails.
                    if(replace)foreach(string old in RecordsOf(targetAlbum,name)) {
                        string away=Path.Combine(targetAlbum,"~replaced-"+Guid.NewGuid().ToString("N").Substring(0,8)+"-"+Path.GetFileName(old));
                        Directory.Move(old,away);
                        aside.Add(Tuple.Create(old,away));
                    }
                    foreach(string record in records) {
                        string destination=Path.Combine(targetAlbum,Path.GetFileName(record));
                        if(Directory.Exists(destination))throw new IOException(L.T("Records of an older file with that name are still left. Press F5 and try again, or use another name."));
                        Directory.Move(record,destination);
                        moved.Add(Tuple.Create(record,destination));
                    }
                }
            } catch(Exception) {
                lock(SharedCoverFiles.MetadataLock) {
                    for(int i=moved.Count-1;i>=0;i--) {try {Directory.Move(moved[i].Item2,moved[i].Item1);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}}
                    // The file those records were about is in the Recycle Bin; the new file must not inherit them.
                    foreach(var pair in aside) {try {Directory.Delete(pair.Item2,true);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}}
                }
                // The original is untouched. A copy that replaced nothing is taken away again, so nothing is doubled.
                if(!replace) {try {File.Delete(to);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}}
                else {Tags.Forget(targetAlbum,new[]{name});Arrived(to,current);}
                throw;
            }
            Arrived(to,current);
            foreach(var pair in aside) {try {lock(SharedCoverFiles.MetadataLock)Directory.Delete(pair.Item2,true);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}}
            var tags=Tags.Of(sourceAlbum,name);
            if(tags.Count>0 && !Directory.Exists(targetAlbum)) {try {store.OpenSubfolder(targetSource);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}}
            Tags.Moved(sourceAlbum,name,targetAlbum,name,replace);
            foreach(var pair in moved) {
                try {OriginalLocator.Remember(GalleryStore.Read(pair.Item2,to),to);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}
            }
            // Tags that couldn't be written at the new place are still with the original: it stays.
            if(tags.Count>0) {
                var arrived=Tags.Of(targetAlbum,name);
                if(!tags.All(t=>arrived.Contains(t,StringComparer.OrdinalIgnoreCase)))
                    return L.P("It was copied whole, but its tags couldn't be saved at the new place, so the original was left where it was.","복사는 다 됐는데 새 위치에 태그를 저장하지 못해서, 원본을 원래 자리에 남겨 뒀어요.");
            }
            // Last of all, the original goes, and only if it is still the file that was copied.
            var gone=OriginalLocator.DeleteIfSame(current,copier.CopiedLength,copier.CopiedStamp,copier.CopiedVolume,copier.CopiedId);
            if(gone==OriginalLocator.Gone.Deleted)return null;
            if(gone==OriginalLocator.Gone.Changed)
                return L.P("It was copied whole, but the original changed meanwhile, so it was not deleted.","복사는 다 됐는데 그사이 원본이 바뀌어서, 원본을 지우지 않았어요.");
            // It couldn't be looked at and deleted in one go (in use, perhaps): the way it was always done, which says why.
            try {
                var still=new FileInfo(current);
                bool other=still.Length!=copier.CopiedLength || still.LastWriteTimeUtc!=copier.CopiedStamp;
                if(!other && copier.CopiedId!=0) {
                    uint volume;ulong id;long birth,length;
                    if(OriginalLocator.Identity(current,out volume,out id,out birth,out length))other=volume!=copier.CopiedVolume || id!=copier.CopiedId;
                }
                if(other)
                    return L.P("It was copied whole, but the original changed meanwhile, so it was not deleted.","복사는 다 됐는데 그사이 원본이 바뀌어서, 원본을 지우지 않았어요.");
            } catch(Exception ex) {
                return L.P("It was copied whole, but the original couldn't be deleted and is still in its old place.","복사는 다 됐는데 원본을 지우지 못해서, 원본이 원래 자리에 그대로 남아 있어요.")+" ("+ex.Message+")";
            }
            try {
                var attributes=File.GetAttributes(current);
                if((attributes&FileAttributes.ReadOnly)!=0)File.SetAttributes(current,attributes&~FileAttributes.ReadOnly);
                File.Delete(current);
            } catch(Exception ex) {
                return L.P("It was copied whole, but the original couldn't be deleted and is still in its old place.","복사는 다 됐는데 원본을 지우지 못해서, 원본이 원래 자리에 그대로 남아 있어요.")+" ("+ex.Message+")";
            }
            return null;
        }

        // A hidden temporary name in the target folder, for a file that will replace another.
        internal static string Landing(string folder,string name) {
            return Path.Combine(folder,"~LoveMyShelf-"+Guid.NewGuid().ToString("N").Substring(0,12)+Path.GetExtension(name));
        }
        // The file at landing takes the place of the one at target: the old one goes to the Recycle Bin, then the rename.
        // If the old one can't be moved to the Recycle Bin, nothing changes and the move of this file fails.
        static void PutInPlace(string landing,string target,Action<string> recycle) {
            try {File.SetAttributes(landing,File.GetAttributes(landing)|FileAttributes.Hidden);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}
            recycle(target);
            if(File.Exists(target))throw new IOException(L.T("The file that was already there couldn't be moved to the Recycle Bin. Nothing was changed."));
            File.Move(landing,target);
            try {File.SetAttributes(target,File.GetAttributes(target)&~FileAttributes.Hidden);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}
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
                        try {RenameInPlace(store,done.Item2,wanted);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}
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
                    } catch(OperationCanceledException) {throw;} catch(Exception quiet) {ErrorLog.Quiet(quiet);}
                    try {facts.DurationMs=VideoFrames.DurationMs(path,token);} catch(OperationCanceledException) {throw;} catch(Exception quiet) {ErrorLog.Quiet(quiet);}
                    try {facts.Picture=VideoFrames.AtTime(path,0,0,maxSide,token);} catch(OperationCanceledException) {throw;} catch(Exception quiet) {ErrorLog.Quiet(quiet);}
                } else {
                    try {
                        using(var stream=File.OpenRead(path)) {
                            var frame=BitmapDecoder.Create(stream,BitmapCreateOptions.DelayCreation,BitmapCacheOption.None).Frames[0];
                            facts.Width=frame.PixelWidth;facts.Height=frame.PixelHeight;
                        }
                    } catch(Exception quiet) {ErrorLog.Quiet(quiet);}
                    try {facts.Picture=Pictures.Load(path,maxSide);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}
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
                } catch(Exception quiet) {ErrorLog.Quiet(quiet);}
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
            UI.WindowSetup(this,L.T("There is a file with the same name"));Owner=owner;ShowInTaskbar=false;
            WindowStartupLocation=WindowStartupLocation.CenterOwner;ResizeMode=ResizeMode.NoResize;
            Width=Math.Min(780,SystemParameters.WorkArea.Width);SizeToContent=SizeToContent.Height;
            var root=new DockPanel();root.Children.Add(new WindowTitleBar(this));
            var page=new StackPanel {Margin=new Thickness(22,16,22,20)};root.Children.Add(page);Content=root;
            var title=UI.Text(NoticeLook.KeepWords(L.F("The “{1}” folder already has a file named “{0}”.",info.Name,info.FolderName)),15);
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
            var headings=new[]{L.T(info.Copy?"The file being copied":"The file being moved"),L.T("The existing file")};
            for(int side=0;side<2;side++) {
                var card=Card(side,headings[side],paths[side]);
                Grid.SetColumn(card,side*2);cards.Children.Add(card);
            }
            page.Children.Add(cards);
            same.TextWrapping=TextWrapping.Wrap;same.Margin=new Thickness(0,10,0,0);same.FontWeight=FontWeights.SemiBold;same.Visibility=Visibility.Collapsed;
            page.Children.Add(same);
            var hint=UI.Text(NoticeLook.KeepWords(L.T("[Replace] sends the file that was in that folder to the Recycle Bin first. [Keep both] adds a number after the name of the file being moved.")),12,true);
            hint.TextWrapping=TextWrapping.Wrap;hint.Margin=new Thickness(0,10,0,0);page.Children.Add(hint);
            CheckBox all=null;
            if(info.Total>info.Number) {
                all=new CheckBox {Content=L.F("Do the same for the other {0:N0} too, without asking",info.Total-info.Number),Margin=new Thickness(0,12,0,0)};
                all.SetResourceReference(Control.ForegroundProperty,"Text");page.Children.Add(all);
            }
            var actions=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,14,0,0)};
            Action<string,ConflictChoice,bool> add=(label,choice,primary)=>{
                var button=UI.Btn(label,()=>{Choice=choice;ApplyToAll=all!=null && all.IsChecked==true;Close();},primary);
                button.FontSize=14;button.MinHeight=32;button.Margin=new Thickness(6,0,0,0);
                actions.Children.Add(button);
            };
            add(L.T(info.Copy?"Replace with the copied file":"Replace with the moved file"),ConflictChoice.Replace,false);
            add(L.T("Keep the existing file"),ConflictChoice.Skip,false);
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
                same.Text=L.T("These are exactly the same file. It makes no difference which one you keep.");same.Visibility=Visibility.Visible;
                Facts.Add("same");
            } catch(OperationCanceledException) {}
            catch(Exception quiet) {ErrorLog.Quiet(quiet);}
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
            if(mine.Missing){rows.Children.Add(UI.Text(L.T("The file is not in that place."),12,true));return;}
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
