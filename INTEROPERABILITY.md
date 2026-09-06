# Interoperability and setup readiness

## PC Agent setup recovery

The PC Agent card now checks per-user configuration independently of executable version. Missing, unreadable or invalid schema-1 identity/network/membership state requires **Complete setup**. It follows the agent's current/legacy state precedence and does not treat a disconnected adapter as missing setup. A matching installed Agent/Setup pair can complete configuration offline; missing or mismatched Setup requires the complete release package. Recovery retains newer installed versions. Local checks run again before actions and after setup exits, including unsuccessful or incomplete setup. Toolkit never writes Agent configuration or accepts its licence; the independently installed Setup owns those operations. The server process contract and remote consent flow are unchanged.

Validation (6 September 2026): 312 Toolkit regression/UI assertions pass, including a harmless local Setup fixture exercising incomplete exit, successful configuration, offline reuse and stale Launch recovery. The recovery card was visually checked at 100% scale and layout assertions pass at 100%, 200% and 250%. The server's 22 regression and 8 frontend checks, installer package checks and both companion validation projects also pass. All configuration fixtures use isolated paths; validation did not install software or change live NDI settings. The 1.3.3 self-contained application, ZIP and Windows installer build successfully. Publication and local installation are separate from build validation.

## Additional QA corrections — 6 September 2026

Malformed optional Agent metadata no longer discards otherwise valid Server evidence. It still prevents Client/combined deployments from reporting complete. Unsupported or unreadable component receipts produce an explicit unknown deployment status instead of being interpreted as a legacy Client installation.

## QA follow-up — 6 September 2026

Configured PC Agent evidence now requires schema 1, nonempty valid endpoint and adapter GUIDs, a usable unicast IPv4 host address and a prefix between /1 and /30. Incomplete, unsupported, link-local, loopback, multicast, network and broadcast state cannot claim a configured installation.

Live adapter/address availability is checked separately and shown in component details. A disconnected adapter does not imply missing installation components. Client-only completeness continues to require local NDI Tools and PC Agent without requiring local KiloLink, WSL or Discovery. Verified cached downloads remain installable offline; unavailable package sources block downloads before installer launch.

Toolkit launches independently released applications. It does not require local Job Configurator, KiloLink or Discovery to run on a client PC. Each component retains its own installer, repository, version and updater.

Environment status reads its schema-1 Client/Server role receipt plus actual local files and configuration. Client requires NDI Tools and configured PC Agent, with server components shown as not required. Combined hosts also display the Agent requirement. Server-only deployments do not require Agent. Reliable legacy client evidence can identify a client installation; shared NDI Tools alone leave the role unverified. Server removal retains shared tools without inventing a missing server deployment.

Package preparation checks the actual required release asset using the downloader's HTTPS/proxy context, handles sources that reject HEAD, and rejects captive-portal HTML. Failures prevent installer launch and explain how to retry. Bounded stalled-read timeouts, existing checksum/product checks and archive safety still apply. A complete verified cache can be used offline; cached release metadata by itself is not an installable package.

Launching installed apps and local self-contained installers does not need internet. Environment and PC Agent Setup perform authoritative checks for their own secondary downloads, including when launched outside Toolkit. These checks can use a separate internet adapter from the production LAN.

Job Configurator and Environment Server require their intended signed-in administrator account; their per-user/WSL startup cannot safely be assigned to a standard-user desktop by entering another account at UAC. Client PCs normally use the remote services and do not need those server components installed.

Validation: `dotnet run --project tests/ToolkitLauncher.Tests/ToolkitLauncher.Tests.csproj`. Full installer/elevation/reboot behavior remains part of controlled deployment acceptance.
