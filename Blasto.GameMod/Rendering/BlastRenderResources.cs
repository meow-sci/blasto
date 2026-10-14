using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Blasto.Core;
using Blasto.Persistence;
using Brutal;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Core;
using KSA;
using KSA.Rendering;
using KSA.Rendering.Planet;

namespace Blasto.Rendering;

internal sealed unsafe class BlastRenderResources : IDisposable
{
    private readonly Renderer _owner;
    private readonly int _frames, _stride;
    private readonly VkDescriptorSet[] _sets;
    private readonly List<VkShaderModule> _modules = new();
    private readonly (BlastMark Mark, double Score, double Distance)[] _selected = new (BlastMark, double, double)[BlastGpuMark.MaximumMarks];
    private DescriptorSetLayoutEx? _setLayout;
    private DescriptorPoolEx? _pool;
    private BufferEx? _buffer;
    private MappedMemory? _map;
    private bool _disposed;
    public VkPipelineLayout Layout;
    public VkPipeline Solid, Wire;
    public Renderer Owner => _owner;

    public BlastRenderResources(PlanetRenderer planet, Action<string> reportCapabilities)
    {
        _owner = TerrainAccess.Renderer(planet);
        _frames = _owner.MaxFramesInFlight;
        BlastGpuMark.AssertLayout();
        ref var limits = ref _owner.GetLimits();
        var capabilities = new TerrainGpuCapabilities(limits.MaxBoundDescriptorSets, limits.MaxPushConstantsSize,
            limits.MaxDescriptorSetStorageBuffers, limits.MaxPerStageDescriptorStorageBuffers,
            limits.MaxStorageBufferRange, BlastGpuMark.Range);
        reportCapabilities(capabilities.Summary);
        capabilities.EnsureSupported();
        _stride = (int)new ByteSize(BlastGpuMark.Range).AlignTo(KSA.Rendering.Utils.MinStorageBufferOffsetAlignment);
        _sets = new VkDescriptorSet[_frames * ViewportRegistry.MAX_VIEWPORTS];
        try
        {
            var bindings = BlastDescriptorLayout.CreateBindings(TerrainAccess.SetLayout(planet));
            _setLayout = _owner.Device.CreateDescriptorSetLayout(new DescriptorSetLayoutEx.CreateInfo { Bindings = bindings }, null);
            VkDescriptorSetLayout[] layouts = [GlobalShaderBindings.DescriptorSetLayout, Program.Instance.TextureSystem.Layout,
                _setLayout, TerrainAccess.Clouds(planet).CloudShadowDescriptorSetLayout,
                planet.GetSunShadowRenderer().TerrainShadowMap.MainSetLayout, Program.GetCSMSystem().DescriptorSetLayout];
            var push = new VkPushConstantRange { StageFlags = VkShaderStageFlags.VertexBit |
                VkShaderStageFlags.TessellationEvaluationBit | VkShaderStageFlags.FragmentBit,
                Offset = ByteSize.Zero, Size = ByteSize.Of<PlanetViewportData.PushConstData>() };
            Layout = _owner.Device.CreatePipelineLayout(layouts, new ReadOnlySpan<VkPushConstantRange>(ref push), null);
            _pool = _owner.Device.CreateDescriptorPool(new DescriptorPoolEx.CreateInfo
                { MaxSets = _sets.Length, PoolSizes = BlastDescriptorLayout.PoolSizes(_sets.Length) }, null);
            _buffer = _owner.Allocator.CreateBuffer(new BufferEx.CreateInfo { Name = "Blasto terrain marks",
                BufferSize = new ByteSize((uint)(_stride * _sets.Length)), BufferUsage = VkBufferUsageFlags.StorageBufferBit,
                AllocRequiredProperties = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit });
            _map = _buffer.Value.Map();
            _map.Value.AsSpan().Clear();
            for (int i = 0; i < _sets.Length; i++)
            {
                _sets[i] = _owner.Device.AllocateDescriptorSet(_pool, _setLayout);
                var info = new VkDescriptorBufferInfo { Buffer = _buffer.Value.VkBuffer,
                    Offset = _buffer.Value.BindOffset + new ByteSize((uint)(i * _stride)), Range = new ByteSize(BlastGpuMark.Range) };
                var write = new VkWriteDescriptorSet { DstSet = _sets[i], DstBinding = 13, DescriptorCount = 1,
                    DescriptorType = VkDescriptorType.StorageBuffer, BufferInfo = &info };
                _owner.Device.UpdateDescriptorSets(new ReadOnlySpan<VkWriteDescriptorSet>(ref write), default);
            }
        }
        catch { Dispose(); throw; }
    }

    public VkShaderModule Compile(string id)
    {
        VkShaderModule module = TerrainBlastShaders.Compile(_owner.Device, id);
        _modules.Add(module);
        return module;
    }

    public VkDescriptorSet Prepare(IViewport viewport, int frame, Celestial body, IReadOnlyList<BlastMark> marks,
        TerrainViewDiagnostics diagnostics, bool diagnosticHighlight, BlastSettings settings)
    {
        if (_disposed || frame < 0 || frame >= _frames || viewport.ShaderSlot < 0 || viewport.ShaderSlot >= ViewportRegistry.MAX_VIEWPORTS)
            throw new InvalidOperationException("Blasto viewport/frame allocation no longer matches the game.");
        int slot = viewport.ShaderSlot * _frames + frame;
        // Each slot is written only after the native frame fence retires its prior use.
        Span<VkCopyDescriptorSet> copies = stackalloc VkCopyDescriptorSet[13];
        for (int i = 0; i < copies.Length; i++) copies[i] = new VkCopyDescriptorSet {
            SrcSet = body.TextureSet, SrcBinding = i, DstSet = _sets[slot], DstBinding = i, DescriptorCount = 1 };
        _owner.Device.UpdateDescriptorSets(default, copies);
        Span<byte> bytes = _map!.Value.AsSpan().Slice(slot * _stride, BlastGpuMark.Range);
        bytes.Clear();
        var camera = viewport.GetCamera();
        var rotation = body.GetCce2Ccf();
        // Use the same precise relative origin as native planet rendering, including followed-body cameras.
        double3 cameraCcf = (-camera.GetPositionEgo(body)).Transform(rotation);
        int count = 0, eligible = 0;
        foreach (var mark in marks)
        {
            if (!string.Equals(mark.BodyId, body.Id, StringComparison.Ordinal) || !mark.IsValid()) continue;
            diagnostics.BodyMarkCount++;
            double distance = (Vector(mark.CenterCcf) - cameraCcf).LengthSquared();
            diagnostics.NearestMarkDistanceMeters = Math.Min(diagnostics.NearestMarkDistanceMeters, Math.Sqrt(distance));
            double radius = BlastVisualMath.EffectiveRadius(mark, settings);
            double strength = BlastVisualMath.EffectiveStrength(mark, settings);
            BlastVisualMath.CheckedGpuFloat(radius, "Effective mark radius", positive: true);
            BlastVisualMath.CheckedGpuFloat(strength, "Effective mark exposure");
            diagnostics.StrongestMarkStrength = Math.Max(diagnostics.StrongestMarkStrength, strength);
            if (strength <= 0) { diagnostics.WeakMarkCount++; continue; }
            // Include any footprint that reaches the view range, even when its center is farther away.
            if (Math.Sqrt(distance) > BlastVisualMath.ViewRangeMeters + radius * mark.Anisotropy)
            { diagnostics.OutOfRangeMarkCount++; continue; }
            eligible++;
            double score = BlastVisualMath.ContributionScore(radius, mark.Anisotropy, strength, distance, settings);
            int insertion = count;
            while (insertion > 0 && ComesBefore(mark, score, distance, _selected[insertion - 1])) insertion--;
            if (insertion >= BlastGpuMark.MaximumMarks) continue;
            int last = Math.Min(count, BlastGpuMark.MaximumMarks - 1);
            for (int j = last; j > insertion; j--) _selected[j] = _selected[j - 1];
            _selected[insertion] = (mark, score, distance);
            count = Math.Min(count + 1, BlastGpuMark.MaximumMarks);
        }
        var inverse = rotation.Inverse();
        for (int i = 0; i < count; i++)
        {
            BlastMark mark = _selected[i].Mark;
            // Subtract in doubles before packing: no planet-scale float subtraction.
            double3 center = (Vector(mark.CenterCcf) - cameraCcf).Transform(inverse);
            double3 u = Vector(mark.TangentU).Transform(inverse), v = Vector(mark.TangentV).Transform(inverse);
            float radius = BlastVisualMath.CheckedGpuFloat(BlastVisualMath.EffectiveRadius(mark, settings), "Effective mark radius", positive: true);
            float strength = BlastVisualMath.CheckedGpuFloat(BlastVisualMath.EffectiveStrength(mark, settings), "Effective mark exposure");
            diagnostics.LargestSelectedRadiusMeters = Math.Max(diagnostics.LargestSelectedRadiusMeters, radius);
            var gpu = new BlastGpuMark { CenterRadius = new float4(float3.Pack(center), radius),
                TangentUStrength = new float4(float3.Pack(u), strength),
                TangentVAnisotropy = new float4(float3.Pack(v), (float)mark.Anisotropy),
                TintRoughness = Tint(mark.Profile), Seed = unchecked((uint)mark.Seed), Profile = (uint)mark.Profile };
            MemoryMarshal.Write(bytes.Slice(BlastGpuMark.HeaderSize + i * BlastGpuMark.Size, BlastGpuMark.Size), in gpu);
        }
        var header = new BlastGpuHeader { Count = (uint)count, Flags = diagnosticHighlight ? 1u : 0u,
            Tuning0 = new float4((float)settings.SizeMultiplier, (float)settings.Visibility, (float)settings.Darkness, (float)settings.StreakStrength),
            Tuning1 = new float4((float)settings.CenterStrength, (float)settings.EdgeSoftness, 0, 0) };
        MemoryMarshal.Write(bytes, in header);
        diagnostics.SelectedMarkCount = count;
        diagnostics.BudgetExcludedMarkCount = eligible - count;
        return _sets[slot];
    }

    private static bool ComesBefore(BlastMark mark, double score, double distance,
        (BlastMark Mark, double Score, double Distance) previous) =>
        score > previous.Score || (score == previous.Score &&
            (distance < previous.Distance || (distance == previous.Distance && mark.Id.CompareTo(previous.Mark.Id) < 0)));

    private static double3 Vector(Double3 v) => new(v.X, v.Y, v.Z);
    private static float4 Tint(SurfaceKind profile) => profile switch {
        SurfaceKind.Martian => new(.54f,.46f,.42f,.98f), SurfaceKind.Soil => new(.58f,.5f,.42f,.98f),
        SurfaceKind.Rock => new(.85f,.85f,.85f,.96f), _ => new(.58f,.58f,.59f,.98f) };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _owner.Device.WaitIdle();
        if (!Solid.IsNull()) _owner.Device.DestroyPipeline(Solid, null);
        if (!Wire.IsNull()) _owner.Device.DestroyPipeline(Wire, null);
        Solid = Wire = default;
        if (!Layout.IsNull()) _owner.Device.DestroyPipelineLayout(Layout, null);
        Layout = default;
        _pool?.Dispose(); _pool = null;
        _map?.Unmap(); _map = null;
        _buffer?.Dispose(); _buffer = null;
        _setLayout?.Dispose(); _setLayout = null;
        foreach (var module in _modules) _owner.Device.DestroyShaderModule(module, null);
        _modules.Clear();
        Array.Clear(_selected);
    }
}
