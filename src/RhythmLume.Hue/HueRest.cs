using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using RhythmLume.Core;
using Zeroconf;

namespace RhythmLume.Hue;

public sealed class HueLinkButtonRequiredException : InvalidOperationException
{
    public HueLinkButtonRequiredException()
        : base("Press the physical link button on the Hue Bridge, then pair again.")
    {
    }
}

public sealed class HueApiException : InvalidOperationException
{
    public HueApiException(string message)
        : base(message)
    {
    }
}

public sealed class HueBridgeDiscovery : IHueBridgeDiscovery
{
    private const string DiscoveryEndpoint = "https://discovery.meethue.com/";
    private readonly HttpClient _httpClient;
    private readonly ILogger<HueBridgeDiscovery> _logger;

    public HueBridgeDiscovery(HttpClient httpClient, ILogger<HueBridgeDiscovery> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<IReadOnlyList<HueBridgeInfo>> DiscoverAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        var mdns = DiscoverMdnsSafeAsync(timeout, timeoutCancellation.Token);
        var broker = DiscoverBrokerSafeAsync(timeoutCancellation.Token);
        var results = (await Task.WhenAll(mdns, broker).ConfigureAwait(false))
            .SelectMany(static bridges => bridges)
            .GroupBy(
                bridge => string.IsNullOrWhiteSpace(bridge.Id)
                    ? bridge.InternalIpAddress
                    : bridge.Id,
                StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.OrderByDescending(bridge => bridge.ModelId is not null).First())
            .OrderBy(static bridge => bridge.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _logger.LogInformation("Discovered {BridgeCount} Hue bridge(s).", results.Length);
        return results;
    }

    public async Task<HueBridgeInfo> ResolveManualAsync(
        string hostOrAddress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(hostOrAddress))
        {
            throw new ArgumentException("Enter a bridge IP address or host name.", nameof(hostOrAddress));
        }

        var host = hostOrAddress.Trim();
        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        var address = addresses.FirstOrDefault(static item =>
            item.AddressFamily is System.Net.Sockets.AddressFamily.InterNetwork or
                System.Net.Sockets.AddressFamily.InterNetworkV6);
        if (address is null)
        {
            throw new HueApiException($"Could not resolve '{host}'.");
        }

        var provisional = new HueBridgeInfo(
            string.Empty,
            address.ToString(),
            $"Hue Bridge ({address})",
            IsManual: true);
        var config = await HueHttp.GetConfigWithFallbackAsync(
            provisional,
            _logger,
            cancellationToken).ConfigureAwait(false);
        return HueJson.ParseBridgeConfig(config, address.ToString(), true);
    }

    private async Task<IReadOnlyList<HueBridgeInfo>> DiscoverMdnsSafeAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            var hosts = await ZeroconfResolver.ResolveAsync(
                "_hue._tcp.local.",
                timeout,
                retries: 2,
                retryDelayMilliseconds: 100,
                callback: null,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return hosts.Select(host =>
            {
                var id = ExtractBridgeId(host);
                return new HueBridgeInfo(
                    id,
                    host.IPAddress,
                    string.IsNullOrWhiteSpace(host.DisplayName)
                        ? $"Hue Bridge ({host.IPAddress})"
                        : host.DisplayName);
            }).ToArray();
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException ||
            !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Hue mDNS discovery failed; broker discovery remains available.");
            return [];
        }
    }

    private async Task<IReadOnlyList<HueBridgeInfo>> DiscoverBrokerSafeAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var bridges = await _httpClient.GetFromJsonAsync<BrokerBridge[]>(
                DiscoveryEndpoint,
                cancellationToken).ConfigureAwait(false);
            return bridges?.Select(static bridge => new HueBridgeInfo(
                    bridge.Id ?? string.Empty,
                    bridge.InternalIpAddress ?? string.Empty,
                    $"Hue Bridge ({bridge.InternalIpAddress})"))
                .Where(static bridge => !string.IsNullOrWhiteSpace(bridge.InternalIpAddress))
                .ToArray() ?? [];
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException ||
            !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Hue broker discovery failed; mDNS discovery remains available.");
            return [];
        }
    }

    private static string ExtractBridgeId(IZeroconfHost host)
    {
        foreach (var propertySet in host.Services.Values.SelectMany(static service => service.Properties))
        {
            if (propertySet.TryGetValue("bridgeid", out var bridgeId))
            {
                return bridgeId;
            }
        }

        var candidate = new string(host.Id.Where(Uri.IsHexDigit).ToArray());
        return candidate.Length >= 16 ? candidate[^16..] : host.Id;
    }

    private sealed record BrokerBridge(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("internalipaddress")] string? InternalIpAddress);
}

public sealed class HueAuthenticator : IHueAuthenticator
{
    private readonly ILogger<HueAuthenticator> _logger;

    public HueAuthenticator(ILogger<HueAuthenticator> logger)
    {
        _logger = logger;
    }

    public async Task<HueCredentials> PairAsync(
        HueBridgeInfo bridge,
        string applicationName,
        string deviceName,
        bool requestEntertainmentKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        var safeApp = SanitizeDeviceTypePart(applicationName, "RhythmLume");
        var safeDevice = SanitizeDeviceTypePart(deviceName, Environment.MachineName);
        var payload = new
        {
            devicetype = $"{safeApp}#{safeDevice}",
            generateclientkey = requestEntertainmentKey
        };
        using var response = await HueHttp.SendWithFallbackAsync(
            bridge,
            HttpMethod.Post,
            "/api",
            JsonContent.Create(payload),
            null,
            _logger,
            cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(json);
        var item = document.RootElement.EnumerateArray().FirstOrDefault();
        if (item.TryGetProperty("error", out var error))
        {
            var type = error.TryGetProperty("type", out var typeElement)
                ? typeElement.GetInt32()
                : -1;
            var description = error.TryGetProperty("description", out var descriptionElement)
                ? descriptionElement.GetString()
                : "Hue Bridge rejected pairing.";
            if (type == 101)
            {
                throw new HueLinkButtonRequiredException();
            }

            throw new HueApiException(description ?? "Hue Bridge rejected pairing.");
        }

        if (!item.TryGetProperty("success", out var success) ||
            !success.TryGetProperty("username", out var usernameElement))
        {
            throw new HueApiException("Hue Bridge returned an invalid pairing response.");
        }

        var username = usernameElement.GetString();
        var clientKey = success.TryGetProperty("clientkey", out var clientKeyElement)
            ? clientKeyElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new HueApiException("Hue Bridge did not return an application key.");
        }

        _logger.LogInformation(
            "Paired with Hue bridge {BridgeId}; entertainment key issued: {HasClientKey}.",
            bridge.Id,
            !string.IsNullOrWhiteSpace(clientKey));
        return new(username, clientKey);
    }

    private static string SanitizeDeviceTypePart(string? value, string fallback)
    {
        var source = string.IsNullOrWhiteSpace(value) ? fallback : value;
        var sanitized = new string(source
            .Where(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            .Take(20)
            .ToArray());
        return string.IsNullOrEmpty(sanitized) ? fallback : sanitized;
    }
}

public sealed class HueCapabilityDetector : IHueCapabilityDetector
{
    private readonly ILogger<HueCapabilityDetector> _logger;

    public HueCapabilityDetector(ILogger<HueCapabilityDetector> logger)
    {
        _logger = logger;
    }

    public async Task<HueBridgeCapabilities> DetectAsync(
        HueBridgeInfo bridge,
        HueCredentials credentials,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(credentials);
        var configPath = $"/api/{Uri.EscapeDataString(credentials.ApplicationKey)}/config";
        using var response = await HueHttp.SendWithFallbackAsync(
            bridge,
            HttpMethod.Get,
            configPath,
            null,
            null,
            _logger,
            cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var config = JsonDocument.Parse(json);
        var modelId = config.RootElement.TryGetProperty("modelid", out var model)
            ? model.GetString()
            : bridge.ModelId;
        var generation = HueJson.GenerationFromModel(modelId);
        var supportsV2 = generation is HueBridgeGeneration.Generation2 or HueBridgeGeneration.BridgePro;
        var supportsEntertainment = supportsV2 && !string.IsNullOrWhiteSpace(credentials.ClientKey);
        var warning = generation == HueBridgeGeneration.Generation1
            ? "Bridge v1 is end-of-support, local-only, and limited to compatibility mode."
            : !supportsEntertainment
                ? "Pair again with entertainment access to enable low-latency streaming."
                : null;
        var capabilities = new HueBridgeCapabilities(
            generation,
            supportsV2,
            supportsEntertainment,
            generation != HueBridgeGeneration.Generation1,
            supportsEntertainment ? 50 : 8,
            warning);
        _logger.LogInformation(
            "Hue bridge capability mode: {Generation}, v2={SupportsV2}, entertainment={Entertainment}.",
            generation,
            supportsV2,
            supportsEntertainment);
        return capabilities;
    }
}

public sealed class HueBridgeClient : IHueBridgeClient
{
    private readonly IHueCapabilityDetector _capabilityDetector;
    private readonly ILogger<HueBridgeClient> _logger;
    private readonly ConcurrentDictionary<string, LightUpdate> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _v1Ids = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _colorSupport = new(StringComparer.Ordinal);
    private HttpClient? _httpClient;
    private HueCredentials? _credentials;
    private CancellationTokenSource? _workerCancellation;
    private Task? _worker;
    private HueUpdateRateLimiter? _rateLimiter;
    private int _updatesPerSecond = 8;

    public HueBridgeClient(
        IHueCapabilityDetector capabilityDetector,
        ILogger<HueBridgeClient> logger)
    {
        _capabilityDetector = capabilityDetector;
        _logger = logger;
    }

    public HueBridgeInfo? Bridge { get; private set; }

    public HueControlMode Mode { get; private set; } = HueControlMode.Disconnected;

    public async Task ConnectAsync(
        HueBridgeInfo bridge,
        HueCredentials credentials,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(credentials);
        await StopWorkerAsync(cancellationToken).ConfigureAwait(false);
        var capabilities = await _capabilityDetector.DetectAsync(
            bridge,
            credentials,
            cancellationToken).ConfigureAwait(false);
        Bridge = bridge;
        _credentials = credentials;
        _updatesPerSecond = Math.Clamp(
            capabilities.RecommendedUpdatesPerSecond,
            1,
            10);
        _rateLimiter = new(_updatesPerSecond);
        _httpClient = HueHttp.CreateClient(
            bridge,
            capabilities.SupportsHttps ? Uri.UriSchemeHttps : Uri.UriSchemeHttp,
            _logger);
        Mode = HueControlMode.Compatibility;
        _workerCancellation = new CancellationTokenSource();
        _worker = Task.Run(
            () => RunOutputWorkerAsync(_workerCancellation.Token),
            CancellationToken.None);
        _logger.LogInformation(
            "Connected Hue compatibility client to {BridgeAddress} at up to {Rate} updates/sec.",
            bridge.InternalIpAddress,
            _updatesPerSecond);
    }

    public async Task<IReadOnlyList<HueLight>> GetLightsAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        IReadOnlyList<HueLight> lights;
        try
        {
            lights = await GetV2LightsAsync(cancellationToken).ConfigureAwait(false);
            if (lights.Count == 0)
            {
                lights = await GetV1LightsAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (
            exception is HttpRequestException or JsonException or HueApiException)
        {
            _logger.LogWarning(exception, "Hue API v2 light listing failed; using API v1.");
            lights = await GetV1LightsAsync(cancellationToken).ConfigureAwait(false);
        }

        _v1Ids.Clear();
        _colorSupport.Clear();
        foreach (var light in lights)
        {
            if (!string.IsNullOrWhiteSpace(light.V1Id))
            {
                _v1Ids[light.Id] = light.V1Id;
            }

            _colorSupport[light.Id] = light.SupportsColor;
        }

        return lights;
    }

    public Task SendUpdatesAsync(
        IReadOnlyList<LightUpdate> updates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(updates);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureConnected();
        foreach (var update in updates)
        {
            _pending[update.LightId] = update;
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await StopWorkerAsync(timeout.Token).ConfigureAwait(false);
        _httpClient?.Dispose();
        _httpClient = null;
        Mode = HueControlMode.Disconnected;
    }

    private async Task<IReadOnlyList<HueLight>> GetV2LightsAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/clip/v2/resource/light");
        request.Headers.TryAddWithoutValidation("hue-application-key", _credentials!.ApplicationKey);
        using var response = await _httpClient!.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var result = new List<HueLight>();
        if (!json.RootElement.TryGetProperty("data", out var data))
        {
            return result;
        }

        foreach (var element in data.EnumerateArray())
        {
            var id = element.GetProperty("id").GetString();
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var idV1 = element.TryGetProperty("id_v1", out var idV1Element)
                ? idV1Element.GetString()?.Split('/').LastOrDefault()
                : null;
            var name = element.TryGetProperty("metadata", out var metadata) &&
                       metadata.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString()
                : null;
            var supportsColor = element.TryGetProperty("color", out _);
            result.Add(new(
                id,
                idV1,
                name ?? $"Light {idV1 ?? id}",
                "Hue",
                true,
                supportsColor,
                Order: result.Count));
        }

        return result;
    }

    private async Task<IReadOnlyList<HueLight>> GetV1LightsAsync(CancellationToken cancellationToken)
    {
        var path = $"/api/{Uri.EscapeDataString(_credentials!.ApplicationKey)}/lights";
        using var response = await _httpClient!.GetAsync(path, cancellationToken).ConfigureAwait(false);
        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(content);
        if (HueJson.TryGetV1Error(json.RootElement, out var error))
        {
            throw new HueApiException(error);
        }

        var result = new List<HueLight>();
        foreach (var property in json.RootElement.EnumerateObject())
        {
            var element = property.Value;
            var name = element.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString() ?? $"Light {property.Name}"
                : $"Light {property.Name}";
            var model = element.TryGetProperty("modelid", out var modelElement)
                ? modelElement.GetString() ?? "Unknown"
                : "Unknown";
            var type = element.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString() ?? string.Empty
                : string.Empty;
            var reachable = !element.TryGetProperty("state", out var state) ||
                            !state.TryGetProperty("reachable", out var reachableElement) ||
                            reachableElement.GetBoolean();
            var supportsColor = type.Contains("color", StringComparison.OrdinalIgnoreCase) ||
                                element.TryGetProperty("capabilities", out var capabilities) &&
                                capabilities.TryGetProperty("control", out var control) &&
                                control.TryGetProperty("colorgamut", out _);
            result.Add(new(
                property.Name,
                property.Name,
                name,
                model,
                reachable,
                supportsColor,
                Order: result.Count));
        }

        return result;
    }

    private async Task RunOutputWorkerAsync(CancellationToken cancellationToken)
    {
        var sent = 0;
        var reportAt = DateTimeOffset.UtcNow.AddSeconds(5);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var update = TakeNextPending();
                if (update is null)
                {
                    await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await _rateLimiter!.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await SendOneAsync(update, cancellationToken).ConfigureAwait(false);
                    sent++;
                }
                catch (Exception exception) when (
                    exception is HttpRequestException or TaskCanceledException or HueApiException)
                {
                    _logger.LogWarning(
                        exception,
                        "Hue update failed for light {LightId}.",
                        update.LightId);
                }

                if (DateTimeOffset.UtcNow >= reportAt)
                {
                    _logger.LogDebug(
                        "Hue compatibility output sent {Count} updates in the last interval; {Pending} coalesced updates pending.",
                        sent,
                        _pending.Count);
                    sent = 0;
                    reportAt = DateTimeOffset.UtcNow.AddSeconds(5);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private LightUpdate? TakeNextPending()
    {
        foreach (var pair in _pending)
        {
            if (_pending.TryRemove(pair.Key, out var update))
            {
                return update;
            }
        }

        return null;
    }

    private async Task SendOneAsync(LightUpdate update, CancellationToken cancellationToken)
    {
        var v1Id = _v1Ids.TryGetValue(update.LightId, out var mappedId)
            ? mappedId
            : update.LightId;
        var brightness = Math.Clamp(update.Brightness, 0, 1);
        var body = new Dictionary<string, object>
        {
            ["on"] = update.TurnOn && brightness > 0.002f,
            ["bri"] = Math.Clamp((int)Math.Round(brightness * 254), 1, 254),
            ["transitiontime"] = Math.Clamp(
                (int)Math.Round(update.Transition.TotalMilliseconds / 100),
                0,
                65535)
        };
        if (!_colorSupport.TryGetValue(update.LightId, out var supportsColor) || supportsColor)
        {
            var (x, y) = HueColorConversion.ToXy(update.Color);
            body["xy"] = new[] { x, y };
        }

        var path = $"/api/{Uri.EscapeDataString(_credentials!.ApplicationKey)}/lights/{Uri.EscapeDataString(v1Id)}/state";
        using var request = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = JsonContent.Create(body)
        };
        using var response = await _httpClient!.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(content);
        if (HueJson.TryGetV1Error(document.RootElement, out var error))
        {
            throw new HueApiException(error);
        }
    }

    private async Task StopWorkerAsync(CancellationToken cancellationToken)
    {
        if (_worker is null)
        {
            return;
        }

        await _workerCancellation!.CancelAsync().ConfigureAwait(false);
        try
        {
            await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_workerCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            _workerCancellation.Dispose();
            _workerCancellation = null;
            _worker = null;
            _pending.Clear();
        }
    }

    private void EnsureConnected()
    {
        if (_httpClient is null || _credentials is null || Bridge is null)
        {
            throw new InvalidOperationException("Connect to a Hue Bridge first.");
        }
    }
}

public static class HueColorConversion
{
    public static (double X, double Y) ToXy(RgbColor color)
    {
        static double Expand(float component) => component > 0.04045f
            ? Math.Pow((component + 0.055f) / 1.055f, 2.4)
            : component / 12.92f;

        var red = Expand(Math.Clamp(color.Red, 0, 1));
        var green = Expand(Math.Clamp(color.Green, 0, 1));
        var blue = Expand(Math.Clamp(color.Blue, 0, 1));
        var x = (red * 0.664511) + (green * 0.154324) + (blue * 0.162028);
        var y = (red * 0.283881) + (green * 0.668433) + (blue * 0.047685);
        var z = (red * 0.000088) + (green * 0.072310) + (blue * 0.986039);
        var sum = x + y + z;
        return sum <= double.Epsilon
            ? (0.3127, 0.3290)
            : (Math.Clamp(x / sum, 0, 1), Math.Clamp(y / sum, 0, 1));
    }
}

internal static class HueHttp
{
    public static HttpClient CreateClient(
        HueBridgeInfo bridge,
        string scheme,
        ILogger logger)
    {
        var handler = new HttpClientHandler();
        if (scheme == Uri.UriSchemeHttps)
        {
            handler.ServerCertificateCustomValidationCallback =
                (_, certificate, chain, errors) =>
                    ValidateBridgeCertificate(bridge, certificate, chain, errors, logger);
        }

        return new HttpClient(handler)
        {
            BaseAddress = new Uri($"{scheme}://{FormatHost(bridge.InternalIpAddress)}"),
            Timeout = TimeSpan.FromSeconds(5)
        };
    }

    public static async Task<HttpResponseMessage> SendWithFallbackAsync(
        HueBridgeInfo bridge,
        HttpMethod method,
        string path,
        HttpContent? content,
        string? applicationKey,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            return await SendAsync(
                bridge,
                Uri.UriSchemeHttps,
                method,
                path,
                content,
                applicationKey,
                logger,
                cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException) when (
            bridge.Generation is HueBridgeGeneration.Generation1 or HueBridgeGeneration.Unknown)
        {
            logger.LogWarning(
                "HTTPS was unavailable on bridge {BridgeId}; falling back to local HTTP for legacy compatibility.",
                bridge.Id);
            return await SendAsync(
                bridge,
                Uri.UriSchemeHttp,
                method,
                path,
                content,
                applicationKey,
                logger,
                cancellationToken).ConfigureAwait(false);
        }
    }

    public static async Task<JsonDocument> GetConfigWithFallbackAsync(
        HueBridgeInfo bridge,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        using var response = await SendWithFallbackAsync(
            bridge,
            HttpMethod.Get,
            "/api/config",
            null,
            null,
            logger,
            cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(json);
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HueBridgeInfo bridge,
        string scheme,
        HttpMethod method,
        string path,
        HttpContent? content,
        string? applicationKey,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(bridge, scheme, logger);
        using var request = new HttpRequestMessage(method, path)
        {
            Content = content
        };
        if (!string.IsNullOrWhiteSpace(applicationKey))
        {
            request.Headers.TryAddWithoutValidation("hue-application-key", applicationKey);
        }

        return await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static bool ValidateBridgeCertificate(
        HueBridgeInfo bridge,
        X509Certificate2? certificate,
        X509Chain? chain,
        SslPolicyErrors errors,
        ILogger logger)
    {
        if (certificate is null ||
            DateTime.UtcNow < certificate.NotBefore.ToUniversalTime() ||
            DateTime.UtcNow > certificate.NotAfter.ToUniversalTime())
        {
            return false;
        }

        if (errors == SslPolicyErrors.None)
        {
            return true;
        }

        var certificateName = NormalizeIdentity(
            certificate.GetNameInfo(X509NameType.DnsName, false));
        var expected = NormalizeIdentity(bridge.Id);
        var identityMatches = expected.Length == 16
            ? string.Equals(certificateName, expected, StringComparison.OrdinalIgnoreCase)
            : certificateName.Length == 16 && certificateName.All(Uri.IsHexDigit);
        var onlyLocalChainIssue = chain is null ||
                                  chain.ChainStatus.All(static status =>
                                      status.Status is X509ChainStatusFlags.UntrustedRoot or
                                          X509ChainStatusFlags.PartialChain or
                                          X509ChainStatusFlags.NoError);
        if (identityMatches && onlyLocalChainIssue)
        {
            logger.LogDebug(
                "Accepted the Hue bridge certificate after exact bridge-identity validation ({BridgeId}).",
                certificateName);
            return true;
        }

        logger.LogWarning(
            "Rejected Hue bridge certificate. Expected identity {Expected}; received {Actual}; errors {Errors}.",
            expected,
            certificateName,
            errors);
        return false;
    }

    private static string NormalizeIdentity(string? value) =>
        new((value ?? string.Empty).Where(Uri.IsHexDigit).ToArray());

    private static string FormatHost(string host) =>
        IPAddress.TryParse(host, out var address) &&
        address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{host}]"
            : host;
}

internal static class HueJson
{
    public static HueBridgeInfo ParseBridgeConfig(
        JsonDocument config,
        string address,
        bool isManual)
    {
        var root = config.RootElement;
        var id = root.TryGetProperty("bridgeid", out var idElement)
            ? idElement.GetString() ?? address
            : address;
        var name = root.TryGetProperty("name", out var nameElement)
            ? nameElement.GetString() ?? $"Hue Bridge ({address})"
            : $"Hue Bridge ({address})";
        var model = root.TryGetProperty("modelid", out var modelElement)
            ? modelElement.GetString()
            : null;
        var version = root.TryGetProperty("swversion", out var versionElement)
            ? versionElement.GetString()
            : null;
        return new(
            id,
            address,
            name,
            model,
            version,
            GenerationFromModel(model),
            isManual);
    }

    public static HueBridgeGeneration GenerationFromModel(string? modelId) =>
        modelId?.ToUpperInvariant() switch
        {
            "BSB001" => HueBridgeGeneration.Generation1,
            "BSB002" => HueBridgeGeneration.Generation2,
            "BSB003" => HueBridgeGeneration.BridgePro,
            _ => HueBridgeGeneration.Unknown
        };

    public static bool TryGetV1Error(JsonElement root, out string message)
    {
        message = string.Empty;
        if (root.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in root.EnumerateArray())
        {
            if (!item.TryGetProperty("error", out var error))
            {
                continue;
            }

            message = error.TryGetProperty("description", out var description)
                ? description.GetString() ?? "Hue API error."
                : "Hue API error.";
            return true;
        }

        return false;
    }
}
