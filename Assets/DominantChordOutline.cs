using System.Collections.Generic;
using UnityEngine;

[DefaultExecutionOrder(120)]
public sealed class DominantChordOutline : MonoBehaviour
{
    Main main;TonalDominance dominance;Material material;
    readonly LineRenderer[] edges=new LineRenderer[3];readonly List<Vector3> path=new(41);
    float visibility;int root,third=4,fifth=7;Color hue;
    void Start(){
        main=GetComponent<Main>();material=new Material(Resources.Load<Shader>("HarmonicGlow"));material.SetColor("_BaseColor",Color.white*.65f);
        for(int i=0;i<3;i++){var go=new GameObject("Dominant chord surface outline");go.transform.SetParent(transform,false);var line=go.AddComponent<LineRenderer>();edges[i]=line;line.sharedMaterial=material;line.useWorldSpace=true;line.widthMultiplier=.009f;line.positionCount=41;line.numCapVertices=3;}
    }
    void LateUpdate(){
        if(main==null)return;dominance??=GetComponent<TonalDominance>();if(dominance==null)return;
        if(dominance.HasChord){root=dominance.ChordRoot;third=dominance.ChordMinor||dominance.ChordQuality=="dim"?3:4;fifth=dominance.ChordQuality=="dim"?6:7;hue=TonalColorField.Chord(root,main.currentKey,dominance.ChordMinor);}
        visibility=Mathf.MoveTowards(visibility,dominance.HasChord?1:0,Time.unscaledDeltaTime*5);
        var vertices=new[]{root,root+third,root+fifth};
        for(int i=0;i<3;i++){var line=edges[i];line.enabled=visibility>.001f;if(!line.enabled)continue;main.ChordOutlinePath(vertices[i],vertices[(i+1)%3],path);for(int j=0;j<path.Count;j++)line.SetPosition(j,path[j]);line.startColor=line.endColor=hue*visibility;}
    }
    void OnDestroy(){if(material!=null)Destroy(material);foreach(var edge in edges)if(edge!=null)Destroy(edge.gameObject);}
}
