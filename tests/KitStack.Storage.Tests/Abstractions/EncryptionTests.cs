using KitStack.Abstractions.Exceptions;
using KitStack.Abstractions.Security;

namespace KitStack.Storage.Tests.Abstractions;

public class EncryptionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(65536)]
    [InlineData(65537)]
    [InlineData(200_000)]
    public async Task Round_trips_any_length(int length)
    {
        var encryptor = new AesGcmFileContentEncryptor(TestData.NewKey());
        var plaintext = TestData.Random(length);

        var encrypted = await EncryptAsync(encryptor, plaintext);
        Assert.True(encryptor.IsEncrypted(encrypted));
        Assert.NotEqual(plaintext, encrypted);

        Assert.Equal(plaintext, await DecryptAsync(encryptor, encrypted));
    }

    [Fact]
    public async Task Tampering_is_detected()
    {
        var encryptor = new AesGcmFileContentEncryptor(TestData.NewKey());
        var encrypted = await EncryptAsync(encryptor, TestData.Random(1000));
        encrypted[^20] ^= 0x01;

        await Assert.ThrowsAsync<StorageException>(() => DecryptAsync(encryptor, encrypted));
    }

    [Fact]
    public async Task Dropping_the_final_chunk_is_detected()
    {
        var encryptor = new AesGcmFileContentEncryptor(TestData.NewKey());
        var encrypted = await EncryptAsync(encryptor, TestData.Random(65536 * 2 + 10));
        // header (30) + two full chunks (5 + 65536 + 16 each): cut off the final chunk.
        var truncated = encrypted[..(30 + 2 * (5 + 65536 + 16))];

        await Assert.ThrowsAsync<StorageException>(() => DecryptAsync(encryptor, truncated));
    }

    [Fact]
    public async Task Previous_keys_decrypt_after_rotation()
    {
        var oldKey = TestData.NewKey();
        var encrypted = await EncryptAsync(new AesGcmFileContentEncryptor(oldKey), TestData.Text());

        var rotated = new AesGcmFileContentEncryptor(TestData.NewKey(), [oldKey]);
        Assert.Equal(TestData.Text(), await DecryptAsync(rotated, encrypted));

        var withoutOldKey = new AesGcmFileContentEncryptor(TestData.NewKey());
        await Assert.ThrowsAsync<StorageConfigurationException>(() => DecryptAsync(withoutOldKey, encrypted));
    }

    [Theory]
    [InlineData("not base64!")]
    [InlineData("c2hvcnQ=")]
    public void Invalid_keys_are_rejected(string key)
        => Assert.Throws<StorageConfigurationException>(() => new AesGcmFileContentEncryptor(key));

    private static async Task<byte[]> EncryptAsync(IFileContentEncryptor encryptor, byte[] plaintext)
    {
        using var output = new MemoryStream();
        await encryptor.EncryptAsync(new MemoryStream(plaintext), output);
        return output.ToArray();
    }

    private static async Task<byte[]> DecryptAsync(IFileContentEncryptor encryptor, byte[] ciphertext)
    {
        using var output = new MemoryStream();
        await encryptor.DecryptAsync(new MemoryStream(ciphertext), output);
        return output.ToArray();
    }
}
