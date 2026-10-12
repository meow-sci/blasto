using System.Reflection;
using System.Runtime.CompilerServices;
using Blasto.Core;
using HarmonyLib;
using KSA;

namespace Blasto.Persistence;

/// <summary>Owns only Blasto's save transaction; never overwrites another mod's callbacks or sidecar.</summary>
internal sealed class BlastPersistence : IDisposable
{
    private static BlastPersistence? _instance;
    private readonly BlastSiteStore _store;
    private readonly Func<BlastSettings> _settings;
    private readonly Action<BlastSettings> _restoreSettings;
    private readonly Action _resetSimulation;
    private readonly Action<string> _log;
    private readonly ConditionalWeakTable<GameSave, BlastSaveDocument> _captures = new();
    private LoadContext? _loading;
    private byte[]? _recovery;
    public string Status { get; private set; } = "Marks are saved with KSA saves.";

    private sealed record LoadContext(BlastSaveDocument? Document, byte[]? Recovery, string? Error);

    public BlastPersistence(BlastSiteStore store, Func<BlastSettings> settings,
        Action<BlastSettings> restoreSettings, Action resetSimulation, Action<string> log)
    {
        _store = store; _settings = settings; _restoreSettings = restoreSettings;
        _resetSimulation = resetSimulation; _log = log;
    }

    public void Install(Harmony harmony)
    {
        _instance = this;
        Patch(harmony, typeof(GameSave), nameof(GameSave.Populate), [], postfix: nameof(Capture));
        Patch(harmony, typeof(UncompressedSave), nameof(UncompressedSave.Write), [], postfix: nameof(Written));
        Patch(harmony, typeof(UncompressedSave), nameof(UncompressedSave.Load), [],
            prefix: nameof(Loading), finalizer: nameof(LoadFinished));
        Patch(harmony, typeof(Universe), nameof(Universe.DeserializeSave), [typeof(UniverseData)],
            prefix: nameof(BeforeDeserialize), postfix: nameof(AfterDeserialize));
        Patch(harmony, typeof(Universe), nameof(Universe.LoadSystem), [typeof(string)],
            prefix: nameof(BeforeSystem));
    }

    private static void Patch(Harmony harmony, Type type, string name, Type[] parameters,
        string? prefix = null, string? postfix = null, string? finalizer = null)
    {
        MethodInfo target = AccessTools.Method(type, name, parameters) ?? throw new MissingMethodException(type.FullName, name);
        HarmonyMethod? Hook(string? method) => method is null ? null : new HarmonyMethod(typeof(BlastPersistence), method)
        { priority = Priority.Last };
        harmony.Patch(target, Hook(prefix), Hook(postfix), finalizer: Hook(finalizer));
    }

    private static void Capture(GameSave __instance, bool __runOriginal)
    {
        var self = _instance;
        if (self is null || !__runOriginal) return;
        try
        {
            self._captures.Remove(__instance);
            self._captures.Add(__instance, new BlastSaveDocument { Settings = self._settings(), Marks = self._store.Snapshot() });
        }
        catch (Exception ex) { self.Report($"Could not capture blast marks: {ex.Message}"); }
    }

    private static void Written(UncompressedSave __instance, bool __result, bool __runOriginal)
    {
        var self = _instance;
        if (self is null || !__runOriginal) return;
        try
        {
            if (!__result) { self.Report("KSA save failed; blast state was not written."); return; }
            if (!self._captures.TryGetValue(__instance, out var document))
            { self.Report("Native save has no matching blast snapshot; sidecar was not written."); return; }
            if (self._recovery is { } recovery)
                BlastSaveCodec.AtomicWrite(Path.Combine(__instance.Directory.FullName, BlastSaveCodec.RecoveryFileName), recovery);
            BlastSaveCodec.Write(__instance.Directory.FullName, document);
            self.Status = $"Saved {document.Marks.Length} blast marks." + (self._recovery is null ? "" : " Unrestored data retained in blasto.recovery.json.");
        }
        catch (Exception ex) { self.Report($"KSA saved, but blast state could not be written: {ex.Message}"); }
        finally { self._captures.Remove(__instance); }
    }

    private static void Loading(UncompressedSave __instance, out LoadContext? __state)
    {
        var self = _instance;
        __state = self?._loading;
        if (self is null || KSA.Program.IsEditorOpen) return;
        string directory = __instance.Directory.FullName;
        try
        {
            var document = BlastSaveCodec.Read(directory);
            string recoveryPath = Path.Combine(directory, BlastSaveCodec.RecoveryFileName);
            byte[]? recovery = File.Exists(recoveryPath) ? BlastSaveCodec.ReadBounded(recoveryPath) : null;
            self._loading = new(document, recovery, null);
        }
        catch (Exception ex)
        {
            byte[]? recovery = null;
            try { recovery = BlastSaveCodec.ReadBounded(Path.Combine(directory, BlastSaveCodec.FileName)); }
            catch (Exception readError) { self._log($"Could not retain invalid sidecar: {readError.Message}"); }
            self._loading = new(null, recovery, ex.Message);
        }
    }

    private static Exception? LoadFinished(Exception? __exception, LoadContext? __state)
    {
        if (_instance is { } self)
        {
            self._loading = __state;
            if (__exception is not null) self.Report($"Native load failed: {__exception.Message}");
        }
        return __exception;
    }

    private static void BeforeDeserialize(UniverseData universeData, bool __runOriginal, out bool __state)
    {
        __state = false;
        if (!__runOriginal || _instance is not { } self || Universe.CurrentSystem is null || universeData is null) return;
        // Only managed records and owned particles are reset; GPU resources remain alive through scene loads.
        // KSA joins the same workers immediately inside DeserializeSave.
        JobSystems.WaitAll();
        self.ResetWorld();
        __state = true;
    }

    private static void AfterDeserialize(bool __state, bool __runOriginal)
    {
        if (!__state || !__runOriginal || _instance is not { } self) return;
        try
        {
            var context = self._loading;
            self._recovery = context?.Recovery;
            if (context?.Document is { } document)
            {
                self._store.Replace(document.Marks);
                self._restoreSettings(document.Settings);
                var system = Universe.CurrentSystem;
                int missing = document.Marks.Count(mark => system is null || !system.All.TryGet<Celestial>(mark.BodyId, out _));
                self.Status = $"Restored {document.Marks.Length} blast marks." +
                    (missing == 0 ? "" : $" {missing} marks reference missing bodies and remain saved.");
            }
            else self.Status = "Loaded scene without blast marks.";
            if (context?.Error is { } error) self.Report($"Blast state was not restored: {error}" +
                (self._recovery is null ? "" : " Original data will be retained as blasto.recovery.json on the next save."));
        }
        catch (Exception ex) { self.Report($"Could not restore blast marks: {ex.Message}"); }
    }

    private static void BeforeSystem(string id, bool __runOriginal)
    {
        if (!__runOriginal || _instance is not { } self || Universe.CurrentSystem is null || SystemLibrary.Find(id) is null) return;
        JobSystems.WaitAll();
        self.ResetWorld();
        self.Status = "New scene; blast marks cleared.";
    }

    private void ResetWorld()
    {
        _resetSimulation();
        _store.Clear();
        _recovery = null;
        _restoreSettings(new BlastSettings());
    }

    private void Report(string message) { Status = message; _log(message); }
    public void Dispose() { if (ReferenceEquals(_instance, this)) _instance = null; _captures.Clear(); _loading = null; _recovery = null; }
}
