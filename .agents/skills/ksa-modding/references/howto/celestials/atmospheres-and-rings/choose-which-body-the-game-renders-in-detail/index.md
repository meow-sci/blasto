# Game Feature

* [Control which body the game picks as the camera's nearby celestial](overview.md) - The game draws one body per camera in full detail and every other as a distant sphere, chosen by CelestialSystem.GetNearbyAstronomical; a small moon can steal the slot and make its planet flicker, fixed here by a guarded transpiler

# How-To

* [Transpile the distance comparison into a ranking call that favours a body within 25 radii](choose-which-body-the-game-renders-in-detail.md) - Rewrite the surface-versus-centre comparison in CelestialSystem.GetNearbyAstronomical with a transpiler that shape-checks the IL, calls a static ranking method that favours a body the camera is within 25 radii of, and returns the original IL on any mismatch
