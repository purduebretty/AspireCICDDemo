using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.Logging;

namespace TicTacToe.AppHost;

// Writes pipeline step/task progress to stdout for CI logs when the AppHost runs WITHOUT the
// aspire CLI attached (i.e. the published binary in a pipeline deploy stage, launched as
// `dotnet TicTacToe.AppHost.dll --operation publish --step deploy`).
//
// When the CLI runs the AppHost it renders this progress itself from backchannel events; the
// AppHost's default reporter only writes to an in-memory channel the CLI reads. Standalone,
// nothing reads that channel, so deploys print almost nothing. Registering this reporter (see
// AppHost.cs — only when no backchannel is present) restores per-step output. Step-scoped
// ILogger output is routed here too (via PipelineLoggerProvider), honoring the AppHost's
// `--log-level` option (config key Pipeline:LogLevel, default Information).
internal sealed class ConsolePipelineReporter : IPipelineActivityReporter
{
    public Task<IReportingStep> CreateStepAsync(string title, CancellationToken cancellationToken = default)
        => CreateStepAsync(title, parentStepId: null, hierarchyLevel: 0, cancellationToken);

    public Task<IReportingStep> CreateStepAsync(string title, string? parentStepId, int hierarchyLevel, CancellationToken cancellationToken = default)
    {
        Write($"→ starting  {title}");
        return Task.FromResult<IReportingStep>(new ConsoleStep(title));
    }

    public Task CompletePublishAsync(PublishCompletionOptions? options = null, CancellationToken cancellationToken = default)
    {
        var state = options?.CompletionState ?? CompletionState.Completed;
        Write($"{Symbol(state)} pipeline {state}: {options?.CompletionMessage}");
        FailProcessOnError(state);
        return Task.CompletedTask;
    }

    public Task CompletePublishAsync(string? completionMessage = null, CompletionState? completionState = null, CancellationToken cancellationToken = default)
    {
        var state = completionState ?? CompletionState.Completed;
        Write($"{Symbol(state)} pipeline {state}: {completionMessage}");
        FailProcessOnError(state);
        return Task.CompletedTask;
    }

    // Standalone (no CLI attached) the AppHost process otherwise exits 0 even when the
    // pipeline fails — the CI task would go green on a failed deploy. Any step or overall
    // completion that reports an error makes the process exit non-zero.
    private static void FailProcessOnError(CompletionState state)
    {
        if (state == CompletionState.CompletedWithError)
        {
            Environment.ExitCode = 1;
        }
    }

    private static string Symbol(CompletionState state) => state switch
    {
        CompletionState.Completed => "✓",
        CompletionState.CompletedWithWarning => "⚠",
        CompletionState.CompletedWithError => "✗",
        _ => "→",
    };

    // Single WriteLine per event so parallel steps can't interleave mid-line.
    private static void Write(string message)
        => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {message}");

    private sealed class ConsoleStep(string title) : IReportingStep
    {
        private readonly long _startedAt = Environment.TickCount64;

        public Task<IReportingTask> CreateTaskAsync(string statusText, CancellationToken cancellationToken = default)
        {
            Write($"  ({title}) {statusText}");
            return Task.FromResult<IReportingTask>(new ConsoleTask(title));
        }

        public Task<IReportingTask> CreateTaskAsync(MarkdownString statusText, CancellationToken cancellationToken = default)
            => CreateTaskAsync(statusText.ToString(), cancellationToken);

        public void Log(LogLevel logLevel, string message, bool enableMarkup)
            => Write($"  ({title}) [{logLevel}] {message}");

        public void Log(LogLevel logLevel, string message) => Log(logLevel, message, enableMarkup: false);

        public void Log(LogLevel logLevel, MarkdownString message) => Log(logLevel, message.ToString(), enableMarkup: false);

        public Task CompleteAsync(string completionText, CompletionState completionState, CancellationToken cancellationToken = default)
        {
            var seconds = (Environment.TickCount64 - _startedAt) / 1000.0;
            Write($"{Symbol(completionState)} {title} ({seconds:0.0}s) {completionText}");
            FailProcessOnError(completionState);
            return Task.CompletedTask;
        }

        public Task CompleteAsync(MarkdownString completionText, CompletionState completionState, CancellationToken cancellationToken = default)
            => CompleteAsync(completionText.ToString(), completionState, cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ConsoleTask(string stepTitle) : IReportingTask
    {
        public Task UpdateAsync(string statusText, CancellationToken cancellationToken = default)
        {
            Write($"  ({stepTitle}) {statusText}");
            return Task.CompletedTask;
        }

        public Task UpdateAsync(MarkdownString statusText, CancellationToken cancellationToken = default)
            => UpdateAsync(statusText.ToString(), cancellationToken);

        public Task CompleteAsync(string? completionMessage, CompletionState completionState, CancellationToken cancellationToken = default)
        {
            if (!string.IsNullOrEmpty(completionMessage))
            {
                Write($"  ({stepTitle}) {Symbol(completionState)} {completionMessage}");
            }
            return Task.CompletedTask;
        }

        public Task CompleteAsync(MarkdownString completionMessage, CompletionState completionState, CancellationToken cancellationToken = default)
            => CompleteAsync(completionMessage.ToString(), completionState, cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
