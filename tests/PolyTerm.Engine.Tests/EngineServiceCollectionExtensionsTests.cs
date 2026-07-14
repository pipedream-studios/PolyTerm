using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PolyTerm.Engine;
using PolyTerm.Engine.Storage;
using Xunit;

namespace PolyTerm.Engine.Tests;

/// <summary>
/// Covers <c>AddPolyTermEngine</c>'s no-options fallback path - the one PolyTerm.Cli actually uses
/// (PolyTerm.App always passes an explicit <see cref="EngineOptions.DatabasePath"/> and never reaches
/// this). Confirms <c>POLYTERM_DATA_DIR</c> redirects it, since without that override PolyTerm.Cli had
/// no way to avoid writing straight into the real per-user vault - found via a real manual smoke test
/// of the built exe, not a hypothetical.
/// </summary>
public sealed class EngineServiceCollectionExtensionsTests : IDisposable
{
    private readonly string _isolatedDir;

    public EngineServiceCollectionExtensionsTests()
    {
        _isolatedDir = Path.Combine(Path.GetTempPath(), $"polyterm-engine-test-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("POLYTERM_DATA_DIR", _isolatedDir);
    }

    [Fact]
    public async Task NoOptions_HonorsPolyTermDataDirOverride_InsteadOfTheRealPerUserVault()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Logging.ILogger<ProfileStore>>(NullLogger<ProfileStore>.Instance);
        services.AddSingleton<Microsoft.Extensions.Logging.ILogger<HostKeyStore>>(NullLogger<HostKeyStore>.Instance);
        services.AddPolyTermEngine();

        var store = services.BuildServiceProvider().GetRequiredService<IProfileStore>();
        await store.SaveAsync(new ConnectionProfile { Name = "isolation-check", ProtocolId = "ssh" });

        File.Exists(Path.Combine(_isolatedDir, "PolyTerm.db")).Should().BeTrue(
            "AddPolyTermEngine() with no options should fall back to POLYTERM_DATA_DIR, not the real per-user vault");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("POLYTERM_DATA_DIR", null);
        try { Directory.Delete(_isolatedDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }
}
