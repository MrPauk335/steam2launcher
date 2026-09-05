# Steam2 Launcher

Лаунчер для старых сборок/бет игр Valve (Half-Life 2 Beta 2003, Portal 2 2010/2009, Left 4 Dead Beta, Left 4 Dead 2 Beta и другие).

Возможности:
- Список игр из локального `games.json` (название, описание, ссылка).
- Авто-скачивание и распаковка архивов (mediafire, github, kotle.uk и др.).
- Поддержка HTTP Basic Auth (при 401 — запрос логина/пароля, сохранение по хосту).
- Запуск игр сразу после установки; авто-поиск .exe.
- Тёмная/светлая тема. WPF / .NET 8.

## Скачать

Готовый пакет — в разделе [Releases](https://github.com/MrPauk335/steam2launcher/releases):
скачай `Steam2Launcher-v1.0.0-win64.zip`, распакуй, положи рядом `games.json` и запусти.

## Сборка

```
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```

## Замечание

Файл `games.json` со списком билдов распространяется отдельно от репозитория.