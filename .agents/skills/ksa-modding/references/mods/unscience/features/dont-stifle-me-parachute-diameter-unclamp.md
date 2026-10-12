---
type: Mod Feature
title: Parachute diameter unclamp ("jpl said no clamps")
description: Don't Stifle Me widens each parachute's runtime Tuning.MinDiameterM and MaxDiameterM to 2-1000 m from prefixes on VehicleEditor.DrawParachuteSection and Parachute.SetDiameter, tracking every touched instance so disabling or unloading restores the authored bounds
mod: unscience
submod: dont-stifle-me
category: vehicles-and-parts
techniques: [harmony-prefix, reflection, save-sidecar]
hooks: [harmony-prefix KSA.VehicleEditor.DrawParachuteSection, harmony-prefix KSA.Parachute.SetDiameter]
lifecycle: [editor:draw-ui, frame:draw-ui, event:save, event:load, event:unload]
game_types: [KSA.VehicleEditor, KSA.Parachute, KSA.ChuteTuning, KSA.Part, KSA.ChuteTuningWindow, KSA.Program]
tags: [vehicle-editor, parachute, limits, symmetry, harmony, runtime-tuning, restore]
---

The stock vehicle editor lets you size a parachute only inside the range its part author wrote into
the template: `MinDiameterM="20" MaxDiameterM="50"` for the stock main chutes and `1`-`15` m for the
drogues.[^ksa-core-utility-xml] Don't Stifle Me's **jpl said no clamps** switch (off by default) opens
that range to **2-1000 m** for every chute the editor touches, including symmetry counterparts and
multi-canopy parts, and puts each chute's own authored bounds back the moment the switch goes off or
the mod unloads. The switch is meant as the first of several "configurable editor value" expansions;
parachute diameter is the only one implemented.[^dsm-readme][^dsm-limit-settings]

Mechanism in one line: two prefixes overwrite the per-instance `Parachute.Tuning.MinDiameterM` and
`MaxDiameterM` fields (plain runtime data copied from the template) before the slider reads them and
before `SetDiameter` clamps with them, while a dictionary remembers the original pair for each
`Parachute` instance.

## How it works

**Where the clamp lives in the game.** Every `Parachute` module carries a mutable
`ChuteTuning Tuning` struct, filled by `Parachute.TuningFromTemplate` when the part is built
(min/max are ordered there, and `DiameterM` is clamped into them). Two things consume the bounds:

1. `VehicleEditor.DrawParachuteSection` draws the "Diameter" `ImGuiHelper.DrawSliderInt` with
   `Tuning.MinDiameterM` / `MaxDiameterM` as its range, then calls `SetDiameter` on the drawn chute and,
   through `ForEachModuleWithSymmetry`, on the matching chute of every symmetry counterpart.
2. `Parachute.SetDiameter(float)` writes the value to every `Parachute` module of its part
   (`base.Parent.Modules.Get<Parachute>()`), each through its own `Tuning.ClampDiameter`, and refreshes
   inert mass if anything changed.[^ksa-vehicleeditor][^ksa-parachute][^ksa-chutetuning]

So widening the slider alone is not enough: the setter clamps again, and on counterparts whose section
was never drawn.

**The two prefixes**
([dont-stifle-me.lib/EditorValueLimitPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/dont-stifle-me.lib/EditorValueLimitPatches.cs#L25-L107)):

| Target | Patch | While the switch is on | While it is off |
|---|---|---|---|
| `VehicleEditor.DrawParachuteSection(Part, ReadOnlySpan<Parachute>)` (private, by name) | prefix binding only `Part part` | Expands every chute in `part.SubtreeModules` so the slider shows 2-1000 m | Calls `RestoreTrackedBounds()` |
| `Parachute.SetDiameter(float)` (public, typed signature) | prefix binding `__instance` | Expands every chute on `__instance.Parent` before the stock clamp, which catches symmetry counterparts and the other canopies of a multi-canopy part | Does nothing |

The draw prefix intentionally binds only `part`: the second parameter is a byref-like
`ReadOnlySpan<Parachute>`, and the authors avoid giving a Harmony patch a span parameter. It re-reads
the chutes with `part.SubtreeModules.Get<Parachute>()`, the same call the game uses to build that span
at the call site.[^scope-part-editor][^ksa-vehicleeditor]

**Tracking and restore.** `ExpandParachuteBounds` records `(MinDiameterM, MaxDiameterM)` for each
`Parachute` the first time it is touched, in a `Dictionary<Parachute, (float Min, float Max)>` keyed by
module identity, then writes 2 / 1000. `RestoreTrackedBounds` writes every recorded pair back and clears
the dictionary. It runs:

- when the checkbox is turned off in the toolbox or the standalone menu (immediately);
- from the draw prefix on any editor frame while the switch is off (a cheap no-op once empty), which
  also covers a switch flipped by a scene load;
- from `EditorValueLimitPatches.Remove`, before the patches are removed, on unload.

Only the bounds are touched. The chosen `DiameterM`, symmetry propagation, inert-mass refresh, drag
physics, cloth and rendering are all stock and simply consume the larger value.[^scope-part-editor]

**Hosting and UI.** The supermod applies the group in its own `TryApply("dont-stifle-me editor limits")`
([unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L93))
and removes it before the scale patches. The checkbox sits in the toolbox section
([dont-stifle-me.lib/DontStifleMeSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/dont-stifle-me.lib/DontStifleMeSubmod.cs#L30-L35))
and in the standalone menu
([dont-stifle-me.lib/DontStifleMeMenu.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/dont-stifle-me.lib/DontStifleMeMenu.cs#L23-L28));
both call `RestoreTrackedBounds` on the frame it is unticked. A red notice appears when
`EditorValueLimitPatches.IsApplied` is false.

**Scene record.** `DontStifleMeSubmod` exposes one `SaveParticipant<SavedLimits>` with id
`dont-stifle-me` and restore order 10, carrying `Enabled`, `Snap` (from
[the free-scaling feature](./dont-stifle-me-editor-free-scaling.md)) and `ExtendedValues` (this switch).
Reset puts the defaults back (`true`, `true`, `false`) before the native world is replaced, and the
replay copies the saved values into the static settings. The sidecar host that writes it is described in
[the scene save sidecar feature](./unscience-scene-save-sidecar.md).[^dsm-saves]

## Lifecycle and timing

| Moment | Game call chain (build 5554) | Lifecycle | Mod action |
|---|---|---|---|
| Part window draw | `Program.OnDrawUiThreadSafe` -> `Editor?.OnDrawUi(MainViewport)` (`Program.cs` L3239) -> `VehicleEditor.OnDrawUi` (L5747) -> `DrawPartUi` (L5997, body L6161) -> `DrawParachuteSection` (L6690) | `editor:draw-ui` | Draw prefix expands or restores |
| Slider changed, same draw | `DrawParachuteSection` -> `ForEachModuleWithSymmetry` -> `SetDiameter` on each counterpart (L1992-L1998) | `editor:draw-ui` | Setter prefix expands the receiving part's chutes |
| Checkbox toggled | toolbox (`[StarMapAfterGui]`) or standalone menu (`DrawProgramMenusHook`) | `frame:draw-ui` | Immediate restore on untick |
| Scene save / load | sidecar capture / reset and replay through the host | `event:save`, `event:load` | Switch value only |
| Unload | `[StarMapUnload]` -> `Patcher.Unload` -> `EditorValueLimitPatches.Remove` | `event:unload` | Restore, then unpatch |

Everything is main-thread ImGui-phase code. `OnDrawUiThreadSafe` is skipped while the HUD is hidden
(`DrawUI == false`), so with F2 the editor draws no part window and neither prefix runs from the
editor.[^ksa-program] The draw prefix runs before the slider reads the bounds in the same call, so the
widened range is visible on the very first frame the section is drawn.

## Key code

| Path | Role |
|---|---|
| [dont-stifle-me.lib/EditorValueLimitPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/dont-stifle-me.lib/EditorValueLimitPatches.cs) | Both prefixes, the tracking dictionary, expand and restore |
| [dont-stifle-me.lib/EditorLimitSettings.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/dont-stifle-me.lib/EditorLimitSettings.cs) | Static `JplSaidNoClamps` switch |
| [dont-stifle-me.lib/DontStifleMeSubmod.Saves.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/dont-stifle-me.lib/DontStifleMeSubmod.Saves.cs) | `dont-stifle-me` scene record |
| [dont-stifle-me.lib/DontStifleMeSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/dont-stifle-me.lib/DontStifleMeSubmod.cs) | Toolbox checkbox with immediate restore |
| [scope/part-editor-and-robotics.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/part-editor-and-robotics.md) | Authors' target table (rows 8a-8c) |

## Snippets

Restore and the two prefixes:

```csharp
// dont-stifle-me.lib/EditorValueLimitPatches.cs L63-L92
    public static void RestoreTrackedBounds()
    {
        foreach ((Parachute parachute, (float min, float max)) in OriginalParachuteBounds)
        {
            parachute.Tuning.MinDiameterM = min;
            parachute.Tuning.MaxDiameterM = max;
        }
        OriginalParachuteBounds.Clear();
    }

    private static void DrawParachuteSectionPrefix(Part part)
    {
        if (!EditorLimitSettings.JplSaidNoClamps)
        {
            RestoreTrackedBounds();
            return;
        }

        Span<Parachute> parachutes = part.SubtreeModules.Get<Parachute>();
        ExpandParachuteBounds(parachutes);
    }

    private static void SetParachuteDiameterPrefix(Parachute __instance)
    {
        if (!EditorLimitSettings.JplSaidNoClamps) return;

        // SetDiameter updates every chute module in this part. Expand the whole group first so
        // symmetry counterparts and multi-canopy parts do not silently clamp back to stock.
        ExpandParachuteBounds(__instance.Parent.Modules.Get<Parachute>());
    }
```

First-touch tracking:

```csharp
// dont-stifle-me.lib/EditorValueLimitPatches.cs L94-L107
    private static void ExpandParachuteBounds(Span<Parachute> parachutes)
    {
        foreach (Parachute parachute in parachutes)
        {
            if (!OriginalParachuteBounds.ContainsKey(parachute))
            {
                OriginalParachuteBounds.Add(parachute,
                    (parachute.Tuning.MinDiameterM, parachute.Tuning.MaxDiameterM));
            }

            parachute.Tuning.MinDiameterM = ParachuteMinimumDiameterM;
            parachute.Tuning.MaxDiameterM = ParachuteMaximumDiameterM;
        }
    }
```

The stock setter the second prefix gets ahead of (game, build 5554):

```csharp
// current/decomp/KSA/Parachute.cs L395-L410
	public void SetDiameter(float diameterM)
	{
		bool flag = false;
		Span<Parachute> span = base.Parent.Modules.Get<Parachute>();
		for (int i = 0; i < span.Length; i++)
		{
			Parachute parachute = span[i];
			float num = parachute.Tuning.ClampDiameter(diameterM);
			flag |= num != parachute.Tuning.DiameterM;
			parachute.Tuning.DiameterM = num;
		}
		if (flag)
		{
			RefreshInertMass();
		}
	}
```

The scene record shared with the scale toggles:

```csharp
// dont-stifle-me.lib/DontStifleMeSubmod.Saves.cs L6-L16
public sealed partial class DontStifleMeSubmod : ISaveParticipantSource
{
    public sealed record SavedLimits(bool Enabled, bool Snap, bool ExtendedValues);
    public IEnumerable<ISaveParticipant> SaveParticipants => new[]
    {
        new SaveParticipant<SavedLimits>("dont-stifle-me",
            () => new(EditorScaleSettings.Enabled, EditorScaleSettings.Snap, EditorLimitSettings.JplSaidNoClamps),
            () => { EditorScaleSettings.Enabled = true; EditorScaleSettings.Snap = true; EditorLimitSettings.JplSaidNoClamps = false; },
            (state, _) => { EditorScaleSettings.Enabled = state.Enabled; EditorScaleSettings.Snap = state.Snap; EditorLimitSettings.JplSaidNoClamps = state.ExtendedValues; }, order: 10)
    };
}
```

## Game APIs touched

| Member | Decomp | Used for |
|---|---|---|
| `VehicleEditor.DrawParachuteSection(Part, ReadOnlySpan<Parachute>)` (private) | [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L1933-L1998) | Prefix target; the diameter slider reads the tuning bounds at L1992 |
| `VehicleEditor.DrawPartUi` call site | [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L6687-L6691) | Builds the span with `part.SubtreeModules.Get<Parachute>()` |
| `VehicleEditor.ForEachModuleWithSymmetry<TModule>` | [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L1363) | Calls `SetDiameter` on counterparts whose section was not drawn |
| `Parachute.SetDiameter(float)` | [current/decomp/KSA/Parachute.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Parachute.cs#L395-L410) | Prefix target; per-module clamp |
| `Parachute.Tuning`, `Parachute.TuningFromTemplate`, `Parachute.GetAuthoredTuning` | [current/decomp/KSA/Parachute.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Parachute.cs#L635-L660) | Runtime bounds copied from the template |
| `Parachute.GetSaveData` / `ApplySaveData` | [current/decomp/KSA/Parachute.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Parachute.cs#L1335-L1372) | `GetSaveData` stores only `DiameterM`; `ApplySaveData` re-clamps a saved diameter against fresh bounds (see pitfalls) |
| `ChuteTuning.MinDiameterM`, `MaxDiameterM`, `ClampDiameter` | [current/decomp/KSA/ChuteTuning.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ChuteTuning.cs#L33-L64) | The fields written and the clamp they feed |
| `ChuteTuningWindow.DrawPhysicsTuning` | [current/decomp/KSA/ChuteTuningWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ChuteTuningWindow.cs#L233-L237) | The other `SetDiameter` caller (debug window in flight) |
| `Program.OnDrawUiThreadSafe` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3178-L3240) | Where the editor UI is drawn |

## Pitfalls and version notes

- **The new range also narrows drogues.** The fixed 2-1000 m pair replaces whatever was authored, so a
  stock drogue (authored 1-15 m) loses its 1-2 m range while the switch is on.[^ksa-core-utility-xml]
- **Restoring bounds does not re-clamp the diameter.** `RestoreTrackedBounds` writes only min and max; a
  chute set to 800 m keeps `DiameterM = 800` after the switch is turned off until something calls
  `SetDiameter` again. The authors describe this as intentional and non-destructive.[^scope-part-editor]
- **Oversized diameters may not survive a reload (from the decomp, not tested in game).**
  `Parachute.GetSaveData` stores `DiameterM` but not the bounds, and `ApplySaveData` assigns
  `Tuning.ClampDiameter(saveData.DiameterM)` against a tuning freshly built from the template. The mod
  only widens bounds lazily (editor draw or `SetDiameter`), so a vehicle rebuilt from save data comes
  back clamped to the authored range (for example 50 m for a stock main chute) whether or not the switch
  is on.[^ksa-parachute] Launching from the editor is not affected: `VehicleEditor.Dispose` hands the
  live editing part tree to `Vehicle.CreateVehicle`, so the enlarged canopy flies.[^ksa-vehicleeditor]
  Editor operations that rebuild a subtree with `PartTree.Deserialize` (picking up and re-attaching
  parts) may go through the same clamp (unverified).
- **The setter prefix is global.** It fires for every `SetDiameter` call while the switch is on,
  including the in-flight "Parachute Tuning" debug window (`ChuteTuningWindow`), so those chutes are also
  expanded and tracked once that window changes a diameter.[^ksa-chutetuningwindow]
- **Strong references.** The dictionary keeps every touched `Parachute` alive until the next restore,
  including chutes of vehicles that were deleted in the meantime; restoring them is harmless.
- **Name-string target.** `DrawParachuteSection` is resolved by name (a rename fails at `Apply` with a
  logged error and the red UI notice); `SetDiameter` uses a typed `new[] { typeof(float) }` signature.
  The authors record both bodies as unchanged from 5402 through 5482; in 5554 `SetDiameter` is as quoted
  above and `DrawParachuteSection` still feeds the tuning bounds to its slider.[^scope-part-editor]

## See also

- [Editor free scaling](./dont-stifle-me-editor-free-scaling.md) (the other half of the sub-mod)
- [Scene save sidecar](./unscience-scene-save-sidecar.md)
- [Free Fallin canopy material substitution](./free-fallin-canopy-material-substitution.md) (another parachute mod in the monorepo)
- [Don't Stifle Me sub-mod](../submods/dont-stifle-me.md) and [mod overview](../overview.md)
- [Lift the parachute diameter limits in the editor, reversibly](../../../howto/vehicles-and-parts/modules-and-resources/customize-parachute-materials-and-limits/customize-parachute-materials-and-limits-2.md) - how-to article demonstrating this feature

[^ksa-core-utility-xml]: Core/CoreUtilityAGameData.xml (stock parachute templates, build 2026.10.10.5554)
[^dsm-readme]: dont-stifle-me/README.md
[^dsm-limit-settings]: dont-stifle-me.lib/EditorLimitSettings.cs
[^ksa-vehicleeditor]: KSA.VehicleEditor (decompiled, build 2026.10.10.5554)
[^ksa-parachute]: KSA.Parachute (decompiled, build 2026.10.10.5554)
[^ksa-chutetuning]: KSA.ChuteTuning (decompiled, build 2026.10.10.5554)
[^scope-part-editor]: scope/part-editor-and-robotics.md
[^dsm-saves]: dont-stifle-me.lib/DontStifleMeSubmod.Saves.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-chutetuningwindow]: KSA.ChuteTuningWindow (decompiled, build 2026.10.10.5554)
