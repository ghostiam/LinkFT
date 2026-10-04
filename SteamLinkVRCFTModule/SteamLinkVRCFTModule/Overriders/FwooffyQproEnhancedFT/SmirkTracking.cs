using Microsoft.Extensions.Logging;
using VRCFaceTracking.Core.Params.Expressions;

namespace SteamLinkVRCFTModule.Overrides.Fwooffy;

public readonly record struct SmirkWeights(float Left, float Right);
public enum SmirkSide { None, Left, Right }
public readonly record struct SmirkDecision(SmirkWeights Weights, SmirkSide Side);

public class SmirkTracking {
	// XR_FB_face_tracking blendshape indices, shared by the Virtual Desktop
	// memory map and Steam Link OSC decoder.
	private const int DimplerLeft = 10;
	private const int DimplerRight = 11;
	private const int LipCornerPullerLeft = 32;
	private const int LipCornerPullerRight = 33;
	private const float MinimumSmirkPeak = 0.07f;
	private const float MinimumSideDifference = 0.025f;

	private readonly ILogger _logger;
	private SmirkWeights _current;
	private long _lastUpdateMs;
	private bool _hasUpdate;
	private SmirkSide _selected;
	private SmirkSide _candidate;
	private long _candidateSinceMs;

	public SmirkTracking(ILogger logger) {
		_logger = logger;
	}

	public SmirkWeights Current => _current;

	public void Update(ReadOnlySpan<float> weights, long nowMs) {
		if (weights.Length <= LipCornerPullerRight) return;

		SmirkDecision decision = Decide(weights);
		if (_selected == SmirkSide.None && decision.Side != SmirkSide.None) {
			_selected = decision.Side;
			_candidate = SmirkSide.None;
		} else if (decision.Side == _selected) {
			_candidate = SmirkSide.None;
		} else if (_candidate != decision.Side) {
			_candidate = decision.Side;
			_candidateSinceMs = nowMs;
		} else if (nowMs - _candidateSinceMs >= 60) {
			_selected = decision.Side;
			_candidate = SmirkSide.None;
		}

		SmirkWeights target = _selected == decision.Side
			? decision.Weights
			: _current;
		if (!_hasUpdate || nowMs < _lastUpdateMs) {
			_current = target;
			_hasUpdate = true;
			_lastUpdateMs = nowMs;
			return;
		}

		long elapsedMs = Math.Clamp(nowMs - _lastUpdateMs, 0, 250);
		_lastUpdateMs = nowMs;
		float alpha = 1.0f - MathF.Exp(-elapsedMs / 65.0f);
		_current = new SmirkWeights(
			_current.Left + (target.Left - _current.Left) * alpha,
			_current.Right + (target.Right - _current.Right) * alpha);
		if (_selected == SmirkSide.Left)
			_current = _current with { Right = 0.0f };
		else if (_selected == SmirkSide.Right)
			_current = _current with { Left = 0.0f };
	}

	public bool TryApply(UnifiedExpressions expression, float nativeValue, out float result) {
		switch (expression) {
			case UnifiedExpressions.MouthCornerPullLeft:
			case UnifiedExpressions.MouthCornerSlantLeft:
				result = _hasUpdate ? _current.Left : nativeValue;
				return true;
			case UnifiedExpressions.MouthCornerPullRight:
			case UnifiedExpressions.MouthCornerSlantRight:
				result = _hasUpdate ? _current.Right : nativeValue;
				return true;
			default:
				result = nativeValue;
				return false;
		}
	}

	public void Reset() {
		_current = default;
		_lastUpdateMs = 0;
		_hasUpdate = false;
		_selected = SmirkSide.None;
		_candidate = SmirkSide.None;
		_candidateSinceMs = 0;
	}

	public static SmirkDecision Decide(ReadOnlySpan<float> weights) {
		if (weights.Length <= LipCornerPullerRight)
			throw new ArgumentException("Face weights must contain both lip corner pullers", nameof(weights));

		float left = FiniteWeight(weights[LipCornerPullerLeft]);
		float right = FiniteWeight(weights[LipCornerPullerRight]);
		float peak = MathF.Max(left, right);
		float difference = left - right;
		float separation = MathF.Abs(difference);
		SmirkDecision native = new(new SmirkWeights(left, right), SmirkSide.None);
		if (peak < MinimumSmirkPeak || separation < MinimumSideDifference || separation / peak < 0.22f)
			return native;

        // The Quest Pro reports a weak one-sided smile with some movement on
        // the relaxed side. The dimple pair provides a second, independent
        // indication of which corner is raised. Require a clear lead or dimple
        // agreement before locking the relaxed side to zero. Even smiles keep
        // their native two-sided values.
		float dimpleDifference = FiniteWeight(weights[DimplerLeft]) - FiniteWeight(weights[DimplerRight]);
		float matchingDimple = MathF.Sign(difference) == MathF.Sign(dimpleDifference)
			? MathF.Abs(dimpleDifference) : 0.0f;
		if (separation < 0.05f && matchingDimple < 0.015f && separation / peak < 0.30f)
			return native;

        // Live Quest Pro samples put a held smirk near 0.10-0.12 while a full
        // smile reached about 0.75. Lift only the weak smirk range; leave
        // strong expressions at their measured values.
		float leading = MathF.Max(peak, MathF.Min(0.52f, peak * 3.5f));
		float boost = 1.0f - SmoothStep(0.24f, 0.43f, peak);
		float boosted = peak + (leading - peak) * boost;
		return difference > 0.0f
			? new SmirkDecision(new SmirkWeights(boosted, 0.0f), SmirkSide.Left)
			: new SmirkDecision(new SmirkWeights(0.0f, boosted), SmirkSide.Right);
	}

	private static float FiniteWeight(float value) =>
		float.IsFinite(value) ? Math.Clamp(value, 0.0f, 1.0f) : 0.0f;

	private static float SmoothStep(float low, float high, float value) {
		float t = Math.Clamp((value - low) / (high - low), 0.0f, 1.0f);
		return t * t * (3.0f - 2.0f * t);
	}
}
