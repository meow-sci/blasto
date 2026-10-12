# Game Feature

* [Set or clear a vehicle's target, including bodies the game cannot target](overview.md) - Set and clear the vehicle target from mod code through the same queued path the UI uses, and make fixed stars and barycenters selectable as a navigation-only target the stock target list refuses

# How-To

* [Make fixed stars and barycenters targetable with weak tables keyed by Vehicle and BurnPlan](set-and-clear-the-vehicle-target-2.md) - Keep a navigation-only target for bodies that are not IOrbiter in ConditionalWeakTables keyed by the Vehicle and by the BurnPlan the solver's flight computer copy shares, queue UI requests, and drain them in an ApplyInputEvents prefix
* [Set and clear the target with Universe.SetTargetCommand and UnsetTargetCommand](set-and-clear-the-vehicle-target-1.md) - Select an orbiting body or star as the vehicle target from an ImGui button using the game's own terminal commands, which queue a ChangeTargetData for the input point so the change matches stock timing
