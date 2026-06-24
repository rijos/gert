using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gert.TurnControl.Local;

/// <summary>
/// DI registration for the in-process turn control plane (the default <c>Local</c> impl): one process
/// where the chat API publishes cancel/answer straight to the in-memory runner subscription. The
/// composition root calls this; swapping in a networked bus (a future Kafka/NATS impl) is a one-line
/// change here - the rest of the system depends only on <see cref="ITurnControlBus"/>.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Register the in-process <see cref="ITurnControlBus"/>.</summary>
    public static IServiceCollection AddGertTurnControlLocal(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Singleton: the in-process queue means the addressed turn always runs in this process, so the
        // one broker is shared by the runner (subscriber) and the endpoints (publishers). TryAdd so a
        // host may substitute a different ITurnControlBus impl.
        services.TryAddSingleton<ITurnControlBus, InProcessTurnControlBus>();
        return services;
    }
}
