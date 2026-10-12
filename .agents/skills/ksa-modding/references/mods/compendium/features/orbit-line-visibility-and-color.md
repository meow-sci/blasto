---
type: Mod Feature
title: Orbit line visibility and color
description: A postfix on Astronomical.ShouldDrawLines forces or suppresses a body's orbit line from a mod-side Always/On/Off table, and color pickers write Orbit.OrbitLineColor directly, applied to one body, its moons, an orbit group, a category or the whole system
mod: compendium
category: flight-and-orbits
techniques: [harmony-postfix]
hooks: ["harmony-postfix KSA.Astronomical.ShouldDrawLines(Astronomical, IViewport, Orbit)"]
lifecycle: [frame:draw-ui, frame:pre-render]
game_types:
  - KSA.Astronomical
  - KSA.Celestial
  - KSA.Orbit
  - KSA.OrbitUiMode
  - KSA.OrbitingStar
  - KSA.CelestialSystem
  - KSA.NearestOrbitPointJob
  - KSA.IViewport
  - KSA.IGameViewport
  - Brutal.Numerics.byte4
tags: [orbit-lines, map-view, harmony, postfix, colors, celestials]
---

Every body page, subgroup, category and the start page have `< Always >`, `< On >` and `< Off >`
buttons for orbit lines, and a dropdown with preset colors and RGB sliders for the line color. Always
shows a line even where the game would hide it (for example far from the camera), Off hides it, and On
returns the body to the game's own rules. This is how the window can show or hide hundreds of
asteroid and comet orbits at once.

## How it works

1. **The game's decision.** A celestial adds its orbit line in `AddLineInstances` only if
   `(ShowOrbit || TargetOfControlledVehicle) && Astronomical.ShouldDrawLines(this, viewport, null)`
   ([current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L1740-L1746)).[^ksa-celestial] `ShouldDrawLines(Astronomical, IViewport, Orbit?)`
   returns true for the global "always show orbit lines" setting, false for `OrbitLineMode.Never`, true
   for the followed body and for `OrbitUiMode.Always`, a distance test for `Near`, and a general
   visibility test otherwise ([current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs#L658-L684)).[^ksa-astronomical]
2. **Override table.** `orbitVisibilityOverrides` maps `<systemId>.<dotted path>` to `Always` or `Off`;
   `On` removes the entry. Setting a mode also writes the body's `ShowOrbit` (a `ref bool` property)
   to `mode != Off` ([src/CdmHelpers.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmHelpers.cs#L193-L197), [src/CdmHelpers.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmHelpers.cs#L235-L270),
   [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L71)).[^cmp-cdmhelpers]
3. **Postfix.** `ShouldDrawLinesPostfix(Astronomical astronomical, ref bool __result)` looks the body up.
   `Always` sets `__result = true` unless the body's `OrbitLineMode` is `Never`; `Off` sets it to false;
   no entry leaves the game's result ([src/Patcher.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/Patcher.cs#L40-L61)).[^cmp-patcher]
4. **Which overload.** The attribute lists the argument types `(Astronomical, IGameViewport, Orbit)`,
   but build 2026.10.10.5554 only has `ShouldDrawLines(Astronomical, IViewport, Orbit?)` (plus
   `IOrbiter` and `IParentBody` overloads that forward to it). Harmony resolves attribute targets with
   `Type.GetMethod(name, flags, null, types, modifiers)`, and the default binder accepts an argument type
   that is assignable to the parameter type; `IGameViewport` extends `IViewport`, so the patch binds to
   the `IViewport` overload ([current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs#L603-L620), [current/decomp/KSA/IGameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IGameViewport.cs#L5)).[^ksa-igameviewport]
   This was confirmed with a stand-alone reflection test that mirrors the three overloads.
5. **Bulk operations.** Moons of a body, an orbit group (from the JSON `OrbitLineGroup`), a category,
   or every celestial under the selected root run the same setter in a loop
   ([src/CdmHelpers.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmHelpers.cs#L366-L445), [src/CdmHelpers.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmHelpers.cs#L703-L714)).
6. **Color.** `Orbit.OrbitLineColor` is a public `byte4` field; the setter copies it, replaces `RGB`
   (a `byte3`, keeping alpha) and writes it back for every celestial in the group. The dropdown offers
   eleven named colors and RGB sliders seeded from the first body's current color
   ([src/CdmHelpers.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmHelpers.cs#L556-L589), [src/CdmHelpers.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmHelpers.cs#L604-L676), [current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L1147),
   [current/decomp/Brutal.Numerics/byte4.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Numerics/byte4.cs#L1303)).[^ksa-orbit] [^ksa-brutal-numerics-byte4]

## Lifecycle and timing

- **Writes at `frame:draw-ui`.** Buttons and sliders change the table, `ShowOrbit` and `OrbitLineColor`
  from the `[StarMapAfterGui]` window on the main thread.
- **Reads at `frame:pre-render`.** Line instancing happens in `Universe.OnPreRender` per viewport
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2431)), which recurses `Celestial.OnPreRender` into `AddLineInstances`
  ([current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L1580-L1592)), so a change made in the UI shows in the same frame's lines.[^ksa-program]
- **Also read on a game worker.** `CelestialSystem.SetNearestOrbitPoint`, which finds the orbit under
  the cursor, calls `ShouldDrawLines` too ([current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L473-L539)). It runs as
  `NearestOrbitPointJob` on `JobSystems.NearestOrbitAndPerformanceWorker`, queued in `PrepareFrame` and
  waited at the start of the next `PrepareFrame` ([current/decomp/KSA/NearestOrbitPointJob.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/NearestOrbitPointJob.cs#L22),
  [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2196-L2201)), so the postfix also runs concurrently with the UI pass.[^ksa-celestialsystem]
  [^ksa-nearestorbitpointjob] Hidden orbits are therefore also not pickable, but see the race below.

## Key code

| Path | Role |
|---|---|
| [src/Patcher.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/Patcher.cs#L40-L61) | `ShouldDrawLines` postfix |
| [src/CdmHelpers.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmHelpers.cs#L193-L270) | Override key, lookup, mode setter |
| [src/CdmHelpers.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmHelpers.cs#L556-L676) | Color write and color dropdown |
| [src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L674-L687) | Per-body mode buttons and color dropdown |

## Snippets

```csharp
// src/Patcher.cs L40-L61
[HarmonyPatch(typeof(Astronomical), nameof(Astronomical.ShouldDrawLines), new[] { typeof(Astronomical), typeof(IGameViewport), typeof(Orbit) })]
[HarmonyPostfix]
public static void ShouldDrawLinesPostfix(Astronomical astronomical, ref bool __result)
{
    if (!Compendium.TryGetOrbitVisibilityOverride(astronomical, out OrbitVisibilityMode mode))
    {
        return;
    }

    switch (mode)
    {
        case OrbitVisibilityMode.Always:
            if (astronomical.OrbitLineMode != OrbitUiMode.Never)
            {
                __result = true;
            }
            break;
        case OrbitVisibilityMode.Off:
            __result = false;
            break;
    }
}
```

```csharp
// src/CdmHelpers.cs L235-L270
internal static bool TryGetOrbitVisibilityOverride(Astronomical astronomical, out OrbitVisibilityMode mode)
{
    if (astronomical is Celestial celestial)
    {
        return orbitVisibilityOverrides.TryGetValue(GetOrbitVisibilityKey(celestial), out mode);
    }

    mode = OrbitVisibilityMode.On;
    return false;
}

private static OrbitVisibilityMode GetOrbitVisibilityMode(Celestial celestial)
{
    if (orbitVisibilityOverrides.TryGetValue(GetOrbitVisibilityKey(celestial), out var mode))
    {
        return mode;
    }

    return celestial.ShowOrbit ? OrbitVisibilityMode.On : OrbitVisibilityMode.Off;
}

private static void SetOrbitVisibilityMode(Celestial celestial, OrbitVisibilityMode mode)
{
    string key = GetOrbitVisibilityKey(celestial);

    if (mode == OrbitVisibilityMode.On)
    {
        orbitVisibilityOverrides.Remove(key);
    }
    else
    {
        orbitVisibilityOverrides[key] = mode;
    }

    celestial.ShowOrbit = mode != OrbitVisibilityMode.Off;
}
```

```csharp
// src/CdmHelpers.cs L556-L574
private static void SetOrbitLineColor(IEnumerable<Celestial> celestials, float3 color)
{
    var rgb = new byte3(
        (byte)(color.X * 255),
        (byte)(color.Y * 255),
        (byte)(color.Z * 255));

    foreach (var celestial in celestials)
    {
        if (celestial.Orbit == null)
        {
            continue;
        }

        var orbitColor = celestial.Orbit.OrbitLineColor;
        orbitColor.RGB = rgb;
        celestial.Orbit.OrbitLineColor = orbitColor;
    }
}
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `Astronomical.ShouldDrawLines(Astronomical, IViewport, Orbit?)` | [current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs#L658-L684) | Postfix target |
| `Astronomical.OrbitLineMode`, `OrbitUiMode.Never` | [current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs#L98) | Respect bodies that never draw lines |
| `Celestial.ShowOrbit` (`ref bool`) | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L71) | Game-side visibility flag |
| `Celestial.AddLineInstances`, `OnPreRender` | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L1580-L1592) | Where lines are emitted |
| `Orbit.OrbitLineColor`, `byte4.RGB` | [current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L1147) | Line color |
| `CelestialSystem.SetNearestOrbitPoint`, `NearestOrbitPointJob` | [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L473-L539) | Second, off-thread caller |

## Pitfalls and version notes

- **Data race.** The override table is a plain `Dictionary` written by the UI on the main thread and
  read by the nearest-orbit job on a worker at the same time. A `ConcurrentDictionary` or an immutable
  snapshot swapped per frame would make the read side safe.
- **Allocation per call.** The postfix builds the key string (system id plus a dotted path built by
  walking parents) for every celestial, every viewport, every frame, plus every nearest-orbit query.
- **Only `Celestial`s.** `TryGetOrbitVisibilityOverride` ignores non-`Celestial` bodies, so orbiting
  stars always follow the game's rules ([current/decomp/KSA/OrbitingStar.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitingStar.cs#L216-L227)).[^ksa-orbitingstar]
- **Session only.** Modes and colors are not saved; colors written to `Orbit.OrbitLineColor` last until
  the system reloads.
- **Binding by assignability.** If a future build adds a `ShouldDrawLines` overload that also accepts an
  `IGameViewport`, the binder could pick it or report an ambiguity; listing the exact parameter type
  (`IViewport`) avoids that.

## See also

- [Celestial compendium window](celestial-info-window.md)
- [Cross-mod JSON catalog](cross-mod-json-catalog.md)
- [Star systems and the body tree](star-systems-and-body-tree.md)
- [Force or hide a body's orbit line and change its colour with a postfix on Astronomical.ShouldDrawLines and Orbit.OrbitLineColor](../../../howto/flight-and-orbits/map-view/draw-a-predicted-trajectory-and-map-markers/draw-a-predicted-trajectory-and-map-markers-6.md) - how-to article demonstrating this feature
- [Attribute argument types that bind by assignability](../../../howto/patching/internals/patch-a-private-game-method-by-name/patch-a-private-game-method-by-name-4.md) - how-to article demonstrating this feature

[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^ksa-astronomical]: KSA.Astronomical (decompiled, build 2026.10.10.5554)
[^cmp-cdmhelpers]: Compendium: src/CdmHelpers.cs
[^cmp-patcher]: Compendium: src/Patcher.cs
[^ksa-igameviewport]: KSA.IGameViewport (decompiled, build 2026.10.10.5554)
[^ksa-orbit]: KSA.Orbit (decompiled, build 2026.10.10.5554)
[^ksa-brutal-numerics-byte4]: Brutal.Numerics.byte4 (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-celestialsystem]: KSA.CelestialSystem (decompiled, build 2026.10.10.5554)
[^ksa-nearestorbitpointjob]: KSA.NearestOrbitPointJob (decompiled, build 2026.10.10.5554)
[^ksa-orbitingstar]: KSA.OrbitingStar (decompiled, build 2026.10.10.5554)
