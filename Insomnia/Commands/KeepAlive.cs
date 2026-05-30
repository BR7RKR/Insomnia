using System.ComponentModel;
using System.Diagnostics;
using Insomnia.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Insomnia.Commands;

public sealed class KeepAliveCommand : AsyncCommand<KeepAliveCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandArgument(0, "<alive-time>")]
        [Description("The amount of milliseconds to keep the computer awake. Use 0 to run until cancelled.")]
        public long AliveTime { get; init; } = 0;

        [CommandArgument(1, "<turn-off>")]
        [Description("Turn off PC after the app's work is over")]
        public bool IsTurnOff { get; init; } = false;
    }
    
    private readonly IShutdownTrackerService _shutdownTracker;
    private readonly ISleepTrackerService _sleepTracker;
    private readonly TimeProvider _timeProvider;
    
    public KeepAliveCommand(
        IShutdownTrackerService shutdownTracker,
        ISleepTrackerService sleepTracker,
        TimeProvider? timeProvider = null)
    {
        _shutdownTracker = shutdownTracker;
        _sleepTracker = sleepTracker;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        Settings settings,
        CancellationToken cancellationToken)
    {
        var shouldTurnOff = false;
        var aliveTime = ToAliveTime(settings.AliveTime);

        try
        {
            _sleepTracker.Start();
            _shutdownTracker.Start();

            AnsiConsole.MarkupLine("[green]Insomnia is active.[/]");
            AnsiConsole.MarkupLine("[grey]Automatic sleep is disabled and shutdown requests will be rejected when Windows allows it.[/]");

            await WaitAsync(aliveTime, cancellationToken);

            shouldTurnOff = settings.IsTurnOff && !cancellationToken.IsCancellationRequested;
        }
        finally
        {
            _shutdownTracker.Stop();
            _sleepTracker.Stop();
        }

        if (shouldTurnOff)
        { 
            TurnOffComputer();
        }

        return 0;
    }

    private static TimeSpan ToAliveTime(long aliveTime)
    {
        if (aliveTime <= 0)
            return Timeout.InfiniteTimeSpan;

        return TimeSpan.FromMilliseconds(aliveTime);
    }

    private async Task WaitAsync(TimeSpan aliveTime, CancellationToken cancellationToken)
    {
        if (aliveTime == Timeout.InfiniteTimeSpan)
        {
            AnsiConsole.MarkupLine("[grey]Press Ctrl+C to stop.[/]");
            await DelayAsync(Timeout.InfiniteTimeSpan, cancellationToken);
            return;
        }

        var endTime = _timeProvider.GetUtcNow() + aliveTime;
        while (!cancellationToken.IsCancellationRequested)
        {
            var remaining = endTime - _timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
                return;

            AnsiConsole.MarkupLine($"[grey]Remaining: {Math.Max(0, (long)remaining.TotalSeconds)}s[/]");
            await DelayAsync(TimeSpan.FromSeconds(1) < remaining ? TimeSpan.FromSeconds(1) : remaining, cancellationToken);
        }
    }

    private async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, _timeProvider, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static void TurnOffComputer()
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "shutdown",
            Arguments = "/s /t 0",
            CreateNoWindow = true,
            UseShellExecute = false
        });

        process?.WaitForExit();
    }
}
