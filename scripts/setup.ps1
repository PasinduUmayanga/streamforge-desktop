[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [ValidateSet("x86", "x64")]
    [string]$Platform = "x64",

    [switch]$SkipRestore,
    [switch]$SkipPlaywright,
    [switch]$SkipFfmpeg,
    [switch]$ForceLocalFfmpeg
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$solutionPath = Join-Path $repositoryRoot "StreamForge.sln"
$infrastructureProject = Join-Path $repositoryRoot "src\StreamForge.Infrastructure\StreamForge.Infrastructure.csproj"
$toolsDirectory = Join-Path $repositoryRoot ".tools"
$localFfmpegDirectory = Join-Path $toolsDirectory "ffmpeg"
$localFfmpegPath = Join-Path $localFfmpegDirectory "ffmpeg.exe"

function Write-Step([string]$Message) {
    Write-Host "`n==> $Message" -ForegroundColor Cyan
}

function Assert-DotNet10 {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        throw ".NET 10 SDK was not found. Install it from https://dotnet.microsoft.com/download/dotnet/10.0"
    }

    $sdkVersions = @(dotnet --list-sdks | ForEach-Object { ($_ -split '\s+')[0] })
    if (-not ($sdkVersions | Where-Object { $_ -match '^10\.' })) {
        throw ".NET 10 SDK was not found. Installed SDKs: $($sdkVersions -join ', ')"
    }
}

function Find-ExistingFfmpeg {
    if (Test-Path -LiteralPath $localFfmpegPath) {
        return $localFfmpegPath
    }

    $configured = $env:STREAMFORGE_FFMPEG
    if (-not [string]::IsNullOrWhiteSpace($configured) -and (Test-Path -LiteralPath $configured)) {
        return $configured
    }

    $fromPath = Get-Command ffmpeg -ErrorAction SilentlyContinue
    if ($fromPath) {
        return $fromPath.Source
    }

    return $null
}

function Install-LocalFfmpeg {
    $archiveUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip"
    $checksumUrl = "$archiveUrl.sha256"
    $archivePath = Join-Path $toolsDirectory "ffmpeg-release-essentials.zip"
    $extractDirectory = Join-Path $toolsDirectory "ffmpeg-extract"

    New-Item -ItemType Directory -Force -Path $toolsDirectory | Out-Null
    Invoke-WebRequest -Uri $archiveUrl -OutFile $archivePath -UseBasicParsing
    $expectedHash = (Invoke-RestMethod -Uri $checksumUrl).Trim().Split()[0].ToLowerInvariant()
    $actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $expectedHash) {
        Remove-Item -LiteralPath $archivePath -Force
        throw "FFmpeg archive checksum verification failed. Expected $expectedHash but received $actualHash."
    }

    if (Test-Path -LiteralPath $extractDirectory) {
        Remove-Item -LiteralPath $extractDirectory -Recurse -Force
    }

    Expand-Archive -LiteralPath $archivePath -DestinationPath $extractDirectory -Force
    $extractedFfmpeg = Get-ChildItem -LiteralPath $extractDirectory -Filter "ffmpeg.exe" -File -Recurse |
        Select-Object -First 1
    if (-not $extractedFfmpeg) {
        throw "The verified FFmpeg archive did not contain ffmpeg.exe."
    }

    New-Item -ItemType Directory -Force -Path $localFfmpegDirectory | Out-Null
    Copy-Item -LiteralPath $extractedFfmpeg.FullName -Destination $localFfmpegPath -Force

    $license = Get-ChildItem -LiteralPath $extractDirectory -File -Recurse |
        Where-Object { $_.Name -match '^LICENSE' } |
        Select-Object -First 1
    if ($license) {
        Copy-Item -LiteralPath $license.FullName -Destination (Join-Path $localFfmpegDirectory $license.Name) -Force
    }

    & $localFfmpegPath -version 2>&1 | Select-Object -First 1 |
        Set-Content -LiteralPath (Join-Path $localFfmpegDirectory "version.txt")

    Remove-Item -LiteralPath $extractDirectory -Recurse -Force
    Remove-Item -LiteralPath $archivePath -Force
    return $localFfmpegPath
}

Push-Location $repositoryRoot
try {
    Write-Step "Checking .NET SDK"
    Assert-DotNet10

    if (-not $SkipRestore) {
        Write-Step "Restoring NuGet packages"
        dotnet restore $solutionPath "/p:Configuration=$Configuration" "/p:Platform=$Platform"
        if ($LASTEXITCODE -ne 0) {
            throw "NuGet restore failed with exit code $LASTEXITCODE."
        }
    }

    if (-not $SkipPlaywright) {
        Write-Step "Building Playwright installer"
        dotnet build $infrastructureProject --configuration $Configuration --no-restore "/p:Platform=AnyCPU"
        if ($LASTEXITCODE -ne 0) {
            throw "Infrastructure build failed with exit code $LASTEXITCODE."
        }

        $playwrightScript = Join-Path $repositoryRoot "src\StreamForge.Infrastructure\bin\$Configuration\net10.0\playwright.ps1"
        if (-not (Test-Path -LiteralPath $playwrightScript)) {
            throw "Playwright installer was not generated at $playwrightScript."
        }

        Write-Step "Ensuring Playwright Chromium is installed"
        & pwsh -NoProfile -File $playwrightScript install chromium
        if ($LASTEXITCODE -ne 0) {
            throw "Playwright Chromium installation failed with exit code $LASTEXITCODE."
        }
    }

    if (-not $SkipFfmpeg) {
        Write-Step "Checking FFmpeg"
        $ffmpeg = Find-ExistingFfmpeg
        if ($ForceLocalFfmpeg -and $ffmpeg -ne $localFfmpegPath) {
            $ffmpeg = $null
        }

        if (-not $ffmpeg) {
            Write-Host "FFmpeg was not found; installing the verified essentials build locally."
            $ffmpeg = Install-LocalFfmpeg
        }

        Write-Host "FFmpeg: $ffmpeg"
    }

    Write-Host "`nStreamForge dependencies are ready." -ForegroundColor Green
}
finally {
    Pop-Location
}
