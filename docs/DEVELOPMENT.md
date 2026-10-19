# Сборка и проверки

[← Revit MCP](../README.md)

Требуются Windows x64, .NET 8 SDK, Node.js 24, pnpm 11.25.0 и Git. Для запуска надстройки нужен Revit 2026.

```powershell
pnpm --dir server install --frozen-lockfile
pnpm --dir server test
node --test installer/configure-mcp.test.mjs
node installer/check-release.mjs
dotnet run --project tests/ApprovalSettings.Tests/ApprovalSettings.Tests.csproj -c Release
./tests/updates.ps1
./build.ps1 -UseRevitNuGet
./build-installer.ps1 -UseRevitNuGet -SkipPluginBuild
```

Без `-UseRevitNuGet` используются API установленного Revit; путь задаётся через `-Revit2026Dir`. Для CI закреплены compile-only пакеты [Nice3point/revit-api](https://github.com/Nice3point/revit-api) 2026.0.4. Их DLL исключены из поставки. Lock-файлы находятся в `plugin/` и `loader/`.

`build.ps1` собирает `RevitMCP.Core.dll` и `RevitMCP.Loader.dll` с общими сборками Contracts и Updates. Выходные каталоги — `bin/Release/` соответствующих проектов. Сборка не меняет установленную надстройку или конфигурацию MCP.

`build-installer.ps1` создаёт в `dist/` EXE, архив соответствующих исходников и `SHA256SUMS.txt`. EXE содержит Node.js, зависимости MCP, загрузчик, начальную рабочую часть и .NET runtime. Упакованный сервер проверяется через MCP initialize и tools/list. Для получения лицензионных текстов Node.js и .NET нужен интернет.

Тесты обновления загружают две разные DLL в одном процессе, проверяют смену поведения, сборку старого контекста GC, откат, восстановление после запуска, отсутствие блокировки DLL, отмену и отказ при нарушении контрольных сумм, состава архива или совместимости.

## GitHub Actions

`build.yml` выполняет тесты и сборку на Windows. Ветка `0.0.2` также получает установщик и пакет обновления. Пакет и манифест канала публикуются на GitHub Pages после успешной проверки. Установщик и исходники доступны в артефакте запуска; GitHub Release не создаётся.

```powershell
./build-update.ps1 -BuildId (git rev-parse HEAD) -Sequence 1
```

Команда формирует `dist/site/updates/0.0.2/`. BuildId — SHA коммита, Sequence — номер запуска GitHub Actions. Повторный запуск того же коммита не заменяет установленный пакет.

Для интеграционной проверки используйте копию проекта Revit: загрузка, preview/apply, обновление на следующую сборку при открытом документе, повторный предпросмотр и откат. Дополнительно проверьте светлую и тёмную темы, масштабирование 100% и 150%, отсутствие активной транзакции при переключении. Автоматические тесты не заменяют проверку в Revit.
