# Архитектура

## Слои

### Extension layer

Папка `TokensLimitsExtension/` содержит код, зависящий от Command Palette и Windows:

- `Program.cs` запускает `Shmuelie.WinRTServer.ComServer` при аргументе `-RegisterProcessAsComServer`.
- `TokensLimitsExtension.cs` реализует `IExtension` и отдаёт `CommandProvider`.
- `TokensLimitsExtensionCommandsProvider` регистрирует команду `com.tokenslimits.extension`, создаёт настройки и управляет жизненным циклом UI-поверхностей.
- `UsageOverviewPage` показывает все включённые провайдеры.
- `TokensLimitsPage` показывает детали одного провайдера.
- `UsageDockBandItem` показывает короткую сводку в dock band.

### Core layer

Папка `TokensLimitsExtension.Core/` не должна зависеть от UI:

- `Models/` содержит `UsageSnapshot`, `UsageWindow`, `UsageMetric` и Codex-специфичные модели.
- `Providers/` содержит `IUsageProvider`, дескрипторы, registry и конфигурацию.
- `Services/` содержит получение snapshot, кэш, форматирование, auth, HTTP-клиент и fallback.

## Контракт данных

Каждый провайдер возвращает `UsageSnapshot`:

| Поле | Смысл |
| --- | --- |
| `ProviderId` / `ProviderDisplayName` | Стабильный ID и имя в UI |
| `PrimaryWindow` / `SecondaryWindow` | Процент расхода, время сброса и размер окна |
| `Plan` | Тариф/план, если он известен |
| `IsEstimate` | Признак расчётной, а не подтверждённой API информации |
| `AdditionalRateLimits` | Дополнительные пары окон |
| `Metrics` | Кредиты, расход, запросы и другие значения без выдуманной квоты |
| `FetchedAt` / `Source` | Время получения и источник данных |

Для совместимости старого Codex-сервиса используется `CodexUsageProviderAdapter`, который преобразует `CodexUsageSnapshot` в общий контракт.

## Жизненный цикл запроса

```text
UI surface
  -> UsageSnapshotCache.GetUsageSnapshotAsync()
  -> IUsageProvider.GetUsageSnapshotAsync()
  -> provider adapter / ConfiguredUsageProvider
  -> HTTP, OAuth/Cookie или локальный источник
  -> UsageSnapshot
  -> formatter
  -> RaiseItemsChanged()
```

`UsageSnapshotCache` хранит последний результат и сериализует конкурентные обновления через один refresh gate. Поэтому overview, detail page и dock band используют общий snapshot.

## Codex

`CodexUsageService` сначала получает валидный access token через `CodexFileAuthTokenProvider`, затем вызывает `https://chatgpt.com/backend-api/wham/usage` через `CodexUsageClient`. Клиент проверяет схему ответа, имеет timeout/cancellation и повторяет transient-ошибки.

Если основной путь не работает, `CodexLocalSessionFallback` читает JSONL из `CODEX_HOME` (или `~/.codex`), `sessions` и `archived_sessions`, суммирует token events за 5 часов и 7 дней и возвращает оценку с `IsEstimate = true`. Базовые оценки задаются как 100 000 токенов на 5 часов и 500 000 на неделю.

## Настройки и реконфигурация

`TokensLimitsSettings` строит поля из `UsageProviderDescriptorRegistry`, загружает JSON settings, хранит секреты отдельно и публикует `Changed`. `TokensLimitsExtensionCommandsProvider` на это событие:

1. инвалидирует все кэши;
2. заново вычисляет список включённых провайдеров;
3. создаёт новые страницы и dock items при изменении состава;
4. запускает обновление поверхностей.

Интервал ограничен 30–3600 секундами, по умолчанию 60 секунд.

## Утилизация ресурсов

При остановке расширения порядок важен: сначала снимаются UI surfaces и таймеры, затем кэши, registry, настройки и принадлежащие `HttpClient`/Codex-сервисы. Новые disposable-компоненты должны быть включены в этот lifecycle и иметь idempotent `Dispose()`.
