# Результаты реализации

Ветка: `codex/usage-ux-improvements`, база `2e6314a`. Работа выполнялась шестью исполнителями в отдельных worktree и интегрирована последовательными commits. Оригинальный checkout `master` не изменялся; папка `readme_media/` сохранена.

## Изменения

- F1: ручное обновление принудительно обновляет свежий кэш, сохраняет общий single-flight и cooldown. Абсолютный deadline не сдвигается от нажатий; планировщик не теряет повтор после смены интервала. Смена credentials очищает старый cooldown.
- F2/U5: сводка использует реальные длительности окон и только существующие значения, показывает баланс вместе с окнами, сохраняет признак оценки и ограничивает длинные compact-метрики. Неизвестные окна и Azure health/отсутствие quota локализованы.
- F3: timeout сохраняет тип ошибки; отсутствие необязательного Cookie не заменяет причину выполненного API-запроса. Агрегат сохраняет наибольший положительный Retry-After независимо от выбранной причины ошибки.
- F4: auth использует первый Codex home, локальный fallback — все перечисленные каталоги. Проверено на временных JSONL и stub HTTP, без реальных credentials.
- F5: автоматический Azure refresh не выполняет inference. Ручная проверка сообщает connection health; она не выдаёт ping tokens за расход аккаунта.
- S1–S4: удалены неиспользуемый refresh helper, семь неиспользуемых version pins, два непрочитанных поля fallback и идентичная ветка placeholder: механическая очистка уменьшила код на 63 строки. Публичные API и COM hosting сохранены. Новые функции и regression-тесты отдельно увеличивают общий diff.
- U1–U4: нативные статусы, свежесть/busy, Details и контекстные действия; вход в существующие настройки из пустого overview. Форма настроек и хранение секретов не заменялись.
- Q1–Q3: исправлены пути Git root/solution в development guide, добавлены offline regression-тесты поведения и матрица возможностей источников.

U6 (избранные Dock), Q4 (история, экспорт и уведомления) отложены по ответу пользователя.

## Проверки

Проверено интегратором на product commit `5718145` (последующие изменения — только отчёт):

| Проверка | Результат |
| --- | --- |
| Locked restore solution, Debug x64 | Успех |
| Debug build solution, `-warnaserror` | Успех, 0 предупреждений / 0 ошибок |
| Unit tests | 168/168, 0 skipped |
| Integration tests | 53/53, 0 skipped |
| Installer / release helper / unregister PowerShell suites | Все три прошли; stub environment |
| Release x64 и ARM64, locked restore и `-warnaserror` | Обе прошли; по 2 известных внешних IL2104, 0 ошибок |
| `git diff 2e6314a --check` | Успех |
| COM/manifest/IDs/lock-file drift | Изменений нет |

Всего **221/221 тест**, на 80 больше baseline 141. Финальный повтор тестов с отдельными TRX выполнен из-за перезаписи общего CI `LogFileName`; оба файла прочитаны и подтверждают 53/53 и 168/168:
`TestResults/final-debug_net10.0_20261005022211.trx` и `TestResults/final-debug_net10.0_20261005022212.trx`. Локальные build/test artifacts остаются ignored и не коммитятся.

Независимые reviews проверили formatter, composition, transport, scheduler, shared validation lifecycle и обе UI-итерации. Найденные дефекты закрыты отдельными commits и regression-тестами. Последний scoped review UI и общий cache review не нашли дополнительных actionable-регрессий.

Исторические `VERIFICATION.md`, `evidence.json` и `Probe.ps1` относятся к базе аудита; Probe намеренно воспроизводит старые дефекты и не является regression suite новой версии.

## Основные commits для просмотра и отката

| Область | Commits интеграционной ветки |
| --- | --- |
| Cooldown/scheduler | `43ccf51`, `1143d9b`, `03d0407` |
| Formatter/локализация метрик | `9bcd158`, `377ef52`, `23b619d` |
| Ошибки запросов / Retry-After | `a512a65`, `2ef754f` |
| CODEX_HOME / native settings | `24cf3aa` |
| Azure / shared validation / отмена | `fc179b6`, `8fabfd3`, `e30c81c` |
| Native UX / явная команда Azure | `2f0e0ba`, `5718145` |
| Удаление неиспользуемого кода | `c282ef1` |
| Эксплуатационная документация | `0702228` |

## Ограничения

- Live Command Palette/Dock: не проверены визуальная компоновка, host-навигация, клавиатура и переполнение. Native computer automation в сеансе отключена. Объектные integration tests не заменяют эту проверку; desktop/mobile здесь соответствует нативному Windows host, отдельного mobile UI нет.
- Реальные провайдеры, signed MSIX upgrade, сертификаты и установка не проверялись. Проверки используют временные каталоги и stub HTTP/process handlers.
- Retry-After агрегируется консервативно: время последующих endpoint-запросов может дополнительно продлить ожидание. Это предотвращает преждевременные повторные запросы; абсолютный UI deadline отражает фактическое решение общего планировщика.
- Версия/identity пакета прежние; ветка не публиковалась и не отправлялась на remote.

## Откат

Крупные изменения разделены commits. Для отката конкретного шага используйте `git revert <SHA>` в отдельной ветке, учитывая зависимости UI от Core API. `git log --oneline 2e6314a..codex/usage-ux-improvements` показывает последовательность. Не откатывайте пользовательские untracked-файлы.
