

using System.IO;
using UnityEditor;
using UnityEngine;

public class ReleaseBuild : MonoBehaviour
{
    // Unity 2019.3+ puts native plugins for x64 players in <Data>/Plugins/x86_64.
    static string PluginsFolder(string dataFolder)
    {
        string x64 = dataFolder + "/Plugins/x86_64";
        return Directory.Exists(x64) ? x64 : dataFolder + "/Plugins";
    }

    // Copy the key Deviare pieces next to the plugins, as the Plugin folder by itself
    // is not sufficient for SpyMgr.Init to succeed.  The x86 halves are also copied
    // explicitly, because an x64 player build can skip 32-bit DLLs, and we need them
    // for injecting into 32-bit games.
    static void CopyDeviare(string dataFolder)
    {
        string plugins = PluginsFolder(dataFolder) + "/";

        FileUtil.ReplaceFile("Assets/Dependencies/deviare32.db", plugins + "deviare32.db");
        FileUtil.ReplaceFile("Assets/Dependencies/deviare64.db", plugins + "deviare64.db");

        string[] x86 = { "Assets/Dependencies/DeviareCOM.dll", "Assets/Dependencies/DvAgent.dll", "Assets/Plugins/GamePlugin.dll" };
        foreach (string dll in x86)
        {
            string target = plugins + Path.GetFileName(dll);
            if (File.Exists(dll) && !File.Exists(target))
                FileUtil.CopyFileOrDirectory(dll, target);
        }
    }

    [MenuItem("Build/Release Build _F5")]
    public static void Build()
    {
        string releaseFolder = "../Release/";

        // Always clean build, delete the prior build completely.
        FileUtil.DeleteFileOrDirectory(releaseFolder);

        UnityEditor.WindowsStandalone.UserBuildSettings.copyPDBFiles = false;

        KatangaXRSetup.Apply(false);

        BuildPlayerOptions buildPlayerOptions = new BuildPlayerOptions();
        buildPlayerOptions.scenes = new[] { "Assets/BigScreen 3D.unity" };
        buildPlayerOptions.locationPathName = releaseFolder + "katanga.exe";
        buildPlayerOptions.target = BuildTarget.StandaloneWindows64;
        buildPlayerOptions.options = BuildOptions.None;
        BuildPipeline.BuildPlayer(buildPlayerOptions);

        CopyDeviare(releaseFolder + "katanga_Data");

        // Setup for Demo, but drop all the BonusShots for ReleaseBuilds, to save space.
        FileUtil.CopyFileOrDirectory("Stereo Pictures", releaseFolder + "Stereo Pictures");
        FileUtil.DeleteFileOrDirectory(releaseFolder + "Stereo Pictures/BonusShots");

        if (Directory.Exists(@"C:\Users\bo3b\Documents\Code\3d_fix_manager\WpfApplication3\bin\VR\Tools"))
        {
            FileUtil.DeleteFileOrDirectory(@"C:\Users\bo3b\Documents\Code\3d_fix_manager\WpfApplication3\bin\VR\Tools\katanga");
            FileUtil.CopyFileOrDirectory(releaseFolder, @"C:\Users\bo3b\Documents\Code\3d_fix_manager\WpfApplication3\bin\VR\Tools\katanga");
        }
        //FileUtil.CopyFileOrDirectory("Assets /Dependencies/katanga.exe.manifest", releaseFolder + "katanga_data/Plugins/katanga.exe.manifest");
    }

    [MenuItem("Build/Debug Build")]
    public static void DebugBuild()
    {
        string releaseFolder = "../Debug/";

        // Always clean build, delete the prior build completely.
        FileUtil.DeleteFileOrDirectory(releaseFolder);

        KatangaXRSetup.Apply(false);

        BuildPlayerOptions buildPlayerOptions = new BuildPlayerOptions();
        buildPlayerOptions.scenes = new[] { "Assets/BigScreen 3D.unity" };
        buildPlayerOptions.locationPathName = releaseFolder + "katanga.exe";
        buildPlayerOptions.target = BuildTarget.StandaloneWindows64;
        buildPlayerOptions.options = BuildOptions.Development | BuildOptions.ShowBuiltPlayer;
        BuildPipeline.BuildPlayer(buildPlayerOptions);

        CopyDeviare(releaseFolder + "katanga_Data");

        FileUtil.CopyFileOrDirectory("Stereo Pictures", releaseFolder + "Stereo Pictures");

        //FileUtil.CopyFileOrDirectory("Assets/Dependencies/katanga.exe.manifest", releaseFolder + "katanga_data/Plugins/katanga.exe.manifest");
    }


    // Demo build, which will remove all game functionality except being able to show stereo photos on the big screen.

    [MenuItem("Build/Demo Build")]
    public static void DemoBuild()
    {
        string demoFolder = "../Demo/";

        // Always clean build, delete the prior build completely.
        FileUtil.DeleteFileOrDirectory(demoFolder);

        UnityEditor.WindowsStandalone.UserBuildSettings.copyPDBFiles = false;

        KatangaXRSetup.Apply(false);

        BuildPlayerOptions buildPlayerOptions = new BuildPlayerOptions();
        buildPlayerOptions.scenes = new[] { "Assets/BigScreen 3D.unity" };
        buildPlayerOptions.locationPathName = demoFolder + "HelixVisionDemo.exe";
        buildPlayerOptions.target = BuildTarget.StandaloneWindows64;
        buildPlayerOptions.options = BuildOptions.ShowBuiltPlayer;
        BuildPipeline.BuildPlayer(buildPlayerOptions);

        // Add in our sample stereo pictures. Can be anything in the folder however.
        FileUtil.CopyFileOrDirectory("Stereo Pictures", demoFolder + "Stereo Pictures");

        // Delete junk that is unnecessary for the demo.
        string plugins = PluginsFolder(demoFolder + "HelixVisionDemo_Data") + "/";
        FileUtil.DeleteFileOrDirectory(plugins + "deviareCOM.dll");
        FileUtil.DeleteFileOrDirectory(plugins + "deviareCOM64.dll");
        FileUtil.DeleteFileOrDirectory(plugins + "dvAgent.dll");
        FileUtil.DeleteFileOrDirectory(plugins + "dvAgent64.dll");
        FileUtil.DeleteFileOrDirectory(plugins + "gamePlugin.dll");
        FileUtil.DeleteFileOrDirectory(plugins + "gamePlugin64.dll");
    }
}
