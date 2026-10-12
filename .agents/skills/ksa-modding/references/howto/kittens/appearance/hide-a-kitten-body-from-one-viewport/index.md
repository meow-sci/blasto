# Game Feature

* [Hide a kitten's body from the main view only, keeping its shadow](overview.md) - How a mod hides a kitten body from the main view only by folding its skeleton per viewport and restoring the full pose in the shadow pool, and what the engine HideHead switch does instead

# How-To

* [Fold all bones onto the head with zero-scale matrices, then give the shadow pass the full-body pose](hide-a-kitten-body-from-one-viewport-1.md) - Mark the main viewport in a KittenEva.UpdateRenderData prefix, collapse every bone to the head in an IAnimProcessor for that view only, and replace the shadow entries with a second skinning of the saved full pose
* [Fold onto the wrist and keep the club-holding paw, or use KittenRenderable.HideHead per view](hide-a-kitten-body-from-one-viewport-2.md) - Collapse every bone onto the right wrist except the right paw subtree for the main view only, keep the full pose for the shadow, and compare the engine HideHead switch that IVA seats use
