# RhythmLume

RhythmLume is an original Windows music-lighting application for Philips Hue. It captures live PC playback or a microphone/line-in endpoint, performs real-time spectral and onset analysis, and turns kick, bass, frequency balance, and song energy into smooth movement across selected lights.

RhythmLume is independent software and is not affiliated with, endorsed by, or derived from Ambify or Signify. Philips Hue is a trademark of Signify.

## Status

The repository contains a complete .NET 8 WPF MVP:

- WASAPI loopback and microphone/line-in device selection
- live level, five-band FFT spectrum, adaptive normalization, spectral flux, and kick detection
- mDNS plus `discovery.meethue.com` bridge discovery and manual IP fallback
- physical link-button pairing with an Entertainment client key
- Windows DPAPI credential encryption scoped to the signed-in user
- Hue v1 light listing fallback and Hue v2 light/Entertainment resource support
- DTLS 1.2 PSK Entertainment streaming on UDP 2100
- coalesced, rate-limited REST compatibility output for Bridge v1 and constrained setups
- Bass Chase, Bass Pulse, Frequency Split, Energy Wave, and Calm Ambient
- editable palettes, room positions, custom light order, latency offset, safety limits, eight presets, and an in-app/file log
- unit coverage for DSP mapping, smoothing, kick threshold/cooldown, effects, clamping, Hue packet encoding, and rate limiting

Software behavior is covered by automated tests. Actual lamp timing, certificate variants, Entertainment-area ownership, Zigbee reachability, and Bridge v1 firmware behavior require verification on real Hue hardware.

## Requirements

- Windows 10 version 2004 (build 19041) or newer
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build
- a Philips Hue Bridge on the same local network
- color-capable Hue lights for color effects (dimmable lights still receive brightness)
- for fast mode: a Bridge v2/Bridge Pro and an Entertainment area created in the official Hue app

The application runs as the current user and does not require administrator privileges.

## Build and run

From a Developer PowerShell:

```powershell
dotnet restore RhythmLume.sln
dotnet build RhythmLume.sln --configuration Release
dotnet test RhythmLume.sln --configuration Release --no-build
dotnet run --project src/RhythmLume.App/RhythmLume.App.csproj
```

Open `RhythmLume.sln` in Visual Studio 2022 17.8 or newer for the normal WPF development experience.

## First-time setup

1. Open **Setup** and choose a system-playback endpoint or a microphone/line-in endpoint.
2. Start capture and confirm that the header meter and **Live** spectrum move.
3. Select **Discover**. If multicast or broker discovery is blocked, enter the bridge IP shown in the official Hue app.
4. Select the bridge.
5. Press the physical round link button on the bridge.
6. Within 30 seconds, select **Pair bridge**.
7. Select participating lights under **Lights** and assign positions/order.
8. On Bridge v2/Pro, select an Entertainment area under **Advanced**. Create one in the official Hue app first if none is listed.
9. Choose a preset or effect, then select **Start light show**.

Pairing stores the application key and Entertainment client key in `%LOCALAPPDATA%\RhythmLume\credentials`. DPAPI encrypts them for the current Windows user. General settings are JSON in `%LOCALAPPDATA%\RhythmLume\settings.json`; secrets are never written there or to logs.

## Architecture

```text
WASAPI callback
  -> bounded audio queue
  -> overlapping Hann-window FFT + adaptive analysis worker
  -> latest-wins analysis snapshots
  -> effect engine
  -> independent Hue output worker
       -> DTLS Entertainment stream (fast)
       -> coalescing REST queue (compatibility)

WPF UI reads throttled snapshots and never sits in the capture/network path.
```

Projects:

| Project | Responsibility |
| --- | --- |
| `RhythmLume.Core` | Domain models, settings, and interfaces |
| `RhythmLume.Audio.Windows` | NAudio WASAPI device enumeration and capture/sample conversion |
| `RhythmLume.Dsp` | FFT, band integration, normalization, smoothing, spectral flux, kick detection |
| `RhythmLume.Hue` | Discovery, authentication, capability detection, REST client, DTLS stream |
| `RhythmLume.Effects` | Musical color engine, five effects, built-in presets |
| `RhythmLume.Infrastructure` | JSON settings, DPAPI credential store, diagnostics/file logging |
| `RhythmLume.App` | WPF composition, MVVM view models, live orchestration and visualization |
| `RhythmLume.Tests` | Hardware-independent unit tests |

### Audio and DSP pipeline

NAudio opens the selected endpoint in WASAPI shared mode. `WasapiLoopbackCapture` receives the rendered system mix; `WasapiCapture` receives an input endpoint. The capture callback only converts the endpoint’s actual float/PCM format to mono, applies gain/gating, timestamps it, and submits it to a bounded queue.

The DSP worker uses:

- 2048 samples at 44.1/48 kHz
- a 512-sample hop (75% overlap)
- a periodic Hann window
- a radix-2 FFT and one-sided power spectrum
- fractional bin overlap for these required bands:
  - sub bass: 20–60 Hz
  - bass/kick: 60–150 Hz
  - low mids: 150–400 Hz
  - mids: 400–2,000 Hz
  - highs: 2,000–10,000 Hz
- adaptive noise-floor and peak tracking
- separate attack/release envelopes
- positive log-spectral flux
- median/MAD-like adaptive bass history, transient gating, and a 120 ms refractory period

A 2048-point window is a latency/resolution compromise. At 48 kHz its 23.4 Hz bin spacing cannot perfectly isolate the 20–60 Hz boundary; fractional integration reduces boundary jumps, while the combined bass transient signal keeps kick response timely.

### Hue update strategy

**Fast / Entertainment mode**

- Bridge v2/Bridge Pro only
- requests `generateclientkey: true` during physical pairing
- activates a v2 `entertainment_configuration`
- performs DTLS 1.2 with PSK identity = application key, PSK = 16-byte decoded client key
- restricts negotiation to `TLS_PSK_WITH_AES_128_GCM_SHA256`
- sends HueStream v2 RGB frames on UDP port 2100, normally at 50 Hz
- limits a frame to 20 channels and stops the Entertainment area on exit

**Compatibility / REST mode**

- works with the local Hue API v1 exposed by Bridge v1 and newer bridges
- coalesces each light to its newest requested state
- permits only one in-flight output worker
- defaults to 8 individual-light updates/second and hard-caps configuration at 10
- uses smoothed brightness/color transitions
- falls back to local HTTP only for legacy Bridge v1/unknown firmware when HTTPS is unavailable

The audio worker never waits for either path. When networking slows, old visual states are replaced by the newest state rather than replayed late.

### TLS handling

Hue API v2 uses HTTPS. Because local bridge URLs use an IP address while bridge certificates identify the bridge ID, RhythmLume accepts a non-public local chain only when:

- the certificate is currently valid,
- its DNS identity exactly matches the expected 16-hex bridge ID (or a valid bridge ID during manual first contact), and
- chain errors are limited to an untrusted/partial local chain.

The application never uses an unconditional “accept any certificate” callback.

## Bridge compatibility

| Capability | Bridge v1 (`BSB001`) | Bridge v2 (`BSB002`) / Bridge Pro |
| --- | --- | --- |
| Local control | Yes | Yes |
| Cloud/remote support | No; ended April 2020 | Not used by RhythmLume |
| API v1 fallback | Yes | Yes |
| API v2 resources | No | Yes |
| HTTPS | Firmware-dependent | Yes |
| Entertainment streaming | No | Yes, with area + client key |
| RhythmLume mode | Compatibility | Fast preferred, compatibility fallback |

Bridge v1 receives no firmware/security updates. RhythmLume supports it only as a best-effort local compatibility target and deliberately lowers output cadence.

## Presets and effects

- **Bass Chase** — each accepted kick advances one light; strong hits include neighbors.
- **Club Mode / Bass Pulse** — all selected lights pulse with bass strength.
- **Spectrum Split** — ordered thirds respond to bass, mids, and treble.
- **Energy Wave** — song energy moves a traveling wave; kicks advance/intensify it.
- **Ambient Flow / Calm Ambient** — low-flash, long-release movement.
- **Deep Bass Room**, **High Energy**, and **Minimal Flicker** tune those engines for distinct behavior.

Effects consume analysis frames. There are no random beat timers or random color jumps.

## Troubleshooting

### No audio devices

- Confirm the endpoint is enabled in Windows **Settings > System > Sound**.
- Playback endpoints appear as **System playback**; recording endpoints appear as **Microphone**.
- Protected/DRM playback may be unavailable to WASAPI loopback.
- Refresh after connecting a USB interface or changing the Windows default device.

### Meter is silent

- Ensure audio is playing through the exact render endpoint selected in RhythmLume.
- Lower the noise gate or raise input gain.
- For line-in, enable the input endpoint and choose it rather than the playback endpoint.

### Bridge is not discovered

- Keep the PC and bridge on the same LAN/VLAN.
- Permit multicast DNS (UDP 5353) in the local firewall/router.
- Confirm internet access to `https://discovery.meethue.com` or use manual IP.
- Disable client isolation on the Wi-Fi network.

### Pairing says link button was not pressed

Press the physical bridge button immediately before **Pair bridge**. Simulated push-link is intentionally unsupported by Hue. If an old app registration was removed, pair again.

### Fast mode is unavailable

- Bridge v1 cannot stream.
- Create an Entertainment area in the official Hue app.
- Pair again so the bridge returns a `clientkey`.
- Stop another Hue Sync/Entertainment application; a configuration has one active streamer.
- Check local UDP 2100 and HTTPS access to the bridge.

### Lights lag in compatibility mode

This is expected with REST, especially with many lights. RhythmLume caps individual updates to protect the bridge. Use an Entertainment area on Bridge v2/Pro for smooth 25–60 Hz behavior, or select fewer lights and increase release time.

### A light is unreachable or does not change color

Confirm it works in the official Hue app. Zigbee lights repeat traffic, so restore power to intermediate bulbs. Brightness-only devices ignore color while remaining selectable.

### Logs

Open **Logs** in the app. Persistent logs are written to `%LOCALAPPDATA%\RhythmLume\logs`. Application keys and client keys are not logged.

## Hue API research and assumptions

Implementation follows these sources:

- [Hue getting started / local pairing](https://developers.meethue.com/develop/get-started-2/)
- [Hue API v2 announcement and migration guidance](https://developers.meethue.com/new-hue-api/)
- [Bridge v1 end-of-support notice](https://developers.meethue.com/important-news-about-bridge-v1/)
- [Hue developer support / REST update-rate guidance](https://developers.meethue.com/support/)
- [Microsoft WASAPI loopback recording](https://learn.microsoft.com/windows/win32/coreaudio/loopback-recording)

Verified assumptions:

1. Supported bridge discovery is mDNS or `discovery.meethue.com`; UPnP is deprecated.
2. Local registration is `POST /api` and requires the physical link button.
3. API v2 uses HTTPS and the `hue-application-key` header.
4. Bridge v1 is local-only/end-of-support and has no Entertainment streaming.
5. Continuous real-time updates must use Entertainment streaming, not REST.
6. REST should stay around 10 commands/sec for individual lights and 1/sec for groups.
7. Entertainment uses DTLS 1.2 PSK on UDP 2100; bridge Zigbee output is slower than the incoming stream, so latest-state streaming is appropriate.

Hardware-dependent assumptions to verify:

- certificate chain/identity details on each bridge firmware generation
- Bridge v1 HTTPS availability and local HTTP fallback
- exact perceived latency by bulb generation and room topology
- gradient channel membership and gamut behavior
- active-streamer contention with official Hue Sync

## Future AirPlay support

AirPlay should be added as a separate input adapter, not mixed into DSP or Hue code. A future implementation can:

1. receive/decrypt an authorized AirPlay audio session through a maintained protocol component,
2. decode to timestamped PCM,
3. resample/downmix into the existing `AudioChunk` contract,
4. preserve sender clock/timing metadata for latency calibration, and
5. route audio onward independently of analysis.

AirPlay licensing, authentication, encrypted-session interoperability, network jitter buffering, and redistribution terms need separate research. Capturing the Windows output of an existing AirPlay receiver already works through WASAPI loopback without protocol changes.

## License

MIT. See [LICENSE](LICENSE).
