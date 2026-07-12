using System.Collections.Generic;
using System.Threading.Tasks;
using Earworm.Persistence.Repositories;

namespace Earworm.Domain.Tenants;

public interface ITenantService
{
    Task<bool> IsAdmittedAsync(string guildId);
    Task AddTenantAsync(string guildId, string? ownerUserId);
    Task RemoveTenantAsync(string guildId);
    Task<IReadOnlyList<TenantRow>> GetAllTenantsAsync();
    Task<int> NormalizeLegacyGuildIdsAsync();
}
