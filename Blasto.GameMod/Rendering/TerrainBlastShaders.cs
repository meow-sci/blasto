using System;
using System.IO;
using System.Text;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using KSA;
using RenderCore;
using RenderCore.Shaders;

namespace Blasto.Rendering;

/// <summary>Private variants are compiled explicitly; stock ShaderReference caches are never modified.</summary>
internal static class TerrainBlastShaders
{
    public static VkShaderModule Compile(Device device, string id)
    {
        var reference = ModLibrary.Get<ShaderReference>(id);
        string path = Path.GetFullPath(reference.ModPath);
        string source = TerrainShaderSource.Transform(File.ReadAllText(path), id);
        // Native resolver sees the original absolute source path, preserving relative includes.
        using CompileOptions options = ShaderCompilerResolve.GetCompileOptionsWithIncludeCallback();
        return ShaderModuleUtils.FromString(device, Encoding.UTF8.GetBytes(source), reference.Stage,
            options, Encoding.UTF8.GetBytes(path + "\0"));
    }

}
