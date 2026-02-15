using PrivStack.Sdk.Capabilities;

namespace PrivStack.Desktop.Tests.Sdk;

/// <summary>
/// Concrete implementation for testing IShareableBehavior defaults and behavior.
/// </summary>
internal sealed class TestShareablePlugin : IShareableBehavior
{
    public IReadOnlyList<string> ShareableEntityTypes { get; init; } = new List<string> { "task", "project" }.AsReadOnly();

    public string GetEntityTitle(string entityId) => $"Title-{entityId}";

    public string GetEntityType(string entityId) => "task";
}

/// <summary>
/// Plugin that overrides IsExcludedFromSharing to true (e.g., Calendar, Contacts).
/// </summary>
internal sealed class ExcludedShareablePlugin : IShareableBehavior
{
    public IReadOnlyList<string> ShareableEntityTypes => Array.Empty<string>().AsReadOnly();

    public bool IsExcludedFromSharing => true;

    public string GetEntityTitle(string entityId) => string.Empty;

    public string GetEntityType(string entityId) => string.Empty;
}

public class ShareableBehaviorTests
{
    [Fact]
    public void DefaultIsExcludedFromSharing_ReturnsFalse()
    {
        IShareableBehavior behavior = new TestShareablePlugin();

        behavior.IsExcludedFromSharing.Should().BeFalse();
    }

    [Fact]
    public void ShareableEntityTypes_ReturnsExpectedTypes()
    {
        var plugin = new TestShareablePlugin();

        plugin.ShareableEntityTypes.Should().BeEquivalentTo(new[] { "task", "project" });
    }

    [Fact]
    public void GetEntityTitle_ReturnsExpectedValue()
    {
        var plugin = new TestShareablePlugin();

        plugin.GetEntityTitle("abc-123").Should().Be("Title-abc-123");
    }

    [Fact]
    public void GetEntityType_ReturnsExpectedValue()
    {
        var plugin = new TestShareablePlugin();

        plugin.GetEntityType("abc-123").Should().Be("task");
    }

    [Fact]
    public void ExcludedPlugin_IsExcludedFromSharing_ReturnsTrue()
    {
        IShareableBehavior behavior = new ExcludedShareablePlugin();

        behavior.IsExcludedFromSharing.Should().BeTrue();
    }

    [Fact]
    public void EmptyEntityTypes_ReturnsEmptyList()
    {
        var plugin = new ExcludedShareablePlugin();

        plugin.ShareableEntityTypes.Should().BeEmpty();
    }

    [Fact]
    public void ShareableEntityTypes_IsReadOnly()
    {
        var plugin = new TestShareablePlugin();

        plugin.ShareableEntityTypes.Should().BeAssignableTo<IReadOnlyList<string>>();
    }

    [Fact]
    public void MockedBehavior_ViaSubstitute_WorksCorrectly()
    {
        var mock = Substitute.For<IShareableBehavior>();
        mock.ShareableEntityTypes.Returns(new List<string> { "note", "page" }.AsReadOnly());
        mock.GetEntityTitle("n-1").Returns("My Note");
        mock.GetEntityType("n-1").Returns("note");
        mock.IsExcludedFromSharing.Returns(false);

        mock.ShareableEntityTypes.Should().HaveCount(2);
        mock.GetEntityTitle("n-1").Should().Be("My Note");
        mock.GetEntityType("n-1").Should().Be("note");
        mock.IsExcludedFromSharing.Should().BeFalse();
    }
}
