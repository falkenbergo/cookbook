using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RhythmLume.Audio.Windows;
using RhythmLume.Core;
using RhythmLume.Dsp;
using RhythmLume.Effects;
using RhythmLume.Hue;
using RhythmLume.Infrastructure;

namespace RhythmLume.App;

public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs eventArgs)
    {
        base.OnStartup(eventArgs);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        _host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.SetMinimumLevel(LogLevel.Debug);
                logging.AddDebug();
            })
            .ConfigureServices(services =>
            {
                services.AddSingleton<IDiagnosticsSink, DiagnosticsSink>();
                services.AddSingleton<ILoggerProvider, DiagnosticsLoggerProvider>();
                services.AddSingleton(new HttpClient
                {
                    Timeout = TimeSpan.FromSeconds(6)
                });

                services.AddSingleton(new AnalysisOptions());
                services.AddSingleton<IBeatDetector>(provider =>
                {
                    var options = provider.GetRequiredService<AnalysisOptions>();
                    return new AdaptiveBassDetector(
                        cooldownMs: options.BeatCooldownMs,
                        noiseFloor: options.NoiseFloor);
                });
                services.AddSingleton<IAudioAnalyzer, RealTimeAudioAnalyzer>();
                services.AddSingleton<IAudioDeviceService, WindowsAudioDeviceService>();
                services.AddSingleton<IAudioInput, WasapiAudioInput>();

                services.AddSingleton<IHueBridgeDiscovery, HueBridgeDiscovery>();
                services.AddSingleton<IHueAuthenticator, HueAuthenticator>();
                services.AddSingleton<IHueCapabilityDetector, HueCapabilityDetector>();
                services.AddSingleton<IHueBridgeClient, HueBridgeClient>();
                services.AddSingleton<IHueStreamingClient, HueStreamingClient>();

                services.AddSingleton<IColorEngine, MusicalColorEngine>();
                services.AddSingleton<ILightEffectEngine, MusicalLightEffectEngine>();
                services.AddSingleton<IPresetStore, BuiltInPresetStore>();
                services.AddSingleton<ISettingsStore, JsonSettingsStore>();
                services.AddSingleton<ICredentialStore, DpapiCredentialStore>();

                services.AddSingleton<MusicOrchestrator>();
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<MainWindow>();
            })
            .Build();

        try
        {
            await _host.StartAsync().ConfigureAwait(true);
            var window = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();
            await _host.Services.GetRequiredService<MainViewModel>()
                .InitializeAsync()
                .ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"RhythmLume could not start.\n\n{exception.Message}",
                "RhythmLume startup error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs eventArgs)
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        if (_host is not null)
        {
            try
            {
                _host.Services.GetRequiredService<MainViewModel>()
                    .ShutdownAsync()
                    .GetAwaiter()
                    .GetResult();
                _host.StopAsync(TimeSpan.FromSeconds(4))
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception)
            {
                // The process is exiting; diagnostics may already be unavailable.
            }
            finally
            {
                _host.Dispose();
            }
        }

        base.OnExit(eventArgs);
    }

    private static void OnDispatcherUnhandledException(
        object sender,
        System.Windows.Threading.DispatcherUnhandledExceptionEventArgs eventArgs)
    {
        MessageBox.Show(
            eventArgs.Exception.Message,
            "RhythmLume error",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        eventArgs.Handled = true;
    }
}
