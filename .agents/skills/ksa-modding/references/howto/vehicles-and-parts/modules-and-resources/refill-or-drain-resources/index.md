# Game Feature

* [Refilling, draining and topping up a vehicle's propellant and batteries](overview.md) - The calls KSA uses to refill or empty tanks and batteries, the stock input buffer that runs them safely, and three mod recipes with their timing windows

# How-To

* [Drain a tank through its mole states and recompute mass](refill-or-drain-resources-3.md) - Take mass off specific tanks with Mole.ConsumeStored on the live state, then repeat the bookkeeping DepleteConsumables does so mass, fill fraction and the flight computer follow
* [Keep a vehicle's fuel and batteries full with a refill before the solver snapshot](refill-or-drain-resources-1.md) - Call Vehicle.RefillConsumables and Battery.Refill from a prefix on Universe.ExecuteNextVehicleSolvers so the worker's copy already holds the refilled state
* [Refuel on demand from a command queue drained in the solver window](refill-or-drain-resources-2.md) - Queue a one-shot refuel or battery refill from a menu, file write or network call and execute it in a prefix on Universe.ExecuteNextVehicleSolvers
