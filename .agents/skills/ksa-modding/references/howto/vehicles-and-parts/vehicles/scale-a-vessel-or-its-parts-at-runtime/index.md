# Game Feature

* [Scaling a vessel or its parts at runtime](overview.md) - How Part.Scale, IRescale and the vehicle editor's scale limits work, and four ways to resize a vessel, a part, an avatar or the editor's limits

# How-To

* [Lift the vehicle editor's part-scale limits with skipping prefixes and a bounds postfix](scale-a-vessel-or-its-parts-at-runtime-4.md) - Widen VehicleEditor.ScaleBoundsFor, replace UpdateSelectedScale with a per-axis drag, and bypass QuantizeScale, reusing the game's private helpers through AccessTools.MethodDelegate
* [Scale a vessel with a recursive Part.Scale write and an avatar scalar](scale-a-vessel-or-its-parts-at-runtime-2.md) - Set Part.Scale on every part and sub-part, and Core.Scale on a kitten's avatar, as the simplest visual scale that also changes what the game believes about the part
* [Scale a whole vessel with separate render, physics and collider channels](scale-a-vessel-or-its-parts-at-runtime-1.md) - Capture original part scales once, write physical scale in the settled window with the engine's own refresh chain, and scale only the drawn size through transpilers on the render path
* [Scale parts per axis with captured baselines](scale-a-vessel-or-its-parts-at-runtime-3.md) - Record each full part's Part.Scale once, write original times a per-axis factor to full parts only, invalidate sub-part caches, and restore exactly
