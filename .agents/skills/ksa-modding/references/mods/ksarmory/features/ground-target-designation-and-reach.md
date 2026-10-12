---
type: Mod Feature
title: Designate a place on the ground that moves with the planet, and show what a falling store can still reach
description: A click on terrain becomes a Ground aimpoint stored in the body's own fixed frame and re-read into ecliptic position and velocity (orbit plus spin) every frame, and a falling guided store's remaining reach is flown and drawn so a click is either a correction or the next aim
mod: ksarmory
category: flight-and-orbits
techniques: [body-fixed-anchor, per-frame-resample, cursor-ray, flown-reach, imgui-input]
hooks: [starmap StarMapAfterGui (via StepOnce and the GUI pass)]
lifecycle: [frame:draw-ui]
game_types: [KSA.Celestial, KSA.Astronomical, Brutal.ImGuiApi.ImGuiIOPtr]
tags: [targeting, designation, ground-target, body-fixed, reach, guided-bomb, imgui-input]
---

Radar names craft; an operator sometimes needs to name a **place**: terrain, a spot ahead of a
target, or something the threat model ignores. KSArmory's `Designator` turns a click on the ground
into an aimpoint, and the reusable insight is in how that aimpoint is stored: as a point in the
body's own rotating frame, re-read every frame, never as an ecliptic coordinate.[^designator][^aimpoint]
A second piece, `StoreReach`, answers what a guided bomb already falling can still be steered onto,
so the same click means "correct the falling store" inside its reach and "aim the next one"
outside it.[^store-reach][^store-retarget]

## How it works

### Aimpoint kinds

`Aimpoint(Kind, Handle, PositionEcl, VelocityEcl, Radius, Anchor)` separates *what* a round aims at
from its kinematics: `Vehicle`, `Part`, `Point` (fixed coordinate), `Ground` (a place on a body),
`None` (a dumb release). `Ground` is distinct from `Point` because a place on the ground is only
still in the body's frame: in the ecliptic it carries the planet's ~29.8 km/s orbital motion plus up
to 465 m/s of spin, so a stored coordinate is left 180 km behind every six seconds and a
proportional-navigation round reads the frame velocity as closing speed.[^aimpoint]

### Anchor and resample

| Step | Code | Detail |
|---|---|---|
| Click to ground | `KsaWorld.TryCursorGroundPoint` | Cursor ray against terrain (see the click-tools feature)[^designator] |
| To anchor | `KsaWorld.TryAnchorToGround` | Nearest body; `anchor = conj(body.GetBodyFixed2Ecl()) * (pointEcl - body.GetPositionEcl())`[^ksaworld][^ksa-astronomical] |
| Back to Ecl | `KsaWorld.TryGroundAnchorEcl(body, anchor)` | `position = body.GetPositionEcl() + R * anchor`; `velocity = body.GetVelocityEcl() + cross(axis * GetAngularVelocity(), R * anchor)` with `axis = R * +Z`[^ksaworld][^ksa-celestial] |
| Every frame | `WeaponSystem.SampleTarget` | A `Ground` aimpoint is `Resampled` from the anchor before guidance reads it[^weapon-system] |

The spin term is not optional: 465 m/s at Earth's equator is 4.6 km of miss over a ten-second
flight.[^ksaworld]

### The click itself

`Designator.Update` (per weapon system, only the one the panel shows) ignores the click when
`ImGui.GetIO().WantCaptureMouse` (the click was on a window) or `KeyShift` (shift-click is the lock
gesture), and lifts the aim point off the ground by the round's fireball radius so a burst reads as
in the air. With nothing under the cursor, a belt-fed gun still fires a burst where it points,
because the sky is where a CIWS's targets are.[^designator][^brutal-imguiio]
`Designator.Draw` paints a ground ring (via the depth-painted ring pass) and a line to the lifted
point, coloured by whether the weapon could take the shot (ready, in range, can guide onto it).[^designator]

### What a falling store can still reach

- `StoreReach.Update` runs only while a store that steers its own fall is in the air, at half the
  pipper's rate, because each solve flies three bomb-sight trajectories (the pipper alone measured
  5.41 ms of a 7.17 ms draw over eight rockets).[^store-reach]
- `TailKitReach.Fly` steps the same `Slug` through the same `TailKit` law the store obeys (three
  flights per solve) and reports a region inscribed in the real footprint. Flown, not a closed form:
  a lateral push settles against drag, so displacement grows as `t` rather than `t^2` (0.46 of
  `a*t^2` delivered at a 21 s fall, 0.18 at 95 s). The impact is held as a ground anchor so the ring
  stays on the planet.[^tailkit-reach][^store-reach]
- `StoreRetarget.Reaches` decides whether a new designation is a correction for the last released
  store or the aim for the next one; one answer feeds the ring, the panel line and the log clause.[^store-retarget][^weapon-system]
- The ballistic computer's bus has the analogous `ReachDisplay` for its divert footprint.[^reach-display]

## Lifecycle and timing

- **Clicks: `frame:draw-ui`.** `UpdateWorldClicks` runs in the `[StarMapAfterGui]` GUI pass after
  the panel (so a click on a window is not a click on the world), and from `StepOnce` when the HUD
  is hidden, because ImGui's input frame is still read every frame before KSA's `DrawUI` gate.[^entry]
- **Resample: `frame:draw-ui`**, inside the round update of `StepSimulation`, against the celestial
  state KSA applied in this frame's `PrepareFrame`.[^weapon-system]
- **Reach**: solved in the same pass, rate-limited by a stopwatch; drawn in the GUI pass.[^store-reach][^entry]

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Sim/Aimpoint.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Aimpoint.cs) | Aimpoint kinds, ground factory, resample |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L978-L1005) | `TryGroundAnchorEcl` |
| [src/KSArmory/Ksa/Designator.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Designator.cs) | Click handling, marker, lift |
| [src/KSArmory/Ksa/WeaponSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystem.cs#L3585-L3600) | Per-frame resample of ground aimpoints |
| [src/KSArmory/Ksa/StoreReach.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/StoreReach.cs) | Falling-store reach, rate-limited |
| [src/KSArmory/Sim/StoreRetarget.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/StoreRetarget.cs) | Correction vs next aim |

## Snippets

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L978-L1005
    public static bool TryGroundAnchorEcl(object? body, double3 anchor,
                                          out double3 positionEcl, out double3 velocityEcl)
    {
        positionEcl = Vec.Zero;
        velocityEcl = Vec.Zero;

        if (body is not Celestial celestial) return false;

        try
        {
            doubleQuat spin = celestial.GetBodyFixed2Ecl();
            double3 offset = spin * anchor;

            positionEcl = celestial.GetPositionEcl() + offset;

            // Orbital motion, plus the surface sweeping under it. omega x r, with omega taken
            // about the spin axis the body's own frame defines.
            double3 axis = Vec.Unit(spin * new double3(0, 0, 1));
            velocityEcl = celestial.GetVelocityEcl()
                          + Vec.Cross(axis * celestial.GetAngularVelocity(), offset);

            return Vec.IsFinite(positionEcl) && Vec.IsFinite(velocityEcl);
        }
        catch
        {
            return false;
        }
    }
```

```csharp
// src/KSArmory/Ksa/Designator.cs L29-L52
    public void Update(IManualFire system, SystemConfig policy)
    {
        if (!policy.MouseFire) return;

        // A click on the panel is not a click on the world behind it.
        if (ImGui.GetIO().WantCaptureMouse) return;
        // Shift is the designate gesture, so a shift-click is not a click on the world.
        // Without this, locking a target while this tool is on also sends a round at it.
        if (ImGui.GetIO().KeyShift) return;

        if (!ImGui.IsMouseClicked(ImGuiMouseButton.Left, repeat: false)) return;

        if (!KsaWorld.TryCursorGroundPoint(out double3 groundEcl, out _, out _, out _))
        {
            // Nothing under the cursor is not a reason to hold fire. A gun shoots where it is
            // pointing rather than at a named place, and the sky is where most of its targets are:
            // requiring a ground hit would leave the CIWS unable to fire at anything above the
            // horizon, which is the one thing a CIWS is for.
            if (system.TriggerArmament == ArmamentKind.Belt) system.FireBurst();
            return;
        }

        system.FireAt(Lifted(groundEcl, system));
    }
```

## Game APIs touched

| Member | Use |
|---|---|
| `Astronomical.GetBodyFixed2Ecl()` ([current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs#L279)), overridden by `Celestial` ([current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L174)) | Body-fixed to ecliptic rotation |
| `Celestial.GetAngularVelocity()` (L184), `GetPositionEcl()`, `GetVelocityEcl()` | Spin rate and body motion |
| `ImGuiIOPtr.WantCaptureMouse`, `KeyShift` ([current/decomp/Brutal.ImGuiApi/ImGuiIOPtr.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGuiIOPtr.cs#L140)), `ImGui.IsMouseClicked` | Click arbitration with the UI |

## Pitfalls and version notes

- **Never hold a ground target as an ecliptic coordinate**; store it body-fixed and resample it,
  including the spin term in its velocity.[^aimpoint][^ksaworld]
- **Arm a tool, do not press a button**, to pick a place: a button samples the cursor while it is
  over the button (the ICBM's site designator follows the same rule).[^designator]
- **Check `WantCaptureMouse`** before treating a click as a click on the world, and handle the
  hidden-HUD frame separately because the GUI pass does not run.[^designator][^entry]
- **Reach solves are expensive**; solve only while a steerable store is airborne and below the
  draw rate.[^store-reach]

## See also

- [Cursor-ray world click tools](./cursor-ray-world-click-tools.md)
- [Depth-painted ground rings](./depth-painted-ground-rings.md)
- [Guided bomb tail kit and sight](./guided-bomb-tail-kit-and-sight.md)
- [Proportional navigation guidance](./proportional-navigation-guidance.md)
- [ICBM ballistic computer](./icbm-ballistic-computer.md)
- [Unguided ballistic lead](./unguided-ballistic-lead.md)
- [Steer a falling store by its predicted impact and take a ground designation as its input](../../../howto/flight-and-orbits/guidance/steer-by-zero-effort-miss/steer-by-zero-effort-miss-3.md) - how-to article demonstrating this feature
- [Click a point on the terrain and keep it as a body-fixed anchor re-read into ecliptic position and velocity every frame](../../../howto/flight-and-orbits/map-view/anchor-and-pick-points-on-bodies/anchor-and-pick-points-on-bodies-1.md) - how-to article demonstrating this feature

[^aimpoint]: src/KSArmory/Sim/Aimpoint.cs
[^designator]: src/KSArmory/Ksa/Designator.cs
[^ksaworld]: src/KSArmory/Ksa/KsaWorld.cs
[^weapon-system]: src/KSArmory/Ksa/WeaponSystem.cs
[^store-reach]: src/KSArmory/Ksa/StoreReach.cs
[^tailkit-reach]: src/KSArmory/Sim/TailKitReach.cs
[^store-retarget]: src/KSArmory/Sim/StoreRetarget.cs
[^reach-display]: src/KSArmory/Sim/ReachDisplay.cs
[^entry]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^ksa-astronomical]: KSA.Astronomical (decompiled, build 2026.10.10.5554)
[^brutal-imguiio]: Brutal.ImGuiApi.ImGuiIOPtr (decompiled, build 2026.10.10.5554)
