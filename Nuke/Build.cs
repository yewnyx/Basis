using System;
using System.IO.Compression;
using System.Threading;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tools.Docker;
using Nuke.Common.Tools.GitVersion;
using Nuke.Common.Tools.Unity;
using xyz.yewnyx.build.Nuke;
using static Nuke.Common.EnvironmentInfo;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.IO.PathConstruction;

namespace xyz.yewnyx.build;

class Build : NukeBuild
{
    public static int Main () => Execute<Build>(x => x.PlayerBuild);

    const string ProjectName = "Basis";

    [Parameter("Build Profile")] BuildProfile BuildProfile;

    [GitVersion] readonly GitVersion GitVersion;

    Target SwitchProfile => _ => _
        .Requires(() => BuildProfile)
        .Executes(() =>
        {
            UnityTasks.Unity(_ => _
                .SetHubVersion("6000.0.14f1")
                .SetProjectPath(RootDirectory)
                .SetExecuteMethod("xyz.yewnyx.build.BuildScript.SwitchProfile")
                .AddCustomArguments("-activeBuildProfile", BuildProfile.BuildProfilePath)
            );
        });

    Target PlayerBuild => _ => _
        .Requires(() => BuildProfile)
        .DependsOn(SwitchProfile)
        .Executes(() =>
        {
            var outputFolder = BuildProfile.GetOutputFolder(RootDirectory);
            outputFolder.CreateOrCleanDirectory();

            var shipFolder = BuildProfile.GetShipFolder(RootDirectory);
            shipFolder.CreateOrCleanDirectory();

            var dontShipFolder = BuildProfile.GetDontShipFolder(RootDirectory);
            dontShipFolder.CreateOrCleanDirectory();

            var outputPath = BuildProfile.GetExecutablePath(RootDirectory, ProjectName);

            UnityTasks.Unity(_ => _
                .SetHubVersion("6000.0.14f1")
                .SetProjectPath(RootDirectory)
                .EnableBatchMode()
                .EnableNoGraphics()
                .SetExecuteMethod("xyz.yewnyx.build.BuildScript.Build")
                .AddCustomArguments("-activeBuildProfile", BuildProfile.BuildProfilePath)
                .AddCustomArguments("-branch", GitVersion.BranchName)
                .AddCustomArguments("-commit", GitVersion.ShortSha)
                .AddCustomArguments("-outputPath", outputPath));

            var dontShipDirectories = (outputFolder / "Ship").GlobDirectories("*_BurstDebugInformation_DoNotShip",
                "*_BackUpThisFolder_ButDontShipItWithYourGame");
            foreach (var directory in dontShipDirectories) {
                directory.MoveToDirectory(dontShipFolder);
            }

            var dontShipArchive = BuildProfile.GetDontShipArchive(RootDirectory, ProjectName, "zip");
            dontShipFolder.ZipTo(dontShipArchive, compressionLevel: CompressionLevel.SmallestSize);
            dontShipFolder.DeleteDirectory();
        });

    Target DockerBuild => _ => _
        .TriggeredBy(PlayerBuild)
        .Requires(() => BuildProfile)
        .OnlyWhenDynamic(() => BuildProfile.IsLinuxServer)
        .Executes(() =>
        {
            var dockerFile = RootDirectory / "Nuke"/ "Docker" / "Dockerfile";
            var dockerIgnore = RootDirectory / "Nuke"/ "Docker" / ".dockerignore";

            var dockerContext = TemporaryDirectory / "DockerContext" / ProjectName;
            dockerContext.CreateOrCleanDirectory();

            var shipFolder = BuildProfile.GetShipFolder(RootDirectory);
            var dockerImage = ProjectName.Replace(" ", "-").ToLower();
            var dockerTag = GitVersion.EscapedBranchName;

            CopyFileToDirectory(dockerFile, dockerContext);
            CopyFileToDirectory(dockerIgnore, dockerContext);
            CopyDirectoryRecursively(shipFolder, dockerContext / "Game");

            DockerTasks.DockerBuild(_ => _
                .SetFile(dockerFile)
                .AddLabel($"org.opencontainers.image.version=\"{GitVersion.BranchName}-{GitVersion.ShortSha}\"")
                .AddTag($"{dockerImage}:latest")
                .AddTag($"{dockerImage}:{dockerTag}")
                .SetPath(dockerContext)
                .AddBuildArg($"SERVER_EXECUTABLE=\"{BuildProfile.GetExecutableFilename(ProjectName)}\"")
            );
            DockerTasks.DockerSave(_ => _
                .SetImages($"{dockerImage}:{dockerTag}")
                .SetOutput(BuildProfile.GetOutputFolder(RootDirectory) / ($"{dockerImage}-{dockerTag}.tar")));
            dockerContext.DeleteDirectory();
        });
}
