# Game Feature

* [Enumerate every star system, star, planet and moon of the loaded universe](overview.md) - Walk the loaded universe's roots and body tree, a flat CelestialSystem.All scan or a player-selected root, and avoid the type traps that stars are not Celestials and Children also lists vehicles

# How-To

* [Flat scan of CurrentSystem.All plus an explicit WorldSun](enumerate-star-systems-and-bodies-3.md) - Enumerate every body with one scan of CelestialSystem.All, reading Parent per body instead of walking children, and add the root star explicitly because it is a StellarBody and not a Celestial
* [Roots then IParentBody.Children into an immutable forest, with role rules in a game-type-free class](enumerate-star-systems-and-bodies-1.md) - Build an immutable forest of every star system by walking CelestialSystem.All for independent roots and recursing through IParentBody.Children, keeping only astronomical parents and classifying stars, barycenters and minor-body exoplanets by pure rules
* [Universe.Roots, RootOf and a selected root, collecting only Celestials or only stars](enumerate-star-systems-and-bodies-2.md) - Use Universe.Roots and IIndependentRoot.RootOf to offer a system choice, recurse through Children to collect planets and moons or stars and barycenters, and keep your own selected root because WorldSun moves
