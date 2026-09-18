using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Main))]
public class InputHandler : MonoBehaviour
{
    Main main;
    MidiPlayer midi;
    string previous = "";
    void Awake(){main=GetComponent<Main>();midi=GetComponent<MidiPlayer>();}
    void Update()
    {
        var kb=Keyboard.current;
        if(kb==null || !ExplorerInputFocus.ViewportOwnsKeyboard) { ReleaseKeyboardNotes(); return; }
        if(kb.aKey.wasPressedThisFrame || kb.bKey.wasPressedThisFrame || kb.cKey.wasPressedThisFrame || kb.dKey.wasPressedThisFrame || kb.eKey.wasPressedThisFrame || kb.fKey.wasPressedThisFrame || kb.gKey.wasPressedThisFrame)main.KeySource="Manual";
        if(kb.aKey.wasPressedThisFrame)main.ChangeKey(0);
        if(kb.bKey.wasPressedThisFrame)main.ChangeKey(2);
        if(kb.cKey.wasPressedThisFrame)main.ChangeKey(3);
        if(kb.dKey.wasPressedThisFrame)main.ChangeKey(5);
        if(kb.eKey.wasPressedThisFrame)main.ChangeKey(7);
        if(kb.fKey.wasPressedThisFrame)main.ChangeKey(8);
        if(kb.gKey.wasPressedThisFrame)main.ChangeKey(10);
        if((midi!=null && midi.IsPlaying) || (GetComponent<HarmonyExplorer>()?.LessonPlaying??false) || (GetComponent<LiveMidiInput>()?.Connected??false)){previous="";return;}
        var controls=new[]{kb.digit1Key,kb.digit2Key,kb.digit3Key,kb.digit4Key,kb.digit5Key,kb.digit6Key,kb.digit7Key,kb.digit8Key,kb.digit9Key,kb.digit0Key,kb.minusKey,kb.equalsKey};
        var keys=new List<Tuple<int,float>>();
        for(int i=0;i<controls.Length;i++)if(controls[i].isPressed)keys.Add(Tuple.Create(HarmonyModel.Mod(i+main.currentKey)+36,.7f));
        string signature=string.Join(",",keys.Select(k=>k.Item1));
        if(signature!=previous){main.PlayKeys(keys);previous=signature;}
    }
    public void ReleaseKeyboardNotes()
    {
        if(previous.Length==0)return;
        previous=""; main.Silence();
    }
    void OnApplicationFocus(bool focus){if(!focus){previous="";main.Silence();}}
}
