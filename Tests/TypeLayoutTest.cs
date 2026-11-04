using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;
using EditCompileReload;

namespace Tests;

public class TypeLayoutTest
{
    private const string Original = "../../../../TestAssembly1/bin/Debug/net472/TestAssembly1.dll";
    private const string SupportedEdits = "../../../../TestAssembly2/bin/Debug/net472/TestAssembly1.dll";

    private static Dictionary<string, TypeLayout> OriginalLayouts() =>
        TypeLayout.FromAssembly(AssemblyDefinition.FromFile(Original));

    private static (AssemblyDefinition, TypeDefinition) Candidate(string typeName)
    {
        var assembly = AssemblyDefinition.FromFile(Original);
        return (assembly, assembly.ManifestModule!.GetAllTypes().First(t => t.FullName == typeName));
    }

    [Test]
    public void AcceptsTheSupportedEditsOfTheReloadTests()
    {
        Assert.That(
            TypeLayout.FindUnsupportedChanges(OriginalLayouts(), AssemblyDefinition.FromFile(SupportedEdits)),
            Is.Empty);
    }

    [Test]
    public void RefusesAnAddedInstanceField()
    {
        var (assembly, type) = Candidate("TestAssembly1.Class1");
        type.Fields.Add(new FieldDefinition("added", FieldAttributes.Private,
            assembly.ManifestModule!.CorLibTypeFactory.Int32));

        var problems = TypeLayout.FindUnsupportedChanges(OriginalLayouts(), assembly);

        Assert.That(problems, Has.Count.EqualTo(1));
        Assert.That(problems[0], Does.Contain("TestAssembly1.Class1: instance fields changed"));
    }

    [Test]
    public void RefusesAnAddedInstanceFieldOnAStruct()
    {
        var (assembly, type) = Candidate("TestAssembly1.Struct");
        type.Fields.Add(new FieldDefinition("added", FieldAttributes.Public,
            assembly.ManifestModule!.CorLibTypeFactory.Int64));

        Assert.That(TypeLayout.FindUnsupportedChanges(OriginalLayouts(), assembly), Has.Count.EqualTo(1));
    }

    [Test]
    public void AcceptsAnAddedStaticField()
    {
        var (assembly, type) = Candidate("TestAssembly1.Class1");
        type.Fields.Add(new FieldDefinition("addedStatic", FieldAttributes.Private | FieldAttributes.Static,
            assembly.ManifestModule!.CorLibTypeFactory.Int32));

        Assert.That(TypeLayout.FindUnsupportedChanges(OriginalLayouts(), assembly), Is.Empty);
    }

    [Test]
    public void RefusesAnAddedVirtualMethod()
    {
        var (assembly, type) = Candidate("TestAssembly1.Class1");
        var module = assembly.ManifestModule!;
        var method = new MethodDefinition("AddedVirtual",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
            MethodSignature.CreateInstance(module.CorLibTypeFactory.Void));
        method.CilMethodBody = new AsmResolver.DotNet.Code.Cil.CilMethodBody();
        method.CilMethodBody.Instructions.Add(AsmResolver.PE.DotNet.Cil.CilOpCodes.Ret);
        type.Methods.Add(method);

        var problems = TypeLayout.FindUnsupportedChanges(OriginalLayouts(), assembly);

        Assert.That(problems, Has.Count.EqualTo(1));
        Assert.That(problems[0], Does.Contain("virtual method added"));
    }

    [Test]
    public void RefusesAChangedBaseType()
    {
        var (assembly, type) = Candidate("TestAssembly1.Class1");
        type.BaseType = assembly.ManifestModule!.GetAllTypes().First(t => t.Name == "Class4");

        var problems = TypeLayout.FindUnsupportedChanges(OriginalLayouts(), assembly);

        Assert.That(problems, Has.Some.Contains("base type changed"));
    }
}
