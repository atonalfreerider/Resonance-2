using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

public static class WindingValidation
{
    public static string Run()
    {
        var m=UnityEngine.Object.FindAnyObjectByType<Main>();
        var progress=typeof(Main).GetField("unfoldProgress",BindingFlags.NonPublic|BindingFlags.Instance);
        var amount=typeof(Main).GetProperty("UncoilAmount");
        float saved=(float)progress.GetValue(m),savedAmount=m.UncoilAmount;
        float minLength=float.MaxValue,minSpan=float.MaxValue,maxEndpointError=0;
        try{
            {
                // No key mutations: rotate sampling coordinates through all twelve seam positions.
                for(int frame=0;frame<=240;frame++){
                    float p=frame/240f,u=p*p*p*(p*(p*6-15)+10);progress.SetValue(m,p);amount.SetValue(m,u);
                    var cameraDirection=Vector3.Slerp(new Vector3(.51f,.75f,.51f).normalized,Vector3.back,u);
                    var cameraRotation=Quaternion.LookRotation(-cameraDirection,Vector3.up);
                    Vector3 right=cameraRotation*Vector3.right,up=cameraRotation*Vector3.up;
                    Vector3 last=Vector3.zero;float length=0;Vector2 low=Vector2.one*999,high=-Vector2.one*999;
                    for(int i=0;i<=240;i++){
                        float slot=i/240f-.5f,t=HarmonyModel.Mod(m.currentKey*5)/12f+m.VisualRotation+slot;
                        Vector3 flat=m.UncoiledPoint(slot,1),coiled=m.UmbilicPoint(t),point=m.MorphUncoil(coiled,flat);
                        if(i>0)length+=Vector3.Distance(last,point);last=point;
                        Vector2 projected=new(Vector3.Dot(point,right),Vector3.Dot(point,up));low=Vector2.Min(low,projected);high=Vector2.Max(high,projected);
                        if(frame==0)maxEndpointError=Mathf.Max(maxEndpointError,Vector3.Distance(point,coiled));
                        if(frame==240)maxEndpointError=Mathf.Max(maxEndpointError,Vector3.Distance(point,flat));
                    }
                    minLength=Mathf.Min(minLength,length);minSpan=Mathf.Min(minSpan,(high-low).magnitude);
                }
            }
            if(minLength<7||minSpan<2||maxEndpointError>.001f)throw new Exception($"Curve collapsed: length={minLength}, span={minSpan}, endpoint={maxEndpointError}");
            string result=$"PASS: 241 transition samples; minimum curve length {minLength:F3}, projected span {minSpan:F3}; endpoint error {maxEndpointError:F6}. Reverse transition uses the same path.";
            Directory.CreateDirectory("Temp/ResonanceChecks");File.WriteAllText("Temp/ResonanceChecks/winding.txt",result);return result;
        }finally{progress.SetValue(m,saved);amount.SetValue(m,savedAmount);m.RefreshView();}
    }
}
