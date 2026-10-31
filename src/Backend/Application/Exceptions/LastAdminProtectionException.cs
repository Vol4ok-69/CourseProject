namespace Application.Exceptions;

/// <summary>
/// Исключение, возникающее при попытке удалить или понизить роль последнего администратора системы.
/// </summary>
public class LastAdminProtectionException : InvalidOperationException
{
    public const string DefaultMessage = "Невозможно удалить или понизить роль последнего администратора системы.";

    public LastAdminProtectionException() : base(DefaultMessage)
    {
    }

    public LastAdminProtectionException(string message) : base(message)
    {
    }

    public LastAdminProtectionException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
