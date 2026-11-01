namespace Application.DTOs;

/// <summary>
/// Обобщенный результат постраничной выборки данных.
/// </summary>
/// <typeparam name="T">Тип элементов страницы.</typeparam>
public class PagedResultDto<T>
{
    /// <summary>
    /// Элементы запрошенной страницы.
    /// </summary>
    public IReadOnlyList<T> Items { get; set; } = [];

    /// <summary>
    /// Общее количество записей, удовлетворяющих условиям выборки (до применения пагинации).
    /// </summary>
    public int TotalCount { get; set; }

    /// <summary>
    /// Фактически примененный номер страницы.
    /// </summary>
    public int Page { get; set; }

    /// <summary>
    /// Фактически примененный размер страницы.
    /// </summary>
    public int PageSize { get; set; }

    public PagedResultDto()
    {
    }

    public PagedResultDto(IReadOnlyList<T> items, int totalCount, int page, int pageSize)
    {
        Items = items;
        TotalCount = totalCount;
        Page = page;
        PageSize = pageSize;
    }
}
