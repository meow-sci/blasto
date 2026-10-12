---
type: How-To
title: Resolve the eye, sight line and roll from an optical head sub-part for the main view or a camera window
description: KSArmory's optical head reads its base subpart's transform from the engine, pivots a head subpart by a rate-limited drive and answers the eye and line of sight against a platform position the caller supplies so the sight and the camera windows are posed inside the viewport pass
category: camera
group: secondary-viewports
topic: mount-a-camera-on-a-part
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/optical-director-heads, mods/ksarmory/features/director-camera-window]
techniques: [direct-camera-write]
hooks: [starmap StarMapAfterGui, subclass KSA.FixedController.OnFrame]
lifecycle: [frame:viewports, frame:draw-ui, frame:render]
game_types: [KSA.Part, KSA.Vehicle, KSA.Camera, KSA.IGameViewport, KSA.FixedController]
tags: [camera, part, subpart, optic, gimbal, sight, roll]
---
KSArmory's optical director is a part whose moving bodies are `<SubPart>` entries: a base on a mast or a pod's roll gimbal, and a head the mod rotates every simulated step. The camera rides the head. Other mods mount a camera on a fixed point of a part; this one mounts it on a part the mod *drives*, and needs the eye,
the line of sight and the roll for both the main view (a gunner's sight) and a camera window. The head and parts mechanics are in [Optical director heads with mast and roll-nod gimbals](../../../../mods/ksarmory/features/optical-director-heads.md); this article covers the camera side. Prerequisites: StarMap.

## Background: how the game does it

`Part.SubParts` lists a part's `<SubPart>` children, each with `PositionParentAsmb` and `Asmb2ParentAsmb` (position and rotation relative to its parent), `Asmb2VehicleAsmb` and `PositionVehicleAsmb` (relative to the vehicle assembly), and the engine keeps them current for whatever moves the base. The vehicle's assembly-to-ego rotation is `Asmb2Ego`
and its centre of mass `CenterOfMassAsmb` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1276-L1281)). A camera pose is then `platformPosition + Asmb2Ego * (pointInVehicleAsmb - centreOfMass)`; nothing in the engine does this for a camera.

## Lifecycle and timing

| Step | Where | Value |
|---|---|---|
| Head drive (aim, slew, write the head sub-part's transform) | The mod's simulation step, `Update(dt)` on frames that advanced time | `frame:draw-ui` (GUI hook) or `frame:celestials` (HUD hidden) |
| Main-view sight pose | Controller `OnFrame` -> `SightCamera.TryPose` against the engine's own platform sample | `frame:viewports` |
| Camera-window pose | `DriveWindow` against the platform's centre **as this window's camera draws it** | `frame:draw-ui` |
| Render | `RenderGame` | `frame:render` |

The platform position is passed in, not read: "for the one caller that runs inside the engine's viewport pass, where 'now' is a different instant from the mod's own sample and the difference is a frame of the planet's motion"
([src/KSArmory/Ksa/OpticalHead.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/OpticalHead.cs#L437-L447)).

## Steps

1. **Read the base's transform off the engine rather than working it out.** Whatever carries the base (a turret's traverse, a hinge, an arm) has already written it, so a head can ride anything without being taught what it is; with no base subpart the fallback is `Fixed`, a director bolted to a hull:

```csharp
// src/KSArmory/Ksa/OpticParts.cs L77-L90
public static MountFrame MountOf(Part director, OpticProfile profile)
{
    try
    {
        return FindBase(director, profile) is { } mount
            ? new MountFrame(mount.PositionParentAsmb, mount.Asmb2ParentAsmb)
            : MountFrame.Fixed;
    }
    catch (Exception e)
    {
        Log.Warn($"optic mount: {e.Message}");
        return MountFrame.Fixed;
    }
}
```

2. **Turn a point in the part's frame into an ecliptic point against a caller-supplied platform position:**

```csharp
// src/KSArmory/Ksa/LauncherPart.cs L956-L972
public static bool TryPartPointEcl(Vehicle platform, Part launcher, double3 partFrame,
                                   double3 platformEcl, out double3 ecl)
{
    ecl = Vec.Zero;

    try
    {
        double3 inVehicle = launcher.PositionVehicleAsmb + launcher.Asmb2VehicleAsmb * partFrame;
        ecl = platformEcl + platform.Asmb2Ego * (inVehicle - platform.CenterOfMassAsmb);

        return Vec.IsFinite(ecl);
    }
    catch
    {
        return false;
    }
}
```

3. **Ask for the view against that position.** The eye is a distance along the aim from the head's pivot, carried through the craft's part transforms; the roll reference is resolved on every read so up and forward come from the same instant:

```csharp
// src/KSArmory/Ksa/OpticParts.cs L165-L179
public static bool TryViewEcl(Vehicle platform, Part director, OpticProfile profile,
                              MountFrame mount, double3 aimPartFrame, double3 platformEcl,
                              out double3 eyeEcl, out double3 forwardEcl)
{
    eyeEcl = forwardEcl = Vec.Zero;

    double3 eyePartFrame = OpticGeometry.EyePartFrame(profile, mount, aimPartFrame);

    if (!LauncherPart.TryPartPointEcl(platform, director, eyePartFrame, platformEcl, out eyeEcl))
    {
        return false;
    }

    return LauncherPart.TryLauncherDirectionEcl(platform, director, aimPartFrame, out forwardEcl);
}
```

4. **Re-solve the line of sight onto the target's drawn position while the head is on target**, which removes a frame of target motion that grows with simulation speed; the aim is deliberately not extrapolated (the mod measured the residual at 0.007 degrees per unit of simulation speed, and a lead at 0.35 degrees of jitter at 10x) ([src/KSArmory/Ksa/OpticalHead.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/OpticalHead.cs#L447-L476)).

5. **Pose the main view from the controller**, asking for the view against the position the engine has just placed the craft at; pose a window from the hook with the platform as that window's camera draws it ([approach 1](../../controllers/write-the-camera-pose-in-phase-with-the-frame/overview.md), [aiming](../aim-a-secondary-viewport-camera/overview.md)):

```csharp
// src/KSArmory/Ksa/SightCamera.cs L48-L70
public bool TryPose(double3 followedEcl, out double3 offsetFromFollowed, out double3 forwardEcl,
                    out double3 upEcl, out double fovDeg)
{
    offsetFromFollowed = forwardEcl = upEcl = Vec.Zero;
    fovDeg = 0.0;

    if (_head is not { } head || !_saved.Valid) return false;
    if (head.Platform is null || head.OpticPart is null) return false;

    // Against the position the engine has just placed the craft at, not the mod's own sample.
    // Pairing the eye with an older platform sample would put a frame of the planet's motion
    // back into the separation, which is the same fault in a different term.
    if (!head.TryOpticViewEclAt(followedEcl, out double3 eye, out forwardEcl)) return false;

    offsetFromFollowed = eye - followedEcl;
    upEcl = head.RollReferenceEcl;
    fovDeg = SightZoom.FovDegreesFor(_saved.FovDeg, _magnification);

    // Diagnostic only, and inside the engine's frame pass -- so it is caught here rather than
    // relying on the caller's catch, which would drop the whole pose for that frame and turn a
    // logging fault into a visible one.
    try { ProbeAim(head, eye, forwardEcl, fovDeg); } catch { /* a probe must never cost a frame */ }
```

## Complete example

- [src/KSArmory/Ksa/OpticalHead.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/OpticalHead.cs), [src/KSArmory/Ksa/OpticParts.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/OpticParts.cs), [src/KSArmory/Ksa/LauncherPart.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/LauncherPart.cs#L894-L972), [src/KSArmory/Ksa/SightCamera.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/SightCamera.cs)
- [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L1109-L1170): the window

## Variations and gotchas

- **Mast against roll-nod.** A pod's roll gimbal has a keyhole on its centreline where the roll angle is undefined; the command is kept at least 4 degrees off it, and the camera's up derotates so the side the pod hangs from stays at the top ([Optical director heads with mast and roll-nod gimbals](../../../../mods/ksarmory/features/optical-director-heads.md)).
- **Levelling.** `OpticConfig.StabiliseHorizon` swaps the camera's roll reference for the site's local vertical, carried between frames near the vertical ([approach 5](../../controllers/drive-the-main-camera-in-fixed-mode/overview.md)).
- **Roster key.** Heads are keyed on (craft, ordinal) because a `Part` reference does not survive the part-tree rebuilds of staging and docking.
- **Subpart refusals.** Each write has its own refusal latch: a refused head freezes, a refused collar is only cosmetic.

## See also

- [Topic overview](overview.md); [approach 1](mount-a-camera-on-a-part-1.md), [3](mount-a-camera-on-a-part-3.md)
- [Optical director heads with mast and roll-nod gimbals](../../../../mods/ksarmory/features/optical-director-heads.md), [Director camera windows on leased secondary viewports](../../../../mods/ksarmory/features/director-camera-window.md)
- [Lease a secondary viewport for a mod camera and show it in a window](../borrow-a-secondary-viewport/overview.md), [Aim a secondary viewport camera, rebuild its matrices and give it its nearby body](../aim-a-secondary-viewport-camera/overview.md), [Keeping an anchor on a part across staging, docking, editor changes and saves](../../../vehicles-and-parts/part-geometry/anchor-to-parts-across-vehicle-changes/overview.md)
