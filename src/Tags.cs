using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace LoveMyShelf {
    // Tags belong to an original (never to one cover), and the original itself is never touched:
    // each folder's record folder keeps one "tags.xml" that lists its files' tags by file name.
    // Moving or renaming a file inside LoveMyShelf carries its tags along (see Moved).
    internal static class Tags {
        const string FileName="tags.xml";
        internal const int MaxLength=40;
        static readonly object Gate=new object();
        static readonly StringComparer Same=StringComparer.OrdinalIgnoreCase;
        static string root;
        // record folder of a folder -> file name -> tags
        static Dictionary<string,Dictionary<string,List<string>>> all;
        // record folder of a folder -> file name -> what Windows calls the file ("volume|id|birth"), so a tagged file
        // that was renamed or moved in File Explorer can be found again, the way a cover finds its original.
        static Dictionary<string,Dictionary<string,string>> marks;
        // Raised (on whatever thread made the change) when any tag was added, removed or renamed.
        internal static event Action Changed;

        [DllImport("shlwapi.dll",CharSet=CharSet.Unicode,ExactSpelling=true)]
        static extern int StrCmpLogicalW(string a,string b);

        internal static void Use(string storeRoot) {
            lock(Gate) {
                if(root!=null && Same.Equals(root,storeRoot))return;
                root=storeRoot;all=null;
            }
        }
        // Reads every folder's tags again (after record folders were moved as a whole).
        internal static void Reload() {lock(Gate)all=null;Raise();}
        static void Raise() {var changed=Changed;if(changed!=null)changed();}
        static Dictionary<string,Dictionary<string,List<string>>> All() {
            if(all!=null)return all;
            var read=new Dictionary<string,Dictionary<string,List<string>>>(Same);
            marks=new Dictionary<string,Dictionary<string,string>>(Same);
            if(root!=null && Directory.Exists(root)) {
                foreach(string folder in Directory.GetDirectories(root,"folder-*")) {
                    try {
                        var known=new Dictionary<string,string>(Same);
                        var files=ReadFile(Path.Combine(folder,FileName),known);
                        if(files.Count>0){read[folder]=files;marks[folder]=known;}
                    } catch(Exception quiet) {ErrorLog.Quiet(quiet);}
                }
            }
            return all=read;
        }
        static string MarkOf(string albumFolder,string name) {
            Dictionary<string,string> known;string mark;
            return marks!=null && marks.TryGetValue(albumFolder,out known) && known.TryGetValue(name,out mark)?mark:null;
        }
        static void SetMark(string albumFolder,string name,string mark) {
            Dictionary<string,string> known;
            if(!marks.TryGetValue(albumFolder,out known)) {
                if(mark==null)return;
                marks[albumFolder]=known=new Dictionary<string,string>(Same);
            }
            if(mark==null)known.Remove(name);else known[name]=mark;
        }
        // What Windows calls the file right now, or null when it can't be read (or isn't there).
        static string MarkNow(string albumFolder,string name) {
            try {
                string source=GalleryStore.SourceOfRecords(albumFolder);
                if(source==null)return null;
                uint volume;ulong id;long birth,length;
                if(!OriginalLocator.Identity(Path.Combine(source,name),out volume,out id,out birth,out length))return null;
                return volume+"|"+id+"|"+birth+"|"+length;
            } catch(Exception) {return null;}
        }
        static Dictionary<string,List<string>> ReadFile(string path,Dictionary<string,string> known) {
            var files=new Dictionary<string,List<string>>(Same);
            if(!File.Exists(path))return files;
            foreach(var file in XElement.Load(path).Elements("file")) {
                string name=(string)file.Attribute("name");
                if(String.IsNullOrEmpty(name))continue;
                var tags=new List<string>();
                foreach(var tag in file.Elements("tag")) {
                    string text=Clean(tag.Value);
                    if(text!=null && !tags.Contains(text,Same))tags.Add(text);
                }
                if(tags.Count>0) {
                    files[name]=tags;
                    string mark=(string)file.Attribute("file");
                    if(!String.IsNullOrEmpty(mark))known[name]=mark;
                }
            }
            return files;
        }
        static void Save(string albumFolder) {
            string path=Path.Combine(albumFolder,FileName);
            Dictionary<string,List<string>> files;
            if(!All().TryGetValue(albumFolder,out files) || files.Count==0) {
                All().Remove(albumFolder);marks.Remove(albumFolder);
                if(File.Exists(path))File.Delete(path);
                return;
            }
            // Forget what is known about files that have no tags any more.
            Dictionary<string,string> known;
            if(marks.TryGetValue(albumFolder,out known))foreach(string gone in known.Keys.Where(k=>!files.ContainsKey(k)).ToList())known.Remove(gone);
            if(!Directory.Exists(albumFolder))throw new DirectoryNotFoundException(L.T("The source folder could not be found."));
            var xml=new XElement("tags",files.OrderBy(f=>f.Key,Same).Select(f=>
                new XElement("file",new XAttribute("name",f.Key),MarkOf(albumFolder,f.Key)==null?null:new XAttribute("file",MarkOf(albumFolder,f.Key)),
                    f.Value.Select(t=>new XElement("tag",t)))));
            string part=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try {
                xml.Save(part);
                if(File.Exists(path))File.Replace(part,path,null);else File.Move(part,path);
            } finally {try {File.Delete(part);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}}
        }
        // A tag as it is kept: no "#" in front, one space between words, no line breaks. Null when nothing is left.
        internal static string Clean(string text) {
            if(text==null)return null;
            var parts=text.Split(new[]{' ','\t','\r','\n','　'},StringSplitOptions.RemoveEmptyEntries);
            string joined=String.Join(" ",parts).TrimStart('#').Trim();
            if(joined.Length>MaxLength)joined=joined.Substring(0,MaxLength).Trim();
            return joined.Length==0?null:joined;
        }
        internal static string AlbumOf(Photo photo) {return Path.GetDirectoryName(photo.LeadFolder??photo.Folder);}
        internal static string NameOf(Photo photo) {return Path.GetFileName(photo.Name);}
        internal static List<string> Of(Photo photo) {return Of(AlbumOf(photo),NameOf(photo));}
        internal static List<string> Of(string albumFolder,string name) {
            lock(Gate) {
                Dictionary<string,List<string>> files;List<string> tags;
                if(All().TryGetValue(albumFolder,out files) && files.TryGetValue(name,out tags))return new List<string>(tags);
                return new List<string>();
            }
        }
        internal static bool Any {get {lock(Gate)return All().Count>0;}}
        // Adds and removes tags on several originals at once; a tag that isn't named stays as it is on each of them.
        internal static void Change(IEnumerable<Photo> photos,IEnumerable<string> add,IEnumerable<string> remove) {
            var files=photos.Select(p=>Tuple.Create(AlbumOf(p),NameOf(p))).ToList();
            Change(files,add,remove);
        }
        internal static void Change(IEnumerable<Tuple<string,string>> files,IEnumerable<string> add,IEnumerable<string> remove) {
            var adding=(add??new string[0]).Select(Clean).Where(t=>t!=null).ToList();
            var removing=(remove??new string[0]).Select(Clean).Where(t=>t!=null).ToList();
            bool changed=false;
            lock(Gate) {
                var touched=new HashSet<string>(Same);
                var seen=new HashSet<string>(Same);
                foreach(var file in files) {
                    if(String.IsNullOrEmpty(file.Item1) || String.IsNullOrEmpty(file.Item2) || !seen.Add(file.Item1+"|"+file.Item2))continue;
                    Dictionary<string,List<string>> inFolder;
                    if(!All().TryGetValue(file.Item1,out inFolder)) {
                        if(adding.Count==0)continue;
                        All()[file.Item1]=inFolder=new Dictionary<string,List<string>>(Same);
                    }
                    List<string> tags;
                    if(!inFolder.TryGetValue(file.Item2,out tags))tags=new List<string>();
                    int before=tags.Count;
                    bool dropped=tags.RemoveAll(t=>removing.Contains(t,Same))>0;
                    foreach(string tag in adding)if(!tags.Contains(tag,Same))tags.Add(tag);
                    if(!dropped && tags.Count==before)continue;
                    if(tags.Count==0)inFolder.Remove(file.Item2);
                    else {
                        inFolder[file.Item2]=tags;
                        if(MarkOf(file.Item1,file.Item2)==null)SetMark(file.Item1,file.Item2,MarkNow(file.Item1,file.Item2));
                    }
                    touched.Add(file.Item1);
                }
                try {foreach(string folder in touched)Save(folder);}
                catch {all=null;throw;} // what is in memory must match what was saved
                changed=touched.Count>0;
            }
            if(changed)Raise();
        }
        // Every tag with the number of originals that carry it, in name order.
        internal static List<KeyValuePair<string,int>> Counts() {
            var counts=new Dictionary<string,int>(Same);
            lock(Gate) {
                foreach(var folder in All().Values)
                    foreach(var tags in folder.Values)
                        foreach(string tag in tags) {
                            int n;counts.TryGetValue(tag,out n);counts[tag]=n+1;
                        }
            }
            var list=counts.ToList();
            list.Sort((a,b)=>{int order=StrCmpLogicalW(a.Key,b.Key);return order!=0?order:String.CompareOrdinal(a.Key,b.Key);});
            return list;
        }
        // The originals that carry a tag: record folder of their folder -> their file names.
        internal static Dictionary<string,HashSet<string>> FilesWith(string tag) {
            var found=new Dictionary<string,HashSet<string>>(Same);
            lock(Gate) {
                foreach(var folder in All())
                    foreach(var file in folder.Value)
                        if(file.Value.Contains(tag,Same)) {
                            HashSet<string> names;
                            if(!found.TryGetValue(folder.Key,out names))found[folder.Key]=names=new HashSet<string>(Same);
                            names.Add(file.Key);
                        }
            }
            return found;
        }
        // A file went to another folder or got another name: its tags go with it.
        // replaced: a file that was already at the new place under that name was replaced, so that file's tags are dropped.
        // Otherwise tags already at the new place are kept and joined (several covers of one original report the same move).
        internal static void Moved(string fromAlbum,string fromName,string toAlbum,string toName,bool replaced=false) {
            if(fromAlbum==null || toAlbum==null || fromName==null || toName==null)return;
            bool changed=false;
            try {
                lock(Gate) {
                    Dictionary<string,List<string>> from,to;
                    List<string> tags=null;
                    bool same=Same.Equals(fromAlbum,toAlbum) && Same.Equals(fromName,toName);
                    if(All().TryGetValue(fromAlbum,out from) && from.TryGetValue(fromName,out tags))from.Remove(fromName);
                    string mark=tags==null?null:MarkOf(fromAlbum,fromName);
                    if(tags!=null)SetMark(fromAlbum,fromName,null);
                    if(same) {
                        // Only the letter case of the name changed: keep the tags under the new spelling.
                        if(tags==null)return;
                        from[toName]=tags;SetMark(fromAlbum,toName,mark);Save(fromAlbum);return;
                    }
                    All().TryGetValue(toAlbum,out to);
                    bool dropped=replaced && to!=null && to.Remove(toName);
                    if(tags==null && !dropped)return;
                    if(tags!=null) {
                        if(to==null)All()[toAlbum]=to=new Dictionary<string,List<string>>(Same);
                        List<string> there;
                        if(to.TryGetValue(toName,out there)) {
                            foreach(string tag in tags)if(!there.Contains(tag,Same))there.Add(tag);
                        } else to[toName]=tags;
                        // The same file at its new place keeps its mark; a file moved across drives gets a new one there.
                        SetMark(toAlbum,toName,MarkNow(toAlbum,toName)??mark);
                    }
                    // The new place is written first: if that fails, the tags are still on disk at the old place.
                    Save(toAlbum);
                    if(tags!=null && !Same.Equals(fromAlbum,toAlbum))Save(fromAlbum);
                    changed=true;
                }
            } catch(IOException quiet) {ErrorLog.Quiet(quiet);lock(Gate)all=null;} catch(UnauthorizedAccessException quiet) {ErrorLog.Quiet(quiet);lock(Gate)all=null;}
            if(changed)Raise();
        }
        // A whole folder's records went to another record folder (its drive letter changed): the tags join the ones there.
        internal static void FolderMoved(string fromAlbum,string toAlbum) {
            if(fromAlbum==null || toAlbum==null || Same.Equals(fromAlbum,toAlbum))return;
            bool changed=false;
            try {
                lock(Gate) {
                    Dictionary<string,List<string>> from,to;
                    if(!All().TryGetValue(fromAlbum,out from) || from.Count==0)return;
                    if(!All().TryGetValue(toAlbum,out to))All()[toAlbum]=to=new Dictionary<string,List<string>>(Same);
                    foreach(var file in from) {
                        List<string> there;
                        if(!to.TryGetValue(file.Key,out there))to[file.Key]=file.Value;
                        else foreach(string tag in file.Value)if(!there.Contains(tag,Same))there.Add(tag);
                        if(MarkOf(toAlbum,file.Key)==null)SetMark(toAlbum,file.Key,MarkOf(fromAlbum,file.Key));
                    }
                    Save(toAlbum);
                    All().Remove(fromAlbum);marks.Remove(fromAlbum);
                    try {File.Delete(Path.Combine(fromAlbum,FileName));} catch(Exception quiet) {ErrorLog.Quiet(quiet);}
                    changed=true;
                }
            } catch(IOException quiet) {ErrorLog.Quiet(quiet);lock(Gate)all=null;} catch(UnauthorizedAccessException quiet) {ErrorLog.Quiet(quiet);lock(Gate)all=null;}
            if(changed)Raise();
        }
        // Tagged files that were renamed or moved in File Explorer are found again by what Windows calls them, and their
        // tags go to the new name or folder. A file with a cover is left to its cover: the cover finds its original and
        // brings the tags along (DriveLinks). Files still in place only get their mark noted, for the next time.
        // The disk is looked at outside the lock, so a slow or sleeping drive never holds up the windows.
        internal static void FollowFiles(GalleryStore store,System.Threading.CancellationToken token) {
            var list=new List<Tuple<string,string,string>>(); // record folder, name, mark
            lock(Gate) {
                foreach(var folder in All())
                    foreach(string name in folder.Value.Keys)list.Add(Tuple.Create(folder.Key,name,MarkOf(folder.Key,name)));
            }
            var moves=new List<Tuple<string,string,string>>();  // record folder, old name, where the file is now
            var notes=new List<Tuple<string,string,string>>();  // record folder, name, mark to note
            foreach(var group in list.GroupBy(t=>t.Item1,Same)) {
                if(token.IsCancellationRequested)return;
                string source=null;bool here=false;
                try {
                    source=GalleryStore.SourceOfRecords(group.Key);
                    // Network folders are left alone: asking them can take long, and their files have no lasting number.
                    here=source!=null && !source.StartsWith(@"\\") && Directory.Exists(source);
                } catch(Exception quiet) {ErrorLog.Quiet(quiet);}
                if(source==null || source.StartsWith(@"\\"))continue;
                var taken=new HashSet<string>(group.Select(t=>t.Item2),Same);
                foreach(var entry in group) {
                    if(token.IsCancellationRequested)return;
                    string name=entry.Item2,mark=entry.Item3,now=null;
                    try {if(here)now=MarkNow(group.Key,name);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}
                    if(now!=null) {
                        if(mark==null){notes.Add(Tuple.Create(group.Key,name,now));continue;}
                        if(SameFile(mark,now))continue;
                        // Another file took this name (the tagged one was renamed and a new file got its old name): look for the tagged one.
                    } else if(mark==null)continue;
                    // A cover record of this file will bring the tags itself once it finds the file.
                    try {if(Directory.Exists(Path.Combine(group.Key,"p-"+GalleryStore.KeyOf(name))))continue;} catch(Exception) {continue;}
                    string found=Find(mark,here?source:null,taken);
                    if(found!=null && !(now!=null && Same.Equals(found,Path.Combine(source,name)))){moves.Add(Tuple.Create(group.Key,name,found));taken.Add(Path.GetFileName(found));}
                }
            }
            if(notes.Count>0) {
                try {
                    lock(Gate) {
                        var touched=new HashSet<string>(Same);
                        foreach(var note in notes) {
                            Dictionary<string,List<string>> files;
                            if(!All().TryGetValue(note.Item1,out files) || !files.ContainsKey(note.Item2) || MarkOf(note.Item1,note.Item2)!=null)continue;
                            SetMark(note.Item1,note.Item2,note.Item3);touched.Add(note.Item1);
                        }
                        foreach(string folder in touched)Save(folder);
                    }
                } catch(IOException quiet) {ErrorLog.Quiet(quiet);lock(Gate)all=null;} catch(UnauthorizedAccessException quiet) {ErrorLog.Quiet(quiet);lock(Gate)all=null;}
            }
            foreach(var move in moves) {
                try {
                    string target=store.OpenSubfolder(Path.GetDirectoryName(move.Item3)).Folder;
                    Moved(move.Item1,move.Item2,target,Path.GetFileName(move.Item3));
                } catch(Exception quiet) {ErrorLog.Quiet(quiet);}
            }
        }
        // "volume|id|birth|length": the first three say it is the very same file.
        static bool SameFile(string mark,string other) {
            var a=mark.Split('|');var b=other.Split('|');
            return a.Length>=3 && b.Length>=3 && a[0]==b[0] && a[1]==b[1] && a[2]==b[2];
        }
        // Where the marked file is now: by its Windows number, or (on drives whose numbers don't last, such as exFAT)
        // the one file in the same folder with the same size and creation time that has no tags of its own.
        static string Find(string mark,string folder,HashSet<string> taken) {
            var parts=mark.Split('|');
            uint volume;ulong id;long birth;
            if(parts.Length<3 || !UInt32.TryParse(parts[0],out volume) || !UInt64.TryParse(parts[1],out id) || !Int64.TryParse(parts[2],out birth))return null;
            string found=null;
            try {found=OriginalLocator.FindById(folder,volume,id,birth);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}
            long length;
            if(found!=null || folder==null || parts.Length<4 || !Int64.TryParse(parts[3],out length))return found;
            try {
                string only=null;
                foreach(var file in new DirectoryInfo(folder).EnumerateFiles()) {
                    if(file.Length!=length || file.CreationTimeUtc.ToFileTimeUtc()!=birth || taken.Contains(file.Name))continue;
                    if(only!=null)return null; // two candidates: not sure which, so neither
                    only=file.FullName;
                }
                return only;
            } catch(IOException) {return null;} catch(UnauthorizedAccessException) {return null;}
        }
        // Forgets the tags of files that are gone for good: the folder is there, the file isn't, and no cover record could find it again.
        internal static void Forget(string albumFolder,IEnumerable<string> names) {
            bool changed=false;
            try {
                lock(Gate) {
                    Dictionary<string,List<string>> files;
                    if(!All().TryGetValue(albumFolder,out files))return;
                    bool removed=false;
                    foreach(string name in names)removed|=files.Remove(name);
                    if(removed){Save(albumFolder);changed=true;}
                }
            } catch(IOException quiet) {ErrorLog.Quiet(quiet);lock(Gate)all=null;} catch(UnauthorizedAccessException quiet) {ErrorLog.Quiet(quiet);lock(Gate)all=null;}
            if(changed)Raise();
        }
        // Gives a tag another name everywhere. A file that already has the new name keeps one of the two.
        internal static void Rename(string tag,string newName) {
            newName=Clean(newName);
            if(newName==null || tag==newName)return;
            Edit(tag,newName);
        }
        // Takes a tag off every file. The files themselves are not touched.
        internal static void Delete(string tag) {Edit(tag,null);}
        static void Edit(string tag,string newName) {
            bool changed=false;
            lock(Gate) {
                foreach(var folder in All().ToList()) {
                    bool touched=false;
                    foreach(var file in folder.Value.ToList()) {
                        var tags=file.Value;
                        int at=tags.FindIndex(t=>Same.Equals(t,tag));
                        if(at<0)continue;
                        tags.RemoveAt(at);
                        if(newName!=null && !tags.Contains(newName,Same))tags.Insert(Math.Min(at,tags.Count),newName);
                        if(tags.Count==0)folder.Value.Remove(file.Key);
                        touched=true;
                    }
                    if(touched) {
                        try {Save(folder.Key);} catch {all=null;throw;} // what is in memory must match what was saved
                        changed=true;
                    }
                }
            }
            if(changed)Raise();
        }
    }
}
