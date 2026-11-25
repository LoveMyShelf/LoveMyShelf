using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace LoveMyShelf {
    // How a chosen part looks, in the large view's playback bar and on the GIF cover window's scene strip.
    // One place, so both stay alike: thin lines, rounded ends, the window's own gray, and the highlight
    // color only where the eye should go (the handle in use, the place now playing).
    internal static class PartLook {
        // The rails (the empty rows over and under the strip's pictures) hold every mark, so nothing lies on a picture.
        // The upper one is a range slider like the program's own sliders; the lower one shows where the preview plays.
        internal const double TopRail=14,BottomRail=12;
        const double Track=3,Knob=10,ChipWidth=6,ChipHeight=9,Dot=7;

        static T Colored<T>(T element,DependencyProperty property,string key) where T:FrameworkElement {
            element.SetResourceReference(property,key);return element;
        }
        // Lines as thick as about 1.5 units on any screen scale, on whole screen pixels so they stay sharp:
        // two pixels at 150 %, three at 225 %.
        internal static double Line(double pixel) {return Math.Max(1,Math.Round(1.5/pixel))*pixel;}

        // The playback bar. The marks' canvas has no height and sits on the middle of the rail, so 0 is the rail.
        // A pale band around the rail from the start to the end, closed by a short rounded bar at each mark, so
        // the band itself ends square under the bars
        // (one bar alone while only one end is set). The played part of the rail still shows through the band.
        internal static void OnBar(Canvas marks,double? from,double? to) {
            if(from.HasValue && to.HasValue && to.Value>from.Value) {
                var band=Colored(new Border {Width=to.Value-from.Value,Height=7,CornerRadius=new CornerRadius(1),Opacity=0.24,SnapsToDevicePixels=true},
                    Border.BackgroundProperty,"Accent");
                Canvas.SetLeft(band,from.Value);Canvas.SetTop(band,-3.5);marks.Children.Add(band);
            }
            foreach(var at in new[]{from,to}) {
                if(!at.HasValue)continue;
                var end=Colored(new Border {Width=2,Height=11,CornerRadius=new CornerRadius(1),SnapsToDevicePixels=true},Border.BackgroundProperty,"Accent");
                Canvas.SetLeft(end,at.Value-1);Canvas.SetTop(end,-5.5);marks.Children.Add(end);
            }
        }

        // The scene strip's chosen part, drawn as a range slider in the upper rail: a thin gray track as long as
        // the clip, the chosen part of it in the highlight color, and (see Knob) a round knob at each end. From each
        // end a fine line in the text's gray runs down beside the pictures, just outside the first and the last
        // chosen scene, so no scene that goes into the GIF is covered; a hairline in the window's color beside it
        // keeps it readable on dark scenes.
        internal static void Range(Canvas strip,double left,double right,double trackLeft,double trackRight,double pictureHeight,double pixel) {
            double line=Line(pixel),middle=TopRail/2;
            if(trackRight>trackLeft) {
                var track=Colored(new Border {Width=trackRight-trackLeft,Height=Track,CornerRadius=new CornerRadius(Track/2),IsHitTestVisible=false,SnapsToDevicePixels=true},
                    Border.BackgroundProperty,"StrongLine");
                Canvas.SetLeft(track,trackLeft);Canvas.SetTop(track,middle-Track/2);strip.Children.Add(track);
            }
            var chosen=Colored(new Border {Width=Math.Max(0,right-left)+line,Height=Track,CornerRadius=new CornerRadius(Track/2),IsHitTestVisible=false,SnapsToDevicePixels=true},
                Border.BackgroundProperty,"Accent");
            Canvas.SetLeft(chosen,left-line/2);Canvas.SetTop(chosen,middle-Track/2);strip.Children.Add(chosen);
            foreach(bool start in new[]{true,false}) {
                double x=start?left-line:right;
                var halo=Colored(new Border {Width=pixel,Height=pictureHeight,IsHitTestVisible=false,SnapsToDevicePixels=true},Border.BackgroundProperty,"Paper");
                Canvas.SetLeft(halo,start?x-pixel:x+line);Canvas.SetTop(halo,TopRail);strip.Children.Add(halo);
                var edge=Colored(new Border {Width=line,Height=pictureHeight+TopRail-middle,IsHitTestVisible=false,SnapsToDevicePixels=true,Opacity=0.85},
                    Border.BackgroundProperty,"Text");
                Canvas.SetLeft(edge,x);Canvas.SetTop(edge,middle);strip.Children.Add(edge);
            }
        }
        // A knob of the range slider, on the track over one end of the chosen part. Both wear a ring in the
        // highlight color; the one the arrow keys move is filled with it.
        internal static void Handle(Canvas strip,double x,bool active) {
            var knob=new Ellipse {Width=Knob,Height=Knob,StrokeThickness=1.5,IsHitTestVisible=false};
            knob.SetResourceReference(Shape.FillProperty,active?"Accent":"Panel");knob.SetResourceReference(Shape.StrokeProperty,"Accent");
            Canvas.SetLeft(knob,x-Knob/2);Canvas.SetTop(knob,(TopRail-Knob)/2);strip.Children.Add(knob);
        }
        // Where the JPG cover's scene is: a tiny upright card in the cover's own 9:16 shape, standing on the track
        // like a bookmark, with a rim in the window's color so the track doesn't run into it.
        internal static FrameworkElement CoverMark(bool onTrack=true) {
            double rim=onTrack?1:0;
            var card=new Border {Width=ChipWidth+2*rim,Height=ChipHeight+2*rim,CornerRadius=new CornerRadius(1.5+rim),BorderThickness=new Thickness(rim),SnapsToDevicePixels=true};
            card.SetResourceReference(Border.BackgroundProperty,"Text");card.SetResourceReference(Border.BorderBrushProperty,"Paper");
            return card;
        }
        internal static void PlaceCoverMark(FrameworkElement card,double x) {
            Canvas.SetLeft(card,x-card.Width/2);Canvas.SetTop(card,(TopRail-card.Height)/2);
        }
        // Where the preview is playing: a small round dot in the lower rail.
        internal static FrameworkElement PlayMark() {
            var dot=new Ellipse {Width=Dot,Height=Dot};
            dot.SetResourceReference(Shape.FillProperty,"Accent");
            return dot;
        }
        internal static void PlacePlayMark(FrameworkElement dot,double x,double railTop) {
            Canvas.SetLeft(dot,x-Dot/2);Canvas.SetTop(dot,railTop+(BottomRail-Dot)/2);
        }
    }
}
