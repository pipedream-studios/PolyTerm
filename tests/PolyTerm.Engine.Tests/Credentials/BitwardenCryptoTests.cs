using System.Security.Cryptography;
using AwesomeAssertions;
using PolyTerm.Engine.Credentials;
using Xunit;

namespace PolyTerm.Engine.Tests.Credentials;

/// <summary>
/// Covers the crypto core of issue #78's CLI-dependency removal: <see cref="BitwardenCrypto"/>
/// reimplements what <c>bw</c>/<c>bw serve</c> used to do for us, so this is the security-critical
/// piece verified independently of any real Bitwarden/Vaultwarden server. Where possible, expected
/// values come from an independent, trusted reference (RFC 5869's own published test vectors for
/// HKDF; Python's <c>hashlib.pbkdf2_hmac</c> for PBKDF2 — not hand-derived, not "whatever this code
/// happens to produce").
/// </summary>
public sealed class BitwardenCryptoTests
{
    // ─── HKDF-Expand (RFC 5869 §A.1, Test Case 1, SHA-256) ─────────────────────
    // Bitwarden's own stretchKey calls HKDF-Expand directly on the master key (no separate
    // HKDF-Extract step - see DefaultKeyGenerationService.stretchKey), so this test feeds the RFC's
    // published PRK straight into HKDF.Expand rather than re-deriving it from the RFC's IKM/salt.

    [Fact]
    public void HkdfExpand_MatchesRfc5869TestVector()
    {
        var prk = Convert.FromHexString("077709362c2e32df0ddc3f0dc47bba6390b6c73bb50f9c3122ec844ad7c2b3e5");
        var info = Convert.FromHexString("f0f1f2f3f4f5f6f7f8f9");
        var expectedOkm = Convert.FromHexString("3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865");

        var okm = new byte[42];
        HKDF.Expand(HashAlgorithmName.SHA256, prk, okm, info);

        okm.Should().Equal(expectedOkm);
    }

    [Fact]
    public void StretchKey_Produces64Bytes_EncAndMacHalvesDiffer()
    {
        var masterKey = Convert.FromHexString("8961c50dae22acabc2cd49116c006c4a2fc67569346d46449d34da1f83eaf68c");

        var stretched = BitwardenCrypto.StretchKey(masterKey);

        stretched.Should().HaveCount(64);
        stretched[..32].Should().NotEqual(stretched[32..], "enc and mac halves are independently derived (different HKDF info strings) and should never coincide");
    }

    // ─── KDF (PBKDF2-SHA256 vectors independently computed via Python's hashlib.pbkdf2_hmac) ──

    [Fact]
    public void DeriveMasterKey_Pbkdf2_MatchesIndependentlyComputedVector()
    {
        // hashlib.pbkdf2_hmac('sha256', b'testpassword123', b'test@example.com', 100000, 32)
        var expected = Convert.FromHexString("8961c50dae22acabc2cd49116c006c4a2fc67569346d46449d34da1f83eaf68c");

        var masterKey = BitwardenCrypto.DeriveMasterKey("testpassword123", "test@example.com", new BitwardenKdfConfig(0, 100000, null, null));

        masterKey.Should().Equal(expected);
    }

    [Fact]
    public void DeriveMasterKey_LowercasesAndTrimsEmailForSalt()
    {
        var expected = BitwardenCrypto.DeriveMasterKey("testpassword123", "test@example.com", new BitwardenKdfConfig(0, 100000, null, null));

        var mixedCase = BitwardenCrypto.DeriveMasterKey("testpassword123", "  Test@Example.COM  ", new BitwardenKdfConfig(0, 100000, null, null));

        mixedCase.Should().Equal(expected, "the KDF salt must match the server's own email normalization or the derived key would never match a real account");
    }

    [Fact]
    public void ComputeMasterPasswordHash_MatchesIndependentlyComputedVector()
    {
        // hashlib.pbkdf2_hmac('sha256', bytes(range(32)), b'testpassword123', 1, 32), base64-encoded
        var masterKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

        var hash = BitwardenCrypto.ComputeMasterPasswordHash(masterKey, "testpassword123");

        hash.Should().Be("lFEkEfjz08oSgLimCs5NBFasv5QqOzW7IG8Sx1gKeHk=");
    }

    [Fact]
    public void DeriveMasterKey_Argon2id_IsDeterministicAndSensitiveToEachParameter()
    {
        var kdf = new BitwardenKdfConfig(1, Iterations: 3, MemoryMiB: 8, Parallelism: 1);

        var first = BitwardenCrypto.DeriveMasterKey("testpassword123", "test@example.com", kdf);
        var again = BitwardenCrypto.DeriveMasterKey("testpassword123", "test@example.com", kdf);
        var differentMemory = BitwardenCrypto.DeriveMasterKey("testpassword123", "test@example.com", kdf with { MemoryMiB = 16 });
        var differentIterations = BitwardenCrypto.DeriveMasterKey("testpassword123", "test@example.com", kdf with { Iterations = 4 });

        first.Should().HaveCount(32).And.Equal(again, "Argon2id must be deterministic for identical inputs, same as any KDF");
        first.Should().NotEqual(differentMemory);
        first.Should().NotEqual(differentIterations);
    }

    [Fact]
    public void DeriveMasterKey_Argon2id_WithoutMemoryOrParallelism_Throws()
    {
        var act = () => BitwardenCrypto.DeriveMasterKey("pw", "e@example.com", new BitwardenKdfConfig(1, 3, null, null));
        act.Should().Throw<ArgumentException>("prelogin must supply Memory/Parallelism for Argon2id accounts - silently defaulting them would derive the wrong key");
    }

    // ─── EncString parsing ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("not-a-valid-encstring")]
    [InlineData("2.onlyoneiv")]
    [InlineData("2.iv|data")] // type 2 needs 3 parts (iv|data|mac), not 2
    public void EncStringParse_RejectsMalformedInput(string malformed)
    {
        var act = () => BitwardenEncString.Parse(malformed);
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void EncStringParse_RejectsUnsupportedType()
    {
        var act = () => BitwardenEncString.Parse("99.data");
        act.Should().Throw<NotSupportedException>();
    }

    // ─── Symmetric encrypt/decrypt round-trip + tamper detection ───────────────
    // No independent oracle for the full AES-CBC+HMAC scheme is used here (that can only really be
    // confirmed against a real server's actual ciphertext) - this instead confirms internal
    // consistency: encrypting with .NET's own primitives per the documented scheme, then decrypting
    // with BitwardenCrypto, recovers the original plaintext, and that tampering is caught.

    [Fact]
    public void DecryptSymmetric_RoundTrips_TypeTwoAesCbcHmac()
    {
        var key = RandomNumberGenerator.GetBytes(64); // 32 enc + 32 mac
        var encString = EncryptForTest("the quick brown fox", key);

        var decrypted = BitwardenCrypto.DecryptSymmetricString(BitwardenEncString.Parse(encString), key);

        decrypted.Should().Be("the quick brown fox");
    }

    [Fact]
    public void DecryptSymmetric_TamperedMac_ThrowsInsteadOfReturningGarbage()
    {
        var key = RandomNumberGenerator.GetBytes(64);
        var encString = EncryptForTest("sensitive value", key);
        var tampered = FlipLastBase64Char(encString);

        var act = () => BitwardenCrypto.DecryptSymmetric(BitwardenEncString.Parse(tampered), key);

        act.Should().Throw<CryptographicException>("a tampered/wrong-key ciphertext must fail loudly, never silently decrypt to the wrong plaintext");
    }

    [Fact]
    public void DecryptSymmetric_WrongKey_ThrowsInsteadOfReturningGarbage()
    {
        var encString = EncryptForTest("sensitive value", RandomNumberGenerator.GetBytes(64));
        var wrongKey = RandomNumberGenerator.GetBytes(64);

        var act = () => BitwardenCrypto.DecryptSymmetric(BitwardenEncString.Parse(encString), wrongKey);

        act.Should().Throw<CryptographicException>();
    }

    private static string EncryptForTest(string plaintext, byte[] key)
    {
        var encKey = key[..32];
        var macKey = key[32..];

        using var aes = Aes.Create();
        aes.Key = encKey;
        aes.GenerateIV();
        using var encryptor = aes.CreateEncryptor();
        var plainBytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
        var cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        var macInput = new byte[aes.IV.Length + cipherBytes.Length];
        aes.IV.CopyTo(macInput, 0);
        cipherBytes.CopyTo(macInput, aes.IV.Length);
        var mac = HMACSHA256.HashData(macKey, macInput);

        return $"2.{Convert.ToBase64String(aes.IV)}|{Convert.ToBase64String(cipherBytes)}|{Convert.ToBase64String(mac)}";
    }

    /// <summary>
    /// Flips the *first* character of the mac segment - deliberately not one adjacent to its base64
    /// padding. HMAC-SHA256's 32-byte MAC base64-encodes to 44 chars with exactly one '=' pad, and the
    /// character immediately before that pad has its bottom 2 bits unused by the decoder (real base64
    /// padding, not content) - flipping between two characters sharing those bottom 2 bits (e.g. 'A'
    /// index 0 and 'B' index 1, both index/4 == 0) silently decodes to byte-identical output. That
    /// happens whenever the original character is 'A'/'B'/'C'/'D' - 1-in-16 by chance across random
    /// keys/IVs - and intermittently failed this test for a reason that had nothing to do with
    /// BitwardenCrypto's actual tamper detection (caught via a real, reproduced-in-CI flake, root
    /// caused by working through the base64 bit layout by hand). The first character of any base64
    /// group has no such ambiguity: it fully determines the top 6 bits of that group's first byte
    /// regardless of what follows, so any change there always changes real decoded content.
    /// </summary>
    private static string FlipLastBase64Char(string encString)
    {
        var macStart = encString.LastIndexOf('|') + 1;
        var chars = encString.ToCharArray();
        chars[macStart] = chars[macStart] == 'A' ? 'B' : 'A';
        return new string(chars);
    }
}
