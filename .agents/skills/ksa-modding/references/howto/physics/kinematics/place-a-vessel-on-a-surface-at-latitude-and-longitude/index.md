# Game Feature

* [Place a vessel on a surface at a latitude and longitude or on a structure](overview.md) - How a mod sets a vessel down on a body's surface with Vehicle.TeleportToLocation, corrects its bounds-based clearance when the vessel is not nose-up, and lends a structure's surface height so a craft stands on a roof

# How-To

* [Build one-part craft in code and set them on the ground](place-a-vessel-on-a-surface-at-latitude-and-longitude-4.md) - Build a single-part craft in code with ModLibrary.Get<PartTemplate>, Vehicle.CreateVehicle and the registry lock, lend the floor height around TeleportToLocation, and strip the furniture from saves
* [Lend the pad template's SurfaceHeight around TeleportToLocation](place-a-vessel-on-a-surface-at-latitude-and-longitude-3.md) - Stand a craft on a roof by temporarily setting a launch-pad static object's SurfaceHeight in a prefix on TeleportToLocation and restoring it in a finalizer, since the start state is computed synchronously from terrain plus pad height
* [Queue TeleportToLocation once per action](place-a-vessel-on-a-surface-at-latitude-and-longitude-1.md) - Pick a craft and a destination, then call Vehicle.TeleportToLocation exactly once per action from a GUI hook and let ApplyInputEvents apply it at the next PrepareFrame, with a hover and pick-up UI over it
* [Transpile the GetInitialKinematicStateForLocation call for head-up bounds](place-a-vessel-on-a-surface-at-latitude-and-longitude-2.md) - Rewrite the one call to Vehicle.GetInitialKinematicStateForLocation inside TeleportToLocation so a kitten, whose body frame has +X as the chest, lands head-up with its feet on the terrain, by passing head-up proxy bounds and converting the result back
