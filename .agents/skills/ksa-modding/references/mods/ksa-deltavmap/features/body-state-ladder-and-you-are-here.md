---
type: Mod Feature
title: Per-body state ladder and "you are here"
description: Each body becomes a vertical ladder of places (surface, low orbit, stationary, SOI edge, intercept; a 1 AU parking orbit for stars and barycenters) derived from live celestial data, and the controlled vessel's orbit is classified onto it as the route origin
mod: ksa-deltavmap
category: flight-and-orbits
techniques: [patched-conic-math, celestial-introspection, vehicle-state-classification]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types:
  - KSA.IParentBody
  - KSA.IRadius
  - KSA.Astronomical
  - KSA.AtmosphericBody
  - KSA.AstronomicalTemplate
  - KSA.AtmosphereReference
  - KSA.PhysicalAtmosphereReference
  - KSA.Celestial
  - KSA.Vehicle
  - KSA.Orbit
  - KSA.Program
tags: [delta-v, ladder, low-orbit, stationary-orbit, soi, gas-giant, vehicle-state, celestials]
---

Every body on the map is drawn as a short vertical ladder of states you can be in: its surface, a
low parking orbit, a stationary orbit and an ellipse out to the edge of its sphere of influence.
A destination entered from outside gets an Intercept node (the loose capture ellipse). Stars and
barycenters, which nothing lands on, get a hub ladder with a single wide parking orbit at 1 AU.
The controlled vessel's current orbit is then classified onto its parent body's ladder and becomes
the "you are here" node every route starts from.

The ladder is computed from live celestial data only (mean radius, mu, SOI, atmosphere template,
rotation rate, terrain height), so modded systems get sensible ladders with no per-body config.
The vessel state uses the game's own contact flag rather than an altitude guess.

## How it works

### Rungs

`OrbitalStates.BuildLadder(IParentBody)` returns a `BodyLadder` with the key radii and the rungs
ordered bottom to top.[^dvm-orbitalstates]

| Rung | Radius | Present when |
|---|---|---|
| `Surface` | `MeanRadius` | `HasSolidSurface`: airless, or atmosphere and `MeanRadius < 15,000 km` |
| `LowOrbit` | `LowOrbitHeuristic.Compute(...)`, see below | `CanHoldOrbit` (SOI above the mean radius) |
| `Stationary` | `cbrt(mu T^2 / (4 pi^2))`, `T = 2 pi / abs(omega)` | the body rotates, the radius is inside the SOI and above `r_lo` |
| `SoiEdge` | `SphereOfInfluence` (an ellipse whose apoapsis touches it) | finite SOI and `CanHoldOrbit` |
| `Intercept` | SOI radius (or mean radius) | added by `VisualTree.BuildLadder` for a destination only[^dvm-visualtree] |
| `YouAreHere` | the vessel's SMA or distance | inserted by the classifier, not part of the physical ladder |

Two guards keep the ladder physically ordered on tiny moons: when the SOI is at or below the
mean radius the body cannot hold an orbit at all and becomes a surface-only flyby destination
(no LowOrbit, Stationary or SoiEdge rung; the comment names Deimos), and when the heuristic low
orbit lands above the SOI it is clamped to `(MeanRadius + SOI) / 2`.

### Low-orbit heuristic

`LowOrbitHeuristic.Compute(meanRadius, hasAtmosphere, atmosphereHeight, nearSurfaceRadius)` follows
the KSP delta-v map convention of "about 10 km above the atmosphere or the highest terrain":[^dvm-lowobitheuristic]

- With an atmosphere: `R + H + max(10 km, 0.1 H)`.
- Airless: `max(GetNearSurfaceRadius() + 10 km, 1.05 R)`; the 5% term wins for large airless
  bodies (the comment cites Luna at about 87 km instead of about 20 km).
- Never below `R + 5 km`.

`GetNearSurfaceRadius` is the game's own helper: `Astronomical` returns `MaxTerrainRadius + 1`,
`AtmosphericBody` overrides it to `MeanRadius + atmosphere height + 1` when it has an atmosphere.
The mod only uses it on the airless branch, so it effectively reads the highest terrain.[^ksa-astronomical][^ksa-atmosphericbody]

### Gas giants and hub ladders

The game has no gas-giant flag, so `HasSolidSurface` drops the surface rung for any body with an
atmosphere and a radius of at least 15,000 km (stock Neptune at about 24,600 km is above it, Earth
at about 6,400 km below). Airless bodies always keep their surface.[^dvm-orbitalstates]

`SystemGraph.BuildNode` builds `BuildLadder` for a planet, moon or minor body and `BuildHubLadder`
for a star or barycenter, once per loaded system.[^dvm-systemgraph] The hub ladder has no
surface, one `LowOrbit` rung labelled "Parking Orbit" at
`HubParkingRadiusFor(1 AU, R, SOI) = max(min(1 AU, 0.5 SOI), 2 R)` and a `SoiEdge` rung when an
orbiting star's SOI is wider than that. The same function sizes the interstellar capture, so the map
and the interstellar estimate agree for one star.

### Detail level

`DetailLevel` (built per tree from the re-root result) gives `Full` detail to the root body, its
moons, every spine hub and the siblings on the nearest hub; every other body is `Core`, which keeps
only `Surface` and `LowOrbit`. The Options menu item "Full ladder everywhere" promotes every body
and rebuilds.[^dvm-detaillevel][^dvm-mapwindow] Detail also picks the arrival anchor: a destination
at `Full` detail with a finite SOI is entered at its `Intercept` node and circularizes down, while a
`Core` destination is entered straight at its low orbit. The outbound `SoiEdge` rung is only built
for the body you are at, never for a destination.[^dvm-visualtree]

### "You are here"

`StateClassifier.Classify(vehicle, ladder)` maps the controlled vessel onto its parent's ladder:[^dvm-stateclassifier]

1. `!vehicle.HasMeaningfulOrbit` (the game defines it as `!Situation.HasAnyContact()`): `Surface`.[^ksa-vehicle]
2. `Eccentricity >= 1`: `SoiEdge` (leaving through the SOI).
3. `Eccentricity < 0.05` and SMA within 5% of `r_lo` or of the stationary radius: snap to that rung.
   This runs before step 4 so a parking orbit on a tight-SOI body still reads as low orbit.
4. Apoapsis above half the SOI: `SoiEdge`.
5. Otherwise a `YouAreHere` node at the actual SMA (the "medium orbit" case).

`ClassifyCruise` handles a vessel orbiting a star or barycenter directly: always its own
`YouAreHere`, at the SMA on a bound orbit, else at its current distance
(`vehicle.GetPositionEcl() - vehicle.Parent.GetPositionEcl()`) with `IsOpenCruise` set so the router
does not treat that distance as an orbit to transfer from.

`VisualTree.InsertYouAreHere` then either flags the snapped rung (`IsYouAreHere = true`) or inserts a
new node raised off the low orbit with a `CircularToCircular` cost. A cruise state hangs off the hub
by a dV-free `HubLink`. At most one node per tree carries the flag, and `StateNode.Id` is stable
(`"<body>.<state>"`, e.g. `Luna.LowOrbit`) so selection survives rebuilds.[^dvm-visualtree][^dvm-statenode]

`MapWindow.RebuildAt` classifies only when the vessel's parent body is the map root; a map
re-rooted elsewhere has no "you are here" and routes from that body's low orbit instead.[^dvm-mapwindow]

## Lifecycle and timing

All of this runs on the main thread from the `[StarMapAfterGui]` draw (`frame:draw-ui`) while the
map window is shown.

| Step | When |
|---|---|
| Ladders for every body | `SystemGraph.Build`, from `MapWindow.EnsureBuilt` on the first draw after the `CelestialSystem` instance changes |
| Detail level and rung materialization | `VisualTree.Build` inside `MapWindow.RebuildAt` |
| Vessel classification | `MapWindow.RebuildAt` only: first build, a root change (Shift+click, opening the map or "Reset Root" when the desired root differs, a vehicle switch, a crossing into another star system), and the layout, detail and visibility toggles |

The classifier is not run every frame. It reads `Vehicle.HasMeaningfulOrbit`, `Vehicle.Orbit`
(`Patch.Orbit`, i.e. `FlightPlan.Patches[0].Orbit`) and `Vehicle.GetPositionEcl()`. Those are
main-thread fields: `Universe.ApplyVehicleSolvers` swaps in the worker's new `FlightPlan`
(`SetFlightPlanUnsynchronized(updateData.NewFlightPlan)`) during `frame:apply-solvers`, and the
next step, dispatched at `frame:dispatch-solvers` before this draw, builds its own `NewFlightPlan`
in `VehicleUpdateState` rather than mutating the vessel's.[^ksa-vehicle][^ksa-program] Reading them
from the draw therefore sees the state applied at the start of this frame. The classification call
and the parent lookup are wrapped in `try` because a freshly spawned vessel can throw while its
patch chain is not materialized.

## Key code

| Path | Role |
|---|---|
| [DeltaVMap/Dv/OrbitalStates.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Dv/OrbitalStates.cs#L85-L279) | `StateKind`, `BodyLadder`, gas-giant test, stationary radius, `BuildLadder`, `BuildHubLadder`, `HasRings` |
| [DeltaVMap/Dv/LowOrbitHeuristic.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Dv/LowOrbitHeuristic.cs#L10-L36) | `r_lo` rule |
| [DeltaVMap/Dv/StateClassifier.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Dv/StateClassifier.cs#L28-L95) | `Classify` and `ClassifyCruise` |
| [DeltaVMap/Model/DetailLevel.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Model/DetailLevel.cs#L20-L65) | Full vs Core bodies and rung filter |
| [DeltaVMap/Model/VisualTree.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Model/VisualTree.cs#L383-L454) | Rung nodes, arrival anchor, `InsertYouAreHere` (L509-L534), `AttachCruiseYouAreHere` (L536-L542) |
| [DeltaVMap/Model/StateNode.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Model/StateNode.cs#L14-L59) | Node with stable Id, `IsYouAreHere`, `IsOpenCruise` |
| [DeltaVMap/Model/SystemGraph.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Model/SystemGraph.cs#L176-L187) | Ladder built once per body |
| [DeltaVMap/Render/MapWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/MapWindow.cs#L1986-L2000) | Classification call in `RebuildAt` |

## Snippets

```csharp
// DeltaVMap/Dv/LowOrbitHeuristic.cs L15-L35
    public static double Compute(double meanRadius, bool hasAtmosphere, double atmosphereHeight, double nearSurfaceRadius)
    {
        double rLo;
        if (hasAtmosphere)
        {
            // About 10 km above the atmosphere boundary (or 10% of its height,
            // whichever is larger, for very tall atmospheres).
            rLo = meanRadius + atmosphereHeight + Math.Max(ClearanceMeters, 0.1 * atmosphereHeight);
        }
        else
        {
            // About 10 km above the highest terrain, but never below 5% of the
            // body radius. The 5% term is a sane minimum parking altitude that
            // wins for larger airless bodies (e.g. Luna lands at ~87 km, not the
            // ~20 km the bare 10 km clearance would give), leaving margin over
            // unmodelled terrain.
            rLo = Math.Max(nearSurfaceRadius + ClearanceMeters, meanRadius * 1.05);
        }

        return Math.Max(rLo, meanRadius + AbsoluteFloorMeters);
    }
```

```csharp
// DeltaVMap/Dv/OrbitalStates.cs L184-L208
        bool canHoldOrbit = true;
        if (rSoi.HasValue)
        {
            if (rSoi.Value <= meanRadius)
                canHoldOrbit = false;
            else if (rLo >= rSoi.Value)
                rLo = (meanRadius + rSoi.Value) / 2.0;
        }

        double? rSync = canHoldOrbit ? ComputeStationaryRadius(body) : null;

        var rungs = new List<LadderRung>(4);
        if (hasSurface)
            rungs.Add(new LadderRung(StateKind.Surface, meanRadius));

        if (canHoldOrbit)
        {
            rungs.Add(new LadderRung(StateKind.LowOrbit, rLo));
            // A stationary rung only makes sense above the low orbit; below it the
            // body rotates faster than a low parking orbit and it is not useful.
            if (rSync.HasValue && rSync.Value > rLo)
                rungs.Add(new LadderRung(StateKind.Stationary, rSync.Value));
            if (rSoi.HasValue)
                rungs.Add(new LadderRung(StateKind.SoiEdge, rSoi.Value));
        }
```

```csharp
// DeltaVMap/Dv/StateClassifier.cs L41-L75
    public static ClassifiedState Classify(Vehicle vehicle, BodyLadder ladder)
    {
        if (!vehicle.HasMeaningfulOrbit)
            return new ClassifiedState(StateKind.Surface, ladder.MeanRadius);

        Orbit orbit = vehicle.Orbit;
        double eccentricity = orbit.Eccentricity;
        double semiMajorAxis = orbit.SemiMajorAxis;
        double apoapsis = orbit.Apoapsis;

        // An escape trajectory is leaving through the SOI boundary.
        if (eccentricity >= 1.0)
            return new ClassifiedState(StateKind.SoiEdge, ladder.SoiRadius ?? apoapsis);

        // Snap a near-circular orbit onto a specific rung first. This has to come
        // before the fractional SOI-edge test below, because on a tight-SOI body
        // the low orbit can sit above half the SOI, and a genuine parking orbit
        // there must still read as LowOrbit, not SoiEdge.
        if (eccentricity < NearCircularEccentricity)
        {
            if (IsNear(semiMajorAxis, ladder.LowOrbitRadius))
                return new ClassifiedState(StateKind.LowOrbit, ladder.LowOrbitRadius);

            if (ladder.StationaryRadius.HasValue && IsNear(semiMajorAxis, ladder.StationaryRadius.Value))
                return new ClassifiedState(StateKind.Stationary, ladder.StationaryRadius.Value);
        }

        // Otherwise, an orbit reaching well out toward the SOI is on the
        // elliptical-to-SOI-edge rung.
        if (ladder.SoiRadius.HasValue && apoapsis > SoiEdgeFraction * ladder.SoiRadius.Value)
            return new ClassifiedState(StateKind.SoiEdge, ladder.SoiRadius.Value);

        // Between rungs: the "medium orbit" case, placed at its actual SMA.
        return new ClassifiedState(StateKind.YouAreHere, semiMajorAxis);
    }
```

```csharp
// DeltaVMap/Render/MapWindow.cs L1986-L2000
        ClassifiedState? egoState = null;
        Vehicle? vehicle = Program.ControlledVehicle;
        if (vehicle != null && TryGetEgoParentId() == node.Id)
        {
            try
            {
                egoState = node.IsHubOnly
                    ? StateClassifier.ClassifyCruise(vehicle)
                    : StateClassifier.Classify(vehicle, node.Ladder);
            }
            catch (Exception ex)
            {
                LogHelper.WarnOnce("classify-" + node.Id, $"[DvMap] Could not classify vehicle state at '{node.Id}': {ex.Message}");
            }
        }
```

## Game APIs touched

| API | Decompiled source | Used for |
|---|---|---|
| `IParentBody.Mu`, `SphereOfInfluence`, `GetAtmosphereReference`, `GetAngularVelocity`, `GetNearSurfaceRadius`, `BodyTemplate`, `Children` | [current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs) | Every ladder radius; `Mu` is `Mass * 6.6743E-11`[^ksa-iparentbody] |
| `IRadius.MeanRadius` | same interface chain | Surface rung and gas-giant threshold |
| `Astronomical.GetNearSurfaceRadius` (`MaxTerrainRadius + 1`) | [current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs) | Highest terrain for an airless body's low orbit[^ksa-astronomical] |
| `AtmosphericBody.GetNearSurfaceRadius` | [current/decomp/KSA/AtmosphericBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphericBody.cs) | Override (top of atmosphere), not reached on the mod's airless branch[^ksa-atmosphericbody] |
| `AstronomicalTemplate.RingsReference` | [current/decomp/KSA/AstronomicalTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AstronomicalTemplate.cs) | `OrbitalStates.HasRings`, the ring marker on the canvas[^ksa-astronomicaltemplate] |
| `PhysicalAtmosphereReference.Height`, `SeaLevelDensity` | [current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs) | Low-orbit clearance, usable-atmosphere floor (0.01 kg/m^3)[^ksa-physicalatmosphere] |
| `Vehicle.HasMeaningfulOrbit`, `Vehicle.Orbit`, `Vehicle.Parent`, `Vehicle.GetPositionEcl` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs) | Surface test (`!Situation.HasAnyContact()`), orbit elements, cruise distance[^ksa-vehicle] |
| `Orbit.Eccentricity`, `SemiMajorAxis`, `Apoapsis` | [current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs) | Rung snapping[^ksa-orbit] |
| `Program.ControlledVehicle` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs) | The vessel to classify[^ksa-program] |

## Pitfalls and version notes

- The marker is a snapshot from the last `RebuildAt`. A burn that changes your orbit while the
  window stays open does not move it; neither does an SOI change inside the same star system
  (`FollowVessel` only re-roots on a vehicle switch or a crossing into another system). Toggling
  "Full ladder everywhere", a layout mode or a visibility option rebuilds and re-classifies;
  Shift+clicking the current root is a no-op (`ReRootTo` returns early), so re-root away and back
  instead.[^dvm-mapwindow]
- `HasMeaningfulOrbit` is false on any contact, so a vessel docked to or touching something reads
  as "Surface" (inferred from the game definition, not tested).
- The 15,000 km gas-giant cut and the 5% / 50% snap fractions are heuristics tuned to the stock
  system; a modded small gas giant or a huge rocky world with air would be misclassified.
- A stationary radius below the low orbit (a fast spinner) is dropped, and one beyond the SOI is
  never offered.
- Checked against build 2026.10.10.5554: `Vehicle.HasMeaningfulOrbit`, `IParentBody` members and
  `GetNearSurfaceRadius` behave as described.

## See also

- [Closed-form patched-conic transfer model](./patched-conic-transfer-model.md)
- [Route finding and accumulation](./route-finding-and-accumulation.md)
- [Live celestial system graph](./live-celestial-system-graph.md)
- [Ego re-rooting and vessel follow](./ego-rerooting-and-vessel-follow.md)
- [Subway-map canvas rendering](./subway-map-canvas-rendering.md)
- [KSA-DeltaVMap overview](../overview.md)
- [Atmosphere height, density and ring templates feeding classification decisions, and angular velocity to a stationary orbit radius](../../../howto/celestials/systems-and-stars/read-body-properties-and-orbit-elements/read-body-properties-and-orbit-elements-2.md) - how-to article demonstrating this feature
- [Build a per-body state ladder and classify the vessel onto it with a snapped eccentricity rule](../../../howto/flight-and-orbits/transfers/plan-multi-leg-and-interstellar-delta-v-routes/plan-multi-leg-and-interstellar-delta-v-routes-2.md) - how-to article demonstrating this feature

[^dvm-orbitalstates]: DeltaVMap/Dv/OrbitalStates.cs
[^dvm-lowobitheuristic]: DeltaVMap/Dv/LowOrbitHeuristic.cs
[^dvm-stateclassifier]: DeltaVMap/Dv/StateClassifier.cs
[^dvm-detaillevel]: DeltaVMap/Model/DetailLevel.cs
[^dvm-statenode]: DeltaVMap/Model/StateNode.cs
[^dvm-visualtree]: DeltaVMap/Model/VisualTree.cs
[^dvm-systemgraph]: DeltaVMap/Model/SystemGraph.cs
[^dvm-mapwindow]: DeltaVMap/Render/MapWindow.cs
[^ksa-iparentbody]: KSA.IParentBody (decompiled, build 2026.10.10.5554)
[^ksa-astronomical]: KSA.Astronomical (decompiled, build 2026.10.10.5554)
[^ksa-atmosphericbody]: KSA.AtmosphericBody (decompiled, build 2026.10.10.5554)
[^ksa-astronomicaltemplate]: KSA.AstronomicalTemplate (decompiled, build 2026.10.10.5554)
[^ksa-physicalatmosphere]: KSA.PhysicalAtmosphereReference (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-orbit]: KSA.Orbit (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
