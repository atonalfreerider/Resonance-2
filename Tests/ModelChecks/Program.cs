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
// Umbilic-surface grammar compliance, one block per rule of the reference.
const int A=0,Bb=1,B=2,C=3,Db=4,D=5,Eb=6,E=7,F=8,Gb=9,G=10,Ab=11;
{
    int[] fourths={C,F,Bb,Eb,Ab,Db,Gb,B,E,A,D,G};int at=C;
    for(int i=0;i<12;i++){Check(at==fourths[i],"§1 circle of fourths order");at=HarmonyModel.Move(at,1,0);}
    Check(at==C,"§1 twelve stations close the circle");
    for(int t=0;t<12;t++){
        Check(Set(HarmonyModel.Object(t,'T'),t),"§1.1 T");Check(Set(HarmonyModel.Object(t,'e'),t,t+4),"§1.1 e = T,3rd");
        Check(Set(HarmonyModel.Object(t,'d'),t+4,t+7),"§1.1 d = 3rd,5th");Check(Set(HarmonyModel.Object(t,'c'),t,t+7),"§1.1 c = T,5th");
        Check(Set(HarmonyModel.Object(t,'l'),t+11,t),"§1.1 l = 7th,T");Check(Set(HarmonyModel.Object(t,'M'),t,t+4,t+7),"§1.1 M");
        Check(Set(HarmonyModel.Object(t,'m'),t+4,t+7,t+11),"§1.1 m");
    }
    foreach(var tri in new[]{new[]{C,Ab,E},new[]{F,Db,A},new[]{D,Bb,Gb},new[]{G,Eb,B}})
        for(int i=0;i<3;i++)Check(HarmonyModel.Move(tri[i],0,1)==tri[(i+1)%3],"§2 S follows the directed augmented triangle");
    for(int n=0;n<12;n++){var d=HarmonyModel.Decompose(n);Check(d.station==n%4&&d.rotation==n/4%3,"§3 s = n mod 4, r = floor(n/4) mod 3");}
    Check(HarmonyModel.Move(C,0,2)==HarmonyModel.Move(HarmonyModel.Move(C,0,1),0,1),"§4 P is two S rotations");
    foreach(string same in new[]{"1 0 M","10M","1M"})Check(HarmonyModel.Evaluate(C,same)[0].Surface==HarmonyModel.Evaluate(C,"M")[0].Surface,"§5 default expansion X = 1 0 X: "+same);
    Check(HarmonyModel.Evaluate(C,"0M")[0].Surface==C,"§5 0X reads the current surface");
    Check(HarmonyModel.Evaluate(C,"0 S m")[0].Surface==Ab&&HarmonyModel.Evaluate(C,"0Sm")[0].Surface==Ab,"§5 station, then rotation, then object");
    var cohered=HarmonyModel.Evaluate(C,"MMMM")[0];
    Check(cohered.Surface==Ab&&cohered.CanonicalSteps==4&&cohered.CanonicalMove==(0,1),"§6 four stations carry into one rotation");
    Check(Set(HarmonyModel.Evaluate(C,"e0l")[0].Pitches,F,A,E)&&Set(HarmonyModel.Evaluate(C,"0el")[0].Pitches,C,E,F),"§7 e0l and 0el are different 3-tone sets");
    // §8: (X)Y reads Y from X's end; X's movement never advances the canonical surface.
    var bridge=HarmonyModel.Evaluate(C,"0m0M > (0Pc) 2d > M");
    Check(bridge[1].Surface==D&&bridge[1].Anchored,"§8.1 anchor: 2d is read from E' and reaches D'");
    Check(bridge[1].CanonicalSurface==Bb&&bridge[1].CanonicalMove==(2,0),"§8.3 exclusion: canonical sum is (2,0), not E'+2");
    Check(bridge[2].Surface==Eb&&!bridge[2].Anchored,"§8.2 remainder: the next object continues from canonical Bb', not D'");
    Check(Set(bridge[2].Held,E,B)&&bridge[2].Pitches.Contains(E)&&bridge[2].Pitches.Contains(B),"§8.2 remainder sustains E-B into later events");
    Check(HarmonyModel.Evaluate(C,"(dPd) 0M")[0].Surface!=C,"§8.4 provenance: a parenthesized history such as dPd is valid and sets the anchor");
    Check(HarmonyModel.Evaluate(C,"(0Pc)(Sc) 0M")[0].Surface==HarmonyModel.Move(HarmonyModel.Move(E,1,1),0,0),"§8 chained remainders anchor at the final one");
    var plain=HarmonyModel.Evaluate(C,"0M")[0];var seventh=HarmonyModel.Evaluate(C,"0m0M")[0];
    Check(HarmonyModel.SameChord(plain,seventh)&&!Set(plain.Pitches,seventh.Pitches.ToArray()),"§9 same sums and final object: same chord, different sounding union");
    Check(!HarmonyModel.SameChord(HarmonyModel.Evaluate(C,"M")[0],HarmonyModel.Evaluate(C,"0M")[0]),"§9 different sums: different chords");
    var targets=HarmonyModel.Evaluate(C,"G > BDG > G");
    Check(targets[0].Surface==G&&Set(targets[0].Pitches,G)&&targets[1].Surface==G&&Set(targets[1].Pitches,G,B,D)&&Set(targets[2].Pitches,G),"§11.2 G > BDG > G targets T=G, M on G', T=G");
    Check(HarmonyModel.Evaluate(G,"C Major")[0].Surface==C&&HarmonyModel.Evaluate(G,"D Minor")[0].Surface==Bb,"§11.2 standard names map to C' M and Bb' m");
    Check(Set(HarmonyModel.Evaluate(C,"D Minor")[0].Pitches,D,F,A),"§11.2 D Minor sounds D F A");
    bool rejectedCluster=false;try{HarmonyModel.Evaluate(C,"CEGB");}catch(ArgumentException){rejectedCluster=true;}
    Check(rejectedCluster,"§11.2 a cluster with no single surface object is flagged");
    for(int t=0;t<12;t++)foreach(char o in HarmonyModel.Objects){var home=HarmonyModel.Identify(HarmonyModel.Object(t,o));Check(home.Count==1&&home[0]==(t,o),"Every local object has one home");}
    Check(HarmonyModel.Home(D,"m")==(Bb,'m')&&HarmonyModel.Home(A,"m7")==(F,'m')&&HarmonyModel.Home(B,"dim")==(G,'d')&&HarmonyModel.Home(G,"7")==(G,'M'),"Axiom 2: minor triads live on the surface a third below");
    // I-V-vi-IV is one key-invariant word from the tonic surface.
    for(int key=0;key<12;key++){
        var chords=new[]{(key,""),(key+7,""),(key+9,"m"),(key+5,"")};var word=new List<string>();int from=HarmonyModel.KeyHome(key,false).surface;
        foreach(var (root,q) in chords){var home=HarmonyModel.Home(root,q);word.Add(HarmonyModel.Token(from,home.surface,home.obj));from=home.surface;}
        Check(string.Join(" > ",word)=="0M > 3PM > 2m > 0M","I-V-vi-IV grammar word in every key");
        var frames=HarmonyModel.Evaluate(key,string.Join(" > ",word));
        Check(Set(frames[2].Pitches,key+9,key,key+4),"The word sounds vi");
    }
    Check(HarmonyModel.Roman(G,"",C,false)=="V"&&HarmonyModel.Roman(A,"m",C,false)=="vi"&&HarmonyModel.Roman(Bb,"",C,false)=="♭VII"&&HarmonyModel.Roman(Db,"",Bb,true)=="III"&&HarmonyModel.Roman(B,"dim",C,false)=="vii°","Roman numerals");
}
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
var alignment=new SongAlignment{anchors=new(){new(0,.25),new(10,11),new(30,29)}};
alignment.Validate();
foreach(double t in new[]{0.0,1,9.5,10,15,29,30,35})Check(Math.Abs(alignment.ToMidi(alignment.ToAudio(t))-t)<1e-9,"Timing inverse round trip across tempo changes");
Check(Math.Abs(alignment.ToAudio(20)-20)<1e-9,"Piecewise tempo alignment");
bool badAnchor=false;try{new SongAlignment{anchors=new(){new(0,0),new(2,1),new(1,2)}}.Validate();}catch(ArgumentException){badAnchor=true;}
Check(badAnchor,"Reject backwards score timing");
// Distinct synthetic pitches with a known local tempo change, independent audio fixture.
int sampleRate=11025;double[] scoreEdges={0,1,2,3,4,5,6},audioEdges={0,1.3,2.6,3.9,4.6,5.3,6};
int[] pitches={57,62,59,64,60,67};var waveform=new float[sampleRate*6];
for(int s=0;s<6;s++)for(int i=(int)(audioEdges[s]*sampleRate);i<(int)(audioEdges[s+1]*sampleRate);i++)waveform[i]=(float)(.7*Math.Sin(2*Math.PI*440*Math.Pow(2,(pitches[s]-69)/12.0)*i/sampleRate));
var scoreChroma=new float[61][];for(int i=0;i<61;i++){scoreChroma[i]=new float[12];scoreChroma[i][(pitches[Math.Min(5,i/10)]-21)%12]=1;}
var warped=SongAlignment.Analyze(waveform,sampleRate,scoreChroma,.1,6,6);
for(int i=1;i<6;i++)Check(Math.Abs(warped.ToAudio(scoreEdges[i])-audioEdges[i])<.36,"Chroma alignment finds local tempo changes");
Console.WriteLine($"PASS: {checks} harmonic, persistence, MIDI, song-form and alignment checks");
