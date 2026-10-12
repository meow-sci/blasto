# Game Feature

* [Read roll, pitch, heading, angle of attack and sideslip in the local horizon frame](overview.md) - How to express a vehicle's attitude and velocity relative to the local east-north-up horizon, using the game's own ENU frame helpers and quaternion type, and how to derive angle of attack and sideslip

# How-To

* [Concatenate GetBody2Cci with the inverse ENU frame and call ToRollPitchYawRadians](read-flight-state-in-a-local-horizon-frame-1.md) - Compute roll, pitch and heading from the vehicle quaternion and the game's local ENU frame, plus angle of attack and sideslip from the body-frame velocity
* [Transform basis vectors with the game's own quaternion type to build a site-frame attitude](read-flight-state-in-a-local-horizon-frame-2.md) - Convert a vehicle's attitude and surface-relative velocity into a local site frame by rotating known basis vectors with the game's Transform, never assuming a quaternion convention, and derive body axes from the measured thrust direction
