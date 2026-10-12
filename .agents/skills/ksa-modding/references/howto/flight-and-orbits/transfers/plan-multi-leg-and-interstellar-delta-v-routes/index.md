# Game Feature

* [Plan and total multi-leg and interstellar delta-v routes](overview.md) - Classify a vessel's orbit state, derive a reference orbit for each body, find a route through the body tree, sum the leg delta-vs, and estimate fuel and coast time for an interstellar trip

# How-To

* [Build a per-body state ladder and classify the vessel onto it with a snapped eccentricity rule](plan-multi-leg-and-interstellar-delta-v-routes-2.md) - Derive surface, low orbit, stationary and SOI-edge states from live celestial data, then classify the controlled vessel's orbit onto its parent's ladder using HasMeaningfulOrbit and eccentricity snapping
* [Find a delta-v route through a re-rooted body tree and total it with a display-time margin](plan-multi-leg-and-interstellar-delta-v-routes-1.md) - Route between two states through their lowest common ancestor in a tree rooted at the body you are at, accumulate the legs in two passes with landing, aerobraking and plane-change toggles, and apply a piloting margin only when strings are built
* [Price an interstellar leg by reusing the stock burn model, with a golden-section speed search and a rocket-equation fallback](plan-multi-leg-and-interstellar-delta-v-routes-3.md) - Fold the hyperbolic excess through every gravity well, search for the cheapest and the fastest affordable cruise speed, and compute fuel with InterstellarBurnPlan for an antimatter ram drive or the rocket equation otherwise
