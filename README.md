# Production Toolkit

A native Windows application for installing, updating and launching four production tools from JohnDevAc's GitHub releases.

**Copyright © 2026 John Lightfoot. All rights reserved. Free for non-commercial use.** Commercial use requires a separate written licence. See [LICENSE.md](LICENSE.md).

## Run

Download the Windows executable or ZIP from the [latest release](https://github.com/JohnDevAc/Production-Toolkit/releases/latest), then double-click **Production Toolkit.exe**. Local builds are in `artifacts/release`. The published Windows x64 executable is self-contained: no separate .NET installation, browser runtime, Python or Node.js is required. Keep it in any convenient folder or pin it to Start. The toolkit runs without elevation; an application's installer may request administrator approval.

The four application cards provide:

- **Stable / Development** selection, remembered separately for each application.
- The installed version and latest published version in that channel.
- **Install**, **Update**, or **Switch version**, according to the detected installation.
- **Launch** for installed applications; **Open setup** for Environment Setup.
- **Download only**, which saves a verified installer or complete ZIP to your chosen location.
- **Locate app** for an existing installation in a custom location, and **Releases** for upstream release notes.

The app checks releases on startup. Select **Check for updates** to refresh later. Installations are detected again when the toolkit regains focus and after an installer exits. Finish one installer before opening another; downloads remain cancellable. The app never automatically accepts an application's licence or runs an unattended installation.

## Supported applications

| Card | Repository | Package and launch behavior |
| --- | --- | --- |
| Kiloview Environment Setup | [Kiloview-Environment-Setup](https://github.com/JohnDevAc/Kiloview-Environment-Setup) | Downloads the setup executable and retains it for later maintenance. Also detects the persistent launcher in `ProgramData/KiloLink/Launcher`. |
| NDI Job Configurator | [Kiloview-Job-Configurator](https://github.com/JohnDevAc/Kiloview-Job-Configurator) | Uses the official installer. Launch uses its installed PowerShell launcher to start the scheduled service and open the browser. Recognizes current and legacy installation paths. |
| Resolume Arena Configurator | [Resolume-Configurator](https://github.com/JohnDevAc/Resolume-Configurator) | Uses the versioned Windows x64 Setup executable and launches the installed native app. |
| NDI Configurator PC Agent | [Kiloview-PC-Onboarding](https://github.com/JohnDevAc/Kiloview-PC-Onboarding) | Downloads the complete self-contained ZIP, extracts the entire payload and runs its Setup executable. Launch starts the installed tray agent. Recognizes legacy Kiloview branding. |

**Environment Setup version checks cover the setup tool**, not the installed KiloLink container or NDI Tools. Open Setup to check and update those products. A saved setup is labelled “saved setup”; it does not imply that the services were installed successfully.

## Version and update rules

- Stable excludes drafts and prereleases, including legacy `-dev` tags incorrectly marked stable by GitHub. Development uses published prereleases; it does not build branch source or retrieve GitHub Actions artifacts.
- The latest release is selected by publication date, with pagination. An unavailable development release never falls back to stable. Releases without a recognized installer show that limitation.
- Installed versions come from executable product metadata, preserving development identifiers. Build metadata does not affect comparisons; Windows versions such as `1.3.2.0` match `v1.3.2`. Unknown versions are reported explicitly.
- Channel changes are explicit. A newer locally installed version is not silently downgraded. Where stable and development publish the exact same SHA-256 package, that equivalent package counts as current in either channel.
- For the retained Environment Setup executable, a saved release tag is used only when its SHA-256 still matches that release.
- Successful installer exit alone is not proof of installation. The toolkit rereads the actual local version afterward. Cancellation, failures and restart-required exit codes are shown separately.
- If GitHub is offline or rate-limited, cached metadata remains available with its original check time and a **Cached** label. “Current in cache” is not a fresh online check. Previously installed applications remain launchable.

## Downloads and local data

Preferences, release snapshots, the activity log and verified download cache are in:

```text
%LOCALAPPDATA%\Production Toolkit
```

Installers are fetched directly from the four projects' GitHub release URLs. Package size and GitHub's SHA-256 digest must match before an installer can run. Releases without a digest require manual installation via their Releases page. Downloads write to temporary files first; failures remove incomplete cache files. ZIP extraction rejects traversal, absolute paths, alternate streams, reserved Windows names, symlinks, duplicate paths and excessive extraction sizes.

Downloaded applications are not bundled into the wrapper. The internal cache is beneath the local data folder above. It can be cleared manually when no installer is running; clearing a retained Environment Setup executable also removes that saved launch target. User-exported downloads remain in the location selected in the save dialog.

The executable is currently unsigned. Windows may show its standard publisher or SmartScreen prompt.

## UI and scaling

The WPF application declares **Per-Monitor V2 DPI awareness**, uses logical pixel layouts, native controls, layout rounding and scalable typography. At startup it measures the complete UI, including the title bar and window borders, and chooses a size that shows all four cards without scrolling when the current monitor's work area allows. It tries a wider window when that reduces wrapping. It checks the size once more after loading release details, provided the user has not resized the window in the meantime.

On desktops too small to hold the complete UI, the window stays within the work area and retains vertical scrolling so every control remains accessible. The layout switches from two columns to one below 1,000 logical pixels. Buttons have keyboard focus states, text labels and standard tab navigation; status is conveyed with both text and colour. The footer identifies John Lightfoot's copyright and the non-commercial licence.

Each application uses its upstream icon. The wrapper has an original toolkit icon, supplied as an editable SVG and an ICO with 16, 20, 24, 32, 40, 48, 64, 96, 128 and 256 pixel images. Icons are embedded into the executable, and application cards select the largest available source image for scaling.

The UI review runner renders the actual WPF view at 100%, 150%, 200% and 250%, plus narrower 720- and 640-pixel layouts. It checks startup sizes against simulated monitor work areas, the absence of scrollbars when the UI fits, equal card widths, control containment, overlapping buttons, and WPF binding errors. Review images use illustrative version states. This verifies rendering and layout; moving the app between physical monitors with different DPI still deserves a hardware acceptance check.

## Build and test

Build on Windows with the .NET 8 SDK:

```powershell
.\scripts\Build.ps1
```

This runs the offline regression/UI suite and publishes a standalone executable, distributable ZIP and checksums to `artifacts/release`. No third-party NuGet dependencies are used. CI runs the same Windows build and retains release artifacts and UI review images.

Pushing a version tag such as `v1.0.0` runs that same build and publishes a GitHub Release only after the checks pass. The tag must match the version in `Directory.Build.props`. Release downloads include the executable, ZIP, SHA-256 checksums and licence notices.

Development launch:

```powershell
dotnet run --project .\src\ToolkitLauncher
```

Run tests and create review images:

```powershell
dotnet run --project .\tests\ToolkitLauncher.Tests -c Release -- --ui artifacts\ui-review
```

Optional live integration check (downloads roughly 350 MB of current stable packages into a temporary directory, verifies and prepares them, and removes the test data; it **does not execute installers**):

```powershell
dotnet run --project .\tests\ToolkitLauncher.Tests -c Release -- --live
```

Regenerate the wrapper's original raster icon from its matching vector geometry:

```powershell
.\scripts\Build-Icon.ps1
```

Upstream installer execution and changes to real services, network adapters, scheduled tasks and application data should be acceptance-tested on a disposable Windows machine. The automated suite never makes those changes.

See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for icon provenance and upstream terms.
