using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

// A button for fingertips: a flat card with a label, pressed by pushing an index fingertip
// through its face (a poke), with a glow as the finger comes near and a click of colour as it
// presses. It fires once per push and must be backed out of before it fires again. Buttons are
// sized for fingers (a few centimetres), and a disabled one ignores pokes.
public sealed class VrButton : MonoBehaviour
{
    public static readonly List<VrButton> All=new();
    public Action Pressed;
    public Vector2 Size;                // metres
    public bool Interactable=true;
    public Color Idle=new(.12f,.2f,.3f,.9f),Hover=new(.2f,.34f,.48f,.95f),Down=new(.5f,.78f,.95f,1f);
    public TextBox Label;public Renderer Face;public Material Material;
    public Texture2D Stripes;Renderer stripeFace;
    float hover,flash;bool armed=true;
    static Material template;

    public static VrButton Create(Transform parent,string text,Vector2 size,float scale,Action pressed,float fontSize=0)
    {
        var go=new GameObject("VR button · "+text);go.transform.SetParent(parent,false);
        var b=go.AddComponent<VrButton>();b.Size=size;b.Pressed=pressed;
        var quad=GameObject.CreatePrimitive(PrimitiveType.Quad);Destroy(quad.GetComponent<Collider>());quad.transform.SetParent(go.transform,false);
        quad.transform.localScale=new Vector3(size.x,size.y,1)*scale;
        template??=Panel(Color.white);b.Material=new Material(template);quad.GetComponent<Renderer>().sharedMaterial=b.Material;b.Face=quad.GetComponent<Renderer>();
        b.Label=TextBox.Create(text,TextAlignmentOptions.Center);b.Label.transform.SetParent(go.transform,false);
        b.Label.transform.localPosition=new Vector3(0,0,-.002f*scale);b.Label.Size=fontSize>0?fontSize:size.y*.42f*10*scale;   // an explicit size is already in scene units
        b.Label.SetFixedWithWrap(size.x*.92f*scale);b.Label.TextField.rectTransform.sizeDelta=new Vector2(size.x*.92f,size.y*.9f)*scale;b.Label.Alignment=TextAlignmentOptions.Center;
        b.Label.TextField.fontMaterial.renderQueue=3200;b.Label.Color=Color.white;
        All.Add(b);return b;
    }
    public static Material Panel(Color color,bool premultiplied=false)
    {
        var m=new Material(Resources.Load<Shader>("VrPanel"));m.SetColor("_Color",color);
        m.SetFloat("_SrcBlend",(float)(premultiplied?UnityEngine.Rendering.BlendMode.One:UnityEngine.Rendering.BlendMode.SrcAlpha));
        m.SetFloat("_DstBlend",(float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);m.renderQueue=3150;return m;
    }
    // A strip of chord colours along the foot of the card (the song list's preview).
    public void SetStripes(Color[] colors,float scale)
    {
        if(colors==null||colors.Length==0)return;
        if(Stripes==null)
        {
            var quad=GameObject.CreatePrimitive(PrimitiveType.Quad);Destroy(quad.GetComponent<Collider>());quad.transform.SetParent(transform,false);
            quad.transform.localPosition=new Vector3(0,-Size.y*.36f*scale,-.001f*scale);quad.transform.localScale=new Vector3(Size.x*.9f,Size.y*.12f,1)*scale;
            Stripes=new Texture2D(colors.Length,1,TextureFormat.RGBA32,false){filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp};
            var m=Panel(Color.white);m.mainTexture=Stripes;stripeFace=quad.GetComponent<Renderer>();stripeFace.sharedMaterial=m;
        }
        var px=new Color[colors.Length];for(int i=0;i<colors.Length;i++){var c=colors[i];c.a=.9f;px[i]=c;}
        Stripes.SetPixels(px);Stripes.Apply();
        Label.transform.localPosition=new Vector3(0,Size.y*.1f*scale,-.002f*scale);
    }
    public void SetText(string text)=>Label.Text=text;

    // Called once a frame by the menu with the fingertips (world) and the rig's scale.
    public void Poke(IEnumerable<HandInput.Hand> hands,float scale)
    {
        if(!isActiveAndEnabled)return;
        float near=0;bool through=false;bool inside=false;
        foreach(var h in hands)
        {
            if(!h.Tracked)continue;
            var local=transform.InverseTransformPoint(h.Index)/scale;var previous=transform.InverseTransformPoint(h.PreviousIndex)/scale;
            bool over=Mathf.Abs(local.x)<=Size.x*.5f+.004f&&Mathf.Abs(local.y)<=Size.y*.5f+.004f;
            if(!over)continue;
            // The face looks toward -z: in front is negative z, pushed through is positive.
            float depth=-local.z;near=Mathf.Max(near,Mathf.Clamp01(1-depth/.05f));
            if(depth<.006f)inside=true;
            if(armed&&depth<=0&&-previous.z>0&&depth>-.04f)through=true;
        }
        if(through&&Interactable&&armed){armed=false;flash=1;Pressed?.Invoke();}
        if(!inside)armed=true;
        hover=Mathf.MoveTowards(hover,near,Time.unscaledDeltaTime*8);flash=Mathf.MoveTowards(flash,0,Time.unscaledDeltaTime*3);
        var c=Color.Lerp(Color.Lerp(Idle,Hover,hover),Down,flash);if(!Interactable)c=Color.Lerp(c,new Color(.1f,.12f,.14f,.7f),.6f);
        Material.SetColor("_Color",c);
    }
    void OnDestroy(){All.Remove(this);if(Material!=null)Destroy(Material);if(Stripes!=null)Destroy(Stripes);}
}
