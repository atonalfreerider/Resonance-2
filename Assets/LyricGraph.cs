using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

// The lyric graph: the words' own structure, not the music's. Each stanza is a column headed by
// its name (verse, rap, chorus…) and its rhyme scheme; each line a row of its words in reading
// order. The links PatternPrep found are drawn between the words themselves:
//  • end rhymes as arcs on the right, from line end to line end, lettered;
//  • line openings that echo (repeated words, head rhyme, alliteration) as arcs on the left;
//  • internal rhymes as dips under the words that rhyme;
//  • refrains (a line heard before) marked at the left with how many times.
// The word being heard is lit and the links of the line being heard glow; the columns slide to
// keep the stanza being heard in view. Ink only: colour here would read as harmony.
// The words and links paint into the graph's own element, repainted only when what is lit or
// where the columns sit changes; the glow of the lit word and links goes to the panel's bloom
// every frame (positions only, no text).
public sealed class LyricGraph
{
    PreparedPatternSong.LyricSheet lyrics;
    sealed class Word {public string Text="";public int First,Last,Line;public float X,Width;public double Start,End;}
    sealed class Row {public int Line,Index;public Word[] Words=Array.Empty<Word>();public float Width;public string Letter="",Mark="";}
    sealed class Column {public string Name="",Scheme="";public float NameWidth;public Row[] Rows=Array.Empty<Row>();public float Width;}
    sealed class Link {public Word A,B;public string Kind="";public int Column;}
    Column[] columns=Array.Empty<Column>();Link[] links=Array.Empty<Link>();
    Word[] wordOf=Array.Empty<Word>();int[] columnOfLine=Array.Empty<int>();int[] rowOfLine=Array.Empty<int>();double[] starts=Array.Empty<double>();
    float scroll=-1;
    // For validation: the word lit now and the links glowing.
    public string LitWord {get;private set;}="";
    public string Stanza {get;private set;}="";
    public int LitLinks {get;private set;}
    public int Columns=>columns.Length;
    public int LinkCount=>links.Length;

    // Widths in font-size units, measured from the panel's sans face as painted by DrawText.
    static float Width(string text)
    {
        float w=0;
        foreach(char c in text)w+=c is 'i' or 'l' or 'j' or '\'' or '.' or ',' or '!' or '|' or ':' or ';'?.17f:c is 't' or 'f' or 'r' or 'I'?.23f:c is 'm' or 'w'?.5f:c is 'M' or 'W'?.54f:char.IsUpper(c)?.4f:c==' '?.17f:.33f;
        return w;
    }
    public void Load(PreparedPatternSong data)
    {
        lyrics=data?.Lyrics;columns=Array.Empty<Column>();links=Array.Empty<Link>();scroll=-1;LitWord="";painted=default;Element.MarkDirtyRepaint();
        if(lyrics?.Syllables==null||lyrics.Syllables.Length==0)return;
        var syl=lyrics.Syllables;wordOf=new Word[syl.Length];columnOfLine=Enumerable.Repeat(-1,lyrics.Lines.Length).ToArray();rowOfLine=new int[lyrics.Lines.Length];
        var seen=new Dictionary<string,int>();var list=new List<Column>();
        foreach(var st in lyrics.Stanzas)
        {
            var rows=new List<Row>();
            for(int li=st.FirstLine;li<st.FirstLine+st.Lines;li++)
            {
                var line=lyrics.Lines[li];if(line.Count==0)continue;
                var words=new List<Word>();float x=0;
                for(int i=line.First;i<line.First+line.Count;)
                {
                    int j=i;while(j+1<line.First+line.Count&&syl[j+1].WordIndex==syl[i].WordIndex)j++;
                    var w=new Word{Text=syl[i].Word,First=i,Last=j,Line=li,X=x,Start=syl[i].Start,End=syl[j].End};w.Width=Width(w.Text);x+=w.Width+.3f;
                    for(int k=i;k<=j;k++)wordOf[k]=w;words.Add(w);i=j+1;
                }
                string norm=new string(line.Text.ToLowerInvariant().Where(char.IsLetter).ToArray());
                seen.TryGetValue(norm,out int heard);seen[norm]=heard+1;
                columnOfLine[li]=list.Count;rowOfLine[li]=rows.Count;
                // A line heard before is a refrain: marked with how many times it has been heard.
                string mark=norm.Length>6&&heard>0?"↺"+(heard>1?heard.ToString():""):"";
                rows.Add(new Row{Line=li,Index=rows.Count,Words=words.ToArray(),Width=x-.3f,Letter=line.Letter,Mark=mark});
            }
            if(rows.Count==0)continue;
            string name=st.Name.ToUpperInvariant();
            list.Add(new Column{Name=name,Scheme=st.Scheme,NameWidth=Width(name),Rows=rows.ToArray(),Width=rows.Max(r=>r.Width)});
        }
        columns=list.ToArray();
        links=(lyrics.Links??Array.Empty<PreparedPatternSong.RhymeLink>()).Where(l=>l.Kind!="repeat"&&l.A<wordOf.Length&&l.B<wordOf.Length&&wordOf[l.A]!=null&&wordOf[l.B]!=null)
            .Select(l=>new Link{A=wordOf[l.A],B=wordOf[l.B],Kind=l.Kind,Column=columnOfLine[wordOf[l.A].Line]}).Where(l=>l.Column>=0&&l.Column==columnOfLine[l.B.Line]).ToArray();
        starts=syl.Select(s=>s.Start).ToArray();
    }
    static Color Ink(float a)=>FormHatch.Ink(a);
    static Color Label(float a)=>FormHatch.Label(a);

    public readonly VisualElement Element;
    Rect area,placed;double beat;Word word;bool heard;int line,current;
    (Word word,bool heard,int line,int current,float scroll,Vector2 size) painted;
    public LyricGraph()
    {
        Element=new VisualElement{name="lyric-graph",pickingMode=PickingMode.Ignore};Element.style.position=Position.Absolute;
        Element.generateVisualContent+=ctx=>Walk(ctx,ctx.painter2D,null);
    }
    // Every frame, outside the draw pass (where layout and repaint requests take effect): show
    // the element where the panel last put the graph, work out what is lit and where the columns
    // sit, and repaint only if either changed.
    public void Tick(bool shown,double beat)
    {
        using var perf=Perf.Board.Auto();
        var display=shown&&columns.Length>0?DisplayStyle.Flex:DisplayStyle.None;
        if(Element.style.display!=display)Element.style.display=display;
        LitWord="";
        if(display==DisplayStyle.None||area.width<1)return;
        if(placed!=area){placed=area;Element.style.left=area.x;Element.style.top=area.y;Element.style.width=area.width;Element.style.height=area.height;}
        this.beat=beat;
        // The syllable being heard (or the last one heard), its word, line and column.
        int lo=0,hi=starts.Length;while(lo<hi){int mid=(lo+hi)/2;if(starts[mid]<=beat)lo=mid+1;else hi=mid;}
        word=wordOf[Mathf.Max(0,lo-1)];
        heard=word!=null&&beat>=word.Start&&beat<Math.Max(word.End,word.Start+.25)+.1;
        line=word?.Line??0;current=Mathf.Max(0,columnOfLine[line]);
        if(heard)LitWord=word.Text;
        Stanza=columns[current].Name;
        // As many columns as fit; the current one second from the left, sliding when it changes.
        int fit=Fit;
        float target=Mathf.Clamp(current-(fit>=3?1:0),0,Math.Max(0,columns.Length-fit));
        scroll=scroll<0||Main.ReducedMotion?target:Mathf.Abs(scroll-target)<.002f?target:Mathf.Lerp(scroll,target,1-Mathf.Exp(-Time.unscaledDeltaTime*5));
        var now=(word,heard,line,current,scroll,area.size);
        if(!now.Equals(painted)){painted=now;Element.MarkDirtyRepaint();}
    }
    // In the panel's draw: remember where the graph goes and add the glow of what is lit.
    public void Draw(OrreryBloom bloom,Rect area)
    {
        this.area=area;LitLinks=0;
        if(columns.Length==0||scroll<0)return;
        Walk(null,null,bloom);
    }
    int Fit=>Mathf.Clamp(Mathf.FloorToInt(area.width/280),1,Math.Max(1,columns.Length));
    // One layout for both passes: painting (ctx, in the element's own coordinates) or glowing (bloom, in the panel's).
    void Walk(MeshGenerationContext ctx,Painter2D p,OrreryBloom bloom)
    {
        if(columns.Length==0||area.width<1)return;
        int fit=Fit;float width=area.width/fit;var origin=bloom!=null?area.position:Vector2.zero;
        int firstColumn=Mathf.Max(0,Mathf.FloorToInt(scroll)),lastColumn=Mathf.Min(columns.Length-1,Mathf.CeilToInt(scroll+fit));
        for(int c=firstColumn;c<=lastColumn;c++)
        {
            float x0=(c-scroll)*width;
            // A column sliding past either edge fades out before it can cross the vocal wheel.
            fade=Mathf.Clamp01(Mathf.Min(1+x0/(width*.3f),1-(x0+width-area.width)/(width*.3f)));
            if(fade<.02f)continue;
            DrawColumn(ctx,p,bloom,origin,c,x0,width,c==current);
        }
    }
    float fade=1;
    Color A(Color c)=>new(c.r,c.g,c.b,c.a*fade);
    void DrawColumn(MeshGenerationContext ctx,Painter2D p,OrreryBloom bloom,Vector2 origin,int c,float x0,float width,bool here)
    {
        var col=columns[c];bool paint=ctx!=null;
        float size=Mathf.Clamp(Mathf.Min((width-70)/Math.Max(1,col.Width+.6f),(area.height-40)/(col.Rows.Length*1.5f)),10,28);
        float left=x0+30,top=34,row=size*1.5f,right=left+col.Width*size;
        // Header: the stanza's name and its rhyme scheme.
        if(paint)
        {
            ctx.DrawText(col.Name,new Vector2(left,4),14,A(here?Color.white:Label(.55f)));
            ctx.DrawText(col.Scheme,new Vector2(left+col.NameWidth*14+12,6),12,A(Label(here?.7f:.4f)));
            if(here){p.strokeColor=A(Label(.8f));p.lineWidth=2;p.BeginPath();p.MoveTo(new Vector2(left,24));p.LineTo(new Vector2(left+Mathf.Max(30,col.NameWidth*14),24));p.Stroke();}
        }
        float Y(Row r)=>top+r.Index*row;
        float WordX(Word w)=>left+w.X*size;
        // Links under the words.
        foreach(var l in links)
        {
            if(l.Column!=c)continue;
            var ra=col.Rows[rowOfLine[l.A.Line]];var rb=col.Rows[rowOfLine[l.B.Line]];
            bool lit=here&&(l.A.Line==line||l.B.Line==line);
            if(!paint){if(!lit)continue;LitLinks++;}
            else{p.strokeColor=A(lit?Label(.95f):Ink(l.Kind=="internal"?.3f:.4f));p.lineWidth=lit?2:1.2f;p.BeginPath();}
            if(l.Kind=="end")
            {
                // Line end to line end, bulging right past the longest line.
                float ya=Y(ra)+size*.55f,yb=Y(rb)+size*.55f,bulge=right+10+Mathf.Abs(yb-ya)*.12f;
                var a=new Vector2(WordX(l.A)+l.A.Width*size+4,ya);var b=new Vector2(WordX(l.B)+l.B.Width*size+4,yb);
                var ca=new Vector2(bulge,ya);var cb=new Vector2(bulge,yb);
                if(paint){p.MoveTo(a);p.BezierCurveTo(ca,cb,b);p.Stroke();}
                else for(int s=0;s<12;s++)bloom.Line(origin+Bez(a,ca,cb,b,s/12f),origin+Bez(a,ca,cb,b,(s+1)/12f),2,Color.white,.1f*fade);
            }
            else if(l.Kind=="front")
            {
                float ya=Y(ra)+size*.55f,yb=Y(rb)+size*.55f,bulge=8+Mathf.Abs(yb-ya)*.12f;
                if(!paint)continue;
                var a=new Vector2(left-4,ya);var b=new Vector2(left-4,yb);
                p.MoveTo(a);p.BezierCurveTo(new Vector2(left-4-bulge,ya),new Vector2(left-4-bulge,yb),b);p.Stroke();
            }
            else
            {
                // An internal rhyme dips under the two words.
                if(!paint)continue;
                var a=new Vector2(WordX(l.A)+l.A.Width*size*.5f,Y(ra)+size*.95f);var b=new Vector2(WordX(l.B)+l.B.Width*size*.5f,Y(rb)+size*.95f);
                float dip=size*.3f+Mathf.Abs(b.x-a.x)*.03f;
                p.MoveTo(a);p.BezierCurveTo(a+new Vector2(0,dip),b+new Vector2(0,dip),b);p.Stroke();
            }
        }
        // Rows: refrain mark, words, rhyme letter; the glow pass only underlines the lit word.
        if(!paint)
        {
            if(here&&heard&&word!=null&&columnOfLine[word.Line]==c)
            {
                float y=Y(col.Rows[rowOfLine[word.Line]]);
                bloom.Line(origin+new Vector2(WordX(word),y+size*1.02f),origin+new Vector2(WordX(word)+word.Width*size*1.1f,y+size*1.02f),2.5f,Color.white,.35f*fade);
            }
            return;
        }
        foreach(var r in col.Rows)
        {
            float y=Y(r);bool now=here&&r.Line==line;
            if(now){p.fillColor=A(Ink(.08f));p.BeginPath();p.MoveTo(new Vector2(x0+4,y-3));p.LineTo(new Vector2(x0+width-6,y-3));p.LineTo(new Vector2(x0+width-6,y+row-6));p.LineTo(new Vector2(x0+4,y+row-6));p.ClosePath();p.Fill();}
            if(r.Mark.Length>0)ctx.DrawText(r.Mark,new Vector2(x0+6,y+size*.1f),size*.7f,A(Label(.45f)));
            foreach(var w in r.Words)
            {
                bool lit=now&&heard&&w==word;bool past=here&&(r.Line<line||r.Line==line&&w.End<=beat);
                var color=lit?Color.white:past?Label(.42f):Label(here?.9f:.6f);
                ctx.DrawText(w.Text,new Vector2(WordX(w),y),lit?size*1.1f:size,A(color));
            }
            if(r.Letter.Length>0)ctx.DrawText(r.Letter,new Vector2(right+34,y+size*.1f),size*.8f,A(Label(now?.95f:.45f)));
        }
    }
    static Vector2 Bez(Vector2 a,Vector2 b,Vector2 c,Vector2 d,float t){float u=1-t;return u*u*u*a+3*u*u*t*b+3*u*t*t*c+t*t*t*d;}
}
