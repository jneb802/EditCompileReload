# EditCompileReload (ECR)
[![EditCompileReload](https://img.shields.io/nuget/v/EditCompileReload?label=EditCompileReload)](https://www.nuget.org/packages/EditCompileReload)
[![EditCompileReload.CompilerPlugin](https://img.shields.io/nuget/v/EditCompileReload.CompilerPlugin?label=EditCompileReload.CompilerPlugin)](https://www.nuget.org/packages/EditCompileReload.CompilerPlugin)

<img width="400" alt="ecr_diagram" src="https://github.com/user-attachments/assets/7a92846c-9795-40e2-9988-1b7696e8bf77" />

Hot code reload as a library for .NET 3.5+ driven by a file watcher.

## How to use (with compiler plugin)
Add the library and plugin as package references in the `.csproj` file. Make sure not to include the library in your release build.
```xml
<ItemGroup Condition="'$(Configuration)' == 'Debug'">
  <PackageReference Include="EditCompileReload" Version="_"/>
  <PackageReference Include="EditCompileReload.CompilerPlugin" Version="_"/>
</ItemGroup>
```
A file watcher is automatically registered in the module initializer of the project's output assembly by the compiler plugin.

Optionally, you can set ECR's logging callbacks in your code.
```csharp
#if DEBUG
using EditCompileReload;
#endif
// ...
#if DEBUG
EcrLog.messageCallback = ...
EcrLog.errorCallback = ...
#endif
```
Example:
```csharp
#if DEBUG
using EditCompileReload;
#endif
// ...
#if DEBUG
EcrLog.messageCallback = s => Log.Message(s);
EcrLog.errorCallback = s => Log.Error(s);
#endif
```
Recompile during runtime to trigger a reload.

### With `PersonalDefinitions.targets`
If you don't want to check in a dependency on ECR into VCS for other developers
you can create a `.gitignore`d targets file with the required `PackageReference`s.

`PersonalDefinitions.targets`:
```xml
<Project>
  <ItemGroup Condition="'$(Configuration)' == 'Debug'">
    <PackageReference Include="EditCompileReload" Version="_"/>
    <PackageReference Include="EditCompileReload.CompilerPlugin" Version="_"/>
  </ItemGroup>

  <PropertyGroup Condition="'$(Configuration)' == 'Debug'">
    <DefineConstants>$(DefineConstants);EDIT_COMPILE_RELOAD</DefineConstants>
  </PropertyGroup>
</Project>
```
In your `.csproj`:
```xml
<Import Project="PersonalDefinitions.targets" Condition="exists('PersonalDefinitions.targets')" />
```
The callbacks:
```csharp
```csharp
#if EDIT_COMPILE_RELOAD
using EditCompileReload;
#endif
// ...
#if EDIT_COMPILE_RELOAD
EcrLog.messageCallback = ...
EcrLog.errorCallback = ...
#endif
```

## How to use (library)
It's best to look at an example in the [test suite](https://github.com/Zetrith/EditCompileReload/blob/master/Tests/RunTest.cs).

## Compiler plugin
The compiler plugin runs an MSBuild task, `HotSwapTask`, which, after the `CopyFilesToOutputDirectory` target:
- Instruments the project assembly adding helpers for hotswapping
- Rewrites a portable `.pdb` to match the instrumented assembly. A `.pdb` in another format is deleted, because a runtime that reads stale symbols for the rewritten methods can crash (Mono aborts)
- Copies the original project assembly to a file with a `.dll_orig` extension

The instrumented assembly watches the `.dll_orig` file in the folder it was loaded from, so the build output can be
deployed elsewhere (for example a game's plugin folder or another machine). Copy the `.dll_orig` of a new build next to
the deployed assembly to reload. Copies that write the file in place and copies that rename a finished file over it are
both detected; the reload starts once the file has not changed for `Ecr.SettleMilliseconds` (1000 by default).

## Running reloads on the host's main thread
Reading, rewriting and loading a changed assembly happen on ECR's processing thread. The step that switches running code
to the new version, the `OnEditCompileReload` callbacks and the `Ecr.Reloaded` event run through `Ecr.applyDispatcher`.
A host whose code runs on one thread, such as a Unity game, queues that step for its main thread:
```csharp
static readonly ConcurrentQueue<Action> reloads = new();
// At startup:
Ecr.applyDispatcher = reloads.Enqueue;
// Once per frame, on the main thread:
while (reloads.TryDequeue(out var apply)) apply();
```
`Ecr.Reloaded` passes a `ReloadInfo` with the original and the newest assembly.

## HarmonyX patches
Calls from an instrumented method go through a small `_Invoke` helper, so HarmonyX/MonoMod can patch instrumented
methods. Patches applied at startup run the newest body of their patch method after a reload. To also apply patch
methods that were added, deleted or retargeted, reference `EditCompileReload.HarmonyX` and attach it once at startup:
```csharp
EditCompileReload.HarmonyX.HarmonyReloader.Attach(typeof(MyPlugin).Assembly, MyPlugin.HarmonyId);
```
It removes startup patches whose method was deleted or whose patch attributes changed, and applies added or changed
attribute patches under the ID `<HarmonyId>.hotreload`. Patches applied by hand with `Harmony.Patch`, reverse patches,
and new patch methods in classes that were not patched at startup are left alone. The host provides `0Harmony`.

## BepInEx host plugin
`EditCompileReload.BepInEx` is a BepInEx 5 plugin that does the host's part for every instrumented plugin, so a mod
only needs the compiler plugin and no reload code. It sends ECR messages to the BepInEx log, runs the reload switch on
Unity's main thread, and runs `HarmonyReloader` for each reloaded plugin under that plugin's GUID.

Build it with `dotnet build EditCompileReload.BepInEx -c Release` and copy the whole
`EditCompileReload.BepInEx/bin/Release` folder into `BepInEx/plugins`. It contains the ECR runtime and its
dependencies, which the instrumented plugins load from there. A mod then only references the compiler plugin package:
```xml
<PackageReference Include="EditCompileReload.CompilerPlugin" Version="..." PrivateAssets="all" />
```
Install the host plugin only next to instrumented builds; it is a development tool.

For a step-by-step setup of a Valheim mod, see [ValheimModGuide.md](ValheimModGuide.md).

## Supported edits
- Adding, modifying and removing:
    - Types
    - Static and instance methods
    - Static fields
    - Enumerators and lambdas
    - Generic classes and methods (case not supported by Roslyn before .NET 8.0)
- Debugging
  - Debug symbols are correctly mapped to hotswapped code (WIP)
- Multiple projects/assemblies
  - Reloading logic runs on a single thread so file watcher triggers are scheduled in the correct order when building multiple projects
- Callback
  - Add a static `OnEditCompileReload` parameterless method anywhere in your code for a reload callback

### Caveats
- Functions running on the stack won't get immediately hotswapped
    - The function needs to be called again for new code to run
    - Function calls are effectively boundaries where hotswapped code can start running
- Stack traces for hotswapped code have twice as many frames as normally
- The original assembly isn't updated for reflection
  - You can still access reloaded assemblies to get new members (f.e. for an attribute scan)
- Compiler-generated code is always hotswapped anew
    - If there's some enumerator in progress it won't get hotswapped, only new instances will run new code
- Multithreading with shared mutable data can cause problems
    - Different threads can start executing new code at different times
    - New code working with such data can break invariants
- It's not possible to:
    - Add members to nested types (current limitation, to be supported)
    - Add instance fields (current limitation, to be supported)
    - Add constructors
    - Add or remove interfaces of a type, or change its field layout
    - Add, remove or modify attributes of existing members
    - Change the base type of a type
    - Add virtual methods
    - Change the virtuality of a method
    - Rename members
      - Would require IDE support

  A build that adds, removes, reorders or retypes instance fields of a loaded type, changes its base type, interfaces
  or layout, adds or removes virtual methods, or adds constructors is refused with an error that lists the changes.
  Without this check, code reloaded against a different field layout reads and writes the wrong memory.

#### Performance
- Library
  - For assemblies with at most a thousand types, rewriting (on my machine) takes <2 seconds
  - For bigger assemblies (tens of thousands of types), performance might not be acceptable as rewriting takes >10 seconds
- Reloaded code runs about twice as slow as normal code

## Acknowledgments
Thanks to Washi1337 for [AsmResolver](https://github.com/Washi1337/AsmResolver).
