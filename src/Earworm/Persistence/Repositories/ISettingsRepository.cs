using System.Threading.Tasks;

namespace Earworm.Persistence.Repositories;

public interface ISettingsRepository
{
    Task<ulong?> GetDjRoleIdAsync();
    Task SetDjRoleIdAsync(ulong? roleId);
    
    Task<ulong?> GetLoggingChannelIdAsync();
    Task SetLoggingChannelIdAsync(ulong? channelId);
    
    Task<bool> IsDjEnabledAsync();
    Task SetDjEnabledAsync(bool enabled);
}
