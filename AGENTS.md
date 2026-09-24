# AGENTS.md

Notes for an AI agent (or a human) working on this fork of Katanga. `README.md` describes the
app; this file is the operational detail: how to build it, how to test it, and what has been
learned about the tools around it.

Machine-specific details (tool paths, headset, the Q3Diag diagnostics setup) are kept out of git in
`local/MACHINE.md`. Read it if it exists.

## What this is

Katanga shows a 3D Vision-fixed DX9/DX11 game (3Dmigoto / HelixMod fix) on a big virtual screen
in VR. Two halves:

- **C++ plugins** (`Katanga.sln`)
  - `DeviarePlugin` → `GamePlugin.dll` / `GamePlugin64.dll`, injected into the game by Deviare.
    It shares the game's stereo backbuffer.
  - `UnityNativePlugin` → `UnityNativePlugin64.dll`, which opens that shared surface on the
    Unity side. A named mutex (`KatangaSetupMutex`) is held from `LaunchAndPlay.Update` until
    `WaitForEndOfFrame` every frame.
- **Unity app** (`UnityScreenApp`, Unity **6000.3.19f1**, OpenXR, D3D11, single pass instanced,
  Mono backend). The branch `claude/blissful-gauss-5mafop` holds the Unity 6 / OpenXR / FSR port.

## Building

### C++ plugins

Needs VS Build Tools with the **v143** toolset and **"C++ ATL for v143 build tools (x86 & x64)"**
(`DeviarePlugin.cpp` includes `atlbase.h`). The build copies the DLLs into
`UnityScreenApp/Assets/Plugins/`, which is gitignored.

```
msbuild /m /p:Configuration=VR Katanga.sln /p:platform=x64   /v:minimal /target:rebuild /p:PlatformToolset=v143
msbuild /m /p:Configuration=VR Katanga.sln /p:platform=Win32 /v:minimal /target:rebuild /p:PlatformToolset=v143
```

Both platforms are needed: the injected plugin must match the game's bitness.

### Unity app

Build the plugins first. Close any editor that has the project open (Unity locks the project),
then:

```
Unity.exe -batchmode -quit -projectPath UnityScreenApp -buildTarget Win64 ^
          -executeMethod ReleaseBuild.Build -logFile unity_build.log
```

`ReleaseBuild.Build` (the F5 "Build/Release Build" menu item) deletes and rewrites `../Release/`,
so it fails while `Release/katanga.exe` is running. Check the log for
`Build Finished, Result: Success.` and `warning CS` / `error CS`; the build currently has no C#
warnings. The first build after a clean clone takes several minutes to import packages.

Unity rewrites some `ProjectSettings/*.asset` files when it opens the project. Don't commit those
along with unrelated changes.

### Output layout (important for 3DFixManager)

Unity 6 puts native plugins in `katanga_Data/Plugins/x86_64/`. `ReleaseBuild.CopyDeviare` and the
CI workflow also put a complete Deviare set (`DeviareCOM(64).dll`, `DvAgent(64).dll`,
`deviare32/64.db`) flat in `katanga_Data/Plugins/`. That copy is required, see below.

## 3DFixManager integration

3DFixManager (3DFM) is the usual launcher. It expects Katanga at
`<3DFM>\Tools\Katanga\katanga.exe`. Findings from reading its code (v1.8.7.0):

- **The Play VR button only appears if both
  `Tools\Katanga\katanga_Data\Plugins\DeviareCOM.dll` and `DeviareCOM64.dll` exist**
  (`Katanga.IsKatangaAvailable()`). It checks once, at window load, relative to 3DFM's working
  directory, so restart 3DFM after changing files. If the check fails it also writes
  `VrModeEnabled = False` to its settings.
- On startup it registers both DLLs from that path with `regsvr32 /s` (elevated). Katanga creates
  `NktSpyMgr` through COM, so the registered path must contain a working Deviare set.
- It launches Katanga with `--game-path`, `--launch-type`, `--steam-path`, `--steam-appid`,
  `--epic-appid` and `--waitfor-exe`. `Game.cs` parses all of them.
- Before Play VR it runs a SteamVR check (`SteamVR.Init` in `VrHelper.GetVrState`). This is left
  over from the OpenVR version of Katanga; the Unity 6 build itself uses OpenXR and does not need
  SteamVR.
- Its `VrFramerateLimit` setting caps the game (90 fps is common). With the headset at
  120 Hz, set it to 120 or run the headset at 90 Hz.

## Testing and measuring

- **Slideshow mode:** run `katanga.exe` with no arguments. Hold Ctrl while starting it to get a
  file picker for a game exe instead.
- **Logs** (in `%USERPROFILE%\AppData\LocalLow\Katanga\Katanga\`):
  - `Player.log` holds Unity output and the OpenXR diagnostic report (runtime, per-eye
    resolution). It has timestamped `Hitch: N ms frame` lines for frames over 25 ms,
    `Slow GC.Collect` lines, and timestamped environment, sharpening and hint changes.
  - `katanga.log` is native plugin logging. `WAIT_TIMEOUT` there means the per-frame mutex stalled.
    One `ReleaseMutex ERROR_NOT_OWNER` per frame is expected (a deliberate double release).
- **Frame timing:** PresentMon on `katanga.exe` gives Katanga's own frame times, because Unity
  presents its mirror window once per frame. Run a second PresentMon on the game exe to get the
  game's own frame rate.
- **A pattern to recognise:** runs of frames at *exactly* 100 ms with an idle GPU most likely mean the
  OpenXR runtime is throttling Katanga, probably while a Virtual Desktop menu is open (not yet
  confirmed). It is not Katanga's own workload.

## Known issues

- Toggling sharpening is followed about 1.3 s later by a ~50 ms + ~95 ms hitch. The cause is not
  confirmed; the toggle code itself is trivial.
- The first seconds after a game starts have 80–160 ms frames while the shared surface is created
  and recreated.

## `local/` (gitignored, never commit)

Local-only material that must not be published:

- `local/MACHINE.md`: this PC's tool paths, headset and Q3Diag setup.
- `local/tools/ilspycmd/`: ILSpy command-line decompiler 11.1.0 (NuGet package, unpacked).
  Needs the .NET 10 runtime, no SDK:

  ```
  dotnet local/tools/ilspycmd/tools/net10.0/any/ilspycmd.dll -p -o <outdir> <assembly.exe>
  ```

- `local/3dfm_src/`: decompiled 3DFixManager 1.8.7.0, for reference only. It is third-party code,
  so read it but don't copy it into this repo. The useful files are `FixManager/Katanga.cs`,
  `FixManager.Models/VrHelper.cs` and `FixManager/MainWindow.xaml.cs` (`toggleVrMode`,
  `registerKatangaDlls`).
