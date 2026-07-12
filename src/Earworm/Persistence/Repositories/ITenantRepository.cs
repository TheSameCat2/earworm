using System;
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
    Task<int> NormalizeLegacyGuildIdsAsync();
}

/// <summary>
/// Raised when legacy tenant aliases cannot be canonicalized without either
/// merging distinct tenant rows or overwriting existing per-guild state.
/// No normalization changes are committed when this exception is raised.
/// </summary>
public sealed class TenantIdentityNormalizationException : InvalidOperationException
{
    public TenantIdentityNormalizationException(string message)
        : base(message)
    {
    }

    public TenantIdentityNormalizationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
