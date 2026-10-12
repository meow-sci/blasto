using System.Text;

namespace Blasto;

public sealed partial class Mod
{
    private string GetDiagnostics()
    {
        var text = new StringBuilder();
        text.AppendLine($"Blasto {typeof(Mod).Assembly.GetName().Version} / KSA {typeof(KSA.Program).Assembly.GetName().Version}");
        text.AppendLine($"Effects={_settings.Enabled}; dust={_settings.DustEnabled}; response={_settings.Intensity:F2}; saved sites={_store.Count}");
        if (_renderer is { } renderer)
        {
            text.AppendLine($"Renderer: {renderer.Status}; enabled={renderer.Enabled}; highlight={renderer.DiagnosticHighlight}");
            text.AppendLine($"Pipelines={renderer.ActivePipelineCount}; builds={renderer.BuildAttempts}; failures={renderer.BuildFailures}; render calls={renderer.RenderCalls}; private binds={renderer.DescriptorBinds}");
            text.AppendLine($"GPU limits: {renderer.GpuCapabilitiesSummary}");
            foreach (var view in renderer.Views.Where(view => view.RenderCalls > 0))
            {
                string distance = double.IsFinite(view.NearestMarkDistanceMeters) ? $"{view.NearestMarkDistanceMeters:F1} m" : "none";
                text.AppendLine($"View {view.Slot} (main={view.IsMain}), body={view.BodyId}: {view.Status}");
                text.AppendLine($"  selected={view.SelectedMarkCount}; on body={view.BodyMarkCount}; too weak={view.WeakMarkCount}; too far={view.OutOfRangeMarkCount}; nearest={distance}; strongest={view.StrongestMarkStrength:P1}; renders={view.RenderCalls}; binds={view.DescriptorBinds}");
            }
        }
        if (_simulation is { } simulation)
        {
            text.AppendLine($"Simulation: {simulation.Status}");
            text.AppendLine($"Applies={simulation.SolverApplyCalls}; captures={simulation.CaptureCalls}; nozzles={simulation.NozzlesExamined}; queued={simulation.PendingSamples}; sampled={simulation.CapturedSamples}; applied={simulation.AppliedSamples}");
            text.AppendLine($"Surface hits={simulation.SurfaceHits}; misses={simulation.SurfaceRejected}; rejected deposits={simulation.DepositRejected}; last={simulation.LastSurfaceResult}");
            text.AppendLine($"Errors: capture={simulation.CaptureErrors}, surface={simulation.SurfaceErrors}, tick={simulation.TickErrors}; warp skips={simulation.WarpSkipped}; queue skips={simulation.QueueRejected}");
            text.AppendLine($"Test placement: {simulation.DebugPlacementStatus}");
        }
        if (_persistence is { } persistence) text.AppendLine($"Save: {persistence.Status}");
        return text.ToString();
    }
}
