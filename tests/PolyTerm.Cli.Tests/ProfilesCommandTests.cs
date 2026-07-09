using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using PolyTerm.Cli.Commands;
using PolyTerm.Engine;
using Xunit;

namespace PolyTerm.Cli.Tests;

/// <summary>Covers the <c>profiles list/add/delete</c> subcommands — no connect-failure surface here (no session is ever opened), just <see cref="IProfileStore"/> CRUD.</summary>
[Collection("ConsoleRedirect")]
public sealed class ProfilesCommandTests
{
    private static IServiceProvider MakeServices(IProfileStore store) =>
        new ServiceCollection().AddSingleton(store).BuildServiceProvider();

    [Fact]
    public async Task List_PrintsEverySavedProfile()
    {
        var store = new Mock<IProfileStore>();
        store.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new ConnectionProfile { Name = "prod-web", ProtocolId = "ssh", Host = "10.0.0.1", Port = 22 },
            new ConnectionProfile { Name = "db-box", ProtocolId = "ssh", Host = "10.0.0.2", Port = 2222 },
        ]);

        var command = ProfilesCommand.Build(MakeServices(store.Object));

        var (exitCode, output, _) = await ConsoleCapture.RunAsync(command, "list");

        exitCode.Should().Be(0);
        output.Should().Contain("prod-web").And.Contain("10.0.0.1:22");
        output.Should().Contain("db-box").And.Contain("10.0.0.2:2222");
    }

    [Fact]
    public async Task Add_SavesAProfile_WithTheGivenFields()
    {
        var store = new Mock<IProfileStore>();
        ConnectionProfile? saved = null;
        store.Setup(s => s.SaveAsync(It.IsAny<ConnectionProfile>(), It.IsAny<CancellationToken>()))
            .Returns<ConnectionProfile, CancellationToken>((p, _) =>
            {
                saved = p;
                return Task.FromResult(p);
            });

        var command = ProfilesCommand.Build(MakeServices(store.Object));

        var (exitCode, output, _) = await ConsoleCapture.RunAsync(command,
            "add", "--name", "new-box", "--protocol", "ssh", "--host", "10.0.0.5", "--port", "22", "--username", "alice");

        exitCode.Should().Be(0);
        saved.Should().NotBeNull();
        saved!.Name.Should().Be("new-box");
        saved.ProtocolId.Should().Be("ssh");
        saved.Host.Should().Be("10.0.0.5");
        saved.Port.Should().Be(22);
        saved.Settings["username"].GetString().Should().Be("alice");
        output.Should().Contain("Created profile");
    }

    [Fact]
    public async Task Delete_RemovesTheGivenProfile()
    {
        var store = new Mock<IProfileStore>();
        var id = Guid.NewGuid();
        store.Setup(s => s.DeleteAsync(id, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var command = ProfilesCommand.Build(MakeServices(store.Object));

        var (exitCode, output, _) = await ConsoleCapture.RunAsync(command, "delete", id.ToString());

        exitCode.Should().Be(0);
        store.Verify(s => s.DeleteAsync(id, It.IsAny<CancellationToken>()), Times.Once);
        output.Should().Contain("Deleted");
    }
}
