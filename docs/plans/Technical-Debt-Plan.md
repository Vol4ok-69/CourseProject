# План устранения технического долга CourseProject (Technical Debt Plan)

**Дата создания:** 06 ноября 2026 г.
**Базовый коммит:** `2068c51`
**Статус документа:** Действующий реестр задач на стабилизацию и устранение дефектов
**Связанный аудит:** [2026-11-06-technical-audit.md](../audits/2026-11-06-technical-audit.md)
**Архитектурные предложения:** [database-analyzer-isolation.md](../architecture/database-analyzer-isolation.md)

---

## 1. Принципы приоритизации и правила ведения

### Статусы задач:
- **`OPEN`** — задача подтверждена, сформулированы базовые критерии приемки, ожидает реализации.
- **`NEEDS_DESIGN`** — задача требует предварительного архитектурного проектирования (RFC/ADR) перед началом кодирования.
- **`ACCEPTED`** — архитектурное решение утверждено (ADR), детальные требования и критерии приемки зафиксированы, ожидает реализации в коде.
- **`NEEDS_VERIFICATION`** — дефект или сценарий требует дополнительного воспроизведения на реальных данных.
- **`IN_PROGRESS`** — задача находится в процессе реализации.
- **`FIXED`** — исправление внесено в код, но требует комплексной верификации.
- **`VERIFIED`** — исправление полностью протестировано и проверено на отсутствие регрессий.
- **`DEFERRED`** — задача отложена на этап развития после завершения MVP.
- **`REJECTED`** — задача отклонена по результатам анализа (ложное срабатывание, не применимо, кодовые изменения не требуются).

### Приоритеты:
- **`P1 (Critical)`** — безопасность, защита от Remote Code Execution, предотвращение потери данных, устранение гонок в файловой системе и критических сбоев конвейера анализа.
- **`P2 (High)`** — существенные функциональные дефекты, ложные срабатывания анализаторов, устойчивость очереди и сквозное интеграционное тестирование.
- **`P3 (Medium/Low)`** — некритичные улучшения валидации, расширение второстепенных эвристик правил безопасности, документация и шаблоны конфигурации.

---

## 2. Сводная таблица задач

| ID | Задача | Компонент | Приоритет | Архитектура | Реализация | Проверка | Миграция БД | Зависимости |
| :--- | :--- | :--- | :---: | :---: | :---: | :---: | :---: | :--- |
| **`TD-001`** | Изоляция анализатора моделей EF Core (Roslyn + Runner в Docker) | `DatabaseAnalyzer` | **P1** | `ACCEPTED` | `IMPLEMENTATION_COMPLETE` | `VERIFIED` | **Нет** | [ADR-001](../architecture/database-analyzer-isolation.md) |
| **`TD-002`** | Отказоустойчивость конвейера анализа при сбоях анализатора БД | `DatabaseAnalyzer` | **P1** | `ACCEPTED` | `IMPLEMENTATION_COMPLETE` | `VERIFIED` | **Нет** | [ADR-001](../architecture/database-analyzer-isolation.md) |
| **`TD-003`** | Изоляция рабочих каталогов `{Path}/{Id}/{Token}` и безопасная очистка | `Backend.Infrastructure` | **P1** | `ACCEPTED` | `DEFERRED` | `DEFERRED` | **Нет** | [ADR-001](../architecture/database-analyzer-isolation.md) |
| **`TD-004`** | Учет первичных/альтернативных ключей и порядка столбцов в DB04 | `DatabaseAnalyzer` | **P2** | `ACCEPTED` | `IMPLEMENTATION_COMPLETE` | `VERIFIED` | **Нет** | Закрыто: маппинг PK/AK в `Indexes`, `AlternateKeys`, 15 тестов (7 тестов маппера + 8 тестов DB04/DB05) в `DatabaseAnalyzer.Tests` |
| **`TD-005`** | Поддержка частичных/фильтрованных индексов в правиле DB05 | `DatabaseAnalyzer` | **P2** | `ACCEPTED` | `IMPLEMENTATION_COMPLETE` | `VERIFIED` | **Нет** | Закрыто в целевом конвейере Roslyn + Runner (SqlFilterComparer, DB05 filter normalization); legacy-класс EfCoreMetadataMapper устарел и не используется в production |
| **`TD-006`** | Инкрементальная синхронизация коммитов репозитория GitHub | `Backend.Infrastructure` | **P2** | `ACCEPTED` | `IMPLEMENTATION_COMPLETE` | `VERIFIED` | **ДА** | [TD-006 REV4](TD-006-Architecture-Investigation-Design-REV4-APPROVED.md) |
| **`TD-007`** | Управление жизненным циклом очереди: retry, backoff, Poison Pill | `Backend.Queue` | **P2** | `ACCEPTED` | `IMPLEMENTATION_COMPLETE` | `VERIFIED` | **ДА** | [TD-007 Plan](TD-007-Implementation-Plan.md) |
| **`TD-008`** | Поддержка хешей SHA-256 (64 hex) и валидация в CreateCommitDto | `Backend.Application` | **P2** | `ACCEPTED` | `IMPLEMENTATION_COMPLETE` | `VERIFIED` | **Нет** | Закрыто в коммите 57da9b8: StringLength(64, 7), Regex hex-валидации, unit и сквозные интеграционные тесты |
| **`TD-009`** | Детектирование паттерна `WithOrigins("*")` в правиле SEC003 | `SecurityAnalyzer` | **P3** | `REJECTED` | `REJECTED` | `NOT APPLICABLE` | **Нет** | Отклонено (DEF-007 признан FALSE POSITIVE: WithOrigins("*") не включает AllowAnyOrigin в ASP.NET Core) |
| **`TD-010`** | Детектирование `ServicePointManager` в правиле SEC005 | `SecurityAnalyzer` | **P3** | `ACCEPTED` | `IMPLEMENTATION_COMPLETE` | `VERIFIED` | **Нет** | Закрыто в коммите 2f24477: обнаружение ServicePointManager.ServerCertificateValidationCallback, подписки (+=), область using-псевдонимов, unit-тесты (72/72) |
| **`TD-011`** | Адаптивная нормализация структуры путей ZIP-архивов | `Backend.Infrastructure` | **P3** | `ACCEPTED` | `IMPLEMENTATION_COMPLETE` | `VERIFIED` | **Нет** | Закрыто в коммите 9f2fda5: адаптивная нормализация ZIP (Single Root Inspection), защита от Zip Slip, Win32/Unicode-коллизии, фильтрация исключений, unit-тесты (1059/1059 в Backend.Tests) |
| **`TD-012`** | Расширение семантических эвристик в `SqlAnalyzer` | `SqlAnalyzer` | **P3** | `DEFERRED` | `DEFERRED` | `DEFERRED` | **Нет** | — |
| **`TD-013`** | Документированный шаблон конфигурации переменных окружения | `Backend.Api` | **P3** | `ACCEPTED` | `IMPLEMENTATION_COMPLETE` | `VERIFIED` | **Нет** | Закрыто в Этапе 6: `.env.example`, отказ от User Secrets |
| **`TD-014`** | Синхронизация базовой документации (`Технический регламент`) | `Documentation` | **P3** | `ACCEPTED` | `IMPLEMENTATION_COMPLETE` | `VERIFIED` | **Нет** | Синхронизировано по ревизии 2068c51 |
| **`TD-015`** | Сквозные интеграционные тесты API (`WebApplicationFactory`) | `Backend.Tests` | **P2** | `ACCEPTED` | `IMPLEMENTATION_COMPLETE` | `VERIFIED` | **Нет** | Закрыто: 45 сквозных тестов в `ApiPipelineIntegrationTests` (включая HTTP 500 via HttpClient) |

> [!WARNING]
> **Задачи, затрагивающие схему базы данных PostgreSQL:**
> В рамках реестра технического долга реализованы две задачи, требующие добавления полей/таблиц и создания миграций EF Core:
> 1. **`TD-007`** — добавление полей `AttemptCount`, `MaxAttempts`, `NextAttemptAt` в таблицу `"Analyses"`, проверочных ограничений `CK_Analyses_AttemptCount`, `CK_Analyses_MaxAttempts` и индекса `IX_Analyses_Status_NextAttemptAt_CreatedAt` (миграция `20261122150614_AddAnalysisRetryAndPoisonPillQueue`).
> 2. **`TD-006`** — добавление флага `IsVerified` и массива хэшей родителей `ParentHashes` (`text[]`) в таблицу `"Commits"`, а также полей отслеживания синхронизации в таблицу `"Projects"` (`LastSyncedAt`, `LastSyncedBranch`, `LastSyncedCommitHash`, `LastSyncedRepositoryHost`, `LastSyncedRepositoryId`) для композитного чекпоинта (миграция `20261124090814_AddCommitProvenanceAndProjectSyncCheckpoint`).
> Все остальные задачи выполняются строго без изменения схемы БД.

---

## 3. Детальные карточки задач

<a id="td-001"></a>
### TD-001: Архитектурная изоляция инспектора EF Core моделей (Roslyn + Контейнер)
- **Связанный дефект:** [DEF-001](../audits/2026-11-06-technical-audit.md#def-001-выполнение-недоверенного-msbuild-кода-и-динамическая-загрузка-сборки)
- **Компонент:** `DatabaseAnalyzer` (`src/Analyzers/Database/`, `src/Analyzers/Database.Runner/`)
- **Статус архитектурного решения:** `ACCEPTED` ([ADR-001](../architecture/database-analyzer-isolation.md), [`TD-001-Stage-2-Architecture.md`](TD-001-Stage-2-Architecture.md))
- **Статус реализации:** `VERIFIED` (Реализованы Roslyn-анализ, автономный Runner, JSON-контракты v1 и Docker-адаптер; все 10 критериев Stage 2 подтверждены тестами)
- **Статус проверки:** `VERIFIED` (проверены 190 тестов `DatabaseAnalyzer.Tests` и 105 тестов `Runner.Tests`, все успешно, exit code 0)
- **Приоритет:** `P1`
- **Миграция БД:** Нет
- **Зависимости:** [ADR-001: Архитектура анализаторов, изоляция анализа БД и отказоустойчивость конвейера](../architecture/database-analyzer-isolation.md)
- **Описание:** Устранить конкретные векторы исполнения недоверенного пользовательского кода в основном процессе сервера при анализе сторонних проектов и исключить прежний механизм накопления невыгружаемых пользовательских сборок в `AssemblyLoadContext.Default`. Отказаться от запуска `dotnet build`, выполнения сборочных инструкций MSBuild и динамической загрузки сборок (`Assembly.LoadFrom` / `AssemblyLoadContext`) над недоверенным кодом в основном процессе сервера. Архитектурное решение утверждено как двухкомпонентное (ADR-001 / TD-001 Stage 2):
  1. **Статический анализатор C# AST на базе Roslyn (`IRoslynMetadataExtractor`):** парсинг исходного кода моделей и контекстов EF Core без предварительной компиляции проектов и без динамической загрузки сторонних сборок на хосте (устранение векторов исполнения кода в основном процессе бэкенда, защита от DoS через ограничения времени и глубины обхода синтаксических деревьев).
  2. **Автономный исполняемый процесс Runner (`NetScope.DatabaseAnalyzer.Runner`) в изолированном Docker-контейнере:** исполнение правил анализа реляционной схемы `DB01`–`DB05` исключительно на основе сериализованных метаданных EF Core, получаемых через входной файл `/exchange/in/input.json` по схеме `database-analyzer-input-v1.schema.json`. Контейнер работает строго без сетевого доступа (`--network none`), **не подключается к СУБД PostgreSQL** (ни к рабочей, ни к пользовательской) и возвращает результаты вычисления правил в `/exchange/out/output.json` по схеме `database-analyzer-output-v1.schema.json`.
  3. **Разграничение ответственности и будущие расширения:** в текущем Stage 2 Runner принимает только JSON с метаданными моделей EF Core и выполняет правила `DB01`–`DB05`. Синтаксический анализ SQL на уровне C# AST выполняет `SqlAnalyzer` (`SQL001`, `SQL002`). Глубокий анализ произвольных DDL-скриптов, сырых SQL-дампов или прямое подключение к тестовой СУБД вынесены в потенциальные будущие расширения и не входят в текущий этап Stage 2.

#### Детальное сопоставление критериев приёмки Stage 2 с фактической реализацией

| № | Критерий приёмки Stage 2 | Фактическая реализация (файлы и методы) | Подтверждающие тесты | Результат проверки |
| :-: | :--- | :--- | :--- | :-: |
| 1 | Исключение вызовов `dotnet build`, `Assembly.LoadFrom` и `AssemblyLoadContext` над пользовательским кодом в основном процессе | `src/Analyzers/Database/DatabaseAnalyzer.cs` (L50-53, L119-123), `src/Analyzers/Database/Roslyn/RoslynMetadataExtractor.cs` | `DatabaseAnalyzerIsolationTests.cs` (`AnalyzeAsync_DoesNotCallDotnetBuildOrAssemblyLoad`), `DatabaseAnalyzerTests.cs` | **PASS** |
| 2 | Извлечение метаданных моделей EF Core напрямую из C# AST без предварительной компиляции проекта | `src/Analyzers/Database/Roslyn/RoslynMetadataExtractor.cs` (`ExtractAsync`), `RoslynModelInspector.cs` (`InspectAsync`) | `RoslynModelInspectorTests.cs`, `RoslynMetadataExtractorTests.cs` | **PASS** |
| 3 | Поддержка строго согласованного набора Fluent API: `HasKey`, `HasIndex`, `IsUnique`, `HasDatabaseName`, `HasFilter`, `HasOne`, `WithMany`, `WithOne`, `HasMany`, `HasForeignKey`, `ToTable` | `src/Analyzers/Database/Roslyn/FluentApiParser.cs` (`ParseEntityConfiguration`, L200-650) | `FluentApiParserTests.cs` (адресные тесты для каждого из 11 методов Fluent API) | **PASS** |
| 4 | Поддержка строго согласованного набора Data Annotations: `[Key]`, `[ForeignKey]`, `[Index]`, `[Table]`, `[Required]` | `src/Analyzers/Database/Roslyn/DataAnnotationParser.cs` (`ParseEntity`, `ParseProperty`) | `DataAnnotationParserTests.cs` (тесты каждого атрибута аннотаций) | **PASS** |
| 5 | Статическая регистрация конфигураций: `OnModelCreating`, отдельные классы `IEntityTypeConfiguration<T>`, вызовы `ApplyConfiguration` / `ApplyConfigurationsFromAssembly` (динамическая рефлексия не поддерживается, формирует `Severity.Warning`) | `src/Analyzers/Database/Roslyn/DbContextSyntaxVisitor.cs`, `src/Analyzers/Database/Roslyn/RoslynModelInspector.cs` (`DiscoverConfigurations`) | `RoslynModelInspectorTests.cs` (`Discovers_EntityTypeConfiguration_Classes`, `Handles_ApplyConfigurationsFromAssembly_Statically`) | **PASS** |
| 6 | Строгое разделение сущностей между несколькими контекстами (`DbContext`) в одном репозитории | `src/Analyzers/Database/Roslyn/RoslynModelInspector.cs` (изоляция по `DiscoveredDbContext`) | `RoslynModelInspectorTests.cs` (`MultipleDbContexts_SeparatesEntitiesStrictly`) | **PASS** |
| 7 | Статический обход локальных вспомогательных методов конфигурации ограничен глубиной 3 с защитой от рекурсивных циклов (формирование `Severity.Warning`) | `src/Analyzers/Database/Roslyn/LocalMethodTraverser.cs` (`TraverseMethod`, лимит `MaxDepth = 3`, стек обнаружения циклов) | `LocalMethodTraverserTests.cs` (`MaxDepth_Exceeded_ProducesWarning`, `RecursiveCycle_Detected_ProducesWarning`) | **PASS** |
| 8 | Строго ограниченный состав конструкций: неподдерживаемые конструкции и рефлексия выставляют `hasIncompleteMetadata = true` и `Severity.Warning` без падения | `src/Analyzers/Database/Roslyn/RoslynModelInspector.cs`, `src/Analyzers/Database/Roslyn/DiscoveredEntity.cs` | `RoslynModelInspectorTests.cs` (`UnsupportedConstruct_SetsIncompleteMetadataFlag_AndEmitsWarning`) | **PASS** |
| 9 | Запуск автономного Runner в Docker-контейнере: offline (`--network none`), без подключения к БД/сети, под `appuser` (10001:10001), таймаут 60 с, read-only root | `src/Analyzers/Database/Runner/DatabaseContainerRunner.cs`, `src/Analyzers/Database/Process/DockerCliProcessAdapter.cs` | См. детальное разделение доказательств Docker ниже: `DockerCliProcessAdapterTests.cs`, `ContainerRunnerSecurityTests.cs`, `DockerContainerIntegrationTests.cs` | **PASS** |
| 10 | Выходной контракт v1 (`database-analyzer-output-v1.schema.json`) с целочисленным `Finding.id`, валидация JSON и маппинг в `AnalyzerFinding` бэкенда | `src/Analyzers/Database.Runner/Program.cs`, `src/Analyzers/Database/Runner/DatabaseContainerRunner.cs` | `DatabaseContainerRunnerContractTests.cs` | **PASS** |

> [!NOTE]
> **Детализация доказательной базы запуска Docker-контейнера (Критерий 9):**
> 1. **Проверка формирования параметров запуска Docker (Unit-тестирование аргументов CLI):** Класс [`DockerCliProcessAdapterTests.cs`](../../tests/DatabaseAnalyzer.Runner.Tests/DockerCliProcessAdapterTests.cs) проверяет сборку аргументов `docker run`: флаги сетевой изоляции `--network none`, непривилегированного пользователя `--user 10001:10001`, режим `--read-only`, RAM-диск `--tmpfs /tmp:rw,noexec,nosuid,size=64m`, лимиты памяти (`--memory 512m`), CPU (`--cpus 1.0`), процессов (`--pids-limit 100`), корректное монтирование томов `/exchange/in` и `/exchange/out`, а также предотвращение инъекций аргументов командной строки.
> 2. **Проверка оркестрации раннера и протокола обмена (Component / Mock Process тестирование):** Класс [`ContainerRunnerSecurityTests.cs`](../../tests/DatabaseAnalyzer.Runner.Tests/ContainerRunnerSecurityTests.cs) проверяет взаимодействие `DatabaseContainerRunner` с адаптером процесса: проверку действующего `IAnalysisLeaseGuard`, изоляцию каталогов обмена `{analysisId}/{token}`, соблюдение 60-секундного таймаута и валидацию возвращаемых JSON-контрактов без вызова реального демона Docker.
> 3. **Проверка фактического запуска реального контейнера (Docker Integration тестирование):** Класс [`DockerContainerIntegrationTests.cs`](../../tests/DatabaseAnalyzer.Runner.Tests/DockerContainerIntegrationTests.cs) содержит 3 интеграционных теста с атрибутом `[DockerFact]`: `DockerContainer_ExecutesSafely_WithIsolationFlags`, `DockerContainer_RunsWithPinnedDigest_Successfully` и `DockerContainer_FullRoslynExtractionAndDatabaseAnalysis_SucceedsWithDB01Finding`. Тесты выполняют реальный запуск Docker-демона с образом `netscope-database-analyzer:latest` (включая запуск по pinned sha256-дайджесту) и подтверждают сквозную передачу метаданных C# AST в контейнер и получение дефекта `DB01`.
> 4. **Ограничение доказательной базы:** Автоматизированные тесты решения проверяют корректность передачи параметров изоляции и факт успешного выполнения контейнера в безопасной конфигурации, однако **не проводят тесты принудительного нарушения ограничений изнутри контейнера во время выполнения** (например, попытки пробить сеть или превысить лимит 512MB RAM для провокации OOM Killer) — соблюдение этих ограничений возложено на ядро Docker на основании проверенных переданных флагов.

> [!NOTE]
> **Анализ требования о «многофайловых конфигурациях вне DbContext»:**
> В соответствии с утверждёнными ADR-001 и критерием 5 Stage 2, конфигурации сущностей в отдельных C# файлах через классы `IEntityTypeConfiguration<T>` **полноценно поддерживаются** статическим анализатором Roslyn (выполняется сканирование синтаксических деревьев всех `.cs` файлов репозитория). При этом динамический поиск конфигураций во внешних бинарных сборках или через runtime-рефлексию прямо запрещён архитектурой изоляции: при обнаружении неразрешимых динамических вызовов формируется диагностическое предупреждение `Severity.Warning`. Никаких несогласованных отклонений или невыполненных требований по конфигурациям вне DbContext нет. Субъективные оценочные проценты (например, «готовность 85–90%») исключены из проектной документации как недостоверные.

- **Критерии приёмки текущего этапа (Stage 2):**
  1. В основном процессе бэкенда исключены конкретные векторы исполнения пользовательского кода: вызовы `dotnet build` сторонних репозиториев и динамическая загрузка сборок (`Assembly.LoadFrom`, `AssemblyLoadContext`).
  2. Roslyn-парсер извлекает метаданные моделей EF Core напрямую из C# AST без предварительной компиляции проекта.
  3. Поддерживается извлечение строго согласованного базового набора Fluent API: `HasKey`, `HasIndex`, `IsUnique`, `HasDatabaseName`, `HasFilter`, `HasOne`, `WithMany`, `WithOne`, `HasMany`, `HasForeignKey`, `ToTable`.
  4. Поддерживается извлечение строго согласованного набора Data Annotations: `[Key]`, `[ForeignKey]`, `[Index]`, `[Table]`, `[Required]`.
  5. Поддерживается статическая регистрация конфигураций через `OnModelCreating`, отдельные классы `IEntityTypeConfiguration<T>` и вызовы `ApplyConfiguration` / `ApplyConfigurationsFromAssembly` только для статически обнаруживаемых типов в синтаксическом дереве (динамическая регистрация через рефлексию не поддерживается; при невозможности достоверного статического анализа формируется `Severity.Warning`).
  6. Обеспечивается строгое разделение сущностей между несколькими контекстами (`DbContext`) в одном репозитории.
  7. Статический обход локальных вспомогательных методов конфигурации строго ограничен глубиной в 3 уровня, выполняется только для статически обнаруживаемых методов в пределах классов конфигурации/DbContext и включает обязательную защиту от рекурсивных циклов (при невозможности достоверного статического анализа формируется `Severity.Warning`).
  8. Состав поддерживаемых конструкций строго ограничен согласованным перечнем; при обнаружении неподдерживаемых конструкций, динамических вызовов или рефлексии формируется флаг неполноты `hasIncompleteMetadata` и диагностическое предупреждение `Severity.Warning`, а не аварийный сбой.
  9. Автономный процесс Runner запускается во внешнем изолированном контейнере: работает строго офлайн (`--network none`), не подключается к СУБД PostgreSQL или сети, анализирует только входной файл обмена `input.json` с метаданными EF Core, исполняется под непривилегированным пользователем `appuser` (10001:10001) с жесткими ограничениями (CPU/RAM/pids лимиты, read-only root, RAM-диск `/tmp`, запрет передачи серверных секретов хоста, timeout 60 с).
  10. Контейнер возвращает результат в виде структурированного JSON по независимому контракту обмена (`database-analyzer-output-v1.schema.json` с целочисленным `Finding.id`), не зависящему от сборок бэкенда. Приём, валидация JSON и преобразование данных в доменные объекты `AnalyzerFinding` выполняются на стороне бэкенда.
- **Необходимые тесты:**
  - Тест парсинга репозиториев со сложными моделями EF Core (Fluent API и Data Annotations) без сборки `.csproj`.
  - Тест обработки репозитория с несколькими классами `DbContext` (отсутствие смешивания сущностей).
  - Тест безопасности: анализ проекта с вредоносными MSBuild-таргетами, статическими конструкторами и попытками рекурсивных циклов в методах конфигурации не приводит к исполнению вредоносного кода или зависанию бэкенда.
  - Проверка отсутствия деградации и утечек памяти процесса при последовательном анализе множества репозиториев.

---

<a id="td-002"></a>
### TD-002: Отказоустойчивость конвейера анализа при частичных сбоях анализатора БД
- **Связанный дефект:** [DEF-002](../audits/2026-11-06-technical-audit.md#def-002-сбой-всего-анализа-коммита-при-отсутствии-idesigntimedbcontextfactory)
- **Компонент:** `DatabaseAnalyzer` (`src/Analyzers/Database/`), `AnalysisService` (`src/Backend/Application/Services/AnalysisService.cs`)
- **Статус архитектурного решения:** `ACCEPTED` ([ADR-001](../architecture/database-analyzer-isolation.md))
- **Статус реализации:** `VERIFIED`
- **Статус проверки:** `VERIFIED` (принято с вердиктом `ACCEPT WITH REMARKS`; 91 тест в `AnalysisServiceQueueTests`, 0 сбоев)
- **Приоритет:** `P1`
- **Миграция БД:** Нет
- **Зависимости:** [ADR-001: Архитектура анализаторов, изоляция анализа БД и отказоустойчивость конвейера](../architecture/database-analyzer-isolation.md)
- **Описание:** Предотвратить сбой всего анализа коммита при невозможности извлечь метаданные базы данных или сбое внешнего контейнера. Обеспечить сохранение результатов остальных анализаторов, разделение информационного пропуска и критических ошибок, понятное диагностическое информирование пользователя, а также строгий контроль жизненного цикла Lease Renewal и гарантий владения задачей.
- **Критерии приёмки:**
  1. Сбой или невозможность построения модели базы данных в `DatabaseAnalyzer` (или ошибка во внешнем контейнере) не прерывает работу других анализаторов (`CodeAnalyzer`, `SqlAnalyzer`, `SecurityAnalyzer`).
  2. Результаты успешно отработавших анализаторов сохраняются в базе данных. Общий статус коммита переходит в `Completed`.
  3. Причина сбоя анализатора БД фиксируется через диагностическое предупреждение `AnalyzerFinding` с уровнем `Severity.Warning` и понятным описанием на русском языке.
  4. Причины сбоев категоризируются:
     - Отсутствие `DbContext` / конфигурации моделей в репозитории (информационный пропуск).
     - Синтаксические или семантические ошибки в C#-коде конфигурации.
     - Использование неподдерживаемых динамических конструкций/рефлексии.
     - Таймаут или сбой процесса внешнего контейнера анализа БД.
  5. Защита от ложных срабатываний: если метаданные EF Core извлечены частично, правила анализа БД (`DB01`–`DB05`) не формируют ложные дефекты по неполным связям; проверки пропускаются либо выполняются только по гарантированно разрешенным сущностям с явной отметкой о частичности модели.
  6. Категорически запрещена фиктивная замена реальных СУБД на SQLite in-memory в качестве универсального решения (ввиду несовместимости диалектов SQL, типов и семантики индексов).
  7. **Контроль Lease Renewal и гарантии владения:**
     - Причина остановки Lease Renewal фиксируется атомарно в потокобезопасном `LeaseRenewalState` (`TrySetTerminalReason`).
     - Штатная остановка (`RequestNormalStop()`) защищена от перезаписи уже установленной терминальной причины (`OwnershipLost`, `RenewalException`, `OwnershipValidationException`, `ExternalCancellation`).
     - После завершения работы анализатора проверяется отмена токена и критическое состояние Lease Renewal; анализатор, проигнорировавший отмену, не может перевести анализ в `Completed`.
     - Ошибки Lease Renewal не маскируются предупреждениями `DatabaseAnalyzer`.
     - При подтверждённой потере владения (`OwnershipLost`, `OwnershipValidationException`) операции `FailAsync`, `CompleteAsync` и `CleanupAsync` строго запрещены.
     - При сбое продления аренды (`RenewalException`) и подтверждённом текущем владении задача помечается `Failed` с сообщением о сбое аренды; очистка каталога выполняется только при подтверждённом владении.
- **Результаты реализации и подтверждение критериев:**
  - В `AnalysisService.cs` реализован безопасный цикл вызова анализаторов с изоляцией исключений `DatabaseAnalyzer` и фиксацией предупреждений `DatabaseAnalyzerRules.InternalAnalyzerError`, `ContainerError`, `ContainerTimeout`, `ExtractionError`.
  - Реализован интерфейс `IDatabaseAnalyzer` с перечислением `DatabaseAnalysisExecutionStatus` (`Success`, `PartialSuccess`, `InformationSkipped`, `ContainerError`, `ContainerTimeout`, `ExtractionError`, `InternalError`), гарантирующий различие информационного пропуска (`MissingDbContext`) от сбоев.
  - Внедрён класс `LeaseRenewalState` с защитой терминальных статусов, предотвращающий переход к `CompleteAsync` при сбоях продления аренды.
  - Реализация подтверждена 91 тестом в `AnalysisServiceQueueTests` (0 сбоев), включая сценарии A–G и 6 специализированных тестов гонок Lease Renewal.

---

<a id="td-003"></a>
### TD-003: Изоляция рабочих каталогов `{Path}/{Id}/{Token}` и безопасная очистка
- **Связанный дефект:** [DEF-003](../audits/2026-11-06-technical-audit.md#def-003-изоляция-рабочих-каталогов-анализа-по-токену-выполнения-и-безопасная-очистка), [API-020](../audits/2026-11-06-technical-audit.md#5-статус-инфраструктурных-замечаний-api-020-api-022-api-024-api-025)
- **Компонент:** `Backend.Infrastructure` (`RepositorySourceService.cs`, `AnalysisService.cs`, `AnalysisBackgroundService.cs`)
- **Статус архитектурного решения:** `ACCEPTED` ([ADR-001](../architecture/database-analyzer-isolation.md))
- **Статус реализации:** `DEFERRED` (дальнейшая реализация отложена; схема путей `{Storage:AnalysisPath}/{AnalysisId}/{ExecutionToken}` поддержана, но механизм фоновой очистки сиротских каталогов и протокол сессионных `pg_advisory_lock` не реализованы)
- **Статус проверки:** `DEFERRED` (проверка отложена вместе с задачей; в `CSharpAnalyzer.Tests.RepositorySourceServiceTests` сохраняются 8 известных сбоев из-за отсутствия `IAnalysisLeaseGuard` в устаревших перегрузках)
- **Статус планирования:** `DEFERRED` (Отложена до отдельного решения)
- **Приоритет:** `P1`
- **Миграция БД:** Нет
- **Зависимости:** [ADR-001: Архитектура анализаторов, изоляция анализа БД и отказоустойчивость конвейера](../architecture/database-analyzer-isolation.md)

> [!IMPORTANT]
> **Решение о планировании (ноябрь 2026 г.):**
> TD-003 остаётся открытой задачей технического долга. На текущем этапе её дальнейшая реализация отложена. Известные проблемы и невыполненные критерии приёмки сохраняются в документации. Возвращение к задаче будет рассмотрено отдельно после принятия соответствующего решения. До этого момента не планировать работы по реализации TD-003 как следующий этап разработки.
>
> Статус `DEFERRED` означает:
> 1. Задача не закрыта и не считается выполненной.
> 2. Известные технические проблемы и архитектурные ограничения сохраняются.
> 3. Текущая частичная реализация не признаётся соответствующей полным требованиям критериев приёмки.
> 4. Дальнейшая работа над задачей временно приостановлена.
> 5. Возвращение к задаче допускается только после отдельного решения о возобновлении работ.

- **Описание:** Исключить гонки в файловой системе между параллельными воркерами при перехвате аренды задач из очереди и вынести пути хранения временных файлов из бинарного каталога приложения.
- **Критерии приёмки:**
  1. Структура пути временного рабочего каталога строго соответствует схеме: `{Storage:AnalysisPath}/{AnalysisId}/{ExecutionToken}`.
  2. Базовый путь настраивается через ключ конфигурации `Storage:AnalysisPath` в `appsettings.json` со значением по умолчанию `Path.Combine(Path.GetTempPath(), "CourseProject", "analyses")`. Размещение рабочих файлов в `AppContext.BaseDirectory` категорически запрещено.
  3. Значение `ExecutionToken` генерируется воркером очереди при взятии задачи в обработку и передается сквозным образом через все уровни конвейера (`RepositorySourceService`, `AnalysisService`, распаковка ZIP, вызовы анализаторов).
  4. Категорически запрещена очистка каталогов по маске или только по `AnalysisId`, чтобы предотвратить удаление файлов нового воркера при перехвате аренды по таймауту.
  5. Немедленная очистка: удаление строго каталога `{Storage:AnalysisPath}/{AnalysisId}/{ExecutionToken}` выполняется в блоке `finally` как при успешном завершении, так и при возникновении исключений.
  6. Фоновая очистка "сиротских" каталогов (`Orphan directory cleanup`): спроектировать, реализовать и протестировать механизм периодической фоновой очистки каталогов, оставшихся после аварийных завершений процессов (OOM, SIGKILL, падение хоста).
     - **Запрет удаления только по TTL:** истечение времени не является достаточным основанием для удаления.
     - **Проверка по паре `(AnalysisId, ExecutionToken)`:** перед удалением фоновый сервис обязан проверить в БД PostgreSQL статус не только `AnalysisId`, но и соответствие конкретного `ExecutionToken`. Каталог разрешено удалять только при подтверждённом отсутствии активного исполнения именно с этим токеном.
     - **Неприкосновенность новых исполнений:** если для того же `AnalysisId` существует новое активное исполнение с другим `ExecutionToken`, его каталог (`{AnalysisId}/{TokenB}`) должен оставаться абсолютно неприкосновенным.
     - **Единый протокол сессионных advisory locks для синхронизации воркеров и очистки:**
       * Рабочий процесс получает `pg_advisory_lock_shared` для `AnalysisId` и удерживает блокировку на протяжении всех файловых операций.
       * После получения `pg_advisory_lock_shared` рабочий процесс обязан повторно проверить в PostgreSQL, что его `ExecutionToken` по-прежнему является актуальным для соответствующего `AnalysisId`. Только после успешной проверки разрешается создавать, распаковывать, читать или изменять файлы рабочего каталога.
       * Если токен больше не актуален или состояние БД невозможно достоверно проверить, воркер обязан освободить блокировку и прекратить выполнение без файловых операций.
       * Очистка получает эксклюзивный `pg_advisory_lock` для того же `AnalysisId`.
       * После получения блокировки очистка повторно проверяет состояние в БД (актуальный `ExecutionToken`) и только затем принимает решение об удалении.
       * Удалять разрешается только директорию, токен которой отличается от актуального токена в БД. Директорию актуального запуска удалять запрещено.
       * При потере соединения с БД рабочий процесс немедленно прекращает файловые операции. Очистка при потере соединения или блокировки прекращает удаление.
       * Блокировка должна удерживаться на протяжении всей соответствующей файловой операции.
       * Если состояние БД или владение блокировкой невозможно достоверно проверить, очиститель обязан отказаться от удаления и повторить проверку позднее.

- **Фактическое состояние реализации и оставшиеся проблемы:**
  - **Изоляция рабочих каталогов:**
    * Схема пути `{Storage:AnalysisPath}/{AnalysisId}/{ExecutionToken}` и ключ `Storage:AnalysisPath` (дефолт `Temp/CourseProject/analyses`) поддержаны в `RepositorySourceService.cs` (`GetExecutionDirectory`) и конфигурации. Размещение в `AppContext.BaseDirectory` исключено в основном конвейере.
    * Сквозная передача `ExecutionToken` поддержана в `AnalysisService.ExecuteAnalysisAsync` и `RepositorySourceService.DownloadAndExtractAsync` при наличии `IAnalysisLeaseGuard`. Однако в `RepositorySourceService.cs` сохраняются устаревшие перегрузки API (без `guard` и без токена), вызов которых прерывается исключением.
    * Защита от пересечения параллельных исполнений обеспечена уникальностью GUID `ExecutionToken`, но механизм синхронизации на уровне СУБД (`pg_advisory_lock_shared` при файловых операциях) не реализован.
  - **Очистка рабочих каталогов:**
    * Очистка в блоке `finally` `AnalysisService.ExecuteAnalysisAsync` вызывает `CleanupAsync(analysis.Id, executionToken, guard)` строго для каталога текущего токена и защищена проверкой `ownershipConfirmed == true`.
    * Защита каталога нового исполнения от старого воркера обеспечена точечным удалением каталога токена.
    * **Фоновая очистка «сиротских» каталогов (`Orphan directory cleanup`) полностью отсутствует:** специализированный фоновый сервис очистки не спроектирован и не написан.
    * Проверка пары `(AnalysisId, ExecutionToken)` в PostgreSQL перед фоновым удалением и единый протокол сессионных advisory locks (`pg_advisory_lock_shared` для воркеров и эксклюзивный `pg_advisory_lock` для очистителя) **не реализованы**.
  - **Тестовое покрытие и известные сбои:**
    * В `tests/CSharpAnalyzer.Tests/RepositorySourceServiceTests.cs` сохраняются **8 известных воспроизводимых сбоев**, вызванных вызовом устаревших методов `DownloadAndExtractAsync` и `CleanupAsync` без предоставления мока `IAnalysisLeaseGuard` (инвариант безопасности TD-003 блокирует файловые операции).
    * При этом в `tests/Backend.Tests/RepositorySourceServiceTests.cs` тесты уже переведены на контракт с `IAnalysisLeaseGuard` и проходят успешно.
    * Тесты взаимодействия воркеров и фонового сервиса очистки (конкурентные блокировки, потеря соединения СУБД) отсутствуют из-за отсутствия самого сервиса.

- **Необходимые тесты (при возобновлении работ):**
  - Тест конкурентной обработки: воркер B выполняет задачу в `{AnalysisId}/{TokenB}`, в то время как воркер A завершает старую аренду и удаляет строго `{AnalysisId}/{TokenA}`, не затрагивая каталог воркера B.
  - Тест гарантии удаления временного каталога при возникновении исключения в конвейере анализа.
  - Тест механизма фоновой очистки «сиротских» каталогов: проверка подтверждения по токену `ExecutionToken` и `AnalysisId` в БД перед удалением; гарантия сохранности каталогов действующих анализов с другим токеном; тест отмены удаления при невозможности взятия блокировки синхронизации.
  - Тест синхронизации воркера и очистителя: пока воркер удерживает разделяемую блокировку и работает с каталогом, очиститель не может получить исключительную блокировку и удалить каталог.
  - Тест успешной очистки: после завершения воркера и освобождения блокировки очиститель получает исключительную блокировку, повторно проверяет ExecutionToken и удаляет только подтверждённый осиротевший каталог.
  - Тест потери соединения PostgreSQL: воркер прекращает операции с каталогом после потери блокировки; очиститель не продолжает удаление при потере своей блокировки.

---

### TD-004: Учет первичных/альтернативных ключей и порядка столбцов в DB04
- **Связанный дефект:** [DEF-004](../audits/2026-11-06-technical-audit.md#def-004-ложные-срабатывания-правила-db04-для-первичных-и-альтернативных-ключей)
- **Компонент:** `DatabaseAnalyzer` (`EfCoreMetadataMapper.cs`, `DatabaseAnalyzer.cs`)
- **Статус:** `VERIFIED` (Верифицировано по результатам архитектурного ревью: подтверждён тестовый запуск от 21.11.2026 — 188/188 тест-кейсов `DatabaseAnalyzer.Tests` [180 тестовых методов], 103/103 тест-кейса `DatabaseAnalyzer.Runner.Tests` [82 тестовых метода], код возврата 0)
- **Приоритет:** `P2`
- **Миграция БД:** Нет
- **Зависимости:** Нет
- **Описание:** Устранить ложные срабатывания правила `DB04` для внешних ключей, поддержанных первичными и альтернативными ключами.
- **Критерии приёмки:**
  1. `EfCoreMetadataMapper` включает свойства всех ключей сущности (первичного и альтернативных) из `entityType.GetKeys()` в список индексов сущности со свойством `IsUnique = true` (отдельный вызов `FindPrimaryKey()` не требуется, так как первичный ключ входит в `GetKeys()`).
  2. Правило `DB04` проверяет, что столбцы внешнего ключа совпадают с префиксом составного индекса в том же порядке следования столбцов.
- **Фактический результат реализации:**
  1. `EfCoreMetadataMapper.cs` обновлен: в коллекцию `Indexes` включаются все ключи из `entityType.GetKeys()` (первичный и альтернативные) со свойством `IsUnique = true`, именем ограничения (`key.GetName()` с fallback `PK_{Table}` / `AK_{Table}_{Props}`) и сохранением порядка столбцов.
  2. Заполняется свойство `AlternateKeys = keys.Where(k => !k.IsPrimaryKey).ToList()` в `DatabaseEntityMetadata`.
  3. В рамках задачи TD-004 добавлено 15 новых тестов в тестовый набор `DatabaseAnalyzer.Tests` (не путать с общим количеством тестов анализатора или всего проекта):
     - 7 адресных unit-тестов маппера в `EfCoreMetadataMapperTests.cs` (PK/AK в `Indexes`, заполнение `AlternateKeys`, сохранение порядка свойств в составных ключах, раздельное представление PK и явных неуникальных/уникальных индексов);
     - 8 регрессионных тестов в `DatabaseAnalyzerTests.cs` для сквозного маппинга и правил DB04/DB05 через `DatabaseRuleEvaluator` (связи 1:1, альтернативные ключи, составные префиксы, обратный порядок столбцов, проверка отсутствия ложных DB05).
- **Необходимые тесты:**
  - Тест модели 1:1 с общим первичным/внешним ключом (отсутствие ложного срабатывания DB04) — реализован и проверен (`DB04_WhenForeignKeyIsAlsoPrimaryKey_InOneToOne_NoDB04Reported`).
  - Тест составного индекса с правильным и неправильным порядком столбцов — реализован и проверен (`DB04_WhenForeignKeyIsCoveredByLeadingPrefixOfCompositeIndex_NoDB04Reported`, `DB04_WhenIndexHasReverseColumnOrder_ProducesDB04Finding`, `DB04_WhenForeignKeyIsNotLeadingPrefixOfIndex_ProducesDB04Finding`).
  - Тесты покрытия FK альтернативным ключом — реализован и проверен (`DB04_WhenForeignKeyIsCoveredByAlternateKey_NoDB04Reported`).
  - Тесты верификации влияния на правило DB05 — реализованы и проверены (`DB05_PrimaryKeyAndExplicitNonUniqueIndex_DoesNotProduceDB05`, `DB05_PrimaryKeyAndExplicitUniqueIndex_ProducesDB05`, `DB05_AlternateKeyAndExplicitUniqueIndex_ProducesDB05`).

---

### TD-005: Поддержка частичных/фильтрованных индексов в правиле DB05
- **Связанный дефект:** [DEF-005](../audits/2026-11-06-technical-audit.md#def-005-ложные-срабатывания-правила-db05-для-частичных-и-фильтрованных-индексов)
- **Компонент:** `DatabaseAnalyzer` (`src/Analyzers/Database/`, `src/Analyzers/Database.Runner/`)
- **Статус архитектурного решения:** `ACCEPTED` ([ADR-001](../architecture/database-analyzer-isolation.md))
- **Статус реализации:** `VERIFIED` (в целевом боевом конвейере Roslyn + Runner; устаревший in-process класс `EfCoreMetadataMapper.cs` не используется в production)
- **Статус проверки:** `VERIFIED` (подтверждено тестом `DatabaseRulesEngineTests.cs:1192` и набором `SqlFilterComparerTests.cs`, 105/105 успешно)
- **Приоритет:** `P2`
- **Миграция БД:** Нет
- **Зависимости:** [TD-001](#td-001)
- **Описание:** Устранить ложные срабатывания правила `DB05` для частичных и фильтрованных индексов.
- **Первоначальное состояние дефекта (DEF-005, 06.11.2026):**
  В исходной монолитной архитектуре до принятия ADR-001 анализ выполнялся через `EfCoreMetadataMapper.cs`. При извлечении индексов из скомпилированной модели EF Core `entityType.GetIndexes()` маппер копировал свойства `Name`, `PropertyNames` и `IsUnique`, но опускал предикат фильтра `index.GetFilter()`. В результате свойство `IndexMetadata.Filter` оставалось `null`, а правило `DB05` ложно рапортовало дубликат для сущностей, где на один и тот же столбец объявлен общий индекс и частичный фильтрованный индекс (например, `WHERE "IsDeleted" = false` для мягкого удаления) либо несколько непересекающихся частичных индексов.
- **Фактическое решение в целевой архитектуре (ADR-001):**
  В рамках архитектурного перехода на двухкомпонентную модель (статика Roslyn + изолированный Runner) поддержка фильтрованных индексов реализована напрямую в целевом конвейере:
  1. `FluentApiParser.cs:597-603` извлекает вызов `.HasFilter(filterVal)` и сохраняет его в `IndexMetadata.Filter`.
  2. `RoslynModelInspector.cs:414` регистрирует гранулярную возможность `"FilteredIndexes"` (`if (ent.Indexes.Any(i => !string.IsNullOrWhiteSpace(i.Filter))) capabilities.Add("FilteredIndexes");`).
  3. `DatabaseContainerRunner.cs:434` передает фильтр в DTO обмена (`Filter = idx.Filter`) и сериализует в `input.json`.
  4. `NetScope.DatabaseAnalyzer.Runner` десериализует `Filter` по контракту `AnalysisInputContract.cs:235-236`.
  5. `DatabaseRulesEngine.cs:528-544` производит сравнение фильтров через консервативный парсер `SqlFilterComparer.AreFiltersEquivalent(idx1.Filter, idx2.Filter, out var isAmbiguous)`. При несовпадении фильтров или наличии фильтра только у одного из индексов правило DB05 не рапортует дубликат (ложное срабатывание исключено). При неоднозначности синтаксиса генерируется диагностика `AMBIGUOUS_SQL_FILTER_SKIPPED`.
  6. **Верификация тестами:** поведение подтверждено тестом `DatabaseRulesEngineTests.cs:1192` (`DB05_DifferentFiltersOnSameColumns_NoFinding`), а также набором тестов токенизатора `SqlFilterComparerTests.cs` (все 105 тестов `DatabaseAnalyzer.Runner.Tests` проходят успешно).
- **Статус устаревшего класса `EfCoreMetadataMapper.cs`:**
  Исторический класс `src/Analyzers/Database/Mapping/EfCoreMetadataMapper.cs` не содержит вызова `index.GetFilter()`. Однако после утверждения ADR-001 компиляция пользовательских проектов и in-process рефлексия выведены из эксплуатации. Класс `EfCoreMetadataMapper` не зарегистрирован в DI-контейнере `Backend.Api`, не вызывается анализатором `DatabaseAnalyzer.AnalyzeAsync` и является устаревшим кодом, сохраняемым исключительно для обратной совместимости старых модульных тестов маппера. Отсутствие `GetFilter()` в `EfCoreMetadataMapper` не является блокером для production-конвейера, поскольку в рабочем конвейере Roslyn + Runner дефект DEF-005 полностью устранён.
- **Критерии приёмки:**
  1. `IndexMetadata` содержит строковое свойство `Filter` (извлекается из `.HasFilter(...)` во Fluent API).
  2. Правило `DB05` признает индексы дубликатами только в том случае, если совпадают столбцы, уникальность и предикаты фильтрации.
- **Подтверждённые проверочные тесты:**
  - Тест сущности с двумя индексами на один столбец: общий индекс и фильтрованный `WHERE "IsDeleted" = 0` (отсутствие срабатывания DB05) — реализован и подтверждён (`DB05_DifferentFiltersOnSameColumns_NoFinding`).

---

### TD-006: Инкрементальная синхронизация коммитов репозитория GitHub
- **Связанный дефект:** [DEF-009 и NEW-001](../audits/2026-11-06-technical-audit.md#def-009-и-new-001-инкрементальная-синхронизация-коммитов-репозитория-github)
- **Компонент:** `Backend.Infrastructure` (`GitHubService.cs`, `GitHubSyncService.cs`, `GitHubSyncCoordinator.cs`)
- **Статус архитектурного решения:** `ACCEPTED` ([TD-006 REV4](TD-006-Architecture-Investigation-Design-REV4-APPROVED.md))
- **Статус реализации:** `IMPLEMENTATION_COMPLETE`
- **Статус проверки:** `VERIFIED` (Верифицировано в коммите `7d5bfbb`: 951/951 тестов `Backend.Tests` успешно, включая 27 тестовых методов `GitHubSyncDagTraversalTests` [полностью покрывающих 25 архитектурных сценариев TR-01 – TR-25] и 4 интеграционных теста с PostgreSQL `PostgreSqlGitHubSyncIntegrationTests`, exit code 0)
- **Приоритет:** `P2`
- **Миграция БД:** **ДА** (`20261124090814_AddCommitProvenanceAndProjectSyncCheckpoint`)
- **Зависимости:** [TD-006 Plan](TD-006-Архитектурное-решение-и-план-разработки-APPROVED.md)
- **Описание:** Инкрементальная синхронизация коммитов репозитория GitHub на базе потокового обхода DAG с доказуемой полнотой истории, композитным чекпоинтом, защитой от гонок на уровне СУБД и транзакционной гарантией целостности связей.
- **Архитектурные основы реализации (Rev 4 Approved):**
  1. *Streaming DAG Traversal:* Алгоритм динамически отслеживает нераскрытые родительские вершины $U = Parents(V_{expanded}) \setminus (V_{fetched} \cup V_{verified})$. Пагинация коммитов от фиксированного HEAD запрашивается по страницам GitHub API. Ранняя остановка пагинации математически строго доказывается условием $U \subseteq V_{verified}$ (все пути замкнуты на ранее верифицированные коммиты).
  2. *Composite Checkpoint & Provenance:*
     - Для предотвращения рассинхронизации при `git push --force` и смене веток в таблицу `"Projects"` добавлены поля отслеживания источника синхронизации: `LastSyncedAt`, `LastSyncedBranch`, `LastSyncedCommitHash`, `LastSyncedRepositoryHost`, `LastSyncedRepositoryId`.
     - При несовпадении верифицированной границы или смене удалённого репозитория выполняется безопасная деградация до глубокой синхронизации графа.
  3. *Хранение графа предков и верификации:* В сущность `Commit` добавлены поле `IsVerified` (`boolean`, default false) и массив родительских хэшей `ParentHashes` (`text[]`, default empty array).
  4. *Конкурентность и транзакционность:*
     - Координация одиночного узла: синглтон `GitHubSyncCoordinator` с in-memory блокировкой по ID проекта предотвращает параллельные запуски синхронизации одного проекта.
     - Блокировка строки на уровне СУБД: `SELECT ... FOR UPDATE` для записи проекта в `AppDbContext` предотвращает параллельные конфликтующие синхронизации в распределённой среде.
     - Атомарное сохранение коммитов: вставка с обработкой SQLSTATE `23505` (duplicate key) через savepoint rollback (Savepoint Transaction Pattern), исключающая падение синхронизации при гонках.
- **Критерии приёмки и результаты реализации:**
  1. **Синхронизация новых коммитов:** Запрос коммитов запрашивает свежие страницы от фиксированного HEAD. Коммиты, появившиеся после предыдущей синхронизации, сохраняются пакетом (`AddRangeAsync`).
  2. **Реализация ранней остановки пагинации:**
     - Остановка выполняется на основе топологического замыкания родительских вершин $U \subseteq V_{verified}$.
     - Исключена зависимость от немонотонных дат авторов (`author.date`) и искусственных лимитов страниц (`maxPages`).
  3. **Обработка существующих коммитов на последующих страницах:** Если коммит уже присутствует в базе данных (проверка по `GetExistingHashesAsync`), он не дублируется при вставке и не вызывает сбоев уникальности `23505` благодаря savepoint-паттерну.
  4. **Обнаружение расхождения истории (Force Push):** Если коммиты удалённого репозитория не находят общего предка с коммитами в БД, система логирует предупреждение о force-push и сохраняет новые коммиты без повреждения существующих связей.
  5. **Тестовая матрица (25 сценариев TR-01 – TR-25):**
     - TR-01: Линейная история коммитов (ранняя остановка на границе чекпоинта);
     - TR-02: Merge-коммит с боковой веткой;
     - TR-03: Множественные слияния и Octopus Merge;
     - TR-04: Немонотонные даты (Clock Skew / Rebase коммиты со старой датой);
     - TR-05: Force Push с полной заменой ветки;
     - TR-06: Force Push с частичным сохранением общего предка;
     - TR-07: Динамический сдвиг страниц GitHub API при поступлении новых коммитов во время синхронизации;
     - TR-08: Пагинация глубокой истории (5+ страниц);
     - TR-09: Обработка Rate Limit и временных сетевых сбоев (RFC 9110);
     - TR-10: Конкурентная синхронизация одного проекта (сериализация через координатор);
     - TR-11: Атомарность сохранения и откат при ошибке;
     - TR-12: Идемпотентность повторной синхронизации без новых коммитов;
     - TR-13 – TR-25: Граничные случаи (пустой репозиторий, однокоммитный репозиторий, несвязанные корни orphan branches, циклические ссылки, пустые деревья, специальные символы в именах веток, повторная верификация границы).
- **Подтверждённые проверочные тесты:**
  - `tests/Backend.Tests/GitHubSyncDagTraversalTests.cs`: 27 тестовых методов (27 тест-кейсов, все успешно), из которых 25 методов (`TR01_...` – `TR25_...`) адресно верифицируют приёмочные сценарии TR-01 – TR-25 матрицы утверждённой архитектуры REV4, а 2 метода проверяют вспомогательные механизмы (откат savepoint при конфликте 23505 и контракт ответа при `AlreadyCurrent`);
  - `tests/Backend.Tests/Integration/PostgreSqlGitHubSyncIntegrationTests.cs`: 4 интеграционных теста с PostgreSQL успешно (`FOR UPDATE` locking, `ParentHashes` / `IsVerified` persistence, savepoint 23505 rollback, checkpoint update);
  - `tests/Backend.Tests/GitHubSyncServiceTests.cs`: 32 модульных теста сервиса синхронизации;
  - Общий запуск набора `Backend.Tests`: **951 тест успешно (0 сбоев, exit code 0)**.

---

### TD-007: Управление жизненным циклом очереди: retry, backoff и Poison Pill
- **Связанный дефект:** [NEW-002 и NEW-003](../audits/2026-11-06-technical-audit.md#new-002-и-new-003-управление-жизненным-циклом-очереди-retry-политика-и-защита-от-poison-pill)
- **Компонент:** `Backend.Queue` (`Analysis.cs`, `AnalysisService.cs`, `AnalysisRepository.cs`, `AnalysisRetryPolicy.cs`, `GitHubRateLimitParser.cs`)
- **Статус архитектурного решения:** `ACCEPTED`
- **Статус реализации:** `IMPLEMENTATION_COMPLETE` (Все замечания ревью Fix 1 – Fix 5.3 устранены, задача полностью верифицирована и принята)
- **Статус проверки:** `VERIFIED` (Верифицировано в коммите `852f82e2e95230c8f1fe231f4b0bdbc2c12b7656`: 1302 теста решения, 10 сценариев PostgreSqlCommitAckLossIntegrationTests, включая полноценный Scenario I, 21 тест операций очереди, 11 тестов повторов и воркера)
- **Приоритет:** `P2`
- **Миграция БД:** **ДА** (`20261122150614_AddAnalysisRetryAndPoisonPillQueue`)
- **Зависимости:** TD-002
- **Описание:** Внедрена долговечная очередь задач с экспоненциальным backoff, обработкой HTTP rate limit (RFC 9110), строгим контролем владения задачей при завершении (Q-03, Q-03-A), восстановлением после потери подтверждения фиксации коммита в СУБД (Q-04, Q-04-A, Q-04-B, Fix5.1, Fix5.2, Fix5.3) и защитой от Poison Pill.
- **Критерии приёмки и результаты реализации:**
  1. В сущность `Analysis` добавлены поля `AttemptCount` (int, default 0), `MaxAttempts` (int, default 3) и `NextAttemptAt` (DateTime?). В конфигурации `AnalysisConfiguration` настроены check constraints `CK_Analyses_AttemptCount` (`"AttemptCount" >= 0`) и `CK_Analyses_MaxAttempts` (`"MaxAttempts" > 0`), а также составной индекс `IX_Analyses_Status_NextAttemptAt_CreatedAt`.
  2. Создана и верифицирована миграция `20261122150614_AddAnalysisRetryAndPoisonPillQueue` и снимок модели `AppDbContextModelSnapshot`.
  3. Публичный контракт `AnalysisDto` сохранён в неизменном виде без утечки внутренних полей очереди.
  4. Реализован парсер `GitHubRateLimitParser` в соответствии со стандартом RFC 9110 (IMF-fixdate, RFC 850, asctime, delta-seconds, дельта серверных часов `Date`, безопасный allow-list `HeaderDiagnostics` без утечки токенов).
  5. Реализована политика `AnalysisRetryPolicy` (`IAnalysisRetryPolicy`) с экспоненциальной задержкой, джиттером, классификацией временных/постоянных ошибок, обработкой SQLSTATE (`08000`, `08003`, `08006`, `57P01`, `40001`, `40P01`) и ограничением ожидания (cap 15 мин).
  6. В `AnalysisRepository` внедрён пропуск заблокированных кандидатов (`skippedCandidateIds` через строго типизированный массив `NpgsqlDbType.Array | NpgsqlDbType.Integer`), серверное время `clock_timestamp()`, обработка `NextAttemptAt`, метод `ScheduleRetryAsync` и атомарный перевод зависших задач в `Failed` (Poison Pill).
  7. **Q-03 и Q-03-A (Строгий контроль владения при завершении и недопустимость NULL lease):** Все перегрузки без guard в `IAnalysisRepository` устранены; `IAnalysisLeaseGuard guard` является строго обязательным параметром для `CompleteAsync`, `FailAsync`, `ScheduleRetryAsync`. В PostgreSQL защищённые операции записи выполняются непосредственно через физическое соединение guard (`guard.Connection`), удерживающее session-level advisory lock (`pid = pg_backend_pid()`), что математически исключает race condition TOCTOU при потере владения. Все предикаты операций записи отклоняют как истёкшие, так и `NULL` значения `LeaseExpiration` (`"LeaseExpiration" >= clock_timestamp()`). Контракт `GetAnalysisWithProcessIdAsync` в `IAnalysisRepository` сделан обязательным методом интерфейса (F51-04), а XML-документация уточнена для Npgsql (чтение сущности и PID на одном физическом соединении для диагностики PostgreSQL backend; F52-02).
  8. **Q-04, Q-04-A, Q-04-B (Ambiguous COMMIT ACK Loss Recovery, Wire Protocol Parser, Fix5.1, Fix5.2, Fix5.3):** `AnalysisService` проверяет фактическое состояние задачи в базе данных через `GetAnalysisWithProcessIdAsync` (гарантия чтения PID и состояния из одного физического соединения, независимого от guard) при сбоях сети во время `CompleteAsync`. Вызов проверки изолирован в блоке `try/catch (checkEx)`: при откате транзакции (`Status == Running`) исходное исключение `commitEx` ретранслируется во внешний обработчик retry/fail (F51-02). Выполняется строгая проверка согласованности результатов `AreResultsConsistent` (совпадение количества находок, полей `Rule`, `Message`, `Severity`, `Recommendation`, `FilePath`, `LineNumber`, типа анализатора, устойчивость к порядку и проверка мультимножества; F51-05). Чужой `ExecutionToken` отклоняется. При неоднозначном коммите (`isAmbiguousCommit = true`) или сбое проверки статуса (`checkEx`) автоматические изменения состояния (`FailAsync`, `ScheduleRetryAsync`) и очистка каталога (`CleanupAsync`) строго блокируются, исключение логируется через `AggregateException` (F51-03). Протокольный парсер `PostgreSqlProtocolParser` валидирует структуру сообщений `Query`, `Parse` и `CommandComplete`, предотвращает переполнение длины и относит к выполнению коммита исключительно команду `COMMIT` в протоколе простых запросов `'Q'` (Option A, F51-01), так как `'P'` лишь подготавливает оператор без его выполнения. Реализованы сценарии: A (реальный сетевой RST после коммита), B (обрыв до коммита), C (сбой проверки БД при recovery), D (повреждённые/неполные результаты), E (чужой execution token), F (идемпотентность повторного восстановления), G (программное исключение в post-commit callback EF Core), H (сквозной жизненный цикл: завершённый коммит не захватывается повторно в следующем цикле воркера; F52-01), I (полноценный тест Fix5.3: реальный сетевой обрыв сокета ДО передачи COMMIT серверу через `proxy.DropOnClientCommit = true`, откат транзакции в СУБД, сохранение рабочего каталога, безопасный повторный захват после истечения `LeaseExpiration` с новым `ExecutionToken` и инкрементом `AttemptCount` с 1 до 2; F53-01; кодировка патча UTF-8 без BOM проверена; F53-02) и штатный режим.
  9. Полный набор тестов решения: **907 тестов `Backend.Tests` пройдено успешно (0 сбоев)**, включая 40 тестов `PostgreSqlProtocolParserTests`, 10 тестов `PostgreSqlCommitAckLossIntegrationTests` (включая сценарии H и I), 21 тест `PostgreSqlQueueOperationsTests`, 7 тестов `AnalysisServiceRetryQueueTests`, 4 теста `AnalysisBackgroundServiceTests`, 19 тестов `AnalysisResultsConsistencyTests`, 13 тестов `GitHubRateLimitParserTests`, 7 тестов `AnalysisRetryPolicyTests`. Всего по активным тестовым проектам решения выполнено **1302 теста (0 сбоев)**: `Backend.Tests` (907), `DatabaseAnalyzer.Runner.Tests` (105), `DatabaseAnalyzer.Tests` (190), `SqlAnalyzer.Tests` (44), `SecurityAnalyzer.Tests` (56). Тесты `CSharpAnalyzer.Tests` (8 тестов) не затрагивались в соответствии со статусом `DEFERRED` задачи TD-003.
  10. **Связанный коммит фиксации:** `852f82e2e95230c8f1fe231f4b0bdbc2c12b7656` (`fix: finalize retry queue and commit recovery`).

---

### TD-008: Поддержка хешей SHA-256 и строгая hex-валидация в CreateCommitDto
- **Связанный дефект:** [DEF-006](../audits/2026-11-06-technical-audit.md#def-006-ограничение-длины-хеша-коммита-в-createcommitdto-sha-1-vs-sha-256)
- **Компонент:** `Backend.Application` (`CreateCommitDto.cs`)
- **Статус:** `VERIFIED` (Верифицировано в коммите `57da9b8`: поддержка длины 7–64, строгая hex-валидация, покрытие unit- и сквозными интеграционными тестами)
- **Приоритет:** `P2`
- **Миграция БД:** Нет (поле `CommitHash` в PostgreSQL имеет тип `text` и не накладывает ограничений)
- **Зависимости:** Нет
- **Описание:** Разрешить ручное добавление коммитов с SHA-256 хешами и гарантировать валидность формата.
- **Критерии приёмки:**
  1. `CommitHash` допускает длину от 7 до 64 символов.
  2. Добавлен Regex-атрибут `^[0-9a-fA-F]{7,64}$` (`\A[0-9a-fA-F]{7,64}\z`).
  3. Невалидные строки (содержащие пробелы, спецсимволы или буквы вне hex) отклоняются с кодом 400 ProblemDetails.
- **Фактический результат реализации:**
  1. В `src/Backend/Application/DTOs/CreateCommitDto.cs` обновлены атрибуты валидации:
     - `[StringLength(64, MinimumLength = 7, ErrorMessage = "Хэш коммита должен содержать от 7 до 64 символов.")]`
     - `[RegularExpression(@"\A[0-9a-fA-F]{7,64}\z", ErrorMessage = "Хэш коммита должен содержать только шестнадцатеричные символы.")]`
  2. В базе данных PostgreSQL столбец `"CommitHash"` имеет тип `text` (`CommitConfiguration.cs`), что позволяет сохранять хэши SHA-256 без изменения схемы и миграций.
  3. Реализация полностью подтверждена автоматическими тестами:
     - Unit-тесты валидации DTO в `tests/Backend.Tests/DtoValidationAndAnalysisErrorTests.cs`:
       * `CreateCommitDto_ValidHashes_PassValidation` (5 сценариев: SHA-1 40 hex, SHA-256 64 hex, минимальный 7 hex, смешанный регистр 40 hex, смешанный регистр 64 hex);
       * `CreateCommitDto_InvalidHashes_FailValidation` (11 сценариев: `null`, пустая строка, длина 6, длина 65, пробелы в начале/конце/середине, переводы строк `\n`/`\r\n`, спецсимволы `-`, `#`).
     - Сквозные интеграционные тесты конвейера в `tests/Backend.Tests/Integration/ApiPipelineIntegrationTests.cs`:
       * `CreateCommit_WithSha256Hash_SucceedsAndPersistsInDatabase` (успешное создание коммита с SHA-256 через `HttpClient`, возврат `201 Created` и проверка сохранения в PostgreSQL);
       * `CreateCommit_WithInvalidHash_Returns400BadRequest_WithValidationProblemDetails` (отклонение невалидных хэшей с возвратом HTTP 400, формата RFC 9457 ValidationProblemDetails, `code: "VALIDATION_FAILED"`, `traceId` и структуры `errors`).
- **Связанные коммиты:**
  - `57da9b8 fix(api): support SHA-256 commit hashes`

---

### TD-009: Детектирование паттерна WithOrigins("*") в правиле SEC003
- **Связанный дефект:** [DEF-007](../audits/2026-11-06-technical-audit.md#def-007-пропуск-небезопасной-конфигурации-cors-через-withorigins-в-sec003)
- **Компонент:** `SecurityAnalyzer` (`Sec003InsecureCorsRule.cs`)
- **Статус архитектурного решения:** `REJECTED`
- **Статус реализации:** `REJECTED`
- **Статус проверки:** `NOT APPLICABLE`
- **Приоритет:** `P3`
- **Миграция БД:** Нет
- **Зависимости:** Нет
- **Описание:** Первоначально предполагалось выявлять вызовы `.WithOrigins("*")` при анализе политик CORS как потенциально небезопасные.
- **Техническая причина отклонения задачи:**
  1. В архитектуре CORS ASP.NET Core метод `CorsPolicyBuilder.WithOrigins("*")` добавляет литеральную строку `"*"` как конкретный разрешённый адрес в коллекцию `Origins`, а не переключает политику в режим `AllowAnyOrigin`.
  2. В runtime промежуточный слой CORS сопоставляет входящий заголовок `Origin` по точному совпадению с элементами `Origins`. Значение `Origin: https://evil.com` не совпадает с `"*"` и запрос отклоняется.
  3. Следовательно, `WithOrigins("*")` не является эквивалентом `AllowAnyOrigin()` и не открывает доступ произвольным источникам.
  4. Добавление такой проверки в правило `SEC003` приводило бы к ложноположительным срабатываниям.
  5. Кодовые изменения не требуются. Поведение правила `Sec003InsecureCorsRule` остаётся корректным для опасных конструкций (`AllowAnyOrigin()` и `SetIsOriginAllowed(_ => true)`).

---

### TD-010: Детектирование ServicePointManager в правиле SEC005
- **Связанный дефект:** [DEF-008](../audits/2026-11-06-technical-audit.md#def-008-игнорирование-servicepointmanager-servercertificatevalidationcallback-в-sec005)
- **Компонент:** `SecurityAnalyzer` (`Sec005DisabledCertificateValidationRule.cs`)
- **Статус архитектурного решения:** `ACCEPTED`
- **Статус реализации:** `VERIFIED`
- **Статус проверки:** `VERIFIED`
- **Приоритет:** `P3`
- **Миграция БД:** Нет
- **Зависимости:** Нет
- **Описание:** Выявлять глобальное отключение валидации SSL-сертификатов через классический API .NET `ServicePointManager.ServerCertificateValidationCallback`.
- **Критерии приёмки:**
  1. Правило `SEC005` фиксирует присваивание свойству `ServicePointManager.ServerCertificateValidationCallback` лямбды или метода, возвращающего `true`.
- **Фактически реализованное поведение:**
  1. Обнаружение простого присваивания (`=`) свойству `ServerCertificateValidationCallback`.
  2. Обнаружение подписки через оператор добавления обработчика (`+=`). Оператор отписки (`-=`) безопасно игнорируется.
  3. Анализ лямбда-выражений с безусловным возвратом `true`: поддерживаются как лямбда-выражения вида `=> true`, так и блочные лямбды `{ return true; }` при отсутствии операторов ветвления (`if`, `switch`, `try`, тернарный оператор). Условные проверки (например, по `SslPolicyErrors.None`) и возврат `false` не считаются нарушением.
  4. Распознавание имени типа: поддерживаются неквалифицированное имя `ServicePointManager`, полное имя `System.Net.ServicePointManager` и глобальное имя `global::System.Net.ServicePointManager`.
  5. Учёт области видимости `using` и alias-директив: лексический обход предков `assignment.Ancestors()` от ближайшего `BaseNamespaceDeclarationSyntax` к внешним пространствам имён и `CompilationUnitSyntax`. Неквалифицированное имя разрешается только в том случае, если псевдоним `ServicePointManager` отсутствует либо явно указывает на системный тип (`System.Net.ServicePointManager` / `global::System.Net.ServicePointManager`). Псевдонимы на сторонние типы (например, `using ServicePointManager = MyNamespace.ServicePointManager;`) и сторонние типы (`MyNamespace.ServicePointManager`, `CustomTypes.ServicePointManager`, `Other.ServicePointManager`) отклоняются без ложных срабатываний. Псевдонимы изолированы в пределах своих `namespace`.
  6. Сохранение существующих проверок обработчиков `HttpClientHandler` и `SocketsHttpHandler` (`ServerCertificateCustomValidationCallback` и `DangerousAcceptAnyServerCertificateValidator`) без регрессий.
- **Ограничения реализации:**
  Анализатор остаётся синтаксическим и не использует `SemanticModel`. Поэтому невозможно гарантированно отличить все случаи использования одноимённого пользовательского типа `ServicePointManager` от системного типа .NET при отсутствии квалификатора пространства имён или при объявлении собственного класса `ServicePointManager` в неквалифицированной области видимости. Это известное ограничение принятой реализации, а не основание для повторного открытия задачи.
- **Подтверждающие тесты:**
  В проекте `tests/SecurityAnalyzer.Tests` добавлены 16 тестов в тестовый класс [`Sec005DisabledCertificateValidationRuleTests.cs`](../../tests/SecurityAnalyzer.Tests/Rules/Sec005DisabledCertificateValidationRuleTests.cs). Все **72 теста `SecurityAnalyzer.Tests` выполнены успешно (0 сбоев)**.
- **Связанные коммиты:**
  - `2f2447792aad063ab39152d1d3609c8835f0524c` (`fix(security): detect ServicePointManager certificate validation bypass`)

---

### TD-011: Адаптивная нормализация структуры путей ZIP-архивов
- **Связанный дефект:** [NEW-004](../audits/2026-11-06-technical-audit.md#new-004-алгоритм-определения-корневого-каталога-zip-архива)
- **Компонент:** `Backend.Infrastructure` (`RepositorySourceService.cs`)
- **Статус архитектурного решения:** `ACCEPTED`
- **Статус реализации:** `VERIFIED`
- **Статус проверки:** `VERIFIED`
- **Приоритет:** `P3`
- **Миграция БД:** Нет
- **Зависимости:** Нет
- **Описание:** Обеспечить корректную адаптивную распаковку ZIP-архивов различной структуры (архивы GitHub с вложенной папкой, плоские архивы, архивы с несколькими корневыми папками) с сохранением полной безопасности и защитой от Zip Slip.
- **Критерии приёмки:**
  1. Перед извлечением выполняется инспекция записей архива: если все извлекаемые файлы имеют общий корневой каталог, первый сегмент отбрасывается (`Skip(1)`), иначе файлы извлекаются напрямую от корня.
- **Фактическая архитектура реализации (Single Root Inspection):**
  1. **Инспекция записей ZIP-архива до извлечения:** анализ метаданных центрального каталога `archive.Entries` в памяти до каких-либо файловых операций на диске.
  2. **Проверка исходных имён путей до нормализации (`ValidateRawArchivePath`):** проверка исходной строки имени записи до любых манипуляций со строками.
  3. **Отклонение опасных путей:** немедленное отклонение записей с пустыми именами, байтом `\0`, абсолютными путями Unix (`/`) и Windows (`\`, `Path.IsPathRooted`), UNC-путями (`\\server\share`), префиксами дисков (`C:`), а также записей, содержащих хотя бы один сегмент `..` на любой глубине (до нормализации, исключая возможность скрыть traversal через отбрасывание первого сегмента).
  4. **Проверка Windows-совместимости сегментов (`ValidateNormalizedSegments`):** запрет альтернативных потоков данных NTFS (ADS через символ `:`), запрет недопустимых символов Win32 (`<`, `>`, `"`, `|`, `?`, `*`) и управляющих символов (`< 0x20`), запрет завершающих точек `.` и пробелов `' '` в сегментах, запрет зарезервированных имён устройств Windows (`CON`, `PRN`, `AUX`, `NUL`, `COM1`–`COM9`, `LPT1`–`LPT9`, `COM¹`–`COM³`, `LPT¹`–`LPT³`), включая очистку пробелов перед расширением (например, `CON .txt`, `NUL .log`).
  5. **Определение общего корневого каталога только по подходящим записям (`HasSingleRootDirectory`):** строгий анализ первого сегмента по `StringComparison.Ordinal`; если в архиве есть хотя бы один файл верхнего уровня (1 сегмент), единый корень признаётся отсутствующим; если все непустые записи имеют совпадающий первый сегмент, он отбрасывается.
  6. **Исключение символических ссылок и служебных директорий из определения общего корня:** символические ссылки Unix (`0xA000`) проверяются на безопасность пути, но не участвуют в определении общего корня и не извлекаются на диск; исключённые записи (`ShouldExclude`, такие как `.git`, `bin`, `obj`) не влияют на определение корня полезных файлов репозитория.
  7. **Сохранение корня, если он сам исключён правилами фильтрации:** если общий первый сегмент совпадает с `ExcludedDirectories` (например, архив, содержащий только `.git/config`), корень не снимается, что гарантирует полную фильтрацию служебного содержимого без утечки файлов на диск.
  8. **Корректная классификация записей с завершающим сегментом `.`:** записи вида `root/.` классифицируются как каталоги, исключая ошибочное создание файла с именем `root`.
  9. **Разделение исходного пути и канонического ключа сравнения:** раздельное хранение `OriginalRelativePath` (с сохранением исходного регистра и формы Unicode для физической записи) и `CanonicalRelativePath` (NFC-ключ для детекции коллизий).
  10. **Unicode NFC-нормализация для проверки коллизий без изменения исходного имени:** канонические ключи формируются через `Normalize(NormalizationForm.FormC)` с обработкой некорректных UTF-16 последовательностей; Unicode-имена (включая кириллицу) полностью поддерживаются.
  11. **Полная предварительная проверка коллизий (`ValidateNoPathCollisions`):** двухпроходная валидация плана до создания файлов и каталогов на диске, независимая от порядка записей в ZIP. Проход А регистрирует каталоги и отклоняет конфликты регистра или формы Unicode (например, `Src/` и `src/`, `É/` и `É/`), допуская точные повторы одного каталога. Проход Б отклоняет дубликаты файлов, регистровые коллизии (`App.cs` / `app.cs`), Unicode-коллизии и конфликты «файл / каталог» (`src` и `src/App.cs`).
  12. **Повторная проверка безопасного пути назначения непосредственно перед извлечением (`GetSafeDestinationPath`):** обязательная финальная проверка на непревышение границ целевого каталога перед каждым вызовом `Directory.CreateDirectory` и `FileStream`.
  13. **Сохранение существующих ограничений и надёжности:** сохранены лимиты `MaxArchiveSizeBytes` (50 МиБ), `MaxSingleFileSizeBytes` (50 МиБ), `MaxTotalExtractedSizeBytes` (250 МиБ), `MaxFileCount` (10 000), потоковое чтение, контроль владения `IAnalysisLeaseGuard`, поддержка `CancellationToken` и токенизированная очистка `CleanupAsync` при сбоях.
- **Подтверждающие тесты:**
  В тестовый класс [`RepositoryArchiveSecurityTests.cs`](../../tests/Backend.Tests/RepositoryArchiveSecurityTests.cs) добавлены 26 тестовых сценариев (включая плоские архивы, несколько корней, один файл, пустой архив, Zip Slip, абсолютные пути, NTFS ADS, устройства Win32, пробелы перед расширением, завершающие точки/пробелы, дубликаты, case-коллизии, Unicode NFC/NFD коллизии, конфликты файл/каталог, исключённые каталоги и симлинки).
  Все **1059 тестов проекта `Backend.Tests` выполнены успешно (0 сбоев)**. *(Примечание: успешный прогон `Backend.Tests` не означает полного зелёного статуса всего solution, так как в `CSharpAnalyzer.Tests` остаются 8 предсуществующих сбоев, связанных с отложенной задачей TD-003).*
- **Связанные коммиты:**
  - `9f2fda5fd74598e49fdf47a706c1f4c1172a732a` (`fix(github): correct excluded entries in archive root detection`)

---

### TD-012: Расширение семантических эвристик в SqlAnalyzer
- **Связанный дефект:** [DEF-010](../audits/2026-11-06-technical-audit.md#def-010-ограничения-синтаксического-подхода-в-sqlanalyzer)
- **Компонент:** `SqlAnalyzer`
- **Статус архитектурного решения:** `DEFERRED`
- **Статус реализации:** `DEFERRED`
- **Статус проверки:** `DEFERRED`
- **Приоритет:** `P3`
- **Миграция БД:** Нет
- **Зависимости:** Нет
- **Описание:** Добавление контекстных проверок типа вызывающего объекта для снижения ложных срабатываний в пользовательских методах.

---

### TD-013: Документированный шаблон конфигурации переменных окружения
- **Связанный дефект:** [API-022](../audits/2026-11-06-technical-audit.md#5-статус-инфраструктурных-замечаний-api-020-api-022-api-024-api-025)
- **Компонент:** `Backend.Api`
- **Статус:** `VERIFIED` (Закрыто и верифицировано: шаблон `.env.example`, отказ от User Secrets, конфигурация через переменные окружения)
- **Приоритет:** `P3`
- **Миграция БД:** Нет
- **Зависимости:** Нет
- **Описание:** Перевести конфигурацию на переменные окружения и создать файл `.env.example` с документированными плейсхолдерами для развертывания в Docker Compose без рабочих секретов.
- **Доказательная база верификации:**
  1. **Файлы:** В корне репозитория присутствует задокументированный шаблон [`.env.example`](../../.env.example) с безопасными плейсхолдерами для PostgreSQL (`POSTGRES_DB`, `POSTGRES_USER`, `POSTGRES_PASSWORD`, `POSTGRES_PORT=5433`), JWT (`JWT_KEY`, `JWT_ISSUER`, `JWT_AUDIENCE`), симметричного шифрования токенов (`TOKEN_ENCRYPTION_KEY`, 256-бит Base64), Swagger (`SWAGGER_ENABLED`) и CORS (`CORS_ALLOWED_ORIGINS`).
  2. **Код загрузки конфигурации:** [`src/Backend/Api/Program.cs`](../../src/Backend/Api/Program.cs) считывает параметры через стандартный механизм `IConfiguration` с поддержкой переопределения через переменные окружения, исключая зависимость от User Secrets в продакшене.
  3. **История коммитов:** Зафиксировано коммитами `7d5e817` (*chore: align project configuration example*) и `757e0ba` (*feat(deployment): add Docker deployment and database initialization*).

---

### TD-014: Синхронизация базовой документации
- **Связанный дефект:** [API-024](../audits/2026-11-06-technical-audit.md#5-статус-инфраструктурных-замечаний-api-020-api-022-api-024-api-025)
- **Компонент:** `Documentation` (`Технический регламент.md`, `Исполнительное резюме.md`, `Архитектура проекта.drawio`)
- **Статус:** `VERIFIED` (Верифицировано по актуальному состоянию кодовой базы)
- **Приоритет:** `P3`
- **Миграция БД:** Нет
- **Зависимости:** Нет
- **Описание:** Устранить противоречия в документах относительно статуса модулей `SqlAnalyzer`, `SecurityAnalyzer`, очереди задач PostgreSQL и схемы базы данных.
- **Доказательная база верификации:**
  1. **Файлы:** Документы [`docs/Технический регламент.md`](../Технический%20регламент.md) и [`docs/Исполнительное резюме.md`](../Исполнительное%20резюме.md) актуализированы: зафиксирована асинхронная очередь в PostgreSQL (`202 Accepted`), фоновая обработка через `AnalysisBackgroundService`, фактическая схема таблиц `"Users"` (поля GitHub PAT) и `"Analyses"`, статус `SqlAnalyzer` (44 теста) и `SecurityAnalyzer` (56 тестов), завершение TD-002 и перенос TD-003 в deferred.
  2. **Архитектурные схемы:** В диаграмме [`docs/Архитектура проекта.drawio`](../Архитектура%20проекта.drawio) блоки анализаторов и фонового сервиса переведены в статус «Реализовано».
  3. **История коммитов:** Синхронизация зафиксирована коммитами `5431edc` (*chore: align project documentation*), `58e0601` (*docs: finalize database analyzer isolation decisions*), `111f4d5` (*docs: document completed lease renewal and fault tolerance work*), `d615428` (*docs: defer TD-003 implementation*).

---

### TD-015: Сквозные интеграционные тесты API на базе WebApplicationFactory
- **Связанный раздел:** [Раздел 3 test-suite-evaluation.md](../audits/test-suite-evaluation.md#3-анализ-реализации-сквозных-интеграционных-тестов-webapplicationfactoryprogram-td-015)
- **Компонент:** `Backend.Tests`
- **Статус:** `VERIFIED` (Архитектурное решение `ACCEPTED`, реализация `VERIFIED`, проверка `VERIFIED`)
- **Приоритет:** `P2`
- **Миграция БД:** Нет
- **Зависимости:** Нет
- **Описание:** Создать набор интеграционных тестов с реальным HTTP-конвейером ASP.NET Core (`WebApplicationFactory<Program>`).
- **Фактическое состояние реализации:**
  1. Реализована тестовая инфраструктура [`ApiWebApplicationFactory.cs`](../../tests/Backend.Tests/Integration/ApiWebApplicationFactory.cs) и полный набор сквозных тестов [`ApiPipelineIntegrationTests.cs`](../../tests/Backend.Tests/Integration/ApiPipelineIntegrationTests.cs) (45 тестов), взаимодействующих с реальным PostgreSQL через [`PostgreSqlFixture.cs`](../../tests/Backend.Tests/Integration/PostgreSqlFixture.cs).
  2. **Реализованные и проверенные критерии:**
     - **Rate Limiting:** тест `RateLimiting_BurstRequests_Returns429TooManyRequests_WithRetryAfterAndProblemDetails` проверяет лимит 10 токенов, отсечку 11-го запроса кодом 429, заголовок `Retry-After` и формат ProblemDetails (`RATE_LIMIT_EXCEEDED`).
     - **CORS Preflight:** тесты `CorsPreflight_AllowedOrigin5173_ReturnsAccessControlHeaders`, `CorsPreflight_AllowedOrigin3000_ReturnsAccessControlHeaders`, `CorsPreflight_UntrustedOrigin_DoesNotReturnAllowOriginHeader` проверяют реальные `OPTIONS`-запросы к конвейеру.
     - **JWT Bearer:** тесты проверяют анонимные запросы (401), поврежденные токены (401), токены с неверной подписью (401), истекшие токены (401) и успешную аутентификацию по валидному токену (200).
     - **Авторизация и IDOR:** проверка изоляции данных между пользователями и администратором (403 Forbidden).
     - **Жизненные циклы сущностей:** сквозные тесты проектов, коммитов, GitHub PAT и очереди задач анализа на реальном PostgreSQL.
     - **Обработка необработанных исключений (HTTP 500):** сквозной тест `UnhandledException_InEndpointExecution_Returns500ProblemDetails_WithMaskedDetails_AndTraceId` проверяет перехват непредвиденного сбоя в конвейере через `HttpClient`, возврат RFC 9457 ProblemDetails и строгое сокрытие внутренних сведений.
- **Результат верификации:**
  1. Использована инфраструктура `WebApplicationFactory<Program>` и реальный `HttpClient`.
  2. Подтверждена работа реального HTTP-конвейера ASP.NET Core: прохождение запроса через middleware (`UseForwardedHeaders`, `UseExceptionHandler`, `UseStatusCodePages`, `UseCors`, `UseAuthentication`, `UseAuthorization`, `UseRateLimiter`), маршрутизацию и контроллер.
  3. Проверено возникновение необработанного исключения на защищенном маршруте синхронизации GitHub (`POST /api/projects/{project.Id}/github/sync`) при сбое вызова `MockGitHubService.GetCommitsAsync`.
  4. Подтверждено строгое соответствие ответа стандарту RFC 9457 ProblemDetails: возврат HTTP-статуса `500 Internal Server Error` и заголовка `Content-Type: application/problem+json`.
  5. Проверено наличие и корректность обязательных полей ProblemDetails: `status: 500`, `title: "Внутренняя ошибка сервера"`, `detail: "Произошла непредвиденная ошибка на стороне сервера."`, `type: "https://httpstatuses.com/500"`, `code: "INTERNAL_SERVER_ERROR"`, `instance: "/api/projects/{project.Id}/github/sync"`, непустой `traceId`.
  6. Проверено безопасное содержимое ответа и полное отсутствие утечки секретов и отладочных данных: синтетический PAT, уникальный секретный маркер, внутреннее сообщение об ошибке, тип исключения `InvalidOperationException` и трассировка стека (`at Api.`, `at Application.`, `at Microsoft.AspNetCore`, `at System.`) отсутствуют в теле ответа.
  7. Подтверждено отсутствие технических полей исключения в структуре JSON (`exception`, `stackTrace`, `stackTraceString`, `innerException`, `errors`).
  8. Подтверждён фактический вызов метода `MockGitHubService.GetCommitsAsync` ровно один раз (`Times.Once`).
  9. Результаты тестирования:
     - Адресный запуск (`UnhandledException_InEndpointExecution`): 1/1 пройден (0 ошибок).
     - Полный запуск набора тестов `Backend.Tests`: 768/768 пройдено на момент закрытия TD-015 (в актуальном состоянии кодовой базы после закрытия TD-007 и TD-006 число тестов `Backend.Tests` составляет 951/951 успешно, 0 ошибок, 0 пропущено).
