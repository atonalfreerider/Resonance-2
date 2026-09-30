using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

[DefaultExecutionOrder(1200)]
public sealed class SongRecordingCaptions : MonoBehaviour
{
    VisualElement root,card,row;
    SongDirector director;SongNarration narration;MidiPlayer midi;VisualizationViews views;
    readonly List<Label> labels=new();
    string text="";string[] words=Array.Empty<string>();float[] ends=Array.Empty<float>();
    int first=-1,last=-1,lit=-1;float total;
    public void Bind(VisualElement ui)
    {
        root=ui;director=GetComponent<SongDirector>();narration=GetComponent<SongNarration>();midi=GetComponent<MidiPlayer>();views=GetComponent<VisualizationViews>();
        card=new VisualElement{name="song-recording-caption",pickingMode=PickingMode.Ignore};
        card.style.position=Position.Absolute;card.style.backgroundColor=new Color(.04f,.06f,.1f,.5f);
        card.style.paddingLeft=card.style.paddingRight=22;card.style.paddingTop=card.style.paddingBottom=14;
        card.style.borderTopLeftRadius=card.style.borderTopRightRadius=card.style.borderBottomLeftRadius=card.style.borderBottomRightRadius=12;
        row=new VisualElement{pickingMode=PickingMode.Ignore};row.style.flexDirection=FlexDirection.Row;row.style.flexWrap=Wrap.Wrap;row.style.justifyContent=Justify.Center;
        card.Add(row);card.style.display=DisplayStyle.None;root.Add(card);
    }
    void LateUpdate()
    {
        var cue=director.CurrentCue;var segment=narration.SegmentFor(director.CueIndex);
        bool show=RecordingMode.Active&&cue!=null&&segment!=null&&midi.Position>=segment.start&&midi.Position<segment.end;
        card.style.display=show?DisplayStyle.Flex:DisplayStyle.None;if(!show)return;
        if(text!=cue.text)
        {
            text=cue.text;words=text.Split((char[])null,StringSplitOptions.RemoveEmptyEntries);ends=new float[words.Length];total=0;
            for(int i=0;i<words.Length;i++){total+=Mathf.Max(2,words[i].Length)+(words[i].EndsWith(".")||words[i].EndsWith(":")?3:0);ends[i]=total;}
            first=-1;lit=-1;
        }
        // Segment times come from the rendered voice. Word positions are an estimate until
        // a bundle supplies word-level speech alignment; seeking always follows the song clock.
        float at=(float)((midi.Position-segment.start)/(segment.end-segment.start))*total;
        int word=0;
        if(segment.wordStarts!=null&&segment.wordStarts.Length==words.Length){while(word+1<words.Length&&segment.wordStarts[word+1]<=midi.Position-segment.start)word++;}
        else while(word+1<ends.Length&&ends[word]<=at)word++;
        int start=0,end=0;
        while(end<=word&&end<words.Length){start=end;do{end++;}while(end<words.Length&&end-start<12&&!words[end-1].EndsWith(".")&&!words[end-1].EndsWith(";")&&!words[end-1].EndsWith(":"));}
        if(first!=start||last!=end)
        {
            first=start;last=end;row.Clear();labels.Clear();lit=-1;
            for(int i=start;i<end;i++){var label=new Label(words[i]){enableRichText=false,pickingMode=PickingMode.Ignore};label.style.unityFontStyleAndWeight=FontStyle.Bold;label.style.marginRight=12;row.Add(label);labels.Add(label);}
        }
        float width=root.resolvedStyle.width,height=root.resolvedStyle.height;if(!float.IsFinite(width)||width<1)return;
        bool portrait=views.Vertical||height>width;float cw=portrait?width-32:Mathf.Min(width-48,1100);
        float size=Mathf.Clamp(cw/18,24,42);
        card.style.left=(width-cw)*.5f;card.style.width=cw;
        card.style.top=portrait?new StyleLength(height*RecordingMode.CaptionTop):new StyleLength(StyleKeyword.Auto);
        card.style.bottom=portrait?new StyleLength(StyleKeyword.Auto):new StyleLength(22);
        foreach(var label in labels)label.style.fontSize=size;
        if(lit==word)return;lit=word;
        for(int i=0;i<labels.Count;i++)
        {
            bool active=i+first==word;var label=labels[i];label.style.color=active?new Color(1,.9f,.55f):i+first<word?new Color(.9f,.93f,.97f):new Color(.6f,.67f,.76f);
            label.style.scale=new Scale(Vector3.one*(active?1.06f:1));
            label.style.textShadow=new TextShadow{offset=Vector2.zero,blurRadius=active?16:0,color=active?new Color(1,.8f,.35f,.8f):Color.clear};
        }
    }
}
