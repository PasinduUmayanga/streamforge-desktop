# StreamForge Feature-Update Guide

This file defines the working rules for future StreamForge feature changes. Read it together with `README.md` before modifying the repository.

## Product boundary

StreamForge is a Windows desktop application with x86 and x64 app artifacts for processing media the user is authorized to save. It supports standard non-DRM HLS, DASH, and MP4 sources. The x86 app artifact runs on 64-bit Windows because the Playwright browser and bundled FFmpeg tooling are 64-bit; do not claim native 32-bit-Windows support.

Do not add:

- DRM bypass or license acquisition;
- encryption-key extraction;
- authentication, paywall, or CAPTCHA circumvention;
- credential, cookie, or authorization-token collection;
- hard-coded signed URLs or site secrets; or
- logging of private session values.

## Local AI updates

The optional local AI advisor is a Qwen3-Coder-Next classifier, not an authority or execution engine. Keep AI provider code in `StreamForge.AiWorker`; deterministic extraction and player identification belong in `StreamForge.Infrastructure`. Preserve these boundaries:

1. Keep the feature disabled by default and restrict runtime endpoints to loopback addresses.
2. Send only bounded structural metadata. Alias hosts and remove cookies, authorization data, query values, ordinary JSON string values, titles, and response bodies.
3. Require schema-constrained output that references an exact captured observation and JSON pointer.
4. Resolve the original value only in memory, then validate its scheme, HTTP response, media type, and DRM status deterministically.
5. Never execute model-generated JavaScript, commands, request headers, authentication actions, CAPTCHA actions, URLs, or decryption instructions.
6. Treat missing Ollama, missing models, timeouts, malformed JSON, and low confidence as ordinary no-result conditions.
7. Use fake model responses in automated tests. Do not install, download, cache, or run language models in CI.
8. Keep model names configurable and document that model licenses and hardware requirements vary.

AI must not be used to claim support for authenticated, challenge-protected, encrypted, or private-protocol sources that deterministic validation cannot process.

Reject unsupported DRM sources with a clear user-facing message.

## Architecture

Keep the existing project boundaries:

- `StreamForge.App`: WinUI presentation, binding, commands, and dependency registration.
- `StreamForge.Core`: models, exceptions, and implementation-independent interfaces.
- `StreamForge.AiWorker`: optional local Qwen/Ollama AI advisor implementation.
- `StreamForge.Infrastructure`: Playwright extraction, deterministic player identification, HTTP analysis, FFmpeg execution, and operating-system integrations.
- `tests`: deterministic tests and local fixtures.

Views and code-behind must not perform media extraction, playlist parsing, network orchestration, or FFmpeg process management. ViewModels may orchestrate interfaces but should not depend on implementation details.

Register new services through Microsoft dependency injection. Avoid static mutable state and service locators.

## Extraction updates

When extending media detection:

1. Prefer generic network, response-content, DOM, or documented player-API strategies.
2. Preserve the exact signed media URL internally, including its query string.
3. Redact query values before displaying or logging a URL.
4. Filter Authorization, Cookie, Set-Cookie, and Proxy-Authorization headers.
5. Capture only non-sensitive headers required for authorized playback, such as User-Agent, Referer, and Origin.
6. Treat `blob:` URLs as player observations, not downloadable media URLs.
7. Keep network interception as the primary fallback when player internals are unavailable.
8. Bound response-body inspection by content type and size.
9. Handle frame navigation, detachment, timeout, and cancellation without crashing.
10. Rank real media responses and player-observed sources above speculative page URLs.

When adding seed-link discovery:

- collect only HTTP(S) links exposed by the current page, frames, player metadata, or relevant observed GET requests;
- exclude static assets, advertisements, duplicate links, credentials, and unsupported schemes;
- cap link count, recursion depth, response size, per-request timeout, and total probe time;
- never replay POST bodies or generate guessed endpoints;
- use the active browser context for ordinary authorized cookies without exporting them to logs or the UI;
- require a standard media content type, manifest/body signature, or explicit supported media URL before creating a candidate; and
- reject DRM indicators before ranking a seed-derived candidate.

Do not add a production dependency on a specific external streaming website. Live pages may be used for manual diagnosis, but automated tests must use local fixtures and synthetic data.

## FFmpeg updates

- Pass arguments with `ProcessStartInfo.ArgumentList`.
- Never build a shell command by concatenating page, stream, header, or output values.
- Keep FFmpeg-specific behavior behind `IFfmpegService` and `IFfmpegLocator`.
- Preserve cancellation and process cleanup.
- Pause/resume changes must keep parser timing and speed estimation consistent.
- Do not delete partial output unless the feature explicitly defines and tests that behavior.
- Continue reporting machine-readable progress from `-progress pipe:1`.

FFmpeg lookup order is bundled executable, `STREAMFORGE_FFMPEG`, then `PATH`.

## UI updates

- Preserve MVVM and async commands.
- Do not block the UI thread.
- Keep all important content reachable through vertical scrolling.
- Test narrow, medium, and wide window states.
- Ensure text boxes stretch within their containers and do not force horizontal overflow.
- Update command availability when operation state changes.
- Keep Analyze and Download mutually safe while an operation is active.
- Redact sensitive data before adding it to the network activity collection.
- Keep the application icon configured for the executable and taskbar.

If a future feature replaces WinUI with a web-based presentation layer, retain `StreamForge.Core` and `StreamForge.Infrastructure` unless a separately approved architecture change says otherwise.

## Models and contracts

- Enable and respect nullable reference types.
- Prefer immutable DTOs where practical.
- Add cancellation tokens to asynchronous service operations.
- Keep user-facing errors friendly; log technical details separately.
- Update interfaces, implementations, dependency registration, and tests together.
- Avoid exposing raw exceptions or stack traces in the UI.

## Testing requirements

Add or update tests for behavior introduced by every feature. Relevant test areas include:

- media type and candidate detection;
- HLS parsing and relative URL resolution;
- player-source parsing;
- sensitive-header and URL sanitization;
- FFmpeg argument construction;
- progress, speed, elapsed-time, and ETA parsing;
- cancellation and pause/resume state; and
- candidate ranking and deduplication.

Do not require live streaming websites in the unit test suite. Put reusable input under `tests/StreamForge.Infrastructure.Tests/Fixtures`.

Before handing off a feature, run:

```powershell
dotnet build src\StreamForge.App\StreamForge.App.csproj
dotnet test tests\StreamForge.Infrastructure.Tests\StreamForge.Infrastructure.Tests.csproj
git diff --check
```

For changes to solution configuration, setup, packaging, or shared project references, also run:

```powershell
dotnet build StreamForge.sln /p:Platform=x64
dotnet build StreamForge.sln /p:Platform=x86
```

The build should finish with no new warnings. All relevant tests must pass.

## Setup and dependency changes

Update `scripts/setup.ps1` when a feature introduces a required runtime dependency. Setup changes must be:

- repeatable;
- safe to run more than once;
- explicit about failures;
- checksum-verified for downloaded binaries when a checksum is available; and
- compatible with local development and AppVeyor.

Do not assume the infrastructure project output already exists before invoking its generated `playwright.ps1` script.

## CI and publishing

- Keep build and test stages separate.
- Do not publish when tests fail.
- Preserve the AppVeyor test-success gate.
- Cache only reproducible dependencies.
- Avoid caching unrelated contents from a machine-wide package directory.
- Remember that AppVeyor cache entries and account caches have size limits; an oversized cache should not obscure build or test results.
- Update artifact names and README instructions together when packaging changes.

## Documentation

Update `README.md` whenever a feature changes:

- supported inputs or players;
- setup prerequisites;
- user-visible workflow;
- command-line instructions;
- privacy or security behavior;
- CI or packaging behavior; or
- known limitations.

Never place live signed media URLs, tokens, cookies, authorization values, or private session information in documentation.

## Feature completion checklist

A feature is complete only when:

1. It respects the application boundaries above.
2. Cancellation and errors leave the UI and child processes in a valid state.
3. Sensitive data remains filtered and redacted.
4. Relevant deterministic tests exist and pass.
5. The application builds without new warnings.
6. Responsive UI behavior is checked when presentation code changes.
7. Setup and AppVeyor are updated when dependencies change.
8. README documentation and current limitations are accurate.
