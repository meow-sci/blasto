# Game Feature

* [Defining articulated subparts and animating or hiding them from code](overview.md) - How a subpart is a Part with settable parent-relative transforms, and four mods' ways of posing wheels, mounts and gimbals and hiding subparts from the main-thread frame

# How-To

* [Aim optical director heads on roll-nod and mast gimbals](define-and-animate-subparts-3.md) - Drive a head with a rate-limited shortest-rotation pointing drive, clamp its travel to a legal path, and write a roll collar and head from one aim so both stay flush
* [Drive articulated turret mounts as sibling subparts with rate-limited drives](define-and-animate-subparts-2.md) - Declare each moving assembly as a sibling subpart, simulate rate-limited drives in plain C#, compose chained poses against the launcher part's frame and latch a channel off when the engine refuses a write
* [Hide subparts by scale and fly them as projectiles of their launcher](define-and-animate-subparts-4.md) - Declare one hidden body per tube, scale it to a millimetre until its round is in the air, then write its position in the launcher part's frame from the simulated round
* [Pose wheels, arms and blades from StarMapAfterGui with a frame latch](define-and-animate-subparts-1.md) - Author each moving body as its own recentred SubPart, find it by instance Id, write its parent-relative transform every frame and fall back to StarMapAfterOnFrame when the HUD is hidden
