using System.Buffers.Binary;
using System.Security.Cryptography;
using KitStack.Abstractions.Exceptions;
using KitStack.Abstractions.Options;

namespace KitStack.Abstractions.Security;

/// <summary>
/// Streaming AES-256-GCM encryption at rest.
/// </summary>
/// <remarks>
/// Format (version 1):
/// <code>
/// header : "KSENC" | 0x01 | keyId (4) | salt (16) | chunkSize (4, big-endian)
/// chunk  : final flag (1) | plaintext length (4, big-endian) | ciphertext | GCM tag (16)
/// </code>
/// A per-file key is derived with HKDF-SHA256 from the master key and the random salt. Each chunk uses
/// the chunk index as nonce and authenticates the header and its final flag, so chunks cannot be
/// reordered, dropped, or truncated without detection. The key id (first 4 bytes of SHA-256 of the
/// master key) selects the right key when <see cref="StorageOptions.PreviousEncryptionKeys"/> are configured.
/// </remarks>
public sealed class AesGcmFileContentEncryptor : IFileContentEncryptor
{
    private const int ChunkSize = 64 * 1024;
    private const int TagSize = 16;
    private const int SaltSize = 16;
    private const int MinKeyBytes = 16;
    private static readonly byte[] Magic = "KSENC\u0001"u8.ToArray();
    private static readonly byte[] HkdfInfo = "KitStack.Storage file key v1"u8.ToArray();
    private const int FixedHeaderLength = 6 + 4 + SaltSize + 4;

    private readonly byte[] _currentKey;
    private readonly byte[] _currentKeyId;
    private readonly Dictionary<uint, byte[]> _keysById = [];

    /// <summary>Create an encryptor from a base64 master key and optional previous keys.</summary>
    /// <exception cref="StorageConfigurationException">A key is not valid base64 or is shorter than 16 bytes.</exception>
    public AesGcmFileContentEncryptor(string masterKeyBase64, IEnumerable<string>? previousKeysBase64 = null)
    {
        _currentKey = DecodeKey(masterKeyBase64, nameof(StorageOptions.MasterEncryptionKey));
        _currentKeyId = KeyId(_currentKey);
        _keysById[BinaryPrimitives.ReadUInt32BigEndian(_currentKeyId)] = _currentKey;

        foreach (var previous in previousKeysBase64 ?? [])
        {
            if (string.IsNullOrWhiteSpace(previous))
                continue;
            var key = DecodeKey(previous, nameof(StorageOptions.PreviousEncryptionKeys));
            _keysById.TryAdd(BinaryPrimitives.ReadUInt32BigEndian(KeyId(key)), key);
        }
    }

    /// <summary>
    /// Build the encryptor described by <paramref name="options"/>, or <c>null</c> when no
    /// <see cref="StorageOptions.MasterEncryptionKey"/> is configured.
    /// </summary>
    public static AesGcmFileContentEncryptor? FromOptions(StorageOptions? options)
        => string.IsNullOrWhiteSpace(options?.MasterEncryptionKey)
            ? null
            : new AesGcmFileContentEncryptor(options.MasterEncryptionKey, options.PreviousEncryptionKeys);

    /// <inheritdoc/>
    public int HeaderLength => Magic.Length;

    /// <inheritdoc/>
    public bool IsEncrypted(ReadOnlySpan<byte> header) => header.StartsWith(Magic);

    /// <inheritdoc/>
    public async Task EncryptAsync(Stream plaintext, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(destination);

        var header = new byte[FixedHeaderLength];
        Magic.CopyTo(header, 0);
        _currentKeyId.CopyTo(header, 6);
        RandomNumberGenerator.Fill(header.AsSpan(10, SaltSize));
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(10 + SaltSize), ChunkSize);
        await destination.WriteAsync(header, cancellationToken).ConfigureAwait(false);

        using var aes = new AesGcm(DeriveFileKey(_currentKey, header.AsSpan(10, SaltSize)), TagSize);

        // Double buffering: a chunk is final when the read after it returns nothing.
        var current = new byte[ChunkSize];
        var next = new byte[ChunkSize];
        var cipher = new byte[ChunkSize];
        var tag = new byte[TagSize];
        var prefix = new byte[5];
        var currentLength = await plaintext.ReadAtLeastAsync(current, ChunkSize, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        ulong index = 0;

        while (true)
        {
            var nextLength = currentLength == ChunkSize
                ? await plaintext.ReadAtLeastAsync(next, ChunkSize, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false)
                : 0;
            var final = nextLength == 0;

            prefix[0] = final ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteInt32BigEndian(prefix.AsSpan(1), currentLength);
            aes.Encrypt(Nonce(index), current.AsSpan(0, currentLength), cipher.AsSpan(0, currentLength), tag, Aad(header, prefix[0]));

            await destination.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
            await destination.WriteAsync(cipher.AsMemory(0, currentLength), cancellationToken).ConfigureAwait(false);
            await destination.WriteAsync(tag, cancellationToken).ConfigureAwait(false);

            if (final)
                break;

            (current, next) = (next, current);
            currentLength = nextLength;
            index++;
        }

        CryptographicOperations.ZeroMemory(current);
        CryptographicOperations.ZeroMemory(next);
    }

    /// <inheritdoc/>
    public async Task DecryptAsync(Stream ciphertext, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        ArgumentNullException.ThrowIfNull(destination);

        var header = new byte[FixedHeaderLength];
        if (await ciphertext.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false) < header.Length
            || !IsEncrypted(header))
            throw new StorageException("Content is not in the KitStack encrypted format.");

        var keyId = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(6, 4));
        if (!_keysById.TryGetValue(keyId, out var masterKey))
            throw new StorageConfigurationException("File was encrypted with a key that is not configured (add it to Storage:PreviousEncryptionKeys).");

        var chunkSize = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(10 + SaltSize));
        if (chunkSize is <= 0 or > 16 * 1024 * 1024)
            throw new StorageException("Encrypted content has an invalid header.");

        using var aes = new AesGcm(DeriveFileKey(masterKey, header.AsSpan(10, SaltSize)), TagSize);
        var cipher = new byte[chunkSize];
        var plain = new byte[chunkSize];
        var tag = new byte[TagSize];
        var prefix = new byte[5];
        ulong index = 0;

        while (true)
        {
            if (await ciphertext.ReadAtLeastAsync(prefix, prefix.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false) < prefix.Length)
                throw new StorageException("Encrypted content is truncated.");

            var final = prefix[0] == 1;
            var length = BinaryPrimitives.ReadInt32BigEndian(prefix.AsSpan(1));
            if (prefix[0] > 1 || length < 0 || length > chunkSize || (!final && length != chunkSize))
                throw new StorageException("Encrypted content is corrupt.");

            if (await ciphertext.ReadAtLeastAsync(cipher.AsMemory(0, length), length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false) < length
                || await ciphertext.ReadAtLeastAsync(tag, TagSize, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false) < TagSize)
                throw new StorageException("Encrypted content is truncated.");

            try
            {
                aes.Decrypt(Nonce(index), cipher.AsSpan(0, length), tag, plain.AsSpan(0, length), Aad(header, prefix[0]));
            }
            catch (AuthenticationTagMismatchException ex)
            {
                throw new StorageException("Encrypted content failed authentication (tampered or wrong key).", ex);
            }

            await destination.WriteAsync(plain.AsMemory(0, length), cancellationToken).ConfigureAwait(false);

            if (final)
                break;
            index++;
        }

        CryptographicOperations.ZeroMemory(plain);
    }

    private static byte[] DecodeKey(string base64, string settingName)
    {
        byte[] key;
        try
        {
            key = Convert.FromBase64String(base64.Trim());
        }
        catch (FormatException ex)
        {
            throw new StorageConfigurationException($"Storage:{settingName} is not valid base64.", ex);
        }

        if (key.Length < MinKeyBytes)
            throw new StorageConfigurationException($"Storage:{settingName} must decode to at least {MinKeyBytes} bytes (32 recommended).");
        return key;
    }

    private static byte[] KeyId(byte[] key) => SHA256.HashData(key).AsSpan(0, 4).ToArray();

    private static byte[] DeriveFileKey(byte[] masterKey, ReadOnlySpan<byte> salt)
        => HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, 32, salt.ToArray(), HkdfInfo);

    private static byte[] Nonce(ulong index)
    {
        var nonce = new byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), index);
        return nonce;
    }

    private static byte[] Aad(byte[] header, byte finalFlag)
    {
        var aad = new byte[header.Length + 1];
        header.CopyTo(aad, 0);
        aad[^1] = finalFlag;
        return aad;
    }
}
