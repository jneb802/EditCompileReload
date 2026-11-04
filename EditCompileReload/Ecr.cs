using System.Reflection;
using AsmResolver;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Serialized;
using AsmResolver.PE;

namespace EditCompileReload;

/// <summary>Describes one applied reload. Passed to <see cref="Ecr.Reloaded"/>.</summary>
public sealed class ReloadInfo
{
    internal ReloadInfo(string assemblyName, int version, Assembly original, Assembly latest)
    {
        AssemblyName = assemblyName;
        Version = version;
        Original = original;
        Latest = latest;
    }

    /// <summary>The simple name of the reloaded assembly.</summary>
    public string AssemblyName { get; }

    /// <summary>The reload version, counting the original load as 1.</summary>
    public int Version { get; }

    /// <summary>The instrumented assembly loaded at startup. Its types, fields and objects stay in use.</summary>
    public Assembly Original { get; }

    /// <summary>The assembly that holds the code of this reload.</summary>
    public Assembly Latest { get; }
}

public class Ecr
{
    public static void ResetAllFields()
    {
        lock (AsmStore.seenPtrsTypes)
        {
            EcrLog.Message($"Resetting {AsmStore.seenPtrsTypes.Count} seen generic type configurations");

            foreach (var ptrsType in AsmStore.seenPtrsTypes)
            {
                var arrField = Type.GetTypeFromHandle(ptrsType).GetField("arr");
                arrField.SetValue(null, new IntPtr[((IntPtr[])arrField.GetValue(null)).Length]);
            }
        }
    }

    // A background thread, so a host process can exit while the thread waits for work.
    private static Thread processingThread = new(ProcessingThreadLogic) { IsBackground = true };
    private static BlockingQueue<Action> actionQueue = new();

    static Ecr()
    {
        processingThread.Name = "EditCompileReload processing thread";
        processingThread.Start();
    }

    public static void ProcessingThreadLogic()
    {
        while (true)
        {
            var action = actionQueue.Dequeue();
            action();
        }
    }

    private static HashSet<string> registeredFileWatchers = [];

    /// <summary>How long the watched file must go without changes before it is reloaded.</summary>
    public static int SettleMilliseconds = 1000;

    /// <summary>
    /// Runs the step that switches running code to a reload. Reading, rewriting and loading the changed assembly happen
    /// on ECR's processing thread; switching method pointers, the <c>OnEditCompileReload</c> callbacks and
    /// <see cref="Reloaded"/> run through this dispatcher. A host whose code runs on one thread, such as a Unity game,
    /// sets it to queue the action for that thread. The processing thread waits until the action has run.
    /// When null, the action runs on the processing thread.
    /// </summary>
    public static Action<Action>? applyDispatcher;

    /// <summary>Raised after a reload has been applied, on the thread <see cref="applyDispatcher"/> uses.</summary>
    public static event Action<ReloadInfo>? Reloaded;

    /// <summary>
    /// Watches <paramref name="fileName"/> in the folder the assembly declaring <paramref name="anchor"/> was loaded from,
    /// so the instrumented assembly can be deployed to another folder or machine. An assembly loaded from bytes has no
    /// folder; it watches <paramref name="fallbackPath"/>, the path at build time.
    /// </summary>
    public static void RegisterFileWatcherNextTo(RuntimeTypeHandle anchor, string fileName, string fallbackPath)
    {
        string location = Type.GetTypeFromHandle(anchor).Assembly.Location;
        RegisterFileWatcher(string.IsNullOrEmpty(location)
            ? fallbackPath
            : Path.Combine(Path.GetDirectoryName(location)!, fileName));
    }

    public static void RegisterFileWatcher(string filePath)
    {
        filePath = Path.GetFullPath(filePath);
        if (!registeredFileWatchers.Add(filePath))
            return;

        var fileName = Path.GetFileName(filePath);
        var watcher = new FileSystemWatcher();
        watcher.Path = Path.GetDirectoryName(filePath);
        // A copy can replace the file by writing it in place (Changed) or by renaming a finished temporary file over
        // it (Renamed or Created), so watch for all three.
        watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName;
        watcher.Filter = fileName;

        var origAsm = AssemblyDefinition.FromImage(PEImage.FromFile(filePath));
        string origAsmName = origAsm.Name!;
        var origAsmReflection = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == origAsmName);

        AsmWriter.UpdateMemberDobs(origAsm);
        AsmStore.assemblyData[origAsmName].layouts = TypeLayout.FromAssembly(origAsm);
        AsmStore.assemblyData[origAsmName].assemblies.Add(origAsmReflection);
        AsmStore.assemblyData[origAsmName].version++;

        EcrLog.Message("Registered file watcher");

        int latestEvent = 0;
        void OnFileEvent(string fullPath)
        {
            EcrLog.Verbose($"Got file event: {fullPath} {fileName}");

            if (!fullPath.EndsWith(fileName)) return;

            // An in-place write raises several events while the file is still incomplete. Reload once the file has
            // been quiet for SettleMilliseconds, and only for the newest event.
            int thisEvent = Interlocked.Increment(ref latestEvent);
            actionQueue.Enqueue(() =>
            {
                Thread.Sleep(SettleMilliseconds);
                if (Thread.VolatileRead(ref latestEvent) != thisEvent)
                    return;
                ProcessAssembly(origAsmName, filePath);
            });
        }

        watcher.Changed += (_, e) => OnFileEvent(e.FullPath);
        watcher.Created += (_, e) => OnFileEvent(e.FullPath);
        watcher.Renamed += (_, e) => OnFileEvent(e.FullPath);

        watcher.EnableRaisingEvents = true;
    }

    public static bool writeSwappedAssembly;

    private static void ProcessAssembly(string assemblyName, string path)
    {
        EcrLog.Message("ProcessAssembly start");
        var total = System.Diagnostics.Stopwatch.StartNew();

        var assemblyDataCopy = AsmStore.assemblyData[assemblyName].Copy();

        try
        {
            var assembly =
                AssemblyDefinition.FromImage(
                    PEImage.FromFile(path),
                    new ModuleReaderParameters(Path.GetDirectoryName(path), EmptyErrorListener.Instance));

            // The loaded types keep their original fields and slots, so code written for another layout would read
            // and write the wrong memory. Refuse such a build before anything changes.
            var unsupported = TypeLayout.FindUnsupportedChanges(AsmStore.assemblyData[assemblyName].layouts, assembly);
            if (unsupported.Count > 0)
            {
                EcrLog.Error($"Reload of {assemblyName} refused; restart to apply these changes:\n  " +
                    string.Join("\n  ", unsupported.ToArray()));
                return;
            }

            AsmWriter.UpdateMemberDobs(assembly);
            var (newAsmBytes, ptrGetterToOrig, _) = AsmWriter.RewriteChanged(assembly);

            if (writeSwappedAssembly)
                File.WriteAllBytes("swapped.dll", newAsmBytes);

            var newAsm = AssemblyDefinition.FromBytes(newAsmBytes);
            var newAsmReflection = AppDomain.CurrentDomain.Load(newAsmBytes);

            AsmHelper.CheckIsUsable(newAsmReflection);

            Exception? applyException = null;
            RunApply(() =>
            {
                try
                {
                    Apply(assemblyName, newAsm, newAsmReflection, ptrGetterToOrig, newAsmBytes.Length, total);
                }
                catch (Exception e)
                {
                    applyException = e;
                }
            });
            if (applyException != null)
                throw new InvalidOperationException("Applying the reload failed", applyException);
        }
        catch (Exception e)
        {
            AsmStore.assemblyData[assemblyName] = assemblyDataCopy;
            EcrLog.Error($"ProcessAssembly exception {e}");
        }
    }

    private static void RunApply(Action apply)
    {
        var dispatcher = applyDispatcher;
        if (dispatcher == null)
        {
            apply();
            return;
        }

        using var done = new ManualResetEvent(false);
        dispatcher(() =>
        {
            try { apply(); }
            finally { done.Set(); }
        });
        done.WaitOne();
    }

    private static void Apply(
        string assemblyName,
        AssemblyDefinition newAsm,
        Assembly newAsmReflection,
        Dictionary<string, string> ptrGetterToOrig,
        int size,
        System.Diagnostics.Stopwatch total)
    {
        var data = AsmStore.assemblyData[assemblyName];
        AsmStore.UpdateMethodReferences(newAsm, newAsmReflection, ptrGetterToOrig);

        data.version++;

        ResetAllFields();
        data.assemblies.Add(newAsmReflection);
        EcrLog.Message($"Reloaded {assemblyName} version {data.version}: {size} bytes in {total.ElapsedMilliseconds}ms");

        foreach (var t in newAsmReflection.GetTypes())
        {
            try
            {
                t.GetMethod(
                    "OnEditCompileReload_Impl",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly,
                    null,
                    [], null
                )?.Invoke(null, null);
            }
            catch (Exception e)
            {
                EcrLog.Error($"Exception while running reload callback on {t}: {e}");
            }
        }

        var reloaded = Reloaded;
        if (reloaded == null)
            return;

        var info = new ReloadInfo(assemblyName, data.version, data.assemblies[0], newAsmReflection);
        foreach (Action<ReloadInfo> handler in reloaded.GetInvocationList())
        {
            try
            {
                handler(info);
            }
            catch (Exception e)
            {
                EcrLog.Error($"Exception in a Reloaded handler: {e}");
            }
        }
    }
}
