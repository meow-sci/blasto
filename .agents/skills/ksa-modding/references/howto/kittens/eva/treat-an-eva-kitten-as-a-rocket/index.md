# Game Feature

* [Fly an EVA kitten as an ordinary rocket vessel, with vessel input and the flight HUD](overview.md) - How a mod makes one chosen kitten take the vessel branches of physics, input and the HUD - flipping the IsKitten snapshot flag, publishing membership to workers, rotating the control frame and swapping the backpack RCS map - and two ways to call the base Vehicle handlers that the kitten overrides

# How-To

* [Call the base through a DynamicMethod non-virtual call so other mods' patches still run](treat-an-eva-kitten-as-a-rocket-3.md) - Iron Man emits a DynamicMethod that makes a plain call to the Vehicle base handlers, entering Harmony's patched replacement so every other prefix and transpiler still runs, unlike a reverse-patch copy
* [Call the base Vehicle handlers through a Harmony reverse patch onto a static stub](treat-an-eva-kitten-as-a-rocket-2.md) - Iron Man replaces KittenEva.OnKey and ProcessInput with prefixes that call the vessel implementations through static stubs that Harmony reverse-patches with a copy of the base Vehicle method bodies, because a cast to Vehicle would dispatch virtually back into the patched override
* [Fly an EVA kitten as a vessel - IsKitten postfix, Ctrl2Body postfix, queued mode switches, input and HUD transpilers](treat-an-eva-kitten-as-a-rocket-1.md) - Iron Man flips VehicleUpdateState.IsKitten for one chosen kitten, publishes membership to workers, queues mode switches to the PrepareFrame handoff, rotates Ctrl2Body, swaps the backpack RCS map and routes vessel keys and flight-computer gauges to the kitten
