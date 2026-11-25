using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace LoveMyShelf {
    internal enum NoticeKind { Info, Busy, Warning, Done }

    // Friendly message card: rounded, softly shadowed, with an icon, a bold title and an optional explanation.
    // Shared by the in-window notice and the corner toast so every message in the app looks the same.
    internal static class NoticeLook {
        internal static readonly Color Blue=Color.FromRgb(0x4C,0x9A,0xFF);
        internal static readonly Color Amber=Color.FromRgb(0xFF,0xB5,0x3D);
        internal static readonly Color Green=Color.FromRgb(0x3D,0xD6,0x8C);

        internal static Border Card(out TextBlock title,out TextBlock body,out ContentControl icon) {
            var card=new Border {
                BorderThickness=new Thickness(1),
                CornerRadius=new CornerRadius(16),Padding=new Thickness(18,14,22,15),
                Effect=new DropShadowEffect {BlurRadius=28,ShadowDepth=6,Direction=270,Opacity=0.45,Color=Colors.Black},
                SnapsToDevicePixels=true
            };
            card.SetResourceReference(Border.BackgroundProperty,"CardBack");card.SetResourceReference(Border.BorderBrushProperty,"CardLine");
            TextOptions.SetTextFormattingMode(card,TextFormattingMode.Ideal);
            var row=new DockPanel();
            icon=new ContentControl {Width=22,Height=22,Margin=new Thickness(0,1,12,0),VerticalAlignment=VerticalAlignment.Top};
            DockPanel.SetDock(icon,Dock.Left);row.Children.Add(icon);
            var text=new StackPanel {VerticalAlignment=VerticalAlignment.Center};
            title=new TextBlock {FontSize=15,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap,
                LineHeight=22,LineStackingStrategy=LineStackingStrategy.BlockLineHeight};
            body=new TextBlock {FontSize=13,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,3,0,0),
                LineHeight=20,LineStackingStrategy=LineStackingStrategy.BlockLineHeight};
            title.SetResourceReference(TextBlock.ForegroundProperty,"Text");body.SetResourceReference(TextBlock.ForegroundProperty,"Subtle");
            text.Children.Add(title);text.Children.Add(body);row.Children.Add(text);
            card.Child=row;
            return card;
        }

        // WPF may break Korean text between any two syllables. Join the letters of each word with
        // U+2060 (WORD JOINER) so lines wrap only at spaces, the way people read Korean.
        internal static string KeepWords(string value) {
            if(String.IsNullOrEmpty(value))return value??"";
            var result=new System.Text.StringBuilder(value.Length*2);
            for(int i=0;i<value.Length;i++) {
                char c=value[i];
                result.Append(c);
                if(i+1<value.Length && IsHangul(c) && !Char.IsWhiteSpace(value[i+1]))result.Append(WordJoiner);
                else if(i+1<value.Length && !Char.IsWhiteSpace(c) && IsHangul(value[i+1]))result.Append(WordJoiner);
            }
            return result.ToString();
        }
        static bool IsHangul(char c) { return (c>=(char)0xAC00 && c<=(char)0xD7A3) || (c>=(char)0x3131 && c<=(char)0x318E); }
        const char WordJoiner=(char)0x2060;

        internal static UIElement Icon(NoticeKind kind) {
            if(kind==NoticeKind.Busy) {
                var grid=new Grid {Width=20,Height=20,Margin=new Thickness(1)};
                grid.Children.Add(new Ellipse {Stroke=new SolidColorBrush(Color.FromArgb(0x40,Blue.R,Blue.G,Blue.B)),StrokeThickness=2.4});
                var arc=new Path {
                    Data=Geometry.Parse("M 10,1.2 A 8.8,8.8 0 0 1 18.8,10"),Stroke=new SolidColorBrush(Blue),StrokeThickness=2.4,
                    StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,RenderTransformOrigin=new Point(0.5,0.5)
                };
                var spin=new RotateTransform();arc.RenderTransform=spin;
                spin.BeginAnimation(RotateTransform.AngleProperty,
                    new DoubleAnimation(0,360,TimeSpan.FromSeconds(0.9)) {RepeatBehavior=RepeatBehavior.Forever});
                grid.Children.Add(arc);
                return grid;
            }
            Color color=kind==NoticeKind.Warning?Amber:kind==NoticeKind.Done?Green:Blue;
            string glyph=kind==NoticeKind.Warning?"!":kind==NoticeKind.Done?"✓":"i";
            var badge=new Grid {Width=22,Height=22};
            badge.Children.Add(new Ellipse {Fill=new SolidColorBrush(color)});
            badge.Children.Add(new TextBlock {
                Text=glyph,FontSize=kind==NoticeKind.Done?12:13,FontWeight=FontWeights.Bold,
                FontFamily=new FontFamily("Segoe UI"),Foreground=new SolidColorBrush(Color.FromRgb(0x14,0x16,0x19)),
                HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,-1,0,0)
            });
            return badge;
        }

        // Fade in while sliding up a few pixels.
        internal static void Enter(UIElement element) {
            var move=new TranslateTransform(0,10);element.RenderTransform=move;
            var ease=new CubicEase {EasingMode=EasingMode.EaseOut};
            element.BeginAnimation(UIElement.OpacityProperty,new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(220)) {EasingFunction=ease});
            move.BeginAnimation(TranslateTransform.YProperty,new DoubleAnimation(10,0,TimeSpan.FromMilliseconds(260)) {EasingFunction=ease});
        }
    }

    // In-window notice that floats over the picture. Setting Text keeps the old status-line behavior:
    // an empty string hides it, text ending with "…" shows a spinner, anything else shows an info icon.
    internal sealed class NoticeCard : Border {
        readonly TextBlock title,body;
        readonly ContentControl icon;
        readonly DispatcherTimer hideTimer=new DispatcherTimer();
        string text="";

        internal NoticeCard() {
            var card=NoticeLook.Card(out title,out body,out icon);
            Child=card;MaxWidth=460;Margin=new Thickness(20,28,20,28);
            IsHitTestVisible=false;Visibility=Visibility.Collapsed;
            hideTimer.Tick+=delegate {hideTimer.Stop();Hide();};
        }

        public string Text {
            get {return text;}
            set {
                if(String.IsNullOrEmpty(value)){text="";Hide();return;}
                Show(value,null,value.TrimEnd().EndsWith("…")?NoticeKind.Busy:NoticeKind.Info);
            }
        }

        internal void Warn(string heading,string detail=null) { Show(heading,detail,NoticeKind.Warning); }

        internal void Show(string heading,string detail,NoticeKind kind,double seconds=0) {
            hideTimer.Stop();
            bool already=Visibility==Visibility.Visible && text==heading+"\n"+detail;
            text=heading+"\n"+detail;
            title.Text=NoticeLook.KeepWords(heading);
            body.Text=NoticeLook.KeepWords(detail);body.Visibility=String.IsNullOrEmpty(detail)?Visibility.Collapsed:Visibility.Visible;
            StopSpin();icon.Content=NoticeLook.Icon(kind);
            BeginAnimation(OpacityProperty,null);
            Visibility=Visibility.Visible;
            if(!already)NoticeLook.Enter(this);else Opacity=1;
            if(seconds>0){hideTimer.Interval=TimeSpan.FromSeconds(seconds);hideTimer.Start();}
        }

        void Hide() {
            hideTimer.Stop();
            if(Visibility!=Visibility.Visible)return;
            var fade=new DoubleAnimation(Opacity,0,TimeSpan.FromMilliseconds(160));
            // Hidden: the icon goes too, so a busy spinner does not keep turning out of sight.
            fade.Completed+=delegate {if(Opacity<0.01){Visibility=Visibility.Collapsed;StopSpin();icon.Content=null;}};
            BeginAnimation(OpacityProperty,fade);
        }
        // The busy spinner turns forever; stop it before its icon is replaced or removed.
        void StopSpin() {
            var grid=icon.Content as Grid;
            if(grid==null)return;
            foreach(UIElement child in grid.Children) {
                var turn=child.RenderTransform as RotateTransform;
                if(turn!=null)turn.BeginAnimation(RotateTransform.AngleProperty,null);
            }
        }
    }
}
