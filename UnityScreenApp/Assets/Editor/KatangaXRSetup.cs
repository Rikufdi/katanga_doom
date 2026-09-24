using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
using UnityEngine.XR.OpenXR.Features.Interactions;

// Configures XR Plugin Management + OpenXR for Katanga.
//
// Katanga used to depend on the SteamVR Unity plugin (OpenVR).  It now uses OpenXR, which
// works on SteamVR's OpenXR runtime, VDXR (Virtual Desktop), Meta/Oculus, and WMR.
// Rather than committing the generated XR settings assets, which are awkward to merge
// and tied to package versions, this sets everything up from code.  It runs when the
// editor loads the project, and from the build menu before building, so a fresh
// checkout or a CI build always ends up configured the same way.

[InitializeOnLoad]
public static class KatangaXRSetup
{
    const string OpenXRLoader = "UnityEngine.XR.OpenXR.OpenXRLoader";

    // Controller profiles to enable.  The runtime picks whichever matches the hardware,
    // and our KatangaInput bindings use generic usages that exist on all of them.
    static readonly HashSet<System.Type> Profiles = new HashSet<System.Type>
    {
        typeof(ValveIndexControllerProfile),
        typeof(HTCViveControllerProfile),
        typeof(OculusTouchControllerProfile),
        typeof(MetaQuestTouchPlusControllerProfile),
        typeof(MetaQuestTouchProControllerProfile),
        typeof(MicrosoftMotionControllerProfile),
        typeof(HPReverbG2ControllerProfile),
        typeof(KHRSimpleControllerProfile),
    };

    static KatangaXRSetup()
    {
        // Settings objects are not ready during the static constructor on first import.
        EditorApplication.delayCall += () => Apply(false);
    }

    [MenuItem("Build/Configure XR (OpenXR)")]
    static void ApplyFromMenu()
    {
        Apply(true);
    }

    public static void Apply(bool verbose)
    {
        // The shared texture from the game and our native plugin are D3D11 only.
        // Only touch it when wrong, as changing graphics APIs mid-build is not allowed.
        GraphicsDeviceType[] apis = PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneWindows64);
        if (PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64) ||
            apis.Length != 1 || apis[0] != GraphicsDeviceType.Direct3D11)
        {
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D11 });
        }

        XRGeneralSettings general = XRGeneralSettingsForBuildTarget(BuildTargetGroup.Standalone);
        if (general == null)
        {
            Debug.LogWarning("KatangaXRSetup: XR Plugin Management settings not available yet.");
            return;
        }
        general.InitManagerOnStart = true;

        if (!general.Manager.activeLoaders.Any(l => l != null && l.GetType().FullName == OpenXRLoader))
            XRPackageMetadataStore.AssignLoader(general.Manager, OpenXRLoader, BuildTargetGroup.Standalone);

        OpenXRSettings openxr = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Standalone);
        if (openxr != null)
        {
            // Both eyes in one instanced draw.  Our shaders are written for it.
            openxr.renderMode = OpenXRSettings.RenderMode.SinglePassInstanced;

            foreach (OpenXRInteractionFeature feature in openxr.GetFeatures<OpenXRInteractionFeature>())
            {
                if (Profiles.Contains(feature.GetType()))
                    feature.enabled = true;
            }
            EditorUtility.SetDirty(openxr);
        }

        EditorUtility.SetDirty(general);
        AssetDatabase.SaveAssets();

        if (verbose)
            Debug.Log("KatangaXRSetup: OpenXR configured for Standalone.");
    }

    static XRGeneralSettings XRGeneralSettingsForBuildTarget(BuildTargetGroup group)
    {
        XRGeneralSettings settings = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(group);
        if (settings != null)
            return settings;

        // First time: create the per build target container asset and the settings for Standalone.
        XRGeneralSettingsPerBuildTarget perTarget;
        if (!EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.settingsKey, out perTarget) || perTarget == null)
        {
            const string folder = "Assets/XR";
            if (!AssetDatabase.IsValidFolder(folder))
                AssetDatabase.CreateFolder("Assets", "XR");

            perTarget = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
            AssetDatabase.CreateAsset(perTarget, folder + "/XRGeneralSettingsPerBuildTarget.asset");
            EditorBuildSettings.AddConfigObject(XRGeneralSettings.settingsKey, perTarget, true);
        }

        if (!perTarget.HasSettingsForBuildTarget(group))
            perTarget.CreateDefaultSettingsForBuildTarget(group);
        if (!perTarget.HasManagerSettingsForBuildTarget(group))
            perTarget.CreateDefaultManagerSettingsForBuildTarget(group);

        return perTarget.SettingsForBuildTarget(group);
    }
}

// Also apply right before any build, including headless CI builds where the
// InitializeOnLoad delayCall may not have run yet.  Runs ahead of XR Management's
// own build processor, which then packages the settings.
class KatangaXRSetupBuildHook : IPreprocessBuildWithReport
{
    public int callbackOrder { get { return -1000; } }

    public void OnPreprocessBuild(BuildReport report)
    {
        if (report.summary.platformGroup == BuildTargetGroup.Standalone)
            KatangaXRSetup.Apply(true);
    }
}
