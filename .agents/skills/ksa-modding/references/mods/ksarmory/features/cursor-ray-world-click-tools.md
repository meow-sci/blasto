---
type: Mod Feature
title: Cursor-ray picking and world click tools
description: The ImGui cursor becomes an Ecl ray through the viewport under it (Camera.ScreenToEgoRay), hit-tested against hulls (Part.RayCastEgo) and terrain, driving fire-at-spot, shift-click lock and test-burst tools guarded by WantCaptureMouse and still run when the UI is hidden
mod: ksarmory
category: ui
techniques: [starmap-lifecycle, harmony-prefix, cursor-ray, mesh-raycast, imgui-input, terrain-sampling]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame]
game_types:
  - KSA.Camera
  - KSA.IGameViewport
  - KSA.ViewportRegistry
  - KSA.Part
  - KSA.Vehicle
  - KSA.Celestial
  - KSA.Program
  - Brutal.ImGuiApi.ImGui
  - Brutal.ImGuiApi.ImGuiIO
tags: [mouse, picking, raycast, cursor, designation, click, imgui-io, terrain-ray, viewport]
---

KSArmory lets the operator act on the 3D world with the mouse:

| Tool | Gesture | Effect |
|---|---|---|
| `Designator` (per system, `SystemConfig.MouseFire`) | left-click | Fire the selected weapon at the ground under the cursor (lifted by the round's fireball radius); with no ground under the cursor a gun still bursts |
| `TargetLock` (always available) | shift + left-click | Lock the shown installation's turret, every station of its weapon and its director onto the craft under the cursor, else onto a body-anchored ground point |
| `BurstTool` (debug, `Config.BurstTool`) | left-click | Detonate a test warhead of the dialled charge or nuclear yield on the ground under the cursor |
| `CraftMover` (debug) | click | Pick up and set down a craft (see the spawn and teleport feature) |

All of them start from one function, `KsaWorld.TryCursorRayEcl`, which turns `ImGui.GetMousePos()`
into a world ray through the visible viewport the cursor is over, and then resolve what the ray
hits: craft hulls by the engine's own mesh ray cast, ground by marching the height field.[^ksarmory-ksaworld]

## How it works

**Screen to ray** (`TryCursorRayEcl`):

1. Cursor from `ImGui.GetMousePos()` (absolute screen pixels across all windows).
2. For each visible `IGameViewport` in `ViewportRegistry.GameViews`, `CursorAim.TryToFramebuffer`
   subtracts `viewport.Position`, rejects a cursor outside `[0, width) × [0, height)`, and scales into
   `camera.FramebufferSize` pixels.[^ksarmory-cursoraim]
3. `camera.ScreenToEgoRay(local).Direction`; origin is `camera.EgoToEcl(0)` (the camera position).
   Directions that are zero or non-finite are rejected (`IsUsableDirection`).

**What is under the ray.**

| Query | Method | Notes |
|---|---|---|
| Craft | `TryCursorCraftHit`: bounding-sphere prefilter (`Picking.TryHitSphere`, near root), then `Part.RayCastEgo` on every part with `craft.GetMatrixAsmb2Ego(camera)` and a camera-origin `Ray` | Hull distance, so a fighter over a hillside is picked by its triangles; sphere only for meshless craft (kittens) |
| Ground | `TryCursorGroundPoint` → `TryRayGround`: per body, `TerrainRay.TryFirstHit` marches the part of the ray below the body's highest terrain against `GetTerrainHeightFromDirCce(accurate: true)` | Returns point, latitude/longitude (`GetLatitudeFromCce`, `GetLongitudeFromCce`) and body id[^ksarmory-terrainray] |
| Craft vs ground | `CraftUnderCursor` returns the craft only if no ground is nearer along the ray | |
| Fallback pick | `Picking.NearestWithin`: nearest projected craft centre within 24 px | For craft too small to land on |
| Aim from a mount | `CursorAim.TryAimFromMount(origin, dir, range, mount)` | Bearing from the launcher to the point at `range` along the ray (parallax-correct), cached once per frame (`SolveCursorAim`) |

**Ground anchors.** A locked ground point is stored body-fixed (`TryAnchorToGround`: nearest body by
height above its mean sphere, `Conjugate(GetBodyFixed2Ecl()) × (p − centre)`), so it stays on the
spot as the planet moves and spins.

**Click handling** (every tool):

- `if (ImGui.GetIO().WantCaptureMouse) return;` so a click on a panel window is not a world click.
- Plain-click tools return on `KeyShift`, because shift-click is the lock gesture.
- `ImGui.IsMouseClicked(ImGuiMouseButton.Left, repeat: false)` for one event per press.
- Only the installation the panel is showing reads the cursor (`_roster.For(_ui.Focused)`), so one
  click never fires or re-aims every launcher in the world.[^ksarmory-mod]

**Feedback** (UI pass only): `Designator.Draw` paints a depth-tested ring on the ground and a line to
the lifted aim point, amber when the shot would go and red when not loaded, out of reach or outside
the seeker's gimbal; `TargetLock.Draw` puts a ring-and-ticks reticle with name, range and closing
speed on the designated thing (on a full-screen `NoInputs` overlay window); `BurstTool.Draw` shows the
lethal-radius sphere, hidden for the flash duration after a burst.[^ksarmory-designator][^ksarmory-targetlock][^ksarmory-bursttool]

**What a click does to a weapon.** Designator calls `WeaponSystem.FireAt(point)` (a gun bursts, a
missile is refused past `Munition.MaxRange`); TargetLock calls `WeaponSystems.DesignateWeapon`, which
`Point`s every station of the weapon and sends any steerable store already in the air, and
`OpticalHead.Designate`.[^ksarmory-weaponsystem][^ksarmory-weaponsystems]

## Lifecycle and timing

`KSArmoryMod.UpdateWorldClicks` (KSArmoryMod.cs L410-L431) is called from two places:[^ksarmory-mod]

| Caller | When | Position in `Program.OnFrame` |
|---|---|---|
| `OnAfterGui` (L601-L605) | `KsaWorld.InFlight`, after `_ui.Draw()` and the overlays | `[StarMapAfterGui]`, postfix on `OnDrawUiViewports`, Program.cs L2268 |
| End of `StepOnce` (L351) | `!KsaWorld.UiDrawn && KsaWorld.InFlight` | Harmony prefix on `Program.OnFrameCelestials` (L2320) or `[StarMapAfterOnFrame]`, both after `ImGui.Render()` (L2286) |

Why both work: `PrepareImGui` (Program.cs L2261) starts the ImGui frame, and with it the mouse state,
**before** the `if (DrawUI)` gate at L2265, so `GetMousePos`, `IsMouseClicked` and `GetIO()` answer
for this frame even when no UI is drawn; with the UI hidden no window can want the mouse. Only the
click handling moves to the step; the markers and rings are drawn only in the GUI pass.[^ksa-program]

What the ray and the hits read: the camera was set up in `OnFrameViewports` (L2263) this frame, and
craft and body positions are the end-of-step state applied in `PrepareFrame`
(`Universe.ApplyVehicleSolvers`). `KsaWorld.BeginFrame()` at the top of `StepOnce` clears the
per-frame cursor-aim cache, so every mount on mouse aim shares one terrain march per frame. A
designation or shot made here is consumed by the next simulated step (the round is created now and
first integrated next frame; turret drives move in the step after).

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L4113-L4150) | `TryCursorRayEcl`: cursor to Ecl ray through the right viewport |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L4152-L4210) | `TryCursorAimEcl`, `TryCursorGroundEcl`, `BeginFrame` |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L4415-L4425) | `CraftUnderCursor`: hull first unless ground is nearer |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L1065-L1095) | `TryCursorGroundPoint` with latitude, longitude, body |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L932-L960) | `TryAnchorToGround`: body-fixed anchor |
| [src/KSArmory/Sim/CursorAim.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/CursorAim.cs) | Screen to viewport to framebuffer, aim from mount, dead zone |
| [src/KSArmory/Sim/Picking.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Picking.cs) | Ray-sphere near root, nearest on screen |
| [src/KSArmory/Sim/TerrainRay.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/TerrainRay.cs) | First terrain hit along a ray |
| [src/KSArmory/Ksa/Designator.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Designator.cs) | Click to fire at the ground; aim marker |
| [src/KSArmory/Ksa/TargetLock.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/TargetLock.cs) | Shift-click lock; designation reticle |
| [src/KSArmory/Ksa/BurstTool.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/BurstTool.cs) | Click to detonate a test burst |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L407-L431) | `UpdateWorldClicks`, scoped to the focused installation |

## Snippets

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L4113-L4144
    public static bool TryCursorRayEcl(out double3 originEcl, out double3 directionEcl)
    {
        originEcl = default;
        directionEcl = default;
        try
        {
            float2 cursor = ImGui.GetMousePos();

            ReadOnlySpan<IGameViewport> viewports = GameViewports;
            for (int i = 0; i < viewports.Length; i++)
            {
                IGameViewport v = viewports[i];
                if (!v.Visible) continue;

                if (v.GetCamera() is not { } camera) continue;

                // Framebuffer pixels, not viewport pixels: ScreenToEgoRay divides by the camera's
                // own framebuffer, and a render or display scale makes those different sizes.
                if (!CursorAim.TryToFramebuffer(cursor, v.Position, v.Width, v.Height,
                                                camera.FramebufferSize.X, camera.FramebufferSize.Y,
                                                out float2 local))
                {
                    continue;
                }

                double3 direction = camera.ScreenToEgoRay(local).Direction;
                if (!CursorAim.IsUsableDirection(direction)) continue;

                originEcl = camera.EgoToEcl(Vec.Zero);
                directionEcl = Vec.Unit(direction);
                return Vec.IsFinite(originEcl);
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

```csharp
// src/KSArmory/Ksa/KSArmoryMod.cs L351-L355
        using (_budget.Measure("cameras")) DriveCameras(dtPlayer);

        // The UI pass reads clicks when it runs; with the UI hidden it does not run at all.
        if (!KsaWorld.UiDrawn && KsaWorld.InFlight) UpdateWorldClicks();
    }
```

## Game APIs touched

| API | Decompiled file | Use |
|---|---|---|
| `Camera.ScreenToEgoRay(float2)`, `Camera.FramebufferSize`, `Camera.EgoToEcl` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L704-L725) | Unproject the cursor (`FramebufferSize` L47, `EgoToEcl` L223)[^ksa-camera] |
| `Camera.Resize`, `GameViewport` resize | [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L266-L267) | Viewport resize sets the camera framebuffer size (Camera.cs L464-L469)[^ksa-gameviewport] |
| `Part.RayCastEgo`, `Vehicle.GetMatrixAsmb2Ego(Camera)` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L2534) | Hull hit along the cursor ray (Vehicle.cs L1270)[^ksa-part][^ksa-vehicle] |
| `Celestial.GetTerrainHeightFromDirCce`, `GetLatitudeFromCce`, `GetLongitudeFromCce`, `GetBodyFixed2Ecl` | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L691-L777) | Ground hit, coordinates, body-fixed anchor (`GetBodyFixed2Ecl` L174)[^ksa-celestial] |
| `ImGui.GetMousePos`, `ImGui.IsMouseClicked`, `ImGui.GetIO` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L8196) | Click detection (`GetIO` L5438)[^brutal-imgui] |
| `ImGuiIOPtr.WantCaptureMouse`, `ImGuiIOPtr.KeyShift` | [current/decomp/Brutal.ImGuiApi/ImGuiIOPtr.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGuiIOPtr.cs#L140) | Panel owns the mouse; shift gesture[^brutal-imguiioptr] |
| `Program.PrepareImGui`, `Program.DrawUI` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2261-L2269) | Input is current before the UI gate |

## Pitfalls and version notes

- **Screen vs framebuffer coordinates.** ImGui's cursor spans every window; the camera unprojects in
  its own framebuffer. Subtract the viewport origin, or a second viewport aims off by a constant.
  The mod's note that viewport size and framebuffer size are always equal in KSA matches
  `GameViewport` calling `BaseCamera.Resize(newSize)` (GameViewport.cs L266); the scale is kept as
  insurance.
- **Camera bearing is not mount bearing.** Pointing a turret down the camera's ray is right only at
  infinity; solve the point at range and take the bearing from the mount.
- **Pick hulls, not spheres.** A craft's mean-radius sphere stands metres off the hull; prefilter with
  it, then use `Part.RayCastEgo`.
- **No structure surfaces.** Ground hits are terrain only; per docs/BLOCKED-ON-KSA.md there is no
  engine query for a launch pad's surface, and adding the pad as a thicker planet swung bearings
  wildly at its edge.[^ksarmory-blocked]
- **Clicks with the UI hidden.** Without the step-side call, hiding the HUD (F2) would silently
  disable every click tool; the markers that show the result stay hidden.
- **Shift is reserved.** Plain-click tools ignore shift-clicks so a lock does not also fire.
- **KSA's own click handling** (vehicle selection and hover outlines) is not suppressed by the mod;
  whether a world click is also consumed by the game was not checked.

## See also

- [World-anchored screen markers](./world-anchored-screen-markers.md)
- [Spawn and teleport test craft](./spawn-and-teleport-test-craft.md)
- [Fire control and target allocation](./fire-control-and-target-allocation.md)
- [Ground target designation and reach](./ground-target-designation-and-reach.md)
- [Optical director heads](./optical-director-heads.md)
- [Depth-painted ground rings](./depth-painted-ground-rings.md)
- [Gizmo world overlays](./gizmo-world-overlays.md)
- [KSA explosions from a point](./ksa-explosions-from-a-point.md)
- [Terrain ground impact test](./terrain-ground-impact-test.md)
- [Dual-hook simulation step](./dual-hook-simulation-step.md)
- [Share input with the game: ignore clicks ImGui owns and keep working with the HUD hidden](../../../howto/ui/input/coordinate-imgui-focus-with-game-input/coordinate-imgui-focus-with-game-input-2.md) - how-to article demonstrating this feature
- [Cursor to world ray: ScreenToEgoRay with viewport offset and framebuffer scale](../../../howto/ui/input/cursor-to-world-ray/cursor-to-world-ray-1.md) - how-to article demonstrating this feature
- [Pick objects under the cursor: Part.RayCastEgo hull test plus a terrain march](../../../howto/ui/input/pick-objects-under-the-cursor/pick-objects-under-the-cursor-2.md) - how-to article demonstrating this feature
- [Run per-frame mod logic once per frame from whichever hook fires first (FrameLatch, pre-render prefix and sim-time steps)](../../../howto/ui/overlays/draw-ui-while-the-hud-is-hidden/draw-ui-while-the-hud-is-hidden-4.md) - how-to article demonstrating this feature

[^ksarmory-ksaworld]: src/KSArmory/Ksa/KsaWorld.cs
[^ksarmory-cursoraim]: src/KSArmory/Sim/CursorAim.cs
[^ksarmory-terrainray]: src/KSArmory/Sim/TerrainRay.cs
[^ksarmory-mod]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksarmory-designator]: src/KSArmory/Ksa/Designator.cs
[^ksarmory-targetlock]: src/KSArmory/Ksa/TargetLock.cs
[^ksarmory-bursttool]: src/KSArmory/Ksa/BurstTool.cs
[^ksarmory-weaponsystem]: src/KSArmory/Ksa/WeaponSystem.cs
[^ksarmory-weaponsystems]: src/KSArmory/Ksa/WeaponSystems.cs
[^ksarmory-blocked]: docs/BLOCKED-ON-KSA.md
[^ksa-program]: KSA.Program (decompiled)
[^ksa-camera]: KSA.Camera (decompiled)
[^ksa-gameviewport]: KSA.GameViewport (decompiled)
[^ksa-part]: KSA.Part (decompiled)
[^ksa-vehicle]: KSA.Vehicle (decompiled)
[^ksa-celestial]: KSA.Celestial (decompiled)
[^brutal-imgui]: Brutal.ImGuiApi.ImGui (decompiled)
[^brutal-imguiioptr]: Brutal.ImGuiApi.ImGuiIOPtr (decompiled)
