using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using RhythmLume.Core;

namespace RhythmLume.Hue;

public sealed class HueStreamingClient : IHueStreamingClient
{
    private readonly ILogger<HueStreamingClient> _logger;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private HttpClient? _httpClient;
    private UdpDatagramTransport? _udpTransport;
    private DtlsTransport? _dtlsTransport;
    private HueCredentials? _credentials;
    private EntertainmentConfiguration? _configuration;
    private int _sequence;

    public HueStreamingClient(ILogger<HueStreamingClient> logger)
    {
        _logger = logger;
    }

    public bool IsConnected => _dtlsTransport is not null;

    public async Task<IReadOnlyList<EntertainmentConfiguration>> GetConfigurationsAsync(
        HueBridgeInfo bridge,
        HueCredentials credentials,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(credentials);
        using var client = HueHttp.CreateClient(bridge, Uri.UriSchemeHttps, _logger);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/clip/v2/resource/entertainment_configuration");
        request.Headers.TryAddWithoutValidation(
            "hue-application-key",
            credentials.ApplicationKey);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(content);
        if (!document.RootElement.TryGetProperty("data", out var data))
        {
            return [];
        }

        var configurations = new List<EntertainmentConfiguration>();
        foreach (var element in data.EnumerateArray())
        {
            var id = element.TryGetProperty("id", out var idElement)
                ? idElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var name = element.TryGetProperty("metadata", out var metadata) &&
                       metadata.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString() ?? "Entertainment area"
                : "Entertainment area";
            var isActive = element.TryGetProperty("status", out var status) &&
                           string.Equals(
                               status.GetString(),
                               "active",
                               StringComparison.OrdinalIgnoreCase);
            var channels = ParseChannels(element);
            configurations.Add(new(id, name, isActive, channels));
        }

        return configurations;
    }

    public async Task ConnectAsync(
        HueBridgeInfo bridge,
        HueCredentials credentials,
        EntertainmentConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(configuration);
        if (string.IsNullOrWhiteSpace(credentials.ClientKey))
        {
            throw new HueApiException(
                "The bridge did not issue an Entertainment client key. Pair again with Entertainment access enabled.");
        }

        byte[] preSharedKey;
        try
        {
            preSharedKey = Convert.FromHexString(credentials.ClientKey);
        }
        catch (FormatException exception)
        {
            throw new HueApiException($"The Entertainment client key is invalid: {exception.Message}");
        }

        if (preSharedKey.Length != 16)
        {
            throw new HueApiException("The Entertainment client key must decode to exactly 16 bytes.");
        }

        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsConnected)
            {
                await DisconnectCoreAsync(cancellationToken).ConfigureAwait(false);
            }

            _credentials = credentials;
            _configuration = configuration;
            _httpClient = HueHttp.CreateClient(bridge, Uri.UriSchemeHttps, _logger);
            await SetConfigurationActionAsync("start", cancellationToken).ConfigureAwait(false);

            var endpoint = await ResolveEndpointAsync(
                bridge.InternalIpAddress,
                cancellationToken).ConfigureAwait(false);
            var socket = new Socket(
                endpoint.AddressFamily,
                SocketType.Dgram,
                ProtocolType.Udp);
            socket.Connect(endpoint);
            _udpTransport = new UdpDatagramTransport(socket);
            using var cancellationRegistration = cancellationToken.Register(
                static state => ((UdpDatagramTransport)state!).Close(),
                _udpTransport);
            var identity = new BasicTlsPskIdentity(
                credentials.ApplicationKey,
                preSharedKey);
            var tlsClient = new HuePskTlsClient(identity);
            var protocol = new DtlsClientProtocol();
            _dtlsTransport = await Task.Run(
                    () => protocol.Connect(tlsClient, _udpTransport),
                    CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(8), cancellationToken)
                .ConfigureAwait(false);

            _sequence = 0;
            _logger.LogInformation(
                "Hue Entertainment stream connected to {Area} with {ChannelCount} channels.",
                configuration.Name,
                configuration.Channels.Count);
        }
        catch
        {
            _dtlsTransport?.Close();
            _dtlsTransport = null;
            _udpTransport?.Close();
            _udpTransport = null;
            await TryStopConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            _httpClient?.Dispose();
            _httpClient = null;
            _credentials = null;
            _configuration = null;
            throw;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public Task SendFrameAsync(
        IReadOnlyDictionary<byte, RgbColor> channelColors,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channelColors);
        cancellationToken.ThrowIfCancellationRequested();
        var transport = _dtlsTransport ??
                        throw new InvalidOperationException("The Hue Entertainment stream is not connected.");
        var configuration = _configuration ??
                            throw new InvalidOperationException("No Entertainment configuration is active.");
        var packet = HueStreamPacketEncoder.Encode(
            configuration.Id,
            unchecked((byte)Interlocked.Increment(ref _sequence)),
            channelColors);
        try
        {
            transport.Send(packet, 0, packet.Length);
        }
        catch (IOException exception)
        {
            _logger.LogWarning(exception, "Hue Entertainment frame send failed.");
            throw;
        }

        return Task.CompletedTask;
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DisconnectCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await DisconnectAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _lifecycle.Dispose();
    }

    private async Task DisconnectCoreAsync(CancellationToken cancellationToken)
    {
        _dtlsTransport?.Close();
        _dtlsTransport = null;
        _udpTransport?.Close();
        _udpTransport = null;
        await TryStopConfigurationAsync(cancellationToken).ConfigureAwait(false);
        _httpClient?.Dispose();
        _httpClient = null;
        _credentials = null;
        _configuration = null;
        _logger.LogInformation("Hue Entertainment stream disconnected.");
    }

    private async Task TryStopConfigurationAsync(CancellationToken cancellationToken)
    {
        if (_httpClient is null || _configuration is null || _credentials is null)
        {
            return;
        }

        try
        {
            await SetConfigurationActionAsync("stop", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or HueApiException)
        {
            _logger.LogWarning(
                exception,
                "Could not explicitly stop the Hue Entertainment area; the bridge will time it out.");
        }
    }

    private async Task SetConfigurationActionAsync(
        string action,
        CancellationToken cancellationToken)
    {
        var path = $"/clip/v2/resource/entertainment_configuration/{Uri.EscapeDataString(_configuration!.Id)}";
        using var request = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = JsonContent.Create(new { action })
        };
        request.Headers.TryAddWithoutValidation(
            "hue-application-key",
            _credentials!.ApplicationKey);
        using var response = await _httpClient!.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(content);
        if (document.RootElement.TryGetProperty("errors", out var errors) &&
            errors.ValueKind == JsonValueKind.Array &&
            errors.GetArrayLength() > 0)
        {
            throw new HueApiException(errors[0].ToString());
        }
    }

    private static IReadOnlyList<EntertainmentChannel> ParseChannels(JsonElement configuration)
    {
        if (!configuration.TryGetProperty("channels", out var channelsElement))
        {
            return [];
        }

        var channels = new List<EntertainmentChannel>();
        foreach (var channel in channelsElement.EnumerateArray())
        {
            if (!channel.TryGetProperty("channel_id", out var channelIdElement) ||
                !channelIdElement.TryGetByte(out var channelId))
            {
                continue;
            }

            var serviceIds = new List<string>();
            if (channel.TryGetProperty("members", out var members))
            {
                foreach (var member in members.EnumerateArray())
                {
                    if (member.TryGetProperty("service", out var service) &&
                        service.TryGetProperty("rid", out var rid))
                    {
                        var value = rid.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            serviceIds.Add(value);
                        }
                    }
                }
            }

            channels.Add(new(channelId, serviceIds));
        }

        return channels;
    }

    private static async Task<IPEndPoint> ResolveEndpointAsync(
        string host,
        CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var parsed))
        {
            return new(parsed, 2100);
        }

        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        var address = addresses.FirstOrDefault(static candidate =>
            candidate.AddressFamily == AddressFamily.InterNetwork) ??
                      addresses.FirstOrDefault() ??
                      throw new HueApiException($"Could not resolve bridge host '{host}'.");
        return new(address, 2100);
    }

    private sealed class HuePskTlsClient : PskTlsClient
    {
        public HuePskTlsClient(TlsPskIdentity identity)
            : base(new BcTlsCrypto(), identity)
        {
        }

        protected override ProtocolVersion[] GetSupportedVersions() =>
            ProtocolVersion.DTLSv12.Only();

        protected override int[] GetSupportedCipherSuites() =>
            [CipherSuite.TLS_PSK_WITH_AES_128_GCM_SHA256];
    }
}

public static class HueStreamPacketEncoder
{
    private static readonly byte[] ProtocolName = "HueStream"u8.ToArray();

    public static byte[] Encode(
        string configurationId,
        byte sequence,
        IReadOnlyDictionary<byte, RgbColor> channelColors)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationId);
        ArgumentNullException.ThrowIfNull(channelColors);
        if (configurationId.Length != 36 || !Guid.TryParse(configurationId, out _))
        {
            throw new ArgumentException(
                "Entertainment configuration ID must be a canonical 36-character UUID.",
                nameof(configurationId));
        }

        if (channelColors.Count > 20)
        {
            throw new ArgumentOutOfRangeException(
                nameof(channelColors),
                "Hue Entertainment supports no more than 20 channels per frame.");
        }

        var packet = new byte[52 + (channelColors.Count * 7)];
        ProtocolName.CopyTo(packet, 0);
        packet[9] = 0x02;
        packet[10] = 0x00;
        packet[11] = sequence;
        packet[12] = 0;
        packet[13] = 0;
        packet[14] = 0;
        packet[15] = 0;
        Encoding.ASCII.GetBytes(configurationId, packet.AsSpan(16, 36));
        var offset = 52;
        foreach (var pair in channelColors.OrderBy(static pair => pair.Key))
        {
            packet[offset] = pair.Key;
            WriteColor(packet.AsSpan(offset + 1, 6), pair.Value);
            offset += 7;
        }

        return packet;
    }

    private static void WriteColor(Span<byte> destination, RgbColor color)
    {
        BinaryPrimitives.WriteUInt16BigEndian(
            destination,
            ToUInt16(color.Red));
        BinaryPrimitives.WriteUInt16BigEndian(
            destination[2..],
            ToUInt16(color.Green));
        BinaryPrimitives.WriteUInt16BigEndian(
            destination[4..],
            ToUInt16(color.Blue));
    }

    private static ushort ToUInt16(float value) =>
        (ushort)Math.Clamp(
            (int)Math.Round(Math.Clamp(value, 0, 1) * ushort.MaxValue),
            0,
            ushort.MaxValue);
}

internal sealed class UdpDatagramTransport : DatagramTransport
{
    private const int DatagramLimit = 16 * 1024;
    private readonly Socket _socket;
    private int _closed;

    public UdpDatagramTransport(Socket socket)
    {
        _socket = socket;
    }

    public int GetReceiveLimit() => DatagramLimit;

    public int GetSendLimit() => DatagramLimit;

    public int Receive(byte[] buf, int off, int len, int waitMillis) =>
        Receive(buf.AsSpan(off, len), waitMillis);

    public int Receive(Span<byte> buffer, int waitMillis)
    {
        if (Volatile.Read(ref _closed) != 0)
        {
            throw new IOException("UDP transport is closed.");
        }

        try
        {
            var microseconds = (int)Math.Min(
                int.MaxValue,
                Math.Max(1L, waitMillis) * 1000L);
            if (!_socket.Poll(microseconds, SelectMode.SelectRead))
            {
                return -1;
            }

            return _socket.Receive(buffer, SocketFlags.None);
        }
        catch (SocketException exception) when (
            exception.SocketErrorCode is SocketError.TimedOut or
                SocketError.WouldBlock)
        {
            return -1;
        }
        catch (ObjectDisposedException exception)
        {
            throw new IOException("UDP transport was closed.", exception);
        }
        catch (SocketException exception)
        {
            throw new IOException("UDP receive failed.", exception);
        }
    }

    public void Send(byte[] buf, int off, int len) => Send(buf.AsSpan(off, len));

    public void Send(ReadOnlySpan<byte> buffer)
    {
        if (Volatile.Read(ref _closed) != 0)
        {
            throw new IOException("UDP transport is closed.");
        }

        try
        {
            _socket.Send(buffer, SocketFlags.None);
        }
        catch (SocketException exception)
        {
            throw new IOException("UDP send failed.", exception);
        }
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        try
        {
            _socket.Shutdown(SocketShutdown.Both);
        }
        catch (SocketException)
        {
        }
        finally
        {
            _socket.Dispose();
        }
    }
}
