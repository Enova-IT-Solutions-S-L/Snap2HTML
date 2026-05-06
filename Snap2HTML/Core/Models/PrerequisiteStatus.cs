namespace Snap2HTML.Core.Models;

/// <summary>
/// Represents the lifecycle state of an external software prerequisite.
/// </summary>
public enum PrerequisiteStatus
{
    /// <summary>
    /// The check has not been performed yet.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Detection is currently in progress.
    /// </summary>
    Checking = 1,

    /// <summary>
    /// The prerequisite is present and ready to use.
    /// </summary>
    Installed = 2,

    /// <summary>
    /// The prerequisite is absent and has not been installed.
    /// </summary>
    NotInstalled = 3,

    /// <summary>
    /// Installation is currently in progress.
    /// </summary>
    Installing = 4,

    /// <summary>
    /// Installation was attempted but did not complete successfully.
    /// </summary>
    InstallFailed = 5,
}
