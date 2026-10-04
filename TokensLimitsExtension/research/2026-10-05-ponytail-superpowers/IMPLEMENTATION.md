# SDD ledger — plan: IMPLEMENTATION.md

## Принятый объём

Основание: аудит README.md и явное поручение пользователя реализовать изменения в отдельных worktree, затем свести в одну новую ветку с отдельными коммитами крупных шагов. Уточнение пользователя: «Исправления и готовые UI/UX; отложенные функции позже».

Реализуются F1–F5, S1–S4, U1–U5, Q1–Q3. U6 (избранные Dock) и Q4 (история/экспорт/уведомления) не входят в эту ветку. Новые секреты, реальные API-вызовы, установка/публикация пакета и push не нужны. Package identity, опубликованные IDs и версия релиза остаются прежними.

База: `2e6314a04d251f432ed182871831a71a34d97e3f`. Итоговая ветка: `codex/usage-ux-improvements`. Первичное рабочее дерево пользователя не используется для реализации; `readme_media/` и исходная папка аудита сохраняются. Аудит скопирован в integration worktree после проверки отсутствия destination; исходные evidence/probe привязаны к старому commit и не выдаются за проверку новой версии.

## Исполнители и границы

| Задача | Ветка | Изменение и самостоятельная проверка |
| --- | --- | --- |
| Refresh | codex/usage-refresh | Core cooldown для default/force, абсолютный RetryAfterUntil, timestamp; unit fake time |
| Formatter | codex/usage-formatting | Длительности окон/метрики/estimate; pure formatter unit tests |
| Transport | codex/usage-errors | Generic timeout и политика агрегации ошибок; stub HTTP state tests |
| Native UX | codex/native-usage-ux | Details/MoreCommands/status/busy/manual refresh/locales; integration object contracts |
| Composition/setup | codex/usage-setup | Все CODEX_HOME для fallback, первый для auth, native SettingsPage wiring; temporary-home tests |
| Azure/cleanup | codex/explicit-azure-validation | Явное connection validation, нулевой background inference; S1–S4 отдельным commit |
| Integration (Codex) | codex/usage-ux-improvements | План, документация, связь Azure с cache/UI, cherry-pick, общая проверка и review |

## Проверка зависимостей до интеграции

| Пересечение | Производитель → потребитель | Решение |
| --- | --- | --- |
| Refresh ↔ UX | State.RetryAfterUntil → отображение cooldown | Nullable init property без изменения positional ctor; UX cherry-picks Core dependency |
| Formatter ↔ UX | Compact summary → overview/Dock | Только Core formatter; UX не редактирует formatter; устаревшие expected labels интегрируются отдельно |
| Settings ↔ UX | Existing SettingsPage → optional overview command | ICommand? settingsCommand последним optional parameter; UX владеет overview, setup — wiring |
| Azure ↔ Transport | ConfiguredUsageProvider.cs | Azure — ранний специализированный branch/method, Transport — generic loop/aggregation; без полного форматирования файла |
| Azure ↔ Integration | Connection-validator capability → shared cache/manual UI | Отдельный Core interface; manual validation не обходит generation/disabled/cooldown; Core/UI связь проверяет root |
| Cleanup ↔ Composition | Fallback/settings | Cleanup только unread fields/идентичные placeholder; factory меняется отдельно |
| Все ↔ Tests | Новые fixtures/tests | Новые test files, не глобальное окружение/real settings/network |

Каждая задача согласована сама с собой: production behavior имеет targeted red/green тесты; механическая очистка проверяется существующим build/test без тестов, зеркалящих удаление. Documentation-only правки проверяются ссылками/путями. Переходы исходного COM/lifecycle не меняются.

## Решения

- Ruling: U6/Q4 отложены — прямой ответ пользователя; стоимость изменения scope позже — отдельная ветка.
- Ruling: settings не перестраиваются в новый 211-field wizard — аудит предлагал сначала native smoke; готовое изменение здесь открывает существующую SettingsPage без новой формы/secret storage.
- Ruling: Azure auto-refresh сообщает отсутствие quota/ручной validation, но не делает inference; manual validation возвращает только connection health, не ping tokens как account usage. Сохранены provider ID и поля.
- Ruling: полноценный Windows UI smoke и signed upgrade нельзя заменить integration tests; native computer automation сеанса отключена, installed package не трогаем. Непроверенные layout/host-specific поведения отмечаются в итоговой документации.
- Ruling: сборка/локальные commits разрешены пользователем; push, публикация, смена доверия сертификатов и обновление установленного пакета в задачу не входят.

## Ход выполнения

- Baseline: locked restore и Debug x64 test всей solution прошли: 119 unit + 22 integration.
- Jev: новый вызов доступен, TypeSafe / jev-1.13.0 / status=ok, advisory next_step=contract_first, confidence=0.95. route confidence=0.23, reliable=false; route не используется как разрешение. decision_id не возвращён. Контракты согласованы явно исполнителями.
- Первая интеграция: F1–F5/Core, F2/U5, F3, F4/U4 и S1–S4 сведены отдельными commits; Debug x64: 155 unit + 26 integration, все прошли.
- Перекрёстное review выявило потерю Retry-After при сочетании 429/401 и остановку таймера после смены интервала. Оба исправления интегрированы отдельными commits с RED/GREEN fake-time/stub regressions.
- Formatter review: actionable findings отсутствуют; неизвестные длительности используют локализованную роль окна. Native UX и общий Azure validation lifecycle ещё интегрируются.
- PowerShell helper suites: installer, release helpers и unregister прошли в отдельных процессах. Это stub-тесты: реальный PowerToys/AppX/сертификаты не изменялись.

## Финальная проверка

Порядок: независимая review scoped commits → cherry-pick в integration branch → тесты общих сценариев actual ICommand.Invoke/cache/disabled/locale → Debug x64 locked restore/build/test → PowerShell helper suites → Release x64/ARM64 build без подписи/установки → diff/status/новые артефакты → final whole-branch review и fixes отдельными commits.

Нельзя считать задачу завершённой по сообщениям исполнителей: итоговые counts и SHA записываются после локальной проверки интегрированной ветки. Старый Probe.ps1 воспроизводит дефекты исходного аудита и не является regression suite обновлённой версии.
