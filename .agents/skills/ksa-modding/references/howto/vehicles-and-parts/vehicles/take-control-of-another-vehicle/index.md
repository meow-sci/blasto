# Game Feature

* [Taking control of another vehicle](overview.md) - How KSA decides which vehicle the player flies, and two ways for a mod to fly another craft, by switching Program.ControlledVehicle or by commanding the craft's flight computer from a PrepareWorker prefix

# How-To

* [Fly another vehicle through its flight computer and input actions](take-control-of-another-vehicle-2.md) - Command attitude, throttle, translation, pulse mode and staging on a craft the player is not flying, queueing from frame hooks and applying in a PrepareWorker prefix
* [Switch the controlled vehicle and restore the main view afterwards](take-control-of-another-vehicle-1.md) - Assign Program.ControlledVehicle to another craft, show it in a borrowed secondary viewport, and hand the controls and the main view back when done
