using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Blasto.Core;
using Blasto.Persistence;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using HarmonyLib;
using KSA;

namespace Blasto.Rendering;

/// <summary>Terrain-only native material integration with stock pipelines retained for immediate fail-open.</summary>
public sealed class TerrainBlastRenderer : IDisposable
{
    private readonly Func<IReadOnlyList<BlastMark>> _marks;
    private readonly Dictionary<PlanetRenderer, BlastRenderResources> _resources = new();
    private readonly List<MethodBase> _patched = new();
    private readonly TerrainViewDiagnostics[] _views = Enumerable.Range(0, 8).Select(slot => new TerrainViewDiagnostics(slot)).ToArray();
    private Harmony? _harmony;
    private bool _disposed;
    private static TerrainBlastRenderer? _current;
    [ThreadStatic] private static bool _buildingPrivate;
    [ThreadStatic] private static RenderSwap? _active;
    public bool Enabled { get; set; } = true;
    public bool DiagnosticHighlight { get; set; }
    private BlastSettings _settings = new();
    public BlastSettings Settings
    {
        get => _settings;
        set
        {
            BlastVisualMath.ValidateGpuSettings(value);
            foreach (var mark in _marks())
            {
                if (!mark.IsValid()) continue;
                BlastVisualMath.CheckedGpuFloat(BlastVisualMath.EffectiveRadius(mark, value), "Effective mark radius", positive: true);
                BlastVisualMath.CheckedGpuFloat(BlastVisualMath.EffectiveStrength(mark, value), "Effective mark exposure");
            }
            _settings = value;
        }
    }
    public IReadOnlyList<TerrainViewDiagnostics> Views => _views;
    public int ActivePipelineCount => _resources.Count;
    public long BuildAttempts { get; private set; }
    public long BuildFailures { get; private set; }
    public long RenderCalls { get; private set; }
    public long DescriptorBinds { get; private set; }
    public string Status { get; private set; } = "Waiting for terrain renderer";
    public string GpuCapabilitiesSummary { get; private set; } = "Waiting for terrain device limits";

    public TerrainBlastRenderer(Func<IReadOnlyList<BlastMark>> marks) => _marks = marks ?? throw new ArgumentNullException(nameof(marks));

    public void Install(Harmony harmony)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(TerrainBlastRenderer));
        if (_harmony != null) return;
        if (_current != null) throw new InvalidOperationException("A Blasto terrain renderer is already installed.");
        _current = this;
        _harmony = harmony;
        try
        {
            BlastGpuMark.AssertLayout();
            // Descriptor binding numbers/stage flags are audited for this build, not inferred from spare fields.
            if (typeof(PlanetRenderer).Assembly.GetName().Version?.ToString() != "2026.10.10.5554")
                throw new NotSupportedException("The terrain integration targets KSA 2026.10.10.5554; stock terrain retained.");
            Patch(TerrainAccess.Build, postfix: nameof(AfterBuild));
            Patch(AccessTools.DeclaredMethod(typeof(PlanetRenderer), nameof(PlanetRenderer.Render)),
                prefix: nameof(BeforeRender), transpiler: nameof(ReplaceForwardBinding), finalizer: nameof(AfterRender));
            Patch(AccessTools.DeclaredMethod(typeof(PlanetRenderer), nameof(PlanetRenderer.Dispose)), prefix: nameof(BeforeNativeDispose));
            Console.WriteLine("[Blasto] Terrain hooks installed: private set2 binding13, six sets, 128-byte push constants.");
        }
        catch (Exception ex)
        {
            Status = "Stock terrain: " + ex.GetBaseException().Message;
            Console.WriteLine("[Blasto] " + Status);
            foreach (var method in _patched) harmony.Unpatch(method, HarmonyPatchType.All, harmony.Id);
            _patched.Clear(); _harmony = null; _current = null;
            throw;
        }
    }

    private void Patch(MethodBase method, string? prefix = null, string? postfix = null, string? transpiler = null, string? finalizer = null)
    {
        HarmonyMethod? Hook(string? name) => name == null ? null : new HarmonyMethod(typeof(TerrainBlastRenderer), name);
        _harmony!.Patch(method, Hook(prefix), Hook(postfix), Hook(transpiler), Hook(finalizer));
        _patched.Add(method);
    }

    private static void AfterBuild(PlanetRenderer __instance)
    {
        if (_buildingPrivate || _current is not { _disposed: false } owner) return;
        owner.BuildPrivate(__instance);
    }

    private unsafe void BuildPrivate(PlanetRenderer planet)
    {
        BuildAttempts++;
        if (_resources.Remove(planet, out var previous)) previous.Dispose();
        BlastRenderResources? resources = null;
        var solid = TerrainAccess.Solid(planet);
        var wire = TerrainAccess.Wire(planet);
        var layout = TerrainAccess.Layout(planet);
        ShaderStages stages = TerrainAccess.Stages(planet);
        Span<VkPipelineShaderStageCreateInfo> originals = stages.Slice(0).ToArray();
        try
        {
            if (stages.Count is not (2 or 4)) throw new InvalidOperationException("Terrain stage count changed.");
            resources = new BlastRenderResources(planet, summary => GpuCapabilitiesSummary = summary);
            var modules = new Dictionary<VkShaderStageFlags, VkShaderModule> {
                [VkShaderStageFlags.VertexBit] = resources.Compile("PlanetVert"),
                [VkShaderStageFlags.FragmentBit] = resources.Compile("PlanetFrag") };
            if (stages.Count == 4) modules[VkShaderStageFlags.TessellationEvaluationBit] = resources.Compile("PlanetTessEvaluation");
            Span<VkPipelineShaderStageCreateInfo> nativeStages = stages.Slice(0);
            for (int i = 0; i < nativeStages.Length; i++)
                if (modules.TryGetValue(nativeStages[i].Stage, out var module)) nativeStages[i].Module = module;
            TerrainAccess.Layout(planet) = resources.Layout;
            _buildingPrivate = true;
            try { TerrainAccess.Build.Invoke(planet, null); }
            finally
            {
                // Capture partial creation too, so a wire-pipeline failure cannot leak the solid pipeline.
                if (!TerrainAccess.Solid(planet).Equals(solid)) resources.Solid = TerrainAccess.Solid(planet);
                if (!TerrainAccess.Wire(planet).Equals(wire)) resources.Wire = TerrainAccess.Wire(planet);
            }
            if (resources.Solid.IsNull() || resources.Wire.IsNull())
                throw new InvalidOperationException("Private terrain pipeline creation returned an empty handle.");
            _resources.Add(planet, resources);
            Status = "Terrain material renderer ready (64 marks per view)";
            Console.WriteLine("[Blasto] " + Status + "; independent frame/view SSBO slots; displaced tessellation lookup.");
            resources = null;
        }
        catch (Exception ex)
        {
            BuildFailures++;
            Status = "Stock terrain: " + ex.GetBaseException().Message;
            Console.WriteLine("[Blasto] Private terrain build failed; " + Status);
        }
        finally
        {
            _buildingPrivate = false;
            originals.CopyTo(stages.Slice(0));
            TerrainAccess.Solid(planet) = solid;
            TerrainAccess.Wire(planet) = wire;
            TerrainAccess.Layout(planet) = layout;
            resources?.Dispose();
        }
    }

    private sealed class RenderSwap
    {
        public required PlanetRenderer Planet;
        public VkPipeline Solid, Wire;
        public VkPipelineLayout Layout;
        public VkDescriptorSet MarkSet;
        public required TerrainViewDiagnostics Diagnostics;
    }

    private static void BeforeRender(PlanetRenderer __instance, IViewport inViewport, int frameIndex, out RenderSwap? __state)
    {
        __state = null;
        if (_current is not { _disposed: false } owner) return;
        owner.RenderCalls++;
        if (inViewport.ShaderSlot < 0 || inViewport.ShaderSlot >= owner._views.Length) return;
        var diagnostics = owner._views[inViewport.ShaderSlot];
        diagnostics.RenderCalls++;
        diagnostics.IsMain = inViewport.IsMain();
        diagnostics.BodyId = "";
        diagnostics.ResetSelection();
        if (!owner.Enabled) { diagnostics.Status = "Effects disabled"; return; }
        if (!owner._resources.TryGetValue(__instance, out var resources))
        { diagnostics.Status = "Private terrain pipeline unavailable"; return; }
        try
        {
            var views = TerrainAccess.Viewports(__instance);
            if (inViewport.ShaderSlot >= views.Length) { diagnostics.Status = "Invalid terrain view slot"; return; }
            var body = views[inViewport.ShaderSlot].NearbyCelestial;
            if (body == null) { diagnostics.Status = "No nearby terrain body"; return; }
            diagnostics.BodyId = body.Id;
            if (!body.IsBillboarded()) { diagnostics.Status = "Body uses distant rendering"; return; }
            if (_active != null) throw new InvalidOperationException("Nested forward planet render is unsupported.");
            var set = resources.Prepare(inViewport, frameIndex, body, owner._marks(), diagnostics, owner.DiagnosticHighlight, owner.Settings);
            diagnostics.Status = diagnostics.SelectedMarkCount > 0 ? "Marks uploaded; waiting for terrain bind" :
                diagnostics.BodyMarkCount == 0 ? "No saved marks on this body" :
                diagnostics.WeakMarkCount == diagnostics.BodyMarkCount ? "Marks below visible strength" : "Marks outside 5 km view range";
            var swap = new RenderSwap { Planet = __instance, Solid = TerrainAccess.Solid(__instance),
                Wire = TerrainAccess.Wire(__instance), Layout = TerrainAccess.Layout(__instance), MarkSet = set, Diagnostics = diagnostics };
            TerrainAccess.Solid(__instance) = resources.Solid;
            TerrainAccess.Wire(__instance) = resources.Wire;
            TerrainAccess.Layout(__instance) = resources.Layout;
            _active = __state = swap;
        }
        catch (Exception ex)
        {
            owner.Enabled = false;
            owner.Status = "Stock terrain: " + ex.GetBaseException().Message;
            diagnostics.Status = owner.Status;
            Console.WriteLine("[Blasto] Terrain upload failed; " + owner.Status);
        }
    }

    private static Exception? AfterRender(Exception? __exception, RenderSwap? __state)
    {
        if (__state != null)
        {
            if (__exception != null) __state.Diagnostics.Status = "Terrain draw failed: " + __exception.GetBaseException().Message;
            TerrainAccess.Solid(__state.Planet) = __state.Solid;
            TerrainAccess.Wire(__state.Planet) = __state.Wire;
            TerrainAccess.Layout(__state.Planet) = __state.Layout;
            _active = null;
        }
        return __exception;
    }

    private static IEnumerable<CodeInstruction> ReplaceForwardBinding(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        int matches = 0;
        MethodInfo replacement = AccessTools.DeclaredMethod(typeof(TerrainBlastRenderer), nameof(BindForward));
        foreach (var instruction in code)
        {
            if (instruction.operand is not MethodInfo method || method.Name != nameof(VkDeviceExtensions.BindDescriptorSets) ||
                method.DeclaringType != typeof(VkDeviceExtensions)) continue;
            if (!method.IsGenericMethod || method.GetGenericArguments()[0] != typeof(CommandBuffer) ||
                method.GetParameters().Length != 6)
                throw new InvalidOperationException("Planet forward descriptor bind signature changed.");
            instruction.opcode = OpCodes.Call;
            instruction.operand = replacement;
            matches++;
        }
        if (matches != 1) throw new InvalidOperationException($"Expected one forward planet descriptor bind, found {matches}.");
        return code;
    }

    private static void BindForward(CommandBuffer command, VkPipelineBindPoint point, VkPipelineLayout layout,
        int firstSet, ReadOnlySpan<VkDescriptorSet> sets, Span<ByteSize32> offsets)
    {
        if (_active == null) { command.BindDescriptorSets(point, layout, firstSet, sets, offsets); return; }
        Span<VkDescriptorSet> privateSets = stackalloc VkDescriptorSet[6];
        sets.CopyTo(privateSets);
        privateSets[2] = _active.MarkSet;
        command.BindDescriptorSets(point, layout, firstSet, privateSets, offsets);
        _active.Diagnostics.DescriptorBinds++;
        if (_current != null) _current.DescriptorBinds++;
        if (_active.Diagnostics.SelectedMarkCount > 0) _active.Diagnostics.Status = "Private terrain bound with marks";
    }

    private static void BeforeNativeDispose(PlanetRenderer __instance)
    {
        if (_current != null && _current._resources.Remove(__instance, out var resources)) resources.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Enabled = false;
        foreach (var resource in _resources.Values) resource.Dispose();
        _resources.Clear();
        if (_harmony != null)
            foreach (var method in _patched) _harmony.Unpatch(method, HarmonyPatchType.All, _harmony.Id);
        _patched.Clear(); _harmony = null;
        if (ReferenceEquals(_current, this)) _current = null;
        Status = "Stock terrain restored";
    }
}
