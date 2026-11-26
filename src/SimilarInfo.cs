using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;

namespace LoveMyShelf {
    // What the "Similar photos" collection tells about each file, so the person can judge for themselves:
    // size and type, the dates, how a file differs from the one suggested to keep, and why that one was suggested.
    internal static class SimilarInfo {
        static string S(string english,string korean) {return L.P(english,korean);}
        static readonly CultureInfo Inv=CultureInfo.InvariantCulture;

        // The dates are read once per file (a photo's "taken" date sits inside the file), off the UI thread.
        internal static void ReadDates(SimilarFile file) {
            lock(file) { // two windows may build their tiles at the same time
                if(file.DatesRead)return;
                // A missing file "was created" in 1601: such a date is left out.
                try {var made=File.GetCreationTime(file.Path);if(made.Year>1700)file.Created=made;} catch(Exception) {}
                file.Taken=Taken(file.Path);
                file.DatesRead=true;
            }
        }
        static DateTime Taken(string path) {
            string kind=Path.GetExtension(path).ToLowerInvariant();
            if(kind!=".jpg" && kind!=".jpeg" && kind!=".jfif" && kind!=".tif" && kind!=".tiff")return DateTime.MinValue;
            try {
                using(var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete)) {
                    var frame=BitmapDecoder.Create(input,BitmapCreateOptions.DelayCreation|BitmapCreateOptions.IgnoreColorProfile,BitmapCacheOption.None).Frames[0];
                    var meta=frame.Metadata as BitmapMetadata;
                    if(meta==null)return DateTime.MinValue;
                    foreach(string query in new[]{"/app1/ifd/exif/{ushort=36867}","/ifd/exif/{ushort=36867}"}) {
                        object value=null;
                        try {value=meta.GetQuery(query);} catch(NotSupportedException) {} catch(System.Runtime.InteropServices.COMException) {}
                        DateTime when;
                        if(value is string && DateTime.TryParseExact(((string)value).Trim().TrimEnd('\0'),"yyyy:MM:dd HH:mm:ss",Inv,DateTimeStyles.None,out when))return when;
                    }
                }
            } catch(Exception) {}
            return DateTime.MinValue;
        }
        internal static DateTime Modified(SimilarFile file) {return new DateTime(file.Ticks,DateTimeKind.Utc).ToLocalTime();}
        static string Day(DateTime when) {return when.ToString("yyyy-MM-dd",Inv);}
        static string Minute(DateTime when) {return when.ToString("yyyy-MM-dd HH:mm",Inv);}
        internal static string Kind(SimilarFile file) {return Path.GetExtension(file.Path).TrimStart('.').ToUpperInvariant();}
        internal static string Resolution(SimilarFile file) {return file.Width>0?file.Width.ToString(Inv)+"×"+file.Height.ToString(Inv):"";}

        // The lines over the bottom of a tile: resolution · type · size, the date, and (in "nearly the same") how it differs.
        internal static string TileLines(SimilarGroup group,SimilarFile file,int covers) {
            var first=new List<string>();
            if(file.Width>0)first.Add(Resolution(file));
            first.Add(Kind(file)+" "+GalleryWindow.Bytes(file.Size).Replace(" "," ")); // "236 KB" stays on one line
            string text=String.Join(" · ",first);
            DateTime taken=file.Taken;
            text+="\n"+(taken>DateTime.MinValue?L.PF("Taken {0}","촬영 {0}",Day(taken)):L.PF("Modified {0}","수정 {0}",Day(Modified(file))));
            if(covers>0)text+=" · "+L.PF("{0:N0} covers","표지 {0:N0}장",covers);
            string differs=group.Exact?L.PF("Folder: {0}","폴더: {0}",FolderName(file.Folder)):Difference(group,file);
            if(differs!=null)text+="\n"+differs;
            return text;
        }
        // Downloads, Desktop and temporary folders (or a folder inside one): where an extra copy usually is.
        static readonly string[] ThrowawayNames={"downloads","download","다운로드","desktop","바탕 화면","바탕화면","temp","tmp","임시"};
        static string[] throwawayRoots;
        internal static bool Throwaway(string folder) {
            if(String.IsNullOrEmpty(folder))return false;
            try {
                if(throwawayRoots==null) {
                    string profile=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    throwawayRoots=new[]{Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),Path.GetTempPath(),
                        String.IsNullOrEmpty(profile)?null:Path.Combine(profile,"Downloads")}
                        .Where(r=>!String.IsNullOrEmpty(r)).Select(r=>r.TrimEnd('\\')+"\\").ToArray();
                }
                string full=folder.TrimEnd('\\')+"\\";
                if(throwawayRoots.Any(r=>full.StartsWith(r,StringComparison.OrdinalIgnoreCase)))return true;
                return full.Split('\\').Any(part=>ThrowawayNames.Contains(part.Trim().ToLowerInvariant()));
            } catch(Exception) {return false;}
        }
        internal static string FolderName(string folder) {string name=Path.GetFileName(folder.TrimEnd('\\'));return String.IsNullOrEmpty(name)?folder:name;}

        // "Nearly the same": what this file has that the suggested one doesn't (or the other way round), from what is known
        // about the files only. A picture comparison would guess wrong too often (recompressed copies differ everywhere a little).
        internal static string Difference(SimilarGroup group,SimilarFile file) {
            var keep=group.Keep;
            if(keep==null || file==keep)return group.Exact?null:S("The one the others are compared with","다른 사진은 이 사진과 비교했어요");
            return Versus(file,keep,true);
        }
        // How this file differs from another one of its group. In the compare window "compare side by side" is left out.
        internal static string Versus(SimilarFile file,SimilarFile keep,bool suggestCompare) {
            string look=suggestCompare?S(": compare side by side"," · 나란히 비교해 보세요"):"";
            if(file.Width<=0 || keep.Width<=0)return S("The size couldn't be read","크기를 읽지 못했어요")+look;
            {
                double a=Aspect(file),b=Aspect(keep);
                if(Math.Abs(a-b)/b>0.02)return S("Different shape: it may be cut","가로세로 비율이 달라요 · 잘렸을 수 있어요");
                if(file.Pixels<keep.Pixels)return L.PF("Smaller: {0}","크기가 작아요 · {0}",Resolution(file));
                if(file.Pixels>keep.Pixels)return L.PF("Bigger: {0} (may be blown up)","크기가 커요 · {0}(늘린 확대본일 수도 있어요)",Resolution(file));
            }
            if(!String.Equals(Kind(file),Kind(keep),StringComparison.OrdinalIgnoreCase))
                return L.PF("Same size, saved as {0}","크기는 같고 {0} 형식이에요",Kind(file));
            // Same size and type: whether the picture itself differs shows in the fingerprints (a watermark, text or a
            // color change moves a few bits; saving again with other settings usually moves none).
            if(file.HasHash && keep.HasHash && SimilarFiles.Distance(file.Hash,keep.Hash)>0)
                return S("Same size, but the picture differs a little (a mark, text or colors?)","크기는 같은데 그림이 조금 달라요(워터마크·글씨·색?)")+look;
            if(file.Size<keep.Size*0.9)return S("Same size, smaller file: compressed more","크기는 같고 용량이 작아요 · 더 많이 압축됐어요");
            if(file.Size>keep.Size*1.1)return S("Same size, bigger file: compressed less","크기는 같고 용량이 커요 · 덜 압축됐어요");
            return S("Same size and about the same file size: probably only saved again","크기·용량이 거의 같아요 · 다시 저장만 된 것 같아요");
        }
        // Width over height, the same whichever way the picture is turned.
        // Same shape (width over height within 2%), so a cover's frame (kept as fractions of the original) fits the other one.
        internal static bool SameShape(SimilarFile a,SimilarFile b) {
            if(a.Width<=0 || b.Width<=0)return false;
            double x=Aspect(a),y=Aspect(b);
            return Math.Abs(x-y)/y<=0.02;
        }
        static double Aspect(SimilarFile f) {double w=f.Width,h=f.Height;return Math.Max(w,h)/Math.Max(1,Math.Min(w,h));}

        // Why the suggested file was picked: the first rule in which it beat the next one.
        internal static string KeepReason(SimilarGroup group) {
            var keep=group.Keep;
            var next=group.Files.FirstOrDefault(f=>f!=keep);
            if(keep==null || next==null)return null;
            if(!group.Exact) {
                if(keep.Pixels>next.Pixels) {
                    string why=S("It has the most pixels (biggest picture). A blown-up copy is also big, so compare them side by side.",
                        "해상도가 가장 커요. 늘려서 만든 확대본도 크게 나오니, 나란히 비교해서 확인해 주세요.");
                    if(!keep.HasCover && group.Files.Any(f=>f.HasCover))why+=" "+S("The covers of the others move to it when you keep it in [Compare side by side].",
                        "다른 사진의 표지는 [나란히 비교]에서 이 사진을 남기면 이 사진으로 옮겨요.");
                    return why;
                }
                if(keep.HasCover && !next.HasCover)return S("Same size as the next one, and it has a cover.","다음 사진과 해상도가 같고, 표지가 있어요.");
            }
            if(group.Exact && keep.HasCover && !next.HasCover)return S("It has a cover.","표지가 있어요.");
            if(group.Exact && !Throwaway(keep.Folder) && Throwaway(next.Folder))
                return (keep.HasCover?S("Several files have a cover; among them: ","표지가 있는 파일이 여럿이라, 그중에서 "):"")
                    +S("it isn't in Downloads, Desktop or a temporary folder (the copies there go).","다운로드·바탕 화면·임시 폴더가 아닌 곳에 있어요(그런 폴더의 사본을 지워요).");
            // The files are listed in the order of the rule, so "next" is the runner-up. When several have a cover, the
            // rest of the rule only chose among those, and the reason says so.
            string among=group.Exact && keep.HasCover?S("Several files have a cover; among them: ","표지가 있는 파일이 여럿이라, 그중에서 "):"";
            if(!group.Exact) {
                long mine=group.Files.Sum(o=>SimilarFiles.Distance(keep.Hash,o.Hash)),theirs=group.Files.Sum(o=>SimilarFiles.Distance(next.Hash,o.Hash));
                if(mine<theirs)return among+S("same size as the next one, and it looks most like all the others (a copy with a watermark or cut edges looks less like them).",
                    "다음 사진과 해상도가 같고, 묶음의 다른 사진들과 가장 닮았어요(워터마크가 붙거나 잘린 사본은 덜 닮아요).");
            }
            if(keep.Ticks<next.Ticks)return group.Exact
                ?among+S("the copies are byte for byte the same; this one has the oldest modified date, so it is likely the first one.","사본은 모두 바이트까지 똑같아요. 이 파일의 수정한 날짜가 가장 오래돼서 처음 파일일 가능성이 커요.")
                :among+S("the rest being equal, it has the oldest modified date.","다른 것이 같아서 수정한 날짜가 가장 오래된 것을 골랐어요.");
            return among+S("the rest is equal, so it is simply first by folder and name.","다른 것이 모두 같아서 폴더·이름 순서로 맨 앞인 것을 골랐어요.");
        }
        // The whole rule, as shown above the tiles and in the questions.
        internal static string Rule(bool exact) {
            return exact
                ?S("“Suggested to keep” in exactly the same files:\nThe files are byte for byte the same, so the picture is the same whichever stays. Only the folder, name and dates differ.\n1. One with a cover\n2. One outside Downloads, Desktop and temporary folders (the copies there go)\n3. The oldest modified date (likely the first one)\n4. First by folder and name\nThe covers and tags of the copies move to the file that stays, so you can keep any of them: right-click it and choose [Keep only this one].",
                   "똑같은 파일의 ‘남기기 추천’은 이렇게 골라요.\n파일 내용이 바이트까지 같아서 어느 것을 남겨도 사진은 같아요. 다른 건 폴더·이름·날짜뿐이에요.\n1. 표지가 있는 파일\n2. 다운로드·바탕 화면·임시 폴더가 아닌 곳의 파일(그런 폴더의 사본을 지워요)\n3. 수정한 날짜가 가장 오래된 파일(처음 파일일 가능성이 커요)\n4. 폴더·이름 순서로 앞인 파일\n사본의 표지와 태그는 남는 파일로 옮겨지니, 남기고 싶은 파일을 오른쪽 클릭해서 [이 파일만 남기고 사본 정리]를 골라도 돼요.")
                :S("“Suggested to keep” in nearly the same photos:\n1. The most pixels (biggest picture). A blown-up copy is also big, so check with [Compare side by side].\n2. When the size is the same, one with a cover\n3. The one most like all the others (a copy with a watermark or cut edges looks less like them)\n4. The oldest modified date\nIn [Compare side by side], the covers of a photo you don't keep move to the one you keep (when the shape is the same; otherwise it asks).\nThe line at the bottom of each tile tells how it differs from the suggested one.",
                   "거의 같은 사진의 ‘남기기 추천’은 이렇게 골라요.\n1. 해상도가 가장 큰 사진. 늘려서 만든 확대본도 크게 나오니 [나란히 비교]로 확인해 주세요.\n2. 해상도가 같으면 표지가 있는 사진\n3. 묶음의 다른 사진들과 가장 닮은 사진(워터마크가 붙거나 잘린 사본은 덜 닮아요)\n4. 수정한 날짜가 가장 오래된 사진\n[나란히 비교]에서 남기지 않은 사진의 표지는 남기는 사진으로 옮겨요(가로세로 비율이 다르면 먼저 물어봐요).\n칸 아래쪽 글은 ‘남기기 추천’ 사진과 무엇이 다른지 알려 줘요.");
        }
        // The card shown when the mouse rests on a tile: everything known about the file.
        internal static string Card(SimilarGroup group,SimilarFile file,int covers) {
            var lines=new List<string>{file.Name,""};
            if(file.Width>0)lines.Add(L.PF("Resolution: {0}","해상도: {0}",Resolution(file)));
            lines.Add(L.PF("Type · size: {0} · {1}","형식 · 용량: {0} · {1}",Kind(file),GalleryWindow.Bytes(file.Size)));
            if(file.Taken>DateTime.MinValue)lines.Add(L.PF("Taken: {0}","찍은 날: {0}",Minute(file.Taken)));
            lines.Add(L.PF("Modified: {0}","수정한 날짜: {0}",Minute(Modified(file))));
            if(file.Created>DateTime.MinValue)lines.Add(L.PF("Came into this folder: {0}","이 폴더에 생긴 날: {0}",Minute(file.Created)));
            lines.Add(L.PF("Folder: {0}","폴더: {0}",file.Folder));
            if(covers>0)lines.Add(L.PF("Covers: {0:N0}","표지: {0:N0}장",covers));
            lines.Add("");
            if(group.Exact)lines.Add(S("Byte for byte the same as the other files of this group.","이 묶음의 다른 파일과 내용이 바이트까지 똑같아요."));
            else {
                lines.Add(S("Nearly the same as the others: the picture matches, but the file isn't the same.","다른 사진과 그림은 거의 같지만 파일은 달라요."));
                if(file!=group.Keep){string d=Difference(group,file);if(d!=null)lines.Add(L.PF("Compared with the suggested one: {0}","‘남기기 추천’ 사진과 비교: {0}",d));}
            }
            if(file==group.Keep) {
                string why=KeepReason(group);
                if(why!=null)lines.Add(L.PF("Suggested to keep because: {0}","‘남기기 추천’인 이유: {0}",why));
            }
            if(file.IsPicture || group.Exact)lines.Add(S("Double-click: compare side by side · Space: large view","더블클릭: 나란히 비교 · Space: 크게 보기"));
            return String.Join("\n",lines);
        }
    }
}
