using System.Runtime.CompilerServices;

namespace SmartCon.Core.Common;

public static class Guard
{
    public static void ThrowIfNull<T>(T? value, [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        if (value is null)
        {
            throw new ArgumentNullException(paramName);
        }
    }
}
