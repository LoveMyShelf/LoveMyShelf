using System;
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
        internal ViewFraming Framing(Size upright) {
            var f=Fraction;
            var center=new Vector((f.X+f.Width/2-0.5)*upright.Width,(f.Y+f.Height/2-0.5)*upright.Height);
            var rotate=Matrix.Identity;rotate.Rotate(Turn);center=rotate.Transform(center);
            return new ViewFraming {Turn=Turn,PictureWidth=upright.Width,PictureHeight=upright.Height,
                ShownWidth=f.Width*upright.Width,ShownHeight=f.Height*upright.Height,PanX=-center.X,PanY=-center.Y};
        }
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

    // Draws the box over the large view and provides the controls for the playback bar. The large view hands its left-button presses here
    // while drawing is on (Press, Drag, Release); a double-click, a right-click and the playback bar keep their jobs.
    internal sealed class CoverAreaLayer : Grid {
        readonly Func<AreaView?> view;
        readonly Canvas canvas=new Canvas {IsHitTestVisible=false};
        readonly Path shade=new Path {Fill=new SolidColorBrush(Color.FromArgb(0x78,0,0,0))};
        // "Enlarge the box": everything outside the box is black, so only the box plays.
        readonly Path mask=new Path {Fill=Brushes.Black};
        readonly Path under=new Path {Stroke=new SolidColorBrush(Color.FromArgb(0xB0,0,0,0)),StrokeThickness=4};
        readonly Path line=new Path {Stroke=Brushes.White,StrokeThickness=2};
        readonly Path grips=new Path {Fill=Brushes.White,Stroke=new SolidColorBrush(Color.FromArgb(0xC0,0,0,0)),StrokeThickness=1};
        internal readonly Border Menu=new Border();
        Button edit,zoomBox,make,clear;
        StackPanel numbers,shapes;
        bool enlarged;
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
        internal Action MakeCover,ToggleZoom,DeleteArea;
        internal bool Typing {get {return left.IsKeyboardFocusWithin || top.IsKeyboardFocusWithin || wide.IsKeyboardFocusWithin || tall.IsKeyboardFocusWithin || across.IsKeyboardFocusWithin || down.IsKeyboardFocusWithin;}}
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
            canvas.Children.Add(mask);canvas.Children.Add(shade);canvas.Children.Add(under);canvas.Children.Add(line);canvas.Children.Add(grips);
            Children.Add(canvas);
            var prefs=CropSurface.Prefs;
            locked=prefs!=null && prefs.CropLock;
            if(locked && lastShape>0)shape=lastShape;
            BuildMenu();
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
        internal void StartEditing() {if(!available || enlarged)return;editing=true;Refresh();Told();}
        internal void StopEditing() {if(grip!=Grip.None)Release();Forget();LeaveKeys();editing=false;Refresh();Told();}
        // Esc while typing a number: the typed text is dropped and the keys go back to the view.
        internal void CancelTyping() {Forget();LeaveKeys();Refresh();}
        internal void ApplyTyping() {CommitTyped();LeaveKeys();Refresh();}
        internal void SetEnlarged(bool on) {
            if(on)StopEditing();
            enlarged=on;Refresh();
        }
        internal void Restore(DrawnArea saved) {area=saved.Copy();Refresh();Raise();}
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
        internal static void ToScreen(AreaView v,DrawnArea a,out Point middle,out Size size,out double tilt) {
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
                    grip=Grip.Corner;Told();return true;
                }
                if(Math.Abs(local.X)<=size.Width/2 && Math.Abs(local.Y)<=size.Height/2){grip=Grip.Move;Told();return true;}
            }
            grip=Grip.New;Told();return true;
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
            Told();
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

        // ----- The controls opened from the playback bar -----
        static TextBox Number() {
            return new TextBox {Width=48,MinHeight=28,VerticalContentAlignment=VerticalAlignment.Center,
                HorizontalContentAlignment=HorizontalAlignment.Center,FontSize=12.5};
        }
        static TextBox Ratio() {var box=Number();box.Width=43;return box;}
        Button Small(string text,Action act,bool primary=false) {
            var b=UI.Btn(text,()=>{ApplyTyping();act();},primary);
            b.Margin=new Thickness(0,2,4,2);b.Padding=new Thickness(8,1,8,2);b.MinHeight=28;b.MinWidth=0;b.FontSize=12.5;
            b.HorizontalAlignment=HorizontalAlignment.Left;return b;
        }
        static TextBlock Label(string text,double margin=0) {
            var t=UI.Text(text,12.5,true);t.Margin=new Thickness(margin,0,5,0);return t;
        }
        static StackPanel Row(params UIElement[] children) {
            var row=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(0,1,0,1)};
            foreach(var child in children)row.Children.Add(child);
            return row;
        }
        void BuildMenu() {
            var rows=new StackPanel();
            edit=Small("",()=>{if(editing)StopEditing();else StartEditing();});
            rows.Children.Add(edit);
            shapes=new StackPanel {Margin=new Thickness(0,3,0,0)};
            keep.Content=L.P("🔒 Keep this shape","🔒 비율 고정");
            keep.VerticalAlignment=VerticalAlignment.Center;
            keep.SetResourceReference(Control.ForegroundProperty,"Text");
            RoutedEventHandler flip=delegate {if(keep.IsChecked!=locked){ApplyTyping();SetLocked(keep.IsChecked==true);}};
            keep.Checked+=flip;keep.Unchecked+=flip;
            shapes.Children.Add(keep);
            var presets=Row();
            foreach(var pair in new[]{new[]{9,16},new[]{3,4},new[]{1,1},new[]{4,3},new[]{16,9}}) {
                int a=pair[0],b=pair[1];
                presets.Children.Add(Small(a+":"+b,()=>SetShape(a/(double)b)));
            }
            shapes.Children.Add(presets);
            shapes.Children.Add(Row(Label(L.P("Width","가로")),across,Label(":",5),Label(L.P("Height","세로")),down));
            Tip(across,L.P("Aspect width","비율 가로"));Tip(down,L.P("Aspect height","비율 세로"));
            rows.Children.Add(shapes);

            numbers=new StackPanel {Margin=new Thickness(0,4,0,2)};
            numbers.Children.Add(Row(Label(L.P("From left","왼쪽에서")),left,Label("%",3),
                Label(L.P("From top","위에서"),7),top,Label("%",3)));
            numbers.Children.Add(Row(Label(L.P("Width","너비")),wide,Label("%",3),
                Label(L.P("Height","높이"),7),tall,Label("%",3)));
            Tip(left,L.P("From left, %","왼쪽에서 몇 %"));Tip(top,L.P("From top, %","위에서 몇 %"));
            Tip(wide,L.P("Width, %","너비 몇 %"));Tip(tall,L.P("Height, %","높이 몇 %"));
            rows.Children.Add(numbers);

            zoomBox=Small("",()=>{if(ToggleZoom!=null)ToggleZoom();});
            make=Small(L.P("Make a cover from this box","이 네모로 표지 만들기"),()=>{if(MakeCover!=null)MakeCover();},true);
            clear=Small(L.P("Clear (Esc)","지우기 (Esc)"),()=>{if(DeleteArea!=null)DeleteArea();});
            rows.Children.Add(zoomBox);rows.Children.Add(make);rows.Children.Add(clear);
            var hint=UI.Text(L.P("Drag to draw · drag inside to move · drag a corner to resize",
                "끌어서 그리기 · 안쪽을 끌면 옮기기 · 모서리를 끌면 크기"),12,true);
            hint.TextWrapping=TextWrapping.Wrap;hint.Margin=new Thickness(0,6,0,0);
            rows.Children.Add(hint);
            Menu.Child=rows;Menu.Width=365;Menu.CornerRadius=new CornerRadius(8);
            Menu.Padding=new Thickness(10,7,10,8);Menu.BorderThickness=new Thickness(1);
            Menu.SetResourceReference(Border.BackgroundProperty,"Paper");Menu.SetResourceReference(Border.BorderBrushProperty,"Line");
            Menu.SetResourceReference(TextElement.FontFamilyProperty,"AppFont");

            // Apply when the keyboard leaves a group, so its numbers can be filled in together.
            foreach(var box in new[]{left,top,wide,tall}) {
                var self=box;
                self.TextChanged+=delegate {if(self.IsKeyboardFocusWithin)numbersTyped=self;};
            }
            numbers.IsKeyboardFocusWithinChanged+=(s,e)=>{if(!(bool)e.NewValue && numbersTyped!=null)CommitTyped();};
            foreach(var box in new[]{across,down})box.TextChanged+=delegate {if(box.IsKeyboardFocusWithin)shapeTyped=true;};
            foreach(var box in new[]{across,down})box.LostKeyboardFocus+=delegate {
                if(!across.IsKeyboardFocusWithin && !down.IsKeyboardFocusWithin && shapeTyped)CommitTyped();
            };
        }
        void Forget() {numbersTyped=null;shapeTyped=false;}
        void CommitTyped() {
            if(numbersTyped!=null){var c=numbersTyped;numbersTyped=null;ApplyNumbers(c);}
            if(shapeTyped){shapeTyped=false;ApplyShapeNumbers();}
        }
        void LeaveKeys() {if(Menu.IsKeyboardFocusWithin && ReturnKeys!=null)ReturnKeys();}
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
        internal Rect ScreenBox {
            get {
                AreaView v;Point middle;Size size;double tilt;
                if(!Where(out v,out middle,out size,out tilt) || enlarged)return Rect.Empty;
                return Box(middle,size,tilt).Bounds;
            }
        }
        static RectangleGeometry Box(Point middle,Size size,double tilt) {
            var box=new RectangleGeometry(new Rect(middle.X-size.Width/2,middle.Y-size.Height/2,size.Width,size.Height));
            if(Math.Abs(tilt)>0.001)box.Transform=new RotateTransform(tilt,middle.X,middle.Y);
            box.Freeze();return box;
        }
        internal void Refresh() {
            edit.Content=editing?L.P("Done drawing (Enter)","다 그렸어요 (Enter)"):
                area==null?L.P("▭ Start drawing","▭ 그리기 시작"):L.P("▭ Edit box","▭ 네모 고치기");
            zoomBox.Content=enlarged?L.P("⛶ Back to previous view","⛶ 원래 보기"):L.P("⛶ Enlarge the box","⛶ 네모만 크게");
            edit.IsEnabled=numbers.IsEnabled=shapes.IsEnabled=!enlarged;
            zoomBox.IsEnabled=make.IsEnabled=clear.IsEnabled=area!=null;
            if(keep.IsChecked!=locked)keep.IsChecked=locked;
            ShowNumbers(area);
            AreaView v;Point middle;Size size;double tilt;
            bool shows=Where(out v,out middle,out size,out tilt) && available;
            mask.Data=shows && enlarged?new CombinedGeometry(GeometryCombineMode.Exclude,
                new RectangleGeometry(new Rect(-1,-1,Math.Max(ActualWidth,v.Room.Right)+2,Math.Max(ActualHeight,v.Room.Bottom)+2)),Box(middle,size,tilt)):null;
            if(!shows || enlarged) {
                shade.Data=under.Data=line.Data=grips.Data=null;return;
            }
            var box=Box(middle,size,tilt);
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
        }
        void ShowNumbers(DrawnArea a) {
            Action<TextBox,double> put=(box,value)=>{if(numbersTyped==null && !box.IsKeyboardFocusWithin)box.Text=value.ToString("0.#",CultureInfo.InvariantCulture);};
            if(a==null) {
                foreach(var box in new[]{left,top,wide,tall})if(numbersTyped==null && !box.IsKeyboardFocusWithin)box.Text="";
            } else {
                put(left,a.Fraction.X*100);put(top,a.Fraction.Y*100);put(wide,a.Fraction.Width*100);put(tall,a.Fraction.Height*100);
            }
            double now=shape;
            if(!locked || now<=0) {
                var shown=view();
                now=a!=null && shown.HasValue?a.Aspect(shown.Value.Upright):shape;
            }
            if(shapeTyped || across.IsKeyboardFocusWithin || down.IsKeyboardFocusWithin)return;
            if(now>0) {
                string name=Covers.Name(now);int at=name.IndexOf(':');
                if(at>0){across.Text=name.Substring(0,at);down.Text=name.Substring(at+1);return;}
            }
            across.Text=down.Text="";
        }
    }
}
