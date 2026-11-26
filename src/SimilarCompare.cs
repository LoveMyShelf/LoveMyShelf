using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LoveMyShelf {
    public partial class GalleryWindow {
        // Opening a tile in this collection (double-click, Enter): the compare window when it can compare, else the large view.
        internal bool SimilarOpen(Photo photo) {
            if(!ShowingSimilar || photo==null || photo.Similar==null || photo.SimilarFile==null)return false;
            var group=photo.Similar;
            if(!group.Exact && (!photo.SimilarFile.IsPicture || group.Files.Count(f=>f.IsPicture)<2))return false;
            CompareSimilar(photo);
            return true;
        }
        // The compare window for this tile's group. Two chosen tiles of one group are the first pair.
        internal void CompareSimilar(Photo photo) {
            var group=photo.Similar;var file=photo.SimilarFile;
            if(group==null || file==null)return;
            var chosen=photos.Where(p=>p!=null && p.Similar==group && selected.Contains(p.Folder) && p.SimilarFile!=null && p.SimilarFile.IsPicture)
                .Select(p=>p.SimilarFile).Distinct().ToList();
            SimilarFile other=null;
            if(!group.Exact && chosen.Count==2 && chosen.Contains(file))other=chosen.First(f=>f!=file);
            HideName();
            new SimilarCompareWindow(this,group,file,other).Show();
        }
        internal Photo SimilarPhotoOf(SimilarFile file) {return photos.FirstOrDefault(p=>p!=null && p.SimilarFile==file);}
        internal void SimilarLarge(SimilarFile file) {
            var p=SimilarPhotoOf(file);
            if(p!=null)OpenPhoto(p);
            else SimilarTell(S("This file isn't shown in the folder window any more.","이 파일은 이제 폴더 창에 없어요."));
        }
        internal void SimilarTell(string text) {Notify(S("Similar photos","비슷한 사진 모음"),text,null,NoticeKind.Info);}
        // From the compare window, no question (the window showed the choice and can take it back until then):
        // this one file goes to the Recycle Bin while the other one stays. Null when done, else why not.
        // The covers of a file (main and extra, hidden ones too), to show in the compare window.
        internal List<Photo> SimilarCoversOf(SimilarFile file) {
            var covers=new List<Photo>();
            try {
                foreach(string record in Mover.RecordsOf(store.AlbumFolderOf(file.Folder),file.Name)) {
                    if(!File.Exists(Path.Combine(record,"photo.xml")))continue;
                    try {covers.Add(GalleryStore.Read(record,file.Path));} catch(Exception quiet) {ErrorLog.Quiet(quiet);}
                }
            } catch(Exception quiet) {ErrorLog.Quiet(quiet);}
            return covers.OrderBy(c=>c.Order).ToList();
        }
        // Moved covers go back to the file (each step on its own, so one problem doesn't stop the other).
        static void Undo(List<SimilarSearch.Moved> records,SimilarFile file) {
            try {SimilarSearch.PutBack(records);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}
            try {SimilarSearch.Unsettle(records,file);} catch(Exception quiet) {ErrorLog.Quiet(quiet);}
        }
        internal void SimilarShowCover(List<Photo> covers,int index) {
            try {OpenViewer(covers,index,true);} catch(Exception ex) {SimilarTell(ex.Message);}
        }
        internal async Task<string> SimilarTrashNow(SimilarFile file,SimilarFile stays,bool moveCovers=false,bool trashCovers=false) {
            if(lifetime.IsCancellationRequested)return S("The folder window was closed, so it stayed.","폴더 창이 닫혀서 그대로 두었어요.");
            if(SimilarSearch.Busy)return S("It's still looking for similar photos.","아직 비슷한 사진을 찾는 중이에요.");
            if(SimilarSearch.Working)return S("Another tidying up is going on.","다른 정리를 하는 중이에요.");
            string problem=null;bool gone=false;int moved=0;List<Photo> oldCovers=null;
            var handle=new System.Windows.Interop.WindowInteropHelper(this).Handle;
            Begin();
            try {
                var pair=new List<SimilarGroup>{new SimilarGroup {Files=new List<SimilarFile>{file,stays},Keep=stays}};
                SimilarSearch.Hold hold=null;
                try {hold=await Task.Run(()=>SimilarSearch.Lock(file,pair,new List<SimilarFile>{file}));} catch(Exception ex) {problem=ex.Message;}
                if(hold==null) {
                    if(problem==null)problem=S("it or the one that stays changed, is gone or is in use","이 파일이나 남길 파일이 바뀌었거나 없거나 쓰는 중이에요");
                } else using(hold) {
                    bool own=true;
                    // Covers that can't be shown to be this file's own (an earlier file had its name) are not moved: it stays.
                    if(moveCovers || trashCovers)try {own=await Task.Run(()=>SimilarSearch.CoversBelongTo(store,file));} catch(Exception) {own=false;}
                    if(CoverWork.HoldsPath(file.Path) || (moveCovers && CoverWork.HoldsPath(stays.Path)) || !hold.StillAt(file.Path))
                        problem=S("it changed or is being edited","파일이 바뀌었거나 편집 중이에요");
                    else if(!own)problem=S("whose covers these are couldn't be checked, so it stayed","표지가 이 파일의 것인지 확인하지 못해서 그대로 두었어요");
                    else {
                        List<SimilarSearch.Moved> records=null;
                        if(moveCovers) {
                            try {
                                problem=await Task.Run(()=>{
                                    records=SimilarSearch.MoveCovers(store,file,stays);
                                    try {SimilarSearch.Settle(records,stays);return null;}
                                    catch(Exception ex) {Undo(records,file);return ex.Message;}
                                });
                            } catch(Exception ex) {problem=ex.Message;}
                        }
                        if(problem==null) {
                            string album=store.AlbumFolderOf(file.Folder);
                            var tags=Tags.Of(album,file.Name);
                            if(trashCovers)oldCovers=SimilarCoversOf(file);
                            try {RecycleBin.MoveFile(file.Path,handle);gone=true;}
                            catch(Exception ex) {
                                if(records!=null)Undo(records,file);
                                problem=ex.Message;
                            }
                            if(gone) {
                                if(records!=null)moved=records.Count;
                                if(oldCovers!=null && oldCovers.Count>0) {
                                    // Its covers go to the Recycle Bin too (the cover pictures, upscaled and GIF ones), then their records.
                                    int failed=0;string why=null;
                                    foreach(var cover in oldCovers) {
                                        try {
                                            foreach(string f in new[]{cover.CoverFile,cover.UpscaleFile,cover.MotionFile}
                                                .Where(f=>!String.IsNullOrEmpty(f) && File.Exists(f)).Distinct(StringComparer.OrdinalIgnoreCase))
                                                RecycleBin.MoveFile(f,handle);
                                            GalleryStore.RemoveRecord(cover);
                                        } catch(Exception ex) {failed++;why=ex.Message;}
                                    }
                                    if(failed>0)SimilarTell(L.PF("{0:N0} covers of “{1}” couldn't go to the Recycle Bin: {2}","‘{1}’의 표지 {0:N0}장은 휴지통으로 보내지 못했어요: {2}",failed,file.Name,why));
                                }
                                // Its tags go to the one that stays.
                                try {
                                    if(tags.Count>0)Tags.Change(new[]{Tuple.Create(store.AlbumFolderOf(stays.Folder),stays.Name)},tags,null);
                                    Tags.Forget(album,new[]{file.Name});
                                } catch(Exception quiet) {ErrorLog.Quiet(quiet);}
                            }
                        }
                    }
                }
            } finally {End();}
            if(gone) {
                if(moved>0)stays.HasCover=true;
                try {FilesRecycled(new List<string>{file.Path,stays.Path});} catch(Exception) {}
                SimilarSearch.Removed(new[]{file});
                return null;
            }
            return problem;
        }
        // Exactly the same files, from the compare window: the keeper stays, the copies' covers and tags move to it and
        // the copies go to the Recycle Bin (as [Keep only this one], without its question). The notice tells how it went.
        internal async Task<string> SimilarTidyNow(SimilarGroup group,SimilarFile keeper,List<SimilarFile> copies) {
            if(SimilarSearch.Busy)return S("It's still looking for similar photos.","아직 비슷한 사진을 찾는 중이에요.");
            if(SimilarSearch.Working)return S("Another tidying up is going on.","다른 정리를 하는 중이에요.");
            var result=new Tidied();
            Begin();
            try {await Tidy(group,keeper,copies,result);} finally {End();}
            if(lifetime.IsCancellationRequested)return null;
            SimilarDone(result.Gone,result.Skipped,result.LastError,result.Moved,result.Kept);
            return null;
        }
    }

    // The compare window. "Nearly the same": two pictures fill the window side by side (zoom and move together, hold
    // Space to swap them); press 1 or 2 for the one to keep, the other goes to the Recycle Bin and the next one of the
    // group comes into its place, until one is left; then the next group. "Exactly the same": one picture and the list
    // of the copies, to choose the one to keep. The last choice can be taken back (Ctrl+Z): the file goes to the
    // Recycle Bin only with the next choice, the next group or when the window closes.
    internal sealed class SimilarCompareWindow : Window {
        static string S(string english,string korean) {return L.P(english,korean);}
        static readonly Brush OverlayBack=Frozen(Color.FromArgb(0xD9,0x12,0x14,0x17)),PillBack=Frozen(Color.FromArgb(0xB8,0x0B,0x0C,0x0E)),
            White=Frozen(Color.FromRgb(0xEC,0xEE,0xF1)),Dim=Frozen(Color.FromRgb(0xB6,0xBB,0xC2)),Blue=Frozen(Color.FromRgb(0x31,0x82,0xF6)),
            Orange=Frozen(Color.FromRgb(0xF5,0x9E,0x0B));
        static Brush Frozen(Color c) {var b=new SolidColorBrush(c);b.Freeze();return b;}

        readonly GalleryWindow owner;
        readonly bool exact;
        SimilarGroup group;
        // Nearly the same: what is shown, who is waiting, and the one kept so far (its side).
        readonly SimilarFile[] slots=new SimilarFile[2];
        readonly bool[] arrived=new bool[2];
        readonly List<SimilarFile> waiting=new List<SimilarFile>();
        readonly List<SimilarFile> trashed=new List<SimilarFile>(),keptToo=new List<SimilarFile>();
        int champion,picks,total,videos,groupsSeen;
        // Exactly the same: the files and the one chosen.
        List<SimilarFile> copies=new List<SimilarFile>();
        SimilarFile chosen;
        readonly HashSet<SimilarFile> seen=new HashSet<SimilarFile>();
        sealed class Pending {
            internal SimilarFile File,Stays,Arrived;internal int Side,Champion;internal bool MoveCovers,TrashCovers;
            internal bool Exact;internal SimilarGroup Group;internal List<SimilarFile> Copies;
        }
        Pending pending;
        // One after the other in the whole program (several compare windows share it), and the files on their way to the
        // Recycle Bin, so another group doesn't show them meanwhile.
        static Task chain=Task.FromResult(0);
        static readonly HashSet<SimilarFile> going=new HashSet<SimilarFile>();
        // Files that covers are moving to (not written yet): when one of them loses next, its covers must move on too.
        static readonly HashSet<SimilarFile> gettingCovers=new HashSet<SimilarFile>();
        readonly bool[] ready=new bool[2];
        bool closing;
        bool finished,swapped,dragging,closed,showInfo,allInfo,compact;
        DateTime calmUntil;Point dragFrom;

        readonly Image[] images={new Image {Stretch=Stretch.Uniform},new Image {Stretch=Stretch.Uniform}};
        readonly Grid[] stages={new Grid(),new Grid()};
        readonly Border[] panes=new Border[2];
        readonly Border[] pills={new Border(),new Border()};
        // Each photo's covers, small (9:16) under its label: so whether a cover is worth keeping can be seen. A click opens it big.
        readonly Border[] coverBoxes={new Border(),new Border()};
        readonly TextBlock[] pillText={new TextBlock(),new TextBlock()};
        readonly MatrixTransform view=new MatrixTransform(); // shared by both pictures
        readonly Grid middle=new Grid();
        readonly Border info=new Border(),card=new Border();
        readonly Grid infoGrid=new Grid();
        readonly TextBlock title=new TextBlock(),message=new TextBlock();
        readonly Button[] keepButtons=new Button[2],nameButtons=new Button[2];
        readonly TextBlock[] keepText=new TextBlock[2],nameText=new TextBlock[2];
        Button infoButton,undoButton,bothButton,moreButton,fitButton,actualButton,swapButton,tidyButton,skipButton;
        readonly CheckBox pixels=new CheckBox();
        readonly StackPanel list=new StackPanel();
        ColumnDefinition listColumn;
        // Windowed: the bars above and below the pictures. Full screen (like the video window): the pictures fill the
        // screen and the bars float over them while the mouse is near the top or the bottom edge.
        readonly Grid root=new Grid();
        readonly StackPanel topBand=new StackPanel {VerticalAlignment=VerticalAlignment.Top};
        readonly Border bottomBand=new Border {VerticalAlignment=VerticalAlignment.Bottom};
        readonly System.Windows.Threading.DispatcherTimer peek=new System.Windows.Threading.DispatcherTimer {Interval=TimeSpan.FromSeconds(2.5)};
        bool full,menuOpen,peeking;

        internal SimilarCompareWindow(GalleryWindow owner,SimilarGroup group,SimilarFile first,SimilarFile second) {
            this.owner=owner;exact=group.Exact;
            UI.WindowSetup(this,exact?S("Exactly the same files","똑같은 파일 정리"):S("Compare side by side","나란히 비교"));Owner=owner;
            var area=SystemParameters.WorkArea;
            Width=Math.Min(1500,area.Width*0.94);Height=Math.Min(1000,area.Height*0.94);
            MinWidth=Math.Min(640,area.Width);MinHeight=Math.Min(440,area.Height);
            WindowStartupLocation=WindowStartupLocation.CenterOwner;
            if(owner.WindowState==WindowState.Maximized)WindowState=WindowState.Maximized;

            // Full screen like the folder window it came from (F11 switches); Esc closes this window.
            root.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});
            topBand.Children.Add(FullScreen.TitleBar(this,FullScreen.IsFull(owner),escapeExits:false,changed:SetFull));
            Content=root;
            UI.Use(root,Panel.BackgroundProperty,"Paper");
            topBand.Children.Add(TopBar());
            bottomBand.Child=exact?ExactBottom():NearBottom();
            BuildMiddle();
            Grid.SetRow(middle,1);root.Children.Add(middle);
            Grid.SetRow(topBand,0);root.Children.Add(topBand);
            Grid.SetRow(bottomBand,2);root.Children.Add(bottomBand);
            SetFull(false);
            PreviewMouseMove+=(s,e)=>Bars(e.GetPosition(this));
            MouseLeave+=delegate {Bars(new Point(-1,-1));};
            peek.Tick+=delegate {peek.Stop();peeking=false;Bars(Mouse.GetPosition(this));};
            Smoothing();

            PreviewKeyDown+=Keys;
            PreviewKeyUp+=(s,e)=>{var k=e.Key==Key.ImeProcessed?e.ImeProcessedKey:e.Key;if(k==Key.Space){Swap(false);e.Handled=true;}};
            Deactivated+=delegate {Swap(false);};
            StateChanged+=delegate {FitMaximized();};
            SizeChanged+=delegate {Arrange();};
            Loaded+=delegate {FitMaximized();Arrange();Start(group,first,second);};
            // The last choice goes to the Recycle Bin before the window really closes.
            Closing+=async (s,e)=>{
                if(closing || (pending==null && chain.IsCompleted))return;
                e.Cancel=true;closing=true;
                IsEnabled=false;Hide();
                Commit();
                try {await chain;} catch(Exception) {}
                Close();
            };
            Closed+=delegate {
                closed=true;
                Commit(); // only when closed with the folder window (Closing isn't raised then)
                images[0].Source=images[1].Source=null;
            };
        }

        // ----- Building the window -----
        static Button Tool(string label,Action action,string tip) {
            var b=UI.Btn(label,action);b.Focusable=false;b.MinHeight=28;b.Padding=new Thickness(10,2,10,2);b.Margin=new Thickness(6,0,0,0);
            if(tip!=null)b.ToolTip=tip;
            return b;
        }
        UIElement TopBar() {
            var bar=new DockPanel {Height=40,Margin=new Thickness(12,0,12,0),LastChildFill=true};DockPanel.SetDock(bar,Dock.Top);
            var tools=new StackPanel {Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};
            DockPanel.SetDock(tools,Dock.Right);
            fitButton=Tool("",Fit,S("Shows the whole pictures again.","사진 전체가 보이게 되돌려요."));
            actualButton=Tool("",ActualSize,S("The bigger picture at one screen pixel per picture pixel; the other is shown at the same place and scale.","큰 사진을 원래 픽셀 그대로 보여 줘요. 다른 사진도 같은 자리·같은 배율로 보여요."));
            tools.Children.Add(fitButton);tools.Children.Add(actualButton);
            if(!exact) {
                swapButton=Tool("",()=>{},S("While you hold it (or Space), the two pictures change places, so a difference jumps out.","누르고 있는 동안(또는 Space) 두 사진이 자리를 바꿔요. 다른 곳이 눈에 확 띄어요."));
                swapButton.PreviewMouseLeftButtonDown+=delegate {Swap(true);};
                swapButton.PreviewMouseLeftButtonUp+=delegate {Swap(false);};
                swapButton.MouseLeave+=delegate {Swap(false);};
                tools.Children.Add(swapButton);
                pixels.Focusable=false;pixels.SetResourceReference(Control.ForegroundProperty,"Text");pixels.VerticalAlignment=VerticalAlignment.Center;pixels.Margin=new Thickness(10,0,4,0);
                pixels.ToolTip=S("When zoomed in, shows each pixel as a square instead of smoothing it, so blur and blockiness are easy to see.","확대했을 때 부드럽게 다듬지 않고 픽셀을 네모 그대로 보여 줘요. 흐림이나 깍두기 무늬가 잘 보여요.");
                pixels.Checked+=delegate {Smoothing();};pixels.Unchecked+=delegate {Smoothing();};
                tools.Children.Add(pixels);
                infoButton=Tool("",ToggleInfo,S("Shows or hides the details of both files over the pictures. The pictures stay as they are.","두 파일의 자세한 정보를 사진 위에 보여 주거나 숨겨요. 사진 크기와 확대한 자리는 그대로예요."));
                infoButton.Margin=new Thickness(10,0,0,0);
                tools.Children.Add(infoButton);
            }
            bar.Children.Add(tools);
            var words=new StackPanel {VerticalAlignment=VerticalAlignment.Center};
            title.FontSize=14;title.FontWeight=FontWeights.SemiBold;title.TextTrimming=TextTrimming.CharacterEllipsis;title.SetResourceReference(TextBlock.ForegroundProperty,"Text");
            message.FontSize=12;message.TextTrimming=TextTrimming.CharacterEllipsis;message.SetResourceReference(TextBlock.ForegroundProperty,"Subtle");
            words.Children.Add(title);words.Children.Add(message);
            bar.Children.Add(words);
            return bar;
        }
        // A key cap ("1") inside a button.
        static UIElement Cap(string key) {
            var cap=new Border {CornerRadius=new CornerRadius(4),BorderThickness=new Thickness(1),Padding=new Thickness(6,0,6,1),Margin=new Thickness(0,0,0,0),VerticalAlignment=VerticalAlignment.Center};
            cap.SetResourceReference(Border.BorderBrushProperty,"StrongLine");
            var t=new TextBlock {Text=key,FontSize=12,FontWeight=FontWeights.SemiBold};
            cap.Child=t;
            return cap;
        }
        UIElement NearBottom() {
            var bar=new Grid {Height=54,Margin=new Thickness(12,6,12,8)};DockPanel.SetDock(bar,Dock.Bottom);
            foreach(var w in new[]{GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto})
                bar.ColumnDefinitions.Add(new ColumnDefinition {Width=w});
            for(int side=0;side<2;side++) {
                int at=side;
                var face=new StackPanel {Orientation=Orientation.Horizontal};
                keepText[side]=new TextBlock {VerticalAlignment=VerticalAlignment.Center,FontSize=14,FontWeight=FontWeights.SemiBold,Margin=side==0?new Thickness(8,0,0,0):new Thickness(0,0,8,0)};
                if(side==0){face.Children.Add(Cap("1"));face.Children.Add(keepText[side]);}
                else {face.Children.Add(keepText[side]);face.Children.Add(Cap("2"));}
                keepButtons[side]=new Button {Content=face,MinHeight=40,Padding=new Thickness(14,4,14,4),Focusable=false,VerticalAlignment=VerticalAlignment.Center,
                    Style=(Style)Application.Current.Resources["Primary"]};
                keepButtons[side].Click+=delegate {Choose(at);};
                Grid.SetColumn(keepButtons[side],side==0?0:4);bar.Children.Add(keepButtons[side]);
                nameText[side]=new TextBlock {TextTrimming=TextTrimming.CharacterEllipsis,FontSize=12.5,VerticalAlignment=VerticalAlignment.Center};
                var nameFace=new DockPanel();
                var arrow=new TextBlock {Text=" ▾",FontSize=12,VerticalAlignment=VerticalAlignment.Center};DockPanel.SetDock(arrow,Dock.Right);
                nameFace.Children.Add(arrow);nameFace.Children.Add(nameText[side]);
                nameButtons[side]=new Button {Content=nameFace,MinHeight=32,Padding=new Thickness(8,2,8,2),Focusable=false,Margin=new Thickness(8,0,8,0),
                    VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=side==0?HorizontalAlignment.Left:HorizontalAlignment.Right,
                    HorizontalContentAlignment=HorizontalAlignment.Stretch,
                    ToolTip=S("Bring another photo of this group to this side, or open this one in the large view.","이 묶음의 다른 사진을 이쪽으로 불러오거나, 이 사진을 크게 봐요.")};
                nameButtons[side].Click+=delegate {NameMenu(at);};
                Grid.SetColumn(nameButtons[side],side==0?1:3);bar.Children.Add(nameButtons[side]);
            }
            var center=new StackPanel {Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};
            undoButton=Tool("",Undo,S("Puts back the photo you didn't choose last (Ctrl+Z). It goes to the Recycle Bin only with your next choice, the next group, or when this window closes.",
                "방금 고르지 않은 사진을 되돌려요(Ctrl+Z). 그 사진은 다음에 고를 때, 다음 묶음으로 갈 때, 이 창을 닫을 때 휴지통으로 가요."));
            bothButton=Tool("",KeepBoth,S("Keeps both. The one on the side that wasn't kept so far stays too, and the next photo comes in its place.",
                "둘 다 남겨요. 지금까지 남긴 쪽이 아닌 사진도 그대로 두고, 그 자리에 다음 사진이 와요."));
            moreButton=Tool("⋯",MoreMenu,S("More","더 보기"));moreButton.MinWidth=34;
            center.Children.Add(undoButton);center.Children.Add(bothButton);center.Children.Add(moreButton);
            Grid.SetColumn(center,2);bar.Children.Add(center);
            return bar;
        }
        UIElement ExactBottom() {
            var bar=new DockPanel {Height=54,Margin=new Thickness(12,6,12,8),LastChildFill=true};DockPanel.SetDock(bar,Dock.Bottom);
            var right=new StackPanel {Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};DockPanel.SetDock(right,Dock.Right);
            undoButton=Tool("",Undo,S("Takes back the last tidying up (Ctrl+Z). It happens only with the next group or when this window closes.",
                "방금 정리를 취소해요(Ctrl+Z). 정리는 다음 묶음으로 갈 때나 이 창을 닫을 때 해요."));
            skipButton=Tool("",()=>{if(pending!=null && pending.Exact && pending.Group==group)pending=null;NextGroup();},S("Leaves this group as it is and shows the next one.","이 묶음은 그대로 두고 다음 묶음을 보여 줘요."));
            tidyButton=new Button {MinHeight=40,Padding=new Thickness(16,4,16,4),Focusable=false,Margin=new Thickness(10,0,0,0),Style=(Style)Application.Current.Resources["Primary"]};
            tidyButton.Click+=delegate {TidyChosen();};
            right.Children.Add(undoButton);right.Children.Add(skipButton);right.Children.Add(tidyButton);
            bar.Children.Add(right);
            var note=UI.Text(S("The picture is byte for byte the same: choose the folder it stays in. Number key or double-click: keep it there at once.",
                "그림은 바이트까지 똑같아요. 이 사진을 남길 폴더를 고르세요. 숫자 키나 더블클릭이면 바로 그 폴더에 남겨요."),12.5,true);
            note.TextWrapping=TextWrapping.Wrap;note.TextTrimming=TextTrimming.CharacterEllipsis;note.MaxHeight=40;
            bar.Children.Add(note);
            return bar;
        }
        void BuildMiddle() {
            middle.Margin=new Thickness(0);
            middle.ColumnDefinitions.Add(new ColumnDefinition());
            middle.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(exact?0:4)});
            listColumn=new ColumnDefinition {Width=exact?new GridLength(420):new GridLength(1,GridUnitType.Star)};
            middle.ColumnDefinitions.Add(listColumn);
            for(int side=0;side<(exact?1:2);side++) {
                int at=side;
                images[side].SnapsToDevicePixels=true;
                stages[side].Children.Add(images[side]);
                stages[side].RenderTransform=view;
                var holder=new Grid();
                panes[side]=new Border {Child=stages[side],ClipToBounds=true,Cursor=Cursors.SizeAll,
                    ToolTip=S("Wheel: zoom · Drag: move · Double-click: large view","휠: 확대 · 끌기: 이동 · 더블클릭: 크게 보기")};
                ToolTipService.SetInitialShowDelay(panes[side],1500);
                UI.Use(panes[side],Border.BackgroundProperty,"TileBand");
                holder.Children.Add(panes[side]);
                // A small label on the picture: what it is, at a glance.
                pills[side].Background=PillBack;pills[side].CornerRadius=new CornerRadius(6);pills[side].Padding=new Thickness(9,4,9,5);
                pills[side].Margin=new Thickness(10);pills[side].HorizontalAlignment=HorizontalAlignment.Left;pills[side].VerticalAlignment=VerticalAlignment.Top;
                pills[side].IsHitTestVisible=false;
                pillText[side].Foreground=White;pillText[side].FontSize=12.5;pillText[side].TextTrimming=TextTrimming.CharacterEllipsis;
                pills[side].Child=pillText[side];
                holder.Children.Add(pills[side]);
                coverBoxes[side].HorizontalAlignment=HorizontalAlignment.Left;coverBoxes[side].VerticalAlignment=VerticalAlignment.Top;
                coverBoxes[side].Margin=new Thickness(10,46,10,10);
                holder.Children.Add(coverBoxes[side]);
                Grid.SetColumn(holder,side*2);
                panes[side].MouseWheel+=(s,e)=>Zoom(e.Delta>0?1.25:0.8,e.GetPosition(panes[at]));
                panes[side].MouseLeftButtonDown+=(s,e)=>{
                    if(e.ClickCount==2){dragging=false;panes[at].ReleaseMouseCapture();Large(at);e.Handled=true;return;}
                    dragging=true;dragFrom=e.GetPosition(panes[at]);panes[at].CaptureMouse();
                };
                panes[side].MouseMove+=(s,e)=>{
                    if(!dragging)return;
                    var now=e.GetPosition(panes[at]);
                    var m=view.Matrix;m.Translate(now.X-dragFrom.X,now.Y-dragFrom.Y);view.Matrix=m;dragFrom=now;
                };
                panes[side].MouseLeftButtonUp+=(s,e)=>{dragging=false;panes[at].ReleaseMouseCapture();};
                panes[side].LostMouseCapture+=(s,e)=>{dragging=false;};
                middle.Children.Add(holder);
            }
            if(exact) {
                var side=new DockPanel {Margin=new Thickness(12,8,12,8)};
                UI.Use(side,Panel.BackgroundProperty,"Paper");
                var head=UI.Text(S("Which folder should this photo stay in?","이 사진을 어느 폴더에 남길까요?"),15);head.FontWeight=FontWeights.SemiBold;head.Margin=new Thickness(2,0,0,8);
                DockPanel.SetDock(head,Dock.Top);side.Children.Add(head);
                side.Children.Add(new ScrollViewer {Content=list,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,Focusable=false});
                Grid.SetColumn(side,2);middle.Children.Add(side);
            } else {
                // The details over the bottom of both pictures (Tab), so the pictures don't move.
                info.Background=OverlayBack;info.Padding=new Thickness(16,10,16,12);info.VerticalAlignment=VerticalAlignment.Bottom;info.Visibility=Visibility.Collapsed;
                Grid.SetColumnSpan(info,3);
                var inner=new DockPanel();
                var top=new DockPanel {Margin=new Thickness(0,0,0,6)};DockPanel.SetDock(top,Dock.Top);
                var more=new Button {Focusable=false,MinHeight=24,Padding=new Thickness(10,0,10,0),FontSize=12};DockPanel.SetDock(more,Dock.Right);
                more.Click+=delegate {allInfo=!allInfo;ShowInfo();};
                more.Loaded+=delegate {more.Content=allInfo?S("Fewer details","간단히 보기"):S("All details","모든 정보 보기");};
                top.Children.Add(more);
                var head=new TextBlock {Text=S("Details · Tab hides them","자세한 정보 · Tab을 누르면 숨겨요"),Foreground=Dim,FontSize=12,VerticalAlignment=VerticalAlignment.Center};
                top.Children.Add(head);
                inner.Children.Add(top);
                infoGrid.ColumnDefinitions.Add(new ColumnDefinition());infoGrid.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(24)});infoGrid.ColumnDefinitions.Add(new ColumnDefinition());
                inner.Children.Add(new ScrollViewer {Content=infoGrid,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,Focusable=false});
                info.Child=inner;info.Tag=more;
                middle.Children.Add(info);
            }
            // The card in the middle when a group is done.
            card.HorizontalAlignment=HorizontalAlignment.Center;card.VerticalAlignment=VerticalAlignment.Center;card.MaxWidth=480;card.Margin=new Thickness(16);
            card.CornerRadius=new CornerRadius(12);card.Padding=new Thickness(26,22,26,20);card.BorderThickness=new Thickness(1);card.Visibility=Visibility.Collapsed;
            UI.Use(card,Border.BackgroundProperty,"Panel");UI.Use(card,Border.BorderBrushProperty,"StrongLine");
            card.Effect=new System.Windows.Media.Effects.DropShadowEffect {BlurRadius=24,ShadowDepth=4,Opacity=0.45,Color=Colors.Black};
            Grid.SetColumnSpan(card,3);
            middle.Children.Add(card);
        }

        // Labels that fit the window: shorter when it is narrow (two windows side by side).
        void Arrange() {
            compact=ActualWidth>0 && ActualWidth<1100;
            fitButton.Content=S("Fit","화면에 맞춤");
            actualButton.Content=compact?"100%":S("Actual size (100%)","실제 크기(100%)");
            if(!exact) {
                swapButton.Content=compact?"⇄":S("⇄ Hold to swap  Space","⇄ 서로 바꿔 보기  Space");
                pixels.Content=compact?S("Pixels","픽셀"):S("Show pixels","픽셀 그대로");
                infoButton.Content=showInfo?(compact?S("ⓘ Hide  Tab","ⓘ 숨기기  Tab"):S("ⓘ Hide details  Tab","ⓘ 정보 숨기기  Tab"))
                    :(compact?S("ⓘ Details  Tab","ⓘ 정보  Tab"):S("ⓘ Show details  Tab","ⓘ 정보 보기  Tab"));
                keepText[0].Text=compact?S("Keep","남기기"):S("Keep the left one","왼쪽 남기기");
                keepText[1].Text=compact?S("Keep","남기기"):S("Keep the right one","오른쪽 남기기");
                undoButton.Content=compact?"↩":S("↩ Undo","↩ 되돌리기");
                bothButton.Content=compact?S("Both","둘 다"):S("Keep both","둘 다 남기기");
                info.MaxHeight=Math.Max(120,middle.ActualHeight*0.5);
            } else {
                undoButton.Content=compact?"↩":S("↩ Undo","↩ 되돌리기");
                skipButton.Content=compact?S("Skip","건너뛰기"):S("Skip this group","이 묶음 건너뛰기");
                listColumn.Width=new GridLength(Math.Max(260,Math.Min(460,ActualWidth*0.4)));
            }
            UpdateTidyButton();
        }
        // A maximized window with its own title bar reaches past the screen by the frame width: keep everything inside.
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
        void SetFull(bool on) {
            full=on;
            Grid.SetRow(middle,on?0:1);Grid.SetRowSpan(middle,on?3:1);
            topBand.SetResourceReference(Panel.BackgroundProperty,on?"CardBack":"Paper");
            bottomBand.SetResourceReference(Border.BackgroundProperty,on?"CardBack":"Paper");
            FitMaximized();
            if(on){peeking=true;peek.Stop();peek.Start();} // shown for a moment, so it's clear where they are
            Bars(IsLoaded?Mouse.GetPosition(this):new Point(-1,-1));
        }
        // In full screen a bar shows while the mouse is near its edge (or a menu from it is open).
        void Bars(Point pointer) {
            if(!full || peeking){topBand.Visibility=bottomBand.Visibility=Visibility.Visible;return;}
            bool inside=new Rect(RenderSize).Contains(pointer);
            double topZone=Math.Max(70,topBand.Visibility==Visibility.Visible?topBand.ActualHeight:0);
            double bottomZone=Math.Max(70,bottomBand.Visibility==Visibility.Visible?bottomBand.ActualHeight:0);
            bool nearTop=inside && pointer.Y<=topZone,nearBottom=inside && ActualHeight-pointer.Y<=bottomZone;
            topBand.Visibility=nearTop || topBand.IsMouseCaptureWithin?Visibility.Visible:Visibility.Collapsed;
            bottomBand.Visibility=nearBottom || menuOpen || bottomBand.IsMouseCaptureWithin?Visibility.Visible:Visibility.Collapsed;
        }
        void FitMaximized() {
            if(WindowState!=WindowState.Maximized || FullScreen.IsFull(this)){root.Margin=new Thickness(0);return;}
            double scale=1;
            try {scale=VisualTreeHelper.GetDpi(this).DpiScaleX;} catch(Exception) {}
            double frame=(GetSystemMetrics(32)+GetSystemMetrics(92))/scale; // SM_CXSIZEFRAME + SM_CXPADDEDBORDER
            root.Margin=new Thickness(frame);
        }

        // ----- A group -----
        void Start(SimilarGroup next,SimilarFile first,SimilarFile second) {
            group=next;groupsSeen++;
            Swap(false);Fit();
            foreach(var f in group.Files)seen.Add(f);
            finished=false;card.Visibility=Visibility.Collapsed;
            if(exact) {
                copies=group.Files.Where(f=>File.Exists(f.Path) && !going.Contains(f)).ToList();
                chosen=null; // nothing chosen beforehand: for each photo the person picks the folder it stays in
                slots[0]=copies.FirstOrDefault(f=>f.IsPicture)??chosen;
                Load(0);
                ReadDatesThen(copies,FillList);
                FillList();
            } else {
                var pictures=group.Files.Where(f=>f.IsPicture && File.Exists(f.Path) && !going.Contains(f)).ToList();
                videos=group.Files.Count(f=>!f.IsPicture);
                total=pictures.Count;picks=0;champion=0;
                trashed.Clear();keptToo.Clear();waiting.Clear();
                var left=pictures.Contains(group.Keep)?group.Keep:pictures.FirstOrDefault();
                SimilarFile right;
                if(first!=null && second!=null && pictures.Contains(first) && pictures.Contains(second)) {
                    left=second==group.Keep?second:first;right=left==first?second:first;
                } else right=first!=null && first!=left && pictures.Contains(first)?first:pictures.FirstOrDefault(f=>f!=left);
                slots[0]=left;slots[1]=right;arrived[0]=arrived[1]=false;
                waiting.AddRange(pictures.Where(f=>f!=left && f!=right));
                Fit();
                Load(0);Load(1);
                ReadDatesThen(pictures,()=>{ShowInfo();ShowPills();});
                if(right==null) {
                    Say(null);Refresh();
                    ShowCard(S("Nothing to compare","비교할 사진이 없어요"),S("Only one photo of this group is left.","이 묶음에는 사진이 하나만 남았어요."),false,true);
                    Refresh();
                    return;
                }
            }
            Say(null);
            Refresh();
        }
        void ReadDatesThen(List<SimilarFile> files,Action then) {
            var unread=files.Where(f=>!f.DatesRead).ToList();
            if(unread.Count==0)return;
            Task.Run(()=>{foreach(var f in unread)try {SimilarInfo.ReadDates(f);} catch(Exception) {}})
                .ContinueWith(t=>{if(!closed)then();},TaskScheduler.FromCurrentSynchronizationContext());
        }
        // The next group of the same kind with a file not shown yet in this window.
        SimilarGroup NextCandidate() {
            var groups=exact?SimilarSearch.Exact:SimilarSearch.Near;
            foreach(var g in groups.ToList()) {
                var here=g.Files.Where(f=>File.Exists(f.Path) && !going.Contains(f) && (exact || f.IsPicture)).ToList();
                if(here.Count<2 || here.All(f=>seen.Contains(f)))continue;
                return g;
            }
            return null;
        }
        void NextGroup() {
            Commit();
            var next=NextCandidate();
            if(next==null) {
                ShowCard(S("You've seen every group","모든 묶음을 다 봤어요"),
                    S("There's no other group to compare in this window.","이 창에서 더 비교할 묶음이 없어요."),false,false);
                return;
            }
            Start(next,null,null);
        }

        // ----- Nearly the same: choosing -----
        void Choose(int side) {
            // Not while the pictures are swapped (what is seen on a side isn't that side's file) or not shown yet.
            if(exact || finished || swapped || DateTime.Now<calmUntil || !ready[0] || !ready[1])return;
            var winner=slots[side];var loser=slots[1-side];
            if(winner==null || loser==null)return;
            // Its covers move to the one that stays: their frame is kept as fractions of the picture, so it fits when the shape
            // is the same. A cut copy (another shape) or an animated one would frame differently: asked first.
            bool move=false,dropCovers=false;
            bool covered=loser.HasCover || gettingCovers.Contains(loser) || (pending!=null && pending.MoveCovers && pending.Stays==loser);
            if(covered) {
                bool animated=AnimationFrames.Supports(loser.Path) || AnimationFrames.Supports(winner.Path);
                bool fits=SimilarInfo.SameShape(loser,winner) && !animated;
                int answer=coverAnswer==2 || (coverAnswer==1 && fits)?coverAnswer:AskCovers(loser,winner,fits,animated);
                if(answer==0)return;
                // Something may have changed while the question was open.
                if(closed || finished || swapped || slots[side]!=winner || slots[1-side]!=loser)return;
                move=answer==1;dropCovers=answer==2;
            }
            Commit();
            Swap(false);
            pending=new Pending {File=loser,Stays=winner,Side=1-side,Champion=champion,Group=group,MoveCovers=move,TrashCovers=dropCovers};
            trashed.Add(loser);
            champion=side;picks++;
            var next=TakeNext();
            pending.Arrived=next;
            slots[1-side]=next;arrived[1-side]=next!=null;arrived[side]=false;
            calmUntil=DateTime.Now.AddMilliseconds(250); // a second press right away doesn't hit the new photo
            Load(1-side); // empty when nothing is left
            Say(L.PF("“{0}” goes to the Recycle Bin{1}. Changed your mind? ↩ Undo (Ctrl+Z)","‘{0}’ 휴지통으로 보내요{1}. 마음이 바뀌면 ↩ 되돌리기(Ctrl+Z)",loser.Name,
                move?L.P(", its covers move to the one you keep",", 표지는 남기는 사진으로 옮겨요"):dropCovers?L.P(" with its covers"," (표지도 함께)"):""));
            if(next==null)Finish();
            Refresh();
        }
        // 0 = not yet asked or "ask again"; 1 = move the covers; 2 = covers to the Recycle Bin too (for the rest of this window).
        int coverAnswer;
        // What happens to the covers of the photo that goes: moved to the one kept, or to the Recycle Bin with it
        // (to make a new cover from the bigger original). 0 = cancel.
        int AskCovers(SimilarFile loser,SimilarFile winner,bool fits,bool animated) {
            int count=Math.Max(1,owner.SimilarCoversOf(loser).Count);
            int result=0;
            var ask=new Window {SizeToContent=SizeToContent.WidthAndHeight,ResizeMode=ResizeMode.NoResize,Owner=this,ShowInTaskbar=false};
            UI.WindowSetup(ask,S("Its covers","표지는 어떻게 할까요?"));ask.Owner=this;ask.WindowStartupLocation=WindowStartupLocation.CenterOwner;
            var root=new DockPanel();root.Children.Add(new WindowTitleBar(ask));UI.Use(root,Panel.BackgroundProperty,"Paper");
            var body=new StackPanel {Margin=new Thickness(22,10,22,18),MaxWidth=520};
            var head=UI.Text(L.PF("“{0}” has {1:N0} covers.","‘{0}’에는 표지가 {1:N0}장 있어요.",loser.Name,count),15);head.FontWeight=FontWeights.SemiBold;head.TextWrapping=TextWrapping.Wrap;
            body.Children.Add(head);
            var note=UI.Text(S("It goes to the Recycle Bin. What about its covers?","이 사진은 휴지통으로 가요. 표지는 어떻게 할까요?"),13.5);note.TextWrapping=TextWrapping.Wrap;note.Margin=new Thickness(0,6,0,14);
            body.Children.Add(note);
            Func<string,string,int,bool,Button> choice=(title,detail,value,primary)=>{
                var face=new StackPanel {Margin=new Thickness(4,2,4,3)};
                var t=new TextBlock {Text=title,FontSize=14,FontWeight=FontWeights.SemiBold};
                var d=new TextBlock {Text=detail,FontSize=12.5,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,2,0,0),Opacity=0.85};
                face.Children.Add(t);face.Children.Add(d);
                var b=new Button {Content=face,HorizontalContentAlignment=HorizontalAlignment.Left,Padding=new Thickness(12,6,12,6),Margin=new Thickness(0,0,0,8),MinHeight=48};
                if(primary){b.Style=(Style)Application.Current.Resources["Primary"];b.IsDefault=true;}
                b.Click+=delegate {result=value;ask.Close();};
                return b;
            };
            body.Children.Add(choice(S("Move them to the photo you keep","남기는 사진으로 표지 옮기기"),
                fits?L.PF("Same frames on “{0}”. You can redo them later with [Edit cover] for a sharper cover.","‘{0}’에서 같은 구도로 이어져요. 더 선명하게 하려면 나중에 [표지 수정]으로 다시 저장하면 돼요.",winner.Name)
                    :(animated?S("One of them moves, so a frame may not fit exactly.","움직이는 그림이라 구도가 딱 맞지 않을 수 있어요.")
                        :S("The two aren't the same shape (maybe cut), so the frame may move a little.","두 사진의 모양이 달라서(잘린 사본일 수 있어요) 구도가 조금 어긋날 수 있어요.")),1,fits));
            body.Children.Add(choice(S("Covers to the Recycle Bin too","표지도 같이 휴지통으로"),
                S("For making new covers from the bigger, better original. You can get them back from the Recycle Bin.","더 크고 좋은 원본으로 표지를 새로 만들 때 좋아요. 휴지통에서 되살릴 수 있어요."),2,!fits));
            var remember=new CheckBox {Content=S("Do the same for the rest of this window without asking","이 창에서는 다시 묻지 않고 같은 답으로 하기"),Margin=new Thickness(2,4,0,12)};
            remember.SetResourceReference(Control.ForegroundProperty,"Text");
            remember.ToolTip=S("“Move” is still asked when the shapes differ.","모양이 다른 사진은 ‘옮기기’여도 다시 물어봐요.");
            body.Children.Add(remember);
            var cancel=UI.Btn(S("Cancel (choose again)  Esc","취소(다시 고르기)  Esc"),()=>{result=0;ask.Close();});
            cancel.IsCancel=true;cancel.HorizontalAlignment=HorizontalAlignment.Right;cancel.Padding=new Thickness(12,2,12,2);
            body.Children.Add(cancel);
            root.Children.Add(body);ask.Content=root;
            ask.ShowDialog();
            if(result!=0 && remember.IsChecked==true)coverAnswer=result;
            return result;
        }
        SimilarFile TakeNext() {
            while(waiting.Count>0) {
                var f=waiting[0];waiting.RemoveAt(0);
                if(File.Exists(f.Path))return f;
            }
            return null;
        }
        void KeepBoth() {
            if(exact || finished)return;
            int other=1-champion;
            if(slots[other]==null)return;
            var mine=slots[champion];var file=slots[other];
            // Whether these two show up together again in the similar photos (asked; can be answered once for the window).
            int answer=bothAnswer!=0?bothAnswer:AskBoth(mine,file);
            if(answer==0)return;
            if(closed || finished || slots[other]!=file || slots[champion]!=mine)return;
            Commit();
            keptToo.Add(file);
            if(answer==2 && mine!=null)SimilarSearch.NotSame(new SimilarGroup {Files=new List<SimilarFile>{mine,file}},null);
            Bring(other,answer==2?S("Both stay, and these two won't be grouped together again.","둘 다 남기고, 앞으로 이 두 장은 같이 묶지 않아요."):S("Both stay (only this time).","둘 다 남겨요(이번만)."));
        }
        // 0 = ask; 1 = keep both only this time; 2 = keep both and don't group them together again.
        int bothAnswer;
        int AskBoth(SimilarFile mine,SimilarFile file) {
            int result=0;
            var ask=new Window {SizeToContent=SizeToContent.WidthAndHeight,ResizeMode=ResizeMode.NoResize,ShowInTaskbar=false};
            UI.WindowSetup(ask,S("Keep both","둘 다 남기기"));ask.Owner=this;ask.WindowStartupLocation=WindowStartupLocation.CenterOwner;
            var root=new DockPanel();root.Children.Add(new WindowTitleBar(ask));UI.Use(root,Panel.BackgroundProperty,"Paper");
            var body=new StackPanel {Margin=new Thickness(22,10,22,18),MaxWidth=520};
            var head=UI.Text(S("Both photos stay.","두 사진을 모두 남겨요."),15);head.FontWeight=FontWeights.SemiBold;
            body.Children.Add(head);
            var note=UI.Text(S("Should these two show up together in the similar photos again?","다음에 찾을 때 이 두 장을 ‘비슷한 사진 모음’에 또 같이 보여 줄까요?"),13.5);
            note.TextWrapping=TextWrapping.Wrap;note.Margin=new Thickness(0,6,0,14);
            body.Children.Add(note);
            Func<string,string,int,bool,Button> choice=(title,detail,value,primary)=>{
                var face=new StackPanel {Margin=new Thickness(4,2,4,3)};
                face.Children.Add(new TextBlock {Text=title,FontSize=14,FontWeight=FontWeights.SemiBold});
                face.Children.Add(new TextBlock {Text=detail,FontSize=12.5,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,2,0,0),Opacity=0.85});
                var b=new Button {Content=face,HorizontalContentAlignment=HorizontalAlignment.Left,Padding=new Thickness(12,6,12,6),Margin=new Thickness(0,0,0,8),MinHeight=48};
                if(primary){b.Style=(Style)Application.Current.Resources["Primary"];b.IsDefault=true;}
                b.Click+=delegate {result=value;ask.Close();};
                return b;
            };
            body.Children.Add(choice(S("Don't show them together again","앞으로 같이 보여 주지 않기"),
                S("For photos that only look alike (the same pose, another shot). The other photos of the group can still be grouped with them.",
                  "구도만 비슷한 다른 사진일 때 좋아요. 이 두 장끼리만 다시 묶지 않고, 묶음의 다른 사진과는 그대로 비교해요."),2,true));
            body.Children.Add(choice(S("Only this time","이번만 둘 다 남기기"),
                S("They show up together again next time you look.","다음에 찾으면 또 같은 묶음으로 나와요."),1,false));
            var remember=new CheckBox {Content=S("Do the same for the rest of this window without asking","이 창에서는 다시 묻지 않고 같은 답으로 하기"),Margin=new Thickness(2,4,0,12)};
            remember.SetResourceReference(Control.ForegroundProperty,"Text");
            body.Children.Add(remember);
            var cancel=UI.Btn(S("Cancel  Esc","취소  Esc"),()=>{result=0;ask.Close();});
            cancel.IsCancel=true;cancel.HorizontalAlignment=HorizontalAlignment.Right;cancel.Padding=new Thickness(12,2,12,2);
            body.Children.Add(cancel);
            root.Children.Add(body);ask.Content=root;
            ask.ShowDialog();
            if(result!=0 && remember.IsChecked==true)bothAnswer=result;
            return result;
        }
        void NotSame() {
            if(exact || finished)return;
            int other=1-champion;
            var file=slots[other];
            if(file==null)return;
            Commit();
            SimilarSearch.NotSame(group,file);
            keptToo.Add(file);
            Bring(other,L.PF("“{0}” stays and won't be grouped with these again.","‘{0}’는 남기고, 앞으로 이 사진들과 묶지 않아요.",file.Name));
        }
        // The next waiting photo comes to this side (or the group is done).
        void Bring(int side,string said) {
            Swap(false);
            var next=TakeNext();
            calmUntil=DateTime.Now.AddMilliseconds(250);
            // When nothing is waiting, the photo kept too stays in sight (it isn't gone; an empty side looked like it was).
            if(next!=null){slots[side]=next;arrived[side]=true;Load(side);}
            else arrived[side]=false;
            Say(said);
            if(next==null)Finish();
            Refresh();
        }
        void Finish() {
            finished=true;
            // Every photo that stays, by name, so "kept both" reads as both.
            var kept=new List<SimilarFile>();
            if(slots[champion]!=null)kept.Add(slots[champion]);
            foreach(var f in keptToo)if(!kept.Contains(f))kept.Add(f);
            string names=String.Join("\n",kept.Take(4).Select(f=>"  · "+f.Name));
            if(kept.Count>4)names+="\n  · "+L.PF("and {0:N0} more","그 밖에 {0:N0}장",kept.Count-4);
            string body=L.PF("Kept: {0:N0}","남긴 사진: {0:N0}장",kept.Count)+"\n"+names;
            body+="\n"+(trashed.Count==0?S("Nothing went to the Recycle Bin.","휴지통으로 보낸 사진은 없어요.")
                :L.PF("To the Recycle Bin: {0:N0} (you can get them back from the Recycle Bin)","휴지통으로: {0:N0}장 (휴지통에서 되살릴 수 있어요)",trashed.Count));
            if(videos>0)body+="\n"+L.PF("{0:N0} videos of this group weren't compared: check them in the folder window.","이 묶음의 영상 {0:N0}개는 비교하지 않았어요. 폴더 창에서 확인해 주세요.",videos);
            ShowCard(S("This group is done","이 묶음 정리가 끝났어요"),body,pending!=null,true);
        }
        void Undo() {
            if(pending==null){Say(S("There's nothing to take back.","되돌릴 게 없어요."));return;}
            var p=pending;pending=null;
            card.Visibility=Visibility.Collapsed;finished=false;
            if(p.Exact){Say(S("Taken back: nothing was tidied up.","취소했어요. 아무것도 정리하지 않았어요."));Refresh();return;}
            Swap(false);
            if(slots[p.Side]!=null && slots[p.Side]!=p.File)waiting.Insert(0,slots[p.Side]); // the one that came in its place waits again
            slots[p.Side]=p.File;arrived[p.Side]=false;
            trashed.Remove(p.File);champion=p.Champion;picks=Math.Max(0,picks-1);
            Load(p.Side);
            Say(L.PF("“{0}” is back.","‘{0}’를 되돌렸어요.",p.File.Name));
            Refresh();
        }
        // The file not chosen last goes to the Recycle Bin now; one after the other, never two at once.
        void Commit() {
            if(pending==null)return;
            var p=pending;pending=null;
            if(p.Exact)foreach(var f in p.Copies)going.Add(f);else going.Add(p.File);
            if(!p.Exact && p.MoveCovers)gettingCovers.Add(p.Stays);
            chain=Run(chain,p);
            Refresh();
        }
        async Task Run(Task before,Pending p) {
            try {await before;} catch(Exception) {}
            string problem;
            try {problem=p.Exact?await owner.SimilarTidyNow(p.Group,p.Stays,p.Copies):await owner.SimilarTrashNow(p.File,p.Stays,p.MoveCovers,p.TrashCovers);}
            catch(Exception ex) {problem=ex.Message;}
            if(p.Exact)foreach(var f in p.Copies)going.Remove(f);else going.Remove(p.File);
            if(!p.Exact && p.MoveCovers)gettingCovers.Remove(p.Stays);
            if(problem==null)return;
            if(!p.Exact && p.Group==group)trashed.Remove(p.File);
            string text=p.Exact?L.PF("This group wasn't tidied up: {0}","이 묶음은 정리하지 못했어요: {0}",problem)
                :L.PF("“{0}” didn't go to the Recycle Bin: {1}","‘{0}’는 휴지통으로 보내지 못했어요: {1}",p.File.Name,problem);
            try {owner.SimilarTell(text);} catch(Exception) {} // a notice that stays, not a line the next choice replaces
            if(!closed)Say(text);
        }

        // ----- Exactly the same: choosing a folder -----
        void FillList() {
            list.Children.Clear();
            for(int i=0;i<copies.Count;i++) {
                var f=copies[i];
                var file=f;
                bool on=f==chosen;
                var box=new Border {CornerRadius=new CornerRadius(8),BorderThickness=new Thickness(on?2:1),Padding=new Thickness(12,9,12,10),Margin=new Thickness(0,0,0,8),Cursor=Cursors.Hand};
                UI.Use(box,Border.BackgroundProperty,on?"Raised":"Panel");
                box.BorderBrush=on?Blue:null;if(!on)box.SetResourceReference(Border.BorderBrushProperty,"Line");
                var lines=new StackPanel();
                var head=new DockPanel();
                // Its number: pressing it keeps the photo in this folder.
                var number=new Border {CornerRadius=new CornerRadius(5),BorderThickness=new Thickness(1),Padding=new Thickness(7,0,7,1),Margin=new Thickness(0,0,10,0),VerticalAlignment=VerticalAlignment.Top,MinWidth=24};
                if(on){number.Background=Blue;number.BorderBrush=Blue;}else number.SetResourceReference(Border.BorderBrushProperty,"StrongLine");
                var digit=new TextBlock {Text=i<9?(i+1).ToString():"·",FontSize=13,FontWeight=FontWeights.SemiBold,HorizontalAlignment=HorizontalAlignment.Center};
                if(on)digit.Foreground=Brushes.White;else digit.SetResourceReference(TextBlock.ForegroundProperty,"Text");
                number.Child=digit;
                DockPanel.SetDock(number,Dock.Left);head.Children.Add(number);
                if(f==group.Keep) {
                    var star=new Border {CornerRadius=new CornerRadius(5),Padding=new Thickness(6,1,6,2),Background=Blue,Margin=new Thickness(8,0,0,0),VerticalAlignment=VerticalAlignment.Top};
                    star.Child=new TextBlock {Text=S("★ Suggested","★ 추천"),FontSize=11,FontWeight=FontWeights.SemiBold,Foreground=Brushes.White};
                    star.ToolTip=S("Only a suggestion (a cover, otherwise not in Downloads, Desktop or a temporary folder, otherwise the oldest). Choose the folder you like.","추천일 뿐이에요(표지가 있는 것 → 다운로드·바탕 화면·임시 폴더가 아닌 것 → 오래된 것). 원하는 폴더를 고르세요.");
                    DockPanel.SetDock(star,Dock.Right);head.Children.Add(star);
                }
                var folder=UI.Text(f.Folder,14);folder.FontWeight=FontWeights.SemiBold;folder.TextWrapping=TextWrapping.Wrap;
                head.Children.Add(folder);
                lines.Children.Add(head);
                var name=UI.Text(f.Name,12.5);name.TextTrimming=TextTrimming.CharacterEllipsis;name.Margin=new Thickness(34,3,0,0);
                lines.Children.Add(name);
                string dates=L.PF("Modified {0}","수정 {0}",SimilarInfo.Modified(f).ToString("yyyy-MM-dd HH:mm"));
                if(f.Created>DateTime.MinValue)dates+=" · "+L.PF("came here {0}","여기 생긴 날 {0}",f.Created.ToString("yyyy-MM-dd"));
                var when=UI.Text(dates,12,true);when.TextWrapping=TextWrapping.Wrap;when.Margin=new Thickness(34,2,0,0);
                lines.Children.Add(when);
                if(f.HasCover) {
                    var cover=new TextBlock {Text=S("Has a cover: it moves to the file that stays","표지 있음 · 남는 파일로 옮겨요"),FontSize=12,Foreground=Orange,FontWeight=FontWeights.SemiBold,Margin=new Thickness(34,3,0,0)};
                    lines.Children.Add(cover);
                }
                box.Child=lines;
                box.MouseLeftButtonDown+=(s,e)=>{
                    if(finished)return;
                    if(chosen!=file){chosen=file;FillList();}
                    if(e.ClickCount==2)TidyChosen(); // double-click: keep it here at once
                };
                list.Children.Add(box);
            }
            UpdateTidyButton();
        }
        void UpdateTidyButton() {
            if(tidyButton==null)return;
            int rest=Math.Max(0,copies.Count-1);
            var face=new StackPanel {Orientation=Orientation.Horizontal};
            var t=new TextBlock {Text=chosen==null?S("Choose the folder first","남길 폴더를 먼저 골라 주세요")
                    :compact?L.PF("Keep here, {0:N0} to the bin","여기 남기기 · {0:N0}개 휴지통",rest):L.PF("Keep it in this folder, {0:N0} copies to the Recycle Bin","이 폴더에 남기고 사본 {0:N0}개 휴지통",rest),
                FontSize=14,FontWeight=FontWeights.SemiBold,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,8,0)};
            face.Children.Add(t);face.Children.Add(Cap("Enter"));
            tidyButton.Content=face;
            tidyButton.IsEnabled=chosen!=null && rest>0 && !finished;
        }
        void TidyChosen() {
            if(!exact || finished || chosen==null || DateTime.Now<calmUntil)return;
            var rest=copies.Where(f=>f!=chosen).ToList();
            if(rest.Count==0)return;
            Commit();
            pending=new Pending {Exact=true,Group=group,Stays=chosen,Copies=rest};
            int covers=rest.Count(f=>f.HasCover);
            string body=L.PF("Stays: {0}\n{1}","남는 파일: {0}\n{1}",chosen.Name,chosen.Folder)
                +"\n\n"+L.PF("{0:N0} copies go to the Recycle Bin when you go on to the next group or close this window.","사본 {0:N0}개는 다음 묶음으로 가거나 이 창을 닫을 때 휴지통으로 보내요.",rest.Count);
            if(covers>0)body+="\n"+L.PF("The covers of {0:N0} copies move to the file that stays.","사본 {0:N0}개의 표지는 남는 파일로 옮겨요.",covers);
            calmUntil=DateTime.Now.AddMilliseconds(250);
            ShowCard(S("Ready to tidy up","정리할 준비가 됐어요"),body,true,true);
            Refresh();
        }

        // ----- The card when a group is done -----
        void ShowCard(string heading,string body,bool canUndo,bool canGoOn) {
            finished=true;
            var panel=new StackPanel();
            var h=UI.Text(heading,18);h.FontWeight=FontWeights.SemiBold;h.Margin=new Thickness(0,0,0,10);panel.Children.Add(h);
            var b=UI.Text(body,13.5);b.TextWrapping=TextWrapping.Wrap;b.LineHeight=21;panel.Children.Add(b);
            var buttons=new WrapPanel {Margin=new Thickness(0,18,0,0),HorizontalAlignment=HorizontalAlignment.Right};
            if(canUndo){var u=Tool(S("↩ Undo  Ctrl+Z","↩ 되돌리기  Ctrl+Z"),Undo,null);u.Margin=new Thickness(0,0,8,6);buttons.Children.Add(u);}
            var close=Tool(S("Close  Esc","닫기  Esc"),Close,null);close.Margin=new Thickness(0,0,8,6);buttons.Children.Add(close);
            if(canGoOn) {
                bool any=NextCandidate()!=null;
                var go=Tool(any?S("Next group →  Enter · Space","다음 묶음 →  Enter · Space"):S("Done  Enter · Space","마치기  Enter · Space"),()=>{if(any)NextGroup();else Close();},null);
                go.Style=(Style)Application.Current.Resources["Primary"];go.MinHeight=34;go.Margin=new Thickness(0,0,0,6);go.FontWeight=FontWeights.SemiBold;
                buttons.Children.Add(go);
            }
            panel.Children.Add(buttons);
            card.Child=panel;card.Visibility=Visibility.Visible;
        }

        // ----- Keys -----
        void Keys(object sender,KeyEventArgs e) {
            if(e.Handled)return;
            var key=e.Key==Key.ImeProcessed?e.ImeProcessedKey:e.Key;
            var modifiers=Keyboard.Modifiers;
            if(key!=Key.Z && key!=Key.Escape && modifiers!=ModifierKeys.None)return;
            switch(key) {
                case Key.Tab:
                    if(!exact && !e.IsRepeat)ToggleInfo();
                    e.Handled=true;break;
                case Key.Space:
                    // On the card at the end of a group, Space goes on like Enter; otherwise it swaps while held.
                    if(finished && card.Visibility==Visibility.Visible) {
                        if(!e.IsRepeat && DateTime.Now>=calmUntil){if(NextCandidate()!=null)NextGroup();else Close();}
                    }
                    else if(!exact && !e.IsRepeat && !finished)Swap(true);
                    e.Handled=true;break;
                case Key.D1:case Key.NumPad1:case Key.D2:case Key.NumPad2:case Key.D3:case Key.NumPad3:case Key.D4:case Key.NumPad4:
                case Key.D5:case Key.NumPad5:case Key.D6:case Key.NumPad6:case Key.D7:case Key.NumPad7:case Key.D8:case Key.NumPad8:case Key.D9:case Key.NumPad9: {
                    int n=key>=Key.NumPad1 && key<=Key.NumPad9?key-Key.NumPad1:key-Key.D1;
                    if(!e.IsRepeat) {
                        if(exact) {
                            // Exactly the same files: the number keeps the photo in that folder at once.
                            if(!finished && n<copies.Count && DateTime.Now>=calmUntil){chosen=copies[n];FillList();TidyChosen();}
                        } else if(n<2)Choose(n);
                    }
                    e.Handled=true;break;
                }
                case Key.Z:
                    if(modifiers==ModifierKeys.Control && !e.IsRepeat){Undo();e.Handled=true;}
                    break;
                case Key.Enter:
                    if(e.IsRepeat || DateTime.Now<calmUntil){e.Handled=true;break;}
                    if(finished && card.Visibility==Visibility.Visible){if(NextCandidate()!=null)NextGroup();else Close();}
                    else if(exact)TidyChosen();
                    e.Handled=true;break;
                case Key.Up:case Key.Down:
                    if(exact && !finished && copies.Count>0) {
                        int i=chosen==null?(key==Key.Down?0:copies.Count-1):copies.IndexOf(chosen)+(key==Key.Down?1:-1);
                        chosen=copies[Math.Max(0,Math.Min(copies.Count-1,i))];FillList();
                        e.Handled=true;
                    }
                    break;
                case Key.Escape:
                    Close();e.Handled=true;break;
            }
        }

        // ----- What is shown -----
        void Say(string text) {
            message.Text=text??(exact
                ?S("Which folder should this photo stay in? Press its number (or double-click it): the copies go to the Recycle Bin and their covers move to it.","이 사진을 어느 폴더에 남길까요? 번호를 누르거나 더블클릭하세요. 사본은 휴지통으로 가고, 표지는 남는 파일로 옮겨요.")
                :S("Press 1 or 2 for the one to keep: the other goes to the Recycle Bin and the next photo comes · Tab: details · hold Space: swap",
                    "남길 사진을 1이나 2로 고르면 다른 사진은 휴지통으로 가고 다음 사진이 와요 · Tab: 정보 · Space 누르고 있기: 서로 바꿔 보기"));
            message.ToolTip=message.Text;
        }
        void Refresh() {
            if(group==null)return;
            if(exact) {
                title.Text=L.PF("Exactly the same · group {0:N0} · {1:N0} files","똑같은 파일 · {0:N0}번째 묶음 · {1:N0}개",groupsSeen,copies.Count);
                undoButton.IsEnabled=pending!=null;
                UpdateTidyButton();
                ShowPills();
                return;
            }
            int round=Math.Min(Math.Max(1,total-1),picks+keptToo.Count+(finished?0:1));
            title.Text=finished?L.PF("Nearly the same · group {0:N0} · done","거의 같은 사진 · {0:N0}번째 묶음 · 끝",groupsSeen)
                :L.PF("Nearly the same · group {0:N0} · {1:N0} photos · comparison {2:N0} of {3:N0}","거의 같은 사진 · {0:N0}번째 묶음 · {1:N0}장 중 {2:N0}번째 비교 (모두 {3:N0}번)",groupsSeen,total,round,Math.Max(1,total-1));
            if(exact)return;
            if(compact)title.Text=finished?L.PF("Group {0:N0} · done","{0:N0}번째 묶음 · 끝",groupsSeen):L.PF("Group {0:N0} · {1:N0} of {2:N0}","{0:N0}번째 묶음 · {1:N0}/{2:N0}번째 비교",groupsSeen,round,Math.Max(1,total-1));
            undoButton.IsEnabled=pending!=null;
            bool pair=slots[0]!=null && slots[1]!=null && !finished;
            keepButtons[0].IsEnabled=keepButtons[1].IsEnabled=bothButton.IsEnabled=pair;
            for(int side=0;side<2;side++) {
                var f=slots[side];
                nameText[side].Text=f==null?"":f.Name+"  ("+SimilarInfo.FolderName(f.Folder)+")";
                nameButtons[side].Visibility=f==null?Visibility.Hidden:Visibility.Visible;
                nameButtons[side].MaxWidth=Math.Max(120,(ActualWidth-(compact?300:520))/2);
            }
            ShowPills();
            ShowInfo();
        }
        string Pill(int side) {
            var f=slots[side];
            if(f==null)return null;
            var parts=new List<string>();
            if(!f.IsPicture)parts.Add(S("A video · double-click: large view","영상이에요 · 더블클릭: 크게 보기"));
            else if(exact)parts.Add(S("The same picture in every file","모든 파일이 같은 그림이에요"));
            else if(keptToo.Contains(f))parts.Add(S("✓ Kept too","✓ 같이 남긴 사진"));
            else if(picks>0 && side==champion)parts.Add(S("✓ Kept so far","✓ 지금까지 남긴 사진"));
            else if(arrived[side])parts.Add(S("New","새로 온 사진"));
            if(!exact && f==group.Keep)parts.Add(S("★ Suggested to keep","★ 남기기 추천"));
            if(f.Width>0)parts.Add(SimilarInfo.Resolution(f));
            if(!exact)parts.Add(GalleryWindow.Bytes(f.Size));
            if(!exact && f.HasCover)parts.Add(S("Has a cover","표지 있음"));
            return String.Join(" · ",parts);
        }
        void ShowPills() {
            for(int side=0;side<(exact?1:2);side++) {
                int from=swapped?1-side:side;
                string text=Pill(from);
                if(text!=null && swapped)text=(side==0?S("⇄ The right one · ","⇄ 오른쪽 사진 · "):S("⇄ The left one · ","⇄ 왼쪽 사진 · "))+text;
                pillText[side].Text=text??"";
                pills[side].Visibility=text==null?Visibility.Collapsed:Visibility.Visible;
                pills[side].BorderThickness=new Thickness(swapped?1.5:0);pills[side].BorderBrush=Blue;
            }
        }
        async void ShowCovers(int side) {
            var file=slots[side];
            int at=swapped?1-side:side;
            coverBoxes[at].Child=null;
            if(exact || file==null || !file.HasCover)return;
            List<Photo> covers=null;
            try {covers=await Task.Run(()=>owner.SimilarCoversOf(file));} catch(Exception) {}
            if(closed || slots[side]!=file || covers==null || covers.Count==0)return;
            var panel=new StackPanel();
            var head=new Border {Background=PillBack,CornerRadius=new CornerRadius(6),Padding=new Thickness(8,3,8,4),HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,0,0,6)};
            head.Child=new TextBlock {Text=L.PF("Covers: {0:N0} · click to see big","표지 {0:N0}장 · 누르면 크게 봐요",covers.Count),Foreground=White,FontSize=12};
            panel.Children.Add(head);
            var row=new WrapPanel {MaxWidth=6*70};
            panel.Children.Add(row);
            coverBoxes[swapped?1-side:side].Child=panel;
            for(int i=0;i<covers.Count && i<6;i++) {
                int index=i;var cover=covers[i];
                var image=new Image {Stretch=Stretch.UniformToFill};
                var frame=new Border {Width=63,Height=112,CornerRadius=new CornerRadius(5),BorderThickness=new Thickness(1.5),BorderBrush=White,Margin=new Thickness(0,0,7,7),
                    Background=PillBack,Cursor=Cursors.Hand,ClipToBounds=true,ToolTip=S("Click to see this cover big","누르면 이 표지를 크게 봐요")};
                var inside=new Grid();inside.Children.Add(image);
                if(cover.HasMotion) {
                    var gif=new Border {Background=PillBack,CornerRadius=new CornerRadius(3),Padding=new Thickness(4,0,4,1),Margin=new Thickness(3),HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Bottom};
                    gif.Child=new TextBlock {Text="GIF",FontSize=10,FontWeight=FontWeights.SemiBold,Foreground=White};
                    inside.Children.Add(gif);
                }
                frame.Child=inside;
                frame.MouseLeftButtonDown+=(s,e)=>{e.Handled=true;owner.SimilarShowCover(covers,index);};
                row.Children.Add(frame);
                string path=cover.GridImage;
                BitmapImage picture=null;
                try {
                    picture=await Task.Run(()=>{
                        var b=new BitmapImage();
                        using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)) {
                            b.BeginInit();b.CacheOption=BitmapCacheOption.OnLoad;b.DecodePixelHeight=240;b.StreamSource=stream;b.EndInit();
                        }
                        b.Freeze();return b;
                    });
                } catch(Exception) {}
                if(closed || slots[side]!=file)return;
                image.Source=picture;
            }
            if(covers.Count>6)row.Children.Add(new Border {Background=PillBack,CornerRadius=new CornerRadius(5),Padding=new Thickness(8,4,8,4),VerticalAlignment=VerticalAlignment.Center,
                Child=new TextBlock {Text="+"+(covers.Count-6).ToString(),Foreground=White,FontSize=13}});
        }
        async void Load(int side) {
            ShowCovers(side);
            var file=slots[side];
            images[side].Source=null;ready[side]=false;
            if(file==null || !file.IsPicture){ready[side]=true;ShowPills();return;}
            BitmapSource picture=null;
            try {picture=await Task.Run(()=>Pictures.Load(file.Path));} catch(Exception) {}
            if(closed || slots[side]!=file)return; // closed, or another file came meanwhile
            ready[side]=true;
            if(swapped)images[1-side].Source=picture;else images[side].Source=picture;
            if(picture==null)pillText[side].Text=Pill(side)+" · "+S("can't be opened","열 수 없어요");
        }
        void Swap(bool on) {
            if(exact || on==swapped)return;
            swapped=on;
            var a=images[0].Source;images[0].Source=images[1].Source;images[1].Source=a;
            var c0=coverBoxes[0].Child;var c1=coverBoxes[1].Child;
            coverBoxes[0].Child=null;coverBoxes[1].Child=null;coverBoxes[0].Child=c1;coverBoxes[1].Child=c0;
            ShowPills();
        }
        void Smoothing() {
            var mode=pixels.IsChecked==true?BitmapScalingMode.NearestNeighbor:BitmapScalingMode.HighQuality;
            foreach(var image in images)RenderOptions.SetBitmapScalingMode(image,mode);
        }
        void Zoom(double factor,Point at) {
            var m=view.Matrix;
            double next=m.M11*factor;
            // Past the limits only the way back works (100% of a tiny picture can be beyond them).
            if((next<0.2 && factor<1) || (next>64 && factor>1))return;
            m.ScaleAt(factor,factor,at.X,at.Y);view.Matrix=m;
        }
        void Fit() {view.Matrix=Matrix.Identity;}
        // One picture pixel per screen pixel for the bigger picture, around the middle of the panes.
        void ActualSize() {
            int count=exact?1:2;
            var biggest=Enumerable.Range(0,count).Where(i=>images[i].Source is BitmapSource).OrderByDescending(i=>((BitmapSource)images[i].Source).PixelWidth).FirstOrDefault();
            var source=images[biggest].Source as BitmapSource;
            if(source==null || images[biggest].ActualWidth<=0)return;
            double perDip=VisualTreeHelper.GetDpi(this).DpiScaleX;
            double factor=source.PixelWidth/perDip/images[biggest].ActualWidth;
            var pane=panes[biggest];
            var m=Matrix.Identity;m.ScaleAt(factor,factor,pane.ActualWidth/2,pane.ActualHeight/2);view.Matrix=m;
        }
        void Large(int side) {
            int from=swapped?1-side:side;
            var f=exact?(chosen??slots[0]):slots[from];
            if(f!=null)owner.SimilarLarge(f);
        }
        void ToggleInfo() {
            showInfo=!showInfo;
            info.Visibility=showInfo?Visibility.Visible:Visibility.Collapsed;
            Arrange();
            ShowInfo();
        }

        // ----- Menus -----
        void NameMenu(int side) {
            var menu=new ContextMenu {PlacementTarget=nameButtons[side],Placement=PlacementMode.Top};
            var here=slots[side];
            if(here!=null) {
                var large=new MenuItem {Header=S("Open this one in the large view","이 사진 크게 보기")};
                large.Click+=delegate {owner.SimilarLarge(here);};
                menu.Items.Add(large);
                menu.Items.Add(new Separator());
            }
            var head=new MenuItem {Header=S("Bring another photo here:","다른 사진을 이쪽으로 불러오기:"),IsEnabled=false};
            menu.Items.Add(head);
            var others=waiting.Where(f=>File.Exists(f.Path)).ToList();
            if(others.Count==0)menu.Items.Add(new MenuItem {Header=S("No other photo is waiting","기다리는 다른 사진이 없어요"),IsEnabled=false});
            foreach(var f in others) {
                var file=f;
                string res=f.Width>0?" · "+SimilarInfo.Resolution(f):"";
                var item=new MenuItem {Header=f.Name+"  ("+SimilarInfo.FolderName(f.Folder)+")"+res+(f==group.Keep?"  ★":"")};
                item.Click+=delegate {
                    if(finished || slots[side]==null || !waiting.Contains(file))return;
                    waiting.Remove(file);waiting.Insert(0,slots[side]);
                    if(side==champion && picks>0)champion=1-side; // the kept one waits again: the other side is the one to beat now
                    slots[side]=file;arrived[side]=true;
                    Swap(false);Load(side);Refresh();
                };
                menu.Items.Add(item);
            }
            Open(menu);
        }
        void Open(ContextMenu menu) {
            menuOpen=true;
            menu.Closed+=delegate {menuOpen=false;Bars(Mouse.GetPosition(this));};
            menu.IsOpen=true;
        }
        void MoreMenu() {
            var menu=new ContextMenu {PlacementTarget=moreButton,Placement=PlacementMode.Top};
            int other=1-champion;
            var skip=new MenuItem {Header=S("Skip this group","이 묶음 건너뛰기"),ToolTip=S("The photos still waiting stay as they are.","아직 비교하지 않은 사진은 그대로 남아요.")};
            skip.Click+=delegate {NextGroup();};
            menu.Items.Add(skip);
            if(compact) {
                var both=new MenuItem {Header=S("Keep both","둘 다 남기기"),IsEnabled=!finished && slots[0]!=null && slots[1]!=null};
                both.Click+=delegate {KeepBoth();};
                menu.Items.Add(both);
            }
            var apart=new MenuItem {Header=other==0?S("The left one is a different photo","왼쪽 사진은 다른 사진이에요"):S("The right one is a different photo","오른쪽 사진은 다른 사진이에요"),
                IsEnabled=!finished && slots[other]!=null,
                ToolTip=S("It stays, and isn't grouped with these photos again.","그 사진은 남고, 앞으로 이 사진들과 묶이지 않아요.")};
            apart.Click+=delegate {NotSame();};
            menu.Items.Add(apart);
            menu.Items.Add(new Separator());
            for(int side=0;side<2;side++) {
                int at=side;
                var large=new MenuItem {Header=side==0?S("Large view of the left one","왼쪽 사진 크게 보기"):S("Large view of the right one","오른쪽 사진 크게 보기"),IsEnabled=slots[side]!=null};
                large.Click+=delegate {Large(at);};
                menu.Items.Add(large);
            }
            Open(menu);
        }

        // ----- The details (Tab) -----
        void ShowInfo() {
            if(exact || !showInfo || group==null)return;
            var more=info.Tag as Button;
            if(more!=null)more.Content=allInfo?S("Fewer details","간단히 보기"):S("All details","모든 정보 보기");
            Func<SimilarFile,string> when=f=>f.Taken>DateTime.MinValue?L.PF("taken {0}","찍은 날 {0}",f.Taken.ToString("yyyy-MM-dd HH:mm")):L.PF("modified {0}","수정 {0}",SimilarInfo.Modified(f).ToString("yyyy-MM-dd HH:mm"));
            var rows=new List<Tuple<string,Func<SimilarFile,string>>>();
            if(allInfo)rows.Add(Tuple.Create(S("Name","이름"),(Func<SimilarFile,string>)(f=>f.Name)));
            rows.Add(Tuple.Create(S("Folder","폴더"),(Func<SimilarFile,string>)(f=>f.Folder)));
            rows.Add(Tuple.Create(S("Resolution","해상도"),(Func<SimilarFile,string>)(f=>f.Width>0?SimilarInfo.Resolution(f)+" ("+(f.Pixels/1e6).ToString("0.0",System.Globalization.CultureInfo.InvariantCulture)+" MP)":S("unknown","알 수 없음"))));
            rows.Add(Tuple.Create(S("Type · size","형식 · 용량"),(Func<SimilarFile,string>)(f=>SimilarInfo.Kind(f)+" · "+GalleryWindow.Bytes(f.Size))));
            if(allInfo) {
                rows.Add(Tuple.Create(S("Taken","찍은 날"),(Func<SimilarFile,string>)(f=>f.Taken>DateTime.MinValue?f.Taken.ToString("yyyy-MM-dd HH:mm"):S("not recorded","기록 없음"))));
                rows.Add(Tuple.Create(S("Modified","수정한 날짜"),(Func<SimilarFile,string>)(f=>SimilarInfo.Modified(f).ToString("yyyy-MM-dd HH:mm"))));
                rows.Add(Tuple.Create(S("Came into this folder","이 폴더에 생긴 날"),(Func<SimilarFile,string>)(f=>f.Created>DateTime.MinValue?f.Created.ToString("yyyy-MM-dd HH:mm"):"")));
            } else rows.Add(Tuple.Create(S("Date","날짜"),when));
            rows.Add(Tuple.Create(S("Cover","표지"),(Func<SimilarFile,string>)(f=>f.HasCover?S("yes","있어요"):S("no","없어요"))));
            infoGrid.Children.Clear();
            for(int side=0;side<2;side++) {
                var file=slots[side];var other=slots[1-side];
                var column=new StackPanel();Grid.SetColumn(column,side*2);
                if(file!=null) {
                    foreach(var row in rows) {
                        string mine=row.Item2(file),theirs=other==null?mine:row.Item2(other);
                        var line=new DockPanel {Margin=new Thickness(0,1,0,2)};
                        var name=new TextBlock {Text=row.Item1,FontSize=12.5,Foreground=Dim,Width=compact?70:96,VerticalAlignment=VerticalAlignment.Top};
                        DockPanel.SetDock(name,Dock.Left);line.Children.Add(name);
                        var value=new TextBlock {Text=mine,FontSize=12.5,Foreground=White,TextWrapping=TextWrapping.Wrap};
                        if(mine!=theirs) {
                            value.FontWeight=FontWeights.SemiBold;
                            var chip=new Border {CornerRadius=new CornerRadius(5),Padding=new Thickness(5,0,5,1),Margin=new Thickness(0,1,7,0),VerticalAlignment=VerticalAlignment.Top,Background=Blue};
                            chip.Child=new TextBlock {Text=S("differs","달라요"),FontSize=11,FontWeight=FontWeights.SemiBold,Foreground=Brushes.White};
                            DockPanel.SetDock(chip,Dock.Left);line.Children.Add(chip);
                        }
                        line.Children.Add(value);
                        column.Children.Add(line);
                    }
                    if(other!=null) {
                        string versus=SimilarInfo.Versus(file,other,false);
                        var words=new TextBlock {Text=L.PF("Next to the other one: {0}","옆 사진과 비교: {0}",versus),FontSize=12.5,Foreground=White,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,5,0,0),FontWeight=FontWeights.SemiBold};
                        column.Children.Add(words);
                    }
                }
                infoGrid.Children.Add(column);
            }
        }
    }
}
