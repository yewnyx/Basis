using System;
using System.ComponentModel;
using System.IO;
using Nuke.Common.IO;
using Nuke.Common.Tooling;

namespace xyz.yewnyx.build.Nuke;

// Copied from https://github.com/yewnyx/UnityNukeExample/blob/main/Nuke/BuildProfile.cs, most build profiles won't
// work unless added to the project.
[TypeConverter(typeof(TypeConverter<BuildProfile>))]
public class BuildProfile : Enumeration
{
    public static BuildProfile AndroidDev = new() { Value = "Android Dev" };
    public static BuildProfile Android = new() { Value = "Android" };
    public static BuildProfile iOSDev = new() { Value = "iOS Dev" };
    public static BuildProfile iOS = new() { Value = "iOS" };
    public static BuildProfile LinuxServerDev = new() { Value = "Linux Server Dev" };
    public static BuildProfile LinuxServerMonoDev = new() { Value = "Linux Server Mono Dev" };
    public static BuildProfile LinuxServer = new() { Value = "Linux Server" };
    public static BuildProfile QuestDev = new() { Value = "Quest Dev" };
    public static BuildProfile Quest = new() { Value = "Quest" };
    public static BuildProfile WindowsDev = new() { Value = "Windows Dev" };
    public static BuildProfile WindowsMonoDev = new() { Value = "Windows Mono Dev" };
    public static BuildProfile WindowsServerDev = new() { Value = "Windows Server Dev" };
    public static BuildProfile WindowsServerMonoDev = new() { Value = "Windows Server Mono Dev" };
    public static BuildProfile WindowsServer = new() { Value = "Windows Server" };
    public static BuildProfile Windows = new() { Value = "Windows" };

    public static implicit operator string(BuildProfile buildProfile) => buildProfile.Value;

    public string SpacelessValue => Value.Replace(" ", "");

    public bool IsLinuxServer => this == LinuxServer || this == LinuxServerDev || this == LinuxServerMonoDev;

    public string Tag => Value switch
    {
        "Android Dev" => "Dev",
        "Android" => string.Empty,
        "iOS Dev" => "Dev",
        "iOS" => string.Empty,
        "Linux Server Dev" => "DedicatedServerDev",
        "Linux Server Mono Dev" => "DedicatedServerDevMono",
        "Linux Server" => "DedicatedServer",
        "Quest Dev" => "Dev",
        "Quest" => string.Empty,
        "Windows Dev" => "Dev",
        "Windows Mono Dev" => "DevMono",
        "Windows Server Dev" => "DedicatedServerDev",
        "Windows Server Mono Dev" => "DedicatedServerDevMono",
        "Windows Server" => "DedicatedServer",
        "Windows" => string.Empty,
        _ => throw new ArgumentOutOfRangeException()
    };

    public string Extension => Value switch
    {
        "Android Dev" => ".apk",
        "Android" => ".apk",
        "iOS Dev" => ".ipa",
        "iOS" => ".ipa",
        "Linux Server Dev" => ".x86_64",
        "Linux Server Mono Dev" => ".x86_64",
        "Linux Server" => ".x86_64",
        "Quest Dev" => ".apk",
        "Quest" => ".apk",
        "Windows Dev" => ".exe",
        "Windows Mono Dev" => ".exe",
        "Windows Server Dev" => ".exe",
        "Windows Server Mono Dev" => ".exe",
        "Windows Server" => ".exe",
        "Windows" => ".exe",
        _ => throw new ArgumentOutOfRangeException()
    };

    public AbsolutePath GetOutputFolder(AbsolutePath rootDirectory) => rootDirectory / "Builds" / SpacelessValue;
    public string GetExecutableFilename(string exeName) => $"{exeName}{Tag}{Extension}";
    public AbsolutePath GetExecutablePath(AbsolutePath rootDirectory, string exeName) => GetShipFolder(rootDirectory) / GetExecutableFilename(exeName);
    public AbsolutePath GetShipFolder(AbsolutePath rootDirectory) => GetOutputFolder(rootDirectory) / "Ship";
    public AbsolutePath GetDontShipFolder(AbsolutePath rootDirectory) => GetOutputFolder(rootDirectory) / "DontShip";
    public AbsolutePath GetDontShipArchive(AbsolutePath rootDirectory, string exeName, string archiveExtension) =>
        GetOutputFolder(rootDirectory) / $"{exeName}{Tag}-{SpacelessValue}.{archiveExtension}";

    public string BuildProfilePath => Path.Combine("Assets", "Settings", "Build Profiles", $"{Value}.asset");
}