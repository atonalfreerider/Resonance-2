using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

// The lyric graph: the words' own structure, not the music's. Each stanza is a column headed by
// its name (verse, rap, chorus…) and its rhyme scheme; each line a row of its words, set large
// and right-aligned so the line ends, where the end rhymes fall, stand in one column.
// Rhymes come from LyricRhymes (worked out when the song loads): perfect, slant and imperfect.
//  • Colour is the vowel, on one wheel for every song (LyricRhymes.Hue): rhyming words share a
//    hue region; family members are vivid, lone stressed words soft, weak words uncoloured.
//  • No lines join rhymes: the shared hue does, across a line and into its neighbours.
//  • The type is sized so about four lines are in view; a long line wraps, still flush right.
//  • A vowel wheel beside the words (long vowels outside, short inside) lights the regions
//    rhyming now. Refrains are marked ↺.
// The column being heard scrolls to keep its line second from the top; the word being heard is
// lit and underlined in the panel's bloom. The words paint into the graph's element only when
// what is lit or where things sit changes.
public sealed class LyricGraph
{
    PreparedPatternSong.LyricSheet lyrics;
    sealed class Word {public string Text="";public int First,Last,Line,Family=-1;public float X,Width,Hue,Shade,LX,LY;public bool Weak;public double Start,End;}
    sealed class Row {public int Line,Index;public Word[] Words=Array.Empty<Word>();public float Width,Offset,Top,Height;public string Letter="",Mark="";public int EndFamily=-1;}
    sealed class Column {public string Name="",Scheme="";public float NameWidth;public Row[] Rows=Array.Empty<Row>();public float Width,Size=16,Total,LaidWidth=-1,LaidHeight=-1;}
    sealed class Link {public Word A,B;public string Kind="";public int Column;public LyricRhymes.Grade Grade;}
    Column[] columns=Array.Empty<Column>();Link[] links=Array.Empty<Link>();
    Word[] wordOf=Array.Empty<Word>();int[] columnOfLine=Array.Empty<int>();int[] rowOfLine=Array.Empty<int>();double[] starts=Array.Empty<double>();
    float scroll=-1,rowScroll;
    // For validation: the word lit now and the links glowing.
    public string LitWord {get;private set;}="";
    public string Stanza {get;private set;}="";
    public int LitLinks {get;private set;}
    public int Columns=>columns.Length;
    public int LinkCount=>links.Length;
    public int Families {get;private set;}

    // A word's colour is its vowel's place on the colour wheel (LyricRhymes.Hue): full strength
    // in a rhyme family, soft for a stressed word that rhymes with nothing near it, none for weak words.
    static Color VowelColor(Word w,bool strong)=>Color.HSVToRGB(w.Hue,strong?.72f:.32f,strong?1f-.14f*w.Shade:.86f);
    static bool Coloured(Word w)=>w.Family>=0||!w.Weak;

    // Widths in font-size units, measured from the panel's sans face as painted by DrawText.
    static float Width(string text)
    {
        float w=0;
        foreach(char c in text)w+=c is 'i' or 'l' or 'j' or '\'' or '.' or ',' or '!' or '|' or ':' or ';'?.17f:c is 't' or 'f' or 'r' or 'I'?.23f:c is 'm' or 'w'?.5f:c is 'M' or 'W'?.54f:char.IsUpper(c)?.4f:c==' '?.17f:.33f;
        return w;
    }
    public void Load(PreparedPatternSong data)
    {
        activeLine=-1;activeHues.Clear();
        lyrics=data?.Lyrics;columns=Array.Empty<Column>();links=Array.Empty<Link>();scroll=-1;rowScroll=0;LitWord="";Families=0;painted=default;Element.MarkDirtyRepaint();
        if(lyrics?.Syllables==null||lyrics.Syllables.Length==0)return;
        var syl=lyrics.Syllables;wordOf=new Word[syl.Length];columnOfLine=Enumerable.Repeat(-1,lyrics.Lines.Length).ToArray();rowOfLine=new int[lyrics.Lines.Length];
        var seen=new Dictionary<string,int>();var list=new List<Column>();var all=new List<Word>();
        var input=new List<(int,int,string,string[],int[])>();
        for(int s=0;s<lyrics.Stanzas.Length;s++)
        {
            var st=lyrics.Stanzas[s];var rows=new List<Row>();
            for(int li=st.FirstLine;li<st.FirstLine+st.Lines;li++)
            {
                var line=lyrics.Lines[li];if(line.Count==0)continue;
                var words=new List<Word>();float x=0;
                for(int i=line.First;i<line.First+line.Count;)
                {
                    int j=i;while(j+1<line.First+line.Count&&syl[j+1].WordIndex==syl[i].WordIndex)j++;
                    var w=new Word{Text=syl[i].Word,First=i,Last=j,Line=li,X=x,Start=syl[i].Start,End=syl[j].End};w.Width=Width(w.Text);x+=w.Width+.3f;
                    for(int k=i;k<=j;k++)wordOf[k]=w;words.Add(w);all.Add(w);
                    input.Add((li,s,w.Text,Enumerable.Range(i,j-i+1).Select(k=>syl[k].Text).ToArray(),Enumerable.Range(i,j-i+1).Select(k=>syl[k].Stress).ToArray()));
                    i=j+1;
                }
                string norm=new string(line.Text.ToLowerInvariant().Where(char.IsLetter).ToArray());
                seen.TryGetValue(norm,out int heard);seen[norm]=heard+1;
                columnOfLine[li]=list.Count;rowOfLine[li]=rows.Count;
                // A line heard before is a refrain: marked with how many times it has been heard.
                string mark=norm.Length>6&&heard>0?"↺"+(heard>1?heard.ToString():""):"";
                rows.Add(new Row{Line=li,Index=rows.Count,Words=words.ToArray(),Width=x-.3f,Letter=line.Letter,Mark=mark});
            }
            if(rows.Count==0)continue;
            string name=st.Name.ToUpperInvariant();float widest=rows.Max(r=>r.Width);
            foreach(var r in rows)r.Offset=widest-r.Width;
            list.Add(new Column{Name=name,Scheme=st.Scheme,NameWidth=Width(name),Rows=rows.ToArray(),Width=widest});
        }
        columns=list.ToArray();
        // Rhyme families and their links; the echoing line openings come from PatternPrep.
        var rhymes=LyricRhymes.Analyse(input,lyrics.Lines.Length);Families=rhymes.Families;
        for(int i=0;i<all.Count;i++){var r=rhymes.Words[i];all[i].Family=r.Family;all[i].Hue=r.Hue;all[i].Shade=r.Shade;all[i].Weak=r.Weak;}
        foreach(var c in columns)foreach(var r in c.Rows)if(r.Words.Length>0)r.EndFamily=r.Words[^1].Family;
        var made=rhymes.Links.Select(l=>new Link{A=all[l.A],B=all[l.B],Kind=l.End?"end":"internal",Grade=l.Grade,Column=columnOfLine[all[l.A].Line]});
        var fronts=(lyrics.Links??Array.Empty<PreparedPatternSong.RhymeLink>()).Where(l=>l.Kind=="front"&&l.A<wordOf.Length&&l.B<wordOf.Length&&wordOf[l.A]!=null&&wordOf[l.B]!=null)
            .Select(l=>new Link{A=wordOf[l.A],B=wordOf[l.B],Kind="front",Column=columnOfLine[wordOf[l.A].Line]});
        links=made.Concat(fronts).Where(l=>l.Column>=0&&l.Column==columnOfLine[l.B.Line]).ToArray();
        starts=syl.Select(s=>s.Start).ToArray();
    }
    static Color Ink(float a)=>FormHatch.Ink(a);
    static Color Label(float a)=>FormHatch.Label(a);

    public readonly VisualElement Element;
    // The square the panel keeps at the top left for the vocal wheel (graph pixels).
    public float LeftColumn;
    // Where the vowel wheel goes, as fractions of the panel: the left column under the vocal wheel.
    public Rect WheelArea;
    Rect area,placed;double beat;Word word;bool heard;int line,current;
    (Word word,bool heard,int line,int current,float scroll,float rows,Vector2 size,float left) painted;
    public LyricGraph()
    {
        Element=new VisualElement{name="lyric-graph",pickingMode=PickingMode.Ignore};Element.style.position=Position.Absolute;Element.style.overflow=Overflow.Hidden;
        Element.generateVisualContent+=ctx=>Walk(ctx,ctx.painter2D,null);
        WheelElement.generateVisualContent+=DrawWheel;
    }
    const float Top=40,LineGap=.3f;
    float TextX=>LeftColumn+10;
    int Fit=>columns.Length==0?1:Mathf.Clamp(Mathf.FloorToInt((area.width-TextX)/700),1,Math.Min(2,columns.Length));

    // Layout: the size is the largest at which about four lyric lines fill the height, a line
    // too long for the width wrapping onto a second row; every row is right-aligned, so the line
    // ends (where end rhymes fall) stand in one column. Worked out once per column, width and height.
    void Lay(Column col,float width)
    {
        float height=area.height-Top;
        if(col.LaidWidth==width&&col.LaidHeight==height)return;
        col.LaidWidth=width;col.LaidHeight=height;
        float usable=width-70;
        int RowsAt(Row r,float s){int rows=1;float x=0;foreach(var w in r.Words){float ww=w.Width*s;if(x>0&&x+ww>usable){rows++;x=0;}x+=ww+.3f*s;}return rows;}
        float size=16;
        for(float s=64;s>=16;s-=1)
        {
            float rows=(float)col.Rows.Average(r=>RowsAt(r,s));
            if(4*(rows*1.5f*s+LineGap*s)<=height){size=s;break;}
        }
        col.Size=size;float y=0,right=usable;
        foreach(var r in col.Rows)
        {
            r.Top=y;
            // Wrap into segments, each set flush right: as many as the width needs, balanced so a
            // wrapped line splits into even parts rather than leaving a word alone.
            List<List<Word>> Wrap(float limit){var segs=new List<List<Word>>{new()};float x=0;
                foreach(var w in r.Words){float ww=w.Width*size;if(x>0&&x+ww>limit){segs.Add(new());x=0;}segs[^1].Add(w);x+=ww+.3f*size;}return segs;}
            var segments=Wrap(usable);
            if(segments.Count>1)
            {
                float total=r.Words.Sum(w=>w.Width*size)+.3f*size*(r.Words.Length-1);
                for(float slack=1.02f;slack<1.6f;slack+=.06f){var even=Wrap(Mathf.Min(usable,total/segments.Count*slack));if(even.Count==segments.Count){segments=even;break;}}
            }
            for(int si=0;si<segments.Count;si++)
            {
                var seg=segments[si];float segWidth=seg.Sum(w=>w.Width*size)+.3f*size*(seg.Count-1),at=right-segWidth;
                foreach(var w in seg){w.LX=at;w.LY=y+si*1.5f*size;at+=w.Width*size+.3f*size;}
            }
            r.Height=segments.Count*1.5f*size+LineGap*size;y+=r.Height;
        }
        col.Total=y;
    }
    // Every frame, outside the draw pass (where layout and repaint requests take effect): show
    // the element where the panel last put the graph, work out what is lit and where the columns
    // sit, and repaint only if either changed.
    public void Tick(bool shown,double beat)
    {
        using var perf=Perf.Board.Auto();
        var display=shown&&columns.Length>0?DisplayStyle.Flex:DisplayStyle.None;
        if(Element.style.display!=display){Element.style.display=display;WheelElement.style.display=display;}
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
        int fit=Fit;
        float target=Mathf.Clamp(current-(fit>=2?0:0),0,Math.Max(0,columns.Length-fit));
        float blend=1-Mathf.Exp(-Time.unscaledDeltaTime*5);
        scroll=scroll<0||Main.ReducedMotion?target:Mathf.Abs(scroll-target)<.002f?target:Mathf.Lerp(scroll,target,blend);
        // Within the column, keep the line being heard second from the top.
        var col=columns[current];Lay(col,(area.width-TextX)/fit);
        var here=col.Rows[rowOfLine[line]];int index=here.Index;
        float rowTarget=Mathf.Clamp(index>0?col.Rows[index-1].Top:0,0,Mathf.Max(0,col.Total-(area.height-Top)));
        rowScroll=Main.ReducedMotion?rowTarget:Mathf.Abs(rowScroll-rowTarget)<.5f?rowTarget:Mathf.Lerp(rowScroll,rowTarget,blend);
        if(line!=activeLine){activeLine=line;FindActiveHues();WheelElement.MarkDirtyRepaint();}
        var now=(word,heard,line,current,scroll,rowScroll,area.size,LeftColumn);
        if(!now.Equals(painted)){painted=now;Element.MarkDirtyRepaint();}
    }
    // In the panel's draw: remember where the graph goes and add the glow of what is lit.
    public void Draw(OrreryBloom bloom,Rect area)
    {
        this.area=area;LitLinks=0;
        if(columns.Length==0||scroll<0)return;
        Walk(null,null,bloom);
    }
    // One layout for both passes: painting (ctx, in the element's own coordinates) or glowing (bloom, in the panel's).
    void Walk(MeshGenerationContext ctx,Painter2D p,OrreryBloom bloom)
    {
        if(columns.Length==0||area.width<1)return;
        var origin=bloom!=null?area.position:Vector2.zero;
        int fit=Fit;float textX=TextX,width=(area.width-textX)/fit;
        int firstColumn=Mathf.Max(0,Mathf.FloorToInt(scroll)),lastColumn=Mathf.Min(columns.Length-1,Mathf.CeilToInt(scroll+fit));
        for(int c=firstColumn;c<=lastColumn;c++)
        {
            float x0=textX+(c-scroll)*width;
            // A column sliding past either edge fades out.
            fade=Mathf.Clamp01(Mathf.Min(1+(x0-textX)/(width*.3f),1-(x0+width-area.width)/(width*.3f)));
            if(fade<.02f)continue;
            DrawColumn(ctx,p,bloom,origin,c,x0,width,c==current);
        }
    }
    float fade=1;
    Color A(Color c)=>new(c.r,c.g,c.b,c.a*fade);
    void DrawColumn(MeshGenerationContext ctx,Painter2D p,OrreryBloom bloom,Vector2 origin,int c,float x0,float width,bool here)
    {
        var col=columns[c];bool paint=ctx!=null;Lay(col,width);
        float size=col.Size,offset=here?rowScroll:0;
        if(paint)
        {
            // Header: the stanza's name and its rhyme scheme.
            float h=16;
            ctx.DrawText(col.Name,new Vector2(x0+8,6),h,A(here?Color.white:Label(.55f)));
            ctx.DrawText(col.Scheme,new Vector2(x0+8+col.NameWidth*h+14,8),h*.85f,A(Label(here?.7f:.4f)));
            if(here){p.strokeColor=A(Label(.8f));p.lineWidth=2;p.BeginPath();p.MoveTo(new Vector2(x0+8,28));p.LineTo(new Vector2(x0+8+Mathf.Max(30,col.NameWidth*h),28));p.Stroke();}
        }
        float Y(float laid)=>Top+laid-offset;
        bool Visible(float laid){float y=Y(laid);return y>=Top-size*.4f&&y<=area.height-size*1.05f;}
        if(!paint)
        {
            // The glow pass underlines the lit word.
            if(here&&heard&&word!=null&&columnOfLine[word.Line]==c&&Visible(word.LY))
            {
                float y=Y(word.LY);bloom.Line(origin+new Vector2(x0+word.LX,y+size*1.05f),origin+new Vector2(x0+word.LX+word.Width*size*1.08f,y+size*1.05f),3f,Coloured(word)?VowelColor(word,true):Color.white,.45f*fade);
            }
            return;
        }
        foreach(var r in col.Rows)
        {
            if(Y(r.Top)>area.height||Y(r.Top+r.Height)<Top-size)continue;
            bool now=here&&r.Line==line;
            if(now){float y0=Y(r.Top)-4,y1=Y(r.Top+r.Height)-LineGap*size-2;p.fillColor=A(Ink(.09f));p.BeginPath();p.MoveTo(new Vector2(x0,y0));p.LineTo(new Vector2(x0+width-4,y0));p.LineTo(new Vector2(x0+width-4,y1));p.LineTo(new Vector2(x0,y1));p.ClosePath();p.Fill();}
            if(r.Mark.Length>0&&Visible(r.Top))ctx.DrawText(r.Mark,new Vector2(x0+4,Y(r.Top)+size*.15f),size*.5f,A(Label(.45f)));
            foreach(var w in r.Words)
            {
                if(!Visible(w.LY))continue;
                bool lit=now&&heard&&w==word;bool past=here&&(r.Line<line||r.Line==line&&w.End<=beat);
                Color color;
                if(Coloured(w)){color=VowelColor(w,w.Family>=0||lit);if(!lit)color=Color.Lerp(color,Label(.5f),past?.4f:here?0:.3f);}
                else color=lit?Color.white:past?Label(.45f):Label(here?.8f:.55f);
                ctx.DrawText(w.Text,new Vector2(x0+w.LX,Y(w.LY)),lit?size*1.06f:size,A(color));
            }
        }
    }
    // The vowel wheel: the colour ring, long vowels outside it and short ones inside, each at its
    // hue. Up to three regions light up: the vowel sounds rhyming now (in the rhyme families of
    // the line being heard and the lines either side), most-used first.
    int activeLine=-1;readonly List<float> activeHues=new();
    void FindActiveHues()
    {
        activeHues.Clear();
        var counts=new Dictionary<float,int>();
        foreach(var c in columns)foreach(var r in c.Rows)
        {
            if(Math.Abs(r.Line-line)>1)continue;
            foreach(var w in r.Words)if(w.Family>=0){counts.TryGetValue(w.Hue,out int n);counts[w.Hue]=n+1+(r.Line==line?1:0);}
        }
        foreach(var kv in counts.OrderByDescending(kv=>kv.Value).Take(3))activeHues.Add(kv.Key);
    }
    // The wheel is its own element, placed by the views in the empty band beside the drum wheel
    // (VisualizationViews), large enough to read: the colour ring, long vowels outside with an
    // example word, short vowels inside. The lit regions glow in layered halos.
    public readonly VisualElement WheelElement=MakeWheelElement();
    static VisualElement MakeWheelElement(){var e=new VisualElement{name="vowel-wheel",pickingMode=PickingMode.Ignore};e.style.position=Position.Absolute;e.style.display=DisplayStyle.None;return e;}
    void DrawWheel(MeshGenerationContext ctx)
    {
        var p=ctx.painter2D;var rect=WheelElement.contentRect;if(rect.width<80||rect.height<80)return;
        float radius=Mathf.Min(rect.width,rect.height)*.27f;var centre=rect.center;
        Vector2 At(float hue,float r){float a=hue*Mathf.PI*2-Mathf.PI*.5f;return centre+new Vector2(Mathf.Cos(a),Mathf.Sin(a))*r;}
        void Arc(float from,float to,float r,float widthPx,Color c){p.strokeColor=c;p.lineWidth=widthPx;p.BeginPath();int n=Mathf.Max(2,Mathf.CeilToInt((to-from)*120));for(int i=0;i<=n;i++){var at=At(Mathf.Lerp(from,to,i/(float)n),r);if(i==0)p.MoveTo(at);else p.LineTo(at);}p.Stroke();}
        const int segments=90;
        for(int i=0;i<segments;i++){float h0=i/(float)segments,h1=(i+1)/(float)segments+.002f;Arc(h0,h1,radius,radius*.16f,Color.HSVToRGB((h0+h1)*.5f,.6f,.72f));}
        foreach(float hue in activeHues)
        {
            var c=Color.HSVToRGB(hue,.8f,1);
            for(int g=3;g>=1;g--){var halo=c;halo.a=.1f*(4-g);Arc(hue-.05f,hue+.05f,radius,radius*(.3f+.1f*g),halo);}
            Arc(hue-.045f,hue+.045f,radius,radius*.3f,c);
        }
        float label=Mathf.Clamp(radius*.15f,13,26),small=label*.62f;
        foreach(var v in LyricRhymes.WheelVowels)
        {
            float hue=LyricRhymes.Hue(v.nucleus);bool lit=activeHues.Any(h=>Mathf.Abs(h-hue)<.001f);
            var colour=Color.HSVToRGB(hue,lit?.8f:.55f,lit?1:.85f);
            var dot=At(hue,radius*(v.isLong?1.2f:.76f));
            if(lit){var halo=colour;halo.a=.25f;p.fillColor=halo;p.BeginPath();p.Arc(dot,label*.8f,Angle.Degrees(0),Angle.Degrees(360));p.Fill();}
            p.fillColor=colour;p.BeginPath();p.Arc(dot,lit?label*.38f:label*.26f,Angle.Degrees(0),Angle.Degrees(360));p.Fill();
            var at=At(hue,radius*(v.isLong?1.55f:.46f));float size=lit?label*1.2f:label;
            float w=Width(v.label)*size;ctx.DrawText(v.label,at-new Vector2(w*.5f,size*.95f),size,lit?Color.white:Label(.8f));
            float ew=Width(v.example)*small;ctx.DrawText(v.example,at-new Vector2(ew*.5f,-size*.1f),small,lit?colour:Label(.5f));
        }
        ctx.DrawText("VOWEL SOUNDS",new Vector2(rect.x+6,rect.y+4),13,Label(.7f));
        ctx.DrawText("long outside · short inside · lit: rhyming now",new Vector2(rect.x+6,rect.yMax-18),11,Label(.5f));
    }
}
