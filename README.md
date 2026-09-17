# StreamForge

[![Build status](https://ci.appveyor.com/api/projects/status/4wcjmfdh9g04lfe0?svg=true)](https://ci.appveyor.com/project/Mahadenamuththa/streamforge-desktop)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Windows App SDK](https://img.shields.io/nuget/v/Microsoft.WindowsAppSDK?label=Windows%20App%20SDK&logo=windows)](https://www.nuget.org/packages/Microsoft.WindowsAppSDK)
[![Playwright](https://img.shields.io/nuget/v/Microsoft.Playwright?label=Microsoft.Playwright)](https://www.nuget.org/packages/Microsoft.Playwright)
[![MVVM Toolkit](https://img.shields.io/nuget/v/CommunityToolkit.Mvvm?label=CommunityToolkit.Mvvm)](https://www.nuget.org/packages/CommunityToolkit.Mvvm)
[![xUnit](https://img.shields.io/nuget/v/xunit?label=xUnit)](https://www.nuget.org/packages/xunit)

StreamForge is a Windows desktop application for detecting authorized non-DRM media streams from supported video pages and remuxing them to local MP4 files with FFmpeg.

## Requirements

- .NET 10 SDK
- Visual Studio 2026 with WinUI / Windows App SDK desktop build tooling

FFmpeg and Playwright Chromium can be installed automatically by the repository setup script. An existing FFmpeg installation from `STREAMFORGE_FFMPEG` or `PATH` is reused.

For WinUI builds, Visual Studio must include the components that provide:

- Windows App SDK / WinUI tooling
- MSIX Packaging Tools
- Universal Windows Platform build tools
- Windows 10 or Windows 11 SDK
- MSVC C++ build tools

If `dotnet build src\StreamForge.App\StreamForge.App.csproj` fails looking for
`Microsoft.Build.AppxPackage.dll` or `Microsoft.Build.Packaging.Pri.Tasks.dll`,
open Visual Studio Installer, modify Visual Studio 2026, and install the WinUI,
UWP, MSIX packaging, Windows SDK, and C++ desktop build components.

## Development

Install missing dependencies, restore NuGet packages, and prepare the Playwright browser:

```powershell
pwsh -NoProfile -File scripts\setup.ps1
```

To force a project-local FFmpeg installation that will be copied into the application output:

```powershell
pwsh -NoProfile -File scripts\setup.ps1 -ForceLocalFfmpeg
```

The setup script:

- verifies that a .NET 10 SDK is available;
- restores all NuGet packages;
- builds the Playwright installer and installs Chromium only when needed;
- reuses FFmpeg from the project tools directory, `STREAMFORGE_FFMPEG`, or `PATH`;
- downloads the FFmpeg Windows essentials build when FFmpeg is missing;
- verifies the FFmpeg archive against its published SHA-256 checksum.

Playwright also performs a one-time Chromium installation automatically when Analyze is used and its expected browser is missing.

After setup:

```powershell
dotnet build StreamForge.sln /p:Platform=x64
dotnet test tests\StreamForge.Infrastructure.Tests\StreamForge.Infrastructure.Tests.csproj
```

To run the desktop app:

```powershell
dotnet run --project src\StreamForge.App\StreamForge.App.csproj
```

Local dependency downloads are stored under `.tools` and are excluded from Git. AppVeyor caches the .NET SDK, NuGet packages, Playwright Chromium, and the project-local FFmpeg binary. CI creates the `StreamForge-win-x64` publish artifact only after the Release build and test suite pass.

StreamForge does not implement DRM circumvention, key extraction, or authentication bypass.
