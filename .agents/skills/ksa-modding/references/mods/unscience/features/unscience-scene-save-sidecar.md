---
type: Mod Feature
title: Per-save scene sidecar wiring and the participant inventory
description: The unscience host collects every sub-mod's save records, pairs each native save's capture with its write by object identity, writes unscience.json beside universe.xml and reports save and load results in the toolbox
mod: unscience
submod: unscience
category: persistence
techniques: [save-sidecar, harmony-postfix, harmony-prefix, imgui-window]
hooks:
  - starmap StarMapAllModsLoaded
  - harmony-postfix KSA.GameSave.Populate
  - harmony-postfix KSA.UncompressedSave.Write
  - harmony-prefix KSA.UncompressedSave.Load
  - harmony-prefix KSA.Universe.DeserializeSave
  - harmony-postfix KSA.Universe.DeserializeSave
  - starmap StarMapAfterGui
lifecycle: [startup:all-mods-loaded, event:save, event:load, frame:prepare, frame:draw-ui]
game_types: [KSA.GameSave, KSA.UncompressedSave, KSA.Universe]
tags: [saves, sidecar, json, participants, restore-order, host, status-ui]
---

Unscience keeps the scene setups its sub-mods create (welds, decals, scaled vessels, lights,
mounted cameras, rings, plumes and so on) inside the player's normal KSA saves. There is no second
save browser: when KSA writes a save folder, the mod adds one file, `unscience.json`, next to
`universe.xml`; when KSA loads that folder, the mod clears the old setups and rebuilds the saved ones
after the game has reconstructed its own world.[^unscience-readme]

This concept is the host side of that system: how the supermod discovers 35 records from 30
sub-mods, wires them into the shared [native save hooks](./lib-native-save-hooks.md) and
[coordinator](./lib-scene-save-coordinator.md), pairs a capture with the write it belongs to, and shows
the result. Mechanism in one line: `UnscienceSaves` flattens `ISaveParticipantSource` sub-mods into one
`SceneSaveCoordinator` and assigns its methods to the eight static `NativeSaveHooks` callbacks.[^unscience-saves]

## How it works

**Discovery.** At `[StarMapAllModsLoaded]`, after every `ISubmod` is constructed and initialized, the
host builds `new UnscienceSaves(_submods)` and only then calls `Patcher.Patch()`, which installs the
hooks ([unscience/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Mod.cs#L136-L139)).
A sub-mod takes part by implementing `ISaveParticipantSource` (any number of records) or
`ISaveParticipant` directly; everything else contributes nothing. The coordinator sorts the flattened
list by `RestoreOrder`, then by ordinal `SaveId`, and throws on a duplicate id.[^coordinator][^isave-participant]

**Pairing capture with write.** A native save is one object: `UncompressedSave.Make(name)` constructs
it, calls `Populate()` (which snapshots `UniverseData`) and then `Write()` on the same instance;
`Overwrite` goes through `Make` as well.[^ksa-uncompressedsave][^ksa-gamesave] The `Populate` postfix
raises `Capturing(GameSave)`; the host captures every participant into a `SaveDocument` and stores it
in a `ConditionalWeakTable<GameSave, SaveDocument>` keyed by that object. The `Write` postfix raises
`Written(UncompressedSave)` only when the native write returned `true`; the host looks the document up
by the same object, writes it with `SaveStorage.Write(save.Directory.FullName, ...)` and removes the
entry. A write with no matching capture is reported, never filled with fresh state.

**The file.** `SaveStorage.FileName` is `unscience.json`; the document carries `SchemaVersion`,
`UniverseSha256` (hash of the sibling `universe.xml`), `CreatedUtc`, `Features` (id to
`{ Version, State }`) and `Warnings`. Size limits, the atomic temp-file write and the hash check on read
are described in the [coordinator concept](./lib-scene-save-coordinator.md).[^save-storage][^save-document]

**Load.** `Loading(UncompressedSave)` reads the sidecar from the save directory and hands it to
`PrepareLoad`; a missing or unreadable file becomes `PrepareLoad(null)` plus a status message, which
means "reset every feature, restore nothing". `Resetting` and `LoadFinished` go straight to the
coordinator; `Restoring` runs `RestoreWorld` inside a `SavedPartReference.BeginOperation()` scope (the
same scope wraps capture), so [part identities](./lib-save-part-identity.md) are resolved against one
consistent snapshot of the reconstructed world.

**Participant inventory** (extracted from every `new SaveParticipant<T>(...)` and the one direct
implementation; restore runs top to bottom, reset bottom to top):

| Order | Record id | Sub-mod | Notes |
|---|---|---|---|
| 0 | `parts-now` | Parts Now | Dependency check only: warns when a runtime part mod's templates are missing; reset is a no-op |
| 5 | `skittles` | Skittles | Applied ImGui style |
| 10 | `dont-stifle-me` | Don't Stifle Me | Editor limit switches |
| 20 | `free-fallin`, `kitchen-sink`, `kitchen-sink-capsule-glass`, `kitchen-sink-g-load`, `the-tick` | Free Fallin, Kitchen Sink, The Tick | Global render toggles and protected-vessel lists |
| 30 | `blinky`, `bloomin-onion`, `its-so-shiny`, `pyro.templates` | | Grids rebind to native parts; rings and shared exhaust templates before overlays |
| 40 | `kiwis-marbles`, `rocky-mcrock-face` | | Celestial welds; ring swaps after Bloomin' Onion rings |
| 50 | `godzilla` | Godzilla | Scale snapshots |
| 60 | `doh`, `garrys-torch`, `pebbles` | | Kitten materials, welds, clutter recipes |
| 61 | `pebbles.clutter-state` | Pebbles | Override-spacing clutter grids, after the recipes |
| 80 | `humble-arteest`, `zippo` | | Colors and lights after Doh's materials exist |
| 90 | `iron-man` | Iron Man | Kitten flight modes, restored disarmed |
| 100 (default) | `dent-wizard`, `eternal-flame`, `i-feel-seen`, `kitten-animations`, `pyro`, `thug-life` | | Dent Wizard's record is empty so its reset still runs on every load |
| 150 | `sphinx` | Sphinx | Statics |
| 160 | `graffiti` | Graffiti | Decals after their anchors exist |
| 180 | `hot-pursuit` | Hot Pursuit | Mounted cameras |
| 190 | `kitchen-sink-iva-camera` | Kitchen Sink | IVA free camera pose |
| 200 | `glass` | Glass | Main camera FOV |
| 210 | `camera-controller-override` | Camera Controller Override | Sequences restore stopped |
| 220 | `byo-music` | BYO Music | Sounds restore paused |

Save Yourself is the only bundled sub-mod with no record: its settings are a global preference.[^parts-now-saves][^kitchen-sink-saves][^dent-wizard-submod]

**What is deliberately not in the sidecar.** Global preferences and catalogs live under
`KsaPaths.ModDataDir` (`<Documents>/My Games/Kitten Space Agency/.unscience`): the host's `window.ini` and
`state.toml`, preset files (`garrys-torch-presets.toml`, `pyro-presets.toml`,
`bloomin-onion-rings.toml`), `save-yourself.toml`, and the copied PNG, GLB and sound libraries.[^ksa-paths][^unscience-state]
The authors' design rule is that user preferences and reusable catalogs stay global while applied
scene state belongs to the save; raw GPU, audio and physics handles are never serialized and are
rebuilt on restore.[^plans-inventory]

**Status UI.** `RenderStatus` draws at the top of the toolbox: a red "Scene saving unavailable" line when
`NativeSaveHooks.IsApplied` is false, the coordinator's last status, a warning count, and an expandable
details block with the adapter count, retained-record count, the warning list and the
**Use current setup for future saves** button (`UseCurrentSetup`).

## Lifecycle and timing

| Moment | Lifecycle | What the host does |
|---|---|---|
| `[StarMapAllModsLoaded]` | `startup:all-mods-loaded` | Construct `UnscienceSaves` (callbacks assigned) before `NativeSaveHooks.Apply` runs inside `Patcher.Patch`, so no save can fire with null callbacks once the patches exist |
| Save from the console or the GAME SAVES window | `event:save` (`frame:input` or `frame:draw-ui`) | Capture in the `Populate` postfix, write in the `Write` postfix, synchronously in the caller's frame phase |
| Load | `event:load`, replayed at `frame:prepare` | The hooks defer the native load to the `PrepareFrame` handoff; `Loading`, `Resetting`, `Restoring`, `LoadFinished` then run back to back on the main thread with physics workers joined |
| Toolbox drawn | `frame:draw-ui` | `RenderStatus` from `[StarMapAfterGui]` |
| `[StarMapUnload]` | | `Dispose` nulls all eight callbacks, finishes any open load and clears pending captures before `Patcher.Unload` |

Why capture at `Populate` rather than at `Write`: since KSA 5482 `Write` first calls
`SaveDirectory.TryReplace`, deleting and recreating the folder, and returns `false` on failure.
Capturing right after `UniverseData.Create()` gives feature state from the same instant as the native
snapshot, and publishing only from a successful `Write` means a failed save never pairs new feature
state with an old `universe.xml` that survived.[^ksa-uncompressedsave][^unscience-readme] The deferral and
worker joins on the load path, and why world replacement belongs at the handoff, are covered in
[native save hooks](./lib-native-save-hooks.md) and
[the PrepareFrame handoff](./lib-physics-frame-hook.md).

## Key code

| Path | Role |
|---|---|
| [unscience/UnscienceSaves.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/UnscienceSaves.cs) | Host wiring: participant discovery, callback assignment, capture/write pairing, status UI |
| [unscience/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Mod.cs#L136-L139) | Construction order: saves wiring, then patches |
| [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L67) | `TryApply("scene saves", () => NativeSaveHooks.Apply(...))`; removed first on unload |
| [ksa-abstractions.lib/Persistence/ISaveParticipant.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/Persistence/ISaveParticipant.cs) | `ISaveParticipant`, `ISaveParticipantSource`, `SaveParticipant<T>` adapter |
| [ksa-abstractions.lib/Persistence/SceneSaveCoordinator.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/Persistence/SceneSaveCoordinator.cs#L26-L31) | Ordering and duplicate-id check |
| [ksa-abstractions.lib/Persistence/SaveStorage.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/Persistence/SaveStorage.cs) | `unscience.json` read/write |
| [kitchen-sink.lib/KitchenSinkSubmod.Saves.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink.lib/KitchenSinkSubmod.Saves.cs) | One sub-mod exposing four records |
| [parts-now.lib/PartsNowSubmod.Saves.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/PartsNowSubmod.Saves.cs) | Order-0 dependency record |
| [unscience/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/README.md) | User-facing scene-save behavior and failure messages |
| [plans/saves-state-inventory.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/plans/saves-state-inventory.md) | Authors' per-feature state inventory and restore risks |
| [plans/saves-acceptance.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/plans/saves-acceptance.md) | Implemented coverage and pending in-game checks |

## Snippets

Discovery, callback wiring, and capture keyed by the native save object:

```csharp
// unscience/UnscienceSaves.cs L20-L46
    public UnscienceSaves(IEnumerable<ISubmod> submods)
    {
        var participants = submods.SelectMany(submod => submod is ISaveParticipantSource source
            ? source.SaveParticipants : submod is ISaveParticipant participant ? new[] { participant } : Array.Empty<ISaveParticipant>());
        _coordinator = new(participants);
        NativeSaveHooks.Capturing = Capture;
        NativeSaveHooks.Written = Write;
        NativeSaveHooks.WriteFailed = WriteFailed;
        NativeSaveHooks.Loading = Load;
        NativeSaveHooks.Resetting = _coordinator.ResetWorld;
        NativeSaveHooks.Restoring = Restore;
        NativeSaveHooks.LoadFinished = _coordinator.FinishLoad;
        NativeSaveHooks.LoadFailed = error => _coordinator.MarkFailure($"Load failed: {error.Message}");
    }

    private void Capture(GameSave save)
    {
        _captures.Remove(save);
        using var identities = SavedPartReference.BeginOperation();
        _captures.Add(save, _coordinator.Capture());
    }

    private void Restore()
    {
        using var identities = SavedPartReference.BeginOperation();
        _coordinator.RestoreWorld();
    }
```

Write only for a matching capture; read before the native load:

```csharp
// unscience/UnscienceSaves.cs L48-L78
    private void Write(UncompressedSave save)
    {
        if (!_captures.TryGetValue(save, out var document))
        {
            _coordinator.Report("Native save had no matching state capture; Unscience state was not written.");
            return;
        }
        try
        {
            SaveStorage.Write(save.Directory.FullName, document);
            _coordinator.MarkWritten(save.Id);
        }
        catch (Exception ex) { _coordinator.MarkFailure($"KSA saved '{save.Id}', but Unscience state could not be written: {ex.Message}"); }
        finally { _captures.Remove(save); }
    }

    private void WriteFailed(UncompressedSave save)
    {
        _captures.Remove(save);
        _coordinator.MarkFailure($"KSA could not write save '{save.Id}'; Unscience state was not written.");
    }

    private void Load(UncompressedSave save)
    {
        try { _coordinator.PrepareLoad(SaveStorage.Read(save.Directory.FullName)); }
        catch (Exception ex)
        {
            _coordinator.PrepareLoad(null);
            _coordinator.Report($"Loading native save without Unscience state: {ex.Message}");
        }
    }
```

One sub-mod contributing several records with explicit orders:

```csharp
// kitchen-sink.lib/KitchenSinkSubmod.Saves.cs L9-L23
public sealed partial class KitchenSinkSubmod : ISaveParticipantSource
{
    public IEnumerable<ISaveParticipant> SaveParticipants => new ISaveParticipant[]
    {
        IvaCameraUnlock.SaveParticipant,
        // Preserve the existing boolean record so older saves still restore IVA visibility.
        new SaveParticipant<bool>("kitchen-sink", () => IvaForceRender.Enabled,
            () => IvaForceRender.Enabled = false, (state, _) => IvaForceRender.Enabled = state, order: 20),
        new SaveParticipant<bool>("kitchen-sink-capsule-glass", () => CapsuleGlassExperiment.Enabled,
            () => CapsuleGlassExperiment.SetEnabled(false), (state, context) =>
            {
                context.Require(!state || CapsuleGlassExperiment.IsAvailable,
                    "Capsule glass experiment render patches are unavailable; saved toggle could not be restored.");
                CapsuleGlassExperiment.SetEnabled(state);
            }, order: 20),
```

Why identity pairing works: the game populates and writes the same instance (build 5554):

```csharp
// current/decomp/KSA/UncompressedSave.cs L141-L146
	public static GameSave? Make(string name)
	{
		UncompressedSave uncompressedSave = new UncompressedSave(name);
		uncompressedSave.Populate();
		return uncompressedSave.Write() ? uncompressedSave : null;
	}
```

## Game APIs touched

| Member | Decomp | Used for |
|---|---|---|
| `GameSave.Populate()` | [current/decomp/KSA/GameSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSave.cs#L37-L40) | Capture point (postfix, via `NativeSaveHooks`) |
| `GameSave.Id` | [current/decomp/KSA/GameSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSave.cs#L13) | Save name in status messages |
| `UncompressedSave.Make(string)` | [current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs#L141-L146) | Same instance populated then written |
| `UncompressedSave.Write()` | [current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs#L108-L135) | `bool` result gates the sidecar write |
| `UncompressedSave.Directory` | [current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs#L13) | Folder that receives `unscience.json` |
| `UncompressedSave.Load()` / `Overwrite()` | [current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs#L45-L105) | Load path (read sidecar first); overwrite goes through `Make` |

## Pitfalls and version notes

- The coordinator constructor throws on a duplicate `SaveId`. It runs inside the host's single
  `OnFullyLoaded` `try` block before `Patcher.Patch()`, so a duplicate id would leave the whole supermod
  uninitialized and unpatched, not just the offending feature.
- `SaveParticipants` is enumerated once at construction. A sub-mod cannot add records later.
- Equal `RestoreOrder` values fall back to ordinal id order, not registration order. Use distinct
  orders when one record depends on another (the `pebbles` / `pebbles.clutter-state` pair uses 60/61).
- If `NativeSaveHooks.Apply` fails, native saves still work but carry no sidecar; the toolbox shows
  "Scene saving unavailable".
- The sidecar records only what native saves lack. Restores rebind to vehicles, parts and kittens KSA
  rebuilt itself; nothing is spawned twice. Moving a save to another machine also needs the PNG, GLB and
  sound libraries and any Parts Now mod folders.[^unscience-readme]
- An atomic sidecar write does not make KSA's own overwrite atomic: a failed folder replace can leave
  the old save intact or partly deleted.[^scope-saves]
- In-game acceptance for many records is pending in the authors' checklist; the managed suites exercise
  the coordinator and adapters without the native game.[^plans-acceptance]

## See also

- [Native save and load hooks](./lib-native-save-hooks.md)
- [Scene save coordinator](./lib-scene-save-coordinator.md)
- [Stable vehicle and part identity](./lib-save-part-identity.md)
- [Supermod host](./unscience-supermod-host.md) and [consolidated patcher](./unscience-consolidated-patcher.md)
- [Timed auto saves](./save-yourself-timed-autosave.md) (a programmatic save producer)
- [Managed test harnesses](./unscience-managed-test-harnesses.md)
- [unscience sub-mod](../submods/unscience.md) and [mod overview](../overview.md)
- [Pair each scene capture with its native write by object identity](../../../howto/persistence/saves/save-mod-state-with-the-scene/save-mod-state-with-the-scene-2.md) - how-to article demonstrating this feature

[^unscience-readme]: unscience/README.md
[^unscience-saves]: unscience/UnscienceSaves.cs
[^coordinator]: ksa-abstractions.lib/Persistence/SceneSaveCoordinator.cs
[^isave-participant]: ksa-abstractions.lib/Persistence/ISaveParticipant.cs
[^ksa-uncompressedsave]: KSA.UncompressedSave (decompiled, build 2026.10.10.5554)
[^ksa-gamesave]: KSA.GameSave (decompiled, build 2026.10.10.5554)
[^save-storage]: ksa-abstractions.lib/Persistence/SaveStorage.cs
[^save-document]: ksa-abstractions.lib/Persistence/SaveDocument.cs
[^parts-now-saves]: parts-now.lib/PartsNowSubmod.Saves.cs
[^kitchen-sink-saves]: kitchen-sink.lib/KitchenSinkSubmod.Saves.cs
[^dent-wizard-submod]: dent-wizard.lib/DentWizardSubmod.cs
[^ksa-paths]: ksa-abstractions.lib/KsaPaths.cs
[^unscience-state]: unscience/UnscienceState.cs
[^plans-inventory]: plans/saves-state-inventory.md
[^scope-saves]: scope/saves.md
[^plans-acceptance]: plans/saves-acceptance.md
