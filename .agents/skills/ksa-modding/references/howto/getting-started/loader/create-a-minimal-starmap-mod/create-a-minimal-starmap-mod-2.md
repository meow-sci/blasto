---
type: How-To
title: Scaffold a mod with mkmod.ts and grow it into a library hosted by a supermod
description: Clone unscience's fixme-mod-name template pair with one script, then keep feature code in a host-agnostic ISubmod library that runs standalone or inside an aggregating mod
category: getting-started
group: loader
topic: create-a-minimal-starmap-mod
approach: 2
difficulty: intermediate
prerequisites: [starmap, harmony, imgui]
demonstrated_by:
  - mods/unscience/features/fixme-mod-name-standalone-mod-template
  - mods/unscience/features/lib-isubmod-contract
  - mods/unscience/features/unscience-supermod-host
techniques: [starmap-lifecycle, harmony-prefix]
hooks:
  - starmap StarMapMod
  - starmap StarMapAllModsLoaded
  - starmap StarMapBeforeGui
  - starmap StarMapAfterGui
  - starmap StarMapUnload
lifecycle: [startup:all-mods-loaded, frame:draw-ui, event:shutdown]
game_types: [KSA.Mod, KSA.ModLibrary, KSA.Program]
tags: [starmap, scaffold, template, library, submod, supermod, imgui]
---

unscience is a monorepo of about 30 features shipped as one mod. Its authors made adding a new
feature a one-command job: a script clones a **template pair** (an entry project plus a sibling
library), and the finished feature library can later be hosted by a single aggregating "supermod".
This article walks that path. The result is a mod you can run on its own or fold into a host,
with all feature code in a game-light library. The bare-bones alternative is
[Hand-built project from the discovery rules](./create-a-minimal-starmap-mod-1.md). The repo is [unscience](https://github.com/meow-sci/unscience);
the scaffold is the `fixme-mod-name` / `fixme-mod-name.lib` pair.[^fixme-mod]

## Background: how the game does it

KSA and StarMap do not care how your solution is laid out; they need a folder with a `mod.toml`
and an entry DLL carrying one `[StarMapMod]` class (see [Hand-built project from the discovery rules](./create-a-minimal-starmap-mod-1.md)). Layout
is a convention of the mod author. unscience's convention is two projects per feature:

| Project | Role |
|---|---|
| `<name>/` | Thin StarMap entry mod: `Mod.cs` with lifecycle attributes, `Patcher.cs` owning a Harmony instance, `mod.toml` |
| `<name>.lib/` | The feature itself: logic, patch classes, and an `ISubmod` panel; no StarMap attributes |
| `ksa-abstractions.lib/` | The shared `ISubmod` contract (`SubmodUI` helper included) |

Only one host is ever installed. The standalone hosts compile but are never packaged;
`Directory.Build.props` marks every project except `unscience` non-publishable.[Directory.Build.props](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/Directory.Build.props)

## Lifecycle and timing

The scaffold maps the standard StarMap hooks one-to-one; none of them fires before
`startup:all-mods-loaded` in the template (full semantics in [StarMap lifecycle attributes](../../lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-1.md)):

| Attribute | Template action | When |
|---|---|---|
| `[StarMapImmediateLoad]` | nothing (zero-argument, so StarMap never binds it) | never dispatched |
| `[StarMapAllModsLoaded]` | `Patcher.Patch()` | postfix on `ModLibrary.LoadAll`, assets loaded, no scene yet |
| `[StarMapBeforeGui]` | empty; per-frame non-UI work belongs here | prefix on `Program.OnDrawUiFrame`, skipped when the HUD is hidden |
| `[StarMapAfterGui]` | F11 toggle and the ImGui window | postfix on `Program.OnDrawUiViewports`, inside the ImGui frame |
| `[StarMapUnload]` | `Patcher.Unload()` | after the game exited; StarMap already unpatched everything |

In the supermod the same calls are fanned out: `Initialize()` on every sub-mod at `AllModsLoaded`,
`Update(dt)` at `BeforeGui`, `RenderContent()` at `AfterGui`, `Dispose()` at `Unload`.[^unscience-mod]

## Steps

1. **Get the template.** Clone unscience. The script `mkmod.ts` copies `fixme-mod-name` and
   `fixme-mod-name.lib` (skipping `bin` and `obj`), replacing the placeholders `fixme-mod-name`
   and `FixmeModName` in directory names, file names and file contents:[mkmod.ts](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/mkmod.ts)

   ```bash
   # usage text from unscience/mkmod.ts
   bun run mkmod.ts orbital-cam OrbitalCam
   ```

   The script uses `Bun.file` / `Bun.write`, so it needs Bun, not plain Node. It validates that the
   first argument is kebab-case and the second PascalCase, and refuses to overwrite an existing
   directory.

2. **Read what you got.** `orbital-cam/mod.toml` names the entry assembly. Keep the `EntryAssembly`
   value and the csproj `AssemblyName` identical:[fixme-mod-name/mod.toml](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/fixme-mod-name/mod.toml)

   ```toml
   # fixme-mod-name/mod.toml L1-L7
   name = "fixme-mod-name"
   description = "fixme-mod-name"
   version = "0.1.0"
   author = "meow sci"

   [StarMap]
   EntryAssembly = "MeowSci.FixmeModName"
   ```

   The folder you deploy is the manifest id (for example `orbital-cam`), while the DLL is named
   `MeowSci.OrbitalCam.dll`. Because the assembly name differs from the folder name, the `[StarMap]`
   table is required here; without it StarMap would look for `orbital-cam.dll`.

3. **Understand the project file.** NuGet gives `StarMap.API` 0.3.6 and `Lib.Harmony` 2.4.2 with
   `PrivateAssets="all"`; game DLLs (`KSA`, `Brutal.Core.*`, `Brutal.ImGui*`) are references with
   `Private=false` resolved through `$(KSAFolder)`, which `Directory.Build.props` sets from the
   `KSA_DLL_DIR` environment variable, a sibling `ksa-game-assemblies` checkout or per-OS defaults;
   `mod.toml` is copied to output.[fixme-mod-name/fixme-mod-name.csproj](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/fixme-mod-name/fixme-mod-name.csproj) A missing `KSA.dll` produces one readable error
   instead of hundreds of missing-type errors. This part is explained in
   [Reference the game's assemblies from a mod project without shipping them](../../build/reference-game-assemblies-from-a-mod-project/overview.md).

4. **The entry class.** The generated `Mod.cs` has every lifecycle attribute wired. The pieces you
   edit:[fixme-mod-name/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/fixme-mod-name/Mod.cs)

   ```csharp
   // fixme-mod-name/Mod.cs L9-L56
   [StarMapMod]
   public class Mod
   {
     public bool ImmediateUnload => false;
   // ...
     [StarMapImmediateLoad]
     public void OnImmediateLoad() { }

     [StarMapAllModsLoaded]
     public void OnFullyLoaded()
     {
       try
       {
         Patcher.Patch();
         _isInitialized = true;
       }
   // ...
     [StarMapBeforeGui]
     public void OnBeforeUi(double dt) { }

     [StarMapAfterGui]
     public void OnAfterUi(double dt)
     {
       try
       {
         if (!_isInitialized || _isDisposed) return;

         if (ImGui.IsKeyPressed(ImGuiKey.F11))
           _windowVisible = !_windowVisible;

         if (_windowVisible)
           RenderWindow();
       }
   // ...
   ```

   Two traps are inherited from the template. The zero-argument `OnImmediateLoad()` is never
   called, because StarMap binds `StarMapImmediateLoad` only for a method taking one `KSA.Mod`;
   declare `OnImmediateLoad(KSA.Mod mod)` if you need that moment.[StarMap.API/BaseAttributes.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.API/BaseAttributes.cs) The
   `ImmediateUnload` property is never read by StarMap at the pinned commit.

5. **Patch ownership.** `Patcher.cs` owns one Harmony instance, applies the assembly's attribute
   patches plus the shared hotkey guard, and reverses them on unload:[fixme-mod-name/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/fixme-mod-name/Patcher.cs)

   ```csharp
   // fixme-mod-name/Patcher.cs L9-L39
   [HarmonyPatch]
   internal static class Patcher
   {
       private static Harmony? _harmony = new Harmony("fixme-mod-name");

       public static void Patch()
       {
           try
           {
               _harmony?.PatchAll(typeof(Patcher).Assembly);
               if (_harmony != null) HotkeyGuard.Patch(_harmony);
           }
           catch (Exception ex)
           {
               Console.WriteLine($"fixme-mod-name: Error applying patches: {ex.Message}");
           }
       }

       public static void Unload()
       {
           try
           {
               if (_harmony != null) HotkeyGuard.Unpatch(_harmony);
               _harmony?.UnpatchAll("fixme-mod-name");
               _harmony = null;
           }
           catch (Exception ex)
           {
               Console.WriteLine($"fixme-mod-name: Error removing patches: {ex.Message}");
           }
       }
   ```

   `HotkeyGuard` lives in `ksa-abstractions.lib/HotkeyGuard.cs` (the repo rule is that every host applies it). Outside
   that repo you would delete those two lines. At game exit `Unload` finds nothing to remove because
   `StarMapCore.DeInit` already ran `UnpatchAll()` without an id.[StarMap.Core/StarMapCore.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/StarMapCore.cs)

6. **Put feature code in the library.** The sibling `orbital-cam.lib` starts as one static example
   class. Everything that is not StarMap glue goes here, so a second host can reuse it:[fixme-mod-name.lib/FixmeModNameLib.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/fixme-mod-name.lib/FixmeModNameLib.cs)

   ```csharp
   // fixme-mod-name.lib/FixmeModNameLib.cs L8-L13
   public sealed class FixmeModNameLib
   {
     public static void Thing()
     {
       Console.WriteLine("Hello from the example library!");
     }
   }
   ```

7. **Define the host contract (when you have more than one feature).** unscience's contract is the
   `ISubmod` interface: a name, one-time `Initialize`, per-frame `Update(double dt)`,
   `RenderContent()` that draws without `ImGui.Begin`/`End`, an optional `RenderFloatingWindows()`
   and `Dispose()`.[ksa-abstractions.lib/ISubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/ISubmod.cs)

   ```csharp
   // ksa-abstractions.lib/ISubmod.cs L7-L37 (comments elided)
   public interface ISubmod
   {
       string Name { get; }
       string Tooltip { get; }
       void Initialize();
       void Update(double dt);
       void RenderContent();
       void RenderFloatingWindows() { }
       void Dispose();
   }
   ```

   Game integration stays out of the interface. Each library ships a static
   `<Feature>Patches.Apply(Harmony)` / `Remove(Harmony)` pair that takes the *caller's* Harmony
   instance, so any host can run it under its own id.

8. **Host it standalone.** The standalone host is a few lines around the contract. This is the Glass
   feature's `Mod.cs`, the pattern to copy for `orbital-cam`:[glass/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/glass/Mod.cs)

   ```csharp
   // glass/Mod.cs L22-L58
       [StarMapAllModsLoaded]
       public void OnFullyLoaded()
       {
           try
           {
               _submod = new GlassSubmod();
               Patcher.Patch();
               _submod.Initialize();
               _isInitialized = true;
           }
           catch (Exception ex)
           {
               Console.WriteLine($"glass: Error during initialization: {ex.Message}");
           }
       }

       [StarMapBeforeGui]
       public void OnBeforeUi(double dt)
       {
           if (!_isInitialized || _isDisposed) return;
           _submod.Update(dt);
       }

       [StarMapAfterGui]
       public void OnAfterUi(double dt)
       {
           try
           {
               if (!_isInitialized || _isDisposed) return;
               if (ImGui.IsKeyPressed(ImGuiKey.F9)) _windowVisible = !_windowVisible;
               if (_windowVisible) RenderWindow();
           }
           catch (Exception ex)
           {
               Console.WriteLine($"glass: Error in OnAfterUi: {ex.Message}");
           }
       }
   ```

9. **Host it in a supermod.** The shipped `unscience/Mod.cs` constructs every sub-mod at
   `AllModsLoaded`, initializes them, restores UI state, hands cross-feature dependencies to a
   consolidated patcher, then patches once:[unscience/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Mod.cs#L65-L148)

   ```csharp
   // unscience/Mod.cs L157-L167 (per frame)
       private void UpdateSubmods(double dt)
       {
           if (!_isInitialized || _isDisposed) return;

           // Update ALL submods every frame, even hidden ones
           foreach (var submod in _submods)
           {
               try { submod.Update(dt); }
               catch (Exception ex) { Console.WriteLine($"unscience/{submod.Name}: Update error: {ex.Message}"); }
           }
       }
   ```

   `RenderContent` runs inside one "Unscience Toolbox" window with a collapsing header per sub-mod; the
   helper `SubmodUI.BeginContentArea` / `EndContentArea` wraps each panel in an auto-height child so all
   panels look alike.[ksa-abstractions.lib/SubmodUI.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/SubmodUI.cs) Only the supermod is deployed.

## Complete example

| Repo path | Role |
|---|---|
| [mkmod.ts](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/mkmod.ts) | Scaffold script (Bun) |
| [fixme-mod-name/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/fixme-mod-name/Mod.cs), [fixme-mod-name/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/fixme-mod-name/Patcher.cs), [fixme-mod-name/mod.toml](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/fixme-mod-name/mod.toml), [fixme-mod-name/fixme-mod-name.csproj](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/fixme-mod-name/fixme-mod-name.csproj) | Entry project of the template |
| [fixme-mod-name.lib/FixmeModNameLib.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/fixme-mod-name.lib/FixmeModNameLib.cs) | Template library |
| [ksa-abstractions.lib/ISubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/ISubmod.cs), [ksa-abstractions.lib/SubmodUI.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/SubmodUI.cs) | Host-agnostic contract |
| [glass.lib/GlassSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/glass.lib/GlassSubmod.cs), [glass/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/glass/Mod.cs) | A real small feature as library plus standalone host |
| [unscience/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Mod.cs) | The aggregating host |

## Variations and gotchas

- **Update runs for hidden sections too**, so keep `Update` cheap; and a standalone host does not
  replay `Update` while the HUD is hidden (`BeforeGui` is skipped), only the supermod registers a
  hidden-HUD replay.
- **`RenderContent` must not call `ImGui.Begin`/`End`** for its main area; the host already opened the
  window.
- **One failing feature must not take the rest down.** The supermod isolates each submod's `Update` and
  `RenderContent` in its own `try`/`catch`; copy that if you write a host. Wider containment advice is in
  [Keep mod exceptions out of KSA's frame loop](../../lifecycle/contain-exceptions-in-starmap-hooks/overview.md).
- **Hotkeys collide.** F11 is shared across hosts; two hosts installed together toggle together.
- **Template README is stale.** It shows an old base-class style; the code uses plain attributes.
- **Dispose at game exit can only do managed cleanup**; the renderer and audio are already gone, see
  [Clean up on game exit and relaunch the game from a mod](../../lifecycle/clean-up-on-game-exit/overview.md).
- Mods that do not need a library split should stay with [Hand-built project from the discovery rules](./create-a-minimal-starmap-mod-1.md).

## See also

- [Hand-built project from the discovery rules](./create-a-minimal-starmap-mod-1.md), [Create a minimal StarMap mod](./overview.md)
- [Minimal standalone StarMap mod template with a separate feature library](../../../../mods/unscience/features/fixme-mod-name-standalone-mod-template.md), [ISubmod contract for features that run standalone or inside a supermod](../../../../mods/unscience/features/lib-isubmod-contract.md),
  [Supermod host that aggregates 31 feature panels into one window](../../../../mods/unscience/features/unscience-supermod-host.md)
- [Game loop and mod lifecycle](../../lifecycle/game-loop-and-lifecycle/overview.md), [Choose and use the right StarMap lifecycle hook](../../lifecycle/choose-a-starmap-startup-hook/overview.md), the how-to topic `isolate-patch-failures-and-fault-proof-patch-bodies`

[^fixme-mod]: unscience: fixme-mod-name/Mod.cs
[^unscience-mod]: unscience: unscience/Mod.cs
