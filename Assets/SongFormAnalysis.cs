using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

// Song -> recurring section families -> repeating chord progression -> chord steps.
// Section roles are taken from markers/user labels, never guessed from repetition alone.
public sealed class SongFormAnalysis
{
    [Serializable] public sealed class ChordStep
    {
        public double Start,End;
        public int Root=-1;
        public string Quality="";
        public float Energy;
        public bool Rest=>Root<0;
        public string Name(bool flats=false)=>Rest?"Rest":HarmonyModel.Name(Root,flats)+Quality;
        public string Identity=>Root+":"+Quality;
    }
    public sealed class Family
    {
        public int Id;
        public string Name;
        public readonly List<Section> Occurrences=new();
    }
    public sealed class Section
    {
        public int FirstBar,BarCount;
        public double Start,End,ProgressionBeats;
        public Family Family;
        public readonly List<ChordStep> Chords=new(); // absolute song beats, first cycle only
        public double Repetitions=>(End-Start)/Math.Max(.00001,ProgressionBeats);
    }
    public readonly List<Family> Families=new();
    public readonly List<Section> Sections=new();
    public readonly List<ChordStep> Timeline=new();
    public string BoundarySource;
    public double EndBeat;
    public Section At(double beat)=>Sections.LastOrDefault(s=>s.Start<=beat && beat<s.End)??Sections.LastOrDefault();

    public static SongFormAnalysis Build(MidiCycleAnalysis midi,int sectionBars=8,string boundaries="")
    {
        var form=new SongFormAnalysis{EndBeat=midi.EndBeat};
        if(midi.Measures.Count==0)return form;
        var notes=midi.Notes.Where(n=>n.Channel!=10).OrderBy(n=>n.Beat).ToList();
        var active=new List<MidiCycleAnalysis.Hit>();int next=0;
        var barSignatures=new List<string>();
        var barProfiles=new List<double[]>();
        foreach(var bar in midi.Measures)
        {
            var barSteps=new List<string>();
            var profile=new double[12];
            // Analyze quarter-beat windows, retaining subdivisions in compound meters.
            double grid=Math.Min(1,4.0/bar.Denominator);
            for(double start=bar.Start;start<bar.End-.000001;start+=grid)
            {
                double end=Math.Min(bar.End,start+grid);
                active.RemoveAll(n=>n.Beat+n.Length<=start);
                while(next<notes.Count && notes[next].Beat<end)active.Add(notes[next++]);
                var weights=new double[12];int bass=int.MaxValue;
                foreach(var n in active)
                {
                    double overlap=Math.Max(0,Math.Min(end,n.Beat+n.Length)-Math.Max(start,n.Beat));
                    if(overlap==0)continue;
                    weights[HarmonyModel.Mod(n.Pitch-21)]+=overlap*n.Velocity;
                    bass=Math.Min(bass,n.Pitch);
                }
                var chord=Infer(weights,bass);chord.Start=start;chord.End=end;
                for(int pc=0;pc<12;pc++)profile[pc]+=weights[pc];
                barSteps.Add(chord.Identity);
                if(form.Timeline.Count>0 && form.Timeline.Last().Identity==chord.Identity)
                {
                    var previous=form.Timeline.Last();
                    previous.Energy=(float)((previous.Energy*(previous.End-previous.Start)+chord.Energy*(end-start))/(end-previous.Start));previous.End=end;
                }
                else form.Timeline.Add(chord);
            }
            barSignatures.Add(bar.Numerator+"/"+bar.Denominator+":"+string.Join(",",barSteps));
            barProfiles.Add(profile);
        }
        var starts=new List<(double beat,string label)>();
        if(!string.IsNullOrWhiteSpace(boundaries))
        {
            foreach(string raw in boundaries.Split('\n'))
            {
                string line=raw.Trim();if(line.Length==0)continue;
                int split=line.IndexOfAny(new[]{' ','\t'});
                if(split<1 || !int.TryParse(line.Substring(0,split),out int bar) || bar<1 || bar>midi.Measures.Count || string.IsNullOrWhiteSpace(line.Substring(split)))
                    throw new ArgumentException("Use one boundary per line: 1 Intro, 5 Verse, 13 Chorus. Bars are 1-based.");
                double beat=midi.Measures[bar-1].Start;
                if(starts.Count>0 && beat<=starts.Last().beat)throw new ArgumentException("Section boundaries must increase, without duplicate bars.");
                starts.Add((beat,line.Substring(split).Trim()));
            }
            if(starts.Count==0 || starts[0].beat!=0)throw new ArgumentException("The first section must start at bar 1.");
            form.BoundarySource="Your section boundaries";
        }
        else if(midi.Markers.Any(m=>m.Beat<midi.EndBeat))
        {
            starts.AddRange(midi.Markers.Where(m=>m.Beat<midi.EndBeat).GroupBy(m=>m.Beat).OrderBy(g=>g.Key).Select(g=>(g.Key,g.Last().Label)));
            if(starts[0].beat>0)starts.Insert(0,(0,"Intro"));
            form.BoundarySource="MIDI section markers";
        }
        else
        {
            sectionBars=Math.Max(1,Math.Min(32,sectionBars));
            for(int bar=0;bar<midi.Measures.Count;bar+=sectionBars)starts.Add((midi.Measures[bar].Start,null));
            form.BoundarySource=$"Estimated {sectionBars}-bar sections · rename / edit boundaries";
        }
        var familyKeys=new Dictionary<string,Family>(StringComparer.OrdinalIgnoreCase);
        for(int i=0;i<starts.Count;i++)
        {
            double end=i+1<starts.Count?starts[i+1].beat:midi.EndBeat,start=starts[i].beat;
            int firstBar=midi.Measures.FindIndex(b=>b.End>start);
            int barCount=midi.Measures.Count(b=>b.Start<end && b.End>start);
            var section=new Section{Start=start,End=end,FirstBar=firstBar,BarCount=barCount,ProgressionBeats=end-start};
            // Only infer a shorter cycle at complete measure boundaries.
            bool whole=Math.Abs(midi.Measures[firstBar].Start-start)<.000001 && Math.Abs(midi.Measures[firstBar+barCount-1].End-end)<.000001;
            if(whole)for(int period=1;period<=barCount/2;period++)
            {
                if(barCount%period!=0)continue;
                bool repeats=true;
                for(int j=period;j<barCount;j++)
                {
                    var bar=midi.Measures[firstBar+j];var original=midi.Measures[firstBar+j%period];
                    if(Math.Abs((bar.End-bar.Start)-(original.End-original.Start))>.00001 ||
                        (barSignatures[firstBar+j]!=barSignatures[firstBar+j%period] && Similarity(barProfiles[firstBar+j],barProfiles[firstBar+j%period])<.94))
                    {repeats=false;break;}
                }
                if(repeats){section.ProgressionBeats=midi.Measures[firstBar+period-1].End-start;break;}
            }
            foreach(var c in form.Timeline.Where(c=>c.End>start && c.Start<start+section.ProgressionBeats))
                section.Chords.Add(new ChordStep{Start=Math.Max(start,c.Start),End=Math.Min(start+section.ProgressionBeats,c.End),Root=c.Root,Quality=c.Quality,Energy=c.Energy});
            string key=starts[i].label;
            if(key==null)key="auto:"+barCount+":"+string.Join("|",barSignatures.Skip(firstBar).Take(barCount));
            if(!familyKeys.TryGetValue(key,out var family))
            {
                // Compare against the first occurrence, avoiding transitive similarity drift.
                // This tolerates melody/voicing variation while keeping the harmonic bar order.
                if(starts[i].label==null)
                    family=form.Families.FirstOrDefault(f=>f.Occurrences[0].BarCount==barCount &&
                        Enumerable.Range(0,barCount).Average(j=>Similarity(barProfiles[firstBar+j],barProfiles[f.Occurrences[0].FirstBar+j]))>=.91);
                if(family==null)
                {
                    family=new Family{Id=form.Families.Count,Name=starts[i].label??("Section "+Letters(form.Families.Count))};
                    form.Families.Add(family);
                }
                familyKeys.Add(key,family);
            }
            section.Family=family;family.Occurrences.Add(section);form.Sections.Add(section);
        }
        return form;
    }
    static string Letters(int n)=>n<26?((char)('A'+n)).ToString():"A"+Letters(n-26);
    static double Similarity(double[] a,double[] b)
    {
        double dot=0,aa=0,bb=0;for(int i=0;i<12;i++){dot+=a[i]*b[i];aa+=a[i]*a[i];bb+=b[i]*b[i];}
        return aa==0 || bb==0?(aa==bb?1:0):dot/Math.Sqrt(aa*bb);
    }
    static readonly (string quality,int[] notes)[] Templates={
        ("",new[]{0,4,7}),("m",new[]{0,3,7}),("7",new[]{0,4,7,10}),
        ("maj7",new[]{0,4,7,11}),("m7",new[]{0,3,7,10}),("dim",new[]{0,3,6})};
    static ChordStep Infer(double[] weights,int bass)
    {
        double total=weights.Sum();if(total<.000001)return new ChordStep();
        int count=weights.Count(w=>w>total*.07);
        if(count<3)return new ChordStep{Root=Array.IndexOf(weights,weights.Max()),Quality=count==1?" tone":" dyad",Energy=(float)total};
        double best=double.NegativeInfinity;int root=0;string quality="";
        for(int r=0;r<12;r++)foreach(var template in Templates)
        {
            double covered=0;int missing=0;
            foreach(int interval in template.notes){double weight=weights[HarmonyModel.Mod(r+interval)];covered+=weight;if(weight<total*.055)missing++;}
            double score=covered/total-missing*.20-(template.notes.Length-3)*.055;
            if(weights[r]<total*.055)score-=.15;
            if(bass!=int.MaxValue && HarmonyModel.Mod(bass-21)==r)score+=.05;
            if(score>best){best=score;root=r;quality=template.quality;}
        }
        return new ChordStep{Root=root,Quality=quality,Energy=(float)total};
    }
}
