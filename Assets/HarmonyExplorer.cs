using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

public class HarmonyExplorer : MonoBehaviour
{
    public bool LessonPlaying { get; private set; }
    Main main; MidiPlayer midi; UIDocument document; PanelSettings settings;
    LiveMidiInput live;
    int sequenceStart;
    Label details, trace, sounding, midiStatus, clock, analysis, historyLabel;
    Label focusHint;
    PatternWheelDeck orrery;
    TextField grammar, path;
    DropdownField keyChoice, surfaceChoice, modeChoice;
    Slider seek;
    readonly List<string> history = new();
    List<HarmonyModel.Frame> frames;
    int step = -1;
    float bpm = 90;
    double nextStep;
    bool loopLesson;
    string lastChord;
    public string LastTrace { get; private set; } = "";
    void Start()
    {
        main=GetComponent<Main>(); midi=GetComponent<MidiPlayer>();
        live=gameObject.AddComponent<LiveMidiInput>();
        settings=ScriptableObject.CreateInstance<PanelSettings>(); settings.scaleMode=PanelScaleMode.ScaleWithScreenSize;
        settings.themeStyleSheet=Resources.Load<ThemeStyleSheet>("HarmonyTheme");
        settings.referenceResolution=new Vector2Int(1280,800); settings.match=.5f;
        document=gameObject.AddComponent<UIDocument>(); document.panelSettings=settings;
        document.visualTreeAsset=Resources.Load<VisualTreeAsset>("HarmonyExplorer");
        gameObject.AddComponent<ExplorerInputFocus>().Bind(document);
        var root=document.rootVisualElement;
        root.pickingMode=PickingMode.Ignore;
        root.style.unityFont=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        var panel=root.Q<ScrollView>("controls");
        panel.RegisterCallback<GeometryChangedEvent>(_ => { float fraction=Mathf.Clamp(panel.worldBound.width/root.worldBound.width,0,.5f); if(Camera.main!=null)Camera.main.rect=new Rect(fraction,0,1-fraction,1); });
        focusHint=Label(panel,"Click the torus to play / orbit. Click this panel to edit.");
        focusHint.AddToClassList("focus-hint");
        var dominance=GetComponent<TonalDominance>()??gameObject.AddComponent<TonalDominance>();
        var recording=GetComponent<SongAudio>()??gameObject.AddComponent<SongAudio>();
        panel.Add(recording.BuildUI());
        Section(panel,"PATTERN CD CHANGER");
        orrery=new PatternWheelDeck(main,midi);panel.Add(orrery);orrery.AttachOverlay(root);
        Section(panel,"TONAL CONTEXT");
        keyChoice=Choice(panel,"Key / tonic",Enumerable.Range(0,12).Select(i=>HarmonyModel.Name(i)).ToList(),main.currentKey,i=> { midi?.Pause(); main.KeySource="Manual"; main.ChangeKey(i); });
        modeChoice=Choice(panel,"Mode",new List<string>{"Major","Natural minor"},0,i=> { main.MinorMode=i==1; main.KeySource="Manual"; main.RefreshView(); Refresh(); });
        foreach(var role in new[]{("I / tonic — BLUE","tonic"),("IV / subdominant — RED","subdominant"),("V / dominant — GREEN","dominant")})
        { var label=new Label(role.Item1); label.AddToClassList("legend"); label.AddToClassList(role.Item2); panel.Add(label); }
        Section(panel,"SURFACE & OBJECTS");
        surfaceChoice=Choice(panel,"Surface (independent of key)",Enumerable.Range(0,12).Select(i=>HarmonyModel.Name(i)+"'").ToList(),main.SelectedSurface,i=>SelectSurface(i));
        details=Label(panel,"");
        var objects=Row(panel);
        foreach(char token in "TMmcedl") { char selected=token; Button(objects,token.ToString(),()=>Audition(selected)); }
        Button(panel,"Silence / release sustain",()=> { StopLesson(); midi?.Pause(); main.Silence(); });
        sounding=Label(panel,""); analysis=Label(panel,"");
        Section(panel,"GUIDED MOTION");
        Choice(panel,"Lesson",new List<string>{"ii – V – I","I – IV – V – I","I – vi – IV – V","V – i","Major → parallel minor"},0,LoadLesson);
        grammar=new TextField("Sequence"){value="0m > PM > M"}; panel.Add(grammar);
        var transport=Row(panel);
        Button(transport,"Prepare",Prepare); Button(transport,"◀ Back",()=>Step(-1)); Button(transport,"Step ▶",()=>Step(1));
        Button(transport,"Play",()=> { Prepare(); if(frames!=null) { LessonPlaying=true; nextStep=Time.unscaledTimeAsDouble; } });
        Button(transport,"Stop",StopLesson);
        Slider(panel,"Tempo",40,180,bpm,v=>bpm=v);
        Toggle(panel,"Loop lesson",false,v=>loopLesson=v);
        trace=Label(panel,"Choose a lesson or enter tokens. > starts a new chord; (objects) sustain until Stop. Grammar v1 is experimental.");
        Section(panel,"VIEW");
        Toggle(panel,"Continuous tonal field",true,v=>{main.ShowSurfaces=v; main.RefreshView();});
        Toggle(panel,"Harmonic partials build light",true,v=>main.ShowHarmonics=v);
        Slider(panel,"Field density",.1f,1.5f,main.FieldDensity,v=>main.FieldDensity=v);
        Slider(panel,"Resonance half-life (seconds)",.15f,6,main.ResonanceHalfLife,v=>main.ResonanceHalfLife=v);
        Slider(panel,"Note fade (seconds)",.15f,2,main.NoteReleaseSeconds,v=>main.NoteReleaseSeconds=v);
        Slider(panel,"Chord energy decay (seconds)",.3f,6,main.VisualReleaseSeconds,v=>main.VisualReleaseSeconds=v);
        Slider(panel,"Primary tone color influence",0,1,dominance.Influence,v=>dominance.Influence=v);
        Slider(panel,"Energy influence",.1f,3,main.ResonanceGain,v=>main.ResonanceGain=v);
        Button(panel,"Clear lingering vibrations",main.ClearVisualMemory);
        Label(panel,"Sustained notes accumulate energy. MIDI velocity and master volume scale the color wash; after release, energy halves over the selected time.");
        Label(panel,"Position blends blue I, red IV and green V. Light adds the first eight ideal partials, folded to pitch classes; it is not a spectrum measurement.");
        Toggle(panel,"Diatonic emphasis (soft)",false,v=>{main.DiatonicStrip=v; main.RefreshView();});
        Toggle(panel,"Selected-surface guide",false,v=>main.SurfaceGuide=v);
        Toggle(panel,"Structural wireframe",true,v=>{main.ShowStructure=v; main.RefreshView();});
        Toggle(panel,"Sounding diagonals d / l",true,v=>{main.ShowDiagonals=v; main.RefreshView();});
        Toggle(panel,"All registers",true,v=>{main.ShowRegisters=v; main.RefreshView();});
        Toggle(panel,"Sounding notes only",false,v=>{main.SoundingOnly=v; main.RefreshView();});
        Toggle(panel,"Flat spelling",false,v=>{main.UseFlats=v; main.RefreshView(); Refresh();});
        Toggle(panel,"Reduced motion",false,v=>{Main.ReducedMotion=v; main.ChangeKey(main.currentKey,0);});
        Button(panel,"Reset camera",()=>Camera.main.GetComponent<CameraControl>()?.ResetView());
        Slider(panel,"Master volume",0,1,main.Synth.Volume,v=>main.Synth.Volume=v);
        Section(panel,"MIDI ONLY / FILTERS");
        Choice(panel,"Live MIDI input (Windows)",LiveMidiInput.Devices(),0,i=>live.Connect(i-1));
        path=new TextField("File path"){value=midi!=null?midi.midiPath:""}; panel.Add(path);
        Button(panel,"Open MIDI…",OpenMidi);
        Button(panel,"Load path",()=>{StopLesson(); midi?.Load(path.value);});
        var mt=Row(panel); Button(mt,"Play / pause",()=>{if(midi.IsPlaying)midi.Pause();else midi.Play();}); Button(mt,"Stop",()=>midi.Stop());
        seek=Slider(panel,"Position",0,1,0,v=>{if(midi.Loaded)midi.Seek(v*midi.Duration);});
        clock=Label(panel,""); midiStatus=Label(panel,"");
        Slider(panel,"MIDI-only speed (recording = 1×)",.25f,2,1,v=>midi.SetSpeed(v));
        Toggle(panel,"Loop song",false,v=>midi.Loop=v);
        Toggle(panel,"Follow declared key",true,v=>midi.FollowKey=v);
        Label(panel,"Percussion is displayed only on the world-space drum deck.");
        Choice(panel,"Channel",new[]{"All"}.Concat(Enumerable.Range(1,16).Select(i=>i.ToString())).ToList(),0,i=>{midi.ChannelFilter=i;ReloadMidi();});
        var track=new IntegerField("Track (0 = all)"){value=0}; panel.Add(track); track.RegisterValueChangedCallback(e=>{midi.TrackFilter=e.newValue-1;ReloadMidi();});
        Section(panel,"HARMONIC HISTORY"); historyLabel=Label(panel,"");
        Button(panel,"Export progression",Export);
        Label(panel,"Click torus: instrument shortcuts. Click panel / Tab: UI shortcuts.\nInstrument: 1–0, −, = notes · A–G tonic\nArrows orbit / zoom · PgUp / PgDn elevation\nRight-drag orbit · Wheel zoom\nSpace MIDI · Esc silence\nCtrl+Esc always stops sound.\nClick a note sphere to inspect its surface.");
        main.StateChanged+=Refresh;
        LoadLesson(0); Refresh();
    }
    void ReloadMidi() { midi.ApplyFilters(); }
    void OpenMidi()
    {
#if UNITY_EDITOR
        string file=UnityEditor.EditorUtility.OpenFilePanel("Open MIDI","","mid,midi");
        if(!string.IsNullOrEmpty(file)){path.value=file; StopLesson(); midi.Load(file);}
#else
        midiStatus.text="Paste a .mid/.midi file path above, then Load path.";
#endif
    }
    public void SelectSurface(int pc) { if(LessonPlaying)StopLesson(); sequenceStart=HarmonyModel.Mod(pc); frames=null; step=-1; main.SelectedSurface=sequenceStart; surfaceChoice.SetValueWithoutNotify(surfaceChoice.choices[main.SelectedSurface]); Refresh(); }
    void Audition(char token)
    {
        StopLesson(); midi?.Pause(); live.Disconnect();
        var pcs=HarmonyModel.Object(main.SelectedSurface,token);
        main.PlayKeys(pcs.Select(pc=>Tuple.Create(pc+36,.7f)).ToList());
        LastTrace=$"{token} on {main.PitchName(main.SelectedSurface)}' → {HarmonyModel.Notes(pcs,main.UseFlats)}";
        trace.text=LastTrace;
    }
    void LoadLesson(int index)
    {
        if(grammar==null) return;
        StopLesson();
        int key=main.currentKey;
        var starts=new[]{key+10,key,key,key+7,key};
        var sequences=new[]{"0m > PM > M","0M > M > 2PM > M","0M > m > 0M > 2PM","0M > Sm","0M > 0Sm"};
        SelectSurface(starts[index]); grammar.value=sequences[index]; frames=null; step=-1;
    }
    void Prepare()
    {
        StopLesson(); midi?.Pause();
        live.Disconnect();
        try { frames=HarmonyModel.Evaluate(sequenceStart,grammar.value); step=-1; trace.text=$"Ready: {frames.Count} steps. Start {main.PitchName(sequenceStart)}'."; }
        catch(Exception e) { frames=null; trace.text=e.Message; }
    }
    void Step(int delta)
    {
        if(frames==null)Prepare(); if(frames==null)return;
        step=Math.Clamp(step+delta,0,frames.Count-1); var frame=frames[step];
        var previous=new HashSet<int>(main.ActiveNotes.Select(n=>HarmonyModel.Mod(n.Item1)));
        main.SelectedSurface=frame.Surface; surfaceChoice.SetValueWithoutNotify(surfaceChoice.choices[frame.Surface]);
        main.PlayKeys(frame.Pitches.Select(pc=>Tuple.Create(pc+36,.7f)).ToList());
        LastTrace=$"{step+1}/{frames.Count}  {frame.Trace}\nShared tones: {HarmonyModel.Notes(frame.Pitches.Where(previous.Contains),main.UseFlats)}";
        trace.text=LastTrace;
    }
    public void StopLesson(){LessonPlaying=false; if(main!=null)main.Silence();}
    void Refresh()
    {
        if(details==null)return;
        keyChoice.SetValueWithoutNotify(keyChoice.choices[main.currentKey]); modeChoice.SetValueWithoutNotify(modeChoice.choices[main.MinorMode?1:0]);
        int s=main.SelectedSurface;
        var pcs=main.ActiveNotes.Select(n=>HarmonyModel.Mod(n.Item1)).Distinct().ToArray();
        bool d=HarmonyModel.Object(s,'d').All(pcs.Contains), l=HarmonyModel.Object(s,'l').All(pcs.Contains), e=HarmonyModel.Object(s,'e').All(pcs.Contains);
        details.text=$"{main.PitchName(s)}' surface: {HarmonyModel.Notes(HarmonyModel.Surface(s),main.UseFlats)}\nM = {main.PitchName(s)} major · m = {main.PitchName(s+4)} minor\nc root–5th · e root–3rd · d 3rd–5th · l 7th–root\nDiagonal: {(d||l?"sounding":"absent")} · e border: {(e?"saturated":"open")}";
        string chord=HarmonyModel.Chord(pcs,main.UseFlats);
        sounding.text=$"Sounding: {chord}\n{HarmonyModel.Notes(pcs,main.UseFlats)}\nKey source: {main.KeySource}";
        if(main.ActiveNotes.Count>0) sounding.text+=$"\nBass: {main.PitchName(main.ActiveNotes.Min(n=>n.Item1))}";
        var candidates=HarmonyModel.CompatibleCollections(pcs);
        analysis.text=pcs.Length==0?"Collection compatibility: no evidence":candidates.Count==0?"Strict collection: none (chromatic set)":"Compatible collections: "+string.Join(", ",candidates.Select(r=>$"{main.PitchName(r)} / {main.PitchName(r+9)}m"));
        if(chord!=lastChord && pcs.Length>0){lastChord=chord;history.Add(chord);if(history.Count>64)history.RemoveAt(0);historyLabel.text=string.Join(" → ",history.Skip(Math.Max(0,history.Count-8)));}
    }
    void Update()
    {
        if(main==null)return;
        orrery?.Tick();
        if(focusHint!=null)focusHint.text=ExplorerInputFocus.ViewportOwnsKeyboard?"TORUS CONTROLS · click panel to edit":"UI CONTROLS · click torus to play / orbit";
        if(Keyboard.current!=null && Keyboard.current.escapeKey.wasPressedThisFrame && (ExplorerInputFocus.ViewportOwnsKeyboard || Keyboard.current.ctrlKey.isPressed)) { StopLesson();midi?.Stop();live.Disconnect();main.Silence(); }
        if(LessonPlaying && Time.unscaledTimeAsDouble>=nextStep)
        {
            if(step>=frames.Count-1){if(loopLesson)step=-1;else{StopLesson();return;}}
            Step(1);nextStep=Time.unscaledTimeAsDouble+60/bpm;
        }
        if(midiStatus!=null){midiStatus.text=midi.Status+"\n"+live.Status;clock.text=$"{midi.Position:0.0} / {midi.Duration:0.0} sec";seek.SetValueWithoutNotify(midi.Duration>0?(float)(midi.Position/midi.Duration):0);}
        if(Mouse.current!=null && Mouse.current.leftButton.wasPressedThisFrame && Mouse.current.position.ReadValue().x>Camera.main.pixelRect.xMin)
        {
            Ray ray=Camera.main.ScreenPointToRay(Mouse.current.position.ReadValue());
            if(Physics.Raycast(ray,out var hit) && hit.collider.GetComponentInParent<Note>() is Note note)SelectSurface(note.Index%12);
        }
    }
    void Export()
    {
        try { string file=Path.Combine(Application.persistentDataPath,"resonance-progression.txt"); File.WriteAllText(file,$"Key: {main.PitchName(main.currentKey)} {(main.MinorMode?"minor":"major")}\nSequence: {grammar.value}\nHistory: {string.Join(" → ",history)}\n");trace.text="Saved: "+file; }
        catch(Exception e){trace.text="Export: "+e.Message;}
    }
    void OnApplicationFocus(bool focus){if(!focus)StopLesson();}
    void OnDestroy(){if(main!=null)main.StateChanged-=Refresh;if(settings!=null)Destroy(settings);if(Camera.main!=null)Camera.main.rect=new Rect(0,0,1,1);}
    static void Section(VisualElement p,string text){var l=new Label(text);l.AddToClassList("section");p.Add(l);}
    static Label Label(VisualElement p,string text){var l=new Label(text);l.AddToClassList("details");p.Add(l);return l;}
    static VisualElement Row(VisualElement p){var r=new VisualElement();r.AddToClassList("row");p.Add(r);return r;}
    static void Button(VisualElement p,string text,Action action){p.Add(new Button(action){text=text});}
    static void Toggle(VisualElement p,string text,bool value,Action<bool> action){var t=new Toggle(text){value=value};t.RegisterValueChangedCallback(e=>action(e.newValue));p.Add(t);}
    static Slider Slider(VisualElement p,string text,float low,float high,float value,Action<float> action){var s=new Slider(text,low,high){value=value};s.RegisterValueChangedCallback(e=>action(e.newValue));p.Add(s);return s;}
    static DropdownField Choice(VisualElement p,string label,List<string> choices,int index,Action<int> action){var d=new DropdownField(label,choices,index);d.RegisterValueChangedCallback(_=>action(d.index));p.Add(d);return d;}
}
