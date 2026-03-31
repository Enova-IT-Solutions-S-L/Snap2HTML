namespace Snap2HTML.Core.Models;

/// <summary>
/// Describes the integrity validation capabilities for a specific file extension.
/// Used to populate the supported formats UI table.
/// </summary>
public sealed record FormatSupportInfo(
    string Category,
    string Extension,
    bool SupportsHeaderValidation,
    bool SupportsFullValidation);
