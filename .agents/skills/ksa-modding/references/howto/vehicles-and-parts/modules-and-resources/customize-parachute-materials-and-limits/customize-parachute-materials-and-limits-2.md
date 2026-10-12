---
type: How-To
title: Lift the parachute diameter limits in the editor, reversibly
description: Overwrite each chute's per-instance min and max diameter in prefixes on the slider draw and on SetDiameter, remember the authored pair, restore it on demand, and plan for the re-clamp on load
category: vehicles-and-parts
group: modules-and-resources
topic: customize-parachute-materials-and-limits
approach: 2
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/dont-stifle-me-parachute-diameter-unclamp]
techniques: [harmony-prefix]
hooks: [harmony-prefix KSA.VehicleEditor.DrawParachuteSection, harmony-prefix KSA.Parachute.SetDiameter]
lifecycle: [editor:draw-ui, frame:draw-ui, event:load, event:save, event:unload]
game_types: [KSA.Parachute, KSA.ChuteTuning, KSA.VehicleEditor, KSA.PartMenu]
tags: [parachute, editor, diameter, clamp, reversible, symmetry]
---
Outcome: in the vehicle editor a chute can be sized from 2 to 1000 m (instead of the authored 20 to 50 m for main chutes or
1 to 15 m for drogues), for every chute the editor touches including symmetry counterparts and multi-canopy parts, and each
chute's own authored bounds come back the moment the switch is turned off or the mod unloads. unscience's Don't Stifle Me
calls the switch "jpl said no clamps".

Prerequisites: StarMap, Harmony. Demonstrated by [unscience/dont-stifle-me-parachute-diameter-unclamp](../../../../mods/unscience/features/dont-stifle-me-parachute-diameter-unclamp.md). The editor's
scale limits are the sibling topic [Scale a vessel or its parts at runtime and lift the editor scale limits](../../vehicles/scale-a-vessel-or-its-parts-at-runtime/overview.md) approach 4.

## Background: how the game does it

See the [overview](overview.md). Two consumers of the bounds:

1. `VehicleEditor.DrawParachuteSection` draws the "Diameter" `DrawSliderInt` with `Tuning.MinDiameterM` /
   `MaxDiameterM` as its range, then calls `SetDiameter` on the drawn chute and, through `ForEachModuleWithSymmetry`, on
   every counterpart [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L1933-L2062)[^ksa-vehicleeditor].
2. `Parachute.SetDiameter(float)` writes the value to every `Parachute` module of its part, each through its own
   `Tuning.ClampDiameter` [current/decomp/KSA/Parachute.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Parachute.cs#L395-L410)[^ksa-parachute].

Widening the slider alone is not enough: the setter clamps again, and on counterparts whose section was never drawn. The bounds
are plain runtime data, built per module from the template (`TuningFromTemplate`) [current/decomp/KSA/Parachute.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Parachute.cs#L635-L648).

## Lifecycle and timing

All main-thread, ImGui-phase code. The draw prefix runs before the slider reads the bounds in the same call, so the widened
range is visible on the first frame the section is drawn (`editor:draw-ui`). `OnDrawUiThreadSafe` is skipped while the HUD is
hidden, so with F2 the editor draws no part window and neither prefix runs. The checkbox itself is a `frame:draw-ui` toggle.
See [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-2.md).

## Steps

1. **Resolve both targets and fail loudly.** `DrawParachuteSection` is private and found by name; `SetDiameter` by a typed
   signature:

```csharp
// dont-stifle-me.lib/EditorValueLimitPatches.cs L25-41
    public static void Apply(Harmony harmony)
    {
        if (IsApplied) return;

        _drawParachuteSection = AccessTools.Method(typeof(VehicleEditor), DrawParachuteSectionName)
            ?? throw new MissingMethodException(nameof(VehicleEditor), DrawParachuteSectionName);
        _setParachuteDiameter = AccessTools.Method(
            typeof(Parachute), nameof(Parachute.SetDiameter), new[] { typeof(float) })
            ?? throw new MissingMethodException(nameof(Parachute), nameof(Parachute.SetDiameter));
        harmony.Patch(_drawParachuteSection,
            prefix: new HarmonyMethod(typeof(EditorValueLimitPatches), nameof(DrawParachuteSectionPrefix)));
        harmony.Patch(_setParachuteDiameter,
            prefix: new HarmonyMethod(typeof(EditorValueLimitPatches), nameof(SetParachuteDiameterPrefix)));

        IsApplied = true;
        Console.WriteLine("dont-stifle-me: editor value-limit patches applied");
    }
```

2. **Draw prefix.** Bind only the `Part` (the second parameter is a byref-like `ReadOnlySpan<Parachute>`, which you do not
   want in a Harmony patch signature) and re-read the chutes the way the game does. While the switch is off, restore:

```csharp
// dont-stifle-me.lib/EditorValueLimitPatches.cs L73-83
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
```

3. **Setter prefix.** Expand the whole group on the receiving part before the stock clamp runs; this catches symmetry
   counterparts and the other canopies of a multi-canopy part:

```csharp
// dont-stifle-me.lib/EditorValueLimitPatches.cs L85-92
    private static void SetParachuteDiameterPrefix(Parachute __instance)
    {
        if (!EditorLimitSettings.JplSaidNoClamps) return;

        // SetDiameter updates every chute module in this part. Expand the whole group first so
        // symmetry counterparts and multi-canopy parts do not silently clamp back to stock.
        ExpandParachuteBounds(__instance.Parent.Modules.Get<Parachute>());
    }
```

4. **Remember the original pair per module** (keyed by module identity) the first time you touch it, then write the wide pair:

```csharp
// dont-stifle-me.lib/EditorValueLimitPatches.cs L94-107
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

5. **Restore** when the switch goes off, from the draw prefix on any editor frame while it is off (covers a switch flipped by a
   scene load), and before unpatching on unload:

```csharp
// dont-stifle-me.lib/EditorValueLimitPatches.cs L63-71
    public static void RestoreTrackedBounds()
    {
        foreach ((Parachute parachute, (float min, float max)) in OriginalParachuteBounds)
        {
            parachute.Tuning.MinDiameterM = min;
            parachute.Tuning.MaxDiameterM = max;
        }
        OriginalParachuteBounds.Clear();
    }
```

6. **Persist the switch, not the bounds.** The bounds are not saved; the mod's scene record carries only the switch value
   (`ExtendedValues`) next to its other editor toggles.
7. **Plan for the re-clamp on load.** `Parachute.GetSaveData` stores `DiameterM` but not the bounds, and `ApplySaveData`
   assigns `Tuning.ClampDiameter(saveData.DiameterM)` against a tuning freshly built from the template
   [current/decomp/KSA/Parachute.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Parachute.cs#L1364-L1377). So a vehicle rebuilt from a save comes back clamped to the authored range (decomp-derived,
   not tested in game). Launching from the editor is unaffected, because the editor hands its live part tree to
   `Vehicle.CreateVehicle`. If you need oversize chutes to survive a load, keep a sidecar of your own (vessel id, part path,
   diameter) and re-apply it after the load. Adapted sketch (not in the mod; compiles against `Parachute.Tuning` and
   `SetDiameter`):

```csharp
// adapted from dont-stifle-me.lib/EditorValueLimitPatches.cs (this helper is not part of the mod)
static void ReapplyDiameter(Part part, float savedDiameterM)
{
    Span<Parachute> chutes = part.SubtreeModules.Get<Parachute>();
    foreach (Parachute chute in chutes)
    {
        chute.Tuning.MinDiameterM = 2f;
        chute.Tuning.MaxDiameterM = 1000f;
    }
    if (chutes.Length > 0) chutes[0].SetDiameter(savedDiameterM);
}
```

## Complete example

- [dont-stifle-me.lib/EditorValueLimitPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/dont-stifle-me.lib/EditorValueLimitPatches.cs)[^unscience-editorvaluelimitpatches]
- [dont-stifle-me.lib/EditorLimitSettings.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/dont-stifle-me.lib/EditorLimitSettings.cs)[^unscience-editorlimitsettings] - the static switch

## Variations and gotchas

- **The fixed 2-1000 m pair also narrows drogues:** a stock drogue authored 1-15 m loses its 1-2 m range while the switch is on.
- **Restoring bounds does not re-clamp the diameter.** `RestoreTrackedBounds` writes only min and max; a chute set to 800 m
  keeps it until something calls `SetDiameter` again. The authors call this intentional and non-destructive.
- **The setter prefix is global.** It fires for every `SetDiameter`, including the in-flight "Parachute Tuning" debug
  window, so those chutes are also expanded and tracked.
- **Strong references.** The dictionary keeps every touched `Parachute` alive until the next restore.
- **Updates.** The authors record both method bodies unchanged from build 5402 to 5554; a rename of `DrawParachuteSection`
  fails at `Apply` with a logged error and a red notice in the UI.

## See also

- [Overview](overview.md), [Article 1](customize-parachute-materials-and-limits-1.md)
- [unscience/dont-stifle-me-parachute-diameter-unclamp](../../../../mods/unscience/features/dont-stifle-me-parachute-diameter-unclamp.md)
- [Scale a vessel or its parts at runtime and lift the editor scale limits](../../vehicles/scale-a-vessel-or-its-parts-at-runtime/overview.md)

[^ksa-parachute]: current/decomp/KSA/Parachute.cs (decompiled, build 2026.10.10.5554)
[^unscience-editorvaluelimitpatches]: unscience: dont-stifle-me.lib/EditorValueLimitPatches.cs
[^ksa-vehicleeditor]: current/decomp/KSA/VehicleEditor.cs (decompiled, build 2026.10.10.5554)
[^unscience-editorlimitsettings]: unscience: dont-stifle-me.lib/EditorLimitSettings.cs
