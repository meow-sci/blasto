# Game Feature

* [Defining a part with its own game data and GLB model](overview.md) - How KSA loads Assets and GameData XML bundles into PartTemplates, which XML elements a part accepts, and three mods' part definitions

# How-To

* [Define a driveable part with colliders, a mass shape and its own editor category](define-a-part-with-game-data-1.md) - Write an Assets bundle with a GLB atlas, PBR material and subparts, and a GameData bundle with an editor tag, mass shape and colliders that make a single part a flyable craft
* [Define furniture as a minimal static part that doubles as a craft](define-a-part-with-game-data-3.md) - The smallest Assets and GameData pair for a prop part that is also a one-part craft, with a hidden door subpart and no control module on the prop
* [Split inert weapon parts into Assets and GameData with real RCS thrusters on subparts](define-a-part-with-game-data-2.md) - Declare art, subparts and structure in one bundle and mass, colliders, connectors, editor tags and engine-driven thrusters in another, with behaviour left to C# that finds the part by Id
