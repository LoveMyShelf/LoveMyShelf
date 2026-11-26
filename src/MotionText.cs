using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LoveMyShelf {
    // The fonts the line of text on a moving cover can use: free fonts that come with Love My Shelf (in the fonts
    // folder beside the program, SIL Open Font License) and the fonts installed on this computer. The preview and the
    // file use the very same font file, so what you see is what is made.
    internal sealed class TextFont {
        public string Id,Name,Path;
        public bool Hangul,Ours;
        public override string ToString() {return Name;}
    }
    internal static class TextFonts {
        // Our own, in the order they are shown. Only the ones whose file is there are offered.
        static readonly string[][] Bundled={
            new[]{"","Pretendard-Bold.otf","Pretendard (basic)","프리텐다드 (기본)"},
            new[]{"blackhansans","BlackHanSans-Regular.ttf","Black Han Sans (bold titles)","검은고딕 (굵은 제목)"},
            new[]{"dohyeon","DoHyeon-Regular.ttf","Do Hyeon (sign letters)","도현 (간판 글씨)"},
            new[]{"gowundodum","GowunDodum-Regular.ttf","Gowun Dodum (soft)","고운돋움 (부드러운 글씨)"}};
        static string FontsFolder {get {return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"fonts");}}
        internal static List<TextFont> Ours() {
            var list=new List<TextFont>();
            foreach(var b in Bundled) {
                string path=System.IO.Path.Combine(FontsFolder,b[1]);
                if(File.Exists(path))list.Add(new TextFont {Id=b[0],Name=L.P(b[2],b[3]),Path=path,Hangul=true,Ours=true});
            }
            return list;
        }
        static string[] SystemFolders() {
            return new[]{System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"Fonts"),
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Microsoft","Windows","Fonts")};
        }
        // The installed fonts, read once (in the background) and kept. A collection file (.ttc) holds several fonts
        // and FFmpeg can only take its first one, so those are left out.
        static Task<List<TextFont>> installed;
        internal static Task<List<TextFont>> Installed() {
            if(installed==null)installed=Task.Run(()=>ReadInstalled());
            return installed;
        }
        static List<TextFont> ReadInstalled() {
            var found=new List<TextFont>();var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var folder in SystemFolders()) {
                string[] files;
                try {files=Directory.GetFiles(folder);} catch(Exception) {continue;}
                foreach(var file in files) {
                    string ext=System.IO.Path.GetExtension(file).ToLowerInvariant();
                    if(ext!=".ttf" && ext!=".otf")continue;
                    try {
                        var face=new GlyphTypeface(UriOf(file));
                        if(face.Symbol)continue;
                        string family=NameOf(face.Win32FamilyNames),style=NameOf(face.Win32FaceNames);
                        if(String.IsNullOrEmpty(family))family=NameOf(face.FamilyNames);
                        if(String.IsNullOrEmpty(family))continue;
                        string name=String.IsNullOrEmpty(style) || style.Equals("Regular",StringComparison.OrdinalIgnoreCase) || style=="보통"?family:family+" "+style;
                        if(!names.Add(name))continue;
                        found.Add(new TextFont {Id="sys:"+System.IO.Path.GetFileName(file),Name=name,Path=file,Hangul=face.CharacterToGlyphMap.ContainsKey('가')});
                    } catch(Exception) {}
                }
            }
            // Fonts with Korean letters first, then by name.
            return found.OrderBy(f=>f.Hangul?0:1).ThenBy(f=>f.Name,StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        static string NameOf(IDictionary<CultureInfo,string> names) {
            if(names==null || names.Count==0)return null;
            string name;
            if(L.Korean && names.TryGetValue(CultureInfo.GetCultureInfo("ko-KR"),out name))return name;
            if(names.TryGetValue(CultureInfo.GetCultureInfo("en-US"),out name))return name;
            return names.Values.First();
        }
        // The file for a saved choice: our own font, an installed font, or (gone, or never picked) the usual one.
        // Whether an installed font's file is still on this computer.
        internal static bool Installs(string id) {
            if(String.IsNullOrEmpty(id) || !id.StartsWith("sys:",StringComparison.Ordinal))return false;
            string file=System.IO.Path.GetFileName(id.Substring(4));
            return file.Length>0 && SystemFolders().Any(folder=>File.Exists(System.IO.Path.Combine(folder,file)));
        }
        internal static string PathOf(string id) {
            if(!String.IsNullOrEmpty(id)) {
                if(id.StartsWith("sys:",StringComparison.Ordinal)) {
                    string file=System.IO.Path.GetFileName(id.Substring(4));
                    foreach(var folder in SystemFolders()) {
                        string path=System.IO.Path.Combine(folder,file);
                        if(file.Length>0 && File.Exists(path))return path;
                    }
                } else foreach(var b in Bundled) {
                    if(b[0]!=id)continue;
                    string path=System.IO.Path.Combine(FontsFolder,b[1]);
                    if(File.Exists(path))return path;
                }
            }
            return MotionLook.FontFile();
        }
        static readonly Dictionary<string,GlyphTypeface> faces=new Dictionary<string,GlyphTypeface>(StringComparer.OrdinalIgnoreCase);
        // A file path as a URI; a # or % in a folder name would otherwise be read as part of the address.
        internal static Uri UriOf(string path) {return new Uri("file:///"+path.Replace('\\','/').Replace("%","%25").Replace("#","%23"));}
        internal static GlyphTypeface Face(string path) {
            if(String.IsNullOrEmpty(path))return null;
            GlyphTypeface face;
            lock(faces) {
                if(faces.TryGetValue(path,out face))return face;
                try {face=new GlyphTypeface(UriOf(path));} catch(Exception) {face=null;}
                faces[path]=face;
                return face;
            }
        }
        // The text's outline at a size, drawn from the font file itself (letters the font doesn't have show as its
        // empty box, the same as in the file). Null when the font can't be read.
        internal static Geometry Outline(string path,string text,double px,out bool missing) {
            missing=false;
            var face=Face(path);
            if(face==null || String.IsNullOrEmpty(text))return null;
            var group=new GeometryGroup {FillRule=FillRule.Nonzero};
            double x=0,baseline=face.Baseline*px;
            var map=face.CharacterToGlyphMap;
            for(int i=0;i<text.Length;i++) {
                int code=text[i];
                if(Char.IsSurrogatePair(text,i)){code=Char.ConvertToUtf32(text[i],text[i+1]);i++;}
                ushort glyph;
                if(!map.TryGetValue(code,out glyph)){glyph=0;if(code!=' ')missing=true;}
                var shape=face.GetGlyphOutline(glyph,px,px);
                if(shape!=null && !shape.IsEmpty()) {
                    shape=shape.Clone();shape.Transform=new TranslateTransform(x,baseline);
                    group.Children.Add(shape);
                }
                double advance;
                x+=face.AdvanceWidths.TryGetValue(glyph,out advance)?advance*px:px*0.5;
            }
            return group;
        }
        internal static bool HasHangul(string path) {
            var face=Face(path);
            return face!=null && face.CharacterToGlyphMap.ContainsKey('가');
        }
    }

    // How the line of text looks: colors as 0xRRGGBB, the edge's thickness as a percent of the letters' size
    // (-1 = the old automatic 1/12), and an optional band behind the text (how solid, 0..100).
    internal static class TextLook {
        internal const int White=0xFFFFFF,Black=0x000000,Yellow=0xFFE14D;
        internal static readonly int[] Swatches={0xFFFFFF,0x000000,0xFFE14D,0xFF4D4D,0xFF9F1C,0x3DDC84,0x4DB8FF,0xFF7EB6};
        internal static string Hex(int rgb) {return "0x"+(rgb&0xFFFFFF).ToString("X6",CultureInfo.InvariantCulture);}
        internal static Color ColorOf(int rgb,double alpha=1) {return Color.FromArgb((byte)Math.Round(255*Math.Max(0,Math.Min(1,alpha))),(byte)(rgb>>16&255),(byte)(rgb>>8&255),(byte)(rgb&255));}
        // Pixels of the edge for letters px tall: 0 = no edge.
        internal static int EdgePixels(MotionRequest r,int px) {
            if(r.EdgeSize<0)return Math.Max(1,(int)Math.Round(px/12.0));
            return r.EdgeSize<=0?0:Math.Max(1,(int)Math.Round(px*r.EdgeSize/100.0));
        }
        internal static int BandPad(int px) {return Math.Max(2,(int)Math.Round(px*0.22));}
    }

    // [Text style ▾]: folded away until wanted. Quick looks, the font, the letters' color, the edge (color and thickness)
    // and a band behind the text.
    internal sealed class TextStylePanel : StackPanel {
        internal event Action Changed;
        internal string FontId="";
        internal int TextColor=TextLook.White,EdgeColor=TextLook.Black,BandColor=TextLook.Black;
        internal double EdgeSize=-1,BandStrength=60;
        internal bool Band;
        static bool lastOpen;
        readonly Button opener;
        readonly StackPanel body=new StackPanel {Margin=new Thickness(0,6,0,4)};
        readonly ComboBox fonts=new ComboBox {MinWidth=200,MaxWidth=320,MinHeight=26,HorizontalAlignment=HorizontalAlignment.Left};
        readonly TextBlock fontNote=UI.Text("",12,true);
        readonly CheckBox bandOn=new CheckBox {FontSize=13,Margin=new Thickness(0,2,0,2)};
        Action<int> showText,showEdge,showBand;
        Action<double> writeEdge,writeBand;
        Func<double> readEdge;
        FrameworkElement bandRows;
        bool loading,filling;
        readonly Func<string,FrameworkElement,FrameworkElement> field;

        internal TextStylePanel(Func<string,FrameworkElement,FrameworkElement> field) {
            this.field=field;
            opener=UI.Btn("",Toggle);opener.HorizontalAlignment=HorizontalAlignment.Left;opener.Margin=new Thickness(0,2,0,2);
            AutomationProperties.SetName(opener,L.P("Text style","글자 꾸미기"));
            opener.ToolTip=L.P("The font, the color of the letters and their edge, and a band behind them.","글꼴, 글자색, 테두리, 글자 뒤 띠를 바꿔요.");
            Children.Add(opener);Children.Add(body);
            Build();
            body.Visibility=lastOpen?Visibility.Visible:Visibility.Collapsed;
            ShowOpener();
            if(lastOpen)Loaded+=delegate {LoadInstalled();};
        }
        void Toggle() {
            lastOpen=body.Visibility!=Visibility.Visible;
            body.Visibility=lastOpen?Visibility.Visible:Visibility.Collapsed;
            ShowOpener();
            if(lastOpen)LoadInstalled();
        }
        void ShowOpener() {opener.Content=L.P("Text style","글자 꾸미기")+(body.Visibility==Visibility.Visible?"  ▴":"  ▾");}
        void Fire() {if(!filling && Changed!=null)Changed();}

        void Build() {
            // Quick looks: one click sets the colors, the edge and the band together.
            var looks=new WrapPanel();
            looks.Children.Add(LookButton(L.P("White, black edge","흰 글자·검은 테두리"),TextLook.White,TextLook.Black,false));
            looks.Children.Add(LookButton(L.P("Yellow, black edge","노란 글자·검은 테두리"),TextLook.Yellow,TextLook.Black,false));
            looks.Children.Add(LookButton(L.P("White on a dark band","검은 띠 위 흰 글자"),TextLook.White,TextLook.Black,true));
            body.Children.Add(field(L.P("Quick look","빠른 스타일"),looks));
            // Font.
            var fontBox=new StackPanel();
            AutomationProperties.SetName(fonts,L.P("Font","글꼴"));
            fonts.SelectionChanged+=delegate {
                if(loading)return;
                var f=fonts.SelectedItem as TextFont;
                if(f==null)return;
                FontId=f.Id;ShowFontNote();Fire();
            };
            fonts.DropDownOpened+=delegate {LoadInstalled();};
            fontBox.Children.Add(fonts);
            fontNote.TextWrapping=TextWrapping.Wrap;fontNote.Margin=new Thickness(1,2,0,4);fontNote.MaxWidth=320;fontNote.HorizontalAlignment=HorizontalAlignment.Left;
            fontBox.Children.Add(fontNote);
            body.Children.Add(field(L.P("Font","글꼴"),fontBox));
            FillFonts(null);
            // Colors.
            body.Children.Add(field(L.P("Letter color","글자색"),Colors(L.P("Letter color","글자색"),()=>TextColor,v=>{TextColor=v;Fire();},out showText)));
            body.Children.Add(field(L.P("Edge color","테두리 색"),Colors(L.P("Edge color","테두리 색"),()=>EdgeColor,v=>{EdgeColor=v;Fire();},out showEdge)));
            body.Children.Add(field(L.P("Edge thickness","테두리 두께"),MotionLook.Number(EdgeSize<0?8:EdgeSize,0,30,1,L.P("% of the letters (0 = none)","% (글자 크기 기준, 0이면 없음)"),
                L.P("Edge thickness","테두리 두께"),v=>{EdgeSize=v;Fire();},out readEdge,out writeEdge)));
            // A band behind the text.
            bandOn.Content=L.P("A band behind the text","글자 뒤에 띠 깔기");
            bandOn.SetResourceReference(Control.ForegroundProperty,"Text");
            RoutedEventHandler band=delegate {Band=bandOn.IsChecked==true;bandRows.Visibility=Band?Visibility.Visible:Visibility.Collapsed;Fire();};
            bandOn.Checked+=band;bandOn.Unchecked+=band;
            body.Children.Add(field(L.P("Band","띠"),bandOn));
            var rows=new StackPanel();
            Func<double> readBand;
            rows.Children.Add(field(L.P("Band color","띠 색"),Colors(L.P("Band color","띠 색"),()=>BandColor,v=>{BandColor=v;Fire();},out showBand)));
            rows.Children.Add(field(L.P("Band strength","띠 진하기"),MotionLook.Number(BandStrength,5,100,5,L.P("% (100 = can't see through)","% (100이면 안 비쳐요)"),
                L.P("Band strength","띠 진하기"),v=>{BandStrength=v;Fire();},out readBand,out writeBand)));
            bandRows=rows;bandRows.Visibility=Visibility.Collapsed;
            body.Children.Add(rows);
            var note=UI.Text(NoticeLook.KeepWords(L.P("A GIF has only 256 colors, so a see-through band can look a little rough. WebP and MP4 keep it smooth.",
                "GIF는 색이 256개뿐이라 비치는 띠가 조금 거칠게 보일 수 있어요. WebP와 MP4는 매끈해요.")),12,true);
            note.TextWrapping=TextWrapping.Wrap;note.Margin=new Thickness(0,2,0,4);note.MaxWidth=420;note.HorizontalAlignment=HorizontalAlignment.Left;
            body.Children.Add(note);
        }
        Button LookButton(string label,int text,int edge,bool withBand) {
            var sample=new Border {Width=26,Height=18,CornerRadius=new CornerRadius(4),Margin=new Thickness(0,0,6,0),BorderThickness=new Thickness(1)};
            sample.Background=new SolidColorBrush(withBand?TextLook.ColorOf(0x202020):TextLook.ColorOf(0x8A9099));
            sample.BorderBrush=new SolidColorBrush(TextLook.ColorOf(0x6B7684));
            var letter=new TextBlock {Text="가",FontWeight=FontWeights.Bold,FontSize=12,Foreground=new SolidColorBrush(TextLook.ColorOf(text)),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
            sample.Child=letter;
            var row=new StackPanel {Orientation=Orientation.Horizontal};
            row.Children.Add(sample);row.Children.Add(new TextBlock {Text=label,VerticalAlignment=VerticalAlignment.Center});
            var b=new Button {Content=row,MinHeight=27,Margin=new Thickness(0,0,6,4),Padding=new Thickness(6,1,8,1)};
            AutomationProperties.SetName(b,label);
            b.Click+=delegate {
                filling=true;
                TextColor=text;EdgeColor=edge;EdgeSize=-1;Band=withBand;BandColor=TextLook.Black;BandStrength=60;
                Show();
                filling=false;Fire();
            };
            return b;
        }
        // A row of color chips plus [Other color…] (the Windows color window, where numbers can be typed too).
        FrameworkElement Colors(string name,Func<int> read,Action<int> pick,out Action<int> show) {
            var row=new WrapPanel {VerticalAlignment=VerticalAlignment.Center};
            var chips=new List<Border>();
            Action<int> mark=null;
            foreach(int rgb in TextLook.Swatches) {
                int value=rgb;
                var chip=new Border {Width=22,Height=22,CornerRadius=new CornerRadius(11),Margin=new Thickness(0,2,5,2),BorderThickness=new Thickness(2),Cursor=Cursors.Hand,
                    Background=new SolidColorBrush(TextLook.ColorOf(value)),Tag=value,Focusable=true};
                AutomationProperties.SetName(chip,name+" "+TextLook.Hex(value).Substring(2));
                chip.ToolTip="#"+TextLook.Hex(value).Substring(2);
                chip.MouseLeftButtonUp+=(s,e)=>{pick(value);mark(value);e.Handled=true;};
                chip.KeyDown+=(s,e)=>{if(e.Key==Key.Enter || e.Key==Key.Space){pick(value);mark(value);e.Handled=true;}};
                chips.Add(chip);row.Children.Add(chip);
            }
            var own=new Border {Width=22,Height=22,CornerRadius=new CornerRadius(11),Margin=new Thickness(0,2,5,2),BorderThickness=new Thickness(2),Visibility=Visibility.Collapsed};
            row.Children.Add(own);
            var other=UI.Btn(L.P("Other color…","다른 색…"),()=>{
                using(var dialog=new System.Windows.Forms.ColorDialog {FullOpen=true,AnyColor=true}) {
                    int now=read();
                    dialog.Color=System.Drawing.Color.FromArgb(now>>16&255,now>>8&255,now&255);
                    if(dialog.ShowDialog()!=System.Windows.Forms.DialogResult.OK)return;
                    int rgb=dialog.Color.R<<16|dialog.Color.G<<8|dialog.Color.B;
                    pick(rgb);mark(rgb);
                }
            });
            other.Margin=new Thickness(2,0,0,0);other.MinHeight=24;
            AutomationProperties.SetName(other,name+" · "+L.P("Other color","다른 색"));
            row.Children.Add(other);
            mark=value=>{
                bool listed=false;
                foreach(var chip in chips) {
                    bool on=(int)chip.Tag==value;listed|=on;
                    if(on)chip.SetResourceReference(Border.BorderBrushProperty,"Accent");
                    else chip.BorderBrush=new SolidColorBrush(TextLook.ColorOf(0xC5CAD1));
                }
                own.Visibility=listed?Visibility.Collapsed:Visibility.Visible;
                own.Background=new SolidColorBrush(TextLook.ColorOf(value));own.SetResourceReference(Border.BorderBrushProperty,"Accent");
                own.ToolTip="#"+TextLook.Hex(value).Substring(2);
            };
            mark(read());
            show=mark;
            return row;
        }
        // Our fonts first; the installed ones join the list once read (the first time the list or the panel opens).
        void FillFonts(List<TextFont> installed) {
            loading=true;
            fonts.Items.Clear();
            var ours=TextFonts.Ours();
            TextFont selected=null;
            foreach(var f in ours){var item=f;fonts.Items.Add(item);if(item.Id==FontId)selected=item;}
            if(installed==null) {
                fonts.Items.Add(new ComboBoxItem {Content=L.P("Fonts on this computer: open the list to load them","내 컴퓨터 글꼴은 목록을 열면 불러와요"),IsEnabled=false});
            } else {
                fonts.Items.Add(new Separator());
                foreach(var f in installed) {
                    var item=new TextFont {Id=f.Id,Name=f.Hangul?f.Name:f.Name+L.P(" (no Korean letters)"," (한글 없음)"),Path=f.Path,Hangul=f.Hangul};
                    fonts.Items.Add(item);
                    if(item.Id==FontId)selected=item;
                }
            }
            if(selected==null && !String.IsNullOrEmpty(FontId) && FontId.StartsWith("sys:",StringComparison.Ordinal) && installed==null && TextFonts.Installs(FontId)) {
                // A font picked earlier, before the installed list is read.
                string path=TextFonts.PathOf(FontId);
                selected=new TextFont {Id=FontId,Name=System.IO.Path.GetFileNameWithoutExtension(path),Path=path,Hangul=TextFonts.HasHangul(path)};
                fonts.Items.Insert(ours.Count,selected);
            }
            // A font that is gone (or never there): the basic one, and the choice says so too.
            if(selected==null && ours.Count>0){selected=ours[0];FontId=selected.Id;}
            fonts.SelectedItem=selected;
            loading=false;
            ShowFontNote();
        }
        bool asked;
        async void LoadInstalled() {
            if(asked)return;
            asked=true;
            List<TextFont> list;
            try {list=await TextFonts.Installed();} catch(Exception) {return;}
            bool open=fonts.IsDropDownOpen;
            FillFonts(list);
            if(open)fonts.IsDropDownOpen=true;
        }
        void ShowFontNote() {
            var f=fonts.SelectedItem as TextFont;
            string note=null;
            if(f!=null && !f.Hangul)note=L.P("This font has no Korean letters, so Korean shows as empty boxes.","이 글꼴에는 한글이 없어서 한글이 빈 네모로 나와요.");
            else if(f!=null && !f.Ours)note=L.P("A font on this computer: fine for your own use. Check its own terms before using it for work.","내 컴퓨터에 깔린 글꼴이에요. 개인적으로 쓰는 건 괜찮고, 일로 쓸 때는 그 글꼴의 사용 조건을 확인해 주세요.");
            fontNote.Text=note==null?"":NoticeLook.KeepWords(note);
            fontNote.Visibility=note==null?Visibility.Collapsed:Visibility.Visible;
        }
        // Puts saved or remembered choices on the controls without firing Changed.
        internal void Load(string font,int text,int edge,double edgeSize,bool band,int bandColor,double bandStrength) {
            filling=true;
            FontId=font??"";TextColor=text;EdgeColor=edge;EdgeSize=edgeSize;Band=band;BandColor=bandColor;BandStrength=bandStrength;
            FillFonts(installedNow);
            Show();
            filling=false;
        }
        List<TextFont> installedNow {get {return installedTask!=null && installedTask.Status==TaskStatus.RanToCompletion?installedTask.Result:null;}}
        Task<List<TextFont>> installedTask {get {return asked?TextFonts.Installed():null;}}
        void Show() {
            showText(TextColor);showEdge(EdgeColor);showBand(BandColor);
            writeEdge(EdgeSize<0?8:EdgeSize);writeBand(BandStrength);
            bandOn.IsChecked=Band;bandRows.Visibility=Band?Visibility.Visible:Visibility.Collapsed;
        }
    }
}
