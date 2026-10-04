using Microsoft.Extensions.Logging;
using System.Text.Json;
using VRCFaceTracking.Core.Params.Expressions;

namespace SteamLinkVRCFTModule.Overrides.Fwooffy;

public class EyebrowSettings {
	public readonly record struct Settings(bool Enabled, float Sensitivity);

	public const float MinimumSensitivity = 0.5f;
	public const float MaximumSensitivity = 3.0f;
	public static readonly Settings Default = new(false, 1.0f);

	private static string ConfigDirectory => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"QproFaceTracking", "config");
	private static string SettingsPath => Path.Combine(ConfigDirectory, "eyebrow-settings.json");

	private Settings _settings = Default;
	private long _nextCheckTick;
	private readonly ILogger _logger;

	public EyebrowSettings(ILogger logger) {
		_logger = logger;
	}

	public Settings Current => _settings;

	public void Refresh() {
		long now = Environment.TickCount64;
		if (now < _nextCheckTick) return;
		_nextCheckTick = now + 250;
		Settings selected = Load();
		if (_settings == selected) return;
		_settings = selected;
		_logger.LogInformation("Eyebrow adjustment: {Enabled}; sensitivity: {Sensitivity:F2}x",
			selected.Enabled, selected.Sensitivity);
	}

	public float Apply(float nativeWeight) {
		Refresh();
		return ApplyWeight(nativeWeight, _settings.Enabled, _settings.Sensitivity);
	}

	public bool TryApply(UnifiedExpressions expression, float nativeValue, out float result) {
		switch (expression) {
			case UnifiedExpressions.BrowPinchLeft:
			case UnifiedExpressions.BrowLowererLeft:
			case UnifiedExpressions.BrowPinchRight:
			case UnifiedExpressions.BrowLowererRight:
			case UnifiedExpressions.BrowInnerUpLeft:
			case UnifiedExpressions.BrowInnerUpRight:
			case UnifiedExpressions.BrowOuterUpLeft:
			case UnifiedExpressions.BrowOuterUpRight:
				result = Apply(nativeValue);
				return true;
			default:
				result = nativeValue;
				return false;
		}
	}

	public static Settings Load() {
		try {
			if (!File.Exists(SettingsPath)) return Default;
			using JsonDocument document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
			JsonElement root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object ||
				!root.TryGetProperty("enabled", out JsonElement enabled) ||
				enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
				!root.TryGetProperty("sensitivity", out JsonElement sensitivity) ||
				sensitivity.ValueKind != JsonValueKind.Number ||
				!sensitivity.TryGetSingle(out float gain) ||
				!float.IsFinite(gain) ||
				gain < MinimumSensitivity || gain > MaximumSensitivity)
				return Default;
			return new Settings(enabled.GetBoolean(), gain);
		}
		catch (IOException) { return Default; }
		catch (UnauthorizedAccessException) { return Default; }
		catch (JsonException) { return Default; }
	}

	public static float ApplyWeight(float nativeWeight, bool enabled, float sensitivity) {
		if (!enabled) return nativeWeight;
		if (!float.IsFinite(nativeWeight)) return 0.0f;
		if (!float.IsFinite(sensitivity)) sensitivity = 1.0f;
		return Math.Clamp(nativeWeight * sensitivity, 0.0f, 1.0f);
	}
}
