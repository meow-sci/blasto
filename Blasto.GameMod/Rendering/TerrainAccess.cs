using System.Reflection;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Core;
using HarmonyLib;
using KSA;
using KSA.Atmosphere.Rendering;
using KSA.Rendering.Planet;

namespace Blasto.Rendering;

/// <summary>Typed references fail immediately if a pinned game integration field changes.</summary>
internal static class TerrainAccess
{
    public static readonly AccessTools.FieldRef<PlanetRenderer, Renderer> Renderer =
        AccessTools.FieldRefAccess<PlanetRenderer, Renderer>("_renderer");
    public static readonly AccessTools.FieldRef<PlanetRenderer, VkPipeline> Solid =
        AccessTools.FieldRefAccess<PlanetRenderer, VkPipeline>("_solid");
    public static readonly AccessTools.FieldRef<PlanetRenderer, VkPipeline> Wire =
        AccessTools.FieldRefAccess<PlanetRenderer, VkPipeline>("_wire");
    public static readonly AccessTools.FieldRef<PlanetRenderer, VkPipelineLayout> Layout =
        AccessTools.FieldRefAccess<PlanetRenderer, VkPipelineLayout>("_renderPipelineLayout");
    public static readonly AccessTools.FieldRef<PlanetRenderer, ShaderStages> Stages =
        AccessTools.FieldRefAccess<PlanetRenderer, ShaderStages>("_renderShaderStages");
    public static readonly AccessTools.FieldRef<PlanetRenderer, DescriptorSetLayoutEx> SetLayout =
        AccessTools.FieldRefAccess<PlanetRenderer, DescriptorSetLayoutEx>("_renderDescriptorSetLayout");
    public static readonly AccessTools.FieldRef<PlanetRenderer, CloudShadowsRenderer> Clouds =
        AccessTools.FieldRefAccess<PlanetRenderer, CloudShadowsRenderer>("_cloudShadowsRenderer");
    public static readonly AccessTools.FieldRef<PlanetRenderer, PlanetViewportData[]> Viewports =
        AccessTools.FieldRefAccess<PlanetRenderer, PlanetViewportData[]>("_planetViewportData");
    public static readonly MethodInfo Build = AccessTools.DeclaredMethod(typeof(PlanetRenderer), "BuildRenderPipeline")
        ?? throw new MissingMethodException("PlanetRenderer.BuildRenderPipeline");
}
