int checks = 0;
void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
bool Set(IEnumerable<int> a, params int[] b) => new HashSet<int>(a).SetEquals(b.Select(n => HarmonyModel.Mod(n)));
for (int tonic = 0; tonic < 12; tonic++)
{
    Check(Set(HarmonyModel.Surface(tonic), tonic, tonic+4, tonic+7, tonic+11), "Major7 surface");
    Check(Set(HarmonyModel.Object(tonic,'m'),tonic+4,tonic+7,tonic+11),"Minor object");
    var iiV = HarmonyModel.Evaluate(tonic+10,"0m > PM > M");
    Check(Set(iiV[0].Pitches,tonic+2,tonic+5,tonic+9),"ii chord");
    Check(Set(iiV[1].Pitches,tonic+7,tonic+11,tonic+2),"V chord");
    Check(Set(iiV[2].Pitches,tonic,tonic+4,tonic+7),"I chord");
    Check(Set(HarmonyModel.Evaluate(tonic,"0M > 0Sm")[1].Pitches,tonic,tonic+3,tonic+7),"Parallel minor");
    Check(Set(HarmonyModel.Evaluate(tonic+7,"0M > Sm")[1].Pitches,tonic,tonic+3,tonic+7),"V-i");
    Check(HarmonyModel.Evaluate(tonic,"M")[0].Surface==HarmonyModel.Mod(tonic+5),"Bare-token default");
    Check(Set(HarmonyModel.Evaluate(tonic,"0m0M")[0].Pitches,tonic,tonic+4,tonic+7,tonic+11),"Coherence union");
    int moved=tonic; for(int i=0;i<4;i++)moved=HarmonyModel.Move(moved,1,0);
    Check(moved==HarmonyModel.Move(tonic,0,1),"Station carry");
    for(int n=-24;n<=24;n++) { var d=HarmonyModel.Decompose(n); Check(HarmonyModel.Move(tonic,d.station,d.rotation)==HarmonyModel.Mod(tonic+5*n),"Signed normalization"); }
}
Check(Set(HarmonyModel.Evaluate(3,"0m0M > (0Pc) 2d")[1].Pitches,7,2,9,0),"Sustained bridge E B F# A");
foreach(string invalid in new[]{"", "4M", "0Q", "(0M", "0M)", "M >", "0M.M", "3P"})
{
    bool rejected=false; try{HarmonyModel.Evaluate(3,invalid);}catch(ArgumentException){rejected=true;}
    Check(rejected,"Reject invalid syntax: "+invalid);
}
var voices=new MidiVoiceState();
voices.NoteOn(1,39,.5f); voices.NoteOn(2,39,.8f); voices.NoteOff(1,39);
Check(voices.Snapshot().Single().Item2==.8f,"Independent channels");
voices.Control(2,64,127); voices.NoteOff(2,39);
Check(voices.Snapshot().Count==1,"Pedal holds note");
voices.Control(2,64,0); Check(voices.Snapshot().Count==0,"Pedal release");
voices.NoteOn(1,40,.5f); voices.NoteOn(1,40,.7f); voices.NoteOff(1,40);
Check(voices.Snapshot().Single().Item2==.7f,"Overlapping retrigger FIFO");
voices.NoteOn(1,40,0);Check(voices.Snapshot().Count==0,"Velocity-zero note off");
voices.NoteOn(1,40,.8f);voices.Control(1,64,127);voices.Control(1,123,0);
Check(voices.Snapshot().Count==1,"All notes off respects pedal");
voices.Control(1,120,0);Check(voices.Snapshot().Count==0,"All sound off clears pedal voices");
Check(HarmonyModel.CompatibleCollections(new[]{3,7,10}).Count>1,"Triad remains ambiguous");
Check(HarmonyModel.CompatibleCollections(Enumerable.Range(0,12)).Count==0,"Chromatic veto is collection-only");
var energy=new float[12];
HarmonicSpectrum.Accumulate(new[]{Tuple.Create(39,1f)},energy,true);
Check(energy[3]>energy[10] && energy[10]>energy[7] && energy[7]>0,"Partial amplitude ordering: C, G, E");
var single=(float[])energy.Clone();
HarmonicSpectrum.Accumulate(new[]{Tuple.Create(39,1f),Tuple.Create(46,1f)},energy,true);
Check(energy[10]>single[10],"Shared partials accumulate");
for(int key=0;key<12;key++)
{
    HarmonicSpectrum.Accumulate(new[]{Tuple.Create(36+key,1f)},energy,true);
    Check(Math.Abs(energy[key]-single[3])<.00001f,"Harmonic excitation transposes");
}
HarmonicSpectrum.Accumulate(new[]{Tuple.Create(39,1f)},energy,false);
Check(energy.Count(v=>v>0)==1,"Fundamental-only visualization");
var memory=new HarmonicMemory();var held=new[]{Tuple.Create(39,.5f)};
memory.Advance(held,1,2,1,false);float initial=memory.Energy[3];
memory.Advance(Array.Empty<Tuple<int,float>>(),2,2,1,false);
Check(Math.Abs(memory.Energy[3]-initial*.5f)<.000001,"Persistence follows selected half-life");
var split=new HarmonicMemory();for(int i=0;i<100;i++)split.Advance(held,.01,2,1,false);
Check(Math.Abs(split.Energy[3]-initial)<.00001,"Energy is independent of frame rate");
var loud=new HarmonicMemory();loud.Advance(new[]{Tuple.Create(39,1f)},1,2,1,false);
Check(Math.Abs(loud.Energy[3]-2*initial)<.00001,"Velocity scales accumulated influence proportionally");
memory.Clear();memory.Advance(held,.01,2,1,false);
Check(memory.Energy[3]>0 && memory.Energy[3]<initial,"Short notes retain energy, longer notes accumulate more");
memory.Advance(new[]{Tuple.Create(46,.5f)},.1,2,1,false);
Check(memory.Energy[3]>0 && memory.Energy[10]>0,"Successive chords coexist in persistent memory");
memory.Clear();Check(memory.Energy.All(x=>x==0),"Clear lingering energy");
var dynamics=new MidiVoiceState();dynamics.NoteOn(1,39,1);dynamics.Control(1,7,64);dynamics.Control(1,11,64);
Check(Math.Abs(dynamics.Snapshot()[0].Item2-64f/127*64/127)<.00001,"Channel volume and expression influence note loudness");
dynamics.Control(1,121,0);Check(Math.Abs(dynamics.Snapshot()[0].Item2-64f/127)<.00001,"Controller reset restores expression, preserving volume");

string testMidi=Path.Combine(Path.GetTempPath(),"resonance-pattern-"+Guid.NewGuid()+".mid");
try
{
    var events=new NAudio.Midi.MidiEventCollection(1,480);
    for(int bar=0;bar<4;bar++)
    {
        var on=new NAudio.Midi.NoteOnEvent(bar*1920,1,bar%2==0?60:62,100,240);
        events.AddEvent(on,0);events.AddEvent(on.OffEvent,0);
    }
    events.AddEvent(new NAudio.Midi.TempoEvent(1000000,1920),0);
    events.AddEvent(new NAudio.Midi.MetaEvent(NAudio.Midi.MetaEventType.EndTrack,0,7680),0);
    events.PrepareForExport();NAudio.Midi.MidiFile.Export(testMidi,events);
    var file=new NAudio.Midi.MidiFile(testMidi,false);
    var pitched=MidiCycleAnalysis.Analyze(file,true);
    Check(pitched.Measures.Count==4,"All measures including final measure retained");
    Check(pitched.Patterns.Count(p=>p.Bars==1)==2,"Pitch-aware patterns distinguish A and B");
    var phrase=pitched.Patterns.Single(p=>p.Bars==2);
    Check(phrase.Occurrences.Count==2 && phrase.Occurrences[0].Bar==0 && phrase.Occurrences[1].Bar==2,"ABAB compresses to two-bar cycle, including seed occurrence");
    Check(Math.Abs(pitched.SecondsAt(8)-6)<.00001 && Math.Abs(pitched.BeatAt(6)-8)<.00001,"Orrery tempo map follows tempo changes in both directions");
    var rhythm=MidiCycleAnalysis.Analyze(file,false);
    Check(rhythm.Patterns.Count==1 && rhythm.Patterns[0].Occurrences.Count==4,"Rhythm-only compression ignores pitch and removes redundant larger cycles");
    events=new NAudio.Midi.MidiEventCollection(1,480);
    var drum=new NAudio.Midi.NoteOnEvent(0,10,36,90,120);events.AddEvent(drum,0);events.AddEvent(drum.OffEvent,0);
    var finalNote=new NAudio.Midi.NoteOnEvent(5280,1,67,70,240);events.AddEvent(finalNote,0);events.AddEvent(finalNote.OffEvent,0);
    events.AddEvent(new NAudio.Midi.TimeSignatureEvent(3840,3,2,24,8),0);
    events.AddEvent(new NAudio.Midi.MetaEvent(NAudio.Midi.MetaEventType.EndTrack,0,6720),0);
    events.PrepareForExport();NAudio.Midi.MidiFile.Export(testMidi,events);
    var meters=MidiCycleAnalysis.Analyze(new NAudio.Midi.MidiFile(testMidi,false),false);
    Check(meters.Measures.Count==4 && meters.Measures[2].Start==8 && meters.Measures[3].Start==11,"Empty measures and changing meter preserve bar boundaries");
    Check(meters.Patterns.Any(p=>p.Channel==10) && meters.Patterns.Any(p=>p.Channel==1),"Mixed percussion and melodic channels remain separate");
    Check(meters.Patterns.Any(p=>p.Occurrences.Any(o=>o.Bar==3)),"One-off final-bar notes stay accessible");
}
finally{if(File.Exists(testMidi))File.Delete(testMidi);}
var tail=new VisualRelease();tail.Set(.8f);tail.Set(0);tail.Advance(.2f,.7f);
Check(tail.Level>0 && tail.Level<.8f,"Released note remains visible while fading");
tail.Set(.9f);Check(tail.Held && tail.Level==.9f,"Retrigger revives a fading visual without another object");
tail.Set(0);tail.Advance(.7f,.7f);Check(tail.Level==0,"Note visual reaches zero at release duration");
var song=new MidiCycleAnalysis{EndBeat=112};
for(int b=0;b<28;b++)song.Measures.Add(new MidiCycleAnalysis.Bar{Start=b*4,End=(b+1)*4,Numerator=4,Denominator=4});
// Verse / Chorus / Verse / Chorus / Bridge / Verse / Chorus, four bars each.
int[][] roots={new[]{60,65,67,60},new[]{65,67,60,60},new[]{60,65,67,60},new[]{65,67,60,60},new[]{62,67,62,67},new[]{60,65,67,60},new[]{65,67,60,60}};
for(int s=0;s<roots.Length;s++)for(int bar=0;bar<4;bar++)foreach(int interval in new[]{0,4,7})
    song.Notes.Add(new MidiCycleAnalysis.Hit{Beat=(s*4+bar)*4,Length=4,Pitch=roots[s][bar]+interval,Velocity=.8f,Channel=1});
var form=SongFormAnalysis.Build(song,4,"1 Verse\n5 Chorus\n9 Verse\n13 Chorus\n17 Bridge\n21 Verse\n25 Chorus");
Check(form.Sections.Count==7 && form.Families.Count==3,"Song form preserves ordered sections and recurring families");
Check(form.Families[0].Occurrences.Count==3 && form.Families[1].Occurrences.Count==3 && form.Families[2].Occurrences.Count==1,"Verse chorus bridge recurrence counts");
Check(form.Sections[0].Chords.Select(c=>c.Root).SequenceEqual(new[]{3,8,10,3}),"Verse carries I IV V I progression");
Check(form.Sections[0].ProgressionBeats==16,"Complete four-chord progression has one sixteen-beat cycle");
Check(form.At(16)==form.Sections[1] && form.At(32)==form.Sections[2],"Section transitions follow the song timeline");
var automatic=SongFormAnalysis.Build(song,4);
Check(automatic.Families.Count==3,"Repeated harmony groups automatic section families without claiming verse labels");
Check(automatic.Families.All(f=>f.Name.StartsWith("Section ")),"Unmarked MIDI has honest section labels");
var repeated=SongFormAnalysis.Build(song,16,"1 Verse\n17 Bridge\n21 Outro");
Check(repeated.Sections[0].ProgressionBeats==32 && repeated.Sections[0].Repetitions==2,"Nested progression cycles repeat inside a longer section");
bool badMap=false;try{SongFormAnalysis.Build(song,4,"5 Verse\n1 Chorus");}catch(ArgumentException){badMap=true;}
Check(badMap,"Reject invalid section boundaries without changing the form");
song.Markers.Add((0,"Verse"));song.Markers.Add((16,"Chorus"));
Check(SongFormAnalysis.Build(song).BoundarySource=="MIDI section markers","MIDI markers supply section names and boundaries");
Console.WriteLine($"PASS: {checks} harmonic, persistence, MIDI and song-form checks");
