---
type: How-To
title: Single-threaded Timestep with own BufferPool in the assembly frame
description: Simulate IVA props as free bodies in a per-vessel Bepu world in the vessel's assembly frame, with proper-acceleration, Euler, Coriolis and centrifugal terms from the vessel's accelerometer and body rates, and write poses onto SubParts
category: physics
group: colliders
topic: run-your-own-bepu-simulation
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/gatos/features/iva-cabin-physics-bepu]
techniques: [bepu-physics, apparent-forces]
hooks: [starmap StarMapAfterGui, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:after-on-frame]
game_types: [BepuPhysics.Simulation, KSA.Part, KSA.Vehicle, KSA.JobSystems]
tags: [physics, bepu, simulation, iva, cabin, props, subpart, accelerometer, coriolis, gatos]
---

Outcome: cups, clipboards and a cat in the IVA drift while coasting, settle on the floor on the pad, slam aft during a
burn and swing around during RCS rotation, colliding with the real interior surfaces. gatOS' `/sim/debug/iva` adopts
IVA props as free-floating rigid bodies in a small Bepu simulation per vessel and only moves render-side SubParts. It
is off by default; with the master switch off nothing is allocated. Prerequisites: StarMap.

## Background: how the game does it

KSA vehicles are one dynamic body each with a coarse outside collider; the interior has no colliders. Putting props into
that world fails (ejection, pushing the craft, the sim not stepping while coasting, worker threads). So the mod builds its own world
against the same embedded Bepu:

```csharp
// current/decomp/BepuPhysics/Simulation.cs L158
	public static Simulation Create<TNarrowPhaseCallbacks, TPoseIntegratorCallbacks>(BufferPool bufferPool, TNarrowPhaseCallbacks narrowPhaseCallbacks, TPoseIntegratorCallbacks poseIntegratorCallbacks, SolveDescription solveDescription, ITimestepper timestepper = null, SimulationAllocationSizes? initialAllocationSizes = null, Shapes shapes = null) where TNarrowPhaseCallbacks : struct, INarrowPhaseCallbacks where TPoseIntegratorCallbacks : struct, IPoseIntegratorCallbacks
```

The apparent acceleration of an object in a moving vessel at offset `r_b` from the centre of mass with cabin velocity `v` is
`a = -a_p - alpha x r_b - 2 omega x v - omega x (omega x r_b)`, with `a_p` the proper acceleration (the vehicle's accelerometer,
`Vehicle.AccelerationBody`), `omega` the body rates and `alpha` the angular acceleration. Gravity never appears
explicitly: on the pad `a_p` is 1 g up, in orbit it is zero, under thrust objects slam aft. These fields are
main-thread state that changes only when `Universe.ApplyVehicleSolvers` copies a finished step in.[^ksa-vehicle]

## Lifecycle and timing

- `IvaPhysicsManager.Update` runs from `StarMapAfterGui` (or `StarMapAfterOnFrame` when the HUD is hidden), after the mod's
  welds driver. It deliberately does **not** hook `Universe.ExecuteNextVehicleSolvers` (the main-thread dispatch window, which the
  mod uses for state writes): there the `SimStep.DeltaTime` follows time warp, while cabin motion is visual and tracks wall-clock.
- It calls `JobSystems.VehicleSolver.Wait()` first. The mod's comment says this makes the accelerometer, body rates and centre
  of mass "the settled values"; in the decomp those fields change only in the next frame's `ApplyVehicleSolvers`, so the reads
  return the step applied at the start of this frame with or without the wait. What the wait does buy is that the SubPart pose
  writes land while no solver worker is running; its cost is a main-thread stall until the in-flight step finishes, paid only
  while the feature is active.
- Objects park (velocities zeroed, poses held) while paused, under time warp, in the editor and outside the IVA camera.

See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Create one `BufferPool` and `Simulation` per vessel with your own narrow-phase and pose-integrator callbacks. Eight
   velocity iterations and one substep match KSA's own solve quality; do your own fixed substepping for temporal
   resolution. Work in the vessel's **assembly frame**, where the interior is static geometry that never moves:

```csharp
// gatOS.GameMod/Game/Ksa/Iva/CabinSim.cs L69-L82
    public CabinSim(CabinTuning tuning)
    {
        _tuning = tuning;
        // A modest block size: a cabin holds a handful of bodies and a few thousand triangles.
        _pool = new BufferPool(8192);
        var narrowPhase = new CabinNarrowPhaseCallbacks(
            (float)tuning.Friction,
            (float)tuning.Restitution,
            new SpringSettings((float)tuning.ContactFrequency, (float)tuning.ContactDamping));
        var poseIntegrator = new CabinPoseCallbacks(_field);
        // 8 velocity iterations / 1 substep matches KSA's own ConstraintSim solve quality; our own
        // fixed substepping (the driver's accumulator) provides the temporal resolution.
        _simulation = Simulation.Create(_pool, narrowPhase, poseIntegrator, new SolveDescription(8, 1));
    }
```

2. Build the interior collision mesh from parts and subparts whose `PartModelModule.Template.Internal` is true, using
   `MeshReference.PositionCompare` transformed into the assembly frame, excluding adopted objects. Cache per vessel and rebuild only
   on a part-count change or adopt/release, never on a timer, because building the mesh's bounding tree is the one genuinely
   expensive operation.
3. Compute the forcing term in a game-free class so it is unit tested, and apply it from the pose-integrator callbacks:

```csharp
// gatOS.SimFs/Iva/CabinPhysics.cs L54-L65
    public static double3Snap ApparentAcceleration(
        double3Snap properAccel, double3Snap bodyRates, double3Snap angularAccel,
        double3Snap offsetFromCom, double3Snap velocity)
    {
        var euler = Cross(angularAccel, offsetFromCom);
        var coriolis = Scale(Cross(bodyRates, velocity), 2.0);
        var centrifugal = Cross(bodyRates, Cross(bodyRates, offsetFromCom));
        return new double3Snap(
            -properAccel.X - euler.X - coriolis.X - centrifugal.X,
            -properAccel.Y - euler.Y - coriolis.Y - centrifugal.Y,
            -properAccel.Z - euler.Z - coriolis.Z - centrifugal.Z);
    }
```

4. Step with a fixed substep and a per-frame cap. `Timestep` is called without an `IThreadDispatcher`, so every callback runs on
   the calling thread. Wake every body when the forcing field changes (Bepu only wakes an island on new contacts, so settled props
   would otherwise stay glued to the floor through launch). Cap catch-up so a hitch cannot spiral:

```csharp
// gatOS.GameMod/Game/Ksa/Iva/CabinSim.cs L234-L272
    public void Step(double dt, in CabinField field, Action<BodyHandle, double>? onImpact = null)
    {
        LastSubsteps = 0;
        if (_simulation is not { } simulation)
            return;

        // A sleeping body is not integrated, so it would never notice the engines lighting: Bepu only
        // wakes an island on new contacts, and a change in an external acceleration field is invisible
        // to it. Without this, props that settled on the pad would stay glued to the floor through
        // launch — the one behaviour the whole feature exists to show.
        WakeAllOnFieldChange(simulation, field);
        _field.Value = field;

        // Cap the catch-up so a hitch (or a load screen) cannot spiral: run at most
        // MaxSubstepsPerFrame and throw the rest away.
        _accumulator += Math.Clamp(dt, 0, 0.25);
        var step = _tuning.SubstepSeconds;
        var budget = _tuning.MaxSubstepsPerFrame;
        var maxSpeed = (float)_tuning.MaxSpeed;
        // The velocity change the forcing field alone will produce this substep. Only its linear term
        // is position-independent, but over a two-metre cabin the rotational terms are ~10⁻² m/s² —
        // three orders below any sensible impact threshold — so this accounts for essentially all of
        // the non-contact change (see ReportImpacts).
        var fieldDelta = -field.ProperAcceleration * (float)step;

        while (_accumulator >= step && LastSubsteps < budget)
        {
            _accumulator -= step;
            LastSubsteps++;

            if (onImpact is not null)
                CaptureVelocities(simulation);

            simulation.Timestep((float)step);

            ClampSpeeds(simulation, maxSpeed);
            if (onImpact is not null)
                ReportImpacts(simulation, fieldDelta, onImpact);
        }
```

5. Drive SubParts, never top-level parts (KSA serialises top-level part transforms into saves). Write each body's pose onto
   the adopted SubPart's `PositionParentAsmb` and `Asmb2ParentAsmb` (both setters reset KSA's cached matrices); release restores
   the captured rest pose:

```csharp
// gatOS.GameMod/Game/Ksa/Iva/FloatingObject.cs L130-L141
        var scale = parent.Scale.X;
        if (!double.IsFinite(scale) || scale <= 0)
            scale = 1;

        var local = double3.Transform(positionVehicle - parent.PositionVehicleAsmb,
            doubleQuat.Conjugate(parentOrientation)) / scale;
        if (!IsFinite(local))
            return;

        Part.PositionParentAsmb = local;
        Part.Asmb2ParentAsmb = doubleQuat.Concatenate(orientation, doubleQuat.Conjugate(parentOrientation));
    }
```

6. Dispose everything with the vessel: drop the bodies without touching a dead part tree when the vessel is gone, and dispose
   the simulation and pool in `Dispose`.

## Complete example

| File | Role |
|---|---|
| [gatOS.GameMod/Game/Ksa/Iva/CabinSim.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Iva/CabinSim.cs) | The Bepu world: creation, interior, substepping, wake |
| [gatOS.SimFs/Iva/CabinPhysics.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Iva/CabinPhysics.cs#L36-L65) | Game-free forcing field |
| [gatOS.GameMod/Game/Ksa/Iva/InteriorGeometry.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Iva/InteriorGeometry.cs) | Interior triangle soup from internal parts |
| [gatOS.GameMod/Game/Ksa/Iva/FloatingObject.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Iva/FloatingObject.cs#L96-L141) | Pose to SubPart |
| [gatOS.GameMod/Game/Ksa/Iva/IvaPhysicsManager.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Iva/IvaPhysicsManager.cs#L303-L360) | Per-frame driver and park conditions |

## Variations and gotchas

- Never add bodies to KSA's vehicle constraint simulation for cosmetic physics: it is coarse, on worker threads, and couples back into flight.
- Never move top-level parts for effects: their transforms are saved.
- A change in an external acceleration field is invisible to Bepu's sleep logic; wake bodies yourself.
- The repo lists in-game validation as pending.
- Everything in the private world is yours to free: shapes, the mesh's tree, the pool.

## See also

- [Overview and comparison](overview.md)
- Mod feature: [gatOS IVA cabin physics](../../../../mods/gatos/features/iva-cabin-physics-bepu.md)
- [Disable collisions for one vessel](../suppress-collisions-for-one-vessel/overview.md)

[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
