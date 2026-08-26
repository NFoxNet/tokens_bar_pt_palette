# Tokens Limits Command Palette extension

Расширение PowerToys Command Palette показывает реальные лимиты и расход подключённых AI-провайдеров прямо в Dock.
Codex включён по умолчанию; остальные провайдеры включаются пользователем в стандартных настройках Command Palette.

## Возможности

- нативный Dock band с коротким видом `5ч\98%, 7д\69%`;
- подробная страница с оставшимся процентом, обратным отсчётом до сброса, тарифом и дополнительными окнами;
- автоматическое обновление без повторного открытия палитры;
- официальный Codex usage API как основной источник;
- локальные JSONL-сессии Codex как помеченный `Оценка` fallback;
- общий каталог из 69 провайдеров, синхронизированный с актуальным `CodexBar` `ProviderManifest`;
- настройки включения/выключения для каждого провайдера;
- provider-specific параметры: API key, Cookie, OAuth token, base URL, account/project/region или локальный путь;
- единый экран со всеми включёнными провайдерами и отдельные подробные страницы;
- реальные дополнительные метрики: использованные токены, баланс, кредиты, расходы и значения квот;
- кеширование snapshot и access-token, чтобы Dock и подробная страница не делали дублирующие запросы;
- настройка `Частота обновления` для общего refresh pipeline.

## Архитектура

`TokensLimitsExtension.Core` не зависит от WinUI и содержит общий контракт:

- `IUsageProvider` — получение `UsageSnapshot`;
- `UsageProviderDescriptor` — стабильный ID, имя, режим авторизации и provider-specific settings;
- `UsageProviderRegistry` — проверка уникальности и порядок провайдеров;
- `UsageProviderDescriptorRegistry` — полный ordered manifest всех поддерживаемых провайдеров;
- `ConfiguredUsageProvider` — общий HTTP/API adapter для остальных провайдеров;
- `UsageProviderEndpointCatalog` — provider-specific официальные API/web endpoint'ы и формы запросов;
- `UsageSnapshotCache` — TTL-кеш и single-flight refresh;
- `CodexUsageProviderAdapter` — адаптер существующей Codex-логики.

UI-слой создаёт для каждой включённой записи кеш, подробную страницу и `WrappedDockItem`. Корневой пункт открывает список
всех включённых провайдеров, а `GetDockBands()` возвращает их bands автоматически. Новый provider endpoint не требует
правок `CommandProvider` или Dock-кода.

## Поддерживаемые провайдеры

`Codex`, `OpenAI`, `Azure OpenAI`, `Claude`, `ClinePass`, `Cursor`, `OpenCode`, `OpenCode Go`, `Alibaba Coding Plan`,
`Alibaba Token Plan`, `Qwen Cloud`, `Droid`, `Fireworks`, `Gemini`, `Antigravity`, `Copilot`, `Devin`, `z.ai`, `MiniMax`,
`Manus`, `Kimi Code`, `Kilo`, `Kiro`, `Vertex AI`, `Augment`, `JetBrains AI`, `Moonshot`, `Amp`, `T3 Chat`, `Ollama`,
`Synthetic`, `OpenRouter`, `ElevenLabs`, `Warp`, `Windsurf`, `Zed`, `Perplexity`, `Xiaomi MiMo`, `Doubao`, `Sakana AI`,
`Abacus AI`, `Mistral`, `DeepSeek`, `DeepInfra`, `Codebuff`, `Crof`, `Venice`, `Command Code`, `Qoder`, `StepFun`,
`AWS Bedrock`, `Grok`, `Groq`, `LLM Proxy`, `LiteLLM`, `Deepgram`, `Poe`, `Chutes`, `Neuralwatt`, `ClawRouter`, `LongCat`,
`sub2api`, `Wayfinder`, `ZenMux`, `ai&`, `ZoomMate`, `xAI`, `Notion AI` и `IBM Bob`.

Для провайдеров, у которых API-контракт отличается от обычного JSON GET, используются отдельные адаптеры:

- `OpenAI` запрашивает административные `organization/costs` и `organization/usage/completions` дневными бакетами,
  поддерживает pagination, project ID и диапазон истории 1–365 дней (`OPENAI_HISTORY_DAYS`). Для организации нужен
  `OPENAI_ADMIN_KEY`.
- `Alibaba Coding Plan` отправляет официальный POST с commodity code и поддерживает регионы `intl`/`cn`.
- `Amp` поддерживает API (`AMP_API_KEY`) и веб-режим с `session` Cookie; ответ `displayText` разбирается в реальные
  free/subscription/credit метрики.
- `Windsurf` использует официальный protobuf `GetPlanStatus`; в настройках указывается session bundle с
  `devin_session_token`, `devin_auth1_token`, `devin_account_id` и `devin_primary_org_id`.
- `Zed` использует cloud profile API и требует пару `user ID` + access token (`ZED_USER_ID`/`ZED_ACCESS_TOKEN`).
- `Ollama` обращается к локальному `/api/tags` и показывает список установленных моделей; серверная квота для локального
  Ollama не выдумывается.

Codex-путь устроен так:

1. `CodexFileAuthTokenProvider` читает `%CODEX_HOME%\auth.json` и обновляет истёкший OAuth-токен штатным endpoint.
2. `CodexUsageClient` получает usage из `https://chatgpt.com/backend-api/wham/usage`, проверяет схему ответа и повторяет только временные `429/5xx` ошибки.
3. При недоступности API `CodexUsageService` читает локальные `sessions`/`archived_sessions`; такие значения явно помечаются как оценочные.

Чувствительные токены и полный JSON-ответ API в лог не записываются.

## Настройки

Настройки расширения доступны через стандартную страницу настроек Command Palette.

- `Частота обновления`: 30 секунд, 1 минута (по умолчанию), 5 минут или 15 минут.
- для каждого провайдера — отдельный toggle `Включить ...`;
- дополнительные поля появляются рядом с провайдером и соответствуют его источнику: API key, Cookie, OAuth token,
  base URL, account/project/region или путь к локальным данным.

По умолчанию включён только Codex. После изменения toggle Command Palette может потребовать перезагрузить расширение,
чтобы перечитать набор Dock bands.

Файл настроек хранится в каталоге, который возвращает `Utilities.BaseSettingsPath`, под именем `tokensLimits.settings.json`.
Секретные значения не попадают в `[TokensLimits]`-логи. Значения из настроек хранятся стандартным JSON-хранилищем
Toolkit; для API key также можно использовать указанную в настройке переменную окружения.

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

## Ограничения и честность данных

- Dock band позиционируется самим Command Palette; расширение использует только публичный нативный `GetDockBands()` и не вмешивается в положение всплывающего окна.
- Локальный fallback не знает серверные лимиты аккаунта и потому является оценкой; реальные проценты отображаются только при успешном ответе API.
- Для работы основного пути требуется действующая авторизация Codex в `%CODEX_HOME%\auth.json`.
- Провайдеры с закрытыми веб-кабинетами требуют ручного Cookie-заголовка; автоматический импорт браузерных сессий не
  используется как обход авторизации.
- Там, где API отдаёт только баланс/стоимость/число токенов и не публикует rolling quota, UI показывает эту реальную
  метрику, а не превращает тариф в выдуманный процент.
- Endpoint'ы отдельных провайдеров являются приватными web API и могут измениться самим провайдером; ошибка запроса
  отображается явно и записывается в лог без полного ответа и секретов.
