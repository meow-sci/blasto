# Game Feature

* [Build a local site frame and compute a launch window into a target plane](overview.md) - Build a local east-north-up frame at a surface site on a rotating body, get surface-relative velocity and terrain height in it, and find when a launch site passes under a target orbit plane and which azimuth to fly

# How-To

* [Build a flat site frame from CCI and take surface-relative velocity as v minus omega cross r](build-a-site-frame-and-compute-a-launch-window-2.md) - Construct a local frame at a landing site with up radial from the body's CCI position, convert state and attitude into it for a flat-ground solver, and subtract the body's rotation from the velocity
* [Find the launch window into a target plane from site latitude, inclination and a pad lead](build-a-site-frame-and-compute-a-launch-window-1.md) - Compute the target plane from a vehicle's state vectors, a co-elliptic chase orbit, and the time until the rotating launch site passes under that plane, then request a warp to it through the game's auto-warp buffer
* [Read orbit-relative surface velocity as radial and tangential parts](build-a-site-frame-and-compute-a-launch-window-4.md) - Compute surface velocity as v minus omega cross r in the CCI frame, split it along the radial direction, and derive latitude and longitude, for a flight HUD readout
* [Take north from the body's spin axis and query terrain height in the same frame](build-a-site-frame-and-compute-a-launch-window-3.md) - Build an east-north-up frame at a surface point from the rotation axis (not the ecliptic pole) and query terrain height with Celestial.GetTerrainHeightFromDirCce, with the sea clamp and the accuracy flag handled
