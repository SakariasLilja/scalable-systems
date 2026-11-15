namespace InitialAttempt.Services;

public static class EmaCalculator
{
    public static double CalculateEma(double closePrice, double previousEma, int period)
    {
        double alpha = 2.0 / (1.0 + period);
        return (closePrice * alpha) + (previousEma * (1.0 - alpha));
    }

    public static (bool BuySignal, bool SellSignal) EvaluateBreakout(
        double currentEma38,
        double currentEma100,
        double previousEma38,
        double previousEma100)
    {
        if (previousEma38 == 0.0 && previousEma100 == 0.0)
        {
            return (false, false);
        }

        bool buySignal = currentEma38 > currentEma100 && previousEma38 <= previousEma100;
        bool sellSignal = currentEma38 < currentEma100 && previousEma38 >= previousEma100;

        return (buySignal, sellSignal);
    }
}
