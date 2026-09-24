# 3DFixManager OpenVR stub (optional)

A stand-in `openvr_api.dll` for **3DFixManager** that stops it from starting SteamVR when you
press **Play VR**. It's a hack on a third-party program, not part of Katanga. Use it if SteamVR
misbehaves for you, and undo it by restoring one file.

## The problem it solves

Before launching Katanga, 3DFixManager checks for a headset through OpenVR, initialising it as
an *overlay* application. For overlay applications OpenVR starts SteamVR if it isn't already
running.

Katanga doesn't use SteamVR: it runs on the active OpenXR runtime, for example Virtual Desktop's
own VDXR. With a Quest over Virtual Desktop, SteamVR and Katanga then fight over the headset,
and SteamVR hangs, restarts its processes over and over, or quits. Starting SteamVR yourself
before pressing Play VR avoids some of it, but not reliably.

Katanga can't simply go through SteamVR instead. It runs elevated (3DFixManager starts it as
admin, and it must be elevated to inject into the game), and the OpenXR loader ignores
`XR_RUNTIME_JSON` in elevated processes. Only the system-wide active runtime counts.

## What it does

The DLL replaces `3dfixmanager\Assemblies\openvr_api.dll` and answers 3DFixManager's OpenVR calls
the way a running SteamVR with a headset would, without starting anything:

- `VR_InitInternal2` succeeds, and `IVRSystem_021` is available
- the headset model (`Prop_ModelNumber_String`) is reported as `Meta Quest 3`
- the display frequency (`Prop_DisplayFrequency_Float`) is reported as 90 Hz

3DFixManager's VR check then passes and it launches Katanga normally. SteamVR never starts.

## Install

1. Build it: run `build.bat` from an *x64 Native Tools Command Prompt for VS*. That produces
   `openvr_api.dll` in this folder.
2. Close 3DFixManager and SteamVR.
3. In `3dfixmanager\Assemblies\`, rename `openvr_api.dll` to `openvr_api.dll.steamvr-original`.
4. Copy the built `openvr_api.dll` into `3dfixmanager\Assemblies\`.
5. Optional: put `openvr_stub.ini` next to it to report a different headset or refresh rate:

   ```ini
   [Headset]
   Model=Meta Quest 3
   Hz=90
   ```

6. Start 3DFixManager and use Play VR as usual.

`3dfixmanager\Assemblies\openvr_stub.log` logs every call, which shows whether 3DFixManager
used the stub. A working launch looks like this:

```
loaded, reporting Meta Quest 3 at 90 Hz
VR_InitInternal2(type 2) -> success, no SteamVR started
VR_GetGenericInterface(FnTable:IVRSystem_021)
GetStringTrackedDeviceProperty(0, 1001, size 0)
GetStringTrackedDeviceProperty(0, 1001, size 13)
VR_ShutdownInternal
```

## Uninstall

Delete `3dfixmanager\Assemblies\openvr_api.dll` and rename `openvr_api.dll.steamvr-original`
back to `openvr_api.dll`.

## Limitations

- Tested with 3DFixManager 1.8.7.0, whose OpenVR binding asks for `IVRSystem_021`. Other versions
  may ask for a different interface version, and the stub then reports it as missing. The log
  shows the version that was asked for.
- A 3DFixManager update may put the original DLL back. Copy the stub in again afterwards.
- It doesn't fix 3DFixManager forcing global NVIDIA vsync on at game launch. Keep the
  `katanga.exe` NVIDIA program profile with **Vertical sync: Off** (see `AGENTS.md`).
- Anything in 3DFixManager that really needs SteamVR, such as reading actual tracking data,
  won't work with the stub. Katanga itself doesn't need it.
- Only the 64-bit 3DFixManager is covered; the stub is built for x64.
