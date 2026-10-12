---
type: How-To
title: Sphere pick, GetLatitudeFromCce and GetLongitudeFromCce, haversine, chord and bearing
description: Pin two points on a planet from the cursor with a sphere ray test, store them as latitude and longitude, and compute great-circle distance, chord and initial bearing
category: celestials
group: terrain
topic: click-a-planet-to-get-latitude-and-longitude
approach: 1
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-measuretools/features/surface-great-circle-tool]
techniques: [screen-to-world-ray, harmony-prefix]
hooks: [harmony-prefix KSA.Program.OnMouseButton, starmap StarMapAfterGui]
lifecycle: [frame:input, frame:draw-ui]
game_types: [KSA.Celestial, KSA.Camera, KSA.Ray, KSA.BoundingSphere3D, KSA.Universe, KSA.CelestialSystem]
tags: [latitude, longitude, cursor-ray, haversine, bearing, pin]
---
Outcome: left-click twice on a planet or moon and read the great-circle distance, the straight chord and the initial bearing between the two points. Demonstrated by KSA-MeasureTools' Surface tool ([Surface great-circle tool](../../../../mods/ksa-measuretools/features/surface-great-circle-tool.md)). Prerequisites: StarMap, Harmony, and an ImGui overlay (see [approach 2](click-a-planet-to-get-latitude-and-longitude-2.md)).

## Background: how the game does it

The cursor ray is in ego space (camera-relative); each body's sphere is `BoundingSphere3D(camera.GetPositionEgo(body), body.MeanRadius)`. A hit is lifted back to the ecliptic from the ray's own origin (`camera.EgoToEcl(ray.Origin) + direction * t`), which is correct for an orthographic projection too, where the origin moves with the cursor. From the ecliptic hit, `GetPositionCceFromEcl` gives the body-centred position and `GetLatitudeFromCce` / `GetLongitudeFromCce` rotate it into the body-fixed frame ([current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L691-L740)). Because the pin is stored as latitude and longitude and resolved each frame through `GetPositionEclFromLatLon`, it tracks the planet's rotation like a ground marker.

## Lifecycle and timing

- **Placement** runs in a prefix on `Program.OnMouseButton` (`frame:input`): the body's rotation and position are the settled ones for this step. The prefix takes the left press while the tool is armed so a placement click does not also focus a body or set a target ([MeasureTools/Patches/Patch_MouseButton.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Patches/Patch_MouseButton.cs#L14-L17)). While a text field wants the keyboard, see [block game hotkeys while typing](../../../ui/input/block-game-hotkeys-while-typing/overview.md).
- **Arc, pins and labels** are drawn in `[StarMapAfterGui]` (`frame:draw-ui`), resolved with the current frame's body rotation, so pins visibly turn with the planet under time warp.
- No game state is written. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Ray-test every celestial.** Keep the nearest positive hit from outside the sphere (a hit from inside is rejected). Once the first pin is down, only its body qualifies, because the great-circle maths needs both points on one sphere. Lift the hit using the ray origin, then convert to latitude and longitude:

```csharp
// MeasureTools/Features/Measure/MapPicker.cs L107-L143
Ray ray = camera.ScreenToEgoRay(mouseViewport);
Celestial? required = MeasureState.Pending.Count > 0
    ? MeasureState.Pending[0].Body as Celestial
    : null;

Celestial? best = null;
double bestT = double.MaxValue;
foreach (Astronomical astronomical in system.All.AsSpan())
{
    if (astronomical is not Celestial celestial)
        continue;
    if (required != null && celestial != required)
        continue;
    var sphere = new BoundingSphere3D(camera.GetPositionEgo(celestial), celestial.MeanRadius);
    if (!ray.Raycast(sphere, out double t, out bool inside) || inside || !(t > 0.0))
        continue;
    if (t < bestT)
    {
        bestT = t;
        best = celestial;
    }
}
if (best == null)
    return null;

// Ego axes are ECL axes, so lifting the hit point is a translation by the
// camera position; lat/lon then come from the body-fixed frame. The origin
// comes from the ray, not from the camera: ScreenToEgoRay starts a
// perspective ray at the camera but an orthographic one at the cursor's own
// near-plane point, which is what the vehicle editor's Projection toggle
// switches to. EgoToEcl is a pure translation, so this is PositionEcl in the
// perspective case.
double3 hitEcl = camera.EgoToEcl(ray.Origin) + ray.Direction * bestT;
double3 hitCce = best.GetPositionCceFromEcl(hitEcl);
double latitude = best.GetLatitudeFromCce(hitCce);
double longitude = best.GetLongitudeFromCce(hitCce);
return Anchor.PinOnSurface(best, latitude, longitude);
```

2. **Store the pin as degrees in the body-fixed frame**, not as a position:

```csharp
// MeasureTools/Features/Measure/Anchor.cs L232-L242
public static Anchor PinOnSurface(Celestial body, double latitudeDeg, double longitudeDeg)
{
    return new Anchor
    {
        Kind = AnchorKind.SurfacePin,
        Body = body,
        Latitude = latitudeDeg,
        Longitude = longitudeDeg,
        Label = body.Id + " " + FormatLatLon(latitudeDeg, longitudeDeg),
    };
}
```

3. **Resolve it each frame** through `Celestial.GetPositionEclFromLatLon(lat, lon)`:

```csharp
// MeasureTools/Features/Measure/Anchor.cs L90-L90
AnchorKind.SurfacePin => ((Celestial)Body!).GetPositionEclFromLatLon(Latitude, Longitude),
```

4. **Great-circle distance** with the haversine in the `atan2` form, stable at all separations, with `R = body.MeanRadius`:

```csharp
// MeasureTools/Features/Measure/Measurement.cs L38-L47
public static double GreatCircleMeters(Celestial body, double lat1Deg, double lon1Deg, double lat2Deg, double lon2Deg)
{
    double phi1 = lat1Deg * (Math.PI / 180.0);
    double phi2 = lat2Deg * (Math.PI / 180.0);
    double sinDPhi = Math.Sin((phi2 - phi1) * 0.5);
    double sinDLambda = Math.Sin((lon2Deg - lon1Deg) * (Math.PI / 180.0) * 0.5);
    double a = sinDPhi * sinDPhi + Math.Cos(phi1) * Math.Cos(phi2) * sinDLambda * sinDLambda;
    double c = 2.0 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(Math.Max(0.0, 1.0 - a)));
    return body.MeanRadius * c;
}
```

5. **Initial bearing** (forward azimuth) from pin one to pin two, normalised to 0..360 degrees with 0 = north:

```csharp
// MeasureTools/Features/Measure/Measurement.cs L49-L60
// Surface mode: initial bearing (forward azimuth) from the first pin to the
// second, 0..360 degrees, 0 = north.
public double BearingDegrees()
{
    double phi1 = Anchors[0].Latitude * (Math.PI / 180.0);
    double phi2 = Anchors[1].Latitude * (Math.PI / 180.0);
    double dLambda = (Anchors[1].Longitude - Anchors[0].Longitude) * (Math.PI / 180.0);
    double y = Math.Sin(dLambda) * Math.Cos(phi2);
    double x = Math.Cos(phi1) * Math.Sin(phi2) - Math.Sin(phi1) * Math.Cos(phi2) * Math.Cos(dLambda);
    double deg = Math.Atan2(y, x) * (180.0 / Math.PI);
    return (deg + 360.0) % 360.0;
}
```

6. **Chord**: the straight-line distance between the two resolved ecliptic points (`Length(b - a)`). The overlay shows the arc distance as the headline and "chord ... brg ..." as a second line.

## Complete example

| File | Role |
|---|---|
| [MeasureTools/Features/Measure/MapPicker.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MapPicker.cs#L101-L144) | `PickSurface` |
| [MeasureTools/Features/Measure/Measurement.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/Measurement.cs#L38-L60) | Haversine distance and bearing |
| [MeasureTools/Features/Measure/Anchor.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/Anchor.cs#L232-L242) | `PinOnSurface` |
| [MeasureTools/Patches/Patch_MouseButton.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Patches/Patch_MouseButton.cs) | Left-press prefix |

## Variations and gotchas

- **Mean sphere only.** Pins sit on the mean-radius sphere, not on terrain; on bodies with large relief the pin is drawn above or below the visible ground, and the distance is a sphere distance. For ground height use the height query.
- **Oblate bodies** are treated as spheres.
- **Camera inside the sphere.** The ray test rejects hits from inside, so the tool does not work with the camera below a body's mean radius.
- **Only celestials** are candidates; stars and vehicles are skipped by the type check.
- **Ecliptic versus rotating.** A pin stored as a `Cce` offset would not rotate with the body; lat/lon in the body-fixed frame does.

## See also

- [Overview](overview.md); [approach 2: drawing the arc](click-a-planet-to-get-latitude-and-longitude-2.md)
- [Surface great-circle tool](../../../../mods/ksa-measuretools/features/surface-great-circle-tool.md)
- [Convert the cursor to a world ray](../../../ui/input/cursor-to-world-ray/overview.md), [Pick objects under the cursor](../../../ui/input/pick-objects-under-the-cursor/overview.md)
