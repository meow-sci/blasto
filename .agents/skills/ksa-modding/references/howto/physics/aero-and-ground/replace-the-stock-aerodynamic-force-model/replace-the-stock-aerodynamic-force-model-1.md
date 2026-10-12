---
type: How-To
title: Call-site transpiler on ComputeDerivatives writing the Disturbances accumulator
description: Replace KSA's atmospheric drag on every vehicle with your own lift and drag by swapping the operand of one ComputeDrag call for a same-signature method, with Mach computed from the game atmosphere
category: physics
group: aero-and-ground
topic: replace-the-stock-aerodynamic-force-model
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/ksaero/features/body-of-revolution-aerodynamics
  - mods/ksaero/features/computedrag-call-site-transpiler
  - mods/ksaero/features/stock-drag-delegate-fallback
  - mods/ksaero/features/mach-from-game-atmosphere
techniques: [harmony-transpiler, custom-physics]
hooks:
  - starmap StarMapAllModsLoaded
  - harmony-transpiler KSA.PhysicsStates.ComputeDerivatives
  - starmap StarMapAfterGui
  - starmap StarMapUnload
lifecycle: [startup:all-mods-loaded, physics:vehicle-solver, frame:draw-ui]
game_types:
  - KSA.PhysicsStates
  - KSA.PhysicsEnvironment
  - KSA.VehicleProperties
  - KSA.BoundingBoxCdA
  - KSA.PhysicalAtmosphereReference
  - KSA.ReadOnlyPhysicsStates
tags: [physics, aerodynamics, drag, lift, mach, speed-of-sound, harmony, transpiler]
---

Outcome: every vehicle in an atmosphere gets your lift and drag instead of KSA's box drag, with gravity, thrust,
buoyancy, parachutes and the ocean drag left exactly as the game computes them. Prerequisites: StarMap, Harmony
(`Lib.Harmony` 2.4.2 and `StarMap.API` 0.3.6 in the demonstrating mod's csproj[^ksaero-csproj]) and a working mod
skeleton (see the getting-started topics). Demonstrated by KSAero, whose whole mod is five source files: the model in
[KSAero/AeroModel.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AeroModel.cs), the patch in
[KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs).

The technique is a general "replace one helper call inside a big game method" pattern; the
[transpiler topic](../../../patching/transpilers/swap-one-call-inside-a-game-method-with-a-transpiler/overview.md) compares it
with other shapes. This page is the aerodynamics case.

## Background: how the game does it

`PhysicsStates.ComputeDerivatives` has three overloads: a static one with ten parameters that does the work, and two
instance forwarders (on `PhysicsStates` and `ReadOnlyPhysicsStates`) that pass their `Origin`, `Kinematic`, `Props` and
`Environment` to it.[^ksa-physicsstates][^ksa-readonlyphysicsstates] Inside the static one, drag is two calls to the same
private helper, selected by which density is passed:

```csharp
// current/decomp/KSA/PhysicsStates.cs L1002-L1017
				if (environment.OceanVolume > 0.0 || environment.OceanSurfaceArea > 0.0)
				{
					ComputeBuoyancy(in kinematic, ref disturbances2, gravitationBub, environment.OceanDensity, environment.OceanVolume);
					ComputeDrag(in origin, in kinematic, in props, in environment, ref disturbances2, double8, environment.OceanDensity, num6, environment.OceanSurfaceArea, dt);
				}
				if (environment.AtmosphericDensity > 0f && num6 > 0.0)
				{
					double num7 = Math.Max(0.0, (double)props.TotalVolume - environment.OceanVolume);
					double num8 = Math.Max(0.0, (double)props.TotalSurfaceArea - environment.OceanSurfaceArea);
					if (num8 > 0.0 || num7 > 0.0)
					{
						ComputeDrag(in origin, in kinematic, in props, in environment, ref disturbances2, double8, environment.AtmosphericDensity, num6, num8, dt);
						ComputeBuoyancy(in kinematic, ref disturbances2, gravitationBub, environment.AtmosphericDensity, num7);
					}
				}
```

`ComputeDrag` is the model to replace:

```csharp
// current/decomp/KSA/PhysicsStates.cs L1089-L1111
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static void ComputeDrag(in BubbleOrigin origin, in KinematicStates kinematic, in VehicleProperties props, in PhysicsEnvironment environment, ref Disturbances disturbances, double3 airVelocityBub, double density, double dragCdA, double surfaceArea, double dt)
	{
		double num = airVelocityBub.LengthSquared();
		double num2 = 0.5 * density * num;
		if (!num2.IsNearlyZero())
		{
			double num3 = Math.Sqrt(num);
			double3 double5 = -airVelocityBub / num3;
			double num4 = (dragCdA + 0.1 * surfaceArea) * num2;
			double num5 = props.TotalMass;
			double num6 = num4 * dt / (num5 * num3);
			num4 /= 1.0 + num6;
			double3 value = num4 * double5;
			double3 forceBody = value.Transform(kinematic.Body2Phys.Inverse());
			disturbances.AddForceBody(double3.Zero, forceBody);
			double3 bodyRates = GetBodyRates(in origin, in kinematic, in environment);
			double num7 = bodyRates.Length() * (double)props.ComputeBoundingSphereRadiusAsmb();
			double num8 = 0.5 * density * num7 * num7;
			double3 torqueBody = 0.1 * surfaceArea * num8 * -bodyRates.NormalizeOrZero();
			disturbances.AddPureTorqueBody(torqueBody);
		}
	}
```

Three facts decide the design:

- **The helper is `AggressiveInlining`.** A Harmony patch on `ComputeDrag` itself is skipped wherever the JIT has already
  inlined the body into `ComputeDerivatives`. Patch the caller and change what it calls.
- **The stock gate stays.** `ComputeDerivatives` only reaches the call when `AtmosphericDensity > 0f` and the box model
  gives `num6 > 0.0`; `VehicleProperties.RecomputeAerodynamicProperties` still fills the box CdA, and the replacement
  inherits that gate.[^ksa-vehicleproperties]
- **The force is a body-axis accumulator.** `Disturbances.AddForceBody(location, force)` adds to the force the integrator
  consumes; a zero location means "through the centre of mass", as stock does.

## Lifecycle and timing

- **Patch time: `startup:all-mods-loaded`.** KSAero applies the patch in its `[StarMapAllModsLoaded]` method. No vehicle
  has been stepped yet, so the method has not been jitted and replaced mid-flight.
- **Run time: `physics:vehicle-solver`.** The replacement executes on the vehicle solver's worker threads, per vehicle,
  once or twice per unconstrained substep and once per constrained one (see the physics-step section of the
  [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#the-physics-step)).
  `Program.PrepareFrame` dispatches the step through `Universe.ExecuteNextVehicleSolvers`, which first calls
  `Vehicle.PrepareWorker` per vehicle on the main thread[^ksa-universe]; the substeps then run while the main thread draws
  and renders. Nothing in the replacement may touch ImGui, Vulkan or main-thread state, must not allocate on the hot path
  and must not throw: a throw marks the bubble failed and the vehicles do not advance.
- **The environment is already filled.** `PhysicsStates.RecomputePositionalEnvironmentValues` refreshes
  `PhysicsEnvironment.AtmosphericPressure` and `AtmosphericDensity` for the vehicle's current altitude before the integrators
  evaluate `ComputeDerivatives` (for example [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L1174-L1175)),
  so the replacement reads them from the `in PhysicsEnvironment` argument and needs no atmosphere lookup of its own.
- **The readout runs in `frame:draw-ui`** (`StarMapAfterGui`) on the main thread and reads the settled state.

## Steps

1. **Create the mod and the model.** Reference `KSA.dll`, `Brutal.Core.Numerics`, `Brutal.Core.Logging`, `Brutal.ImGui` (for a
   readout), `BepuPhysics` and `BepuUtilities` as `Private=false`, add `Lib.Harmony` and `StarMap.API`, and put a `mod.toml` with `[StarMap] EntryAssembly`
   in the output. The model itself is plain math over doubles: KSAero turns the bounding box into a cylinder along
   assembly +X of length `dx` and diameter `sqrt(dy dz)`, which keeps stock's frontal area and so stock's nose-on drag, and
   evaluates Allen and Perkins' crossflow coefficients for lift and drag at the angle of attack
   ([KSAero/AeroModel.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AeroModel.cs#L7-L43)).[^ksaero-aeromodel]
   Any model works as long as it returns a body-axis force per unit `q * A_ref`.

2. **Resolve both methods exactly.** `ComputeDerivatives` exists three times and `ComputeDrag` is private, so pass the full
   parameter list with `MakeByRefType()` for every `in` and `ref` parameter. Resolve the density field the same way:

```csharp
// KSAero/AirForcePatch.cs L32-L48
    private static readonly MethodInfo? ComputeDerivatives = AccessTools.Method(typeof(PhysicsStates), "ComputeDerivatives", new[]
    {
        typeof(BubbleOrigin).MakeByRefType(), typeof(KinematicStates).MakeByRefType(),
        typeof(VehicleProperties).MakeByRefType(), typeof(PhysicsEnvironment).MakeByRefType(),
        typeof(double), typeof(double), typeof(double3), typeof(double3),
        typeof(ReadOnlySpan<ActiveNozzle>), typeof(ReadOnlySpan<ActiveChute>),
    });

    private static readonly MethodInfo? ComputeDrag = AccessTools.Method(typeof(PhysicsStates), "ComputeDrag", new[]
    {
        typeof(BubbleOrigin).MakeByRefType(), typeof(KinematicStates).MakeByRefType(),
        typeof(VehicleProperties).MakeByRefType(), typeof(PhysicsEnvironment).MakeByRefType(),
        typeof(Disturbances).MakeByRefType(), typeof(double3),
        typeof(double), typeof(double), typeof(double), typeof(double),
    });

    private static readonly FieldInfo? AtmosphericDensity = AccessTools.Field(typeof(PhysicsEnvironment), nameof(PhysicsEnvironment.AtmosphericDensity));
```

   The static ten-parameter overload has `ReadOnlySpan<ActiveNozzle>` and `ReadOnlySpan<ActiveChute>` as its last two
   parameters; the instance forwarders reach it, so one patch covers all callers.

3. **Keep a delegate to the stock drag.** Bind a delegate with the identical signature so the replacement can fall back for
   a vehicle it cannot model (a box with no extent, for example). `Apply` also refuses early when any member is missing:

```csharp
// KSAero/AirForcePatch.cs L28-L30, L50-L59
    private delegate void ComputeDragFn(in BubbleOrigin origin, in KinematicStates kinematic, in VehicleProperties props,
        in PhysicsEnvironment environment, ref Disturbances disturbances, double3 airVelocityBub, double density,
        double dragCdA, double surfaceArea, double dt);

    // ... (the three AccessTools lookups from step 2)

    private static ComputeDragFn? _stockDrag;

    internal static void Apply(Harmony harmony)
    {
        if (ComputeDerivatives == null || ComputeDrag == null || AtmosphericDensity == null)
            throw new MissingMemberException("PhysicsStates.ComputeDerivatives, PhysicsStates.ComputeDrag or PhysicsEnvironment.AtmosphericDensity not found.");

        _stockDrag = AccessTools.MethodDelegate<ComputeDragFn>(ComputeDrag);
        harmony.Patch(ComputeDerivatives, transpiler: new HarmonyMethod(typeof(AirForcePatch), nameof(Transpiler)));
    }
```

4. **Write the transpiler: change one operand, refuse otherwise.** Find `call ComputeDrag` instructions and keep only the
   one whose preceding arguments load `PhysicsEnvironment.AtmosphericDensity`. The density load sits five instructions
   before the call; the window of 12 just has to reach it without running into the previous call's arguments, and stops at
   the first `call` it meets going backwards. That is what separates the atmospheric call from the ocean one, which loads
   `OceanDensity`:

```csharp
// KSAero/AirForcePatch.cs L61-L93
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> code = instructions.ToList();
        MethodInfo replacement = AccessTools.Method(typeof(AirForcePatch), nameof(ComputeAirForces));
        int replaced = 0;

        for (int i = 0; i < code.Count; i++)
        {
            if (code[i].Calls(ComputeDrag!) && PassesAtmosphericDensity(code, i))
            {
                code[i].operand = replacement;
                replaced++;
            }
        }

        // Anything else means the method has changed shape, and stock aero is the safe answer.
        if (replaced != 1)
            throw new InvalidOperationException($"Expected one atmospheric ComputeDrag call in ComputeDerivatives, found {replaced}.");

        return code;
    }

    private static bool PassesAtmosphericDensity(List<CodeInstruction> code, int callIndex)
    {
        for (int j = callIndex - 1; j >= Math.Max(0, callIndex - ArgumentWindow); j--)
        {
            if (code[j].LoadsField(AtmosphericDensity!))
                return true;
            if (code[j].opcode == OpCodes.Call || code[j].opcode == OpCodes.Callvirt)
                return false;
        }
        return false;
    }
```

   Because `ComputeAirForces` has the same signature as `ComputeDrag`, the operand swap needs no stack changes.

5. **Write the replacement with the stock signature.** Compute the airspeed direction in body axes, Mach from the game
   atmosphere (next section), the coefficients, and add one force through the centre of mass. KSAero reproduces stock's
   semi-implicit limiter on the whole force, so one substep can never reverse the airspeed, keeps stock's rotational damping
   torque, and scales by the fraction of the surface still out of the water:

```csharp
// KSAero/AirForcePatch.cs L99-L133
    internal static void ComputeAirForces(in BubbleOrigin origin, in KinematicStates kinematic, in VehicleProperties props,
        in PhysicsEnvironment environment, ref Disturbances disturbances, double3 airVelocityBub, double density,
        double dragCdA, double surfaceArea, double dt)
    {
        AeroBody body = BodyFor(in props);
        if (!body.IsValid)
        {
            _stockDrag!(in origin, in kinematic, in props, in environment, ref disturbances, airVelocityBub, density, dragCdA, surfaceArea, dt);
            return;
        }

        double speedSquared = airVelocityBub.LengthSquared();
        double q = 0.5 * density * speedSquared;
        if (q.IsNearlyZero())
            return;

        double speed = Math.Sqrt(speedSquared);
        double3 dirBody = (airVelocityBub / speed).Transform(kinematic.Body2Phys.Inverse());
        double mach = speed / Math.Max(AeroModel.SpeedOfSound(environment.AtmosphericPressure, density), 1e-9);
        AeroCoefficients c = AeroModel.Evaluate(in body, dirBody.X, dirBody.Y, dirBody.Z, mach);

        // Stock passes the surface area still out of the water, so a half-submerged vehicle feels half the air.
        double exposed = props.TotalSurfaceArea > 0f ? Math.Clamp(surfaceArea / props.TotalSurfaceArea, 0.0, 1.0) : 1.0;
        double3 force = q * body.ReferenceArea * exposed * new double3(c.ForceX, c.ForceY, c.ForceZ);

        // Stock's semi-implicit limiter, applied to the whole force: one substep can never reverse the airspeed.
        double mass = props.TotalMass;
        force *= 1.0 / (1.0 + force.Length() * dt / (mass * speed));
        disturbances.AddForceBody(double3.Zero, force);

        double3 bodyRates = PhysicsStates.GetBodyRates(in origin, in kinematic, in environment);
        double rimSpeed = bodyRates.Length() * props.ComputeBoundingSphereRadiusAsmb();
        double rotationalQ = 0.5 * density * rimSpeed * rimSpeed;
        disturbances.AddPureTorqueBody(StockSkinDragCoefficient * surfaceArea * rotationalQ * -bodyRates.NormalizeOrZero());
    }
```

6. **Install on `StarMapAllModsLoaded`, and roll back on any failure.** The patch is all-or-nothing: if the transpiler
   throws, Harmony unpatches the id and stock aerodynamics keep running. The version string check is a warning, not a gate:

```csharp
// KSAero/Mod.cs L16-L36
    [StarMapAllModsLoaded]
    public void OnFullyLoaded()
    {
        string gameVersion = VersionInfo.Current.VersionString;
        if (gameVersion != TestedGameVersion)
            DefaultCategory.Log.Warning($"[KSAero] Tested against {TestedGameVersion}, current is {gameVersion}.");

        var harmony = new Harmony(HarmonyId);
        try
        {
            AirForcePatch.Apply(harmony);
            _harmony = harmony;
            DefaultCategory.Log.Info("[KSAero] Loaded. Atmospheric forces now use the body-of-revolution model.");
        }
        catch (Exception ex)
        {
            // The transpiler refuses rather than guesses, so a failure here leaves stock aero running.
            harmony.UnpatchAll(HarmonyId);
            DefaultCategory.Log.Warning($"[KSAero] Patching failed, stock aerodynamics stay in place: {ex}");
        }
    }
```

   Expect `Loaded. Atmospheric forces now use the body-of-revolution model.` in the game log on success, and the
   `Patching failed` warning with the match count on a game build where the call shape changed.

## Mach number and the speed of sound

The game has no Mach number, so a model that wants one derives it. KSA's atmosphere is an isothermal exponential:
`PhysicalAtmosphereReference` multiplies both `SeaLevelPressure` and `SeaLevelDensity` by the same `exp(-h / ScaleHeight)`, so
`p / rho` is the same at every altitude of a body.[^ksa-physicalatmospherereference] The speed of sound `sqrt(gamma p / rho)`
is therefore a per-body constant: `sqrt(1.4 * 101325 / 1.225)`, about 340 m/s on KSA's Earth. KSAero computes it from
the values already in the environment:

```csharp
// KSAero/AeroModel.cs L152-L153
    public static double SpeedOfSound(double pressure, double density)
        => pressure > 0.0 && density > 0.0 ? Math.Sqrt(AeroParameters.Gamma * pressure / density) : 0.0;
```

- **Inside the solver (worker thread).** Pass `environment.AtmosphericPressure` and the `density` argument, as step 5
  does. Both are read from the `in PhysicsEnvironment` the call receives, so there is no lookup, no lock and no allocation.
  `PhysicsEnvironment` stores pressure and density as `float`, which is plenty for a ratio.[^ksa-physicsenvironment]
  `Gamma = 1.4` is the author's choice: the game models no composition, so it cannot be read from a body.
- **On the main thread.** KSAero's readout window reads the controlled vehicle's settled environment and the game's own
  airspeed function. Reading `Vehicle.PhysicsEnvironment` there is safe because the main-thread copy only changes inside
  `ApplyVehicleSolvers`:

```csharp
// KSAero/AeroReadout.cs L37-L51
        ref readonly PhysicsEnvironment environment = ref vehicle.PhysicsEnvironment;
        if (!environment.InPhysicsRadius || environment.AtmosphericDensity <= 0f)
            return;

        ref readonly VehicleProperties props = ref vehicle.Props;
        AeroBody body = AirForcePatch.BodyFor(in props);
        if (!body.IsValid)
            return;

        float3 air = PhysicsStates.ComputeAirVelocityBody(in vehicle.BubbleOrigin, in vehicle.KinematicStates, in environment);
        double speed = air.Length();
        double density = environment.AtmosphericDensity;
        double q = 0.5 * density * speed * speed;
        double soundSpeed = AeroModel.SpeedOfSound(environment.AtmosphericPressure, density);
        double mach = soundSpeed > 0.0 ? speed / soundSpeed : 0.0;
```

  The guards matter: above the atmosphere's boundary height `RecomputePositionalValues` sets both values to zero, and the
  helper returns `0` rather than dividing by zero. The worker-side call also floors the sound speed at `1e-9`.

## Complete example

| File | Role |
|---|---|
| [KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs) | Method resolution, call-site transpiler, `ComputeAirForces`, stock fallback |
| [KSAero/AeroModel.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AeroModel.cs) | Body of revolution, coefficients, `SpeedOfSound` |
| [KSAero/Mod.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/Mod.cs) | StarMap entry, version warning, rollback, unload |
| [KSAero/AeroReadout.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AeroReadout.cs) | ImGui readout of Mach, q, angle of attack, CL and CD for the controlled vehicle |
| [README.md](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/README.md) | What the model gives (50 m by 3.7 m launcher: stock Cd 7.38, KSAero 0.435 on frontal area) |

## Variations and gotchas

- **Patch the caller, never `ComputeDrag`.** The helper is `AggressiveInlining`; a prefix or postfix on it is lost where the
  JIT inlined it.
- **Count the matches.** Throwing when the match count is not exactly one turns a silent wrong-call rewrite on a changed
  game build into a visible, harmless fallback to stock. If another mod's transpiler has already changed the operand,
  `Calls(ComputeDrag)` finds nothing and this patch refuses too.
- **Parameter types are the contract.** The `AccessTools.Method` lookup needs the exact parameter list, including
  `ReadOnlySpan<ActiveNozzle>`; any change to the ten parameters on a game update makes `Apply` throw
  `MissingMemberException`, which the rollback catches.
- **The ocean call stays stock.** The match demands an `AtmosphericDensity` load, so the ocean `ComputeDrag` and both
  buoyancy calls still run the game's code; a half-submerged vehicle is handled by scaling with the exposed surface fraction.
- **The box model still gates the call.** `num6 > 0.0` (the stock CdA) decides whether `ComputeDrag` is reached, so
  `RecomputeAerodynamicProperties` must keep producing a positive CdA for your vehicle.
- **Mind unguarded exceptions.** `ComputeAirForces` falls back to stock for an invalid box, but a `NaN` from your own model
  would flow into the integrator; validate inputs the way `body.IsValid` does.
- **Mach is constant per body only while pressure and density share one exponent.** A mod that replaces the pressure and
  density functions independently (see the topic override-atmosphere-and-ocean-in-the-physics-environment) can make
  `p / rho` vary with altitude; the formula above then still gives the local `sqrt(gamma p / rho)`, which is what the
  environment supplies.
- **Tested-version drift.** `TestedGameVersion` is `v2026.10.7.5541` in the pinned KSAero, while the decompiled sources in
  this knowledge base are build 2026.10.10.5554. The decompiled static `ComputeDerivatives` of that build still has exactly
  one `ComputeDrag` call that loads `AtmosphericDensity` (the other loads `OceanDensity`), which is the shape the matcher
  needs; the version warning in the log is the only runtime signal beyond the refusal.

## See also

- [Overview and comparison](overview.md)
- [Speed of sound from the atmosphere reference for a measurement tool](replace-the-stock-aerodynamic-force-model-2.md): measure what this patch (or stock) produces
- Mod features: [Body-of-revolution aerodynamics](../../../../mods/ksaero/features/body-of-revolution-aerodynamics.md), [ComputeDrag call-site transpiler](../../../../mods/ksaero/features/computedrag-call-site-transpiler.md), [Stock drag delegate fallback](../../../../mods/ksaero/features/stock-drag-delegate-fallback.md), [Mach from the game atmosphere](../../../../mods/ksaero/features/mach-from-game-atmosphere.md)
- [Write worker-thread-safe solver patches](../../solver/write-worker-thread-safe-solver-patches/overview.md): approach 1 there is this patch seen as a thread-safety pattern
- [Swap one call inside a game method with a transpiler](../../../patching/transpilers/swap-one-call-inside-a-game-method-with-a-transpiler/overview.md)
- [Lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)

[^ksaero-csproj]: KSAero: KSAero/KSAero.csproj
[^ksa-physicsstates]: KSA.PhysicsStates (decompiled, build 2026.10.10.5554)
[^ksa-readonlyphysicsstates]: KSA.ReadOnlyPhysicsStates (decompiled, build 2026.10.10.5554)
[^ksa-vehicleproperties]: KSA.VehicleProperties (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksaero-aeromodel]: KSAero: KSAero/AeroModel.cs
[^ksa-physicalatmospherereference]: KSA.PhysicalAtmosphereReference (decompiled, build 2026.10.10.5554)
[^ksa-physicsenvironment]: KSA.PhysicsEnvironment (decompiled, build 2026.10.10.5554)
