---
description: Как исправить ошибку "file is being used by another process" при сборке
---

# Исправление заблокированных DLL (MSB3021)

## Когда возникает

Ошибка `MSB3021: не удалось скопировать файл ... because it is being used by another process` появляется когда:
- В терминале была выполнена команда `[Assembly]::LoadFrom(...)` — .NET загружает DLL в процесс PowerShell и **не отпускает** до завершения процесса
- Revit запущен и держит DLL плагина
- Другой процесс (VS, Rider) держит файл

## Быстрое решение

1. **Закрыть терминал** в IDE (кнопка корзины / Kill Terminal)
2. Открыть **новый терминал**
3. Запустить сборку заново:
```powershell
dotnet clean SmartCon.sln --verbosity quiet
dotnet build SmartCon.sln --verbosity quiet
```

## Если не помогает

1. Закрыть Revit (если запущен)
2. Проверить какой процесс держит файл:
```powershell
# Установить handle из Sysinternals (один раз):
# winget install Microsoft.Sysinternals.Handle
handle64.exe SmartCon.App.dll
```
3. Убить найденный процесс или перезапустить IDE

## Профилактика

**НИКОГДА** не использовать `[Assembly]::LoadFrom()` в терминале IDE для проверки DLL.

Вместо этого для проверки embedded resources:
```powershell
# Безопасный способ — через отдельный процесс:
powershell -NoProfile -Command "[System.Reflection.Assembly]::LoadFrom('путь').GetManifestResourceNames()"
```
Или ещё лучше — написать unit-тест.
