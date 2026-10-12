using System;
using System.Collections.Generic;

namespace Blasto.Rendering;

/// <summary>Decode the native uint32_t limits exposed as signed ints by Brutal's Vulkan binding.</summary>
internal readonly record struct TerrainGpuCapabilities(
    int MaxBoundDescriptorSetsRaw,
    int MaxPushConstantsSizeRaw,
    int MaxDescriptorSetStorageBuffersRaw,
    int MaxPerStageDescriptorStorageBuffersRaw,
    int MaxStorageBufferRangeRaw,
    uint RequiredStorageBufferRange)
{
    public uint MaxBoundDescriptorSets => unchecked((uint)MaxBoundDescriptorSetsRaw);
    public uint MaxPushConstantsSize => unchecked((uint)MaxPushConstantsSizeRaw);
    public uint MaxDescriptorSetStorageBuffers => unchecked((uint)MaxDescriptorSetStorageBuffersRaw);
    public uint MaxPerStageDescriptorStorageBuffers => unchecked((uint)MaxPerStageDescriptorStorageBuffersRaw);
    public uint MaxStorageBufferRange => unchecked((uint)MaxStorageBufferRangeRaw);

    public string Summary => string.Join("; ",
        Describe(nameof(MaxBoundDescriptorSets), MaxBoundDescriptorSets, 6u, MaxBoundDescriptorSetsRaw),
        Describe(nameof(MaxPushConstantsSize), MaxPushConstantsSize, 128u, MaxPushConstantsSizeRaw),
        Describe(nameof(MaxDescriptorSetStorageBuffers), MaxDescriptorSetStorageBuffers, 3u, MaxDescriptorSetStorageBuffersRaw),
        Describe(nameof(MaxPerStageDescriptorStorageBuffers), MaxPerStageDescriptorStorageBuffers, 3u, MaxPerStageDescriptorStorageBuffersRaw),
        Describe(nameof(MaxStorageBufferRange), MaxStorageBufferRange, RequiredStorageBufferRange, MaxStorageBufferRangeRaw));

    public void EnsureSupported()
    {
        var failed = new List<string>();
        Check(nameof(MaxBoundDescriptorSets), MaxBoundDescriptorSets, 6u, MaxBoundDescriptorSetsRaw);
        Check(nameof(MaxPushConstantsSize), MaxPushConstantsSize, 128u, MaxPushConstantsSizeRaw);
        Check(nameof(MaxDescriptorSetStorageBuffers), MaxDescriptorSetStorageBuffers, 3u, MaxDescriptorSetStorageBuffersRaw);
        Check(nameof(MaxPerStageDescriptorStorageBuffers), MaxPerStageDescriptorStorageBuffers, 3u, MaxPerStageDescriptorStorageBuffersRaw);
        Check(nameof(MaxStorageBufferRange), MaxStorageBufferRange, RequiredStorageBufferRange, MaxStorageBufferRangeRaw);
        if (failed.Count != 0)
            throw new InvalidOperationException("Device limits below the private terrain storage-buffer requirements: " + string.Join("; ", failed));

        void Check(string name, uint actual, uint required, int raw)
        {
            if (actual < required) failed.Add(Describe(name, actual, required, raw));
        }
    }

    private static string Describe(string name, uint actual, uint required, int raw) =>
        FormattableString.Invariant($"{name}: actual={actual}, required={required}, raw=0x{actual:X8}, signed={raw}");
}
