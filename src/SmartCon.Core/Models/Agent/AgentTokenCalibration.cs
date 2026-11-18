namespace SmartCon.Core.Models.Agent;

/// <summary>
/// Learns the ratio between the local prompt-token estimate and the provider's
/// reported <c>prompt_tokens</c>. The factor is per session: the first sample
/// snaps to the observed ratio, later samples follow an EMA (70/30) clamped to
/// 0.5..2.0 so a single outlier cannot skew the estimate.
/// </summary>
public sealed class AgentTokenCalibration
{
    private const double MinFactor = 0.5;
    private const double MaxFactor = 2.0;
    private const double EmaWeight = 0.3;

    public double Factor { get; private set; } = 1.0;

    public int Samples { get; private set; }

    public void Observe(int estimatedPromptTokens, int actualPromptTokens)
    {
        if (estimatedPromptTokens <= 0 || actualPromptTokens <= 0)
            return;

        var ratio = (double)actualPromptTokens / estimatedPromptTokens;
        ratio = Clamp(ratio);

        Factor = Samples == 0
            ? ratio
            : 0.7 * Factor + EmaWeight * ratio;
        Factor = Clamp(Factor);
        Samples++;
    }

    public int Apply(int rawEstimate)
    {
        var calibrated = (int)System.Math.Round(rawEstimate * Factor);
        return calibrated < 0 ? 0 : calibrated;
    }

    private static double Clamp(double value)
    {
        if (value < MinFactor)
            return MinFactor;

        return value > MaxFactor ? MaxFactor : value;
    }
}
