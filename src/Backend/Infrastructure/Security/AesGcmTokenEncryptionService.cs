using Application.Interfaces;
using System.Security.Cryptography;
using System.Text;

namespace Infrastructure.Security;

/// <summary>
/// Реализация сервиса шифрования конфиденциальных токенов по алгоритму AES-256-GCM
/// с версионированным форматом бинарного контейнера.
/// </summary>
public sealed class AesGcmTokenEncryptionService : ITokenEncryptionService
{
    /// <summary>Текущая версия бинарного формата хранения токена.</summary>
    public const byte CurrentVersion = 1;

    /// <summary>Размер Nonce (IV) в байтах (12 байт, рекомендовано NIST SP 800-38D).</summary>
    public const int NonceSizeBytes = 12;

    /// <summary>Размер Authentication Tag в байтах (16 байт = 128 бит).</summary>
    public const int TagSizeBytes = 16;

    /// <summary>Размер заголовка контейнера: 1 байт версии + 12 байт Nonce + 16 байт Tag = 29 байт.</summary>
    public const int HeaderSizeBytes = 1 + NonceSizeBytes + TagSizeBytes;

    private readonly byte[] _key;

    /// <summary>
    /// Инициализирует сервис с заданным 32-байтовым ключом шифрования (AES-256).
    /// </summary>
    /// <param name="key">32-байтовый ключ шифрования.</param>
    public AesGcmTokenEncryptionService(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (key.Length != 32)
        {
            throw new ArgumentException(
                $"Ключ шифрования AES-256 должен содержать ровно 32 байта. Получено: {key.Length} байт.",
                nameof(key));
        }

        _key = key.ToArray();
    }

    /// <inheritdoc />
    public string Encrypt(string plainText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plainText);

        var plainBytes = Encoding.UTF8.GetBytes(plainText);

        var nonce = new byte[NonceSizeBytes];
        RandomNumberGenerator.Fill(nonce);

        var tag = new byte[TagSizeBytes];
        var cipherTextBytes = new byte[plainBytes.Length];

        using (var aesGcm = new AesGcm(_key, TagSizeBytes))
        {
            aesGcm.Encrypt(nonce, plainBytes, cipherTextBytes, tag);
        }

        var payload = new byte[HeaderSizeBytes + cipherTextBytes.Length];
        payload[0] = CurrentVersion;
        Buffer.BlockCopy(nonce, 0, payload, 1, NonceSizeBytes);
        Buffer.BlockCopy(tag, 0, payload, 1 + NonceSizeBytes, TagSizeBytes);
        Buffer.BlockCopy(cipherTextBytes, 0, payload, HeaderSizeBytes, cipherTextBytes.Length);

        return Convert.ToBase64String(payload);
    }

    /// <inheritdoc />
    public string Decrypt(string cipherTextBase64)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cipherTextBase64);

        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(cipherTextBase64);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Зашифрованные данные токена имеют некорректный формат Base64.", ex);
        }

        if (payload.Length < HeaderSizeBytes)
        {
            throw new InvalidOperationException(
                $"Зашифрованные данные токена повреждены: длина контейнера ({payload.Length} байт) меньше минимального размера заголовка ({HeaderSizeBytes} байт).");
        }

        var version = payload[0];
        if (version != CurrentVersion)
        {
            throw new NotSupportedException(
                $"Неподдерживаемая версия формата шифрования токена: {version}. Поддерживается только версия {CurrentVersion}.");
        }

        var nonce = payload.AsSpan(1, NonceSizeBytes);
        var tag = payload.AsSpan(1 + NonceSizeBytes, TagSizeBytes);
        var cipherText = payload.AsSpan(HeaderSizeBytes);

        var plainBytes = new byte[cipherText.Length];

        using (var aesGcm = new AesGcm(_key, TagSizeBytes))
        {
            try
            {
                aesGcm.Decrypt(nonce, cipherText, tag, plainBytes);
            }
            catch (CryptographicException ex)
            {
                throw new InvalidOperationException(
                    "Ошибка расшифровки токена: данные повреждены или аутентификационный тег не совпадает.",
                    ex);
            }
        }

        return Encoding.UTF8.GetString(plainBytes);
    }
}
