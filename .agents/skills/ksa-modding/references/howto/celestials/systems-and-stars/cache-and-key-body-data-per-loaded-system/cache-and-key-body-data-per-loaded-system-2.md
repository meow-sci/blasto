---
type: How-To
title: ConditionalWeakTable of immutable statics per Celestial
description: Cache each body's constant data in a ConditionalWeakTable keyed by the body instance so a system reload invalidates it automatically, and read only position and velocity per sample
category: celestials
group: systems-and-stars
topic: cache-and-key-body-data-per-loaded-system
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/gatos/features/celestial-body-catalog-reads]
techniques: [conditional-weak-table-cache]
hooks: [harmony-prefix KSA.GameViewport.OnFrame, starmap StarMapBeforeGui]
lifecycle: [frame:viewports, frame:draw-ui]
game_types: [KSA.Celestial, KSA.CelestialSystem, KSA.IParentBody, KSA.Orbit]
tags: [cache, conditional-weak-table, immutable, snapshot, telemetry]
---
Outcome: a body sample costs two vector reads and one shared immutable record; the cache clears itself when bodies are replaced. Demonstrated by gatOS ([Celestial body catalog reads](../../../../mods/gatos/features/celestial-body-catalog-reads.md)); the repo's comment records the pre-cache cost as about 84 allocations per tick for a 20-body system.

## Background: how the game does it

Everything except `GetPositionEcl()` and `GetVelocityEcl()` is constant for a session: mass, radius, `Mu`, sphere of influence, rotation rate, parent and child ids, orbit elements (bodies are on rails), atmosphere and ocean constants. A `ConditionalWeakTable<Celestial, BodyStatics>` keyed by the body instance holds the statics without keeping a replaced body alive; when `Universe.LoadSystem` builds new objects, the old entries are simply collected.

## Lifecycle and timing

The sampler runs on the main thread (a viewport-frame prefix or the draw hook). Statics are built lazily on the first sight of each body. Orbit-solver results are copied onto bodies by `Universe.ApplyOrbitSolvers` at the start of `PrepareFrame`, so a read later in the frame is race-free. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Declare the tables, one per key type** (the star is a `StellarBody`, not a `Celestial`; the system summary is keyed by the system instance):

```csharp
// gatOS.GameMod/Game/Ksa/Readers/BodyReader.cs L32-L34
private static readonly ConditionalWeakTable<Celestial, BodyStatics> CelestialCache = new();
private static readonly ConditionalWeakTable<StellarBody, BodyStatics> StarCache = new();
private static readonly ConditionalWeakTable<CelestialSystem, SystemSnapshot> SystemCache = new();
```

2. **Fetch or build statics with the factory overload:**

```csharp
// gatOS.GameMod/Game/Ksa/Readers/BodyReader.cs L87-L91
private static BodySnapshot FromCelestial(Celestial c)
{
    var statics = CelestialCache.GetValue(c, BuildCelestialStatics);
    return Assemble(statics, c.GetPositionEcl(), c.GetVelocityEcl());
}
```

3. **Assemble each sample from statics plus the two live vectors** and keep the statics shared by reference.

4. **Re-publish unchanged lists by reference** when a sub-cadence skips a tick, so consumers compare cheaply:

```csharp
// gatOS.GameMod/Game/TelemetrySampler.cs L304-L316
private (IReadOnlyList<BodySnapshot>, SystemSnapshot?) SampleBodiesPaced(CelestialSystem system)
{
    var rate = _settings.BodiesRateHz;
    var now = Stopwatch.GetTimestamp();
    if (rate > 0 && _lastBodiesTimestamp != 0 && now - _lastBodiesTimestamp < Stopwatch.Frequency / rate)
        return (_lastBodies, _lastSystem);

    var (bodies, summary) = SampleBodies(system);
    _lastBodies = bodies;
    _lastSystem = summary;
    _lastBodiesTimestamp = now;
    return (bodies, summary);
}
```

## Complete example

| File | Role |
|---|---|
| [gatOS.GameMod/Game/Ksa/Readers/BodyReader.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Readers/BodyReader.cs#L82-L127) | Per-celestial statics cache |
| [gatOS.GameMod/Game/TelemetrySampler.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/TelemetrySampler.cs#L304-L316) | Bodies sub-cadence |

## Variations and gotchas

- **The cache key is an object,** so a reload invalidates it for free; a dictionary keyed by id would not.
- **Statics must be immutable.** Share them across threads and ticks.
- **Unbound orbits:** apoapsis is gated on `Orbit.IsBound()` (0 when unbound).

## See also

- [Overview](overview.md); [approach 1](cache-and-key-body-data-per-loaded-system-1.md); [approach 3](cache-and-key-body-data-per-loaded-system-3.md)
- [Celestial body catalog reads](../../../../mods/gatos/features/celestial-body-catalog-reads.md)
- [Read a body's properties and orbit elements](../../../celestials/systems-and-stars/read-body-properties-and-orbit-elements/overview.md)
