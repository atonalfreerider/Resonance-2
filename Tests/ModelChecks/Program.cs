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
Console.WriteLine($"PASS: {checks} harmonic-model and MIDI-state checks");
