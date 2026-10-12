# Game Feature

* [Make the camera follow a point or mod-simulated object, and change the follow target without a jump](overview.md) - Camera.Following takes any IFollowable, so a mod object can be followed with no registration; what the interface must answer, and how SetFollow's teleport and control swap are avoided

# How-To

* [Change the follow target with changeControl and alert false, then write the captured position back](follow-a-point-that-is-not-a-vehicle-3.md) - gatOS' director follows its own anchor with SetFollow(changeControl false, alert false) and restores the captured position and rotation because SetFollow teleports the camera, and uses the same pair to change tidal locking
* [Follow a point with an anchor IFollowable that re-resolves a vessel, part, body or held point on every call](follow-a-point-that-is-not-a-vehicle-1.md) - gatOS implements IFollowable as CameraFollowable, whose GetPositionEcl re-resolves its anchor each call, with identity body-fixed rotation, a one metre radius and a stable OrbitView, so the stock camera tracks a point with no registration
* [Follow on the main viewport without taking control, with follow and mode kept as separate halves](follow-a-point-that-is-not-a-vehicle-4.md) - KSArmory's TryFollowOnMainViewport points the main camera at a mod object with SetFollow(changeControl false), because following a non-Vehicle with the default would null the player's controlled vehicle
* [Re-apply the follow after every viewport claim on a secondary camera](follow-a-point-that-is-not-a-vehicle-5.md) - Hot Pursuit repeats SetFollow with changeControl and alert false after each secondary viewport claim, because a claim resets the viewport to defaults, and unfollows when its target is gone
* [Report the drawn position of a mod-simulated round from a RoundFollowable](follow-a-point-that-is-not-a-vehicle-2.md) - KSArmory's RoundFollowable answers the position a round is drawn at this frame, with a non-zero radius, a stable OrbitView and an identity body-fixed rotation, so the camera and the picture agree and the stock controllers do not hit a null OrbitView
