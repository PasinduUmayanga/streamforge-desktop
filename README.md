# StreamForge

[![AppVeyor](https://img.shields.io/appveyor/build/Pasindu/streamforge-desktop?logo=appveyor)](https://ci.appveyor.com/project/Pasindu/streamforge-desktop)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Windows App SDK](https://img.shields.io/nuget/v/Microsoft.WindowsAppSDK?label=Windows%20App%20SDK&logo=windows)](https://www.nuget.org/packages/Microsoft.WindowsAppSDK)
[![Playwright](https://img.shields.io/nuget/v/Microsoft.Playwright?label=Microsoft.Playwright)](https://www.nuget.org/packages/Microsoft.Playwright)
[![MVVM Toolkit](https://img.shields.io/nuget/v/CommunityToolkit.Mvvm?label=CommunityToolkit.Mvvm)](https://www.nuget.org/packages/CommunityToolkit.Mvvm)
[![xUnit](https://img.shields.io/nuget/v/xunit?label=xUnit)](https://www.nuget.org/packages/xunit)

StreamForge is a Windows desktop application for detecting authorized non-DRM media streams from supported video pages and remuxing them to local MP4 files with FFmpeg.

## Requirements

- .NET 10 SDK
- Visual Studio 2026 with WinUI / Windows App SDK desktop build tooling
- FFmpeg available from `PATH` for development
- Playwright Chromium installed for the app runtime

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

```powershell
dotnet restore
dotnet build
dotnet test
```

To run the desktop app:

```powershell
dotnet build src\StreamForge.App\StreamForge.App.csproj
dotnet run --project src\StreamForge.App\StreamForge.App.csproj
```

StreamForge does not implement DRM circumvention, key extraction, or authentication bypass.
