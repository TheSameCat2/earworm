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
        DiscordGuildId.TryNormalize(guildId, out var canonicalGuildId)
            ? _repository.IsAdmittedAsync(canonicalGuildId)
            : Task.FromResult(false);

    public Task AddTenantAsync(string guildId, string? ownerUserId) =>
        _repository.AddTenantAsync(
            DiscordGuildId.Normalize(guildId, nameof(guildId)),
            ownerUserId);

    public Task RemoveTenantAsync(string guildId) =>
        _repository.RemoveTenantAsync(
            DiscordGuildId.Normalize(guildId, nameof(guildId)));

    public Task<IReadOnlyList<TenantRow>> GetAllTenantsAsync() =>
        _repository.GetAllTenantsAsync();

    public Task<int> NormalizeLegacyGuildIdsAsync() =>
        _repository.NormalizeLegacyGuildIdsAsync();
}
