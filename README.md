# Tokens Limits Command Palette extension

Расширение PowerToys Command Palette показывает оставшиеся лимиты Codex прямо в Dock и открывает подробную страницу по нажатию.

## Возможности

- нативный Dock band с коротким видом `5ч\98%, 7д\69%`;
- подробная страница с оставшимся процентом, обратным отсчётом до сброса, тарифом и дополнительными окнами;
- автоматическое обновление без повторного открытия палитры;
- официальный Codex usage API как основной источник;
- локальные JSONL-сессии Codex как помеченный `Оценка` fallback;
- кеширование snapshot и access-token, чтобы Dock и подробная страница не делали дублирующие запросы;
- заготовка настроек с пунктом `Частота обновления`;
- мультипровайдерный контракт: новый провайдер добавляется в Core registry и получает тот же Dock/page pipeline.

## Архитектура

`TokensLimitsExtension.Core` не зависит от WinUI и содержит общий контракт:

- `IUsageProvider` — получение `UsageSnapshot`;
- `UsageProviderDescriptor` — стабильный ID и отображаемое имя;
- `UsageProviderRegistry` — проверка уникальности и порядок провайдеров;
- `UsageSnapshotCache` — TTL-кеш и single-flight refresh;
- `CodexUsageProviderAdapter` — адаптер существующей Codex-логики.

UI-слой создаёт для каждой записи реестра кеш, подробную страницу и `WrappedDockItem`. Поэтому новый провайдер не требует правок `CommandProvider` или Dock-кода.

Codex-путь устроен так:

1. `CodexFileAuthTokenProvider` читает `%CODEX_HOME%\auth.json` и обновляет истёкший OAuth-токен штатным endpoint.
2. `CodexUsageClient` получает usage из `https://chatgpt.com/backend-api/wham/usage`, проверяет схему ответа и повторяет только временные `429/5xx` ошибки.
3. При недоступности API `CodexUsageService` читает локальные `sessions`/`archived_sessions`; такие значения явно помечаются как оценочные.

Чувствительные токены и полный JSON-ответ API в лог не записываются.

## Настройки

Настройки расширения доступны через стандартную страницу настроек Command Palette. Сейчас есть один параметр:

- `Частота обновления`: 30 секунд, 1 минута (по умолчанию), 5 минут или 15 минут.

Файл настроек хранится в каталоге, который возвращает `Utilities.BaseSettingsPath`, под именем `tokensLimits.settings.json`. В будущем новые `ChoiceSetSetting`/`TextSetting` можно добавлять в `TokensLimitsSettings`, не меняя провайдеры и Dock.

## Сборка и тесты

Из корня репозитория:

```powershell
dotnet restore .\TokensLimitsExtension\TokensLimitsExtension.sln
dotnet test .\TokensLimitsExtension\TokensLimitsExtension.sln -p:EnableMsixTooling=false
dotnet build .\TokensLimitsExtension\TokensLimitsExtension.sln -p:EnableMsixTooling=false
```

Для локальной установки и регистрации расширения:

```powershell
.\build-and-deploy.ps1
```

Скрипт останавливает связанные процессы, публикует выбранные `Configuration`/`Platform`, регистрирует ровно опубликованный `AppxManifest.xml` и проверяет наличие установленного пакета. После регистрации перезапустите или перезагрузите расширения Command Palette через PowerToys, если хост не подхватил обновление автоматически.

## Ограничения

- Dock band позиционируется самим Command Palette; расширение использует только публичный нативный `GetDockBands()` и не вмешивается в положение всплывающего окна.
- Локальный fallback не знает серверные лимиты аккаунта и потому является оценкой; реальные проценты отображаются только при успешном ответе API.
- Для работы основного пути требуется действующая авторизация Codex в `%CODEX_HOME%\auth.json`.
