---
type: Mod Feature
title: Headlamps and tail lamps as KSA spotlights
description: Submit a car's head and tail lamps as KSA spot lights from a Harmony postfix on PartTree.UpdateRenderData, the only point where per-frame lights survive LightSystem.ClearLights, and keep them in step with the part's own light switch
mod: ksacars
category: rendering
techniques: [harmony-postfix, xml-asset]
hooks: [starmap StarMapAllModsLoaded, harmony-postfix KSA.PartTree.UpdateRenderData, starmap StarMapAfterGui]
lifecycle: [startup:all-mods-loaded, frame:draw-ui, frame:render]
game_types:
  - KSA.PartTree
  - KSA.Vehicle
  - KSA.Program
  - KSA.Part
  - KSA.PowerConsumer
  - KSA.PowerConsumerTemplate
  - KSA.Rendering.Lighting.Light
  - KSA.Rendering.Lighting.ILightSystem
  - KSA.Rendering.Lighting.ClusteredLightSystem
  - KSA.Rendering.Lighting.ELightFlags
  - KSA.IViewport
tags: [rendering, lights, spotlight, harmony, postfix, light-switch, render-data]
---

At night a KSACars car lights the road: two warm dipped or main beams from the headlamps, and a
small red spill behind from the tail lamps that brightens when you brake. The lamps are real KSA
spot lights, handed to the light system each frame for each car and viewport, beside the lights
KSA's own `LightModule`s submit.[^ksacars-buggies] They are switched with the part's own
`<PowerConsumer LightSwitch="true">`, so KSA's light key, its part window and the save file all
agree with the mod's panel.[^ksacars-gamedata]

## How it works

1. **Hook.** `LightsHook.Install` (from `[StarMapAllModsLoaded]`) finds
   `PartTree.UpdateRenderData(ref readonly double4x4, bool, IViewport, int)` with
   `AccessTools.Method` (passing `typeof(double4x4).MakeByRefType()` for the `ref readonly`
   matrix) and adds a postfix. The postfix takes `__instance`, `ref double4x4 matrixAsmb2Ego` and
   `IViewport viewport` by name and calls `Buggies.LightLamps`.[^ksacars-lightshook]
2. **Find the car.** `LightLamps` matches the `PartTree` against each car's `Craft.Parts`. It
   stops if the beam is `Off` or `Part.IsLightSwitchedOff()` is true; that test is false only
   when the part's light switch is active *and* switched on, so a flat battery puts the lamps out
   together with the lenses KSA darkens itself.[^ksacars-buggies][^ksa-part]
3. **Aim.** The car's up (+X) and forward (+Y) come from `Part.Asmb2VehicleAsmb`;
   `Headlights.Aim` tips forward down by the beam's dip. `Shine` rotates that direction by the
   3x3 part of the `asmb2Ego` matrix KSA is drawing the craft with, and transforms each lamp
   position (`BuggyProfile.HeadLamps` / `TailLamps`, in the part frame) to Ego the same way, so
   the beam sits exactly on the lamp as drawn.[^ksacars-headlights][^ksacars-profile]
4. **Submit.** For each lamp:
   `Program.LightSystem.CreateLightInstance(Light.CreateSpotLight(at, aim, range, outer, inner, colour, intensity, flags), viewport)`.
   Headlamps cast soft shadows (`ELightFlags.CastsShadows | SoftShadows`); tail lamps cast none.[^ksa-light][^ksa-elightflags]

| Beam | Range m | Intensity | Inner / outer cone rad | Dip rad | Colour |
|---|---|---|---|---|---|
| Dipped (`Low`) | 45 | 60 | 0.60 / 1.20 | 0.14 | (1.0, 0.90, 0.72) tungsten |
| Main (`High`) | 160 | 400 | 0.14 / 0.40 | 0.02 | same |
| Tail | 7 | 6, x4 braking | 0.5 / 1.1 | 0.35 | (1.0, 0.05, 0.03) |

`Headlights.Braking` is true when the driver asks for the opposite of the way the car is rolling
(faster than 0.5 m/s).[^ksacars-headlights]

**The switch.** Each car's `PartGameData` declares `<PowerConsumer LightSwitch="true">` drawing
0.001 W.[^ksacars-gamedata] KSA resolves that module as `Part.LightSwitch`, flips
`LightIsActive` from its light key (`Vehicle.ToggleLights`) or the module's own "Light Switch"
checkbox, and saves it with the craft.[^ksa-part][^ksa-powerconsumer] Every frame `Buggies.Sync`
runs `Headlights.Reconcile(beam, switchNow, seen)`: if the switch differs from what it read last
frame, KSA moved it and wins (on comes back as dipped); otherwise the mod's beam setting drives the
switch. A car loaded from a save with the switch on starts dipped. Dipped versus main is the
mod's and is not saved.[^ksacars-buggies][^ksacars-headlights-tests]

## Lifecycle and timing

| Moment | Where | Thread |
|---|---|---|
| Lights list emptied | `Program.OnFrame` -> `LightSystem.ClearLights()` (Program.cs L2317), after the GUI pass, `ImGui.Render` and `UpdateGauges` | main |
| Car lamps submitted | `Program.Render` -> `UpdateRenderingResources` -> per visible viewport, per vehicle in frame: `Vehicle.UpdateRenderData` -> `PartTree.UpdateRenderData` -> **postfix** | main |
| KSA's own lamps submitted | same `PartTree.UpdateRenderData`, `LightModule.UpdateRenderData` per module, just before the postfix | main |
| Lights written to the GPU | same `UpdateRenderingResources`, after the vehicle loop: `LightSystem.WriteForwardLights` and `LightSystem.UpdateRenderResources` | main |
| Switch reconciled | `[StarMapAfterGui]` -> `StepOnce` -> `Buggies.Sync` | main |

`Program.OnFrame` calls `LightSystem.ClearLights()` and `LightSystem.OnFrame` at L2317-L2318, then
`OnPreRender` and `Render`.[^ksa-program] `ClusteredLightSystem.ClearLights` empties both the main
collection and every viewport's forward list.[^ksa-clusteredlightsystem] A light created from
`StarMapAfterGui` is therefore cleared before it is used, and one created from
`StarMapAfterOnFrame` is cleared at the start of the next frame's light pass. Inside
`UpdateRenderingResources` (Program.cs L4337-L4409) every visible viewport loops the vehicles in
frame and calls `Vehicle.UpdateRenderData`, which calls `Parts.UpdateRenderData` with the
craft's `asmb2Ego` for that viewport's camera; only after that loop are the forward lights written
per viewport.[^ksa-program][^ksa-vehicle][^ksa-parttree] So the postfix runs once per car per
visible viewport, with the right matrix and viewport in hand, after `ClearLights` and before the
upload, which is the only window that works. That window is `frame:render` (inside `Program.Render`,
after `OnPreRender` has returned and before `RenderGame` records commands), not `frame:pre-render`
or `frame:lights`: `LightSystem.ClearLights` at `frame:lights` is only what empties the list before
it. `Vehicle.UpdateRenderData` skips a craft too small
to render, so a distant car's lamps are not submitted either.[^ksa-vehicle]

## Key code

| Path | Role |
|---|---|
| [src/KSACars/Ksa/LightsHook.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/LightsHook.cs) | Installs the postfix on the `ref readonly` overload; never throws; pins the signature |
| [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L168-L209) | `LightLamps` and `Shine`: aim, transform to Ego, `CreateSpotLight`, `CreateLightInstance` |
| [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L129-L135) | `Sync`: reconcile panel beam and the part's light switch each frame |
| [src/KSACars/Sim/Headlights.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/Headlights.cs) | Beam shapes, colours, braking boost, `Reconcile`, `Aim` |
| [src/KSACars/Sim/BuggyProfile.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/BuggyProfile.cs#L76-L80) | `HeadLamps` and `TailLamps` positions in the part frame |
| [src/KSACars/KSACarsGameData.xml](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/KSACarsGameData.xml#L33-L39) | The `<PowerConsumer LightSwitch="true">` on each car |

## Snippets

```csharp
// src/KSACars/Ksa/LightsHook.cs L32-L46
        try
        {
            MethodInfo? target = AccessTools.Method(typeof(PartTree), nameof(PartTree.UpdateRenderData),
                [typeof(double4x4).MakeByRefType(), typeof(bool), typeof(IViewport), typeof(int)]);
            if (target is null)
            {
                Log.Warn("KSA has no PartTree.UpdateRenderData to hook; the cars' headlamps stay dark");
                return;
            }

            MethodInfo postfix = typeof(LightsHook).GetMethod(nameof(AfterRenderData),
                                                              BindingFlags.NonPublic | BindingFlags.Static)!;
            _harmony = new Harmony(HarmonyId);
            _harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            Installed = true;
```

```csharp
// src/KSACars/Ksa/Buggies.cs L172-L209
    public static void LightLamps(PartTree parts, in double4x4 asmb2Ego, IViewport viewport)
    {
        if (Active.Count == 0) return;
        foreach (Entry e in Active.Values)
        {
            if (!ReferenceEquals(e.Craft.Parts, parts)) continue;
            // A flat battery puts the lamps out with the lenses, which KSA darkens itself.
            if (Headlights.Shape(e.Beam) is not { } beam || e.Part.IsLightSwitchedOff()) return;

            doubleQuat part2Asmb = e.Part.Asmb2VehicleAsmb;
            double3 up = part2Asmb * new double3(1, 0, 0);
            double3 forward = part2Asmb * new double3(0, 1, 0);
            Shine(e, e.Drive.Profile.HeadLamps, Headlights.Aim(up, forward, beam.DipRad), beam, Headlights.Colour,
                  beam.Intensity, ELightFlags.CastsShadows | ELightFlags.SoftShadows, in asmb2Ego, viewport);
            // ...
            return;
        }
    }

    private static void Shine(Entry e, double3[] lamps, double3 d, BeamShape beam, float3 colour, float intensity,
                              ELightFlags flags, in double4x4 asmb2Ego, IViewport viewport)
    {
        double3 aim = new((d.X * asmb2Ego.M11) + (d.Y * asmb2Ego.M21) + (d.Z * asmb2Ego.M31),
                          (d.X * asmb2Ego.M12) + (d.Y * asmb2Ego.M22) + (d.Z * asmb2Ego.M32),
                          (d.X * asmb2Ego.M13) + (d.Y * asmb2Ego.M23) + (d.Z * asmb2Ego.M33));
        foreach (double3 lamp in lamps)
        {
            double3 at = (e.Part.PositionVehicleAsmb + (e.Part.Asmb2VehicleAsmb * lamp)).Transform(asmb2Ego);
            Program.LightSystem.CreateLightInstance(
                Light.CreateSpotLight(at, aim, beam.Range, beam.OuterAngle, beam.InnerAngle, colour, intensity, flags),
                viewport);
        }
    }
```

```csharp
// src/KSACars/Sim/Headlights.cs L48-L60
    /// <summary>
    /// Keeps the panel's setting and the part's own light switch in step, whichever was moved.
    /// <paramref name="seen"/> is what the switch read last frame; a switch that differs from it was
    /// thrown in KSA's part window, and wins.
    /// </summary>
    public static (BeamSetting Beam, bool Switch) Reconcile(BeamSetting beam, bool switchNow, bool seen)
    {
        if (switchNow != seen)
        {
            return (switchNow ? (beam == BeamSetting.Off ? BeamSetting.Low : beam) : BeamSetting.Off, switchNow);
        }
        return (beam, beam != BeamSetting.Off);
    }
```

## Game APIs touched

| API | Decompiled source | Used for |
|---|---|---|
| `PartTree.UpdateRenderData(ref readonly double4x4, bool, IViewport, int)` | [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L1158-L1192) | Patch target; submits `LightModule` lights at L1173-L1179 |
| `Vehicle.UpdateRenderData(IViewport, int)` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3713-L3726) | Caller; computes `asmb2Ego` per viewport camera |
| `Program.OnFrame` `LightSystem.ClearLights` / `OnFrame`; `UpdateRenderingResources` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2317-L2318) | Clear point; the vehicle loop and light upload are at L4337-L4409 |
| `Program.LightSystem` (`ILightSystem.CreateLightInstance(Light, IViewport)`) | [current/decomp/KSA.Rendering.Lighting/ILightSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Lighting/ILightSystem.cs) | Adds a light for one viewport |
| `ClusteredLightSystem.CreateLightInstance` / `ClearLights` | [current/decomp/KSA.Rendering.Lighting/ClusteredLightSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Lighting/ClusteredLightSystem.cs#L897-L934) | Up to 256 forward lights per viewport; zero-range lights and `LightMode.None` viewports dropped |
| `Light.CreateSpotLight(double3, double3, float, float outer, float inner, float3, float, ELightFlags)` | [current/decomp/KSA.Rendering.Lighting/Light.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Lighting/Light.cs#L54-L79) | Builds the spot; swaps inner and outer if reversed, clamps outer below pi/2 |
| `LightModule.UpdateRenderData` | [current/decomp/KSA/LightModule.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LightModule.cs#L110-L121) | How KSA's own lamps reach `CreateLightInstance` |
| `Part.LightSwitch`, `Part.IsLightSwitchedOff()` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1461-L1473) | The resolved switch module; off unless active and powered |
| `PowerConsumer.LightIsActive`, `ApplySaveData` | [current/decomp/KSA/PowerConsumer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PowerConsumer.cs#L162-L173) | Switch state, restored from the save |
| `PowerConsumerTemplate.LightSwitch` / `LightIsActive` attributes | [current/decomp/KSA/PowerConsumerTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PowerConsumerTemplate.cs#L11-L15) | XML attributes on `<PowerConsumer>` |
| `Vehicle.ToggleLights()` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1396-L1409) | KSA's light key flips every part's switch |

## Pitfalls and version notes

- **Too early or too late is invisible.** Lights from any StarMap hook never reach the GPU; this
  is why the mod patches the render-data pass instead.[^ksacars-lightshook]
- **The postfix runs per viewport.** With a secondary viewport open the lamps are submitted once
  for each; `CreateLightInstance` keeps a separate forward list per viewport shader slot.[^ksa-clusteredlightsystem]
- **The `ref readonly` overload.** Reflection needs `typeof(double4x4).MakeByRefType()`, and the
  postfix parameter must be declared `ref`. The patch is on a public method, and a never-called
  `PinTheSignature` puts it in the mod's API surface report so a signature change breaks the
  build.[^ksacars-lightshook]
- **Light budget.** KSA keeps at most 256 forward lights per viewport and `MaxLights` in the main
  collection; four lamps per car count against those.[^ksa-clusteredlightsystem]
- **Angles.** The mod's comment notes KSA takes both cone angles as the whole cone, edge to edge;
  `CreateSpotLight` itself only orders and clamps them.[^ksacars-headlights][^ksa-light]
- **A light is not a glowing lens.** A spotlight placed at the lens lights the bodywork around it
  as a blob; the lenses glow through the emissive map instead (see
  [coloured emissive lenses](./coloured-emissive-lenses.md)).
- **No battery charge.** Nothing on a car charges the battery, so the switch draws almost
  nothing.[^ksacars-gamedata]

## See also

- [Coloured emissive lenses per subpart](./coloured-emissive-lenses.md)
- [Car panel and ModMenu entry](./car-panel-and-modmenu.md) (where the beam is chosen)
- [Car part game data](./car-part-gamedata.md)
- [KSACars overview](../overview.md)
- [Postfix PartTree.UpdateRenderData per craft and submit spot lights with CreateSpotLight](../../../howto/rendering/lights-and-particles/add-dynamic-lights-to-the-world/add-dynamic-lights-to-the-world-1.md) - how-to article demonstrating this feature

[^ksacars-lightshook]: src/KSACars/Ksa/LightsHook.cs
[^ksacars-buggies]: src/KSACars/Ksa/Buggies.cs
[^ksacars-headlights]: src/KSACars/Sim/Headlights.cs
[^ksacars-profile]: src/KSACars/Sim/BuggyProfile.cs
[^ksacars-gamedata]: src/KSACars/KSACarsGameData.xml
[^ksacars-headlights-tests]: tests/KSACars.Tests/HeadlightsTests.cs
[^ksa-program]: KSA.Program (decompiled)
[^ksa-parttree]: KSA.PartTree (decompiled)
[^ksa-vehicle]: KSA.Vehicle (decompiled)
[^ksa-light]: KSA.Rendering.Lighting.Light (decompiled)
[^ksa-elightflags]: KSA.Rendering.Lighting.ELightFlags (decompiled)
[^ksa-clusteredlightsystem]: KSA.Rendering.Lighting.ClusteredLightSystem (decompiled)
[^ksa-part]: KSA.Part (decompiled)
[^ksa-powerconsumer]: KSA.PowerConsumer (decompiled)
