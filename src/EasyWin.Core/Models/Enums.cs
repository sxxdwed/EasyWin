namespace EasyWin.Core.Models;

public enum ExecutionMode
{
    Live = 0,
    DryRun = 1,
}

public static class ExecutionModeExtensions
{
    public static bool IsDryRun(this ExecutionMode mode) => mode == ExecutionMode.DryRun;
}

public enum DeploymentStage
{
    NotStarted = 0,
    ValidateEnvironment,
    ValidateManifest,
    ValidateImage,
    ValidateTargetDisk,
    PrepareStaging,
    PrepareBoot,
    AwaitingWinPeBoot,
    PrepareDisk,
    PartitionDisk,
    ApplyImage,
    ConfigureBoot,
    GenerateUnattend,
    PreparePostInstall,
    AwaitingFirstBoot,
    InstallDrivers,
    InstallApplications,
    ApplyProfile,
    ActivateWindows,
    CleanupBoot,
    CleanupStaging,
    Completed,
    Failed,
    Cancelled,
}

public enum DeploymentErrorSeverity
{
    Warning = 0,
    Error = 1,
    Critical = 2,
}

public enum DiskBusType
{
    Unknown = 0,
    Scsi,
    Atapi,
    Ata,
    IEEE1394,
    Ssa,
    FibreChannel,
    Usb,
    Raid,
    ISCSI,
    Sas,
    Sata,
    Sd,
    Mmc,
    Virtual,
    FileBackedVirtual,
    StorageSpaces,
    Nvme,
    StorageClassMemory,
    Ufs,
}

public enum PartitionRole
{
    Unknown = 0,
    EfiSystem,
    MicrosoftReserved,
    Windows,
    Recovery,
    Deployment,
    Data,
}

public enum WindowsImageContainer
{
    Wim = 0,
    Esd = 1,
}

public enum ProcessorArchitecture
{
    Unknown = 0,
    X86,
    X64,
    Arm64,
}

public enum DriverCategory
{
    Other = 0,
    Chipset,
    Gpu,
    Lan,
    Wifi,
    Audio,
}

public enum DriverSelectionMode
{
    Automatic = 0,
    Explicit = 1,
    None = 2,
}

public enum ManifestFileKind
{
    Other = 0,
    WindowsImage,
    WinPeImage,
    WinPeSdi,
    EasyWinBinary,
    BcdBackup,
    ApplicationInstaller,
    Driver,
    Configuration,
}
