using UnityEngine;

public static class TonalColorField
{
    public static readonly Color Tonic = new(.06f,.16f,1f);
    public static readonly Color Subdominant = new(1f,.045f,.09f);
    public static readonly Color Dominant = new(.07f,1f,.16f);
    public static Color Pitch(int pitch,int key)
    {
        int fifth=HarmonyModel.Mod((pitch-key)*7);
        float blue=Weight(fifth), red=Weight(fifth+1), green=Weight(fifth-1);
        return (Tonic*blue+Subdominant*red+Dominant*green)/(blue+red+green);
    }
    static float Weight(float fifthDistance)
    {
        float d=.018f+1-Mathf.Cos(fifthDistance*Mathf.PI/6);
        return 1/(d*d);
    }
}
