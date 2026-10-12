# Game Feature

* [Blur the screen with a two-pass separable Gaussian](overview.md) - How to blur the whole frame with a horizontal and a vertical ShaderExtensions pass, whether the weights come from the CPU or from a fixed non-linear tap distribution, and how to keep a sharp copy for a composite

# How-To

* [Blur the screen: a non-linear tap distribution and a refracted horizontal pass](blur-the-screen-with-a-separable-gaussian-2.md) - Blur with a fixed 97-tap Gaussian whose taps are squared toward the centre, bend the sample position through a normal mask in the horizontal pass, and composite the sharp scene with an input reference
* [Blur the screen: CPU-computed normalized weights up to radius 20 in a std140 block](blur-the-screen-with-a-separable-gaussian-1.md) - Compute Gaussian weights on the CPU, upload them with the radius in one uniform block and run one shader file as the horizontal and vertical pass of both ShaderExtensions chains
