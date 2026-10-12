# Game Feature

* [Make a walking kitten move and face along the look direction, and drive it with the mouse](overview.md) - How the stock grounded walk takes its heading from the main camera, how two first-person mods patch KittenLocomotion.StepGrounded to supply their own heading across threads, and how a captured mouse drives a kitten action such as a putter swing

# How-To

* [Capture the cursor and drive a kitten action with raw mouse deltas](steer-a-walking-kitten-2.md) - KSAGolf's borrowed camera controller takes the mouse whole while a swing is possible, hides and unbinds the cursor, ignores the first jump after capture, converts horizontal pixels to a club angle with a clamp and strikes only when the leading face crosses the ball
* [Walk and face along the look with a StepGrounded prefix and postfix and a volatile heading](steer-a-walking-kitten-1.md) - Two mods publish the look heading as a volatile float array; a prefix on KittenLocomotion.StepGrounded replaces the camera forward and a postfix pins FacingDirPhys to the look in both command and state
