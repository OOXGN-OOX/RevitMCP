# Сборка и проверки

[← Revit MCP](../README.md)

## Требования

- Windows x64. Autodesk Revit 2026 нужен для запуска и проверки надстройки.
- .NET 8 SDK с Windows Desktop targeting pack.
- Node.js 20+ и pnpm.
- Git для формирования соответствующих исходников.

Для компиляции без установленного Revit используются закреплённые NuGet-пакеты `Nice3point.Revit.Api.RevitAPI` и `Nice3point.Revit.Api.RevitAPIUI` версии `2026.0.4`. Они подключены только как compile-зависимости, исключены из поставки и проверяются по `plugin/packages.ci.lock.json`. Источник пакетов — [Nice3point/revit-api](https://github.com/Nice3point/revit-api).

Если SDK установлен вне PATH, добавьте его каталог в PATH перед запуском. Параметр `-Revit2026Dir` скрипта `build.ps1` задаёт нестандартный путь к Revit.

## Установка зависимостей и тесты

```powershell
pnpm --dir server install --frozen-lockfile
pnpm --dir server test
node --test installer/configure-mcp.test.mjs
dotnet run --project tests/ApprovalSettings.Tests/ApprovalSettings.Tests.csproj -c Release
```

`server/pnpm-workspace.yaml` и `server/.npmrc` включают hoisted-режим для актуальных и предыдущих версий pnpm. Зависимости устанавливаются и на файловые системы без символических ссылок, включая exFAT.

Тесты проверяют TCP-протокол, отмену и тайм-ауты, соответствие MCP-схем диспетчеру C#, редактирование конфигураций клиентов и сохранение настроек подтверждения. Они не выполняют операции в открытой модели Revit.

## DLL и ручная установка

```powershell
.\build.ps1
.\install.ps1
```

`build.ps1` использует API установленного Revit, если он доступен; иначе выбирает NuGet. Явная сборка без Revit:

```powershell
.\build.ps1 -UseRevitNuGet
```

DLL: `plugin/bin/Release/net8.0-windows/RevitMCP.dll`. Манифест ручной установки: `%APPDATA%\Autodesk\Revit\Addins\2026\RevitMCP.addin`.

Для ручного подключения MCP используйте `node` и абсолютный путь к `server/src/index.js`. Образец конфигурации находится в `server/codex-mcp.example.toml`. Перезапустите клиент после изменения конфигурации.

## Установщик

```powershell
.\build-installer.ps1
```

Результат в `dist/`: `RevitMCP-0.0.1-Setup.exe`, `RevitMCP-0.0.1-source.zip`, английские промпты, архив с промптами и SVG-знаками, `SHA256SUMS.txt`. EXE содержит Node.js, MCP-зависимости, DLL, .NET runtime, лицензионные материалы и соответствующие исходники. Сборка получает лицензионные тексты Node.js и .NET из официальных репозиториев, поэтому на машине сборки требуется интернет. Параметр `-UseRevitNuGet` передаётся компиляции надстройки. Упакованный MCP-сервер проверяется через initialize и tools/list до сборки EXE.

Версия согласуется между `.csproj`, метаданными fallback-компилятора, MCP-пакетом, MCP-сервером, установщиком и манифестом. `build-installer.ps1` проверяет её перед упаковкой.

```powershell
node installer/check-release.mjs
```

Готовая DLL переиспользуется, если её версия совпадает и исходники надстройки и скрипт сборки не изменялись после компиляции.

Проверка упакованного сервера без изменения модели:

```powershell
node installer/smoke.mjs <payload>/node/node.exe <payload>/server/src/index.js
```

Для проверки связи с установленной надстройкой добавьте `revit_ping` или `get_project_info`. Записывающие команды smoke-проверка не допускает.

## Проверка в Revit

На копии проекта проверьте загрузку вкладки Revit MCP, окно подключения, переключение подтверждения и пару preview/apply. Оцените значки в светлой и тёмной темах, при масштабировании 100% и 150%. Установку, восстановление и удаление проверяйте с закрытым Revit; повторный запуск должен сохранять настройки и сторонние MCP-подключения.

## GitHub Actions

- `build.yml`: тесты и DLL на Windows для веток, pull requests, ручного и повторно используемого запуска. Готовая DLL доступна в артефактах запуска.
- `release.yml`: на каждом push в `main` запускает проверки. Если версия ещё не опубликована и есть `docs/releases/X.Y.Z.md`, собирает установщик, соответствующие исходники, графическое задание и SHA-256. Затем создаёт черновик релиза, загружает все файлы и публикует готовый выпуск.
- Опубликованная версия не перезаписывается. Изменение исходников с прежней версией проходит проверки, но не создаёт повторный релиз.
- Runner использует .NET 8, Node.js 24 и pnpm 11.25.0; Revit на runner не устанавливается. Модели Revit не открываются.
- Для выпуска используется стандартный `GITHUB_TOKEN` с правом `contents: write`; отдельные секреты и собственный runner не требуются.
