# Game Feature

* [Commanding engines, throttle and RCS from code](overview.md) - How KSA turns key presses into engine and thruster commands, the input-buffer and private-field routes a mod can use, and four recipes up to a custom RCS allocator

# How-To

* [Animate a grid of engines with buffered SetIsActive calls](command-engines-throttle-and-rcs-from-code-3.md) - Treat each engine pair as a pixel, ignite the vehicle once, and toggle only the cells that changed with EngineController.SetIsActive(null, bool) from a scroll or pattern loop
* [Choose which RCS jets fire from a live wrench table](command-engines-throttle-and-rcs-from-code-4.md) - Measure each thruster's force and torque from live nozzle performance, then pulse jets by control-axis groups or a fuel-optimal LP from a postfix on FlightComputer.ComputeControl
* [Set manual throttle and RCS command flags by writing the boxed manual-control struct](command-engines-throttle-and-rcs-from-code-2.md) - Reach Vehicle._manualControlInputs by reflection, box it, set EngineThrottle or ThrusterCommandFlags and write it back, so a script's input is indistinguishable from a held key
* [Switch engines on and off and set minimum throttle through KSA's input-event buffers](command-engines-throttle-and-rcs-from-code-1.md) - Ignite with Vehicle.SetEnum, activate individual engines and RCS thrusters with SetIsActive, and set deep-throttle floors with a derived-data dirty mark
