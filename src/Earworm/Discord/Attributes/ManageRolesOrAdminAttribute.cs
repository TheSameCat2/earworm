using System;
using System.Threading.Tasks;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.SlashCommands;
using DSharpPlus.SlashCommands.Attributes;

namespace Earworm.Discord.Attributes;

/// <summary>
/// /config dj-role and similar. PRD §7 — must be MANAGE_ROLES or
/// ADMINISTRATOR (not DJ role itself, to avoid chicken-and-egg bootstrap).
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class ManageRolesOrAdminAttribute : SlashCheckBaseAttribute
{
    public override Task<bool> ExecuteChecksAsync(InteractionContext ctx)
    {
        if (ctx.Member == null) return Task.FromResult(false);
        bool ok = ctx.Member.Permissions.HasPermission(Permissions.Administrator)
            || ctx.Member.Permissions.HasPermission(Permissions.ManageRoles);
        return Task.FromResult(ok);
    }
}
