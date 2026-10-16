using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace TelegArm.Helpers
{
    /// <summary>
    /// A small SVG path-data reader (M/L/H/V/C/S/Z, absolute and relative) into a GDI+ GraphicsPath, scaled from a viewBox
    /// into a rectangle. For icons drawn as shapes rather than font glyphs (a font glyph is tofu on RT 8.1).
    /// </summary>
    internal static class SvgPath
    {
        /// <summary>The path <paramref name="d"/> (in a <paramref name="viewBox"/> x viewBox box) fitted into <paramref name="r"/>.</summary>
        public static GraphicsPath Parse(string d, float viewBox, RectangleF r)
        {
            var path = new GraphicsPath(FillMode.Winding);
            float sx = r.Width / viewBox, sy = r.Height / viewBox;
            Func<float, float, PointF> map = (x, y) => new PointF(r.X + x * sx, r.Y + y * sy);
            var tokens = Tokenize(d);
            int i = 0;
            char cmd = 'M';
            float cx = 0, cy = 0, startX = 0, startY = 0, lastC2x = 0, lastC2y = 0;
            bool lastWasCurve = false;
            Func<float> next = () => float.Parse(tokens[i++], CultureInfo.InvariantCulture);
            while (i < tokens.Count)
            {
                if (char.IsLetter(tokens[i][0])) cmd = tokens[i++][0];
                bool rel = char.IsLower(cmd);
                switch (char.ToUpperInvariant(cmd))
                {
                    case 'M':
                    {
                        float x = next(), y = next();
                        if (rel) { x += cx; y += cy; }
                        path.StartFigure();
                        cx = startX = x; cy = startY = y;
                        cmd = rel ? 'l' : 'L';   // further pairs are lines
                        lastWasCurve = false;
                        break;
                    }
                    case 'L':
                    {
                        float x = next(), y = next();
                        if (rel) { x += cx; y += cy; }
                        path.AddLine(map(cx, cy), map(x, y));
                        cx = x; cy = y; lastWasCurve = false;
                        break;
                    }
                    case 'H':
                    {
                        float x = next();
                        if (rel) x += cx;
                        path.AddLine(map(cx, cy), map(x, cy));
                        cx = x; lastWasCurve = false;
                        break;
                    }
                    case 'V':
                    {
                        float y = next();
                        if (rel) y += cy;
                        path.AddLine(map(cx, cy), map(cx, y));
                        cy = y; lastWasCurve = false;
                        break;
                    }
                    case 'C':
                    case 'S':
                    {
                        float x1, y1;
                        if (char.ToUpperInvariant(cmd) == 'C')
                        {
                            x1 = next(); y1 = next();
                            if (rel) { x1 += cx; y1 += cy; }
                        }
                        else
                        {
                            // the first control point mirrors the previous curve's second one
                            x1 = lastWasCurve ? 2 * cx - lastC2x : cx;
                            y1 = lastWasCurve ? 2 * cy - lastC2y : cy;
                        }
                        float x2 = next(), y2 = next(), x = next(), y = next();
                        if (rel) { x2 += cx; y2 += cy; x += cx; y += cy; }
                        path.AddBezier(map(cx, cy), map(x1, y1), map(x2, y2), map(x, y));
                        lastC2x = x2; lastC2y = y2; lastWasCurve = true;
                        cx = x; cy = y;
                        break;
                    }
                    case 'Z':
                        path.CloseFigure();
                        cx = startX; cy = startY; lastWasCurve = false;
                        break;
                    default:
                        throw new FormatException("SVG path command '" + cmd + "' is not supported");
                }
            }
            return path;
        }

        /// <summary>Letters, and numbers split the SVG way: "-.27-.27" is three numbers? no: "-.27", "-.27"; ".67.36" is ".67", ".36".</summary>
        private static List<string> Tokenize(string d)
        {
            var t = new List<string>();
            int i = 0;
            while (i < d.Length)
            {
                char c = d[i];
                if (char.IsWhiteSpace(c) || c == ',') { i++; continue; }
                if (char.IsLetter(c)) { t.Add(c.ToString()); i++; continue; }
                int s = i;
                bool dot = false, exp = false;
                if (c == '-' || c == '+') i++;
                while (i < d.Length)
                {
                    char k = d[i];
                    if (char.IsDigit(k)) { i++; continue; }
                    if (k == '.' && !dot && !exp) { dot = true; i++; continue; }
                    if ((k == 'e' || k == 'E') && !exp) { exp = true; i++; if (i < d.Length && (d[i] == '-' || d[i] == '+')) i++; continue; }
                    break;
                }
                t.Add(d.Substring(s, i - s));
            }
            return t;
        }
    }
}
