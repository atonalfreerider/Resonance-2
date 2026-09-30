using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
using Debug=UnityEngine.Debug;

// The Meta Quest build: hands only, OpenXR, passthrough through Meta's OpenXR package.
//
// Configure sets up only the Android side of the project (the desktop build is untouched):
// the OpenXR loader for Android, the Quest, hand tracking, passthrough (AR session and camera)
// and hand interaction features, and Android player settings (IL2CPP, ARM64, Vulkan, ASTC,
// single-pass instanced). Build configures, then builds the APK to Builds/Quest/Resonance.apk;
// it is meant for batch mode in a separate checkout so the open editor never switches platform:
//
//   Unity.exe -batchmode -quit -projectPath <checkout> -buildTarget Android -executeMethod QuestBuild.Build
//
// Tools/Resonance/Quest/Simulate in editor toggles the headset session in play mode without a
// headset (the mouse is the right index fingertip, the left button pinches).
public static class QuestBuild
{
    public const string Package="com.primitive.resonance";
    public const string Scene="Assets/Scenes/Resonance.unity";
    public const string Output="Builds/Quest/Resonance.apk";
    public const string PipelinePath="Assets/Quest/Resources/QuestPipeline.asset";
    const string SimulateKey="Resonance.SimulateQuest";

    [MenuItem("Tools/Resonance/Quest/Configure Android XR")]
    public static void Configure()
    {
        ConfigureLoader();
        ConfigureFeatures();
        ConfigurePlayer();
        MakePipeline();
        AssetDatabase.SaveAssets();
        Debug.Log("Quest: Android XR configured (OpenXR, Quest, hands, passthrough).");
    }

    static void ConfigureLoader()
    {
        const string path="Assets/XR/XRGeneralSettingsPerBuildTarget.asset";
        if(!EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.settingsKey,out XRGeneralSettingsPerBuildTarget perTarget)||perTarget==null)
        {
            perTarget=AssetDatabase.LoadAssetAtPath<XRGeneralSettingsPerBuildTarget>(path);
            if(perTarget==null)
            {
                Directory.CreateDirectory("Assets/XR");
                perTarget=ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
                AssetDatabase.CreateAsset(perTarget,path);
            }
            EditorBuildSettings.AddConfigObject(XRGeneralSettings.settingsKey,perTarget,true);
        }
        if(!perTarget.HasSettingsForBuildTarget(BuildTargetGroup.Android))perTarget.CreateDefaultSettingsForBuildTarget(BuildTargetGroup.Android);
        if(!perTarget.HasManagerSettingsForBuildTarget(BuildTargetGroup.Android))perTarget.CreateDefaultManagerSettingsForBuildTarget(BuildTargetGroup.Android);
        var general=perTarget.SettingsForBuildTarget(BuildTargetGroup.Android);general.InitManagerOnStart=true;
        var manager=perTarget.ManagerSettingsForBuildTarget(BuildTargetGroup.Android);
        if(!manager.activeLoaders.Any(l=>l is OpenXRLoader))
            XRPackageMetadataStore.AssignLoader(manager,typeof(OpenXRLoader).FullName,BuildTargetGroup.Android);
        EditorUtility.SetDirty(perTarget);EditorUtility.SetDirty(general);EditorUtility.SetDirty(manager);
    }

    static void ConfigureFeatures()
    {
        UnityEditor.XR.OpenXR.Features.FeatureHelpers.RefreshFeatures(BuildTargetGroup.Android);
        var settings=OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
        if(settings==null)throw new Exception("Quest: no OpenXR settings for Android");
        settings.renderMode=OpenXRSettings.RenderMode.SinglePassInstanced;
        // Every feature this app needs, by type name so a missing package fails loudly here.
        string[] wanted=
        {
            "UnityEngine.XR.OpenXR.Features.MetaQuestSupport.MetaQuestFeature",
            "UnityEngine.XR.Hands.OpenXR.HandTracking",
            "UnityEngine.XR.Hands.OpenXR.MetaHandTrackingAim",
            "UnityEngine.XR.OpenXR.Features.Interactions.HandInteractionProfile",
            "UnityEngine.XR.OpenXR.Features.Meta.ARSessionFeature",
            "UnityEngine.XR.OpenXR.Features.Meta.ARCameraFeature",
        };
        var features=settings.GetFeatures();
        foreach(var name in wanted)
        {
            var feature=features.FirstOrDefault(f=>f!=null&&f.GetType().FullName==name);
            if(feature==null)throw new Exception("Quest: OpenXR feature not found: "+name);
            feature.enabled=true;EditorUtility.SetDirty(feature);
        }
        // Controller profiles would make the runtime ask for controllers; this app is hands only.
        foreach(var f in features)
            if(f is OpenXRInteractionFeature&&f.GetType().FullName!="UnityEngine.XR.OpenXR.Features.Interactions.HandInteractionProfile"&&f.enabled)
            {f.enabled=false;EditorUtility.SetDirty(f);}
        EditorUtility.SetDirty(settings);
    }

    static void ConfigurePlayer()
    {
        var android=NamedBuildTarget.Android;
        PlayerSettings.SetApplicationIdentifier(android,Package);
        PlayerSettings.productName="Resonance";
        PlayerSettings.Android.minSdkVersion=AndroidSdkVersions.AndroidApiLevel32;
        PlayerSettings.SetScriptingBackend(android,ScriptingImplementation.IL2CPP);
        PlayerSettings.Android.targetArchitectures=AndroidArchitecture.ARM64;
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android,false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.Android,new[]{GraphicsDeviceType.Vulkan});
        PlayerSettings.defaultInterfaceOrientation=UIOrientation.LandscapeLeft;
        PlayerSettings.colorSpace=ColorSpace.Linear;
        EditorUserBuildSettings.androidBuildSubtarget=MobileTextureSubtarget.ASTC;
    }

    // The headset's render pipeline: the desktop one without HDR (a big cost on a mobile GPU),
    // with 4x MSAA and with alpha kept through post-processing so passthrough shows through the
    // cleared background. VrSession swaps it in on the device only.
    [MenuItem("Tools/Resonance/Quest/Make headset pipeline")]
    public static void MakePipeline()
    {
        var desktop=GraphicsSettings.defaultRenderPipeline;if(desktop==null)return;
        Directory.CreateDirectory(Path.GetDirectoryName(PipelinePath));
        if(AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(PipelinePath)==null)
        {
            var copy=UnityEngine.Object.Instantiate(desktop);AssetDatabase.CreateAsset(copy,PipelinePath);
        }
        var asset=AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(PipelinePath);
        var so=new SerializedObject(asset);
        void Set(string property,Action<SerializedProperty> apply){var p=so.FindProperty(property);if(p!=null)apply(p);else Debug.LogWarning("Quest pipeline: no "+property);}
        Set("m_SupportsHDR",p=>p.boolValue=false);
        Set("m_MSAA",p=>p.intValue=4);
        Set("m_RenderScale",p=>p.floatValue=1);
        Set("m_AllowPostProcessAlphaOutput",p=>p.boolValue=true);
        so.ApplyModifiedPropertiesWithoutUndo();EditorUtility.SetDirty(asset);AssetDatabase.SaveAssets();
    }

    public static void Build()
    {
        try
        {
            if(EditorUserBuildSettings.activeBuildTarget!=BuildTarget.Android)
                EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android,BuildTarget.Android);
            Configure();
            Directory.CreateDirectory(Path.GetDirectoryName(Output));
            var options=new BuildPlayerOptions{scenes=new[]{Scene},locationPathName=Output,target=BuildTarget.Android,targetGroup=BuildTargetGroup.Android,options=BuildOptions.None};
            var report=BuildPipeline.BuildPlayer(options);
            Debug.Log($"Quest build: {report.summary.result}, {report.summary.totalErrors} errors, {report.summary.totalSize/1048576f:0.0} MB, {report.summary.totalTime}");
            if(Application.isBatchMode)EditorApplication.Exit(report.summary.result==UnityEditor.Build.Reporting.BuildResult.Succeeded?0:1);
        }
        catch(Exception e)
        {
            Debug.LogException(e);
            if(Application.isBatchMode)EditorApplication.Exit(1);
        }
    }

    [MenuItem("Tools/Resonance/Quest/Simulate in editor")]
    static void ToggleSimulate(){EditorPrefs.SetBool(SimulateKey,!EditorPrefs.GetBool(SimulateKey));}
    [MenuItem("Tools/Resonance/Quest/Simulate in editor",true)]
    static bool ToggleSimulateCheck(){Menu.SetChecked("Tools/Resonance/Quest/Simulate in editor",EditorPrefs.GetBool(SimulateKey));return true;}
}
