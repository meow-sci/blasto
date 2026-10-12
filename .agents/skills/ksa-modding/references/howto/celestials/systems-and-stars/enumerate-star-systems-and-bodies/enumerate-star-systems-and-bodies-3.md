---
type: How-To
title: Flat scan of CurrentSystem.All plus an explicit WorldSun
description: Enumerate every body with one scan of CelestialSystem.All, reading Parent per body instead of walking children, and add the root star explicitly because it is a StellarBody and not a Celestial
category: celestials
group: systems-and-stars
topic: enumerate-star-systems-and-bodies
approach: 3
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/gatos/features/celestial-body-catalog-reads]
techniques: [immutable-snapshot]
hooks: [harmony-prefix KSA.GameViewport.OnFrame, starmap StarMapBeforeGui]
lifecycle: [frame:viewports, frame:draw-ui]
game_types: [KSA.Universe, KSA.CelestialSystem, KSA.Astronomical, KSA.Celestial, KSA.StellarBody]
tags: [flat-scan, all, worldsun, snapshot, telemetry]
---
Outcome: a snapshot list of every body without caring about the tree. Demonstrated by gatOS's body reader ([Celestial body catalog reads](../../../../mods/gatos/features/celestial-body-catalog-reads.md)). The cheapest walk when parent and child structure is not needed (read `Parent` per body instead).

## Background: how the game does it

`CelestialSystem.All` is a `LookupCollection<Astronomical>` holding every body; `UnsafeAsList()` returns its backing list ([current/decomp/KSA/LookupCollection.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LookupCollection.cs#L210), [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L64-L66)). Planets and moons are `Celestial`; the root star is a separate `StellarBody`, which is **not** a `Celestial`, so a scan that filters on `Celestial` omits it. `Universe.WorldSun` is the nearest star to the camera at the time, which for a single-star universe is the root star.

## Lifecycle and timing

gatOS runs the sampler on the main thread from a viewport-frame prefix (`frame:viewports`) or the draw hook (`frame:draw-ui`). Body positions come from the rails ephemeris and do not depend on the in-flight vehicle step: the orbit solver jobs `PrepareFrame` dispatches only stage new state vectors in their `CelestialUpdateTask`, and `Universe.ApplyOrbitSolvers` copies them onto the bodies at the start of the next `PrepareFrame` ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1905-L1920)), so reads later in the frame are race-free. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Scan `All`, keep `Celestial`s, remember whether the star was listed:**

```csharp
// gatOS.GameMod/Game/Ksa/Readers/BodyReader.cs L57-L78
internal static (IReadOnlyList<BodySnapshot> Bodies, SystemSnapshot? System) Sample(CelestialSystem system)
{
    var all = system.All.UnsafeAsList();
    var bodies = new List<BodySnapshot>(all.Count);
    var sun = Universe.WorldSun;
    var sunListed = false;
    foreach (var astronomical in all)
        if (astronomical is Celestial celestial)
        {
            bodies.Add(FromCelestial(celestial));
            if (sun is not null && celestial.Id == sun.Id)
                sunListed = true;
        }

    if (sun is not null && !sunListed)
        bodies.Add(FromStar(sun, bodies));

    var summary = SystemCache.GetValue(system,
        s => new SystemSnapshot(Universe.WorldSun?.Id ?? "", s.HomeBody?.Id, Universe.WorldSun?.Id));
    return (bodies, summary);
}

```

2. **Add the star yourself** when the scan did not list it (it is a `StellarBody`), reading `Mass`, `MeanRadius` and the like through the type or `IParentBody`.

3. **Name the system after its star** (`CelestialSystem` has no name), and use `HomeBody` for the launch body; cache this summary per system instance.

4. **Read parent per body instead of walking children:** each `Celestial` has `Parent` and `Children` (ids captured once).

5. **Re-publish unchanged lists by reference** between samples so consumers can compare cheaply; see [cache and key per-body data](../../../celestials/systems-and-stars/cache-and-key-body-data-per-loaded-system/overview.md) for the statics cache that makes each sample two vector reads per body.

## Complete example

| File | Role |
|---|---|
| [gatOS.GameMod/Game/Ksa/Readers/BodyReader.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Readers/BodyReader.cs#L56-L76) | System walk, star added explicitly, summary |
| [gatOS.GameMod/Game/TelemetrySampler.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/TelemetrySampler.cs#L304-L316) | Bodies sub-cadence |

## Variations and gotchas

- **Do not assume the star appears as a `Celestial`.**
- **Atmosphere values are reference wrappers** with implicit `double` conversions; cast explicitly.
- **Frames.** Body positions are ECL; vessel positions are CCI relative to the parent. Combine by adding the parent's ECL position to the vessel's CCE offset (see [coordinate frames](../../../flight-and-orbits/flight-state/understand-ksa-coordinate-frames-and-epochs/overview.md)).
- **Instances are cache keys,** so a system reload naturally invalidates a `ConditionalWeakTable`.

## See also

- [Overview](overview.md); [approach 1](enumerate-star-systems-and-bodies-1.md); [approach 2](enumerate-star-systems-and-bodies-2.md)
- [Celestial body catalog reads](../../../../mods/gatos/features/celestial-body-catalog-reads.md)
- [Read a body's properties and orbit elements](../../../celestials/systems-and-stars/read-body-properties-and-orbit-elements/overview.md), [Understand KSA coordinate frames and epochs](../../../flight-and-orbits/flight-state/understand-ksa-coordinate-frames-and-epochs/overview.md)
