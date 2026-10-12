# Game Feature

* [Compute a suicide-burn start altitude and the thrust it needs](overview.md) - Work out when to light the engines for a just-in-time landing burn from live thrust, mass flow and gravity, and invert a thrust demand into a throttle setting

# How-To

* [Closed-form suicide-burn calculator on radar altitude](compute-a-suicide-burn-1.md) - Compute the radar altitude at which a full-thrust landing burn must start, its duration and the countdown to it from live ambient-pressure thrust, mass flow and averaged gravity
* [Evaluate the core engine model at a throttle and pressure, and invert a thrust demand into a throttle](compute-a-suicide-burn-3.md) - Reuse the game's own engine model to get thrust and mass flow at any throttle and ambient pressure, mirror the flight computer's g-load throttle cap, and bracket a throttle for a requested thrust with a saturation status
* [Sum ComputeActivePerformance thrust per active controller for a live thrust and T/W readout](compute-a-suicide-burn-2.md) - Get the current total engine thrust and thrust-to-weight of the controlled vehicle by calling EngineController.ComputeActivePerformance for each active controller at the live ambient pressure and throttle
