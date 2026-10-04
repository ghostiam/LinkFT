using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace SteamLinkVRCFTModule.Overrides.Fwooffy;

public class SteamLabels : IDisposable {
	public const int LabelPort = 27274;
	public const long SteamFaceTimeoutMs = 250;
	public const string FaceWeightPrefix = "/sl/xrfb/facew/";

	public static IReadOnlyList<string> ExpressionNames { get; } = Array.AsReadOnly(new[] {
		"BrowLowererL", "BrowLowererR", "CheekPuffL", "CheekPuffR",
		"CheekRaiserL", "CheekRaiserR", "CheekSuckL", "CheekSuckR",
		"ChinRaiserB", "ChinRaiserT", "DimplerL", "DimplerR",
		"EyesClosedL", "EyesClosedR", "EyesLookDownL", "EyesLookDownR",
		"EyesLookLeftL", "EyesLookLeftR", "EyesLookRightL", "EyesLookRightR",
		"EyesLookUpL", "EyesLookUpR", "InnerBrowRaiserL", "InnerBrowRaiserR",
		"JawDrop", "JawSidewaysLeft", "JawSidewaysRight", "JawThrust",
		"LidTightenerL", "LidTightenerR", "LipCornerDepressorL",
		"LipCornerDepressorR", "LipCornerPullerL", "LipCornerPullerR",
		"LipFunnelerLb", "LipFunnelerLt", "LipFunnelerRb", "LipFunnelerRt",
		"LipPressorL", "LipPressorR", "LipPuckerL", "LipPuckerR",
		"LipStretcherL", "LipStretcherR", "LipSuckLb", "LipSuckLt",
		"LipSuckRb", "LipSuckRt", "LipTightenerL", "LipTightenerR",
		"LipsToward", "LowerLipDepressorL", "LowerLipDepressorR",
		"MouthLeft", "MouthRight", "NoseWrinklerL", "NoseWrinklerR",
		"OuterBrowRaiserL", "OuterBrowRaiserR", "UpperLidRaiserL",
		"UpperLidRaiserR", "UpperLipRaiserL", "UpperLipRaiserR",
		"TongueTipInterdental", "TongueTipAlveolar", "TongueFrontDorsalPalate",
		"TongueMidDorsalPalate", "TongueBackDorsalVelar", "TongueOut",
		"TongueRetreat"
	});

	public static readonly Dictionary<string, int> ExpressionIndices = BuildExpressionIndices();

	private static readonly JsonSerializerOptions JsonOptions = new() {
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase
	};

	private readonly ILogger _logger;
	private UdpClient? _steamLabelSocket;
	private long _steamLabelSequence;
	private long _steamSourceChangeSequence;
	private long _lastPublishedSteamFaceTick;
	private long _nextSteamSchemaTick;
	private long _nextSteamLabelTick;

	public SteamLabels(ILogger logger) {
		_logger = logger;
	}

	public void Initialize() {
		try {
			_steamLabelSocket = new UdpClient(AddressFamily.InterNetwork);
			_steamLabelSocket.Connect(IPAddress.Loopback, LabelPort);
		} catch (SocketException error) {
			_logger.LogError(error, "Could not initialize the local Quest Pro steam label socket on port {Port}", LabelPort);
		}
	}

	public void Publish(long now, ReadOnlySpan<float> expressions, long lastSteamFaceTick, long lastSteamEyeTick, long lastSteamGazeTick) {
		if (_steamLabelSocket is null)
			return;

		long qpc = Stopwatch.GetTimestamp();
		try {
			if (qpc >= _nextSteamSchemaTick) {
				SendSteamLabel(new {
					V = 1,
					Type = "schema",
					Names = ExpressionNames
				});
				_nextSteamSchemaTick = qpc + Stopwatch.Frequency * 2;
			}

			if (qpc < _nextSteamLabelTick)
				return;

			_nextSteamLabelTick = qpc + Stopwatch.Frequency / 60;

			if (_lastPublishedSteamFaceTick != lastSteamFaceTick) {
				_lastPublishedSteamFaceTick = lastSteamFaceTick;
				++_steamSourceChangeSequence;
			}

			bool gazeValid = lastSteamGazeTick != 0 && (now - lastSteamGazeTick <= SteamFaceTimeoutMs);
			bool lowerFaceValid = lastSteamFaceTick != 0 && (now - lastSteamFaceTick <= SteamFaceTimeoutMs);
			bool upperFaceValid = lastSteamEyeTick != 0 && (now - lastSteamEyeTick <= SteamFaceTimeoutMs);

			SendSteamLabel(new {
				V = 1,
				Type = "sample",
				Sequence = ++_steamLabelSequence,
				Qpc = qpc,
				QpcFrequency = Stopwatch.Frequency,
				UtcUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
				SourceChangeSequence = _steamSourceChangeSequence,
				SourceUnchangedMs = lastSteamFaceTick != 0 ? (double)(now - lastSteamFaceTick) : 0.0,
				Values = expressions.ToArray(),
				// Bit 1 is Virtual Desktop's alternate tongue layout, which
				// Steam Link's named XR_FB weights do not use.
				FaceFlags = lowerFaceValid ? 1 : 0,
				IsEyeFollowingBlendshapesValid = upperFaceValid,
				LeftEyeIsValid = gazeValid,
				RightEyeIsValid = gazeValid,
				LeftEyeOrientation = new float[] { 0, 0, 0, 1 },
				RightEyeOrientation = new float[] { 0, 0, 0, 1 },
				LeftEyePosition = new float[] { 0, 0, 0 },
				RightEyePosition = new float[] { 0, 0, 0 },
				LeftEyeConfidence = gazeValid ? 1.0f : 0.0f,
				RightEyeConfidence = gazeValid ? 1.0f : 0.0f
			});
		} catch (SocketException) {
			// The label receiver is optional for ordinary live tracking.
		}
	}

	private void SendSteamLabel<T>(T message) {
		if (_steamLabelSocket is null)
			return;

		byte[] packet = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
		_steamLabelSocket.Send(packet, packet.Length);
	}

	private static Dictionary<string, int> BuildExpressionIndices() {
		var dict = new Dictionary<string, int>(ExpressionNames.Count, StringComparer.OrdinalIgnoreCase);
		for (int i = 0; i < ExpressionNames.Count; i++) {
			dict[ExpressionNames[i]] = i;
		}
		return dict;
	}

	public void Dispose() {
		_steamLabelSocket?.Dispose();
		_steamLabelSocket = null;
	}
}
