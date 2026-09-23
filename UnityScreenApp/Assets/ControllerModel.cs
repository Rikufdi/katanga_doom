using UnityEngine;
using UnityEngine.InputSystem.XR;

// Shows a 3D model of the VR controller in each tracked hand.
//
// The SteamVR plugin got these as render models from the SteamVR runtime.  OpenXR has
// no cross-runtime way to get controller models, so we ship our own: the glTF models
// from the WebXR Input Profiles project (MIT licensed, see
// Resources/ControllerModels/LICENSE.md), imported by glTFast.
//
// The model is picked from the OpenXR interaction profile the runtime reports for the
// hand, and swapped if that changes, e.g. when a different controller is turned on.
// XRRig adds one of these to each hand object.

public class ControllerModel : MonoBehaviour
{
    public bool isLeft;

    // The hands are on their own layer, so the controller light only lights them.
    public const int HandLayer = 26;

    // glTFast converts glTF to Unity space by flipping X, while Unity's OpenXR poses
    // flip Z.  The difference is a half turn around Y.  The WebXR models have their
    // origin at the grip pose, which is what TrackedPoseDriver follows.
    static readonly Quaternion modelRotation = Quaternion.Euler(0.0f, 180.0f, 0.0f);

    const string fallbackProfile = "generic-trigger-squeeze-thumbstick";

    string currentProfile;
    GameObject model;

    private void Update()
    {
        XRController device = isLeft ? XRController.leftHand : XRController.rightHand;

        string profile = device != null ? ProfileFor(device) : null;
        if (profile != currentProfile)
        {
            currentProfile = profile;
            LoadModel(profile);
        }

        if (model != null)
        {
            // Not every profile layout has isTracked, treat those as always tracked.
            bool tracked = device != null && device.added &&
                           (device.isTracked == null || device.isTracked.isPressed);
            if (model.activeSelf != tracked)
                model.SetActive(tracked);
        }
    }

    // Map the OpenXR interaction profile device layouts to WebXR input profile ids.
    static string ProfileFor(XRController device)
    {
        switch (device.GetType().Name)
        {
            case "ValveIndexController": return "valve-index";
            case "ViveController": return "htc-vive";
            case "OculusTouchController": return "oculus-touch-v3";
            case "QuestTouchPlusController": return "meta-quest-touch-plus";
            case "QuestProTouchController": return "meta-quest-touch-pro";
            case "WMRSpatialController": return "microsoft-mixed-reality";
            case "ReverbG2Controller": return "hp-mixed-reality";
            default: return fallbackProfile;
        }
    }

    void LoadModel(string profile)
    {
        if (model != null)
        {
            Destroy(model);
            model = null;
        }
        if (profile == null)
            return;

        GameObject prefab = LoadPrefab(profile);
        if (prefab == null)
            prefab = LoadPrefab(fallbackProfile);
        if (prefab == null)
        {
            Debug.LogWarning("ControllerModel: no model found for " + profile);
            return;
        }

        model = Instantiate(prefab, transform, false);
        model.name = "Model (" + profile + ")";
        model.transform.localPosition = Vector3.zero;
        model.transform.localRotation = modelRotation;
        SetLayer(model.transform, HandLayer);

        foreach (Renderer r in model.GetComponentsInChildren<Renderer>())
        {
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
        }

        print("Controller model " + (isLeft ? "left: " : "right: ") + profile);
    }

    // Some controllers (Vive wand) have one model for both hands.
    GameObject LoadPrefab(string profile)
    {
        string folder = "ControllerModels/" + profile + "/";
        GameObject prefab = Resources.Load<GameObject>(folder + (isLeft ? "left" : "right"));
        if (prefab == null)
            prefab = Resources.Load<GameObject>(folder + "none");
        return prefab;
    }

    static void SetLayer(Transform t, int layer)
    {
        t.gameObject.layer = layer;
        foreach (Transform child in t)
            SetLayer(child, layer);
    }
}
