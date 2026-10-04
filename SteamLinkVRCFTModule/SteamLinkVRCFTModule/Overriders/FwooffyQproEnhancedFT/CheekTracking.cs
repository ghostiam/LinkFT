using System.Text.Json;
using Microsoft.Extensions.Logging;
using VRCFaceTracking.Core.Params.Expressions;

namespace SteamLinkVRCFTModule.Overrides.Fwooffy;

internal readonly record struct CheekPuffWeights(float Left, float Right);
internal readonly record struct CheekSuckWeights(float Left, float Right);

internal enum CheekPuffMode { Off, Balanced, Strong, Calibrated }
internal enum CheekSuckMode { Off, Balanced, Strong }

internal readonly record struct CheekPuffCalibration(
	float LeftNeutral, float LeftFull, float RightNeutral, float RightFull,
	float LeftDeadZone = 0, float RightDeadZone = 0, CheekPuffRawResponse? RawResponse = null);

internal readonly record struct CheekPuffRawResponse(
	float LeftOnRight, float RightOnLeft, float BothLeft, float BothRight);

internal static class CheekPuffCalibrationProfile {
	internal const string VirtualDesktopSource = "virtual-desktop";
	internal const string SteamLinkSource = "steam-link";
	internal const float MinimumRange = 0.02f;
	internal const float RawDeadZoneFloor = 0.03f;

	private static string DefaultConfigDirectory => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"QproFaceTracking", "config");

	internal static bool IsValid(CheekPuffCalibration calibration) =>
		IsValidSide(calibration.LeftNeutral, calibration.LeftFull, calibration.LeftDeadZone) &&
		IsValidSide(calibration.RightNeutral, calibration.RightFull, calibration.RightDeadZone) &&
		(calibration.RawResponse is null || IsValidRawResponse(calibration));

	private static bool IsValidRawResponse(CheekPuffCalibration calibration) {
		CheekPuffRawResponse raw = calibration.RawResponse!.Value;
		if (!InRange(raw.LeftOnRight) || !InRange(raw.RightOnLeft) ||
		    !InRange(raw.BothLeft) || !InRange(raw.BothRight)) return false;
		float leftRange = calibration.LeftFull - calibration.LeftNeutral;
		float rightRange = calibration.RightFull - calibration.RightNeutral;
		float leftCross = raw.LeftOnRight - calibration.LeftNeutral;
		float rightCross = raw.RightOnLeft - calibration.RightNeutral;
		float determinant = leftRange * rightRange - leftCross * rightCross;
		if (determinant < leftRange * rightRange * 0.12f) return false;
		float bothLeft = raw.BothLeft - calibration.LeftNeutral;
		float bothRight = raw.BothRight - calibration.RightNeutral;
		float bothLeftStrength = (rightRange * bothLeft - leftCross * bothRight) / determinant;
		float bothRightStrength = (leftRange * bothRight - rightCross * bothLeft) / determinant;
		if (bothLeftStrength is not (>= 0.15f and <= 3.0f) || bothRightStrength is not (>= 0.15f and <= 3.0f)) return false;
		var noise = RawNoiseMargins(calibration);
		var sensitivity = RawNoiseMargins(calibration with { LeftDeadZone = 1, RightDeadZone = 1 });
		float maximumSensitivity = 1 / (MinimumRange * (1 - RawDeadZoneFloor)) + 0.0001f;
		return noise.Left <= 0.20f && noise.Right <= 0.20f &&
		       sensitivity.Left / (1 - MathF.Max(RawDeadZoneFloor, noise.Left)) <= maximumSensitivity &&
		       sensitivity.Right / (1 - MathF.Max(RawDeadZoneFloor, noise.Right)) <= maximumSensitivity;
	}

	private static bool InRange(float value) => float.IsFinite(value) && value is >= 0.0f and <= 1.0f;

	internal static (float Left, float Right) RawNoiseMargins(CheekPuffCalibration calibration) {
		CheekPuffRawResponse raw = calibration.RawResponse!.Value;
		float leftRange = calibration.LeftFull - calibration.LeftNeutral;
		float rightRange = calibration.RightFull - calibration.RightNeutral;
		float leftCross = raw.LeftOnRight - calibration.LeftNeutral;
		float rightCross = raw.RightOnLeft - calibration.RightNeutral;
		float determinant = leftRange * rightRange - leftCross * rightCross;
		float a = rightRange / determinant, b = -leftCross / determinant;
		float c = -rightCross / determinant, d = leftRange / determinant;
		float bothLeft = (rightRange * (raw.BothLeft - calibration.LeftNeutral) - leftCross * (raw.BothRight - calibration.RightNeutral)) / determinant;
		float bothRight = (leftRange * (raw.BothRight - calibration.RightNeutral) - rightCross * (raw.BothLeft - calibration.LeftNeutral)) / determinant;
		float Weighted(float left, float right) => MathF.Abs(left) * calibration.LeftDeadZone +
		                                          MathF.Abs(right) * calibration.RightDeadZone;
		float leftDominant = (1.0f - bothLeft) / bothRight;
		float rightDominant = (1.0f - bothRight) / bothLeft;
		float leftNoise = MathF.Max(Weighted(a, b), MathF.Max(
			Weighted(a + leftDominant * c, b + leftDominant * d), Weighted(a / bothLeft, b / bothLeft)));
		float rightNoise = MathF.Max(Weighted(c, d), MathF.Max(
			Weighted(c / bothRight, d / bothRight), Weighted(c + rightDominant * a, d + rightDominant * b)));
		return (leftNoise, rightNoise);
	}

	internal static (float Left, float Right) CorrectRawStrength(CheekPuffCalibration calibration, float left, float right) {
		CheekPuffRawResponse raw = calibration.RawResponse!.Value;
		float leftRange = calibration.LeftFull - calibration.LeftNeutral;
		float rightRange = calibration.RightFull - calibration.RightNeutral;
		float leftCross = raw.LeftOnRight - calibration.LeftNeutral;
		float rightCross = raw.RightOnLeft - calibration.RightNeutral;
		float determinant = leftRange * rightRange - leftCross * rightCross;
		(float Left, float Right) Unmix(float leftValue, float rightValue) =>
			((rightRange * leftValue - leftCross * rightValue) / determinant,
			 (leftRange * rightValue - rightCross * leftValue) / determinant);
		var strength = Unmix(left - calibration.LeftNeutral, right - calibration.RightNeutral);
		var both = Unmix(raw.BothLeft - calibration.LeftNeutral, raw.BothRight - calibration.RightNeutral);
		float shared = MathF.Max(0.0f, MathF.Min(strength.Left / both.Left, strength.Right / both.Right));
		return (strength.Left + shared * (1.0f - both.Left), strength.Right + shared * (1.0f - both.Right));
	}

	internal static CheekPuffCalibration? Load(string source, string? configDirectory = null) {
		string path = ProfilePath(source, configDirectory);
		try {
			if (!File.Exists(path)) return null;
			using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
			JsonElement root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object ||
			    !root.TryGetProperty("version", out JsonElement version) ||
			    version.ValueKind != JsonValueKind.Number ||
			    !version.TryGetInt32(out int versionNumber) || versionNumber is not (1 or 2 or 3) ||
			    !TryReadAnchor(root, "leftNeutral", out float leftNeutral) ||
			    !TryReadAnchor(root, "leftFull", out float leftFull) ||
			    !TryReadAnchor(root, "rightNeutral", out float rightNeutral) ||
			    !TryReadAnchor(root, "rightFull", out float rightFull))
				return null;

			float leftDeadZone = 0, rightDeadZone = 0;
			if (versionNumber >= 2 &&
			    (!TryReadAnchor(root, "leftDeadZone", out leftDeadZone) ||
			     !TryReadAnchor(root, "rightDeadZone", out rightDeadZone)))
				return null;
			CheekPuffRawResponse? rawResponse = null;
			if (versionNumber == 3) {
				if (!root.TryGetProperty("rawResponse", out JsonElement raw) || raw.ValueKind != JsonValueKind.Object ||
				    !TryReadAnchor(raw, "leftOnRight", out float leftOnRight) ||
				    !TryReadAnchor(raw, "rightOnLeft", out float rightOnLeft) ||
				    !TryReadAnchor(raw, "bothLeft", out float bothLeft) ||
				    !TryReadAnchor(raw, "bothRight", out float bothRight)) return null;
				rawResponse = new(leftOnRight, rightOnLeft, bothLeft, bothRight);
			}
			CheekPuffCalibration calibration = new(
				leftNeutral, leftFull, rightNeutral, rightFull, leftDeadZone, rightDeadZone, rawResponse);
			return IsValid(calibration) ? calibration : null;
		} catch (IOException) { return null; }
		catch (UnauthorizedAccessException) { return null; }
		catch (JsonException) { return null; }
	}

	internal static string ProfilePath(string source, string? configDirectory = null) {
		if (source is not (VirtualDesktopSource or SteamLinkSource))
			throw new ArgumentException("Unknown face tracking source", nameof(source));
		return Path.Combine(configDirectory ?? DefaultConfigDirectory,
			$"cheek-puff-calibration-{source}.json");
	}

	private static bool IsValidSide(float neutral, float full, float deadZone) =>
		float.IsFinite(neutral) && float.IsFinite(full) && float.IsFinite(deadZone) &&
		neutral >= 0.0f && full <= 1.0f && full - neutral >= MinimumRange &&
		deadZone >= 0 && deadZone <= (full - neutral) * 0.25f;

	private static bool TryReadAnchor(JsonElement root, string name, out float value) {
		value = 0.0f;
		return root.TryGetProperty(name, out JsonElement element) &&
		       element.ValueKind == JsonValueKind.Number &&
		       element.TryGetSingle(out value) && float.IsFinite(value);
	}
}

internal static class DeveloperCheekPuffBaseline {
	private static readonly CheekPuffCalibration VirtualDesktop = new(
		LeftNeutral: 0.0f, LeftFull: 0.17802724f,
		RightNeutral: 0.0f, RightFull: 0.31023937f);
	private static readonly CheekPuffCalibration SteamLink = new(
		LeftNeutral: 0.0f, LeftFull: 0.33188242f,
		RightNeutral: 0.0f, RightFull: 0.31035322f);

	internal static CheekPuffCalibration? ForSource(string source) => source switch {
		CheekPuffCalibrationProfile.VirtualDesktopSource => VirtualDesktop,
		CheekPuffCalibrationProfile.SteamLinkSource => SteamLink,
		_ => null,
	};
}

internal static class CheekPuffMapping {
	internal static CheekPuffWeights RawFromFaceWeights(ReadOnlySpan<float> weights) {
		if (weights.Length < 4)
			throw new ArgumentException("Face weights must contain both cheek puff expressions", nameof(weights));

		return new CheekPuffWeights(
			float.IsFinite(weights[2]) ? Math.Clamp(weights[2], 0.0f, 1.0f) : 0.0f,
			float.IsFinite(weights[3]) ? Math.Clamp(weights[3], 0.0f, 1.0f) : 0.0f);
	}

	internal static CheekPuffWeights FromFaceWeights(ReadOnlySpan<float> weights, bool strong = false) {
		CheekPuffWeights raw = RawFromFaceWeights(weights);
		float left = raw.Left;
		float right = raw.Right;
		float peak = MathF.Max(left, right);
		if (peak < 0.02f)
			return new CheekPuffWeights(0.0f, 0.0f);

        // Quest Pro can raise both raw cheek weights during a one-sided puff.
        // A genuine two-sided puff is nearly symmetric, while a one-sided puff
        // leaves a small but repeatable lead on the active side. Preserve the
        // shared weight only when the two source weights agree closely.
		float asymmetry = MathF.Abs(left - right) / peak;
        if (strong && peak >= 0.08f)
        {
            // Require a meaningful side lead so a slightly uneven two-cheek
            // puff does not snap to one side. In a clear one-sided pose the
            // selected cheek gets the full value requested by the user.
			if (asymmetry >= 0.24f && MathF.Abs(left - right) >= 0.025f)
				return left > right
					? new CheekPuffWeights(1.0f, 0.0f)
					: new CheekPuffWeights(0.0f, 1.0f);
			if (asymmetry <= 0.12f && MathF.Min(left, right) >= 0.08f)
				return new CheekPuffWeights(1.0f, 1.0f);
		}
		float unilateralBlend = SmoothStep(0.12f, 0.28f, asymmetry);
		float bilateral = MathF.Min(left, right) * (1.0f - unilateralBlend);
		float leftOnly = MathF.Max(0.0f, (left - right - 0.015f) * 4.0f);
		float rightOnly = MathF.Max(0.0f, (right - left - 0.015f) * 4.0f);
		CheekPuffWeights balanced = new(
			Math.Clamp(MathF.Max(bilateral, leftOnly), 0.0f, 1.0f),
			Math.Clamp(MathF.Max(bilateral, rightOnly), 0.0f, 1.0f));
		if (!strong) return balanced;
        // Keep uncertain transitions continuous instead of flipping between
        // left, right, and both when the source weights are nearly tied.
		return new CheekPuffWeights(
			Math.Clamp((balanced.Left - 0.03f) * 7.0f, 0.0f, 1.0f),
			Math.Clamp((balanced.Right - 0.03f) * 7.0f, 0.0f, 1.0f));
	}

    internal static CheekPuffWeights CalibratedFromFaceWeights(
        ReadOnlySpan<float> weights, CheekPuffCalibration? calibration) {
        // Existing profiles contain Balanced anchors. New personal profiles
        // contain native paired anchors and their measured cross-talk.
		if (calibration is not { } valid || !CheekPuffCalibrationProfile.IsValid(valid))
			return FromFaceWeights(weights);
		if (valid.RawResponse.HasValue)
			return CalibratedRaw(RawFromFaceWeights(weights), valid);
		CheekPuffWeights balanced = FromFaceWeights(weights);
		return new CheekPuffWeights(
			Normalize(balanced.Left, valid.LeftNeutral, valid.LeftFull, valid.LeftDeadZone),
			Normalize(balanced.Right, valid.RightNeutral, valid.RightFull, valid.RightDeadZone));
	}

    private static CheekPuffWeights CalibratedRaw(CheekPuffWeights raw, CheekPuffCalibration calibration) {
        // The full two-cheek pose can saturate the native channels. Its own
        // measured anchor keeps both cheeks at full strength even when the
        // sum of the two individual responses would exceed the input range.
		var strength = CheekPuffCalibrationProfile.CorrectRawStrength(calibration, raw.Left, raw.Right);
		var noise = CheekPuffCalibrationProfile.RawNoiseMargins(calibration);
		return new(Normalize(strength.Left, 0, 1, MathF.Max(CheekPuffCalibrationProfile.RawDeadZoneFloor, noise.Left)),
			Normalize(strength.Right, 0, 1, MathF.Max(CheekPuffCalibrationProfile.RawDeadZoneFloor, noise.Right)));
	}

	private static float Normalize(float value, float neutral, float full, float deadZone) =>
		float.IsFinite(value)
			? Math.Clamp((value - neutral - deadZone) / (full - neutral - deadZone), 0.0f, 1.0f)
			: 0.0f;

	private static float SmoothStep(float low, float high, float value) {
		float t = Math.Clamp((value - low) / (high - low), 0.0f, 1.0f);
		return t * t * (3.0f - 2.0f * t);
	}
}

// Strong mode needs the previous pose: raw left/right weights often become
// nearly equal for a moment while a one-sided puff changes sides.
internal sealed class CheekPuffTracker {
	private const float NeutralPeak = 0.02f;
	private const float FullStrengthPeak = 0.08f;
	private const long SideConfirmationMs = 60;
	private const long BothFromNeutralMs = 40;
	private const long BothFromSideMs = 220;
	private const float CalibratedResponseMs = 100.0f;
	private const long CalibratedResetGapMs = 500;

	private enum Pose { Neutral, Left, Right, Both }

	private Pose _pose;
	private Pose _candidate;
	private long _candidateSinceMs;
	private CheekPuffMode _lastMode;
	private bool _calibratedInitialized;
	private long _lastCalibratedTickMs;
	private CheekPuffCalibration? _lastCalibration;
	private CheekPuffWeights _calibratedOutput;

	internal CheekPuffWeights Update(ReadOnlySpan<float> weights, bool strong, long nowMs) =>
		Update(weights, strong ? CheekPuffMode.Strong : CheekPuffMode.Balanced, nowMs);

	internal CheekPuffWeights Update(ReadOnlySpan<float> weights, CheekPuffMode mode,
		long nowMs, CheekPuffCalibration? calibration = null) {
		if (_lastMode != mode) {
			Reset();
			_lastMode = mode;
		}
		if (mode == CheekPuffMode.Off) {
			Reset();
			return CheekPuffMapping.RawFromFaceWeights(weights);
		}
		if (mode == CheekPuffMode.Balanced) {
			Reset();
			return CheekPuffMapping.FromFaceWeights(weights);
		}
		if (mode == CheekPuffMode.Calibrated) {
			if (calibration is not { } valid || !CheekPuffCalibrationProfile.IsValid(valid)) {
				Reset();
				return CheekPuffMapping.FromFaceWeights(weights);
			}
			long elapsedMs = nowMs - _lastCalibratedTickMs;
			bool restart = !_calibratedInitialized || _lastCalibration != valid ||
			               elapsedMs < 0 || elapsedMs >= CalibratedResetGapMs;
			if (restart) { _pose = Pose.Neutral; ClearCandidate(); }
			CheekPuffWeights target = StabilizeCalibratedPose(weights,
				CheekPuffMapping.CalibratedFromFaceWeights(weights, valid), valid.RawResponse.HasValue, nowMs);
			if (restart) {
				_calibratedOutput = target;
				_calibratedInitialized = true;
				_lastCalibration = valid;
			} else {
				// Filtering after normalization limits amplified sensor noise.
				// Elapsed time keeps the response consistent across frame rates.
				float blend = 1.0f - MathF.Exp(-elapsedMs / CalibratedResponseMs);
				_calibratedOutput = new CheekPuffWeights(
					_calibratedOutput.Left + (target.Left - _calibratedOutput.Left) * blend,
					_calibratedOutput.Right + (target.Right - _calibratedOutput.Right) * blend);
			}
			// A confirmed individual pose must not retain a smoothing tail
			// in the opposite cheek when switching sides.
			if (_pose == Pose.Left) _calibratedOutput = _calibratedOutput with { Right = 0 };
			else if (_pose == Pose.Right) _calibratedOutput = _calibratedOutput with { Left = 0 };
			_lastCalibratedTickMs = nowMs;
			return _calibratedOutput;
		}
		if (weights.Length < 4)
			throw new ArgumentException("Face weights must contain both cheek puff expressions", nameof(weights));

		float left = float.IsFinite(weights[2]) ? Math.Clamp(weights[2], 0.0f, 1.0f) : 0.0f;
		float right = float.IsFinite(weights[3]) ? Math.Clamp(weights[3], 0.0f, 1.0f) : 0.0f;
		float peak = MathF.Max(left, right);
		if (peak < NeutralPeak) {
			Reset();
			return new CheekPuffWeights(0.0f, 0.0f);
		}

		Pose observed = Observe(left, right, peak);
		if (_pose == Pose.Neutral) {
			if (observed is Pose.Left or Pose.Right) {
				float difference = MathF.Abs(left - right);
				bool clearSide = difference / peak >= 0.24f && difference >= 0.025f;
				if (clearSide || Confirm(observed, nowMs, BothFromNeutralMs))
					Select(observed);
			} else if (observed == Pose.Both && Confirm(Pose.Both, nowMs, BothFromNeutralMs))
				Select(Pose.Both);
			else if (observed != Pose.Both)
				ClearCandidate();
		} else if (_pose == Pose.Both) {
			if (observed is Pose.Left or Pose.Right) {
				if (Confirm(observed, nowMs, SideConfirmationMs))
					Select(observed);
			} else
				ClearCandidate();
		} else {
			Pose opposite = _pose == Pose.Left ? Pose.Right : Pose.Left;
			if (observed == opposite) {
				if (Confirm(opposite, nowMs, SideConfirmationMs))
					Select(opposite);
			} else if (observed == Pose.Both) {
				if (Confirm(Pose.Both, nowMs, BothFromSideMs))
					Select(Pose.Both);
			} else
				ClearCandidate();
		}

		// Let the active cheek release smoothly at very low source weights,
		// while the other cheek remains exactly zero throughout a side switch.
		float strength = Math.Clamp((peak - NeutralPeak) / (FullStrengthPeak - NeutralPeak), 0.0f, 1.0f);
		return _pose switch {
			Pose.Left => new CheekPuffWeights(strength, 0.0f),
			Pose.Right => new CheekPuffWeights(0.0f, strength),
			Pose.Both => new CheekPuffWeights(strength, strength),
			_ => new CheekPuffWeights(0.0f, 0.0f)
		};
	}

	internal void Reset() {
		_pose = Pose.Neutral;
		ClearCandidate();
		_calibratedInitialized = false;
		_lastCalibration = null;
	}

	private CheekPuffWeights StabilizeCalibratedPose(ReadOnlySpan<float> weights,
		CheekPuffWeights target, bool rawProfile, long nowMs) {
		if (MathF.Max(target.Left, target.Right) <= 0.04f) {
			_pose = Pose.Neutral;
			ClearCandidate();
			return target;
		}
		// New profiles already remove measured cross-talk. Old profiles and
		// the developer baselines still use the original Balanced strengths;
		// their raw side lead is needed during a brief symmetric crossover.
		CheekPuffWeights signal = rawProfile ? target : CheekPuffMapping.RawFromFaceWeights(weights);
		float peak = MathF.Max(signal.Left, signal.Right);
		Pose observed = rawProfile ? ObserveCalibrated(target)
			: Observe(signal.Left, signal.Right, peak, NeutralPeak);
		bool clearSide = peak > 0 && MathF.Abs(signal.Left - signal.Right) / peak >= 0.24f &&
		                 MathF.Abs(signal.Left - signal.Right) >= 0.025f;
		if (_pose == Pose.Neutral) {
			if (observed is Pose.Left or Pose.Right) {
				if (clearSide || Confirm(observed, nowMs, BothFromNeutralMs)) Select(observed);
			} else if (observed == Pose.Both) Select(Pose.Both);
			else ClearCandidate();
		} else if (_pose == Pose.Both) {
			if (observed is Pose.Left or Pose.Right) {
				if (Confirm(observed, nowMs, SideConfirmationMs)) Select(observed);
			} else ClearCandidate();
		} else {
			Pose opposite = _pose == Pose.Left ? Pose.Right : Pose.Left;
			if (observed == opposite) {
				if (Confirm(opposite, nowMs, SideConfirmationMs)) Select(opposite);
			} else if (observed == Pose.Both) {
				if (Confirm(Pose.Both, nowMs, BothFromSideMs)) Select(Pose.Both);
			} else ClearCandidate();
		}
		return _pose switch {
			Pose.Left => target with { Right = 0 },
			Pose.Right => target with { Left = 0 },
			_ => target,
		};
	}

	private Pose Observe(float left, float right, float peak, float minimumPeak = FullStrengthPeak) {
		if (peak < minimumPeak) return Pose.Neutral;
		float difference = MathF.Abs(left - right);
		float asymmetry = difference / peak;
		// Once one side is selected, a steady lead by the other side is
		// enough to switch. The Quest Pro's two raw weights can both stay high
		// during a real side change, so the initial 0.24 threshold is too
		// strict here. Confirmation time filters out brief bilateral noise.
		if (_pose == Pose.Left && right - left >= 0.06f && asymmetry >= 0.09f)
			return Pose.Right;
		if (_pose == Pose.Right && left - right >= 0.06f && asymmetry >= 0.09f)
			return Pose.Left;
		if (asymmetry >= 0.24f && difference >= 0.025f)
			return left > right ? Pose.Left : Pose.Right;
		// The first recorded one-cheek pose can have a smaller lead than a
		// settled pose. Confirm it briefly before activating either side.
		if (_pose == Pose.Neutral && asymmetry >= 0.14f && difference >= 0.05f)
			return left > right ? Pose.Left : Pose.Right;
		if (asymmetry <= 0.12f && MathF.Min(left, right) >= minimumPeak)
			return Pose.Both;
		return Pose.Neutral;
	}

	private Pose ObserveCalibrated(CheekPuffWeights strength) {
		// The personal fit has already removed measured cross-talk. Both
		// corrected axes can be active at unequal strengths; raw asymmetry
		// would otherwise suppress the weaker cheek forever.
		float activation = _pose == Pose.Both ? 0.025f : 0.04f;
		bool left = strength.Left > activation, right = strength.Right > activation;
		if (left && right) return Pose.Both;
		if (left) return Pose.Left;
		if (right) return Pose.Right;
		return Pose.Neutral;
	}

	private bool Confirm(Pose candidate, long nowMs, long durationMs) {
		if (_candidate != candidate || nowMs < _candidateSinceMs) {
			_candidate = candidate;
			_candidateSinceMs = nowMs;
			return false;
		}
		return nowMs - _candidateSinceMs >= durationMs;
	}

	private void Select(Pose pose) {
		_pose = pose;
		ClearCandidate();
	}

	private void ClearCandidate() {
		_candidate = Pose.Neutral;
		_candidateSinceMs = 0;
	}
}

internal static class CheekSuckMapping {
	// XR_FB exposes cheek suck as two independent, nonnegative expressions.
	// Virtual Desktop and Steam Link both put them at indices 6 and 7.
	internal static CheekSuckWeights RawFromFaceWeights(ReadOnlySpan<float> weights) {
		Validate(weights);
		return new CheekSuckWeights(Clean(weights[6]), Clean(weights[7]));
	}

	internal static CheekSuckWeights FromFaceWeights(ReadOnlySpan<float> weights, bool strong = false) {
		Validate(weights);
		Span<float> cheekPair = stackalloc float[4];
		cheekPair[2] = Clean(weights[6]);
		cheekPair[3] = Clean(weights[7]);
		CheekPuffWeights separated = CheekPuffMapping.FromFaceWeights(cheekPair, strong);
		return new CheekSuckWeights(separated.Left, separated.Right);
	}

	internal static float Clean(float value) => float.IsFinite(value) ? Math.Clamp(value, 0.0f, 1.0f) : 0.0f;

	internal static void Validate(ReadOnlySpan<float> weights) {
		if (weights.Length < 8)
			throw new ArgumentException("Face weights must contain both cheek suck expressions", nameof(weights));
	}
}

// Use the same side confirmation and bilateral dwell as individual cheek puff.
// Each tracker owns its own state, so suck and puff cannot change each other's
// active side. Only the dedicated cheek suck channels feed this tracker.
internal sealed class CheekSuckTracker {
	private readonly CheekPuffTracker _sideTracker = new();

	internal CheekSuckWeights Update(ReadOnlySpan<float> weights, CheekSuckMode mode, long nowMs) {
		CheekSuckMapping.Validate(weights);
		Span<float> cheekPair = stackalloc float[4];
		cheekPair[2] = CheekSuckMapping.Clean(weights[6]);
		cheekPair[3] = CheekSuckMapping.Clean(weights[7]);
		CheekPuffMode sideMode = mode switch {
			CheekSuckMode.Off => CheekPuffMode.Off,
			CheekSuckMode.Balanced => CheekPuffMode.Balanced,
			CheekSuckMode.Strong => CheekPuffMode.Strong,
			_ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown cheek suck mode")
		};
		CheekPuffWeights separated = _sideTracker.Update(cheekPair, sideMode, nowMs);
		return new CheekSuckWeights(separated.Left, separated.Right);
	}

	internal void Reset() => _sideTracker.Reset();
}

internal class CheekTracking {
	private static readonly string ConfigDirectory = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"QproFaceTracking", "config");
	private static readonly string CheekPuffModePath = Path.Combine(ConfigDirectory, "cheek-puff-mode.txt");
	private static readonly string CheekSuckModePath = Path.Combine(ConfigDirectory, "cheek-suck-mode.txt");

	private readonly ILogger _logger;
	private readonly CheekPuffTracker _cheekPuffTracker = new();
	private readonly CheekSuckTracker _cheekSuckTracker = new();

	private CheekPuffMode _cheekPuffMode = CheekPuffMode.Strong;
	private CheekPuffCalibration? _cheekPuffCalibration;
	private string? _cheekPuffCalibrationOrigin;
	private long _nextCheekPuffModeCheckTick;

	private CheekSuckMode _cheekSuckMode = CheekSuckMode.Strong;
	private long _nextCheekSuckModeCheckTick;

	private CheekPuffWeights _currentPuff;
	private CheekSuckWeights _currentSuck;
	private bool _hasUpdate;

	internal CheekTracking(ILogger logger) {
		_logger = logger;
	}

	internal CheekPuffWeights CurrentPuff => _currentPuff;
	internal CheekSuckWeights CurrentSuck => _currentSuck;

	internal void Refresh() {
		RefreshCheekPuffMode();
		RefreshCheekSuckMode();
	}

	internal void Update(ReadOnlySpan<float> expressions, long tick) {
		if (expressions.Length < 8) return;

		RefreshCheekPuffMode();
		_currentPuff = _cheekPuffTracker.Update(expressions, _cheekPuffMode, tick, _cheekPuffCalibration);

		RefreshCheekSuckMode();
		_currentSuck = _cheekSuckTracker.Update(expressions, _cheekSuckMode, tick);

		_hasUpdate = true;
	}

	internal bool TryApply(UnifiedExpressions expression, float nativeValue, out float result) {
		switch (expression) {
			case UnifiedExpressions.CheekPuffLeft:
				result = _hasUpdate ? _currentPuff.Left : nativeValue;
				return true;
			case UnifiedExpressions.CheekPuffRight:
				result = _hasUpdate ? _currentPuff.Right : nativeValue;
				return true;
			case UnifiedExpressions.CheekSuckLeft:
				result = _hasUpdate ? _currentSuck.Left : nativeValue;
				return true;
			case UnifiedExpressions.CheekSuckRight:
				result = _hasUpdate ? _currentSuck.Right : nativeValue;
				return true;
			default:
				result = nativeValue;
				return false;
		}
	}

	internal void Reset() {
		_cheekPuffTracker.Reset();
		_cheekSuckTracker.Reset();
		_currentPuff = default;
		_currentSuck = default;
		_hasUpdate = false;
	}

	private void RefreshCheekPuffMode() {
		long now = Environment.TickCount64;
		if (now < _nextCheekPuffModeCheckTick) return;
		_nextCheekPuffModeCheckTick = now + 250;

		CheekPuffMode selected;
		try {
			string saved = File.Exists(CheekPuffModePath)
				? File.ReadAllText(CheekPuffModePath).Trim()
				: "strong";
			selected = saved.ToLowerInvariant() switch {
				"off" => CheekPuffMode.Off,
				"balanced" => CheekPuffMode.Balanced,
				"calibrated" => CheekPuffMode.Calibrated,
				_ => CheekPuffMode.Strong
			};
		} catch (IOException) { return; }
		catch (UnauthorizedAccessException) { return; }

		string source = CheekPuffCalibrationProfile.SteamLinkSource;
		CheekPuffCalibration? calibration = selected == CheekPuffMode.Calibrated
			? CheekPuffCalibrationProfile.Load(source) : null;
		string? origin = calibration.HasValue ? "personal profile" : null;
		if (selected == CheekPuffMode.Calibrated && !calibration.HasValue) {
			calibration = DeveloperCheekPuffBaseline.ForSource(source);
			origin = calibration.HasValue ? "developer baseline" : "balanced fallback; no profile";
		}

		if (_cheekPuffMode == selected && _cheekPuffCalibration == calibration &&
		    _cheekPuffCalibrationOrigin == origin) return;

		_cheekPuffMode = selected;
		_cheekPuffCalibration = calibration;
		_cheekPuffCalibrationOrigin = origin;
		_cheekPuffTracker.Reset();
		_logger.LogInformation("Cheek puff style: {Style}", selected switch {
			CheekPuffMode.Off => "native passthrough",
			CheekPuffMode.Balanced => "balanced",
			CheekPuffMode.Calibrated => $"calibrated ({origin})",
			_ => "1/0 individual"
		});
	}

	private void RefreshCheekSuckMode() {
		long now = Environment.TickCount64;
		if (now < _nextCheekSuckModeCheckTick) return;
		_nextCheekSuckModeCheckTick = now + 250;

		CheekSuckMode selected;
		try {
			string saved = File.Exists(CheekSuckModePath)
				? File.ReadAllText(CheekSuckModePath).Trim()
				: "strong";
			selected = saved.ToLowerInvariant() switch {
				"off" => CheekSuckMode.Off,
				"balanced" => CheekSuckMode.Balanced,
				_ => CheekSuckMode.Strong
			};
		} catch (IOException) { return; }
		catch (UnauthorizedAccessException) { return; }

		if (_cheekSuckMode == selected) return;
		_cheekSuckMode = selected;
		_cheekSuckTracker.Reset();
		_logger.LogInformation("Cheek suck style: {Style}", selected switch {
			CheekSuckMode.Off => "native passthrough",
			CheekSuckMode.Balanced => "balanced",
			_ => "strong individual"
		});
	}
}
