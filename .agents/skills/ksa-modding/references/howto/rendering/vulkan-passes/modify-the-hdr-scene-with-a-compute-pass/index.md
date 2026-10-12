# Game Feature

* [Paint into or rescale the HDR scene with a compute pass shipped as a Shader asset](overview.md) - From the pre-bloom hook a mod can read the scene colour and depth and write back into it with compute only, and KSA compiles the mod's .comp files when they are declared as Shader assets

# How-To

* [Add a fireball's diffuse term where the pre-pass culled it beyond about 3 km](modify-the-hdr-scene-with-a-compute-pass-3.md) - Push one point light through the public LightDebug list and fill in the far range with a compute dispatch that reads the pre-pass irradiance by reflection
* [Depth-reading compute dispatches for rings, holes, tracers and relighting](modify-the-hdr-scene-with-a-compute-pass-1.md) - Ship .comp files as Shader assets, build ComputePipelineWrapper pipelines over the HDR scene colour and depth, and dispatch from a prefix on SunbloomRenderer.Render
* [In-place rescale using KSA's light pre-pass images](modify-the-hdr-scene-with-a-compute-pass-2.md) - Rescale each scene pixel by the share of light that did not come from ambient by reading the light pre-pass irradiance images in a Priority.First compute prefix
