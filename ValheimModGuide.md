# Hot reload for Valheim mods

This guide sets up a BepInEx 5 Valheim mod so that code changes load into a running game or dedicated server.
After each edit you rebuild and copy one file; the game picks up the new code within seconds. You do not restart the
game, reload the world or reconnect to a server.

The mod needs no reload code. Hot reload is an opt-in build of the mod plus a separate development plugin, the
**host plugin**, installed next to it. Normal builds of the mod are not affected.

## How it works

- **At build time,** the compiler plugin rewrites every method of the mod so that it calls through a table of method
  pointers. It also writes the unchanged build next to the output as `<Mod>.dll_orig`.
- **When the game loads the mod,** the mod starts watching for a newer `<Mod>.dll_orig` in its own plugin folder.
- **When a newer file is copied in,** ECR compiles the changed methods into a new assembly, loads it, and points the
  table at the new code. The host plugin runs this switch on Unity's main thread, then re-applies Harmony patches that
  were added or changed.

The mod's assembly keeps its name and its types. Objects that already exist keep working, and asset bundles whose
prefabs use the mod's `MonoBehaviour` classes still load.

## What can and cannot be reloaded

**These edits reload:**
- Method bodies, including Harmony patch methods, `Update` methods of live components, lambdas and iterators.
- New methods, new classes and new static fields.
- New Harmony patch methods that use `[HarmonyPatch]` attributes, plus deleted ones and ones whose target or patch type
  changed.

**These edits are refused.** The log lists each change, the old code keeps running, and you restart to apply them:
- Adding, removing, reordering or retyping instance fields (including auto-properties, which have a hidden field).
- Changing a base class, interfaces or class layout.
- Adding or removing virtual or override methods.
- Adding constructors.

Once you undo a refused edit, the next build reloads normally.

**Things that need care:**
- **Code that runs once at startup does not run again.** This includes `Awake`, config binding, and registering
  prefabs, items, pieces, RPCs, commands and event handlers. A reload changes what those methods would do next time,
  not what they already did. To re-run setup on each reload, add a parameterless static method named
  `OnEditCompileReload` anywhere in the mod. The host calls it on the main thread after each reload. Otherwise,
  restart for these changes.
- **A method that is already running finishes with its old code.** This applies to a coroutine or iterator in
  progress, and to a loop inside a long call. The next call runs the new code.
- **Harmony patches applied by hand** with `harmony.Patch(...)` are not re-applied. Their patch method bodies still
  reload. Reverse patches are not reloaded.
- **A new patch method can be skipped deliberately.** It is not applied if its class already had patch methods at
  startup that the mod chose not to apply, for example a patch class that is only applied when another mod is present.
- **The hot reload build is slower.** Every call goes through the pointer table, a small cost per call, and stack
  traces through reloaded code have extra frames. Never ship it.

## Requirements

- BepInEx 5 (BepInExPack for Valheim) with its bundled HarmonyX.
- The .NET SDK used to build the mod.
- A mod built as an SDK-style `.csproj` targeting .NET Framework (for example `net48`).

## 1. Build the packages and the host plugin

Clone this repository and build from its root. Give the packages a version of your own, so restore cannot pick up a
different package with the same version from nuget.org:

```sh
dotnet build EditCompileReload.Plugin -c Release -p:Version=1.0.2-local.1
dotnet build EditCompileReload.BepInEx -c Release
mkdir -p nupkgs
cp EditCompileReload.Plugin/bin/Release/EditCompileReload.CompilerPlugin.1.0.2-local.1.nupkg nupkgs/
```

This produces:
- `nupkgs/EditCompileReload.CompilerPlugin.1.0.2-local.1.nupkg`, the build step your mod uses. It has no runtime
  dependency.
- `EditCompileReload.BepInEx/bin/Release/`, the host plugin. This folder is the plugin: the host DLL, the ECR runtime
  and their dependencies.

Rebuild both from the same checkout whenever you update this repository, and bump the package version, so the mod's
build step and the host plugin's runtime always match.

## 2. Add an opt-in build to the mod

Create `HotReload.targets` next to the mod's `.csproj`:

```xml
<Project>
  <!--
    Hot reload development build: dotnet build -p:HotReload=true
    Never ship this build.
  -->
  <PropertyGroup>
    <EcrVersion Condition="'$(EcrVersion)' == ''">1.0.2-local.1</EcrVersion>
    <EcrPackageSource Condition="'$(EcrPackageSource)' == ''">$(HOME)/src/EditCompileReload/nupkgs</EcrPackageSource>
    <RestoreAdditionalProjectSources>$(RestoreAdditionalProjectSources);$(EcrPackageSource)</RestoreAdditionalProjectSources>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="EditCompileReload.CompilerPlugin" Version="$(EcrVersion)" PrivateAssets="all" />
  </ItemGroup>
</Project>
```

Set `EcrPackageSource` to your `nupkgs` folder, or pass `-p:EcrPackageSource=<folder>` when building. Then add one
line to the `.csproj`, inside `<Project>`:

```xml
<Import Project="HotReload.targets" Condition="'$(HotReload)' == 'true'" />
```

A normal `dotnet build` does not import the file, so its output is unchanged. Build the hot reload version with:

```sh
dotnet build -p:HotReload=true
```

The output folder (for example `bin/Debug`) now contains three files for the mod:

| File | What it is |
|---|---|
| `<Mod>.dll` | The rewritten assembly the game loads |
| `<Mod>.pdb` | Debug symbols, rewritten to match `<Mod>.dll` |
| `<Mod>.dll_orig` | The unchanged build; copying it into the plugin folder triggers a reload |

## 3. Install

In the BepInEx profile you test with (the game folder, or a mod manager profile):

1. Copy the whole host plugin folder, `EditCompileReload.BepInEx/bin/Release/`, into `BepInEx/plugins/` as its own
   folder, for example `BepInEx/plugins/EditCompileReload.BepInEx/`.
2. Copy `<Mod>.dll`, `<Mod>.pdb` and `<Mod>.dll_orig` into the mod's plugin folder.
3. Remove or disable every other copy of the mod in the profile, including copies bundled inside other packages.
   BepInEx loads only one plugin per GUID, and it may not be yours.
4. Start the game.

The BepInEx log should show:

```
[Info   :   BepInEx] Loading [EditCompileReload hot reload host 1.0.2]
[Info   :EditCompileReload hot reload host] Registered file watcher
```

`Registered file watcher` means the mod is watching its `.dll_orig` and reloads are ready.

## 4. Edit, build, copy

After each change:

```sh
dotnet build -p:HotReload=true
cp bin/Debug/<Mod>.dll_orig "<profile>/BepInEx/plugins/<Mod folder>/"
```

Copy only `<Mod>.dll_orig`. The loaded `<Mod>.dll` stays in place until the next restart. The copy can write the file
in place or rename a finished file over it; both are detected. The reload starts once the file has not changed for one
second, so a slow copy is not read halfway.

The log shows the result of each reload:

```
[Info   :EditCompileReload hot reload host] Reloaded <Mod> version 2: 1032704 bytes in 3498ms
[Info   :EditCompileReload hot reload host] Harmony: 1 reloaded patch(es) applied, 0 startup patch(es) removed
```

or, for an edit that needs a restart:

```
[Error  :EditCompileReload hot reload host] Reload of <Mod> refused; restart to apply these changes:
  MyMod.MyClass: instance fields changed (added, removed, reordered or retyped). Before: []. After: [count: System.Int32]
```

A typical cycle takes 7–12 seconds from starting the build to the new code running: about 2–3 seconds to build, a few
seconds to copy, and 3–5 seconds to reload.

When you restart the game, copy all three files again. The game always starts from `<Mod>.dll`.

## Remote machines and dedicated servers

The same steps work on a Linux or Windows dedicated server, or on a game client on another machine. Install the host
plugin and the three files there, and copy `<Mod>.dll_orig` to it after each build. Use `rsync` for remote copies: it
sends only the parts of the file that changed, which matters when the mod embeds large asset bundles.

```sh
rsync -a bin/Debug/<Mod>.dll_orig user@server:<profile>/BepInEx/plugins/<Mod folder>/
```

- **A dedicated server reloads on its main thread like a client.** If a reload arrives while the server is still
  loading the world, it waits until loading finishes.
- **Reload each side your change affects.** If a change affects both client and server code, reload both. Keep
  network messages compatible between the versions that are running.
- **Clients stay connected while either side reloads.**

## Servers with mod verification or anti-cheat

Some server mods check the plugins and files of joining clients. With a hot reload build:

- `<Mod>.dll` has a different file hash from the release build, although its version is the same. A server that
  checks hashes needs the hot reload build's hash allowed.
- The host plugin is an extra plugin. A server with an allowlist needs it listed, or installed on the server too.
- Each reload loads an assembly from memory. Scanners that look for code loaded from outside the plugin folders may
  report or kick a client after a reload. Use a test server where this is logged only, or turned off.

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| No `Registered file watcher` line | The mod in the plugin folder is not the hot reload build, another copy of the mod loaded instead, or the host plugin is missing. Check which file BepInEx loaded for the mod's GUID. |
| Copying the file does nothing | The file was copied into a different folder from the one the mod was loaded from, or under a different name. The watched file is `<Mod>.dll_orig` next to the loaded `<Mod>.dll`. |
| `Reload of <Mod> refused` | The edit needs a restart (see the list above). Undo it to keep reloading, or restart and copy all three files. |
| Startup code changes have no effect | Startup code does not run again. Use `OnEditCompileReload`, or restart. |
| A new Harmony patch did not apply | It was applied by hand with `harmony.Patch`, or its class had patches at startup that the mod chose not to apply. The `Harmony:` log line counts the patches applied. |
| `Could not load file or assembly 'EditCompileReload'` | The host plugin folder is missing or incomplete. Copy the whole folder, not only the host DLL. |
| The mod fails to load after an update of this repository | The mod was built with a different version of the compiler plugin from the host plugin's runtime. Rebuild both from the same checkout and bump the package version. |

## Turning it off

Build without `-p:HotReload=true`, install that build in place of the three files, and remove the host plugin folder.
Never ship a hot reload build or the host plugin.
