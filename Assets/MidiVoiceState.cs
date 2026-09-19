using System;
using System.Collections.Generic;
using System.Linq;

// Same-note retriggers are FIFO. A release affects only its own channel.
public sealed class MidiVoiceState
{
    readonly Dictionary<(int channel, int note), List<float>> pressed = new();
    readonly Dictionary<(int channel, int note), float> sustained = new();
    readonly HashSet<int> pedal = new();
    readonly Dictionary<int,float> volume = new(), expression = new();
    public void NoteOn(int channel, int note, float velocity)
    {
        if (velocity <= 0) { NoteOff(channel, note); return; }
        var key = (channel, note);
        if (!pressed.TryGetValue(key, out var voices)) pressed[key] = voices = new List<float>();
        voices.Add(velocity);
    }
    public void NoteOff(int channel, int note)
    {
        var key = (channel, note);
        if (!pressed.TryGetValue(key, out var voices) || voices.Count == 0) return;
        if (pedal.Contains(channel)) sustained[key] = Math.Max(sustained.TryGetValue(key, out float v) ? v : 0, voices[0]);
        voices.RemoveAt(0); if (voices.Count == 0) pressed.Remove(key);
    }
    public void Control(int channel, int control, int value)
    {
        if(control==7)volume[channel]=Math.Clamp(value/127f,0,1);
        if(control==11)expression[channel]=Math.Clamp(value/127f,0,1);
        if (control == 64)
        {
            if (value >= 64) pedal.Add(channel);
            else { pedal.Remove(channel); foreach (var key in sustained.Keys.Where(k => k.channel == channel).ToArray()) sustained.Remove(key); }
        }
        if (control == 120 || control == 123)
        {
            foreach (var key in pressed.Keys.Where(k => k.channel == channel).ToArray())
            {
                if (control == 123 && pedal.Contains(channel)) sustained[key] = pressed[key].Max();
                pressed.Remove(key);
            }
            if (control == 120) foreach (var key in sustained.Keys.Where(k => k.channel == channel).ToArray()) sustained.Remove(key);
        }
        if (control == 121) { expression.Remove(channel); pedal.Remove(channel); foreach (var key in sustained.Keys.Where(k => k.channel == channel).ToArray()) sustained.Remove(key); }
    }
    float Gain(int channel)=>(volume.TryGetValue(channel,out float v)?v:1)*(expression.TryGetValue(channel,out float e)?e:1);
    public List<Tuple<int,float>> Snapshot() => pressed.Select(p => Tuple.Create(p.Key.note, p.Value.Max()*Gain(p.Key.channel)))
        .Concat(sustained.Select(p => Tuple.Create(p.Key.note, p.Value*Gain(p.Key.channel))))
        .GroupBy(p => p.Item1).Select(g => Tuple.Create(g.Key, g.Max(p => p.Item2))).OrderBy(p => p.Item1).ToList();
}
