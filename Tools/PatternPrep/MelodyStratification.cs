// Offline, pitch-ordered voice assignment. Simultaneous score onsets form a chord;
// original onset/duration remain exact. Missing voices use nearest-register continuity.
public static class MelodyStratification
{
    public static void Build(PreparedPatternSong song,Func<double,double> seconds)
    {
        var output=new List<PreparedPatternSong.MelodyStrand>();
        foreach(var lane in song.Notes.Where(n=>n.Channel!=10).GroupBy(n=>(n.Track,n.Channel)))
        {
            var groups=new List<List<PreparedPatternSong.MelodyNote>>();
            foreach(var n in lane.OrderBy(n=>n.Beat).ThenBy(n=>n.Pitch)){
                var note=new PreparedPatternSong.MelodyNote{Pitch=n.Pitch,Start=seconds(n.Beat),End=seconds(n.Beat+n.Length),Velocity=n.Velocity};
                if(groups.Count==0||note.Start-groups[^1][0].Start>.000001)groups.Add(new());
                groups[^1].Add(note);
            }
            int count=groups.Max(g=>g.Count);
            var strands=Enumerable.Range(0,count).Select(_=>new List<PreparedPatternSong.MelodyNote>()).ToArray();
            var full=groups.Where(g=>g.Count==count).Select(g=>g.OrderBy(n=>n.Pitch).ToArray()).ToArray();
            var previous=Enumerable.Range(0,count).Select(i=>full.Average(g=>g[i].Pitch)).ToArray();
            foreach(var group in groups){
                var notes=group.OrderBy(n=>n.Pitch).ToArray();int m=notes.Length;
                // Dynamic programming chooses an ordered subset of voice slots, never
                // weaving a simultaneous low note through a higher harmony voice.
                var cost=new double[m+1,count+1];var take=new bool[m+1,count+1];
                for(int i=1;i<=m;i++)cost[i,0]=double.PositiveInfinity;
                for(int j=1;j<=count;j++)for(int i=1;i<=m;i++){
                    double assigned=cost[i-1,j-1]+Math.Abs(notes[i-1].Pitch-previous[j-1]);
                    cost[i,j]=cost[i,j-1];if(assigned<=cost[i,j]){cost[i,j]=assigned;take[i,j]=true;}
                }
                for(int i=m,j=count;i>0&&j>0;j--)if(take[i,j]){strands[j-1].Add(notes[i-1]);previous[j-1]=notes[i-1].Pitch;i--;}
            }
            for(int i=0;i<count;i++)if(strands[i].Count>0)output.Add(new(){Track=lane.Key.Track,Channel=lane.Key.Channel,Rank=i,Notes=strands[i].OrderBy(n=>n.Start).ToArray()});
        }
        song.MelodyStrands=output.ToArray();
        if(output.Sum(s=>s.Notes.Length)!=song.Notes.Count(n=>n.Channel!=10))throw new Exception("Voice assignment lost notes");
    }
}
