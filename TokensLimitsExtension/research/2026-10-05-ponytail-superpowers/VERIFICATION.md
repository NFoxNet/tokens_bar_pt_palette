# Проверки и границы доказательств

5 октября 2026. HEAD `2e6314a04d251f432ed182871831a71a34d97e3f`. Команды выполнялись из solution directory `C:\Users\Honor\Documents\Work\Repos\tokens_bar_pt_palette\TokensLimitsExtension`.

| Команда / проверка | Результат |
| --- | --- |
| `dotnet restore .\TokensLimitsExtension.sln --locked-mode` | Exit 0; восстановлены четыре проекта |
| `dotnet build .\TokensLimitsExtension.sln --configuration Debug -p:Platform=x64 --no-restore` | Exit 0; 0 warnings, 0 errors |
| `dotnet test .\TokensLimitsExtension.sln --configuration Debug -p:Platform=x64 --no-restore --no-build` | Exit 0; 119 unit + 22 integration, 0 failed, 0 skipped |
| `pwsh -NoProfile -File ..\scripts\tests\Install-TokensLimitsExtension.Tests.ps1` | Exit 0; `Installer restart tests passed.` |
| `pwsh -NoProfile -File ..\scripts\tests\Build-Release.Helpers.Tests.ps1` | Exit 0; stdout пустой |
| `pwsh -NoProfile -File ..\scripts\tests\Unregister.Tests.ps1` | Exit 0; `Unregister tests passed.` |
| Reflection Core catalog | 69 providers, 140 provider fields, 211 registered settings; default enabled — Codex |
| Reflection local Toolkit | ShowDetails, IsLoading, MoreCommands, RequestedShortcut, SettingsPage доступны |
| Whole-tree symbol / PackageReference / lock-file searches | Подтверждают S1–S3; метаданные старого coverage не являются вызовами helper |

PowerShell suites подменяют установку/процессы/удаление пакета тестовыми функциями. Их сообщения «Stopping PowerToys» и «Removing ...» описывают сценарий stub-теста: настоящий PowerToys не останавливался, AppX не удалялся, сертификаты не импортировались.

## Воспроизводимые probes

После указанной Debug x64 сборки, в **PowerShell 7.6 / .NET 10**:

```powershell
pwsh -NoProfile -File .\research\2026-10-05-ponytail-superpowers\Probe.ps1
```

Скрипт загружает собранные Core/UI/test assemblies, использует существующие private synthetic fixtures и stub HttpMessageHandler. Реальный configuration manager не создаётся; реальные auth.json, provider secrets, сессии, API и package registration не используются. Создаётся только `evidence.json` рядом с отчётом. Private fixtures/пути сборки привязаны к проверенному commit; после исправлений ожидаемые дефектные наблюдения надо пересмотреть.

Это позитивные проверки воспроизведения **существующих дефектов**, а не зелёные regression tests исправлений:

| Сценарий | Наблюдение на текущем коде | Контракт, который стоит получить |
| --- | --- | --- |
| Ручная команда Refresh со свежим кэшем | 1 → 1; принудительный cache refresh даёт 2 | Явное действие делает новый запрос через общий coordinator |
| Retry-After 120 секунд | 1 → 1 после force; 1 → 2 после default | Пользовательский refresh соблюдает cooldown |
| 30-дневное окно | Label helper `30д`; Dock `5ч\70%, 7д\—` | Длительность из модели |
| Estimated total balance | `Total balance: 12 USD`, без estimate | Признак оценки сохраняется; real provider reachability этой комбинации не доказана |
| Timeout single generic endpoint | `UnsupportedResponse` | `Timeout` |
| Timeout API-only DeepSeek, Cookie не настроен | `MissingConfiguration` | Реальная ошибка настроенного источника имеет осмысленный приоритет |

F4 (multi-CODEX_HOME) и F5 (Azure validation) подтверждены чтением композиции/endpoint/scheduler, без запуска реальных home или Azure. Для F4 предлагается новый regression test именно default composition на временных каталогах. Для F5 — stub request counter по scheduler ticks и явной команде.

## Что не проверено

- Реальное окно Command Palette, ширины, DPI, keyboard/focus, Dock/transient navigation и настройки. Native computer automation текущего сеанса отключена; browser не заменяет Windows COM host.
- Текущая установленная версия/состояние PowerToys или MSIX. Это не диагностика установленного приложения.
- Реальные usage APIs, актуальные схемы каждого из 69 providers, стоимость Azure deployment.
- Release/trimming x64/ARM64, ARM64 runtime, подписанная установка/upgrade/сохранение реальных secrets, COM shutdown/live allocations.
- Производительность до/после упрощений: патч не применялся, runtime baseline не снимался.

Официальные Microsoft источники проверены веб-инструментом; локальная reflection подтверждает часть API используемого Toolkit. Это подтверждение технической возможности, не готовая визуальная приёмка.

Jev: первый вызов отклонён `candidate action id has an invalid format`; исправленный запрос вернул `unavailable / TypeSafe / local integration error`. Решение не получено, поэтому report не приписывает Jev свои выводы.

## Проверка границ изменений

До работы: только `?? ../readme_media/`, tracked diff отсутствовал. После работы должен появиться только `research/2026-10-05-ponytail-superpowers/`; результаты build/test остаются в ignored bin/obj. Изменений исходников, lock-файлов, CI/scripts, user settings, ветки и коммитов не делалось. Проверка `git diff`, `git status` и содержания новых файлов обязательна перед передачей отчёта.
