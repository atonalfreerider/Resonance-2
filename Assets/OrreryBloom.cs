using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UIElements;

// An HDR glow pass through the same URP volume as the torus, with transparent output.
public sealed class OrreryBloom : System.IDisposable
{
    static int nextId;
    readonly GameObject go;
    readonly Camera camera;
    readonly Mesh mesh;
    readonly Material material,composite;
    readonly RenderTexture hdr;
    public readonly RenderTexture Texture;
    readonly GameObject display;
    readonly PanelSettings panelSettings;
    readonly VisualElement displayRoot,clip;
    readonly Image image;
    readonly List<Vector3> vertices=new();
    readonly List<Color> colors=new();
    readonly List<int> indices=new();
    float width=1,height=1;
    public OrreryBloom(Main owner)
    {
        go=new GameObject("Orrery HDR bloom"){hideFlags=HideFlags.HideAndDontSave,layer=31};
        go.transform.position=new Vector3(20000+1000*nextId++,20000,0);
        mesh=new Mesh{name="Orrery energized geometry"};mesh.MarkDynamic();
        go.AddComponent<MeshFilter>().sharedMesh=mesh;
        material=new Material(Resources.Load<Shader>("OrreryEmission"));
        go.AddComponent<MeshRenderer>().sharedMaterial=material;
        var camGo=new GameObject("Orrery bloom camera"){hideFlags=HideFlags.HideAndDontSave};camGo.transform.SetParent(go.transform,false);
        camGo.transform.localPosition=new Vector3(0,0,-10);camera=camGo.AddComponent<Camera>();
        camera.orthographic=true;camera.orthographicSize=1;camera.nearClipPlane=.1f;camera.farClipPlane=20;
        camera.cullingMask=1<<31;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.clear;
        camera.allowHDR=true;camera.allowMSAA=false;camera.depth=-20;
        var data=camera.GetUniversalAdditionalCameraData();data.renderPostProcessing=true;
        data.volumeLayerMask=Camera.main.GetUniversalAdditionalCameraData().volumeLayerMask;
        data.renderShadows=false;
        hdr=new RenderTexture(384,384,24,RenderTextureFormat.ARGBHalf){name="Orrery HDR source"};hdr.Create();camera.targetTexture=hdr;
        Texture=new RenderTexture(384,384,0,RenderTextureFormat.ARGBHalf){name="Orrery transparent bloom"};Texture.Create();
        composite=new Material(Resources.Load<Shader>("OrreryTransparent"));
        // Separate texture-only panel: sharing a render-texture batch with the
        // controls corrupts clipping in this Unity version.
        display=new GameObject("Orrery transparent display"){hideFlags=HideFlags.HideAndDontSave};
        panelSettings=Object.Instantiate(owner.GetComponent<UIDocument>().panelSettings);panelSettings.sortingOrder+=1;
        var document=display.AddComponent<UIDocument>();document.panelSettings=panelSettings;
        displayRoot=document.rootVisualElement;displayRoot.pickingMode=PickingMode.Ignore;
        clip=new VisualElement{pickingMode=PickingMode.Ignore};clip.style.position=Position.Absolute;clip.style.overflow=Overflow.Hidden;displayRoot.Add(clip);
        image=new Image{image=Texture,pickingMode=PickingMode.Ignore,scaleMode=ScaleMode.StretchToFill};image.style.position=Position.Absolute;clip.Add(image);
        RenderPipelineManager.endCameraRendering+=Composite;
    }
    public void Place(VisualElement owner)
    {
        Rect bounds=owner.worldBound,limit=owner.panel?.visualTree.worldBound??Rect.zero;
        var scroll=owner.GetFirstAncestorOfType<ScrollView>();if(scroll!=null)limit=scroll.contentViewport.worldBound;
        float left=Mathf.Max(bounds.xMin,limit.xMin),top=Mathf.Max(bounds.yMin,limit.yMin);
        float right=Mathf.Min(bounds.xMax,limit.xMax),bottom=Mathf.Min(bounds.yMax,limit.yMax);
        bool visible=owner.visible&&owner.resolvedStyle.display!=DisplayStyle.None&&right>left&&bottom>top;
        camera.enabled=visible;displayRoot.style.display=visible?DisplayStyle.Flex:DisplayStyle.None;
        if(!visible)return;
        clip.style.left=left;clip.style.top=top;clip.style.width=right-left;clip.style.height=bottom-top;
        image.style.left=bounds.x-left;image.style.top=bounds.y-top;image.style.width=bounds.width;image.style.height=bounds.height;
    }
    public void Begin(float w,float h,bool visible)
    {
        width=Mathf.Max(1,w);height=Mathf.Max(1,h);vertices.Clear();colors.Clear();indices.Clear();camera.enabled=visible;
    }
    Vector3 Point(Vector2 p)=>new((p.x/width-.5f)*2,(.5f-p.y/height)*2,0);
    void Triangle(Vector2 a,Vector2 b,Vector2 c,Color color)
    {
        int n=vertices.Count;vertices.Add(Point(a));vertices.Add(Point(b));vertices.Add(Point(c));
        colors.Add(color);colors.Add(color);colors.Add(color);indices.Add(n);indices.Add(n+1);indices.Add(n+2);
    }
    public void Disk(Vector2 center,float radius,Color color,float power)
    {
        if(power<.005f)return;color*=power*5;
        for(int i=0;i<32;i++){float a=i*Mathf.PI/16,b=(i+1)*Mathf.PI/16;Triangle(center,center+new Vector2(Mathf.Cos(a),Mathf.Sin(a))*radius,center+new Vector2(Mathf.Cos(b),Mathf.Sin(b))*radius,color);}
    }
    public void Arc(Vector2 center,float radius,double start,double end,Color color,float power)
    {
        if(power<.005f||end<=start)return;color*=power*4;
        int steps=Mathf.Max(2,Mathf.CeilToInt((float)(end-start)*160));float thickness=2;
        Vector2 P(double phase,float r){float angle=(float)phase*Mathf.PI*2;return center+new Vector2(Mathf.Sin(angle),-Mathf.Cos(angle))*r;}
        for(int i=0;i<steps;i++){double a=start+(end-start)*i/steps,b=start+(end-start)*(i+1)/steps;var p=P(a,radius-thickness);var q=P(a,radius+thickness);var r=P(b,radius+thickness);var s=P(b,radius-thickness);Triangle(p,q,r,color);Triangle(p,r,s,color);}
    }
    public void End(){mesh.Clear();mesh.SetVertices(vertices);mesh.SetColors(colors);mesh.SetTriangles(indices,0);mesh.RecalculateBounds();}
    void Composite(ScriptableRenderContext context,Camera rendered)
    {
        if(rendered!=camera)return;
        var command=CommandBufferPool.Get("Transparent orrery bloom");
        command.Blit(hdr,Texture,composite);
        context.ExecuteCommandBuffer(command);CommandBufferPool.Release(command);
    }
    public void Dispose()
    {
        RenderPipelineManager.endCameraRendering-=Composite;camera.targetTexture=null;
        hdr.Release();Texture.Release();Object.Destroy(hdr);Object.Destroy(Texture);Object.Destroy(mesh);Object.Destroy(material);Object.Destroy(composite);Object.Destroy(go);Object.Destroy(display);Object.Destroy(panelSettings);
    }
}
