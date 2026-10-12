# Game Feature

* [Scale or move a vessel's colliders at runtime](overview.md) - How a mod resizes collision geometry independently of render and mass, or stows part colliders in the hull and moves or toggles them, by overriding what ColliderModule receives or by writing its part-frame position and NeedsColliderUpdate

# How-To

* [Override ColliderModule.SetScale and call UpdateCollisionGeometry](scale-or-move-a-vessels-colliders-at-runtime-1.md) - Make collision shapes follow a different scale from mass and render by injecting an answer into ColliderModule.SetScale and the centre getter, rebuilding through the private Vehicle.UpdateCollisionGeometry at the handoff, and restoring the nominal bounds
* [Write PositionPartAsmb and NeedsColliderUpdate](scale-or-move-a-vessels-colliders-at-runtime-2.md) - Declare collider boxes in part XML stowed inside the hull and move or toggle them at runtime by writing the module's part-frame position, rotation and NeedsColliderUpdate, with a subpart shrunk to 0.001 as the visibility switch
