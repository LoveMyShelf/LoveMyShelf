using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LoveMyShelf {
    // Shown once before the main window: pick a language, then read a short note on fair use.
    // People upgrading from an older version already have a language and only see the note.
    internal sealed class FirstRunWindow : Window {
        readonly Preferences settings;
        readonly StackPanel page=new StackPanel {Margin=new Thickness(28,22,28,26)};

        internal FirstRunWindow(Preferences settings) {
            this.settings=settings;
            UI.WindowSetup(this,"LoveMyShelf");
            Width=Math.Min(520,SystemParameters.WorkArea.Width);SizeToContent=SizeToContent.Height;
            ResizeMode=ResizeMode.NoResize;
            var content=new DockPanel();
            content.Children.Add(new WindowTitleBar(this));
            content.Children.Add(page);Content=content;
            if(!settings.LanguageChosen)ShowLanguage();else if(!settings.UsageNoticeShown)ShowShapes();else ShowNotice();
        }

        void ShowLanguage() {
            page.Children.Clear();
            var title=UI.Text("언어를 골라 주세요 · Choose your language",18);
            title.FontWeight=FontWeights.SemiBold;title.TextWrapping=TextWrapping.Wrap;page.Children.Add(title);
            var hint=UI.Text("나중에 ‘설명서 겸 옵션’에서 바꿀 수 있어요. · You can change this later in Guide & Options.",13,true);
            hint.TextWrapping=TextWrapping.Wrap;hint.Margin=new Thickness(0,6,0,18);page.Children.Add(hint);
            bool koreanFirst=CultureInfo.CurrentUICulture.TwoLetterISOLanguageName=="ko";
            var korean=Big("한국어",true);
            var english=Big("English",false);
            var row=new UniformGrid2();
            row.Children.Add(korean);row.Children.Add(english);page.Children.Add(row);
            (koreanFirst?korean:english).IsDefault=true;
            Loaded+=delegate {(koreanFirst?korean:english).Focus();};
        }

        Button Big(string label,bool korean) {
            var button=UI.Btn(label,()=>{
                settings.Korean=korean;settings.LanguageChosen=true;L.Korean=korean;
                if(settings.UsageNoticeShown)ShowNotice();else ShowShapes();
            },true);
            button.FontSize=16;button.MinHeight=48;button.Margin=new Thickness(0,0,8,0);
            return button;
        }

        void ShowNotice() {
            page.Children.Clear();
            Title="LoveMyShelf";
            var title=UI.Text(L.T("Welcome to LoveMyShelf"),18);
            title.FontWeight=FontWeights.SemiBold;page.Children.Add(title);
            var lines=new[]{
                L.T("LoveMyShelf is a program that shows the photos, GIFs and videos in your folders at a glance, inside your own computer. It doesn’t upload anything to the internet.")
            };
            for(int i=0;i<lines.Length;i++) {
                var text=UI.Text((i==0?"":"•  ")+NoticeLook.KeepWords(lines[i]),14,i>0);
                text.TextWrapping=TextWrapping.Wrap;text.Margin=new Thickness(0,i==0?10:6,0,i==0?6:0);
                text.LineHeight=22;text.LineStackingStrategy=LineStackingStrategy.BlockLineHeight;
                page.Children.Add(text);
            }
            var tip=UI.Text(NoticeLook.KeepWords(L.T("Shortcuts, how-tos and settings are all in [Guide & Options] at the top right. Pressing F1 opens it too.")),13,true);
            tip.TextWrapping=TextWrapping.Wrap;tip.Margin=new Thickness(0,14,0,0);page.Children.Add(tip);
            var ok=UI.Btn(L.T("Let’s start"),()=>{
                settings.UsageNoticeShown=true;DialogResult=true;
            },true);
            ok.FontSize=14;ok.MinWidth=110;ok.MinHeight=36;ok.IsDefault=true;
            ok.HorizontalAlignment=HorizontalAlignment.Right;ok.Margin=new Thickness(0,22,0,0);
            page.Children.Add(ok);
            Dispatcher.BeginInvoke(new Action(()=>ok.Focus()));
        }

        // A new install picks the shape of the grid's cells, with a small picture of each. 3:4 is picked to begin with,
        // so one press starts. It can be changed any time later (Guide & Options, or the right-click menu of the grid).
        void ShowShapes() {
            page.Children.Clear();
            Width=Math.Min(760,SystemParameters.WorkArea.Width-40);
            var title=UI.Text(L.P("Which shape should the grid's cells be?","바둑판 칸은 어떤 모양이 좋을까요?"),18);
            title.FontWeight=FontWeights.SemiBold;title.TextWrapping=TextWrapping.Wrap;page.Children.Add(title);
            var hint=UI.Text(NoticeLook.KeepWords(L.P("Covers and photos are shown whole inside these cells. You can change this any time, and the covers you made stay as they are.",
                "표지와 사진이 이 칸 안에 잘리지 않고 다 보여요. 나중에 언제든 바꿀 수 있고, 바꿔도 만든 표지는 그대로예요.")),13,true);
            hint.TextWrapping=TextWrapping.Wrap;hint.Margin=new Thickness(0,6,0,16);page.Children.Add(hint);
            var row=new System.Windows.Controls.Primitives.UniformGrid {Columns=3,Rows=1};
            var cards=new Border[Covers.Shapes.Length];
            int chosen=Covers.DefaultShape;
            Action mark=()=>{
                for(int i=0;i<cards.Length;i++) {
                    cards[i].SetResourceReference(Border.BorderBrushProperty,i==chosen?"Accent":"Line");
                    cards[i].BorderThickness=new Thickness(i==chosen?2.5:1);
                    cards[i].Padding=new Thickness(i==chosen?10.5:12);
                }
            };
            string[] notes={
                L.P("For lots of tall phone videos and pictures. Tall ones show big.","세로로 찍은 휴대폰 영상·짤이 많을 때 좋아요. 세로 사진이 크게 보여요."),
                L.P("Easy-going when tall and wide ones are mixed. The same shape as an Instagram profile.","세로·가로가 섞여 있을 때 무난해요. 인스타그램 프로필 칸과 같은 모양이에요."),
                L.P("For lots of wide photos. Tall and wide ones show at about the same size.","가로 사진이 많을 때 좋아요. 세로·가로가 비슷한 크기로 보여요.")
            };
            for(int i=0;i<cards.Length;i++) {
                int shape=i;
                var inside=new StackPanel();
                var head=new DockPanel {Margin=new Thickness(0,0,0,8)};
                var name=UI.Text(Covers.ShapeLabel(i),15);name.FontWeight=FontWeights.SemiBold;
                if(i==Covers.DefaultShape) {
                    var badge=new Border {CornerRadius=new CornerRadius(8),Padding=new Thickness(7,1,7,2),VerticalAlignment=VerticalAlignment.Center,
                        Child=UI.Text(L.P("Recommended","추천"),11)};
                    badge.SetResourceReference(Border.BackgroundProperty,"Raised");
                    UI.Right(head,badge);
                }
                head.Children.Add(name);inside.Children.Add(head);
                inside.Children.Add(new ShapeSample(Covers.Shape(i)) {Height=150,Margin=new Thickness(0,0,0,10)});
                var note=UI.Text(NoticeLook.KeepWords(notes[i]),12.5,true);note.TextWrapping=TextWrapping.Wrap;
                note.LineHeight=19;note.LineStackingStrategy=LineStackingStrategy.BlockLineHeight;inside.Children.Add(note);
                var card=new Border {CornerRadius=new CornerRadius(12),Margin=new Thickness(0,0,i<cards.Length-1?10:0,0),Child=inside,Cursor=System.Windows.Input.Cursors.Hand};
                card.SetResourceReference(Border.BackgroundProperty,"Panel");
                System.Windows.Automation.AutomationProperties.SetName(card,Covers.ShapeLabel(i));
                card.MouseLeftButtonUp+=delegate {chosen=shape;mark();};
                card.MouseLeftButtonDown+=(s,e)=>{if(e.ClickCount==2){chosen=shape;mark();Start(chosen);}};
                cards[i]=card;row.Children.Add(card);
            }
            mark();
            page.Children.Add(row);
            var later=UI.Text(NoticeLook.KeepWords(L.P("Later: Guide & Options → Browsing files → Shape of the grid's cells.","나중에 바꾸려면: 설명서 겸 옵션 → 파일 둘러보기 → 바둑판 칸 모양")),12,true);
            later.TextWrapping=TextWrapping.Wrap;later.Margin=new Thickness(0,12,0,0);page.Children.Add(later);
            var go=UI.Btn(L.P("Start with this","이걸로 시작하기"),()=>Start(chosen),true);
            go.FontSize=14;go.MinWidth=130;go.MinHeight=38;go.IsDefault=true;
            go.HorizontalAlignment=HorizontalAlignment.Right;go.Margin=new Thickness(0,16,0,0);
            page.Children.Add(go);
            Dispatcher.BeginInvoke(new Action(()=>go.Focus()));
        }
        void Start(int shape) {
            settings.TileShape=shape;
            Width=Math.Min(520,SystemParameters.WorkArea.Width);
            ShowNotice();
        }
        // A little grid of made-up pictures (tall, wide, square) in cells of one shape, as the grid would show them.
        sealed class ShapeSample : FrameworkElement {
            readonly double cell;
            internal ShapeSample(double cell) {this.cell=cell;}
            // Tall, wide and square pictures in turn, each with its own soft color.
            static readonly double[] Pictures={9.0/16,16.0/9,1,3.0/4,16.0/9,9.0/16,4.0/3};
            static readonly Color[] Tints={Color.FromRgb(0x9C,0xC3,0xF5),Color.FromRgb(0xF5,0xC2,0x9C),Color.FromRgb(0xA8,0xDB,0xB8),
                Color.FromRgb(0xD3,0xB5,0xF0),Color.FromRgb(0xF2,0xD8,0x8A),Color.FromRgb(0x9F,0xD8,0xE0),Color.FromRgb(0xF0,0xAF,0xC4)};
            protected override void OnRender(DrawingContext dc) {
                double width=ActualWidth,height=ActualHeight;if(width<=0 || height<=0)return;
                const int columns=3;const double gap=3;
                double w=(width-gap*(columns-1))/columns,h=w/cell;
                int rows=Math.Max(1,(int)Math.Floor((height+gap)/(h+gap)));
                if(rows*h+(rows-1)*gap>height){h=(height-gap*(rows-1))/rows;w=h*cell;}
                double left=(width-(w*columns+gap*(columns-1)))/2;
                var band=(Brush)FindResource("TileBand");
                int k=0;
                for(int r=0;r<rows;r++) {
                    int c=0;
                    while(c<columns) {
                        double shape=Pictures[k%Pictures.Length];
                        bool wide=Covers.TakesTwoCells(shape,cell);
                        // A wide one that would hang over the end leaves the last cell empty and starts the next row, as in the grid.
                        if(wide && c+1>=columns)break;
                        int span=wide?2:1;
                        var box=new Rect(left+c*(w+gap),r*(h+gap),w*span+gap*(span-1),h);
                        dc.DrawRoundedRectangle(band,null,box,3,3);
                        double pw=box.Width,ph=box.Height;
                        if(shape>pw/ph)ph=pw/shape;else pw=ph*shape;
                        var pic=new Rect(box.X+(box.Width-pw)/2,box.Y+(box.Height-ph)/2,pw,ph);
                        DrawPicture(dc,pic,Tints[k%Tints.Length]);
                        c+=span;k++;
                    }
                }
            }
            // A made-up photo: a soft sky, a sun and two hills.
            static void DrawPicture(DrawingContext dc,Rect r,Color tint) {
                var sky=new LinearGradientBrush(Color.FromRgb((byte)Math.Min(255,tint.R+40),(byte)Math.Min(255,tint.G+40),(byte)Math.Min(255,tint.B+40)),tint,90);
                dc.PushClip(new RectangleGeometry(r,2,2));
                dc.DrawRectangle(sky,null,r);
                var dark=Color.FromRgb((byte)(tint.R*0.72),(byte)(tint.G*0.72),(byte)(tint.B*0.72));
                double s=Math.Min(r.Width,r.Height);
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0xE6,0xFF,0xFF,0xFF)),null,new Point(r.Right-s*0.28,r.Top+s*0.28),s*0.11,s*0.11);
                var hills=new StreamGeometry();
                using(var g=hills.Open()) {
                    g.BeginFigure(new Point(r.Left,r.Bottom),true,true);
                    g.LineTo(new Point(r.Left,r.Bottom-r.Height*0.28),false,false);
                    g.LineTo(new Point(r.Left+r.Width*0.38,r.Bottom-r.Height*0.52),false,false);
                    g.LineTo(new Point(r.Left+r.Width*0.62,r.Bottom-r.Height*0.30),false,false);
                    g.LineTo(new Point(r.Left+r.Width*0.80,r.Bottom-r.Height*0.42),false,false);
                    g.LineTo(new Point(r.Right,r.Bottom-r.Height*0.22),false,false);
                    g.LineTo(new Point(r.Right,r.Bottom),false,false);
                }
                hills.Freeze();
                dc.DrawGeometry(new SolidColorBrush(dark),null,hills);
                dc.Pop();
            }
        }

        // Two equal columns without pulling in UniformGrid's namespace for one use.
        sealed class UniformGrid2 : System.Windows.Controls.Primitives.UniformGrid {
            internal UniformGrid2() {Columns=2;Rows=1;}
        }
    }
}
