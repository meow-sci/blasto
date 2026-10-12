# Game Feature

* [Share types and design an inter-mod API between StarMap mods](overview.md) - Why mods that load their own copy of a type cannot talk to each other, and five ways to share identity or avoid needing it - contract imports, API design, duck typing, name-based binding and stable fork identity

# How-To

* [Bind plugins by attribute type name the way StarMap does](share-types-between-mods-4.md) - Copy StarMap's host-side pattern - register your own attribute instances by type name, match plugin attributes by name, then validate signatures with the host's instance - so plugins need not share your assembly
* [Design a text-only registration API with fault results and a CI-checked surface record](share-types-between-mods-2.md) - Shape the surface other mods bind to so it survives refactors - text in, results out, a closing deadline, a schema constant - and diff a generated API-surface record in CI
* [Duck-type an API: copy look-alike attributes and bind by full name across load contexts](share-types-between-mods-3.md) - Let consumer mods copy a few attribute classes into their own assembly while the host scans every AssemblyLoadContext with GetCustomAttributesData and wires delegates, so no assembly reference or mod dependency is needed
* [Fork or rename a library mod without breaking its dependents: new mod id, unchanged assembly identity](share-types-between-mods-5.md) - Change only the KSA mod id (folder name) while keeping the assembly name, namespace, attribute full names and Harmony id, so dependents keep working through ImportedAssemblies
* [Share a contract assembly: the importer lists it in ImportedAssemblies](share-types-between-mods-1.md) - Publish a game-free contract assembly and let consumer mods resolve it from your load context so both sides share one Assembly, one Type identity and one static registry
