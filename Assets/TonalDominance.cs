using System.Linq;
using UnityEngine;

// Fundamental/triad evidence drives the global wash; upper partials cannot elect V.
[DefaultExecutionOrder(100)]
public sealed class TonalDominance : MonoBehaviour
{
    Main main;MidiPlayer midi;
    readonly float[] weights=new float[12];
    public Color Hue {get;private set;}=TonalColorField.Tonic;
    public float Energy {get;private set;}
    public float Influence=.82f;
    public Color Blend(Color local,float energy)=>Color.Lerp(local,Hue,Influence*Mathf.Clamp01(energy*3));
    void Awake(){main=GetComponent<Main>();midi=GetComponent<MidiPlayer>();}
    void Update()
    {
        System.Array.Clear(weights,0,12);float total=0;int bass=int.MaxValue;
        foreach(var n in main.ActiveNotes){weights[HarmonyModel.Mod(n.Item1)]+=n.Item2;total+=n.Item2;bass=Mathf.Min(bass,n.Item1);}
        float best=-1;int root=main.currentKey;bool minor=main.MinorMode;
        for(int pc=0;pc<12;pc++)for(int quality=0;quality<2;quality++)
        {
            int third=quality==0?4:3;
            float evidence=weights[pc]+.8f*weights[(pc+third)%12]+.65f*weights[(pc+7)%12];
            if(weights[pc]>0&&weights[(pc+third)%12]>0&&weights[(pc+7)%12]>0)evidence+=total*.35f;
            if(bass!=int.MaxValue&&pc==bass%12)evidence+=total*.18f;
            if(evidence>best){best=evidence;root=pc;minor=quality==1;}
        }
        if(midi!=null&&midi.IsPlaying&&midi.SongForm!=null)
        {
            double beat=midi.Cycles.BeatAt(midi.ScorePosition);
            var chord=midi.SongForm.Timeline.LastOrDefault(c=>c.Start<=beat&&c.End>beat);
            if(chord!=null&&!chord.Rest){root=chord.Root;minor=chord.Quality.StartsWith("m")&&!chord.Quality.StartsWith("maj");}
        }
        float dt=Time.unscaledDeltaTime;
        if(total>0)Hue=Color.Lerp(Hue,TonalColorField.Chord(root,main.currentKey,minor),1-Mathf.Exp(-dt*7));
        Energy=Mathf.Lerp(Energy,total*main.Synth.Volume/.6f,1-Mathf.Exp(-dt*(total>Energy?9:1.2f)));
    }
}
