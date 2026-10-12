# Game Feature

* [Keeping an anchor on a part across staging, docking, editor changes and saves](overview.md) - Which game operations move Part objects between vehicles and four ways for a mod to re-find a part afterwards

# How-To

* [Re-resolve a part every frame by vehicle id and Part.InstanceId, dormant when gone](anchor-to-parts-across-vehicle-changes-2.md) - Store a vehicle id and a Part.InstanceId, find both again every frame including subparts, and pause the feature instead of failing when either is missing
* [Read editor parts through EditingSpace and Harmony hooks without double-counting](anchor-to-parts-across-vehicle-changes-4.md) - Access the craft being built and the in-hand parts with Program.Editor, EditingSpace.AllParts and UnattachedPartTrees, and choose the hook that sees live transforms
* [Save a part reference as tree paths plus a topology hash and resolve it after reload](anchor-to-parts-across-vehicle-changes-3.md) - Capture a vehicle id, tree and subpart index paths, template id and a SHA-256 topology signature, and resolve to the same part or null after load
* [Track a part by object identity and rehome the anchor every frame](anchor-to-parts-across-vehicle-changes-1.md) - Store the Part object, validate it against its owning PartTree each frame and search the system and the editor for the same object when the owner changes
