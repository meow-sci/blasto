# Game Feature

* [Chase a projectile or other fast body with the main camera](overview.md) - A mod-simulated shell, ball or missile is not a Vehicle, so the camera follows it through a custom IFollowable and a level, stable pose behind the flight path

# How-To

* [Chase a round with a RoundFollowable on a level-horizon controller, the pose solved in the viewport pass](chase-a-fast-body-with-the-main-camera-1.md) - KSArmory follows the round itself through RoundFollowable, installs a FixedController subclass for a stable up vector and orbit drag, and solves the stand-off pose from settled state inside the engine's viewport pass
* [Hold a line to the target near the flight path with a carried lift and lean off the singular axis](chase-a-fast-body-with-the-main-camera-2.md) - KSAGolf's pure ChaseView solves a stable eye behind a fast ball, held within 80 degrees of the flight path with a carried lift and a lean off the axis the stock controller cannot cross; only the geometry is tested and part of it is wired in game at the pinned ref
