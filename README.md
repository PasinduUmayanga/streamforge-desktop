# StreamForge

[![Build status](https://ci.appveyor.com/api/projects/status/4wcjmfdh9g04lfe0?svg=true)](https://ci.appveyor.com/project/Mahadenamuththa/streamforge-desktop)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Windows App SDK](https://img.shields.io/nuget/v/Microsoft.WindowsAppSDK?label=Windows%20App%20SDK&logo=windows)](https://www.nuget.org/packages/Microsoft.WindowsAppSDK)
[![Playwright](https://img.shields.io/nuget/v/Microsoft.Playwright?label=Microsoft.Playwright)](https://www.nuget.org/packages/Microsoft.Playwright)
[![MVVM Toolkit](https://img.shields.io/nuget/v/CommunityToolkit.Mvvm?label=MVVM%20Toolkit)](https://www.nuget.org/packages/CommunityToolkit.Mvvm)
[![xUnit](https://img.shields.io/nuget/v/xunit?label=xUnit)](https://www.nuget.org/packages/xunit)

StreamForge is a Windows desktop application that detects authorized, non-DRM media streams used by supported video pages and remuxes them into local MP4 files with FFmpeg.

Only download media that you own or are authorized to save. StreamForge does not implement DRM circumvention, key extraction, paywall bypass, or authentication bypass.

## Current features

- .NET 10 and WinUI 3 desktop application with an MVVM presentation layer.
- Responsive single-column and two-column layouts for narrow, medium, and wide windows.
- Live network-analysis panel with redacted query-string values.
- JavaScript page loading and request observation through Playwright Chromium.
- Detection from request URLs, response content types, textual API responses, performance entries, player APIs, and HTML media elements.
- Dynamic iframe and DooPlay-style server discovery.
- HLS `.m3u8`, MPEG-DASH `.mpd`, and direct `.mp4` source detection.
- HLS master-playlist parsing with relative URL resolution and quality selection.
- FFmpeg remuxing with the detected User-Agent, Referer, and Origin context.
- Download percentage, bytes downloaded, current speed, elapsed time, and estimated time remaining.
- Download cancellation and Windows process-level pause and resume.
- Automatic Playwright Chromium setup and optional project-local FFmpeg setup.
- Application and taskbar icon.
- Sensitive-header filtering and DRM detection.

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

## Application flow

```text
Video page URL
      ↓
Playwright loads the page and observes requests
      ↓
Player APIs, HTML media, iframes, and API responses are inspected
      ↓
Supported non-DRM media candidates are ranked
      ↓
HLS qualities are analyzed
      ↓
User selects a quality and output path
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

The UI does not contain stream-extraction or FFmpeg process logic. Core contracts isolate the presentation layer from the Playwright, HTTP, and process implementations.

## Requirements

- Windows 10 version 1809 or later
- x64 environment
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
dotnet test tests\StreamForge.Infrastructure.Tests\StreamForge.Infrastructure.Tests.csproj
```

To build only the desktop application:

```powershell
dotnet build src\StreamForge.App\StreamForge.App.csproj
```

## Run

```powershell
dotnet run --project src\StreamForge.App\StreamForge.App.csproj
```

Then:

1. Paste a supported video-page URL.
2. Select **Analyze** and review the redacted network activity.
3. Select an available quality.
4. Enter the destination MP4 path.
5. Select **Download**.
6. Use **Pause**, **Resume**, or **Cancel** when necessary.

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

## Continuous integration

AppVeyor installs .NET 10, prepares Playwright and FFmpeg, builds the x64 Release configuration, and runs the infrastructure test suite. The `StreamForge-win-x64` artifact is published only after the test gate succeeds.

The pipeline caches the .NET SDK, Playwright browser, project-local FFmpeg, and—when it fits the account cache limit—NuGet packages. AppVeyor may skip an oversized NuGet cache without failing an otherwise successful build.

## Current limitations

- Windows x64 is the only packaged target.
- HLS quality selection is implemented; DASH quality selection is not yet implemented.
- Only one download can run at a time.
- Download history, queues, resume-after-restart, subtitles, alternate audio tracks, and automatic updates are not implemented.
- Some sites can prevent analysis through authentication, bot challenges, unsupported encryption, short-lived authorization, or private player protocols.
- External websites can change without notice; extraction improvements should remain generic and must not hard-code signed media URLs.

See [agent.md](agent.md) before implementing or reviewing feature updates.
