using Snap2HTML.Core.Models;

namespace Snap2HTML.Infrastructure.Prerequisites;

/// <summary>
/// Represents an external software component that Snap2HTML depends on for optional features.
/// Implementations detect whether the component is present and, when possible, install it.
/// </summary>
public interface IPrerequisite
{
    /// <summary>
    /// Stable identifier used for lookups (e.g. "SqlLocalDB").
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Human-readable display name shown in the UI.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Short explanation of what this prerequisite enables.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Whether this prerequisite must be present for a core feature to work.
    /// </summary>
    bool IsRequired { get; }

    /// <summary>
    /// Whether Snap2HTML is able to install this prerequisite automatically.
    /// </summary>
    bool CanInstall { get; }

    /// <summary>
    /// Current installation status.
    /// Updated by <see cref="CheckAsync"/> and <see cref="InstallAsync"/>.
    /// </summary>
    PrerequisiteStatus Status { get; }

    /// <summary>
    /// Detects whether the prerequisite is already installed and updates <see cref="Status"/>.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task CheckAsync(CancellationToken ct = default);

    /// <summary>
    /// Installs the prerequisite, reporting progress messages to <paramref name="progress"/>.
    /// Updates <see cref="Status"/> on completion or failure.
    /// Only valid when <see cref="CanInstall"/> is <see langword="true"/>.
    /// </summary>
    /// <param name="progress">Optional sink for human-readable progress messages.</param>
    /// <param name="ct">Cancellation token.</param>
    Task InstallAsync(IProgress<string>? progress = null, CancellationToken ct = default);
}
