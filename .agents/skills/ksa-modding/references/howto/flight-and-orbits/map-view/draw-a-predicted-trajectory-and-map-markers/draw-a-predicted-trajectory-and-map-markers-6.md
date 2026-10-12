---
type: How-To
title: Force or hide a body's orbit line and change its colour with a postfix on Astronomical.ShouldDrawLines and Orbit.OrbitLineColor
description: Override the game's decision about whether a celestial body's orbit line is drawn with a postfix on Astronomical.ShouldDrawLines driven by a mod-side Always/On/Off table, keep the body's ShowOrbit flag in step, and recolour the line by writing Orbit.OrbitLineColor while keeping alpha
category: flight-and-orbits
group: map-view
topic: draw-a-predicted-trajectory-and-map-markers
approach: 6
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/compendium/features/orbit-line-visibility-and-color]
techniques: [harmony-postfix, starmap-lifecycle]
hooks:
  - starmap StarMapImmediateLoad
  - harmony-postfix KSA.Astronomical.ShouldDrawLines(Astronomical, IViewport, Orbit)
lifecycle: [startup:immediate-load, frame:draw-ui, frame:pre-render]
game_types:
  - KSA.Astronomical
  - KSA.Celestial
  - KSA.Orbit
  - KSA.OrbitUiMode
  - KSA.IViewport
  - KSA.IGameViewport
  - KSA.CelestialSystem
  - KSA.NearestOrbitPointJob
  - Brutal.Numerics.byte4
tags: [orbit-lines, map-view, visibility, color, harmony, postfix, celestials]
---
Outcome: you decide, per body, whether its orbit line is always drawn, never drawn, or left to the game, and what colour it is. Compendium offers `< Always >`, `< On >` and `< Off >` buttons and an RGB colour picker for one body, its moons, a group, a category or the whole system, which is how its window shows or hides hundreds of asteroid and comet orbits at once.[Orbit line visibility and color](../../../../mods/compendium/features/orbit-line-visibility-and-color.md)

This approach is about the orbit lines of celestial bodies (planets, moons, asteroids). A vehicle's trajectory preview is a `FlightPlan` and is covered by [approaches 1 and 2](draw-a-predicted-trajectory-and-map-markers-1.md).

Prerequisites: StarMap and Harmony.

## Background: how the game does it

- **The decision.** A celestial adds its orbit line in `AddLineInstances` only if `(ShowOrbit || TargetOfControlledVehicle) && Astronomical.ShouldDrawLines(this, viewport, null)`; `OrbitingStar` has the same line.[^ksa-celestial] [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L1740-L1746) [current/decomp/KSA/OrbitingStar.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitingStar.cs#L222-L228)
- **`ShouldDrawLines`.** It returns true when the global "always show orbit lines" setting is on, false for `OrbitUiMode.Never`, true for the followed body and for `Always`, a distance test for `Near`, and a general visibility test otherwise:

```csharp
// current/decomp/KSA/Astronomical.cs L658-L684
	public static bool ShouldDrawLines(Astronomical astronomical, IViewport inViewport, Orbit? orbit = null)
	{
		if (GameSettings.ShowOrbitLinesAlways())
		{
			return true;
		}
		if (astronomical.OrbitLineMode == OrbitUiMode.Never)
		{
			return false;
		}
		if (Program.IsFollowing(astronomical))
		{
			return true;
		}
		if (astronomical.OrbitLineMode == OrbitUiMode.Always)
		{
			return true;
		}
		if (astronomical.OrbitLineMode == OrbitUiMode.Near)
		{
			Camera camera = inViewport.GetCamera();
			double3 positionEcl = camera.PositionEcl;
			double num = Math.Abs((astronomical.GetPositionEcl() - positionEcl).Length());
			return num < astronomical.MeanRadius * (double)astronomical.OrbitVisibleRadius && ShouldDrawUiOrLines(astronomical, inViewport, orbit);
		}
		return ShouldDrawUiOrLines(astronomical, inViewport, orbit);
	}
```

- **Overloads.** `ShouldDrawLines(IOrbiter, ...)` and `ShouldDrawLines(IParentBody, ...)` forward to the `Astronomical` overload, so a patch on the `Astronomical` overload covers all three.[^ksa-astronomical] [current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs#L603-L619)
- **Second caller.** `CelestialSystem.SetNearestOrbitPoint` (the orbit under the cursor) calls `ShouldDrawLines` too, so a hidden orbit is not pickable. It runs from `NearestOrbitPointJob` on a worker thread.[^ksa-celestialsystem] [^ksa-nearestorbitpointjob] [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L522) [current/decomp/KSA/NearestOrbitPointJob.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/NearestOrbitPointJob.cs#L16-L26)
- **Colour.** `Orbit.OrbitLineColor` is a public `byte4` field that the orbit renderer reads when it draws: the `Orbit.DrawLines` overload used for a body's own line passes `OrbitLineColor` as the colour.[^ksa-orbit] [current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L1147) [current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L2562-L2565)
- **Not the vehicle path.** `FlightPlan.AddLineInstances` decides per patch with `Astronomical.ShouldDrawUiOrLines`, a different method, so this postfix does not change a vehicle's flight-plan lines.[^ksa-flightplan] [current/decomp/KSA/FlightPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightPlan.cs#L923)

## Lifecycle and timing

| Part | Where | Lifecycle |
|---|---|---|
| Install the patch | `[StarMapImmediateLoad]` calls `Patcher.Patch()` | `startup:immediate-load` |
| Write the table, `ShowOrbit` and colours | Buttons and sliders in the mod window, main thread | `frame:draw-ui` |
| Read the override | Postfix, from `Celestial.AddLineInstances` per body per viewport | `frame:pre-render` |
| Read the override again | `NearestOrbitPointJob` on a worker | off the main thread, queued in `PrepareFrame` |

Line instancing happens in `Universe.OnPreRender` per viewport, so a change made in the UI shows in the same frame's lines. The second reader runs concurrently with the UI pass. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Install the patch at load.** Compendium creates one `Harmony` instance and calls `PatchAll` for its assembly from `[StarMapImmediateLoad]`:

```csharp
// src/Patcher.cs L6-L20
    [HarmonyPatch]
    internal static class Patcher
    {
        private static Harmony? _harmony = new Harmony("Compendium");

        public static void Patch()
        {
            if (_harmony == null)
            {
                _harmony = new Harmony("Compendium");
            }

            Console.WriteLine("Patching Compendium...");
            _harmony.PatchAll(typeof(Patcher).Assembly);
            RootNavigationTargets.PatchesInstalled = true;
```

```csharp
// src/CompendiumMain.cs L1235-L1245
        [StarMapImmediateLoad]
        public void OnImmediateLoad(Mod mod)
        {
            try
            {
                // Gets the current working directory path (of the DLL)
                dllDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                LoadSettings();

                // Enable orbit visibility and root navigation targeting patches.
                Patcher.Patch();
```

2. **Keep a mod-side table keyed by system and body path.** The key is the current system id plus a dotted path built by walking parents, so the same body name in different systems stays distinct. Only `Celestial`s are looked up:

```csharp
// src/CdmHelpers.cs L193-L197
        private static string GetOrbitVisibilityKey(Celestial celestial)
        {
            string currentSystem = Universe.CurrentSystem?.Id ?? "Dummy";
            return $"{currentSystem}.{GetCelestialPath(celestial)}";
        }
```

```csharp
// src/CdmHelpers.cs L235-L244
        internal static bool TryGetOrbitVisibilityOverride(Astronomical astronomical, out OrbitVisibilityMode mode)
        {
            if (astronomical is Celestial celestial)
            {
                return orbitVisibilityOverrides.TryGetValue(GetOrbitVisibilityKey(celestial), out mode);
            }

            mode = OrbitVisibilityMode.On;
            return false;
        }
```

3. **Postfix `ShouldDrawLines`.** `Always` forces true unless the body's own mode is `Never`; `Off` forces false; no entry leaves the game's answer:

```csharp
// src/Patcher.cs L40-L61
        [HarmonyPatch(typeof(Astronomical), nameof(Astronomical.ShouldDrawLines), new[] { typeof(Astronomical), typeof(IGameViewport), typeof(Orbit) })]
        [HarmonyPostfix]
        public static void ShouldDrawLinesPostfix(Astronomical astronomical, ref bool __result)
        {
            if (!Compendium.TryGetOrbitVisibilityOverride(astronomical, out OrbitVisibilityMode mode))
            {
                return;
            }

            switch (mode)
            {
                case OrbitVisibilityMode.Always:
                    if (astronomical.OrbitLineMode != OrbitUiMode.Never)
                    {
                        __result = true;
                    }
                    break;
                case OrbitVisibilityMode.Off:
                    __result = false;
                    break;
            }
        }
```

   The attribute lists `typeof(IGameViewport)` while the game's method takes `IViewport`. Harmony resolves the target with the default reflection binder, which accepts an argument type assignable to the parameter type, and `IGameViewport` extends `IViewport`; the author confirmed this with a reflection test. Listing `typeof(IViewport)` is the exact form and avoids ambiguity if the game adds an overload.

4. **Keep `ShowOrbit` in step.** The game's own gate (`ShowOrbit || TargetOfControlledVehicle`) runs before `ShouldDrawLines`, so a postfix alone cannot switch a line on for a body whose `ShowOrbit` is false. The setter writes the table and the flag together; `On` removes the entry so the game's rules apply again:

```csharp
// src/CdmHelpers.cs L256-L270
        private static void SetOrbitVisibilityMode(Celestial celestial, OrbitVisibilityMode mode)
        {
            string key = GetOrbitVisibilityKey(celestial);

            if (mode == OrbitVisibilityMode.On)
            {
                orbitVisibilityOverrides.Remove(key);
            }
            else
            {
                orbitVisibilityOverrides[key] = mode;
            }

            celestial.ShowOrbit = mode != OrbitVisibilityMode.Off;
        }
```

5. **Recolour by writing the field.** Copy the `byte4`, replace its `RGB` part so alpha is kept, and write it back; no patch is needed:

```csharp
// src/CdmHelpers.cs L556-L574
        private static void SetOrbitLineColor(IEnumerable<Celestial> celestials, float3 color)
        {
            var rgb = new byte3(
                (byte)(color.X * 255),
                (byte)(color.Y * 255),
                (byte)(color.Z * 255));

            foreach (var celestial in celestials)
            {
                if (celestial.Orbit == null)
                {
                    continue;
                }

                var orbitColor = celestial.Orbit.OrbitLineColor;
                orbitColor.RGB = rgb;
                celestial.Orbit.OrbitLineColor = orbitColor;
            }
        }
```

   The window loops the same setters over a body's moons, an orbit group, a category or every celestial under the selected root for the bulk buttons.

## Complete example

| File | Role |
|---|---|
| [src/Patcher.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/Patcher.cs#L6-L61) | Harmony setup and the `ShouldDrawLines` postfix |
| [src/CdmHelpers.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmHelpers.cs#L193-L270) | Override key, lookup and mode setter |
| [src/CdmHelpers.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmHelpers.cs#L556-L676) | Colour write and colour dropdown |
| [src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L674-L687) | Per-body mode buttons and colour dropdown |

## Variations and gotchas

- **Data race.** The override table is a plain `Dictionary` written by the UI on the main thread and read by the nearest-orbit job on a worker. A `ConcurrentDictionary`, or an immutable snapshot swapped per frame, makes the read side safe.
- **Allocation per call.** The postfix builds a key string (system id plus a path built by walking parents) for every celestial, every viewport and every frame. Cache the key on the body if the list is long.
- **Only `Celestial`s.** `TryGetOrbitVisibilityOverride` ignores other `Astronomical`s, so orbiting stars (which share the same `ShouldDrawLines` gate) always follow the game's rules.
- **Session only.** Modes and colours are not saved; a colour written to `Orbit.OrbitLineColor` lasts until the system reloads.
- **Not for vehicle lines.** Use [approaches 1 and 2](draw-a-predicted-trajectory-and-map-markers-2.md) to colour a flight plan; `OrbitLineColor` on a detached plan's orbits is how approach 2 draws its brightness ramp.
- **Overload drift.** If a later build adds another overload accepting an `IGameViewport`, binding by assignability could report an ambiguity; name the exact parameter types.

## See also

- [Overview](overview.md); [Click-through overlay projecting CCI points](draw-a-predicted-trajectory-and-map-markers-5.md)
- [Pick bodies and orbit lines under the cursor](../anchor-and-pick-points-on-bodies/anchor-and-pick-points-on-bodies-4.md)
- [Orbit line visibility and color (mod feature)](../../../../mods/compendium/features/orbit-line-visibility-and-color.md)

[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^ksa-astronomical]: KSA.Astronomical (decompiled, build 2026.10.10.5554)
[^ksa-celestialsystem]: KSA.CelestialSystem (decompiled, build 2026.10.10.5554)
[^ksa-nearestorbitpointjob]: KSA.NearestOrbitPointJob (decompiled, build 2026.10.10.5554)
[^ksa-orbit]: KSA.Orbit (decompiled, build 2026.10.10.5554)
[^ksa-flightplan]: KSA.FlightPlan (decompiled, build 2026.10.10.5554)
