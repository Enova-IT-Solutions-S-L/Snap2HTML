namespace Snap2HTML.Infrastructure.Prerequisites;

/// <summary>
/// Manages the collection of external software prerequisites for Snap2HTML.
/// </summary>
public interface IPrerequisiteManager
{
    /// <summary>
    /// Returns all registered prerequisites.
    /// </summary>
    IReadOnlyList<IPrerequisite> GetAll();

    /// <summary>
    /// Returns the first registered prerequisite that implements <typeparamref name="T"/>,
    /// or <see langword="null"/> if none is registered.
    /// </summary>
    T? Get<T>() where T : class, IPrerequisite;

    /// <summary>
    /// Runs <see cref="IPrerequisite.CheckAsync"/> on every registered prerequisite
    /// concurrently and waits for all checks to complete.
    /// </summary>
    Task CheckAllAsync(CancellationToken ct = default);
}
