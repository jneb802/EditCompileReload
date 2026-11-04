using System.Collections.Concurrent;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using EditCompileReload.HarmonyX;

namespace EditCompileReload.BepInEx;

/// <summary>
/// Runs EditCompileReload reloads for every instrumented plugin, so the plugins themselves need no reload code.
/// <para>
/// An instrumented plugin starts watching its <c>.dll_orig</c> file when it loads. This plugin sends ECR's messages to
/// the BepInEx log, switches each reload over on Unity's main thread, and keeps the reloaded plugin's attribute-based
/// Harmony patches in step with its code. It is a development tool: install it only next to instrumented builds.
/// </para>
/// </summary>
[BepInPlugin(Guid, "EditCompileReload hot reload host", Version)]
public sealed class HotReloadHostPlugin : BaseUnityPlugin
{
    public const string Guid = "EditCompileReload.BepInEx";
    public const string Version = "1.0.2";

    // Reloads are prepared on ECR's processing thread; the step that switches running code waits here for Update.
    private readonly ConcurrentQueue<Action> mainThreadQueue = new();
    private readonly Dictionary<Assembly, HarmonyReloader> harmonyReloaders = new();

    private void Awake()
    {
        EcrLog.messageCallback = message => Logger.LogInfo(message);
        EcrLog.errorCallback = message => Logger.LogError(message);
        Ecr.applyDispatcher = mainThreadQueue.Enqueue;
        Ecr.Reloaded += ReloadHarmonyPatches;
    }

    private void Update()
    {
        while (mainThreadQueue.TryDequeue(out Action? apply))
            apply();
    }

    private void OnDestroy()
    {
        Ecr.Reloaded -= ReloadHarmonyPatches;
        Ecr.applyDispatcher = null;
    }

    private void ReloadHarmonyPatches(ReloadInfo info)
    {
        if (!harmonyReloaders.TryGetValue(info.Original, out HarmonyReloader? reloader))
        {
            reloader = new HarmonyReloader(info.Original, PluginGuid(info.Original) ?? info.AssemblyName);
            harmonyReloaders[info.Original] = reloader;
        }

        reloader.Apply(info.Latest);
    }

    private static string? PluginGuid(Assembly assembly) =>
        Chainloader.PluginInfos.Values
            .FirstOrDefault(plugin => plugin.Instance != null && plugin.Instance.GetType().Assembly == assembly)
            ?.Metadata.GUID;
}
