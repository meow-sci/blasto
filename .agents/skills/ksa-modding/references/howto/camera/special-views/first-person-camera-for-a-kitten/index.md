# Game Feature

* [Put the camera in an EVA kitten's eyes, with mouse look and look-driven walking](overview.md) - How a mod reads the head bone, borrows the main view in Fixed mode at that point, keeps a mouse look in a world frame and makes the walking kitten face where the player looks

# How-To

* [Pose the eye from the head bone in the golf pose with a pitch-tilted up, a captured-cursor look and a drawn turn for a standing kitten](first-person-camera-for-a-kitten-2.md) - KSAGolf builds the first-person eye 12 cm ahead of and 5 cm above the head bone, tilts the up vector with pitch, looks with the captured cursor at 0.08 degrees per pixel or a right-drag at the orbit rate, and draws the turn to the look on the skeleton until the kitten walks
* [Read the head bone through an IAnimProcessor, pose the eye in the viewport pass and walk where the kitten looks](first-person-camera-for-a-kitten-1.md) - KSAStructures adds an EyesPose animation processor to the flown kitten's avatar, converts the Head_M bone to the ecliptic, borrows the main view in Fixed mode, takes right-drag look through the controller and patches KittenLocomotion.StepGrounded to face and walk along the look
