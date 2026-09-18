using System;
using System.Collections.Generic;

// Exact integration of dE/dt = input - decay*E. Independent of rendering frame rate.
public sealed class HarmonicMemory
{
    public readonly float[] Energy = new float[12];
    readonly float[] input = new float[12];
    public void Clear() => Array.Clear(Energy,0,Energy.Length);
    public void Advance(IEnumerable<Tuple<int,float>> notes,double seconds,float halfLife,float gain,bool partials)
    {
        if(seconds<=0)return;
        HarmonicSpectrum.Accumulate(notes,input,partials,false);
        double decay=Math.Log(2)/Math.Max(.05,halfLife), retain=Math.Exp(-decay*seconds);
        for(int i=0;i<12;i++)Energy[i]=(float)(Energy[i]*retain+input[i]*Math.Max(0,gain)*(1-retain)/decay);
    }
}
