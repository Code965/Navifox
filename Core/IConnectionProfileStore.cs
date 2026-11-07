using Navifox.Core.Models;

namespace Navifox.Core;

public interface IConnectionProfileStore
{
    Task<IReadOnlyList<ConnectionProfile>> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(IReadOnlyList<ConnectionProfile> profiles, CancellationToken cancellationToken = default);
}
