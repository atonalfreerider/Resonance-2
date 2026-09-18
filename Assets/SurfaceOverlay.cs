using UnityEngine;

// Surface identity stays independent of the selected key and sounding chord.
public class SurfaceOverlay : MonoBehaviour
{
    Main main;
    readonly LineRenderer[] lines = new LineRenderer[4];
    readonly TextBox[] labels = new TextBox[4];
    readonly Vector3[] curve = new Vector3[33];
    readonly Vector3[] segment = new Vector3[2];
    void Start()
    {
        main = GetComponent<Main>();
        for (int i=0;i<4;i++)
        {
            var go = new GameObject("Selected surface " + "cedl"[i]); go.transform.SetParent(transform,false);
            lines[i]=go.AddComponent<LineRenderer>(); lines[i].useWorldSpace=true;
            lines[i].sharedMaterial=new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            lines[i].sharedMaterial.SetColor("_BaseColor",new Color(.8f,.88f,1f,.8f));
            lines[i].startWidth=lines[i].endWidth=i<2?.006f:.003f;
            labels[i]=TextBox.Create("cedl"[i].ToString(),TMPro.TextAlignmentOptions.Center);
            labels[i].transform.SetParent(transform,false); labels[i].Size=.36f;
        }
    }
    void LateUpdate()
    {
        int root=main.SelectedSurface;
        for(int i=0;i<4;i++)
        {
            bool show=i<2?main.ShowStructure:main.ShowDiagonals;
            lines[i].enabled=show; labels[i].gameObject.SetActive(show); if(!show)continue;
            if(i==0)
            {
                for(int j=0;j<curve.Length;j++)curve[j]=main.SurfaceCurve(root,root+7,j/(float)(curve.Length-1));
                lines[i].positionCount=curve.Length;lines[i].SetPositions(curve);labels[i].transform.position=curve[curve.Length/2];
            }
            else
            {
                int a=i==1?root:i==2?root+4:root+11;
                int b=i==1?root+4:i==2?root+7:root;
                segment[0]=main.SurfaceCurve(a,a,0);segment[1]=main.SurfaceCurve(b,b,0);
                lines[i].positionCount=2;lines[i].SetPositions(segment);labels[i].transform.position=(segment[0]+segment[1])*.5f;
            }
            labels[i].Billboard();
        }
    }
    void OnDestroy(){foreach(var line in lines)if(line!=null)Destroy(line.sharedMaterial);}
}
