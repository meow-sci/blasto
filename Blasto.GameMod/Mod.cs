using System.Collections.Concurrent;
using System.Reflection;
using Blasto.Core;
using Blasto.Effects;
using Blasto.Persistence;
using Blasto.Rendering;
using Blasto.Simulation;
using HarmonyLib;
using KSA;
using StarMap.API;

namespace Blasto;

[StarMapMod]
public sealed partial class Mod
{
    private const string HarmonyId = "meowsci.blasto";
    private static Mod? _instance;
    private readonly BlastSiteStore _store = new();
    private readonly ConcurrentQueue<Action> _commands = new();
    private Harmony? _harmony;
    private BlastSimulation? _simulation;
    private TerrainBlastRenderer? _renderer;
    private BlastPersistence? _persistence;
    private BlastSettings _settings = new();
    private BlastMark[] _renderSnapshot = [];
    private long _snapshotVersion = -1;
    private bool _disposed, _initialized, _windowVisible;
    private string _message = "Initializing";
    public bool ImmediateUnload => false;

    [StarMapBeforeMain]
    public void BeforeMain()
    {
        if (_initialized || _disposed) return;
        _instance = this;
        _harmony = new Harmony(HarmonyId);
        try
        {
            _simulation = new BlastSimulation(_store, Log);
            _renderer = new TerrainBlastRenderer(GetRenderSnapshot);
            _persistence = new BlastPersistence(_store, () => _settings, ApplySettings, ResetSimulation, Log);
            _renderer.Install(_harmony);
            _simulation.Install(_harmony);
            _persistence.Install(_harmony);
            Patch(typeof(KSA.Program), "DrawProgramMenusHook", nameof(DrawMenu), false);
            Patch(typeof(KSA.Program), "OnDrawUiConsole", nameof(DrawHiddenUi), true);
            Patch(typeof(KSA.Program), nameof(KSA.Program.Dispose), nameof(BeforeGameDispose), true);
            Patch(typeof(GameSettings), nameof(GameSettings.OnKeyAll), nameof(GuardTextInput), true);
            _harmony.Patch(AccessTools.Method(typeof(Universe), nameof(Universe.ApplyVehicleSolvers)),
                prefix: new HarmonyMethod(typeof(Mod), nameof(DrainCommands)) { priority = Priority.First });
            ApplySettings(_settings);
            _initialized = true;
            _message = "Ready. Launch, land or hover over dry terrain to create blast marks.";
            Log("Initialized for KSA 2026.10.10.5554.");
        }
        catch (Exception ex)
        {
            _message = $"Initialization failed: {ex.Message}";
            Log(ex.ToString());
            ReleaseResources();
            _harmony.UnpatchAll(HarmonyId);
        }
    }

    [StarMapAfterGui]
    public void AfterGui(double dt)
    {
        if (!_disposed) DrawWindow();
    }

    [StarMapUnload]
    public void Unload()
    {
        if (!_disposed) ReleaseResources();
        _harmony?.UnpatchAll(HarmonyId);
        _harmony = null;
        if (ReferenceEquals(_instance, this)) _instance = null;
    }

    private BlastMark[] GetRenderSnapshot()
    {
        if (_snapshotVersion != _store.Version)
        { _renderSnapshot = _store.Snapshot(); _snapshotVersion = _store.Version; }
        return _renderSnapshot;
    }

    private void ApplySettings(BlastSettings settings)
    {
        if (!settings.IsValid()) throw new ArgumentException("Invalid effect settings.");
        LandingDust.ValidateTuning(settings);
        // Validate products used by the GPU before committing any part of the new settings.
        if (_renderer is { } renderer) { renderer.Settings = settings; renderer.Enabled = settings.Enabled; }
        _settings = settings;
        if (_simulation is { } simulation)
        {
            simulation.Enabled = settings.Enabled; simulation.DustEnabled = settings.DustEnabled;
            simulation.Intensity = settings.Intensity; simulation.ErosionThreshold = settings.ErosionThreshold;
            simulation.DustOpacity = settings.DustOpacity; simulation.DustSize = settings.DustSize;
            simulation.DustSpeed = settings.DustSpeed;
        }
    }

    private void ResetSimulation()
    {
        while (_commands.TryDequeue(out _)) { }
        _simulation?.Reset();
    }

    private void ReleaseResources()
    {
        if (_disposed) return;
        _disposed = true;
        // During normal shutdown this prefix runs before KSA destroys particles and the Vulkan device.
        // Late StarMap unload is then a managed-only no-op.
        try { if (_initialized) JobSystems.WaitAll(); }
        catch (Exception ex) { Log($"Worker shutdown: {ex.Message}"); }
        foreach (var disposable in new IDisposable?[] { _simulation, _persistence, _renderer })
            try { disposable?.Dispose(); }
            catch (Exception ex) { Log($"Cleanup: {ex.Message}"); }
        _simulation = null; _persistence = null; _renderer = null;
        while (_commands.TryDequeue(out _)) { }
    }

    private void Patch(Type type, string name, string hook, bool prefix)
    {
        MethodInfo target = AccessTools.Method(type, name) ?? throw new MissingMethodException(type.FullName, name);
        var patch = new HarmonyMethod(typeof(Mod), hook);
        if (prefix) _harmony!.Patch(target, prefix: patch); else _harmony!.Patch(target, postfix: patch);
    }

    private static void DrainCommands()
    {
        if (_instance is not { _disposed: false } self) return;
        // Commands can reset the capture generation. Join workers first so an old
        // physics interval cannot enqueue a fresh-generation sample after a clear.
        if (!self._commands.IsEmpty) JobSystems.WaitAll();
        while (self._commands.TryDequeue(out var command))
            try { command(); } catch (Exception ex) { self._message = ex.Message; Log(ex.ToString()); }
    }

    private static void BeforeGameDispose() => _instance?.ReleaseResources();
    private static void DrawHiddenUi() { if (!KSA.Program.DrawUI) _instance?.DrawWindow(); }
    private static void Log(string message) => Console.WriteLine($"[Blasto] {message}");
}
