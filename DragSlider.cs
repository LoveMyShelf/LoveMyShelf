using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace LoveMyShelf {
    internal sealed class DragSlider : Slider {
        bool dragging;
        public event Action PointerDragStarted, PointerDragCompleted;

        public DragSlider() {
            Style=(Style)Application.Current.Resources[typeof(Slider)];
        }

        internal double ValueAt(Point point) {
            var track=GetTemplateChild("PART_Track") as Track;
            if(track==null || Minimum==Maximum)return Value;
            var position=TranslatePoint(point,track);
            double length=Orientation==Orientation.Horizontal?track.ActualWidth:track.ActualHeight;
            double thumb=Orientation==Orientation.Horizontal?track.Thumb.ActualWidth:track.Thumb.ActualHeight;
            if(length<=thumb)return Value;
            double axis=Orientation==Orientation.Horizontal?position.X:length-position.Y;
            double ratio=Math.Max(0,Math.Min(1,(axis-thumb/2)/(length-thumb)));
            if(IsDirectionReversed)ratio=1-ratio;
            return Minimum+(Maximum-Minimum)*ratio;
        }

        void MoveTo(Point point) {
            double value=ValueAt(point);
            if(IsSnapToTickEnabled && TickFrequency>0) {
                double lower=Minimum+Math.Floor((value-Minimum)/TickFrequency)*TickFrequency;
                double upper=Math.Min(Maximum,lower+TickFrequency);
                value=value-lower<upper-value?lower:upper;
            }
            SetCurrentValue(ValueProperty,value);
        }

        protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e) {
            if(!IsMoveToPointEnabled || !IsEnabled){base.OnPreviewMouseLeftButtonDown(e);return;}
            if(BeginPointerDrag(e.GetPosition(this)))e.Handled=true;
        }

        internal bool BeginPointerDrag(Point point) {
            Focus();
            if(!CaptureMouse())return false;
            dragging=true;
            if(PointerDragStarted!=null)PointerDragStarted();
            MoveTo(point);
            return true;
        }

        protected override void OnMouseMove(MouseEventArgs e) {
            if(e.LeftButton==MouseButtonState.Pressed)ContinuePointerDrag(e.GetPosition(this));
            base.OnMouseMove(e);
        }

        internal void ContinuePointerDrag(Point point) {
            if(dragging && IsMouseCaptured)MoveTo(point);
        }

        protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e) {
            if(!dragging){base.OnPreviewMouseLeftButtonUp(e);return;}
            EndPointerDrag(e.GetPosition(this));
            e.Handled=true;
        }

        internal void EndPointerDrag(Point point) {
            ContinuePointerDrag(point);
            Finish();
            ReleaseMouseCapture();
        }

        protected override void OnLostMouseCapture(MouseEventArgs e) {
            base.OnLostMouseCapture(e);
            if(!IsMouseCaptured)Finish();
        }

        void Finish() {
            if(!dragging)return;
            dragging=false;
            if(PointerDragCompleted!=null)PointerDragCompleted();
        }
    }
}
