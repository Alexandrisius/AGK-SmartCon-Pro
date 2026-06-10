---
module: formula-engine
---
# Formula Engine (AST Parser)

> Загружать: при работе с парсингом формул Revit.
> Источник истины: `src/SmartCon.Core/Math/FormulaEngine/*.cs` и `Ast/*.cs`, `Solver/*.cs`.

AST-парсер и решатель формул Revit (ADR-005). Все типы — internal, живут в `SmartCon.Core/Math/FormulaEngine/`.

## Token

Токен лексера.

**Файл:** `FormulaEngine/Token.cs`

## TokenType

Тип токена (number, identifier, operator, etc.).

**Файл:** `FormulaEngine/TokenType.cs`

## Tokenizer

Лексер: разбирает строку формулы в список токенов.

**Файл:** `FormulaEngine/Tokenizer.cs`

---

## AstNode

Базовый класс узла AST.

**Файл:** `FormulaEngine/Ast/AstNode.cs`

## NumberNode

Узел AST: числовой литерал.

**Файл:** `FormulaEngine/Ast/NumberNode.cs`

## VariableNode

Узел AST: переменная (ссылка на параметр).

**Файл:** `FormulaEngine/Ast/VariableNode.cs`

## UnaryOpNode

Узел AST: унарная операция (отрицание, логическое NOT).

**Файл:** `FormulaEngine/Ast/UnaryOpNode.cs`

## BinaryOpNode

Узел AST: бинарная операция (Plus, Minus, Multiply, Divide, And, Or, Eq, Lt, ...).

**Файл:** `FormulaEngine/Ast/BinaryOpNode.cs`

## FunctionCallNode

Узел AST: вызов функции (sin, cos, log, exp, if, and, or, not, size_lookup).

**Файл:** `FormulaEngine/Ast/FunctionCallNode.cs`

## IfNode

Узел AST: if(condition, then, else) — тернарный условный оператор.

**Файл:** `FormulaEngine/Ast/IfNode.cs`

## SizeLookupNode

Узел AST: size_lookup(TableName, ...args).

**Файл:** `FormulaEngine/Ast/SizeLookupNode.cs`

---

## UnaryOp

Перечисление унарных операторов.

**Файл:** `FormulaEngine/Ast/UnaryOp.cs`

## BinaryOp

Перечисление бинарных операторов.

**Файл:** `FormulaEngine/Ast/BinaryOp.cs`

---

## Parser

Рекурсивный спуск-парсер формул Revit. Преобразует токены в AST.

**Файл:** `FormulaEngine/Parser.cs`

## Evaluator

Интерпретатор AST — вычисляет значение при заданных параметрах.

**Файл:** `FormulaEngine/Evaluator.cs`

## FormulaSolver

Единая точка входа: Evaluate + SolveFor (ADR-005). Использует IfSimplifier → AlgebraicInverter → BisectionSolver.

**Файл:** `FormulaEngine/Solver/FormulaSolver.cs`

## IfSimplifier

Упрощает if()-ветки при известных условиях.

**Файл:** `FormulaEngine/Solver/IfSimplifier.cs`

## AlgebraicInverter

Алгебраическая инверсия простых формул (a = b + c → b = a - c).

**Файл:** `FormulaEngine/Solver/AlgebraicInverter.cs`

## BisectionSolver

Численное решение методом бисекции для нелинейных формул.

**Файл:** `FormulaEngine/Solver/BisectionSolver.cs`

## VariableExtractor

Извлекает список переменных из формулы.

**Файл:** `FormulaEngine/Solver/VariableExtractor.cs`

---

## SizeLookupParser

Парсинг size_lookup(...) выражений.

**Файл:** `FormulaEngine/SizeLookupParser.cs`

## UnitStripper

Удаление единиц измерения из строки формулы (mm, m, etc.).

**Файл:** `FormulaEngine/UnitStripper.cs`

## FormulaParseException

Исключение при ошибке парсинга формулы.

**Файл:** `FormulaEngine/FormulaParseException.cs`
