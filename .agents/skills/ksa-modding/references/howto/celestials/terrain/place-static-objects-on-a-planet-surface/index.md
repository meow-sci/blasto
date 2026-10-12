# Game Feature

* [Place a static object on a planet surface at a latitude and longitude, and keep it anchored](overview.md) - Stand XML static objects on a planet with landmark references, redraw them when the engine culls them, and anchor mod-drawn models in the body-fixed frame so they follow rotation and restore from saves

# How-To

* [Course frame anchored at the kitten and saved as latitude, longitude, heading and floor height](place-static-objects-on-a-planet-surface-3.md) - Anchor a mod-built structure to the ground at a kitten's feet in the body-fixed frame, level its floor above the highest ground, draw its meshes per viewport and save it as latitude, longitude, heading and height above ground
* [GroundAnchor composed each frame into a camera-relative matrix](place-static-objects-on-a-planet-surface-2.md) - Anchor a mod-drawn model to a planet with a body-fixed position and normal, rebuild its camera-relative matrix every frame in the render-data hook, and restore it from the scene save
* [IsLaunchPad landmarks added at StarMapAllModsLoaded and a render-data postfix for culled objects](place-static-objects-on-a-planet-surface-1.md) - Append LandmarkReference entries to a body template before the engine resolves static objects so a multi-part XML building gets render models and colliders, and redraw it with a postfix when the engine culls it
