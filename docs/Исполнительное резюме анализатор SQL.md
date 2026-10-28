# Спецификация компонента SqlAnalyzer (Целевой компонент MVP)

> **Статус документа:** Целевая техническая спецификация для реализации в рамках MVP.  
> **Статус реализации в кодовой базе:** Запланирован к разработке (проект `src/Analyzers/Sql` зарезервирован в архитектуре, код и тесты не создавались в рамках текущей задачи).

---

## 1. Назначение и место в архитектуре

Модуль **SqlAnalyzer** предназначен для статического анализа исходного кода C# с целью выявления потенциальных проблем безопасности и качества при формировании и выполнении SQL-запросов через Entity Framework Core.

### Архитектурное положение:
- **Расположение проекта:** `src/Analyzers/Sql` (будет подключен к решению `CourseProject.slnx`).
- **Зависимости проекта:**
  - `Domain` (доступ к `AnalyzerType.Sql`, перечислению `Severity`);
  - `Application` (интерфейс `IAnalyzer`, модели `AnalyzerContext`, `AnalyzerFinding`);
  - `Microsoft.CodeAnalysis.CSharp` (Roslyn AST и семантическая модель).
- **Контракт взаимодействия:** реализует единый интерфейс `IAnalyzer`:
  ```csharp
  namespace Sql;

  public sealed class SqlAnalyzer : IAnalyzer
  {
      public AnalyzerType Type => AnalyzerType.Sql;

      public async Task<IReadOnlyList<AnalyzerFinding>> AnalyzeAsync(
          AnalyzerContext context, 
          CancellationToken cancellationToken = default);
  }
  ```
- **Способ анализа:** статический синтаксический и семантический анализ деревьев Roslyn (`CSharpSyntaxTree`, `SyntaxNode`, `InvocationExpressionSyntax`) без динамического перехвата запросов в runtime.

---

## 2. Спецификация правил статического анализа

### SQL001: Потенциально небезопасное формирование SQL-запроса
- **Идентификатор правила:** `SQL001`
- **Название:** Небезопасная передача динамически сформированной строки в методы выполнения сырого SQL.
- **Уровень критичности:** `Severity.Warning`
- **Назначение:** Обнаружение очевидных синтаксических паттернов потенциальной SQL-инъекции, когда строковое выражение, сформированное с помощью интерполяции строк (`$"..."`) или бинарной конкатенации (`+`), передается в методы EF Core, ожидающие сырую непараметризованную SQL-строку.
- **Целевые API EF Core:**
  - `RelationalQueryableExtensions.FromSqlRaw`
  - `RelationalDatabaseFacadeExtensions.ExecuteSqlRaw`
  - `RelationalDatabaseFacadeExtensions.ExecuteSqlRawAsync`
- **Условие срабатывания:**
  Анализатор обнаруживает вызов целевого метода (`FromSqlRaw`, `ExecuteSqlRaw`, `ExecuteSqlRawAsync`), где первый аргумент:
  1. Является выражением интерполированной строки: `InterpolatedStringExpressionSyntax` (`$"SELECT ... {userInput}"`);
  2. Является бинарным выражением конкатенации строк: `BinaryExpressionSyntax` с оператором `+` (`"SELECT ... " + userInput`);
  3. Является локальной переменной (`IdentifierNameSyntax`), значение которой в текущем методе инициализировано интерполяцией или конкатенацией строк.
- **Примеры срабатывания:**
  ```csharp
  // Срабатывание: прямая интерполяция в FromSqlRaw
  var users = db.Users.FromSqlRaw($"SELECT * FROM Users WHERE Name = '{name}'").ToList();

  // Срабатывание: конкатенация строк
  var query = "SELECT * FROM Orders WHERE Status = " + status;
  var orders = db.Orders.FromSqlRaw(query).ToList();

  // Срабатывание: интерполяция в ExecuteSqlRawAsync
  await db.Database.ExecuteSqlRawAsync($"UPDATE Users SET Active = 1 WHERE RoleId = {roleId}");
  ```
- **Примеры отсутствия срабатывания (корректный код):**
  ```csharp
  // Безопасно: использование безопасного параметризованного FromSqlInterpolated
  var users = db.Users.FromSqlInterpolated($"SELECT * FROM Users WHERE Name = {name}").ToList();

  // Безопасно: вызов с явными параметрами NpgsqlParameter / DbParameter
  var param = new NpgsqlParameter("@name", name);
  var users = db.Users.FromSqlRaw("SELECT * FROM Users WHERE Name = @name", param).ToList();

  // Безопасно: константная строковая литера
  var allActive = db.Users.FromSqlRaw("SELECT * FROM Users WHERE IsActive = TRUE").ToList();
  ```
- **Текст сообщения:**
  `Потенциально небезопасное формирование SQL: метод '{MethodName}' вызван со строкой, сформированной через интерполяцию или конкатенацию.`
- **Рекомендация:**
  `Используйте безопасный параметризованный метод 'FromSqlInterpolated' / 'ExecuteSqlInterpolatedAsync' либо передавайте параметры через явные объекты параметров запроса (DbParameter) во избежание SQL-инъекций.`
- **Ограничения статического анализа:**
  - Анализатор проверяет только локальные синтаксические конструкции внутри анализируемого метода или файла.
  - Если SQL-строка формируется через вызовы внешних вспомогательных методов, строковые билдеры (`StringBuilder`) или фабрики, передаваемые через параметры, статический анализ на уровне синтаксического дерева не гарантирует отслеживание потока данных (Taint Analysis).
  - Правило не заявляет обнаружение всех возможных SQL-инъекций в приложении, а покрывает наиболее распространенные и грубые ошибки использования API EF Core.

---

### SQL002: Использование конструкции SELECT * в сырых SQL-запросах
- **Идентификатор правила:** `SQL002`
- **Название:** Использование неявной выборки всех столбцов (SELECT *).
- **Уровень критичности:** `Severity.Info`
- **Назначение:** Выявление запросов, которые извлекают все столбцы таблицы вместо явного перечисления необходимых атрибутов, что может приводить к избыточной передаче данных по сети, усложнению покрытия индексами и поломке запросов при изменении схемы таблицы.
- **Целевые API:**
  - Методы выполнения SQL EF Core (`FromSqlRaw`, `FromSqlInterpolated`).
- **Условие срабатывания:**
  Первый строковый аргумент метода представляет собой статически распознаваемый строковый литерал (или константу), содержащий синтаксическую конструкцию `SELECT *` (без учета регистра и с возможными пробельными символами, регулярное выражение `(?i)\bSELECT\s+\*\b`).
- **Примеры срабатывания:**
  ```csharp
  // Срабатывание: явное указание SELECT *
  var users = db.Users.FromSqlRaw("SELECT * FROM Users").ToList();

  // Срабатывание в интерполированном безопасном запросе
  var activeUsers = db.Users.FromSqlInterpolated($"SELECT * FROM Users WHERE Age > {minAge}").ToList();
  ```
- **Примеры отсутствия срабатывания:**
  ```csharp
  // Корректно: явное перечисление столбцов
  var users = db.Users.FromSqlRaw("SELECT Id, Username, Email FROM Users").ToList();

  // Корректно: запрос агрегации
  var count = db.Database.SqlQueryRaw<int>("SELECT COUNT(*) FROM Users");
  ```
- **Текст сообщения:**
  `Запрос содержит конструкцию 'SELECT *'. Рекомендуется явно указывать список извлекаемых столбцов.`
- **Рекомендация:**
  `Замените символ '*' на явный перечень необходимых столбцов. Это улучшит читаемость кода, позволит СУБД использовать покрывающие индексы и предотвратит избыточную передачу неиспользуемых данных.`
- **Ограничения статического анализа:**
  - Правило срабатывает только на статически детерминированные строковые литералы. Если текст запроса формируется динамически из внешних источников, проверка пропускается.
  - Конструкция `SELECT *` в некоторых сценариях (например, маппинг в полноценную сущность EF Core) является штатным поведением EF Core, поэтому уровень критичности строго зафиксирован как `Info` (рекомендация), а не дефект или ошибка.
  - Правило не анализирует реальный план выполнения запроса СУБД (Query Execution Plan) и объем извлекаемых данных.

---

## 3. План тестирования при реализации

После создания проекта `src/Analyzers/Sql` в рамках следующего этапа разработки для него будет создан проект юнит-тестов `tests/SqlAnalyzer.Tests` с обязательным покрытием сценариев:

1. **Тесты для SQL001:**
   - `AnalyzeAsync_InterpolatedStringInFromSqlRaw_ReturnsWarning()`
   - `AnalyzeAsync_ConcatenatedStringInFromSqlRaw_ReturnsWarning()`
   - `AnalyzeAsync_InterpolatedStringInExecuteSqlRawAsync_ReturnsWarning()`
   - `AnalyzeAsync_FromSqlInterpolated_DoesNotReturnWarning()` (позитивный сценарий)
   - `AnalyzeAsync_ConstantStringWithParameters_DoesNotReturnWarning()` (позитивный сценарий)
2. **Тесты для SQL002:**
   - `AnalyzeAsync_SelectStarInFromSqlRaw_ReturnsInfo()`
   - `AnalyzeAsync_SelectStarCaseInsensitive_ReturnsInfo()`
   - `AnalyzeAsync_ExplicitColumnsInFromSqlRaw_DoesNotReturnInfo()` (позитивный сценарий)
   - `AnalyzeAsync_SelectCountStar_DoesNotReturnInfo()` (проверка исключения агрегатных функций)
3. **Интеграционные тесты:**
   - Проверка возврата `AnalyzerFinding` с типом `AnalyzerType.Sql`, корректным путем к C# файлу и номером строки в исходнике.
