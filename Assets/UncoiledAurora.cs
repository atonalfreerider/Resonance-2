using UnityEngine;

// One shared, static curtain mesh; the GPU animates outward pressure bands.
[DefaultExecutionOrder(140)]
public sealed class UncoiledAurora : MonoBehaviour
{
    Main main;Mesh mesh;Material material;
    readonly MeshRenderer[] spikes=new MeshRenderer[96];
    readonly float[] energy=new float[96],drive=new float[96];
    MaterialPropertyBlock block;
    readonly float[] pitchEnergy=new float[12];
    float support;
    public int VisibleSpikes {get;private set;}
    void Awake()
    {
        main=GetComponent<Main>();block=new MaterialPropertyBlock();
        material=new Material(Resources.Load<Shader>("UncoiledAurora"));
        const int across=32,height=32;
        var vertices=new Vector3[(across+1)*(height+1)];var uv=new Vector2[vertices.Length];var triangles=new int[across*height*6];
        for(int y=0;y<=height;y++)for(int x=0;x<=across;x++){int i=y*(across+1)+x;uv[i]=new Vector2(x/(float)across,y/(float)height);vertices[i]=new Vector3(uv[i].x*2-1,uv[i].y,0);}
        int k=0;for(int y=0;y<height;y++)for(int x=0;x<across;x++){int i=y*(across+1)+x;triangles[k++]=i;triangles[k++]=i+1;triangles[k++]=i+across+1;triangles[k++]=i+1;triangles[k++]=i+across+2;triangles[k++]=i+across+1;}
        mesh=new Mesh{name="Radial note aurora curtain"};mesh.vertices=vertices;mesh.uv=uv;mesh.triangles=triangles;mesh.bounds=new Bounds(Vector3.zero,Vector3.one*8);mesh.UploadMeshData(true);
        for(int i=0;i<96;i++){var go=new GameObject("Uncoiled note aurora "+i);go.transform.SetParent(transform,false);go.AddComponent<MeshFilter>().sharedMesh=mesh;spikes[i]=go.AddComponent<MeshRenderer>();spikes[i].sharedMaterial=material;spikes[i].enabled=false;}
    }
    void LateUpdate()
    {
        System.Array.Clear(drive,0,drive.Length);VisibleSpikes=0;
        if(main.UncoilActive)foreach(var n in main.ActiveNotes)if(n.Item1>=0&&n.Item1<96)drive[n.Item1]=n.Item2;
        System.Array.Clear(pitchEnergy,0,12);
        var dominant=main.GetComponent<TonalDominance>();float supported=0;
        for(int n=0;n<96;n++){pitchEnergy[n%12]+=drive[n];int interval=HarmonyModel.Mod(n-(dominant!=null?dominant.ChordRoot:main.currentKey));if(interval==0||interval==7||interval==(dominant!=null&&dominant.ChordMinor?3:4))supported+=drive[n];}
        support=Mathf.Lerp(support,supported,1-Mathf.Exp(-Time.unscaledDeltaTime*(supported>support?16:18)));
        for(int i=0;i<96;i++){
            energy[i]=Mathf.Lerp(energy[i],drive[i],1-Mathf.Exp(-Time.unscaledDeltaTime*(drive[i]>energy[i]?35:18)));
            var r=spikes[i];r.enabled=main.UncoilAmount>.01f&&energy[i]>.003f;if(!r.enabled)continue;VisibleSpikes++;
            var point=main.UncoiledPoint(main.UncoiledSlot(i),(i/12+1)/8f);
            r.transform.localPosition=transform.InverseTransformPoint(main.NoteEmissionPoint(i))+Vector3.back*.025f;
            r.transform.localRotation=Quaternion.FromToRotation(Vector3.up,Vector3.Slerp(transform.InverseTransformDirection(main.NoteEmissionNormal(i)),point.normalized,main.UncoilAmount));
            r.GetPropertyBlock(block);Color local=TonalColorField.Pitch(i,main.currentKey);
            block.SetColor("_Hue",dominant!=null?Color.Lerp(local,dominant.Hue,.76f):local);
            int relative=HarmonyModel.Mod(i-(dominant!=null?dominant.ChordRoot:main.currentKey));
            bool member=relative==0||relative==7||relative==(dominant!=null&&dominant.ChordMinor?3:4);
            float resonance=member?1+support*(relative==0?.65f:.32f):1;
            block.SetFloat("_Prominence",relative==0?1:member?.55f:.1f);
            block.SetFloat("_Support",resonance+(member?pitchEnergy[i%12]*.25f:0));
            block.SetFloat("_Energy",energy[i]*main.OctaveSpread);r.SetPropertyBlock(block);
        }
    }
    void OnDestroy(){foreach(var r in spikes)if(r!=null)Destroy(r.gameObject);if(material!=null)Destroy(material);if(mesh!=null)Destroy(mesh);}
}
