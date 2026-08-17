using OrderService.Application;

namespace OrderService.IntegrationTests.Fixtures;

public sealed class TestClock(DateTimeOffset initial) : IClock
{
    private long _ticks = initial.UtcTicks;
    public DateTimeOffset UtcNow => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
    public void Set(DateTimeOffset value) => Interlocked.Exchange(ref _ticks, value.UtcTicks);
    public void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks, duration.Ticks);
}
