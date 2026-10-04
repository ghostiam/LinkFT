using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace SteamLinkVRCFTModule.Overrides.Fwooffy;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal readonly struct PupilPacket {
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

	private const uint ExpectedMagic = 'Q' | ('P' << 8) | ('D' << 16) | ('I' << 24);
	private const byte ExpectedVersion = 1;
	public bool IsHeaderValid => Magic == ExpectedMagic && Version == ExpectedVersion;
	public bool IsLeftEyeValid => (Flags & PupilFlags.LeftEyeValid) != 0 && float.IsFinite(LeftPupilMm) && LeftPupilMm is >= 2.0f and <= 9.0f;
	public bool IsRightEyeValid => (Flags & PupilFlags.RightEyeValid) != 0 && float.IsFinite(RightPupilMm) && RightPupilMm is >= 2.0f and <= 9.0f;
}

public class PupilTracking : IDisposable {
	private const int PupilPort = 27277;
	private const int PupilPacketBytes = 16;
	private const long PupilTimeoutMs = 350;

	private readonly ILogger _logger;
	private UdpClient? _pupilSocket;
	private PupilPacket _lastPupilPacket;
	private long _lastPupilPacketReceivedAt;

	public PupilTracking(ILogger logger) {
		_logger = logger;
	}

	public bool IsHeaderValid => _lastPupilPacket.IsHeaderValid;

	public void Initialize(CancellationToken cancellationToken) {
		try {
			_pupilSocket = new UdpClient(new IPEndPoint(IPAddress.Loopback, PupilPort));
			Task.Run(() => ReceivePupilData(cancellationToken), cancellationToken);
		} catch (SocketException error) {
			_logger.LogError(error, "Could not bind the local Quest Pro pupil port {Port}", PupilPort);
		}
	}

	public float Apply(bool isRight, float defaultValue) {
		long now = Environment.TickCount64;
		bool isFresh = _lastPupilPacketReceivedAt != 0 && now - _lastPupilPacketReceivedAt <= PupilTimeoutMs;
		if (!isFresh) return defaultValue;

		var data = _lastPupilPacket;
		bool isValid = isRight ? data.IsRightEyeValid : data.IsLeftEyeValid;
		if (!isValid) return defaultValue;

		return isRight ? data.RightPupilMm : data.LeftPupilMm;
	}

	private async Task ReceivePupilData(CancellationToken token) {
		if (_pupilSocket is null)
			return;

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
			// The socket was closed in Dispose()
		} catch (SocketException ex) {
			_logger.LogError(ex, "SocketException in ReceivePupilData");
		} catch (Exception ex) {
			_logger.LogError(ex, "Unexpected error in ReceivePupilData");
		}
	}

	public void Dispose() {
		_pupilSocket?.Dispose();
		_pupilSocket = null;
	}

	public bool TryApply(EyeExpression expression, float nativeValue, out float result) {
		switch (expression) {
			case EyeExpression.EyeLeftPupilDiameter:
				result = Apply(false, nativeValue);
				return true;
			case EyeExpression.EyeRightPupilDiameter:
				result = Apply(true, nativeValue);
				return true;

			// VRCFT normalizes combined dilation using these limits. The relative
			// camera estimate eases from 2 to 8 with neutral at 5, so use the
			// same range to make VRChat's 0..1 animation respond visibly.
			case EyeExpression.EyeMaxDilation:
				result = IsHeaderValid ? 8.0f : nativeValue;
				return true;
			case EyeExpression.EyeMinDilation:
				result = IsHeaderValid ? 2.0f : nativeValue;
				return true;

			default:
				result = nativeValue;
				return false;
		}
	}
}
