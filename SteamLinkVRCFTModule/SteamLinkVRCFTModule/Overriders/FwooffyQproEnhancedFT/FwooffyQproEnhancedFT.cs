using Microsoft.Extensions.Logging;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using VRCFaceTracking.Core.Params.Expressions;

namespace SteamLinkVRCFTModule.Overrides.Fwooffy;

public class QproEnhancedFT : IOverrider {
	internal static IReadOnlyList<string> ExpressionNames { get; } = Array.AsReadOnly(new[] {
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

	private static readonly Dictionary<string, int> ExpressionIndices = BuildExpressionIndices();
	private const string FaceWeightPrefix = "/sl/xrfb/facew/";
	private const long SteamFaceTimeoutMs = 250;

	private static readonly JsonSerializerOptions JsonOptions = new() {
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase
	};

	private const int GazePort = 27275;
	private const int GazePacketBytes = 24;
	private const long GazeTimeoutMs = 250;
	private const int TonguePort = 27276;
	private const int TonguePacketBytes = 56;
	private const long TongueTimeoutMs = 300;
	private const int PupilPort = 27277;
	private const int PupilPacketBytes = 16;
	private const long PupilTimeoutMs = 350;
	private const int LabelPort = 27274;
	private const int CheekTelemetryPort = 27278;
	private static readonly IPEndPoint CheekTelemetryEndpoint = new(IPAddress.Loopback, CheekTelemetryPort);

	private readonly EyebrowSettings _eyebrowSettings;
	private readonly ILogger _logger;
	private readonly CancellationTokenSource _cancellationTokenSource = new();

	private UdpClient? _gazeSocket;
	private UdpClient? _tongueSocket;
	private UdpClient? _pupilSocket;
	private UdpClient? _steamLabelSocket;
	private UdpClient? _cheekTelemetrySocket;

	private readonly float[] _expressions = new float[70];

	private long _steamLabelSequence;
	private long _steamSourceChangeSequence;
	private long _lastSteamFaceTick;
	private long _lastSteamEyeTick;
	private long _lastSteamGazeTick;
	private long _lastPublishedSteamFaceTick;
	private long _nextSteamSchemaTick;
	private long _nextSteamLabelTick;
	private long _nextCheekTelemetryTick;

	GazePacket _lastGazePacket;
	long _lastGazePacketReceivedAt;

	PupilPacket _lastPupilPacket;
	long _lastPupilPacketReceivedAt;

	TonguePacket _lastTonguePacket;
	long _lastTonguePacketReceivedAt;


	public QproEnhancedFT(ILogger logger) {
		_logger = logger;
		_eyebrowSettings = new EyebrowSettings(logger);
	}

	public void Initialize() {
		try {
			_steamLabelSocket = new UdpClient(AddressFamily.InterNetwork);
			_steamLabelSocket.Connect(IPAddress.Loopback, LabelPort);
		} catch (SocketException error) {
			_logger.LogError(error, "Could not initialize the local Quest Pro steam label socket on port {Port}", LabelPort);
		}

		try {
			_gazeSocket = new UdpClient(new IPEndPoint(IPAddress.Loopback, GazePort));
			Task.Run(ReceiveGazeData, _cancellationTokenSource.Token);
		} catch (SocketException error) {
			_logger.LogError(error, "Could not bind the local Quest Pro gaze port {Port}", GazePort);
		}

		try {
			_tongueSocket = new UdpClient(new IPEndPoint(IPAddress.Loopback, TonguePort));
			Task.Run(ReceiveTongueData, _cancellationTokenSource.Token);
		} catch (SocketException error) {
			_logger.LogError(error, "Could not bind the local Quest Pro tongue port {Port}", TonguePort);
		}

		try {
			_pupilSocket = new UdpClient(new IPEndPoint(IPAddress.Loopback, PupilPort));
			Task.Run(ReceivePupilData, _cancellationTokenSource.Token);
		} catch (SocketException error) {
			_logger.LogError(error, "Could not bind the local Quest Pro pupil port {Port}", PupilPort);
		}

		try {
			_cheekTelemetrySocket = new UdpClient(AddressFamily.InterNetwork);
		} catch (SocketException error) {
			_logger.LogWarning(error, "Cheek calibration preview could not open its local output socket.");
		}

		_eyebrowSettings.Refresh();
	}

	public void Teardown() {
		_cancellationTokenSource.Cancel();
		_steamLabelSocket?.Dispose();
		_steamLabelSocket = null;
		_gazeSocket?.Dispose();
		_gazeSocket = null;
		_tongueSocket?.Dispose();
		_tongueSocket = null;
		_pupilSocket?.Dispose();
		_pupilSocket = null;
		_cheekTelemetrySocket?.Dispose();
		_cheekTelemetrySocket = null;
	}

	public float Apply(EyeExpression expression, float nativeValue) {
		switch (expression) {
			case EyeExpression.EyeLeftGazeX:
				return FromGazePacketOrDefault(false, true, nativeValue);
			case EyeExpression.EyeLeftGazeY:
				return FromGazePacketOrDefault(false, false, nativeValue);

			case EyeExpression.EyeRightGazeX:
				return FromGazePacketOrDefault(true, true, nativeValue);
			case EyeExpression.EyeRightGazeY:
				return FromGazePacketOrDefault(true, false, nativeValue);

			case EyeExpression.EyeLeftPupilDiameter:
				return FromPupilPacketOrDefault(false, nativeValue);
			case EyeExpression.EyeRightPupilDiameter:
				return FromPupilPacketOrDefault(true, nativeValue);
			
			// VRCFT normalizes combined dilation using these limits. The relative
			// camera estimate eases from 2 to 8 with neutral at 5, so use the
			// same range to make VRChat's 0..1 animation respond visibly.
			case EyeExpression.EyeMaxDilation:
				return _lastPupilPacket.IsHeaderValid ? 8.0f : nativeValue;
			case EyeExpression.EyeMinDilation:
				return _lastPupilPacket.IsHeaderValid ? 2.0f : nativeValue;

			default:
				return nativeValue;
		}
	}

	public float Apply(UnifiedExpressions expression, float nativeValue) {
		switch (expression) {
			case UnifiedExpressions.TongueOut:
			case UnifiedExpressions.TongueUp:
			case UnifiedExpressions.TongueDown:
			case UnifiedExpressions.TongueLeft:
			case UnifiedExpressions.TongueRight:
			case UnifiedExpressions.TongueRoll:
			case UnifiedExpressions.TongueBendDown:
			case UnifiedExpressions.TongueCurlUp:
			case UnifiedExpressions.TongueSquish:
			case UnifiedExpressions.TongueFlat:
			case UnifiedExpressions.TongueTwistLeft:
			case UnifiedExpressions.TongueTwistRight:
				return FromTonguePacketOrDefault(expression, nativeValue);

			case UnifiedExpressions.BrowPinchLeft:
			case UnifiedExpressions.BrowLowererLeft:
			case UnifiedExpressions.BrowPinchRight:
			case UnifiedExpressions.BrowLowererRight:
			case UnifiedExpressions.BrowInnerUpLeft:
			case UnifiedExpressions.BrowInnerUpRight:
			case UnifiedExpressions.BrowOuterUpLeft:
			case UnifiedExpressions.BrowOuterUpRight:
				return _eyebrowSettings.Apply(nativeValue);

			// TODO: Implement cheek overrides (CheekPuffLeft, CheekPuffRight, CheekSuckLeft, CheekSuckRight) via CheekPuffTracker / calibration
			case UnifiedExpressions.CheekPuffLeft:
			case UnifiedExpressions.CheekPuffRight:
			case UnifiedExpressions.CheekSuckLeft:
			case UnifiedExpressions.CheekSuckRight:
				return nativeValue;

			default:
				return nativeValue;
		}
	}

	private float FromGazePacketOrDefault(bool isRight, bool isX, float defaultValue) {
		long now = Environment.TickCount64;
		bool isFresh = _lastGazePacketReceivedAt != 0 && now - _lastGazePacketReceivedAt <= GazeTimeoutMs;
		if (!isFresh) return defaultValue;

		var data = _lastGazePacket;
		bool isValid = isRight ? data.IsRightEyeValid : data.IsLeftEyeValid;
		if (!isValid) return defaultValue;

		var x = isRight ? data.RightGazeX : data.LeftGazeX;
		var y = isRight ? data.RightGazeY : data.LeftGazeY;

		return isX ? x : y;
	}

	private float FromPupilPacketOrDefault(bool isRight, float defaultValue) {
		long now = Environment.TickCount64;
		bool isFresh = _lastPupilPacketReceivedAt != 0 && now - _lastPupilPacketReceivedAt <= PupilTimeoutMs;
		if (!isFresh) return defaultValue;

		var data = _lastPupilPacket;
		bool isValid = isRight ? data.IsRightEyeValid : data.IsLeftEyeValid;
		if (!isValid) return defaultValue;

		return isRight ? data.RightPupilMm : data.LeftPupilMm;
	}

	private float FromTonguePacketOrDefault(UnifiedExpressions expression, float defaultValue) {
		long now = Environment.TickCount64;
		bool isFresh = _lastTonguePacketReceivedAt != 0 && now - _lastTonguePacketReceivedAt <= TongueTimeoutMs;
		if (!isFresh) return defaultValue;

		var data = _lastTonguePacket;
		if (!data.IsTongueValid) return defaultValue;

		float value = expression switch {
			UnifiedExpressions.TongueOut => data.TongueOut,
			UnifiedExpressions.TongueUp => data.TongueUp,
			UnifiedExpressions.TongueDown => data.TongueDown,
			UnifiedExpressions.TongueLeft => data.TongueLeft,
			UnifiedExpressions.TongueRight => data.TongueRight,
			UnifiedExpressions.TongueRoll => data.TongueRoll,
			UnifiedExpressions.TongueBendDown => data.TongueBendDown,
			UnifiedExpressions.TongueCurlUp => data.TongueCurlUp,
			UnifiedExpressions.TongueSquish => data.TongueSquish,
			UnifiedExpressions.TongueFlat => data.TongueFlat,
			UnifiedExpressions.TongueTwistLeft => data.TongueTwistLeft,
			UnifiedExpressions.TongueTwistRight => data.TongueTwistRight,
			_ => defaultValue
		};

		return float.IsFinite(value) ? Math.Clamp(value, 0.0f, 1.0f) : defaultValue;
	}

	public void ProcessOscMessages(IReadOnlyList<OSCM> messages) {
		long tick = Environment.TickCount64;

		foreach (var oscMessage in messages) {
			if (oscMessage == null || oscMessage.Values.Count == 0)
				continue;

			string address = oscMessage.Address;
			if (address.StartsWith(FaceWeightPrefix, StringComparison.OrdinalIgnoreCase)) {
				string name = address[FaceWeightPrefix.Length..];
				if (ExpressionIndices.TryGetValue(name, out int index)) {
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

		PublishCheekCalibrationSample(_expressions, tick);
		PublishSteamLabels(tick, _expressions);
	}

	private void PublishCheekCalibrationSample(ReadOnlySpan<float> expressions, long nowMs) {
		if (_cheekTelemetrySocket is null || nowMs < _nextCheekTelemetryTick)
			return;

		_nextCheekTelemetryTick = nowMs + 50;

		// Expression index constants for OpenXR XR_FB expressions
		const int CheekPuffLIndex = 2; // CheekPuffL in ExpressionNames
		const int CheekPuffRIndex = 3; // CheekPuffR in ExpressionNames

		float left = expressions.Length > CheekPuffLIndex && float.IsFinite(expressions[CheekPuffLIndex])
			? Math.Clamp(expressions[CheekPuffLIndex], 0.0f, 1.0f)
			: 0.0f;
		float right = expressions.Length > CheekPuffRIndex && float.IsFinite(expressions[CheekPuffRIndex])
			? Math.Clamp(expressions[CheekPuffRIndex], 0.0f, 1.0f)
			: 0.0f;

		byte[] packet = new byte[16];
		packet[0] = (byte)'Q';
		packet[1] = (byte)'C';
		packet[2] = (byte)'P';
		packet[3] = (byte)'2'; // raw
		packet[4] = 1; // SourceSteamLink = 1

		BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8), BitConverter.SingleToInt32Bits(left));
		BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(12), BitConverter.SingleToInt32Bits(right));

		try {
			_cheekTelemetrySocket.Send(packet, packet.Length, CheekTelemetryEndpoint);
		} catch (SocketException) {
			_nextCheekTelemetryTick = nowMs + 1000;
		}
	}

	private void PublishSteamLabels(long now, ReadOnlySpan<float> expressions) {
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

			if (_lastPublishedSteamFaceTick != _lastSteamFaceTick) {
				_lastPublishedSteamFaceTick = _lastSteamFaceTick;
				++_steamSourceChangeSequence;
			}

			bool gazeValid = _lastSteamGazeTick != 0 && (now - _lastSteamGazeTick <= SteamFaceTimeoutMs);
			bool lowerFaceValid = _lastSteamFaceTick != 0 && (now - _lastSteamFaceTick <= SteamFaceTimeoutMs);
			bool upperFaceValid = _lastSteamEyeTick != 0 && (now - _lastSteamEyeTick <= SteamFaceTimeoutMs);

			SendSteamLabel(new {
				V = 1,
				Type = "sample",
				Sequence = ++_steamLabelSequence,
				Qpc = qpc,
				QpcFrequency = Stopwatch.Frequency,
				UtcUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
				SourceChangeSequence = _steamSourceChangeSequence,
				SourceUnchangedMs = _lastSteamFaceTick != 0 ? (double)(now - _lastSteamFaceTick) : 0.0,
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

	[StructLayout(LayoutKind.Sequential, Pack = 1)]
	private readonly struct GazePacket {
		public readonly uint Magic;
		public readonly byte Version;
		public readonly GazeFlags Flags;
		public readonly ushort Reserved;
		public readonly float LeftGazeX;
		public readonly float LeftGazeY;
		public readonly float RightGazeX;
		public readonly float RightGazeY;

		[Flags]
		public enum GazeFlags : byte {
			None = 0,
			LeftEyeValid = 1 << 0,
			RightEyeValid = 1 << 1
		}

		const uint ExpectedMagic = 'Q' | ('P' << 8) | ('G' << 16) | ('E' << 24);
		const byte ExpectedVersion = 1;
		public bool IsHeaderValid => Magic == ExpectedMagic && Version == ExpectedVersion;
		public bool IsLeftEyeValid => (Flags & GazeFlags.LeftEyeValid) != 0;
		public bool IsRightEyeValid => (Flags & GazeFlags.RightEyeValid) != 0;
	}

	[StructLayout(LayoutKind.Sequential, Pack = 1)]
	private readonly struct PupilPacket {
		public readonly uint Magic;
		public readonly byte Version;
		public readonly PupilFlags Flags;
		public readonly ushort Reserved;
		public readonly float LeftPupilMm;
		public readonly float RightPupilMm;

		[Flags]
		public enum PupilFlags : byte {
			None = 0,
			LeftEyeValid = 1 << 0,
			RightEyeValid = 1 << 1
		}

		const uint ExpectedMagic = 'Q' | ('P' << 8) | ('D' << 16) | ('I' << 24);
		const byte ExpectedVersion = 1;
		public bool IsHeaderValid => Magic == ExpectedMagic && Version == ExpectedVersion;
		public bool IsLeftEyeValid => (Flags & PupilFlags.LeftEyeValid) != 0 && float.IsFinite(LeftPupilMm) && LeftPupilMm is >= 2.0f and <= 9.0f;
		public bool IsRightEyeValid => (Flags & PupilFlags.RightEyeValid) != 0 && float.IsFinite(RightPupilMm) && RightPupilMm is >= 2.0f and <= 9.0f;
	}

	[StructLayout(LayoutKind.Sequential, Pack = 1)]
	private readonly struct TonguePacket {
		public readonly uint Magic;
		public readonly byte Version;
		public readonly TongueFlags Flags;
		public readonly ushort Reserved;
		public readonly float TongueOut;
		public readonly float TongueUp;
		public readonly float TongueDown;
		public readonly float TongueLeft;
		public readonly float TongueRight;
		public readonly float TongueRoll;
		public readonly float TongueBendDown;
		public readonly float TongueCurlUp;
		public readonly float TongueSquish;
		public readonly float TongueFlat;
		public readonly float TongueTwistLeft;
		public readonly float TongueTwistRight;

		[Flags]
		public enum TongueFlags : byte {
			None = 0,
			Enabled = 1 << 0
		}

		const uint ExpectedMagic = 'Q' | ('P' << 8) | ('T' << 16) | ('O' << 24);
		const byte ExpectedVersion = 1;
		public bool IsHeaderValid => Magic == ExpectedMagic && Version == ExpectedVersion;
		public bool IsTongueValid => (Flags & TongueFlags.Enabled) != 0;
	}

	private async Task ReceiveGazeData() {
		if (_gazeSocket is null)
			return;

		var token = _cancellationTokenSource.Token;

		try {
			while (!token.IsCancellationRequested) {
				UdpReceiveResult result = await _gazeSocket.ReceiveAsync(token);
				byte[] packet = result.Buffer;

				if (packet.Length != GazePacketBytes)
					continue;

				GazePacket data = MemoryMarshal.AsRef<GazePacket>(packet);
				if (!data.IsHeaderValid)
					continue;

				_lastGazePacket = data;
				_lastGazePacketReceivedAt = Environment.TickCount64;
			}
		} catch (OperationCanceledException) {
			// Normal completion when the token is canceled
		} catch (ObjectDisposedException) {
			// The socket was closed in Teardown()
		} catch (SocketException ex) {
			_logger.LogError(ex, "SocketException in ReceiveGazeData");
		} catch (Exception ex) {
			_logger.LogError(ex, "Unexpected error in ReceiveGazeData");
		}
	}

	private async Task ReceivePupilData() {
		if (_pupilSocket is null)
			return;

		var token = _cancellationTokenSource.Token;

		try {
			while (!token.IsCancellationRequested) {
				UdpReceiveResult result = await _pupilSocket.ReceiveAsync(token);
				byte[] packet = result.Buffer;

				if (packet.Length != PupilPacketBytes)
					continue;

				PupilPacket data = MemoryMarshal.AsRef<PupilPacket>(packet);
				if (!data.IsHeaderValid)
					continue;

				_lastPupilPacket = data;
				_lastPupilPacketReceivedAt = Environment.TickCount64;
			}
		} catch (OperationCanceledException) {
			// Normal completion when the token is canceled
		} catch (ObjectDisposedException) {
			// The socket was closed in Teardown()
		} catch (SocketException ex) {
			_logger.LogError(ex, "SocketException in ReceivePupilData");
		} catch (Exception ex) {
			_logger.LogError(ex, "Unexpected error in ReceivePupilData");
		}
	}

	private async Task ReceiveTongueData() {
		if (_tongueSocket is null)
			return;

		var token = _cancellationTokenSource.Token;

		try {
			while (!token.IsCancellationRequested) {
				UdpReceiveResult result = await _tongueSocket.ReceiveAsync(token);
				byte[] packet = result.Buffer;

				if (packet.Length != TonguePacketBytes)
					continue;

				TonguePacket data = MemoryMarshal.AsRef<TonguePacket>(packet);
				if (!data.IsHeaderValid)
					continue;

				_lastTonguePacket = data;
				_lastTonguePacketReceivedAt = Environment.TickCount64;
			}
		} catch (OperationCanceledException) {
			// Normal completion when the token is canceled
		} catch (ObjectDisposedException) {
			// The socket was closed in Teardown()
		} catch (SocketException ex) {
			_logger.LogError(ex, "SocketException in ReceiveTongueData");
		} catch (Exception ex) {
			_logger.LogError(ex, "Unexpected error in ReceiveTongueData");
		}
	}
}