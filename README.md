# StreamForge

[![Build status](https://ci.appveyor.com/api/projects/status/4wcjmfdh9g04lfe0?svg=true)](https://ci.appveyor.com/project/Mahadenamuththa/streamforge-desktop)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Windows App SDK](https://img.shields.io/nuget/v/Microsoft.WindowsAppSDK?label=Windows%20App%20SDK&logo=windows)](https://www.nuget.org/packages/Microsoft.WindowsAppSDK)
[![Playwright](https://img.shields.io/nuget/v/Microsoft.Playwright?label=Microsoft.Playwright)](https://www.nuget.org/packages/Microsoft.Playwright)
[![MVVM Toolkit](https://img.shields.io/nuget/v/CommunityToolkit.Mvvm?label=MVVM%20Toolkit)](https://www.nuget.org/packages/CommunityToolkit.Mvvm)
[![xUnit](https://img.shields.io/nuget/v/xunit?label=xUnit)](https://www.nuget.org/packages/xunit)

StreamForge is a Windows desktop application that detects authorized, non-DRM media streams used by supported video pages, or accepts direct HLS/DASH/MP4 media URLs, and remuxes them into local MP4 files with FFmpeg.

Only download media that you own or are authorized to save. StreamForge does not implement DRM circumvention, key extraction, paywall bypass, or authentication bypass.

## Current features

- .NET 10 and WinUI 3 desktop application with an MVVM presentation layer.
- Separate `win-x86` and `win-x64` application artifacts for 32-bit and 64-bit app processes on 64-bit Windows.
- Responsive single-column and two-column controls for narrow, medium, and wide windows.
- Full-width adaptive network-analysis panel with wrapping request details and redacted query-string values.
- Live ordered identification checklist showing Waiting, Identifying, Success, Skipped, and Failed states.
- Operation-aware controls that lock page and output inputs and prevent duplicate analysis/download starts.
- JavaScript page loading and request observation through Playwright Chromium.
- Detection from request URLs, response content types, textual API responses, performance entries, player APIs, and HTML media elements.
- Dynamic iframe and DooPlay-style server discovery.
- Bounded discovery and probing of player, iframe, metadata, and likely GET API seed links.
- Direct HLS `.m3u8`, MPEG-DASH `.mpd`, and `.mp4` URLs can be analyzed without browser page loading.
- HLS `.m3u8`, MPEG-DASH `.mpd`, and direct `.mp4` source detection.
- HLS master-playlist parsing with relative URL resolution and quality selection.
- FFmpeg remuxing with the detected User-Agent, Referer, and Origin context.
- Download percentage, bytes downloaded, current speed, elapsed time, and estimated time remaining.
- Download cancellation and Windows process-level pause and resume.
- Automatic Playwright Chromium setup and optional project-local FFmpeg setup.
- Application and taskbar icon.
- Sensitive-header filtering and DRM detection.
- Explicit diagnostics for authentication responses, bot challenges, expired authorization, DRM indicators, timeouts, and unsupported blob-backed protocols.
- Optional loopback-only Qwen3-Coder-Next analysis of sanitized JSON response structure when deterministic extraction finds no stream.

## Player detection

Network interception remains the primary strategy. StreamForge also recognizes public source information exposed by:

- JW Player
- Video.js
- Shaka Player
- Clappr
- Flowplayer
- Plyr
- Fluid Player
- MediaElement.js
- OpenPlayerJS
- ArtPlayer
- DPlayer
- Generic HTML5 `<video>` and `<source>` elements

Player versions that keep configuration private can still be detected when their normal media requests expose a supported URL or media content type.

## Optional local AI diagnostics

StreamForge can ask a locally running Qwen3-Coder-Next model through an Ollama-compatible local endpoint to classify unfamiliar JSON player-response structures after all normal extraction strategies fail. This feature is disabled by default and is advisory only. All AI-specific implementation lives in the `StreamForge.AiWorker` project; extraction code talks to it only through the `IAiExtractionAdvisor` Core interface.

Install Ollama separately, then pull the model shown by default in the application:

```powershell
ollama pull qwen3-coder-next
```

Start Ollama, enable **Use local AI diagnostics**, and keep the endpoint set to `http://localhost:11434`. StreamForge rejects non-loopback AI endpoints so analysis metadata is not sent to a remote service. The model name can be changed to another model already installed in Ollama.

The advisor receives only:

- aliased host names;
- URL paths and query-parameter names with all values redacted;
- request method, response status, resource type, and content type; and
- JSON pointers and primitive value types, without ordinary string values.

It never receives cookies, authorization headers, signed query values, page titles, or complete response bodies. A suggestion must identify an exact captured JSON field with at least the required confidence, and StreamForge then performs an independent HTTP/media probe. Model output cannot execute JavaScript, commands, authentication flows, CAPTCHA actions, or DRM operations.

Ollama and model files are optional external dependencies. They are not automatically installed, bundled, cached, or run by AppVeyor. Review the license and hardware requirements of any model you select.

## Application flow

```text
Step 1: paste video page URL or direct media URL and configure optional local AI
      ↓
Step 2: watch analysis progress with identification steps and network calls side by side
      ↓
Playwright loads the page and observes requests
      ↓
Player APIs, HTML media, iframes, and API responses are inspected
      ↓
Player/embed/API seed links are collected and probed when no direct media request is available
      ↓
Optional local AI classifies sanitized response structure if normal detection fails
      ↓
Supported non-DRM media candidates are ranked
      ↓
Step 3: HLS qualities are analyzed and selected
      ↓
Step 4: user confirms output path and starts FFmpeg
      ↓
Step 5: download progress, pause, resume, and cancel controls
      ↓
FFmpeg downloads/remuxes the stream
      ↓
Progress, speed, elapsed time, and remaining time are displayed
      ↓
Local MP4 output
```

## Repository structure

```text
StreamForge/
├── src/
│   ├── StreamForge.App/             WinUI views, ViewModel, and dependency injection
│   ├── StreamForge.AiWorker/        Optional local Qwen/Ollama AI advisor implementation
│   ├── StreamForge.Core/            Models and service interfaces
│   └── StreamForge.Infrastructure/  Extraction, stream analysis, and FFmpeg services
├── tests/
│   ├── StreamForge.Core.Tests/
│   └── StreamForge.Infrastructure.Tests/
├── scripts/
│   └── setup.ps1                    Dependency bootstrap script
├── agent.md                         Feature-development guidance
├── appveyor.yml                     Build, test, and publish pipeline
└── StreamForge.sln
```

The UI does not contain stream-extraction, AI-classification, or FFmpeg process logic. Core contracts isolate the presentation layer from the Playwright, local AI, HTTP, and process implementations.

## Requirements

- Windows 10 version 1809 or later
- 64-bit Windows environment; the x86 artifact runs as a 32-bit process through WOW64
- .NET 10 SDK
- PowerShell 7
- Visual Studio 2026 with WinUI and Windows desktop build tooling

The Visual Studio installation should include:

- Windows App SDK / WinUI tooling
- MSIX Packaging Tools
- Universal Windows Platform build tools
- Windows 10 or Windows 11 SDK
- MSVC C++ desktop build tools

If a build cannot find `Microsoft.Build.AppxPackage.dll`, `Microsoft.Build.Packaging.Pri.Tasks.dll`, or an MSVC tools directory, open Visual Studio Installer, modify the installation, and add the components listed above.

## Setup

From the repository root, run:

```powershell
pwsh -NoProfile -File scripts\setup.ps1
```

The setup script:

- verifies that a .NET 10 SDK is available;
- restores NuGet packages;
- builds the infrastructure project so the Playwright installer is generated;
- installs Playwright Chromium when required;
- looks for FFmpeg in `.tools`, `STREAMFORGE_FFMPEG`, and `PATH`;
- downloads a project-local FFmpeg Windows essentials build when FFmpeg is missing; and
- verifies downloaded FFmpeg archives against their published SHA-256 checksum.

To force a project-local FFmpeg installation that is copied into publish output:

```powershell
pwsh -NoProfile -File scripts\setup.ps1 -ForceLocalFfmpeg
```

Useful setup options are:

```powershell
pwsh -NoProfile -File scripts\setup.ps1 -SkipRestore
pwsh -NoProfile -File scripts\setup.ps1 -SkipPlaywright
pwsh -NoProfile -File scripts\setup.ps1 -SkipFfmpeg
```

When Analyze is first used, the application also attempts a one-time Chromium installation if the expected Playwright browser is missing.

## Build and test

```powershell
dotnet build StreamForge.sln /p:Platform=x64
dotnet build StreamForge.sln /p:Platform=x86
dotnet test tests\StreamForge.Infrastructure.Tests\StreamForge.Infrastructure.Tests.csproj
```

To build only the desktop application:

```powershell
dotnet build src\StreamForge.App\StreamForge.App.csproj /p:Platform=x64
dotnet build src\StreamForge.App\StreamForge.App.csproj /p:Platform=x86
```

To publish both architecture-specific outputs locally:

```powershell
dotnet publish src\StreamForge.App\StreamForge.App.csproj -c Release -r win-x64 /p:Platform=x64 -o artifacts\StreamForge-win-x64
dotnet publish src\StreamForge.App\StreamForge.App.csproj -c Release -r win-x86 /p:Platform=x86 -o artifacts\StreamForge-win-x86
```

## Run

```powershell
dotnet run --project src\StreamForge.App\StreamForge.App.csproj
```

Then:

1. Use **Step 1: Setup** to paste a supported video-page URL or direct HLS/DASH/MP4 URL and configure optional local AI diagnostics.
2. Use **Step 2: Analysis** to watch identification steps and relevant network calls side by side while analysis runs.
3. Use **Step 3: Select stream** to review the detected source, request context, and quality.
4. Use **Step 4: Download options** to confirm the MP4 path and start FFmpeg.
5. Use **Step 5: Progress** to watch progress or use **Pause**, **Resume**, and **Cancel**.

The output path is editable. In the current milestone, **Browse** resets it to a generated filename in the Windows Videos folder rather than opening the native save picker.

## FFmpeg resolution order

At runtime StreamForge checks:

1. `ffmpeg\ffmpeg.exe` beside the application;
2. the path in `STREAMFORGE_FFMPEG`; and
3. `ffmpeg.exe` available through `PATH`.

Arguments are passed with `ProcessStartInfo.ArgumentList`; page URLs and output paths are not concatenated into a shell command.

## Privacy and security

- Authorization, Cookie, Set-Cookie, and Proxy-Authorization headers are excluded from captured application data.
- Query-string values are redacted in the network activity display.
- The application may retain non-sensitive request context needed by FFmpeg, such as User-Agent, Referer, and Origin.
- DRM-marked streams are rejected; StreamForge does not retrieve or decrypt keys.
- Signed media URLs are temporary and should not be copied into logs, tests, documentation, or source code.
- Local AI is off by default, accepts loopback endpoints only, and receives structural metadata rather than response values.
- Every AI-suggested field is resolved from the original in-memory response and independently validated as HLS, DASH, or MP4 before use.
- Seed probing is limited to page-exposed HTTP(S) links, eight requests, one nested level, and a short overall time budget.
- Seed responses must resolve to a supported media MIME/body signature or expose an explicit HLS, DASH, or MP4 URL.

## Continuous integration

AppVeyor installs .NET 10, prepares Playwright and FFmpeg, builds separate x86 and x64 Release jobs, and runs the infrastructure test suite, including the `StreamForge.AiWorker` tests referenced by that suite. AI tests use an in-memory fake HTTP service, so CI neither downloads a model nor requires Ollama. The `StreamForge-win-x86` and `StreamForge-win-x64` artifacts are published only after the test gate succeeds for their respective jobs.

The pipeline caches the .NET SDK, Playwright browser, project-local FFmpeg, and—when it fits the account cache limit—NuGet packages. AppVeyor may skip an oversized NuGet cache without failing an otherwise successful build.

## Current limitations

- The application is packaged for x86 and x64, but the Playwright Chromium and downloaded FFmpeg tools are 64-bit. Consequently, the x86 application artifact is intended for 64-bit Windows running a 32-bit app process; native 32-bit Windows is not supported.
- HLS quality selection is implemented; DASH quality selection is not yet implemented.
- Only one download can run at a time.
- Download history, queues, resume-after-restart, subtitles, alternate audio tracks, and automatic updates are not implemented.
- Authentication and bot challenges are identified more clearly, but StreamForge does not automate sign-in or challenge bypasses.
- Local Qwen3-Coder-Next analysis can help identify unfamiliar response fields; it cannot supply access rights, renew authorization, infer secret protocols, solve challenges, or process DRM.
- Short-lived authorization can still expire between page analysis and FFmpeg startup. Retry analysis while the authorized page session is active.
- POST-only or private binary player protocols cannot be replayed as generic seed links and remain unsupported unless their normal network traffic exposes standard media.
- External websites can change without notice; extraction improvements should remain generic and must not hard-code signed media URLs.

See [agent.md](agent.md) before implementing or reviewing feature updates.
