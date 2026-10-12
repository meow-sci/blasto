# Game Feature

* [Make static objects cast sun and lamp shadows with invisible casters](overview.md) - Static objects never enter a shadow pass, so add depth-only caster meshes to the SuperMesh shadow pool and let a building, roof or pillar block sunlight and lamp light without drawing a second mesh

# How-To

* [Add invisible depth-only shadow casters to the SuperMesh shadow pool](add-invisible-shadow-casters.md) - Make static objects or other geometry block sunlight and lamp light by appending ShadowRenderables for a never-drawn caster mesh from a prefix on PrepareShadowCasters and AddPoolToShadowBucket
