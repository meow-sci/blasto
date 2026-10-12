# Game Feature

* [Pick the vessel, part, world quad or terrain point under the cursor](overview.md) - How a mod decides what the mouse is over in the 3D view, from screen-space nearest through hull ray casts to terrain marches, and how it acts on the answer

# How-To

* [Pick objects under the cursor: clearance-stepped terrain march with bisection, then a buffered TeleportToLocation](pick-objects-under-the-cursor-4.md) - Find the terrain point under the cursor by walking the ray out from the eye and bisecting the first crossing, then place a craft there once per action with Vehicle.TeleportToLocation
* [Pick objects under the cursor: Part.RayCastEgo hull test plus a terrain march](pick-objects-under-the-cursor-2.md) - Pick the craft whose hull the cursor ray hits using the engine's own part ray cast, decide whether ground is nearer, and use the ground point to fire at a spot
* [Pick objects under the cursor: ray hit to texture UV for interactive in-world UI](pick-objects-under-the-cursor-3.md) - Turn a cursor ray hit on a world quad into texture coordinates and then widget cells so an in-world panel can be clicked and typed into
* [Pick objects under the cursor: Ray.RaycastWatertight against parts, then a terrain march with bisection](pick-objects-under-the-cursor-5.md) - Turn the cursor or screen-centre ray into a surface anchor, a mesh-precise part hit first and a bisected terrain hit behind it, as a one-shot placement command
* [Pick objects under the cursor: screen-space nearest with a per-item reach](pick-objects-under-the-cursor-1.md) - Choose the vessel under the mouse by projecting every vehicle to the screen and taking the nearest one within a reach that scales with its apparent size
