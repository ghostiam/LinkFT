using Microsoft.Extensions.Logging;
using VRCFaceTracking.Core.Params.Expressions;

namespace SteamLinkVRCFTModule.Overrides.Fwooffy;

public class QproEnhancedFT : IOverrider {
	private readonly CancellationTokenSource _cancellationTokenSource = new();

	private readonly EyebrowSettings _eyebrowSettings;
	private readonly GazeTracking _gazeTracking;
	private readonly PupilTracking _pupilTracking;
	private readonly TongueTracking _tongueTracking;
	private readonly CheekTelemetry _cheekTelemetry;
	private readonly SteamLabels _steamLabels;
	private readonly SmirkTracking _smirkTracking;

	private readonly float[] _expressions = new float[70];
	private long _lastSteamFaceTick;
	private long _lastSteamEyeTick;
	private long _lastSteamGazeTick;

	public QproEnhancedFT(ILogger logger) {
		_eyebrowSettings = new EyebrowSettings(logger);
		_gazeTracking = new GazeTracking(logger);
		_pupilTracking = new PupilTracking(logger);
		_tongueTracking = new TongueTracking(logger);
		_cheekTelemetry = new CheekTelemetry(logger);
		_steamLabels = new SteamLabels(logger);
		_smirkTracking = new SmirkTracking(logger);
	}

	public void Initialize() {
		var token = _cancellationTokenSource.Token;

		_steamLabels.Initialize();
		_gazeTracking.Initialize(token);
		_tongueTracking.Initialize(token);
		_pupilTracking.Initialize(token);
		_cheekTelemetry.Initialize();

		_eyebrowSettings.Refresh();
	}

	public void Teardown() {
		_cancellationTokenSource.Cancel();

		_steamLabels.Dispose();
		_gazeTracking.Dispose();
		_tongueTracking.Dispose();
		_pupilTracking.Dispose();
		_cheekTelemetry.Dispose();
	}

	public float Apply(EyeExpression expression, float nativeValue) {
		if (_gazeTracking.TryApply(expression, nativeValue, out float gazeVal))
			return gazeVal;
		if (_pupilTracking.TryApply(expression, nativeValue, out float pupilVal))
			return pupilVal;
		return nativeValue;
	}

	public float Apply(UnifiedExpressions expression, float nativeValue) {
		if (_tongueTracking.TryApply(expression, nativeValue, out float tongueVal))
			return tongueVal;
		if (_eyebrowSettings.TryApply(expression, nativeValue, out float eyebrowVal))
			return eyebrowVal;
		if (_smirkTracking.TryApply(expression, nativeValue, out float smirkVal))
			return smirkVal;

		// TODO: Implement cheek overrides (CheekPuffLeft, CheekPuffRight, CheekSuckLeft, CheekSuckRight) via CheekPuffTracker / calibration
		return nativeValue;
	}

	public void ProcessOscMessages(IReadOnlyList<OSCM> messages) {
		long tick = Environment.TickCount64;

		foreach (var oscMessage in messages) {
			if (oscMessage == null) continue;
			if (oscMessage.Values.Count < 1) continue;

			string address = oscMessage.Address;
			if (address.StartsWith(SteamLabels.FaceWeightPrefix, StringComparison.OrdinalIgnoreCase)) {
				string name = address[SteamLabels.FaceWeightPrefix.Length..];
				if (SteamLabels.ExpressionIndices.TryGetValue(name, out int index)) {
					float val = Convert.ToSingle(oscMessage.Values[0]);
					_expressions[index] = Math.Clamp(val, 0f, 1f);
					_lastSteamFaceTick = tick;
					if (name is "EyesClosedL" or "EyesClosedR" or "LidTightenerL" or "LidTightenerR")
						_lastSteamEyeTick = tick;
				}
			} else if (address == "/sl/eyeTrackedGazePoint") {
				_lastSteamGazeTick = tick;
			}
		}

		_smirkTracking.Update(_expressions, tick);
		_cheekTelemetry.Publish(_expressions, tick);
		_steamLabels.Publish(tick, _expressions, _lastSteamFaceTick, _lastSteamEyeTick, _lastSteamGazeTick);
	}
}
