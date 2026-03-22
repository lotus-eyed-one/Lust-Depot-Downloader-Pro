using Spectre.Console;
using LustsDepotDownloaderPro.Core;
using LustsDepotDownloaderPro.Utils;
using LustsDepotDownloaderPro.Models;

namespace LustsDepotDownloaderPro.UI;

/// <summary>
/// Clean release-mode UI.
/// Shows: game name, progress bar, %, speed, ETA.
/// All Logger.Info/Warn traffic is silenced to file-only while rendering.
/// </summary>
public class TerminalUI
{
    private string _appName = "";

    public void ShowHeader()
    {
        var version = EmbeddedConfig.AppVersion;
        AnsiConsole.Write(new Rule($"[bold cyan]Lusts Depot Downloader Pro v{version}[/]")
            { Justification = Justify.Center });
        AnsiConsole.WriteLine();
    }

    public void ShowStatus(string message) =>
        AnsiConsole.MarkupLine($"[cyan]i  {EscapeMarkup(message)}[/]");

    public void SetAppName(string name) => _appName = name;

    public async Task RunDownloadWithProgressAsync(DownloadEngine engine, DownloadSession session)
    {
        Logger.SilentMode = true;

        try
        {
            string header = string.IsNullOrWhiteSpace(_appName)
                ? $"AppID {session.AppId}"
                : _appName;

            AnsiConsole.WriteLine();

            await AnsiConsole.Progress()
                .AutoClear(false)
                .HideCompleted(false)
                .Columns(
                    new TaskDescriptionColumn { Alignment = Justify.Left },
                    new ProgressBarColumn     { Width = 34 },
                    new PercentageColumn(),
                    new RemainingTimeColumn(),
                    new SpinnerColumn(Spinner.Known.Dots))
                .StartAsync(async ctx =>
                {
                    var dlTask = ctx.AddTask(
                        $"[cyan]{EscapeMarkup(header)}[/]",
                        maxValue: 100);

                    // Pre-set % from last checkpoint so it doesn't flash 0% on resume
                    var initialStats = engine.GetStatistics();
                    if (initialStats.Percent > 0)
                        dlTask.Value = initialStats.Percent;

                    engine.ProgressChanged += (_, e) =>
                    {
                        dlTask.Value = e.PercentComplete;

                        string fileName = !string.IsNullOrEmpty(e.CurrentFile)
                            ? Path.GetFileName(e.CurrentFile)
                            : "...";

                        if (fileName.Length > 38)
                            fileName = "..." + fileName[^35..];

                        string speedStr = e.SpeedMBps >= 0.01
                            ? $"  {e.SpeedMBps:F1} MB/s"
                            : "";

                        dlTask.Description =
                            $"[cyan]{EscapeMarkup(header)}[/]" +
                            $"  [grey]{EscapeMarkup(fileName)}{speedStr}[/]";
                    };

                    await engine.RunAsync();

                    dlTask.Value = 100;
                    dlTask.StopTask();
                });

            AnsiConsole.WriteLine();

            if (session.WasCancelled)
            {
                AnsiConsole.MarkupLine("[yellow]Paused — progress saved. Resume with:[/]");
                AnsiConsole.MarkupLine(
                    $"[grey]  --app {session.AppId} " +
                    $"--output \"{EscapeMarkup(session.OutputDir)}\" --resume[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("[bold green]  Download complete![/]");
            }
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C — engine already saved progress
        }
        finally
        {
            Logger.SilentMode = false;
        }
    }

    public void ShowSummary(DownloadStatistics stats)
    {
        var t = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Cyan1)
            .AddColumn("Metric").AddColumn("Value");

        t.AddRow("Downloaded", $"[green]{stats.DownloadedMB:F2} MB[/]");
        t.AddRow("Total",      $"{stats.TotalMB:F2} MB");
        t.AddRow("Progress",   $"[cyan]{stats.Percent:F1}%[/]");
        t.AddRow("Speed",      $"[yellow]{stats.SpeedMBps:F2} MB/s[/]");
        t.AddRow("Chunks",     $"{stats.CompletedChunks}/{stats.TotalChunks}");
        t.AddRow("Status",     stats.IsCompleted ? "[green]Complete[/]" :
                               stats.IsPaused    ? "[yellow]Paused[/]" : "[cyan]Running[/]");
        AnsiConsole.Write(t);
    }

    private static string EscapeMarkup(string s) =>
        s.Replace("[", "[[").Replace("]", "]]");
}
