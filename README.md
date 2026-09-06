# Production Toolkit

See [interoperability and setup readiness](INTEROPERABILITY.md) for client/server status and verified offline packages.

A native Windows application for installing, updating and launching four production tools from JohnDevAc's GitHub releases.

**Copyright © 2026 John Lightfoot. All rights reserved. Proprietary software, free for non-commercial use.** Commercial use requires a separate written licence. Public source availability does not make this an open-source licence. See [LICENSE.md](LICENSE.md).

## Run

Download **Production-Toolkit-1.3.1-win-x64-Setup.exe** from the [latest release](https://github.com/JohnDevAc/Production-Toolkit/releases/latest) and run it. Setup installs Production Toolkit for your Windows account, creates desktop and Start menu shortcuts, and adds an entry to **Settings → Apps → Installed apps** with an uninstaller. The default location is `%LOCALAPPDATA%\Programs\Production Toolkit`; a custom location can be selected and will be reused by updates.

The Windows x64 application is self-contained: no separate .NET installation, browser runtime, Python or Node.js is required. Production Toolkit installs and updates without administrator elevation; installers for the managed applications may request it. Users of the earlier portable releases should run this installer once and then use the installed shortcuts. Local builds are in `artifacts/release`; portable executable and ZIP downloads remain available as secondary options.

## Updating Production Toolkit

On startup, and when you select **Check for updates**, the toolkit checks its own GitHub repository for a newer stable Windows installer. If one is available, choose **Update and restart** or **Later**. Downloads can be cancelled. The installer is not started until its size, GitHub SHA-256 digest and embedded version match the selected release. Failed checks or downloads leave the existing application usable; no periodic polling runs.

Setup asks the running installation to close gracefully once it is ready to replace its files. It retains the installation directory, settings and download/icon caches, replaces the executable, updates the same Windows app entry and shortcuts, and relaunches the installed version. A normal installer run also closes and relaunches an open copy. Active managed-app downloads and installers must finish or be cancelled before maintenance can proceed. Failed or cancelled setup attempts reopen the surviving version when Setup had closed it. Older installers are blocked from downgrading a newer app. Windows is never automatically rebooted.

Uninstall through Windows **Installed apps** or the installed `unins000.exe`. Uninstall closes the toolkit and removes its program files, registration and shortcuts. Settings, caches and user-created files are retained. The four production applications managed by the toolkit remain independently installed.

## Application dashboard

Uninstalled applications show a large app icon, their name, a Stable / Development selector and a single **Install** button. Installed applications keep their detailed cards:

- **Stable / Development** selection, remembered separately for each application.
- The installed version and latest published version in that channel.
- **Install**, **Update**, or **Switch version**, according to the detected installation.
- **Launch** for installed applications; **Open setup** for Environment Setup.
- Matching layouts with colours derived from each application's icon.

The app reads installed versions, icons and release information on startup and during **Check for updates**. When an installer or **Open setup** window closes, all cards automatically refresh their local installed versions, icons, actions and environment status. This also detects components installed by another app's setup and changes made during repair, uninstall or incomplete setup. It uses the saved release information without another GitHub check, so API cooldowns do not block the refresh. There is no timer or background polling. Finish one installer before opening another; downloads remain cancellable. The app never automatically accepts an application's licence or runs an unattended installation. Routine launch, cancellation and completion confirmations stay out of the cards; errors and active installation/download progress remain visible.

## Supported applications

| Card | Repository | Package and launch behavior |
| --- | --- | --- |
| Kiloview Environment Setup | [Kiloview-Environment-Setup](https://github.com/JohnDevAc/Kiloview-Environment-Setup) | Downloads the setup executable and retains it for later maintenance. Also detects the persistent launcher in `ProgramData/KiloLink/Launcher`. |
| NDI Job Configurator | [Kiloview-Job-Configurator](https://github.com/JohnDevAc/Kiloview-Job-Configurator) | Uses the official installer. Launch uses its installed PowerShell launcher to start the scheduled service and open the browser. Recognizes current and legacy installation paths. |
| Resolume Arena Configurator | [Resolume-Configurator](https://github.com/JohnDevAc/Resolume-Configurator) | Uses the versioned Windows x64 Setup executable and launches the installed native app. |
| NDI Configurator PC Agent | [Kiloview-PC-Onboarding](https://github.com/JohnDevAc/Kiloview-PC-Onboarding) | Downloads the complete self-contained ZIP, extracts the entire payload and runs its Setup executable. Launch starts the installed tray agent. Recognizes legacy Kiloview branding. |

**Environment Setup separates the setup download from the installed environment.** Its setup executable is labelled **Downloaded · up to date** or **Downloaded · out of date**, independently of the **Installed fully**, **Installed partially**, or **Not installed** environment status. Cached release comparisons remain labelled as cached. A persistent setup launcher also counts as an available setup executable.

The environment check reads Setup's configuration, the managed WSL registration and KiloLink container, startup watchdog, NDI Tools registration and launcher, and NDI Discovery executable/service or task. Component rows show the container's running state and web response, the installed NDI Tools version, and whether Discovery is running and owns its listening port. Hover the status or component rows for diagnostic details and the local check time. A stopped service can still be fully installed; an unreadable or stopped WSL environment is labelled **Installation not verified** when its container cannot be inspected. Pending setup continuation is reported as partial.

When no environment components are detected, its card uses the simple Install view even if the setup executable has already been downloaded. **Install** or **Complete setup** can reuse that executable. Successful downloads are retained even if the elevation prompt is cancelled. Open Setup to update or repair the underlying server and NDI products; the toolkit does not claim their versions are current just because the setup download is current.

Environment detection is read-only, runs only at startup or **Check for updates**, and has a 20-second limit. It does not elevate, start Windows services or scheduled tasks, or start Docker/containers. Docker inspection runs only when the managed WSL distribution is already reported running. It never calls the upstream installer to discover state.

## Version and update rules

- Stable excludes drafts and prereleases, including legacy `-dev` tags incorrectly marked stable by GitHub. Development uses published prereleases; it does not build branch source or retrieve GitHub Actions artifacts.
- The latest release is selected by publication date, with pagination. An unavailable development release never falls back to stable. Releases without a recognized installer show that limitation.
- Installed versions are reread from executable product metadata during each check, preserving development identifiers. Apps updated internally, replaced in place or removed outside the toolkit are detected on the next check. Registered installation folders, standard paths and locations saved by earlier toolkit versions are supported. Build metadata does not affect comparisons; Windows versions such as `1.3.2.0` match `v1.3.2`. Unknown versions are reported explicitly.
- Channel changes are explicit. A newer locally installed version is not silently downgraded. Where stable and development publish the exact same SHA-256 package, that equivalent package counts as current in either channel.
- For the retained Environment Setup executable, a saved release tag is used only when its SHA-256 still matches that release. A newer persistent launcher takes precedence over an older saved setup; at equal versions the persistent launcher is preferred.
- Successful installer exit alone is not proof of installation. The next startup or explicit update check rereads the actual local version. Failures and restart-required exit codes remain visible.
- If GitHub is offline or rate-limited, cached metadata remains available with its original check time and a **Cached** label. “Current in cache” is not a fresh online check. Previously installed applications remain launchable.
- Rapid restarts reuse successful release checks for ten minutes. **Check for updates** can refresh sooner, but repeated clicks within one minute reuse the last result. Installed versions and icons are still read on startup and each explicit check. The toolkit's own release check uses the same cache and request queue as the four app cards.
- GitHub API requests run one at a time. A rate limit pauses all remaining API requests until GitHub's reset or `Retry-After` deadline; that deadline survives restarting the toolkit. Expiry never starts an automatic retry: reopen the toolkit or click **Check for updates** afterward. GitHub's unauthenticated allowance is shared by applications using the same public IP, so other software can still exhaust it. See [GitHub's rate-limit documentation](https://docs.github.com/en/rest/using-the-rest-api/rate-limits-for-the-rest-api).

## Downloads and local data

Preferences, release snapshots, icon cache, the activity log and verified download cache are in:

```text
%LOCALAPPDATA%\Production Toolkit
```

Installers are fetched directly from the four projects' GitHub release URLs. Package size and GitHub's SHA-256 digest must match before an installer can run. Releases without a digest require manual installation via their Releases page. Downloads write to temporary files first; failures remove incomplete cache files. ZIP extraction rejects traversal, absolute paths, alternate streams, reserved Windows names, symlinks, duplicate paths and excessive extraction sizes.

Downloaded applications are not bundled into the wrapper. The internal cache is beneath the local data folder above. It can be cleared manually when no installer is running; clearing a retained Environment Setup executable also removes that saved launch target.

The executable is currently unsigned. Windows may show its standard publisher or SmartScreen prompt.

## UI and scaling

The WPF application declares **Per-Monitor V2 DPI awareness**, uses logical pixel layouts, native controls, layout rounding and scalable typography. At startup it measures the complete UI, including the title bar and window borders, and chooses a size that shows all four cards without scrolling when the current monitor's work area allows. It tries a wider window when that reduces wrapping. It checks the size once more after loading release details, provided the user has not resized the window in the meantime.

On desktops too small to hold the complete UI, the window stays within the work area and retains vertical scrolling so every control remains accessible. The layout switches from two columns to one below 1,000 logical pixels. Buttons have keyboard focus states, text labels and standard tab navigation; status is conveyed with both text and colour. The footer identifies John Lightfoot's copyright and the non-commercial licence.

Each application uses its own icon. At startup and during update checks, the toolkit extracts the current icon directly from the installed executable without launching it or relying on the Windows shell icon cache. If no installed icon is available, it retrieves the project icon at the selected release tag. HTTP ETags and an on-disk cache preserve working icons when offline; embedded upstream icons are the final fallback. Repository icon paths are listed in `Catalog.cs`; if a project moves an icon to a different path, the bundled fallback remains available until that mapping is updated.

Cards share consistent controls, spacing and equal column widths, with matching heights within each row. Uninstalled cards use the simpler large-icon layout. The dominant icon colour supplies tinted card backgrounds, panels and borders, plus matching buttons and progress bars. Text contrast is maintained by darkening the button colour; monochrome icons receive a neutral palette. A changed icon automatically regenerates the palette during the same check. Green/amber version-status colours retain their usual meanings. Selecting a different channel changes version comparisons immediately; its icon is reread during the next update check.

The wrapper has an original toolkit icon, supplied as an editable SVG and an ICO with 16, 20, 24, 32, 40, 48, 64, 96, 128 and 256 pixel images. Cards select the largest available icon frame for scaling.

The UI review runner renders the actual WPF view at 100%, 150%, 200% and 250%, plus narrower 720- and 640-pixel layouts. It checks startup sizes against simulated monitor work areas, the absence of scrollbars when the UI fits, equal card widths, control containment, overlapping buttons, and WPF binding errors. Review images use illustrative version states. This verifies rendering and layout; moving the app between physical monitors with different DPI still deserves a hardware acceptance check.

## Build and test

Build on Windows with the .NET 8 SDK:

```powershell
.\scripts\Build.ps1
```

This runs the offline regression/UI suite and publishes a Windows installer, standalone executable, distributable ZIP and checksums to `artifacts/release`. The build downloads a pinned, SHA-256-verified Inno Setup 6.7.3 compiler into `artifacts/tools` when needed. No third-party NuGet dependencies are used. CI runs the same Windows build plus the installer lifecycle test, and retains release artifacts, UI review images and installer logs.

Pushing a version tag such as `v1.2.0` runs that same build and publishes a GitHub Release only after the checks pass. The tag must match the version in `Directory.Build.props`. Release downloads include the installer, executable, ZIP, SHA-256 checksums and licence notices. Future installers must retain `AppId=JohnLightfoot.ProductionToolkit` and the `Production-Toolkit-VERSION-win-x64-Setup.exe` filename pattern for in-place updates.

Development launch:

```powershell
dotnet run --project .\src\ToolkitLauncher
```

Run tests and create review images:

```powershell
dotnet run --project .\tests\ToolkitLauncher.Tests -c Release -- --ui artifacts\ui-review
```

Optional live icon check for the current stable and development releases of all four projects:

```powershell
dotnet run --project .\tests\ToolkitLauncher.Tests -c Release -- --live-icons
```

Test a real install, shortcut targets, an upgrade of a running older copy, relaunch, downgrade protection and uninstall:

```powershell
.\scripts\Test-Installer.ps1
```

The lifecycle test builds an older-version fixture, installs it under a unique test identity and workspace directory, and removes its registration and test shortcuts afterward. Test launches use `--skip-startup-checks` to keep the lifecycle checks independent of live GitHub releases; the normal installer and shortcuts always use standard startup checks. It does not install any of the managed production applications. Logs remain in `artifacts/installer-test`. Physical monitor transitions and Windows policy-specific installation restrictions still require hardware acceptance testing.

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
