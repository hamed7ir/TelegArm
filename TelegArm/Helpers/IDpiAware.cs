namespace TelegArm.Helpers
{
    /// <summary>Redesign step 1.1 — a control that keeps DPI-dependent metrics of its own (owner-drawn sizes,
    /// cached fonts, glyph geometry). <see cref="DpiScaler.Rescale"/> has ALREADY rescaled its bounds, padding,
    /// margins and explicit font when this is called, so the implementer re-derives only what is its own —
    /// typically fonts from <see cref="FontCache"/> and lengths from <see cref="Dpi.Px(int,int)"/>.
    /// Called children-first, so a container's callback sees its children already done.</summary>
    public interface IDpiAware
    {
        void OnDpiChanged(int oldDpi, int newDpi);
    }

    /// <summary>A top-level window that knows its own DPI (AppForm, through WindowDpi, from step 1.3).
    /// <see cref="Dpi.Of"/> asks a control's top-level window; any other window is at the system DPI.</summary>
    public interface IDpiHost
    {
        int Dpi { get; }
    }
}
