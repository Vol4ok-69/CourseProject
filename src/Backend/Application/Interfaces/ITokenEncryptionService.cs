namespace Application.Interfaces;

/// <summary>
/// Предоставляет методы двустороннего симметричного шифрования и расшифровки конфиденциальных токенов.
/// </summary>
public interface ITokenEncryptionService
{
    /// <summary>
    /// Шифрует открытый текст токена в версионированную строку Base64.
    /// </summary>
    /// <param name="plainText">Открытый текст токена.</param>
    /// <returns>Версионированная зашифрованная строка в формате Base64.</returns>
    string Encrypt(string plainText);

    /// <summary>
    /// Расшифровывает версионированную строку Base64 обратно в открытый текст токена.
    /// </summary>
    /// <param name="cipherTextBase64">Зашифрованная строка в формате Base64.</param>
    /// <returns>Расшифрованный открытый текст токена.</returns>
    string Decrypt(string cipherTextBase64);
}
