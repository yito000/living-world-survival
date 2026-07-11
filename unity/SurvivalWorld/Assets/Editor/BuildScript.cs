using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;

public static class BuildScript
{
    private static readonly string[] ClientScenes =
    {
        "Assets/Scenes/Bootstrap.unity",
        "Assets/Scenes/World_MVP.unity",
    };

    private static readonly string[] ServerScenes =
    {
        "Assets/Scenes/World_MVP.unity",
    };

    public static void BuildLinuxServer()
    {
        const string outputPath = "Build/Server/survival-server.x86_64";
        ValidateScenes(ServerScenes);
        EnsureOutputDirectory(outputPath);

        var options = new BuildPlayerOptions
        {
            scenes = ServerScenes,
            locationPathName = outputPath,
            target = BuildTarget.StandaloneLinux64,
            subtarget = (int)StandaloneBuildSubtarget.Server,
            options = BuildOptions.None,
        };

        Report(BuildPipeline.BuildPlayer(options));
    }

    public static void BuildWindowsClient()
    {
        const string outputPath = "Build/Client/survival.exe";
        ValidateScenes(ClientScenes);
        EnsureOutputDirectory(outputPath);

        var options = new BuildPlayerOptions
        {
            scenes = ClientScenes,
            locationPathName = outputPath,
            target = BuildTarget.StandaloneWindows64,
            subtarget = (int)StandaloneBuildSubtarget.Player,
            options = BuildOptions.None,
        };

        Report(BuildPipeline.BuildPlayer(options));
    }

    private static void ValidateScenes(string[] scenes)
    {
        foreach (var scene in scenes)
        {
            if (!File.Exists(scene))
            {
                throw new FileNotFoundException($"Build scene not found: {scene}", scene);
            }
        }
    }

    private static void EnsureOutputDirectory(string locationPathName)
    {
        var directory = Path.GetDirectoryName(locationPathName);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static void Report(BuildReport report)
    {
        var summary = report.summary;
        Console.WriteLine($"Build result: {summary.result}, size: {summary.totalSize} bytes, time: {summary.totalTime}");

        if (summary.result != BuildResult.Succeeded)
        {
            EditorApplication.Exit(1);
        }
    }
}
