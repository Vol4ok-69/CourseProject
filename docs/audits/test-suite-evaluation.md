# Оценка и аудит тестового набора CourseProject

**Дата аудита:** Ноябрь 2026 г. (Актуализировано по результатам реализации TD-002, 22.11.2026)<br/>
**Базовый коммит:** `main`<br/>
**Фактическое число тестов в решении (`CourseProject.slnx`):** 1180 тестов (1172 успешно, 8 известных сбоев TD-003) в 6 тестовых сборках:
- `Backend.Tests`: 768 тестов (все успешно, exit code 0)
- `DatabaseAnalyzer.Tests`: 190 тестов (все успешно, exit code 0)
- `DatabaseAnalyzer.Runner.Tests`: 105 тестов (все успешно, exit code 0)
- `SecurityAnalyzer.Tests`: 56 тестов (все успешно, exit code 0)
- `SqlAnalyzer.Tests`: 44 теста (все успешно, exit code 0)
- `CSharpAnalyzer.Tests`: 17 тестов (9 успешно, 8 известных сбоев TD-003 в классе `RepositorySourceServiceTests`, exit code 1)

*Команда полного запуска `dotnet test --no-restore` завершается с кодом возврата (exit code) **1** исключительно из-за 8 непрошедших тестов проекта `CSharpAnalyzer.Tests`. В остальных 5 проектах суммарно 1172 теста завершаются с exit code **0**. Инструментальное сравнение с предварительно сохранённой внешней базовой линией (baseline diff) не производилось; все 8 сбоев точно соответствуют известному состоянию отложенной задачи TD-003 (требование `IAnalysisLeaseGuard` в `RepositorySourceService`).*

*Примечание об источниках данных: отдельные файлы отчётов (`.trx`) или полные дампы вывода команд для запуска от 22.11.2026 в репозиторий не сохранялись; статистика набора тестов приведена по отчёту запуска сессии Antigravity, а не по приложенным сохранённым логам.*

---

## 1. Методология, классификация уровней и шкала оценки

### 1.1. Классификация уровней тестирования в проекте
Для исключения терминологической путаницы в проекте строго разделяются четыре уровня тестов:
1. **Unit-тесты (Модульные):** Изолированная проверка логики одного конкретного метода или класса с мокированием внешних зависимостей (`ProjectsControllerTests`, `PasswordHasherTests`, синтаксические правила `SqlAnalyzer.Tests`, `CSharpAnalyzer.Tests`, `SecurityAnalyzer.Tests`). Конвейер middleware ASP.NET Core и СУБД в них не участвуют.
2. **Component-тесты (Компонентные):** Проверка взаимодействия группы тесно связанных модулей без внешних сетевых сервисов (проверка сериализации JSON RFC 9457 `ProblemDetailsAndEnumTests`, маппинг метаданных `EfCoreMetadataMapperTests`).
3. **Service-level тесты (Сервисные):** Проверка прикладных сервисов с реальным графом сущностей EF Core, но на базе SQLite in-memory провайдера и с моками сетевых клиентов (`AnalysisServiceQueueTests`, `AdminProtectionTests`). Они проверяют бизнес-правила и смену состояний, но не эмулируют сетевой стек и специфичные возможности PostgreSQL.
4. **Настоящие Integration-тесты (Интеграционные):**
   - *HTTP Integration:* Сквозная проверка конвейера ASP.NET Core через `WebApplicationFactory<Program>` с реальным прохождением middleware (Rate Limiting, CORS, Authentication, ProblemDetails).
   - *Database Integration:* Проверка транзакционных блокировок и конкурентности против реального экземпляра PostgreSQL (Testcontainers или локальный экземпляр).

### 1.2. Шкала оценки качества тестов (0–10)
- **0–3 (Фиктивные/бесполезные тесты):** Тесты без осмысленных утверждений, тестирование очевидных свойств POCO-объектов, тавтологии, тестирование самого фреймворка.
- **4–6 (Поверхностные тесты):** Простые тесты с искусственными заглушками; тестируют мок самого мока, пропуская поведение реального конвейера ASP.NET Core и граничные условия.
- **7–8 (Качественные компонентные и сервисные тесты):** Надёжная проверка синтаксических конструкций Roslyn AST, бизнес-правил и контрактов с валидными утверждениями.
- **9–10 (Высокоточные тесты):** Комплексная проверка параллелизма, транзакционности, парсинга AST или сквозного жизненного цикла.

### 1.3. Возможные решения:
- **`KEEP`** — сохранить в текущем виде, тест выполняет свои задачи качественно.
- **`REWRITE`** — переписать или доработать assertions из-за устаревших предположений или ложных срабатываний.
- **`MERGE`** — объединить дублирующиеся тесты для снижения накладных расходов.
- **`REMOVE`** — удалить (только если тест проверяет функционал, удаленный из кодовой базы, либо является стопроцентным дубликатом).

---

## 2. Детальная оценка тестовых проектов и классов

### 2.1. Анализаторы исходного кода (`SqlAnalyzer.Tests`, `CSharpAnalyzer.Tests`, `SecurityAnalyzer.Tests`)

| Тестовый набор | Кол-во тестов | Оценка (0-10) | Назначение и качество | Решение | Аргументация и недостающие сценарии |
| :--- | :---: | :---: | :--- | :---: | :--- |
| **`SqlAnalyzer.Tests`** | 44 | **9/10** | Проверка правил `SQL001` (интерполяция/конкатенация) и `SQL002` (`SELECT *`). Высокая точность проверки AST. | **`KEEP`** | Тесты используют реальный синтаксический анализатор Roslyn. Тщательно покрыты сценарии конкатенации, `$""`, `string.Format`. *Недостает:* проверки вызовов через методы-обертки и проверки устойчивости к синтаксическим ошибкам в файле. |
| **`CSharpAnalyzer.Tests`** | 17 | **8/10** | Проверка правил `CS001` (LargeClass > 500 строк) и `CS002` (LongMethod > 50 строк). | **`KEEP`** | Корректно подсчитывают строки кода с учетом комментариев и пустых строк. *Недостает:* тестов на частичные классы (`partial class`) и многофайловые объявления. |
| **`SecurityAnalyzer.Tests`** | 56 | **8/10** | Проверка правил `SEC001`–`SEC005` (утечки паролей, плоские секреты в appsettings, опасный CORS, слабая криптография, отключение SSL). | **`KEEP`** | Отличная база тестовых шаблонов уязвимого кода. *Недостает:* тестов для обнаружения `.WithOrigins("*")` (DEF-007) и `ServicePointManager` (DEF-008). |

---

### 2.2. Анализатор базы данных (`DatabaseAnalyzer.Tests` — 190 тест-кейсов, `DatabaseAnalyzer.Runner.Tests` — 105 тест-кейсов)

| Тестовый набор | Кол-во тестов | Оценка (0-10) | Назначение и качество | Решение | Аргументация и статус |
| :--- | :---: | :---: | :--- | :---: | :--- |
| **`DatabaseAnalyzer.Tests`** | 190 тест-кейсов | **8/10** | Проверка правил `DB01`–`DB05`, маппинга метаданных `EfCoreMetadataMapper`, разрешения проектов и моделей, изоляции и обработки частичных сбоев в рамках TD-001/TD-002. | **`KEEP`** | Тщательно покрывает логику анализатора моделей. Задачи TD-001, TD-002, TD-004 и TD-005 закрыты (`VERIFIED`, подтверждено 190/190 успешно в `DatabaseAnalyzer.Tests` и 105/105 успешно в `DatabaseAnalyzer.Runner.Tests`). |
| **`DatabaseAnalyzer.Runner.Tests`** | 105 тест-кейсов | **9/10** | Проверка CLI-адаптера Docker, параметров запуска контейнера и контракта обмена runner. | **`KEEP`** | Обеспечивает верификацию взаимодействия с Docker CLI и изоляцию контейнера анализатора (подтверждено 105/105 успешно). |

---

### 2.3. Серверная часть и контроллеры API (`Backend.Tests` — 768 тестов)

Полная и верифицированная разборка всех 30 тестовых классов сборки `Backend.Tests` (ровно 768 тестов, 0 сбоев, exit code 0):

| № | Тестовый класс | Кол-во тестов | Оценка (0-10) | Тип конвейера | Решение | Назначение и проверяемые сценарии |
| :-: | :--- | :-: | :-: | :--- | :-: | :--- |
| 1 | **`AnalysisServiceQueueTests`** | 91 | **9/10** | Service-level (SQLite + моки) | **`KEEP`** | Жизненный цикл очереди, смена статусов, генерация токенов выполнения, сценарии частичных сбоев DatabaseAnalyzer (A–G), потокобезопасность `LeaseRenewalState` и изоляция отмены при гонках (6 тестов TD-002). |
| 2 | **`CorsPolicyTests`** | 84 | **9/10** | Unit / Middleware | **`KEEP`** | Строгий разбор портов (1..65535, запрет 0, пустых и нечисловых), нормализация по RFC 6454 (порты 80/443), белый список origin'ов, middleware CORS. |
| 3 | **`DtoValidationAndAnalysisErrorTests`** | 68 | **8/10** | Unit / Validation | **`KEEP`** | Валидация входных DTO (пароли, логины, URL репозиториев, хеши коммитов SHA-1 и SHA-256 по TD-008), санитайзер сообщений об ошибках (маскирование IP-адресов, абсолютных путей, паролей). |
| 4 | **`GitHubServiceTests`** | 50 | **8/10** | Unit / Mock HTTP | **`KEEP`** | Интеграция с GitHub API: получение коммитов, проверка веток, валидация URL, устойчивость к сбоям сети и таймаутам, безопасная передача токенов. |
| 5 | **`ApiPipelineIntegrationTests`** | 45 | **9/10** | HTTP Integration (`WebApplicationFactory` + PG) | **`KEEP`** | Сквозной HTTP-конвейер: Rate Limiting (429 Retry-After), CORS (OPTIONS 5173/3000/untrusted), JWT Bearer (401/200), RBAC (403/200), IDOR, RFC 9457 ProblemDetails, enum строки, SHA-256 (TD-008), сквозные исключения HTTP 500 через HttpClient (TD-015). |
| 6 | **`RepositorySourceServiceTests`** | 38 | **8/10** | Unit / Security | **`KEEP`** | Скачивание и распаковка архивов репозиториев с обязательной проверкой `IAnalysisLeaseGuard`, защита от Path Traversal, Zip Slip и атак по префиксу каталога. |
| 7 | **`PostgreSqlWorkspaceCleanupIntegrationTests`** | 38 | **9/10** | Database Integration (PostgreSQL) | **`KEEP`** | Интеграционная очистка рабочих каталогов на PostgreSQL, сценарии A–H, атомарность токенов выполнения, advisory locks, блокировка очистки при активном воркере. |
| 8 | **`ProblemDetailsAndEnumTests`** | 36 | **9/10** | Component / Serialization | **`KEEP`** | Строковая сериализация перечислений, RFC 9457 `ProblemDetails`, маскирование стека и исключений в `UseExceptionHandler` (HTTP 500). |
| 9 | **`PaginationTests`** | 34 | **8/10** | Unit / Controller | **`KEEP`** | Пагинация списков сущностей, валидация `page`/`pageSize`, граничные значения, заголовки и метаданные пагинации. |
| 10 | **`GitHubSyncServiceTests`** | 32 | **8/10** | Unit / Service | **`KEEP`** | Синхронизация коммитов репозитория, сопоставление веток, сохранение авторов и дат в БД. |
| 11 | **`ProjectsControllerTests`** | 25 | **8/10** | Controller Unit | **`KEEP`** | CRUD проектов, проверка прав владельца (защита от IDOR), валидация DTO, сопоставление маршрутов. |
| 12 | **`TokenEncryptionServiceTests`** | 24 | **9/10** | Unit / Cryptography | **`KEEP`** | Симметричное шифрование AES-256-GCM для токенов GitHub PAT, валидация ключей (256 бит Base64), защита от повреждения шифротекста/тегов. |
| 13 | **`RateLimitingTests`** | 23 | **7/10** | Unit / Pipeline | **`KEEP`** | Конфигурация лимитов Token Bucket (AuthPolicy, GeneralPolicy), проверка счетчиков и алгоритмов скользящего окна. |
| 14 | **`GitHubCredentialServiceTests`** | 21 | **8/10** | Unit / Security | **`KEEP`** | Безопасное сохранение, извлечение и удаление токенов доступа GitHub, привязка к пользователю. |
| 15 | **`AuthGitHubControllerTests`** | 19 | **8/10** | Controller Unit | **`KEEP`** | OAuth/PAT флоу интеграции с GitHub, валидация учетных данных, связывание аккаунтов. |
| 16 | **`PostgreSqlAdvisoryLockIntegrationTests`** | 16 | **9/10** | Database Integration (PostgreSQL) | **`KEEP`** | Конкурентные транзакционные распределенные блокировки (`pg_advisory_xact_lock`), защита от гонок регистрации и захвата задач. |
| 17 | **`AnalysesControllerTests`** | 15 | **8/10** | Controller Unit | **`KEEP`** | Эндпоинты запуска и проверки анализа, возврат HTTP 202 Accepted, проверка прав доступа к результатам. |
| 18 | **`AdminUsersControllerTests`** | 15 | **8/10** | Controller Unit | **`KEEP`** | Управление пользователями из административной панели, валидация прав администратора, запрет самодеградации. |
| 19 | **`AuthControllerTests`** | 14 | **8/10** | Controller Unit | **`KEEP`** | Регистрация, аутентификация, валидация сложности паролей, выдача JWT токенов. |
| 20 | **`AnalysisQueueTests`** | 13 | **8/10** | Service-level (SQLite) | **`KEEP`** | Базовая работа очереди задач, смена статусов `Pending` -> `Running` -> `Completed`/`Failed`. |
| 21 | **`AdminProtectionTests`** | 13 | **8/10** | Unit / Service | **`KEEP`** | Защита последнего администратора от удаления и понижения роли на уровне сервиса `UserService`. |
| 22 | **`DatabaseInitializationAndHealthTests`** | 10 | **9/10** | Database Integration (PostgreSQL) | **`KEEP`** | Накат миграций EF Core, health checks базы данных, проверка корректности структуры схем и таблиц. |
| 23 | **`CommitsControllerTests`** | 9 | **8/10** | Controller Unit | **`KEEP`** | Получение списка коммитов проекта, пагинация, фильтрация по веткам. |
| 24 | **`RepositoryArchiveSecurityTests`** | 7 | **8/10** | Unit / Security | **`KEEP`** | Защита от Zip Bomb, симлинков, выходов за пределы целевой папки при распаковке ZIP. |
| 25 | **`WorkspaceFileLockProviderTests`** | 7 | **8/10** | Unit / FileLock | **`KEEP`** | Межпроцессные файловые блокировки `FileShare.None` на каталоги анализа, предотвращение конфликтов очистки. |
| 26 | **`JwtLoggingTests`** | 7 | **7/10** | Unit / Logging | **`KEEP`** | Маскирование чувствительных данных: токены `Bearer`, пароли и секреты исключаются из журналов логов. |
| 27 | **`DatabaseUniqueConstraintTests`** | 5 | **9/10** | Component / EF Model | **`KEEP`** | Валидация уникальных ограничений EF Core: `(ProjectId, Hash)`, `(Username)` и составные ключи. |
| 28 | **`AnalysisDeduplicationTests`** | 5 | **8/10** | Service-level | **`KEEP`** | Предотвращение дублирования задач анализа для одного и того же коммита в очереди. |
| 29 | **`AnalysisBackgroundServiceTests`** | 3 | **8/10** | Service-level | **`KEEP`** | Жизненный цикл фонового воркера `AnalysisBackgroundService`, обработка исключений в цикле обработки. |
| 30 | **`AppDbContextQueueModelTests`** | 1 | **8/10** | Component / EF Model | **`KEEP`** | Проверка соответствия модели EF Core для сущностей распределенной очереди. |
| **ИТОГО** | **30 тестовых классов** | **768** | **8.4/10** | **Смешанный (Unit, Component, Integration)** | **`KEEP`** | **Все 768 тестов успешно пройдены (0 непрошедших, 0 пропущенных, код возврата 0)** |

#### 2.3.1. Выделенные тесты целостности Lease Renewal и изоляции отмены в `AnalysisServiceQueueTests` (TD-002)

В рамках задачи **TD-002** набор тестов очереди `AnalysisServiceQueueTests` расширен до 91 теста (все 91 успешно). Особое внимание уделено защите от гонок и корректному разграничению штатной отмены и сбоев продления аренды:

1. `TD002_LeaseRenewal_RenewalExceptionDuringAnalyzerThatIgnoresCancellation_DoesNotCompleteSuccessfully`:
   - *Назначение:* Проверяет случай, когда анализатор перехватывает и игнорирует отмену (`cancellationToken`), продолжая работу, но фоновое продление аренды завершилось критической ошибкой (`RenewalException`).
   - *Гарантия:* После выхода из анализатора задача не помечается как `Completed`; конвейер обнаруживает терминальное состояние аренды и безопасно завершает задачу со статусом ошибки.
2. `TD002_LeaseRenewal_RenewalExceptionDuringDatabaseAnalyzer_DoesNotBecomeInternalAnalyzerWarning`:
   - *Назначение:* Проверяет, что сбой фонового продления аренды во время выполнения `DatabaseAnalyzer` не маскируется внутренним блоком перехвата анализатора и не превращается в синтетическое предупреждение `AnalyzerFinding` (`Severity.Warning`), а пробрасывается наружу как прерывание анализа.
3. `TD002_LeaseRenewal_RenewalExceptionDuringAnalyzerReturningEmptyResults_DoesNotBecomeNormalStop`:
   - *Назначение:* Проверяет, что если анализатор возвращает пустой список результатов на фоне произошедшего сбоя продления аренды, конвейер не регистрирует штатную остановку (`NormalStop`) и не маскирует ошибку фоновой аренды.
4. `TD002_LeaseRenewal_NormalStop_DoesNotOverwriteTerminalStopReason`:
   - *Назначение:* Проверяет потокобезопасность `LeaseRenewalState`: вызов `RequestNormalStop` из основного потока не может перезаписать терминальную причину (`TrySetTerminalReason`), ранее зафиксированную фоновой задачей (например, `RenewalException` или `OwnershipLost`).
5. `TD002_LeaseRenewal_RenewalException_WhenOwnershipCannotBeConfirmed_DoesNotCallFailCompleteOrCleanup`:
   - *Назначение:* Проверяет ключевой инвариант безопасности распределённой очереди: при сбое продления аренды, если текущий обработчик не может подтвердить сохранение владения задачей (`ValidateLeaseOwnershipAsync == false`), строго запрещено вызывать `FailAsync`, `CompleteAsync` и `CleanupAsync`, исключая разрушение рабочего пространства другого воркера, перехватившего задачу.
6. `TD002_LeaseRenewal_RenewalException_WhenOwnershipConfirmed_MarksTaskFailedWithoutCompletion`:
   - *Назначение:* Проверяет, что при сбое продления аренды, если текущий экземпляр смог успешно подтвердить действующее владение задачей, он переводит задачу в `Failed` с фиксацией диагностического сообщения и выполняет очистку рабочего пространства, не вызывая `CompleteAsync`.

---

### 2.4. Детальный разбор 8 известных сбоев в `CSharpAnalyzer.Tests`

Все 8 известных воспроизводимых сбоев решения локализованы в одном тестовом классе `CSharpAnalyzer.Tests.RepositorySourceServiceTests` тестового проекта `tests/CSharpAnalyzer.Tests`:

| № | Полное имя теста | Фактическое исключение | Фактическая причина и механизм сбоя | Связь с TD-003 |
|:---:|---|---|---|:---:|
| 1 | `CSharpAnalyzer.Tests.RepositorySourceServiceTests.DownloadAndExtractAsync_ShouldExtractRepository` | `InvalidOperationException: Файловая операция рабочего пространства запрещена без действующего IAnalysisLeaseGuard.` | Тест вызывает устаревшую 4-параметрическую перегрузку `DownloadAndExtractAsync(url, hash, analysisId, pat)` без передачи `IAnalysisLeaseGuard`. В `RepositorySourceService.cs` (L349) вызов без guard запрещен инвариантом безопасности TD-003. | **Доказана** (L349) |
| 2 | `CSharpAnalyzer.Tests.RepositorySourceServiceTests.DownloadAndExtractAsync_ShouldExcludeUnnecessaryDirectories` | `InvalidOperationException: Файловая операция рабочего пространства запрещена без действующего IAnalysisLeaseGuard.` | Аналогично: вызов устаревшей перегрузки `DownloadAndExtractAsync` без guard блокируется проверкой владения рабочим пространством. | **Доказана** (L349) |
| 3 | `CSharpAnalyzer.Tests.RepositorySourceServiceTests.CleanupAsync_ShouldDeleteAnalysisDirectory` | `InvalidOperationException: Файловая операция рабочего пространства запрещена без действующего IAnalysisLeaseGuard.` | Тест вызывает устаревшую перегрузку `CleanupAsync(int analysisId)`. В `RepositorySourceService.cs` (L624) удаление без guard блокируется инвариантом TD-003. | **Доказана** (L624) |
| 4 | `CSharpAnalyzer.Tests.RepositorySourceServiceTests.DownloadAndExtractAsync_ShouldCleanupAfterFailure` | `Assert.ThrowsAsync<InvalidDataException> Failure` (Actual: `InvalidOperationException: Файловая операция рабочего пространства запрещена...`) | Тест ожидал `InvalidDataException` от повреждённого архива, но метод упал раньше на проверке guard в L349. | **Доказана** (L349) |
| 5 | `CSharpAnalyzer.Tests.RepositorySourceServiceTests.DownloadAndExtractAsync_WithoutToken_ThrowsInvalidOperationException` | `Assert.Contains() Failure` (Expected: `"требуется персональный токен доступа (PAT)"`, Actual: `"Файловая операция рабочего пространства запрещена..."`) | Тест ожидал сообщение об отсутствии PAT, но проверка guard в L358 сработала раньше валидации аргументов. | **Доказана** (L358) |
| 6 | `CSharpAnalyzer.Tests.RepositorySourceServiceTests.DownloadAndExtractAsync_EmptyOrWhitespaceToken_ThrowsArgumentException(invalidToken: "")` | `Assert.ThrowsAsync<ArgumentException> Failure` (Actual: `InvalidOperationException: Файловая операция рабочего пространства запрещена...`) | Тест ожидал `ArgumentException`, но вызов без guard упал раньше на L349. | **Доказана** (L349) |
| 7 | `CSharpAnalyzer.Tests.RepositorySourceServiceTests.DownloadAndExtractAsync_EmptyOrWhitespaceToken_ThrowsArgumentException(invalidToken: "   ")` | `Assert.ThrowsAsync<ArgumentException> Failure` (Actual: `InvalidOperationException: Файловая операция рабочего пространства запрещена...`) | Тест ожидал `ArgumentException`, но вызов без guard упал раньше на L349. | **Доказана** (L349) |
| 8 | `CSharpAnalyzer.Tests.RepositorySourceServiceTests.DownloadAndExtractAsync_EmptyOrWhitespaceToken_ThrowsArgumentException(invalidToken: null)` | `Assert.ThrowsAsync<ArgumentException> Failure` (Actual: `InvalidOperationException: Файловая операция рабочего пространства запрещена...`) | Тест ожидал `ArgumentException`, но вызов без guard упал раньше на L349. | **Доказана** (L349) |

**Вывод:** Все 8 сбоев имеют строго доказанную причинную связь с задачей **TD-003**. Они вызваны тем, что в `RepositorySourceService.cs` внедрен инвариант безопасности файловых операций (запрет распаковки/очистки без `IAnalysisLeaseGuard`), в то время как тестовый класс `tests/CSharpAnalyzer.Tests/RepositorySourceServiceTests.cs` остался на устаревшем API и не предоставляет мок `IAnalysisLeaseGuard`. При этом класс `tests/Backend.Tests/RepositorySourceServiceTests.cs` уже переведен на новое API с `IAnalysisLeaseGuard` и успешно проходит.

**Текущий статус проблемы:** В соответствии с проектным решением дальнейшая реализация задачи **TD-003 отложена (`DEFERRED`)**, её статус реализации сохраняется как **`OPEN`**, а статус проверки — **`UNVERIFIED`**. Данные 8 известных сбоев документированы как сохраняющаяся техническая проблема тестового класса `CSharpAnalyzer.Tests`, их исправление отложено до отдельного решения о возобновлении работ по TD-003 и не входит в ближайший этап разработки.

---

## 3. Анализ реализации сквозных интеграционных тестов (`WebApplicationFactory<Program>`, TD-015)

В рамках задачи **TD-015** в проекте `Backend.Tests` разработан и функционирует полноценный конвейер сквозных интеграционных HTTP-тестов:
- Класс тестовой инфраструктуры: [`ApiWebApplicationFactory.cs`](../../tests/Backend.Tests/Integration/ApiWebApplicationFactory.cs) на базе `WebApplicationFactory<Program>`.
- Сквозные тесты HTTP-конвейера: [`ApiPipelineIntegrationTests.cs`](../../tests/Backend.Tests/Integration/ApiPipelineIntegrationTests.cs) (33 теста).
- Интеграция с реальной СУБД: [`PostgreSqlFixture.cs`](../../tests/Backend.Tests/Integration/PostgreSqlFixture.cs) (подключение к изолированной БД PostgreSQL, накат миграций, изоляция данных между прогонами).

### Фактическое покрытие критериев TD-015:

1. **Реальный Rate Limiting Pipeline — ПОДТВЕРЖДЕНО (`VERIFIED`):**
   - Метод: `RateLimiting_BurstRequests_Returns429TooManyRequests_WithRetryAfterAndProblemDetails`.
   - Проверка: отправка 10 запросов подряд к `/api/auth/login` (лимит AuthPolicy) и 11-го запроса без паузы. Подтверждён возврат HTTP `429 Too Many Requests`, заголовка `Retry-After`, формата `application/problem+json`, `status: 429`, `code: "RATE_LIMIT_EXCEEDED"`, `traceId`.

2. **Реальный CORS Pipeline — ПОДТВЕРЖДЕНО (`VERIFIED`):**
   - Методы: `CorsPreflight_AllowedOrigin5173_ReturnsAccessControlHeaders`, `CorsPreflight_AllowedOrigin3000_ReturnsAccessControlHeaders`, `CorsPreflight_UntrustedOrigin_DoesNotReturnAllowOriginHeader`.
   - Проверка: отправка реальных HTTP-запросов `OPTIONS /api/projects` через `HttpClient` с `Origin: http://localhost:5173` и `Origin: http://localhost:3000` возвращает разрешающие заголовки (`Access-Control-Allow-Origin`, `Access-Control-Allow-Methods`, `Access-Control-Allow-Headers`). Для `Origin: http://malicious-site.com` заголовок `Access-Control-Allow-Origin` не возвращается.

3. **Реальная JWT Authentication и Authorization Pipeline — ПОДТВЕРЖДЕНО (`VERIFIED`):**
   - Методы:
     - `AnonymousRequest_ToProtectedEndpoint_Returns401Unauthorized_WithProblemDetails` (отсутствие токена → 401 ProblemDetails);
     - `InvalidBearerToken_Returns401Unauthorized_WithProblemDetails` (повреждённый токен → 401 ProblemDetails);
     - `TokenWithInvalidSignature_Returns401Unauthorized_WithProblemDetails` (неверная цифровая подпись → 401 ProblemDetails);
     - `ExpiredToken_Returns401Unauthorized_WithProblemDetails` (истёкший токен → 401 ProblemDetails);
     - `ValidToken_AllowsAccess_Returns200Ok` (валидный токен → 200 OK);
     - `User_CannotAccess_OtherUsersProject_Returns403Forbidden` (проверка IDOR → 403 Forbidden);
     - `RegularUser_CannotAccess_AdminUsersEndpoint_Returns403Forbidden` (RBAC → 403 Forbidden);
     - `AdminUser_CanAccess_AdminUsersEndpoint_Returns200Ok` (RBAC Admin → 200 OK).

4. **Сериализация HTTP-контрактов и валидация DTO — ПОДТВЕРЖДЕНО (`VERIFIED`):**
   - Методы: `EnumProperties_SerializedAsString_NotNumeric`, `MalformedJson_Returns400BadRequest_WithValidationProblemDetails`, `MissingRequiredDtoFields_Returns400BadRequest_WithErrorsDictionary`.

5. **Сквозная обработка ошибок (RFC 9457 ProblemDetails при необработанных исключениях 500) — ПОДТВЕРЖДЕНО (`VERIFIED`):**
   - **Фактическое состояние:** Обработка непредвиденных исключений через `UseExceptionHandler` и возврат RFC 9457 `500 Internal Server Error` со схемой ProblemDetails (скрытие стектрейса и паролей, генерация `traceId`) полностью верифицирована сквозным HTTP-тестом `UnhandledException_InEndpointExecution_Returns500ProblemDetails_WithMaskedDetails_AndTraceId` в [`ApiPipelineIntegrationTests.cs`](../../tests/Backend.Tests/Integration/ApiPipelineIntegrationTests.cs) (коммит `e12b4ba`). Запрос через реальный `HttpClient` к маршруту `POST /api/projects/{project.Id}/github/sync` моделирует сбой зависимости (`MockGitHubService.GetCommitsAsync`), подтверждает возврат статуса 500, заголовок `application/problem+json`, обязательные поля RFC 9457, сокрытие внутреннего сообщения/секретов и однократный вызов сервиса.
   - Все критерии задачи **TD-015** полностью выполнены и проверены, задача закрыта в статусе **`VERIFIED`**.

---

## 3.6. Ограничения WebApplicationFactory и требование тестовой СУБД

> [!IMPORTANT]
> `WebApplicationFactory<Program>` развёртывает в памяти виртуальный HTTP-сервер для прогона конвейера промежуточного ПО, но **не предоставляет автоматически базу данных PostgreSQL**.
> 
> По умолчанию тестовый хост использует конфигурацию DI, прописанную в `Program.cs`. Если в тестах требуется взаимодействие с хранилищем, фабрика должна быть явно сконфигурирована на работу с реальным экземпляром PostgreSQL (например, через библиотеку `Testcontainers.PostgreSql`, поднимающую одноразовый Docker-контейнер на время прогона, либо через тестовую строку подключения к изолированной БД).
> 
> Подмена провайдера на SQLite in-memory внутри `WebApplicationFactory` допустима только для проверки маршрутизации и валидации DTO, но маскирует платформенные особенности PostgreSQL.

---

## 4. Разграничение тестов: SQLite vs PostgreSQL

> [!IMPORTANT]
> В документации и отчётах категорически запрещено называть тесты «PostgreSQL-интеграционными», если они инициализируют контекст через `optionsBuilder.UseInMemoryDatabase()` или `optionsBuilder.UseSqlite()`.

### 4.1. Сценарии, которые НЕВОЗМОЖНО проверить через SQLite in-memory:
1. **Консультативные блокировки уровня транзакции (Advisory Locks):**
   - Вызовы `SELECT pg_advisory_xact_lock(hashtext('admin_role_protection'))` (защита от удаления последнего администратора) и `analysis_enqueue_{commitId}` (дедупликация задач анализа). SQLite не поддерживает консультативные блокировки PostgreSQL и возвращает синтаксическую ошибку.
2. **Конкурентный захват задач очереди (`SKIP LOCKED`):**
   - Конструкция `SELECT ... FOR UPDATE SKIP LOCKED` в `AnalysisRepository.AcquireNextPendingAnalysisAsync`. SQLite блокирует файл базы целиком на запись и не поддерживает пропуск заблокированных строк (`SKIP LOCKED`).
3. **Обработка платформенных ошибок нарушения уникальности (Код `23505`):**
   - В PostgreSQL перехватывается `PostgresException` со свойством `SqlState == "23505"`. В SQLite при нарушении уникальности выбрасывается `SqliteException` с кодом ошибки 19 (`SQLITE_CONSTRAINT`), что делает проверку фильтров исключений на SQLite невалидной.
4. **Серверные генераторы ключей `GENERATED BY DEFAULT AS IDENTITY`:**
   - Поведение identity-колонок, сброс последовательностей и явная вставка идентификаторов работают по стандарту ANSI SQL / PostgreSQL, отличаясь от эвристик `AUTOINCREMENT` в SQLite.
5. **Специфичные типы данных и расширения:**
   - Поля `jsonb`, массивы `text[]`, регистронезависимый `citext` поддерживаются Npgsql, но падают при попытке создать или валидировать модель в SQLite без специализированных конвертеров.
