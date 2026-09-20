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
- `JsonLocalizationService` загружает упакованные и пользовательские JSON-словари; UI получает его через настройки, а Core зависит только от `ILocalizationService`.

Для одного провайдера создаются две независимые `TokensLimitsPage`: одна
принадлежит обычной навигации через overview, вторая — transient-навигации,
которую Command Palette запускает из Dock. Это намеренное разделение
жизненных циклов: страница, открытая из Dock, не должна переиспользоваться
обычным overview-элементом после закрытия палитры.

Dock представлен одним стабильным band с историческим ID Codex, чтобы уже
закреплённый пользователем элемент продолжил работать после обновления. Его
`TokensLimitsDockBandPage` динамически содержит `UsageDockBandItem` всех
включённых провайдеров; provider не становится отдельным закрепляемым band.

### Core layer

Папка `TokensLimitsExtension.Core/` не должна зависеть от UI:

- `Models/` содержит `UsageSnapshot`, `UsageWindow`, `UsageMetric` и Codex-специфичные модели.
- `Providers/` содержит `IUsageProvider`, дескрипторы, registry и конфигурацию. Общая маршрутизация и transport остаются в `ConfiguredUsageProvider.cs`; выделенные границы Kiro CLI, локального источника и Amp text parser находятся в `KiroUsageProviderAdapter.cs`, `LocalUsageProviderAdapter.cs` и `AmpUsageDisplayParser.cs`. Чистые JSON/XML-нормализаторы вынесены в `UsageJsonParser.cs`.
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
| `UsageMetric.SemanticKey` / `NumericValue` / `CurrencyCode` | Необязательная семантика для единообразных сводок, например подтверждённого баланса без смешения валют |
| `FetchedAt` / `Source` | Время получения и источник данных |

Для совместимости старого Codex-сервиса используется `CodexUsageProviderAdapter`, который преобразует `CodexUsageSnapshot` в общий контракт.

## Жизненный цикл запроса

```text
UsageRefreshCoordinator (one timer)
  -> UsageSnapshotCache.RefreshAsync()
  -> IUsageProvider.GetUsageSnapshotAsync()
  -> provider adapter / ConfiguredUsageProvider
  -> HTTP, OAuth/Cookie или локальный источник
  -> UsageSnapshot
  -> formatter
  -> RaiseItemsChanged()
```

`UsageRefreshCoordinator` владеет единственным периодическим расписанием. `UsageSnapshotCache` хранит последний результат, публикует безопасное UI-состояние и сериализует конкурентные обновления через один refresh gate. Поэтому overview, обычная detail page, Dock detail page и dock band используют общий snapshot, но не общий объект страницы и не имеют собственных таймеров. При временной ошибке старый snapshot остаётся доступен как устаревший; отключение провайдера отменяет его выполняющийся запрос. Detail page добавляет действия «обновить», «открыть кабинет» (только HTTPS из descriptor) и «скопировать безопасную диагностику»; ручное обновление проходит через тот же cache/coordinator и не создаёт отдельный сетевой запрос.

Обычная detail page, Dock detail page и dock item кэшируются отдельно по стабильному ID провайдера. При выключении провайдера их убирают из активных списков, а coordinator исключает cache из набора доступных для обновления и отменяет текущий запрос. Неактивные объекты снимают подписки на cache и язык; страница возвращает пустой список, а refresh для всех поверхностей ничего не запрашивает. При повторном включении те же объекты подписываются снова и синхронизируются с текущим cache. Число удерживаемых страниц и dock items ограничено каталогом провайдеров, а не числом переключений. Перестроение списков сериализовано, reentrant изменения настроек объединяются; после начала утилизации перестроение не публикует новые поверхности.

## Codex

`CodexUsageService` сначала получает валидный access token через `CodexFileAuthTokenProvider`, затем вызывает `https://chatgpt.com/backend-api/wham/usage` через `CodexUsageClient`. Auth-файл читается с пределом 1 MiB, а клиент проверяет схему ответа, ограничивает тело ответа настраиваемым лимитом (по умолчанию 1 MiB), имеет timeout/cancellation и повторяет transient-ошибки.

Если основной путь не работает, `CodexLocalSessionFallback` читает JSONL из `CODEX_HOME` (или `~/.codex`), `sessions` и `archived_sessions`, суммирует token events за 5 часов и 7 дней и возвращает метрики с `IsEstimate = true`. Локальный fallback не подставляет подтверждённую квоту, процент или искусственный reset: UI показывает исходные количества токенов и помечает их как оценку. Кэш читает только добавленные байты после завершённой строки, перечитывает файл после truncation/замены и пропускает строку длиннее 256 KiB потоково.

## Настройки и реконфигурация

`TokensLimitsSettings` строит поля из `UsageProviderDescriptorRegistry`, загружает JSON settings, хранит секреты отдельно и публикует общее `Changed` и адресное `ProviderConfigurationChanged` с набором provider ID. Для сравнения конфигурации используются process-local SHA-256 fingerprints отдельных провайдеров; открытые ключи и Cookie не удерживаются в общем fingerprint. Настройки и зашифрованные секреты всегда читаются и записываются в едином стабильном каталоге `%LOCALAPPDATA%\TokensLimitsExtension`. При первом запуске после обновления содержимое host/package-local каталога переносится туда, если там найден более новый secret store. При смене языка cache не инвалидируется; при смене ключа, аккаунта, URL или включения провайдера очищается только его старый snapshot до нового запроса. `TokensLimitsExtensionCommandsProvider` на событие настроек:

1. `UsageSnapshotCache` очищает snapshot только при изменении конфигурации провайдера;
2. заново вычисляет список включённых провайдеров;
3. обновляет набор активных кэшей в coordinator до публикации изменённого состава поверхностей;
4. собирает активные списки из кэшированных обычных страниц, отдельных Dock detail pages и dock items, сохраняя порядок registry;
5. новые detail pages сразу получают текущее состояние cache, а UI-поверхности дальше обновляются по его событиям без самостоятельного сетевого опроса.

Интервал ограничен 30–3600 секундами, по умолчанию 60 секунд.

## Утилизация ресурсов

При остановке расширения порядок важен: сначала снимаются UI surfaces и coordinator, затем кэши, registry, настройки и принадлежащие `HttpClient`/Codex-сервисы. Новые disposable-компоненты должны быть включены в этот lifecycle и иметь idempotent `Dispose()`.
