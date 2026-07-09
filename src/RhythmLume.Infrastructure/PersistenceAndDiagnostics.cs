using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using RhythmLume.Core;

namespace RhythmLume.Infrastructure;

public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _settingsPath;
    private readonly ILogger<JsonSettingsStore> _logger;

    public JsonSettingsStore(ILogger<JsonSettingsStore> logger)
        : this(GetDefaultDataDirectory(), logger)
    {
    }

    public JsonSettingsStore(string dataDirectory, ILogger<JsonSettingsStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _settingsPath = Path.Combine(dataDirectory, "settings.json");
        _logger = logger;
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_settingsPath))
        {
            return new();
        }

        try
        {
            await using var stream = File.OpenRead(_settingsPath);
            return await JsonSerializer.DeserializeAsync<AppSettings>(
                    stream,
                    SerializerOptions,
                    cancellationToken)
                .ConfigureAwait(false) ?? new();
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                exception,
                "Settings could not be loaded from {SettingsPath}; defaults will be used.",
                _settingsPath);
            return new();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var directory = Path.GetDirectoryName(_settingsPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = _settingsPath + ".tmp";
        await using (var stream = new FileStream(
                         temporaryPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         16 * 1024,
                         FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(
                    stream,
                    settings,
                    SerializerOptions,
                    cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporaryPath, _settingsPath, true);
    }

    public static string GetDefaultDataDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RhythmLume");
}

public sealed class DpapiCredentialStore : ICredentialStore
{
    private static readonly byte[] Entropy =
        SHA256.HashData(Encoding.UTF8.GetBytes("RhythmLume.HueCredentials.v1"));
    private readonly string _directory;
    private readonly ILogger<DpapiCredentialStore> _logger;

    public DpapiCredentialStore(ILogger<DpapiCredentialStore> logger)
        : this(Path.Combine(JsonSettingsStore.GetDefaultDataDirectory(), "credentials"), logger)
    {
    }

    public DpapiCredentialStore(string directory, ILogger<DpapiCredentialStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        _logger = logger;
    }

    public async Task SaveAsync(
        string bridgeId,
        HueCredentials credentials,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bridgeId);
        ArgumentNullException.ThrowIfNull(credentials);
        EnsureWindows();
        Directory.CreateDirectory(_directory);
        var clearText = JsonSerializer.SerializeToUtf8Bytes(credentials);
        try
        {
            var encrypted = ProtectedData.Protect(
                clearText,
                Entropy,
                DataProtectionScope.CurrentUser);
            await File.WriteAllBytesAsync(
                    GetPath(bridgeId),
                    encrypted,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clearText);
        }
    }

    public async Task<HueCredentials?> LoadAsync(
        string bridgeId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bridgeId);
        EnsureWindows();
        var path = GetPath(bridgeId);
        if (!File.Exists(path))
        {
            return null;
        }

        byte[]? clearText = null;
        try
        {
            var encrypted = await File.ReadAllBytesAsync(path, cancellationToken)
                .ConfigureAwait(false);
            clearText = ProtectedData.Unprotect(
                encrypted,
                Entropy,
                DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<HueCredentials>(clearText);
        }
        catch (Exception exception) when (
            exception is CryptographicException or JsonException or IOException)
        {
            _logger.LogWarning(
                exception,
                "Saved Hue credentials for bridge {BridgeId} could not be decrypted.",
                bridgeId);
            return null;
        }
        finally
        {
            if (clearText is not null)
            {
                CryptographicOperations.ZeroMemory(clearText);
            }
        }
    }

    public Task RemoveAsync(string bridgeId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bridgeId);
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetPath(bridgeId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private string GetPath(string bridgeId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(bridgeId.ToUpperInvariant()));
        return Path.Combine(_directory, $"{Convert.ToHexString(hash)}.dat");
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Hue credentials use Windows DPAPI and can only be accessed on Windows.");
        }
    }
}

public sealed class DiagnosticsSink : IDiagnosticsSink
{
    private const int MaximumEntries = 1_000;
    private readonly object _sync = new();
    private readonly List<DiagnosticEntry> _entries = [];

    public event Action<DiagnosticEntry>? EntryAdded;

    public ReadOnlyCollection<DiagnosticEntry> Entries
    {
        get
        {
            lock (_sync)
            {
                return _entries.ToList().AsReadOnly();
            }
        }
    }

    public void Write(string level, string source, string message)
    {
        var entry = new DiagnosticEntry(
            DateTimeOffset.Now,
            level,
            source,
            message);
        lock (_sync)
        {
            _entries.Add(entry);
            if (_entries.Count > MaximumEntries)
            {
                _entries.RemoveRange(0, _entries.Count - MaximumEntries);
            }
        }

        EntryAdded?.Invoke(entry);
    }
}

public sealed class DiagnosticsLoggerProvider : ILoggerProvider
{
    private readonly IDiagnosticsSink _sink;
    private readonly Channel<string> _fileLines;
    private readonly Task _writer;
    private readonly string _logPath;

    public DiagnosticsLoggerProvider(IDiagnosticsSink sink)
    {
        _sink = sink;
        var logDirectory = Path.Combine(
            JsonSettingsStore.GetDefaultDataDirectory(),
            "logs");
        Directory.CreateDirectory(logDirectory);
        _logPath = Path.Combine(logDirectory, $"rhythmlume-{DateTime.UtcNow:yyyyMMdd}.log");
        _fileLines = Channel.CreateBounded<string>(new BoundedChannelOptions(2_000)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        _writer = Task.Run(WriteLogAsync);
    }

    public ILogger CreateLogger(string categoryName) =>
        new DiagnosticsLogger(categoryName, _sink, _fileLines.Writer);

    public void Dispose()
    {
        _fileLines.Writer.TryComplete();
        try
        {
            _writer.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
        }
    }

    private async Task WriteLogAsync()
    {
        try
        {
            await using var stream = new FileStream(
                _logPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                16 * 1024,
                FileOptions.Asynchronous);
            await using var writer = new StreamWriter(stream);
            await foreach (var line in _fileLines.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                await writer.WriteLineAsync(line).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
            // The in-app diagnostic sink remains available if disk logging fails.
        }
    }

    private sealed class DiagnosticsLogger : ILogger
    {
        private readonly string _category;
        private readonly IDiagnosticsSink _sink;
        private readonly ChannelWriter<string> _fileWriter;

        public DiagnosticsLogger(
            string category,
            IDiagnosticsSink sink,
            ChannelWriter<string> fileWriter)
        {
            _category = category;
            _sink = sink;
            _fileWriter = fileWriter;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            if (exception is not null)
            {
                message = $"{message} | {exception.GetType().Name}: {exception.Message}";
            }

            var source = _category.Split('.').LastOrDefault() ?? _category;
            _sink.Write(logLevel.ToString(), source, message);
            _fileWriter.TryWrite(
                $"{DateTimeOffset.Now:O} [{logLevel}] {_category}: {message}");
        }
    }
}
