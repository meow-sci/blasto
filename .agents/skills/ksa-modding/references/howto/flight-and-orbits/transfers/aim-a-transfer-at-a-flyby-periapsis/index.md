# Game Feature

* [Aim a transfer at a flyby periapsis and predict its encounter](overview.md) - Target a chosen periapsis around a destination body instead of its center by solving the transfer with Lambert, predict the encounter periapsis of a planned burn, and iterate the departure burn until the miss is closed

# How-To

* [Offset the Lambert aim point by an impact parameter and hand the result to the planner's Create path](aim-a-transfer-at-a-flyby-periapsis-1.md) - Solve a transfer to a chosen periapsis by offsetting the target by the impact parameter for that periapsis, iterating Lambert three times, and creating the retargeted burn through the stock Create button
* [Predict a planned burn's encounter with CalculateBurnPatch and ComputeCompleteTrajectory filtered to the target](aim-a-transfer-at-a-flyby-periapsis-2.md) - Run a candidate departure burn through the same patched-conic propagation stock uses for its previews, read the periapsis of the resulting encounter with the target, and draw the plan in place of stock's preview
* [Solve your own universal-variable Lambert against a spinning-planet target](aim-a-transfer-at-a-flyby-periapsis-3.md) - Solve over flight time in the parent body's inertial frame, carry the surface aim point forward by the planet's rotation, and search for the cheapest flight time with a coarse scan and golden section
