using Microsoft.Extensions.Logging;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace SteamLinkVRCFTModule.Overrides.Fwooffy;

public class CheekTelemetry : IDisposable {
	public const int CheekTelemetryPort = 27278;
	private static readonly IPEndPoint CheekTelemetryEndpoint = new(IPAddress.Loopback, CheekTelemetryPort);

	// Expression index constants for OpenXR XR_FB expressions
	private const int CheekPuffLIndex = 2; // CheekPuffL in ExpressionNames
	private const int CheekPuffRIndex = 3; // CheekPuffR in ExpressionNames

	private readonly ILogger _logger;
	private UdpClient? _cheekTelemetrySocket;
	private long _nextCheekTelemetryTick;

	public CheekTelemetry(ILogger logger) {
		_logger = logger;
	}

	public void Initialize() {
		try {
			_cheekTelemetrySocket = new UdpClient(AddressFamily.InterNetwork);
		} catch (SocketException error) {
			_logger.LogWarning(error, "Cheek calibration preview could not open its local output socket.");
		}
	}

	public void Publish(ReadOnlySpan<float> expressions, long nowMs) {
		if (_cheekTelemetrySocket is null || nowMs < _nextCheekTelemetryTick)
			return;

		_nextCheekTelemetryTick = nowMs + 50;

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

	public void Dispose() {
		_cheekTelemetrySocket?.Dispose();
		_cheekTelemetrySocket = null;
	}
}
