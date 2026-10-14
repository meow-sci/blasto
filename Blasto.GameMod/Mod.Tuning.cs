using Blasto.Persistence;
using Brutal.ImGuiApi;
using Brutal.Numerics;

namespace Blasto;

public sealed partial class Mod
{
    private string _tuningError = "";

    private void DrawTuning()
    {
        ImGui.TextWrapped("Drag to adjust. Double-click or Ctrl-click a value to type beyond the drag range.");
        if (ImGui.CollapsingHeader("Surface appearance (live)"u8, ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (DrawTuningControl("Size multiplier"u8, _settings.SizeMultiplier, .05f, .1f, 10f, true,
                    "Multiplier of each mark's stored radius. Updates existing and future marks immediately."u8, out double size))
                TryApplyTuning(_settings with { SizeMultiplier = size });
            if (DrawTuningControl("Visibility"u8, _settings.Visibility, .1f, 0f, 30f, false,
                    "Amplifies surface visibility to bring weak saved exposure into view. Updates existing and future marks immediately; zero hides their normal appearance."u8, out double visibility))
                TryApplyTuning(_settings with { Visibility = visibility });
            if (DrawTuningControl("Darkness"u8, _settings.Darkness, .05f, 0f, 10f, false,
                    "Surface darkening multiplier. Updates existing and future marks immediately."u8, out double darkness))
                TryApplyTuning(_settings with { Darkness = darkness });
            if (DrawTuningControl("Streak strength"u8, _settings.StreakStrength, .05f, 0f, 8f, false,
                    "Radial streak contrast multiplier. Updates existing and future marks immediately."u8, out double streak))
                TryApplyTuning(_settings with { StreakStrength = streak });
            if (DrawTuningControl("Center strength"u8, _settings.CenterStrength, .02f, 0f, 4f, false,
                    "Central surface response multiplier. Updates existing and future marks immediately."u8, out double center))
                TryApplyTuning(_settings with { CenterStrength = center });
            if (DrawTuningControl("Edge softness"u8, _settings.EdgeSoftness, .02f, .05f, 3f, true,
                    "Edge falloff multiplier; larger values soften the boundary. Updates existing and future marks immediately."u8, out double edge))
                TryApplyTuning(_settings with { EdgeSoftness = edge });
        }
        if (ImGui.CollapsingHeader("Mark buildup (future exposure)"u8, ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (DrawTuningControl("Buildup rate"u8, _settings.Intensity, .1f, 0f, 20f, false,
                    "Exposure buildup multiplier. Changes future engine exposure; stored mark exposure is preserved. Zero stops new buildup."u8, out double intensity))
                TryApplyTuning(_settings with { Intensity = intensity });
            if (DrawTuningControl("Erosion threshold (Pa)"u8, _settings.ErosionThreshold, .5f, 0f, 100f, false,
                    "Minimum exhaust pressure in pascals for future surface buildup and dust emission. Zero accepts all positive pressure."u8, out double threshold))
                TryApplyTuning(_settings with { ErosionThreshold = threshold });
        }
        if (ImGui.CollapsingHeader("Dust"u8, ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (DrawTuningControl("Dust opacity"u8, _settings.DustOpacity, .02f, 0f, 5f, false,
                    "Opacity multiplier for dust. Updates currently active emitters and future dust emission immediately."u8, out double opacity))
                TryApplyTuning(_settings with { DustOpacity = opacity });
            if (DrawTuningControl("Dust size"u8, _settings.DustSize, .02f, .1f, 5f, true,
                    "Particle size multiplier for newly emitted dust. Existing particles finish their current lifetimes."u8, out double dustSize))
                TryApplyTuning(_settings with { DustSize = dustSize });
            if (DrawTuningControl("Dust speed"u8, _settings.DustSpeed, .02f, 0f, 5f, false,
                    "Velocity multiplier for newly emitted dust. Existing particles finish their current lifetimes."u8, out double speed))
                TryApplyTuning(_settings with { DustSpeed = speed });
        }
        if (ImGui.Button("Reset tuning defaults"u8))
        {
            if (TryApplyTuning(new BlastSettings(Enabled: _settings.Enabled, DustEnabled: _settings.DustEnabled)))
                _message = "Tuning defaults restored.";
        }
        ImGui.SetItemTooltip("Restores appearance, buildup and dust tuning. Keeps both enable switches as they are."u8);
        if (_tuningError.Length > 0)
            ImGui.TextColored(new float4(1f, .45f, .35f, 1f), _tuningError);
    }

    private bool TryApplyTuning(BlastSettings settings)
    {
        try
        {
            ApplySettings(settings);
            _tuningError = "";
            return true;
        }
        catch (ArgumentException ex)
        {
            _tuningError = $"Tuning rejected: {ex.Message} The previous settings are retained.";
            return false;
        }
    }

    private bool DrawTuningControl(ImString label, double current, float speed, float minimum,
        float maximum, bool positive, ImString tooltip, out double updated)
    {
        float value = (float)current;
        // None intentionally leaves manual text entry free of the drag range.
        bool changed = ImGui.DragFloat(label, ref value, speed, minimum, maximum, "%.3g"u8, ImGuiSliderFlags.None);
        ImGui.SetItemTooltip(tooltip);
        updated = value;
        if (!changed) return false;
        if (!float.IsFinite(value) || (positive ? value <= 0 : value < 0))
        {
            _tuningError = $"{label.ToString()}: enter a finite {(positive ? "positive" : "nonnegative")} number. The previous value is retained.";
            return false;
        }
        _tuningError = "";
        return true;
    }
}
