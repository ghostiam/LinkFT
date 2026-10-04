using Microsoft.Extensions.Logging;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using VRCFaceTracking.Core.Params.Expressions;

namespace SteamLinkVRCFTModule.Overriders;

public class FwooffyQproEnhancedFT : Overrider.IOverider {
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
	private const long SteamFaceTimeoutMs = 500;

	private readonly ILogger _logger;
	private readonly CancellationTokenSource _cancellationTokenSource = new();

	private UdpClient? _gazeSocket;
	private UdpClient? _tongueSocket;
	private UdpClient? _pupilSocket;
	private UdpClient? _steamLabelSocket;

	GazePacket _lastGazePacket;
	long _lastGazePacketReceivedAt;

	PupilPacket _lastPupilPacket;
	long _lastPupilPacketReceivedAt;


	public FwooffyQproEnhancedFT(ILogger logger) {
		_logger = logger;
	}

	public void Initialize() {
		try {
			_steamLabelSocket = new UdpClient(AddressFamily.InterNetwork);
			_steamLabelSocket.Connect(IPAddress.Loopback, LabelPort);
		} catch (SocketException error) {
			_logger.LogError(error, "Could not bind the local Quest Pro steam label port {Port}", LabelPort);
		}

		try {
			_gazeSocket = new UdpClient(new IPEndPoint(IPAddress.Loopback, GazePort));
			Task.Run(ReceiveGazeData, _cancellationTokenSource.Token);
		} catch (SocketException error) {
			_logger.LogError(error, "Could not bind the local Quest Pro gaze port {Port}", GazePort);
		}

		try {
			_tongueSocket = new UdpClient(new IPEndPoint(IPAddress.Loopback, TonguePort));
			_tongueSocket.Client.Blocking = false;
		} catch (SocketException error) {
			_logger.LogError(error, "Could not bind the local Quest Pro tongue port {Port}", TonguePort);
		}

		try {
			_pupilSocket = new UdpClient(new IPEndPoint(IPAddress.Loopback, PupilPort));
			Task.Run(ReceivePupilData, _cancellationTokenSource.Token);
		} catch (SocketException error) {
			_logger.LogError(error, "Could not bind the local Quest Pro pupil port {Port}", PupilPort);
		}
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
	}

	public float Apply(Overrider.EyeExpression expression, float nativeValue) {
		switch (expression) {
			case Overrider.EyeExpression.EyeLeftGazeX:
				return FromGazePacketOrDefault(false, true, nativeValue);
			case Overrider.EyeExpression.EyeLeftGazeY:
				return FromGazePacketOrDefault(false, false, nativeValue);

			case Overrider.EyeExpression.EyeRightGazeX:
				return FromGazePacketOrDefault(true, true, nativeValue);
			case Overrider.EyeExpression.EyeRightGazeY:
				return FromGazePacketOrDefault(true, false, nativeValue);

			case Overrider.EyeExpression.EyeLeftPupilDiameter:
				return FromPupilPacketOrDefault(false, nativeValue);
			case Overrider.EyeExpression.EyeRightPupilDiameter:
				return FromPupilPacketOrDefault(true, nativeValue);

			default:
				return nativeValue;
		}
	}

	public float Apply(UnifiedExpressions expression, float nativeValue) {
		return nativeValue;
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
}