# PdmLite

Мини-PDM для Autodesk Inventor 2025: плагин (add-in) для Inventor и внешнее приложение
**PdmLite Explorer** для просмотра проекта.

> Статус: **этап 1, шаг 1 — каркас.** Реализованы дерево проекта, карточка файла,
> связь плагина и Explorer. Плагин запускается в Inventor 2025, Explorer открывается и
> закрывается без ошибок. iProperties, PostgreSQL и переименование — следующие шаги.

## Что реализовано

**Плагин в Inventor**
- Вкладка **PdmLite** на ленте (во всех средах): «Дерево проекта», «Показать в Explorer», «Обновить проект».
- Закрепляемое окно PdmLite: дерево активного проекта Inventor (папки и файлы
  `.ipt/.iam/.idw/.dwg/.ipn`), поиск, карточка выбранного файла.
- Активный документ Inventor выделяется в дереве; двойной щелчок открывает документ.
- При смене проекта Inventor дерево перестраивается.

**PdmLite Explorer**
- Открытие проекта: папка или файл `.ipj`; недавние проекты; F5 — обновить.
- То же дерево и та же карточка, что в плагине (общие модули).
- Строка состояния: связь с Inventor и активный документ.
- Двойной щелчок открывает документ **в уже запущенном Inventor**; если Inventor не запущен,
  файл открывается через Windows.
- «Сервис → Открыть активный проект Inventor».
- Справка → Модули: какие модули загружены.

**Связь между ними**: «Показать в Explorer» в Inventor открывает Explorer (или передаёт
команду уже запущенному) и выделяет активный документ. Подробнее — в [архитектуре](docs/ARCHITECTURE.md#связь-плагина-и-explorer).

## Сборка и установка

Требуется Windows, **.NET 8 SDK** и **Inventor 2025** (для сборки плагина нужна
`Autodesk.Inventor.Interop.dll` из папки Inventor).

Установка «как у пользователя» (в `%LOCALAPPDATA%\PdmLite`, без Visual Studio):

```powershell
# сборка и установка для текущего пользователя (Inventor и Explorer должны быть закрыты)
powershell -ExecutionPolicy Bypass -File deploy\install.ps1
```

После установки запустите Inventor: появится вкладка **PdmLite**. Explorer:
`%LOCALAPPDATA%\PdmLite\Explorer\PdmLite.Explorer.exe`.
Журналы: `%LOCALAPPDATA%\PdmLite\logs`. Удаление: `deploy\uninstall.ps1`.

### Разработка в Visual Studio

1. Откройте `PdmLite.sln` (Visual Studio 2022 17.8+, Inventor 2025 закрыт).
2. **Соберите решение** (Ctrl+Shift+B). Плагин регистрируется в Inventor автоматически:
   сборка пишет `%APPDATA%\Autodesk\Inventor 2025\Addins\PdmLite.addin` со ссылкой на
   только что собранную `PdmLite.AddIn.dll`.
3. Запуск:
   - **Inventor с плагином** — проект `PdmLite.AddIn` как запускаемый, F5 (профиль «Inventor 2025»
     запускает Inventor; точки останова в плагине работают);
   - **Explorer** — проект `PdmLite.Explorer`, F5;
   - **оба сразу** — «Настроить запускаемые проекты» → «Несколько запускаемых проектов».

Если Inventor запущен, сборка остановится с понятной ошибкой: он держит файлы плагина.
Отключить автоматическую регистрацию: `-p:PdmLiteRegisterAddIn=false`.
Если Inventor установлен не в стандартную папку: `-p:InventorInteropPath="D:\...\Autodesk.Inventor.Interop.dll"`
(и поправьте путь к `Inventor.exe` в `src/PdmLite.AddIn/Properties/launchSettings.json`).

Тесты: `dotnet test` или «Тест → Обозреватель тестов».

## Структура

```
src/
  PdmLite.Core         ядро: домен, контракты, шина событий, правила имён (без Inventor и WPF)
  PdmLite.Link         связь плагин ⇄ Explorer (именованные каналы, JSON)
  PdmLite.Hosting      загрузка модулей, DI, журналы, настройки
  PdmLite.UI.Shared    общие WPF-компоненты, тема оформления и иконки
  PdmLite.AddIn        загрузчик плагина Inventor (изолирует версии библиотек)
  PdmLite.AddIn.Core   хост: плагин Inventor
  PdmLite.Explorer     хост: внешнее приложение
modules/
  PdmLite.Modules.ProjectTree    дерево проекта
  PdmLite.Modules.Properties     карточка документа
  PdmLite.Modules.InventorLink   связь Explorer с Inventor
tests/
  PdmLite.Core.Tests
build/                 общие настройки сборки проектов с Inventor API
tools/icons/           источник иконок и генератор (Icons.xaml, .ico, предпросмотр)
deploy/                манифест плагина, установка
```

Модули включаются и отключаются в `modules.json` рядом с плагином и Explorer.

## Стек

| Что | Чем |
|---|---|
| Платформа | .NET 8, C# |
| CAD | Autodesk Inventor 2025 (Inventor API), Apprentice Server (Inventor View) |
| UI | WPF + MVVM (CommunityToolkit.Mvvm) |
| БД | PostgreSQL + EF Core (Npgsql) — следующий шаг |
| DI / журналы | Microsoft.Extensions.DependencyInjection, Serilog |
| Тесты | xUnit |

## Документация

- [Архитектура](docs/ARCHITECTURE.md)
- [Стиль оформления и иконки](docs/STYLE.md)
- [Принятые решения и открытые вопросы](docs/DECISIONS.md)
