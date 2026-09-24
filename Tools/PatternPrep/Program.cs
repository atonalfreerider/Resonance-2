using NAudio.Midi;
using System.Security.Cryptography;
using System.Text.Json;

if(args.Length==1&&args[0]=="--test-harmony"){KeyContext.SelfTest();KeyAnalysis.SelfTest();return;}
if(args.Length==2&&args[0]=="--fixture"){SelfTests.Fixture(args[1]);return;}
if(args.Length==3&&args[0]=="--write-fixture"){SelfTests.WriteFixture(args[1],args[2]);return;}
if(args.Length>=1&&args[0]=="--sweep"){SelfTests.Sweep(args.Skip(1).ToArray());return;}
if(args.Length>=1&&args[0]=="--self-test"){KeyContext.SelfTest();KeyAnalysis.SelfTest();SelfTests.Run(args.Contains("--verbose"));return;}
if(args.Length>=1&&args[0]=="--verbose"){FormAnalysis.Verbose=true;args=args.Skip(1).ToArray();}
if(args.Length==0)throw new ArgumentException("PatternPrep score.mid [legacy-authored.json] | --self-test");
var path=Path.GetFullPath(args[0]);
var jsonOptions=new JsonSerializerOptions{IncludeFields=true,WriteIndented=false};
string settingsPath=Path.Combine(Path.GetDirectoryName(path),"song.json");
var settings=File.Exists(settingsPath)?JsonSerializer.Deserialize<SongSettings>(File.ReadAllText(settingsPath),jsonOptions):new SongSettings();
var midi=new MidiFile(path,false);
var cycles=MidiCycleAnalysis.Analyze(midi,true);
var trackNames=Enumerable.Range(0,midi.Tracks).Select(t=>midi.Events[t].OfType<TextEvent>().FirstOrDefault(e=>e.MetaEventType==MetaEventType.SequenceTrackName)?.Text??"").ToArray();
for(int i=0;i<trackNames.Length;i++)if(settings.TrackAliases.TryGetValue(trackNames[i],out var alias))trackNames[i]=alias;
var analysis=FormAnalysis.Build(cycles,settings,trackNames);
var form=analysis.Form;
if(FormAnalysis.Verbose)SelfTests.Dump(analysis);
var data=PreparedPatternSong.Capture(cycles,form);
data.TrackNames=trackNames;data.Style=analysis.Style;data.FormName=analysis.FormName;
for(int i=0;i<data.Sections.Length;i++){
    var info=analysis.Sections[i];var section=data.Sections[i];
    section.Label=info.Label;section.Role=info.Role;section.Letter=info.Letter;section.Visit=info.Visit;section.Transpose=info.Transpose;
    section.PhraseBars=info.PhraseBars;section.Loops=info.Loops;section.CycleBeats=info.CycleBeats;section.Similarity=info.Similarity;
    section.KeyRoot=info.KeyRoot;section.KeyMinor=info.KeyMinor;
    section.ParentPath=i<settings.SectionParents.Length?settings.SectionParents[i]:info.Parent;
}
FormAnalysis.Apply(analysis,data);
data.LeadVocalTrack=settings.LeadVocalTrack;
if(data.LeadVocalTrack<0)data.LeadVocalTrack=Array.FindIndex(data.TrackNames,n=>n.Contains("vocal",StringComparison.OrdinalIgnoreCase)&&!n.Contains("back",StringComparison.OrdinalIgnoreCase));
data.Key=settings.Key;data.Minor=settings.Minor;data.KeySource=settings.KeySource;
if(!string.IsNullOrWhiteSpace(settings.SectionSource))data.Provenance=settings.SectionSource;
data.Title=SongTitle(path);data.TrackCount=midi.Tracks;
data.MidiSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
if(args.Length>1){var authored=JsonSerializer.Deserialize<PreparedPatternSong>(File.ReadAllText(args[1]),jsonOptions);
    if(authored.Disks?.Length>0){data.Disks=authored.Disks;foreach(var disk in data.Disks)foreach(var visit in disk.Visits)visit.Seconds=cycles.SecondsAt(visit.Beat);data.Provenance="Legacy authored pattern sequences; section boundaries estimated offline";}}
var events=new List<(int track,int order,MidiEvent e)>();int order=0;
for(int t=0;t<midi.Tracks;t++)foreach(var e in midi.Events[t])events.Add((t,order++,e));
var held=new Dictionary<(int,int,int),Queue<float>>();
var sustained=new Dictionary<(int,int,int),float>();var pedal=new bool[17];
var volume=Enumerable.Repeat(1f,17).ToArray();var expression=Enumerable.Repeat(1f,17).ToArray();
float Gain(int channel)=>volume[channel]*expression[channel];
var frames=new List<PreparedPatternSong.Frame>();int key=-1;bool minor=false;
foreach(var group in events.OrderBy(e=>e.e.AbsoluteTime).ThenBy(e=>e.order).GroupBy(e=>e.e.AbsoluteTime))
{
    var attacks=new List<PreparedPatternSong.Voice>();bool changed=false;
    foreach(var entry in group){var e=entry.e;
        if(e is KeySignatureEvent ks){key=HarmonyModel.Mod((ks.MajorMinor!=0?0:3)+7*ks.SharpsFlats);minor=ks.MajorMinor!=0;changed=true;}
        if(e is NoteEvent ne && (e.CommandCode==MidiCommandCode.NoteOn||e.CommandCode==MidiCommandCode.NoteOff)){
            var id=(entry.track,e.Channel,ne.NoteNumber);changed=true;
            if(e is NoteOnEvent on&&on.Velocity>0){if(!held.ContainsKey(id))held[id]=new();held[id].Enqueue(on.Velocity/127f);attacks.Add(new(){Track=entry.track,Channel=e.Channel,Pitch=ne.NoteNumber,Velocity=on.Velocity/127f*Gain(e.Channel)});}
            else if(held.TryGetValue(id,out var stack)&&stack.Count>0){float v=stack.Dequeue();if(pedal[e.Channel])sustained[id]=Math.Max(sustained.GetValueOrDefault(id),v);}
        }
        if(e is ControlChangeEvent cc){changed=true;int c=(int)cc.Controller;
            if(c==7)volume[e.Channel]=cc.ControllerValue/127f;if(c==11)expression[e.Channel]=cc.ControllerValue/127f;
            if(c==64){pedal[e.Channel]=cc.ControllerValue>=64;if(!pedal[e.Channel])foreach(var id in sustained.Keys.Where(k=>k.Item2==e.Channel).ToArray())sustained.Remove(id);}
            if(c is 120 or 123){if(c==123&&pedal[e.Channel])foreach(var item in held.Where(p=>p.Key.Item2==e.Channel&&p.Value.Count>0))sustained[item.Key]=Math.Max(sustained.GetValueOrDefault(item.Key),item.Value.Max());foreach(var id in held.Keys.Where(k=>k.Item2==e.Channel).ToArray())held.Remove(id);if(c==120)foreach(var id in sustained.Keys.Where(k=>k.Item2==e.Channel).ToArray())sustained.Remove(id);}
            if(c==121){expression[e.Channel]=1;pedal[e.Channel]=false;foreach(var id in sustained.Keys.Where(k=>k.Item2==e.Channel).ToArray())sustained.Remove(id);}
        }
    }
    if(changed){var voices=held.Where(p=>p.Value.Count>0).ToDictionary(p=>p.Key,p=>p.Value.Max());foreach(var p in sustained)voices[p.Key]=Math.Max(voices.GetValueOrDefault(p.Key),p.Value);
        frames.Add(new(){Time=cycles.SecondsAt(group.Key/(double)midi.DeltaTicksPerQuarterNote),Key=key,Minor=minor,Attacks=attacks.ToArray(),Voices=voices.Select(p=>new PreparedPatternSong.Voice{Track=p.Key.Item1,Channel=p.Key.Item2,Pitch=p.Key.Item3,Velocity=p.Value*Gain(p.Key.Item2)}).ToArray()});}
}
data.Duration=cycles.SecondsAt(cycles.EndBeat)+.08;data.Frames=frames.ToArray();
KeyContext.Apply(data,settings,cycles);
foreach(var hit in data.Notes.Concat(data.Disks.SelectMany(d=>d.Hits.Concat(d.Visits.SelectMany(v=>v.Hits)))))
{
    if(hit.Channel!=10)continue;
    // General MIDI instrument bands, not claims about the spectrum of this recording.
    (hit.RippleFrequency,hit.LowHz,hit.HighHz,hit.DecaySeconds)=hit.Pitch switch {
        35 or 36=>(4,40f,120f,.9f),38 or 39 or 40=>(18,120f,8000f,.35f),
        42 or 44 or 54 or 69 or 70=>(48,4000f,12000f,.13f),46=>(48,4000f,12000f,.22f),
        49 or 51 or 52 or 53 or 55 or 57 or 59=>(32,2000f,14000f,.5f),_=>(10,80f,2000f,.42f)};
    (hit.RippleRadius,hit.RippleWidth,hit.StrikeRadius)=hit.RippleFrequency switch {
        4=>(1.7f,.06f,.42f),18=>(.58f,.027f,.66f),48=>(hit.Pitch==46?.23f:.16f,.009f,1.02f),32=>(.43f,.017f,1.02f),_=>(.5f,.025f,.83f)};
}
GrammarWords.Build(data);
SectionCompression.Build(data,settings);
InstrumentPatterns.Build(data,cycles,analysis.Grid,settings.Key,settings.Minor);
DrumCompression.Build(data);
data.Summary=GrammarWords.Summary(data);
RegionPhases.Build(data);
MelodyStratification.Build(data,cycles.SecondsAt);
string output=path+".patterns.json",temporary=output+".tmp";File.WriteAllText(temporary,JsonSerializer.Serialize(data,jsonOptions));File.Move(temporary,output,true);
Console.WriteLine($"{output}: {data.FormName}\n  {data.Summary}\n  lossless reconstruction verified");

// Bundle folders are staged as ".../bundle" or ".../stems"; prefer the library title.
static string SongTitle(string midiPath)
{
    var folder=new DirectoryInfo(Path.GetDirectoryName(midiPath));
    foreach(var dir in new[]{folder,folder.Parent})
    {
        if(dir==null)continue;
        var library=Path.Combine(dir.FullName,"library.json");
        if(!File.Exists(library))continue;
        try{using var doc=JsonDocument.Parse(File.ReadAllText(library));if(doc.RootElement.TryGetProperty("title",out var title)&&title.GetString() is {Length:>0} text)return text;}catch(JsonException){}
    }
    return folder.Name;
}
