using System.Collections.Generic;
using System.Threading.Tasks;
using Earworm.Persistence.Repositories;

namespace Earworm.Domain.Tenants;

public sealed class TenantService : ITenantService
{
    private readonly ITenantRepository _repository;

    public TenantService(ITenantRepository repository)
    {
        _repository = repository;
    }

    public Task<bool> IsAdmittedAsync(string guildId) =>
        _repository.IsAdmittedAsync(guildId);

    public Task AddTenantAsync(string guildId, string? ownerUserId) =>
        _repository.AddTenantAsync(guildId, ownerUserId);

    public Task RemoveTenantAsync(string guildId) =>
        _repository.RemoveTenantAsync(guildId);

    public Task<IReadOnlyList<TenantRow>> GetAllTenantsAsync() =>
        _repository.GetAllTenantsAsync();
}
