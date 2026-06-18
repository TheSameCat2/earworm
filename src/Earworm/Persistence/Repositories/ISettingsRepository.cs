using System.Threading.Tasks;

namespace Earworm.Persistence.Repositories;

public interface ISettingsRepository
{
    Task<ulong?> GetDjRoleIdAsync(string guildId);
    Task SetDjRoleIdAsync(string guildId, ulong? roleId);

    Task<ulong?> GetLoggingChannelIdAsync(string guildId);
    Task SetLoggingChannelIdAsync(string guildId, ulong? channelId);

    Task<bool> IsDjEnabledAsync(string guildId);
    Task SetDjEnabledAsync(string guildId, bool enabled);

    Task<ulong?> GetNowPlayingChannelIdAsync(string guildId);
    Task SetNowPlayingChannelIdAsync(string guildId, ulong? channelId);
}
