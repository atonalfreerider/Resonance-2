using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
public static class PortableWindowsBuild
{
    public const string Output="Builds/Resonance-Windows";
    public static void Queue(){EditorApplication.delayCall+=Build;}
    public static void Build(){
        Directory.CreateDirectory(Output);
        try{
            if(EditorApplication.isPlaying)throw new InvalidOperationException("Stop Play mode before building");
            var settings=new SerializedObject(UnityEngine.Rendering.GraphicsSettings.GetGraphicsSettings());var shaders=settings.FindProperty("m_AlwaysIncludedShaders");
            var unlit=Shader.Find("Universal Render Pipeline/Unlit");if(unlit==null)throw new Exception("Required URP Unlit shader is missing");
            bool included=false;for(int i=0;i<shaders.arraySize;i++)included|=shaders.GetArrayElementAtIndex(i).objectReferenceValue==unlit;
            if(!included){int index=shaders.arraySize;shaders.InsertArrayElementAtIndex(index);shaders.GetArrayElementAtIndex(index).objectReferenceValue=unlit;settings.ApplyModifiedPropertiesWithoutUndo();AssetDatabase.SaveAssets();}
            var result=BuildPipeline.BuildPlayer(new BuildPlayerOptions{scenes=new[]{"Assets/Scenes/Resonance.unity"},locationPathName=Output+"/Resonance.exe",target=BuildTarget.StandaloneWindows64,options=BuildOptions.None});
            if(result.summary.result!=BuildResult.Succeeded)throw new Exception("Build failed: "+result.summary.result+", errors="+result.summary.totalErrors);
            string source="PreparedSongs/TicketToRide-Restored",dest=Output+"/PreparedSongs/TicketToRide";Directory.CreateDirectory(dest);
            foreach(string file in new[]{"aligned.mid","aligned.mid.patterns.json","aligned.mid.prepared.json","recording.wav","report.html","fingerprints.png","alignment-audition.wav","analysis.json","timing-map.csv","song.json"})File.Copy(Path.Combine(source,file),Path.Combine(dest,file),true);
            var manifest=JsonUtility.FromJson<SongAudio.Prepared>(File.ReadAllText(dest+"/aligned.mid.prepared.json"));manifest.sourceAudioPath=Path.GetFileName(manifest.sourceAudioPath);File.WriteAllText(dest+"/aligned.mid.prepared.json",JsonUtility.ToJson(manifest,true));
            File.WriteAllText(Output+"/README.txt","RESONANCE - WINDOWS PORTABLE\r\n\r\nExtract the entire folder, then run Resonance.exe. Keep Resonance_Data, UnityPlayer.dll and PreparedSongs beside the executable. No Unity installation is required.\r\n\r\nTicket to Ride loads automatically, paused. Press Play on the left time rack. Drag the rack up/down to seek. Click the torus area before using camera arrow keys; click the controls to edit settings. Alt+F4 exits.\r\n\r\nThe included WAV is the recording decoded from the supplied MP3; the aligned MIDI and saved analysis drive the visuals. All analysis is precomputed. Timing alignment has known weak windows; the bundled report describes them.\r\n");
            File.WriteAllText("Temp/ResonanceChecks/windows-build.txt","SUCCEEDED\n"+Path.GetFullPath(Output+"/Resonance.exe")+"\nBytes: "+result.summary.totalSize+"\nWarnings: "+result.summary.totalWarnings);
        }catch(Exception e){File.WriteAllText("Temp/ResonanceChecks/windows-build.txt","FAILED\n"+e);Debug.LogException(e);}
    }
}
