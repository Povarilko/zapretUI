# zapret UI

Окно управления [zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube).

## Возможности

- **Статус обхода в реальном времени** — пилюли: служба zapret, процесс winws.exe,
  драйвер WinDivert, TG WS Proxy; активная стратегия и время работы
- **Тесты стратегий** — батч-прогон выбранных `.bat` с живым журналом, прогрессом,
  прогнозом оставшегося времени, таблицей результатов по группам целей и проверкой
  «Без обхода» (baseline); кнопки «Запустить лучшую» и «Запустить эту стратегию»
- **Редактор списков** — свои домены/исключения, базовые списки только для чтения,
  winws подхватывает сохранённые изменения без перезапуска
- **История запусков** — кто и как запускал стратегии, длительность, счёт последнего теста
- **TG WS Proxy** — скачивание/обновление, автозапуск, ссылка `tg://` для Telegram Desktop
- **Настройки zapret** — игровой фильтр (TCP/UDP, свои диапазоны портов), IPset
  (none / loaded / any, обновление списка с GitHub)
- **Диагностика** — те же проверки, что `Run Diagnostics` в service.bat, с автоисправлениями
  (TCP timestamps, зависший WinDivert) и кнопками удаления конфликтующих служб
- **Перенос и бэкап** — экспорт/импорт настроек в один `.zip`, миграция из старой папки zapret
- **Автообновление** zapret с GitHub, установка в новую папку рядом и переезд настроек
- **Оформление** — светлая/тёмная тема как в Windows, акцент из «Персонализации»,
  тёмный заголовок окна
- Ярлыки на рабочий стол и в «Пуск», создание службы Windows, ручной и сервисный запуск стратегий

## Системные требования

- Windows 10 (1607+) или Windows 11, **x64**
- Права администратора (нужны для управления службой и чтения командной строки winws)
- [.NET Desktop Runtime 7](https://dotnet.microsoft.com/download/dotnet/7.0) —
  **установится автоматически** при первом запуске, если его нет

## Установка

1. Положите `zapret-ui.exe` рядом с `service.bat` в папке zapret
   (или запустите где угодно — окно само предложит скачать/указать zapret).
2. Запустите → подтвердите запрос UAC.

Всё. Файл один, ничего больше не нужно: загрузчик распакует приложение
в `%LOCALAPPDATA%\Programs\zapret-ui` и откроет окно.

## Сборка из исходников

```powershell
# Требуется: .NET 7 SDK и Visual Studio 2022 с рабочей нагрузкой «C++»
.\build-release.ps1
```

Итог: `NativeLauncher\publish\zapret-ui.exe` (+ `zapret-ui.zip` рядом, если заархивировать).

Что делает сборка:

1. `dotnet publish` основного приложения в варианте framework-dependent — это payload.
2. Копирует payload к исходникам нативного загрузчика.
3. `rc.exe` + `cl.exe` (MSVC): линкует ~60 КБ нативного кода и встроенный payload
   в один `zapret-ui.exe`.

## Как устроено

```
zapret-ui.csproj        WPF-приложение (net7.0-windows)
├─ App.xaml(.cs)        запуск, права администратора, тема
├─ Core/                логика без привязки к окну
│  ├─ Zapret.cs         стратегии, служба (sc.exe + SCM API), игровой фильтр, IPset
│  ├─ ProcCmd.cs        P/Invoke: командная строка процессов через PEB, дерево процессов,
│  │                    запуск с перенаправленными stdin/stdout, статус служб через SCM
│  ├─ TestRunner.cs     запуск test zapret.ps1, разбор лога (CP866) по ходу, прогнозы
│  ├─ ResultsModel.cs   модель результатов, чтение файлов test_results_*.txt
│  ├─ Targets.cs        модель targets.txt (группы и адреса теста)
│  ├─ TgProxy.cs        TG WS Proxy: релизы с GitHub, установка, автозапуск
│  ├─ Updater.cs        проверка обновлений zapret, установка релизов
│  ├─ Migration.cs      экспорт/импорт/перенос настроек
│  ├─ Diagnostics.cs    проверки из Run Diagnostics service.bat
│  ├─ Shortcuts.cs      ярлыки через IShellLink (COM), без WScript.Shell
│  ├─ UiState.cs        utils\ui-state.json — тот же формат, что у PowerShell-версии
│  ├─ Theme.cs          палитры тем, кисти с привязкой к источникам
│  └─ Oem.cs            встроенная таблица CP866 — без пакетов кодировок
├─ MainWindow*.cs       окно: статус, результаты, история, списки, настройки, TG
├─ Ui/                  XAML-диалоги (10 шт.) + Theme.xaml — Fluent-стили
└─ NativeLauncher/      загрузчик на чистом C (Win32)
   ├─ launcher.c        UAC, проверка/доустановка .NET Desktop Runtime 7,
   │                    распаковка payload в %LOCALAPPDATA%\Programs\zapret-ui\<хеш>
   └─ launcher.rc       VERSIONINFO + payload как ресурсы
```

## Ссылки

- [Flowseal/zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube) —
  сам обход DPI, без которого это окно бесполезно
- [Flowseal/tg-ws-proxy](https://github.com/Flowseal/tg-ws-proxy) — TG WS Proxy

