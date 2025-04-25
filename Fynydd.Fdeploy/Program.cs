﻿namespace Fynydd.Fdeploy;

internal abstract class Program
{
	private static async Task Main(string[] args)
	{
        Console.OutputEncoding = Encoding.UTF8;

        var totalTimer = new Stopwatch();

        totalTimer.Start();
        
        var runner = new AppRunner(args);

        if (runner.AppState.CancellationTokenSource.IsCancellationRequested)
        {
            await runner.OutputExceptionsAsync();
        }

        await runner.DeployAsync();
        
        if (runner.AppState.CancellationTokenSource.IsCancellationRequested)
        {
            await runner.OutputExceptionsAsync();
            await Console.Out.WriteLineAsync();
        }

        else
        {
            if (runner is { VersionMode: false, HelpMode: false, InitMode: false })
            {
                await Console.Out.WriteLineAsync();
                await AppRunner.ColonOutAsync("Completed Deployment", $"{DateTime.Now:HH:mm:ss.fff}");
            }
        }
        
        if (runner is { VersionMode: false, HelpMode: false, InitMode: false })
            await AppRunner.ColonOutAsync("Total Run Time", $"{totalTimer.Elapsed.FormatElapsedTime()}");

        await Console.Out.WriteLineAsync();

        Environment.Exit(runner.AppState.CancellationTokenSource.IsCancellationRequested ? 1 : 0);
    }
}
