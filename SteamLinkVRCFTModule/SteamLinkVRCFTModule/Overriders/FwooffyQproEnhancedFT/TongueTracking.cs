using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using VRCFaceTracking.Core.Params.Expressions;

namespace SteamLinkVRCFTModule.Overrides.Fwooffy;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal readonly struct TonguePacket {
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

	private const uint ExpectedMagic = 'Q' | ('P' << 8) | ('T' << 16) | ('O' << 24);
	private const byte ExpectedVersion = 1;
	public bool IsHeaderValid => Magic == ExpectedMagic && Version == ExpectedVersion;
	public bool IsTongueValid => (Flags & TongueFlags.Enabled) != 0;
}

public class TongueTracking : IDisposable {
	private const int TonguePort = 27276;
	private const int TonguePacketBytes = 56;
	private const long TongueTimeoutMs = 300;

	private readonly ILogger _logger;
	private UdpClient? _tongueSocket;
	private TonguePacket _lastTonguePacket;
	private long _lastTonguePacketReceivedAt;

	public TongueTracking(ILogger logger) {
		_logger = logger;
	}

	public void Initialize(CancellationToken cancellationToken) {
		try {
			_tongueSocket = new UdpClient(new IPEndPoint(IPAddress.Loopback, TonguePort));
			Task.Run(() => ReceiveTongueData(cancellationToken), cancellationToken);
		} catch (SocketException error) {
			_logger.LogError(error, "Could not bind the local Quest Pro tongue port {Port}", TonguePort);
		}
	}

	public float Apply(UnifiedExpressions expression, float defaultValue) {
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

	public bool TryApply(UnifiedExpressions expression, float nativeValue, out float result) {
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
				result = Apply(expression, nativeValue);
				return true;
			default:
				result = nativeValue;
				return false;
		}
	}

	private async Task ReceiveTongueData(CancellationToken token) {
		if (_tongueSocket is null)
			return;

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
			// The socket was closed in Dispose()
		} catch (SocketException ex) {
			_logger.LogError(ex, "SocketException in ReceiveTongueData");
		} catch (Exception ex) {
			_logger.LogError(ex, "Unexpected error in ReceiveTongueData");
		}
	}

	public void Dispose() {
		_tongueSocket?.Dispose();
		_tongueSocket = null;
	}
}
