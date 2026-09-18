using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;

// Timestamped snapshots preserve MIDI notes shorter than a rendered frame.
[RequireComponent(typeof(AudioSource))]
public class MusicSynth : MonoBehaviour
{
    sealed class Snapshot { public double Time; public float[] Amplitudes; public int Generation; }
    readonly ConcurrentQueue<Snapshot> queue = new();
    readonly float[] target = new float[96], envelope = new float[96];
    readonly double[] phase = new double[96], increment = new double[96];
    volatile int generation;
    int audioGeneration;
    public volatile float Volume = .6f;
    int rate;
    AudioClip carrier;
    void Awake()
    {
        rate = AudioSettings.outputSampleRate;
        for (int i = 0; i < 96; i++) increment[i] = 2 * Math.PI * 27.5 * Math.Pow(2, i / 12.0) / rate;
        var source = GetComponent<AudioSource>();
        source.spatialBlend = 0; source.playOnAwake = false; source.loop = true;
        carrier = AudioClip.Create("Synth carrier", rate, 1, rate, false);
        source.clip = carrier; source.Play();
    }
    public void ResetVoices() { generation++; }
    public void Schedule(double time, IEnumerable<Tuple<int, float>> notes)
    {
        var amps = new float[96];
        foreach (var n in notes) if (n.Item1 >= 0 && n.Item1 < 96) amps[n.Item1] = Mathf.Clamp01(n.Item2);
        queue.Enqueue(new Snapshot { Time = time, Amplitudes = amps, Generation = generation });
    }
    void OnAudioFilterRead(float[] data, int channels)
    {
        double start = AudioSettings.dspTime;
        for (int i = 0; i < data.Length; i += channels)
        {
            int currentGeneration = generation;
            if (audioGeneration != currentGeneration) { Array.Clear(target, 0, 96); audioGeneration = currentGeneration; }
            double now = start + (i / channels) / (double)rate;
            while (queue.TryPeek(out var snapshot))
            {
                if (snapshot.Generation != currentGeneration) { queue.TryDequeue(out _); continue; }
                if (snapshot.Time > now) break;
                if (queue.TryDequeue(out snapshot)) Array.Copy(snapshot.Amplitudes, target, 96);
            }
            double sample = 0;
            for (int n = 0; n < 96; n++)
            {
                envelope[n] += (target[n] - envelope[n]) * (target[n] > envelope[n] ? .004f : .001f);
                if (envelope[n] < .00001f) continue;
                sample += Math.Sin(phase[n]) * envelope[n] * .06;
                phase[n] += increment[n]; if (phase[n] > Math.PI * 2) phase[n] -= Math.PI * 2;
            }
            float value = (float)Math.Tanh(sample) * Volume;
            for (int c = 0; c < channels; c++) data[i + c] = value;
        }
    }
    void OnDisable() { ResetVoices(); }
    void OnDestroy() { if (carrier != null) Destroy(carrier); }
}
