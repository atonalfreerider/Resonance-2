using System;
using System.Collections.Generic;

// The first eight partials, folded into the nearest 12-TET pitch classes.
// This describes the visualization's excitation, not a measurement of the sine synth.
public static class HarmonicSpectrum
{
    public static void Accumulate(IEnumerable<Tuple<int,float>> notes, float[] energy, bool partials)
    {
        Array.Clear(energy,0,energy.Length);
        foreach(var note in notes)
        {
            for(int h=1;h<=(partials?8:1);h++)
            {
                int pc=HarmonyModel.Mod(note.Item1+(int)Math.Round(12*Math.Log(h,2)));
                energy[pc]+=Math.Max(0,note.Item2)/(float)Math.Pow(h,1.35);
            }
        }
        // Soft compression lets additional notes build energy without clipping to a constant.
        for(int i=0;i<12;i++)energy[i]=1-(float)Math.Exp(-energy[i]);
    }
}
