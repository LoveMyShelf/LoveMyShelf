using System.Linq;

namespace LoveMyShelf {
    // "Send to phone…" in the blank-space menu: the covers on screen, in the order shown, go to PhoneWindow.
    public partial class GalleryWindow {
        internal void SendToPhone() {
            var list=photos.Where(p=>p!=null && p.ShowsCover)
                .Select(p=>new PhoneItem {Cover=p.ActiveCoverFile,Original=p.Linked?p.Original:null}).ToList();
            string name=active!=null?active.Name:"Love My Shelf";
            PhoneWindow.Show(this,name,list,store.Root);
        }
    }
}
