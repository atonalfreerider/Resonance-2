using Unity.Profiling;

// Profiler markers for the per-frame paths, read by Tools → Resonance → Measure frame time
// (and visible in the Profiler). A marker costs almost nothing when nothing records it.
public static class Perf
{
    public static readonly ProfilerMarker
        MainUpdate=new("Resonance.Main.Update"),RefreshView=new("Resonance.Main.RefreshView"),Umbilic=new("Resonance.Main.SetUmbilic"),
        Field=new("Resonance.UmbilicField"),Aurora=new("Resonance.ChordAurora"),Featured=new("Resonance.FeaturedInstrument"),FeaturedFrames=new("Resonance.FeaturedInstrument.Frames"),
        Outline=new("Resonance.DominantChordOutline"),Views=new("Resonance.VisualizationViews"),Overlay=new("Resonance.SurfaceOverlay"),
        Wheels=new("Resonance.PatternWheels.Draw"),WheelsTick=new("Resonance.PatternWheels.Tick"),Changers=new("Resonance.InstrumentChangers"),
        Vocal=new("Resonance.LyricWheel.Vocal"),Words=new("Resonance.LyricWheel.Words"),Board=new("Resonance.LyricGraph"),
        Rack=new("Resonance.DrumLyricRack"),Drums=new("Resonance.DrumPatternDeck"),Midi=new("Resonance.MidiPlayer"),Explorer=new("Resonance.HarmonyExplorer"),
        Chords=new("Resonance.Chord"),Notes=new("Resonance.Note"),Dominance=new("Resonance.TonalDominance"),Director=new("Resonance.SongDirector");
    public static readonly string[] Names=
    {
        "Resonance.Main.Update","Resonance.Main.RefreshView","Resonance.Main.SetUmbilic","Resonance.UmbilicField","Resonance.ChordAurora","Resonance.FeaturedInstrument","Resonance.FeaturedInstrument.Frames",
        "Resonance.DominantChordOutline","Resonance.VisualizationViews","Resonance.SurfaceOverlay","Resonance.PatternWheels.Draw","Resonance.PatternWheels.Tick","Resonance.InstrumentChangers",
        "Resonance.LyricWheel.Vocal","Resonance.LyricWheel.Words","Resonance.LyricGraph","Resonance.DrumLyricRack","Resonance.DrumPatternDeck","Resonance.MidiPlayer","Resonance.HarmonyExplorer",
        "Resonance.Chord","Resonance.Note","Resonance.TonalDominance","Resonance.SongDirector",
    };
}
