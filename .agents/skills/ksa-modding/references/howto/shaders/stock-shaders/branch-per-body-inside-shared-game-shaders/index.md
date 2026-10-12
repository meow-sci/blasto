# Game Feature

* [Branch per body inside shared game shaders](overview.md) - One stock shader draws every body, so per-body behaviour has to be keyed on values the shader can already see, such as the body radius plus an XML-written number, or a point's own height

# How-To

* [Branch per body in shared shaders: a relief-aware terminator closing over a sqrt(2 relief / R) band](branch-per-body-inside-shared-game-shaders-2.md) - Replace the smooth-ball self-shadow in the planet and ground-clutter fragment shaders with a horizon test from each point's own height while keeping other bodies' eclipses
* [Branch per body in shared shaders: measured ln-density tables recognised by radius and an exact scale height](branch-per-body-inside-shared-game-shaders-1.md) - Replace six exponential density sites in four stock atmosphere shaders with one injected function that picks a per-body table by radius and scale height, and generate the tables offline from the same script as the flight model
