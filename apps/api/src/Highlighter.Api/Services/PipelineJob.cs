using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Highlighter.Api.Contracts;

namespace Highlighter.Api.Services;

public enum JobState { Starting, Running, Succeeded, Failed, Killed }

/// <summary>Marks a job as chat-started: when it ends, the supervisor writes a
/// completion message into agent_messages for this project + chat context.</summary>
public sealed record AgentChatRef(Guid ProjectId, string Context);

/// <summary>One tracked worker process: state machine, in-memory log window,
/// durable file sink, and live subscriber fan-out. Mutations run under one gate;
/// a slow SSE subscriber can never block the pumps (bounded channels, DropOldest).</summary>
public sealed class PipelineJob
{
    private readonly object _gate = new();
    private readonly LogRingBuffer _buffer;
    private readonly List<Channel<JobLogLineDto>> _subscribers = [];
    private StreamWriter? _sink;

    internal PipelineJob(
        string id, string kind, Guid? projectId, IReadOnlyList<string> argv,
        string logPath, Guid? ownerId = null, int bufferCapacity = 4000)
    {
        Id = id;
        Kind = kind;
        ProjectId = projectId;
        OwnerId = ownerId;
        Argv = argv;
        LogPath = logPath;
        StartedAt = DateTimeOffset.UtcNow;
        _buffer = new LogRingBuffer(bufferCapacity);
    }

    public string Id { get; }
    public string Kind { get; }
    public Guid? ProjectId { get; }

    /// <summary>User the job was started for; null for system jobs (cleanup).
    /// Job reads are scoped on it exactly like project reads are on user_id.</summary>
    public Guid? OwnerId { get; }

    public IReadOnlyList<string> Argv { get; }
    public string LogPath { get; }
    public DateTimeOffset StartedAt { get; }
    public JobState State { get; private set; } = JobState.Starting;
    public int? ExitCode { get; private set; }
    public string? FailureReason { get; private set; }
    public DateTimeOffset? EndedAt { get; private set; }
    public bool KillRequested { get; private set; }

    internal Process? Process { get; set; }

    /// <summary>Set when the studio agent started this job from chat.</summary>
    internal AgentChatRef? AgentChat { get; set; }

    /// <summary>In-process jobs have no OS process to signal; force-cancel
    /// cancels this instead.</summary>
    internal CancellationTokenSource? ExternalCts { get; set; }

    internal void RequestKill()
    {
        lock (_gate) KillRequested = true;
    }

    /// <summary>True when this job is visible to the given user scope (null
    /// scope = auth off = everything visible, matching project reads).</summary>
    public bool VisibleTo(Guid? userId) => userId is null || OwnerId == userId;

    public bool IsTerminal => State is JobState.Succeeded or JobState.Failed or JobState.Killed;

    public JobDto ToDto()
    {
        lock (_gate)
            return new JobDto(Id, Kind, ProjectId, State.ToString().ToLowerInvariant(), Argv,
                ExitCode, FailureReason, LogPath, StartedAt, EndedAt, _buffer.LastSeq);
    }

    public List<JobLogLineDto> Tail(int count) => _buffer.Tail(count);

    /// <summary>Replays the last <paramref name="tail"/> lines, then follows live
    /// output until the job ends. Snapshot and subscription happen under one lock,
    /// so nothing is missed or duplicated in between.</summary>
    public async IAsyncEnumerable<JobLogLineDto> StreamLogsAsync(
        int tail, [EnumeratorCancellation] CancellationToken ct = default)
    {
        Channel<JobLogLineDto>? channel = null;
        List<JobLogLineDto> snapshot;
        lock (_gate)
        {
            snapshot = _buffer.Tail(tail);
            if (!IsTerminal)
            {
                channel = Channel.CreateBounded<JobLogLineDto>(new BoundedChannelOptions(1024)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                });
                _subscribers.Add(channel);
            }
        }
        try
        {
            foreach (var line in snapshot) yield return line;
            if (channel is not null)
                await foreach (var line in channel.Reader.ReadAllAsync(ct)) yield return line;
        }
        finally
        {
            if (channel is not null)
                lock (_gate) _subscribers.Remove(channel);
        }
    }

    internal void OpenSink(IEnumerable<string> headerLines)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            _sink = new StreamWriter(
                new FileStream(LogPath, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                AutoFlush = true,
            };
            foreach (var line in headerLines)
                _sink.WriteLine(Format("api", line, DateTimeOffset.UtcNow));
        }
    }

    internal void Append(string stream, string line)
    {
        lock (_gate)
        {
            var dto = _buffer.Append(stream, line, DateTimeOffset.UtcNow);
            _sink?.WriteLine(Format(stream, line, dto.At));
            foreach (var subscriber in _subscribers) subscriber.Writer.TryWrite(dto);
        }
    }

    internal void MarkRunning()
    {
        lock (_gate) State = JobState.Running;
    }

    internal void MarkExited(int exitCode, string? rowStatusNote)
    {
        lock (_gate)
        {
            ExitCode = exitCode;
            EndedAt = DateTimeOffset.UtcNow;
            State = KillRequested ? JobState.Killed
                : exitCode == 0 ? JobState.Succeeded
                : JobState.Failed;
            FailureReason = State switch
            {
                JobState.Failed => $"worker exited with code {exitCode}",
                JobState.Killed => "killed by force-cancel",
                _ => null,
            };
            CloseSinkLocked(rowStatusNote);
            CompleteSubscribersLocked();
        }
    }

    /// <summary>Terminal failure without a process exit (spawn threw, supervision broke).</summary>
    internal void MarkFailed(string reason)
    {
        lock (_gate)
        {
            State = JobState.Failed;
            FailureReason = reason;
            EndedAt = DateTimeOffset.UtcNow;
            CloseSinkLocked(null);
            CompleteSubscribersLocked();
        }
    }

    private void CloseSinkLocked(string? rowStatusNote)
    {
        if (_sink is not null)
        {
            var duration = ((EndedAt ?? DateTimeOffset.UtcNow) - StartedAt).TotalSeconds;
            var summary = $"exited after {duration:0.0}s — state {State.ToString().ToLowerInvariant()}"
                + (ExitCode is { } code ? $", exit code {code}" : "")
                + (FailureReason is null ? "" : $" ({FailureReason})");
            _sink.WriteLine(Format("api", summary, DateTimeOffset.UtcNow));
            if (rowStatusNote is not null)
                _sink.WriteLine(Format("api", rowStatusNote, DateTimeOffset.UtcNow));
            _sink.Dispose();
            _sink = null;
        }
    }

    private void CompleteSubscribersLocked()
    {
        foreach (var subscriber in _subscribers) subscriber.Writer.TryComplete();
        _subscribers.Clear();
    }

    internal static string Format(string stream, string line, DateTimeOffset at) =>
        $"{at.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffZ} [{stream}] {line}";
}
