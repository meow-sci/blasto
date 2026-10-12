# Game Feature

* [Animate seated kittens, draw them on unflown craft and fly another craft from a chair](overview.md) - How a seat builds and draws its own KittenRenderable, only for the controlled vehicle, and three mod techniques for posing a seated kitten, drawing crew of craft nobody is flying and sitting in a chair to fly a different craft

# How-To

* [Attach a pose processor to a seat's kitten each time the seat rebuilds it, and stop the idle actions fighting the hands](animate-and-draw-seated-kitten-crew-1.md) - KSACars checks every frame whether the driver seat holds a new KittenRenderable, adds a DriverHands processor to its CharacterModel.AnimProcessors, nulls the private _seatedIdleActionAnims and lets the processor put each wrist on a grip that turns with the wheel
* [Draw seated crew on craft nobody is flying with a Harmony postfix on Vehicle.UpdateSeatedCrewRenderData](animate-and-draw-seated-kitten-crew-2.md) - KSACars postfixes the crew-draw method, skips portrait viewports and calls IVASeat.UpdateSeatedRenderable for every other active car so an open-cockpit passenger stays visible when a kitten on EVA is controlled
* [Sit in a chair to fly another craft - swap ControlledVehicle, borrow a camera window and restore everything on log-off](animate-and-draw-seated-kitten-crew-3.md) - KSAStructures builds a chair part with an IVASeat and an EVA door, treats the controlled chair as seated, hands the controls to the nearest craft and returns control and the main view on log-off or death
