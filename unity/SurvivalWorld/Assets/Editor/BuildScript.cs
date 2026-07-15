using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;

public static class BuildScript
{
    private const string GrpcLinuxNativePlugin = "Assets/Packages/Grpc.Core.2.46.6/runtimes/linux-x64/native/libgrpc_csharp_ext.x64.so";
    private const string GrpcLinuxNativeFileName = "libgrpc_csharp_ext.x64.so";
    private const string GrpcLinuxNativeAliasFileName = "libgrpc_csharp_ext.so";

    private static readonly string[] ClientScenes =
    {
        "Assets/Scenes/Bootstrap.unity",
        "Assets/Scenes/World_MVP.unity",
    };

    private static readonly string[] ServerScenes =
    {
        "Assets/Scenes/Bootstrap.unity",
        "Assets/Scenes/World_MVP.unity",
    };

    public static void BuildLinuxServer()
    {
        const string outputPath = "Build/Server/survival-server.x86_64";
        ValidateScenes(ServerScenes);
        ConfigureLinuxServerNativePlugins();
        EnsureOutputDirectory(outputPath);

        var options = new BuildPlayerOptions
        {
            scenes = ServerScenes,
            locationPathName = outputPath,
            target = BuildTarget.StandaloneLinux64,
            subtarget = (int)StandaloneBuildSubtarget.Server,
            options = BuildOptions.None,
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);
        Report(report);
        CopyLinuxServerGrpcNativeAlias(outputPath, report);
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

    private static void ConfigureLinuxServerNativePlugins()
    {
        ConfigureLinux64Plugin(GrpcLinuxNativePlugin, "x86_64");
    }

    private static void ConfigureLinux64Plugin(string assetPath, string cpu)
    {
        var importer = AssetImporter.GetAtPath(assetPath) as PluginImporter;
        if (importer == null)
        {
            throw new FileNotFoundException($"Linux native plugin importer not found: {assetPath}", assetPath);
        }

        importer.SetCompatibleWithAnyPlatform(false);
        importer.SetCompatibleWithEditor(false);
        importer.SetCompatibleWithPlatform(BuildTarget.StandaloneLinux64, true);
        importer.SetPlatformData(BuildTarget.StandaloneLinux64, "CPU", cpu);
        importer.SaveAndReimport();
    }

    private static void CopyLinuxServerGrpcNativeAlias(string outputPath, BuildReport report)
    {
        if (report.summary.result != BuildResult.Succeeded)
        {
            return;
        }

        string executableDirectory = Path.GetDirectoryName(outputPath);
        string executableName = Path.GetFileNameWithoutExtension(outputPath);
        if (string.IsNullOrEmpty(executableDirectory) || string.IsNullOrEmpty(executableName))
        {
            throw new InvalidOperationException("Invalid Linux server output path: " + outputPath);
        }

        string pluginDirectory = Path.Combine(executableDirectory, executableName + "_Data", "Plugins", "x86_64");
        string sourcePath = Path.Combine(pluginDirectory, GrpcLinuxNativeFileName);
        string aliasPath = Path.Combine(pluginDirectory, GrpcLinuxNativeAliasFileName);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException($"gRPC Linux native plugin not found in build output: {sourcePath}", sourcePath);
        }

        File.Copy(sourcePath, aliasPath, overwrite: true);
        Console.WriteLine($"Copied gRPC Linux native alias: {aliasPath}");
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
