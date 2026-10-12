# Game Feature

* [Keep a craft under full physics while your mod pushes it](overview.md) - Why a railed craft ignores velocity writes, which per-craft checks decide rails status, and two ways to keep a pushed craft in the physics bubble

# How-To

* [Postfix PhysicsBubble.KittenWantsWake from a concurrent set](keep-a-pushed-craft-off-rails-1.md) - Make the per-craft wake check answer true for craft your mod is pushing, mark them from the PrepareWorker prefix, and wake a landed or coasting craft before the first push
* [Request wake or off-rails from the flight computer command sink](keep-a-pushed-craft-off-rails-2.md) - Let a mod actuator writer on FlightComputer.ComputeControl set AnyActuatorCommanded and an earlier wake-up time in the stock output, so the physics bubble takes the craft off rails and re-runs the flight computer on time
