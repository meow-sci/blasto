using System;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

namespace Blasto.Rendering;

/// <summary>Forward-only set2 clone. All native consumers retain their original layout and sets.</summary>
internal static class BlastDescriptorLayout
{
    public static VkDescriptorSetLayoutBinding[] CreateBindings(DescriptorSetLayoutEx native)
    {
        var counts = native.Descriptors;
        if (counts.Count != 5 || !Has(VkDescriptorType.UniformBufferDynamic, 2) ||
            !Has(VkDescriptorType.StorageBuffer, 2) || !Has(VkDescriptorType.CombinedImageSampler, 2) ||
            !Has(VkDescriptorType.SampledImage, 5) || !Has(VkDescriptorType.Sampler, 2))
            throw new InvalidOperationException("Native terrain descriptor schema changed; retaining stock rendering.");
        bool Has(VkDescriptorType type, int count) => counts.TryGetValue(type, out int actual) && actual == count;

        const VkShaderStageFlags V = VkShaderStageFlags.VertexBit;
        const VkShaderStageFlags T = VkShaderStageFlags.TessellationEvaluationBit;
        const VkShaderStageFlags F = VkShaderStageFlags.FragmentBit;
        const VkShaderStageFlags C = VkShaderStageFlags.ComputeBit;
        return [
            Binding(0,VkDescriptorType.UniformBufferDynamic,V|T|F|C|VkShaderStageFlags.TessellationControlBit),
            Binding(1,VkDescriptorType.UniformBufferDynamic,T|F|C),
            Binding(2,VkDescriptorType.StorageBuffer,V|T|F|C),
            Binding(3,VkDescriptorType.StorageBuffer,V|T|F|C),
            Binding(4,VkDescriptorType.CombinedImageSampler,V),
            Binding(5,VkDescriptorType.CombinedImageSampler,F|C),
            Binding(6,VkDescriptorType.SampledImage,V|T|F),
            Binding(7,VkDescriptorType.SampledImage,F),
            Binding(8,VkDescriptorType.SampledImage,F),
            Binding(9,VkDescriptorType.SampledImage,V|T|F),
            Binding(10,VkDescriptorType.SampledImage,V|T|F),
            Binding(11,VkDescriptorType.Sampler,V|T|F),
            Binding(12,VkDescriptorType.Sampler,V|T|F),
            Binding(13,VkDescriptorType.StorageBuffer,F)
        ];
    }

    private static VkDescriptorSetLayoutBinding Binding(int binding, VkDescriptorType type, VkShaderStageFlags stages) =>
        new() { Binding = binding, DescriptorCount = 1, DescriptorType = type, StageFlags = stages };

    public static VkDescriptorPoolSize[] PoolSizes(int slots) => [
        new() { Type = VkDescriptorType.UniformBufferDynamic, DescriptorCount = 2 * slots },
        new() { Type = VkDescriptorType.StorageBuffer, DescriptorCount = 3 * slots },
        new() { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 2 * slots },
        new() { Type = VkDescriptorType.SampledImage, DescriptorCount = 5 * slots },
        new() { Type = VkDescriptorType.Sampler, DescriptorCount = 2 * slots }
    ];
}
