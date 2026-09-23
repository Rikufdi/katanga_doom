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
// at startup to the existing VRCamera/LeftHand/RightHand objects.  Works with any
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

        AddPoseDriver(GameObject.Find("LeftHand"), "<XRController>{LeftHand}/devicePosition", "<XRController>{LeftHand}/deviceRotation");
        AddPoseDriver(GameObject.Find("RightHand"), "<XRController>{RightHand}/devicePosition", "<XRController>{RightHand}/deviceRotation");

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
