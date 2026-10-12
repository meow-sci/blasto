# Game Feature

* [Predict impact points with drag and build a ballistic computer](overview.md) - Predict where a falling vehicle will hit the ground, measure lift and drag under the active aero model, and compute the velocity to be gained for a powered flight to a ballistic target

# How-To

* [Build a ballistic computer with a closed-form aim and a flown impact check](predict-impact-point-and-build-a-ballistic-computer-3.md) - Solve the aim with Lambert in the parent's inertial frame, steer by velocity to be gained, time the cutoff against the achieved throttle, and check the answer by flying it against the real terrain with drag
* [Predict an impact point with gravity, drag and a measured drag table](predict-impact-point-and-build-a-ballistic-computer-1.md) - Integrate gravity and drag on the air-relative velocity to the ground, with a drag table measured from the game's own force function, and measure lift under the active aero model
* [Predict where a released store lands by flying a throwaway round in a ground-carried frame](predict-impact-point-and-build-a-ballistic-computer-2.md) - Fly a throwaway copy of the real round through the real gravity, air and terrain in a frame that rides with the ground, rate-limit the solve on wall clock, and store the answer relative to the platform
