using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Extensions.Microsoft.DependencyInjection;
using NATS.Net;

namespace NatsSyncLab.SharedKernel.Messaging;

public static class NatsServiceCollectionExtensions
{
    public static IServiceCollection AddNatsJetStream(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var options = configuration
            .GetSection(NatsOptions.SectionName)
            .Get<NatsOptions>() ?? new NatsOptions();

        services.AddSingleton(Options.Create(options));

        services.AddNatsClient(nats =>
            nats.ConfigureOptions(builder =>
                builder.Configure(opts => opts.Opts = opts.Opts with { Url = options.Url })));

        services.AddSingleton<INatsJSContext>(sp =>
            sp.GetRequiredService<INatsConnection>().CreateJetStreamContext());

        services.AddHostedService<JetStreamInitializer>();

        return services;
    }
}
