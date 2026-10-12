using Brutal.ImGuiApi;
using Brutal.Numerics;

namespace Blasto;

public sealed partial class Mod
{
    private bool _confirmClear;
    private static void DrawMenu()
    {
        if (_instance is not { _disposed: false } self || !ImGui.BeginMenu("Blasto")) return;
        try
        {
            if (ImGui.MenuItem("Landing effects...")) self._windowVisible = true;
        }
        finally { ImGui.EndMenu(); }
    }

    private void DrawWindow()
    {
        if (!_windowVisible || _disposed) return;
        try
        {
            ImGui.SetNextWindowSize(new float2(540, 640), ImGuiCond.FirstUseEver);
            bool contents = ImGui.Begin("Blasto — landing effects", ref _windowVisible);
            try
            {
                if (!contents) return;
                ImGui.Text($"Blasto {typeof(Mod).Assembly.GetName().Version?.ToString(3) ?? "unknown"}");
                bool enabled = _settings.Enabled;
                bool dust = _settings.DustEnabled;
                float intensity = (float)_settings.Intensity;
                if (ImGui.Checkbox("Enable landing effects", ref enabled))
                    ApplySettings(_settings with { Enabled = enabled });
                if (ImGui.Checkbox("Landing dust", ref dust))
                    ApplySettings(_settings with { DustEnabled = dust });
                if (ImGui.SliderFloat("Surface response", ref intensity, .1f, 5f))
                    ApplySettings(_settings with { Intensity = intensity });
                ImGui.TextWrapped("Marks stay with the surface after the vehicle leaves. Effects and settings are stored with KSA saves.");
                ImGui.Separator();
                ImGui.Text($"Saved sites: {_store.Count} / {_store.Capacity}");
                if (_renderer is { } renderer)
                {
                    ImGui.TextWrapped(renderer.Status);
                    foreach (var view in renderer.Views.Where(view => view.IsMain && view.RenderCalls > 0))
                        ImGui.TextWrapped($"Viewing {view.BodyId}: {view.SelectedMarkCount} marks sent to terrain. {view.Status}");
                    bool highlight = renderer.DiagnosticHighlight;
                    if (ImGui.Checkbox("Highlight marks (diagnostic)", ref highlight))
                        renderer.DiagnosticHighlight = highlight;
                }
                if (_simulation is { } simulation)
                {
                    ImGui.TextWrapped(simulation.Status);
                    if (simulation.WarpSkipped > 0)
                        ImGui.TextWrapped($"Skipped {simulation.WarpSkipped} long physics steps. Existing marks remain intact.");
                    if (simulation.QueueRejected > 0)
                        ImGui.TextWrapped($"Exposure budget reached: {simulation.QueueRejected} samples skipped.");
                }
                if (_store.CapacityRejected > 0)
                    ImGui.TextWrapped("Site limit reached. Existing marks are preserved; new locations are skipped.");
                if (_persistence is { } persistence) ImGui.TextWrapped(persistence.Status);
                ImGui.Separator();
                if (ImGui.Button("Place test mark below vehicle"))
                {
                    if (_simulation is { } sim && sim.TryPlaceDebugMark(out _))
                    {
                        ApplySettings(_settings with { Enabled = true });
                        if (_renderer is { } testRenderer) testRenderer.DiagnosticHighlight = true;
                        _message = "Projection highlight enabled. Turn it off to inspect the normal surface effect.";
                    }
                }
                if (_simulation is { } placement) ImGui.TextWrapped(placement.DebugPlacementStatus);
                ImGui.TextWrapped("Creates a strong, persistent 20 m test mark within 350 m of dry terrain and enables a bright magenta projection highlight. Highlight is temporary and ignores lighting and height tolerance.");
                if (ImGui.Button("Copy diagnostics"))
                {
                    string report = GetDiagnostics();
                    ImGui.SetClipboardText(report);
                    Log(report);
                    _message = "Diagnostics copied to clipboard and written to the game log.";
                }
                if (ImGui.CollapsingHeader("Diagnostics")) ImGui.TextWrapped(GetDiagnostics());
                if (!_confirmClear)
                {
                    if (ImGui.Button("Clear this scene's marks...")) _confirmClear = true;
                }
                else
                {
                    ImGui.TextWrapped("Remove all marks in the current scene? Existing disk saves change only when saved again.");
                    if (ImGui.Button("Clear marks"))
                    {
                        _commands.Enqueue(() => { ResetSimulation(); _store.Clear(); _message = "Scene marks cleared."; });
                        _confirmClear = false;
                    }
                    ImGui.SameLine();
                    if (ImGui.Button("Cancel")) _confirmClear = false;
                }
                ImGui.Separator();
                ImGui.TextWrapped(_message);
            }
            finally { ImGui.End(); }
        }
        catch (Exception ex)
        { _windowVisible = false; Log($"Controls closed after UI error: {ex.Message}"); }
    }

    private static bool GuardTextInput(ref bool __result)
    {
        if (!KSA.Program.ConsoleWindow.IsOpen && ImGui.GetIO().WantTextInput)
        { __result = true; return false; }
        return true;
    }
}
