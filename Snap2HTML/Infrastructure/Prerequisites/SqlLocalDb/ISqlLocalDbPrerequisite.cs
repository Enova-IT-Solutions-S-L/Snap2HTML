namespace Snap2HTML.Infrastructure.Prerequisites.SqlLocalDb;

/// <summary>
/// Marker interface for the SQL Server LocalDB prerequisite.
/// Used for typed lookup via <see cref="IPrerequisiteManager.Get{T}"/>.
/// </summary>
public interface ISqlLocalDbPrerequisite : IPrerequisite
{
}
