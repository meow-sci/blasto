---
type: How-To
title: Pure, allocation-free, never-throwing code over the in arguments
description: Replace a call inside PhysicsStates.ComputeDerivatives with a function that touches only its arguments, never allocates and never throws, and falls back to the stock method when it cannot answer
category: physics
group: solver
topic: write-worker-thread-safe-solver-patches
approach: 1
difficulty: advanced
prerequisites: [harmony]
demonstrated_by:
  - mods/ksaero/features/body-of-revolution-aerodynamics
  - mods/ksaero/features/stock-drag-delegate-fallback
techniques: [harmony-transpiler, custom-physics]
hooks: [harmony-transpiler KSA.PhysicsStates.ComputeDerivatives]
lifecycle: [physics:vehicle-solver, frame:dispatch-solvers]
game_types: [KSA.PhysicsStates, KSA.PhysicsBubble, KSA.VehicleUpdateTask, KSA.Disturbances, KSA.VehicleProperties]
tags: [physics, worker-thread, transpiler, aerodynamics, pure-function, allocation-free, fallback]
---

Outcome: a replacement force that runs on every vehicle worker thread, every substep, with no shared state
and therefore nothing to synchronise. KSAero swaps the single `ComputeDrag` call site inside
`ComputeDerivatives` for its own function of the same signature. Prerequisite: Harmony. The rules below
apply to any patch whose body runs inside the step, whatever it computes.

## Background: how the game does it

`Program.PrepareFrame` calls `Universe.ExecuteNextVehicleSolvers`, which queues a `VehicleUpdateTask` on
`JobSystems.VehicleSolver`. The task's `Run` [current/decomp/KSA/VehicleUpdateTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateTask.cs#L261-L298) fans bubbles out over
`JobSystems.VehicleWorkerPool`; each bubble steps substeps; each substep calls
`PhysicsStates.ComputeDerivatives` [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L956-L1087), which calls the private
`ComputeDrag` [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L1089-L1111):

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

Because it is `AggressiveInlining`, patching `ComputeDrag` itself can be skipped where the JIT inlined it;
the call site inside `ComputeDerivatives` is the reliable target (see the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-4.md#steps)).[^ksa-physicsstates]

## Lifecycle and timing

The replacement runs in `physics:vehicle-solver`, never on the main thread:

| Step | Thread | Consequence for the patch |
|---|---|---|
| `ExecuteNextVehicleSolvers` queues the task | main | Nothing of yours runs here |
| `VehicleUpdateTask.Run` fans out bubbles | `VehicleSolver` runner, then `VehicleWorkerPool` workers | Different bubbles run in parallel; vehicles in one bubble run in parallel in the unconstrained step |
| `ComputeDerivatives` per substep | worker | Verlet calls it twice per substep, RK8 12 times per long step, the constrained path once; other mods also call it on probe copies |
| `PhysicsBubble.MarkFailed` on any exception | worker | The bubble's step is not applied that frame (`PublishResults` skips a failed bubble); the error repeats every frame |
| Next frame `ApplyVehicleSolvers` | main | Results copied back; whatever you read from `Vehicle` during the step was one step stale |

What that imposes:

| Rule | Why |
|---|---|
| Read only the call's `in` arguments, constants and immutable tables | The structs are the worker's own copy for this substep; a `Vehicle` or `Program.ControlledVehicle` read gives the stale main-thread copy |
| No allocation, LINQ, boxing, logging | Vehicles x substeps x 2 calls per frame |
| Never throw | An exception marks the bubble failed |
| No state between calls | Verlet evaluates twice per substep and the end state is evaluated again at the start of the next; no counters, no "current vehicle" cache |
| Respect `dt` | Substep length varies up to 2 s; KSAero keeps stock's semi-implicit limiter so one substep cannot reverse the airspeed |

## Steps

1. Resolve the private call and the field that identifies the right call site, and stop if either is missing.
   KSAero looks up `ComputeDrag` with an explicit by-ref parameter list so the lookup survives overloads:

```csharp
// KSAero/AirForcePatch.cs L40-L58
    private static readonly MethodInfo? ComputeDrag = AccessTools.Method(typeof(PhysicsStates), "ComputeDrag", new[]
    {
        typeof(BubbleOrigin).MakeByRefType(), typeof(KinematicStates).MakeByRefType(),
        typeof(VehicleProperties).MakeByRefType(), typeof(PhysicsEnvironment).MakeByRefType(),
        typeof(Disturbances).MakeByRefType(), typeof(double3),
        typeof(double), typeof(double), typeof(double), typeof(double),
    });

    private static readonly FieldInfo? AtmosphericDensity = AccessTools.Field(typeof(PhysicsEnvironment), nameof(PhysicsEnvironment.AtmosphericDensity));

    private static ComputeDragFn? _stockDrag;

    internal static void Apply(Harmony harmony)
    {
        if (ComputeDerivatives == null || ComputeDrag == null || AtmosphericDensity == null)
            throw new MissingMemberException("PhysicsStates.ComputeDerivatives, PhysicsStates.ComputeDrag or PhysicsEnvironment.AtmosphericDensity not found.");

        _stockDrag = AccessTools.MethodDelegate<ComputeDragFn>(ComputeDrag);
        harmony.Patch(ComputeDerivatives, transpiler: new HarmonyMethod(typeof(AirForcePatch), nameof(Transpiler)));
    }
```

2. Rewrite exactly one call site and refuse anything else. If the match count is not one, throw before the
   patch is applied, so the game runs stock physics instead of a half-patched method:

```csharp
// KSAero/AirForcePatch.cs L61-L90
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

3. Give the replacement the stock signature so it drops into the call site unchanged. It computes from its
   arguments only and returns early where the answer is degenerate:

```csharp
// KSAero/AirForcePatch.cs L110-L133
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

   KSAero has no `try`/`catch` here. It avoids throwing by guarding its divisions (`Math.Max(..., 1e-9)`),
   returning early on near-zero `q`, and falling back (next step) when the geometry is degenerate.

4. Keep the stock behaviour reachable for inputs you cannot model. `ComputeDrag` is private, so bind a
   delegate to it once at install time. The hot path is then a plain delegate call with no reflection and no
   allocation; this only works because you do not patch `ComputeDrag` itself:

```csharp
// KSAero/AirForcePatch.cs L28-L30
    private delegate void ComputeDragFn(in BubbleOrigin origin, in KinematicStates kinematic, in VehicleProperties props,
        in PhysicsEnvironment environment, ref Disturbances disturbances, double3 airVelocityBub, double density,
        double dragCdA, double surfaceArea, double dt);
```

```csharp
// KSAero/AirForcePatch.cs L99-L108
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
```

5. Install from `[StarMapAllModsLoaded]` and treat an `Apply` exception as "feature off", logged once. The
   expected sign of success is a log line from your install code and visibly different flight behaviour; a
   signature change in a game update shows up as the `MissingMemberException` or the `Expected one ...
   found N` message, never as a crash mid-flight.

## Complete example

| File | Role |
|---|---|
| [KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L99-L133) | `ComputeAirForces` and the transpiler |
| [KSAero/AeroModel.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AeroModel.cs#L156-L215) | The pure model: no static mutable state, no allocation |

## Variations and gotchas

- Diagnostics belong on the main thread. KSAero's readout window re-evaluates the same pure function from
  UI state instead of logging from the worker.
- Other mods call `PhysicsStates.ComputeDerivatives` on probe copies (KSA-AdvancedFlightComputer reads its
  drag table this way), so a patch that mutates anything outside its arguments corrupts their predictions.
- The RK8 long-step path never reaches the drag branch (CCI bubble frame, outside the physics radius, no
  chutes), so the replacement is not called there.
- Patching a method you also want to call: a delegate to a patched method calls the patched version. Use a
  Harmony reverse patch if you need the original body of a method you patch.
- If you need main-thread data in the function, do not read it here; use one of the hand-offs in
  [approaches 2 to 6](overview.md).

## See also

- [Overview and comparison](overview.md)
- [Lifecycle reference: run code inside the physics step](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-4.md)
- Mod features: [KSAero body-of-revolution aerodynamics](../../../../mods/ksaero/features/body-of-revolution-aerodynamics.md), [stock ComputeDrag delegate fallback](../../../../mods/ksaero/features/stock-drag-delegate-fallback.md)
- The topic replace-the-stock-aerodynamic-force-model (another batch) for the physics of the replacement

[^ksa-physicsstates]: KSA.PhysicsStates (decompiled, build 2026.10.10.5554)
