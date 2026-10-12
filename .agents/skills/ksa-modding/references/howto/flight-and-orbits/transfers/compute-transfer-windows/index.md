# Game Feature

* [Compute phase angles, synodic periods and the time to the next transfer window](overview.md) - Find the phase angle and the time until the next transfer window between two bodies using the game's AlignmentTime branches and Orbit.GetPhaseAngle, and schedule a multi-pass transfer around it

# How-To

* [Mirror the branches of OrbitalTransfers.AlignmentTime with a live phase clock](compute-transfer-windows-1.md) - Compute the target phase angle, synodic rate and time to the next window in a game-free kernel, refresh the live phase each frame from Orbit.GetPhaseAngle, and show the result as a list and a polar clock
* [Shift a same-parent window and re-solve Lambert for a multi-pass Hohmann departure](compute-transfer-windows-2.md) - Raise the apoapsis in periapsis kicks whose orbit periods sum to a whole number of parking periods so the last kick lands on the stock window, and move the whole schedule forward when the window is too close
