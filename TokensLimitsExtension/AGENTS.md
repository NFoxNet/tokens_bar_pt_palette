# Инструкции для AI-агентов

## Назначение проекта

TokensLimitsExtension — расширение Microsoft PowerToys Command Palette для просмотра лимитов и расхода токенов у Codex и других AI-провайдеров. Расширение работает как out-of-process COM server и поставляется в MSIX.

Перед изменениями прочитайте [обзор проекта](doc/README.md) и профильную страницу документации из `doc/`. Для специфичных сценариев Command Palette учитывайте существующие инструкции в `.github/instructions/` и skills в `.github/skills/`.

## Границы изменений

- Сохраняйте разделение между UI/интеграцией и переиспользуемым Core-слоем.
- Не меняйте hosting-паттерн `Program.cs` без необходимости: аргумент `-RegisterProcessAsComServer`, COM registration и lifecycle должны остаться совместимыми с Command Palette.
- CLSID из `[Guid]` в `TokensLimitsExtension.cs` должен совпадать с `ClassId` в `Package.appxmanifest` во всех местах.
- Не коммитьте токены, Cookie, OAuth credentials, локальные settings/secrets или реальные ответы API.
- Не логируйте access token, API key, Cookie, credentials JSON и полный ответ API. Логи должны быть пригодны для диагностики без раскрытия секретов.
- При переустановке или переключении MSIX-регистрации обязательно сохраняйте данные приложения: используйте `Remove-AppxPackage -PreserveApplicationData`. Не удаляйте пакет без этого параметра, поскольку можно потерять зашифрованные provider secrets. Для локальной Debug-проверки сначала предпочитайте остановить extension process и зарегистрировать Debug `AppxManifest.xml` напрямую; подпись и сертификаты для этого не нужны.
- Для публичного MSIX `Publisher` в manifest обязан совпадать с subject сертификата. PFX, пароль сертификата и GitHub secrets никогда не коммитятся и не попадают в логи; в GitHub Release можно публиковать только `.cer`, `.msix`/`.msixbundle`, checksums и исходный код. Self-signed сертификат годится лишь для явно задокументированного sideload-канала; для доверенной установки без ручного импорта нужен Microsoft Store или доверенный code-signing/Trusted Signing.
- Не придумывайте процент лимита, если провайдер его не возвращает. Используйте `UsageMetric`, а оценку помечайте `IsEstimate = true`.
- Не ломайте обратную совместимость идентификаторов провайдеров, настроек, команд и dock bands без явной задачи.
- Существующие изменения в рабочем дереве принадлежат пользователю: перед правкой проверьте `git status` и не откатывайте чужие изменения.

## Карта решения

- `TokensLimitsExtension/` — COM entry point, `CommandProvider`, страницы, dock band, настройки и сборка MSIX.
- `TokensLimitsExtension.Core/` — модели usage, контракты, registry провайдеров, HTTP-клиенты, нормализаторы, кэш и fallback.
- `TokensLimitsExtension.Tests/` — быстрые unit-тесты Core и провайдеров.
- `TokensLimitsExtension.IntegrationTests/` — проверки взаимодействия страниц и CommandProvider.
- `TokensLimitsExtension.Core/Providers/UsageProviderDescriptorRegistry.cs` — упорядоченный каталог поддерживаемых провайдеров и их полей настроек.

Основной runtime-поток:

`Program` → `TokensLimitsExtension` → `TokensLimitsExtensionCommandsProvider` → `UsageProviderRegistry` → `UsageSnapshotCache` → `UsageOverviewPage` / `TokensLimitsPage` / dock band.

Официальный Codex API обслуживается через `CodexUsageService`; при проблемах с авторизацией или запросом используется локальный fallback по JSONL-сессиям Codex. Остальные провайдеры создаются как `ConfiguredUsageProvider` и подключаются по дескрипторам.

## Правила разработки

- Используйте nullable reference types и существующий стиль C#.
- Для новых функций сначала добавляйте или меняйте контракт/модель Core, затем адаптер, UI и тесты.
- Для сетевого кода проверяйте timeout, cancellation, transient retry, HTTP status и схему ответа.
- Для страниц не выполняйте сеть из `GetItems()`: используйте кэш и асинхронный `RefreshAsync()`.
- При изменении настроек учитывайте событие `Changed`, инвалидацию кэша и перестроение включённых поверхностей.
- Для нового провайдера добавьте дескриптор, реализацию маршрутизации/парсинга, безопасные настройки, тесты успешного и ошибочного ответа и обновление `doc/providers.md`.
- В тестах не используйте интернет: применяйте stub `HttpMessageHandler`, фиктивную конфигурацию и временные каталоги.
- Не меняйте формат отображения или русские подписи без тестов, если они являются частью пользовательского поведения.

## Проверка перед завершением

```powershell
dotnet restore .\TokensLimitsExtension.sln
dotnet build .\TokensLimitsExtension.sln --configuration Debug -p:Platform=x64 --no-restore
dotnet test .\TokensLimitsExtension.sln --configuration Debug -p:Platform=x64 --no-restore
```

Для UI/runtime-проверки в Visual Studio используйте `Build > Deploy`, затем в Command Palette выполните `Reload`. Для отладки выбирайте профиль `(Package)`, а не `(Unpackaged)`, и проверяйте Output window.

Перед ответом пользователю убедитесь, что:

1. Изменение покрыто подходящими тестами или явно указано, почему тест невозможен.
2. Документация обновлена, если изменились настройки, провайдеры, команды, упаковка или эксплуатация.
3. `git diff` не содержит секретов и случайных артефактов сборки.
4. В итоговом сообщении перечислены изменённые файлы, проверки и оставшиеся ограничения.

## Документация

- [doc/README.md](doc/README.md) — навигация и быстрый старт.
- [doc/architecture.md](doc/architecture.md) — компоненты и поток данных.
- [doc/development.md](doc/development.md) — сборка, тесты, Deploy и отладка.
- [doc/providers.md](doc/providers.md) — модель провайдеров и правила добавления.
- [doc/operations.md](doc/operations.md) — настройки, безопасность и диагностика.
