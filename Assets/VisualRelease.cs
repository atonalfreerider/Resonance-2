using System;

// A retriggerable visual envelope. Audio note-off remains immediate.
public sealed class VisualRelease
{
    public float Level { get; private set; }
    public bool Held { get; private set; }
    float start,elapsed;
    public void Set(float value)
    {
        if(value>0){Level=value;Held=true;elapsed=0;start=value;}
        else if(Held){Held=false;start=Level;elapsed=0;}
    }
    public void Advance(float delta,float seconds)
    {
        if(Held)return;
        elapsed+=Math.Max(0,delta);
        float remaining=Math.Max(0,1-elapsed/Math.Max(.01f,seconds));
        Level=start*remaining*remaining;
    }
    public void Clear(){Level=start=elapsed=0;Held=false;}
}
