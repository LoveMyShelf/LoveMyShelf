using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace LoveMyShelf {
    // A box drawn over the large view: the part of the picture a new cover starts from. It is kept apart from the
    // view's own zoom, turn and place, and stored the way a cover's crop is (ViewFraming.CoverFrame): as fractions of
    // the upright picture, in the picture turned by Turn degrees, so the box sits still on the picture while it zooms.
    internal sealed class DrawnArea {
        internal Rect Fraction;
        internal double Turn; // the view's turn when the box was drawn, degrees clockwise
        // Width over height in pixels, for a picture of this upright shape.
        internal double Aspect(double upright) {return Fraction.Height<=0?0:Fraction.Width/Fraction.Height*upright;}
        internal DrawnArea Copy() {return new DrawnArea {Fraction=Fraction,Turn=Turn};}
    }

    // How the large view shows the picture right now: where its middle is on screen, its upright size there,
    // its turn, and the room it has (the window without the bar under it).
    internal struct AreaView {
        internal Point Middle;
        internal Size Picture;
        internal double Turn;
        internal Rect Room;
        internal double Upright {get {return Picture.Height<=0?0:Picture.Width/Picture.Height;}}
    }

    // Draws the box over the large view and the bar to change it. The large view hands its left-button presses here
    // while drawing is on (Press, Drag, Release); a double-click, a right-click and the playback bar keep their jobs.
    internal sealed class CoverAreaLayer : Grid {
        readonly Func<AreaView?> view;
        readonly Canvas canvas=new Canvas {IsHitTestVisible=false};
        readonly Path shade=new Path {Fill=new SolidColorBrush(Color.FromArgb(0x78,0,0,0))};
        readonly Path under=new Path {Stroke=new SolidColorBrush(Color.FromArgb(0xB0,0,0,0)),StrokeThickness=4};
        readonly Path line=new Path {Stroke=Brushes.White,StrokeThickness=2};
        readonly Path grips=new Path {Fill=Brushes.White,Stroke=new SolidColorBrush(Color.FromArgb(0xC0,0,0,0)),StrokeThickness=1};
        readonly Border bar=new Border(),chip=new Border();
        readonly TextBox left=Number(),top=Number(),wide=Number(),tall=Number(),across=Ratio(),down=Ratio();
        readonly CheckBox keep=new CheckBox();
        DrawnArea area;
        bool editing,available;
        // The shape kept while "Keep this shape" is on (width over height in pixels); 0 = none picked yet.
        double shape;
        bool locked;
        // While the mouse is down: what is being done, and where things were when it started.
        enum Grip {None,New,Move,Corner}
        Grip grip;
        Point pressAt,startMiddle;
        Size startSize;
        Vector fixedCorner; // the opposite corner, in the box's own turned frame, while a corner is dragged
        DrawnArea before;
        // Drawn this session: the next box of any video starts with the same shape when it is kept.
        static double lastShape;
        const double GripReach=11,Smallest=12;

        internal event Action Changed;
        // Drawing turned on or off (the large view's button follows it).
        internal event Action EditingChanged;
        internal Action MakeCover;
        // Gives the keyboard back to the large view (its shortcuts), after a number is typed or a button pressed.
        internal Action ReturnKeys;
        // A number typed and not applied yet: which place/size box, and whether a shape number.
        TextBox numbersTyped;
        bool shapeTyped;
        internal DrawnArea Area {get {return area==null?null:area.Copy();}}
        internal bool Editing {get {return editing;}}
        internal bool Dragging {get {return grip!=Grip.None;}}
        internal bool Locked {get {return locked;}}
        internal double Shape {get {return shape;}}

        internal CoverAreaLayer(Func<AreaView?> view) {
            this.view=view;
            canvas.Children.Add(shade);canvas.Children.Add(under);canvas.Children.Add(line);canvas.Children.Add(grips);
            Children.Add(canvas);
            var prefs=CropSurface.Prefs;
            locked=prefs!=null && prefs.CropLock;
            if(locked && lastShape>0)shape=lastShape;
            BuildBar();BuildChip();
            SizeChanged+=delegate {Refresh();};
            Refresh();
        }

        // The large view says whether a box can be drawn now (a video, GIF or WebP is playing).
        internal bool Available {
            get {return available;}
            set {
                if(available==value)return;available=value;
                if(!value){if(grip!=Grip.None){area=before;grip=Grip.None;}editing=false;Forget();LeaveKeys();}
                Refresh();Told();
            }
        }
        internal void StartEditing() {if(!available)return;editing=true;Refresh();Told();}
        internal void StopEditing() {if(grip!=Grip.None)Release();Forget();LeaveKeys();editing=false;Refresh();Told();}
        // Esc while typing a number: the typed text is dropped and the keys go back to the view.
        internal void CancelTyping() {Forget();LeaveKeys();Refresh();}
        void Told() {if(EditingChanged!=null)EditingChanged();}
        internal void Clear() {
            grip=Grip.None;Forget();
            if(area==null){Refresh();return;}
            area=null;Refresh();Raise();
        }

        // ----- Turning screen places into the picture's fractions and back -----
        static Vector Turned(Vector v,double degrees) {var m=Matrix.Identity;m.Rotate(degrees);return m.Transform(v);}
        static DrawnArea FromScreen(AreaView v,Point middle,Size size,double turn) {
            Vector inPicture=Turned(middle-v.Middle,-v.Turn);
            double w=size.Width/v.Picture.Width,h=size.Height/v.Picture.Height;
            double x=0.5+inPicture.X/v.Picture.Width,y=0.5+inPicture.Y/v.Picture.Height;
            return new DrawnArea {Fraction=new Rect(x-w/2,y-h/2,w,h),Turn=turn};
        }
        static void ToScreen(AreaView v,DrawnArea a,out Point middle,out Size size,out double tilt) {
            var f=a.Fraction;
            var inPicture=new Vector((f.X+f.Width/2-0.5)*v.Picture.Width,(f.Y+f.Height/2-0.5)*v.Picture.Height);
            middle=v.Middle+Turned(inPicture,v.Turn);
            size=new Size(f.Width*v.Picture.Width,f.Height*v.Picture.Height);
            tilt=v.Turn-a.Turn;
        }
        static Point Inside(Rect room,Point p) {
            return new Point(Math.Max(room.Left,Math.Min(room.Right,p.X)),Math.Max(room.Top,Math.Min(room.Bottom,p.Y)));
        }
        // A box of this shape (width over height) as near as possible to the size asked for.
        static Size Shaped(double width,double height,double aspect) {
            if(aspect<=0)return new Size(width,height);
            if(width>=height*aspect)height=width/aspect;else width=height*aspect;
            return new Size(width,height);
        }

        // ----- The mouse, handed over by the large view -----
        internal Cursor CursorAt(Point p) {
            AreaView v;Point middle;Size size;double tilt;
            if(!editing || !Where(out v,out middle,out size,out tilt))return Cursors.Cross;
            var local=Turned(p-middle,-tilt);
            if(Math.Abs(Math.Abs(local.X)-size.Width/2)<=GripReach && Math.Abs(Math.Abs(local.Y)-size.Height/2)<=GripReach)
                return (local.X<0)==(local.Y<0)?Cursors.SizeNWSE:Cursors.SizeNESW;
            if(Math.Abs(local.X)<=size.Width/2 && Math.Abs(local.Y)<=size.Height/2)return Cursors.SizeAll;
            return Cursors.Cross;
        }
        bool Where(out AreaView v,out Point middle,out Size size,out double tilt) {
            middle=new Point();size=new Size();tilt=0;v=new AreaView();
            var shown=view();
            if(!shown.HasValue || shown.Value.Picture.Width<=0 || shown.Value.Picture.Height<=0)return false;
            v=shown.Value;
            if(area==null)return false;
            ToScreen(v,area,out middle,out size,out tilt);
            return true;
        }
        // True when the press starts drawing, moving or resizing (the large view then holds the mouse).
        internal bool Press(Point p) {
            if(!editing)return false;
            var shown=view();
            if(!shown.HasValue || shown.Value.Picture.Width<=0 || !shown.Value.Room.Contains(p))return false;
            Forget();LeaveKeys();
            var v=shown.Value;
            before=area==null?null:area.Copy();
            pressAt=p;
            if(area!=null) {
                Point middle;Size size;double tilt;
                ToScreen(v,area,out middle,out size,out tilt);
                var local=Turned(p-middle,-tilt);
                startMiddle=middle;startSize=size;
                if(Math.Abs(Math.Abs(local.X)-size.Width/2)<=GripReach && Math.Abs(Math.Abs(local.Y)-size.Height/2)<=GripReach) {
                    fixedCorner=new Vector(local.X<0?size.Width/2:-size.Width/2,local.Y<0?size.Height/2:-size.Height/2);
                    grip=Grip.Corner;return true;
                }
                if(Math.Abs(local.X)<=size.Width/2 && Math.Abs(local.Y)<=size.Height/2){grip=Grip.Move;return true;}
            }
            grip=Grip.New;return true;
        }
        internal void Drag(Point p) {
            if(grip==Grip.None)return;
            var shown=view();
            if(!shown.HasValue || shown.Value.Picture.Width<=0)return;
            var v=shown.Value;
            double kept=locked?shape:0;
            if(grip==Grip.New) {
                p=Inside(v.Room,p);
                double w=Math.Abs(p.X-pressAt.X),h=Math.Abs(p.Y-pressAt.Y);
                if(w<2 && h<2)return;
                var size=Shaped(w,h,kept);
                var corner=new Point(pressAt.X+(p.X<pressAt.X?-size.Width:size.Width),pressAt.Y+(p.Y<pressAt.Y?-size.Height:size.Height));
                area=FromScreen(v,new Point((pressAt.X+corner.X)/2,(pressAt.Y+corner.Y)/2),size,v.Turn);
            } else if(grip==Grip.Move) {
                var middle=Inside(v.Room,startMiddle+(p-pressAt));
                area=FromScreen(v,middle,startSize,area.Turn);
            } else {
                double tilt=v.Turn-area.Turn;
                var local=Turned(p-startMiddle,-tilt);
                double w=Math.Max(Smallest,Math.Abs(local.X-fixedCorner.X)),h=Math.Max(Smallest,Math.Abs(local.Y-fixedCorner.Y));
                var size=Shaped(w,h,kept>0?kept:0);
                var far=new Vector(fixedCorner.X+(local.X<fixedCorner.X?-size.Width:size.Width),fixedCorner.Y+(local.Y<fixedCorner.Y?-size.Height:size.Height));
                var middle=startMiddle+Turned((fixedCorner+far)/2,tilt);
                area=FromScreen(v,middle,size,area.Turn);
            }
            Refresh();
        }
        internal void Release() {
            if(grip==Grip.None)return;
            var was=grip;grip=Grip.None;
            var shown=view();
            double upright=shown.HasValue?shown.Value.Upright:0;
            if(was==Grip.New) {
                // A click (or the first click of a double-click) draws nothing: the box that was there stays.
                if(area==null || !shown.HasValue)area=before;
                else {
                    Point middle;Size size;double tilt;
                    ToScreen(shown.Value,area,out middle,out size,out tilt);
                    if(size.Width<8 || size.Height<8)area=before;
                }
            }
            if(area!=null && upright>0)area=KeepOnPicture(area,upright,locked?shape:0)??before;
            if(was==Grip.New && area!=null && locked && shape<=0 && upright>0)Keep(area.Aspect(upright));
            Refresh();
            if(!SameAs(before,area))Raise();
        }
        static bool SameAs(DrawnArea a,DrawnArea b) {return a==null?b==null:b!=null && a.Fraction==b.Fraction && a.Turn==b.Turn;}
        // Upright, a box reaching past the picture is cut to the picture (the black bands beside it are not part of it),
        // keeping its shape when the shape is kept. A turned box is left to the cover editor, which keeps the frame inside.
        // Null when nothing of it is on the picture.
        static DrawnArea KeepOnPicture(DrawnArea a,double upright,double keepShape) {
            if(Math.Abs(a.Turn)>0.01)return a;
            var f=a.Fraction;f.Intersect(new Rect(0,0,1,1));
            if(f.IsEmpty || f.Width<0.002 || f.Height<0.002)return null;
            if(keepShape>0 && upright>0) {
                double want=keepShape/upright; // width over height in fractions
                if(f.Width/f.Height>want){double w=f.Height*want;f=new Rect(f.X+(f.Width-w)/2,f.Y,w,f.Height);}
                else {double h=f.Width/want;f=new Rect(f.X,f.Y+(f.Height-h)/2,f.Width,h);}
            }
            return new DrawnArea {Fraction=f,Turn=a.Turn};
        }
        void Raise() {if(Changed!=null)Changed();}

        // ----- Shape and exact numbers -----
        void Keep(double aspect) {if(aspect>0 && !Double.IsInfinity(aspect)){shape=aspect;lastShape=aspect;}}
        internal void SetLocked(bool on) {
            locked=on;
            if(on) {
                var shown=view();
                if(area!=null && shown.HasValue && shown.Value.Upright>0)Keep(area.Aspect(shown.Value.Upright));
                else if(shape<=0 && lastShape>0)shape=lastShape;
            }
            if(keep.IsChecked!=on)keep.IsChecked=on;
            GalleryWindow.RememberCropLock(on);
            Refresh();
        }
        // Gives the box this shape (width over height in pixels) around its middle; with no box, one in the middle of the view.
        internal void SetShape(double aspect) {
            if(!(aspect>0) || Double.IsInfinity(aspect))return;
            Forget();
            Keep(aspect);
            var shown=view();
            if(!shown.HasValue || shown.Value.Picture.Width<=0){Refresh();return;}
            var v=shown.Value;
            Point middle;Size size;double tilt;double turn=v.Turn;
            if(area!=null){ToScreen(v,area,out middle,out size,out tilt);turn=area.Turn;}
            else {
                // The part of the picture that shows: the picture's turned box, cut to the room.
                var box=TurnedSize(v.Picture,v.Turn);
                var seen=new Rect(v.Middle.X-box.Width/2,v.Middle.Y-box.Height/2,box.Width,box.Height);seen.Intersect(v.Room);
                if(seen.IsEmpty)seen=v.Room;
                middle=new Point(seen.X+seen.Width/2,seen.Y+seen.Height/2);size=new Size(seen.Width*0.8,seen.Height*0.8);
            }
            double h=size.Height,w=h*aspect;
            // Not bigger than the room.
            double fit=Math.Min(1,Math.Min(v.Room.Width*0.96/Math.Max(1,w),v.Room.Height*0.96/Math.Max(1,h)));
            area=FromScreen(v,middle,new Size(w*fit,h*fit),turn);
            area=KeepOnPicture(area,v.Upright,aspect)??area;
            Refresh();Raise();
        }
        static Size TurnedSize(Size s,double degrees) {
            double a=degrees*Math.PI/180,c=Math.Abs(Math.Cos(a)),n=Math.Abs(Math.Sin(a));
            return new Size(s.Width*c+s.Height*n,s.Width*n+s.Height*c);
        }
        // Places the box by numbers: left, top, width and height in % of the picture.
        internal void Place(double x,double y,double w,double h) {
            if(Double.IsNaN(x+y+w+h) || Double.IsInfinity(x+y+w+h))return;
            var shown=view();
            double turn=area!=null?area.Turn:shown.HasValue?shown.Value.Turn:0;
            w=Math.Max(0.5,w);h=Math.Max(0.5,h);
            if(Math.Abs(turn)<=0.01){w=Math.Min(100,w);h=Math.Min(100,h);x=Math.Max(0,Math.Min(100-w,x));y=Math.Max(0,Math.Min(100-h,y));}
            area=new DrawnArea {Fraction=new Rect(x/100,y/100,w/100,h/100),Turn=turn};
            Refresh();Raise();
        }

        // ----- The bar (while drawing) and the small note (a box is there, drawing is off) -----
        static TextBox Number() {
            return new TextBox {Width=52,MinHeight=26,VerticalContentAlignment=VerticalAlignment.Center,HorizontalContentAlignment=HorizontalAlignment.Center,FontSize=12.5};
        }
        static TextBox Ratio() {
            return new TextBox {Width=44,MinHeight=26,VerticalContentAlignment=VerticalAlignment.Center,HorizontalContentAlignment=HorizontalAlignment.Center,FontSize=12.5};
        }
        // A card at the top middle, in the app's paper and line colors (light or dark, whichever is in use).
        static void Dress(Border card) {
            card.CornerRadius=new CornerRadius(8);card.Padding=new Thickness(12,8,10,8);card.BorderThickness=new Thickness(1);
            card.HorizontalAlignment=HorizontalAlignment.Center;card.VerticalAlignment=VerticalAlignment.Top;card.Margin=new Thickness(10,10,10,0);
            card.Effect=new System.Windows.Media.Effects.DropShadowEffect {Color=Colors.Black,BlurRadius=8,ShadowDepth=2,Direction=270,Opacity=0.3};
            card.SetResourceReference(Border.BackgroundProperty,"Paper");card.SetResourceReference(Border.BorderBrushProperty,"Line");
            card.SetResourceReference(TextElement.FontFamilyProperty,"AppFont");
        }
        Button Small(string text,string tip,Action act,bool primary=false) {
            var b=UI.Btn(text,()=>{LeaveKeys();CommitTyped();act();},primary);b.Margin=new Thickness(0,2,4,2);b.Padding=new Thickness(8,0,8,0);b.MinHeight=26;b.FontSize=12.5;b.Focusable=false;
            if(tip!=null){b.ToolTip=tip;AutomationProperties.SetName(b,tip);}
            return b;
        }
        static TextBlock Label(string text,double left=0) {var t=UI.Text(text,12.5,true);t.Margin=new Thickness(left,0,5,0);return t;}
        void BuildBar() {
            var rows=new StackPanel();
            var head=new TextBlock {TextWrapping=TextWrapping.Wrap,FontSize=12.5,Margin=new Thickness(0,0,0,4)};
            head.SetResourceReference(TextBlock.ForegroundProperty,"Text");
            head.Inlines.Add(new Run(L.P("▭ Draw where to cut  ","▭ 자를 곳 그리기  ")) {FontWeight=FontWeights.SemiBold,FontSize=13.5});
            var hint=new Run(L.P(
                "Drag to draw · drag inside to move · drag a corner to resize",
                "끌어서 그리기 · 안쪽을 끌면 옮기기 · 모서리를 끌면 크기"));
            hint.SetResourceReference(TextElement.ForegroundProperty,"Subtle");
            head.Inlines.Add(hint);
            rows.Children.Add(head);
            // Place and size, in % of the picture.
            var numbers=new WrapPanel();
            numbers.Children.Add(Label(L.P("Place from left","위치 왼쪽에서")));numbers.Children.Add(left);numbers.Children.Add(Label("%",3));
            numbers.Children.Add(Label(L.P("from top","위에서"),8));numbers.Children.Add(top);numbers.Children.Add(Label("%",3));
            numbers.Children.Add(Label(L.P("Size width","크기 너비"),14));numbers.Children.Add(wide);numbers.Children.Add(Label("%",3));
            numbers.Children.Add(Label(L.P("height","높이"),8));numbers.Children.Add(tall);numbers.Children.Add(Label("%",3));
            Tip(left,L.P("Left edge, % of the picture from its left","왼쪽 가장자리, 사진 왼쪽에서 몇 %"));
            Tip(top,L.P("Top edge, % of the picture from its top","위쪽 가장자리, 사진 위에서 몇 %"));
            Tip(wide,L.P("Width, % of the picture's width","너비, 사진 너비의 몇 %"));
            Tip(tall,L.P("Height, % of the picture's height","높이, 사진 높이의 몇 %"));
            rows.Children.Add(numbers);
            // Shape: keep it, pick it, or type it.
            var shapes=new WrapPanel {Margin=new Thickness(0,3,0,0)};
            keep.Content=L.P("🔒 Keep this shape","🔒 비율 고정");keep.IsChecked=locked;keep.FontSize=12.5;keep.FontWeight=FontWeights.SemiBold;
            keep.VerticalAlignment=VerticalAlignment.Center;keep.Margin=new Thickness(0,2,10,2);keep.Focusable=false;
            keep.SetResourceReference(Control.ForegroundProperty,"Text");
            keep.ToolTip=L.P("On: drawing and dragging a corner keep the shape and change only the size. The cover editor keeps it too.",
                "켜면 그리거나 모서리를 끌어도 비율은 그대로이고 크기만 바뀌어요. 표지 만들기에서도 그대로 이어져요.");
            AutomationProperties.SetName(keep,L.P("Keep this shape","비율 고정"));
            RoutedEventHandler flip=delegate {if(keep.IsChecked!=locked){LeaveKeys();CommitTyped();SetLocked(keep.IsChecked==true);}};
            keep.Checked+=flip;keep.Unchecked+=flip;
            shapes.Children.Add(keep);
            shapes.Children.Add(Label(L.P("Shape","비율")));
            shapes.Children.Add(across);shapes.Children.Add(Label(":",3));shapes.Children.Add(down);
            Tip(across,L.P("Shape: width (any number, like 16 or 1080)","비율 가로 (16이나 1080처럼 아무 숫자)"));
            Tip(down,L.P("Shape: height (any number, like 9 or 1920)","비율 세로 (9나 1920처럼 아무 숫자)"));
            var gap=new Border {Width=8};shapes.Children.Add(gap);
            foreach(var pair in new[]{new[]{9,16},new[]{3,4},new[]{1,1},new[]{4,3},new[]{16,9}}) {
                int a=pair[0],b=pair[1];
                shapes.Children.Add(Small(a+":"+b,L.P("Makes the box "+a+":"+b+".","네모를 "+a+":"+b+" 모양으로 만들어요."),()=>SetShape(a/(double)b)));
            }
            rows.Children.Add(shapes);
            var actions=new WrapPanel {Margin=new Thickness(0,5,0,0)};
            var make=Small(L.P("Make a cover from this box","이 네모로 표지 만들기"),L.P("Opens Choose a scene; the cover starts from this box.","장면 고르기가 열리고, 표지가 이 네모로 시작해요."),
                ()=>{if(area!=null && MakeCover!=null)MakeCover();},true);
            actions.Children.Add(make);
            var clear=Small(L.P("Clear","지우기"),L.P("Takes the box away. Make cover then starts as before.","네모를 없애요. 표지 만들기는 예전처럼 시작해요."),Clear);
            actions.Children.Add(clear);
            actions.Children.Add(Small(L.P("Done drawing (Esc)","다 그렸어요 (Esc)"),L.P("Stops drawing. The box stays and Make cover starts from it.","그리기를 끝내요. 네모는 남아서 표지 만들기가 이 네모로 시작해요."),StopEditing));
            rows.Children.Add(actions);
            Changed+=delegate {make.IsEnabled=clear.IsEnabled=area!=null;};
            make.IsEnabled=clear.IsEnabled=false;
            bar.Child=rows;
            Dress(bar);
            bar.MaxWidth=720;
            Children.Add(bar);
            // Numbers apply with Enter (the keys then go back to the view) or once the keyboard leaves the group,
            // so the boxes can be filled one after another, even when there is no box yet.
            foreach(var box in new[]{left,top,wide,tall}) {
                var self=box;
                self.TextChanged+=delegate {if(self.IsKeyboardFocusWithin)numbersTyped=self;};
                self.KeyDown+=(s,e)=>{if(e.Key==Key.Enter){LeaveKeys();CommitTyped();e.Handled=true;}};
            }
            numbers.IsKeyboardFocusWithinChanged+=(s,e)=>{if(!(bool)e.NewValue && numbersTyped!=null){var c=numbersTyped;numbersTyped=null;ApplyNumbers(c);}};
            foreach(var box in new[]{across,down}) {
                var self=box;
                self.TextChanged+=delegate {if(self.IsKeyboardFocusWithin)shapeTyped=true;};
                self.KeyDown+=(s,e)=>{if(e.Key==Key.Enter){LeaveKeys();CommitTyped();e.Handled=true;}};
            }
            foreach(var box in new[]{across,down})box.LostKeyboardFocus+=delegate {
                if(across.IsKeyboardFocusWithin || down.IsKeyboardFocusWithin || !shapeTyped)return;
                shapeTyped=false;ApplyShapeNumbers();
            };
        }
        void Forget() {numbersTyped=null;shapeTyped=false;}
        void CommitTyped() {
            if(numbersTyped!=null){var c=numbersTyped;numbersTyped=null;ApplyNumbers(c);}
            if(shapeTyped){shapeTyped=false;ApplyShapeNumbers();}
        }
        void LeaveKeys() {if(IsKeyboardFocusWithin && ReturnKeys!=null)ReturnKeys();}
        static void Tip(TextBox box,string name) {box.ToolTip=name;AutomationProperties.SetName(box,name);}
        static bool Read(TextBox box,out double value) {
            string text=box.Text.Trim().TrimEnd('%').Trim();
            return Double.TryParse(text,NumberStyles.Float,CultureInfo.CurrentCulture,out value) ||
                Double.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out value);
        }
        void ApplyNumbers(TextBox changed) {
            double x,y,w,h;
            var now=area!=null?area.Fraction:new Rect(0.25,0.25,0.5,0.5);
            bool gotX=Read(left,out x),gotY=Read(top,out y),gotW=Read(wide,out w),gotH=Read(tall,out h);
            // Nothing readable typed: nothing changes (no box appears from empty or wrong text).
            if(!gotX && !gotY && !gotW && !gotH){Refresh();return;}
            if(!gotX)x=now.X*100;
            if(!gotY)y=now.Y*100;
            if(!gotW)w=now.Width*100;
            if(!gotH)h=now.Height*100;
            var shown=view();
            if(locked && shape>0 && shown.HasValue && shown.Value.Upright>0) {
                double want=shape/shown.Value.Upright; // width over height in fractions
                if(changed==tall)w=h*want;else if(changed==wide)h=w/want;
                // Too big for the picture: smaller, still the same shape.
                double over=Math.Max(w/100,h/100);
                if(over>1){w/=over;h/=over;}
            }
            Place(x,y,w,h);
        }
        void ApplyShapeNumbers() {
            double a,b;
            if(!Read(across,out a) || !Read(down,out b) || !(a>0) || !(b>0) || Double.IsInfinity(a/b) || Double.IsNaN(a/b)){Refresh();return;}
            SetShape(a/b);
        }
        void BuildChip() {
            var line=new WrapPanel();
            var text=UI.Text(L.P("▭ Make cover starts from this box","▭ 표지 만들기가 이 네모로 시작해요"),12.5);text.Margin=new Thickness(0,0,10,0);
            line.Children.Add(text);
            line.Children.Add(Small(L.P("Change","고치기"),L.P("Draw or change the box.","네모를 다시 그리거나 고쳐요."),StartEditing));
            line.Children.Add(Small(L.P("Clear","지우기"),L.P("Takes the box away. Make cover then starts as before.","네모를 없애요. 표지 만들기는 예전처럼 시작해요."),Clear));
            chip.Child=line;
            Dress(chip);chip.Padding=new Thickness(10,4,6,4);
            chip.Opacity=0.94;
            Children.Add(chip);
        }

        // Puts the box, the dim outside and the bar where they belong now.
        internal void Refresh() {
            bar.Visibility=available && editing?Visibility.Visible:Visibility.Collapsed;
            chip.Visibility=available && !editing && area!=null?Visibility.Visible:Visibility.Collapsed;
            if(ActualWidth>0)bar.MaxWidth=Math.Max(240,Math.Min(720,ActualWidth-24));
            if(keep.IsChecked!=locked)keep.IsChecked=locked;
            AreaView v;Point middle;Size size;double tilt;
            bool shows=Where(out v,out middle,out size,out tilt) && available;
            if(!shows){shade.Data=under.Data=line.Data=grips.Data=null;ShowNumbers(null);PlaceCards(null,v);return;}
            var box=new RectangleGeometry(new Rect(middle.X-size.Width/2,middle.Y-size.Height/2,size.Width,size.Height));
            if(Math.Abs(tilt)>0.001)box.Transform=new RotateTransform(tilt,middle.X,middle.Y);
            box.Freeze();
            PlaceCards(box.Bounds,v);
            // Dimmed outside only while drawing, so the picture is easy to watch otherwise.
            shade.Data=editing?new CombinedGeometry(GeometryCombineMode.Exclude,new RectangleGeometry(v.Room),box):null;
            under.Data=box;line.Data=box;
            line.StrokeDashArray=editing?null:new DoubleCollection {4,3};
            if(editing) {
                var corners=new GeometryGroup();
                foreach(var sx in new[]{-1,1})foreach(var sy in new[]{-1,1}) {
                    var at=middle+Turned(new Vector(sx*size.Width/2,sy*size.Height/2),tilt);
                    corners.Children.Add(new RectangleGeometry(new Rect(at.X-5,at.Y-5,10,10)));
                }
                grips.Data=corners;
            } else grips.Data=null;
            ShowNumbers(area);
        }
        // The bar and the note go above or below the box, wherever there is more room, so they cover as little of it as
        // they can. They don't jump while the box is being dragged.
        void PlaceCards(Rect? box,AreaView v) {
            if(grip!=Grip.None)return;
            bool low=false;
            if(box.HasValue && v.Room.Height>0) {
                // Whichever side has more free room above or below the box.
                low=box.Value.Top-v.Room.Top<v.Room.Bottom-box.Value.Bottom;
            }
            double bottom=Math.Max(0,ActualHeight-v.Room.Bottom)+10;
            foreach(var card in new[]{bar,chip}) {
                card.VerticalAlignment=low?VerticalAlignment.Bottom:VerticalAlignment.Top;
                card.Margin=low?new Thickness(10,0,10,bottom):new Thickness(10,10,10,0);
            }
        }
        void ShowNumbers(DrawnArea a) {
            Action<TextBox,double> put=(box,value)=>{if(!box.IsKeyboardFocusWithin)box.Text=value.ToString("0.#",CultureInfo.InvariantCulture);};
            if(a==null) {
                foreach(var box in new[]{left,top,wide,tall})if(!box.IsKeyboardFocusWithin)box.Text="";
            } else {
                put(left,a.Fraction.X*100);put(top,a.Fraction.Y*100);put(wide,a.Fraction.Width*100);put(tall,a.Fraction.Height*100);
            }
            double now=shape;
            if(!locked || now<=0) {
                var shown=view();
                now=a!=null && shown.HasValue?a.Aspect(shown.Value.Upright):shape;
            }
            if(across.IsKeyboardFocusWithin || down.IsKeyboardFocusWithin)return;
            if(now>0) {
                string name=Covers.Name(now);int at=name.IndexOf(':');
                if(at>0){across.Text=name.Substring(0,at);down.Text=name.Substring(at+1);return;}
            }
            across.Text=down.Text="";
        }
    }
}
