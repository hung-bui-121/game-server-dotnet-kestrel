using System;
using System.Linq;
using SampleGame.Client;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Batch-mode entry points of the KestrelDemo sample client (run by sdk/unity/ci/smoke-build.ps1):
/// <see cref="SetupDemo"/> creates the demo scene, <see cref="BuildAndroid"/> is the CI-02 smoke build
/// (Android arm64, IL2CPP, managed stripping High), <see cref="EndToEnd"/> runs the sample flow with UnityWebRequest
/// against a live SampleHost (<c>GS_SERVER_URL</c>; <c>-e2eDevice name</c> keeps one guest device across runs).
/// </summary>
public static class KestrelDemoBuild
{
    public const string DemoScene = "Assets/Scenes/SampleGameClient.unity";

    /// <summary>Command-line switch of <see cref="EndToEnd"/>: the PlayerPrefs name of a guest device kept across runs.</summary>
    private const string DeviceSwitch = "-e2eDevice";

    public static void SetupDemo()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
        new GameObject("SampleGameClient").AddComponent<SampleGameClient>();
        EditorSceneManager.SaveScene(scene, DemoScene);

        var scenes = EditorBuildSettings.scenes.Where(s => s.path != DemoScene).ToList();
        scenes.Insert(0, new EditorBuildSettingsScene(DemoScene, true));
        EditorBuildSettings.scenes = scenes.ToArray();

        // The demo talks to a local SampleHost over plain HTTP: allowed in the Editor and development builds only.
        PlayerSettings.insecureHttpOption = InsecureHttpOption.DevelopmentOnly;
        AssetDatabase.SaveAssets();
        Console.WriteLine("[demo] scene and settings ready");
    }

    /// <summary>
    /// Started without -quit: the editor update loop drives UnityWebRequest and the awaits until Exit. Each run logs in a new guest,
    /// unless <c>-e2eDevice name</c> names a device kept in PlayerPrefs (the scene client's rule), so a second run logs in the same
    /// player and resends what the first one left pending (FG-022).
    /// </summary>
    public static void EndToEnd()
    {
        var url = Environment.GetEnvironmentVariable("GS_SERVER_URL") ?? "http://localhost:5080/";
        if (!TryDeviceName(out var device))
        {
            Console.WriteLine($"[e2e] {DeviceSwitch} needs a device name, e.g. {DeviceSwitch} t068");
            EditorApplication.Exit(1);
            return;
        }

        var started = DateTime.UtcNow;
        var finished = false;
        EditorApplication.update += () =>
        {
            if (!finished && DateTime.UtcNow - started > TimeSpan.FromSeconds(90))
            {
                Console.WriteLine("[e2e] timeout");
                EditorApplication.Exit(2);
            }
        };

        RunEndToEnd(new Uri(url), device, () => finished = true);
    }

    private static async void RunEndToEnd(Uri url, string device, Action done)
    {
        var exitCode = 1;
        try
        {
            var client = SampleGameFlow.CreateClient(url, "editor", line => Console.WriteLine("[e2e] " + line));
            var (localId, secret) = device == null ? SampleGameFlow.NewDevice() : SampleGameFlow.StoredDevice("gs.e2e." + device);
            var run = await SampleGameFlow.RunAsync(client, localId, secret, line => Console.WriteLine("[e2e] " + line));

            // A battle end whose answer was dropped is the pending path, not a failure: the next run with the same device resends it.
            var ended = run.EndCode == "OK" || run.EndPendingId != null;
            var ok = run.StartCode == "OK" && ended && run.WalletRev > 0 && client.Config.Version != null;
            Console.WriteLine(
                $"[e2e] result={(ok ? "OK" : "UNEXPECTED")} player={run.PlayerId} new={run.IsNewPlayer} buy={run.BuyCode} gold={run.Gold} " +
                $"pending={client.PendingCommands.Count}");
            exitCode = ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[e2e] failed: " + ex);
        }
        finally
        {
            done();
            EditorApplication.Exit(exitCode);
        }
    }

    /// <summary>The value after <see cref="DeviceSwitch"/>; null without the switch. False when the switch has no value.</summary>
    private static bool TryDeviceName(out string device)
    {
        var args = Environment.GetCommandLineArgs();
        var at = Array.IndexOf(args, DeviceSwitch);
        device = at >= 0 && at + 1 < args.Length && !args[at + 1].StartsWith("-", StringComparison.Ordinal) ? args[at + 1] : null;
        return at < 0 || device != null;
    }

    public static void BuildAndroid()
    {
        var android = UnityEditor.Build.NamedBuildTarget.Android;
        PlayerSettings.SetScriptingBackend(android, ScriptingImplementation.IL2CPP);
        PlayerSettings.SetManagedStrippingLevel(android, ManagedStrippingLevel.High);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.SetApplicationIdentifier(android, "com.gameserver.kestreldemo");

        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { DemoScene },
            locationPathName = "Build/KestrelDemo.apk",
            target = BuildTarget.Android,
            options = BuildOptions.Development,
        });

        Console.WriteLine($"[smoke] result={report.summary.result} size={report.summary.totalSize} errors={report.summary.totalErrors}");
        EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
    }
}
