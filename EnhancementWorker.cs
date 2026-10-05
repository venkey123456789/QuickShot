namespace QuickShot;

/// <summary>One active job and one waiting path; all GPU access stays on one worker.</summary>
internal sealed class EnhancementWorker
{
    private readonly object gate = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly Func<Bitmap, Bitmap>? customUpscale;
    private EnhancementJob? waiting;
    private Task? running;
    private bool stopped;
    private Fsr1Upscaler? upscaler;
    private NisUpscaler? nis;
    private readonly ExternalAiUpscaler external;

    public event Action<EnhancementJob, string?>? Completed;

    public EnhancementWorker(Func<Bitmap, Bitmap>? upscale = null, string? engineRoot = null)
    {
        customUpscale = upscale;
        external = new ExternalAiUpscaler(engineRoot);
    }

    public bool TryEnqueue(string originalPath, UpscalingMode mode = UpscalingMode.Fsr4x)
    {
        _ = EnhancementSettings.Suffix(mode);
        var job = new EnhancementJob(Path.GetFullPath(originalPath), mode);
        lock (gate)
        {
            if (stopped) return false;
            if (running is null || running.IsCompleted)
            {
                running = Task.Run(() => Run(job));
                return true;
            }
            if (waiting is not null) return false;
            waiting = job;
            return true;
        }
    }

    private void Run(EnhancementJob job)
    {
        while (true)
        {
            string? error = null;
            try
            {
                cancellation.Token.ThrowIfCancellationRequested();
                using var source = new Bitmap(job.OriginalPath);
                EnhancementLimits.GetOutputSize(source.Width, source.Height);
                using var enhanced = customUpscale is not null
                    ? customUpscale(source)
                    : job.Mode switch
                    {
                        UpscalingMode.Fsr4x => (upscaler ??= new Fsr1Upscaler()).Upscale4x(source),
                        UpscalingMode.Nis4x => (nis ??= new NisUpscaler()).Upscale4x(source),
                        _ => external.Upscale4x(source, job.Mode, cancellation.Token)
                    };
                EnhancedFileWriter.Save(enhanced, job.OriginalPath, cancellation.Token, job.Mode);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception ex)
            {
                error = ex.Message;
                // Recreate the device on the next capture after a GPU failure.
                upscaler?.Dispose();
                upscaler = null;
                nis?.Dispose(); nis = null;
            }

            lock (gate)
            {
                // A UI subscriber must not strand a queued job or break shutdown.
                if (!stopped)
                    try { Completed?.Invoke(job, error); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
                if (stopped || waiting is null)
                {
                    running = null;
                    if (stopped) { upscaler?.Dispose(); upscaler = null; nis?.Dispose(); nis = null; }
                    return;
                }
                job = waiting;
                waiting = null;
            }
        }
    }

    public Task StopAsync()
    {
        lock (gate)
        {
            stopped = true;
            waiting = null;
            cancellation.Cancel();
            if (running is null)
            {
                upscaler?.Dispose();
                upscaler = null;
                nis?.Dispose(); nis = null;
            }
            return running ?? Task.CompletedTask;
        }
    }
}
