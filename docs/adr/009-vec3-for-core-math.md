# ADR-009: Vec3 вместо XYZ для чистой математики в Core

**Статус:** accepted
**Дата:** 2026-03-26

## Контекст

`VectorUtils` (Core/Math) содержит базовые векторные операции для алгоритмов выравнивания (ConnectorAligner). Изначально планировалось использовать `Autodesk.Revit.DB.XYZ` напрямую — это допускалось I-09 как value-carrier.

**Проблема:** `RevitAPI.dll` в Revit 2025 (.NET 8) имеет нативные зависимости. `new XYZ(x,y,z)` невозможно вызвать вне процесса Revit — CLR не может загрузить сборку. Это делает `VectorUtils` **нетестируемым** в unit-тестах (xUnit, `dotnet test`).

## Решение

Создан `Vec3` — лёгкий `readonly record struct` в `SmartCon.Core.Math`:

```csharp
public readonly record struct Vec3(double X, double Y, double Z);
```

- `VectorUtils` работает **только** с `Vec3`
- Конвертация `XYZ ↔ Vec3` — в `SmartCon.Revit/Extensions/XYZExtensions.cs`:
  - `XYZ.ToVec3()` → `Vec3`
  - `Vec3.ToXYZ()` → `XYZ`
- `ConnectorProxy` и другие модели **сохраняют** `XYZ` / `ElementId` / `Domain` как carriers (I-09)
- Алгоритмы (ConnectorAligner, будущие фазы) конвертируют на входе/выходе

## Последствия

**Плюсы:**
- `VectorUtils` полностью тестируем без Revit runtime (54/54 тестов проходят)
- Core/Math не зависит от RevitAPI.dll даже в runtime
- `Vec3` имеет операторы `+`, `-`, `*`, что делает код алгоритмов чище

**Минусы:**
- Необходимость конвертации `.ToVec3()` / `.ToXYZ()` на границе Core ↔ Revit
- Два представления вектора в проекте (XYZ для Revit API, Vec3 для математики)

## Альтернативы рассмотренные

1. **Тестировать только в Revit** — непрактично, замедляет разработку
2. **Заменить XYZ на Vec3 везде (включая модели)** — слишком много конвертаций при работе с Revit API
3. **System.Numerics.Vector3** — float вместо double, потеря точности неприемлема

## Обновление I-09

I-09 уточнён: Revit value-типы (XYZ, ElementId, Domain) разрешены как carriers **в моделях и интерфейсах**. Для чистой математики в Core/Math используется Vec3.
