# Game Feature

* [Mount a camera on a vessel part and look through it](overview.md) - Posing a secondary or main camera from a sub-part's transform so it rides a nose, mast or turret, including scaled and articulated parts and mounts that survive staging

# How-To

* [Click a hull point and store it in the hit sub-part's local frame](mount-a-camera-on-a-part-3.md) - Hot Pursuit raycasts the cursor against every vessel's part meshes with Part.RayCastEgo, stores the hit point and normal in the closest sub-part's local frame with the vehicle id and Part.InstanceId, and re-resolves them every frame so a camera pauses instead of failing when its target is gone
* [Pose a camera from the part's Asmb2Ego matrices in a FixedController.OnFrame prefix](mount-a-camera-on-a-part-1.md) - Hot Pursuit composes the vehicle matrix in the main camera's ego space with the sub-part chain, transforms the mount normal by the inverse-transpose so scaled parts do not skew the aim, and writes the pose before Camera.OnFrame builds the matrices
* [Resolve the eye, sight line and roll from an optical head sub-part for the main view or a camera window](mount-a-camera-on-a-part-2.md) - KSArmory's optical head reads its base subpart's transform from the engine, pivots a head subpart by a rate-limited drive and answers the eye and line of sight against a platform position the caller supplies so the sight and the camera windows are posed inside the viewport pass
