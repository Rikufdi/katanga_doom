using System;
using System.Collections;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

// Vsync off for the game while it runs in VR.  --no-game-vsync-off turns it off.
//
// 3DFixManager forces NVIDIA's global vsync on at every launch.  A windowed game doesn't care, but
// in exclusive fullscreen the game then waits for the monitor: 60 fps on a 60 Hz TV against a
// 72/90 Hz headset, a steady judder that frame sync can't fix.  Katanga forces vsync off in the
// game's NVIDIA profile before launching it, and puts the profile back as soon as the game shows
// its first frames: the driver has read its settings by then, and the game keeps them for this run.
// So playing the game on the TV afterwards is unchanged.  The original state is journaled first
// (UnityNativePlugin/DriverProfile.cpp); if Katanga dies before restoring, the next start does it.

public static class GameVsync
{
    [DllImport("UnityNativePlugin64", CharSet = CharSet.Unicode)]
    static extern int GameVsyncOff(string exeName, string journalPath, out int status);

    [DllImport("UnityNativePlugin64", CharSet = CharSet.Unicode)]
    static extern int RestoreGameVsync(string journalPath, StringBuilder profileName, int profileNameLength, out int status);

    public static readonly bool Enabled = !KatangaArgs.Has("--no-game-vsync-off");

    // Seconds after the game's first frame before the profile is put back.
    const float RestoreDelay = 10.0f;

    static bool changed;

    static string JournalPath
    {
        get { return Path.Combine(Application.persistentDataPath, "game_vsync_restore.txt"); }
    }

    // At startup: a journal left behind means an earlier run never restored the profile.
    public static void RecoverFromCrash()
    {
        if (File.Exists(JournalPath))
            Restore("left over from an earlier run that didn't finish");
    }

    // Just before the game is launched.
    public static void Apply(string exeName)
    {
        if (!Enabled)
        {
            Debug.Log("Game vsync: left alone (--no-game-vsync-off)");
            return;
        }

        int result = GameVsyncOff(exeName, JournalPath, out int status);
        switch (result)
        {
            case 0: Debug.Log("Game vsync: " + exeName + "'s NVIDIA profile already has vsync off, nothing changed"); break;
            case 1: changed = true; Debug.Log("Game vsync: forced off in " + exeName + "'s NVIDIA profile for this run"); break;
            case 2: break;   // no NVIDIA driver
            default: Debug.Log(String.Format("Game vsync: could not change {0}'s NVIDIA profile (NvAPI status {1}); with 3DFixManager's forced vsync a fullscreen game may be held to the monitor's refresh", exeName, status)); break;
        }
    }

    // Once the game shows frames: restore after a short delay.
    public static IEnumerator RestoreSoon()
    {
        if (!changed)
            yield break;
        yield return new WaitForSecondsRealtime(RestoreDelay);
        Restore("the game is running");
    }

    // Game exit and Katanga quit, in case the game never showed a frame.  Does nothing once restored.
    public static void RestoreIfChanged(string reason)
    {
        if (changed)
            Restore(reason);
    }

    static void Restore(string reason)
    {
        var name = new StringBuilder(260);
        int result = RestoreGameVsync(JournalPath, name, name.Capacity, out int status);
        if (result == 5)
        {
            changed = false;
            Debug.Log("Game vsync: NVIDIA profile \"" + name + "\" restored (" + reason + ")");
        }
        else if (result != 4)
            Debug.Log(String.Format("Game vsync: could not restore NVIDIA profile \"{0}\" (NvAPI status {1}), will retry at the next start", name, status));
    }
}
