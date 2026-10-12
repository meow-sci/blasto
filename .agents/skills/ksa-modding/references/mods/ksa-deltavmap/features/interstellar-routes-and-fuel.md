---
type: Mod Feature
title: Interstellar routes and fuel
description: Prices a trip to another star system as one leg at a chosen cruise speed, using patched-conic gravity-well chains, a Newtonian coast, cheapest and fastest-affordable speed searches, and the game's own antimatter-drive integrator for fuel
mod: ksa-deltavmap
category: flight-and-orbits
techniques: [patched-conic-math, golden-section-search, stock-model-reuse, cached-ui-strings, imgui-window]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types:
  - KSA.InterstellarBurnPlan
  - KSA.InterstellarDrive
  - KSA.InterstellarBurnEstimate
  - KSA.InterstellarBurnEnd
  - KSA.OrbitalTransfers
  - KSA.TransferPlanner
  - KSA.CelestialSystem
  - KSA.Vehicle
  - KSA.VehicleProperties
  - KSA.Mole
  - KSA.Orbit
  - KSA.IParentBody
  - KSA.Universe
  - KSA.ConsoleWidgets
tags:
  - interstellar
  - delta-v
  - patched-conic
  - antimatter
  - ram-scoop
  - rocket-equation
  - golden-section
  - cruise-speed
  - imgui
---

Clicking a body of another star system plans a route there like any other. The interstellar part
is folded into a single leg: the departure burn that climbs out of every gravity well between the
vessel and its star, a straight coast to the destination star, and a capture that falls into every
well down to the target's low orbit (or a parking orbit around a star). The player picks a cruise
speed (0.001 c, 0.01 c, 0.1 c, a logarithmic slider, or MAX, the fastest the vessel's staged
delta-v can pay for), and the panel shows departure, coast and capture lines, the cheapest trip at
any speed, feasibility, and a fuel estimate.

The fuel estimate reuses the game rather than imitating it: for a vessel with an active antimatter
ram drive the mod feeds the public `InterstellarBurnPlan.Burn` and `CoastCollection` in exactly the
order the stock Transfer Planner does; any other vessel gets the rocket equation over its staged
sequences. A button opens the stock planner on its Interstellar plan (see
[the stock transfer planner bridge](./stock-transfer-planner-bridge.md)).

## How it works

### The model

The kernel follows the game's interstellar planner: Newtonian, an escape hyperbola with excess
speed `vInf`, a straight coast of `distance / vInf`, and a capture.[^dvm-interstellarmath] Stock
`OrbitalTransfers.SolveInterstellar` sets `Transit = distance / hyperbolicExcess` the same way and
computes the start speed with `DepartureSpeedForExcess(mu, r, vInf) = sqrt(vInf^2 + 2 mu / r)`,
which `InterstellarMath.SpeedForExcess` mirrors.[^ksa-orbitaltransfers]

| Choice | Mod (`InterstellarLegs`) | Stock planner |
|---|---|---|
| Coast start | Nearest star at or above the departure body, else the system root | `Universe.NearestStar(Source.GetPositionEcl())` |
| Coast end | Nearest star at or above the target, else the system root | the destination root |
| Capture at a star or barycenter | Periapsis slider 0.01 to 1000 AU, default 1 AU, kept inside half a finite SOI and outside `2 R` (`HubParkingRadiusFor`) | `_targetPeriapsisAu = 1f`, slider 0.01 to 10000 AU |
| Departure | Every well from the parking orbit up (moon, planet, star) | Only a vessel orbiting the source star directly (`SolveInterstellar` returns false otherwise) |
| Arrival | Chain down into a planet's or moon's own low orbit | Brake near periapsis around the destination star |
| Speed cap | `OrbitalTransfers.MAX_INTERSTELLAR_SPEED` (269,813,212.2 m/s, 0.9 c) | same constant; MAX SPEED slider 0.001 to 0.9 c |

Sources: `InterstellarLegs` header comment and code; `TransferPlanner.SourceStar`,
`_targetPeriapsisAu`, `INTERSTELLAR_PLAN_PERIAPSIS`.[^dvm-interstellarlegs][^ksa-transferplanner]

### Gravity-well chains

`InterstellarLegs.Chain(body, aim, arrival)` lists the levels from the system root down to the body;
each `WellStep` is the outer body's `Mu`, the inner body's transfer radius, the inner body's own
speed (from its own `Orbit.Mu`, so a star of a multiple system moves at the right speed; an open
orbit uses the periapsis speed), and optionally `AimSine`, the sine of the aim direction out of
that level's orbital plane (`dot(aim, normal)` with `Orbit.GetOrbitNormalCci()`, lifted to ECL axes
through `Parent.GetCci2Cce()` below the root).[^dvm-interstellarlegs]

`InterstellarMath.ChainBurn(vInf, steps, park)` folds the excess inward:[^dvm-interstellarmath]

1. At each level the vessel needs `v = sqrt(excess^2 + 2 mu_outer / r)` at the inner body's radius.
2. Subtracting the body's velocity `u` leaves `excess' = sqrt(v^2 + u^2 - 2 u * along)`, where
   `along` is `v` for a free direction (burn along the body's motion, the best case) or, for a
   plane-limited level, `BestSpeedAlongMotion`: a 32-sample scan plus 40 golden-section steps over
   the departure point on the orbit (the "best time of year") using the hyperbola geometry in the
   code comment.
3. The last level ends in `max(0, sqrt(excess^2 + 2 mu_park / r_park) - v_park)`.

A departure chain is plane-limited only on the level directly under the root (that excess must
point along the aim); deeper levels take the free best case, so they are a lower bound. An arrival
chain carries a sine on every level, and `CaptureChainBurn` may spend part of the burn during the
coast to rotate the excess into each level's plane: for each plane-limited level it golden-sections
(32 steps) the correction `d` in `[0, normal part]` that minimizes `d` plus the rest of the chain,
carrying the corrected sine into a deeper level that shares the plane (`SharesOuterPlane`, normals
parallel to 1e-9). With `d = 0` the result is exactly the plain chain, so the corrected capture is
never worse; the offline tests assert both.[^dvm-tests]

### Searches over the cruise speed

The total is not monotonic in `vInf`: out of a tilted plane a slow escape bends too much, so a few
km/s of cruise can make the departure cheaper. `InterstellarLeg` therefore searches:[^dvm-interstellarmath]

| Search | Method |
|---|---|
| `CheapestSpeed` | Doubling scan from 1 m/s to the cap, then 60 golden-section steps between the neighbours of the best sample |
| `MaxCruiseSpeed(budget)` | 0 below the cheapest trip, the cap if the cap fits, else bisection (up to 200 steps) on the rising branch above the cheapest speed |
| Capture table | For a capture with plane-limited levels, captures are tabulated lazily at 6 nodes per octave from 1 m/s to 0.9 c and interpolated with a monotone cubic Hermite in `log2(speed)` (Fritsch and Carlson); the fastest speed is then polished on the exact total with Illinois regula falsi |

`InterstellarMath.LegBudget(available, dvScale, restDv) = max(0, available / dvScale - restDv)` is
what MAX may spend after the rest of the route and without the piloting margin;
`RouteTotal = (rest + depart + capture - split) * dvScale`, where `split` is the circularize the
route already charges after an Intercept anchor.

### Cruise speed choice

`CruiseSpeed` stores a mode, not a speed: `Preset` (fractions 0.001, 0.01, 0.1 of c, default 0.01),
`Custom` (the slider, 1e-5 c to 0.9 c) or `Max`. MAX is shown only for a vessel without an antimatter
drive and with a delta-v reading (or while it is already selected), and it keeps following the
budget during a burn. When the budget
cannot pay even the cheapest trip, MAX resolves to the cheapest speed (held at least at the slider
floor of about 3 km/s) and the panel says "MAX: THIS VESSEL CANNOT PAY FOR THE CHEAPEST TRIP". A
vessel that loses MAX (a drive appears) falls back to `Custom` at the speed it last had.[^dvm-cruisespeed][^dvm-interstellarsection]

### Departure park and vessels in transit

`InterstellarLegs.DeparturePark` uses the vessel's own orbit when `VesselParksAt` holds (the
vessel orbits the departure body with its periapsis above the surface, open orbits included): the
burn is at its periapsis with `PeriapsisSpeed(mu, pe, e)`, and around a system root its plane fixes
an `AimSine`. Otherwise the body's ladder low orbit, circular. A vessel coasting on an open orbit
around a system root (`InTransit`) has no departure: `TryTransitState` reads the excess
`sqrt(mu (e - 1) / pe)`, whether it still falls inward (`dot(r, v) < 0`) and the outgoing asymptote
direction; a capture in that system is priced only while inbound, and a trip on to another system
adds `CourseChangeBurn = vInf * sqrt(2 - 2 cos(angle))`.[^dvm-interstellarlegs]

### Fuel

`InterstellarFuel.TryReadVessel` calls `InterstellarDrive.TryGetFrom(vehicle, out drive)`, which
scans active `EngineController`s for an `AntimatterRamCore`, sums `JetPower`, derives
`RatedThrust = 2 * JetPower / CarriedExhaustVelocity` and adds the capture area of every scoop that
is on.[^ksa-interstellardrive] The mod then sums the antimatter and hydrogen aboard and the free tank
room with the same loops as the planner's private `TransferPlanner.MassAboard` and `TankSpace`
(`ContainerVolume / GetStoredVolume(1f) - Mass`, in float).[^ksa-transferplanner][^ksa-mole]

`InterstellarFuel.Plan` is a line-for-line copy of the private `TransferPlanner.PlanInterstellarFuel`:
a speed-up `Burn` from 0 to the cruise speed over at most half the distance, a brake `Burn` on the
hydrogen left, `CoastCollection` of the scoop over the coast, and a second brake with the scooped
hydrogen when any was collected.[^dvm-interstellarfuel][^ksa-interstellarburnplan] The antimatter
split defaults to 30% per burn, as in stock. Rows show burn time, end reason
(`InterstellarBurnEnd`: target speed, antimatter, hydrogen, halfway, vessel burned away), speed
after, coast time, hydrogen scooped, time of flight and antimatter and hydrogen needed against
aboard.

Without a drive, `RocketPropellant.Needed(StagedDv.Stages, total)` walks the staged sequences in
firing order. `BurnedShare` uses the rocket equation within a sequence,
`(1 - q^(dv / DeltaV)) / (1 - q)` with `q = (wet - burned) / wet`, which is the same rule as the
planner's private `TransferPlanner.BurnedShare`. Past the last stage it reports the extra
propellant the last stage alone would need (`dry * exp((DeltaV + extra) / ve) - wet`, a lower
bound) and, when that overflows a double, the base-10 mass ratio.[^dvm-rocketpropellant]

### Section caching

`InterstellarSection` never re-lays out the map or re-routes. Each frame `Update` brings it current
with keyed caches:[^dvm-interstellarsection]

| Cache | Key | Recomputed when |
|---|---|---|
| Leg geometry (`InterstellarLeg`) | graph, departure Id, target Id, periapsis (hub target), arrival flag | route or periapsis changes |
| Cheapest speed and leg minimum | leg, parking orbit rounded to 1 km, 1 m/s and 1e-6 of the sine | the park moves |
| Fastest cruise | leg, park, budget | budget or park changes |
| Figures and strings (`ViewKey`) | vessel state, mode, speed, scale, drive version, antimatter split, rest, split, limits version | any of those |
| Drive fuel plan | `DriveFuelInputs` record equality | any drive input changes |

The vessel state is latched at most every 0.25 s (the staged readout's rate) unless its shape
changed, and the drive is re-read at most every 0.5 s. While the periapsis slider is dragged the
searches keep their last result and rerun on release. A leg or view that throws is latched as
failed for its key, so it shows a status instead of throwing every frame.

## Lifecycle and timing

All of it runs on the main thread inside the `[StarMapAfterGui]` draw (`frame:draw-ui`) while the
map window is open and an interstellar route is selected:

- `RouteAccumulator.AccumulateInterstellar` runs with the rest of the route, event-driven
  (click, toggle), and produces the `InterstellarRoute` (departure and target bodies, `RestDv`,
  `CaptureSplitDv`, `FromVessel`, `InTransit`).[^dvm-routeaccumulator]
- `MapWindow.DrawPanel` calls `InterstellarSection.Update` every frame before drawing the panel;
  it returns true when the vessel parked at or left the departure body, and the window then
  re-accumulates the route once.[^dvm-mapwindow] The control widgets call `Evaluate` again in the
  same frame on a change, so totals follow immediately.
- Vessel reads (`Program.ControlledVehicle`, its `Orbit`, `GetPositionEcl`, `Props.TotalMass`,
  `Parts.Moles.ModulesAndStates`, `Parts.Modules`) see main-thread state applied in
  `frame:apply-solvers`. The next vehicle step is already running (`frame:dispatch-solvers`
  precedes the draw) and writes into its own `VehicleUpdateState.UpdateData`, so the mod only
  reads values applied at the start of this frame and writes nothing.
- `InterstellarBurnPlan.Burn` integrates a few thousand steps; it runs only when `DriveFuelInputs`
  change, which is the same caching the stock planner uses (`_interstellarFuelInputs != inputs`).[^ksa-transferplanner]

## Key code

| Path | Role |
|---|---|
| [DeltaVMap/Dv/InterstellarMath.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Dv/InterstellarMath.cs#L55-L453) | Pure kernel: chain fold, plane-limited speed, capture correction, transit helpers; `InterstellarLeg` searches at L476-L820 |
| [DeltaVMap/Model/InterstellarLegs.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Model/InterstellarLegs.cs#L22-L269) | Builds chains, aim, distance, departure park, transit state from game bodies |
| [DeltaVMap/Dv/CruiseSpeed.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Dv/CruiseSpeed.cs#L21-L76) | Preset, Custom, MAX modes |
| [DeltaVMap/Dv/InterstellarFuel.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Dv/InterstellarFuel.cs#L53-L112) | Feeds the game's burn integrator; vessel drive and propellant read |
| [DeltaVMap/Dv/RocketPropellant.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Dv/RocketPropellant.cs#L51-L122) | Rocket-equation fallback over staged sequences |
| [DeltaVMap/Render/InterstellarSection.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/InterstellarSection.cs#L231-L369) | Per-frame evaluate with caches; controls, readouts, fuel rows, stock planner button (L463-L481) |
| [DeltaVMap/Route/RouteAccumulator.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Route/RouteAccumulator.cs#L252-L318) | Folds the Interstellar and Approach steps into one leg |
| [DeltaVMap/Dv/OrbitalStates.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Dv/OrbitalStates.cs#L226-L239) | `HubParkingRadiusFor`, shared by the hub ladder and the capture[^dvm-orbitalstates] |

## Snippets

```csharp
// DeltaVMap/Dv/InterstellarMath.cs L82-L107, L198-L207
    public static double SpeedForExcess(double mu, double radius, double vInf)
    {
        return Math.Sqrt(vInf * vInf + 2.0 * mu / radius);
    }

    // The size of v - u for a vessel velocity v of size vNeeded and a body velocity u of size
    // bodySpeed, where along is the component of v along u.
    public static double RelativeExcess(double vNeeded, double bodySpeed, double along)
    {
        double sq = vNeeded * vNeeded + bodySpeed * bodySpeed - 2.0 * bodySpeed * along;
        return Math.Sqrt(Math.Max(0.0, sq));
    }

    public static double ExcessAtBody(double outerExcess, in WellStep step)
    {
        return ExcessAtBody(outerExcess, in step, AlongSamples, AlongRefineSteps);
    }

    private static double ExcessAtBody(double outerExcess, in WellStep step, int samples, int refineSteps)
    {
        double needed = SpeedForExcess(step.OuterMu, step.Radius, outerExcess);
        double along = step.AimSine is double aimSine && aimSine != 0.0
            ? BestSpeedAlongMotion(step.OuterMu, step.Radius, outerExcess, aimSine, samples, refineSteps)
            : needed;
        return RelativeExcess(needed, step.BodySpeed, along);
    }
    // ...
    public static double ChainBurn(double vInf, ReadOnlySpan<WellStep> stepsOuterToInner, in ParkingOrbit park)
    {
        if (stepsOuterToInner.IsEmpty && park.AimSine.HasValue)
            return ExcessAtBody(vInf, new WellStep(park.Mu, park.Radius, park.Speed, park.AimSine));

        double excess = vInf;
        for (int i = 0; i < stepsOuterToInner.Length; i++)
            excess = ExcessAtBody(excess, in stepsOuterToInner[i]);
        return Math.Max(0.0, SpeedForExcess(park.Mu, park.Radius, excess) - park.Speed);
    }
```

```csharp
// DeltaVMap/Model/InterstellarLegs.cs L182-L212
    private static WellStep[] Chain(PhysicalNode body, double3 aimUnit, bool arrival)
    {
        var levels = new List<PhysicalNode>();
        for (PhysicalNode? n = body; n != null && n.Parent != null; n = n.Parent)
            levels.Add(n);

        var steps = new WellStep[levels.Count];
        double3 outerNormal = double3.Zero;
        for (int i = 0; i < levels.Count; i++)
        {
            PhysicalNode level = levels[levels.Count - 1 - i];
            Orbit orbit = ((IOrbiter)level.Astro).Orbit;
            double radius = OrbitalStates.TransferRadius(orbit);
            double? aimSine = null;
            bool sharesOuterPlane = false;
            if (level.Parent!.IsSystemRoot || arrival)
            {
                double3 normal = level.Parent!.IsSystemRoot
                    ? orbit.GetOrbitNormalCci()
                    : Rotate(orbit.GetOrbitNormalCci(), orbit.Parent.GetCci2Cce());
                aimSine = AimSine(aimUnit, normal);
                if (arrival)
                {
                    sharesOuterPlane = i > 0 && SamePlane(normal, outerNormal);
                    outerNormal = normal;
                }
            }
            steps[i] = new WellStep(level.Parent!.Body.Mu, radius, BodySpeed(orbit, radius), aimSine, sharesOuterPlane);
        }
        return steps;
    }
```

```csharp
// DeltaVMap/Dv/InterstellarFuel.cs L55-L76
    public static DriveFuelPlan Plan(in DriveFuelInputs inputs)
    {
        InterstellarDrive drive = inputs.Drive;
        InterstellarBurnEstimate departure = InterstellarBurnPlan.Burn(in drive, inputs.Mass, 0.0, inputs.CruiseSpeed,
            inputs.DepartureAntimatter, inputs.HydrogenAboard, 0.5 * inputs.Distance, inputs.GasDensity);
        double hydrogenLeft = Math.Max(0.0, inputs.HydrogenAboard - departure.CarriedHydrogen);
        double brakeDistance = inputs.Distance - departure.Distance;
        InterstellarBurnEstimate brake = InterstellarBurnPlan.Burn(in drive, departure.EndMass, departure.EndSpeed, inputs.FinalSpeed,
            inputs.BrakeAntimatter, hydrogenLeft, brakeDistance, inputs.GasDensity);

        double coast = departure.EndSpeed > 0.0
            ? Math.Max(0.0, inputs.Distance - departure.Distance - brake.Distance) / departure.EndSpeed
            : 0.0;
        double scooped = InterstellarBurnPlan.CoastCollection(in drive, inputs.GasDensity, departure.EndSpeed, coast,
            inputs.HydrogenSpace + departure.CarriedHydrogen);
        if (scooped > 0.0)
        {
            brake = InterstellarBurnPlan.Burn(in drive, departure.EndMass + scooped, departure.EndSpeed, inputs.FinalSpeed,
                inputs.BrakeAntimatter, hydrogenLeft + scooped, brakeDistance, inputs.GasDensity);
        }
        return new DriveFuelPlan(departure, brake, coast, scooped);
    }
```

```csharp
// DeltaVMap/Render/InterstellarSection.cs L601-L602, L606-L627
    private void EnsureLimits(InterstellarLeg leg, in ParkingOrbit park, in VesselState state, double restDv)
    {
        // ...
        var cheapestKey = new CheapestKey(state.Leg, state.KeyPark);
        if (_cheapestKey != cheapestKey)
        {
            _cheapestKey = null;
            _cruiseKey = null;
            _cheapestSpeed = leg.CheapestSpeed(in park, SpeedCap);
            _cheapestLegDv = leg.TotalDv(_cheapestSpeed, in park);
            _cheapestKey = cheapestKey;
            _limitsVersion++;
        }

        double? budget = !HasDrive && state.Available is double available
            ? InterstellarMath.LegBudget(available, _dvScale, restDv)
            : null;
        var cruiseKey = new CruiseKey(state.Leg, state.KeyPark, budget);
        if (_cruiseKey == cruiseKey)
            return;
        _cruiseKey = null;
        _maxSpeed = budget is double b ? leg.MaxCruiseSpeed(b, in park, SpeedCap, _cheapestSpeed) : 0.0;
        _cruiseKey = cruiseKey;
        _limitsVersion++;
    }
```

## Game APIs touched

| API | Decompiled source | Used for |
|---|---|---|
| `InterstellarBurnPlan.Burn(in InterstellarDrive, double mass, double startSpeed, double targetSpeed, double antimatterBudget, double hydrogenBudget, double distanceBudget, double gasDensity)` | [current/decomp/KSA/InterstellarBurnPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InterstellarBurnPlan.cs) | Speed-up and brake burns; fixed step `abs(target - start) * mass / RatedThrust / 2000`, at most 100,000 steps[^ksa-interstellarburnplan] |
| `InterstellarBurnPlan.CoastCollection(in InterstellarDrive, double gasDensity, double speed, double seconds, double tankSpace)` | same | Hydrogen scooped during the coast, clamped to tank room |
| `InterstellarDrive.TryGetFrom(Vehicle, out InterstellarDrive)` and its record fields | [current/decomp/KSA/InterstellarDrive.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InterstellarDrive.cs) | Drive detection from active engines; `Antimatter` and `ReactionMass` phase names[^ksa-interstellardrive] |
| `InterstellarBurnEstimate` (`Seconds`, `EndSpeed`, `Distance`, `EndMass`, `Antimatter`, `CarriedHydrogen`, `End`) | [current/decomp/KSA/InterstellarBurnEstimate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InterstellarBurnEstimate.cs) | Burn results shown in the fuel rows[^ksa-interstellarburnestimate] |
| `TransferPlanner.PlanInterstellarFuel`, `MassAboard`, `TankSpace`, `BurnedShare` (private, mirrored not called) | [current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs) | Reference the mod copies so its readout equals stock for the same inputs[^ksa-transferplanner] |
| `OrbitalTransfers.MAX_INTERSTELLAR_SPEED`; `DepartureSpeedForExcess`, `SolveInterstellar`, `TargetInterstellarPeriapsis` (mirrored) | [current/decomp/KSA/OrbitalTransfers.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitalTransfers.cs) | Speed cap; the Newtonian model the kernel follows[^ksa-orbitaltransfers] |
| `CelestialSystem.InterstellarGasDensity` | [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs) | Scoop and ram-drive input[^ksa-celestialsystem] |
| `Orbit.GetOrbitNormalCci`, `GetPositionCce`, `GetVelocityCce`, `Mu`, `Periapsis`, `Eccentricity`; `IParentBody.GetCci2Cce` | [current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs) | Aim sines, transit excess and asymptote, body speeds[^ksa-orbit] |
| `Mole.SubstancePhase`, `ContainerVolume`, `GetStoredVolume` | [current/decomp/KSA/Mole.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mole.cs) | Propellant aboard and tank room[^ksa-mole] |
| `ConsoleWidgets.Segmented`, `RegionHeader`, `Readout`, `Button` | [current/decomp/KSA/ConsoleWidgets.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleWidgets.cs) | Stock planner look for presets, readouts and the button[^ksa-consolewidgets] |

## Pitfalls and version notes

- Final speed differs from stock by design. For a star or barycenter target the mod brakes to the
  circular speed at the capture periapsis and for a planet or moon to rest (then lists the chain
  capture as an impulsive burn). The `BuildDriveRows` comment says this is "as stock does", but
  stock brakes to its own FINAL SPEED slider (`_finalSpeedKmS`, default `sqrt(200)` km/s, about
  14.1 km/s) and only shows the circular speed at periapsis as a readout. The fuel figures
  therefore match stock only when the stock slider is set to the same final speed.
- The departure chain uses the free best case below the top level and takes no coast correction,
  so a slow trip out of a tilted plane is an upper bound on the departure and a lower bound on
  deeper levels (both stated in the code comments).
- "Best time of year" means the departure point and the arrival plane correction are optimized;
  the panel says so ("NEWTONIAN, BEST TIME OF YEAR"). Actual timing is left to the stock planner.
- The section mirrors private stock code (`PlanInterstellarFuel`, `MassAboard`, `TankSpace`,
  `BurnedShare`); a game update that changes those methods will not break compilation but will make
  the readouts drift. Re-diff them on each update.
- `InterstellarDrive.TryGetFrom` skips inactive engines, so an unstaged antimatter engine reads as
  "no drive" and the rocket-equation fallback is shown.
- Checked against build 2026.10.10.5554: every public game API above exists with the signature shown.

## See also

- [Route finding and accumulation](./route-finding-and-accumulation.md)
- [Closed-form patched-conic transfer model](./patched-conic-transfer-model.md)
- [Vehicle staged delta-v readout](./vehicle-staged-dv-readout.md)
- [Stock transfer planner bridge](./stock-transfer-planner-bridge.md)
- [Multi-system scene composition](./multi-system-scene-composition.md)
- [Offline layout and math tests](./offline-layout-and-math-tests.md)
- [Panel rows built on the game's ConsoleWidgets kit](./stock-console-widget-panels.md)
- [KSA-DeltaVMap overview](../overview.md)
- [Price an interstellar leg by reusing the stock burn model, with a golden-section speed search and a rocket-equation fallback](../../../howto/flight-and-orbits/transfers/plan-multi-leg-and-interstellar-delta-v-routes/plan-multi-leg-and-interstellar-delta-v-routes-3.md) - how-to article demonstrating this feature

[^dvm-interstellarmath]: DeltaVMap/Dv/InterstellarMath.cs
[^dvm-interstellarlegs]: DeltaVMap/Model/InterstellarLegs.cs
[^dvm-cruisespeed]: DeltaVMap/Dv/CruiseSpeed.cs
[^dvm-interstellarfuel]: DeltaVMap/Dv/InterstellarFuel.cs
[^dvm-rocketpropellant]: DeltaVMap/Dv/RocketPropellant.cs
[^dvm-interstellarsection]: DeltaVMap/Render/InterstellarSection.cs
[^dvm-routeaccumulator]: DeltaVMap/Route/RouteAccumulator.cs
[^dvm-orbitalstates]: DeltaVMap/Dv/OrbitalStates.cs
[^dvm-mapwindow]: DeltaVMap/Render/MapWindow.cs
[^dvm-tests]: DeltaVMap.Tests/Program.cs
[^ksa-interstellarburnplan]: KSA.InterstellarBurnPlan (decompiled, build 2026.10.10.5554)
[^ksa-interstellardrive]: KSA.InterstellarDrive (decompiled, build 2026.10.10.5554)
[^ksa-interstellarburnestimate]: KSA.InterstellarBurnEstimate (decompiled, build 2026.10.10.5554)
[^ksa-transferplanner]: KSA.TransferPlanner (decompiled, build 2026.10.10.5554)
[^ksa-orbitaltransfers]: KSA.OrbitalTransfers (decompiled, build 2026.10.10.5554)
[^ksa-celestialsystem]: KSA.CelestialSystem (decompiled, build 2026.10.10.5554)
[^ksa-orbit]: KSA.Orbit (decompiled, build 2026.10.10.5554)
[^ksa-mole]: KSA.Mole (decompiled, build 2026.10.10.5554)
[^ksa-consolewidgets]: KSA.ConsoleWidgets (decompiled, build 2026.10.10.5554)
