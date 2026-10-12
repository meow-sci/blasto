# Game Feature

* [Declare ShaderEx, PostProcessingShader, GlobalPostShader and ImGuiShader assets](overview.md) - The four ShaderExtensions XML asset types, how Id, RenderPassId, paths and binding children fit together, and how references let a later pass read an earlier pass's image

# How-To

* [Declare post-processing assets: a small file with a chain, an input reference and an ImGuiShader](declare-post-processing-shader-assets-in-xml-2.md) - One asset file with three chained PostProcessingShader passes, a PostProcessingInputReference that lets the composite see the unblurred scene, and an ImGuiShader for one window
* [Declare post-processing assets: input and output references resolved while the chain is built](declare-post-processing-shader-assets-in-xml-3.md) - How PostProcessingInputReference and PostProcessingOutputReference pick an earlier pass's image, which stage each defaults to, and why a reference to a later pass cannot work
* [Declare post-processing assets: paired pre and post passes sharing a RenderPassId and buffer](declare-post-processing-shader-assets-in-xml-1.md) - Ship a whole effects chain as one XML file with a PostProcessingShader and a GlobalPostShader per effect, ordered by RenderPassId and sharing parameter buffers by Id
