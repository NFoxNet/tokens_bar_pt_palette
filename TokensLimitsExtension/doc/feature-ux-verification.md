# Проверка функциональной ветки

Ветка: `codex/feature-ux-development`. Основа: `0ea3a0c` (v0.0.5.9). Функциональный этап завершён без публикации и установки. Для локальных установочных пакетов версия обновлена до `0.0.6.0`; публикация и проверка обновления установленного пакета остаются отдельными шагами.

## Объём

Персональный Dock; помощник подключения; опциональная нативная сводка; пороговые уведомления; ограниченная локальная история; прогноз темпа для подтверждённых фиксированных окон. Сводка, уведомления и история выключены по умолчанию. Реализация выполнена отдельными worktrees и сведена последовательными коммитами.

## Проверки

Первый полный прогон продукта `649eb99`: restore прошёл; Debug x64 build — 0 предупреждений/ошибок; 257 Core +116 integration tests прошли. Финальный продукт `2e43776`: независимый restore прошёл; Debug x64 build — 0 предупреждений/ошибок; **257 Core +141 integration =398 тестов прошли**, 0 failures/skips.

Независимые проверки подсистем закрыли ошибки выбора показателя, требований авторизации/Azure, времён истории, границ уведомлений, видимости ошибок записи и повторного появления очищенного снимка. Финальное ревью всей ветки `a0f8784` обнаружило два важных случая: прогноз предыдущей серии после смены аккаунта/конфигурации и сохранение прогноза после устаревания/ошибки. Оба закрыты единой волной `2e43776`; scoped re-review дал PASS, 0 Critical/Important/Material Minor осталось. Новые тесты сначала дали 17 ожидаемых поведенческих сбоев без ошибок компиляции; после исправления 34 профильные проверки прошли.

## Границы проверки

Тесты используют фиктивные HTTP handlers, провайдеры, часы и временные каталоги. Реальные credentials и ответы API не использовались. Live PowerToys/flyout/toast/COM, ARM64 и обновление установленного MSIX с пользовательскими настройками не проверены. Мобильный интерфейс к этому расширению Windows не применим. Реальные payloads с подтверждёнными фиксированными границами не проверены, поэтому доступность прогноза зависит от провайдера.

Исходный checkout сохранён: ветка `master`, те же несвязанные `TokensLimitsExtension/research/` и `readme_media/`. Коммиты выполнялись с явным списком файлов. Финальная ветка предназначена для отдельной ручной проверки в PowerToys по [сценариям](feature-ux.md).

## Основные изменённые файлы

| Область | Файлы |
| --- | --- |
| Dock и UI | `UsageDockBandItem.cs`, `UsageSummaryPage.cs`, `ProviderSetupPage.cs`, `UsageHistoryPage.cs`, `UsageOverviewPage.cs` |
| Интеграция и настройки | `TokensLimitsExtensionCommandsProvider.cs`, `.Features.cs`, `TokensLimitsSettings.cs`, `.Features.cs` |
| Core | `UsageHistoryEntry.cs`, `UsageHistoryStore.cs`, `UsageHistoryController.cs`, `UsagePaceEstimator.cs`, `UsageNotificationEvaluator.cs`, `UsageDisplayFormatter.cs` |
| Данные провайдеров | `UsageSnapshot.cs`, `CodexUsageSnapshot.cs`, `CodexUsageService.cs`, `CodexUsageProviderAdapter.cs`, `UsageJsonParser.cs` |
| Локализация | `LocalizationKeyCatalog.cs`, `lang/en.json`, `lang/ru.json` |
| Проверки | Core/IntegrationTests: история, темп, уведомления, подключение, Dock, настройки, локализация и композиция |
| Документация | `doc/README.md`, `architecture.md`, `operations.md`, `providers.md`, `feature-ux.md`; утверждённая спецификация и план в `docs/superpowers/` |

Прогноз теперь проверяется по текущему сегменту и подтверждённым границам окна. При ошибке, оценке, смене источника/аккаунта или истечении свежести он скрывается даже до новой записи. Отображение использует сохранённые в памяти числовые строки: чтение страницы не обращается к провайдеру или файлам и не меняет частоту записи.

## Подготовка установочных пакетов 0.0.6.0

Версия синхронизирована в manifest и проекте. Release x64/ARM64 собраны с оптимизацией и подписаны существующим сертификатом `CN=NFoxNet`; закрытый ключ не экспортировался. Сериализация истории переведена на существующий в проекте паттерн generated JSON context, устраняющий IL2026 при trimming без изменения формата данных.

После исправления Debug x64 solution tests: 257 Core +141 integration =398 passed, 0 failures/skips. Offline проверки установщика прошли. Для обоих MSIX `Test-ReleasePackage.ps1` подтвердил версию `0.0.6.0`, архитектуру, Publisher, точный сертификат, CMS-подпись, manifest/COM/assets и хеши всех блоков. SHA256SUMS проверены до и после копирования. Сертификат совпадает с ранее сохранённым публичным сертификатом проекта.

Готовый комплект: `artifacts/release-0.0.6.0/` в основном checkout: два MSIX, публичный CER, CMD/PowerShell установщик, SHA256SUMS. Пакеты не устанавливались и не публиковались. Каждая Release-сборка завершилась без ошибок, с тремя предупреждениями: два IL2104 в Windows SDK/WinRT и отсутствие `mspdbcmf.exe` для отдельного пакета символов. Live PowerToys, ARM64 runtime и сохранность реальных настроек при обновлении остаются непроверенными.

## Компактные настройки (1570c67)

Форма разделена на пять ActionSet/ShowCard: Общие, Провайдеры, Dock, Уведомления, История. Внутри провайдеров — отдельные карточки. Для сохранения используются прежний SettingsForm, прежние ключи и защищённое хранилище. Submit каждого раздела содержит только его идентификаторы; hidden values остаются зарегистрированы. Все входы переключены на одно представление. После сохранения форма остаётся открытой; включённые функции показывают зависимые параметры при повторном раскрытии раздела.

До реализации пять новых проверок падали на плоской форме. После реализации restore и Debug x64 build прошли с 0 warnings/errors; финальный полный test run — **257 Core +149 integration =406 passed**, 0 failures/skips. Восемь новых проверок покрывают EN/RU, пять разделов, conditional fields, доступность всех прежних полей, scoped Submit, reload обычных значений/секретов, удаление одного секрета без сброса другого, ошибку порога и создание/чтение/освобождение WinRT COM-представлений ICommandSettings/IFormContent. Два прежних теста маршрута помощника и тест overview обновлены на новую согласованную страницу.

Независимое read-only ревью не обнаружило Critical/Important/Minor замечаний. Release x64 и ARM64 собраны, оба подписанных MSIX прошли Test-ReleasePackage и SHA256SUMS; комплект `artifacts/release-0.0.6.0/` основного checkout обновлён только после проверки контрольных сумм прежних файлов. В каждой Release-сборке остаются три прежних SDK/WinRT/symbol warnings; новых warnings в нашем коде нет. Документация обновлена в operations.md, feature-ux.md и английских release notes.

JSON/COM-тесты не подтверждают живой PowerToys renderer: вложенные ShowCard, фокус, узкое окно и раскрытие после сохранения нужно проверить вручную по operations.md/feature-ux.md. ARM64 runtime и обновление реальных настроек остаются отдельными acceptance gaps. Установленный пакет и secrets пользователя не менялись; push/публикация не выполнялись.

## Установочное обновление 0.0.6.1

После диагностики установленного 0.0.6.0 подтверждён отказ Windows 0x80073CFB: повторная версия с отличающимся содержимым не заменяет пакет. Версия поднята до 0.0.6.1 в commit 033cfc9, без смены identity/Publisher/сертификата. Подписанные Release x64/ARM64 собраны из этого коммита; каждый build завершён с 0 errors и тремя прежними SDK/WinRT/symbol warnings.

Release x64 test run: 257 Core +149 integration =406 passed, 0 failures/skips. Оба MSIX прошли CMS/manifest/version/architecture/COM/assets/block hashes проверку с сертификатом предыдущего пакета. SHA256SUMS проверены до и после копирования. Готовый комплект: artifacts/release-0.0.6.1/ основного checkout; прежние пакеты не удалены. Установленное приложение остаётся 0.0.6.0: установка/живой renderer/обновление реальных настроек не проверялись и не выполнялись.

## Оформление карточек 0.0.6.2

Согласованное оформление реализовано в 006b48e, версия поднята отдельным коммитом 8f0a09c. Широкие Container emphasis наследуют тему хоста; заголовок/описание переносятся, индикаторы состояния и enabled count основаны на сохранённых значениях. Кнопка «Настроить» выровнена вправо отдельной строкой под заголовком, чтобы раскрытая форма занимала всю ширину, а не узкую колонку. Системный тумблер не имитируется: штатный Input.Toggle остаётся внутри карточки. Save scopes и защищённое хранилище не менялись.

До изменения четыре проверки падали на старой разметке; после изменения Debug x64 restore/build прошли с 0 warnings/errors и все 257 Core +151 integration =408 tests passed. Release x64 повторил 408 passed, 0 failures/skips. Новые проверки EN/RU покрывают фон темы, растяжение колонки, перенос текста, правое раскрытие, доступную подпись и обновление On/Off с появлением зависимого поля. Прежние проверки всех ключей, Submit scope, reload и секретов сохранены.

Независимое ревью не нашло Critical/Important дефектов; Minor отличие от примера — кнопка справа ниже заголовка — объяснено ограничением ширины раскрытия. Jev advisory рекомендовал retain вместо переписывания сохранения ради точного header layout.

Подписанные Release x64/ARM64 из 8f0a09c прошли CMS/проверку прежнего сертификата, manifest/version/architecture/COM/assets и хешей блоков. SHA256SUMS проверены до и после копирования в artifacts/release-0.0.6.2/ основного checkout. Каждая сборка завершена с 0 errors и тремя прежними SDK/WinRT/symbol warnings. Прежние пакеты не удалены; установленный 0.0.6.1 не менялся.

Живой PowerToys renderer и состояние фокуса/раскрытия, узкое окно, ARM64 runtime и реальное обновление с пользовательскими настройками не проверены. Ручные сценарии — в operations.md и feature-ux.md. Для смены установленного содержимого используется новая версия, без удаления пакета и секретов.

## Исправление раскрытия и логотипов 0.0.6.3

Скриншот установленного 0.0.6.2 подтвердил, что ActionSet не дал ожидаемого размещения Configure, а общий ромб не заменяет логотипы сервисов. Исправление использует selectAction на полном заголовке карточки и стрелку справа. UI-only состояние хранит один раскрытый раздел/провайдер; поля остальных карточек отсутствуют в шаблоне. Навигационные Submit передают associatedInputs=none и не вызывают нативное сохранение. Save по-прежнему делегируется SettingsForm с прежними ключами, валидацией и защищёнными секретами.

Провайдеры используют общий ProviderIconCatalog и уже включённые в MSIX PNG. Логотипы передаются как data URI, которые поддерживает официальный WinUI Adaptive Cards renderer; чтение локальных PNG происходит только при раскрытом списке провайдеров. Официальный PowerToys ContentFormViewModel передаёт inputs и action data в SubmitForm раздельно. Jev вызван, но недоступен из-за ошибки локальной интеграции; вывод не использовался как доказательство корректности.

Установленный пакет проверен без изменений: версия 0.0.6.2. Для нового содержимого подготовлена следующая версия 0.0.6.3. Живой PowerToys renderer, фокус клавиатуры, узкое окно, ARM64 runtime и обновление реальных пользовательских настроек не проверены.

Регрессионные тесты подготовлены в отдельном worktree и перенесены коммитом 0d8ca53. На старой форме: 8 failed / 4 passed; после исправления и корректировки тестового порядка кликов Debug x64 restore/build прошли без warnings/errors, 257 Core +153 integration =410 tests passed, 0 failures/skips. Проверены навигация без сохранения/Changed, закрытие карточки, неизвестные маршруты, все зарегистрированные ключи и scope Save, сохранность/очистка секретов, состояние EN/RU и реальные bytes логотипов. Тестовый output включает те же PNG, что MSIX. Независимое read-only ревью не нашло подтверждённых дефектов.

Продуктовое исправление: 2f18adc; версия и документация: debd803. Подписанные Release x64/ARM64 собраны из debd803, 0 errors и по три прежних SDK/WinRT/symbol warnings, без новых предупреждений нашего кода. Release x64 повторил 410 passed. Каждый MSIX прошёл CMS/привязку к прежнему сертификату, manifest/version/architecture/COM/assets/block hashes; SHA256SUMS проверены до и после копирования шести файлов в artifacts/release-0.0.6.3/ основного checkout. Основной checkout сохранил прежние untracked research/ и readme_media/. Установка, push и публикация не выполнялись.

## Native WinUI settings window (0.0.6.4 candidate)

Plan: `docs/superpowers/plans/2026-10-12-native-settings-window.md`. Editor, host and documentation were developed in separate worktrees and integrated as milestone commits. Product implementation: `53eebb8` (plus editor `2a48eb8`, host `329f35a`, documentation `aef88f8`).

The PowerToys page now contains only the open action. The lazy in-process STA window edits the same settings instance through SettingsForm; no second store/IPC/migration was introduced. Real SettingsExpander/SettingsCard controls supply the theme and animations. Nested provider expanders use the toolkit's native expander style selector; provider fields and local logos are loaded on expansion. Inputs use flexible widths. Settings and protected secrets retain their identifiers and format.

Review identified and fixed two defects: a secret-store write failure could falsely report success, and failed host shutdown could skip settings cleanup. Set/Remove now restore the protected-value dictionary on failed file writes; the settings field gets a localized error and its previous mask. Locked-file regression tests verify replacement/deletion failure, unchanged disk bytes, old secret availability and successful retry. Cleanup runs in finally after UI shutdown. Re-review found no remaining important blocker.

Debug x64 full offline tests passed: **257 Core +162 integration =419**, zero failures/skips. Native smoke found two additional startup/render blockers that source review missed: the STA worker needed explicit thread-local RoInitialize, and the application XAML needed ApplicationDefinition metadata. First layout also exposed the default SettingsCard style being applied to nested expanders; the native toolkit style selector fixes this. A fixed 280-wide field clipped at 640x480; flexible field widths fixed the failing bounds check. The opt-in smoke now checks real controls after layout, local provider icon type/fields, narrow input bounds, reuse, close/reopen and shutdown using a temporary settings directory. No installed package or real credentials were modified.

Live PowerToys COM activation, visual animation quality, keyboard/high contrast/reduced motion and ARM64 runtime remain unverified. Debug smoke uses an unpackaged bootstrap host and is not equivalent to a signed-MSIX upgrade or host acceptance.
Final package product SHA: `ed3833d`. Debug and Release x64 both passed all 419 offline tests. Native collections notify the toolkit repeater when provider membership changes; the smoke scrolls the virtualized Dock section into view and confirms the new row is loaded. It also passes using the final MSIX's trimmed application/Core/toolkit libraries and embedded XAML resources, without Debug XBF files. This unpackaged helper retains its full .NET/Windows SDK bootstrap libraries and does not replace installed-package/COM acceptance.

Final signed 0.0.6.4 x64/ARM64 builds completed with zero errors and three existing SDK/WinRT/symbol warnings each. Both packages passed CMS/signer pinning, manifest/version/architecture/COM/assets and signed payload block hash verification. The six release files were copied to the main checkout's `artifacts/release-0.0.6.4/` after verifying that the earlier task-owned files had not changed; SHA256SUMS matched before and after copying. Windows App Runtime 1.8 is required (framework minimum 8000.994.2142.0). Installation, push and public publication were not performed. Main-checkout research/media files remain untouched.
## Native settings startup fix (0.0.6.5)

The installed 0.0.6.4 was confirmed by Get-AppxPackage. Windows Application events and the local minidump showed fail-fast 0xc000027b in Microsoft.UI.Xaml/Application.Start with stowed E_POINTER (0x80004003). No settings or credential contents were inspected.

The previous native smoke retained untrimmed Microsoft projections and WinRT.Runtime. Replacing all three with the 0.0.6.4 package DLLs reproduced the startup crash. Restoring Microsoft.WinUI alone did not fix it; restoring WinRT.Runtime did. With the package Microsoft.WinUI restored again, startup succeeded and the detailed helper then failed on a legitimately trimmed Window.Content getter. This isolates the production startup defect to trimmed WinRT.Runtime, rather than the button or Windows installation.

The fix roots only WinRT.Runtime; other application and SDK trimming remains enabled. NativeSmokeTests --startup-only exercises production open, repeated activation and shutdown without test-only projection getters. Test-NativeSettingsReleaseStartup.ps1 uses the actual package's three projection/runtime DLLs and embedded PRI, failing if any are missing; no Debug XBF files are copied. The final x64 0.0.6.5 passes this check. Debug offline tests: 419 passed. Both signed MSIX packages pass signer, version, architecture, COM/assets and payload block hashes. No installed package or real user settings/secrets were changed.

Remaining acceptance: live PowerToys button after upgrade, ARM64 runtime, animation/accessibility visual checks. The helper retains its own .NET/unpackaged bootstrap files, so it is not installed-COM acceptance.