using System;
using System.Windows.Forms;
using TelegArm.Core;
using TelegArm.Helpers;

namespace TelegArm.UI.Controls
{
    /// <summary>The composer / chat-list search field: exactly one of <see cref="Plain"/> (ThemedTextBox, a native EDIT — the
    /// search box always, the composer when ComposerEngine says "Plain") or <see cref="Rich"/> (EmojiComposer — the composer
    /// by default since plan step E5, 2026-10-14) is set. 1.x's third engine, MaterialSkin's MaterialTextBox2, is gone: the RT
    /// "dead input" it was blamed for came from TelegArm launching TabTip itself (device trip 2026-10-14; Program.cs).</summary>
    internal sealed class InputBox
    {
        public readonly ThemedTextBox Plain;
        public readonly EmojiComposer Rich;

        private InputBox(ThemedTextBox plain, EmojiComposer rich) { Plain = plain; Rich = rich; }

        /// <summary>The colour-emoji composer unless ComposerEngine (or TELEGARM_COMPOSER) says "Plain". Read once per process.</summary>
        public static bool UseEmojiComposer
        {
            get
            {
                if (_emoji.HasValue) return _emoji.Value;
                string env = null, setting = "";
                try { env = Environment.GetEnvironmentVariable("TELEGARM_COMPOSER"); } catch { }
                try { setting = AppSettings.Instance.ComposerEngine ?? ""; } catch { }
                string v = !string.IsNullOrEmpty(env) ? env : setting;
                _emoji = !string.Equals(v, "Plain", StringComparison.OrdinalIgnoreCase);
                Logger.Diag("[COMPOSER] engine=" + (_emoji.Value ? "Emoji" : "Plain") + " (" + (!string.IsNullOrEmpty(env) ? "TELEGARM_COMPOSER" : setting.Length > 0 ? "setting" : "default") + ")");
                return _emoji.Value;
            }
        }
        private static bool? _emoji;

        public static InputBox CreatePlain(ThemedTextBox box) { return new InputBox(box, null); }
        public static InputBox CreateRich(EmojiComposer box) { return new InputBox(null, box); }

        /// <summary>The control to put in a layout.</summary>
        public Control Control { get { return Rich != null ? (Control)Rich : Plain; } }

        public string Text
        {
            get { return Rich != null ? Rich.Text : Plain.Text; }
            set { if (Rich != null) Rich.Text = value; else Plain.Text = value; }
        }

        public string Hint
        {
            get { return Rich != null ? Rich.Hint : Plain.Hint; }
            set { if (Rich != null) Rich.Hint = value; else Plain.Hint = value; }
        }

        public int SelectionStart
        {
            get { return Rich != null ? Rich.SelectionStart : Plain.SelectionStart; }
            set { if (Rich != null) Rich.SelectionStart = value; else Plain.SelectionStart = value; }
        }

        public int SelectionLength
        {
            get { return Rich != null ? Rich.SelectionLength : Plain.SelectionLength; }
            set { if (Rich != null) Rich.SelectionLength = value; else Plain.SelectionLength = value; }
        }

        public void SelectAll() { if (Rich != null) Rich.SelectAll(); else Plain.SelectAll(); }
        public bool Focus() { return Control.Focus(); }
        public bool ContainsFocus { get { return Control.ContainsFocus; } }

        /// <summary>Read-only when false — never Enabled = false on the control (ThemedTextBox and EmojiComposer notes).</summary>
        public bool Enabled
        {
            get { return Rich != null ? Rich.InputEnabled : Plain.InputEnabled; }
            set { if (Rich != null) Rich.InputEnabled = value; else Plain.InputEnabled = value; }
        }

        public bool Visible
        {
            get { return Control.Visible; }
            set { Control.Visible = value; }
        }

        public ContextMenuStrip ContextMenuStrip
        {
            get { return Control.ContextMenuStrip; }
            set { Control.ContextMenuStrip = value; }
        }

        public event EventHandler TextChanged
        {
            add { Control.TextChanged += value; }
            remove { Control.TextChanged -= value; }
        }

        public event KeyEventHandler KeyDown
        {
            add { Control.KeyDown += value; }
            remove { Control.KeyDown -= value; }
        }

        /// <summary>Re-read the palette.</summary>
        public void Recolor() { if (Plain != null) Plain.Recolor(); else Rich.Invalidate(); }
    }
}
