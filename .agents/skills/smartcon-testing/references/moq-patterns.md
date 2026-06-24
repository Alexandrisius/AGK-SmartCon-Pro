# Moq Patterns — Callbacks, Returns, Verify

## Most important rule: always pair `.Callback` with `.Returns` for async

```csharp
// ❌ BROKEN — async method returns null, await throws NullReferenceException
mockService
    .Setup(s => s.MethodAsync(It.IsAny<string>()))
    .Callback<string>(arg => captured = arg);

// ✅ CORRECT — explicit Task.FromResult
mockService
    .Setup(s => s.MethodAsync(It.IsAny<string>()))
    .Callback<string>(arg => captured = arg)
    .Returns(Task.FromResult("value"));
```

This is **Moq issue #702**. Without `.Returns`, Moq returns `default(T)`, which
is `null` for `Task`/`Task<T>`.

## Basic patterns

### Setup with literal value

```csharp
mock.Setup(m => m.Property).Returns("literal");
mock.Setup(m => m.Method("specific")).Returns(42);
```

### Setup with `It.IsAny<T>` / `It.Is<T>()`

```csharp
mock.Setup(m => m.Method(It.IsAny<string>(), It.IsAny<int>())).Returns(true);
mock.Setup(m => m.Method(It.Is<string>(s => s.StartsWith("cat"))))
    .Returns("starts with cat");
mock.Setup(m => m.Method(It.IsIn("a", "b", "c"))).Returns(...);
```

### Setup async

```csharp
mock.Setup(m => m.MethodAsync(It.IsAny<string>()))
    .ReturnsAsync(new MyResult { Id = 1 });  // ReturnsAsync = .Returns(Task.FromResult(...))
```

### Setup with callback (capture arguments)

```csharp
string? capturedArg = null;
int callCount = 0;
mock.Setup(m => m.Method(It.IsAny<string>()))
    .Callback<string>(arg => { capturedArg = arg; callCount++; })
    .Returns("value");
```

### Setup with multiple parameters

```csharp
mock.Setup(m => m.Method(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
    .Callback<int, string, CancellationToken>((id, name, ct) => { ... })
    .ReturnsAsync(result);
```

## Verify patterns

### Verify call count

```csharp
mock.Verify(m => m.Method(It.IsAny<string>()), Times.Once);
mock.Verify(m => m.Method(It.IsAny<string>()), Times.Exactly(2));
mock.Verify(m => m.Method(It.IsAny<string>()), Times.Never);
mock.Verify(m => m.Method(It.IsAny<string>()), Times.AtLeastOnce);
```

### Verify call never happened (negative assertion)

```csharp
mock.Verify(m => m.BadMethod(It.IsAny<string>()), Times.Never);
```

### Verify with specific argument

```csharp
mock.Verify(m => m.Method("specific-string"), Times.Once);
```

### Verify all expectations met

```csharp
mock.Verify();  // verifies ALL Setup(...) with Verifiable() (none by default)
mock.VerifyNoOtherCalls();  // ensures only verified methods were called
```

## Setup with custom logic (Callback that returns)

For complex behavior, use `.Returns(() => ...)`:

```csharp
mock.Setup(m => m.GetValue(It.IsAny<string>()))
    .Returns(() =>
    {
        if (someCondition) return "A";
        return "B";
    });
```

For `Task<T>`-returning methods, wrap in `Task.FromResult`:

```csharp
mock.Setup(m => m.GetValueAsync(It.IsAny<string>()))
    .Returns(() => Task.FromResult(someCondition ? "A" : "B"));
```

## Setup that throws

```csharp
mock.Setup(m => m.Method(It.IsAny<string>()))
    .Throws(new InvalidOperationException("expected"));

// For async
mock.Setup(m => m.MethodAsync(It.IsAny<string>()))
    .ThrowsAsync(new InvalidOperationException("expected"));
```

## Multi-step return sequence

```csharp
mock.SetupSequence(m => m.GetValue(It.IsAny<string>()))
    .Returns("first")
    .Returns("second")
    .Returns("third");
// 4th call returns default — use .Setup() for that
```

## Strict mocks (catch unexpected calls)

```csharp
var mock = new Mock<IDependency>(MockBehavior.Strict);
// Throws on any unexpected call
```

Default is `MockBehavior.Loose` — returns `default` for unexpected calls.

## Setup property with setter (callback on change)

```csharp
mock.SetupSet(m => m.Property = It.IsAny<int>())
    .Callback<int>(value => Console.WriteLine($"Property set to {value}"));
```

## Verify property access

```csharp
mock.VerifyGet(m => m.Property, Times.AtLeastOnce);
mock.VerifySet(m => m.Property = It.IsAny<int>(), Times.Once);
```

## Setup callback that calls into another method (danger zone)

```csharp
// ❌ Risky — if the inner method is also mocked, infinite recursion possible
mock.Setup(m => m.OuterMethod(It.IsAny<string>()))
    .Callback<string>(arg => mock.Object.InnerMethod(arg));

// ✅ Safer — set state then assert
string? capturedArg = null;
mock.Setup(m => m.OuterMethod(It.IsAny<string>()))
    .Callback<string>(arg => capturedArg = arg)
    .Returns("ok");

mock.Setup(m => m.InnerMethod(It.IsAny<string>()))
    .Returns("inner result");

SUT.OuterMethod("test");
Assert.Equal("test", capturedArg);
```

## Argument matching with custom predicates

```csharp
mock.Setup(m => m.Method(It.Is<MyEntity>(e => e.IsActive)))
    .Returns("only if IsActive");
```

## When Setup fails: check parameter types

If `mock.Setup(m => m.Method(someObj))` doesn't match, Moq uses
`EqualityComparer<T>.Default`. For POCOs without overridden equality, it's
reference equality. Use `It.Is<T>(predicate)` or match by individual fields.

## Reset mocks between tests

```csharp
mock.Reset();           // clear all Setups and Verifications
mock.Invocations.Clear(); // clear call history (for Verify)
```

Or recreate the mock in the `Build()` helper per test (current SmartCon
pattern).
