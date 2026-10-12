# Game Feature

* [Let the IVA camera leave the seat and fly around the cabin](overview.md) - How IVA mode is pinned to a seat, what depends on the mode staying IVA, and the controller-swap approach that frees the camera without losing interiors, audio or ray tracing

# How-To

* [Let the IVA camera leave the seat with an IVAController subclass that delegates movement to a private FlyController](unlock-the-iva-camera.md) - Kitchen Sink swaps the main viewport's IvaController for a subclass that stays in IVA mode but flies like the free camera in the vessel's body frame, with a Return to Seat path, a head-visibility postfix and scene-save restore
