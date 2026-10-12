# Game Feature

* [Smuggle data into stock shaders through unused uniforms, state bits and sentinel values](overview.md) - Stock shaders have fixed inputs, so a patched shader borrows fields that are free or irrelevant for the case at hand and decodes them in GLSL

# How-To

* [Smuggle data into stock shaders: a marker in a material's ExtraData plus a shader-side branch](send-extra-data-to-stock-shaders-without-new-uniforms-3.md) - Create a custom material whose ExtraData carries parameters and a marker value, then branch on the marker in a patched model shader so only that material changes
* [Smuggle data into stock shaders: a temporary value swap in a prefix, or an edit of the boxed struct in a postfix](send-extra-data-to-stock-shaders-without-new-uniforms-1.md) - Write a clock and a switch into atmosphere uniform fields the haze bodies do not use for the length of one call, send spin through unused ring slots, and clear the LUT-valid flags when the switch changes
* [Smuggle data into stock shaders: cut the stock MiePhaseFunction and insert a dispatcher on asymmetry sentinels](send-extra-data-to-stock-shaders-without-new-uniforms-4.md) - Select new shader behaviour from out-of-range values of an existing XML parameter and ship lookup data as generated GLSL source, with no new uniform, binding or texture
* [Smuggle data into stock shaders: spare high bits of the part state flags](send-extra-data-to-stock-shaders-without-new-uniforms-2.md) - Pack a 21-bit value into bits 11 to 31 of PartTreeRenderData's per-instance state word with postfixes on the private writers, and invalidate the cache so the new bits are rewritten
