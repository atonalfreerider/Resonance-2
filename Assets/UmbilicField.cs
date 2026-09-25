using UnityEngine;

[RequireComponent(typeof(MeshFilter),typeof(MeshRenderer))]
public class UmbilicField : MonoBehaviour
{
    MeshRenderer occluder;
    const int Along=240, Across=24;
    Main main;
    Mesh mesh;
    Material material;
    MeshRenderer rendererComponent;
    readonly Vector3[] vertices=new Vector3[(Along+1)*(Across+1)];
    readonly Vector4[] anchors=new Vector4[12], colors=new Vector4[12], excitation=new Vector4[12];
    readonly float[] target=new float[12];
    readonly HarmonicMemory memory=new();
    int integratedFrame=-1;
    float phase=float.NaN, twist,unfold=-1;int geometryKey=-1;
    int maskKey=-1;
    bool maskMinor;
    readonly Vector2[] tonalCoverage=new Vector2[(Along+1)*(Across+1)];
    readonly Vector3[] deformed=new Vector3[(Along+1)*(Across+1)];readonly Color[] localColors=new Color[(Along+1)*(Across+1)];
    // Coverage follows the surface's parameters, so while the pose moves it is refreshed at most
    // every 150 ms and once more when the pose settles.
    // Computed in slices over a few frames into scratch buffers, then applied at once, so a key
    // change never spends a whole frame on it.
    float poseMoved=-1,lastCoverage=-1;bool coverageStale;int coverageCursor=-1,sliceKey;bool sliceMinor;
    readonly Vector2[] coverageScratch=new Vector2[(Along+1)*(Across+1)];readonly Color[] colorScratch=new Color[(Along+1)*(Across+1)];
    readonly System.Collections.Generic.List<Vector3[]> regions=new();readonly System.Collections.Generic.List<float> strengths=new();readonly System.Collections.Generic.List<Color> regionColors=new();
    Vector3[] centres=new Vector3[0];float[] reach=new float[0];
    public Mesh SurfaceMesh => mesh;
    public float[] Energy => target;
    public float[] ResidualEnergy => memory.Energy;
    public void ClearMemory(){memory.Clear();System.Array.Clear(excitation,0,excitation.Length);}
    public void Integrate(System.Collections.Generic.IEnumerable<System.Tuple<int,float>> notes,double seconds)
    {
        memory.Advance(notes,seconds,main.ResonanceHalfLife,main.ResonanceGain*main.Synth.Volume/.6f,main.ShowHarmonics);
        integratedFrame=Time.frameCount;
    }
    public void Initialize(Main owner)
    {
        main=owner; mesh=new Mesh{name="Continuous umbilic field"};mesh.MarkDynamic();
        var uv=new Vector2[vertices.Length];var triangles=new int[Along*Across*6];int k=0;
        for(int i=0;i<=Along;i++)for(int j=0;j<=Across;j++)uv[i*(Across+1)+j]=new Vector2(i/(float)Along,j/(float)Across);
        for(int i=0;i<Along;i++)for(int j=0;j<Across;j++)
        {
            int a=i*(Across+1)+j,b=a+Across+1;
            triangles[k++]=a;triangles[k++]=b;triangles[k++]=a+1;
            triangles[k++]=a+1;triangles[k++]=b;triangles[k++]=b+1;
        }
        mesh.vertices=vertices;mesh.uv=uv;mesh.triangles=triangles;
        GetComponent<MeshFilter>().sharedMesh=mesh;
        material=new Material(Resources.Load<Shader>("UmbilicField"));
        rendererComponent=GetComponent<MeshRenderer>();rendererComponent.sharedMaterial=material;
        // The torus hides what lies behind it (the drum wheel below) without changing its glow:
        // the same surface, depth only, drawn after the torus's layers and before the drum wheel.
        if(occluder==null){occluder=new GameObject("Torus depth occluder"){layer=gameObject.layer}.AddComponent<MeshRenderer>();occluder.transform.SetParent(transform,false);
            occluder.gameObject.AddComponent<MeshFilter>().sharedMesh=mesh;occluder.sharedMaterial=new Material(Resources.Load<Shader>("TorusOccluder"));
            occluder.shadowCastingMode=UnityEngine.Rendering.ShadowCastingMode.Off;occluder.receiveShadows=false;}
        UpdateGeometry();
    }
    void UpdateGeometry()
    {
        if(phase==main.VisualRotation && twist==main.VisualTwist&&unfold==main.UncoilAmount&&geometryKey==main.currentKey)return;
        if(geometryKey==main.currentKey&&unfold==main.UncoilAmount){poseMoved=Time.unscaledTime;coverageStale=true;}
        unfold=main.UncoilAmount;geometryKey=main.currentKey;
        phase=main.VisualRotation;twist=main.VisualTwist;
        for(int i=0;i<=Along;i++)
        {
            float t=i/(float)Along-.5f+HarmonyModel.Mod(main.currentKey*5)/12f+phase;
            Vector3 a=main.UmbilicPoint(t),b=main.UmbilicPoint(t+1f/3f);
            for(int j=0;j<=Across;j++)vertices[i*(Across+1)+j]=Vector3.Lerp(a,b,j/(float)Across);
        }
        // Coiled and still (no uncoil, no transition widening) the surface is the umbilic itself.
        bool coiled=main.UncoilAmount<=0&&main.TransitionWiden<=0&&main.OctaveSpread<=0;
        if(coiled)System.Array.Copy(vertices,deformed,vertices.Length);
        else for(int i=0;i<=Along;i++)for(int j=0;j<=Across;j++){
            float slot=i/(float)Along-.5f;
            int index=i*(Across+1)+j;deformed[index]=main.MorphUncoil(vertices[index],main.UncoiledPoint(slot,.3f+.7f*j/Across));
            float t=HarmonyModel.Mod(main.currentKey*5)/12f+phase+slot;
            deformed[index]+=(vertices[index]-main.UmbilicPoint(t))*(.3f*main.TransitionWiden*(1-main.OctaveSpread));
        }
        mesh.SetVertices(deformed);mesh.SetUVs(2,vertices);mesh.RecalculateBounds();
        for(int pc=0;pc<12;pc++)anchors[pc]=main.UmbilicPoint(HarmonyModel.Mod(pc*5)/12f+phase);
        material.SetVectorArray("_Anchors",anchors);
        if(!coverageStale&&coverageCursor<0)maskKey=-1;
    }
    // Defined chord regions have soft spatial support; unclaimed surface emits no light.
    void UpdateCoverage()
    {
        // While the pose moves the coverage is kept (it follows the surface's parameters); it is
        // refreshed once the pose has settled, or at once for a new key or mode.
        bool settled=Time.unscaledTime-poseMoved>.25f;
        if(coverageStale&&settled){coverageStale=false;maskKey=-1;}
        if(coverageCursor<0)
        {
            if(maskKey==main.currentKey&&maskMinor==main.MinorMode)return;
            maskKey=main.currentKey;maskMinor=main.MinorMode;sliceKey=maskKey;sliceMinor=maskMinor;
            regions.Clear();strengths.Clear();regionColors.Clear();
            void Triad(int root,int third,float strength)
            {
                regions.Add(new[]{(Vector3)anchors[HarmonyModel.Mod(root)],(Vector3)anchors[HarmonyModel.Mod(root+third)],(Vector3)anchors[HarmonyModel.Mod(root+7)]});
                strengths.Add(strength);regionColors.Add(TonalColorField.Chord(root,main.currentKey,third==3));
            }
            int collection=main.CollectionRoot;
            foreach(int offset in new[]{0,5,7})Triad(collection+offset,4,1);
            foreach(int offset in new[]{2,4,9})Triad(collection+offset,3,.28f);
            Triad(main.currentKey+1,4,.18f); // Neapolitan bII.
            Triad(main.currentKey+7,4,main.MinorMode?.35f:1); // Major dominant in minor.
            foreach(int offset in new[]{2,4,9,11})Triad(collection+offset,4,.18f); // Secondary dominants.
            // Each region's bounding sphere: a vertex beyond it (plus the fade distance) is untouched.
            centres=new Vector3[regions.Count];reach=new float[regions.Count];
            for(int r=0;r<regions.Count;r++){var q=regions[r];centres[r]=(q[0]+q[1]+q[2])/3;reach[r]=Mathf.Max(Vector3.Distance(centres[r],q[0]),Mathf.Max(Vector3.Distance(centres[r],q[1]),Vector3.Distance(centres[r],q[2])))+.26f;reach[r]*=reach[r];}
            coverageCursor=0;
        }
        int end=Mathf.Min(vertices.Length,coverageCursor+vertices.Length/4+1);
        for(int i=coverageCursor;i<end;i++)
        {
            float coverage=0,major=0,weight=0;Color local=Color.black;
            for(int r=0;r<regions.Count;r++)
            {
                if((vertices[i]-centres[r]).sqrMagnitude>reach[r])continue;
                var region=regions[r];
                float distance=TriangleDistance(vertices[i],region[0],region[1],region[2]);
                float fade=1-Mathf.SmoothStep(0,1,Mathf.InverseLerp(.025f,.26f,distance));
                coverage=Mathf.Max(coverage,fade*strengths[r]);
                float w=Mathf.Pow(fade,8)*strengths[r];local+=regionColors[r]*w;weight+=w;
                if(r<3)major=Mathf.Max(major,Mathf.Pow(fade,6));
            }
            coverageScratch[i]=new Vector2(coverage,major);
            colorScratch[i]=weight>.00001f?local/weight:Color.black;
        }
        coverageCursor=end;
        if(coverageCursor<vertices.Length)return;
        coverageCursor=-1;lastCoverage=Time.unscaledTime;
        if(sliceKey!=main.currentKey||sliceMinor!=main.MinorMode){maskKey=-1;return;}   // the key moved on while slicing
        System.Array.Copy(coverageScratch,tonalCoverage,tonalCoverage.Length);System.Array.Copy(colorScratch,localColors,localColors.Length);
        mesh.uv2=tonalCoverage;mesh.colors=localColors;
    }
    static float SegmentDistance(Vector3 p,Vector3 a,Vector3 b)
    {
        Vector3 edge=b-a;
        return Vector3.Distance(p,a+edge*Mathf.Clamp01(Vector3.Dot(p-a,edge)/Mathf.Max(edge.sqrMagnitude,.000001f)));
    }
    static float TriangleDistance(Vector3 p,Vector3 a,Vector3 b,Vector3 c)
    {
        Vector3 ab=b-a,ac=c-a,normal=Vector3.Cross(ab,ac);
        float norm=normal.sqrMagnitude;
        if(norm>.000001f)
        {
            Vector3 q=p-normal*(Vector3.Dot(p-a,normal)/norm),aq=q-a;
            float u=Vector3.Dot(Vector3.Cross(aq,ac),normal)/norm;
            float v=Vector3.Dot(Vector3.Cross(ab,aq),normal)/norm;
            if(u>=0 && v>=0 && u+v<=1)return Vector3.Distance(p,q);
        }
        return Mathf.Min(SegmentDistance(p,a,b),Mathf.Min(SegmentDistance(p,b,c),SegmentDistance(p,c,a)));
    }
    void LateUpdate()
    {
        using var perf=Perf.Field.Auto();
        if(main==null)return;
        
        UpdateGeometry();UpdateCoverage();rendererComponent.enabled=main.ShowSurfaces;if(occluder!=null)occluder.enabled=main.ShowSurfaces;
        HarmonicSpectrum.Accumulate(main.ActiveNotes,target,main.ShowHarmonics);
        if(integratedFrame!=Time.frameCount)Integrate(main.ActiveNotes,Time.unscaledDeltaTime);
        for(int pc=0;pc<12;pc++)
        {
            Color c=TonalColorField.Pitch(pc,main.currentKey); colors[pc]=new Vector4(c.r,c.g,c.b,1);
            excitation[pc].x=memory.Energy[pc];
            int rel=HarmonyModel.Mod(pc-main.CollectionRoot);
            excitation[pc].y=rel is 0 or 2 or 4 or 5 or 7 or 9 or 11?1:0;
        }
        material.SetVectorArray("_Colors",colors);material.SetVectorArray("_Excitation",excitation);
        var dominance=main.GetComponent<TonalDominance>();
        if(dominance!=null){Color c=dominance.Hue;material.SetVector("_Primary",new Vector4(c.r,c.g,c.b,dominance.Energy));material.SetFloat("_Dominance",dominance.Influence);}
        material.SetFloat("_Key",main.currentKey);material.SetFloat("_Unfold",main.UncoilAmount);material.SetFloat("_Opacity",main.FieldDensity*Mathf.Lerp(1,1.8f,main.UncoilAmount));material.SetFloat("_Flow",Main.ReducedMotion?0:1);
        material.SetFloat("_Diatonic",main.DiatonicStrip?1:0);material.SetFloat("_SoundingOnly",main.SoundingOnly?1:0);
    }
    void OnDestroy(){if(mesh!=null)Destroy(mesh);if(material!=null)Destroy(material);if(occluder!=null)Destroy(occluder.sharedMaterial);}
}
