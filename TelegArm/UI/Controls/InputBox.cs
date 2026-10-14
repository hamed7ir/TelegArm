using System;
using System.Windows.Forms;
using MaterialSkin.Controls;
using TelegArm.Core;
using TelegArm.Helpers;

namespace TelegArm.UI.Controls
{
    /// <summary>Redesign step 1.5 — the composer / chat-list search field while BOTH engines exist (AppSettings.ComposerEngine):
    /// exactly one of <see cref="Legacy"/> (MaterialTextBox2) or <see cref="Plain"/> (ThemedTextBox) is set.
    ///
    /// ⚠ WHY AN ADAPTER AND NOT A Control-TYPED FIELD. MaterialTextBox2 HIDES Control's events — TextChanged, KeyDown,
    ///   KeyPress, GotFocus, LostFocus, Enter, Leave, Click are re-declared (non-virtual, checked by reflection over the
    ///   shipped MaterialSkin 2.3.1 DLL). A subscriber holding it as a plain Control binds Control's event, which the
    ///   inner box never raises: Enter-to-send and typing notifications would go silent with no error. Every member here
    ///   is compiled against the CONCRETE type of its engine, so the Legacy half binds exactly what 1.x's
    ///   MaterialTextBox2-typed fields bound — that path is unchanged.
    /// Step 1.5b (after device gate G1) deletes the Legacy half; the fields then become ThemedTextBox.</summary>
    internal sealed class InputBox
    {
        public readonly MaterialTextBox2 Legacy;
        public readonly ThemedTextBox Plain;

        private InputBox(MaterialTextBox2 legacy, ThemedTextBox plain) { Legacy = legacy; Plain = plain; }

        /// <summary>Plain unless ComposerEngine (or TELEGARM_COMPOSER) says "Legacy". Read once per process.
        /// ⚠ Plain became the DEFAULT on 2026-10-14: the device trip showed the RT "dead input" came from TelegArm launching
        /// TabTip itself (now removed, Program.cs), not from the text box — a plain native EDIT types fine on RT without it.
        /// Legacy (MaterialSkin) stays selectable as the fallback until the new composer lands.</summary>
        public static bool UsePlain
        {
            get
            {
                if (_plain.HasValue) return _plain.Value;
                string env = null;
                try { env = Environment.GetEnvironmentVariable("TELEGARM_COMPOSER"); } catch { }
                string setting = "";
                try { setting = AppSettings.Instance.ComposerEngine ?? ""; } catch { }
                if (string.Equals(env, "plain", StringComparison.OrdinalIgnoreCase)) _plain = true;
                else if (string.Equals(env, "legacy", StringComparison.OrdinalIgnoreCase)) _plain = false;
                else _plain = !string.Equals(setting, "Legacy", StringComparison.OrdinalIgnoreCase);
                Logger.Diag("[COMPOSER] engine=" + (_plain.Value ? "Plain" : "Legacy") + " (" + (env != null && env.Length > 0 ? "TELEGARM_COMPOSER" : setting.Length > 0 ? "setting" : "default") + ")");
                return _plain.Value;
            }
        }
        private static bool? _plain;

        public static InputBox CreateLegacy(MaterialTextBox2 box) { return new InputBox(box, null); }
        public static InputBox CreatePlain(ThemedTextBox box) { return new InputBox(null, box); }

        /// <summary>The control to put in a layout.</summary>
        public Control Control { get { return Legacy != null ? (Control)Legacy : Plain; } }

        public string Text
        {
            get { return Legacy != null ? Legacy.Text : Plain.Text; }
            set { if (Legacy != null) Legacy.Text = value; else Plain.Text = value; }
        }

        public string Hint
        {
            get { return Legacy != null ? Legacy.Hint : Plain.Hint; }
            set { if (Legacy != null) Legacy.Hint = value; else Plain.Hint = value; }
        }

        public int SelectionStart
        {
            get { return Legacy != null ? Legacy.SelectionStart : Plain.SelectionStart; }
            set { if (Legacy != null) Legacy.SelectionStart = value; else Plain.SelectionStart = value; }
        }

        public int SelectionLength
        {
            get { return Legacy != null ? Legacy.SelectionLength : Plain.SelectionLength; }
            set { if (Legacy != null) Legacy.SelectionLength = value; else Plain.SelectionLength = value; }
        }

        public void SelectAll() { if (Legacy != null) Legacy.SelectAll(); else Plain.SelectAll(); }
        public bool Focus() { return Legacy != null ? Legacy.Focus() : Plain.Focus(); }
        public bool ContainsFocus { get { return Control.ContainsFocus; } }

        /// <summary>Legacy: Control.Enabled, as in 1.x. Plain: <see cref="ThemedTextBox.InputEnabled"/> — read-only, never
        /// Enabled = false on the EDIT (the RT contract).</summary>
        public bool Enabled
        {
            get { return Legacy != null ? Legacy.Enabled : Plain.InputEnabled; }
            set { if (Legacy != null) Legacy.Enabled = value; else Plain.InputEnabled = value; }
        }

        public bool Visible
        {
            get { return Control.Visible; }
            set { Control.Visible = value; }
        }

        public ContextMenuStrip ContextMenuStrip
        {
            get { return Legacy != null ? Legacy.ContextMenuStrip : Plain.ContextMenuStrip; }
            set { if (Legacy != null) Legacy.ContextMenuStrip = value; else Plain.ContextMenuStrip = value; }
        }

        public event EventHandler TextChanged
        {
            add { if (Legacy != null) Legacy.TextChanged += value; else Plain.TextChanged += value; }
            remove { if (Legacy != null) Legacy.TextChanged -= value; else Plain.TextChanged -= value; }
        }

        public event KeyEventHandler KeyDown
        {
            add { if (Legacy != null) Legacy.KeyDown += value; else Plain.KeyDown += value; }
            remove { if (Legacy != null) Legacy.KeyDown -= value; else Plain.KeyDown -= value; }
        }

        /// <summary>Plain only: re-read the palette (the legacy box is themed by MaterialSkin).</summary>
        public void Recolor() { if (Plain != null) Plain.Recolor(); }
    }
}
