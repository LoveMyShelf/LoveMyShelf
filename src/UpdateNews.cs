using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace LoveMyShelf {
    // "What's new": shown before updating, so people know what they get and that their covers are safe.
    internal sealed class UpdateNewsWindow : Window {
        static readonly Dictionary<string,string> Icons=new Dictionary<string,string> {
            {"sparkle","M12,3 L13.8,9.2 L20,11 L13.8,12.8 L12,19 L10.2,12.8 L4,11 L10.2,9.2 Z"},
            {"download","M12,4 V15 M7.5,10.5 L12,15 L16.5,10.5 M5,19.5 H19"},
            {"play","M8,5 L19,12 L8,19 Z"},
            {"clock","M12,3.5 A8.5,8.5 0 1 1 11.99,3.5 Z M12,7.5 V12 L15,14"},
            {"order","M5,7 H19 M5,12 H19 M5,17 H13"},
            {"check","M5,12.5 L10,17.5 L19.5,7"},
            {"news","M5,5 H19 V19 H5 Z M8,9 H16 M8,12.5 H16 M8,16 H13"},
            {"image","M4,6 H20 V18 H4 Z M4,15 L9,11 L13,14 L16,12 L20,15"},
            {"folder","M3.5,7 H9.5 L11.5,9.5 H20.5 V18.5 H3.5 Z"}
        };
        static string K(string english,string korean) {return L.Korean?korean:english;}

        internal UpdateNewsWindow(Window owner,UpdatePlan plan,CancellationToken appClosing) {
            UI.WindowSetup(this,K("What’s new","새 버전 소식"));
            if(owner!=null && owner.IsVisible){Owner=owner;WindowStartupLocation=WindowStartupLocation.CenterOwner;}
            ShowInTaskbar=false;
            Width=Math.Min(620,SystemParameters.WorkArea.Width);Height=Math.Min(780,SystemParameters.WorkArea.Height-24);
            MinWidth=Math.Min(380,Width);MinHeight=Math.Min(420,Height);

            var root=new DockPanel();Content=root;
            var titleBar=new WindowTitleBar(this);DockPanel.SetDock(titleBar,Dock.Top);root.Children.Add(titleBar);

            var head=new StackPanel {Margin=new Thickness(28,10,28,14)};
            DockPanel.SetDock(head,Dock.Top);root.Children.Add(head);
            var version=Wrapped(L.F("Version {0} is out",plan.Version),13,"Accent");version.FontWeight=FontWeights.SemiBold;head.Children.Add(version);
            var title=Wrapped(K("Here’s what got better","이번에는 이런 점이 좋아졌어요"),23,"Text");
            title.FontWeight=FontWeights.SemiBold;title.LineHeight=32;title.Margin=new Thickness(0,4,0,0);head.Children.Add(title);
            string summary=K(plan.Notes[0].SummaryEn,plan.Notes[0].SummaryKo);
            if(summary.Length>0) {
                var line=Wrapped(summary,14,"Subtle");line.Margin=new Thickness(0,6,0,0);head.Children.Add(line);
            }

            var footer=new DockPanel {Margin=new Thickness(28,12,28,18),LastChildFill=true};
            DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
            var update=UI.Btn(K("Update now","지금 업데이트"),()=>DialogResult=true,true);
            update.IsDefault=true;update.MinHeight=36;update.MinWidth=118;update.Margin=new Thickness(8,0,0,0);UI.Right(footer,update);
            var later=UI.Btn(K("Later","나중에"),()=>DialogResult=false);
            later.IsCancel=true;later.MinHeight=36;later.MinWidth=84;UI.Right(footer,later);
            var size=UI.Text(K("Checking the download size…","다운로드 용량을 확인하고 있어요…"),12,true);
            size.TextTrimming=TextTrimming.CharacterEllipsis;size.Margin=new Thickness(0,0,10,0);footer.Children.Add(size);

            var safe=new Border {Margin=new Thickness(28,6,28,0),Padding=new Thickness(14,10,14,10),CornerRadius=new CornerRadius(10),BorderThickness=new Thickness(1)};
            safe.SetResourceReference(Border.BackgroundProperty,"Panel");safe.SetResourceReference(Border.BorderBrushProperty,"Line");
            var safeRow=new DockPanel();
            var safeIcon=Drawing("check",new SolidColorBrush(NoticeLook.Green),18);safeIcon.Margin=new Thickness(0,0,10,0);
            DockPanel.SetDock(safeIcon,Dock.Left);safeRow.Children.Add(safeIcon);
            safeRow.Children.Add(Wrapped(K("Your covers, settings, and original photos and videos stay exactly as they are.","만든 표지와 설정, 원본 사진과 영상은 그대로 남아요."),13,"Text"));
            safe.Child=safeRow;DockPanel.SetDock(safe,Dock.Bottom);root.Children.Add(safe);

            var page=new StackPanel {Margin=new Thickness(28,0,28,10)};
            bool several=plan.Notes.Count>1;
            foreach(var note in plan.Notes) {
                if(several) {
                    var heading=Wrapped(K("Version ","버전 ")+note.Version,16,"Text");heading.FontWeight=FontWeights.SemiBold;
                    heading.Margin=new Thickness(0,note==plan.Notes[0]?2:20,0,0);page.Children.Add(heading);
                }
                AddItems(page,note);
            }
            root.Children.Add(new ScrollViewer {Content=page,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,Focusable=false});

            var stop=CancellationTokenSource.CreateLinkedTokenSource(appClosing);
            var token=stop.Token;
            Closed+=delegate {stop.Cancel();stop.Dispose();};
            Loaded+=async delegate {
                try {
                    long bytes=await AppUpdater.DownloadSizeAsync(plan,token);
                    size.Text=bytes>0?K("Download size: ","다운로드 용량 ")+DetailsWindow.Size(bytes):K("Already downloaded","이미 다운로드해 뒀어요");
                } catch(OperationCanceledException) {}
                catch(Exception) {size.Text="";}
            };
        }

        // Also used by the "Version and what's new" page in Guide & Options.
        internal static void AddItems(StackPanel page,ReleaseNote note) {
            AddGroup(page,note,"new",K("New","새로 생겼어요"));
            AddGroup(page,note,"better",K("Easier to use","더 편해졌어요"));
            AddGroup(page,note,"fixed",K("Fixed","고쳤어요"));
        }
        static void AddGroup(StackPanel page,ReleaseNote note,string group,string label) {
            var items=note.Items.Where(i=>i.Group==group).ToList();
            if(items.Count==0)return;
            var heading=Wrapped(label,13,"Subtle");heading.FontWeight=FontWeights.SemiBold;heading.Margin=new Thickness(0,14,0,2);page.Children.Add(heading);
            for(int i=0;i<items.Count;i++)page.Children.Add(Row(items[i],i<items.Count-1));
        }
        static UIElement Row(ReleaseItem item,bool line) {
            var row=new Border {Padding=new Thickness(0,10,0,10),BorderThickness=new Thickness(0,0,0,line?1:0)};
            row.SetResourceReference(Border.BorderBrushProperty,"Line");
            var content=new DockPanel();row.Child=content;
            var tile=new Border {Width=36,Height=36,CornerRadius=new CornerRadius(9),Margin=new Thickness(0,1,13,0),VerticalAlignment=VerticalAlignment.Top};
            tile.SetResourceReference(Border.BackgroundProperty,"Raised");
            var drawing=Drawing(item.Icon,null,20);drawing.HorizontalAlignment=HorizontalAlignment.Center;drawing.VerticalAlignment=VerticalAlignment.Center;
            tile.Child=drawing;DockPanel.SetDock(tile,Dock.Left);content.Children.Add(tile);
            var text=new StackPanel();content.Children.Add(text);
            var title=Wrapped(K(item.TitleEn,item.TitleKo),15,"Text");title.FontWeight=FontWeights.SemiBold;text.Children.Add(title);
            string body=K(item.TextEn,item.TextKo);
            if(body.Length>0) {
                var detail=Wrapped(body,13,"Subtle");detail.Margin=new Thickness(0,3,0,0);text.Children.Add(detail);
            }
            if(item.Keys.Length>0) {
                var keys=new WrapPanel {Margin=new Thickness(0,6,0,0)};
                foreach(string key in item.Keys)keys.Children.Add(Key(key));
                text.Children.Add(keys);
            }
            return row;
        }
        static UIElement Key(string key) {
            var chip=new Border {BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(5),Padding=new Thickness(7,2,7,2),Margin=new Thickness(0,0,5,3)};
            chip.SetResourceReference(Border.BorderBrushProperty,"StrongLine");chip.SetResourceReference(Border.BackgroundProperty,"Panel");
            chip.Child=UI.Text(key,11);
            return chip;
        }
        static FrameworkElement Drawing(string name,Brush color,double side) {
            string data;
            if(name==null || !Icons.TryGetValue(name,out data))data=Icons["sparkle"];
            var path=new Path {Data=Geometry.Parse(data),StrokeThickness=1.7,StrokeLineJoin=PenLineJoin.Round,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round};
            if(color!=null)path.Stroke=color;else path.SetResourceReference(Shape.StrokeProperty,"Text");
            var canvas=new Canvas {Width=24,Height=24};canvas.Children.Add(path);
            return new Viewbox {Width=side,Height=side,Child=canvas,VerticalAlignment=VerticalAlignment.Top};
        }
        static TextBlock Wrapped(string text,double size,string color) {
            var block=new TextBlock {Text=NoticeLook.KeepWords(text),FontSize=size,TextWrapping=TextWrapping.Wrap,
                LineHeight=Math.Round(size*1.55),LineStackingStrategy=LineStackingStrategy.BlockLineHeight};
            block.SetResourceReference(TextBlock.ForegroundProperty,color);
            return block;
        }
    }
}
