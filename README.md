# TWM Launcher

Исходный код лаунчера для Amazing Online на C# / WPF с интерфейсом WebView2.

## Требования

- Windows 10/11 x64
- Visual Studio 2022 или новее с workload **.NET desktop development**
- .NET 10 SDK / Targeting Pack
- WebView2 Runtime (обычно уже установлен в Windows; при необходимости устанавливается отдельно)

## Сборка

1. Установите перечисленные выше компоненты.
2. Откройте `TWM_Launcher.sln` в Visual Studio.
3. Восстановите NuGet-пакеты.
4. Выберите конфигурацию `Release` и соберите решение.

Профиль публикации Windows x64 находится в `Properties/PublishProfiles/TWM-Launcher-win-x64.pubxml`.

## Сторонние зависимости

Зависимости .NET перечислены в `AMM.App.csproj` и восстанавливаются через NuGet. Для каждой сторонней зависимости действуют условия её собственной лицензии.
