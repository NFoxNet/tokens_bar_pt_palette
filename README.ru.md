# Tokens Limits

Tokens Limits — расширение Microsoft PowerToys Command Palette, которое показывает лимиты и показатели аккаунтов Codex и других AI-провайдеров в одном Dock band.

## Установка

Скачайте файлы из [последнего GitHub Release](https://github.com/NFoxNet/tokens_bar_pt_palette/releases/latest), оставьте MSIX, сертификат, установщик и файл контрольных сумм в одной папке и запустите `Install-TokensLimitsExtension.cmd`. После установки перезагрузите расширения Command Palette в PowerToys.

Установщик проверяет подпись и контрольные суммы. При обновлении настройки и зашифрованные ключи сохраняются.

## Возможности

- Лимиты Codex и явно отмечаемый локальный fallback при недоступности API.
- Баланс и метрики DeepSeek, а также адаптеры других провайдеров.
- Общий цикл обновления и единый snapshot для Dock, обзора и детализации.
- Английский язык по умолчанию и русский в комплекте; новые языки добавляются JSON-файлами в `TokensLimitsExtension/lang/`.
- Безопасное хранение ключей и настроек в профиле пользователя Windows.

## Документация

- [Полная документация проекта](TokensLimitsExtension/doc/README.md)
- [Разработка и тестирование](TokensLimitsExtension/doc/development.md)
- [Релизы и установка](TokensLimitsExtension/doc/release.md)
- [English README](README.md)

## Лицензия

[MIT](LICENSE)
