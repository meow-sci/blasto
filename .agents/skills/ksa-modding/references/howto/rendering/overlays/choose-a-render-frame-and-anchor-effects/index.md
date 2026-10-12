# Game Feature

* [Choose a render frame and anchor effects to bodies, parts and terrain](overview.md) - Ecl positions keep the 29.8 km/s orbital carrier while ego positions are camera-relative translations, so picking the right frame and sample instant keeps overlays, effects and decals on the craft, planet or part they belong to

# How-To

* [Anchor to the engine's own render position and add travel since launch](choose-a-render-frame-and-anchor-effects-2.md) - Place gizmos and projectile subparts relative to where the engine draws the launcher, using Ego as a pure translation of Ecl and travel measured as a difference of platform-relative offsets
* [Body-fixed mesh with Ccf-to-Cce rotation and body position at draw time](choose-a-render-frame-and-anchor-effects-5.md) - Store course pieces and a ball in the planet's body-fixed frame and turn them into per-viewport ego transforms inside a draw hook after ClearBuckets
* [Explosion presets with a BubbleOrigin fixed in the body frame](choose-a-render-frame-and-anchor-effects-3.md) - Spawn a stock explosion anywhere by handing ExplosionSystem.SpawnPreset a BubbleOrigin in the planet's rotating frame, so smoke stays over the ground where the burst happened
* [Part-local, cloth-barycentric or geodetic anchors with double-precision composition](choose-a-render-frame-and-anchor-effects-6.md) - Anchor decals to parts, canopy cloth triangles or a latitude and longitude by re-resolving stable ids each frame and composing the decal-to-ego matrix in double precision
* [Pooled emitters re-anchored to the celestial's rotating frame every frame](choose-a-render-frame-and-anchor-effects-4.md) - Take pooled particle emitters, point their Origin at a body-fixed BubbleOrigin each frame, and always Kill them before giving them back
* [Two-instant draw anchor against GetPositionEgo](choose-a-render-frame-and-anchor-effects-1.md) - Pair the craft's rendered Ego position sampled this frame with Ecl offsets measured against the same-instant platform sample so overlays do not slide hundreds of metres per frame
