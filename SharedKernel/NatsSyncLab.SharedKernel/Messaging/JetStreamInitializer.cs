using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NatsSyncLab.SharedKernel.Contracts;

namespace NatsSyncLab.SharedKernel.Messaging;

public sealed class JetStreamInitializer(
    INatsJSContext jetStream,
    IOptions<NatsOptions> options,
    ILogger<JetStreamInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var nats = options.Value;

        await EnsureStreamAsync(nats.StreamA, Subjects.SourceA, nats, cancellationToken);
        await EnsureStreamAsync(nats.StreamB, Subjects.SourceB, nats, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task EnsureStreamAsync(
        string streamName,
        string subject,
        NatsOptions natsOptions,
        CancellationToken cancellationToken)
    {
        await jetStream.CreateOrUpdateStreamAsync(
            new StreamConfig(streamName, [subject])
            {
                Storage = StreamConfigStorage.File,
                Retention = StreamConfigRetention.Limits,
                MaxAge = natsOptions.MaxAge,
                DuplicateWindow = natsOptions.DuplicateWindow,
            },
            cancellationToken);

        logger.LogInformation(
            "JetStream stream {StreamName} ready for subject {Subject}",
            streamName,
            subject);
    }
}
