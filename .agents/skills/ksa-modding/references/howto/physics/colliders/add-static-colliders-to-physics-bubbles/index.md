# Game Feature

* [Add mod-owned static colliders that vessels and kittens collide with](overview.md) - How a mod makes placed models or course geometry solid, by adding Bepu statics to every nearby physics bubble each pass, or by declaring XML static objects, and why the stock contact filter must be taught to admit them

# How-To

* [Declare XML static objects and let the engine collide the nearest pad](add-static-colliders-to-physics-bubbles-3.md) - Stand a walkable building on a planet by declaring StaticObject XML and appending launch-pad landmarks to the planet template at AllModsLoaded, so the engine places and collides the nearest pad's colliders for every body
* [Per-bubble static handles from a BeginStaticObjectPass postfix](add-static-colliders-to-physics-bubbles-1.md) - Add a Bepu Box or two-sided Mesh static to every nearby surface bubble at the start of each substep, and transpile the stock contact filter so only those handles in that exact simulation count as ground
* [Worker prefixes on DetectCollisions and Simulate with statics registered as ground clutter](add-static-colliders-to-physics-bubbles-2.md) - Add Bepu box statics to each Ccf bubble from prefixes on the constraint-sim passes and register them as infinite-mass ground clutter, so the stock filter already treats them as ground for kittens and vessels
