namespace SmartCon.Core.Math.FormulaEngine.Solver;

/// <summary>
/// Численный решатель методом бисекции.
/// Fallback для нелинейных формул (тригонометрия, x^2, etc.).
/// Адаптивный поиск интервала + стандартная бисекция.
/// </summary>
internal static class BisectionSolver
{
    private const int MaxIterations = 200;
    private const double DefaultEpsilon = 1e-9;

    /// <summary>
    /// Найти x такой что f(x) ≈ target.
    /// Возвращает null если не удалось найти корень.
    /// </summary>
    internal static double? Solve(
        Func<double, double> f,
        double target,
        double initialLo = -1e6,
        double initialHi = 1e6,
        double eps = DefaultEpsilon)
    {
        // g(x) = f(x) - target, ищем g(x) = 0
        double g(double x)
        {
            var val = f(x) - target;
            return double.IsNaN(val) || double.IsInfinity(val) ? double.NaN : val;
        }

        // Попробовать найти интервал с переменой знака
        var (lo, hi, found) = FindBracket(g, initialLo, initialHi);
        if (!found)
            return null;

        // Стандартная бисекция
        for (int iter = 0; iter < MaxIterations; iter++)
        {
            double mid = (lo + hi) / 2.0;
            double gMid = g(mid);

            if (double.IsNaN(gMid))
                return null;

            if (System.Math.Abs(gMid) < eps)
                return mid;

            if (System.Math.Abs(hi - lo) < eps)
                return mid;

            double gLo = g(lo);
            if (double.IsNaN(gLo))
                return null;

            if ((gLo > 0) == (gMid > 0))
                lo = mid;
            else
                hi = mid;
        }

        // Вернуть лучшее приближение
        double finalMid = (lo + hi) / 2.0;
        return System.Math.Abs(g(finalMid)) < 0.01 ? finalMid : null;
    }

    /// <summary>
    /// Адаптивный поиск интервала [lo, hi] где g(lo) и g(hi) имеют разные знаки.
    /// </summary>
    private static (double Lo, double Hi, bool Found) FindBracket(
        Func<double, double> g, double lo, double hi)
    {
        // Сначала пробуем стандартные интервалы
        double[][] intervals =
        [
            [0, 1], [0, 10], [0, 100], [0, 1000], [0, 1e4], [0, 1e6],
            [-1, 1], [-10, 10], [-100, 100], [-1000, 1000],
            [0.001, 0.01], [0.01, 0.1], [0.1, 1],
            [1, 10], [1, 100], [1, 1000], [1, 1e6],
            [0.001, 1], [0.001, 10], [0.001, 100],
            [lo, hi]
        ];

        foreach (var interval in intervals)
        {
            double gLo = g(interval[0]);
            double gHi = g(interval[1]);

            if (double.IsNaN(gLo) || double.IsNaN(gHi))
                continue;

            if ((gLo > 0) != (gHi > 0))
                return (interval[0], interval[1], true);
        }

        // Экспоненциальное расширение
        double a = 0.001, b = 1;
        for (int i = 0; i < 60; i++)
        {
            double gA = g(a);
            double gB = g(b);
            if (!double.IsNaN(gA) && !double.IsNaN(gB) && (gA > 0) != (gB > 0))
                return (a, b, true);

            double span = System.Math.Max(System.Math.Abs(b - a), 1.0);
            a -= span;
            b += span * 2;
            if (b > 1e12 || a < -1e12)
                break;
        }

        return (0, 0, false);
    }
}
