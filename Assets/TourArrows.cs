using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UIElements;

// Short-lived arrows that point at a note or a chord label on one of the two pianos' toruses while the narration speaks of it
// ("he drifts toward A", "a low A flat"). They come from arrows.json beside the story, track their target as it moves, fade in and out,
// and are drawn from the empty side of their torus: the tail and its label sit outside the ring, never on it.
[DefaultExecutionOrder(1210)]
public sealed class TourArrows : MonoBehaviour
{
    [Serializable] public sealed class Arrow { public double at, until; public int torus; public string target, label; public int pitchClass, midiPitch; }
    [Serializable] sealed class Data { public Arrow[] arrows; }
    const float FadeIn = .35f, FadeOut = .5f;
    ConcertoTori pair; MidiPlayer midi; VisualElement root; Canvas canvas;
    Arrow[] arrows = Array.Empty<Arrow>();
    readonly List<Label> labels = new();
    public int ActiveCount { get; private set; }
    public void Bind(VisualElement ui, string folder)
    {
        pair = GetComponent<ConcertoTori>(); midi = GetComponent<MidiPlayer>(); root = ui;
        if (string.IsNullOrEmpty(folder)) return;
        string file = Path.Combine(folder, "arrows.json");
        if (!File.Exists(file)) return;
        try { arrows = JsonUtility.FromJson<Data>(File.ReadAllText(file))?.arrows ?? arrows; } catch (Exception e) { Debug.LogWarning("arrows.json: " + e.Message); }
        canvas = new Canvas { name = "tour-arrows", pickingMode = PickingMode.Ignore };
        canvas.style.position = Position.Absolute; canvas.style.left = canvas.style.right = canvas.style.top = canvas.style.bottom = 0;
        root.Add(canvas);
    }
    void OnDestroy() { canvas?.RemoveFromHierarchy(); }
    Label LabelAt(int i)
    {
        while (labels.Count <= i)
        {
            var l = new Label { pickingMode = PickingMode.Ignore, enableRichText = false };
            l.style.position = Position.Absolute; l.style.width = 240; l.style.unityTextAlign = TextAnchor.MiddleCenter;
            l.style.fontSize = 24; l.style.unityFontStyleAndWeight = FontStyle.Bold; l.style.color = new Color(.92f, .97f, 1);
            l.style.textShadow = new TextShadow { offset = Vector2.zero, blurRadius = 10, color = new Color(.05f, .15f, .3f, .95f) };
            canvas.Add(l); labels.Add(l);
        }
        return labels[i];
    }
    void LateUpdate()
    {
        if (canvas == null || arrows.Length == 0 || root.panel == null) return;
        canvas.Items.Clear(); ActiveCount = 0;
        float W = root.resolvedStyle.width, H = root.resolvedStyle.height;
        double pos = midi.Position;
        if (pair != null && pair.SplitActive && W > 1 && H > 1 && midi.IsAudible)
            foreach (var a in arrows)
            {
                if (pos < a.at || pos >= a.until + FadeOut) continue;
                var shape = a.torus == 0 ? pair.Piano : pair.Orchestra;
                if (shape == null || !shape.gameObject.activeInHierarchy) continue;
                Vector3 world;
                if (a.target == "note") { int n = a.midiPitch - 21; if (n < 0 || n >= shape.NoteCount) continue; world = shape.NotePosition(n); }
                else world = shape.LabelPosition(a.pitchClass);
                var screen = Camera.main.WorldToScreenPoint(world); if (screen.z <= 0) continue;
                var t = RuntimePanelUtils.ScreenToPanel(root.panel, new Vector2(screen.x, Screen.height - screen.y));
                if (!float.IsFinite(t.x) || !float.IsFinite(t.y)) continue;
                var view = a.torus == 0 ? pair.PianoViewport : pair.OrchestraViewport;
                var centre = new Vector2(view.center.x * W, (1 - view.center.y) * H);
                float alpha = Mathf.Min(Mathf.Clamp01((float)(pos - a.at) / FadeIn), Mathf.Clamp01((float)(a.until + FadeOut - pos) / FadeOut));
                alpha = alpha * alpha * (3 - 2 * alpha);
                // The tail leaves from outside the ring: to the side of the target away from the torus's centre, a little above or below.
                float side = t.x < centre.x ? -1 : 1, vert = t.y < centre.y ? -1 : 1;
                var tail = new Vector2(Mathf.Clamp(t.x + side * W * .2f, 130, W - 130), Mathf.Clamp(t.y + vert * H * .045f, 70, H - 70));
                if (Mathf.Abs(tail.x - t.x) < 70) tail.x = Mathf.Clamp(t.x + side * 90, 130, W - 130);
                // The top corners hold the portrait and the picture pop-up: a tail that would land there drops lower, still outside the ring.
                if (tail.y < H * .24f && (tail.x > W * .5f || tail.x < W * .3f)) tail.y = Mathf.Min(H - 70, t.y + H * .1f);
                var label = LabelAt(ActiveCount);
                label.text = a.label; label.style.display = DisplayStyle.Flex; label.style.opacity = alpha;
                label.style.left = tail.x - 120; label.style.top = tail.y + (vert < 0 ? -40 : 12);
                canvas.Items.Add(new Item { Tail = tail, Tip = t, Alpha = alpha, Age = (float)(pos - a.at) });
                ActiveCount++;
            }
        for (int i = ActiveCount; i < labels.Count; i++) labels[i].style.display = DisplayStyle.None;
        canvas.MarkDirtyRepaint();
    }
    struct Item { public Vector2 Tail, Tip; public float Alpha, Age; }
    sealed class Canvas : VisualElement
    {
        public readonly List<Item> Items = new();
        public Canvas() { generateVisualContent += Draw; }
        void Draw(MeshGenerationContext ctx)
        {
            var p = ctx.painter2D; p.lineCap = LineCap.Round; p.lineJoin = LineJoin.Round;
            foreach (var it in Items)
            {
                Vector2 approach = it.Tip + (it.Tail - it.Tip).normalized * 16;
                Vector2 mid = (it.Tail + approach) * .5f, d = approach - it.Tail, n = new Vector2(-d.y, d.x).normalized;
                Vector2 control = mid + n * d.magnitude * .22f;
                // a ring around the target that breathes while the arrow is up
                float ring = 15 + 3 * Mathf.Sin(it.Age * 5);
                for (int layer = 0; layer < 2; layer++)
                {
                    p.lineWidth = layer == 0 ? 5 : 2f;
                    p.strokeColor = layer == 0 ? new Color(.08f, .2f, .38f, .55f * it.Alpha) : new Color(.9f, .97f, 1, .95f * it.Alpha);
                    p.BeginPath(); p.MoveTo(it.Tail); p.QuadraticCurveTo(control, approach); p.Stroke();
                    Vector2 dir = (approach - control).normalized, side = new Vector2(-dir.y, dir.x);
                    p.BeginPath(); p.MoveTo(approach - dir * 14 + side * 7); p.LineTo(approach); p.LineTo(approach - dir * 14 - side * 7); p.Stroke();
                    p.BeginPath(); p.Arc(it.Tip, ring, 0, 360); p.Stroke();
                }
            }
        }
    }
}
