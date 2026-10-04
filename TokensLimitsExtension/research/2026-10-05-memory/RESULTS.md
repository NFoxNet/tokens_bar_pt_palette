# Расход памяти: кандидат v0.0.5.8

Ветка `codex/v0.0.5.8-memory`, база `7d8c029` (v0.0.5.7).
Четыре изменения сокращают удерживаемую историю и временные выделения UI/JSON.
Частота опроса, настройки, provider/command IDs и COM hosting сохраняются.

## Изменения и точки отката

| Коммит в общей ветке | Изменение |
| --- | --- |
| `b821fcc` | Version/AppxPackageVersion/manifest → 0.0.5.8 |
| `29a5f8f` | `CodexLocalSessionFallback`: события от включительной границы 7d и будущие события; отдельные cumulative/offset/HasTokenEvents, повторный разбор при откате часов, value-type событие |
| `48be244` | `TokensLimitsPage`: граф строк до первого запроса хоста не создаётся; общая metadata один раз на render, индивидуальные Details, освобождение при отключении/Dispose |
| `807df97` | `CodexUsageClient`, `ConfiguredUsageProvider`, `UsageJsonParser`: UTF-8 parsing из bounded memory, меньше копий; legacy charset/text и защитные пределы сохранены |
| `9162360` | `UsageOverviewPage`: только строка изменившегося cache, стабильные Details/actions, защита публикации от конкурентных событий |
| `91deebe` | Документация архитектуры и жизненного цикла |
| `7e34afa` | Один и тот же synthetic probe, baseline и сырые результаты до/после |

Работы выполнены в отдельных managed worktree и сведены через отдельные
коммиты. Новых production dependencies, настроек или forced GC нет.

## Сравнение

Windows 10.0.26200.0, .NET 10.0.12, x64, Release. Standalone harness использует
одинаковый исходный код и project-reference overrides на обеих версиях:
library output, без trimming/MSIX deployment. SHA-256 `MemoryProbe/Program.cs`:
`EEFD4FA621198DE7B2AE140317FF4A914A437B3036B8B2E2EA7C6450E5B98E65`.
Base production — `7d8c029`; after production — `91deebe`. Данные и timestamp
фиксированы; реальные аккаунты, settings и session logs не читались.

В таблице выделенные байты — среднее пяти операций после одного warmup;
холодное чтение fallback выполнено один раз. Это `GC.GetTotalAllocatedBytes`,
а не размер живого heap. HTTP handler создаёт `StringContent`, поэтому его
кодирование тоже входит в одинаковый измеряемый сценарий.

| Сценарий / измеряемая величина | До | После | Снижение |
| --- | ---: | ---: | ---: |
| Fallback: удерживаемые события, 10 000 старых + 100 свежих | 10 100 | 100 | 99,01% событий |
| Fallback: cold allocation, bytes | 6 121 184 | 5 462 024 | 10,77% |
| Обновление 5 providers, включая подписанные страницы, bytes | 1 286 102 | 51 272 | 96,01% |
| Обновление 15 providers, включая подписанные страницы, bytes | 7 814 984 | 148 800 | 98,10% |
| Один refresh при уже открытой странице, 15 providers, bytes | 559 517 | 89 093 | 84,08% |
| Codex HTTP JSON, 100 KiB body, bytes | 545 416 | 340 600 | 37,55% |
| Codex HTTP JSON, 1023 KiB body, bytes | 6 293 224 | 4 198 104 | 33,29% |
| Generic JSON, 100 KiB body / два endpoints, bytes | 1 358 846 | 744 200 | 45,23% |
| Generic JSON, 1023 KiB body / два endpoints, bytes | 13 670 005 | 7 830 957 | 42,71% |
| Строки неоткрытой detail page после cache refresh, fixture | 8 | 0 | Граф отложен |
| Сохранённые Details остальных overview rows после одного refresh, 15 providers | 0/14 | 14/14 | Объекты переиспользуются |

Значения расхода совпали: fallback 200 tokens за 5h/7d, Codex API 2% used,
reset 1790000000, окно 18000s; generic 15%/28% used. Codex HTTP case выполняет
6 stub requests, generic — 12; число запросов осталось тем же. После первого
чтения detail page возвращает те же восемь строк fixture и обновляется сразу.

У warmed fallback выделения немного выросли: 3 576 → 3 760 bytes за вызов,
при нулевом чтении файлов в обеих версиях. У локального JSONL parser нет
существенного выигрыша на padding-файлах; основная экономия там относится к
удерживаемым событиям. Эти результаты не означают снижение каждой операции.

## Проверки

- Locked restore; Debug x64 build с `-warnaserror`: 0 warnings/errors.
- Debug и Release x64: по 191 unit + 67 integration = **258 passed**.
- Release x64 и ARM64: 0 errors, по два известных внешних WinRT `IL2104`.
- PowerShell helper, unregister и installer tests: exit 0.
- Каждый production patch прошёл отдельное независимое ревью. Финальное
  ревью immutable `7d8c029..91deebe` не нашло воспроизводимых замечаний и
  отдельно запустило 56 integration + 93 Core теста без ошибок.
- Probe Release build/run на обеих версиях прошли; source hash совпал;
  `git diff --check` чистый. Проверены charset/BOM/text compatibility, body
  limits, cancellation, cumulative append/reset, rolling boundaries, rollback,
  file replacement/truncation/deletion, локализация и конкурентные события.

## Воспроизведение и пределы

Команды приведены в [BASELINE.md](BASELINE.md). Сырые результаты:
[BEFORE.txt](BEFORE.txt) и [AFTER.txt](AFTER.txt). Probe находится в
[MemoryProbe/Program.cs](MemoryProbe/Program.cs), отдельно от solution/CI.
Для baseline нужен тот же harness поверх production base `7d8c029`;
измерительная ветка `codex/memory-measure` содержит только research-файлы.

Process-wide allocation может включать фоновую работу runtime. Время операции
записано отдельно от forced GC измерительного harness, но короткие прогоны и
один cold sample не доказывают CPU-выигрыш. Probe создаёт одну detail page на
provider и не моделирует удержание native объектов хостом. Command Palette
может запросить страницу заранее, поэтому отложенная материализация зависит
от его запросов, а не гарантированной видимости UI.

Working set/private bytes установленного расширения, live COM/Dock,
ARM64 runtime и обновление MSIX с пользовательскими данными не проверялись.
Полученные проценты относятся только к указанным fixtures; снижение общей
оперативной памяти PowerToys требует отдельного runtime-замера.
