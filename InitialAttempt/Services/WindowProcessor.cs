using System.Collections.Concurrent;
using InitialAttempt.Models;

namespace InitialAttempt.Services;

public interface IWindowProcessor
{
    FinalizedWindowRecord? ProcessTick(MinimalTradingEntry tick);
}

public class WindowProcessor : IWindowProcessor
{
    private class SymbolState
    {
        public DateTime WindowStart { get; set; }
        public DateTime WindowEnd { get; set; }
        public double LastPrice { get; set; }
        public double Ema38 { get; set; } = 0.0;
        public double Ema100 { get; set; } = 0.0;
        public bool Initialized { get; set; } = false;
    }

    private readonly ConcurrentDictionary<string, SymbolState> states = new();

    public FinalizedWindowRecord? ProcessTick(MinimalTradingEntry tick)
    {
        if (!tick.Last.HasValue || !tick.TradingDate.HasValue || !tick.TradingTime.HasValue)
        {
            return null;
        }

        var date = tick.TradingDate.Value;
        var time = tick.TradingTime.Value;
        int minuteBucket = (time.Minute / 5) * 5;
        var tickWindowStart = new DateTime(date.Year, date.Month, date.Day, time.Hour, minuteBucket, 0, DateTimeKind.Unspecified);
        var tickWindowEnd = tickWindowStart.AddMinutes(5);
        double currentPrice = (double)tick.Last.Value;

        var state = states.GetOrAdd(tick.Id, _ => new SymbolState
        {
            WindowStart = tickWindowStart,
            WindowEnd = tickWindowEnd,
            LastPrice = currentPrice,
            Initialized = true
        });

        lock (state)
        {
            if (!state.Initialized)
            {
                state.WindowStart = tickWindowStart;
                state.WindowEnd = tickWindowEnd;
                state.LastPrice = currentPrice;
                state.Initialized = true;
                return null;
            }

            if (tickWindowStart > state.WindowStart)
            {
                double closePrice = state.LastPrice;
                double prevEma38 = state.Ema38;
                double prevEma100 = state.Ema100;

                double newEma38 = EmaCalculator.CalculateEma(closePrice, prevEma38, 38);
                double newEma100 = EmaCalculator.CalculateEma(closePrice, prevEma100, 100);

                var (buySignal, sellSignal) = EmaCalculator.EvaluateBreakout(newEma38, newEma100, prevEma38, prevEma100);

                var record = new FinalizedWindowRecord(
                    tick.Id,
                    state.WindowStart,
                    state.WindowEnd,
                    newEma38,
                    newEma100,
                    closePrice,
                    buySignal,
                    sellSignal
                );

                state.WindowStart = tickWindowStart;
                state.WindowEnd = tickWindowEnd;
                state.LastPrice = currentPrice;
                state.Ema38 = newEma38;
                state.Ema100 = newEma100;

                return record;
            }

            state.LastPrice = currentPrice;
            return null;
        }
    }
}
