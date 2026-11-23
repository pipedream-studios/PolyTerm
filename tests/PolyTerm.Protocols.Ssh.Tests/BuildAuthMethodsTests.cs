using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using PolyTerm.Engine;
using Renci.SshNet;
using Xunit;

namespace PolyTerm.Protocols.Ssh.Tests;

/// <summary>
/// Which auth methods <see cref="SshSession.BuildAuthMethods"/> offers for a resolved credential. Pure logic,
/// so these need no real sshd and run on every platform.
/// </summary>
public sealed class BuildAuthMethodsTests
{
    /// <summary>A throwaway, passphrase-less Ed25519 key in OpenSSH format, the shape Bitwarden stores SSH Key items in. Used for nothing else.</summary>
    private const string TestEd25519Key = """
        -----BEGIN OPENSSH PRIVATE KEY-----
        b3BlbnNzaC1rZXktdjEAAAAABG5vbmUAAAAEbm9uZQAAAAAAAAABAAAAMwAAAAtzc2gtZW
        QyNTUxOQAAACBcuuYFMeOWWNHGHEh6Vw3iYSqF9USjEQ6XtdE1HM8b9AAAAIgHGPqQBxj6
        kAAAAAtzc2gtZWQyNTUxOQAAACBcuuYFMeOWWNHGHEh6Vw3iYSqF9USjEQ6XtdE1HM8b9A
        AAAEAVwQV8eM1eVq+mA+u96QpCRNH/kcw83Yg8xOuZJsGoEFy65gUx45ZY0cYcSHpXDeJh
        KoX1RKMRDpe10TUczxv0AAAAAAECAwQF
        -----END OPENSSH PRIVATE KEY-----

        """;

    /// <summary>Issue #283: a Bitwarden SSH Key item resolves to a private key and no password, so only public-key auth is offered.</summary>
    [Fact]
    public void PrivateKeyOnlyCredential_OffersOnlyPublicKeyAuth()
    {
        using var credential = new CredentialMaterial { PrivateKey = Encoding.UTF8.GetBytes(TestEd25519Key) };

        var methods = SshSession.BuildAuthMethods("deploy", credential, new Dictionary<string, JsonElement>()).ToList();

        methods.Should().ContainSingle().Which.Should().BeOfType<PrivateKeyAuthenticationMethod>()
            .Which.Username.Should().Be("deploy");
    }

    [Fact]
    public void PasswordOnlyCredential_OffersPasswordAndKeyboardInteractive_ButNoPublicKey()
    {
        using var credential = new CredentialMaterial { Username = "root", Secret = Encoding.UTF8.GetBytes("pw") };

        var methods = SshSession.BuildAuthMethods("root", credential, new Dictionary<string, JsonElement>()).ToList();

        methods.Select(m => m.GetType()).Should().Equal(typeof(PasswordAuthenticationMethod), typeof(KeyboardInteractiveAuthenticationMethod));
    }
}
