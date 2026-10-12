# Game Feature

* [Add dynamic spot and point lights to the world from a mod](overview.md) - KSA clears its light list every frame, so a mod submits its own Light instances after ClearLights and before the lights are written, for headlights, lamps, flashes and fireballs, and budgets which ones cast shadows

# How-To

* [Postfix PartTree.UpdateRenderData per craft and submit spot lights with CreateSpotLight](add-dynamic-lights-to-the-world-1.md) - Light the road with headlamps and tail lamps by creating Light.CreateSpotLight instances for each craft and viewport in a postfix on PartTree.UpdateRenderData, after the frame's lights were cleared and before they are written to the GPU
* [Push a point light through the public LightDebug list, with a compute fill beyond 3 km](add-dynamic-lights-to-the-world-3.md) - Light the land around a fireball by appending a Light.CreatePointLight to LightDebug.Lights, the one list the engine re-pushes after clearing its lights, and fill in the pixels the clustered light pre-pass drops past about 3 km
* [Submit per-viewport lights from an UpdateStaticObjectRenderData postfix with a shadow-caster budget](add-dynamic-lights-to-the-world-2.md) - Light a building with ceiling lamps by building spot lights in each viewport's camera-relative frame from a postfix on LocationReference.UpdateStaticObjectRenderData and flagging only the nearest ones to cast shadows
