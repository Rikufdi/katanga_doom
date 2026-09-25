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
- Its `VrFramerateLimit` setting caps the game. **With Katanga's frame sync (below), set it to
  unlimited**: a second limiter fights the pacing.
- **It forces NVIDIA's global vsync On at every game launch** (`SetVsyncMode(VsyncMode.On)`), and
  only turns it Off again in VR mode when SteamVR initialised and the desktop refresh is lower than
  the headset's. With vsync forced on, Katanga's desktop mirror window waits on the monitor, which
  held the whole VR loop at 60 fps on a 60 Hz TV. Fix: an NVIDIA program profile for
  `katanga.exe` with **Vertical sync: Off**, which overrides the global setting. Katanga makes
  sure of that itself at startup (`UnityNativePlugin/DriverProfile.cpp`, NvAPI DRS): it reuses the
  profile that holds `katanga.exe` or creates "Katanga VR", and sets VSYNCMODE force off and
  adaptive vsync off. The driver reads profiles when a process starts, so a profile created now
  takes effect from the next launch. `Player.log` says `NVIDIA profile: ...`. Writing needs admin
  rights, which Katanga has under 3DFM. `--no-vsync-profile` skips it.
- That check initialises OpenVR as an overlay application (`SteamVR.Init` in
  `OpenVRApiModule`), which starts SteamVR if it isn't running. Katanga then opens its OpenXR
  session on the active runtime (Virtual Desktop's VDXR here), Virtual Desktop switches away from
  SteamVR, and SteamVR hangs, restarts its processes or quits. Starting SteamVR first helps only
  partly. **`Extras/3DFixManagerOpenVRStub/`** is an optional stand-in `openvr_api.dll` for
  3DFixManager that answers its check without starting SteamVR (install steps and limits in its
  README). Katanga itself can't be pointed at SteamVR's OpenXR runtime instead, because it runs
  elevated and the OpenXR loader ignores `XR_RUNTIME_JSON` in elevated processes.
- Most DX11 games launch as `DX11Exe`: the fix's 3Dmigoto `d3d11.dll` shares frames with Katanga
  itself ("DirectConnection"), and GamePlugin is only injected for pacing (see Frame pacing).

## Frame pacing (frame sync)

The game and the headset each run their own clock. Even at the same nominal rate they drift,
and while the game's frames land near the moment Katanga takes the image, the headset shows
game frames twice and skips others: judder when the camera pans. No frame limiter can fix that,
because it doesn't know when the headset refreshes. Katanga instead makes the game wait for it:

- Katanga creates the auto-reset event `KatangaFrameEvent` and signals it once per VR frame,
  right after `ScreenImage` has taken its snapshot of the game image
  (`LaunchAndPlay.GameFrameTaken`, with a fallback at `EndOfFrame`). The game plugin waits for
  it (at most 40 ms, then runs free until the signal returns) at the top of `Present`, so the game
  presents exactly once per headset frame. `--no-frame-sync` turns it off.
- Releasing at the end of Katanga's frame instead was also tried. It looked worse in the
  headset: on the shared GPU the game's ~6.5 ms of work stretches to ~10.5 ms, and it needs the
  extra time.
- `ScreenImage` takes **one** snapshot of the side-by-side image before cutting the eyes. Two reads
  of the shared texture let the game's write land between them, and the eyes then show different
  frames ("two frames mixed"), which pacing made happen every frame.
- For `DX11Exe` (3Dmigoto) games Katanga injects GamePlugin in **pacing only mode**: it loads it
  with `LoadCustomDll` (not unloaded on exit, so the hook never points at freed code) and calls the
  exported `StartPacing` through `CallCustomApi`, because `OnLoad` only runs for DLLs attached to a
  hook. The plugin must hook the **real** `dxgi.dll` `IDXGISwapChain::Present`: a swap chain
  created inside the game comes back wrapped by 3Dmigoto, whose `Present` the game doesn't use.
  Katanga finds `Present` in its own clean process and publishes its offset in `dxgi.dll` through
  the `Local\KatangaPacing` mapping (`Shared/KatangaPacing.h`). System DLLs load at the same
  address in every process for the whole boot, and the plugin checks the `dxgi.dll` build matches.
  Katanga is 64 bit and can't look into the 32 bit `dxgi.dll`, so for 32 bit games it runs
  `%windir%\SysWOW64\rundll32.exe "<Plugins>\GamePlugin.dll",ProbePresent`: the 32 bit plugin
  finds `Present` in that clean 32 bit process and fills in the mapping.
- Proof in `katanga.log`: `pacing hook on IDXGISwapChain::Present installed`, `first paced
  Present`, and every 900 presents `N of the last 900 held for the VR frame`. `Player.log` says
  `Frame sync: pacing active` or why not.
- Requirements: the `katanga.exe` vsync-off profile above (Katanga sets it), and the 3DFM limiter
  at unlimited.
- `local/pacetest/` (see `local/MACHINE.md`) tests the pacing plugin without a game or headset,
  64 and 32 bit, including the 32 bit `rundll32` probe (`pacetest32.exe <GamePlugin.dll> probe`).

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
  game's own frame rate. PresentMon traps, all hit in practice:
  - 2.5.1 takes **one** `--process_name`; a second one silently matches nothing. Run one session
    per process with `--date_time`, which puts both files on the same clock.
  - Without admin rights it only resolves names of processes that already run when it starts, so
    start it after the game and Katanga are up.
  - A PresentMon that is killed can leave its ETW session behind (`logman query -ets`), which then
    starves new captures (tens of thousands of lost events, empty output). Stop sessions with
    `PresentMon --terminate_existing_session --session_name <name>`, not by killing the process.
- **Judging pacing from present times is only valid when the game presents before Katanga's
  image snapshot.** With the game released right after the snapshot, it presents in the middle of
  Katanga's frame, and comparing present times shows fake repeat/skip pairs. Trust the headset.
- **Unreal Engine games drop to 3 fps whenever their window is not in the foreground**, for
  example when alt-tabbing out during a test. Ignore those stretches.
- **Patterns to recognise:**
  - Runs of frames at *exactly* 100 or 200 ms with an idle GPU mean the OpenXR runtime (Virtual
    Desktop) is throttling Katanga. It is not Katanga's own workload, and it also pauses pacing.
  - Katanga at exactly 60 fps on a 90 Hz headset: forced vsync on a 60 Hz desktop display (see
    3DFixManager above).
  - The game at 144 fps and GPU near 100% with "frame sync on": pacing is not reaching the game's
    `Present`. Check `katanga.log` for `first paced Present`.
  - Long stutters right after `Controller left/right connected` lines: Quest controllers sleep when
    put down and reconnect often. `ControllerModel` used to rebuild the glTF model on every
    reconnect; it now keeps it. Compare those timestamped lines with the `Hitch` lines.
  - Short stutters when the game spawns NPCs, streams in areas or saves: the game's own CPU work
    (PresentMon shows high `MsCPUBusy` for the game, Katanga's frames stay clean). With frame sync
    a late game frame shows as one repeated frame. Not something Katanga can fix.

## CPU headroom experiments (Little Nightmares II, 5 min runs, same section)

Virtual Desktop's OpenXR runtime spins one thread inside Katanga at a full core, for exact frame
delivery; Katanga itself uses very little CPU. These options exist, all off by default, and can go
in `katanga_options.txt` next to `katanga.exe` (3DFixManager passes fixed arguments):

- `--cpu-isolation N`: Katanga, spinner included, on the last N physical cores, the game on the rest.
- `--isolate-spinner`: only the spinning thread gets a logical CPU of its own (found by measuring on
  a background thread; sampling on the main thread stops the runtime spinning).
- `--game-priority`: the game above normal priority.

| Run | Game frames >15 ms / >25 ms per min | Headset repeats/skips |
|---|---|---|
| A: frame sync, no options | 12.6 / 1.6 | ≈ 0 |
| B: + `--cpu-isolation 1 --game-priority` | 11.8 / 2.5 | ≈ 0 |
| D: + `--isolate-spinner` | 17.5 / 2.1 | ≈ 0 |
| E: `--no-frame-sync`, 3DFM limiter at headset Hz | 15.8 / 4.5 | ~8% of frames |

| F: frame sync at **72 Hz** (slot 13.9 ms) | missed slot 7.1/min (outside two throttles) | ≈ 0 |
| G: frame sync at **72 Hz**, 8 min | missed slot 4.2/min, >25 ms 1.9 | ≈ 0 |

None of the CPU options gave the game measurable headroom; the differences are within session to
session variation. **A lower headset refresh rate does help**: at 72 Hz the game misses its slot
roughly a third as often as at 90 Hz, because each frame gets 13.9 ms instead of 11.1 ms.

The occasional **major slowdown** (seconds of exactly 100 ms Katanga frames, pacing drops out) is
Virtual Desktop's runtime holding Katanga back. The headset's logcat shows the effect, not the
cause: the decoder's input rate falls (72 → ~50/s), stale frames appear and VD's predicted latency
jumps (59 → 82 ms). It comes and goes between sessions (two in 4 minutes, none in 8), which points
at something external such as Wi-Fi retransmissions or an encoder stall. Catch one with Q3Diag
running (`local/tools/pacing/q3diag_session.py`) to see the Wi-Fi and encoder numbers at that
moment. Turning pacing off did not reduce the game's slow frames either (the driver's
frame queue is back then), so the remaining stutters are the game's own work and a frame queue in
Katanga would only add latency. A lower headset refresh rate gives each game frame more time.
Keep the options for games that load every core, and remeasure there.

## Known issues

- Toggling sharpening is followed about 1.3 s later by a ~50 ms + ~95 ms hitch. The cause is not
  confirmed; the toggle code itself is trivial.
- The first seconds after a game starts have 80–160 ms frames while the shared surface is created
  and recreated.
- On a Quest 3 the controllers are detected correctly (`meta-quest-touch-plus-v2`), but the model
  looks like a special edition (possibly the Xbox edition of the Quest 3S controllers), not the
  standard Touch Plus. Cosmetic only; low priority.
- With frame sync, a game frame that doesn't finish within the headset frame (heavy scenes, the
  game's own save or loading stalls) is shown one frame late: a small drop, no judder. Lower game
  settings or a lower headset refresh rate give it more room.
- VRAM: Katanga falls to about 4–6 fps when VRAM is nearly full, for example with a local AI
  model server loaded. Check `nvidia-smi` and per-process GPU memory before profiling.

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
