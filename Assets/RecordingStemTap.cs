using System;
using System.Collections.Concurrent;
using System.IO;
using UnityEngine;

// Captures the signal of the AudioSource it sits on while a video take records (RecordingSession), so the editor's
// package beside the video (Tools/Video/edit_package.py) has that sound on its own: the app's synth in a tutorial
// take, which exists in no file. It sits after the source's other filters (added last), so it hears what they
// produce, before the source's volume. The audio thread only copies blocks into a queue; the file is written when
// the take ends.
public sealed class RecordingStemTap : MonoBehaviour
{
    readonly ConcurrentQueue<float[]> blocks=new();
    volatile bool capturing;
    volatile int channels=2;
    public bool Capturing=>capturing;
    public void Begin(){while(blocks.TryDequeue(out _)){}capturing=true;}
    // Stop and write a 32-bit float WAV at the output rate; false when nothing was heard.
    public bool End(string path)
    {
        capturing=false;
        long count=0;foreach(var b in blocks)count+=b.Length;
        if(count==0)return false;
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        using var file=new BinaryWriter(File.Create(path));
        int rate=AudioSettings.outputSampleRate,ch=channels;long bytes=count*4;
        file.Write(new[]{(byte)'R',(byte)'I',(byte)'F',(byte)'F'});file.Write((uint)(36+bytes));
        file.Write(new[]{(byte)'W',(byte)'A',(byte)'V',(byte)'E',(byte)'f',(byte)'m',(byte)'t',(byte)' '});
        file.Write(16u);file.Write((ushort)3);file.Write((ushort)ch);file.Write((uint)rate);file.Write((uint)(rate*ch*4));file.Write((ushort)(ch*4));file.Write((ushort)32);
        file.Write(new[]{(byte)'d',(byte)'a',(byte)'t',(byte)'a'});file.Write((uint)bytes);
        var buffer=new byte[16384*4];
        while(blocks.TryDequeue(out var block))
        {
            for(int i=0;i<block.Length;)
            {
                int n=Math.Min(block.Length-i,buffer.Length/4);Buffer.BlockCopy(block,i*4,buffer,0,n*4);file.Write(buffer,0,n*4);i+=n;
            }
        }
        return true;
    }
    void OnAudioFilterRead(float[] data,int channels)
    {
        if(!capturing)return;
        this.channels=channels;var copy=new float[data.Length];Array.Copy(data,copy,data.Length);blocks.Enqueue(copy);
    }
    void OnDisable(){capturing=false;}
}
