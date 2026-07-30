using System.Text.Json.Nodes;

namespace Highlighter.Pipeline;

/// <summary>Concurrent chunk transcription with strictly ordered delivery.
///
/// Transcription was the pipeline's one serial critical path: each chunk's
/// Azure Speech round-trip (pure API latency, zero local CPU) blocked the
/// capture sweep thread. Workers here run those calls in parallel, while
/// delivery stays serialized and in chunk-index order — the scoring
/// coordinator's contract (single-threaded, contiguous, ordered AddChunk)
/// is preserved exactly.
///
/// Ordering without a dedicated thread: results land in a reorder buffer, and
/// whichever worker completes the next expected index drains the buffer under
/// one lock. Submit blocks when every worker slot is busy — natural
/// backpressure against unbounded audio buffering.</summary>
public sealed class TranscribePool
{
    private readonly SemaphoreSlim _slots;
    private readonly List<Task> _workers = [];
    private readonly object _deliverGate = new();
    private readonly Dictionary<int, (JsonObject Result, Action<JsonObject> Deliver)> _ready = [];
    private int _nextIndex;

    public TranscribePool(int concurrency)
    {
        _slots = new SemaphoreSlim(Math.Max(1, concurrency));
    }

    /// <summary>Queue one chunk (call in index order, 0-based and contiguous —
    /// exactly how capture produces them). work runs on a pool thread and must
    /// not throw (return a placeholder result instead); deliver runs under the
    /// delivery lock, in index order.</summary>
    public void Submit(int index, Func<JsonObject> work, Action<JsonObject> deliver)
    {
        _slots.Wait();
        _workers.Add(Task.Run(() =>
        {
            try
            {
                var result = work();
                lock (_deliverGate)
                {
                    _ready[index] = (result, deliver);
                    while (_ready.Remove(_nextIndex, out var item))
                    {
                        try
                        {
                            item.Deliver(item.Result);
                        }
                        catch (Exception exc)
                        {
                            // Advance regardless: a stuck _nextIndex would
                            // strand every later chunk behind this one.
                            Console.WriteLine($"Chunk {_nextIndex} delivery failed: {exc.Message}");
                        }
                        _nextIndex++;
                    }
                }
            }
            finally
            {
                _slots.Release();
            }
        }));
    }

    /// <summary>Wait for every submitted chunk to transcribe and deliver. The
    /// last finishing worker drains the buffer, so after this returns the
    /// delivery sequence is complete and in order.</summary>
    public void Finish()
    {
        Task.WaitAll(_workers.ToArray());
    }
}
