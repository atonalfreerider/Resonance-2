using UnityEngine;

[DefaultExecutionOrder(130)]
public sealed class ChordAurora : MonoBehaviour
{
    const int Particles=10000;
    public int MeshBakeCount {get;private set;}
    float phase;
    readonly float[] previous=new float[96],attacks=new float[96];
    public bool SingleNote=>current>=0&&volumes[current].Note>=0;
    public int EmitterNote=>current<0?-1:volumes[current].Note;
    public float Crosswind {get;private set;}
    public float Shock {get;private set;}
    public void Strike(int index,float velocity){if(index>=0&&index<96)attacks[index]=Mathf.Max(attacks[index],velocity);}

    readonly System.Collections.Generic.Dictionary<(int,int,int,int),Mesh> bakedMeshes=new();
    Main main;DominantChordOutline region;MidiPlayer midi;SongAudio recording;
    readonly float[] samples=new float[512];readonly Volume[] volumes=new Volume[3];
    int current=-1;float rotation,twist;
    public float Energy=>current<0?0:volumes[current].Energy;
    public int EmitterRoot=>current<0?-1:volumes[current].Root;
    public bool MinorShape=>current>=0&&volumes[current].Third==3;
    public int VolumeCount=>volumes.Length;
    public static float Release(float current,float target,float dt)=>Mathf.Lerp(current,target,1-Mathf.Exp(-Mathf.Max(0,dt)*(target>current?28:22)));
    public static float ModeShape(float u,bool minor)=>Mathf.Pow(Mathf.Abs(Mathf.Sin(Mathf.PI*u*(minor?2:1))),.8f);
    sealed class Volume
    {
        public int Root=-1,Third,Fifth,Note=-1;public float Energy,Height,Age,PulseAge,LastShock;public bool Dying;
        public Color Hue;public GameObject Object;public Mesh Mesh;public Material Material;public MeshRenderer Renderer;public bool Placed;
        public void Die(){if(!Dying){Dying=true;Age=0;}}
    }
    static float Seed(int i,float multiplier)
    {
        unchecked {uint h=(uint)i+(uint)(multiplier*1000000)+0x9e3779b9u;h^=h>>16;h*=0x7feb352du;h^=h>>15;h*=0x846ca68bu;h^=h>>16;return (h&0xffffff)/16777216f;}
    }
    void OnEnable()
    {
        if(!Application.isPlaying)return;main=GetComponent<Main>();region=GetComponent<DominantChordOutline>();current=-1;
        // Both particle shapes up front, so the first chord of a song does not build one.
        Shape(false);Shape(true);
        for(int n=0;n<volumes.Length;n++){
            var v=new Volume();volumes[n]=v;v.Object=new GameObject("Aurora volume "+n);v.Object.transform.SetParent(transform,false);
            v.Object.AddComponent<MeshFilter>();
            v.Renderer=v.Object.AddComponent<MeshRenderer>();v.Material=new Material(Resources.Load<Shader>("ChordAurora"));v.Renderer.sharedMaterial=v.Material;v.Renderer.enabled=false;

        }
    }
    // Chord volumes share one particle mesh per shape (major, minor), built once: a particle's
    // place in the chord's triangle (x, u) and its seeds never change. The vertex shader lays
    // the particles on the emitting surface from a 17 by 17 grid of surface points and normals,
    // so a new chord or a moving torus pose (a key change, a tension's lean) only uploads the
    // grid. Single-note volumes, a small disc of particles, are baked on the CPU.
    const int Grid=16,Nodes=(Grid+1)*(Grid+1);
    readonly Mesh[] shapes=new Mesh[2];
    readonly Vector4[] gridPoint=new Vector4[Nodes],gridNormal=new Vector4[Nodes];readonly Vector3[] gridScratch=new Vector3[Nodes];
    static readonly int GridPointId=Shader.PropertyToID("_GridPoint"),GridNormalId=Shader.PropertyToID("_GridNormal"),UseGridId=Shader.PropertyToID("_UseGrid");
    Mesh Shape(bool minor)
    {
        int k=minor?1:0;if(shapes[k]!=null)return shapes[k];
        var mesh=new Mesh{name=minor?"Aurora particles, minor modes":"Aurora particles, major modes",indexFormat=UnityEngine.Rendering.IndexFormat.UInt32};
        int n=Particles*4;var vertices=new Vector3[n];var normals=new Vector3[n];var uv0=new Vector2[n];var modes=new Vector4[n];var field=new Vector4[n];var triangles=new int[Particles*6];
        float mode=minor?2:1;
        for(int i=0;i<Particles;i++){
            float x=Mathf.Sqrt(Seed(i,.754877666f)),u=Seed(i,.569840296f),h=Seed(i,.438579f);
            float edge=Mathf.SmoothStep(0,1,Mathf.Clamp01(Mathf.Min(1-x,Mathf.Min(x*u,x*(1-u)))*12));
            var bakedMode=new Vector4(h,Mathf.Sin(mode*Mathf.PI*u),Mathf.Sin(3*mode*Mathf.PI*u),1.9f);
            var bakedField=new Vector4(x,u,Seed(i,.211f),Mathf.Pow(1-h,1.5f)*Mathf.SmoothStep(0,1,(h+.025f)/.09f)*edge);
            for(int j=0;j<4;j++){int q=i*4+j;normals[q]=Vector3.up;uv0[q]=new Vector2(j%2,j/2);modes[q]=bakedMode;field[q]=bakedField;}
            int t=i*6,v=i*4;triangles[t]=v;triangles[t+1]=v+2;triangles[t+2]=v+1;triangles[t+3]=v+1;triangles[t+4]=v+2;triangles[t+5]=v+3;
        }
        mesh.SetVertices(vertices);mesh.SetNormals(normals);mesh.SetUVs(0,uv0);mesh.SetUVs(1,modes);mesh.SetUVs(2,field);mesh.SetTriangles(triangles,0);
        mesh.bounds=new Bounds(Vector3.zero,Vector3.one*24);mesh.UploadMeshData(true);MeshBakeCount++;
        return shapes[k]=mesh;
    }
    // The chord's emitting surface as grid nodes: one surface point each, normals from the
    // grid's own tangents, turned to face the surface normal at the region's middle.
    void Place(Volume v)
    {
        var a=main.ChordRegionCoordinate(v.Root,v.Root);var b=main.ChordRegionCoordinate(v.Root,v.Root+v.Third);var c=main.ChordRegionCoordinate(v.Root,v.Root+v.Fifth);
        for(int gx=0;gx<=Grid;gx++)for(int gu=0;gu<=Grid;gu++)
        {
            float x=gx/(float)Grid,u=gu/(float)Grid;var uv=a*(1-x)+b*x*(1-u)+c*x*u;
            gridScratch[gx*(Grid+1)+gu]=transform.InverseTransformPoint(main.ChordRegionPoint(uv,true));
        }
        var reference=transform.InverseTransformVector(main.ChordRegionNormal(a*(1-.66f)+b*.33f+c*.33f,true));
        for(int gx=0;gx<=Grid;gx++)for(int gu=0;gu<=Grid;gu++)
        {
            int rx=Mathf.Max(1,gx);   // the apex row collapses to a point: borrow the next row's frame
            var along=gridScratch[Mathf.Min(Grid,rx+1)*(Grid+1)+gu]-gridScratch[(rx-1)*(Grid+1)+gu];
            var sideways=gridScratch[rx*(Grid+1)+Mathf.Min(Grid,gu+1)]-gridScratch[rx*(Grid+1)+Mathf.Max(0,gu-1)];
            var normal=Vector3.Cross(along,sideways);normal=normal.sqrMagnitude>1e-12f?normal.normalized:reference;
            if(Vector3.Dot(normal,reference)<0)normal=-normal;
            int g=gx*(Grid+1)+gu;gridPoint[g]=gridScratch[g];gridNormal[g]=normal;
        }
        v.Material.SetVectorArray(GridPointId,gridPoint);v.Material.SetVectorArray(GridNormalId,gridNormal);v.Placed=true;
    }
    void Map(Volume v)
    {
        if(v.Note<0)
        {
            v.Material.SetFloat(UseGridId,1);v.Object.GetComponent<MeshFilter>().sharedMesh=v.Mesh=Shape(v.Third==3);Place(v);return;
        }
        v.Material.SetFloat(UseGridId,0);v.Placed=true;
        var key=(v.Root,v.Third,v.Fifth,v.Note);
        if(bakedMeshes.TryGetValue(key,out var cached)){v.Mesh=cached;v.Object.GetComponent<MeshFilter>().sharedMesh=cached;return;}
        v.Mesh=new Mesh{name="Baked note aurora"};bakedMeshes.Add(key,v.Mesh);v.Object.GetComponent<MeshFilter>().sharedMesh=v.Mesh;
        const int particles=1000;
        var vertices=new Vector3[particles*4];var normals=new Vector3[particles*4];var uv0=new Vector2[particles*4];var modes=new Vector4[particles*4];var field=new Vector4[particles*4];var triangles=new int[particles*6];
        Vector3 soloPoint=transform.InverseTransformPoint(main.CoiledNoteEmissionPoint(v.Note));
        Vector3 soloNormal=transform.InverseTransformDirection(main.ChordRegionNormal(main.ChordRegionCoordinate(HarmonyModel.Mod(v.Note),HarmonyModel.Mod(v.Note)),true));
        Vector3 side=Vector3.Cross(soloNormal,Mathf.Abs(soloNormal.y)<.9f?Vector3.up:Vector3.right).normalized;
        Vector3 across=Vector3.Cross(soloNormal,side);
        for(int i=0;i<particles;i++){
            float x=Mathf.Sqrt(Seed(i,.754877666f)),u=Seed(i,.569840296f),h=Seed(i,.438579f);
            float angle=u*Mathf.PI*2;var anchor=soloPoint+(side*Mathf.Cos(angle)+across*Mathf.Sin(angle))*(x*.07f);
            float edge=Mathf.Pow(1-x*x,2);float mode=v.Third==3?2:1;
            var bakedMode=new Vector4(h,Mathf.Sin(mode*Mathf.PI*u),Mathf.Sin(3*mode*Mathf.PI*u),1.9f);
            var bakedField=new Vector4(x,u,Seed(i,.211f),Mathf.Pow(1-h,1.5f)*Mathf.SmoothStep(0,1,(h+.025f)/.09f)*edge);
            for(int j=0;j<4;j++){int k=i*4+j;vertices[k]=anchor;normals[k]=soloNormal;uv0[k]=new Vector2(j%2,j/2);modes[k]=bakedMode;field[k]=bakedField;}
            int t=i*6,n=i*4;triangles[t]=n;triangles[t+1]=n+2;triangles[t+2]=n+1;triangles[t+3]=n+1;triangles[t+4]=n+2;triangles[t+5]=n+3;
        }
        v.Mesh.SetVertices(vertices);v.Mesh.SetNormals(normals);v.Mesh.SetUVs(0,uv0);v.Mesh.SetUVs(1,modes);v.Mesh.SetUVs(2,field);v.Mesh.SetTriangles(triangles,0);
        v.Mesh.RecalculateBounds();var bounds=v.Mesh.bounds;bounds.Expand(12f);v.Mesh.bounds=bounds;v.Mesh.UploadMeshData(true);MeshBakeCount++;
    }
    void LateUpdate()
    {
        using var perf=Perf.Aurora.Auto();
        if(main==null||region==null||volumes[0]==null)return;

        if(midi==null)midi=GetComponent<MidiPlayer>();if(recording==null)recording=GetComponent<SongAudio>();
        if(rotation!=main.VisualRotation||twist!=main.VisualTwist)
        {
            rotation=main.VisualRotation;twist=main.VisualTwist;
            // Note discs are rebaked; chord volumes only re-place their grid (hidden ones when they show).
            foreach(var mesh in bakedMeshes.Values)Destroy(mesh);bakedMeshes.Clear();
            foreach(var v in volumes){v.Placed=false;if(v.Root>=0&&(v.Renderer.enabled||v==(current>=0?volumes[current]:null)))Map(v);}
        }
        int count=0,strongest=-1;float strongestEnergy=0;
        var levels=new Vector3();float total=0,register=0,shock=0;Vector3 wind=Vector3.zero;
        foreach(var note in main.ActiveNotes)if(note.Item2>.001f){count++;if(note.Item2>strongestEnergy){strongestEnergy=note.Item2;strongest=note.Item1;}}
        int solo=count==1||!region.HasRegion?strongest:-1;
        int root=solo>=0?HarmonyModel.Mod(solo):region.RegionRoot,third=solo>=0?0:region.RegionThird,fifth=solo>=0?0:region.RegionFifth;
        Color color=solo>=0?TonalColorField.Chord(root,main.currentKey,false):region.RegionColor;
        if(count>0&&(current<0||volumes[current].Root!=root||volumes[current].Third!=third||volumes[current].Fifth!=fifth||volumes[current].Note!=solo)){
            if(current>=0)volumes[current].Die();current=(current+1)%volumes.Length;var next=volumes[current];
            next.Root=root;next.Third=third;next.Fifth=fifth;next.Note=solo;next.Energy=next.Height=next.Age=next.PulseAge=next.LastShock=0;next.Dying=false;next.Hue=color;Map(next);
        }
        for(int i=0;i<96;i++)attacks[i]*=Mathf.Exp(-Time.unscaledDeltaTime*12);
        foreach(var note in main.ActiveNotes){
            int index=note.Item1;if(index<0||index>=96)continue;
            if(note.Item2>previous[index]+.04f)Strike(index,note.Item2);
            int pc=HarmonyModel.Mod(index-root);bool member=solo>=0?index==solo:pc==0||pc==third||pc==fifth;
            if(member){total+=note.Item2;register+=index*note.Item2;shock+=attacks[index];
                if(solo>=0)levels=Vector3.one*note.Item2;else if(pc==0)levels.x+=note.Item2;else if(pc==third)levels.y+=note.Item2;else levels.z+=note.Item2;
            }else wind+=transform.InverseTransformDirection(main.NoteEmissionNormal(index))*(attacks[index]+note.Item2*.18f);
        }
        System.Array.Clear(previous,0,previous.Length);foreach(var note in main.ActiveNotes)if(note.Item1>=0&&note.Item1<96)previous[note.Item1]=note.Item2;
        wind=Vector3.ClampMagnitude(wind,1.3f);Crosswind=wind.magnitude;Shock=Mathf.Clamp01(shock);
        float loudness=main.Synth!=null?Mathf.Clamp01(main.Synth.Volume/.6f):1;
        bool score=midi!=null&&midi.isActiveAndEnabled&&midi.Loaded&&!(main.NotesUseSynth&&main.ActiveNotes.Count>0);
        if(score&&recording!=null&&recording.Ready){loudness=0;var audible=GetComponent<StemPlayback>()?.AudibleSource??recording.Source;if(audible.isPlaying){audible.GetOutputData(samples,0);foreach(float s in samples)loudness+=s*s;loudness=Mathf.Clamp01(Mathf.Sqrt(loudness/samples.Length)*7);}}
        if(score&&!midi.IsPlaying)total=0;
        float target=count>0?Mathf.Clamp01(total*(solo>=0?1:.65f))*loudness:0;
        float frequency=Mathf.Lerp(3.8f,7f,Mathf.Clamp01(register/Mathf.Max(total,.001f)/80));
        phase+=Time.unscaledDeltaTime*(Main.ReducedMotion?.5f:frequency);
        for(int layer=0;layer<volumes.Length;layer++){
            var v=volumes[layer];if(v.Root<0)continue;
            float drive=layer==current?target:0;
            if(drive>.003f){v.Hue=color;v.Dying=false;v.Age=0;v.Energy=Release(v.Energy,drive,Time.unscaledDeltaTime);v.Height=1;}
            else {v.Die();v.Age+=Time.unscaledDeltaTime;v.Energy=Release(v.Energy,0,Time.unscaledDeltaTime);}
            v.Renderer.enabled=v.Energy>.0005f&&main.CoiledVisibility>.001f;if(!v.Renderer.enabled)continue;
            if(!v.Placed)Map(v);
            Color hue=v.Dying?Chord.ReleaseHue(v.Hue,v.Age):v.Hue;
            v.Material.SetColor("_Hue",hue);
            if(layer==current&&drive>.003f){if(Shock>v.LastShock+.025f)v.PulseAge=0;v.LastShock=Shock;v.Material.SetVector("_Drive",new Vector4(levels.x,levels.y,levels.z,Shock*loudness));v.Material.SetVector("_Wind",new Vector4(wind.x,wind.y,wind.z,solo>=0?1:0));}
            v.PulseAge+=Time.unscaledDeltaTime;v.Material.SetFloat("_Pulse",v.PulseAge);
            v.Material.SetFloat("_ShapeOpacity",main.CoiledVisibility);v.Material.SetVector("_Wave",new Vector4(phase,v.Energy,v.Age,v.Dying?1:0));

        }
    }
    void OnDisable(){foreach(var v in volumes)if(v!=null){if(v.Object!=null){v.Object.SetActive(false);Destroy(v.Object);}if(v.Material!=null)Destroy(v.Material);}foreach(var mesh in bakedMeshes.Values)Destroy(mesh);bakedMeshes.Clear();for(int k=0;k<2;k++)if(shapes[k]!=null){Destroy(shapes[k]);shapes[k]=null;}current=-1;}
}
