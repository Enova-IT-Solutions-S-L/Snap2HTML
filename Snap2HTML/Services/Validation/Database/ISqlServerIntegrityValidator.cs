namespace Snap2HTML.Services.Validation.Database;

/// <summary>
/// Interface for SQL Server data file integrity validation.
/// Extends <see cref="IFileIntegrityValidator"/> with SQL Server MDF/NDF-specific capabilities.
/// </summary>
public interface ISqlServerIntegrityValidator : IFileIntegrityValidator
{
}
