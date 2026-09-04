# Разработка, сборка и тесты

## Требования

- Windows 10 19041 или новее.
- Visual Studio с workload для Windows App SDK/.NET или установленный .NET 10 SDK.
- Windows SDK 26100 и восстановленные NuGet-пакеты.
- Для runtime-проверки — Microsoft PowerToys с включённым Command Palette.

Версии пакетов централизованы в `Directory.Packages.props`. Не добавляйте разные версии одного пакета в отдельные `.csproj` без необходимости.

## CLI-цикл

Из корня репозитория:

```powershell
dotnet restore .\TokensLimitsExtension.sln
dotnet build .\TokensLimitsExtension.sln --configuration Debug -p:Platform=x64 --no-restore
dotnet test .\TokensLimitsExtension.sln --configuration Debug -p:Platform=x64 --no-restore
```

## Visual Studio и Command Palette

1. Выберите конфигурацию `Debug` и платформу `x64` или `ARM64`.
2. Используйте `Build > Deploy`, а не только `Build`: Deploy регистрирует обновлённый MSIX.
3. Запускайте профиль `(Package)`, а не `(Unpackaged)`.
4. После Deploy в Command Palette выполните `Reload` → `Reload Command Palette extensions`.
5. Для диагностики смотрите Output window в режиме Debug; код пишет сообщения через `Debug.WriteLine` и `ExtensionHost.LogMessage`.

## Быстрая локальная установка для проверки UI

Обычная `dotnet build` не создаёт `.msix`: её результат — текущая DLL в
`TokensLimitsExtension/bin/x64/Debug/...`. Для ежедневной проверки кода
регистрируйте Debug manifest напрямую. Это не требует сертификата, не меняет
PowerShell execution policy и не удаляет `%LOCALAPPDATA%\TokensLimitsExtension`
с настройками и зашифрованными ключами.

Из корня решения выполните:

```powershell
dotnet build .\TokensLimitsExtension.sln --configuration Debug -p:Platform=x64

Get-Process TokensLimitsExtension -ErrorAction SilentlyContinue |
  Stop-Process -Force

$manifest = Resolve-Path .\TokensLimitsExtension\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\AppxManifest.xml
Add-AppxPackage -Register $manifest -ForceApplicationShutdown
```

Затем откройте Command Palette и выполните **Reload Command Palette
extensions**. При следующем изменении повторите эти же команды. Если требуется
переключиться обратно на публичный MSIX, установите его штатным
`Install-TokensLimitsExtension.cmd`; перед явным удалением пакета всегда
используйте `Remove-AppxPackage -PreserveApplicationData`.

Путь к созданному релизному MSIX после сборки с
`-p:GenerateAppxPackageOnBuild=true` —
`TokensLimitsExtension/AppPackages/.../*.msix`. Такой пакет предназначен для
проверки сценария установки и подписи, а не для быстрой итерации UI.

### Проверка Dock-навигации

После Deploy проверьте последовательность: клик по Tokens Limits в Dock →
открытие деталей → закрытие палитры → повторный вызов основной палитры по
горячей клавише. Основная палитра должна открыться на корневом экране, а не
на странице лимитов. Для этой проверки важна актуальная версия Command
Palette: в старых сборках хоста был upstream-баг transient-навигации Dock.
Описание соответствующего исправления хоста: [PowerToys PR #48089](https://github.com/microsoft/PowerToys/pull/48089).

## Release/MSIX

Для локальной проверки пакета применяйте отдельные сборки архитектур:

```powershell
dotnet build .\TokensLimitsExtension.sln --configuration Release -p:GenerateAppxPackageOnBuild=true -p:Platform=x64
dotnet build .\TokensLimitsExtension.sln --configuration Release -p:GenerateAppxPackageOnBuild=true -p:Platform=ARM64
```

Release включает trimming, поэтому проверяйте предупреждения AOT/trim. Профили публикации находятся в `TokensLimitsExtension/Properties/PublishProfiles/`.

Для публичного GitHub-релиза используйте `scripts/Build-Release.ps1`. Скрипт требует PFX, чей subject в точности совпадает с `Publisher` в `Package.appxmanifest`, подписывает x64 и ARM64 MSIX и создаёт SHA-256 checksums. PFX и пароль не должны попадать в репозиторий или логи. Полная процедура — в [release.md](release.md).

## Тестовые уровни

- `TokensLimitsExtension.Tests` — парсинг, нормализация, auth, fallback, cache, registry и каталог провайдеров.
- `TokensLimitsExtension.IntegrationTests` — страницы, команды, dock band и общий пользовательский контракт.

Сетевые тесты используют stub handlers. Не превращайте unit-тесты в тесты внешних кабинетов: реальные endpoint-проверки должны быть отдельным, явно opt-in процессом.

## Добавление изменения

Перед коммитом:

- проверьте `git diff` и `git status`;
- запустите build и test для целевой архитектуры;
- если затронут MSIX/manifest — проверьте Deploy;
- если меняется provider contract — обновите unit и integration tests;
- если меняются пользовательские настройки или команды — обновите `doc/`;
- убедитесь, что в diff нет `bin/`, `obj/`, пакетов и секретов.
