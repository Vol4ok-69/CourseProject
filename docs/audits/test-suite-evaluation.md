# Оценка и аудит тестового набора CourseProject

**Дата аудита:** Ноябрь 2026 г. (Актуализировано по результатам реализации Этапов 1–6)<br/>
**Базовый коммит:** `main`<br/>
**Фактическое число тестов в решении (`CourseProject.slnx`):** 1103 теста (1095 успешно, 8 известных воспроизводимых сбоев TD-003) в 6 тестовых сборках:
- `Backend.Tests`: 710 тестов (все успешно)
- `DatabaseAnalyzer.Tests`: 173 теста (все успешно)
- `DatabaseAnalyzer.Runner.Tests`: 103 теста (все успешно)
- `SecurityAnalyzer.Tests`: 56 тестов (все успешно)
- `SqlAnalyzer.Tests`: 44 теста (все успешно)
- `CSharpAnalyzer.Tests`: 17 тестов (9 успешно, 8 известных сбоев TD-003 в классе `RepositorySourceServiceTests`)

*Последняя известная статистика тестового набора, полученная в предыдущем подтверждённом цикле тестирования (16.11.2026). Не является результатом нового запуска на дату актуализации документа.*

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

### 2.2. Анализатор базы данных (`DatabaseAnalyzer.Tests` — 188 тест-кейсов [180 методов], `DatabaseAnalyzer.Runner.Tests` — 103 тест-кейса [82 метода])

| Тестовый набор | Кол-во тестов | Оценка (0-10) | Назначение и качество | Решение | Аргументация и статус |
| :--- | :---: | :---: | :--- | :---: | :--- |
| **`DatabaseAnalyzer.Tests`** | 188 тест-кейсов (180 методов) | **8/10** | Проверка правил `DB01`–`DB05`, маппинга метаданных `EfCoreMetadataMapper`, разрешения проектов и моделей. | **`KEEP`** | Тщательно покрывает логику анализатора моделей. Задача техдолга TD-004 (DB04, PK/AK и порядок столбцов) закрыта (`VERIFIED`, подтверждено запуском 188/188 тест-кейсов от 21.11.2026 с 15 добавленными тестами: 7 маппера и 8 DB04/DB05); для правила `DB05` открыта задача TD-005. |
| **`DatabaseAnalyzer.Runner.Tests`** | 103 тест-кейса (82 метода) | **9/10** | Проверка CLI-адаптера Docker, параметров запуска контейнера и контракта обмена runner. | **`KEEP`** | Обеспечивает верификацию взаимодействия с Docker CLI и изоляцию контейнера анализатора (подтверждено запуском 103/103 тест-кейсов от 21.11.2026). |

---

### 2.3. Серверная часть и контроллеры API (`Backend.Tests` — 710 тестов)

| Тестовый класс | Кол-во тестов | Оценка (0-10) | Тип конвейера | Решение | Аргументация и статус |
| :--- | :---: | :---: | :--- | :---: | :--- |
| **`ApiPipelineIntegrationTests.cs`** | 33 | **9/10** | Настоящий HTTP Integration (`WebApplicationFactory` + PostgreSQL) | **`KEEP`** | Сквозные интеграционные тесты реального HTTP-конвейера: Rate Limiting (429 Retry-After), CORS (OPTIONS 5173/3000/untrusted), JWT Bearer (401/200), RBAC (403/200), IDOR, RFC 9457 ProblemDetails, enum строки, жизненный цикл проектов, коммитов, PAT и очереди задач на реальном PostgreSQL. |
| **`CorsPolicyTests.cs`** | 41 | **9/10** | Unit-тест CORS-пайплайна и парсера | **`KEEP`** | Тщательно проверяет строгий разбор портов (1..65535, запрет порта 0, пустых портов, нечисловых значений), нормализацию по RFC 6454 (удаление портов 80/443), а также unit-пайплайн `CorsMiddleware`. |
| **`RateLimitingTests.cs`** | 10 | **5/10** | Синтетический pipeline | **`KEEP`** | Дополняет сквозные интеграционные тесты проверкой базовой конфигурации Token Bucket. |
| **`ProblemDetailsAndEnumTests.cs`** | 18 | **9/10** | Сериализация JSON и Middleware ExceptionHandler | **`KEEP`** | Проверяет сериализацию перечислений строками, структуру RFC 9457 `ProblemDetails` и обработку необработанных исключений в `UseExceptionHandler` (возврат 500, маскирование секретов и стека). |
| **`AppDbContextQueueModelTests.cs`** | 12 | **9/10** | SQLite in-memory / EF Model | **`KEEP`** | Тщательно проверяет соответствие модели EF Core: типы полей очереди, составные уникальные индексы. **Важно:** не классифицировать эти тесты как «PostgreSQL-интеграционные», так как они выполняются на базе SQLite in-memory провайдера. |
| **`JwtLoggingTests.cs`** | 8 | **7/10** | Изолированный логгер | **`KEEP`** | Проверяет подавление токенов в логах и валидацию заголовка `Authorization`. |
| **`AdminProtectionTests.cs`** | 14 | **8/10** | Unit-тесты сервиса | **`KEEP`** | Проверяет логику запрета деградации/удаления последнего админа на уровне `UserService` (согласовано с защитой через advisory lock в СУБД). |
| **`AnalysisServiceQueueTests.cs`** | 32 | **9/10** | Service-level (SQLite in-memory + моки) | **`KEEP`** | Детально проверяет смену статусов очереди, генерацию токенов выполнения, продление и истечение аренды. |
| **`ProjectsControllerTests.cs`** | 42 | **8/10** | Controller Unit | **`KEEP`** | Тщательное тестирование авторизации, изоляции пользователей (IDOR) и маппинга DTO. |
| **`CommitsControllerTests.cs`** | 36 | **8/10** | Controller Unit | **`KEEP`** | Проверяет CRUD коммитов, пагинацию и обработку конфликтов уникальности 409. |
| **`AnalysesControllerTests.cs`** | 38 | **8/10** | Controller Unit | **`KEEP`** | Проверяет возвращение кода 202 Accepted, валидацию и получение результатов. |
| **`AuthControllerTests.cs`** | 28 | **8/10** | Controller Unit | **`KEEP`** | Проверяет регистрацию, логин, валидацию паролей и выдачу JWT. |
| **Прочие тесты репозиториев, сервисов и интеграций** | 396 | **8/10** | Unit / Component / Integration | **`KEEP`** | Обеспечивают высокую плотность проверки бизнес-логики, валидации DTO, шифрования токенов и PostgreSQL-фикстур. |

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

5. **Сквозная обработка ошибок (RFC 9457 ProblemDetails при необработанных исключениях 500) — ЧАСТИЧНО (`IN_PROGRESS`):**
   - **Фактическое состояние:** Обработка непредвиденных исключений через `UseExceptionHandler` и возврат RFC 9457 `500 Internal Server Error` со схемой ProblemDetails (скрытие стектрейса и паролей, генерация `traceId`) протестирована на уровне конвейера middleware в [`ProblemDetailsAndEnumTests.cs#L136-L178`](../../tests/Backend.Tests/ProblemDetailsAndEnumTests.cs#L136-L178) (`Pipeline_UnhandledException_Returns500ProblemDetailsWithMaskedDetails`).
   - **Остаточный критерий для закрытия TD-015:** Добавить в `ApiPipelineIntegrationTests.cs` сквозной вызов через `HttpClient` тестовой фабрики, провоцирующий непредвиденное исключение (через мок зависимости или тестовую конфигурацию), с валидацией HTTP 500 и формата ProblemDetails. В связи с этим задача **TD-015** сохраняется в статусе **`IN_PROGRESS`** до реализации этого теста.

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
