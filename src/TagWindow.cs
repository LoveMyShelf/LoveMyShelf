using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LoveMyShelf {
    // A small rounded label for one tag. State: 2 = on every chosen file, 1 = on some of them, 0 = on none.
    internal sealed class TagChip : Border {
        internal readonly string Word;
        internal readonly int Before;
        internal int State;
        readonly TextBlock label=new TextBlock {FontSize=13,VerticalAlignment=VerticalAlignment.Center};
        internal TagChip(string tag,int state,Action changed) {
            Word=tag;Before=State=state;
            CornerRadius=new CornerRadius(13);Padding=new Thickness(11,4,11,5);Margin=new Thickness(0,0,6,6);
            BorderThickness=new Thickness(1);Cursor=Cursors.Hand;Child=label;Focusable=true;
            System.Windows.Automation.AutomationProperties.SetName(this,tag);
            MouseLeftButtonUp+=delegate {Toggle();changed();};
            KeyDown+=(s,e)=>{if(e.Key==Key.Space || e.Key==Key.Enter){Toggle();changed();e.Handled=true;}};
            Show();
        }
        // Some → all → none → all: one click never quietly takes a tag off files that had it.
        void Toggle() {State=State==2?0:2;Show();}
        internal void TurnOn() {State=2;Show();}
        void Show() {
            label.Text=State==2?"✓ "+Word:State==1?"– "+Word:Word;
            label.SetResourceReference(TextBlock.ForegroundProperty,State==0?"Subtle":"Text");
            SetResourceReference(BackgroundProperty,State==2?"Raised":"Panel");
            SetResourceReference(BorderBrushProperty,State==2?"Accent":State==1?"StrongLine":"Line");
            ToolTip=State==1?L.T("Only some of the chosen files have this tag. Click to put it on all of them."):null;
        }
    }
    // Puts tags on the chosen originals and takes them off. Tags are kept only in LoveMyShelf's own records.
    internal sealed class TagWindow : Window {
        readonly List<Photo> targets;
        readonly WrapPanel chips=new WrapPanel();
        readonly TextBox box=new TextBox {Padding=new Thickness(6),FontSize=14};
        readonly TextBlock note=UI.Text("",12,true);
        internal TagWindow(Window owner,List<Photo> photos) {
            // One entry for each original: an original's cover tiles all stand for the same file.
            targets=photos.GroupBy(p=>Tags.AlbumOf(p)+"|"+Tags.NameOf(p),StringComparer.OrdinalIgnoreCase).Select(g=>g.First()).ToList();
            UI.WindowSetup(this,L.T("Tags"));Owner=owner;ShowInTaskbar=false;
            WindowStartupLocation=WindowStartupLocation.CenterOwner;ResizeMode=ResizeMode.NoResize;
            Width=Math.Min(520,SystemParameters.WorkArea.Width);SizeToContent=SizeToContent.Height;
            var root=new DockPanel();root.Children.Add(new WindowTitleBar(this));
            var page=new StackPanel {Margin=new Thickness(22,16,22,20)};root.Children.Add(page);Content=root;
            var title=UI.Text(targets.Count==1?NoticeLook.KeepWords(Tags.NameOf(targets[0])):L.F("{0:N0} files",targets.Count),14);
            title.TextTrimming=TextTrimming.CharacterEllipsis;page.Children.Add(title);
            var row=new DockPanel {Margin=new Thickness(0,10,0,0)};
            var add=UI.Btn(L.T("Add"),AddTyped);add.FontSize=14;add.MinHeight=32;add.Margin=new Thickness(6,0,0,0);add.Padding=new Thickness(14,0,14,0);
            DockPanel.SetDock(add,Dock.Right);row.Children.Add(add);
            var hint=UI.Text(L.T("Type a new tag, then press Enter"),14,true);hint.Margin=new Thickness(9,0,0,0);hint.IsHitTestVisible=false;
            var field=new Grid();field.Children.Add(box);field.Children.Add(hint);
            System.Windows.Automation.AutomationProperties.SetName(box,L.T("Type a new tag, then press Enter"));
            row.Children.Add(field);page.Children.Add(row);
            // On a short screen (225% scaling leaves about 295 units) the tag list gives way so the buttons stay on screen.
            var scroll=new ScrollViewer {Content=chips,MaxHeight=Math.Min(230,Math.Max(64,SystemParameters.WorkArea.Height-250)),VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Margin=new Thickness(0,12,0,0)};
            page.Children.Add(scroll);
            note.TextWrapping=TextWrapping.Wrap;note.Margin=new Thickness(0,6,0,0);page.Children.Add(note);
            var promise=UI.Text(NoticeLook.KeepWords(L.T("Tags are kept only inside LoveMyShelf. The original files and their names are never changed.")),12,true);
            promise.TextWrapping=TextWrapping.Wrap;promise.Margin=new Thickness(0,8,0,0);page.Children.Add(promise);
            var actions=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,14,0,0)};
            var cancel=UI.Btn(L.T("Cancel"),()=>DialogResult=false);cancel.IsCancel=true;
            var ok=UI.Btn(L.T("Save"),Save,true);ok.Margin=new Thickness(6,0,0,0);
            foreach(var b in new[]{cancel,ok}){b.FontSize=14;b.MinHeight=32;b.Padding=new Thickness(16,0,16,0);}
            actions.Children.Add(cancel);actions.Children.Add(ok);page.Children.Add(actions);

            // Tags already on the chosen files come first, then every other tag in name order.
            var counts=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
            foreach(var photo in targets)foreach(string tag in Tags.Of(photo)) {
                int n;counts.TryGetValue(tag,out n);counts[tag]=n+1;
            }
            var known=Tags.Counts().Select(c=>c.Key).ToList();
            foreach(string tag in known.Where(t=>counts.ContainsKey(t)).Concat(known.Where(t=>!counts.ContainsKey(t)))) {
                int n;counts.TryGetValue(tag,out n);
                chips.Children.Add(new TagChip(tag,n==0?0:n==targets.Count?2:1,ShowNote));
            }
            box.TextChanged+=delegate {
                hint.Visibility=box.Text.Length==0?Visibility.Visible:Visibility.Collapsed;
                // Typing narrows the tags shown, so a tag used before is one click away.
                string typed=box.Text.Trim().TrimStart('#');
                foreach(TagChip chip in chips.Children)
                    chip.Visibility=typed.Length==0 || chip.State>0 || chip.Word.IndexOf(typed,StringComparison.OrdinalIgnoreCase)>=0?Visibility.Visible:Visibility.Collapsed;
            };
            box.PreviewKeyDown+=(s,e)=>{
                if(e.Key!=Key.Enter)return;
                // Enter with a word typed adds it; Enter on the empty box saves, like the Save button.
                if(box.Text.Trim().Length>0)AddTyped();else Save();
                e.Handled=true;
            };
            ShowNote();
            Loaded+=delegate {box.Focus();};
        }
        void ShowNote() {
            note.Text=chips.Children.Count==0?L.T("There are no tags yet. Type a name above, for example a person's name."):"";
            note.Visibility=note.Text.Length==0?Visibility.Collapsed:Visibility.Visible;
        }
        void AddTyped() {
            string tag=Tags.Clean(box.Text);
            box.Text="";box.Focus();
            if(tag==null)return;
            var chip=chips.Children.OfType<TagChip>().FirstOrDefault(c=>String.Equals(c.Word,tag,StringComparison.OrdinalIgnoreCase));
            if(chip==null) {
                chip=new TagChip(tag,0,ShowNote);
                chips.Children.Insert(0,chip);
            }
            chip.TurnOn();
            chip.BringIntoView();
            ShowNote();
        }
        void Save() {
            // A word still in the box counts: nobody expects it to be thrown away.
            if(Tags.Clean(box.Text)!=null)AddTyped();
            var all=chips.Children.OfType<TagChip>().ToList();
            var add=all.Where(c=>c.State==2 && c.Before!=2).Select(c=>c.Word).ToList();
            var remove=all.Where(c=>c.State==0 && c.Before!=0).Select(c=>c.Word).ToList();
            try {
                if(add.Count>0 || remove.Count>0)Tags.Change(targets,add,remove);
                DialogResult=true;
            } catch(Exception ex) {UI.Error(this,ex);}
        }
    }
    // Asks for one line of text (a tag's new name).
    internal sealed class TagNameWindow : Window {
        internal string Value {get;private set;}
        internal TagNameWindow(Window owner,string title,string label,string text,string action) {
            UI.WindowSetup(this,title);Owner=owner;ShowInTaskbar=false;
            WindowStartupLocation=WindowStartupLocation.CenterOwner;ResizeMode=ResizeMode.NoResize;
            Width=Math.Min(420,SystemParameters.WorkArea.Width);SizeToContent=SizeToContent.Height;
            var root=new DockPanel();root.Children.Add(new WindowTitleBar(this));
            var page=new StackPanel {Margin=new Thickness(22,16,22,20)};root.Children.Add(page);Content=root;
            var caption=UI.Text(NoticeLook.KeepWords(label),14);caption.TextWrapping=TextWrapping.Wrap;page.Children.Add(caption);
            var box=new TextBox {Text=text,Padding=new Thickness(6),FontSize=14,Margin=new Thickness(0,6,0,0),MaxLength=Tags.MaxLength};
            System.Windows.Automation.AutomationProperties.SetName(box,label);
            page.Children.Add(box);
            var actions=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,14,0,0)};
            var cancel=UI.Btn(L.T("Cancel"),()=>DialogResult=false);cancel.IsCancel=true;
            var ok=UI.Btn(action,()=>{
                string value=Tags.Clean(box.Text);
                if(value==null){box.Focus();return;}
                Value=value;DialogResult=true;
            },true);
            ok.IsDefault=true;ok.Margin=new Thickness(6,0,0,0);
            foreach(var b in new[]{cancel,ok}){b.FontSize=14;b.MinHeight=32;b.Padding=new Thickness(16,0,16,0);}
            actions.Children.Add(cancel);actions.Children.Add(ok);page.Children.Add(actions);
            Loaded+=delegate {box.Focus();box.SelectAll();};
        }
    }
}
