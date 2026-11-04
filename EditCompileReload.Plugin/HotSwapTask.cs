using AsmResolver.DotNet;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Task = Microsoft.Build.Utilities.Task;

namespace EditCompileReload.Plugin;

public class HotSwapTask : Task
{
    public string ProjectAssembly { get; set; }
    public string OutputFolder { get; set; }

    [Output]
    public ITaskItem[] MoveSourceFiles { get; set; }

    [Output]
    public ITaskItem[] DestinationFiles { get; set; }

    public override bool Execute()
    {
        Directory.CreateDirectory(OutputFolder);
        string outputFile = $"{OutputFolder}{Path.GetFileName(ProjectAssembly)}";
        (byte[] asmBytes, var implToOldOrigRid) = AsmWriter.RewriteOriginal(ProjectAssembly);
        File.WriteAllBytes(outputFile, asmBytes);
        var sources = new List<ITaskItem> { new TaskItem(outputFile) };
        var destinations = new List<ITaskItem> { new TaskItem(ProjectAssembly) };

        // The compiler's symbols describe the methods before rewriting. A runtime that reads them for the rewritten
        // assembly looks up method rows that do not exist; Mono aborts on that. Rewrite them to match.
        string pdb = Path.ChangeExtension(ProjectAssembly, ".pdb");
        if (File.Exists(pdb))
        {
            byte[] pdbBytes = File.ReadAllBytes(pdb);
            if (IsPortablePdb(pdbBytes))
            {
                string outputPdb = Path.ChangeExtension(outputFile, ".pdb");
                File.WriteAllBytes(outputPdb,
                    AsmWriter.RewritePortablePdb(pdbBytes, AssemblyDefinition.FromBytes(asmBytes), implToOldOrigRid));
                sources.Add(new TaskItem(outputPdb));
                destinations.Add(new TaskItem(pdb));
            }
            else
            {
                Log.LogWarning($"EditCompileReload deleted {pdb}: only portable PDBs can be rewritten, and stale symbols can crash Mono. Set <DebugType>portable</DebugType>.");
                File.Delete(pdb);
            }
        }

        MoveSourceFiles = sources.ToArray();
        DestinationFiles = destinations.ToArray();
        return true;
    }

    private static bool IsPortablePdb(byte[] bytes) =>
        bytes.Length >= 4 && bytes[0] == (byte)'B' && bytes[1] == (byte)'S' && bytes[2] == (byte)'J' && bytes[3] == (byte)'B';
}
