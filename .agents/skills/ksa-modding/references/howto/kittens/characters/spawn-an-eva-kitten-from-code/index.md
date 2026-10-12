# Game Feature

* [Spawn an EVA kitten from code](overview.md) - How a mod creates a real KittenEva with a backpack part, a chosen character and an orbit beside another vessel, registers it with its parent body and avoids racing the vehicle solver

# How-To

* [Build a KittenEva from a saved craft that names a Character, inside the shapes lock](spawn-an-eva-kitten-from-code-2.md) - KSArmory loads a craft save, builds a KittenEva instead of a Vehicle when the save names a Character (as VehicleTemplate does), holds ConstraintSim.UnlockShapesBlocking around construction and attaches it to the parent body with the derived-data calls a saved tree has never been through
* [Replicate EVADoor.CreateKittenEva with the public KittenEva constructor, place the kitten beside a vessel, then track and despawn it](spawn-an-eva-kitten-from-code-1.md) - doh builds the backpack part and the eight-argument KittenEva by hand, derives position and orbit from one StateVectors read of a reference vehicle, waits for the vehicle solver before constructing, and keeps a registry that despawns safely
