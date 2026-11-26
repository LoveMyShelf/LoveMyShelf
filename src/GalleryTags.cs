using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace LoveMyShelf {
    // The [Tags] list at the bottom. Clicking a tag opens its view, like before. The list stays open, so clicking
    // another tag shows both together (files with either tag), and clicking a shown tag again takes it out.
    public partial class GalleryWindow {
        const char TagJoin='\t';
        // Where the window was before a tag's view opened, for [Clear the picked tags].
        string beforeTags;

        static List<string> SplitTags(string joined) {
            var tags=new List<string>();
            foreach(string part in (joined??"").Split(TagJoin)) {
                string tag=part.Trim();
                if(tag.Length>0 && !tags.Contains(tag,StringComparer.OrdinalIgnoreCase))tags.Add(tag);
            }
            return tags;
        }
        static Album TagAlbum(IEnumerable<string> tags) {
            var list=tags.ToList();
            return new Album(TagKey+String.Join(TagJoin.ToString(),list),String.Join(" · ",list.Select(t=>"#"+t)));
        }

        void OpenTagMenu() {
            var menu=new ContextMenu {PlacementTarget=tagsButton,Placement=PlacementMode.Top};
            FillTagMenu(menu);
            menu.IsOpen=true;
        }
        // The tag list: each tag with how many originals carry it, checked while it is shown.
        void FillTagMenu(ContextMenu menu) {
            menu.Items.Clear();
            var counts=Tags.Counts();
            var shown=ShownTags;
            string single=ShownTag;
            if(counts.Count==0) {
                menu.Items.Add(new MenuItem {Header=L.T("There are no tags yet. Right-click a file and click [Tags…]."),IsEnabled=false});
            }
            // Only while two or more are shown: one click back to where the window was.
            if(shown.Count>=2) {
                var clear=new MenuItem {Header=L.P("Clear the picked tags","고른 태그 모두 풀기")};
                clear.Click+=delegate {ClearShownTags();};
                menu.Items.Add(clear);menu.Items.Add(new Separator());
            }
            // A shown tag that no file carries any more still gets its line (0), so it can be taken out.
            var rows=counts.ToList();
            foreach(string tag in shown)
                if(!rows.Any(r=>String.Equals(r.Key,tag,StringComparison.OrdinalIgnoreCase)))rows.Add(new KeyValuePair<string,int>(tag,0));
            foreach(var pair in rows) {
                string tag=pair.Key;
                var item=new MenuItem {Header=tag.Replace("_","__"),InputGestureText=pair.Value.ToString("N0"),StaysOpenOnClick=true,
                    IsChecked=shown.Contains(tag,StringComparer.OrdinalIgnoreCase)};
                item.Click+=delegate {ToggleShownTag(tag);if(menu.IsOpen)FillTagMenu(menu);};
                menu.Items.Add(item);
            }
            // A quiet hint the first time a tag is open, so the person knows a second click adds another.
            if(shown.Count==1 && rows.Count>=2)
                menu.Items.Add(new MenuItem {Header=L.P("Click another tag to see it together","다른 태그를 누르면 같이 보여요"),IsEnabled=false});
            if(single!=null) {
                menu.Items.Add(new Separator());
                var rename=new MenuItem {Header=L.F("Rename the tag “{0}”…",single).Replace("_","__")};
                rename.Click+=delegate {RenameShownTag();};
                var delete=new MenuItem {Header=L.F("Delete the tag “{0}”…",single).Replace("_","__")};
                delete.Click+=delegate {DeleteShownTag();};
                menu.Items.Add(rename);menu.Items.Add(delete);
            }
            menu.Items.Add(new Separator());
            var people=new MenuItem {Header=L.T("Find people by face…")};
            people.Click+=delegate {FaceWindow.Open(this,store);};
            menu.Items.Add(people);
            var similar=new MenuItem {Header=L.P("Similar photos","비슷한 사진 모음")};
            similar.Click+=delegate {Select(SimilarAlbum);};
            menu.Items.Add(similar);
        }
        // Not in a tag's view: open it (Back returns). In one: add the tag, or take it out if it is shown.
        internal void ToggleShownTag(string tag) {
            if(!ShowingTag) {
                beforeTags=active==null?null:LocationKey(active);
                Select(TagAlbum(new[]{tag}));
                return;
            }
            var shown=ShownTags;
            int at=shown.FindIndex(t=>String.Equals(t,tag,StringComparison.OrdinalIgnoreCase));
            if(at>=0)shown.RemoveAt(at);else shown.Add(tag);
            if(shown.Count==0){ClearShownTags();return;}
            // Changing which tags are shown is the same place, so it doesn't add steps to Back.
            historyMove=true;
            try {Select(TagAlbum(shown));} finally {historyMove=false;}
        }
        // Back to the folder (or collection) the window showed before the tags.
        internal void ClearShownTags() {
            if(!ShowingTag)return;
            string key=beforeTags;beforeTags=null;
            // The step before is usually where the tags were opened from (also after going Back into them).
            if(backStack.Count>0 && !backStack[backStack.Count-1].StartsWith("@"+TagKey,StringComparison.Ordinal)){GoBack();return;}
            if(key!=null)GoToLocation(key,null);
            if(!ShowingTag)return;
            // That place can't be opened any more (a drive taken out): Back, or the cover collection.
            if(backStack.Count>0)GoBack();
            else Select(allCrops);
        }
    }
}
