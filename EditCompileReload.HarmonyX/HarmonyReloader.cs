using System.Reflection;
using HarmonyLib;

namespace EditCompileReload.HarmonyX;

/// <summary>
/// Keeps a mod's attribute-based Harmony patches in step with EditCompileReload reloads.
/// <para>
/// Patches the mod applied at startup point at the instrumented patch methods, which already run the newest body after
/// a reload. What a reload cannot reach by itself are patch methods that were added, deleted, or given a different
/// target or patch type. After each reload this class removes startup patches whose method was deleted or changed,
/// and applies every added or changed patch method from the reloaded code under its own Harmony ID.
/// </para>
/// <para>
/// Patches applied by hand with <see cref="Harmony.Patch(MethodBase, HarmonyMethod, HarmonyMethod, HarmonyMethod, HarmonyMethod, HarmonyMethod)"/>
/// are left alone. A new patch method in a class the mod did not patch at startup is not applied, so conditional patch
/// classes stay off. Reverse patches are not supported.
/// </para>
/// </summary>
public sealed class HarmonyReloader
{
    private const BindingFlags DeclaredStatic =
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    private static readonly Dictionary<string, HarmonyPatchType> AttributeKinds = new()
    {
        ["HarmonyLib.HarmonyPrefix"] = HarmonyPatchType.Prefix,
        ["HarmonyLib.HarmonyPostfix"] = HarmonyPatchType.Postfix,
        ["HarmonyLib.HarmonyTranspiler"] = HarmonyPatchType.Transpiler,
        ["HarmonyLib.HarmonyFinalizer"] = HarmonyPatchType.Finalizer,
        ["HarmonyLib.HarmonyILManipulator"] = HarmonyPatchType.ILManipulator,
        ["HarmonyLib.HarmonyReversePatch"] = HarmonyPatchType.ReversePatch,
    };

    private static readonly Dictionary<string, HarmonyPatchType> NamedKinds = new()
    {
        ["Prefix"] = HarmonyPatchType.Prefix,
        ["Postfix"] = HarmonyPatchType.Postfix,
        ["Transpiler"] = HarmonyPatchType.Transpiler,
        ["Finalizer"] = HarmonyPatchType.Finalizer,
        ["ILManipulator"] = HarmonyPatchType.ILManipulator,
        ["ReversePatch"] = HarmonyPatchType.ReversePatch,
    };

    private readonly Assembly original;
    private readonly Harmony reloadHarmony;
    private readonly Dictionary<string, PatchDefinition> startupPatches;
    private readonly HashSet<string> removedStartupPatches = [];

    /// <param name="original">The mod assembly loaded at startup.</param>
    /// <param name="harmonyId">The mod's Harmony ID. Reloaded patches use this ID with ".hotreload" appended.</param>
    public HarmonyReloader(Assembly original, string harmonyId)
    {
        this.original = original;
        reloadHarmony = new Harmony(harmonyId + ".hotreload");
        startupPatches = Collect(original, isOriginal: true);
    }

    /// <summary>Creates a reloader for <paramref name="original"/> and runs it after each of its reloads.</summary>
    public static HarmonyReloader Attach(Assembly original, string harmonyId)
    {
        var reloader = new HarmonyReloader(original, harmonyId);
        Ecr.Reloaded += info =>
        {
            if (info.Original == original)
                reloader.Apply(info.Latest);
        };
        return reloader;
    }

    /// <summary>Brings the patches in line with <paramref name="latest"/>, the assembly of the newest reload.</summary>
    public void Apply(Assembly latest)
    {
        var latestPatches = Collect(latest, isOriginal: false);
        reloadHarmony.UnpatchSelf();

        var applied = AppliedPatchMethods();
        int removed = 0;
        foreach (var startup in startupPatches.Values)
        {
            if (removedStartupPatches.Contains(startup.Key))
                continue;

            if (latestPatches.TryGetValue(startup.Key, out var current) && current.Description == startup.Description)
            {
                latestPatches.Remove(startup.Key); // Unchanged: the startup patch already runs the newest body.
                continue;
            }

            foreach (var target in applied.Where(a => a.Value.Contains(startup.Method)).Select(a => a.Key).ToList())
                reloadHarmony.Unpatch(target, startup.Method);
            removedStartupPatches.Add(startup.Key);
            removed++;
            EcrLog.Message($"Harmony: removed startup patch {startup.Key}; it was deleted or its patch attributes changed");
        }

        var patchedClasses = new HashSet<string>(applied.Values
            .SelectMany(methods => methods)
            .Where(m => m.DeclaringType?.Assembly == original)
            .Select(m => m.DeclaringType!.FullName!));

        int added = 0;
        foreach (var patch in latestPatches.Values)
        {
            bool classExistedAtStartup = startupPatches.Values.Any(p => p.ClassName == patch.ClassName);
            if (classExistedAtStartup && !patchedClasses.Contains(patch.ClassName) &&
                !removedStartupPatches.Any(k => startupPatches[k].ClassName == patch.ClassName))
            {
                EcrLog.Message($"Harmony: skipped {patch.Key}; its class was not patched at startup");
                continue;
            }

            foreach (var target in patch.Targets)
            {
                try
                {
                    var targetMethod = Resolve(target) ?? throw new InvalidOperationException("target method not found: " + Describe(target));
                    var harmonyMethod = target.Clone(patch.Method);
                    reloadHarmony.Patch(
                        targetMethod,
                        prefix: patch.Kind == HarmonyPatchType.Prefix ? harmonyMethod : null,
                        postfix: patch.Kind == HarmonyPatchType.Postfix ? harmonyMethod : null,
                        transpiler: patch.Kind == HarmonyPatchType.Transpiler ? harmonyMethod : null,
                        finalizer: patch.Kind == HarmonyPatchType.Finalizer ? harmonyMethod : null,
                        ilmanipulator: patch.Kind == HarmonyPatchType.ILManipulator ? harmonyMethod : null);
                    added++;
                }
                catch (Exception e)
                {
                    EcrLog.Error($"Harmony: could not apply reloaded patch {patch.Key}: {e.Message}");
                }
            }
        }

        EcrLog.Message($"Harmony: {added} reloaded patch(es) applied, {removed} startup patch(es) removed");
    }

    private static Dictionary<MethodBase, HashSet<MethodInfo>> AppliedPatchMethods()
    {
        var applied = new Dictionary<MethodBase, HashSet<MethodInfo>>();
        foreach (var target in Harmony.GetAllPatchedMethods().ToList())
        {
            var info = Harmony.GetPatchInfo(target);
            if (info == null) continue;
            applied[target] = new HashSet<MethodInfo>(
                info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers).Concat(info.ILManipulators)
                    .Select(p => p.PatchMethod));
        }

        return applied;
    }

    private Dictionary<string, PatchDefinition> Collect(Assembly assembly, bool isOriginal)
    {
        var patches = new Dictionary<string, PatchDefinition>();
        foreach (var type in GetTypes(assembly))
        {
            if (type.Namespace == "ECR") continue;

            // A reloaded copy of an existing class has no class attributes; read them from the startup class.
            var classSource = isOriginal ? type : original.GetType(type.FullName!) ?? type;
            HarmonyMethod? classInfo;
            try
            {
                var classAttributes = HarmonyMethodExtensions.GetFromType(classSource) ?? [];
                classInfo = classAttributes.Count > 0 ? HarmonyMethod.Merge(classAttributes) : null;
            }
            catch (Exception e)
            {
                // An attribute refers to an assembly that is not loaded, such as an optional dependency. Harmony cannot
                // patch this class either, so it is not reloaded.
                SkipUnreadable(type.FullName!, e, isOriginal);
                continue;
            }

            foreach (var method in type.GetMethods(DeclaredStatic))
            {
                string name = method.Name;
                if (name.EndsWith("_Invoke") || name.Contains("_PtrGetter")) continue;
                bool isImpl = name.EndsWith("_Impl");
                if (isOriginal && isImpl) continue; // Startup patches point at the stub, not the implementation.
                string baseName = isImpl ? name.Substring(0, name.Length - "_Impl".Length) : name;

                HarmonyPatchType? kind;
                List<HarmonyMethod> targets;
                try
                {
                    kind = GetKind(method, baseName, classInfo != null);
                    if (kind == null) continue;
                    if (kind == HarmonyPatchType.ReversePatch)
                    {
                        if (isOriginal) EcrLog.Message($"Harmony: reverse patch {type.FullName}.{baseName} is not reloaded");
                        continue;
                    }

                    targets = GetTargets(method, classInfo);
                }
                catch (Exception e)
                {
                    SkipUnreadable($"{type.FullName}.{baseName}", e, isOriginal);
                    continue;
                }

                if (targets.Count == 0) continue;

                var definition = new PatchDefinition(
                    $"{type.FullName}.{baseName}({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.FullName).ToArray())})",
                    type.FullName!,
                    method,
                    kind.Value,
                    targets);

                // A method added in this reload has a stub with its attributes and an implementation without them;
                // a method from an earlier version only has the implementation, which keeps its attributes.
                if (!patches.TryGetValue(definition.Key, out var existing) || (isImpl && !existing.Method.Name.EndsWith("_Impl")))
                    patches[definition.Key] = definition;
            }
        }

        return patches;
    }

    // Reported once, when the startup assembly is scanned; later scans skip the same members without a message.
    private static void SkipUnreadable(string member, Exception e, bool isOriginal)
    {
        if (!isOriginal) return;
        Exception cause = e is TargetInvocationException { InnerException: not null } ? e.InnerException! : e;
        string reason = cause is FileNotFoundException missing && missing.FileName != null
            ? "an attribute needs " + missing.FileName.Split(',')[0] + ", which is not loaded"
            : cause.GetType().Name + ": " + cause.Message;
        EcrLog.Message($"Harmony: not checking {member} for patches; {reason}");
    }

    private static HarmonyPatchType? GetKind(MethodInfo method, string baseName, bool inPatchClass)
    {
        foreach (var attribute in method.GetCustomAttributes(true))
            if (AttributeKinds.TryGetValue(attribute.GetType().FullName!, out var kind))
                return kind;

        bool hasPatchAttribute = method.GetCustomAttributes(true).Any(a => a.GetType().FullName == "HarmonyLib.HarmonyPatch");
        if ((inPatchClass || hasPatchAttribute) && NamedKinds.TryGetValue(baseName, out var named))
            return named;
        return null;
    }

    // Mirrors HarmonyX's attribute handling: several complete [HarmonyPatch] targets on one method patch each of them;
    // otherwise all attributes merge into one target. Class attributes fill in what the method leaves out.
    private static List<HarmonyMethod> GetTargets(MethodInfo method, HarmonyMethod? classInfo)
    {
        var attributes = HarmonyMethodExtensions.GetFromMethod(method) ?? [];
        static bool IsComplete(HarmonyMethod m) => m.declaringType != null && m.methodName != null;

        var incomplete = attributes.Where(a => !IsComplete(a)).ToList();
        var complete = attributes.Where(IsComplete).ToList();
        var merged = complete.Count > 1
            ? complete.Select(c => HarmonyMethod.Merge(incomplete.Concat([c]).ToList())).ToList()
            : [HarmonyMethod.Merge(attributes)];

        return merged
            .Select(m => classInfo != null ? classInfo.Merge(m) : m)
            .Where(m => m.declaringType != null)
            .ToList();
    }

    private static MethodBase? Resolve(HarmonyMethod target)
    {
        var type = target.declaringType;
        switch (target.methodType ?? MethodType.Normal)
        {
            case MethodType.Normal:
                return target.methodName == null ? null : AccessTools.DeclaredMethod(type, target.methodName, target.argumentTypes);
            case MethodType.Getter:
                return target.methodName == null ? null : AccessTools.DeclaredProperty(type, target.methodName)?.GetGetMethod(true);
            case MethodType.Setter:
                return target.methodName == null ? null : AccessTools.DeclaredProperty(type, target.methodName)?.GetSetMethod(true);
            case MethodType.Constructor:
                return AccessTools.DeclaredConstructor(type, target.argumentTypes);
            case MethodType.StaticConstructor:
                return AccessTools.GetDeclaredConstructors(type).FirstOrDefault(c => c.IsStatic);
            case MethodType.Enumerator:
                return target.methodName == null ? null :
                    AccessTools.EnumeratorMoveNext(AccessTools.DeclaredMethod(type, target.methodName, target.argumentTypes));
            default:
                return null;
        }
    }

    private static string Describe(HarmonyMethod m) =>
        $"{m.declaringType?.FullName}.{m.methodName}" +
        $"({(m.argumentTypes == null ? "*" : string.Join(", ", m.argumentTypes.Select(t => t.FullName).ToArray()))})" +
        $" {m.methodType ?? MethodType.Normal} priority={m.priority}" +
        $" before=[{string.Join(",", m.before ?? [])}] after=[{string.Join(",", m.after ?? [])}]";

    private static IEnumerable<Type> GetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types.Where(t => t != null)!;
        }
    }

    private sealed class PatchDefinition
    {
        public PatchDefinition(string key, string className, MethodInfo method, HarmonyPatchType kind, List<HarmonyMethod> targets)
        {
            Key = key;
            ClassName = className;
            Method = method;
            Kind = kind;
            Targets = targets;
            Description = kind + " " + string.Join("; ", targets.Select(HarmonyReloader.Describe).ToArray());
        }

        public string Key { get; }
        public string ClassName { get; }
        public MethodInfo Method { get; }
        public HarmonyPatchType Kind { get; }
        public List<HarmonyMethod> Targets { get; }
        public string Description { get; }
    }
}

internal static class HarmonyMethodCopy
{
    /// <summary>A copy of the target's patch settings that runs <paramref name="method"/>.</summary>
    public static HarmonyMethod Clone(this HarmonyMethod target, MethodInfo method) => new(method)
    {
        priority = target.priority,
        before = target.before,
        after = target.after,
        debug = target.debug,
    };
}
