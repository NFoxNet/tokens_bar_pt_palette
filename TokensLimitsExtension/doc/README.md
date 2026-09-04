# TokensLimitsExtension

Расширение для Microsoft PowerToys Command Palette, которое показывает остаток лимитов и дополнительные показатели использования у Codex и подключённых AI-провайдеров.

## Что умеет проект

- Показывает один верхнеуровневый пункт `Tokens Limits` и список включённых провайдеров.
- Создаёт отдельную страницу лимитов для каждого включённого провайдера и показывает их вместе в одном стабильном dock band.
- Нормализует разные ответы API в общий контракт `UsageSnapshot`.
- Поддерживает rolling windows, дополнительные лимиты, тариф, метрики кредитов/расходов и явные оценки.
- Для Codex использует официальный usage endpoint, обновление access token и локальный fallback по журналам сессий.
- Кэширует последний snapshot, чтобы overview, detail page и dock band не делали дублирующие запросы.
- Хранит секретные поля отдельно от обычных настроек через `ProtectedSecretStore`.

## Быстрый маршрут по репозиторию

| Путь | Назначение |
| --- | --- |
| `TokensLimitsExtension/` | UI, COM server, настройки и MSIX-проект |
| `TokensLimitsExtension.Core/` | Модели, контракты, провайдеры и сервисы |
| `TokensLimitsExtension.Tests/` | Unit-тесты |
| `TokensLimitsExtension.IntegrationTests/` | Интеграционные тесты страниц и команд |
| `TokensLimitsExtension.Core/Providers/UsageProviderDescriptorRegistry.cs` | Каталог провайдеров и их настроек |
| `TokensLimitsExtension/Properties/PublishProfiles/` | Профили публикации для x64 и ARM64 |
| `.github/` | Инструкции и сценарии для разработки Command Palette |
| `lang/` | JSON-словари интерфейса; `en.json` — обязательный fallback |

## Технологический контур

- C# / .NET 10.
- Приложение: `net10.0-windows10.0.26100.0`.
- Минимальная Windows: `10.0.19041.0`.
- Целевые архитектуры: `x64` и `ARM64`.
- UI/runtime: Microsoft Command Palette Extensions Toolkit, Windows App SDK, WinRT COM server.
- Тесты: xUnit и `Microsoft.NET.Test.Sdk`.

## Пользовательский поток

1. Command Palette загружает COM server через `Program`.
2. `TokensLimitsExtensionCommandsProvider` создаёт настройки, registry, кэши и страницы.
3. Включённые провайдеры отображаются в overview и dock bands.
4. При первом открытии или по таймеру кэш получает snapshot асинхронно.
5. Страница показывает основной/дополнительный window, тариф, дополнительные лимиты и метрики.
6. Изменение настроек инвалидирует кэш, обновляет интервал и перестраивает список включённых провайдеров.

## Документы

- [Архитектура](architecture.md)
- [Разработка, сборка и тесты](development.md)
- [Провайдеры и добавление нового адаптера](providers.md)
- [Языки и иконки](localization.md)
- [Настройки, безопасность и диагностика](operations.md)
- [Публичные релизы, установка и подпись](release.md)

## Статус и важное ограничение

Поддержка провайдера зависит от того, какие данные он реально публикует. Если API даёт баланс, кредиты или число запросов, но не даёт лимит окна, расширение показывает метрику без искусственного процента. Локальный Codex fallback является оценкой и должен отображаться с соответствующей пометкой.
