---
type: Mod Feature
title: Optical director heads with mast and roll-nod gimbals
description: Optical directors are inert parts whose head and pod roll collar are subparts slewed by a rate-limited drive, clamped to a mast elevation band or a roll-nod annulus with a keyhole, and resolved into an eye, sight line and roll for camera and laser
mod: ksarmory
category: vehicles-and-parts
techniques: [subpart-transform-writes, rate-limited-drive, gimbal-geometry, part-roster-by-ordinal, xml-asset]
hooks:
  - starmap StarMapAfterGui (via StepOnce)
  - harmony-prefix KSA.Program.OnFrameCelestials (via StepOnce, HUD hidden)
  - subclass-override KSA.FixedController.OnFrame (view re-solved through SightCamera.TryPose)
lifecycle: [frame:draw-ui, frame:celestials, frame:viewports, frame:render]
game_types: [KSA.Part, KSA.Vehicle, KSA.PartTree]
tags:
  - optical-director
  - targeting-pod
  - gimbal
  - keyhole
  - subparts
  - laser-rangefinder
  - litening
  - sensor
  - eo-director
---

An optical director in KSArmory is a sensor and a camera mount in one part: it finds its own targets,
slews its head onto them, and is what the sight, the camera windows and the laser look through. There
is no KSA part module behind it. Each director is an inert part whose moving bodies are `<SubPart>`
entries, and the mod writes their parent-relative transforms every simulated step from a drive it
integrates itself, the same subpart technique the mod uses for turrets.[^ksarmory-opticalhead][^ksarmory-opticparts]

Three heads ship: a standalone **EO Director** on a mast, the Pantsir's **1TPP1** director on its
turret roof (the same instrument inside the launcher part, riding the traverse), and a **LITENING
targeting pod** on a roll-nod gimbal with a laser rangefinder and designator.[^ksarmory-arsenal]
A director needs no weapon on its craft: a hull carrying one is an observation post.[^ksarmory-opticalheads]

## How it works

### Parts and profiles

| Profile field (`OpticProfile`) | EO Director | 1TPP1 (Pantsir) | LITENING |
|---|---|---|---|
| `PartId` | `KSArmory_Prefab_Optic` | `KSArmory_Prefab_Launcher6` | `KSArmory_Prefab_Litening` |
| `Gimbal` | `Mast` | `Mast` | `RollNod` |
| Subpart markers (base / roll / head) | `Optic_Base` / none / `Optic_Head` | `Launcher_OpticBase` / none / `Launcher_OpticHead` | `Litening_Body` / `Litening_Roll` / `Litening_Head` |
| `HeadPivot` (offset from the base) | (0.63, 0, 0) | (0.63, 0, 0) | (0.26669, 0.94282, 0) |
| Travel | -20 to 85 degrees elevation | -20 to 85 | 4 to 150 degrees off the centreline |
| `SlewRateDeg` | 90 (default) | 90 (default) | 140 |
| `MaxMagnification` | 16 (default) | 16 (default) | 53 |
| Laser | no | no | yes, 20 km (default `LaserRangeMetres`) |

Profiles live in `Sim/Arsenal.cs`; `Catalogue.OpticForPart` maps a part Id to one, and a weapon pack
can add more.[^ksarmory-arsenal][^ksarmory-opticprofile][^ksarmory-catalogue] The part frame convention
is `+X` out of the mounting face (`OpticGeometry.MountNormal`), `+Y` along the host (`RestDirection`,
the face the window is modelled on) and `+Z` the host's right.[^ksarmory-opticgeometry][^ksarmory-assets-xml]
In `KSArmoryAssets.xml` the moving meshes are exported recentred on the pivot and the asset puts the
offset back; the pod's roll collar and window share one pivot because they turn on one bearing, and
the pod ships with its rail as a subpart. Mass, colliders and editor tags (`Sensors`, `Radial`) are in
`KSArmoryGameData.xml`. The mod's `tools/validate-parts.py` checks that the pivot agrees across the
model script, the XML and the C#.[^ksarmory-assets-xml][^ksarmory-gamedata-xml][^ksarmory-validate-parts]

### The roster (`OpticalHeads`)

- One `OpticalHead` with its own `OpticConfig` per director, keyed on **(craft, ordinal)**: the part's
  index among directors on that craft, which survives the part-tree rebuilds of staging and docking
  where a `Part` reference does not. `OpticParts.FindAll` walks `Vehicle.Parts.Parts`.[^ksarmory-opticalheads][^ksarmory-opticparts][^ksa-vehicle][^ksa-parttree]
- A craft that is nothing but a pod (left lying after a break-up) gets no head.
- A director carried onto another craft by a decoupler is followed by part Id and handed over with
  its settings, designation and drive intact (`Rehome`); one not found anywhere for 120 consecutive
  searches is dropped, because a tree read mid-rebuild also returns nothing.[^ksarmory-opticalheads][^ksarmory-opticalhead]
- `Driving(craft)` returns the head that holds a view (lowest ordinal), else the first: the one the
  panel's rows, the sight and shift-click locking act on.[^ksarmory-opticalheads]

### One step of a head

`SampleWorld` runs every pass, before anything is drawn: it records the platform's position, finds
the director, head and roll subparts by marker (`Part.SubParts`, Id substring match), and resolves the
sensor boresight (the mount normal carried into Ecl) and `Boresight` (local up at the platform).[^ksarmory-opticalhead][^ksarmory-opticparts][^ksa-part]

`Update(dt)` runs only on frames that advanced simulated time, on the unclamped step:[^ksarmory-mod][^ksarmory-opticalhead]

1. Hold the magnification to the optic's own maximum.
2. Scan with its own `Radar` and sensor profile (search cone out of the mounting face).
3. Choose the aim in the part frame, in fixed precedence: **mouse aim** (the cursor's bearing from the
   head's pivot, at a rate scaled by how far past the dead zone it is) > **hand** sliders >
   **designation** (a craft's drawn centre, or a ground point re-anchored every frame) > **tracking**
   the set's watched contact (drawn position, from the pivot) > **rest** (a mast head along the host,
   a pod looking out of its mounting face, since along the host is its keyhole). The sight's
   "why it is not on the contact" text (`OpticFollow.Why`) walks the same precedence.[^ksarmory-opticalhead][^ksarmory-opticgeometry][^ksarmory-optichold]
4. `PointingDrive.Update`: the shortest rotation onto the command, at most `rate * dt`, which has no
   singularity looking straight up; `OnTarget` below 0.0175 rad.[^ksarmory-pointingdrive]
5. Clamp what the drive reached to the travel again: between two legal directions the shortest arc
   can pass through the mast.
6. Write the roll collar (`OpticGeometry.RollPose`, a rotation about the pod's centreline by
   `RollAngleRad`) and then the head (`OpticGeometry.Pose`: position `mount.ToPart(HeadPivot)`,
   rotation carrying `RestDirection` onto the aim). Each write has its own refusal latch: a refused
   head freezes and stops reporting `OpticOnTarget`, a refused collar is only cosmetic.[^ksarmory-opticparts][^ksarmory-opticgeometry]
7. **MASK**: a ray from the eye towards what it follows (or along its axis) against its own craft.
8. **Laser**: if lasing and not masked, a ray against other craft and the ground out to
   `LaserRangeMetres`; the hit becomes a `LaserSpot` with a pulse code, published for seekers (no round
   homes on it yet, see [seekers and laser designation](./seekers-uplink-and-laser-homing.md)).[^ksarmory-opticalhead]

### Mast against roll-nod

| | Mast (`GimbalKind.Mast`) | Roll-nod (`GimbalKind.RollNod`) |
|---|---|---|
| Travel | An elevation band over the mounting face; the clamp keeps the bearing and moves only elevation (`ClampToTravel`) | An annulus about the pod's centreline, `KeyholeDeg` to `MaxOffBoresightDeg`; the clamp keeps the roll plane and moves only the nod (`ClampOffBoresight`) |
| Bodies written | Head | Roll collar and head, same pivot |
| Mesh roll reference | Mount normal | Minus the mount's forward |
| Camera up (levelling off) | The head's own up | The other end of the head's up: **derotation**, so the side the pod hangs from stays at the top of the picture however far the nose has rolled |
| Hand controls | Bearing and elevation | Nose roll and nod off boresight |
| Warning | none | `GIMBAL LIMIT` within 0.5 degrees of either stop |

The **keyhole** is the pod's singularity: on its centreline the roll angle is undefined, and a target
crossing near it asks the collar for an unbounded roll rate, as an alt-azimuth mount does at zenith.
Keeping the command at least 4 degrees off the axis keeps the collar's angle continuous.[^ksarmory-opticprofile][^ksarmory-opticgeometry][^ksarmory-opticalhead]
`OpticConfig.StabiliseHorizon` swaps the camera's roll reference for the site's local vertical,
carried from frame to frame near the vertical.[^ksarmory-opticconfig]

### Riding something: the mount frame

Where the base is comes from the engine, not from the mover: `OpticParts.MountOf` reads the base
subpart's `PositionParentAsmb` and `Asmb2ParentAsmb`, so the 1TPP1 rides the Pantsir's traverse and the
pod sits on its rail with no extra C#. The head's rotation is absolute in the part frame (turning the
base under a head already aimed must not turn the head); the mount contributes position and the roll
reference.[^ksarmory-opticparts][^ksarmory-opticgeometry]

### Eye, line of sight and roll for consumers

`IOpticalHead` is the read-only slice the sight, the camera owners and the claim ladder take.[^ksarmory-roles]
`TryOpticViewEclAt(platformEcl, ...)` puts the eye `EyeForward` metres along the aim from the pivot
and carries both through the craft's part transforms against a platform position **the caller
supplies**: the engine's own sample inside the viewport pass for the main view
(`SightCamera.TryPose`), the window camera's drawn centre for a camera window. While the drive is on
target and following a designation or a track, the line of sight is re-solved onto that target's
drawn position at that instant, removing a frame of target motion that grows with simulation speed.[^ksarmory-opticalhead][^ksarmory-sightcamera]
The aim is deliberately not extrapolated: the mod measured the residual at 0.007 degrees per unit of
simulation speed and a lead from the drive's last turn at 0.35 degrees of jitter at 10x.[^ksarmory-opticalhead]
`RollReferenceEcl` is resolved on every read rather than sampled, so the camera's up and forward come
from the same instant.

### Panel rows (`UiOptic`)

Camera window, main view and terrain map buttons; the laser switch with what it is ranging; `Track`,
`Mouse` and `Hand`; the dead-zone slider; hand sliders named for the gimbal; magnification detents
for the main view; and under Settings the symbology and level-horizon switches, the laser code (four
digits, 1111 to 1788) and "never look at the vehicle I'm flying". A designation shows as "Watching"
with a Release button, the only way to clear a ground designation.[^ksarmory-uioptic]

## Lifecycle and timing

| Value | Where (build 5554) | What runs |
|---|---|---|
| `frame:draw-ui` | `[StarMapAfterGui]` postfix on `OnDrawUiViewports` (Program.cs L2268) runs `StepOnce` and `StepSimulation`: `_heads.Sync` (KSArmoryMod.cs L689-L695), `_heads.SampleWorld()` (L705), and inside the simulated-step gate `_heads.Update(dtSim, _airborne)` (L795) | Roster, sampling, scan, drive, transform writes, mask, laser[^ksarmory-mod][^ksa-program] |
| `frame:celestials` | HUD hidden: Harmony prefix on `Program.OnFrameCelestials` (L2320) runs `StepOnce` | The same, before the render |
| `frame:viewports` (next frame) | The main view's controller asks `SightCamera.TryPose`, which calls `TryOpticViewEclAt` with the engine's position for the followed craft | Line of sight re-solved in the pass that draws it[^ksarmory-sightcamera] |
| `frame:render` | Subpart transforms written in the hook are drawn the same frame: the setters invalidate the tree's render data (`Part.ResetCachedPosMatrixValues` calls `Tree?.RenderData.InvalidateTransforms()`, Part.cs L1241-L1249) | Head and collar move on screen this frame[^ksa-part] |
| `frame:draw-ui` | `_ui.Draw()` after `StepOnce` | `UiOptic` rows |

Heads update on the unclamped simulated step: the clamp that keeps a round from stepping over its
fuse would make a rate-limited drive under-advance on alternate frames above about 13x, which shows as
picture shake. `SmoothedStep` evens out the display's frame pacing for the drive.[^ksarmory-mod][^ksarmory-opticalhead]

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Ksa/OpticalHead.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/OpticalHead.cs#L224-L298) | `SampleWorld`, `Update` |
| [src/KSArmory/Ksa/OpticalHead.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/OpticalHead.cs#L306-L386) | Mask, gimbal limit, laser |
| [src/KSArmory/Ksa/OpticalHead.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/OpticalHead.cs#L447-L513) | `TryOpticViewEclAt` and the drawn-target re-solve |
| [src/KSArmory/Ksa/OpticalHead.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/OpticalHead.cs#L554-L787) | Aim precedence, designation, cursor aim, roll reference |
| [src/KSArmory/Ksa/OpticalHeads.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/OpticalHeads.cs) | Roster by craft and ordinal, decoupler hand-over |
| [src/KSArmory/Ksa/OpticParts.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/OpticParts.cs) | Find parts and subparts, mount frame, transform writes, view |
| [src/KSArmory/Sim/OpticGeometry.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/OpticGeometry.cs) | `MountFrame`, poses, roll angle, rest aim, travel clamps |
| [src/KSArmory/Sim/OpticProfile.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/OpticProfile.cs) | `GimbalKind`, profile fields |
| [src/KSArmory/Sim/OpticConfig.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/OpticConfig.cs) | Per-head settings |
| [src/KSArmory/Sim/OpticHold.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/OpticHold.cs) | Why a head is not on its contact, in aim precedence order |
| [src/KSArmory/Sim/Arsenal.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Arsenal.cs#L1207-L1330) | `EoDirector`, `PantsirDirector`, `PodSensor`, `Litening` |
| [src/KSArmory/KSArmoryAssets.xml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/KSArmoryAssets.xml#L1091-L1201) | The two director `<Part>`s and their subparts |
| [src/KSArmory/Ksa/Ui/UiOptic.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Ui/UiOptic.cs) | Panel rows |

## Snippets

```csharp
// src/KSArmory/Ksa/OpticalHead.cs L255-L297
    public void Update(double dt, IReadOnlyList<IContact>? airborne = null)
    {
        if (Platform is not { IsDisposed: false } platform || Director is null) return;

        // The one place it is held to this optic's own: the panel, the window and the bridge all write it.
        _policy.Magnification = Math.Clamp(_policy.Magnification, SightZoom.MinMagnification,
                                           SightZoom.Clamp(Profile.MaxMagnification));

        Radar.Sensor = Sensor;
        Radar.Scan(platform, SensorBoresight, dt, airborne);
        // ...
        double3 aim = AimPartFrame();

        _drive.Update(_driveStep.Next(dt), aim, Profile.SlewRateRad * _mouseRate);
        // ...
        _drive.Hold(OpticGeometry.ClampToTravel(Profile, Mount, _drive.Direction));
        // ...
        if (RollPart is { } shell && _rollWorks
            && !OpticParts.TryApplyRoll(shell, Profile, Mount, AimWhenDrawn))
        {
            _rollWorks = false;
            Log.Warn("optic: the engine refused the roll gimbal's transform; the nose is frozen "
                     + "where it stopped and the head goes on aiming");
        }

        UpdateMask(platform);
        UpdateLaser(platform);

        if (OpticPart is not { } head || !_driveWorks) return;

        if (!OpticParts.TryApplyAim(head, Profile, Mount, AimWhenDrawn))
        {
            _driveWorks = false;
            Log.Warn("optic: the engine refused the head's transform; it is frozen where it stopped");
        }
```

```csharp
// src/KSArmory/Sim/OpticGeometry.cs L361-L380
    public static double3 ClampOffBoresight(OpticProfile profile, MountFrame mount,
                                            double3 aimPartFrame)
    {
        double3 aim = Vec.Unit(aimPartFrame);
        if (Vec.Len2(aim) < 0.5) return aimPartFrame;

        double3 axis = mount.Forward;

        double off = OffBoresightRad(mount, aim);
        double keyhole = Math.Max(0.0, float.DegreesToRadians(profile.KeyholeDeg));
        double reach = Math.Clamp(float.DegreesToRadians(profile.MaxOffBoresightDeg), keyhole, Math.PI);

        double wanted = Math.Clamp(off, keyhole, reach);
        if (Math.Abs(wanted - off) < 1e-9) return aim;

        double3 across = Vec.RejectFrom(aim, axis);
        if (Vec.Len2(across) < 1e-12) return aim;

        return Vec.Unit(Vec.Unit(across) * Math.Sin(wanted) + axis * Math.Cos(wanted));
    }
```

```csharp
// src/KSArmory/Ksa/OpticParts.cs L138-L155
    private static bool TryApplyPose(Part body, DrivePose pose, string what)
    {
        try
        {
            body.Asmb2ParentAsmb = pose.Rotation;
            body.PositionParentAsmb = pose.Position;

            // Part caches its matrices; without this the new value is stored and ignored.
            body.ResetCachedPosMatrixValues();

            return true;
        }
        catch (Exception e)
        {
            Log.Warn($"optic {what} transform: {e.Message}");
            return false;
        }
    }
```

```csharp
// src/KSArmory/Sim/Arsenal.cs L1300-L1330
    public static readonly OpticProfile Litening = new()
    {
        PartId = "KSArmory_Prefab_Litening",
        DisplayName = "LITENING",
        Sensor = PodSensor.Name,
        Gimbal = GimbalKind.RollNod,

        BaseMarker = "Litening_Body",
        RollMarker = "Litening_Roll",
        HeadMarker = "Litening_Head",

        HeadPivot = new(0.26669, 0.94282, 0.00000),
        // ...
        EyeForward = 0.223f,
        // ...
        SlewRateDeg = 140f,

        MaxOffBoresightDeg = 150f,
        KeyholeDeg = 4f,
        // ...
        MaxMagnification = 53f,
        // ...
        HasLaser = true,
    };
```

## Game APIs touched

| API | Decompiled file | Use |
|---|---|---|
| `Part.PositionParentAsmb`, `Asmb2ParentAsmb` (setters call `ResetCachedPosMatrixValues`) | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L756-L781) | Writing head and collar poses; reading the base's pose as the mount |
| `Part.ResetCachedPosMatrixValues` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1241-L1249) | Invalidates cached matrices and the tree's render transforms |
| `Part.SubParts`, `Part.Id` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1127) | Finding base, roll and head by marker |
| `Vehicle.Parts` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L605) | The craft's part tree |
| `PartTree.Parts` | [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L136) | Directors in part order |
| `Program.OnFrame` phases | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2238-L2335) | Where the step and the render fall |

## Pitfalls and version notes

- **Clamp twice.** Clamping only the command lets the drive take the shortest rotation between two
  legal directions, which at low elevation and opposite bearings passes through the mast.[^ksarmory-opticalhead]
- **Never let the command into the keyhole.** On a roll-nod head the collar's angle is undefined on
  the axis; without `KeyholeDeg` the nose spins as the aim creeps past it.[^ksarmory-opticprofile][^ksarmory-opticgeometry]
- **A head on a moving mount reads it a frame late.** `SampleWorld` runs before the weapon updates
  the traverse in the same step, and the viewport pass runs before the hook. On the Pantsir the
  traverse axis is the mount normal, so only the eye's position lags (the mod measured 19 cm at 0.93 m
  off the axis, 70 degrees per second and a 165 ms step); a director on another kind of mover would
  have its aim and roll a frame stale too.[^ksarmory-opticalhead]
- **Redundant cache reset in 5554.** The `PositionParentAsmb` and `Asmb2ParentAsmb` setters already
  call `ResetCachedPosMatrixValues`, so `TryApplyPose`'s explicit call is harmless but no longer needed
  (Part.cs L756-L781).[^ksa-part][^ksarmory-opticparts]
- **Subpart lookup is a substring match** on the subpart Id (`KSArmory_Optic_Head` contains
  `Optic_Head`), case-insensitive; a renamed subpart shows as "head subpart not found" in the panel.[^ksarmory-opticparts][^ksarmory-uioptic]
- **A refused write is latched.** A head whose transform write throws reports itself frozen and off
  target, so the sight cannot paint a settled bracket over a picture pointing elsewhere; `Reset` clears
  the latches when a head moves to a new craft.[^ksarmory-opticalhead]
- **Laser designation is half-built.** Spots are cast and published every frame, but no guidance mode
  homes on them at this ref.[^ksarmory-opticalhead]

## See also

- [Director camera windows](./director-camera-window.md)
- [Gunner's sight overlay](./gunner-sight-overlay.md)
- [Borrow the main view and hand it back](./borrow-main-view-camera.md)
- [Articulated mount subparts](./articulated-mount-subparts.md)
- [Seekers, uplink and laser designation](./seekers-uplink-and-laser-homing.md)
- [Radar search and lock](./radar-search-and-lock.md)
- [Radar scope and relief map](./radar-scope-and-relief-map.md)
- [Weapon part definitions in XML](./weapon-part-definitions-xml.md)
- [Armoury pack registration API](./armoury-pack-registration-api.md)
- [Resolve the eye, sight line and roll from an optical head sub-part for the main view or a camera window](../../../howto/camera/secondary-viewports/mount-a-camera-on-a-part/mount-a-camera-on-a-part-2.md) - how-to article demonstrating this feature
- [Aim optical director heads on roll-nod and mast gimbals](../../../howto/vehicles-and-parts/parts/define-and-animate-subparts/define-and-animate-subparts-3.md) - how-to article demonstrating this feature

[^ksarmory-opticalhead]: src/KSArmory/Ksa/OpticalHead.cs
[^ksarmory-opticalheads]: src/KSArmory/Ksa/OpticalHeads.cs
[^ksarmory-opticparts]: src/KSArmory/Ksa/OpticParts.cs
[^ksarmory-uioptic]: src/KSArmory/Ksa/Ui/UiOptic.cs
[^ksarmory-opticgeometry]: src/KSArmory/Sim/OpticGeometry.cs
[^ksarmory-opticprofile]: src/KSArmory/Sim/OpticProfile.cs
[^ksarmory-opticconfig]: src/KSArmory/Sim/OpticConfig.cs
[^ksarmory-optichold]: src/KSArmory/Sim/OpticHold.cs
[^ksarmory-pointingdrive]: src/KSArmory/Sim/PointingDrive.cs
[^ksarmory-arsenal]: src/KSArmory/Sim/Arsenal.cs
[^ksarmory-catalogue]: src/KSArmory/Sim/Catalogue.cs
[^ksarmory-roles]: src/KSArmory/Ksa/WeaponSystemRoles.cs
[^ksarmory-mod]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksarmory-sightcamera]: src/KSArmory/Ksa/SightCamera.cs
[^ksarmory-assets-xml]: src/KSArmory/KSArmoryAssets.xml
[^ksarmory-gamedata-xml]: src/KSArmory/KSArmoryGameData.xml
[^ksarmory-validate-parts]: tools/validate-parts.py
[^ksa-part]: KSA.Part (decompiled, build 2026.10.10.5554)
[^ksa-parttree]: KSA.PartTree (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
