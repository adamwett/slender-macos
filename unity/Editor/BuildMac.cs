using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.OSXStandalone;
using UnityEngine;

/// <summary>
/// Builds a native Apple Silicon (arm64) macOS player.
/// Invoked headlessly: Unity -quit -batchmode -projectPath ... -executeMethod BuildMac.Build
/// </summary>
public static class BuildMac
{
    const string ScenePath = "Assets/Slender.unity";
    const string OutputPath = "Build/Slender.app";

    [MenuItem("Port/Build Apple Silicon")]
    public static void Build()
    {
        PlayerSettings.companyName = "Parsec Productions";
        PlayerSettings.productName = "Slender";
        PlayerSettings.bundleVersion = "0.9.6";
        PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Standalone,
            "com.parsecproductions.slender");

        // Mono is fine on arm64 and matches the original build's runtime; IL2CPP
        // only adds build time for a game this small.
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, ScriptingImplementation.Mono2x);
        PlayerSettings.SetApiCompatibilityLevel(BuildTargetGroup.Standalone, ApiCompatibilityLevel.NET_Unity_4_8);
        UserBuildSettings.architecture = UnityEditor.Build.OSArchitecture.ARM64;
        UserBuildSettings.createXcodeProject = false;

        PlayerSettings.defaultScreenWidth = 1280;
        PlayerSettings.defaultScreenHeight = 720;
        PlayerSettings.fullScreenMode = FullScreenMode.FullScreenWindow;
        PlayerSettings.runInBackground = false;
        PlayerSettings.resizableWindow = true;
        PlayerSettings.macRetinaSupport = true;
        PlayerSettings.colorSpace = ColorSpace.Gamma; // 3.5 authored in gamma space

        EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Standalone, BuildTarget.StandaloneOSX);

        var options = new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = OutputPath,
            target = BuildTarget.StandaloneOSX,
            targetGroup = BuildTargetGroup.Standalone,
            options = BuildOptions.None,
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);
        var s = report.summary;
        Debug.Log($"=== BUILD {s.result} === {s.totalSize / (1024 * 1024)} MB in {s.totalTime} " +
                  $"errors={s.totalErrors} warnings={s.totalWarnings} -> {s.outputPath}");

        if (s.result != BuildResult.Succeeded)
        {
            foreach (var step in report.steps)
                foreach (var msg in step.messages)
                    if (msg.type == LogType.Error || msg.type == LogType.Exception)
                        Debug.LogError($"[{step.name}] {msg.content}");
            EditorApplication.Exit(1);
        }
        EditorApplication.Exit(0);
    }
}
