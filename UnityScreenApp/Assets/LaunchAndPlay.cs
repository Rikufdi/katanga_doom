using System;
using System.Runtime.InteropServices;
using System.IO;

using UnityEngine;
using UnityEngine.UI;
using System.Collections;
using System.Threading;
using System.Text;

public class LaunchAndPlay : MonoBehaviour
{
    // For multipoint logging, including native C++ plugins/
    private static FileStream m_FileStream;

    // Game object that handles launching and communicating with the running game.
    // If launched with no arguments, will be SlideShow.
    Game game;

    // Reference to the 2D shader for DesktopDuplication use.  Connected in Unity
    // Inspector.  Needs to have a reference, otherwise builds are missing this shader.
    public Shader shader2D;

    // Primary Texture received from game as shared ID3D11ShaderResourceView
    // It automatically updates as the injected DLL copies the bits into the
    // shared resource.
    Texture2D _bothEyes = null;
    bool gameTextureLive = false;

    // Optional FSR 1.0 upscale/sharpen of the game image, see GameUpscaler.cs.
    GameUpscaler upscaler;
    public static System.Int32 gGameSharedHandle = 0;
    bool ownMutex = false;

    // Filled in once the game is live.  
    public static float gameAspectRatio = 16f/9f;

    // Attached reference from Unity editor to main screen object
    public Renderer screenRenderer;

    // Original grey texture for the screen at launch, used again for resolution changes.
    Texture greyTexture;

    // On screen status text.
    public Text infoText;

    // -----------------------------------------------------------------------------
    // -----------------------------------------------------------------------------

    // The very first thing that happens for the app.

    [DllImport("UnityNativePlugin64")]
    private static extern void CreateSetupMutex();

    // Frame pacing: once per VR frame we signal the game side, which waits for it in
    // Present.  That locks the game to the headset's clock.  --no-frame-sync turns it off.
    public static bool frameSync = true;
    private static bool frameEventCreated = false;

    [DllImport("UnityNativePlugin64")]
    [return: MarshalAs(UnmanagedType.I1)]   // C++ bool is one byte
    private static extern bool CreateFrameEvent();
    [DllImport("UnityNativePlugin64")]
    private static extern void SignalFrameEvent();
    [DllImport("UnityNativePlugin64")]
    private static extern void DestroyFrameEvent();

    private void Awake()
    {
        CreateKatangaLog();
        print("Awake");

        // Add our log handler, to capture all output to a non-unity debug log,
        // including our native C++ plugins for both game and Unity.
        Application.logMessageReceived += DuplicateLog;

        // Set our FatalExit as the handler for exceptions so that we get usable 
        // information from the users.
        Application.logMessageReceived += FatalExit;

        // Create the mutex used to block drawing when the game side is busy rebuilding
        // the graphic environment.
        print("CreateSetupMutex");
        CreateSetupMutex();

        EnsureDriverProfile();
    }

    // Katanga's desktop mirror window must never wait for vsync: with vsync forced on (as
    // 3DFixManager does globally at every game launch) it throttles the whole VR loop to the
    // desktop display's rate.  Make sure katanga.exe has an NVIDIA profile with vsync off.
    // --no-vsync-profile skips this.

    [DllImport("UnityNativePlugin64")]
    private static extern int EnsureKatangaDriverProfile(out int status);

    private void EnsureDriverProfile()
    {
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--no-vsync-profile") >= 0)
        {
            print("NVIDIA profile: skipped (--no-vsync-profile)");
            return;
        }

        int result = EnsureKatangaDriverProfile(out int status);
        switch (result)
        {
            case 0: print("NVIDIA profile: vsync already forced off for Katanga"); break;
            case 1: print("NVIDIA profile: vsync forced off for Katanga, takes effect next time Katanga starts"); break;
            case 2: print("NVIDIA profile: no NVIDIA driver, not needed"); break;
            default: print(String.Format("NVIDIA profile: could not set vsync off for Katanga (NvAPI status {0}). If Katanga runs at the desktop's refresh rate, set Vertical sync Off for katanga.exe in the NVIDIA Control Panel.", status)); break;
        }
    }

    // -----------------------------------------------------------------------------

    // Three launch modes:
    // 1) No params on input, show Desktop Duplicator. (Texture.cs on StereoScreen)
    // 2) Single param of --slideshow-mode show slides mode. (SlideShow.cs subclass of Game, on Player)
    // 3) Multiple params specify game to connect normally. (Game.cs on Player)
    //
    // The uDesktopDuplication is attached to the Stereoscreen object, and will
    // automatically run at launch.  We will check here first, and if we have
    // a no-params launch, we'll let that run as the way to see the desktop in VR.
    // If we have a --slideshow-mode or normal VR launch, we'll disable the uDesktopDuplication
    // so that it does not interfere or waste any GPU cycles.

    void Start()
    {
        print("Start: Command line arguments: " + System.Environment.CommandLine);
        string[] args = System.Environment.GetCommandLineArgs();

        // Store the current Texture2D on the Quad as the original grey. We use this
        // as the default when images stop arriving, or we lose the original.
        greyTexture = screenRenderer.material.mainTexture;

        upscaler = new GameUpscaler();

        // Mipmapped per eye copies of whatever is on the screen, for clean filtering.
        if (screenRenderer.GetComponent<ScreenImage>() == null)
            screenRenderer.gameObject.AddComponent<ScreenImage>();

        // Default assumption is normal VR game connection.
        game = GetComponent<Game>();
        game.ParseGameArgs(args);


        // If we wound up with no input params, then this is the DesktopDuplicator mode,
        // which is already set to run on the StereoScreen object via Texture.cs.
        // The script is disabled by default to avoid interfering, so we can activate
        // it here.
        if (game.DesktopMode())
        {
            print("** Running as Desktop Duplication **");
            uDesktopDuplication.Texture script = screenRenderer.GetComponent<uDesktopDuplication.Texture>();
            screenRenderer.material.shader = shader2D;  // Switch shader to 2D version.
            script.enabled = true;                      // Texture.cs is live
            enabled = false;                            // LaunchAndPlay.cs is off
            return;
        }

        // If we are setup for Demo/SlideShow mode, we can replace the game object and
        // use the SlideShow subclass to just show screenshots.
        if (game.SlideShowMode())
        {
            game = GetComponent<SlideShow>();
            print("** Running as Demo Slideshow **");
        }

        // Must exist before the game side is injected, which opens it at load.
        if (frameSync && !game.SlideShowMode())
        {
            frameEventCreated = CreateFrameEvent();
            print("Frame sync: " + (frameEventCreated ? "on" : "failed to create event"));
        }
        else
        {
            print("Frame sync: off");
        }

        // With the game properly selected, add name to the big screen as info on launch.
        infoText.text = "Launching...\n\n" + game.DisplayName();

        // Allow the launching process to continue asychronously.
        StartCoroutine(game.Launch());

        // Start alternating drawing cycle to block mutex during drawing.
        StartCoroutine(EndOfFrame());
    }

    // -----------------------------------------------------------------------------

    // Our x64 Native DLL allows us direct access to DX11 in order to take
    // the shared handle and turn it into a ID3D11ShaderResourceView for Unity.

    [DllImport("UnityNativePlugin64")]
    private static extern IntPtr CreateSharedTexture(int sharedHandle);
    [DllImport("UnityNativePlugin64")]
    private static extern int GetGameWidth();
    [DllImport("UnityNativePlugin64")]
    private static extern int GetGameHeight();
    [DllImport("UnityNativePlugin64")]
    private static extern int GetGameFormat();

    readonly bool noMipMaps = false;
    readonly bool linearColorSpace = true;

    // PollForSharedSurface will just wait until the CreateDevice has been called in 
    // DeviarePlugin, and thus we have created a shared surface for copying game bits into.
    // This is asynchronous because it's in the game world, and we don't know when
    // it will happen.  This is a polling mechanism, which is not
    // great, but should be checking on a 4 byte HANDLE from the game side,
    // once a frame, which is every 11ms.  Only worth doing something more 
    // heroic if this proves to be a problem.
    //
    // Once the PollForSharedSurface returns with non-null, we are ready to continue
    // with the VR side of showing those bits.  This can also happen later in the
    // game if the resolution is changed mid-game, and either DX9->Reset or
    // DX11->ResizeBuffers is called.
    //
    // The goal here is to rebuild the drawing chain when the resolution changes,
    // but also to try very hard to avoid using those old textures, as they may
    // have been disposed in mid-drawing here.  This is all 100% async from the
    // game itself, as multi-threaded as it gets.  We have been getting crashes
    // during multiple resolution changes, that are likely to be related to this.

    void PollForSharedSurface()
    {
        System.Int32 pollHandle = game.GetSharedHandle();

        debugprint("PollForSharedSurface handle: " + pollHandle);

        // When the game notifies us to Resize or Reset, we will set the gGameSharedHANDLE
        // to NULL to notify this side.  When this happens, immediately set the Quad
        // drawing texture to the original grey, so that we stop using the shared buffer 
        // that might very well be dead by now.

        if (pollHandle == 0)
        {
            screenRenderer.material.mainTexture = greyTexture;
            gameTextureLive = false;
            return;
        }

        // The game side is going to kick gGameSharedHandle to null *before* it resets the world
        // over there, so we'll likely get at least one frame of grey space.  If it's doing a full
        // screen reset, it will be much longer than that.  In any case, as soon as it switches
        // back from null to a valid Handle, we can rebuild our chain here.  This also holds
        // true for initial setup, where it will start as null.

        if (pollHandle != gGameSharedHandle)
        {
            gGameSharedHandle = pollHandle;

            print("-> Got shared handle: " + gGameSharedHandle.ToString("x"));


            // Call into the x64 UnityNativePlugin DLL for DX11 access, in order to create a ID3D11ShaderResourceView.
            // You'd expect this to be a ID3D11Texture2D, but that's not what Unity wants.
            // We also fetch the Width/Height/Format from the C++ side, as it's simpler than
            // making an interop for the GetDesc call.

            IntPtr shared = CreateSharedTexture(gGameSharedHandle);
            int gameWidth = GetGameWidth();     // double width texture
            int gameHeight = GetGameHeight();
            int format = GetGameFormat();
            gameAspectRatio = (float)(gameWidth / 2) / (float)gameHeight;

            // Make aspect ratio match the game settings.  Shrink or widen width only, so that
            // the location of center does not change.  This gameWidth will also be used by the
            // ControllerActions when resizing screen, but the master width is saved and restored
            // by ControllerActions. 
            // gameWidth is ephemeral, the ControllerActions PlayerPrefs(size-x) is the default.

            Vector3 scale = screenRenderer.transform.localScale;
            scale.x = -scale.y * (gameAspectRatio);
            screenRenderer.transform.localScale = scale;

            // Really not sure how this color format works.  The DX9 values are completely different,
            // and typically the games are ARGB format there, but still look fine here once we
            // create DX11 texture with RGBA format.
            // DXGI_FORMAT_R8G8B8A8_UNORM = 28,
            // DXGI_FORMAT_R8G8B8A8_UNORM_SRGB = 29,    (The Surge, DX11)
            // DXGI_FORMAT_B8G8R8A8_UNORM = 87          (The Ball, DX9)
            // DXGI_FORMAT_B8G8R8A8_UNORM_SRGB = 91,
            // DXGI_FORMAT_R10G10B10A2_UNORM = 24       ME:Andromenda, Alien, CallOfCthulu   Unity RenderTextureFormat, but not TextureFormat
            //  No SRGB variant of R10G10B10A2.
            // DXGI_FORMAT_B8G8R8X8_UNORM = 88,         Trine   Unity RGB24

            // ToDo: This colorSpace doesn't do anything.
            //  Tested back to back, setting to false/true has no effect on TV gamma

            // After quite a bit of testing, this CreateExternalTexture does not appear to respect the
            // input parameters for TextureFormat, nor colorSpace.  It appears to use whatever is 
            // defined in the Shared texture as the creation parameters.  
            // If we see a format we are not presently handling properly, it's better to know about
            // it than silently do something wrong, so fire off a FatalExit.

            bool colorSpace = linearColorSpace;
            if (format == 28 || format == 87 || format == 88)
                colorSpace = linearColorSpace;
            else if (format == 29 || format == 91)
                colorSpace = !linearColorSpace;
            else if (format == 24)
                colorSpace = linearColorSpace;
            else
                MessageBox(IntPtr.Zero, String.Format("Game uses unknown DXGI_FORMAT: {0}", format), "Unknown format", 0);

            // This is the Unity Texture2D, double width texture, with right eye on the left half.
            // It will always be up to date with latest game image, because we pass in 'shared'.

            _bothEyes = Texture2D.CreateExternalTexture(gameWidth, gameHeight, TextureFormat.RGBA32, noMipMaps, colorSpace, shared);

            print("..eyes width: " + _bothEyes.width + " height: " + _bothEyes.height + " format: " + _bothEyes.format);


            // This is the primary Material for the Quad used for the virtual TV.
            // Assigning the 2x width _bothEyes texture to it means it always has valid
            // game bits.  The custom sbsShader.shader for the material takes care of 
            // showing the correct half for each eye.

            screenRenderer.material.mainTexture = _bothEyes;
            gameTextureLive = true;


            // These are test Quads, and will be removed.  One for each eye. Might be deactivated.
            GameObject leftScreen = GameObject.Find("left");
            if (leftScreen != null)
            {
                Material leftMat = leftScreen.GetComponent<Renderer>().material;
                leftMat.mainTexture = _bothEyes;
                // Using same primary 2x width shared texture, specify which half is used.
                leftMat.mainTextureScale = new Vector2(0.5f, 1.0f);
                leftMat.mainTextureOffset = new Vector2(0.5f, 0);
            }
            GameObject rightScreen = GameObject.Find("right");
            if (rightScreen != null)
            {
                Material rightMat = rightScreen.GetComponent<Renderer>().material;
                rightMat.mainTexture = _bothEyes;
                rightMat.mainTextureScale = new Vector2(0.5f, 1.0f);
                rightMat.mainTextureOffset = new Vector2(0.0f, 0);
            }

            // With the game fully launched and showing frames, we no longer need InfoText.
            // Setting it Inactive makes it not take any drawing cycles, as opposed to an empty string.
            infoText.gameObject.SetActive(false);
        }
    }

    // -----------------------------------------------------------------------------

    // Update is called once per frame, before rendering. Great diagram:
    // https://docs.unity3d.com/Manual/ExecutionOrder.html
    //
    // We are grabbing the mutex at the top of Update, then releasing at the 
    // WaitForEndOfFrame, which is after scene rendering.  This should block
    // any game side usage during the time this Unity side is drawing.

    [DllImport("UnityNativePlugin64")]
    [return: MarshalAs(UnmanagedType.I1)]   // C++ bool is one byte
    private static extern bool GrabSetupMutex();

    void Update()
    {
        debugprint("Update");

        ownMutex = GrabSetupMutex();
        if (!ownMutex)
            screenRenderer.material.mainTexture = greyTexture;

        debugprint("-> GrabSetupMutex, ownMutex=" + ownMutex);

        // Keep checking for a change in resolution by the game. This needs to be
        // done every frame to avoid using textures disposed by Reset.
        // During actual drawing, from yield null to yield WaitForEndOfFrame, we want
        // to lock out the game side from changing the underlying graphics.  

        if (ownMutex)
            PollForSharedSurface();

        // Upscaling reads the shared surface, so only while we hold the mutex, and it
        // needs to be redone every frame since the game keeps updating that surface.
        ScreenImage.sourceIsLive = ownMutex && gameTextureLive && _bothEyes != null;
        if (ownMutex && gameTextureLive && _bothEyes != null)
        {
            if (GameUpscaler.enabled)
                screenRenderer.material.mainTexture = upscaler.Process(_bothEyes);
            else
                screenRenderer.material.mainTexture = _bothEyes;
        }

        // No forced GC.Collect here any more.  The project uses the incremental garbage
        // collector, which spreads collection over frames; a forced full collection every
        // 30 frames only added its own stalls (the "Slow GC.Collect" log lines).

        // Log hitches with a wall clock time, so they can be matched up against
        // PresentMon captures and user actions like cycling the environment.
        if (Time.unscaledDeltaTime > 0.025f && Time.frameCount > 100)
            print(string.Format("[{0:HH:mm:ss.fff}] Hitch: {1:F1} ms frame", System.DateTime.Now, Time.unscaledDeltaTime * 1000f));

        // On game exit, we want to switch to DesktopDuplication view, rather than exit.
        if (game.Exited())
        {
            debugprint("Showing desktop view.");
            uDesktopDuplication.Texture script = screenRenderer.GetComponent<uDesktopDuplication.Texture>();
            screenRenderer.material.shader = shader2D;  // Switch shader to 2D version.
            script.enabled = true;                      // Texture.cs is live
            enabled = false;                            // LaunchAndPlay.cs is off
        }
    }


    // If running in Editor, Application.Quit doesn't happen, which leaves the mutex open.
    // For the UnityEditor case, we'll specify it should quit, which will call our
    // OnApplicationQuit methods.

    void Quit()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
         Application.Quit();
#endif
    }


    // Waits for the end of frame, after all rendering is complete. At this point,
    // we are not in conflict with game side, and we can relinquish the Mutex.
    // We lock out the game side from Update until WaitForEndOfFrame, because
    // we might be drawing from the shared surface.

    [DllImport("UnityNativePlugin64")]
    [return: MarshalAs(UnmanagedType.I1)]   // C++ bool is one byte
    private static extern bool ReleaseSetupMutex();

    private IEnumerator EndOfFrame()
    {
        print("Launch EndOfFrame");

        while (true)
        {
            yield return new WaitForEndOfFrame();

            bool release = ReleaseSetupMutex();
            debugprint("<- ReleaseSetupMutex, ownMutex=" + release);

            ownMutex = false;

            // Frames where ScreenImage took no snapshot still release the game once.
            if (frameEventCreated && !gameFrameReleased)
                SignalFrameEvent();
            gameFrameReleased = false;
        }
    }

    // Release the game as soon as this frame's game image has been taken, rather than at
    // the end of the frame.  On a shared GPU the game's ~6.5 ms of work stretches to about
    // 10.5 ms, close to the 11.1 ms headset frame, and a late frame makes the headset show
    // the previous one again.  Starting it right after the snapshot gives it the time
    // Katanga spends rendering its own frame.  ScreenImage calls this after its snapshot.
    //
    // Note for measuring: the game now presents in the middle of Katanga's frame, so
    // comparing game and Katanga present times shows fake repeat/skip pairs.  In the
    // headset this was the smoothest setup tested.
    private static bool gameFrameReleased = false;

    public static void GameFrameTaken()
    {
        if (frameEventCreated && !gameFrameReleased)
        {
            SignalFrameEvent();
            gameFrameReleased = true;
        }
    }


    // -----------------------------------------------------------------------------

    [DllImport("UnityNativePlugin64")]
    private static extern void CloseFileMappedIPC();
    [DllImport("UnityNativePlugin64")]
    private static extern void DestroySetupMutex();

    private void OnApplicationQuit()
    {
        print("OnApplicationQuit");

        if (upscaler != null)
            upscaler.Release();

        if (frameEventCreated)
            DestroyFrameEvent();

        CloseFileMappedIPC();

        ReleaseSetupMutex();

        print("DestroySetupMutex");
        DestroySetupMutex();
    }

    // -----------------------------------------------------------------------------

    // Error handling.  Anytime we get an error that should *never* happen, we'll
    // just exit by putting up a MessageBox. We still want to check for any and all
    // possible error returns. Whenever we throw an exception anywhere in C# or
    // the C++ plugin, it will come here, so we can use the throw on fatal error model.
    //
    // We are using user32.dll MessageBox, instead of Windows Forms, because Unity
    // only supports an old version of .Net, because of its antique Mono runtime.

    [DllImport("user32.dll")]
    static extern int MessageBox(IntPtr hWnd, string text, string caption, int type);

    static void FatalExit(string condition, string stackTrace, LogType type)
    {
        if (type == LogType.Exception)
        {
            MessageBox(IntPtr.Zero, condition, "Katanga Fatal Error", 0);
            Application.Quit();
        }
    }



    // -----------------------------------------------------------------------------

    // Create a duplicate log file in the LocalLow directory, right next to the Unity
    // Output.log.  We can write to this from both C++ plugins.
    // This part will just write a time stamp header and close the path.  Not used
    // from this high level code.

    [DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath(
     [MarshalAs(UnmanagedType.LPStruct)] Guid rfid,
     uint dwFlags,
     IntPtr hToken,
     out IntPtr pszPath  // API uses CoTaskMemAlloc
     );
    public static readonly Guid LocalAppDataLow = new Guid("A520A1A4-1780-4FF6-BD18-167343C5AF16");

    [DllImport("UnityNativePlugin64", CallingConvention = CallingConvention.Cdecl)]
    private static extern void OpenLogFile();

    static void CreateKatangaLog()
    {
        IntPtr localLowString;
        SHGetKnownFolderPath(LocalAppDataLow, 0, IntPtr.Zero, out localLowString);

        string katanga_log = Marshal.PtrToStringUni(localLowString);
        katanga_log += @"\Katanga\Katanga\katanga.log";

        m_FileStream = new FileStream(katanga_log, FileMode.Create, FileAccess.Write, FileShare.Write);

        DateTime dateTime = DateTime.UtcNow;
        string dateAndTime = dateTime.ToLongDateString() + " @ " + dateTime.ToLongTimeString() + " UTC\n";
        m_FileStream.Write(Encoding.Default.GetBytes(dateAndTime), 0, dateAndTime.Length);
        m_FileStream.WriteByte(0xa);
        m_FileStream.Close();

        // Send this path to the Unity native plugin side, so it can log to same file.
        OpenLogFile();
    }

    // For every message sent through Debug.Log/print, we want to also duplicate them
    // into our Katanga specific log file so we have a commplete set of info, including
    // exact sequence of events.

    static void DuplicateLog(string condition, string stackTrace, LogType type)
    {
        //m_FileStream.Write(Encoding.Default.GetBytes(condition), 0, condition.Length);
        //m_FileStream.WriteByte(0xa);

        //if (type != LogType.Log)
        //    m_FileStream.Write(Encoding.Default.GetBytes(stackTrace), 0, stackTrace.Length);

        //m_FileStream.Flush();
    }

    // For Debug builds, we want to generate more verbose info, especially around 
    // mutex handling.  This is far to much for release builds however.  Anything that
    // is a one-off init, or a rare scenario is OK for print. Anything per-frame must
    // be debugprint.  
    // But, at startup, we'll log every call as Debug, until we successfully get past
    // the first CreateSharedTexture.

    static void debugprint(object message)
    {
#if !UNITY_EDITOR
        if (Debug.isDebugBuild)
            print(message);
#endif
    }

    // -----------------------------------------------------------------------------
    // -----------------------------------------------------------------------------

    // Wait for the EndOfFrame, and then trigger the sync Event to allow
    // the game to continue.  Will use the _gameEventSignal from the NativePlugin
    // to trigger the Event which was actually created in the game process.
    // _gameEventSignal is a HANDLE from x32 game.
    //
    // WaitForFixedUpdate happens right at the start of next frame.
    // The goal here is to sync up the game with the VR state.  VR state needs
    // to take precedence, and is running at 90 Hz.  As long as the game can
    // maintain better than that rate, we can delay the game each frame to keep 
    // them in sync. If the game cannot keep that rate, it will drop to 1/2
    // rate at 45 Hz. Not as good, but acceptable.
    //
    // At end of frame, stall the game draw calls with TriggerEvent.

    //long startTime = Stopwatch.GetTimestamp();
    //    System.Int32 _gameEventSignal = 0;
    //static int ResetEvent = 0;
    //static int SetEvent = 1;


    //// At start of frame, immediately after we've presented in VR, 
    //// restart the game app.
    //private IEnumerator SyncAtStartOfFrame()
    //{
    //    int callcount = 0;
    //    print("SyncAtStartOfFrame, first call: " + startTime.ToString());

    //    while (true)
    //    {
    //        // yield, will run again after Update.  
    //        // This is super early in the frame, measurement shows maybe 
    //        // 0.5ms after start.  
    //        yield return null;

    //        callcount += 1;

    //        // Here at very early in frame, allow game to carry on.
    //        System.Int32 dummy = SetEvent;
    //        object deviare = dummy;
    //        _spyMgr.CallCustomApi(_gameProcess, _nativeDLLName, "TriggerEvent", ref deviare, false);


    //        long nowTime = Stopwatch.GetTimestamp();

    //        // print every 30 frames 
    //        if ((callcount % 30) == 0)
    //        {
    //            long elapsedTime = nowTime - startTime;
    //            double elapsedMS = elapsedTime * (1000.0 / Stopwatch.Frequency);
    //            print("SyncAtStartOfFrame: " + elapsedMS.ToString("F1"));
    //        }

    //        startTime = nowTime;

    //        // Since this is another thread as a coroutine, we won't block the main
    //        // drawing thread from doing its thing.
    //        // Wait here by CPU spin, for 9ms, close to end of frame, before we
    //        // pause the running game.  
    //        // The CPU spin here is necessary, no normal waits or sleeps on Windows
    //        // can do anything faster than about 16ms, which is way to slow for VR.
    //        // Burning one CPU core for this is not a big deal.

    //        double waited;
    //        do
    //        {
    //            waited = Stopwatch.GetTimestamp() - startTime;
    //            waited *= (1000.0 / Stopwatch.Frequency);
    //            //if ((callcount % 30) == 0)
    //            //{
    //            //    print("waiting: " + waited.ToString("F1"));
    //            //}
    //        } while (waited < 3.0);


    //        // Now at close to the end of each VR frame, tell game to pause.
    //        dummy = ResetEvent;
    //        deviare = dummy;
    //        _spyMgr.CallCustomApi(_gameProcess, _nativeDLLName, "TriggerEvent", ref deviare, false);
    //    }
    //}

    //private IEnumerator SyncAtEndofFrame()
    //{
    //    int callcount = 0;
    //    long firstTime = Stopwatch.GetTimestamp();
    //    print("SyncAtEndofFrame, first call: " + firstTime.ToString());

    //    while (true)
    //    {
    //        yield return new WaitForEndOfFrame();

    //        // print every 30 frames 
    //        if ((callcount % 30) == 0)
    //        {
    //            long nowTime = Stopwatch.GetTimestamp();
    //            long elapsedTime = nowTime - startTime;
    //            double elapsedMS = elapsedTime * (1000.0 / Stopwatch.Frequency);
    //            print("SyncAtEndofFrame: " + elapsedMS.ToString("F1"));
    //        }

    //        //TriggerEvent(_gameEventSignal);        

    //        System.Int32 dummy = 0;  // ResetEvent
    //        object deviare = dummy;
    //        _spyMgr.CallCustomApi(_gameProcess, _nativeDLLName, "TriggerEvent", ref deviare, false);
    //    }
    //}


}

