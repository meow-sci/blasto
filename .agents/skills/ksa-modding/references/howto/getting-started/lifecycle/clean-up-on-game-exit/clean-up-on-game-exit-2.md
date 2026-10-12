---
type: How-To
title: Relaunch the game from a mod with a PrepareAll prefix and Process.Start
description: Detect that a startup-only change needs a fresh process, ask the player with a self-pumped popup, then restart StarMap.exe with a marker argument and exit cleanly
category: getting-started
group: lifecycle
topic: clean-up-on-game-exit
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/starmap/features/new-mods-restart-flow, mods/starmap/features/startup-modal-popup]
techniques: [harmony-prefix, starmap-lifecycle]
hooks: [harmony-prefix KSA.ModLibrary.PrepareAll]
lifecycle: [startup:asset-load, event:shutdown]
game_types: [KSA.Program, KSA.ModLibrary, KSA.ModEntry, KSA.Popup]
tags: [restart, relaunch, process-start, popup, startup, new-mods]
---

Some changes only take effect on the next launch: a newly enabled mod's code, a startup-only patch. StarMap itself
needs this. It detects mods the player just enabled in KSA's "MOD FOUND" dialog (their content loads, their code
does not), asks the player, and relaunches `StarMap.exe`. This article extracts that pattern: a Harmony prefix on
`ModLibrary.PrepareAll`, a popup shown from inside the `Program` constructor, and a clean relaunch. For ordinary
shutdown clean-up see [StarMapUnload plus ProcessExit for exit paths that skip it](./clean-up-on-game-exit-1.md).

## Background: how the game does it

- `ModLibrary.PrepareManifest` (run by StarMap before `Main`) appends each unlisted mod folder as
  `new ModEntry(id, count)`, which sets `Enabled = false` and `New = true`.[current/decomp/KSA/ModEntry.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModEntry.cs#L40-L47)
- StarMap skips disabled entries, so no code loads for a new mod.
- In the `Program` constructor KSA shows one `ConfirmModPopup` per new entry via `ConfirmMods`, then
  `Manifest.Save()`, then `ModLibrary.PrepareAll()`. "Enable" sets `Enabled = true`; `New` is not serialized and
  stays set for the session.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L887-L899)[current/decomp/KSA/ConfirmModPopup.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConfirmModPopup.cs#L14-L24)
- No frame loop runs yet, so a popup must be driven by a hand-written frame pump. KSA's own `ConfirmMods.OnFrame`
  is the template: `Glfw.PollEvents`, `ImGuiBackend.NewFrame`, `ImGui.NewFrame`, draw, `ImGui.Render`, acquire and
  record one render pass, submit.[current/decomp/KSA/ConfirmMods.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConfirmMods.cs#L1-L80)

## Lifecycle and timing

`startup:asset-load`: the prefix runs as the first statement of `ModLibrary.PrepareAll`, inside the `Program`
constructor, after the window, Vulkan renderer, ImGui context and loading screen exist (constructor lines 794-855)
and after KSA's own popups, but before any mod's `PrepareSystems` (so before any `StarMapImmediateLoad`). That is
the last point where a restart costs nothing, because no asset has been loaded yet. The pump needs the renderer
(L794), `Program.MainPass` (L825) and ImGui (L842-852), so it works from `StarMapImmediateLoad` or any patch
on constructor code after those lines, but **not** from `StarMapBeforeMain`.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L794-L855) Blocking is
fine here: the constructor is already blocking.

## Steps

1. **Patch `ModLibrary.PrepareAll` with a prefix.** StarMap does it with attributes in a class patched at
   `PatchAll`; any Harmony registration works:

   ```csharp
   // StarMap.Core/Patches/ModLibraryPatches.cs L30-L75
   [HarmonyPatch(nameof(ModLibrary.PrepareAll))]
   [HarmonyPrefix]
   public static void BeforePrepareAll()
   {
       static bool wereNewModsEnabled()
       {
           foreach (var mod in ModLibrary.Manifest.Mods)
           {
               if (mod.New && mod.Enabled)
                   return true;
           }
           return false;
       }

       if (ModLibrary.HasNewMods() && wereNewModsEnabled())
       {
           var confirmRestart = new ConfirmRestart();
           while(confirmRestart.Show)
           {
               confirmRestart.OnFrame();
           }

           if (confirmRestart.Restart)
           {
               Console.WriteLine("StarMap - RESTARTING");
               Console.WriteLine("======================================");

               var mainModule = Process.GetCurrentProcess().MainModule;
               if (mainModule is null || Path.GetDirectoryName(mainModule.FileName) is not string starMapInstallLocation)
               {
                   Console.WriteLine("Unable to get StarMap install location for restart.");
               }
               else {
                   var startInfo = new ProcessStartInfo
                   {
                       FileName = mainModule.FileName,
                       Arguments = "--restarted",
                       WorkingDirectory = starMapInstallLocation,
                   };

                   Process.Start(startInfo);
               }

               Environment.Exit(0);
           }
       }
   ```

2. **Detect the condition** with the game's own data: `ModLibrary.HasNewMods()` (any manifest entry with `New`)
   plus an `Enabled && New` scan, as above. For your own reason to restart (a startup-only setting), use your own
   predicate; the rest is identical.
3. **Show a popup, if you want consent.** Subclass `KSA.Popup`, build the body with the game's console-style
   helpers, and run a `while (Show) OnFrame()` pump. `ConfirmRestartPopup` is a copy of `ConfirmModPopup`:[StarMap.Core/UI/ConfirmRestart/ConfirmRestartPopup.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/UI/ConfirmRestart/ConfirmRestartPopup.cs#L13-L49)

   ```csharp
   // StarMap.Core/UI/ConfirmRestart/ConfirmRestartPopup.cs L13-L25
   private static readonly PopupButton<ConfirmRestartPopup> PopupButtonContinue = CreateButton("Continue", (Action<ConfirmRestartPopup>)(popup =>
   {
       popup.Active = false;
       popup.UI.Restart = false;
       popup.UI.Show = false;
   }));
   private static readonly PopupButton<ConfirmRestartPopup> PopupButtonRestart = CreateButton("Restart", (Action<ConfirmRestartPopup>)(popup =>
   {
       popup.Active = false;
       popup.UI.Restart = true;
       popup.UI.Show = false;
   }));
   ```

   The pump (`ConfirmRestart.OnFrame`, L39-L99 of [StarMap.Core/UI/ConfirmRestart/ConfirmRestart.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/UI/ConfirmRestart/ConfirmRestart.cs)) returns unless `Program.IsMainThread()`,
   exits the process if the window is closed, and otherwise renders one frame. Copy it from the *current*
   decompiled `ConfirmMods`, which also calls `WaitForPreviousPresent` and sets `InputSampleTicks`, both of which
   StarMap's copy omits.
4. **Relaunch.** Use the running executable and its folder, not guessed paths: `Process.GetCurrentProcess().MainModule.FileName`
   as `FileName` and its directory as `WorkingDirectory` (StarMap resolves `./StarMapConfig.json` and
   `./0Harmony.dll` against the working directory, [Host process, contexts and hook binding walkthrough](../../loader/how-starmap-launches-ksa/how-starmap-launches-ksa-1.md)).
5. **Forward the original arguments.** StarMap passes only `--restarted`, so a `-InstancePath` flag is lost and the
   restarted game would read the default Documents folder. Forward them yourself (adapted):

   ```csharp
   // adapted from StarMap.Core/Patches/ModLibraryPatches.cs L30-L75: forward the original arguments
   var startInfo = new ProcessStartInfo
   {
       FileName = mainModule.FileName,
       WorkingDirectory = starMapInstallLocation,
   };
   foreach (var arg in Environment.GetCommandLineArgs().Skip(1).Append("--restarted"))
       startInfo.ArgumentList.Add(arg);
   Process.Start(startInfo);
   ```

   `STARMAP_INSTANCE_PATH` in the environment survives on its own because the child inherits it.
6. **Exit.** `Environment.Exit(0)` ends the old process immediately. `--restarted` is only a marker: KSA ignores
   unknown arguments and StarMap never reads it.

## Complete example

| File | Role |
|---|---|
| [StarMap.Core/Patches/ModLibraryPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModLibraryPatches.cs) | Detection, popup loop, relaunch |
| [StarMap.Core/UI/ConfirmRestart/ConfirmRestart.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/UI/ConfirmRestart/ConfirmRestart.cs) | Frame pump with its own render pass |
| [StarMap.Core/UI/ConfirmRestart/ConfirmRestartPopup.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/UI/ConfirmRestart/ConfirmRestartPopup.cs) | The popup content and buttons |
| [current/decomp/KSA/ConfirmMods.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConfirmMods.cs) | KSA's template for the pump |

## Variations and gotchas

- **`StarMapUnload` does not run through `Environment.Exit`**; a `ProcessExit` handler does
  ([StarMapUnload plus ProcessExit for exit paths that skip it](./clean-up-on-game-exit-1.md)). Release anything the next process needs (locks, ports, child processes) before
  exiting.
- **`dotnet StarMap.dll` launches.** The KSAGolf README tells Linux users to start StarMap as `dotnet StarMap.dll`;
  then `MainModule.FileName` is the `dotnet` host, and relaunching it with only `--restarted` probably does not start
  StarMap (inferred from the code, untested). Include the assembly path as the first argument in that case.
- **Content-only mods trigger it too.** The check ignores whether the new mod has a DLL; "Continue" is correct then.
- **Closing the window in the popup exits via `Environment.Exit(0)`** inside the pump.
- **One pump frame draws before the popup exists** (the constructor calls `OnFrame()` before creating the popup),
  so the first frame shows only the blank background.
- **Do not use this for hot reload.** If a setting can be applied in-process, do that; a relaunch costs a full
  startup.
- **It runs on the main thread inside the constructor**; a worker-thread loop would spin forever because the
  pump returns immediately off the main thread.

## See also

- [StarMapUnload plus ProcessExit for exit paths that skip it](./clean-up-on-game-exit-1.md), [Clean up on game exit and relaunch the game from a mod](./overview.md)
- [Restart when new mods are enabled](../../../../mods/starmap/features/new-mods-restart-flow.md), [Startup modal popup with a self-pumped frame](../../../../mods/starmap/features/startup-modal-popup.md)
- [Folder and manifest rules as StarMap consumes them](../../loader/how-starmap-launches-ksa/how-starmap-launches-ksa-2.md), [Keep mod exceptions out of KSA's frame loop](../contain-exceptions-in-starmap-hooks/overview.md)
