using System;
using System.Windows.Automation;
using System.Windows.Controls;

namespace LoveMyShelf {
    // Every [Reset] works both ways (2026-11-27 user: "if what it was before looks better, pressing Reset once more
    // should bring back the settings from just before, like PotPlayer"): the first press puts the default back and the
    // button becomes [Undo reset]; pressing that brings back what was there just before. Changing anything in between
    // forgets the old settings and the button is [Reset] again.
    //   var toggle=ResetToggle.Attach(button,() => now,v => SetTo(v),() => ResetAll());
    //   ... and wherever the settings change: toggle.Changed();
    internal sealed class ResetToggle {
        readonly Button button;
        readonly Func<object> read;
        readonly Action<object> restore;
        readonly Action reset;
        readonly object resetLabel, resetTip;
        readonly string resetName;
        object before, after;
        internal string KeyPrefix = ""; // a shortcut shown before the label, like "Z  "
        bool busy;

        ResetToggle(Button button, Func<object> read, Action<object> restore, Action reset) {
            this.button = button; this.read = read; this.restore = restore; this.reset = reset;
            resetLabel = button.Content; resetTip = button.ToolTip; resetName = AutomationProperties.GetName(button);
        }

        // The button's own Click must not also reset: Attach takes the reset over.
        internal static ResetToggle Attach(Button button, Func<object> read, Action<object> restore, Action reset) {
            var toggle = new ResetToggle(button, read, restore, reset);
            button.Click += delegate { toggle.Press(); };
            return toggle;
        }

        // For a button that already runs something on Click (it calls Press itself).
        internal static ResetToggle For(Button button, Func<object> read, Action<object> restore, Action reset) {
            return new ResetToggle(button, read, restore, reset);
        }

        internal bool CanUndo { get { return before != null; } }

        internal void Press() {
            busy = true;
            try {
                if (before != null && Equals(read(), after)) {
                    var back = before;
                    Forget();
                    restore(back);
                } else {
                    var now = read();
                    reset();
                    var then = read();
                    if (Equals(now, then)) { Forget(); return; } // it was already the default: nothing to bring back
                    before = now; after = then;
                    button.Content = KeyPrefix + L.P("Undo reset", "되돌리기");
                    button.ToolTip = L.P("Brings back the settings from just before Reset.", "원래대로 누르기 바로 전 설정으로 돌아가요.");
                    AutomationProperties.SetName(button, L.P("Undo reset", "되돌리기"));
                    button.IsEnabled = true;
                }
            } finally { busy = false; }
        }

        // The settings changed: if they are no longer what Reset left, the old ones are forgotten.
        internal void Changed() {
            if (busy || before == null) return;
            if (!Equals(read(), after)) Forget();
        }

        void Forget() {
            before = after = null;
            button.Content = resetLabel; button.ToolTip = resetTip;
            AutomationProperties.SetName(button, resetName ?? "");
        }
    }
}
