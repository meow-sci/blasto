# Game Feature

* [Execute burns with RCS translation and throttle inputs from code](overview.md) - Drive throttle and RCS translation inputs from a mod, execute a burn with RCS only, and trim and release several payloads along one line from a bus

# How-To

* [Drive throttle and translation through Vehicle.ProcessInput, re-applied from the PrepareWorker window](execute-burns-with-rcs-and-throttle-inputs-4.md) - Press and release the engine and translation actions on any vehicle with the keyboard's own ProcessInput call, servo the throttle toward a target, and re-apply commands that PrepareWorker clears on the controlled craft
* [Intercept the Auto burn-mode request and fly the burn with RCS only](execute-burns-with-rcs-and-throttle-inputs-1.md) - Patch Vehicle.SetEnum so Auto on an engine-less burn starts an RCS executor, publish an immutable command to the vehicle worker keyed by the shared BurnPlan, and end the burn through the stock completion flags
* [Trim a bus with one translation direction at a time, then go quiet so KSA puts it back on rails](execute-burns-with-rcs-and-throttle-inputs-2.md) - Null the velocity error against a propagated burnout solution by pressing translation actions one direction at a time in pulse mode, release payloads along one line, and stop commanding actuators so the craft returns to rails
* [Write throttle and RCS flags by reflection into Vehicle._manualControlInputs](execute-burns-with-rcs-and-throttle-inputs-3.md) - Set the main-engine throttle and the thruster command flags by boxing the private manual control struct, mutating it and writing it back, so a script's input looks like a held key
