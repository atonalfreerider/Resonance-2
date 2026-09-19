void Check(bool value,string message){if(!value)throw new Exception(message);}
var names=new[]{"Intro","Verse","Chorus","Verse","Chorus","Bridge","Verse","Chorus","Verse","Chorus","Outro"};
var families=names.Distinct().ToArray();
var song=new PreparedPatternSong{Sections=names.Select((name,i)=>new PreparedPatternSong.Section{Name=name,Family=Array.IndexOf(families,name),Start=i*32,End=(i+1)*32,ParentPath="Song"}).ToArray()};
SectionCompression.GroupRepeatedForms(song);SectionCompression.BuildHierarchy(song);
Check(song.Form[0].Children.Length==4,"Root should have intro, verse+chorus, bridge, outro");
var carrier=song.Form.Single(n=>n.Name=="Verse + Chorus");
Check(carrier.Children.Length==2,"The carrier needs two child gears");
Check(carrier.Route.Length==8,"All eight verse/chorus visits belong to the carrier");
foreach(var section in song.Sections){var node=song.Form[section.Node];Check(node.Family==section.Family,"Grouping changed section identity");Check(section.ParentPath==(section.Name is "Verse" or "Chorus"?"Song/Verse + Chorus":"Song"),"Wrong group");}
for(int i=0;i<song.Sections.Length;i++)Check(song.Form[0].Route[i].Section==i,"Root route lost a section visit");
var unique=new PreparedPatternSong{Sections=new[]{new PreparedPatternSong.Section{Name="Intro",Family=0},new PreparedPatternSong.Section{Name="Outro",Family=1}}};
SectionCompression.GroupRepeatedForms(unique);SectionCompression.BuildHierarchy(unique);Check(unique.Form[0].Children.Length==2,"Unique forms must not be grouped");
Console.WriteLine("PASS: recurring pair carrier, all section visits, family identities, unique-form preservation");
foreach(int count in new[]{3,4,5,8}){
    var repeated=new PreparedPatternSong{Sections=Enumerable.Range(0,count*2).Select(i=>new PreparedPatternSong.Section{Name="Part "+(i%count),Family=i%count,Start=i*8,End=(i+1)*8}).ToArray()};
    SectionCompression.GroupRepeatedForms(repeated);SectionCompression.BuildHierarchy(repeated);
    Check(repeated.Form[0].Children.Length==1&&repeated.Form[repeated.Form[0].Children[0]].Children.Length==count,"Composite size "+count+" failed");
}
Console.WriteLine("PASS: N=3,4,5,8 repeating carriers");
var mixed=new PreparedPatternSong{Sections=new[]{0,1,0,2,0,1,0,2}.Select((id,i)=>new PreparedPatternSong.Section{Name="Part "+id,Family=id,Start=i*8,End=(i+1)*8}).ToArray()};
SectionCompression.GroupRepeatedForms(mixed);SectionCompression.BuildHierarchy(mixed);
Check(mixed.Form[0].Children.Length==1&&mixed.Form[mixed.Form[0].Children[0]].Children.Length==3,"A-B-A-C carrier must reuse A rather than duplicating its gear");
Console.WriteLine("PASS: repeating sequences with reused internal sections");

var harmony=new PreparedPatternSong{Notes=new[]{
    new MidiCycleAnalysis.Hit{Track=0,Channel=1,Pitch=60,Beat=.02,Length=1},
    new MidiCycleAnalysis.Hit{Track=0,Channel=1,Pitch=67,Beat=.02,Length=1},
    new MidiCycleAnalysis.Hit{Track=0,Channel=1,Pitch=62,Beat=1.02,Length=1},
    new MidiCycleAnalysis.Hit{Track=0,Channel=1,Pitch=69,Beat=1.02,Length=1},
    new MidiCycleAnalysis.Hit{Track=0,Channel=1,Pitch=70,Beat=2,Length=.5},
    new MidiCycleAnalysis.Hit{Track=1,Channel=10,Pitch=36,Beat=0,Length=.1}}};
MelodyStratification.Build(harmony,b=>b);
Check(harmony.MelodyStrands.Length==2,"Two harmony voices expected");
Check(harmony.MelodyStrands[0].Notes.Select(n=>n.Pitch).SequenceEqual(new[]{60,62}),"Lower voice continuity");
Check(harmony.MelodyStrands[1].Notes.Select(n=>n.Pitch).SequenceEqual(new[]{67,69,70}),"Upper voice continuity through missing lower voice");
Check(harmony.MelodyStrands[1].Notes[0].Start==.02&&harmony.MelodyStrands[1].Notes[0].End==1.02,"Preserve exact original timing");
Console.WriteLine("PASS: independent ordered harmony strands, missing voice, exact timing, percussion exclusion");
