# Interoperability and setup readiness

Toolkit launches independently released applications. It does not require local Job Configurator, KiloLink or Discovery to run on a client PC. Each component retains its own installer, repository, version and updater.

Environment status reads its schema-1 Client/Server role receipt plus actual local files and configuration. Client requires NDI Tools and configured PC Agent, with server components shown as not required. Combined hosts also display the Agent requirement. Server-only deployments do not require Agent. Reliable legacy client evidence can identify a client installation; shared NDI Tools alone leave the role unverified. Server removal retains shared tools without inventing a missing server deployment.

Package preparation checks the actual required release asset using the downloader's HTTPS/proxy context, handles sources that reject HEAD, and rejects captive-portal HTML. Failures prevent installer launch and explain how to retry. Bounded stalled-read timeouts, existing checksum/product checks and archive safety still apply. A complete verified cache can be used offline; cached release metadata by itself is not an installable package.

Launching installed apps and local self-contained installers does not need internet. Environment and PC Agent Setup perform authoritative checks for their own secondary downloads, including when launched outside Toolkit. These checks can use a separate internet adapter from the production LAN.

Job Configurator and Environment Server require their intended signed-in administrator account; their per-user/WSL startup cannot safely be assigned to a standard-user desktop by entering another account at UAC. Client PCs normally use the remote services and do not need those server components installed.

Validation: `dotnet run --project tests/ToolkitLauncher.Tests/ToolkitLauncher.Tests.csproj`. Full installer/elevation/reboot behavior remains part of controlled deployment acceptance.

