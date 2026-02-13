using System.Security.Cryptography;
using System.Text;

namespace PrivStack.Desktop.Services.FileSync;

/// <summary>
/// Handles encryption/decryption of file-based sync events using AES-256-GCM.
/// Key is derived from the master password + workspace ID via HKDF (SHA-256).
/// </summary>
internal static class FileEventEncryption
{
    private const int KeyLength = 32;   // AES-256
    private const int NonceLength = 12; // AES-GCM standard
    private const int TagLength = 16;   // AES-GCM standard

    private static readonly byte[] HkdfInfo = "PrivStack-FileSync-v1"u8.ToArray();

    /// <summary>
    /// Derives a 256-bit encryption key from the master password and workspace ID.
    /// Uses HKDF with SHA-256. The workspace ID acts as the salt so each workspace
    /// has a unique key even with the same master password.
    /// </summary>
    public static byte[] DeriveKey(string masterPassword, string workspaceId)
    {
        var ikm = Encoding.UTF8.GetBytes(masterPassword);
        var salt = Encoding.UTF8.GetBytes(workspaceId);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, KeyLength, salt, HkdfInfo);
    }

    /// <summary>
    /// Encrypts plaintext JSON using AES-256-GCM.
    /// Returns raw binary nonce and ciphertext (ciphertext includes appended auth tag).
    /// </summary>
    public static (byte[] Nonce, byte[] CiphertextWithTag) Encrypt(byte[] key, string plaintext)
    {
        return EncryptBytes(key, Encoding.UTF8.GetBytes(plaintext));
    }

    /// <summary>
    /// Encrypts raw bytes using AES-256-GCM.
    /// Used for snapshot data (protobuf-serialized binary, not UTF-8 text).
    /// </summary>
    public static (byte[] Nonce, byte[] CiphertextWithTag) EncryptBytes(byte[] key, byte[] plaintextBytes)
    {
        var nonce = new byte[NonceLength];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagLength];

        using var aes = new AesGcm(key, TagLength);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        // Append tag to ciphertext for a single binary field
        var ciphertextWithTag = new byte[ciphertext.Length + tag.Length];
        Buffer.BlockCopy(ciphertext, 0, ciphertextWithTag, 0, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, ciphertextWithTag, ciphertext.Length, tag.Length);

        return (nonce, ciphertextWithTag);
    }

    /// <summary>
    /// Decrypts ciphertext (with appended auth tag) using AES-256-GCM.
    /// Returns the plaintext JSON string.
    /// </summary>
    public static string Decrypt(byte[] key, byte[] nonce, byte[] ciphertextWithTag)
    {
        return Encoding.UTF8.GetString(DecryptBytes(key, nonce, ciphertextWithTag));
    }

    /// <summary>
    /// Decrypts ciphertext (with appended auth tag) using AES-256-GCM.
    /// Returns raw plaintext bytes. Used for snapshot data (protobuf binary).
    /// </summary>
    public static byte[] DecryptBytes(byte[] key, byte[] nonce, byte[] ciphertextWithTag)
    {
        if (ciphertextWithTag.Length < TagLength)
            throw new CryptographicException("Ciphertext too short — missing auth tag.");

        var ciphertextLength = ciphertextWithTag.Length - TagLength;
        var ciphertext = ciphertextWithTag.AsSpan(0, ciphertextLength);
        var tag = ciphertextWithTag.AsSpan(ciphertextLength, TagLength);

        var plaintext = new byte[ciphertextLength];

        using var aes = new AesGcm(key, TagLength);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return plaintext;
    }

    /// <summary>
    /// Securely clears a key from memory.
    /// </summary>
    public static void ClearKey(byte[]? key)
    {
        if (key != null)
            CryptographicOperations.ZeroMemory(key);
    }
}
