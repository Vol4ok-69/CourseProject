# Предварительный план последующих работ (основа для TD-017)

> Данный документ является предложением по результатам аудита TD-016 и **не является** утверждённой задачей в `Technical-Debt-Plan.md`.

## 1. Блокирующие исправления (P0/P1)
- Подтверждённых дефектов уровня **P0/P1 не выявлено (0)**. Блокирующих исправлений нет.

## 2. Стабилизация OpenAPI и клиентских контрактов (можно выполнять параллельно, до генерации SDK)
- **`TD017-001` (P2, источники: `TD016-F-001`, `TD016-F-002`, `TD016-F-003`, `TD016-F-004`, `TD016-F-004B`, `TD016-F-005`):**
  - *Документация OpenAPI (без изменения поведения API):* перегенерация `swagger.json` (актуальные `GitHubSyncResultDto`, `CreateCommitDto.CommitHash`, `409` в `SyncGitHub`), `CustomOperationIds`, `OperationFilter` для `security: []` на `register`/`login`, схема `ApiProblemDetails` (`code`, `traceId`, `errors`, `application/problem+json`), `SupportNonNullableReferenceTypes()` для `nullable: false` (плюс отдельная проверка настройки `required` в Swashbuckle `10.2.3`), XML-описание часового пояса `CommitDate`, документирование `Retry-After` на `429` (сам заголовок уже возвращается в `Program.cs:153-157`) и ограничений пагинации (без добавления `[Range]`/`[Required]`, если они меняют фактическую валидацию API).
  - *Критерии приёмки:* все 26 MVC-операций имеют уникальные `operationId`; `register`/`login` имеют `security: []`; `SyncGitHub` документирует `409` и актуальный `GitHubSyncResultDto`; пробная генерация C# и TypeScript клиентов проходит без ошибок.

## 3. PostgreSQL и производительность
- **`TD017-002` (источник: `TD016-F-008`):** Сначала установить назначение `courseproject-db` (целевая или локальная/устаревшая среда). Применение двух недостающих миграций выполнять **условно** — только после подтверждения роли БД, создания резервной копии и проверки миграционного плана.
- **`TD017-003` (P2/P3, источник: `TD016-F-009`):** Наполнить тестовую БД репрезентативными данными и выполнить `EXPLAIN (ANALYZE, BUFFERS)` для полного запроса `TD-007`, списочного `Include(Results)` (`GetByCommitIdAsync`), индекса `("ProjectId", "CommitDate" DESC)` и оценки `API-023`.

## 4. Безопасность (динамическая проверка `needs_validation` и точечные улучшения)
- **`TD017-004` (источник: `TD016-F-010` — `HUNTER-W2-001`, `HUNTER-W2-002`):** Изолированно проверить и при подтверждении добавить `matchTimeout`/`NonBacktracking` в `ConnectionStringPasswordRegex` (`Sec001`, `Sec002`) и лимиты размера/файловые проверки `ReparsePoint` в `CSharpAnalyzer`, `SqlAnalyzer`, `SecurityAnalyzer`.
- **`TD017-005` (источник: `TD016-F-010` — `CAND-SE-002`, `HUNTER-W2-003`, `HUNTER-W2-004`, `QUEUE-01`, `QUEUE-03`, `ZW-001`, `ZW-003`, `ZW-005`):** Проверить rate limiting на ресурсоёмких эндпоинтах и `/health`, оптимизировать пересчёт `allExpandedParents` и лимиты страниц в `GitHubSyncService`, учесть директории в `MaxFileCount` и убрать ранний выход до очистки `containerExchangeDir`.
- **`TD017-006` (источник: `TD016-F-010` — `DOCKER-01`, `DOCKER-02`, `auth-jwt-no-revocation-stale-claims`):** Проверить на целевом Docker-хосте права сокета `/var/run/docker.sock` (`DOCKER_GID`) и `0777` на `out/`.

## 5. Тестовое покрытие и качество тестов
- **`TD017-007` (P2, источники: `TD016-F-011`, `TD016-F-011B`, `TD016-F-011C`):** Добавить HTTP-интеграционные тесты `AdminUsersController` (`PUT`/`DELETE`), усилить `Times.Never`/БД-проверки при `403`/`409` и обеспечить обязательный запуск `[DockerFact]` в CI с доступным Docker.
- **`TD017-008` (P3, источник: `TD016-F-012`):** Доработать тесты (`ProblemDetailsAndEnumTests`, `RateLimitingTests`, `ScenarioD`, `AnalysisQueueTests`, `GitHubServiceTests` malformed JSON/`TaskCanceledException`, `FOR UPDATE SKIP LOCKED` на 2+ задачах, whitespace/extra fields DTO).

## 6. Hardening (можно отложить)
- **`TD017-009` (Hardening, источник: `TD016-F-014`):** Полный перечень из `HARDENING.md`:
  1. `SUPPLY-01`: `packages.lock.json` + `dotnet restore --locked-mode`.
  2. `SUPPLY-02`: пиннинг Docker-образов по `@sha256:`.
  3. Явные `ClockSkew = TimeSpan.Zero` и `ValidAlgorithms = [HmacSha256]`.
  4. Проверка минимальной длины `Jwt:Key` (32 байта) на старте.
  5. Ограничение `AllowedHosts` вместо `"*"` (для не-контейнерных развёртываний).
  6. Вывод `EfCoreModelInspector` и `LocalProcessAdapter` из production-сборки `DatabaseAnalyzer.dll`.
  7. Периодическая очистка устаревших файлов `.locks/` и запрет ведущего `-` в `ValidImageRegex` (`DockerCliProcessAdapter.cs:415`).
  8. Привязка AAD (`User.Id` + purpose) и обнуление `_key` в `AesGcmTokenEncryptionService`.

## 7. Продуктовые решения владельца проекта (`TD017-010`, источник: `TD016-F-013`)

| # | Вопрос | Кто решает | До чего необходимо | Блокер клиентов? |
|---|---|---|---|---|
| 1 | Привязка `TelegramUserId`/`ChatId` и доставка уведомлений (webhook/polling) | Владелец проекта | До начала реализации Telegram-бота | **Да для Telegram-бота** (нет для CLI/Desktop) |
| 2 | Необходимость отмены анализа через HTTP (`AnalysisStatus.Cancelled`) | Владелец проекта | До добавления UI-кнопки отмены | Нет (не входит в текущие требования) |
| 3 | Стратегия версионирования (`/api/v1/` vs аддитивная эволюция) | Владелец / Архитектор | Желательно до публикации SDK в отдельные репо | Нет (рекомендация эволюции) |
| 4 | Политика смены `RepoUrl` у проекта с существующими коммитами | Владелец / Архитектор | До массовой эксплуатации редактирования проектов | Нет |
| 5 | Поведение `GET /api/projects/owner/{ownerId}` при несуществующем `ownerId` (`200 []` vs `404`) | Владелец / Архитектор | До реализации админ-просмотра проектов по владельцу | Нет |
| 6 | Допустимость коротких хэшей (`7..39`) в `CreateCommitDto` | Владелец / Архитектор | До ручного создания коммитов через CLI/Desktop | Нет |
| — | *Дополнительно (`TD016-F-007`):* пагинация `GET /api/projects` и `/owner/{ownerId}` | Владелец / Архитектор | До роста числа проектов >100 на аккаунт | Нет (ограничение масштаба) |
