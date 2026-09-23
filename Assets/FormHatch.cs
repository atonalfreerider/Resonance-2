using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

// Form identity without colour. Colour is reserved for tonality (chord function: I blue,
// IV red, V green); every section family is instead drawn with a hatch pattern in neutral
// ink, and returns read as the same texture coming back. Patterns are generated in band
// coordinates, s along the band and v across it (both in the caller's length units), so a
// ring sector, a rack strip, a swatch and a 3D drum-disc rim all carry the same texture.
public static class FormHatch
{
    public enum Pattern { Diagonal, Cross, Dots, Rings, BackDiagonal, Ticks, Grid, Zigzag }
    static readonly Pattern[] Order = { Pattern.Diagonal, Pattern.Cross, Pattern.Dots, Pattern.Rings, Pattern.BackDiagonal, Pattern.Grid, Pattern.Zigzag, Pattern.Ticks };
    // Pop roles keep one texture each; classical letters (A, B, C…) take patterns in order.
    public static Pattern ForRole(string role, int index)
    {
        string r = (role ?? "").Trim().ToLowerInvariant();
        if (r.StartsWith("verse")) return Pattern.Diagonal;
        if (r.StartsWith("pre")) return Pattern.Rings;
        if (r.StartsWith("chorus") || r.StartsWith("refrain")) return Pattern.Cross;
        if (r.StartsWith("post")) return Pattern.BackDiagonal;
        if (r.StartsWith("bridge")) return Pattern.Dots;
        if (r.StartsWith("intro") || r.StartsWith("outro") || r.StartsWith("coda") || r.StartsWith("count")) return Pattern.Ticks;
        if (r.StartsWith("interlude") || r.StartsWith("link") || r.StartsWith("solo") || r.StartsWith("break")) return Pattern.Zigzag;
        return Order[Math.Abs(index) % Order.Length];
    }
    // Form ink: one light, muted teal for every structural mark (hatches, gears, brackets), so
    // chord colour stays the loudest thing on screen. Text is a soft white for legibility.
    public static Color Ink(float alpha) => new(.5f, .74f, .72f, alpha);
    public static Color Label(float alpha) => new(.84f, .91f, .9f, alpha);
    public static readonly Color Metal = new(.34f, .5f, .5f, .6f), Shadow = new(.01f, .016f, .017f, .9f);

    // Strokes (s0, v0) → (s1, v1) and dots (s, v) filling the band [s0, s1] × [v0, v1].
    public static void Generate(Pattern pattern, float s0, float s1, float v0, float v1, float spacing, List<(float s0, float v0, float s1, float v1)> lines, List<(float s, float v)> dots)
    {
        float length = s1 - s0, depth = v1 - v0;
        if (length <= 0 || depth <= 0) return;
        spacing = Mathf.Max(1e-4f, spacing);
        int count = Mathf.Max(1, Mathf.FloorToInt(length / spacing));
        float step = length / count;
        float Clamp(float s) => Mathf.Clamp(s, s0, s1);
        void Slant(float s, bool forward)
        {
            float a = s - depth / 2, b = s + depth / 2;
            if (forward) lines.Add((Clamp(a), v0, Clamp(b), v1)); else lines.Add((Clamp(a), v1, Clamp(b), v0));
        }
        void Rings(int rows)
        {
            int pieces = Mathf.Max(1, Mathf.CeilToInt(length / (spacing * .5f)));
            for (int row = 1; row <= rows; row++)
            {
                float v = v0 + depth * row / (rows + 1);
                for (int k = 0; k < pieces; k++) lines.Add((s0 + length * k / pieces, v, s0 + length * (k + 1) / pieces, v));
            }
        }
        for (int k = 0; k < count; k++)
        {
            float s = s0 + step * (k + .5f);
            switch (pattern)
            {
                case Pattern.Diagonal: Slant(s, true); break;
                case Pattern.BackDiagonal: Slant(s, false); break;
                case Pattern.Cross: Slant(s, true); Slant(s, false); break;
                case Pattern.Ticks: lines.Add((s, v0, s, v1)); break;
                case Pattern.Grid: if (k % 2 == 0) lines.Add((s, v0, s, v1)); break;
                case Pattern.Dots:
                    if (depth > spacing * 1.6f) { dots.Add((s, v0 + depth * .3f)); dots.Add((Clamp(s + step / 2), v0 + depth * .7f)); }
                    else dots.Add((s, v0 + depth / 2));
                    break;
                case Pattern.Zigzag:
                    float a = s - step / 2, mid = s, b = s + step / 2;
                    lines.Add((Clamp(a), v0, Clamp(mid), v1)); lines.Add((Clamp(mid), v1, Clamp(b), v0)); break;
            }
        }
        if (pattern == Pattern.Rings) Rings(2);
        if (pattern == Pattern.Grid) Rings(1);
    }

    // Painter2D helpers shared by the pattern wheel and the side-panel swatches.
    static readonly List<(float, float, float, float)> lineBuffer = new();
    static readonly List<(float, float)> dotBuffer = new();
    public static void Draw(Painter2D p, Pattern pattern, float s0, float s1, float v0, float v1, float spacing, Func<float, float, Vector2> map, Color ink, float width = 1f)
    {
        lineBuffer.Clear(); dotBuffer.Clear();
        Generate(pattern, s0, s1, v0, v1, spacing, lineBuffer, dotBuffer);
        p.strokeColor = ink; p.lineWidth = width; p.lineCap = LineCap.Butt; p.BeginPath();
        foreach (var (a0, b0, a1, b1) in lineBuffer) { p.MoveTo(map(a0, b0)); p.LineTo(map(a1, b1)); }
        p.Stroke();
        if (dotBuffer.Count == 0) return;
        p.fillColor = ink;
        foreach (var (s, v) in dotBuffer) { p.BeginPath(); p.Arc(map(s, v), width * 1.1f, Angle.Degrees(0), Angle.Degrees(360)); p.Fill(); }
    }
    // A hatched ring sector: phase 0 is twelve o'clock, increasing clockwise.
    public static void Sector(Painter2D p, Pattern pattern, Vector2 center, float inner, float outer, double from, double to, Color ink, float spacing = 6, float width = .8f)
    {
        if (to - from < 1e-5) return;
        float mid = (inner + outer) / 2, circumference = 2 * Mathf.PI * mid;
        Vector2 Map(float s, float v) { float a = s / mid; return center + new Vector2(Mathf.Sin(a), -Mathf.Cos(a)) * v; }
        Draw(p, pattern, (float)from * circumference, (float)to * circumference, inner, outer, spacing, Map, ink, width);
    }

    // A legend chip for a section family: its hatch in a small outlined rectangle.
    public sealed class Swatch : VisualElement
    {
        public Pattern Pattern;
        public Swatch(Pattern pattern)
        {
            Pattern = pattern; pickingMode = PickingMode.Ignore;
            style.width = 22; style.height = 14; style.flexShrink = 0;
            generateVisualContent += ctx =>
            {
                var r = contentRect; if (r.width < 1) return; var p = ctx.painter2D;
                Draw(p, Pattern, r.xMin, r.xMax, r.yMin, r.yMax, 4.5f, (s, v) => new Vector2(s, v), Ink(.85f), .9f);
                p.strokeColor = Ink(.6f); p.lineWidth = 1; p.BeginPath(); p.MoveTo(r.min); p.LineTo(new Vector2(r.xMax, r.yMin)); p.LineTo(r.max); p.LineTo(new Vector2(r.xMin, r.yMax)); p.ClosePath(); p.Stroke();
            };
        }
    }
}
