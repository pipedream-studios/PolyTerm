using System.Text;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace PolyTerm.App.Import;

/// <summary>
/// Decrypts mRemoteNG's "Password" attribute (confVersion ≥ 2.6 / mRemoteNG ≥ 1.75's
/// <c>AeadCryptographyProvider</c>): AES-256-GCM with a key derived via PBKDF2-HMAC-SHA1 (1000
/// iterations) from a 16-byte salt embedded in the ciphertext, and a 16-byte GCM nonce, also embedded.
/// Blob layout (all concatenated, then base64-encoded): salt(16) || nonce(16) || ciphertext || tag(16).
/// The salt doubles as GCM associated data (mirrors mRemoteNG's own <c>SimpleEncrypt</c>/<c>SimpleDecrypt</c>).
/// .NET's built-in <see cref="System.Security.Cryptography.AesGcm"/> only accepts 12-byte nonces on
/// Windows (CNG), so this uses BouncyCastle directly (already a transitive dependency via SSH.NET) —
/// the same library and parameters mRemoteNG's own encryptor uses, confirmed against its source
/// (<c>AeadCryptographyProvider.cs</c>/<c>Pkcs5S2KeyGenerator.cs</c>) and cross-checked against an
/// independent PyCryptodome-based reference implementation of the same documented algorithm.
/// </summary>
internal static class MRemoteNgPasswordCrypto
{
    /// <summary>mRemoteNG's built-in default master password when the user never set one.</summary>
    public const string DefaultMasterPassword = "mR3m";

    private const int SaltLength = 16;
    private const int NonceLength = 16;
    private const int TagBits = 128;
    private const int KeyBits = 256;
    private const int Iterations = 1000;

    /// <summary>
    /// Attempts to decrypt a base64 "Password" attribute value with <paramref name="password"/>.
    /// Returns null (never throws) if the blob is malformed, too short to contain salt+nonce+tag, or
    /// the password is wrong (surfaces as a GCM authentication-tag mismatch).
    /// </summary>
    public static string? TryDecrypt(string base64CipherText, string password)
    {
        if (string.IsNullOrEmpty(base64CipherText)) return "";

        byte[] blob;
        try { blob = Convert.FromBase64String(base64CipherText); }
        catch (FormatException) { return null; }

        if (blob.Length < SaltLength + NonceLength + TagBits / 8) return null;

        var salt = blob[..SaltLength];
        var nonce = blob[SaltLength..(SaltLength + NonceLength)];
        var cipherTextAndTag = blob[(SaltLength + NonceLength)..];

        byte[] key;
        try { key = DeriveKey(password, salt); }
        catch (OverflowException) { return null; } // password has a char outside mRemoteNG's supported Latin-1 range

        try
        {
            var cipher = new GcmBlockCipher(new AesEngine());
            cipher.Init(forEncryption: false, new AeadParameters(new KeyParameter(key), TagBits, nonce, salt));

            var plainText = new byte[cipher.GetOutputSize(cipherTextAndTag.Length)];
            var len = cipher.ProcessBytes(cipherTextAndTag, 0, cipherTextAndTag.Length, plainText, 0);
            len += cipher.DoFinal(plainText, len);

            return Encoding.UTF8.GetString(plainText, 0, len);
        }
        catch (InvalidCipherTextException)
        {
            return null; // wrong master password, or a corrupted/foreign blob
        }
    }

    // Matches Pkcs5S2KeyGenerator.DeriveKey exactly: PBKDF2-HMAC-SHA1, password bytes via
    // PbeParametersGenerator.Pkcs5PasswordToBytes (each UTF-16 char truncated to one byte — throws
    // OverflowException for any char outside 0-255, same as mRemoteNG's own encryption would).
    private static byte[] DeriveKey(string password, byte[] salt)
    {
        var passwordBytes = PbeParametersGenerator.Pkcs5PasswordToBytes(password.ToCharArray());
        var generator = new Pkcs5S2ParametersGenerator(new Sha1Digest());
        generator.Init(passwordBytes, salt, Iterations);
        var keyParam = (KeyParameter)generator.GenerateDerivedMacParameters(KeyBits);
        return keyParam.GetKey();
    }
}
