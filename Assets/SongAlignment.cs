using System;
using System.Collections.Generic;
using System.Linq;

// All times are seconds. The recording is never stretched; only score time is mapped.
[Serializable]
public sealed class SongAlignment
{
    [Serializable] public struct Anchor { public double midi, audio; public Anchor(double m,double a){midi=m;audio=a;} }
    public List<Anchor> anchors=new();
    public double similarity;
    public string method="Unreviewed duration estimate";
    public double ToAudio(double midi)=>Map(midi,false);
    public double ToMidi(double audio)=>Map(audio,true);
    double Map(double time,bool inverse)
    {
        if(anchors.Count<2)return time;
        int lo=0,hi=anchors.Count-1;
        double X(int i)=>inverse?anchors[i].audio:anchors[i].midi;
        double Y(int i)=>inverse?anchors[i].midi:anchors[i].audio;
        while(hi-lo>1){int mid=(lo+hi)/2;if(X(mid)<=time)lo=mid;else hi=mid;}
        return Y(lo)+(time-X(lo))*(Y(hi)-Y(lo))/(X(hi)-X(lo));
    }
    public void Validate()
    {
        if(anchors.Count<2)throw new ArgumentException("At least two timing anchors are required.");
        for(int i=0;i<anchors.Count;i++)
        {
            var a=anchors[i];
            if(double.IsNaN(a.midi)||double.IsNaN(a.audio)||double.IsInfinity(a.midi)||double.IsInfinity(a.audio)||a.midi<0)
                throw new ArgumentException("Anchor times must be finite; score times must be nonnegative.");
            if(i>0 && (a.midi<=anchors[i-1].midi || a.audio<=anchors[i-1].audio))
                throw new ArgumentException("Both columns must increase strictly; timing cannot run backwards.");
        }
    }
    public static SongAlignment Estimate(double midi,double audio)=>new(){anchors=new(){new(0,0),new(midi,audio)}};

    // Banded dynamic time warping on normalized chroma. No Unity calls on worker thread.
    public static SongAlignment Analyze(float[] samples,int rate,float[][] score,double step,double midiDuration,double audioDuration)
    {
        int n=score.Length,m=(int)Math.Ceiling(audioDuration/step)+1;
        var recording=new float[m][];
        const int size=4096;
        var re=new double[size];var im=new double[size];
        for(int frame=0;frame<m;frame++)
        {
            int center=(int)(frame*step*rate);
            for(int k=0;k<size;k++){int ix=center+k-size/2;re[k]=(ix>=0&&ix<samples.Length?samples[ix]:0)*(.5-.5*Math.Cos(2*Math.PI*k/(size-1)));im[k]=0;}
            FFT(re,im);
            var chroma=new float[12];
            for(int note=40;note<=95;note++)
            {
                double hz=440*Math.Pow(2,(note-69)/12.0);int bin=(int)Math.Round(hz*size/rate);
                double energy=0;
                for(int b=Math.Max(1,bin-1);b<=Math.Min(size/2-1,bin+1);b++)energy+=re[b]*re[b]+im[b]*im[b];
                chroma[(note-21)%12]+=(float)Math.Sqrt(energy);
            }
            Normalize(chroma);recording[frame]=chroma;
        }
        foreach(var c in score)Normalize(c);
        var path=new byte[n*m];var prev=new double[m];var row=new double[m];
        Array.Fill(prev,double.PositiveInfinity);
        for(int i=0;i<n;i++)
        {
            Array.Fill(row,double.PositiveInfinity);
            int center=(int)(i*(m-1.0)/(n-1)),band=Math.Max(30,m/5);
            for(int j=Math.Max(0,center-band);j<=Math.Min(m-1,center+band);j++)
            {
                double dot=0;for(int p=0;p<12;p++)dot+=score[i][p]*recording[j][p];
                double cost=1-dot;
                if(i==0&&j==0){row[j]=cost;continue;}
                double diagonal=i>0&&j>0?prev[j-1]:double.PositiveInfinity;
                double up=i>0?prev[j]+.08:double.PositiveInfinity;
                double left=j>0?row[j-1]+.08:double.PositiveInfinity;
                byte direction=diagonal<=up&&diagonal<=left?(byte)1:up<=left?(byte)2:(byte)3;
                row[j]=cost+(direction==1?diagonal:direction==2?up:left);path[i*m+j]=direction;
            }
            (prev,row)=(row,prev);
        }
        var pairs=new List<Anchor>();int x=n-1,y=m-1;double similarity=0;int count=0;
        while(x>0||y>0)
        {
            pairs.Add(new Anchor(Math.Min(midiDuration,x*step),Math.Min(audioDuration,y*step)));
            for(int p=0;p<12;p++)similarity+=score[x][p]*recording[y][p];count++;
            byte d=path[x*m+y];if(d==1){x--;y--;}else if(d==2)x--;else if(d==3)y--;else break;
        }
        pairs.Reverse();var result=new SongAlignment{similarity=similarity/Math.Max(1,count),method="Automatic chroma alignment — review timing anchors"};
        result.anchors.Add(new Anchor(0,0));
        foreach(var a in pairs)
        {
            var last=result.anchors.Last();
            if(a.midi-last.midi>=.5 && a.audio-last.audio>=.1 && a.midi<midiDuration-.2 && a.audio<audioDuration-.2)result.anchors.Add(a);
        }
        result.anchors.Add(new Anchor(midiDuration,audioDuration));result.Validate();return result;
    }
    static void Normalize(float[] a){double norm=Math.Sqrt(a.Sum(v=>(double)v*v));if(norm>1e-8)for(int i=0;i<a.Length;i++)a[i]/=(float)norm;}
    static void FFT(double[] re,double[] im)
    {
        int n=re.Length;
        for(int i=1,j=0;i<n;i++){int bit=n>>1;for(; (j&bit)!=0;bit>>=1)j^=bit;j^=bit;if(i<j){(re[i],re[j])=(re[j],re[i]);(im[i],im[j])=(im[j],im[i]);}}
        for(int len=2;len<=n;len<<=1)
        {
            double angle=-2*Math.PI/len,wr0=Math.Cos(angle),wi0=Math.Sin(angle);
            for(int i=0;i<n;i+=len){double wr=1,wi=0;for(int j=0;j<len/2;j++){int a=i+j,b=a+len/2;double tr=wr*re[b]-wi*im[b],ti=wr*im[b]+wi*re[b];re[b]=re[a]-tr;im[b]=im[a]-ti;re[a]+=tr;im[a]+=ti;double next=wr*wr0-wi*wi0;wi=wr*wi0+wi*wr0;wr=next;}}
        }
    }
}
