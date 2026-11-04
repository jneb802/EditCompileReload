using AsmResolver.DotNet;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace EditCompileReload;

/// <summary>
/// The parts of a loaded type that a reload cannot change. Reloaded code still runs against the type loaded at startup,
/// so it must find the same instance fields at the same offsets, the same base type and interfaces, and the same virtual
/// method slots. Compiler-generated types are always reloaded as new types, so they are not recorded.
/// </summary>
public sealed class TypeLayout
{
    private string baseType = "";
    private string layoutKind = "";
    private string[] instanceFields = [];
    private HashSet<string> interfaces = [];
    private HashSet<string> virtualMethods = [];
    private HashSet<string> instanceConstructors = [];

    public static Dictionary<string, TypeLayout> FromAssembly(AssemblyDefinition assembly)
    {
        var layouts = new Dictionary<string, TypeLayout>();
        foreach (var type in assembly.ManifestModule!.GetAllTypes())
        {
            if (type.IsModuleType || AsmWriter.IsCompilerGeneratedRecursive(type))
                continue;
            layouts[type.ToString()] = From(type);
        }

        return layouts;
    }

    private static TypeLayout From(TypeDefinition type)
    {
        return new TypeLayout
        {
            baseType = type.BaseType?.FullName ?? "",
            layoutKind = $"{type.Attributes & TypeAttributes.LayoutMask} pack={type.ClassLayout?.PackingSize} size={type.ClassLayout?.ClassSize}",
            instanceFields = type.Fields
                .Where(f => !f.IsStatic)
                .Select(f => $"{f.Name}: {f.Signature?.FieldType.FullName}" + (f.FieldOffset is { } offset ? $" @{offset}" : ""))
                .ToArray(),
            interfaces = type.Interfaces.Select(i => i.Interface?.FullName ?? "").ToHashSet(),
            virtualMethods = type.Methods.Where(m => m.IsVirtual).Select(Describe).ToHashSet(),
            instanceConstructors = type.Methods.Where(m => m.IsConstructor && !m.IsStatic).Select(Describe).ToHashSet(),
        };
    }

    private static string Describe(MethodDefinition method) => $"{method.Name}{method.Signature}";

    /// <summary>Lists the changes in <paramref name="candidate"/> that a reload cannot apply to the loaded types.</summary>
    public static List<string> FindUnsupportedChanges(Dictionary<string, TypeLayout> original, AssemblyDefinition candidate)
    {
        var problems = new List<string>();
        foreach (var type in candidate.ManifestModule!.GetAllTypes())
        {
            if (type.IsModuleType || AsmWriter.IsCompilerGeneratedRecursive(type))
                continue;
            if (!original.TryGetValue(type.ToString(), out var before))
                continue; // A new type is loaded as a new type.

            var after = From(type);
            string name = type.ToString();

            if (before.baseType != after.baseType)
                problems.Add($"{name}: base type changed from {before.baseType} to {after.baseType}");

            if (before.layoutKind != after.layoutKind)
                problems.Add($"{name}: layout changed from {before.layoutKind} to {after.layoutKind}");

            if (!before.instanceFields.SequenceEqual(after.instanceFields))
                problems.Add($"{name}: instance fields changed (added, removed, reordered or retyped). " +
                    $"Before: [{string.Join(", ", before.instanceFields)}]. After: [{string.Join(", ", after.instanceFields)}]");

            foreach (var added in after.interfaces.Where(i => !before.interfaces.Contains(i)))
                problems.Add($"{name}: interface added: {added}");
            foreach (var removed in before.interfaces.Where(i => !after.interfaces.Contains(i)))
                problems.Add($"{name}: interface removed: {removed}");

            foreach (var added in after.virtualMethods.Where(m => !before.virtualMethods.Contains(m)))
                problems.Add($"{name}: virtual method added or made virtual: {added}");
            foreach (var removed in before.virtualMethods.Where(m => !after.virtualMethods.Contains(m)))
                problems.Add($"{name}: virtual method removed or made non-virtual: {removed}");

            foreach (var added in after.instanceConstructors.Where(c => !before.instanceConstructors.Contains(c)))
                problems.Add($"{name}: constructor added: {added}");
        }

        return problems;
    }
}
