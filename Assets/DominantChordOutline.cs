using System.Collections.Generic;
using UnityEngine;

[DefaultExecutionOrder(120)]
public sealed class DominantChordOutline : MonoBehaviour
{
    Main main;MidiPlayer midi;TonalDominance dominance;Material material,fillMaterial;Mesh fillMesh;GameObject fillObject;
    const int Resolution=24;
    readonly List<Vector3> fillVertices=new();readonly List<int> fillTriangles=new();
    readonly LineRenderer[] edges=new LineRenderer[3];readonly List<Vector3> path=new(41);
    float visibility;int root,third=4,fifth=7;Color hue;
    public bool RegionVisible=>visibility>0;
    public int RegionRoot=>root;
    public int RegionThird=>third;
    public int RegionFifth=>fifth;
    public Color RegionColor=>hue;
    public static SongFormAnalysis.ChordStep PhaseAt(SongFormAnalysis.ChordStep[] phases,double beat)
    {
        if(phases==null)return null;
        int lo=0,hi=phases.Length-1,index=-1;
        while(lo<=hi){int mid=(lo+hi)/2;if(phases[mid].Start<=beat){index=mid;lo=mid+1;}else hi=mid-1;}
        if(index<0)return null;var phase=phases[index];
        return beat<phase.End&&!phase.Rest&&!phase.Quality.Contains("tone")&&!phase.Quality.Contains("dyad")?phase:null;
    }
    void OnEnable(){
        if(!Application.isPlaying)return;
        visibility=0;
        main=GetComponent<Main>();material=new Material(Resources.Load<Shader>("HarmonicGlow"));material.SetColor("_BaseColor",Color.white*1.5f);
        for(int i=0;i<3;i++){var go=new GameObject("Dominant chord surface outline");go.transform.SetParent(transform,false);var line=go.AddComponent<LineRenderer>();edges[i]=line;line.sharedMaterial=material;line.useWorldSpace=true;line.widthMultiplier=.02f;line.positionCount=41;line.numCapVertices=3;}
        fillObject=new GameObject("Active chord surface tint");fillObject.transform.SetParent(transform,false);
        fillMesh=new Mesh{name="Curved chord region"};fillMesh.MarkDynamic();fillObject.AddComponent<MeshFilter>().sharedMesh=fillMesh;
        fillMaterial=new Material(Resources.Load<Shader>("ChordRegion"));fillObject.AddComponent<MeshRenderer>().sharedMaterial=fillMaterial;fillObject.SetActive(false);
        fillTriangles.Clear();
        for(int row=0;row<Resolution;row++)for(int col=0;col<Resolution-row;col++){
            int a=VertexIndex(row,col),b=VertexIndex(row+1,col),c=VertexIndex(row,col+1);
            fillTriangles.AddRange(new[]{a,b,c});
            if(col<Resolution-row-1)fillTriangles.AddRange(new[]{b,VertexIndex(row+1,col+1),c});
        }
    }
    static int VertexIndex(int row,int col)=>row*(Resolution+1)-row*(row-1)/2+col;
    void LateUpdate(){
        if(main==null)main=GetComponent<Main>();if(dominance==null)dominance=GetComponent<TonalDominance>();var camera=Camera.main;if(main==null||dominance==null||camera==null||fillObject==null)return;
        if(midi==null)midi=GetComponent<MidiPlayer>();
        bool show=dominance.HasChord,minor=dominance.ChordMinor;string quality=dominance.ChordQuality;int nextRoot=dominance.ChordRoot;
        if(midi!=null&&midi.isActiveAndEnabled&&midi.Loaded&&midi.Cycles!=null&&!(main.NotesUseSynth&&main.ActiveNotes.Count>0)){
            var phase=PhaseAt(midi.Prepared?.RegionPhases,midi.Cycles.BeatAt(midi.ScorePosition));show=phase!=null;
            if(show){nextRoot=phase.Root;quality=phase.Quality;minor=quality.StartsWith("m")&&!quality.StartsWith("maj");}
        }
        if(show){root=nextRoot;third=minor||quality=="dim"?3:4;fifth=quality=="dim"?6:7;hue=TonalColorField.Chord(root,main.currentKey,minor);}
        visibility=show?1:0;
        var vertices=new[]{root,root+third,root+fifth};
        for(int i=0;i<3;i++){var line=edges[i];if(line==null)continue;line.enabled=visibility>.001f;if(!line.enabled)continue;main.ChordOutlinePath(vertices[i],vertices[(i+1)%3],path);for(int j=0;j<path.Count;j++)line.SetPosition(j,path[j]+(camera.transform.position-path[j]).normalized*.012f);line.startColor=line.endColor=hue*visibility;}
        fillObject.SetActive(visibility>.001f);
        if(visibility>.001f){
            Vector2 a=main.ChordRegionCoordinate(root,root),b=main.ChordRegionCoordinate(root,root+third),c=main.ChordRegionCoordinate(root,root+fifth);
            fillVertices.Clear();
            for(int row=0;row<=Resolution;row++)for(int col=0;col<=Resolution-row;col++){
                var uv=a+(b-a)*(row/(float)Resolution)+(c-a)*(col/(float)Resolution);
                var world=main.ChordRegionPoint(uv);world+=(camera.transform.position-world).normalized*.006f;
                fillVertices.Add(transform.InverseTransformPoint(world));
            }
            fillMesh.SetVertices(fillVertices);fillMesh.SetTriangles(fillTriangles,0);fillMesh.RecalculateBounds();
            fillMaterial.SetColor("_BaseColor",new Color(hue.r,hue.g,hue.b,.19f*visibility));
        }
    }
    void OnDisable(){if(material!=null)Destroy(material);foreach(var edge in edges)if(edge!=null){edge.gameObject.SetActive(false);Destroy(edge.gameObject);}if(fillObject!=null){fillObject.SetActive(false);Destroy(fillObject);}if(fillMesh!=null)Destroy(fillMesh);if(fillMaterial!=null)Destroy(fillMaterial);fillObject=null;}
}
