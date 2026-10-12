# Game Feature

* [Computing engine thrust, Isp and mass flow at ambient pressure, and inverting for throttle](overview.md) - How KSA evaluates an engine at a pressure and throttle, which cores follow the throttle, the manual g-load throttle cap, and two ways to reuse the model

# How-To

* [Evaluate engines at an ambient pressure and throttle, mirror the g-load cap and invert for a thrust demand](compute-engine-thrust-at-ambient-pressure-1.md) - Call EngineController.ComputeActivePerformance with core-type checks, reproduce the structural throttle cap, and find the throttle for a requested thrust with a bracketed Illinois search
* [Use ComputeFromCores or VacuumData inside a stage delta-v estimator](compute-engine-thrust-at-ambient-pressure-2.md) - Sum engine thrust and mass flow per stage at vacuum from VacuumData or at pressure from RocketControllerData.ComputeFromCores, and feed them to the rocket equation
