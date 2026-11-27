using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace LoveMyShelf {
    // One adjustment line: name | slider | number box. Click the line once, then hold ← / → and the value runs,
    // faster the longer the key is held (2026-11-27 user: "click once and hold left/right, the buttons aren't needed").
    // The mouse does the same: drag the slider, or roll the wheel over the chosen line. Double-click the name for 0.
    // ↑ / ↓ go to the line above or below; Esc ends. The picture follows about 16 times a second.
    internal sealed class AdjustRow : Border {
        readonly int min, max;
        readonly Action<int> change;
        readonly TextBox box;
        readonly Slider slider;
        readonly DispatcherTimer apply = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        readonly DispatcherTimer hold = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        int current, applied;
        bool syncing, edited;
        Key heldKey = Key.None;
        int heldWay;
        DateTime heldSince, lastWheel;

        internal AdjustRow(string name, int min, int max, Action<int> change) {
            this.min = min; this.max = max; this.change = change;
            Focusable = true;
            FocusVisualStyle = null;
            Padding = new Thickness(4, 2, 4, 2);
            Margin = new Thickness(0, 1, 0, 1);
            CornerRadius = new CornerRadius(5);
            BorderThickness = new Thickness(1);
            BorderBrush = Brushes.Transparent;
            Background = Brushes.Transparent;
            AutomationProperties.SetName(this, name);
            ToolTip = L.P("Click the line once, then hold ← or → and it changes quickly. Rolling the wheel over the chosen line works too. Double-click the name for 0.",
                "줄을 한 번 누른 뒤 ←/→를 꾹 누르면 빠르게 바뀌어요. 고른 줄 위에서 휠을 굴려도 돼요. 이름을 두 번 누르면 0이 돼요.");

            var line = new DockPanel { LastChildFill = true };
            var label = UI.Text(name, 12);
            label.Width = 62;
            label.TextWrapping = TextWrapping.Wrap;
            label.VerticalAlignment = VerticalAlignment.Center;
            label.MouseLeftButtonDown += (s, e) => { if (e.ClickCount == 2) { SetNow(0); e.Handled = true; } };
            DockPanel.SetDock(label, Dock.Left);
            line.Children.Add(label);

            box = new TextBox { Width = 52, MinHeight = 26, VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            AutomationProperties.SetName(box, name);
            box.KeyDown += (s, e) => {
                if (e.Key == Key.Enter) { ApplyTyped(); Focus(); e.Handled = true; }
                else if (e.Key == Key.Escape) { edited = false; ShowNumber(); Focus(); e.Handled = true; }
            };
            box.TextChanged += delegate { if (box.IsKeyboardFocusWithin) edited = true; };
            box.LostKeyboardFocus += delegate { if (edited) ApplyTyped(); };
            DockPanel.SetDock(box, Dock.Right);
            line.Children.Add(box);

            slider = new Slider { Minimum = min, Maximum = max, Focusable = false, IsMoveToPointEnabled = true,
                VerticalAlignment = VerticalAlignment.Center, SmallChange = 1, LargeChange = 10 };
            AutomationProperties.SetName(slider, name);
            slider.ValueChanged += (s, e) => { if (!syncing) Set((int)Math.Round(e.NewValue)); };
            slider.AddHandler(System.Windows.Controls.Primitives.Thumb.DragCompletedEvent,
                new System.Windows.Controls.Primitives.DragCompletedEventHandler((s, e) => Flush()));
            slider.PreviewMouseLeftButtonUp += delegate { Flush(); };
            line.Children.Add(slider);
            Child = line;

            // A very narrow column (the GIF window's side at its smallest) drops the slider first, so nothing gets cut.
            SizeChanged += delegate {
                var want = ActualWidth >= 240 ? Visibility.Visible : Visibility.Collapsed;
                if (slider.Visibility != want) slider.Visibility = want;
            };
            // A click anywhere on the line but the number box chooses it (the slider still moves as usual).
            PreviewMouseLeftButtonDown += (s, e) => {
                if (!box.IsMouseOver && !IsKeyboardFocused) Focus();
            };
            IsKeyboardFocusWithinChanged += delegate { Highlight(); };
            LostKeyboardFocus += delegate { StopHold(); };
            apply.Tick += delegate { apply.Stop(); Flush(); };
            hold.Tick += delegate { Held(); };
            ShowNumber();
        }

        internal int Value { get { return current; } }

        // Shows a value set from elsewhere (a key like Q/W/E, auto adjust, reset). It wins over anything still waiting.
        internal void Show(int value) {
            value = Clamp(value);
            // While the person is changing this line (a key held, the slider dragged, or a value still waiting to be
            // applied), an outside Show that only repeats the value last applied is an echo, not a new value: keep going.
            // (2026-11-27: a panel that refreshed every 50 ms made a held key jump back and forth.)
            bool changing = heldKey != Key.None || slider.IsMouseCaptureWithin || apply.IsEnabled;
            if (changing && value == applied) return;
            apply.Stop();
            current = applied = value;
            if (!box.IsKeyboardFocusWithin || !edited) ShowNumber();
            Sync();
        }

        int Clamp(int value) { return Math.Max(min, Math.Min(max, value)); }

        void Set(int value) {
            value = Clamp(value);
            if (value == current) return;
            current = value;
            edited = false;
            ShowNumber();
            Sync();
            if (!apply.IsEnabled) apply.Start();
        }

        void SetNow(int value) { Set(value); Flush(); }

        void Flush() {
            apply.Stop();
            if (current == applied) return;
            applied = current;
            change(current);
        }

        void ShowNumber() { box.Text = current.ToString(CultureInfo.CurrentCulture); }

        void Sync() {
            if (Math.Abs(slider.Value - current) < 0.5) return;
            syncing = true;
            slider.Value = current;
            syncing = false;
        }

        void ApplyTyped() {
            edited = false;
            double typed;
            if (Double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out typed) ||
                Double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out typed)) {
                if (!Double.IsNaN(typed) && !Double.IsInfinity(typed)) { SetNow((int)Math.Round(Math.Max(-100000, Math.Min(100000, typed)))); ShowNumber(); return; }
            }
            ShowNumber();
        }

        void Highlight() {
            bool on = IsKeyboardFocusWithin;
            if (on) { SetResourceReference(BackgroundProperty, "Raised"); SetResourceReference(BorderBrushProperty, "Accent"); }
            else { Background = Brushes.Transparent; BorderBrush = Brushes.Transparent; }
            var rows = Parent as AdjustRows;
            if (rows != null) rows.ShowHint();
        }

        protected override void OnKeyDown(KeyEventArgs e) {
            if (!IsKeyboardFocused) { base.OnKeyDown(e); return; }
            switch (e.Key) {
                case Key.Left: case Key.Right:
                    if (!e.IsRepeat || heldKey == Key.None) {
                        heldKey = e.Key;
                        heldWay = e.Key == Key.Left ? -1 : 1;
                        heldSince = DateTime.UtcNow;
                        Set(current + heldWay);
                        hold.Start();
                    }
                    e.Handled = true;
                    return;
                case Key.Up: case Key.Down:
                    MoveTo(e.Key == Key.Up ? -1 : 1);
                    e.Handled = true;
                    return;
                case Key.Escape:
                    Leave();
                    e.Handled = true;
                    return;
            }
            base.OnKeyDown(e);
        }

        protected override void OnKeyUp(KeyEventArgs e) {
            if (e.Key == heldKey) { StopHold(); e.Handled = true; return; }
            base.OnKeyUp(e);
        }

        // The longer the key is held, the bigger each step: after a short wait 1, then 2, then 5 (about 25 steps a second).
        void Held() {
            if (heldKey == Key.None || !Keyboard.IsKeyDown(heldKey) || !IsKeyboardFocused) { StopHold(); return; }
            double held = (DateTime.UtcNow - heldSince).TotalMilliseconds;
            if (held < 350) return;
            int step = held < 1000 ? 1 : held < 1800 ? 2 : 5;
            Set(current + heldWay * step);
        }

        void StopHold() {
            hold.Stop();
            heldKey = Key.None;
            Flush();
        }

        protected override void OnPreviewMouseWheel(MouseWheelEventArgs e) {
            if (!IsKeyboardFocusWithin) { base.OnPreviewMouseWheel(e); return; }
            var now = DateTime.UtcNow;
            int step = (now - lastWheel).TotalMilliseconds < 45 ? 5 : 1;
            lastWheel = now;
            int notches = Math.Max(1, Math.Abs(e.Delta) / 120);
            Set(current + Math.Sign(e.Delta) * step * notches);
            e.Handled = true;
        }

        void MoveTo(int way) {
            var panel = Parent as Panel;
            if (panel == null) return;
            int at = panel.Children.IndexOf(this);
            for (int i = at + way; i >= 0 && i < panel.Children.Count; i += way) {
                var next = panel.Children[i] as AdjustRow;
                if (next != null && next.IsVisible) { next.Focus(); return; }
            }
        }

        void Leave() {
            StopHold();
            var window = Window.GetWindow(this);
            if (window != null) window.Focus();
        }

        // True when an adjustment line is chosen and the key is one it uses, so the window leaves that key alone
        // (← → otherwise skip video time or move the GIF start and end).
        internal static bool OwnsKey(KeyEventArgs e) {
            var focused = Keyboard.FocusedElement as DependencyObject;
            if (focused is AdjustRow)
                return e.Key == Key.Left || e.Key == Key.Right || e.Key == Key.Up || e.Key == Key.Down || e.Key == Key.Escape;
            var typing = focused as TextBox;
            if (typing != null && e.Key == Key.Escape) {
                for (DependencyObject at = typing; at != null; at = VisualTreeHelper.GetParent(at))
                    if (at is AdjustRow) return true;
            }
            return false;
        }

        // True when the wheel over this spot belongs to a chosen line (so a scrolling column leaves it alone).
        internal static bool TakesWheel(object source) {
            for (var at = source as DependencyObject; at != null; at = at is Visual ? VisualTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at)) {
                var row = at as AdjustRow;
                if (row != null) return row.IsKeyboardFocusWithin;
            }
            return false;
        }
    }

    // The five lines (brightness, contrast, saturation, color temperature, sharpness) and one short hint under them.
    internal sealed class AdjustRows : StackPanel {
        readonly AdjustRow[] rows;
        readonly TextBlock hint;

        // change(field, value): field 0 brightness level, 1 contrast, 2 saturation, 3 color temperature, 4 sharpness.
        internal AdjustRows(Action<int, int> change) {
            rows = new[] {
                new AdjustRow(L.T("Brightness"), -100, 100, v => change(0, v)),
                new AdjustRow(L.P("Contrast", "대비"), -100, 100, v => change(1, v)),
                new AdjustRow(L.P("Saturation", "채도"), -100, 100, v => change(2, v)),
                new AdjustRow(L.P("Color temp", "색 온도"), -100, 100, v => change(3, v)),
                new AdjustRow(L.P("Sharpness", "선명하게"), 0, 100, v => change(4, v)),
            };
            rows[3].ToolTip = L.P("Negative makes it cooler (less yellow); positive makes it warmer. Click the line once, then hold ← or →.",
                "음수는 차갑게(누런기 줄이기), 양수는 따뜻하게 해요. 줄을 한 번 누른 뒤 ←/→를 꾹 눌러요.");
            foreach (var row in rows) Children.Add(row);
            hint = UI.Text("", 12, true);
            hint.TextWrapping = TextWrapping.Wrap;
            hint.Margin = new Thickness(4, 2, 0, 2);
            Children.Add(hint);
            ShowHint();
        }

        internal void Show(AdjustValues values) {
            rows[0].Show(Light.Level(values.Brightness));
            rows[1].Show(values.Contrast);
            rows[2].Show(values.Saturation);
            rows[3].Show(values.Temperature);
            rows[4].Show(values.Sharpness);
        }

        internal void ShowHint() {
            bool chosen = false;
            foreach (var row in rows) if (row.IsKeyboardFocusWithin) chosen = true;
            hint.Text = chosen
                ? L.P("Hold ← → or roll the wheel to change it. ↑ ↓ for another line, Esc to finish.",
                    "←/→를 꾹 누르거나 휠을 굴려 바꿔요. ↑/↓는 다른 줄, Esc는 끝이에요.")
                : L.P("Click a line, then hold ← or →.", "줄을 누르고 ←/→를 꾹 눌러요.");
        }
    }
}
