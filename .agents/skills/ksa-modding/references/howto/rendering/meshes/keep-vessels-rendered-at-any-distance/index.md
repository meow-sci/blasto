# Game Feature

* [Keep a vessel or kitten rendered at any distance](overview.md) - KSA culls vehicles that project below one pixel; override the cull so a distant ship, a projectile launcher or a scaled-up vessel still draws

# How-To

* [Enlarge the cull radius with call-site transpilers so a scaled vessel is culled sanely](keep-vessels-rendered-at-any-distance-4.md) - Redirect the single IsLargeEnoughToRender call and the single MeanRadius read in the vessel draw path to a scaled-radius mirror, guarded by exactly-one-match checks, while the draw matrix is scaled around the centre of mass
* [Postfix Vehicle.UpdateRenderData and ask the pixel test again for a launcher with rounds in flight](keep-vessels-rendered-at-any-distance-3.md) - Draw a culled vessel's parts from a postfix that repeats the engine's one-pixel test and renders the part tree only when the engine did not, so a distant launcher keeps its projectiles visible
* [Replace the sub-pixel cull with prefixes installed lazily on a dedicated Harmony instance](keep-vessels-rendered-at-any-distance-2.md) - Mark vessels by id to be force-rendered, install two cull-free prefixes only while at least one vessel is marked, and fall back to the stock cull on any fault
* [Skip the one-pixel cull with prefixes on Vehicle.GetWorldMatrix and UpdateRenderData](keep-vessels-rendered-at-any-distance-1.md) - Keep chosen vessels and EVA kittens drawn at any distance by rebuilding the native result without the pixel test in two skip-original Harmony prefixes resolved by name
