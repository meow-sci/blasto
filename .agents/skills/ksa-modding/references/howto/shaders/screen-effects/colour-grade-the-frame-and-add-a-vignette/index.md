# Game Feature

* [Colour-grade the frame and add an aspect-correct vignette](overview.md) - Where in KSA's frame a colour grade belongs, the GLSL building blocks MPFX uses for lift/gamma/gain, temperature, hue, contrast, saturation and vignette, and a TV or thermal look for a secondary camera feed

# How-To

* [Colour-grade a camera feed: a two-dispatch measure and map pass per viewport with a cached pipeline](colour-grade-the-frame-and-add-a-vignette-2.md) - Give a secondary camera window a TV or thermal look with a compute pass on its HDR scene image before bloom, using a 1x1 log-mean reduction in a first dispatch and a mapping dispatch second
* [Colour-grade the frame: pre-ImGui grading passes on the HDR scene](colour-grade-the-frame-and-add-a-vignette-1.md) - Write colour balance, colour temperature, hue shift, contrast, saturation, overlay and an aspect-correct vignette as short ShaderExtensions passes that return their input at neutral values and are driven by one parameter block each
