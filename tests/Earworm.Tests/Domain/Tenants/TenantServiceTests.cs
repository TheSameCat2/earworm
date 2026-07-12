using System;
using System.Threading.Tasks;
using Earworm.Domain.Tenants;
using Earworm.Persistence.Repositories;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Earworm.Tests.Domain.Tenants;

public sealed class TenantServiceTests
{
    [Fact]
    public async Task Service_CanonicalizesGuildIdsAtItsBoundary()
    {
        var repository = Substitute.For<ITenantRepository>();
        repository.IsAdmittedAsync("123").Returns(true);
        var service = new TenantService(repository);

        await service.AddTenantAsync("  +00123 ", "owner");
        (await service.IsAdmittedAsync("00123")).Should().BeTrue();
        await service.RemoveTenantAsync("+123");

        await repository.Received(1).AddTenantAsync("123", "owner");
        await repository.Received(1).IsAdmittedAsync("123");
        await repository.Received(1).RemoveTenantAsync("123");
    }

    [Fact]
    public async Task Service_RejectsInvalidMutationIds_AndTreatsInvalidAdmissionChecksAsFalse()
    {
        var repository = Substitute.For<ITenantRepository>();
        var service = new TenantService(repository);

        (await service.IsAdmittedAsync("not-a-guild")).Should().BeFalse();
        (await service.IsAdmittedAsync("0")).Should().BeFalse();
        Func<Task> add = () => service.AddTenantAsync("not-a-guild", null);
        Func<Task> remove = () => service.RemoveTenantAsync("not-a-guild");

        await add.Should().ThrowAsync<ArgumentException>();
        await remove.Should().ThrowAsync<ArgumentException>();
        await repository.DidNotReceiveWithAnyArgs().IsAdmittedAsync(default!);
    }
}
