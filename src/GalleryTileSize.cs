using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace LoveMyShelf {
    // [Cover size] at the bottom: one click opens the sizes to choose from (the one in use is marked) and a box
    // for any size, instead of stepping through them one click at a time.
    // 2026-11-27 user: "표지 크기 한번 누르면 이렇게 선택할 수 있는 걸 쫙 뜨게 하자"
    public partial class GalleryWindow {
        const int SmallestTile=60,LargestTile=480;
        Popup sizePopup;
        StackPanel sizeList;
        DateTime sizeClosedAt;

        void ToggleSizeList(Button anchor) {
            if(sizePopup==null) {
                sizeList=new StackPanel {Margin=new Thickness(4)};
                var border=new Border {Child=sizeList,MinWidth=220,CornerRadius=new CornerRadius(8),BorderThickness=new Thickness(1)};
                UI.Use(border,Border.BackgroundProperty,"Panel");UI.Use(border,Border.BorderBrushProperty,"Line");
                // Opens upward like [Sort], since the button is at the bottom of the window.
                sizePopup=new Popup {PlacementTarget=anchor,Placement=PlacementMode.Top,StaysOpen=false,AllowsTransparency=true,Child=border};
                sizePopup.Closed+=delegate {sizeClosedAt=DateTime.UtcNow;};
                sizePopup.KeyDown+=(s,e)=>{if(e.Key==Key.Escape){sizePopup.IsOpen=false;e.Handled=true;}};
            }
            // A click on the button while the list is open closes it (the click outside already did).
            if(sizePopup.IsOpen || (DateTime.UtcNow-sizeClosedAt).TotalMilliseconds<250){sizePopup.IsOpen=false;return;}
            FillSizeList();
            sizePopup.IsOpen=true;
        }
        void FillSizeList() {
            sizeList.Children.Clear();
            var title=UI.Text(L.P("Cover size","표지 크기"),12.5,true);title.Margin=new Thickness(8,4,8,4);
            sizeList.Children.Add(title);
            var sizes=new[]{Tuple.Create(88,"Small tiles"),Tuple.Create(120,"Medium tiles"),Tuple.Create(164,"Large tiles"),Tuple.Create(220,"Extra large tiles")};
            foreach(var size in sizes) {
                int value=size.Item1;bool now=density==value;
                var row=new DockPanel {LastChildFill=true};
                var mark=UI.Text(now?"✓":"",13);mark.Width=18;DockPanel.SetDock(mark,Dock.Left);
                var px=UI.Text(value+" px",12,true);px.Margin=new Thickness(16,0,0,0);DockPanel.SetDock(px,Dock.Right);
                var name=UI.Text(L.T(size.Item2),13.5);
                if(now){UI.Use(name,TextBlock.ForegroundProperty,"Accent");UI.Use(mark,TextBlock.ForegroundProperty,"Accent");}
                row.Children.Add(mark);row.Children.Add(px);row.Children.Add(name);
                var item=new Button {Content=row,HorizontalContentAlignment=HorizontalAlignment.Stretch,Padding=new Thickness(8,6,8,6),
                    Background=System.Windows.Media.Brushes.Transparent,BorderThickness=new Thickness(0),Cursor=Cursors.Hand};
                item.Click+=delegate {SetTileSize(value);};
                sizeList.Children.Add(item);
            }
            // Any size: typed in, or stepped with − and +.
            var own=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(8,8,8,4)};
            var label=UI.Text(L.P("Your own:","직접:"),13);label.VerticalAlignment=VerticalAlignment.Center;label.Margin=new Thickness(0,0,6,0);
            var box=new TextBox {Text=density.ToString(CultureInfo.InvariantCulture),Width=56,VerticalContentAlignment=VerticalAlignment.Center,MinHeight=28};
            AutomationPropertiesName(box,L.P("Cover size in pixels","표지 크기(px)"));
            Action<int> step=d=>{int v;if(!Int32.TryParse(box.Text.Trim(),out v))v=density;SetTileSize(v+d);};
            var minus=UI.Btn("−",()=>step(-10));minus.Margin=new Thickness(0,0,4,0);minus.MinWidth=28;
            var plus=UI.Btn("+",()=>step(10));plus.Margin=new Thickness(4,0,0,0);plus.MinWidth=28;
            var unit=UI.Text("px",13,true);unit.VerticalAlignment=VerticalAlignment.Center;unit.Margin=new Thickness(6,0,8,0);
            Action apply=()=>{int v;if(Int32.TryParse(box.Text.Trim(),out v))SetTileSize(v);else box.Text=density.ToString(CultureInfo.InvariantCulture);};
            var ok=UI.Btn(L.P("Apply","적용"),apply);
            box.KeyDown+=(s,e)=>{if(e.Key==Key.Enter){apply();e.Handled=true;}};
            own.Children.Add(label);own.Children.Add(minus);own.Children.Add(box);own.Children.Add(plus);own.Children.Add(unit);own.Children.Add(ok);
            sizeList.Children.Add(own);
            var range=UI.Text(L.PF("From {0} to {1} px. The width a cover aims for; the cells fill the row.","{0}~{1}px까지 돼요. 칸 너비의 기준이고, 줄에 맞춰 조금씩 늘어나요.",SmallestTile,LargestTile),11.5,true);
            range.TextWrapping=TextWrapping.Wrap;range.MaxWidth=260;range.Margin=new Thickness(8,0,8,6);
            sizeList.Children.Add(range);
        }
        static void AutomationPropertiesName(UIElement element,string name) {System.Windows.Automation.AutomationProperties.SetName(element,name);}
        // The list stays open, so the new size can be seen and changed again right away.
        void SetTileSize(int value) {
            value=Math.Max(SmallestTile,Math.Min(LargestTile,value));
            if(value!=density){density=value;LayoutRows();}
            if(sizePopup!=null && sizePopup.IsOpen)FillSizeList();
        }
    }
}
