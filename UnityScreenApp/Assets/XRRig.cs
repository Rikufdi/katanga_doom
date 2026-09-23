using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.XR;
using UnityEngine.XR.Management;

// Sets up head and controller tracking for the OpenXR runtime.
//
// This replaces the SteamVR Player prefab.  Rather than keeping a prefab in the scene
// that has to be kept in sync with a VR SDK, we attach Input System TrackedPoseDrivers
// at startup to the existing VRCamera/LeftHand/RightHand objects, plus a controller
// model for each hand (ControllerModel.cs).  Works with any
// OpenXR runtime: SteamVR, VDXR (Virtual Desktop), Meta, WMR.

public static class XRRig
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Setup()
    {
        Camera cam = Camera.main;
        if (cam != null)
            AddPoseDriver(cam.gameObject, "<XRHMD>/centerEyePosition", "<XRHMD>/centerEyeRotation");
        else
            Debug.LogWarning("XRRig: no MainCamera found for head tracking.");

        GameObject leftHand = GameObject.Find("LeftHand");
        GameObject rightHand = GameObject.Find("RightHand");
        AddPoseDriver(leftHand, "<XRController>{LeftHand}/devicePosition", "<XRController>{LeftHand}/deviceRotation");
        AddPoseDriver(rightHand, "<XRController>{RightHand}/devicePosition", "<XRController>{RightHand}/deviceRotation");

        AddControllerModel(leftHand, true);
        AddControllerModel(rightHand, false);
        if (cam != null)
            AddControllerLight(cam.transform);

        KatangaInput.Enable();

        SetFloorOrigin();
        LogRuntime();
    }

    static void AddPoseDriver(GameObject target, string position, string rotation)
    {
        if (target == null || target.GetComponent<TrackedPoseDriver>() != null)
            return;

        TrackedPoseDriver driver = target.AddComponent<TrackedPoseDriver>();
        driver.trackingType = TrackedPoseDriver.TrackingType.RotationAndPosition;
        driver.updateType = TrackedPoseDriver.UpdateType.UpdateAndBeforeRender;
        driver.ignoreTrackingState = true;
        driver.positionInput = new InputActionProperty(new InputAction(target.name + "Position", InputActionType.Value, position, expectedControlType: "Vector3"));
        driver.rotationInput = new InputActionProperty(new InputAction(target.name + "Rotation", InputActionType.Value, rotation, expectedControlType: "Quaternion"));
    }

    static void AddControllerModel(GameObject hand, bool isLeft)
    {
        if (hand == null || hand.GetComponent<ControllerModel>() != null)
            return;
        hand.AddComponent<ControllerModel>().isLeft = isLeft;
    }

    // The scene has no lights, it is all skybox ambient and unlit screen.  The controller
    // models are PBR, so give them a soft head mounted light that only affects the hand
    // layer, leaving the floor and environment exactly as before.

    static void AddControllerLight(Transform head)
    {
        GameObject lightObject = new GameObject("ControllerLight");
        lightObject.transform.SetParent(head, false);
        lightObject.transform.localRotation = Quaternion.Euler(30.0f, 0.0f, 0.0f);

        Light light = lightObject.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.0f;
        light.shadows = LightShadows.None;
        light.cullingMask = 1 << ControllerModel.HandLayer;
    }

    // The scene was built for SteamVR standing/room-scale, with the floor at y=0 and
    // the screen at a fixed height above it.  OpenXR runtimes may default to a seated
    // (device) origin, so ask for the floor origin explicitly.

    static void SetFloorOrigin()
    {
        List<XRInputSubsystem> subsystems = new List<XRInputSubsystem>();
        SubsystemManager.GetSubsystems(subsystems);
        foreach (XRInputSubsystem subsystem in subsystems)
        {
            if (!subsystem.TrySetTrackingOriginMode(TrackingOriginModeFlags.Floor))
                Debug.LogWarning("XRRig: runtime refused Floor tracking origin, using " + subsystem.GetTrackingOriginMode());
        }
    }

    static void LogRuntime()
    {
        XRGeneralSettings settings = XRGeneralSettings.Instance;
        if (settings == null || settings.Manager == null || settings.Manager.activeLoader == null)
        {
            Debug.LogWarning("XRRig: no XR loader active. Is an OpenXR runtime (SteamVR, VDXR) installed and set as active?");
            return;
        }

        Debug.Log("XR loader: " + settings.Manager.activeLoader.name);
        Debug.Log("OpenXR runtime: " + UnityEngine.XR.OpenXR.OpenXRRuntime.name + " " + UnityEngine.XR.OpenXR.OpenXRRuntime.version);
    }
}
