namespace Snap2HTML.Infrastructure.Prerequisites;

/// <summary>
/// Default implementation of <see cref="IPrerequisiteManager"/>.
/// Prerequisites are registered at construction time and checked in parallel.
/// </summary>
public sealed class PrerequisiteManager : IPrerequisiteManager
{
    private readonly IPrerequisite[] _prerequisites;

    /// <param name="prerequisites">
    /// Ordered list of prerequisites to manage.
    /// </param>
    public PrerequisiteManager(params IPrerequisite[] prerequisites)
    {
        _prerequisites = prerequisites;
    }

    /// <inheritdoc />
    public IReadOnlyList<IPrerequisite> GetAll() => _prerequisites;

    /// <inheritdoc />
    public T? Get<T>() where T : class, IPrerequisite
        => _prerequisites.OfType<T>().FirstOrDefault();

    /// <inheritdoc />
    public Task CheckAllAsync(CancellationToken ct = default)
    {
        var tasks = _prerequisites.Select(p => p.CheckAsync(ct));
        return Task.WhenAll(tasks);
    }
}
