using UnityEngine;

public static class TonalColorField
{
    public static readonly Color Tonic = new(.06f,.16f,1f);
    public static readonly Color Subdominant = new(1f,.045f,.09f);
    public static readonly Color Dominant = new(.07f,1f,.16f);
    public static Color Pitch(int pitch,int key)
    {
        // Explicit key-relative hues keep remote/minor degrees from inheriting V's green.
        return HarmonyModel.Mod(pitch-key) switch
        {
            0=>Tonic,5=>Subdominant,7=>Dominant,
            2=>new Color(.58f,.16f,.9f),4=>new Color(.24f,.24f,.86f),9=>new Color(.76f,.09f,.55f),
            1=>new Color(.42f,.16f,.25f),3=>new Color(.48f,.13f,.60f),
            6=>new Color(.3f,.22f,.38f),8=>new Color(.43f,.1f,.38f),
            10=>new Color(.35f,.18f,.5f),_=>new Color(.24f,.3f,.42f)
        };
    }
    public static Color Chord(int root,int key,bool minor)
    {
        var c=Pitch(root,key);
        return minor?Color.Lerp(c,new Color(.7f,.06f,.85f),.55f)*.82f:c;
    }
}
