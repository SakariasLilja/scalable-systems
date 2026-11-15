using System.Threading.Channels;
using InitialAttempt.Models;

namespace InitialAttempt.Services;

public interface IWindowStreamConsumer
{
    ChannelReader<FinalizedWindowRecord> GetFinalizedChannel();
    event Action<FinalizedWindowRecord>? OnWindowFinalized;
}
