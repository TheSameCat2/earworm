using System.Threading.Tasks;
using System.Collections.Generic;

namespace Earworm.Persistence.Repositories;

/// <summary>
/// Row projection for the <c>tenants</c> table.
/// </summary>
public sealed record TenantRow(
    string GuildId,
    string? OwnerUserId,
    string Plan,
    string Status,
    long CreatedAt);

public interface ITenantRepository
{
    Task<bool> IsAdmittedAsync(string guildId);
    Task AddTenantAsync(string guildId, string? ownerUserId);
    Task RemoveTenantAsync(string guildId);
    Task<IReadOnlyList<TenantRow>> GetAllTenantsAsync();
}
