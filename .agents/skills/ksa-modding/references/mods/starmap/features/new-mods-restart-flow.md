---
type: Mod Feature
title: Restart when new mods are enabled
description: A Harmony prefix on ModLibrary.PrepareAll detects mods the player just enabled in KSA's MOD FOUND popup, which StarMap skipped at launch, and offers to relaunch StarMap.exe so their code loads
mod: starmap
category: getting-started
techniques: [harmony-prefix, starmap-lifecycle]
hooks: [harmony-prefix KSA.ModLibrary.PrepareAll]
lifecycle: [startup:asset-load]
game_types: [KSA.ModLibrary, KSA.ModEntry, KSA.ConfirmMods, KSA.ConfirmModPopup, KSA.Program]
tags: [starmap, manifest, restart, new-mods, process, harmony]
---

StarMap decides which mods have code before the game's window even exists, from the manifest as
it is on disk. KSA, however, asks the player about newly found mods later, inside its `Program`
constructor. A mod enabled in that dialog gets its content loaded by KSA but has no StarMap entry
assembly loaded. StarMap closes that gap with a prefix on `ModLibrary.PrepareAll`: if any newly
found mod was just enabled, it shows a "Game requires restart" popup and, on Restart, launches a
fresh `StarMap.exe` and exits.

## How it works

1. **First discovery.** `ModLibrary.PrepareManifest` (run by StarMap before `Main`) appends every
   new mod folder as `new ModEntry(id, count)`, which sets `Enabled = false` and `New = true`.[^ksa-modentry][^ksa-modlibrary]
2. **StarMap skips it.** `ModLoader.PrepareMods` logs "Not loading mod: X because it is disabled in
   manifest" and loads no assembly for it.[^sm-modloader]
3. **KSA asks the player.** In the `Program` constructor, `ModLibrary.HasNewMods()` is true, so
   KSA shows one `ConfirmModPopup` per new entry ("A new mod with id 'X' has been found, do you
   want it enabled?"); "Enable" sets `Enabled = true`. Then `ModLibrary.Manifest.Save()` writes the
   choice. `Save` updates `State` and `Index` but leaves `New` set.[^ksa-program][^ksa-confirmmodpopup][^ksa-modmanifest]
4. **StarMap checks.** `ModLibraryPatches.BeforePrepareAll` runs as the first thing in
   `ModLibrary.PrepareAll`. If `HasNewMods()` and any entry is both `New` and `Enabled`, it creates
   a `ConfirmRestart` and pumps its frames until the popup closes.[^sm-modlibrarypatches]
5. **Continue** closes the popup and the game keeps loading: the new mod's content loads, its code
   does not.
6. **Restart** starts `Process.GetCurrentProcess().MainModule.FileName` (the running `StarMap.exe`)
   with the single argument `--restarted` and `WorkingDirectory` set to that executable's folder,
   then calls `Environment.Exit(0)`. The new process reads the saved manifest, where the mod is now
   enabled and no longer new, and loads its assembly.

The popup itself is described in [startup modal popup](startup-modal-popup.md).

## Lifecycle and timing

`startup:asset-load`, inside the `Program` constructor, after the window, Vulkan renderer, ImGui
context and loading screen exist and after KSA's own "MOD FOUND" popups, but before any mod's
`PrepareSystems` (so before any `StarMapImmediateLoad`). That is the last point at which a restart
costs nothing: no asset has been loaded yet. It runs on the main thread, blocking the constructor
while the popup is up.

## Key code

| Path | Role |
|---|---|
| [StarMap.Core/Patches/ModLibraryPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModLibraryPatches.cs#L30-L76) | Detection, popup loop, relaunch |
| [StarMap.Core/UI/ConfirmRestart/ConfirmRestart.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/UI/ConfirmRestart/ConfirmRestart.cs#L11-L99) | Popup host with its own frame pump |
| [StarMap.Core/UI/ConfirmRestart/ConfirmRestartPopup.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/UI/ConfirmRestart/ConfirmRestartPopup.cs#L7-L50) | "Continue" / "Restart" buttons |

## Snippets

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

## Game APIs touched

| Member | File | Use |
|---|---|---|
| `ModLibrary.PrepareAll` | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L373-L447) | Prefix target |
| `ModLibrary.HasNewMods` | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L1911-L1925) | Any manifest entry with `New` |
| `ModEntry(string id, int modsCount)` | [current/decomp/KSA/ModEntry.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModEntry.cs#L40-L47) | New entries start disabled |
| `ConfirmModPopup` | [current/decomp/KSA/ConfirmModPopup.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConfirmModPopup.cs#L14-L24) | KSA's Enable / No buttons set `Enabled` |
| `Program..ctor` new-mods block | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L887-L899) | `ConfirmMods` loop, `Manifest.Save`, then `PrepareAll` |
| `ModManifest.Save` | [current/decomp/KSA/ModManifest.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModManifest.cs#L27-L54) | Writes `manifest.toml`; `New` is not serialized |

## Pitfalls and version notes

- **The restart drops command-line options.** Only `--restarted` is passed (KSA ignores it). An
  `-InstancePath <dir>` given to the first `StarMap.exe` is lost, so the restarted game would read
  the default Documents folder; the `STARMAP_INSTANCE_PATH` environment variable survives because
  the child inherits the environment.[^sm-documentspath] `GameArguments` from
  `StarMapConfig.json` are re-read and still apply.
- **`--restarted` is not read by StarMap.** It is a marker only; no code in this checkout checks it.
- **Unload does not run** on either exit path (`Environment.Exit`).
- **Content-only mods trigger it too.** The check does not look at whether the new mod has a DLL,
  so enabling a pure content mod also shows the popup; "Continue" is correct then.
- History: added in StarMap PR #78 ("Feature/confirm reload"); the popup was adjusted in PR #86 for
  the current KSA reference.

## See also

- [Startup modal popup with a self-pumped frame](startup-modal-popup.md)
- [Discover code mods through KSA's mod manifest](manifest-mod-discovery.md)
- [Redirect the KSA documents folder per instance](instance-path-override.md)
- [Relaunch the game from a mod with a PrepareAll prefix and Process.Start](../../../howto/getting-started/lifecycle/clean-up-on-game-exit/clean-up-on-game-exit-2.md) - how-to article demonstrating this feature
- [Understand the mod folder, manifest and enable/ordering rules](../../../howto/getting-started/loader/how-starmap-launches-ksa/how-starmap-launches-ksa-2.md) - how-to article demonstrating this feature

[^sm-modlibrarypatches]: StarMap.Core/Patches/ModLibraryPatches.cs
[^sm-modloader]: StarMap.Core/ModRepository/ModLoader.cs
[^sm-documentspath]: StarMap.Core/Patches/DocumentsPathPatches.cs
[^ksa-program]: KSA.Program (decompiled)
[^ksa-modlibrary]: KSA.ModLibrary (decompiled)
[^ksa-modentry]: KSA.ModEntry (decompiled)
[^ksa-confirmmodpopup]: KSA.ConfirmModPopup (decompiled)
[^ksa-modmanifest]: KSA.ModManifest (decompiled)
