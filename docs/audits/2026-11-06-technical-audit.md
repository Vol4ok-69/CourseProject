# Технический аудит проекта CourseProject

**Дата первоначального аудита:** 06 ноября 2026 г.<br/>
**Дата последней актуализации:** 25 ноября 2026 г. (актуализация включает исправления от 25.11.2026: коммиты 10c82f0, 185379b, dcda0ff, d0fa163, а также отклонение DEF-007 / TD-009 как ложноположительного срабатывания; первоначальный аудит от 06.11.2026 сохранён как историческая база)<br/>
**Базовая ветка:** `main`
**Базовый коммит:** `d0fa163` (*fix(github-sync): classify history using verified ancestors*; базовый коммит первоначального аудита: `7d5bfbb` / ревизия `2068c51`)
**Состояние рабочего дерева:** Clean строго на дату первоначального аудита (06.11.2026); последующие обновления документа отражают результаты реализации и верификации задач техдолга.
**Верификация тестовой базы (`dotnet test`):** 1411 тестов всего: 1399 успешно пройдено, 8 известных сбоев TD-003 в `CSharpAnalyzer.Tests`, 4 динамически пропущено в `DatabaseAnalyzer.Runner.Tests` при отсутствии Docker daemon на хосте [при доступном Docker 105 успешно / 0 пропущено] (6 сборок: `Backend.Tests` — 999 успешно [0 сбоев], `DatabaseAnalyzer.Tests` — 190 успешно, `DatabaseAnalyzer.Runner.Tests` — 101 успешно / 4 пропущено `[DockerFact]`, `SecurityAnalyzer.Tests` — 56 успешно, `SqlAnalyzer.Tests` — 44 успешно, `CSharpAnalyzer.Tests` — 9 успешно / 8 сбоев TD-003; код возврата 1).
**Состояние модели EF Core:** `dotnet ef migrations has-pending-model-changes` подтверждает полное соответствие снимка `AppDbContextModelSnapshot.cs` сущностям предметной области (`No changes have been made to the model since the last migration`). Обе миграции `20261122150614_AddAnalysisRetryAndPoisonPillQueue` (TD-007) и `20261124090814_AddCommitProvenanceAndProjectSyncCheckpoint` (TD-006) применены и согласованы.

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
  - [`EfCoreModelInspector.cs#L104-L138`](../../src/Analyzers/Database/Providers/EfCoreModelInspector.cs#L104-L138) (метод `BuildProjectAsync`)
  - [`EfCoreModelInspector.cs#L71-L89`](../../src/Analyzers/Database/Providers/EfCoreModelInspector.cs#L71-L89) (метод `InspectAsync`)
  - [`EfCoreModelInspector.cs#L224`](../../src/Analyzers/Database/Providers/EfCoreModelInspector.cs#L224) (метод `LoadProjectAssemblyAsync`)

1. **Первоначальное обнаруженное состояние (на 06.11.2026):**
   - **Выполнение произвольного MSBuild-кода:** Метод `BuildProjectAsync` запускал внешний процесс `dotnet build "{projectPath}"` над кодом, скачанным из стороннего репозитория. В файлах `.csproj`, `.props` или `.targets` злоумышленник мог объявить задачи сборки с выполнением произвольных системных команд через `<Exec Command="..." />` или inline-задачи Roslyn `<UsingTask TaskFactory="RoslynCodeTaskFactory" ...>`, которые исполнялись с полными правами процесса бэкенда на хосте.
   - **Проблема `Assembly.LoadFrom`:** Скомпилированная сборка загружалась вызовом `Assembly.LoadFrom(assemblyPath)` в `AssemblyLoadContext.Default`. В .NET Core / .NET такие сборки не могут быть выгружены из памяти, что приводило к монотонному накоплению сборок, утечке памяти CLR и конфликтам версий (`TypeLoadException`).
   - **Статические конструкторы и инстанцирование:** При поиске реализаций `IDesignTimeDbContextFactory<T>` и вызове `Activator.CreateInstance(factoryType)` управление передавалось пользовательскому коду фабрик и статических конструкторов (`.cctor`) внутри основного процесса сервера.
   - **Ограничения `AssemblyLoadContext`:** Коллекционируемый `AssemblyLoadContext` (`isCollectible: true`) решает только вопрос GC-выгрузки сборки, но не является песочницей (не ограничивает доступ к файлам, сети, процессам ОС и памяти).
   - **Сценарий воспроизведения:** Пользователь отправляет репозиторий с таргетом `<Target Name="Exploit" AfterTargets="Build"><Exec Command="powershell -Command ..." /></Target>`, приводящим к RCE на хосте.

2. **Принятое архитектурное решение (ADR-001, статус `ACCEPTED`):**
   - Утверждён двухкомпонентный подход ([ADR-001](../architecture/database-analyzer-isolation.md)):
     1. Извлечение метаданных EF Core моделей через статический парсинг C# AST на базе `Roslyn` без сборки проектов (`dotnet build`) и без динамической загрузки сторонних сборок (`Assembly.LoadFrom`, `AssemblyLoadContext`), что полностью исключает выполнение недоверенного кода в основном процессе сервера и накопление невыгружаемых пользовательских сборок в `AssemblyLoadContext.Default`. Поддерживается согласованный набор Fluent API (`HasKey`, `HasIndex`, `IsUnique`, `HasDatabaseName`, `HasFilter`, `HasOne`, `WithMany`, `WithOne`, `HasMany`, `HasForeignKey`, `ToTable`) и Data Annotations (`[Key]`, `[ForeignKey]`, `[Index]`, `[Table]`, `[Required]`), разделение нескольких `DbContext`, типы в `ApplyConfiguration` / `ApplyConfigurationsFromAssembly` и статический обход методов конфигурации глубиной до 3 уровней с защитой от циклов (при невозможности статического анализа выдается предупреждение `Severity.Warning`).
     2. Использование внешнего изолированного Docker-контейнера для глубокого анализа структуры БД (PostgreSQL/SQL), DDL-скриптов и локальных утилит анализа: строго офлайн (`--network none`), без доступа к рабочей/пользовательской БД по сети, read-only root, RAM-диск `/tmp`, лимиты CPU/RAM/pids, непривилегированный пользователь `appuser` (`10001:10001`), обмен по версионированному JSON-контракту.
     3. Сохранение `SqlAnalyzer` (`src/Analyzers/Sql`) для синтаксического анализа SQL на уровне C# AST (правила `SQL001`, `SQL002`) без дублирования задач контейнера.

3. **Фактически реализованные компоненты (по состоянию на 20.11.2026):**
   - В `src/Analyzers/Database/Roslyn/` реализованы синтаксические парсеры `DataAnnotationParser`, `FluentApiParser` и фасад `RoslynMetadataExtractor`, извлекающие метаданные моделей EF Core без вызова `dotnet build` и без загрузки сборок.
   - В `src/Analyzers/Database.Runner/` реализован автономный исполняемый раннер `NetScope.DatabaseAnalyzer.Runner`, проверяющий правила `DB01`–`DB05` со своими моделями DTO.
   - Реализован версионированный JSON-протокол обмена через файлы `/exchange/in/input.json` (монтирование readonly) и `/exchange/out/output.json` с независимой валидацией схем на стороне бэкенда и раннера.
   - В бэкенде реализованы `DockerCliProcessAdapter` и `DatabaseContainerRunner`, запускающие контейнер `netscope-database-analyzer:latest` со всеми политиками безопасности (`--network none`, `--read-only`, `tmpfs`, лимиты CPU/памяти/процессов, `appuser`, таймаут 60 с).
   - Модульные тесты подтверждают корректность работы компонентов: `DatabaseAnalyzer.Tests` (190 тестов) и `DatabaseAnalyzer.Runner.Tests` (105 тестов).

4. **Статус задачи:**
   - Задача **`TD-001`** ([Technical-Debt-Plan.md](../plans/Technical-Debt-Plan.md#td-001)), статус реализации: **`VERIFIED`**. Все 10 приёмочных критериев Stage 2 подтверждены тестами парсинга моделей Fluent API / Data Annotations, разделения контекстов и запуска изолированного раннера.

---

### DEF-002: Сбой всего анализа коммита при отсутствии `IDesignTimeDbContextFactory`
- **Статус:** `RESOLVED` (Устранён в рамках задачи [TD-002](../plans/Technical-Debt-Plan.md#td-002))
- **Приоритет:** `P1 (Critical)`
- **Компонент:** `DatabaseAnalyzer` (`src/Analyzers/Database/`), `AnalysisService` (`src/Backend/Application/Services/AnalysisService.cs`)
- **Ссылки на код:**
  - [`DatabaseAnalyzer.cs#L25-L100`](../../src/Analyzers/Database/DatabaseAnalyzer.cs)
  - [`AnalysisService.cs#L190-L368`](../../src/Backend/Application/Services/AnalysisService.cs#L190-L368)
  - [`IDatabaseAnalyzer.cs`](../../src/Backend/Application/Analyzers/IDatabaseAnalyzer.cs)

1. **Первоначальное обнаруженное состояние (на 06.11.2026):**
   - Метод `EfCoreModelInspector.InspectAsync` искал исключительно реализацию интерфейса `IDesignTimeDbContextFactory<T>`. Если исследуемый проект использовал стандартную регистрацию `AddDbContext<AppDbContext>` в `Program.cs` и не объявлял фабрику времени разработки, инспектор выбрасывал исключение `InvalidOperationException("No IDesignTimeDbContextFactory was found...")`.
   - Исключение не перехватывалось в `EfCoreMetadataProvider`, пробивало конвейер до `AnalysisService.ExecuteAnalysisAsync` и переводило весь анализ коммита в статус `Failed`.
   - При этом результаты успешно отработавших анализаторов C#, SQL и безопасности безвозвратно терялись.

2. **Принятое архитектурное решение (ADR-001, статус `ACCEPTED`):**
   - **Отказоустойчивость конвейера:** Сбой или невозможность построения модели базы данных в `DatabaseAnalyzer` (или ошибка во внешнем контейнере анализатора БД) не прерывает работу других анализаторов (`CodeAnalyzer`, `SqlAnalyzer`, `SecurityAnalyzer`).
   - **Сохранение успешных результатов:** Находки успешно завершившихся анализаторов штатно сохраняются в базе данных. Общий статус коммита переходит в `Completed`.
   - **Информирование пользователя:** Причина сбоя анализатора БД фиксируется через диагностическое замечание `AnalyzerFinding` с уровнем `Severity.Warning` и понятным описанием на русском языке.
   - **Категоризация причин:** Различаются отсутствие `DbContext` / конфигурации, синтаксические/семантические ошибки C#, неподдерживаемые динамические вызовы/рефлексия и таймаут/сбой контейнера.
   - **Защита от ложных срабатываний:** Если метаданные модели извлечены частично, правила `DB01`–`DB05` не должны выдавать ложные предупреждения по неполным связям; проверки пропускаются или выполняются только по гарантированно разрешенным сущностям с явной отметкой о частичности модели.
   - **Категорический запрет универсальной подмены SQLite:** Запрещено использовать универсальную подмену провайдера на SQLite in-memory (`UseSqlite`), так как реальные модели EF Core содержат PostgreSQL-специфичные конструкции (`jsonb`, массивы, расширения `citext`, identity), приводящие к сбою валидации в SQLite.

3. **Фактически реализованное решение (ноябрь 2026 г., TD-002):**
   - Реализованный статический Roslyn-парсер (`RoslynMetadataExtractor`) не требует наличия `IDesignTimeDbContextFactory<T>` и извлекает метаданные напрямую из синтаксических деревьев `DbContext` и конфигурационных классов.
   - Автономный раннер `Database.Runner` выполняет валидацию входных данных и возвращает структурированный результат со статусом выполнения.
   - В `AnalysisService.cs` реализована отказоустойчивость: сбой или информационный пропуск `DatabaseAnalyzer` изолируется и не роняет общий конвейер, если отработал хотя бы один другой анализатор. Находки сохраняются, формируются предупреждения `Severity.Warning` (`DatabaseAnalyzerRules.InternalAnalyzerError`, `ContainerError`, `ContainerTimeout`, `ExtractionError`).
   - Разграничены информационный пропуск (`MissingDbContext`, `DatabaseAnalysisExecutionStatus.InformationSkipped`) и критические ошибки.
   - Реализована защита целостности Lease Renewal (`LeaseRenewalState`) и гарантий владения задачей: штатная остановка не может замаскировать критическую ошибку продления аренды, а при потере владения запрещены модифицирующие операции (`FailAsync`, `CompleteAsync`, небезопасная очистка).

4. **Статус устранения дефекта:**
   - Дефект **полностью устранён** в рамках задачи **`TD-002`** ([Technical-Debt-Plan.md](../plans/Technical-Debt-Plan.md#td-002)), статус реализации: **`VERIFIED`**.
   - Решение подтверждено набором из 91 теста в `AnalysisServiceQueueTests` (включая сценарии A–G и 6 специальных тестов граничных условий Lease Renewal), 0 сбоев.

---

### DEF-003: Изоляция рабочих каталогов анализа по токену выполнения и безопасная очистка
- **Статус:** `CONFIRMED`
- **Приоритет:** `P1 (Critical)`
- **Компонент:** `Backend.Application` / `Backend.Infrastructure`
- **Ссылки на код:**
  - [`RepositorySourceService.cs#L50-L55`](../../src/Backend/Infrastructure/GitHub/RepositorySourceService.cs#L50-L55)
  - [`RepositorySourceService.cs#L60-L75`](../../src/Backend/Infrastructure/GitHub/RepositorySourceService.cs#L60-L75) (очистка при сбое распаковки)
  - [`AnalysisService.cs#L210-L225`](../../src/Backend/Application/Services/AnalysisService.cs#L210-L225) (основной `finally`)

1. **Первоначальное обнаруженное состояние (на 06.11.2026):**
   - Рабочий каталог для распаковки репозитория формировался без учета токена выполнения:
     `analysisDirectory = Path.Combine(storageDirectory, analysisId.ToString(), "repository")`.
   - Внутри `RepositorySourceService.DownloadAndExtractAsync` при ошибке скачивания вызывалась локальная очистка `await CleanupAsync(analysisId, ct)`, жестко удалявшая директорию `data/analysis/{analysisId}` без проверки токена выполнения.
   - При истечении аренды задачи в PostgreSQL воркер B перехватывал задачу с новым `ExecutionToken` и начинал распаковку в тот же каталог `data/analysis/{analysisId}`. Запоздавший воркер A в это время мог вызвать очистку и удалить каталог активного воркера B, приводя к гонке и сбою.

2. **Принятое архитектурное решение (ADR-001, статус `ACCEPTED`):**
   - **Структура пути:** Формировать рабочий каталог строго по схеме: `{Storage:AnalysisPath}/{AnalysisId}/{ExecutionToken}`.
   - **Базовый путь хранилища:** Вынести путь в конфигурацию `Storage:AnalysisPath` в `appsettings.json` со значением по умолчанию `Path.Combine(Path.GetTempPath(), "CourseProject", "analyses")` (категорический запрет использования `AppContext.BaseDirectory`).
   - **Сквозная передача ExecutionToken:** Токен генерируется воркером при взятии задачи и передается через весь конвейер.
   - **Запрет очистки по маске/AnalysisId:** Категорически запрещено удалять каталоги по маске или только по `AnalysisId`.
   - **Немедленная очистка:** В блоке `finally` удалять строго каталог текущего токена `{Storage:AnalysisPath}/{AnalysisId}/{ExecutionToken}`.
   - **Фоновая очистка «сиротских» каталогов (`Orphan directory cleanup`):**
     1. Запрет удаления каталогов только по возрасту (TTL).
     2. Проверка в БД по паре `(AnalysisId, ExecutionToken)`: удаление разрешено только при подтверждённом отсутствии активного исполнения с этим токеном.
     3. Неприкосновенность новых исполнений: каталоги с другим токеном (`{AnalysisId}/{TokenB}`) неприкосновенны.
     4. Единый протокол сессионных advisory locks: воркер удерживает `pg_advisory_lock_shared` для `AnalysisId` на всё время файловых операций и повторно проверяет актуальность токена в БД перед операциями. Сервис очистки получает эксклюзивный `pg_advisory_lock`, повторно проверяет токен в БД и удаляет только директории неактуальных токенов.
     5. При потере соединения с БД воркер прекращает файловые операции, а сервис очистки прекращает удаление.

3. **Фактически реализованные компоненты (по состоянию на ноябрь 2026 г.):**
   - В `RepositorySourceService.cs` поддержана схема пути `{Storage:AnalysisPath}/{AnalysisId}/{ExecutionToken}` и конфигурационный ключ `Storage:AnalysisPath` (дефолт `Path.Combine(Path.GetTempPath(), "CourseProject", "analyses")`), исключающий размещение рабочих файлов в `AppContext.BaseDirectory`.
   - В `AnalysisService.cs` и `RepositorySourceService.cs` поддержана сквозная передача `executionToken` и требование `IAnalysisLeaseGuard` для операций с рабочим пространством.
   - Очистка рабочего каталога в блоке `finally` `AnalysisService.cs` защищена проверкой подтверждённого владения задачей (`ownershipConfirmed == true`).

4. **Оставшиеся проблемы и решение об откладывании:**
   - **Фоновая очистка «сиротских» каталогов:** механизм периодической фоновой очистки каталогов, оставшихся после аварийных завершений процессов (OOM, SIGKILL, падение хоста), в кодовой базе не реализован.
   - **Протокол сессионных блокировок:** единый протокол сессионных `pg_advisory_lock_shared` (для воркеров на время файловых операций) и эксклюзивного `pg_advisory_lock` (для сервиса очистки) не реализован.
   - **Тестовые сбои в `CSharpAnalyzer.Tests`:** в тестовом классе `RepositorySourceServiceTests.cs` сохраняются 8 известных сбоев тестов, вызывающих устаревшие методы без передачи мока `IAnalysisLeaseGuard`.
   - **Решение о планировании:** задача **`TD-003`** ([Technical-Debt-Plan.md](../plans/Technical-Debt-Plan.md#td-003)) сохраняет статус реализации **`OPEN`**, статус проверки **`UNVERIFIED`** и статус планирования **`DEFERRED`** (Отложена). На текущем этапе её дальнейшая реализация временно приостановлена и не входит в ближайший этап разработки; возвращение к ней допускается только по отдельному решению.

---

### DEF-004: Ложные срабатывания правила DB04 для первичных и альтернативных ключей
- **Статус:** `RESOLVED` (Закрыто в рамках задачи [TD-004](../plans/Technical-Debt-Plan.md#td-004))
- **Приоритет:** `P2 (High)`
- **Компонент:** `DatabaseAnalyzer` (`src/Analyzers/Database/`)
- **Ссылки на код:**
  - [`EfCoreMetadataMapper.cs#L37-L58`](../../src/Analyzers/Database/Mapping/EfCoreMetadataMapper.cs#L37-L58)
  - [`DatabaseAnalyzer.cs#L101-L120`](../../src/Analyzers/Database/DatabaseAnalyzer.cs#L101-L120)
- **Первоначальное обнаруженное состояние (на 06.11.2026, историческое описание):**
  - В `EfCoreMetadataMapper` список индексов сущности формировался исключительно из явных индексов модели `entityType.GetIndexes()`.
  - В реляционных базах данных (PostgreSQL) первичный ключ (`entityType.FindPrimaryKey()`) и альтернативные ключи уникальности (`entityType.GetKeys()`) физически создают уникальные B-дерево индексы на стороне СУБД.
  - **Ложное срабатывание:** В идентифицирующих связях «один-к-одному» или «один-ко-многим», где внешний ключ одновременно входит в состав первичного ключа (например, `OrderDetails.OrderId` является одновременно PK и FK), анализатор генерировал ложное предупреждение `DB04` («Внешний ключ не имеет индекса»).
  - **Требование к составным индексам:** Правило `DB04` должно учитывать строгий порядок столбцов в составном индексе. Индекс эффективно поддерживает внешний ключ, только если столбцы внешнего ключа образуют **точный префикс** составного индекса в том же порядке:
    ```csharp
    index.PropertyNames.Take(foreignKeyProperties.Count)
         .SequenceEqual(foreignKeyProperties, StringComparer.Ordinal)
    ```
- **Фактическое исправление и верификация (в рамках TD-004):**
  1. *Исправление в `EfCoreMetadataMapper.cs`:*
     - Первичный и альтернативные ключи извлекаются за один проход через `entityType.GetKeys()` (отдельный вызов `FindPrimaryKey()` исключен, так как первичный ключ входит в `GetKeys()`).
     - Все ключи включаются в результирующую коллекцию `Indexes` как уникальные индексы (`IsUnique = true`) с именованием ограничения `key.GetName()` (с fallback `PK_{tableName}` для PK и `AK_{tableName}_{propertyNames}` для AK).
     - Свойство `AlternateKeys` в `DatabaseEntityMetadata` заполняется только альтернативными ключами: `keys.Where(k => !k.IsPrimaryKey).ToList()`.
     - Строго сохраняется порядок свойств составных ключей (`key.Properties.Select(p => p.Name).ToList()`).
     - Обычные индексы и ключи остаются отдельными объектами в `Indexes` (без ошибочной дедупликации, сохраняя различия между явными неуникальными/уникальными индексами и ключами).
  2. *Верификация правил `DB04` и `DB05`:*
     - Поведение `DatabaseRuleEvaluator` для правила `DB04` проверено на реальных метаданных `EfCoreMetadataMapper`: связи 1:1 (PK/FK), покрытие альтернативным ключом, составные индексы с совпадающим префиксом, индексы с обратным порядком столбцов.
     - Проверено влияние на правило `DB05`: само правило не изменялось, поведение для комбинаций PK и явных индексов соответствует ожидаемому.
  3. *Разграничение конвейеров:*
     - Дефект DEF-004 относился исключительно к историческому in-process пути (`EfCoreMetadataMapper` + `DatabaseRuleEvaluator`), который не используется в основном DI-контейнере бэкенда.
     - В активном production-конвейере Roslyn AST (`RoslynMetadataExtractor`) + версионированный JSON v1 (`/exchange/in/input.json`) + изолированный Docker Runner (`netscope-database-analyzer:latest`, `DatabaseRulesEngine`) данный дефект не воспроизводился.
  4. *Тестовая база и подтверждённый запуск (21.11.2026):*
     - В рамках TD-004 добавлено 15 тестов в `DatabaseAnalyzer.Tests` (не путать с общим количеством тестов анализатора): 7 unit-тестов маппера в [`tests/DatabaseAnalyzer.Tests/Mapping/EfCoreMetadataMapperTests.cs`](../../tests/DatabaseAnalyzer.Tests/Mapping/EfCoreMetadataMapperTests.cs) и 8 регрессионных тестов в [`tests/DatabaseAnalyzer.Tests/DatabaseAnalyzerTests.cs`](../../tests/DatabaseAnalyzer.Tests/DatabaseAnalyzerTests.cs);
     - Фактический результат подтверждённого тестового запуска от 21.11.2026:
       - `DatabaseAnalyzer.Tests`: 188/188 выполненных тест-кейсов (в кодовой базе объявлено 180 тестовых методов: 176 `[Fact]` + 4 `[Theory]`), 0 сбоев;
       - `DatabaseAnalyzer.Runner.Tests`: 103/103 выполненных тест-кейса (в кодовой базе объявлено 82 тестовых метода: 76 `[Fact]` + 6 `[Theory]`), 0 сбоев;
       - Суммарно выполнено 291 тест-кейс (262 тестовых метода в кодовой базе), код возврата 0.

---

### DEF-005: Ложные срабатывания правила DB05 для частичных и фильтрованных индексов
- **Статус:** `RESOLVED` в целевом конвейере ADR-001 / [TD-005](../plans/Technical-Debt-Plan.md#td-005) (Legacy-класс `EfCoreMetadataMapper.cs` не используется в production)
- **Приоритет:** `P2 (High)`
- **Компонент:** `DatabaseAnalyzer` (`src/Analyzers/Database/`, `src/Analyzers/Database.Runner/`)
- **Ссылки на код:**
  - [`FluentApiParser.cs#L597-L603`](../../src/Analyzers/Database/Roslyn/FluentApiParser.cs#L597-L603)
  - [`DatabaseContainerRunner.cs#L434`](../../src/Analyzers/Database/Runner/DatabaseContainerRunner.cs#L434)
  - [`SqlFilterComparer.cs`](../../src/Analyzers/Database.Runner/Rules/SqlFilterComparer.cs)
  - [`DatabaseRulesEngine.cs#L528-L544`](../../src/Analyzers/Database.Runner/Rules/DatabaseRulesEngine.cs#L528-L544)
  - [`EfCoreMetadataMapper.cs#L72-L80`](../../src/Analyzers/Database/Mapping/EfCoreMetadataMapper.cs#L72-L80) *(устаревший in-process маппер)*
- **Первоначальное состояние проблемы (на 06.11.2026):**
  - Устаревший класс `EfCoreMetadataMapper` при извлечении метаданных индексов игнорировал предикат фильтра `index.GetFilter()`.
  - В реальных проектах на один и тот же столбец часто создаются два индекса: общий и частичный (например, для мягкого удаления `WHERE "IsDeleted" = false`), либо два частичных индекса с непересекающимися условиями.
  - Анализатор ложно помечал частичные индексы как избыточные дубликаты DB05.
- **Фактическое состояние реализации в целевом конвейере (ADR-001):**
  - В целевом архитектурном решении статический экстрактор `FluentApiParser.cs` извлекает `.HasFilter(filterVal)` и сохраняет его в `IndexMetadata.Filter`.
  - Контейнерный раннер передает фильтр в DTO обмена `input.json` (`AnalysisInputContract.cs:235-236`).
  - Правило `DB05` в `DatabaseRulesEngine.cs` сравнивает фильтры через консервативный парсер `SqlFilterComparer.AreFiltersEquivalent(idx1.Filter, idx2.Filter, out var isAmbiguous)`. Если предикаты фильтрации отличаются или фильтр задан только для одного индекса, дубликат не рапортуется (ложное срабатывание исключено).
  - Поведение верифицировано тестом `DatabaseRulesEngineTests.cs:1192` (`DB05_DifferentFiltersOnSameColumns_NoFinding`) и набором `SqlFilterComparerTests.cs`.
  - Устаревший класс `EfCoreMetadataMapper.cs` не используется ни в одном сервисе бэкенда. Дефект в эксплуатируемом конвейере полностью устранён.

---

### DEF-006: Ограничение длины хеша коммита в CreateCommitDto (SHA-1 vs SHA-256)
- **Статус:** `RESOLVED` (Устранён в рамках задачи [TD-008](../plans/Technical-Debt-Plan.md#td-008))
- **Приоритет:** `P3 (Medium)`
- **Компонент:** `Backend.Application` (`src/Backend/Application/DTOs/`)
- **Ссылки на код:**
  - [`CreateCommitDto.cs#L12-L16`](../../src/Backend/Application/DTOs/CreateCommitDto.cs#L12-L16)
  - [`Commit.cs#L9`](../../src/Backend/Domain/Entities/Commit.cs#L9)
  - [`CommitConfiguration.cs#L17-L18`](../../src/Backend/Infrastructure/Data/Configurations/CommitConfiguration.cs#L17-L18)
- **Первоначальное обнаруженное состояние (на 06.11.2026):**
  - В DTO ручного создания коммита `CreateCommitDto` был задан атрибут: `[StringLength(40, MinimumLength = 7)]`. Это блокировало ручное добавление коммитов современных репозиториев Git, использующих алгоритм хеширования SHA-256 (длина хеша 64 символа).
  - При этом доменная сущность `Commit.cs` содержит `public string CommitHash { get; set; }` без ограничений длины.
  - Конфигурация EF Core `CommitConfiguration.cs` отображает свойство в тип `text` PostgreSQL, а составной индекс `IX_Commits_ProjectId_CommitHash` успешно индексирует строки до 64+ символов.
- **Фактически реализованное решение (ноябрь 2026 г., коммит `57da9b8`):**
  1. В `CreateCommitDto.cs` расширен допустимый диапазон длины: `[StringLength(64, MinimumLength = 7, ErrorMessage = "Хэш коммита должен содержать от 7 до 64 символов.")]`.
  2. Добавлена строгая проверка шестнадцатеричного формата: `[RegularExpression(@"\A[0-9a-fA-F]{7,64}\z", ErrorMessage = "Хэш коммита должен содержать только шестнадцатеричные символы.")]`.
  3. Реализация полностью подтверждена тестами:
     - unit-тесты валидации DTO в `DtoValidationAndAnalysisErrorTests.cs` (тесты SHA-1, SHA-256, 7 hex, смешанного регистра и 11 негативных граничных случаев);
     - сквозные тесты в `ApiPipelineIntegrationTests.cs`: `CreateCommit_WithSha256Hash_SucceedsAndPersistsInDatabase` (201 Created + сохранение в PostgreSQL) и `CreateCommit_WithInvalidHash_Returns400BadRequest_WithValidationProblemDetails` (400 ProblemDetails).
  - Дефект полностью устранён, задача TD-008 переведена в статус `VERIFIED`.

---

### DEF-007: Пропуск небезопасной конфигурации CORS через `WithOrigins("*")` в SEC003
- **Статус:** `REJECTED / FALSE POSITIVE`
- **Приоритет:** `P3 (Medium)`
- **Компонент:** `SecurityAnalyzer` (`src/Analyzers/Security/Rules/`)
- **Ссылки на код:**
  - [`Sec003InsecureCorsRule.cs#L28-L60`](../../src/Analyzers/Security/Rules/Sec003InsecureCorsRule.cs#L28-L60)
- **Первоначальное описание проблемы (почему замечание было выдвинуто):**
  - Правило `Sec003InsecureCorsRule` синтаксически проверяет наличие вызовов `AllowAnyOrigin()` и предикатов `SetIsOriginAllowed(_ => true)`.
  - Предполагалось, что вызов `.WithOrigins("*")` открывает доступ для любых источников аналогично `AllowAnyOrigin()`, но пропускается синтаксическим анализатором.
- **Техническая причина отклонения замечания (False Positive):**
  - По результатам детальной проверки механизма сопоставления CORS в ASP.NET Core установлено, что вызов `CorsPolicyBuilder.WithOrigins("*")` добавляет строковый литерал `"*"` в коллекцию конкретных разрешённых адресов (`Origins`), а не переключает политику в режим `AllowAnyOrigin` (который очищает `Origins` и устанавливает внутренний флаг `_allowAnyOrigin = true`).
  - При обработке входящего HTTP-запроса промежуточный слой CORS в ASP.NET Core производит точное посимвольное сопоставление значения заголовка `Origin` с элементами списка `Origins`. Строка `"*"` не интерпретируется как глобальный wildcard: браузерный запрос с заголовком `Origin: https://evil.com` не совпадёт со значением `"*"` и будет отклонён.
  - Таким образом, вызов `.WithOrigins("*")` не является функциональным эквивалентом `AllowAnyOrigin()` и не разрешает произвольные источники. Это некорректная конфигурация, разрешающая запросы только при буквальной передаче клиентом заголовка `Origin: *`, но не создающая уязвимости чрезмерно открытого CORS для сторонних доменов.
  - Добавление проверки `WithOrigins("*")` в правило `SEC003` приводило бы к ложноположительной диагностике безопасности (ошибочной классификации некорректной настройки как уязвимости раскрытия данных).
  - Реализация в `Sec003InsecureCorsRule` не требуется. Существующее поведение правила остаётся корректным для действительно опасных конструкций (`AllowAnyOrigin()` и предикатов `SetIsOriginAllowed`, безусловно возвращающих `true`).

---

### DEF-008: Игнорирование `ServicePointManager.ServerCertificateValidationCallback` в SEC005
- **Статус:** `CONFIRMED`
- **Приоритет:** `P3 (Medium)`
- **Компонент:** `SecurityAnalyzer` (`src/Analyzers/Security/Rules/`)
- **Ссылки на код:**
  - [`Sec005DisabledCertificateValidationRule.cs#L16, L27-L31`](../../src/Analyzers/Security/Rules/Sec005DisabledCertificateValidationRule.cs#L16)
- **Описание проблемы:**
  - Правило `Sec005DisabledCertificateValidationRule` проверяет присваивание свойства с точным именем `ServerCertificateCustomValidationCallback` (свойство обработчиков `HttpClientHandler` и `SocketsHttpHandler`).
  - Классический глобальный механизм отключения валидации сертификатов в .NET — присваивание статического свойства `ServicePointManager.ServerCertificateValidationCallback` — правилом не проверяется.
- **Требуемое исправление:**
  - Добавить проверку присваивания `ServicePointManager.ServerCertificateValidationCallback` предикатом, безусловно возвращающим `true`.

---

### DEF-009 и NEW-001: Инкрементальная синхронизация коммитов репозитория GitHub
- **Статус:** `RESOLVED` (Устранён в рамках задачи [TD-006](../plans/Technical-Debt-Plan.md#td-006-инкрементальная-синхронизация-коммитов-репозитория-github): утверждённая архитектура Rev 4, коммит `7d5bfbb`, миграция `20261124090814_AddCommitProvenanceAndProjectSyncCheckpoint`, потоковый DAG-обход, композитный чекпоинт, 27 тестовых методов в `GitHubSyncDagTraversalTests` [полностью покрывающих 25 архитектурных сценариев TR-01 – TR-25] и 4 интеграционных теста PostgreSQL)
- **Приоритет:** `P2 (High)`
- **Компонент:** `Backend.Infrastructure` (`src/Backend/Infrastructure/Services/`, `GitHubService.cs`, `GitHubSyncService.cs`, `GitHubSyncCoordinator.cs`)
- **Ссылки на код:**
  - [`GitHubService.cs#L26-L105`](../../src/Backend/Infrastructure/GitHub/GitHubService.cs#L26-L105) (пагинация коммитов)
  - [`GitHubSyncService.cs#L45-L75`](../../src/Backend/Application/Services/GitHubSyncService.cs#L45-L75) (оркестрация синхронизации)
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
  - [`Sql001UnsafeQueryRule.cs`](../../src/Analyzers/Sql/Rules/Sql001UnsafeQueryRule.cs)
  - [`Sql002SelectStarRule.cs`](../../src/Analyzers/Sql/Rules/Sql002SelectStarRule.cs)
- **Описание проблемы:**
  - Правила сопоставляют имена методов `FromSqlRaw`, `ExecuteSqlRaw`, `SqlQueryRaw` исключительно по синтаксическому имени идентификатора. Если пользовательский класс объявляет метод с таким же именем, не имеющий отношения к EF Core, правило выдаст ложное срабатывание.
  - Однако для условий курсового проекта, анализирующего разнородные сторонние репозитории без гарантированного `dotnet restore` и компиляции, синтаксический анализ является обоснованным инженерным компромиссом. Задача отложена до этапа внедрения опционального семантического анализа.

---

## 4. Новые обнаруженные архитектурные вопросы (NEW-002, NEW-003, NEW-004)

### NEW-002 и NEW-003: Управление жизненным циклом очереди, retry-политика и защита от Poison Pill
- **Статус:** `RESOLVED` (Устранён в рамках задачи [TD-007](../plans/Technical-Debt-Plan.md#td-007-управление-жизненным-циклом-очереди-retry-backoff-и-poison-pill): коммит `852f82e`, миграция `20261122150614_AddAnalysisRetryAndPoisonPillQueue`, внедрение экспоненциального backoff, строгий контроль владения задачей через `IAnalysisLeaseGuard`, протокольный парсер wire protocol PostgreSQL, восстановление при потере COMMIT ACK, защита от Poison Pill, 1302 теста решения)
- **Приоритет:** `P2 (High)`
- **Компонент:** `Backend.Domain` / `Backend.Application` / `Backend.Infrastructure`
- **Ссылки на код:**
  - [`Analysis.cs#L20-L30`](../../src/Backend/Domain/Entities/Analysis.cs#L20-L30)
  - [`AnalysisService.cs#L190-L205`](../../src/Backend/Application/Services/AnalysisService.cs#L190-L205)
  - [`AnalysisBackgroundService.cs#L45-L65`](../../src/Backend/Api/BackgroundServices/AnalysisBackgroundService.cs#L45-L65)
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
  - [`RepositorySourceService.cs#L64-L78`](../../src/Backend/Infrastructure/GitHub/RepositorySourceService.cs#L64-L78)
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
| **API-020** | `OPEN` *(DEFERRED)* | **Хранилище в `AppContext.BaseDirectory`:** Путь `Path.Combine(AppContext.BaseDirectory, "data", "analysis")` загрязняет каталог исполняемых файлов и не работает в контейнерах с read-only root. Задача объединена с `DEF-003` / `TD-003`: внедрить конфигурационную секцию `Storage:AnalysisPath` с дефолтом в `Path.Combine(Path.GetTempPath(), "CourseProject", "analyses")`, а каталоги изолировать по `{Storage:AnalysisPath}/{AnalysisId}/{ExecutionToken}` с безопасной очисткой. Реализация отложена (`DEFERRED`) до отдельного решения. |
| **API-022** | `FIXED` | **Шаблон переменных окружения:** Полностью устранено в рамках реализации Этапа 6: осуществлен переход на единый механизм конфигурации ASP.NET Core через переменные окружения и файл `.env`, создан документированный шаблон `.env.example`, механизм User Secrets полностью выведен из эксплуатации, а `appsettings.json` очищен от секретов (задача TD-013 закрыта). |
| **API-024** | `FIXED` | **Устаревшая документация:** Полностью устранено в рамках задачи [TD-014](../plans/Technical-Debt-Plan.md#td-014-синхронизация-базовой-документации). Документы `Технический регламент.md`, `Исполнительное резюме.md`, `Архитектура проекта.drawio` и спецификации анализаторов синхронизированы по состоянию ревизии `2068c51`. |
| **API-025** | `FIXED` | **Защита последнего администратора:** В коде [`AdminUsersController.cs#L104-L140`](../../src/Backend/Api/Controllers/AdminUsersController.cs#L104-L140) и `UserService.cs` реализована надежная защита через консультативную блокировку PostgreSQL `SELECT pg_advisory_xact_lock(hashtext('admin_role_protection'))` с повторным подсчетом админов в транзакции. Дефект полностью устранен и исключен из реестра открытых проблем. |

---

## 6. Замечания по качеству тестового кода (Косметические)

- **NEW-005 (BOM в тестах очереди):** В файле [`AnalysisServiceQueueTests.cs#L3`](../../tests/Backend.Tests/AnalysisServiceQueueTests.cs#L3) присутствует невидимый символ Unicode BOM. Не является дефектом логики, не включается в реестр долга, устраняется попутно при правке тестов.

---

## 7. Актуализация результатов исправлений от 25.11.2026

25 ноября 2026 года в ходе дополнительного технического аудита и стабилизации кодовой базы были выявлены и устранены четыре подтверждённых дефекта. Во избежание конфликта с исторической нумерацией дефектов первоначального аудита от 06.11.2026 результаты зафиксированы в настоящем разделе с указанием полных хешей коммитов, затрагиваемых компонентов и подтверждающей тестовой базы.

### 7.1. Маппинг исключений синхронизации GitHub в RFC 9457 ProblemDetails (DEF-001 от 25.11.2026)
- **Статус:** `RESOLVED`
- **Коммит:** [`10c82f0095810bdd22511e613e5a72cbfc27955b`](https://github.com/Ivan-Vol4ok69/CourseProject/commit/10c82f0095810bdd22511e613e5a72cbfc27955b) (*fix(api): map GitHub sync exceptions to problem details*)
- **Компонент:** `Backend.Api` (`src/Backend/Api/Controllers/ProjectsController.cs`, `src/Backend/Api/Common/ApiErrorCodes.cs`)
- **Устранённая проблема:**
  - В методе `ProjectsController.SyncGitHub` отсутствовал перехват специфических исключений `SyncCheckpointChangedException` (выбрасывается при параллельном изменении контрольной точки синхронизации) и `IncompleteHistoryException` (выбрасывается при получении неполной истории коммитов из GitHub API).
  - Неперехваченные исключения приводили к неконтролируемому возврату HTTP 500 Internal Server Error с потенциальной утечкой внутренних деталей реализации (трассировка стека, внутренние токены и хеши).
- **Реализованное решение:**
  - В `ProjectsController.SyncGitHub` добавлены явные блоки `catch`:
    - `SyncCheckpointChangedException`: возвращает HTTP 409 Conflict со структурированным объектом ProblemDetails и кодом ошибки `ApiErrorCodes.SyncCheckpointChanged` (`SYNC_CHECKPOINT_CHANGED`);
    - `IncompleteHistoryException`: возвращает HTTP 502 Bad Gateway со структурированным объектом ProblemDetails и кодом ошибки `ApiErrorCodes.GitHubIncompleteHistory` (`GITHUB_INCOMPLETE_HISTORY`), без утечки внутренней информации.
  - В `ApiErrorCodes.cs` добавлены константы кодов ошибок `SyncCheckpointChanged` и `GitHubIncompleteHistory`.
- **Подтверждающие тесты:**
  - Добавлен интеграционный тестовый класс [`GitHubSyncExceptionMappingIntegrationTests.cs`](../../tests/Backend.Tests/Integration/GitHubSyncExceptionMappingIntegrationTests.cs) (3 метода / 7 тест-кейсов с `[Theory]`), проверяющий возврат HTTP 409 и HTTP 502 ProblemDetails, отсутствие утечек внутренних строк и сохранение контракта существующих исключений. Все тесты выполнены успешно.

### 7.2. Завершение и ожидание задачи продления аренды анализа (DEF-003 от 25.11.2026)
- **Статус:** `RESOLVED`
- **Коммит:** [`185379bf0bd00b62115fdaaefa85cba900d5453c`](https://github.com/Ivan-Vol4ok69/CourseProject/commit/185379bf0bd00b62115fdaaefa85cba900d5453c) (*fix(analysis): ensure lease renewal task is awaited*)
- **Компонент:** `Backend.Application` (`src/Backend/Application/Services/AnalysisService.cs`)
- **Устранённая проблема:**
  - В `AnalysisService.cs` фоновая задача периодического продления аренды `leaseRenewalTask` на путях завершения и обработки исключений останавливалась через `linkedCts.Cancel()`, однако сам объект задачи `leaseRenewalTask` не всегда безопасно ожидался (`await`), что могло приводить к фоновым гонкам при освобождении `IAnalysisLeaseGuard` и очистке рабочего каталога в блоке `finally`.
  - Примечание: данное исправление не отменяет и не переопределяет историческую инфраструктурную задачу `DEF-003` / `TD-003` (фоновая очистка «сиротских» каталогов и сессионные advisory locks), которая сохраняет статус `DEFERRED`.
- **Реализованное решение:**
  - Реализован централизованный вспомогательный метод `StopLeaseRenewalAsync(linkedCts, leaseRenewalTask)`, гарантирующий отмену токена, безопасное ожидание `await leaseRenewalTask` и подавление ожидаемого `OperationCanceledException`.
  - Метод вызывается на всех путях выхода из `ExecuteAnalysisAsync`, включая нормальное завершение, отмену, ошибки анализаторов и блок `finally` перед очисткой каталога.
- **Подтверждающие тесты:**
  - Добавлен тестовый класс [`AnalysisLeaseRenewalLifecycleTests.cs`](../../tests/Backend.Tests/Unit/AnalysisLeaseRenewalLifecycleTests.cs) (10 специализированных тестов жизненного цикла задачи продления аренды: отмена по токену, порядок остановки, исключение ошибок продления, завершение задачи строго до диспоуза guard). Все 10 тестов выполнены успешно.

### 7.3. Валидация даты коммита, обязательность UTC и нормализация CommitHash (DEF-002 от 25.11.2026)
- **Статус:** `RESOLVED`
- **Коммит:** [`dcda0ff058c50fe3130b39880a351b97fac76ab3`](https://github.com/Ivan-Vol4ok69/CourseProject/commit/dcda0ff058c50fe3130b39880a351b97fac76ab3) (*fix(api): validate commit dates and normalize commit hashes*)
- **Компонент:** `Backend.Application` / `Backend.Infrastructure` (`CreateCommitDto.cs`, `CommitService.cs`, `GitHubSyncService.cs`, `CommitRepository.cs`)
- **Устранённая проблема:**
  - В `CreateCommitDto.cs` поле `CommitDate` было объявлено как `DateTime` (value type): отсутствующее в JSON поле привязывалось к `DateTime.MinValue`, обходя проверку обязательности.
  - Npgsql и PostgreSQL тип `timestamp with time zone` отклоняют `DateTime` со значением `DateTimeKind.Unspecified` или `DateTimeKind.Local`.
  - Значение `CommitHash` не нормализовалось к нижнему регистру при сохранении, что приводило к сбоям поиска коммитов в регистрозависимых операторах PostgreSQL (`WHERE "CommitHash" IN (...)`).
  - При синхронизации через GitHub Sync `CommitRepository.SaveSyncBatchAsync` формировал коллекцию `hashesToLookup` без предварительной нормализации `existingToUpdate`, из-за чего существующие коммиты в нижнем регистре не находились, если входной DTO содержал символы верхнего регистра.
- **Реализованное решение:**
  - В `CreateCommitDto.cs`: свойство `CommitDate` объявлено как `DateTime?` с атрибутом `[Required]`, добавлена валидация на отклонение `DateTimeKind.Unspecified`.
  - В `CommitService.cs`: добавлена нормализация даты к UTC (`DateTime.SpecifyKind(..., DateTimeKind.Utc)`) и нормализация `CommitHash.Trim().ToLowerInvariant()`.
  - В `GitHubSyncService.cs`: нормализация хешей коммитов при создании доменных сущностей.
  - В `CommitRepository.cs`: нормализация хешей в `SaveSyncBatchAsync` для `newCommits` и `existingToUpdate` до добавления в `hashesToLookup` и выполнения SQL-запроса к PostgreSQL.
- **Подтверждающие тесты:**
  - Добавлены тесты [`CommitServiceTests.cs`](../../tests/Backend.Tests/Unit/CommitServiceTests.cs) (7 тестов) и интеграционные тесты [`CommitValidationAndNormalizationIntegrationTests.cs`](../../tests/Backend.Tests/Integration/CommitValidationAndNormalizationIntegrationTests.cs) (7 тестов): проверка отклонения отсутствующей даты, `Unspecified` даты, нормализация UTC и lowercase для ручного добавления и пакетной синхронизации GitHub. Все тесты выполнены успешно.

### 7.4. Классификация общей истории GitHub и обход проверенных предков (DEF-004 от 25.11.2026)
- **Статус:** `RESOLVED`
- **Коммит:** [`d0fa163714d41fb25a0c74cd267f7c069b5034e3`](https://github.com/Ivan-Vol4ok69/CourseProject/commit/d0fa163714d41fb25a0c74cd267f7c069b5034e3) (*fix(github-sync): classify history using verified ancestors*)
- **Компонент:** `Backend.Application` (`src/Backend/Application/Services/GitHubHistoryClassifier.cs`)
- **Устранённая проблема:**
  - При инкрементальной синхронизации GitHub классификатор `GitHubHistoryClassifier` строил множество известных предков `currentAncestry` только из загруженных коммитов и непосредственных проверенных границ (`verifiedBoundaries`).
  - Если общий предок находился глубже непосредственной проверенной границы в истории проверенных коммитов БД, алгоритм не обходил проверенных родителей за границей и возвращал `UnrelatedHistory` вместо `Diverged` или `FastForward`.
  - Кроме того, требовалось исключить повторный учёт узлов при ограничении глубины обхода и сохранить приоритет доказательства `FastForward` при исчерпании лимита, если путь к общему предку уже полностью подтверждён.
- **Реализованное решение:**
  - В `GitHubHistoryClassifier.cs` реализован BFS-обход проверенных предков через очередь `boundaryQueue` с наполнением `currentAncestry`.
  - Введён единый кэш `commitCache` и лимит `DefaultMaxTraversalNodes = 10_000` уникальных исследованных коммитов за один вызов `ClassifyAsync`.
  - При обнаружении общего предка, являющегося целевым `targetRemoteCommitHash`, классификатор возвращает доказанный `FastForward` даже при достижении лимита обхода графа или обнаружении неполноты удалённых родителей.
- **Подтверждающие тесты:**
  - Добавлен тестовый класс [`GitHubHistoryClassifierTests.cs`](../../tests/Backend.Tests/Unit/GitHubHistoryClassifierTests.cs) (21 тест, включая детальные сценарии A–N для нелинейных ветвлений, обхода проверенных предков, сохранения FastForward и лимита узлов). Все 21 тест выполнены успешно.
