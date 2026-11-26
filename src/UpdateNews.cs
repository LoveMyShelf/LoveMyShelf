using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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
            var version=Wrapped(L.F("Version {0} is out",plan.Version),14,"Accent");version.FontWeight=FontWeights.SemiBold;head.Children.Add(version);
            var title=Wrapped(K("Here’s what got better","이번에는 이런 점이 좋아졌어요"),24,"Text");
            title.FontWeight=FontWeights.Bold;title.LineHeight=34;title.Margin=new Thickness(0,4,0,0);head.Children.Add(title);
            string summary=K(plan.Notes[0].SummaryEn,plan.Notes[0].SummaryKo);
            if(summary.Length>0) {
                var line=Wrapped(summary,16,"Text");line.Margin=new Thickness(0,8,0,0);head.Children.Add(line);
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
            safeRow.Children.Add(Wrapped(K("Your covers, settings, and original photos and videos stay exactly as they are.","만든 표지와 설정, 원본 사진과 영상은 그대로 남아요."),14,"Text"));
            safe.Child=safeRow;DockPanel.SetDock(safe,Dock.Bottom);root.Children.Add(safe);

            var page=new StackPanel {Margin=new Thickness(28,0,28,10)};
            bool several=plan.Notes.Count>1;
            foreach(var note in plan.Notes) {
                if(several) {
                    var heading=Wrapped(K("Version ","버전 ")+note.Version,20,"Text");heading.FontWeight=FontWeights.Bold;
                    heading.Margin=new Thickness(0,note==plan.Notes[0]?4:34,0,0);page.Children.Add(heading);
                    if(note!=plan.Notes[0]) {
                        string past=K(note.SummaryEn,note.SummaryKo);
                        if(past.Length>0){var line=Wrapped(past,16,"Text");line.Margin=new Thickness(0,4,0,0);page.Children.Add(line);}
                    }
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
        // Everything stays open: nothing is folded away behind a "more" button.
        internal static void AddItems(StackPanel page,ReleaseNote note) {
            var star=note.Items.FirstOrDefault(i=>i.Star);
            if(star!=null)page.Children.Add(StarCard(star));
            AddGroup(page,note,"new",K("New","새로 생겼어요"),star);
            AddGroup(page,note,"better",K("Easier to use","더 편해졌어요"),star);
            AddGroup(page,note,"fixed",K("Fixed","고쳤어요"),star);
        }
        static void AddGroup(StackPanel page,ReleaseNote note,string group,string label,ReleaseItem star) {
            var items=note.Items.Where(i=>i.Group==group && i!=star).ToList();
            if(items.Count==0)return;
            var heading=Wrapped(label,14,"Accent");heading.FontWeight=FontWeights.Bold;heading.Margin=new Thickness(0,22,0,0);page.Children.Add(heading);
            for(int i=0;i<items.Count;i++)page.Children.Add(Row(items[i],i<items.Count-1,false));
        }
        // The version's main item, in a soft blue card at the top.
        static UIElement StarCard(ReleaseItem item) {
            var card=new Grid {Margin=new Thickness(0,16,0,0)};
            card.Children.Add(Tint(0.09,12,false));card.Children.Add(Tint(0.35,12,true));
            var stack=new StackPanel {Margin=new Thickness(19,15,19,5)};card.Children.Add(stack);
            var label=Wrapped(K("The main thing this time","이번 판의 핵심"),14,"Accent");label.FontWeight=FontWeights.Bold;stack.Children.Add(label);
            stack.Children.Add(Row(item,false,true));
            return card;
        }
        // A see-through layer of the theme's accent colour (a fill, or only its edge), so it follows a theme change.
        static Border Tint(double opacity,double corner,bool edge) {
            var layer=new Border {CornerRadius=new CornerRadius(corner),Opacity=opacity,BorderThickness=new Thickness(edge?1:0)};
            layer.SetResourceReference(edge?Border.BorderBrushProperty:Border.BackgroundProperty,"Accent");
            return layer;
        }
        static UIElement Row(ReleaseItem item,bool line,bool big) {
            var row=new Border {Padding=new Thickness(0,big?8:14,0,14),BorderThickness=new Thickness(0,0,0,line?1:0)};
            row.SetResourceReference(Border.BorderBrushProperty,"Line");
            var content=new DockPanel();row.Child=content;
            var tile=new Grid {Width=38,Height=38,Margin=new Thickness(0,2,14,0),VerticalAlignment=VerticalAlignment.Top};
            if(big)tile.Children.Add(Tint(0.16,10,false));
            else {var back=new Border {CornerRadius=new CornerRadius(10)};back.SetResourceReference(Border.BackgroundProperty,"Raised");tile.Children.Add(back);}
            var drawing=Drawing(item.Icon,null,21);drawing.HorizontalAlignment=HorizontalAlignment.Center;drawing.VerticalAlignment=VerticalAlignment.Center;
            tile.Children.Add(drawing);DockPanel.SetDock(tile,Dock.Left);content.Children.Add(tile);
            var text=new StackPanel();content.Children.Add(text);
            var title=Wrapped(K(item.TitleEn,item.TitleKo),big?19:17,"Text");title.FontWeight=FontWeights.Bold;text.Children.Add(title);
            string body=K(item.TextEn,item.TextKo);
            var bold=L.Korean?item.BoldKo:item.BoldEn;
            bool first=true;
            foreach(string part in body.Split('\n')) {
                string paragraph=part.Trim();
                if(paragraph.Length==0)continue;
                var block=Body(paragraph,bold,15);block.Margin=new Thickness(0,first?5:10,0,0);text.Children.Add(block);
                first=false;
            }
            string tryIt=K(item.TryEn,item.TryKo);
            if(tryIt.Length>0)text.Children.Add(TryLine(tryIt));
            if(item.Keys.Length>0) {
                var keys=new WrapPanel {Margin=new Thickness(0,8,0,0)};
                foreach(string key in item.Keys)keys.Children.Add(Key(key));
                text.Children.Add(keys);
            }
            return row;
        }
        // "Try it": one short thing to do right away, set off by a blue bar on the left.
        static UIElement TryLine(string text) {
            var box=new Border {BorderThickness=new Thickness(3,0,0,0),Padding=new Thickness(12,2,0,2),Margin=new Thickness(0,12,0,0)};
            box.SetResourceReference(Border.BorderBrushProperty,"Accent");
            var block=Wrapped("",15,"Text");
            var label=new Run(K("Try it:  ","이렇게 해 보세요  ")) {FontWeight=FontWeights.Bold};label.SetResourceReference(TextElement.ForegroundProperty,"Accent");
            block.Inlines.Add(label);Fill(block.Inlines,text,null,15);
            box.Child=block;return box;
        }
        // [Button names] become small chips and the listed phrases are bold. The plain text stays as written,
        // so an older copy of the program shows the same words without the marks.
        static readonly Regex ButtonName=new Regex(@"\[([^\s\[\]][^\[\]\n]*?[^\s\[\]]|[^\s\[\]])\]");
        internal static TextBlock Body(string text,string[] bold,double size) {
            var block=Wrapped("",size,"Text");
            Fill(block.Inlines,text,bold,size);
            return block;
        }
        static void Fill(InlineCollection inlines,string text,string[] bold,double size) {
            var marks=new List<Tuple<int,int,bool>>(); // start, length, is a button
            foreach(Match match in ButtonName.Matches(text))marks.Add(Tuple.Create(match.Index,match.Length,true));
            foreach(string phrase in bold??new string[0]) {
                if(String.IsNullOrEmpty(phrase))continue;
                // The first place where it doesn't run into a button or another bold phrase.
                for(int at=text.IndexOf(phrase,StringComparison.Ordinal);at>=0;at=text.IndexOf(phrase,at+1,StringComparison.Ordinal)) {
                    int start=at;
                    if(marks.Any(m=>start<m.Item1+m.Item2 && m.Item1<start+phrase.Length))continue;
                    marks.Add(Tuple.Create(start,phrase.Length,false));break;
                }
            }
            marks.Sort((a,b)=>a.Item1.CompareTo(b.Item1));
            int done=0;
            foreach(var mark in marks) {
                if(mark.Item1>done)inlines.Add(new Run(Piece(text,done,mark.Item1)));
                if(mark.Item3) {
                    var chip=new Border {CornerRadius=new CornerRadius(5),BorderThickness=new Thickness(1),Padding=new Thickness(6,0,6,0),Margin=new Thickness(1,0,1,0)};
                    chip.SetResourceReference(Border.BackgroundProperty,"Panel");chip.SetResourceReference(Border.BorderBrushProperty,"StrongLine");
                    var label=UI.Text(text.Substring(mark.Item1+1,mark.Item2-2),size-2);label.FontWeight=FontWeights.SemiBold;
                    label.LineHeight=Math.Round(size*1.25);label.LineStackingStrategy=LineStackingStrategy.BlockLineHeight;chip.Child=label;
                    inlines.Add(new InlineUIContainer(chip) {BaselineAlignment=BaselineAlignment.Center});
                } else inlines.Add(new Run(Piece(text,mark.Item1,mark.Item1+mark.Item2)) {FontWeight=FontWeights.Bold});
                done=mark.Item1+mark.Item2;
            }
            if(done<text.Length)inlines.Add(new Run(Piece(text,done,text.Length)));
        }
        // One piece of a sentence. Where it meets the next piece with no space between them (a bold word and its
        // particle, a button name and 을/를), a word joiner keeps them on the same line.
        const char WordJoiner=(char)0x2060;
        static string Piece(string text,int from,int to) {
            string piece=NoticeLook.KeepWords(text.Substring(from,to-from));
            if(from>0 && !Char.IsWhiteSpace(text[from-1]) && !Char.IsWhiteSpace(text[from]))piece=WordJoiner+piece;
            if(to<text.Length && !Char.IsWhiteSpace(text[to-1]) && !Char.IsWhiteSpace(text[to]))piece+=WordJoiner;
            return piece;
        }
        static UIElement Key(string key) {
            var chip=new Border {BorderThickness=new Thickness(1,1,1,2),CornerRadius=new CornerRadius(6),Padding=new Thickness(8,2,8,2),Margin=new Thickness(0,0,6,4)};
            chip.SetResourceReference(Border.BorderBrushProperty,"StrongLine");chip.SetResourceReference(Border.BackgroundProperty,"Raised");
            var label=UI.Text(key,13);label.FontWeight=FontWeights.SemiBold;chip.Child=label;
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
                LineHeight=Math.Round(size*1.6),LineStackingStrategy=LineStackingStrategy.BlockLineHeight};
            block.SetResourceReference(TextBlock.ForegroundProperty,color);
            return block;
        }
    }
}
