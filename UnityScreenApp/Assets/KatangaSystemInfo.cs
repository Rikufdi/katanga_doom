using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.XR.OpenXR.Features;

#if UNITY_EDITOR
using UnityEditor;
#endif

// Reads the OpenXR system name, which is the headset model on most runtimes.
//
// Controller interaction profiles don't say which Quest controller is in use.  SteamVR
// and VDXR only offer the older oculus/touch_controller profile, so Quest 2, 3, 3S and
// Pro controllers all look the same to the app.  VDXR does put the real headset name in
// XrSystemProperties.systemName ("Meta Quest 3", "Meta Quest Pro", ...), and the Meta
// runtime does too, so ControllerModel uses this to pick the right controller model.
//
// Unity doesn't expose the system name, so this small feature calls
// xrGetSystemProperties itself.  KatangaXRSetup enables it.

#if UNITY_EDITOR
[UnityEditor.XR.OpenXR.Features.OpenXRFeature(UiName = "Katanga System Info",
    BuildTargetGroups = new[] { BuildTargetGroup.Standalone },
    Company = "Katanga",
    Desc = "Reads the OpenXR system name (headset model) for picking controller models.",
    OpenxrExtensionStrings = "",
    Version = "1.0.0",
    FeatureId = featureId)]
#endif
public class KatangaSystemInfo : OpenXRFeature
{
    public const string featureId = "com.katanga.systeminfo";

    // Empty until the OpenXR system has been created.
    public static string SystemName { get; private set; } = "";

    const int XR_TYPE_SYSTEM_PROPERTIES = 5;

    [StructLayout(LayoutKind.Sequential)]
    struct XrSystemProperties
    {
        public int type;
        public IntPtr next;
        public ulong systemId;
        public uint vendorId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public byte[] systemName;
        public uint maxSwapchainImageHeight;
        public uint maxSwapchainImageWidth;
        public uint maxLayerCount;
        public uint orientationTracking;
        public uint positionTracking;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int GetInstanceProcAddrDelegate(ulong instance, [MarshalAs(UnmanagedType.LPStr)] string name, out IntPtr function);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int GetSystemPropertiesDelegate(ulong instance, ulong systemId, ref XrSystemProperties properties);

    ulong instance;

    protected override bool OnInstanceCreate(ulong xrInstance)
    {
        instance = xrInstance;
        return true;
    }

    protected override void OnSystemChange(ulong xrSystem)
    {
        try
        {
            GetInstanceProcAddrDelegate getProcAddr =
                Marshal.GetDelegateForFunctionPointer<GetInstanceProcAddrDelegate>(xrGetInstanceProcAddr);

            IntPtr function;
            if (getProcAddr(instance, "xrGetSystemProperties", out function) != 0 || function == IntPtr.Zero)
                return;

            GetSystemPropertiesDelegate getSystemProperties =
                Marshal.GetDelegateForFunctionPointer<GetSystemPropertiesDelegate>(function);

            XrSystemProperties properties = new XrSystemProperties
            {
                type = XR_TYPE_SYSTEM_PROPERTIES,
                systemName = new byte[256],
            };
            if (getSystemProperties(instance, xrSystem, ref properties) != 0)
                return;

            int length = Array.IndexOf(properties.systemName, (byte)0);
            if (length < 0)
                length = properties.systemName.Length;
            SystemName = Encoding.UTF8.GetString(properties.systemName, 0, length);

            Debug.Log("OpenXR system: " + SystemName + " (vendor 0x" + properties.vendorId.ToString("x") + ")");
        }
        catch (Exception e)
        {
            Debug.LogWarning("KatangaSystemInfo: could not read system properties: " + e.Message);
        }
    }
}
