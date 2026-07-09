using System.Text;
using FluentAssertions;
using PolyTerm.App.Import;
using Xunit;

namespace PolyTerm.App.Tests;

public sealed class MRemoteNgImporterTests
{
    private static ImportResult ImportXml(string xml) =>
        MRemoteNgImporter.Import(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

    [Fact]
    public void Import_RootLevelSshConnection_MapsCorrectly()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Connections>
              <Node Name="MyServer" Type="Connection" Protocol="SSH2" Hostname="10.0.0.1" Port="22" Username="alice" />
            </Connections>
            """;

        var result = ImportXml(xml);

        result.Profiles.Should().ContainSingle();
        var profile = result.Profiles[0];
        profile.Name.Should().Be("MyServer");
        profile.ProtocolId.Should().Be("ssh");
        profile.Host.Should().Be("10.0.0.1");
        profile.Port.Should().Be(22);
        profile.Settings.Should().ContainKey("username")
            .WhoseValue.GetString().Should().Be("alice");
    }

    [Theory]
    [InlineData("SSH2", "ssh")]
    [InlineData("SSH1", "ssh")]
    [InlineData("RDP", "rdp")]
    [InlineData("VNC", "vnc")]
    public void Import_MapsMRemoteNgProtocol_ToExpectedProtocolId(string mrnProtocol, string expected)
    {
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Connections>
              <Node Name="Session1" Type="Connection" Protocol="{mrnProtocol}" Hostname="host.example.com" />
            </Connections>
            """;

        var result = ImportXml(xml);

        result.Profiles.Should().ContainSingle();
        result.Profiles[0].ProtocolId.Should().Be(expected);
    }

    [Fact]
    public void Import_UnsupportedProtocol_IsSkippedWithReason()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Connections>
              <Node Name="PuttySession" Type="Connection" Protocol="RAW" Hostname="host.example.com" />
            </Connections>
            """;

        var result = ImportXml(xml);

        result.Profiles.Should().BeEmpty();
        result.Skipped.Should().ContainSingle(s => s.Contains("PuttySession") && s.Contains("RAW"));
    }

    /// <summary>Unknown *node* types (not unsupported protocols) are silently skipped — no entry added to Skipped at all. Verified against the source comment ("Unknown / unsupported types (e.g. PuttySession) — skip silently") since this is easy to get backwards from the protocol-skip case above.</summary>
    [Fact]
    public void Import_UnknownNodeType_IsSilentlyIgnored_NoSkippedEntry()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Connections>
              <Node Name="SomethingElse" Type="PuttySession" />
            </Connections>
            """;

        var result = ImportXml(xml);

        result.Profiles.Should().BeEmpty();
        result.Skipped.Should().BeEmpty();
    }

    [Fact]
    public void Import_MissingPort_FallsBackToProtocolDefault()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Connections>
              <Node Name="NoPort" Type="Connection" Protocol="RDP" Hostname="host.example.com" />
            </Connections>
            """;

        var result = ImportXml(xml);

        result.Profiles.Should().ContainSingle();
        result.Profiles[0].Port.Should().Be(3389);
    }

    [Fact]
    public void Import_NestedFolders_CreateChainWithParentsBeforeChildren()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Connections>
              <Node Name="Servers" Type="Container">
                <Node Name="Prod" Type="Container">
                  <Node Name="ProdServer" Type="Connection" Protocol="SSH2" Hostname="10.0.0.5" Port="22" Username="admin" />
                </Node>
              </Node>
            </Connections>
            """;

        var result = ImportXml(xml);

        var outer = result.Profiles.Single(p => p.Name == "Servers");
        var inner = result.Profiles.Single(p => p.Name == "Prod");
        var session = result.Profiles.Single(p => p.Name == "ProdServer");

        outer.IsFolder.Should().BeTrue();
        outer.ParentId.Should().BeNull();
        inner.IsFolder.Should().BeTrue();
        inner.ParentId.Should().Be(outer.Id);
        session.ParentId.Should().Be(inner.Id);

        var order = result.Profiles.ToList();
        order.IndexOf(outer).Should().BeLessThan(order.IndexOf(inner));
        order.IndexOf(inner).Should().BeLessThan(order.IndexOf(session));
    }

    [Fact]
    public void Import_NeverAttachesCredentialDirectly_EvenWhenAPasswordDecryptsSuccessfully()
    {
        // Attaching a real Credential means writing to the OS keychain (async I/O) — deliberately
        // the caller's job (MainWindow.ImportMRemoteNg_Click), not this pure XML-parsing importer's.
        // See MRemoteNgPasswordCryptoTests for the actual decryption coverage.
        var encrypted = EncryptForTest("hunter2", "mR3m");
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Connections>
              <Node Name="MyServer" Type="Connection" Protocol="SSH2" Hostname="10.0.0.1" Username="alice" Password="{encrypted}" />
            </Connections>
            """;

        var result = ImportXml(xml);

        result.Profiles[0].Credential.Should().BeNull();
        result.Profiles[0].Settings.Should().NotContainKey("password");
        result.DecryptedPasswords.Should().ContainKey(result.Profiles[0].Id).WhoseValue.Should().Be("hunter2");
    }

    [Fact]
    public void Import_DecryptsPassword_WithMRemoteNgsDefaultMasterPassword_WhenNoneIsGiven()
    {
        var encrypted = EncryptForTest("s3cr3t", "mR3m");
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Connections>
              <Node Name="MyServer" Type="Connection" Protocol="SSH2" Hostname="10.0.0.1" Password="{encrypted}" />
            </Connections>
            """;

        var result = ImportXml(xml); // no master password argument — should default to "mR3m"

        result.DecryptedPasswords.Should().ContainKey(result.Profiles[0].Id).WhoseValue.Should().Be("s3cr3t");
        result.PasswordDecryptFailedCount.Should().Be(0);
    }

    [Fact]
    public void Import_DecryptsPassword_WithAnExplicitlyProvidedMasterPassword()
    {
        var encrypted = EncryptForTest("s3cr3t", "correct-horse-battery-staple");
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Connections>
              <Node Name="MyServer" Type="Connection" Protocol="SSH2" Hostname="10.0.0.1" Password="{encrypted}" />
            </Connections>
            """;

        var result = MRemoteNgImporter.Import(new MemoryStream(Encoding.UTF8.GetBytes(xml)), "correct-horse-battery-staple");

        result.DecryptedPasswords.Should().ContainKey(result.Profiles[0].Id).WhoseValue.Should().Be("s3cr3t");
    }

    [Fact]
    public void Import_CountsFailedDecryption_WithoutThrowing_WhenMasterPasswordIsWrong()
    {
        var encrypted = EncryptForTest("s3cr3t", "the-real-password");
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Connections>
              <Node Name="MyServer" Type="Connection" Protocol="SSH2" Hostname="10.0.0.1" Password="{encrypted}" />
            </Connections>
            """;

        var result = ImportXml(xml); // tries the wrong (default) master password

        result.Profiles.Should().ContainSingle("import proceeds even though the password didn't decrypt");
        result.DecryptedPasswords.Should().BeEmpty();
        result.PasswordDecryptFailedCount.Should().Be(1);
    }

    [Fact]
    public void Import_TreatsAnEmptyPasswordAttribute_AsNoPassword_NotAFailure()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Connections>
              <Node Name="MyServer" Type="Connection" Protocol="SSH2" Hostname="10.0.0.1" Password="" />
            </Connections>
            """;

        var result = ImportXml(xml);

        result.DecryptedPasswords.Should().BeEmpty();
        result.PasswordDecryptFailedCount.Should().Be(0);
    }

    // Mirrors mRemoteNG's own AeadCryptographyProvider.SimpleEncrypt exactly (random salt/nonce, salt
    // as AAD, tag appended to ciphertext) — same helper as MRemoteNgPasswordCryptoTests, duplicated
    // here so this test file doesn't depend on that one's internals.
    private static string EncryptForTest(string plainText, string password)
    {
        var salt = new byte[16];
        var nonce = new byte[16];
        System.Security.Cryptography.RandomNumberGenerator.Fill(salt);
        System.Security.Cryptography.RandomNumberGenerator.Fill(nonce);

        var passwordBytes = Org.BouncyCastle.Crypto.PbeParametersGenerator.Pkcs5PasswordToBytes(password.ToCharArray());
        var generator = new Org.BouncyCastle.Crypto.Generators.Pkcs5S2ParametersGenerator(new Org.BouncyCastle.Crypto.Digests.Sha1Digest());
        generator.Init(passwordBytes, salt, 1000);
        var key = ((Org.BouncyCastle.Crypto.Parameters.KeyParameter)generator.GenerateDerivedMacParameters(256)).GetKey();

        var cipher = new Org.BouncyCastle.Crypto.Modes.GcmBlockCipher(new Org.BouncyCastle.Crypto.Engines.AesEngine());
        cipher.Init(true, new Org.BouncyCastle.Crypto.Parameters.AeadParameters(new Org.BouncyCastle.Crypto.Parameters.KeyParameter(key), 128, nonce, salt));

        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var cipherText = new byte[cipher.GetOutputSize(plainBytes.Length)];
        var len = cipher.ProcessBytes(plainBytes, 0, plainBytes.Length, cipherText, 0);
        len += cipher.DoFinal(cipherText, len);

        var blob = new byte[salt.Length + nonce.Length + len];
        Buffer.BlockCopy(salt, 0, blob, 0, salt.Length);
        Buffer.BlockCopy(nonce, 0, blob, salt.Length, nonce.Length);
        Buffer.BlockCopy(cipherText, 0, blob, salt.Length + nonce.Length, len);

        return Convert.ToBase64String(blob);
    }

    [Fact]
    public void Import_MalformedXml_ThrowsFormatException()
    {
        var act = () => ImportXml("this is not xml at all <<<");

        act.Should().Throw<FormatException>().WithMessage("*not valid XML*");
    }

    [Fact]
    public void Import_WrongRootElement_ThrowsFormatException()
    {
        var xml = """<?xml version="1.0"?><NotConnections></NotConnections>""";

        var act = () => ImportXml(xml);

        act.Should().Throw<FormatException>().WithMessage("*Connections*");
    }
}
