namespace NatsSyncLab.SharedKernel.Messaging;

public sealed class NatsOptions
{
    public const string SectionName = "Nats";

    public string Url { get; set; } = "nats://localhost:4222";

    public string StreamA { get; set; } = "A_EVENTS";

    public string StreamB { get; set; } = "B_EVENTS";

    public string StreamDeadLetter { get; set; } = "B_DEAD_LETTER";

    public string DeadLetterSubjectPrefix { get; set; } = "b.deadletter";

    public TimeSpan DuplicateWindow { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan MaxAge { get; set; } = TimeSpan.FromDays(7);

    public TimeSpan DeadLetterMaxAge { get; set; } = TimeSpan.FromDays(30);

    public long DeadLetterMaxBytes { get; set; } = 20 * 1024 * 1024;
}
