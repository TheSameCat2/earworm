using Xunit;
using FluentAssertions;

namespace Earworm.Tests;

public class PlaceholderTest
{
    [Fact]
    public void Test_ShouldPass_ToVerifyHarness()
    {
        // Arrange
        var isAwesome = true;

        // Act & Assert
        isAwesome.Should().BeTrue();
    }
}
