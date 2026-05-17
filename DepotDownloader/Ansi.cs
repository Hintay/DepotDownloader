// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System;
using System.Threading.Tasks;
using Spectre.Console;

namespace DepotDownloader;

static class Ansi
{
    // https://conemu.github.io/en/AnsiEscapeCodes.html#ConEmu_specific_OSC
    // https://learn.microsoft.com/en-us/windows/terminal/tutorials/progress-bar-sequences
    public enum ProgressState
    {
        Hidden = 0,
        Default = 1,
        Error = 2,
        Indeterminate = 3,
        Warning = 4,
    }

    const char ESC = (char)0x1B;
    const char BEL = (char)0x07;

    private static bool useProgress;

    public static bool CanUseInteractiveProgress => !Console.IsInputRedirected && !Console.IsOutputRedirected;

    public static void Init()
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            return;
        }

        var (supportsAnsi, legacyConsole) = AnsiDetector.Detect(stdError: false, upgrade: true);

        useProgress = supportsAnsi && !legacyConsole;
    }

    public static void Progress(ulong downloaded, ulong total)
    {
        if (total == 0)
        {
            Progress(ProgressState.Default, 0);
            return;
        }

        var progress = (byte)MathF.Round(downloaded / (float)total * 100.0f);
        Progress(ProgressState.Default, progress);
    }

    public static string FormatBytes(double bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }

    public static void Progress(ProgressState state, byte progress = 0)
    {
        if (!useProgress)
        {
            return;
        }

        Console.Write($"{ESC}]9;4;{(byte)state};{progress}{BEL}");
    }

    public static async Task RunWithProgressAsync(GlobalDownloadCounter counter, Func<Task> action)
    {
        await AnsiConsole.Progress()
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(
                new TaskDescriptionColumn { Alignment = Justify.Left },
                new ProgressBarColumn { Width = 40 },
                new PercentageColumn(),
                new RemainingTimeColumn())
            .StartAsync(async ctx =>
            {
                var maxValue = counter.totalDownloadSize == 0 ? 1.0 : counter.totalDownloadSize;
                var downloadTask = ctx.AddTask("Preparing", maxValue: maxValue);
                counter.AttachProgressTasks(downloadTask, () => ctx.AddTask("Verifying", autoStart: true, maxValue: 1.0));

                try
                {
                    await action().ConfigureAwait(false);
                }
                finally
                {
                    counter.FinishVerify();
                    if (!downloadTask.IsFinished)
                    {
                        downloadTask.Value = downloadTask.MaxValue;
                    }
                }
            }).ConfigureAwait(false);
    }
}

class GlobalDownloadCounter
{
    public ulong completeDownloadSize;
    public ulong totalBytesCompressed;
    public ulong totalBytesUncompressed;
    public ulong totalDownloadSize;

    DateTime downloadStartTime;
    uint currentDepotId;
    int currentDepotCompletedChunks;
    int currentDepotTotalChunks;
    ulong diskBytesWritten;
    long diskWriteTicks;
    bool useInteractiveOutput;

    ProgressTask progressTask;
    ProgressTask verifyTask;
    Func<ProgressTask> verifyTaskFactory;
    ulong verifyTotalBytes;
    ulong verifyDoneBytes;
    int verifyTotalChunks;
    int verifyDoneChunks;
    DateTime? verifyStartTime;
    DateTime? networkStartTime;

    public void Begin(ulong totalSize, bool useInteractiveProgress)
    {
        totalDownloadSize = totalSize;
        useInteractiveOutput = useInteractiveProgress;
        downloadStartTime = DateTime.UtcNow;

        Ansi.Progress(0, totalSize);
    }

    public void AttachProgressTasks(ProgressTask downloadTask, Func<ProgressTask> verifyFactory)
    {
        progressTask = downloadTask;
        verifyTaskFactory = verifyFactory;

        lock (this)
        {
            if (verifyTotalBytes > 0)
            {
                EnsureVerifyTask();
            }
        }

        RefreshProgressTask();
    }

    public void RegisterVerifyWork(ulong bytes, int chunks)
    {
        if (bytes == 0 && chunks == 0)
        {
            return;
        }

        lock (this)
        {
            verifyTotalBytes += bytes;
            verifyTotalChunks += chunks;
            EnsureVerifyTask();

            if (verifyTask != null)
            {
                verifyTask.MaxValue = verifyTotalBytes;
                verifyTask.Description = BuildVerifyDescription();
            }
        }
    }

    public void AddVerifiedChunk(ulong bytes)
    {
        lock (this)
        {
            verifyStartTime ??= DateTime.UtcNow;
            verifyDoneBytes += bytes;
            verifyDoneChunks++;

            if (verifyTask != null)
            {
                verifyTask.Value = verifyDoneBytes;
                verifyTask.Description = BuildVerifyDescription();
            }
        }
    }

    public void FinishVerify()
    {
        lock (this)
        {
            if (verifyTask == null)
            {
                return;
            }

            if (verifyTotalBytes > 0)
            {
                verifyTask.MaxValue = verifyTotalBytes;
                verifyTask.Value = verifyTotalBytes;
                verifyDoneBytes = verifyTotalBytes;
            }

            if (verifyDoneChunks < verifyTotalChunks)
            {
                verifyDoneChunks = verifyTotalChunks;
            }

            verifyTask.Description = BuildVerifyDescription();

            if (!verifyTask.IsFinished)
            {
                verifyTask.StopTask();
            }
        }
    }

    void EnsureVerifyTask()
    {
        if (verifyTask != null || verifyTaskFactory == null)
        {
            return;
        }

        verifyTask = verifyTaskFactory();
        verifyTask.MaxValue = verifyTotalBytes == 0 ? 1.0 : verifyTotalBytes;
        verifyTask.Description = BuildVerifyDescription();
    }

    string BuildVerifyDescription()
    {
        if (verifyTotalChunks == 0 && verifyTotalBytes == 0)
        {
            return "Verifying";
        }

        double bytesPerSecond = 0;
        if (verifyStartTime.HasValue)
        {
            var elapsed = DateTime.UtcNow - verifyStartTime.Value;
            if (elapsed.TotalSeconds > 0)
            {
                bytesPerSecond = verifyDoneBytes / elapsed.TotalSeconds;
            }
        }

        return $"Verifying  | C {verifyDoneChunks}/{verifyTotalChunks} | Disk {Ansi.FormatBytes(bytesPerSecond)}/s";
    }

    public void SetCurrentDepot(uint depotId, int completedChunks = 0, int totalChunks = 0)
    {
        lock (this)
        {
            currentDepotId = depotId;
            currentDepotCompletedChunks = completedChunks;
            currentDepotTotalChunks = totalChunks;
            UpdateProgressDisplay();
        }
    }

    public void SetCurrentDepotChunks(int completedChunks, int totalChunks)
    {
        lock (this)
        {
            currentDepotCompletedChunks = completedChunks;
            currentDepotTotalChunks = totalChunks;
            UpdateProgressDisplay();
        }
    }

    public void Log(string format, params object[] args)
    {
        var message = string.Format(format, args);

        if (!useInteractiveOutput)
        {
            Console.WriteLine(message);
            return;
        }

        AnsiConsole.WriteLine(message);
    }

    public void InteractiveLog(string format, params object[] args)
    {
        if (!useInteractiveOutput)
        {
            return;
        }

        AnsiConsole.WriteLine(string.Format(format, args));
    }

    public void FileCompleted(float depotPercent, string filePath)
    {
        Log("{0,6:#00.00}% {1}", depotPercent, filePath);
    }

    public void AddCompletedBytes(ulong bytes)
    {
        lock (this)
        {
            if (completeDownloadSize >= bytes)
            {
                completeDownloadSize -= bytes;
            }
            else
            {
                completeDownloadSize = 0;
            }

            UpdateProgressDisplay();
        }
    }

    public void AddCompletedChunk(ulong bytes, int completedChunks, int totalChunks, ulong diskBytes, TimeSpan diskElapsed)
    {
        lock (this)
        {
            networkStartTime ??= DateTime.UtcNow;
            currentDepotCompletedChunks = completedChunks;
            currentDepotTotalChunks = totalChunks;
            diskBytesWritten += diskBytes;
            diskWriteTicks += diskElapsed.Ticks;

            if (completeDownloadSize >= bytes)
            {
                completeDownloadSize -= bytes;
            }
            else
            {
                completeDownloadSize = 0;
            }

            UpdateProgressDisplay();
        }
    }

    public void Finish()
    {
        lock (this)
        {
            Ansi.Progress(totalDownloadSize, totalDownloadSize);

            if (progressTask != null && totalDownloadSize > 0)
            {
                progressTask.MaxValue = totalDownloadSize;
                progressTask.Value = totalDownloadSize;
                progressTask.Description = BuildProgressDescription();
            }
        }
    }

    void UpdateProgressDisplay()
    {
        if (totalDownloadSize == 0)
        {
            return;
        }

        var downloaded = totalDownloadSize - completeDownloadSize;

        Ansi.Progress(downloaded, totalDownloadSize);
        RefreshProgressTask();
    }

    void RefreshProgressTask()
    {
        if (progressTask == null)
        {
            return;
        }

        if (totalDownloadSize > 0)
        {
            progressTask.MaxValue = totalDownloadSize;
            progressTask.Value = totalDownloadSize - completeDownloadSize;
        }

        progressTask.Description = BuildProgressDescription();
    }

    string BuildProgressDescription()
    {
        if (totalDownloadSize == 0)
        {
            return currentDepotId == 0 ? "Preparing" : $"Depot {currentDepotId}";
        }

        double bytesPerSecond = 0;
        if (networkStartTime.HasValue)
        {
            var elapsed = DateTime.UtcNow - networkStartTime.Value;
            if (elapsed.TotalSeconds > 0)
            {
                bytesPerSecond = totalBytesUncompressed / elapsed.TotalSeconds;
            }
        }

        var diskElapsed = TimeSpan.FromTicks(diskWriteTicks);
        var diskBytesPerSecond = diskElapsed.TotalSeconds > 0 ? diskBytesWritten / diskElapsed.TotalSeconds : 0;

        return $"Depot {currentDepotId} | C {currentDepotCompletedChunks}/{currentDepotTotalChunks} | Net {Ansi.FormatBytes(bytesPerSecond)}/s | Disk {Ansi.FormatBytes(diskBytesPerSecond)}/s";
    }
}
