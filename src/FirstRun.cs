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
            UI.WindowSetup(this,"Love My Shelf");
            Width=Math.Min(520,SystemParameters.WorkArea.Width);SizeToContent=SizeToContent.Height;
            ResizeMode=ResizeMode.NoResize;
            var content=new DockPanel();
            content.Children.Add(new WindowTitleBar(this));
            content.Children.Add(page);Content=content;
            if(settings.LanguageChosen)ShowNotice();else ShowLanguage();
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
                ShowNotice();
            },true);
            button.FontSize=16;button.MinHeight=48;button.Margin=new Thickness(0,0,8,0);
            return button;
        }

        void ShowNotice() {
            page.Children.Clear();
            Title="Love My Shelf";
            var title=UI.Text(L.T("Welcome to Love My Shelf"),18);
            title.FontWeight=FontWeights.SemiBold;page.Children.Add(title);
            var lines=new[]{
                L.T("Love My Shelf is a program that shows the photos, GIFs and videos in your folders at a glance, inside your own computer. It doesn’t upload anything to the internet.")
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

        // Two equal columns without pulling in UniformGrid's namespace for one use.
        sealed class UniformGrid2 : System.Windows.Controls.Primitives.UniformGrid {
            internal UniformGrid2() {Columns=2;Rows=1;}
        }
    }
}
