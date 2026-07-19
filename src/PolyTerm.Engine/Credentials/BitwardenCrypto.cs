using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;

namespace PolyTerm.Engine.Credentials;

/// <summary>
/// KDF configuration for a Bitwarden/Vaultwarden account, as returned by
/// <c>POST /identity/accounts/prelogin</c>. <see cref="KdfType"/> 0 = PBKDF2-SHA256, 1 = Argon2id.
/// </summary>
public readonly record struct BitwardenKdfConfig(int KdfType, int Iterations, int? MemoryMiB, int? Parallelism);

/// <summary>A parsed Bitwarden "CipherString"/EncString: <c>type.iv|data|mac</c> for symmetric types, <c>type.data</c> for plain-RSA-OAEP types.</summary>
internal readonly struct BitwardenEncString
{
    public int Type { get; }
    public byte[]? Iv { get; }
    public byte[] Data { get; }
    public byte[]? Mac { get; }

    private BitwardenEncString(int type, byte[]? iv, byte[] data, byte[]? mac)
    {
        Type = type;
        Iv = iv;
        Data = data;
        Mac = mac;
    }

    /// <summary>Number of pipe-separated parts expected for each encryption type, mirroring Bitwarden's own <c>EXPECTED_NUM_PARTS_BY_ENCRYPTION_TYPE</c> table.</summary>
    private static int ExpectedParts(int type) => type switch
    {
        0 => 2, // AesCbc256_B64: iv|data
        2 => 3, // AesCbc256_HmacSha256_B64: iv|data|mac
        3 => 1, // Rsa2048_OaepSha256_B64: data
        4 => 1, // Rsa2048_OaepSha1_B64: data
        _ => throw new NotSupportedException($"Unsupported Bitwarden encryption type {type}."),
    };

    /// <exception cref="FormatException">The string isn't a well-formed EncString.</exception>
    /// <exception cref="NotSupportedException">The encryption type isn't one this parser handles.</exception>
    public static BitwardenEncString Parse(string encString)
    {
        var dot = encString.IndexOf('.');
        if (dot < 0 || !int.TryParse(encString[..dot], out var type))
            throw new FormatException($"Malformed Bitwarden EncString (no type prefix): '{encString}'.");

        var parts = encString[(dot + 1)..].Split('|');
        var expected = ExpectedParts(type);
        if (parts.Length != expected)
            throw new FormatException($"Malformed Bitwarden EncString: type {type} expects {expected} part(s), got {parts.Length}.");

        return type switch
        {
            0 => new BitwardenEncString(type, Convert.FromBase64String(parts[0]), Convert.FromBase64String(parts[1]), null),
            2 => new BitwardenEncString(type, Convert.FromBase64String(parts[0]), Convert.FromBase64String(parts[1]), Convert.FromBase64String(parts[2])),
            3 or 4 => new BitwardenEncString(type, null, Convert.FromBase64String(parts[0]), null),
            _ => throw new NotSupportedException($"Unsupported Bitwarden encryption type {type}."),
        };
    }
}

/// <summary>
/// Client-side Bitwarden/Vaultwarden vault cryptography — the piece that replaces what <c>bw</c>
/// CLI/<c>bw serve</c> used to do for us (issue #78's CLI-dependency removal). Implements the same
/// KDF → stretch → decrypt chain as Bitwarden's own official clients; algorithm details verified
/// against Bitwarden's security whitepaper, the current <c>bitwarden/clients</c> SDK source, and
/// Vaultwarden's server source (which confirms the wire format without ever performing decryption
/// itself — that only ever happens client-side, per Bitwarden's zero-knowledge design).
/// </summary>
internal static class BitwardenCrypto
{
    /// <summary>
    /// Derives the 256-bit Master Key from the master password and account email (used as salt,
    /// lowercased/trimmed per Bitwarden convention), per the account's KDF config from prelogin.
    /// </summary>
    public static byte[] DeriveMasterKey(string password, string email, BitwardenKdfConfig kdf)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        var saltBytes = Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant());

        return kdf.KdfType switch
        {
            0 => Rfc2898DeriveBytes.Pbkdf2(passwordBytes, saltBytes, kdf.Iterations, HashAlgorithmName.SHA256, 32),
            1 => DeriveArgon2id(passwordBytes, saltBytes, kdf),
            _ => throw new NotSupportedException($"Unsupported Bitwarden KDF type {kdf.KdfType}."),
        };
    }

    private static byte[] DeriveArgon2id(byte[] password, byte[] salt, BitwardenKdfConfig kdf)
    {
        if (kdf.MemoryMiB is not { } memoryMiB || kdf.Parallelism is not { } parallelism)
            throw new ArgumentException("Argon2id KDF requires Memory and Parallelism from prelogin.", nameof(kdf));

        // Bitwarden's clients report memory in MiB but Argon2's own parameter is KiB (see
        // DefaultKeyGenerationService.deriveKeyFromPassword's kdfConfig.toSdkConfig() call).
        var parameters = new Argon2Parameters.Builder(Argon2Parameters.Argon2id)
            .WithIterations(kdf.Iterations)
            .WithMemoryAsKB(memoryMiB * 1024)
            .WithParallelism(parallelism)
            .WithSalt(salt)
            .Build();

        var generator = new Argon2BytesGenerator();
        generator.Init(parameters);
        var output = new byte[32];
        generator.GenerateBytes(password, output);
        return output;
    }

    /// <summary>
    /// The "Master Password Hash" actually transmitted to the server: PBKDF2-SHA256(payload=masterKey,
    /// salt=password, iterations=1), base64-encoded. Never send <paramref name="masterKey"/> itself.
    /// </summary>
    public static string ComputeMasterPasswordHash(byte[] masterKey, string password)
    {
        var hash = Rfc2898DeriveBytes.Pbkdf2(masterKey, Encoding.UTF8.GetBytes(password), 1, HashAlgorithmName.SHA256, 32);
        return Convert.ToBase64String(hash);
    }

    /// <summary>
    /// Stretches the 256-bit Master Key into a 512-bit Stretched Master Key: HKDF-Expand-SHA256 with
    /// info "enc" and "mac" (32 bytes each), concatenated — matches DefaultKeyGenerationService.stretchKey.
    /// </summary>
    public static byte[] StretchKey(byte[] masterKey)
    {
        var stretched = new byte[64];
        HKDF.Expand(HashAlgorithmName.SHA256, masterKey, stretched.AsSpan(0, 32), "enc"u8);
        HKDF.Expand(HashAlgorithmName.SHA256, masterKey, stretched.AsSpan(32, 32), "mac"u8);
        return stretched;
    }

    /// <summary>
    /// Decrypts a symmetric (type 0/2) EncString using a 64-byte key (first 32 bytes = AES key, last
    /// 32 = MAC key — the same split used for the Stretched Master Key, User Symmetric Key, and
    /// Organization Symmetric Key alike). Verifies the MAC (constant-time) before decrypting
    /// (encrypt-then-MAC — verify first, never decrypt untrusted ciphertext blind).
    /// </summary>
    /// <exception cref="CryptographicException">The MAC doesn't match (wrong key, or tampered/corrupt data).</exception>
    public static byte[] DecryptSymmetric(BitwardenEncString enc, byte[] key)
    {
        if (key.Length != 64) throw new ArgumentException("Symmetric key must be 64 bytes (32 enc + 32 mac).", nameof(key));
        var encKey = key.AsSpan(0, 32);
        var macKey = key.AsSpan(32, 32);

        if (enc.Type == 2)
        {
            if (enc.Iv is null || enc.Mac is null) throw new ArgumentException("Type 2 EncString requires iv and mac.", nameof(enc));

            var macInput = new byte[enc.Iv.Length + enc.Data.Length];
            enc.Iv.CopyTo(macInput, 0);
            enc.Data.CopyTo(macInput, enc.Iv.Length);
            var computedMac = HMACSHA256.HashData(macKey, macInput);

            if (!CryptographicOperations.FixedTimeEquals(computedMac, enc.Mac))
                throw new CryptographicException("Bitwarden EncString MAC verification failed.");
        }
        else if (enc.Type != 0)
        {
            throw new NotSupportedException($"DecryptSymmetric doesn't support encryption type {enc.Type}.");
        }

        using var aes = Aes.Create();
        aes.Key = encKey.ToArray();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var decryptor = aes.CreateDecryptor(aes.Key, enc.Iv);
        return decryptor.TransformFinalBlock(enc.Data, 0, enc.Data.Length);
    }

    /// <summary>Convenience wrapper: decrypts a symmetric EncString and decodes it as UTF-8 text.</summary>
    public static string DecryptSymmetricString(BitwardenEncString enc, byte[] key) =>
        Encoding.UTF8.GetString(DecryptSymmetric(enc, key));

    /// <summary>
    /// Decrypts an asymmetric (type 3/4, plain RSA-OAEP, no separate MAC) EncString with an RSA
    /// private key — used to unwrap an organization's symmetric key from the user's profile/sync
    /// response. Types 5/6 (RSA-OAEP + an additional HMAC layer) aren't supported: not observed from
    /// Vaultwarden in practice, and worth a real failure rather than a silent wrong decrypt if ever hit.
    /// </summary>
    public static byte[] DecryptAsymmetric(BitwardenEncString enc, RSA privateKey) => enc.Type switch
    {
        3 => privateKey.Decrypt(enc.Data, RSAEncryptionPadding.OaepSHA256),
        4 => privateKey.Decrypt(enc.Data, RSAEncryptionPadding.OaepSHA1),
        _ => throw new NotSupportedException($"DecryptAsymmetric doesn't support encryption type {enc.Type}."),
    };
}
