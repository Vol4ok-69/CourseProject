# Технический аудит проекта CourseProject

**Дата проведения:** 06 ноября 2026 г.  
**Базовая ветка:** `main`  
**Базовый коммит:** `2068c51` (*feat(github): enforce owner-scoped PATs for sync and analysis*)  
**Состояние рабочего дерева:** Clean (режим аудита: READ-ONLY, исходный код и тесты не изменялись)  
**Верификация тестовой базы (`dotnet test`):** 709 тестов успешно пройдено, 0 сбоев (5 сборок: `SqlAnalyzer.Tests` — 44, `CSharpAnalyzer.Tests` — 17, `SecurityAnalyzer.Tests` — 56, `Backend.Tests` — 528, `DatabaseAnalyzer.Tests` — 64).  
**Состояние модели EF Core:** `dotnet ef migrations has-pending-model-changes` подтверждает полное соответствие снимка `AppDbContextModelSnapshot.cs` сущностям предметной области (`No changes have been made to the model since the last migration`).

---

## 1. Назначение документа

Настоящий документ фиксирует результаты углубленного независимого технического аудита серверной части (`Backend`) и модулей статического анализа (`Analyzers`) проекта CourseProject.

Аудит выполнен на основе:
- Ревизии `2068c51` после реализации персональных токенов GitHub PAT, защиты от Zip Slip, строгой пагинации и маскирования секретов;
- Фактического анализа исходных текстов, интерфейсов и модульных тестов;
- Сопоставления с планом [API-Stabilization-Plan.md](../plans/API-Stabilization-Plan.md);
- Проверки архитектурных требований [Технический регламент.md](../Технический%20регламент.md) и [Правила-базы-данных.txt](../Правила-базы-данных.txt).

---

## 2. Ограничения проведённого аудита

1. **Недоступность тестовой СУБД в Docker (порт 5433):**
   Локальная служба Docker Engine на момент аудита была остановлена, контейнер `courseproject-postgres` на порту 5433 был недоступен (`TcpTestSucceeded: False`). Проверка структуры таблиц, внешних ключей и уникальных ограничений выполнялась по коду миграций EF Core (`src/Backend/Infrastructure/Data/Migrations/`), классам конфигурации (`IEntityTypeConfiguration<T>`) и вызову `has-pending-model-changes`.
2. **Ограничение построения семантической модели Roslyn для произвольного пользовательского кода:**
   В модуле `SqlAnalyzer` анализ методов `FromSqlRaw` и `ExecuteSqlRaw` выполняется на уровне синтаксических деревьев C# AST (`SyntaxNode`), а не семантической модели (`SemanticModel`). Это осознанное проектное ограничение: сторонний репозиторий, загружаемый на анализ, может не содержать восстановленных пакетов NuGet или содержать ошибки сборки, что делает построение полной семантической модели невозможным без предварительного `dotnet restore`.

---

## 3. Детальный реестр дефектов (DEF-001 – DEF-010)

### DEF-001: Выполнение недоверенного MSBuild-кода и динамическая загрузка сборки
- **Статус:** `CONFIRMED`
- **Приоритет:** `P1 (Critical)`
- **Компонент:** `DatabaseAnalyzer` (`src/Analyzers/Database/Providers/`)
- **Ссылки на код:**
  - [`EfCoreModelInspector.cs#L104-L138`](file:///c:/Users/ivank/Desktop/Uceba/Projects/CourseProject/src/Analyzers/Database/Providers/EfCoreModelInspector.cs#L104-L138) (метод `BuildProjectAsync`)
  - [`EfCoreModelInspector.cs#L71-L89`](file:///c:/Users/ivank/Desktop/Uceba/Projects/CourseProject/src/Analyzers/Database/Providers/EfCoreModelInspector.cs#L71-L89) (метод `InspectAsync`)
  - [`EfCoreModelInspector.cs#L224`](file:///c:/Users/ivank/Desktop/Uceba/Projects/CourseProject/src/Analyzers/Database/Providers/EfCoreModelInspector.cs#L224) (метод `LoadProjectAssemblyAsync`)
- **Описание проблемы:**
  1. **Выполнение произвольного MSBuild-кода:** Метод `BuildProjectAsync` запускает внешний процесс `dotnet build "{projectPath}"` над кодом, скачанным из стороннего репозитория. В файлах `.csproj`, `.props` или `.targets` злоумышленник может объявить задачи сборки с выполнением произвольных системных команд через `<Exec Command="..." />` или inline-задачи Roslyn `<UsingTask TaskFactory="RoslynCodeTaskFactory" ...>`. Сборка выполняется с полными правами процесса бэкенда операционной системы хоста.
  2. **Проблема `Assembly.LoadFrom`:** Скомпилированный бинарный файл сборки загружается вызовом `Assembly.LoadFrom(assemblyPath)`. В .NET Core / .NET сборки, загруженные в стандартный контекст загрузки по умолчанию (`AssemblyLoadContext.Default`), **не могут быть выгружены из памяти**. Многократный запуск инспекции приводит к утечке памяти CLR, разрастанию рабочего набора процесса и потенциальным конфликтам версий типов (`TypeLoadException`).
  3. **Статические конструкторы и инстанцирование:** При вызове `type.GetInterfaces()`, поиске реализаций `IDesignTimeDbContextFactory<T>` и вызове `Activator.CreateInstance(factoryType)` управление передается пользовательскому C#-коду фабрики и статических конструкторов классов контекста внутри основного процесса сервера.
  4. **Ограничения `AssemblyLoadContext`:** Важно подчеркнуть, что переход на изолированный коллекционируемый контекст (`AssemblyLoadContext` с флагом `isCollectible: true`) решает **исключительно** проблему выгрузки сборки из оперативной памяти сборщиком мусора. **`AssemblyLoadContext` не является песочницей (sandbox)**: он не изолирует доступ к файловой системе, сокетам, переменным окружения, процессам ОС и не ограничивает процессорное время или память.
- **Сценарий воспроизведения:** Пользователь отправляет на анализ репозиторий, содержащий в `.csproj` таргет `<Target Name="Exploit" AfterTargets="Build"><Exec Command="powershell -Command ..." /></Target>`. Сервер выполняет системную команду при вызове `dotnet build`.
- **Архитектурный статус:** Переход на статический анализ моделей Roslyn AST либо вынос в изолированный sandbox-контейнер является предметом отдельного архитектурного проектирования (см. [database-analyzer-isolation.md](../architecture/database-analyzer-isolation.md)). Не фиксировать конкретное решение как окончательно утвержденное до согласования RFC.

---

### DEF-002: Сбой всего анализа коммита при отсутствии `IDesignTimeDbContextFactory`
- **Статус:** `CONFIRMED`
- **Приоритет:** `P1 (Critical)`
- **Компонент:** `DatabaseAnalyzer` (`src/Analyzers/Database/Providers/`)
- **Ссылки на код:**
  - [`EfCoreModelInspector.cs#L64-L69`](file:///c:/Users/ivank/Desktop/Uceba/Projects/CourseProject/src/Analyzers/Database/Providers/EfCoreModelInspector.cs#L64-L69)
  - [`EfCoreMetadataProvider.cs#L28-L31`](file:///c:/Users/ivank/Desktop/Uceba/Projects/CourseProject/src/Analyzers/Database/Providers/EfCoreMetadataProvider.cs#L28-L31)
  - [`AnalysisService.cs#L190-L205`](file:///c:/Users/ivank/Desktop/Uceba/Projects/CourseProject/src/Backend/Application/Services/AnalysisService.cs#L190-L205)
- **Описание проблемы:**
  - Метод `EfCoreModelInspector.InspectAsync` ищет строго реализацию интерфейса `IDesignTimeDbContextFactory<T>`. Если проект использует стандартную конфигурацию `AddDbContext<AppDbContext>` в `Program.cs` и не содержит отдельного класса фабрики времени разработки, метод выбрасывает `InvalidOperationException("No IDesignTimeDbContextFactory was found...")`.
  - Это исключение не перехватывается в `EfCoreMetadataProvider`, пробивает конвейер до `AnalysisService.ExecuteAnalysisAsync` и переводит весь анализ коммита в статус `Failed`. При этом результаты успешно отработавших анализаторов C#, SQL и безопасности теряются.
- **Предпочтительное поведение:**
  - Невозможность извлечь метаданные базы данных должна трактоваться как частичный результат: в результатах анализа фиксируется информационная запись/предупреждение (`Severity.Warning` с рекомендацией добавить фабрику времени разработки или описание причины), а остальные анализаторы (`CSharpAnalyzer`, `SqlAnalyzer`, `SecurityAnalyzer`) должны штатно сохранять свои находки, переводя анализ в статус `Completed`.
  - **Предостережение:** Нельзя предписывать универсальное создание контекста через подмену провайдера на SQLite in-memory (`UseSqlite`), так как реальные модели EF Core часто содержат конфигурации, специфичные для PostgreSQL/Npgsql (типы `jsonb`, массивы, расширения `citext`, геометрию, генераторы identity), которые вызывают сбой при валидации модели провайдером SQLite.

---

### DEF-003: Изоляция рабочих каталогов анализа по токену выполнения и безопасная очистка
- **Статус:** `CONFIRMED`
- **Приоритет:** `P1 (Critical)`
- **Компонент:** `Backend.Application` / `Backend.Infrastructure`
- **Ссылки на код:**
  - [`RepositorySourceService.cs#L50-L55`](file:///c:/Users/ivank/Desktop/Uceba/Projects/CourseProject/src/Backend/Infrastructure/GitHub/RepositorySourceService.cs#L50-L55)
  - [`RepositorySourceService.cs#L60-L75`](file:///c:/Users/ivank/Desktop/Uceba/Projects/CourseProject/src/Backend/Infrastructure/GitHub/RepositorySourceService.cs#L60-L75) (очистка при сбое распаковки)
  - [`AnalysisService.cs#L210-L225`](file:///c:/Users/ivank/Desktop/Uceba/Projects/CourseProject/src/Backend/Application/Services/AnalysisService.cs#L210-L225) (основной `finally`)
- **Описание проблемы:**
  - Рабочий каталог для извлечения репозитория формируется без учета токена выполнения:  
    `analysisDirectory = Path.Combine(storageDirectory, analysisId.ToString(), "repository")`.
  - **Анализ конкурентности и владения:** Основной блок `finally` в `AnalysisService.ExecuteAnalysisAsync` уже содержит проверку:
    ```csharp
    if (!ownershipLost)
    {
        await CleanupAsync(analysisId, CancellationToken.None);
    }
    ```
    Однако:
    1. Внутри `RepositorySourceService.DownloadAndExtractAsync` при возникновении ошибки скачивания или превышения квот вызывается локальная очистка `await CleanupAsync(analysisId, ct)`, которая жестко удаляет директорию `data/analysis/{analysisId}` без проверки токена выполнения.
    2. Если воркер A задержался (например, из-за долгой сборки или GC-паузы), аренда задачи истекает в PostgreSQL. Воркер B перехватывает задачу с новым `ExecutionToken` и начинает распаковку в ту же папку `data/analysis/{analysisId}`. В это время воркер A, получив исключение таймаута или завершаясь, может вызвать очистку и удалить каталог, куда воркер B в данный момент распаковывает файлы.
- **Требуемое исправление:**
  - Связать путь рабочего каталога с уникальным токеном выполнения:  
    `Path.Combine(storageDirectory, $"{analysisId}_{executionToken}")`.
  - Передавать `executionToken` во все методы скачивания, инспекции и очистки.
  - Объединить данное исправление с задачей конфигурируемого пути хранилища (см. API-020).

---

### DEF-004: Ложные срабатывания правила DB04 для первичных и альтернативных ключей
- **Статус:** `CONFIRMED`
- **Приоритет:** `P2 (High)`
- **Компонент:** `DatabaseAnalyzer` (`src/Analyzers/Database/`)
- **Ссылки на код:**
  - [`EfCoreMetadataMapper.cs#L37-L45`](file:///c:/Users/ivank/Desktop/Uceba/Projects/CourseProject/src/Analyzers/Database/Mapping/EfCoreMetadataMapper.cs#L37-L45)
  - [`DatabaseAnalyzer.cs#L101-L120`](file:///c:/Users/ivank/Desktop/Uceba/Projects/CourseProject/src/Analyzers/Database/DatabaseAnalyzer.cs#L101-L120)
- **Описание проблемы:**
  - В `EfCoreMetadataMapper` список индексов сущности формируется исключительно из явных индексов модели `entityType.GetIndexes()`.
  - В реляционных базах данных (PostgreSQL) первичный ключ (`entityType.FindPrimaryKey()`) и альтернативные ключи уникальности (`entityType.GetKeys()`) физически создают уникальные B-дерево индексы на стороне СУБД.
  - **Ложное срабатывание:** В идентифицирующих связях «один-к-одному» или «один-ко-многим», где внешний ключ одновременно входит в состав первичного ключа (например, `OrderDetails.OrderId` является одновременно PK и FK), анализатор генерирует ложное предупреждение `DB04` («Внешний ключ не имеет индекса»).
  - **Требование к составным индексам:** Правило `DB04` должно учитывать строгий порядок столбцов в составном индексе. Индекс эффективно поддерживает внешний ключ, только если столбцы внешнего ключа образуют **точный префикс** составного индекса в том же порядке:
    ```csharp
    index.PropertyNames.Take(foreignKeyProperties.Count)
         .SequenceEqual(foreignKeyProperties, StringComparer.Ordinal)
    ```

---

### DEF-005: Ложные срабатывания правила DB05 для частичных и фильтрованных индексов
- **Статус:** `CONFIRMED`
- **Приоритет:** `P2 (High)`
- **Компонент:** `DatabaseAnalyzer` (`src/Analyzers/Database/`)
- **Ссылки на код:**
  - [`EfCoreMetadataMapper.cs#L62-L70`](file:///c:/Users/ivank/Desktop/Uceba/Projects/CourseProject/src/Analyzers/Database/Mapping/EfCoreMetadataMapper.cs#L62-L70)
  - [`DatabaseAnalyzer.cs#L125-L152`](file:///c:/Users/ivank/Desktop/Uceba/Projects/CourseProject/src/Analyzers/Database/DatabaseAnalyzer.cs#L125-L152)
- **Описание проблемы:**
  - `EfCoreMetadataMapper` при извлечении метаданных индексов копирует свойства `Name`, `PropertyNames` и `IsUnique`, но игнорирует предикат фильтра `index.GetFilter()`.
  - В реальных проектах на один и тот же столбец часто создаются два индекса: один общий, а второй частичный (например, для мягкого удаления `WHERE "IsDeleted" = false`), либо два частичных индекса с непересекающимися условиями.
  - Анализатор `DatabaseAnalyzer` в правиле `DB05` сравнивает только состав столбцов и ложно помечает частичные индексы как избыточные дубликаты.
- **Требуемое исправление:**
  - Добавить свойство `Filter` в DTO метаданных индекса `IndexMetadata`.
  - Признавать индексы дублирующимися только при совпадении набора столбцов, уникальности и идентичности фильтра (либо когда оба фильтра отсутствуют).

---

### DEF-006: Ограничение длины хеша коммита в CreateCommitDto (SHA-1 vs SHA-256)
- **Статус:** `PARTIALLY_CONFIRMED`
- **Приоритет:** `P3 (Medium)`
- **Компонент:** `Backend.Application` (`src/Backend/Application/DTOs/`)
- **Ссылки на код:**
  - [`CreateCommitDto.cs#L14-L15`](file:///c:/Users/ivank/Desktop/Uceba/Projects/CourseProject/src/Backend/Application/DTOs/CreateCommitDto.cs#L14-L15)
  - [`Commit.cs#L12`](file:///c:/Users/ivank/Desktop/Uceba/Projects/CourseProject/src/Backend/Domain/Entities/Commit.cs#L12)
  - [`CommitConfiguration.cs#L17`](file:///c:/Users/ivank/Desktop/Uceba/Projects/CourseProject/src/Backend/Infrastructure/Data/Configurations/CommitConfiguration.cs#L17)
- **Фактическое состояние:**
  - В DTO ручного создания коммита `CreateCommitDto` задан атрибут: `[StringLength(40, MinimumLength = 7)]`. Это блокирует ручное добавление коммитов современных репозиториев Git, использующих алгоритм хеширования SHA-256 (длина хеша 64 символа).
  - При этом доменная сущность `Commit.cs` содержит `public string CommitHash { get; set; }` без ограничений длины.
  - Конфигурация EF Core `CommitConfiguration.cs` отображает свойство в тип `text` PostgreSQL, а составной индекс `IX_Commits_ProjectId_CommitHash` успешно индексирует строки до 64+ символов.
  - Автоматическая синхронизация через `GitHubSyncService` переносит хеши коммитов напрямую из GitHub API в сущность, минуя `CreateCommitDto`.
- **Требуемое исправление:**
  - Расширить длину в `CreateCommitDto`: `[StringLength(64, MinimumLength = 7)]`.
  - Добавить строгую валидацию шестнадцатеричного формата (hex): `[RegularExpression("^[0-9a-fA-F]{7,64}$", ErrorMessage = "Хеш коммита должен содержать от 7 до 64 шестнадцатеричных символов.")]`.

---

### DEF-007: Пропуск небезопасной конфигурации CORS через `WithOrigins("*")` в SEC003
- **Статус:** `CONFIRMED`
- **Приоритет:** `P3 (Medium)`
- **Компонент:** `SecurityAnalyzer` (`src/Analyzers/Security/Rules/`)
- **Ссылки на код:**
  - [`Sec003InsecureCorsRule.cs#L28-L60`](file:///c:/Users/ivank/Desktop/Uceba/Projects/CourseProject/src/Analyzers/Security/Rules/Sec003InsecureCorsRule.cs#L28-L60)
- **Описание проблемы:**
  - Правило `Sec003InsecureCorsRule` синтаксически проверяет наличие вызовов `AllowAnyOrigin()` и предикатов `SetIsOriginAllowed(_ => true)`.
  - Использование вызова `.WithOrigins("*")` синтаксически пропускается анализатором.
- **Семантические нюансы runtime ASP.NET Core:**
  - Нельзя утверждать без интеграционной проверки, что `.WithOrigins("*")` во всех случаях распахивает доступ. В runtime ASP.NET Core вызов `.WithOrigins("*").AllowCredentials()` приводит к падению приложения при старте с `InvalidOperationException` (спецификация W3C запрещает wildcard origin вместе с credentials).
  - Однако в сценариях без передачи credentials вызов `.WithOrigins("*")` функционально эквивалентен `AllowAnyOrigin()` и должен детектироваться правилом `SEC003` с соответствующим пояснением.

---

### DEF-008: Игнорирование `ServicePointManager.ServerCertificateValidationCallback` в SEC005
- **Статус:** `CONFIRMED`
- **Приоритет:** `P3 (Medium)`
- **Компонент:** `SecurityAnalyzer` (`src/Analyzers/Security/Rules/`)
- **Ссылки на код:**
  - [`Sec005DisabledCertificateValidationRule.cs#L16, L27-L31`](file:///c:/Users/ivank/Desktop/Uceba/Projects/CourseProject/src/Analyzers/Security/Rules/Sec005DisabledCertificateValidationRule.cs#L16)
- **Описание проблемы:**
  - Правило `Sec005DisabledCertificateValidationRule` проверяет присваивание свойства с точным именем `ServerCertificateCustomValidationCallback` (свойство обработчиков `HttpClientHandler` и `SocketsHttpHandler`).
  - Классический глобальный механизм отключения валидации сертификатов в .NET — присваивание статического свойства `ServicePointManager.ServerCertificateValidationCallback` — правилом не проверяется.
- **Требуемое исправление:**
  - Добавить проверку присваивания `ServicePointManager.ServerCertificateValidationCallback` предикатом, безусловно возвращающим `true`.

---

### DEF-009 и NEW-001: Инкрементальная синхронизация коммитов репозитория GitHub
- **Статус:** `CONFIRMED` (объединены в одну задачу)
- **Приоритет:** `P2 (High)`
- **Компонент:** `Backend.Infrastructure` (`src/Backend/Infrastructure/Services/`)
- **Ссылки на код:**
  - [`GitHubService.cs#L26-L105`](file:///c:/Users/ivank/Desktop/Uceba/Projects/CourseProject/src/Backend/Infrastructure/GitHub/GitHubService.cs#L26-L105) (пагинация коммитов)
  - [`GitHubSyncService.cs#L45-L75`](file:///c:/Users/ivank/Desktop/Uceba/Projects/CourseProject/src/Backend/Application/Services/GitHubSyncService.cs#L45-L75) (оркестрация синхронизации)
- **Описание проблемы:**
  - В методе `GitHubService.GetCommitsAsync` пагинация реализована через бесконечный цикл `while (!string.IsNullOrEmpty(currentUrl))` со страницами по 100 элементов.
  - При каждой ручной или автоматической синхронизации сервис запрашивает все коммиты репозитория с самого начала, не прерывая опрос. Для крупных репозиториев (50 000+ коммитов) это гарантированно приводит к исчерпанию лимита GitHub API (5000 запросов/час) или сетевому таймауту.
  - **Недопустимость жесткого лимита страниц:** Предложение ограничить пагинацию константой (например, `maxPages = 10`) является некорректным, так как приводит к незаметной потере старой истории коммитов репозитория.
- **Требуемое исправление (Инкрементальная синхронизация):**
  1. Пагинация должна запрашивать свежие коммиты от HEAD и производить безопасную раннюю остановку только при обнаружении устойчивого окна уже известных коммитов (sliding overlap window) либо подтверждении родительских связей, исключая пропуск коммитов из параллельных веток и нелинейных слияний.
  2. Параметр `since` может использоваться как вспомогательный фильтр, но не гарантирует полноту выборки сам по себе из-за нелинейности истории веток и возможного расхождения дат автора (`commit.author.date`).
  3. Недопустимо введение искусственного лимита страниц (`maxPages`). Защита от дубликатов обеспечивается проверкой по `GetExistingHashesAsync`. Подробные требования: [TD-006](../plans/Technical-Debt-Plan.md#td-006-инкрементальная-синхронизация-коммитов-репозитория-github).

---

### DEF-010: Ограничения синтаксического подхода в `SqlAnalyzer`
- **Статус:** `PARTIALLY_CONFIRMED`
- **Приоритет:** `P3 (Medium)`
- **Компонент:** `SqlAnalyzer` (`src/Analyzers/Sql/Rules/`)
- **Ссылки на код:**
  - [`Sql001UnsafeQueryRule.cs`](file:///c:/Users/ivank/Desktop/Uceba/Projects/CourseProject/src/Analyzers/Sql/Rules/Sql001UnsafeQueryRule.cs)
  - [`Sql002SelectStarRule.cs`](file:///c:/Users/ivank/Desktop/Uceba/Projects/CourseProject/src/Analyzers/Sql/Rules/Sql002SelectStarRule.cs)
- **Описание проблемы:**
  - Правила сопоставляют имена методов `FromSqlRaw`, `ExecuteSqlRaw`, `SqlQueryRaw` исключительно по синтаксическому имени идентификатора. Если пользовательский класс объявляет метод с таким же именем, не имеющий отношения к EF Core, правило выдаст ложное срабатывание.
  - Однако для условий курсового проекта, анализирующего разнородные сторонние репозитории без гарантированного `dotnet restore` и компиляции, синтаксический анализ является обоснованным инженерным компромиссом. Задача отложена до этапа внедрения опционального семантического анализа.

---

## 4. Новые обнаруженные архитектурные вопросы (NEW-002, NEW-003, NEW-004)

### NEW-002 и NEW-003: Управление жизненным циклом очереди, retry-политика и защита от Poison Pill
- **Статус:** `NEEDS_DESIGN` (архитектурное улучшение)
- **Приоритет:** `P2 (High)`
- **Компонент:** `Backend.Domain` / `Backend.Application` / `Backend.Infrastructure`
- **Ссылки на код:**
  - [`Analysis.cs#L20-L30`](file:///c:/Users/ivank/Desktop/Uceba/Projects/CourseProject/src/Backend/Domain/Entities/Analysis.cs#L20-L30)
  - [`AnalysisService.cs#L190-L205`](file:///c:/Users/ivank/Desktop/Uceba/Projects/CourseProject/src/Backend/Application/Services/AnalysisService.cs#L190-L205)
  - [`AnalysisBackgroundService.cs#L45-L65`](file:///c:/Users/ivank/Desktop/Uceba/Projects/CourseProject/src/Backend/Api/BackgroundServices/AnalysisBackgroundService.cs#L45-L65)
- **Описание:**
  - В настоящее время любая кратковременная ошибка (сетевой сбой GitHub API при скачивании архива, таймаут I/O) немедленно переводит сущность `Analysis` в терминальный статус `Failed`. Сущность не содержит полей для подсчета попыток выполнения (`AttemptCount`, `MaxAttempts`).
  - **Угроза аварийного цикла (Poison Pill):** Если конкретный репозиторий провоцирует критический сбой процесса (например, `OutOfMemoryException` или переполнение стека в пользовательском коде), фоновый воркер аварийно завершается. После перезапуска сервиса задача с истекшим `LeaseExpiration` снова берется из БД в обработку тем же воркером, вызывая бесконечный цикл падений сервиса (`CrashLoop`).
- **Требования к проектированию:**
  - Классификация ошибок на временные (transient) и постоянные (terminal).
  - Введение механизма экспоненциальной задержки (backoff) перед повторной попыткой.
  - Ограничение максимального числа попыток (`MaxAttempts = 3`) с переводом в `Failed` при превышении лимита.
  - Потребуется миграция базы данных PostgreSQL для добавления полей `AttemptCount` и `NextAttemptAt`.

---

### NEW-004: Алгоритм определения корневого каталога ZIP-архива
- **Статус:** `NEEDS_VERIFICATION`
- **Приоритет:** `P3 (Medium)`
- **Компонент:** `Backend.Infrastructure` (`src/Backend/Infrastructure/Services/`)
- **Ссылки на код:**
  - [`RepositorySourceService.cs#L64-L78`](file:///c:/Users/ivank/Desktop/Uceba/Projects/CourseProject/src/Backend/Infrastructure/GitHub/RepositorySourceService.cs#L64-L78)
- **Описание:**
  - При извлечении файлов архива код выполняет:
    `string.Join(Path.DirectorySeparatorChar, segments.Skip(1))`.
  - Это рассчитано на формат архивов GitHub ZIPball, где все содержимое всегда вложено в одну корневую папку `{owner}-{repo}-{commitSha}/`.
  - Если архив упакован без общего корневого каталога («плоский» архив), вызов `segments.Skip(1)` приведет к удалению имен всех файлов в корне архива (они будут пропущены).
- **Статус верификации:** Для штатного продакшн-конвейера GitHub zipball структура гарантирована самим GitHub API. Проблема актуальна для сценариев расширения системы (ручная загрузка произвольных ZIP-архивов пользователем). Требуется адаптивный алгоритм: проверять, действительно ли все записи архива имеют общий корневой сегмент пути.

---

## 5. Статус инфраструктурных замечаний (API-020, API-022, API-024, API-025)

| ID | Статус | Анализ и зафиксированные решения |
| :--- | :---: | :--- |
| **API-020** | `OPEN` | **Хранилище в `AppContext.BaseDirectory`:** Путь `Path.Combine(AppContext.BaseDirectory, "data", "analysis")` загрязняет каталог исполняемых файлов и не работает в контейнерах с read-only root. Задача объединена с `DEF-003`: внедрить конфигурационную секцию `StorageOptions:BasePath` с дефолтом в `Path.GetTempPath()`, а каталоги изолировать по `{analysisId}_{executionToken}`. |
| **API-022** | `OPEN` | **Шаблон переменных окружения:** В репозитории отсутствуют примеры переменных окружения. Создать файл `appsettings.Example.json` с документированной структурой секций (`ConnectionStrings`, `Jwt`, `Encryption`), содержащий понятные плейсхолдеры без рабочих и фиктивных секретов. |
| **API-024** | `FIXED` | **Устаревшая документация:** Полностью устранено в рамках задачи [TD-014](../plans/Technical-Debt-Plan.md#td-014-синхронизация-базовой-документации). Документы `Технический регламент.md`, `Исполнительное резюме.md`, `Архитектура проекта.drawio` и спецификации анализаторов синхронизированы по состоянию ревизии `2068c51`. |
| **API-025** | `FIXED` | **Защита последнего администратора:** В коде [`AdminUsersController.cs#L104-L140`](file:///c:/Users/ivank/Desktop/Uceba/Projects/CourseProject/src/Backend/Api/Controllers/AdminUsersController.cs#L104-L140) и `UserService.cs` реализована надежная защита через консультативную блокировку PostgreSQL `SELECT pg_advisory_xact_lock(hashtext('admin_role_protection'))` с повторным подсчетом админов в транзакции. Дефект полностью устранен и исключен из реестра открытых проблем. |

---

## 6. Замечания по качеству тестового кода (Косметические)

- **NEW-005 (BOM в тестах очереди):** В файле [`AnalysisServiceQueueTests.cs#L3`](file:///c:/Users/ivank/Desktop/Uceba/Projects/CourseProject/tests/Backend.Tests/AnalysisServiceQueueTests.cs#L3) присутствует невидимый символ Unicode BOM. Не является дефектом логики, не включается в реестр долга, устраняется попутно при правке тестов.
