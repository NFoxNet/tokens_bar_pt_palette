# Tokens Limits: упрощение, функционал и UI/UX

Дата: 5 октября 2026, Asia/Bangkok. Проверенный commit: `2e6314a04d251f432ed182871831a71a34d97e3f`, ветка `master`, версия проекта `0.0.5.6`.

## Вывод

Проекту полезнее несколько точечных исправлений и улучшение нативных сценариев, чем перестройка архитектуры. Общий кэш, единый scheduler, отдельные жизненные циклы страниц Dock и обычной палитры, защищённые secrets и bounded readers уже решают реальные задачи. Самый заметный долг — расхождения между обещанным действием «Обновить», поведением кэша и показанным статусом.

Рекомендуемый порядок:

1. Исправить ручное обновление и cooldown, подписи окон в сводке, тип generic timeout и передачу нескольких `CODEX_HOME`.
2. Убрать подтверждённый мёртвый код и лишние записи версий; устранить путаницу рабочих каталогов в инструкции сборки.
3. Улучшить нативный overview/details/settings: видимые состояния, быстрые действия, полезная панель деталей и понятный путь к настройке.
4. Отдельно решить судьбу автоматического Azure OpenAI validation-запроса. Историю, прогнозы и уведомления отложить до проверки потребности и качества исходных данных.

Выполненный анализ не является внедрением этих рекомендаций. Продуктовые исходники, настройки, MSIX-регистрация, ветка и коммиты не изменены. Добавлена только эта папка отчёта. Изначальная пользовательская папка `../readme_media/` сохранена.

## Как получены выводы

Ponytail Audit использован только для сложности и сокращений. Superpowers — для изучения текущих сценариев, независимых проходов Core/UI/сборки и проверки выводов. Для выбора следующего шага вызван Jev: после отклонения первого вызова из-за формата candidate ID исправленный вызов вернул `status=unavailable`, `provider=TypeSafe`, `advisory=true`, причину `local integration error`. Успешного Jev-решения или `decision_id` нет; приоритеты ниже — инженерная оценка Codex по проверенным источникам.

Изучены `AGENTS.md`, README Git-репозитория, документы `doc/`, инструкции/skills Command Palette, Core, UI, четыре проекта, тесты, родительские scripts и CI/release workflows. В Git отслеживаются 62 C#-файла и 4 проекта. Каталог содержит 69 провайдеров, 140 provider-specific полей и 69 переключателей; вместе с языком и интервалом настройки регистрируют 211 элементов. Это количество зарегистрированных полей, а не измерение размеров экрана или времени заполнения.

Проверки: locked restore, Debug x64 build с 0 предупреждений/ошибок, 119 unit + 22 integration tests, три PowerShell helper suites. Дополнительные локальные probes на синтетических данных подтвердили шесть проблемных сценариев. Подробности и ограничения — в [VERIFICATION.md](VERIFICATION.md); воспроизводимый скрипт — [Probe.ps1](Probe.ps1); список задач — [backlog.csv](backlog.csv).

**Обозначения:** «подтверждено» означает источник и/или выполненный локальный probe; «предложение» — изменение поведения, которое ещё надо принять и проверить. P1 — функциональная корректность или нежелательные запросы; P2 — удобство/сопровождение; P3 — необязательное развитие. Никакое замечание о компоновке не выдаётся за увиденный дефект в работающем PowerToys.

## Ponytail: что действительно можно сократить

Один пункт — одна конкретная замена; ранжирование по размеру сокращения. Пути ниже отсчитываются от каталога solution, где лежит этот отчёт в `research/`.

- **S1 — delete:** удалить неиспользуемый `UsageRefreshHelpers` целиком; замена не нужна, refresh уже принадлежит cache/coordinator. [`TokensLimitsExtension/UsageRefreshHelpers.cs`](../../TokensLimitsExtension/UsageRefreshHelpers.cs), строки 1–50. Полный поиск по дереву, скрытым файлам, тестам и scripts нашёл только определения и внутренний вызов helper; старый coverage XML содержит имена методов как метаданные, а не runtime-вызовы. Класс `internal`, COM entrypoint отсутствует. Потенциал: **−50 строк**, 0 зависимостей.
- **S2 — delete:** убрать семь неиспользуемых `PackageVersion`; замена не нужна. [`Directory.Packages.props`](../../Directory.Packages.props), строки 8–10, 12, 14, 16–17. Это `Microsoft.CodeAnalysis.NetAnalyzers`, `Microsoft.Web.WebView2`, `Microsoft.Windows.CsWin32`, `Microsoft.Windows.SDK.BuildTools`, `Microsoft.WindowsAppSDK`, `StyleCop.Analyzers`, `System.Text.Json`. В текущих `PackageReference` и lock-графах они не используются. Сохранить реально используемые SDK/MSIX/WinRT-пакеты. Потенциал: **−7 XML-строк**, **0 удалённых установленных зависимостей**.
- **S3 — delete:** удалить два private-поля `_fiveHourLimitTokens`/`_weeklyLimitTokens` и присваивания; замена не нужна. [`CodexLocalSessionFallback.cs`](../../TokensLimitsExtension.Core/Services/CodexLocalSessionFallback.cs), строки 23–24, 48–49. Чтений нет. Публичные параметры, `Limits`, constructors и совместимость оставить. Потенциал: **−4 строки**.
- **S4 — shrink:** заменить `field.IsSecret ? string.Empty : string.Empty` на `string.Empty`. [`TokensLimitsSettings.cs`](../../TokensLimitsExtension/Settings/TokensLimitsSettings.cs), строки 269–271. Обе ветки одинаковы; последующая локализованная настройка placeholder остаётся. Потенциал: **−2 строки**.

`net: -63 lines, -0 deps possible.` Это сумма текущих исходных строк для предложенных замен, а не применённый diff и не обещание ускорения runtime. После внедрения: locked restore, Debug build и существующие тесты; результаты нынешней сборки не доказывают корректность ещё не сделанного удаления.

Дополнительные кандидаты, которые сейчас не стоит смешивать с безопасной очисткой:

- `FormatCompactBandSubtitle` (`UsageDisplayFormatter.cs:95–100`) не имеет потребителей внутри дерева, но является публичным Core API. Удаление требует отдельной проверки внешней совместимости; в −63 строки не включено.
- `GetRetryAfter` в `ConfiguredUsageProvider.cs:258–272` и `CodexUsageClient.cs:328–342` совпадает. Общий helper может сократить около 12–14 строк после учёта самого helper, но новая точка связывания ради такой экономии необязательна. Лучше объединить при следующем изменении политики Retry-After.
- **Coverlet сохранить.** В CI сбор coverage не включён, однако в локальных `TestResults/` уже есть coverage XML. Отсутствие `--collect` в workflow не доказывает ненужность зависимости для пользователя.
- `ConfiguredUsageProvider` имеет 3 078 строк, но обслуживает разные протоколы и источники: JSON, Cookie, CLI, локальные файлы, GraphQL/tRPC, protobuf, сбор нескольких страниц/проектов. Разбивка файла может облегчить навигацию; сама по себе она не сокращает сложность. Общий transport и parser уже существуют. «Один класс на каждый из 69 провайдеров» сейчас не обоснован.
- Интерфейсы с одной production-реализацией здесь часто являются тестовыми швами. Bounded readers, cancellation generations, отдельные Dock pages, stable IDs и проверки подписанного MSIX не являются лишними слоями.

## Функциональные находки

### F1 · P1 · Ручное обновление не соответствует действию и обходит cooldown

**Подтверждено:** `TokensLimitsPage.cs:64–67,236–242` вызывает обычный `RefreshAsync`; coordinator вызывается без `force: true`. `UsageSnapshotCache.cs:247–257` возвращает свежий кэш без запроса, а `UsageRefreshCoordinator.cs:104–115` проверяет cooldown только для `force`.

Probe: первый запрос → 1 вызов; повторный default refresh → по-прежнему 1; forced refresh → 2. Другой probe после 429 с Retry-After 120 секунд: forced refresh оставляет 1 вызов, default refresh увеличивает число до 2. Таким образом, нынешнее ручное действие может ничего не делать в пределах TTL и повторно обращаться к rate-limited провайдеру во время cooldown. Существующий `ForceRefreshDoesNotBypassRetryCooldown` покрывает именно force-путь, а не команду страницы.

**Минимальное решение:** ручной запрос пропускать через общий coordinator в force-режиме; обычные startup/automatic calls сохранить. При блокировке cooldown показывать причину и время следующей попытки. Само исправление не должно создавать второй HTTP-путь или таймер. На архитектурном уровне стоит решить, должен ли cooldown запрещать любой внешний dispatch, а не только force.

**Приёмка:** вызвать именно `ICommand.Invoke()` строки «Обновить» при свежем snapshot; provider counter увеличивается на один. Повторные одновременные действия разделяют одну task. Во время 429 cooldown counter не меняется; после срока запрос разрешён. Данные остаются на экране, запрос выключенного провайдера невозможен.

### F2 · P1 · Сводка подписывает чужие окна как Codex

**Подтверждено:** `UsageDisplayFormatter.cs:32–59` жёстко ставит «5ч / 7д», хотя `UsageWindow.LimitWindowSeconds` несёт действительную длительность. Эту строку используют Dock и overview. Detail для non-Codex уже умеет `GetWindowLabel`. Parser создаёт, например, 4-часовые и 30-дневные окна (`UsageJsonParser.cs:461,472`).

Probe для 30-дневного окна с 30% used: правильная метка helper — `30д`, текущая Dock-строка — `5ч\70%, 7д\—`. Это ошибка подписи времени, не расчёта процента.

**Минимальное решение:** переиспользовать `GetWindowLabel` для существующих окон; не рисовать второе окно, если его нет; для неизвестной длительности использовать нейтральную локализованную подпись. Не выводить предположительные лимиты.

**Приёмка:** 1ч, 4ч, 5ч, 24ч, 7д, 30д; только secondary; неизвестная длительность; metrics-only; EN/RU. Detail, overview и Dock согласованы.

### F3 · P1 · Generic timeout превращается в другую ошибку

**Подтверждено:** `ConfiguredUsageProvider.cs:228–249` создаёт timeout exception без `FailureKind.Timeout`; при агрегации тип также не сохраняется. Classifier в `UsageProviderState.cs:80–90` разрешает такую цепочку как `UnsupportedResponse`. Specialized deadline, напротив, выставляет Timeout (`ConfiguredUsageProvider.cs:3003–3011`).

Stub probe одного generic endpoint `aiand`, зависшего при чтении тела: cache state = `UnsupportedResponse`, ожидаемый тип = `Timeout`. В дополнительном сценарии DeepSeek с заданным API key и без Cookie cache получил `MissingConfiguration`: последующие пропущенные Cookie-endpoints вытеснили реальный timeout первого запроса. Не утверждается, что любой таймаут всегда превращается ровно в UnsupportedResponse.

**Минимальное решение:** сохранить typed cause deadline и выбрать явную политику агрегации. Когда пригодный настроенный источник пытался отвечать, его transport failure не должен автоматически уступать отсутствию credentials у необязательной альтернативы. Аналогично проверить смесь 429 и других ошибок; правило сохранения Retry-After должно быть выражено тестом.

**Приёмка:** один generic timeout → Timeout; apiKey-only DeepSeek timeout → transport status, а не предложение заполнить необязательный Cookie; caller cancellation остаётся cancellation. Проверить state/diagnostics и scheduler backoff, а не только `exception.Message`. Существующий generic timeout test проверяет текст сообщения.

### F4 · P1 · Несколько CODEX_HOME теряются при сборке сервиса

**Подтверждено по источнику:** `doc/operations.md:17` разрешает список путей через запятую; `CodexLocalSessionFallback.cs:44–46,82–86` умеет их обходить. Но `TokensLimitsExtensionCommandsProvider.cs:505–518` оставляет первый путь и передаёт только его fallback. Сессии остальных home не входят в runtime-оценку.

**Минимальное решение:** оставить первый home для `auth.json`, а fallback передавать полный нормализованный список. Это не multi-account quota aggregation: локальные token counts остаются явно помеченной оценкой. Реальные auth/session файлы в ходе аудита не читались.

**Приёмка:** тест с двумя временными home и синтетическими JSONL через тот путь композиции, который использует default factory; отдельно проверить пустую переменную, whitespace и прежний single-home сценарий. Core-теста напрямую на fallback недостаточно для проверки фабрики.

### F5 · P1 · Azure validation делает inference в регулярном usage refresh

**Подтверждено по источнику:** endpoint `azureopenai` в `UsageProviderEndpoint.cs:64–72` — `POST .../chat/completions` с сообщением `ping` и `max_tokens:1`. Descriptor прямо описывает проверку доступности deployment, а не получение account quota (`UsageProviderDescriptorRegistry.cs:132–145`). Включённый провайдер проходит обычный polling scheduler; отдельной ручной opt-in команды validation в текущем UI нет.

Это вызов генерации при просмотре лимитов. Он может расходовать квоту/оплачиваемые ресурсы согласно условиям конкретного deployment; стоимость и live Azure-поведение не проверялись. Tokens в таком ответе описывают пробный запрос и не должны выглядеть как расход всего аккаунта.

**Предложение:** сохранить provider ID/настройки, вынести «Проверить подключение» в явное нативное ручное действие; автоматический usage refresh не должен отправлять inference probe. Показывать «квота этим источником не предоставляется». Не изобретать Azure usage API или фальшивый процент.

**Приёмка:** stub handler после включения и нескольких scheduler ticks фиксирует 0 chat-completion POST; явное validation-действие делает 1 и понятно подписывает результат. Для будущего account-usage источника нужна отдельная проверка официального API и scope permissions.

## UI/UX в пределах Command Palette

Это Windows extension, а не отдельный сайт. Подтверждённые средства: нативные `ListPage`, `ListItem.Details`, `Tags`, `MoreCommands`, `CommandContextItem.RequestedShortcut`, `Settings.SettingsPage`, `Page.IsLoading`, один стабильный Dock band с несколькими элементами. Microsoft описывает эти поверхности в документации [ListPage](https://learn.microsoft.com/en-us/windows/powertoys/command-palette/microsoft-commandpalette-extensions-toolkit/listpage), [ListItem](https://learn.microsoft.com/en-us/windows/powertoys/command-palette/microsoft-commandpalette-extensions-toolkit/listitem), [CommandItem](https://learn.microsoft.com/en-us/windows/powertoys/command-palette/microsoft-commandpalette-extensions-toolkit/commanditem), [CommandContextItem](https://learn.microsoft.com/en-us/windows/powertoys/command-palette/microsoft-commandpalette-extensions-toolkit/commandcontextitem), [Settings](https://learn.microsoft.com/en-us/windows/powertoys/command-palette/microsoft-commandpalette-extensions-toolkit/settings) и [Dock support](https://learn.microsoft.com/en-us/windows/powertoys/command-palette/adding-dock-support). Доступность ShowDetails, IsLoading, MoreCommands, RequestedShortcut и SettingsPage дополнительно проверена reflection по локально собранному Toolkit.

Host определяет компоновку, темы, фокус и отображение Dock. Внешняя документация не гарантирует одинаковое поведение всех версий PowerToys или fork Toolkit. Предложения ниже используют существующие поверхности; изменения host, произвольный XAML/HTML dashboard и собственный рендер графиков не требуются.

### U1 · P2 · Краткая причина ошибки уже в overview

`UsageOverviewPage.cs:161–165` объединяет ошибки без snapshot в «Limits unavailable»; detail различает MissingConfiguration, Authentication, RateLimited, Network/Timeout и UnsupportedResponse (`TokensLimitsPage.cs:275–295`).

**Предложение:** короткие локализованные статусы «Настроить», «Войти», «Повтор через …», «Нет сети», «Ответ изменился» в subtitle/tag. Подробный следующий шаг — в Details. Переиспользовать существующий error enum, не разбирать текст exception.

**Приёмка:** stub каждого error kind; причина различима в EN/RU до открытия detail. Изменение статуса сохраняет выбранную строку и не создаёт сетевых запросов из GetItems.

### U2 · P2 · Видимое обновление и свежесть данных

`TokensLimitsPage.cs:133–149` и `UsageDockBandItem.cs:117–130` при существующем snapshot не показывают IsRefreshing; «последнее успешное обновление» появляется в detail только при stale (`TokensLimitsPage.cs:223`). Кэш уже хранит необходимые timestamps. Факт отсутствия этих веток подтверждён; оценка ощущения «действие не сработало» — UX-гипотеза.

**Предложение:** detail использует IsLoading/короткий статус «Обновляю…», значения остаются доступны. В Details показать время получения всегда. Retry-After обозначать до конкретного срока; текущий TimeSpan в state сам по себе не является уменьшающимся countdown. Для одного периодического обновления не стоит раздувать каждую Dock-строку анимацией.

**Приёмка:** заблокированный stub с предыдущим snapshot, завершение успехом/ошибкой/cancel; данные не исчезают, busy заканчивается, timestamp меняется только после успеха. Время в UI обновляется с существующим расписанием, без таймера на каждую поверхность. Stale и «давно получено» не смешиваются: текущий `IsStale` означает snapshot + ошибка, а не простое истечение TTL.

### U3 · P2 · Полезные Details и действия рядом с провайдером

Overview и detail включают ShowDetails (`UsageOverviewPage.cs:43`, `TokensLimitsPage.cs:49`), но их `ListItem` не заполняют Details. Actions в detail добавляются после всех метрик (`TokensLimitsPage.cs:209–254`); parser допускает до 32 метрик в одном ответе и до 64 при merge. Реальное отступание/обрезание текста хостом не проверялось.

**Предложение:** короткий список показывает текущую квоту/баланс/состояние; Details раскрывает source, plan, timestamp и значения выбранной метрики. Добавить MoreCommands на строку провайдера: обновить, открыть кабинет, скопировать безопасную диагностику; предложить shortcut для refresh с проверкой конфликтов host. Существующие строки действий сначала оставить для discoverability.

**Приёмка:** действия доступны с клавиатуры без прокрутки длинного списка; source по-прежнему очищен, dashboard только descriptor HTTPS, diagnostics без credentials. После смены языка и данных обновляются Details/MoreCommands и их render signature; стабильность объектов и фокуса сохраняется. Не подключать обновление напрямую к сети в GetItems.

### U4 · P2 · Первый запуск и настройки 211 элементов

Empty overview использует NoOpCommand и лишь просит включить провайдеры (`UsageOverviewPage.cs:144–148`). `TokensLimitsSettings.cs:248–280` регистрирует весь каталог независимо от enabled. Это установленные свойства источника; текущая визуальная компоновка и неудобство длинного экрана не измерялись.

**Минимум:** открываемое действие «Настройки Tokens Limits», использующее уже существующую SettingsPage, и точная инструкция входа через настройки расширения. Native API для страницы есть; фактическую навигацию/возврат нужно проверить в host. Для MissingConfiguration прямо указать нужное поле/тип credentials без его значения.

**Следующий шаг только после визуального smoke:** сохранить обычный JsonSettingsManager и дать выбор провайдера/быстрый вход в соответствующие настройки. Возможность секций, сворачивания или локальной provider-form в используемом Toolkit проверить отдельно; прямое скрытие полей выключенного провайдера не предлагается, потому что мешает предварительной настройке. Не дублировать secret storage и не менять существующие settings keys.

**Приёмка:** all-disabled → понятный путь в настройки → включить один provider → увидеть его без перезапуска процесса. Поля связаны с нужным провайдером; EN/RU, неправильный интервал, пустые credentials и секретная маска проверены в реальном host.

### U5 · P2 · Правила выбора компактной сводки

`UsageDisplayFormatter.cs:40–54` всегда отдаёт предпочтение totalBalance; окно при его наличии не попадает в строку. Metrics-only summary берёт первые две метрики, поэтому порядок payload может определять полезность сводки. Кроме того, ранний totalBalance-return теряет `estimatePrefix`: синтетический IsEstimate=true snapshot выводит `Total balance: 12 USD` без пометки. В текущих штатных balance-адаптерах такая estimate-комбинация не подтверждена; это воспроизведённый пробел контракта formatter.

**Предложение:** для metrics-only провайдера оставить баланс приоритетным; при наличии quota-window показать окно и, если помещается, короткий баланс. Явные semantic keys использовать раньше случайного порядка JSON. Пометку оценки сохранять во всех ветках. Overview может показывать больше текста, чем Dock, но значения и смысл должны совпадать. Сначала исправить F2, затем выбирать presentation policy.

**Приёмка:** DeepSeek balance сохраняет нынешний смысл; combined windows+balance, token estimate, длинная metric, разные валюты, absent value. Валюты не складываются; unknown не становится нулём или процентом.

### U6 · P3 · Dock: видимость отдельно от включения провайдера

Сейчас enabled list определяет и overview, и состав одного band. При большом числе выбранных провайдеров полезен отдельный opt-in выбор «Показывать в Dock»/порядок избранных. Не нужен новый band для каждого провайдера: Microsoft описывает multi-button band как атомарную группу ([Dock support](https://learn.microsoft.com/en-us/windows/powertoys/command-palette/adding-dock-support)).

Это **предложение функции**, не обнаруженный дефект. Внедрять только если реальный Dock с несколькими используемыми провайдерами неудобен. Сохранить band/command/provider IDs, по умолчанию прежний состав, отдельную transient detail page и возможность открыть остальные через overview. Проверка: upgrade старых settings/pin, клавиатурная и Dock-навигация, повторное включение без роста retained pages.

## Сопровождение и функциональное развитие

**Q1 · P2 — рабочий каталог в документации.** Git root находится на уровень выше solution: `tokens_bar_pt_palette`, solution — `TokensLimitsExtension/`. `doc/development.md:14–26` говорит запускать из корня, но .NET/helper commands рассчитаны на solution cwd; sampler в `:48–52` использует Git-root пути. Указать два каталога явно; при выборе solution cwd sampler = `..\scripts\Measure-ExtensionBaseline.ps1`, output = `.\codex_docs\baseline-live.json`. Не менять скрипты ради исправления инструкции. Проверка путей через Test-Path из объявленного cwd достаточна.

**Q2 · P2 — тестировать выходной пользовательский контракт.** 69 descriptors и endpoint catalog не равны 69 проверенным live интеграциям. Тест MirrorsTheCurrentCodexBarManifest проверяет каталог; generic timeout test сейчас проверяет сообщение. Добавить focused tests для команды manual refresh, formatter окон, error-state aggregation и composition CODEX_HOME из F1–F4. При следующем изменении конкретного adapter — успешный/ошибочный synthetic fixture этого пути. Не создавать сотни тестов, повторяющих таблицу URL, и не включать интернет в unit tests.

**Q3 · P2 — честная карта возможностей.** В `doc/providers.md` полезно различать account quota, balance/cost, локальную оценку, health/model catalog, источник API/web/CLI и необходимые scopes. Например, Azure validation и Ollama model catalog не отвечают на тот же вопрос, что Codex quota. Это улучшает ожидания пользователя и помогает выбрать допустимую summary policy без фальшивых процентов. Новая абстракция capability registry не нужна, пока таблицы документации достаточно.

**Q4 · P3 — история/экспорт/уведомления.** Возможны opt-in локальная история snapshots и безопасный CSV/JSON export через нативные команды. Сначала установить конкретный сценарий: «что изменилось за неделю» или «предупредить перед исчерпанием». Ограничить retention, не сохранять auth/raw responses, отдельно хранить estimate/source/time и не суммировать валюты. Прогноз reset/остатка допустим лишь при достаточных наблюдениях и всегда с меткой оценки. Постоянная БД, background service, собственный dashboard и notifier framework для текущего запроса не обоснованы.

## Предлагаемый нативный сценарий

Это текстовый эскиз предлагаемого поведения, не скриншот работающего расширения. Числа и статусы условные. Цель: overview быстро отвечает «где осталось и что делать», detail объясняет происхождение.

```text
Tokens Limits
  Codex       5ч: 62% осталось · 7д: 88% осталось
  DeepSeek    Баланс: 12 USD
  Claude      Войти — проверить credentials
  Настройки Tokens Limits

More actions выбранного провайдера:
  Обновить · Открыть кабинет · Безопасная диагностика

Details выбранного провайдера:
  Источник · Тариф · Получено в … · Следующая попытка в …
```

Полные значения остаются в native Details/списке. У Dock короткая строка; смысл процента явно «осталось». Причина ошибки доступна текстом, а не только цветом. На телефоны это Windows-расширение не переносится: аналог responsive-проверки здесь — узкая палитра, высокий DPI и горизонтальный/вертикальный Dock.

## Приёмка в PowerToys перед внедрением UI

В этом аудите live PowerToys не разворачивался: native computer automation в текущем сеансе отключена. Unit/integration tests не доказывают фактическую геометрию, фокус или transient navigation в host. Для будущих изменений нужна следующая проверка на development-устройстве или через безопасное signed in-place обновление согласно документации:

| Сценарий | Что проверить |
| --- | --- |
| Первое открытие; все providers выключены | Есть действие настройки, нет бесконечной загрузки |
| Keyboard-only | Открытие, возврат, More actions, refresh, копирование; выбор не скачет |
| 100/150/200% DPI; узкое окно | Значения/статусы доступны, длинные строки имеют полное раскрытие |
| Horizontal/vertical Dock; несколько providers | Компактность, отличимые icons/labels, полный detail по активации |
| Refresh с данными; offline; 401; 429 | Last-known values, busy/error/next-at; cooldown нельзя обойти |
| EN/RU; смена языка | Все новые строки в отдельных `lang/en.json`/`lang/ru.json`, parity; сеть не вызывается |
| Disabled/re-enabled; смена credentials | Исчезновение активной поверхности; старые account data очищены |
| Dock → detail → закрыть → обычная палитра | Root обычной палитры; отдельная transient page; нет роста объектов |
| Upgrade | Stable IDs, Dock pin и защищённые provider secrets сохранены |

Release x64/ARM64, install/upgrade, COM shutdown, live allocations и фактические API в этот аудит не входят. Их существующие gates сохраняются; Debug build не заменяет MSIX/host acceptance.

## Очередность небольших патчей

| Этап | Содержание | Критерий результата |
| --- | --- | --- |
| A | F1–F4 отдельными focused исправлениями | Команда делает правильный запрос, таймаут типизирован, окна/источники честны |
| B | F5 и S1–S4; Q1 | Нет background inference validation; удалены доказанные остатки; пути документации однозначны |
| C | U1–U5 и Q2–Q3 | Нативные статусы/действия/setup понятны; есть host smoke и EN/RU проверки |
| D | U6/Q4 при подтверждённой потребности | Пользовательский сценарий оправдывает новую настройку/хранилище |

Не требуется переписывать COM hosting, менять GUID/identity, добавлять self-repair установки или копировать большой внешний UI. Самый большой ожидаемый выигрыш сейчас — соответствие показанных данных и действий реальному состоянию провайдера.
