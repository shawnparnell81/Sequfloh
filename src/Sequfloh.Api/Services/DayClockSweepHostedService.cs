using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sequfloh.Tracking;

namespace Sequfloh.Platform;

/// <summary>
/// Closes days that have reached the scheduled end even when nobody is clicking.
/// The same sweep runs on each day-clock request, so a restart or a missed tick still closes the day at the right clock time.
/// </summary>
public sealed class DayClockSweepHostedService(IServiceScopeFactory scopes, IHostEnvironment env, ILogger<DayClockSweepHostedService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (env.IsEnvironment("Testing")) return;
        try { await Task.Delay(TimeSpan.FromSeconds(20), ct); }
        catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<SequflohDayClockService>().SweepAsync(null, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { log.LogWarning(ex, "Day clock sweep did not finish"); }
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
            catch (OperationCanceledException) { return; }
        }
    }
}
