using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace SteamLinkVRCFTModule.Overrides.Fwooffy;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal readonly struct GazePacket {
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

	private const uint ExpectedMagic = 'Q' | ('P' << 8) | ('G' << 16) | ('E' << 24);
	private const byte ExpectedVersion = 1;
	public bool IsHeaderValid => Magic == ExpectedMagic && Version == ExpectedVersion;
	public bool IsLeftEyeValid => (Flags & GazeFlags.LeftEyeValid) != 0;
	public bool IsRightEyeValid => (Flags & GazeFlags.RightEyeValid) != 0;
}

public class GazeTracking : IDisposable {
	private const int GazePort = 27275;
	private const int GazePacketBytes = 24;
	private const long GazeTimeoutMs = 250;

	private readonly ILogger _logger;
	private UdpClient? _gazeSocket;
	private GazePacket _lastGazePacket;
	private long _lastGazePacketReceivedAt;

	public GazeTracking(ILogger logger) {
		_logger = logger;
	}

	public void Initialize(CancellationToken cancellationToken) {
		try {
			_gazeSocket = new UdpClient(new IPEndPoint(IPAddress.Loopback, GazePort));
			Task.Run(() => ReceiveGazeData(cancellationToken), cancellationToken);
		} catch (SocketException error) {
			_logger.LogError(error, "Could not bind the local Quest Pro gaze port {Port}", GazePort);
		}
	}

	public float Apply(bool isRight, bool isX, float defaultValue) {
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

	private async Task ReceiveGazeData(CancellationToken token) {
		if (_gazeSocket is null)
			return;

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
			// The socket was closed in Dispose()
		} catch (SocketException ex) {
			_logger.LogError(ex, "SocketException in ReceiveGazeData");
		} catch (Exception ex) {
			_logger.LogError(ex, "Unexpected error in ReceiveGazeData");
		}
	}

	public void Dispose() {
		_gazeSocket?.Dispose();
		_gazeSocket = null;
	}

	public bool TryApply(EyeExpression expression, float nativeValue, out float result) {
		switch (expression) {
			case EyeExpression.EyeLeftGazeX:
				result = Apply(false, true, nativeValue);
				return true;
			case EyeExpression.EyeLeftGazeY:
				result = Apply(false, false, nativeValue);
				return true;
			case EyeExpression.EyeRightGazeX:
				result = Apply(true, true, nativeValue);
				return true;
			case EyeExpression.EyeRightGazeY:
				result = Apply(true, false, nativeValue);
				return true;
			default:
				result = nativeValue;
				return false;
		}
	}
}
