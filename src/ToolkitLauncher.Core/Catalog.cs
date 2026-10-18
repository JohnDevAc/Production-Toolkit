namespace ToolkitLauncher.Core;

public enum ReleaseChannel { Stable, Development }
public enum PackageKind { Executable, ZipWithSetup, PortableZip }

public sealed record AppDefinition(string Id, string Name, string Subtitle, string Description,
    string Repository, string Glyph, string Accent, string[] AssetPatterns,
    PackageKind PackageKind, string[] SetupNames, string[] ExecutableNames,
    string[] KnownPaths, string[] RegistryNames)
{
    public string[] RequiredPortableFiles { get; init; } = [];
    public string RepositoryUrl => $"https://github.com/JohnDevAc/{Repository}";
    public string RepositoryIconPath => Id switch
    {
        "environment" => "assets/setup.ico", "job" => "wwwroot/NDIJobConfigurator.ico",
        "resolume" => "src/ResolumeConfigurator/Assets/app-icon.ico", "pc-agent" => "assets/PcAgent.ico",
        "udp-to-ndi" => "assets/app.ico", "testcard" => "src/TestCard.App/Assets/TestCard.ico",
        _ => throw new InvalidOperationException("No icon source configured for this application.")
    };
    public bool IsEnvironment => Id == "environment";
    public bool IsJob => Id == "job";
    public bool IsPcAgent => Id == "pc-agent";
}

public static class Catalog
{
    public static readonly AppDefinition[] Apps =
    [
        new("environment", "Kiloview Environment Setup", "SERVER & NDI SERVICES",
            "Install and maintain KiloLink Server Pro, NDI Tools and Discovery Server.",
            "Kiloview-Environment-Setup", "\uE968", "#267E76",
            [@"^Kiloview-Environment-Setup\.exe$"], PackageKind.Executable, [],
            ["Kiloview-Environment-Setup.exe"],
            [@"%ProgramData%\KiloLink\Launcher\Kiloview-Environment-Setup.exe"], []),
        new("job", "NDI Job Configurator", "KILOVIEW JOB CONFIGURATOR",
            "Discover devices, configure your job and monitor the production network.",
            "Kiloview-Job-Configurator", "\uE8F1", "#4864CC",
            [@"^NDI-Job-Configurator\.exe$", @"^Kiloview-Job-Configurator\.exe$"],
            PackageKind.Executable, [], ["NDIJobConfigurator.exe", "KiloviewSetup.exe"],
            [@"%LOCALAPPDATA%\Programs\NDI Job Configurator\NDIJobConfigurator.exe",
             @"%LOCALAPPDATA%\Programs\Kiloview Setup\KiloviewSetup.exe"],
            ["NDI Job Configurator", "Kiloview Job Configurator", "Kiloview Setup"]),
        new("resolume", "Resolume Arena Configurator", "RESOLUME WORKSPACES",
            "Build Arena compositions and outputs from your NDI Job Configurator fleet.",
            "Resolume-Configurator", "\uE714", "#8960BB",
            [@"^Resolume-Arena-Configurator-.+-win-x64-Setup\.exe$"],
            PackageKind.Executable, [], ["Resolume Arena Configurator.exe"],
            [@"%LOCALAPPDATA%\Programs\Resolume Arena Configurator\Resolume Arena Configurator.exe"],
            ["Resolume Arena Configurator"]),
        new("pc-agent", "NDI Configurator PC Agent", "KILOVIEW PC ONBOARDING",
            "Install the Windows tray agent for remote onboarding and endpoint monitoring.",
            "Kiloview-PC-Onboarding", "\uE7F4", "#B47829",
            [@"^NDI-Configurator-PC-Agent-win-x64\.zip$", @"^Kiloview-PC-Onboarding-win-x64\.zip$"],
            PackageKind.ZipWithSetup,
            ["NDI Configurator PC Agent Setup.exe", "Kiloview PC Onboarding.exe"],
            ["NDI Configurator PC Agent.exe", "Kiloview PC Agent.exe"],
            [@"%ProgramFiles%\NDI Configurator\PC Agent\NDI Configurator PC Agent.exe",
             @"%ProgramFiles%\Kiloview\PC Agent\Kiloview PC Agent.exe"],
            ["NDI Configurator PC Agent", "Kiloview PC Agent"]),
        new("udp-to-ndi", "UDP to NDI", "UDP / RTP VIDEO BRIDGE",
            "Bridge UDP and RTP video and audio streams to NDI outputs.",
            "udp-to-ndi", "\uE8AB", "#287DAD",
            [@"^UDP-to-NDI-Setup-.+-x64\.exe$"], PackageKind.Executable, [],
            ["UDP to NDI.exe"],
            [@"%LOCALAPPDATA%\Programs\UDP to NDI\UDP to NDI.exe"], ["UDP to NDI"]),
        new("testcard", "Test Card Generator", "NDI & UDP TEST SIGNALS",
            "Generate HD and 4K test patterns, motion and audio for NDI and UDP.",
            "TestCardGen", "\uE7F4", "#288E82",
            [@"^TestCard-.+-win-x64\.zip$"], PackageKind.PortableZip, [],
            ["TestCard.exe"], [], [])
        {
            RequiredPortableFiles = ["TestCard.dll", "TestCard.Worker.exe", "TestCard.Worker.dll",
                "TestCard.runtimeconfig.json", "TestCard.Worker.runtimeconfig.json", ".runtime/dotnet.exe"]
        }
    ];
}
