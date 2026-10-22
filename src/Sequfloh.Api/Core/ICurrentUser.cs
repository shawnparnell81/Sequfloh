namespace Sequfloh.Application.Abstractions;

public interface ICurrentUser
{
    Guid? UserId { get; }
    string UserName { get; }
    string? IpAddress { get; }
    string? CorrelationId { get; }
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
