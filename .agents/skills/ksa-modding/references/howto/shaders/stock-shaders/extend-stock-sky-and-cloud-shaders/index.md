# Game Feature

* [Extend KSA's sky and cloud shaders: per-pixel haze and cloud lighting fixes](overview.md) - How the sky and volumetric cloud passes are structured, and four text-patch recipes that add a per-pixel march over the sky LUTs and fix cloud lighting, handover and ring shadows

# How-To

* [Extend the sky shader: a per-pixel raymarch inserted into Atmosphere.comp and blended by tangent altitude](extend-stock-sky-and-cloud-shaders-1.md) - Insert a function before main() and branch the sky path in Atmosphere.comp so haze bodies march per pixel over the azimuth-free LUT result, with the cost culled where the effect cannot show
* [Fix cloud lighting: light a thick raymarch step at its first optical depth by bisection](extend-stock-sky-and-cloud-shaders-2.md) - Patch RaymarchCloud.comp so a dense step is lit where its optical depth passes 1, with a short near-sample shadow march toward the Sun, keeping dense decks bright at any step size
* [Fix the cloud handover: flow and lift the volumetric map to match the 2D layer](extend-stock-sky-and-cloud-shaders-3.md) - Patch CloudFunctions.glsl so volumetric cloud lookups follow the same flowmap phases as the 2D shell and faint map values are lifted to the opacity the 2D layer draws
* [Reuse stock helpers in a cloud shader: include RingShadows.glsl and select mips without derivatives](extend-stock-sky-and-cloud-shaders-4.md) - Add the rings' shadow to both cloud passes by including the game's RingShadows.glsl from the patched copy and picking the texture mip in compute from the ring coordinate's change across one pixel
