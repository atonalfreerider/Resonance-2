using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Main))]
public class InputHandler : MonoBehaviour
{
    private Main main;
    private MidiPlayer midiPlayer;

    void Awake()
    {
        main = GetComponent<Main>();
        midiPlayer = GetComponent<MidiPlayer>();
    }

    void Update()
    {
        if (Keyboard.current.aKey.wasPressedThisFrame) main.ChangeKey(0);
        if (Keyboard.current.bKey.wasPressedThisFrame) main.ChangeKey(2);
        if (Keyboard.current.cKey.wasPressedThisFrame) main.ChangeKey(3);
        if (Keyboard.current.dKey.wasPressedThisFrame) main.ChangeKey(5);
        if (Keyboard.current.eKey.wasPressedThisFrame) main.ChangeKey(7);
        if (Keyboard.current.fKey.wasPressedThisFrame) main.ChangeKey(8);
        if (Keyboard.current.gKey.wasPressedThisFrame) main.ChangeKey(10);

        if (midiPlayer != null && midiPlayer.IsPlaying) return;

        List<int> keys = new();
        if (Keyboard.current.digit1Key.isPressed) keys.Add(0);
        if (Keyboard.current.digit2Key.isPressed) keys.Add(1);
        if (Keyboard.current.digit3Key.isPressed) keys.Add(2);
        if (Keyboard.current.digit4Key.isPressed) keys.Add(3);
        if (Keyboard.current.digit5Key.isPressed) keys.Add(4);
        if (Keyboard.current.digit6Key.isPressed) keys.Add(5);
        if (Keyboard.current.digit7Key.isPressed) keys.Add(6);
        if (Keyboard.current.digit8Key.isPressed) keys.Add(7);
        if (Keyboard.current.digit9Key.isPressed) keys.Add(8);
        if (Keyboard.current.digit0Key.isPressed) keys.Add(9);
        if (Keyboard.current.minusKey.isPressed) keys.Add(10);
        if (Keyboard.current.equalsKey.isPressed) keys.Add(11);

        if (keys.Any() || Keyboard.current.anyKey.wasReleasedThisFrame)
        {
            var adjusted = keys.Select(k => new Tuple<int, float>((k + main.currentKey) % Main.Tones +  36, 1f)).ToList();
            main.PlayKeys(adjusted);
        }
    }
}