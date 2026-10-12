---
type: How-To
title: Draw an engine diagram from nozzle positions and live engine state
description: List the active stage's engines with state and propellant, transform each nozzle's LocationAsmb into vehicle space and lay the engines out as a top-down diagram
category: vehicles-and-parts
group: part-geometry
topic: draw-an-engine-diagram-from-nozzle-positions
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksa-public-telemetry/features/engine-diagram-from-nozzle-positions]
techniques: [part-tree-modules]
hooks: [starmap StarMapAfterOnFrame, starmap StarMapAfterGui]
lifecycle: [frame:after-on-frame, frame:draw-ui]
game_types:
  - KSA.PartTree
  - KSA.ModuleList
  - KSA.ModuleStateful
  - KSA.EngineController
  - KSA.SequenceList
  - KSA.Tank
  - KSA.Part
  - KSA.RocketCore
  - KSA.RocketNozzle
  - KSA.Vehicle
  - KSA.Mole
  - KSA.MoleState
  - KSA.SolidMotor
tags: [engines, nozzles, diagram, propellant, imgui-drawlist]
---
Outcome: a gauge that shows the current stage's engines as dots placed where their nozzles actually are, white when burning and grey when off, armed or starved, plus an arc for propellant left. A Falcon-9 style ring of nine,
a single vacuum engine or a side-booster cluster each come out right with no per-vehicle configuration, because the layout is computed from the part tree every frame.

Prerequisite: StarMap. Demonstrated by [ksa-public-telemetry/engine-diagram-from-nozzle-positions](../../../../mods/ksa-public-telemetry/features/engine-diagram-from-nozzle-positions.md), which samples in `[StarMapAfterOnFrame]` and draws with ImGui in `[StarMapAfterGui]`.

## Background: how the game does it

See the [overview](overview.md). In short: engines are `EngineController`s in `parts.States`; each has `Cores`; each core's `Rocket.Nozzles` have a part-local `LocationAsmb`; the part's `MatrixAsmb2VehicleAsmb` takes it to vehicle assembly space
(the transform the game applies in `RocketNozzle.ResetState`, [current/decomp/KSA/RocketNozzle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RocketNozzle.cs#L277-L286)[^ksa-rocketnozzle]). Stock engines exhaust along their own -X (`<ExhaustDirection X="-1" Y="0" Z="0" />` [current/Content/Core/CorePropulsionAGameData.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/CorePropulsionAGameData.xml#L24)[^ksa-content-corepropulsionagamedata]), so for
engines stacked along the root part's axis, X is the thrust axis and Y/Z is the cross-section. Remaining propellant is the same tank read described in [Reading live part module state](../../modules-and-resources/read-live-part-module-state/overview.md).

## Lifecycle and timing

- **Sample in `frame:after-on-frame`** (`[StarMapAfterOnFrame]`). Module states (`RocketCoreState`, `EngineControllerState`, `MoleState`) are main-thread copies refreshed when the game applies vehicle solver results in `PrepareFrame`
  (`Parts.UpdateFromTaskResults`, [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2531)[^ksa-vehicle]), so reading after the frame gives this frame's applied values; the step dispatched later in `PrepareFrame` works on its own copies.
- **Draw in `frame:draw-ui`** (`[StarMapAfterGui]`) from the snapshot. The mod also replays the draw while the game HUD is hidden, which is why it lives behind a hidden-UI patch.
- **Rebuild every frame.** Staging, decoupling and engine shutdown change the set; recomputing a few dozen transforms per frame is cheaper than tracking events.

See [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md). The mod's hook:

```csharp
// TelemetryOverlayMod.cs L65-L86
[StarMapAfterOnFrame]
public void OnAfterFrame(double currentPlayerTime, double dtPlayer)
{
    if (_disabled || _config is null)
    {
        return;
    }

    try
    {
        TelemetrySampler.Sample(_snapshot, _config, dtPlayer);
        HiddenUiPatch.RecordDelta(dtPlayer);
        Succeeded();
    }
    catch (Exception ex)
    {
        Fail("sampling", ex);
    }
}

[StarMapAfterGui]
public void OnAfterGui(double dt) => DrawEverything(dt, gameUiHidden: false);
```

## Steps

1. **Get the engine controllers with their states.** If the list is missing the vehicle has no engines. Choose the stage to draw: `SequenceList.ActiveSequence`, but if nothing belongs to it (before the first engine stage) advance to the nearest sequence ahead that has engines,
   so the pad view already shows the first stage. An engine belongs on the diagram when `controller.Sequence <= activeSequence || controller.IsActive`:

```csharp
// Telemetry/TelemetrySampler.cs L375-L389
private static int EffectiveSequence(
    PartTree parts,
    ModuleStateful<EngineController, EngineControllerState, EngineControllerGlobalState, EmptyStruct>.StateList controllerStates)
{
    DiagramSequencePicker picker = new(parts.SequenceList?.ActiveSequence ?? 0);
    var enumerator = controllerStates.ModulesAndStates.GetEnumerator();

    while (enumerator.MoveNext())
    {
        EngineController controller = enumerator.Current.Module;
        picker.Offer(controller.Sequence, controller.IsActive);
    }

    return picker.Result;
}
```

```csharp
// Telemetry/TelemetrySampler.cs L415-L416
public static bool BelongsOnDiagram(int sequence, int activeSequence, bool isActive)
    => sequence <= activeSequence || isActive;
```

2. **Enumerate cores and skip verniers.** `parts.RocketCores.GetModulesAndStates(controller.Cores.AsSpan())` yields each core with its `RocketCoreState`. Drop cores whose largest nozzle exit radius is under 40% of the controller's largest, so steering chambers do not clutter the diagram.
   Per engine record throttle, chamber pressure and temperature (`Conditions.Core`), mass flow and `GetActualThrustTime()`:

```csharp
// Telemetry/TelemetrySampler.cs L426-L433,L461-L491
if (!ModuleStateful<EngineController, EngineControllerState, EngineControllerGlobalState, EmptyStruct>
        .TryGetFrom(parts.States, out var controllerStates))
{
    return;
}

int activeSequence = EffectiveSequence(parts, controllerStates);
snapshot.PartCount = parts.Count;
// ...
    var coreEnumerator = parts.RocketCores
        .GetModulesAndStates(controller.Cores.AsSpan())
        .GetEnumerator();

    while (coreEnumerator.MoveNext())
    {
        var core = coreEnumerator.Current;
        ref readonly RocketCoreState coreState = ref core.State;

        controllerThrottle = MathF.Max(controllerThrottle, coreState.Throttle);

        float exitRadius = GetMaxExitRadius(core.Module);
        if (largestExit > 0f && exitRadius < mainEngineThreshold)
        {
            continue;
        }

        EngineSample sample = default;
        sample.ExitRadius = exitRadius;
        sample.Sequence = controller.Sequence;
        sample.Throttle = coreState.Throttle;
        sample.ChamberPressure = coreState.Conditions.Core.Pressure;
        sample.ChamberTemperature = coreState.Conditions.Core.Temperature;
        sample.MassFlowRate = coreState.MassFlowRate;
        sample.ThrustTimeRemaining = coreState.GetActualThrustTime();
        sample.Status = ClassifyEngine(controllerActive, in coreState);

        GetNozzleCentroid(core.Module, out float x, out float y);
        sample.DiagramX = x;
        sample.DiagramY = y;

```

3. **Classify status.** Inactive (controller off), Starved (`!IsPropellantAvailable`), Armed (throttle 0), Throttled or Nominal (throttle at or above a threshold):

```csharp
// Telemetry/TelemetrySampler.cs L514-L534
private static EngineStatus ClassifyEngine(bool controllerActive, ref readonly RocketCoreState state)
{
    if (!controllerActive)
    {
        return EngineStatus.Inactive;
    }

    if (!state.IsPropellantAvailable)
    {
        return EngineStatus.Starved;
    }

    if (state.Throttle <= 0f)
    {
        return EngineStatus.Armed;
    }

    return state.Throttle >= NominalThrottleThreshold
        ? EngineStatus.Nominal
        : EngineStatus.Throttled;
}
```

4. **Project the nozzles.** For each nozzle take `LocationAsmb`, transform by the owning part's `MatrixAsmb2VehicleAsmb` and average Y and Z (X is the thrust axis and is discarded):

```csharp
// Telemetry/TelemetrySampler.cs L575-L613
private static void GetNozzleCentroid(RocketCore core, out float x, out float y)
{
    x = 0f;
    y = 0f;

    RocketNozzle[]? nozzles = core.Rocket?.Nozzles;
    if (nozzles is null || nozzles.Length == 0)
    {
        return;
    }

    float sumY = 0f, sumZ = 0f;
    int count = 0;

    for (int i = 0; i < nozzles.Length; i++)
    {
        RocketNozzle nozzle = nozzles[i];
        Part? part = nozzle.Parent;
        if (part is null)
        {
            continue;
        }

        float4x4 asmb2Vehicle = float4x4.Pack(part.MatrixAsmb2VehicleAsmb);
        float3 vehicleLocation = nozzle.LocationAsmb.Transform(asmb2Vehicle);

        sumY += vehicleLocation.Y;
        sumZ += vehicleLocation.Z;
        count++;
    }

    if (count == 0)
    {
        return;
    }

    x = sumY / count;
    y = sumZ / count;
}
```

   This ignores gimbal deflection (nozzles are at their neutral position); the game's own `UpdateState` rotates about the gimbal pivot first. The cross-section's on-screen orientation is not fixed by anything the mod reads, hence the mod's "Diagram rotation" slider.

5. **Normalise.** One engine sits at the centre. Otherwise re-centre on the mean, divide by the largest radius so the outermost engine lands on the unit circle, fan coincident engines (multi-core parts) onto a small ring, and fall back to a 0.7-radius circle if every engine collapsed to one point:

```csharp
// Telemetry/TelemetrySampler.cs L623-L630,L662-L669
if (count == 1)
{
    EngineSample only = snapshot.Engines[0];
    only.DiagramX = 0f;
    only.DiagramY = 0f;
    snapshot.Engines[0] = only;
    return;
}
// ...
float scale = 1f / halfSpan;
for (int i = 0; i < count; i++)
{
    EngineSample engine = snapshot.Engines[i];
    engine.DiagramX = (engine.DiagramX - centreX) * scale;
    engine.DiagramY = (engine.DiagramY - centreY) * scale;
    snapshot.Engines[i] = engine;
}
```

   Dot size is the exit radius relative to the largest, clamped to 0.25..1 ([Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs#L536-L556)[^ksa-public-telemetry-telemetrysampler]).

6. **Add propellant.** Sum mass and capacity over every tank's moles; the arc shows the ratio:

```csharp
// Telemetry/TelemetrySampler.cs L761-L763,L767-L790
ReadOnlySpan<MoleState> moleStates = parts.Moles.States;
Span<Tank> tanks = parts.Tanks.Modules;

// ...
for (int t = 0; t < tanks.Length; t++)
{
    List<Mole> moles = tanks[t].Moles;

    for (int m = 0; m < moles.Count; m++)
    {
        Mole mole = moles[m];
        int idx = mole.StatesIdx;
        if (idx < 0 || idx >= moleStates.Length)
        {
            continue;
        }

        float capacity = mole.GetStoredMass(mole.ContainerVolume);

        if (capacity <= 0f)
        {
            continue;
        }

        aggregateMass += moleStates[idx].Mass;
        aggregateCapacity += capacity;
    }
}
```

   For per-tank figures, keep `moleStates[idx].Mass / capacity` per tank in the same loop instead of two running sums (adapted from the loop above; the mod only keeps the aggregate).

7. **Draw.** Plot radius is chosen so the outermost dot plus its radius fits the plate; the dot radius comes from half the minimum pairwise separation. Each dot is an `AddCircleFilled`, with a faint 1.1x halo when burning, rotated at draw time with diagram +Y mapped to screen up:

```csharp
// Rendering/EngineClusterPanel.cs L335-L373
for (int i = 0; i < set.Count; i++)
{
    float local = Math.Clamp(
        (phase - set.RingStarts[i]) / (1f - Tuning.RingStaggerSpan), 0f, 1f);

    if (local <= 0f)
    {
        continue;
    }

    float alpha = opacity * local;
    float popScale = Tuning.IntroDotStartScale + (1f - Tuning.IntroDotStartScale) * local;

    float2 offset = set.Offsets[i];

    // Rotated at draw time rather than baked into the set, so the slider
    // turns the dots on the spot and a swap's outgoing set turns with them
    // instead of hanging at the angle it was adopted at.
    float rotatedX = offset.X * rotationCos - offset.Y * rotationSin;
    float rotatedY = offset.X * rotationSin + offset.Y * rotationCos;

    float2 pos = new(
        center.X + rotatedX * plotRadius,
        center.Y - rotatedY * plotRadius);

    uint color = set.Colors[i];

    float dotRadius =
        MathF.Max(baseDotRadius * set.SizeFactors[i], 2.5f * scale) * popScale;

    if (set.Burning[i])
    {
        // subtle glow around burning engines. prob should make this configurable but I'll do it later.
        drawList.AddCircleFilled(in pos, dotRadius * 1.1f,
            OverlayStyle.WithOpacity(color, alpha * 0.20f));
    }

    drawList.AddCircleFilled(in pos, dotRadius, OverlayStyle.WithOpacity(color, alpha));
}
```

## Complete example

- [Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs#L375-L613): sampling, filtering, status and nozzle centroid.
- [Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs#L615-L796): normalisation, overlap fan-out and propellant.
- [Rendering/EngineClusterPanel.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/EngineClusterPanel.cs#L335-L412)[^ksa-public-telemetry-engineclusterpanel] - dots and dot-size maths.
- [TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L65-L96)[^ksa-public-telemetry-telemetryoverlaymod] - the hooks.

## Variations and gotchas

- **Generic arity.** The `ModuleStateful<EngineController, EngineControllerState, EngineControllerGlobalState, EmptyStruct>` spelling must match the game's declaration exactly ([current/decomp/KSA/EngineController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EngineController.cs#L7)[^ksa-enginecontroller]); a change in type parameters breaks compilation, not runtime.
- **Whole-vehicle propellant.** The arc sums every tank (on purpose), so it is not a per-stage gauge; capacity includes oxidiser and fuel alike.
- **Vernier filter is a heuristic.** Engines with genuinely different nozzle sizes on one controller can disappear.
- **Axis assumption.** If a stage thrusts along the vehicle's Y or Z axis (built sideways from its root part), Y/Z is no longer a cross-section and engines in a row spread along a line; coincident ones collapse and trigger the fan-out.
- **Ordinals.** Cores are enumerated from live state lists; do not cache indices across staging.
- **Solid motors** are `RocketCore`s and show up as engines; their grain is not in a `Tank`, so the arc excludes it.

## See also

- [Overview](overview.md)
- [ksa-public-telemetry/engine-diagram-from-nozzle-positions](../../../../mods/ksa-public-telemetry/features/engine-diagram-from-nozzle-positions.md)
- [Reading live part module state](../../modules-and-resources/read-live-part-module-state/overview.md), [Read part meshes, attach nodes and part-local points](../get-part-points-in-vehicle-and-world-frames/overview.md)
- [Switch engines on and off, set throttle and RCS commands](../../modules-and-resources/command-engines-throttle-and-rcs-from-code/overview.md), [Refill, drain or top up a vehicle's propellant and batteries](../../modules-and-resources/refill-or-drain-resources/overview.md)
- [Keep an anchor on a part through staging, docking, editor changes and saves](../anchor-to-parts-across-vehicle-changes/overview.md), [Ray cast against part meshes and terrain](../../../physics/mod-simulation/ray-cast-against-vessel-meshes-and-terrain/overview.md)

[^ksa-rocketnozzle]: current/decomp/KSA/RocketNozzle.cs (decompiled, build 2026.10.10.5554)
[^ksa-content-corepropulsionagamedata]: current/Content/Core/CorePropulsionAGameData.xml (game content, build 2026.10.10.5554)
[^ksa-vehicle]: current/decomp/KSA/Vehicle.cs (decompiled, build 2026.10.10.5554)
[^ksa-public-telemetry-telemetrysampler]: KSA-Public-Telemetry: Telemetry/TelemetrySampler.cs
[^ksa-public-telemetry-engineclusterpanel]: KSA-Public-Telemetry: Rendering/EngineClusterPanel.cs
[^ksa-public-telemetry-telemetryoverlaymod]: KSA-Public-Telemetry: TelemetryOverlayMod.cs
[^ksa-enginecontroller]: current/decomp/KSA/EngineController.cs (decompiled, build 2026.10.10.5554)
