namespace Asynkron.TestRunner;

/// <summary>
/// Thread-safe work queue for distributing tests to workers.
/// Tracks assigned work so crashed workers' tests can be reclaimed.
/// </summary>
public class WorkQueue
{
    private readonly object _lock = new();
    private readonly Queue<string> _pending = new();
    private readonly Queue<string> _suspicious = new(); // Priority queue for isolated retry
    private readonly Dictionary<int, HashSet<string>> _assigned = new();

    public WorkQueue(IEnumerable<string> tests)
    {
        foreach (var test in tests)
            _pending.Enqueue(test);
    }

    /// <summary>
    /// Total tests remaining (pending + suspicious + assigned)
    /// </summary>
    public int RemainingCount
    {
        get
        {
            lock (_lock)
            {
                return _pending.Count + _suspicious.Count + _assigned.Values.Sum(h => h.Count);
            }
        }
    }

    /// <summary>
    /// Tests waiting to be assigned
    /// </summary>
    public int PendingCount
    {
        get
        {
            lock (_lock) return _pending.Count + _suspicious.Count;
        }
    }

    /// <summary>
    /// Check if there's any work available (regular batches only)
    /// </summary>
    public bool HasWork
    {
        get
        {
            lock (_lock) return _pending.Count > 0;
        }
    }

    /// <summary>
    /// Number of suspicious tests waiting for isolated retry
    /// </summary>
    public int SuspiciousCount
    {
        get
        {
            lock (_lock) return _suspicious.Count;
        }
    }

    /// <summary>
    /// Check if all work is complete (nothing pending, nothing assigned)
    /// </summary>
    public bool IsComplete
    {
        get
        {
            lock (_lock)
            {
                return _pending.Count == 0 &&
                       _suspicious.Count == 0 &&
                       _assigned.Values.All(h => h.Count == 0);
            }
        }
    }

    /// <summary>
    /// Take a batch of tests for a main worker (skips suspicious tests).
    /// </summary>
    public List<string> TakeBatch(int workerId, int maxSize)
    {
        lock (_lock)
        {
            if (!_assigned.ContainsKey(workerId))
                _assigned[workerId] = new HashSet<string>();

            var batch = new List<string>();
            while (batch.Count < maxSize && _pending.Count > 0)
            {
                var test = _pending.Dequeue();
                batch.Add(test);
                _assigned[workerId].Add(test);
            }

            return batch;
        }
    }

    /// <summary>
    /// Take a single suspicious test for an isolation worker.
    /// Returns null if no suspicious tests available.
    /// </summary>
    public string? TakeSuspicious(int workerId)
    {
        lock (_lock)
        {
            if (_suspicious.Count == 0)
                return null;

            if (!_assigned.ContainsKey(workerId))
                _assigned[workerId] = new HashSet<string>();

            var test = _suspicious.Dequeue();
            _assigned[workerId].Add(test);
            return test;
        }
    }

    /// <summary>
    /// Mark a test as completed (passed, failed, skipped).
    /// Removes from assigned list.
    /// </summary>
    public void TestCompleted(int workerId, string fqn)
    {
        lock (_lock)
        {
            if (_assigned.TryGetValue(workerId, out var assigned))
                assigned.Remove(fqn);
        }
    }

    /// <summary>
    /// Mark a test as hanging - removes from assigned, does not re-queue.
    /// </summary>
    public void TestHanging(int workerId, string fqn)
    {
        lock (_lock)
        {
            if (_assigned.TryGetValue(workerId, out var assigned))
                assigned.Remove(fqn);
        }
    }

    /// <summary>
    /// Mark tests as suspicious - they'll be retried in isolation.
    /// </summary>
    public void MarkSuspicious(int workerId, IEnumerable<string> tests)
    {
        lock (_lock)
        {
            if (_assigned.TryGetValue(workerId, out var assigned))
            {
                foreach (var test in tests)
                {
                    assigned.Remove(test);
                    _suspicious.Enqueue(test);
                }
            }
        }
    }

    /// <summary>
    /// Worker crashed - reclaim all assigned tests back to pending.
    /// </summary>
    public List<string> WorkerCrashed(int workerId)
    {
        lock (_lock)
        {
            if (!_assigned.TryGetValue(workerId, out var assigned))
                return [];

            var reclaimed = assigned.ToList();
            foreach (var test in reclaimed)
                _pending.Enqueue(test);

            assigned.Clear();
            return reclaimed;
        }
    }

    /// <summary>
    /// Get tests currently assigned to a worker (for timeout checking)
    /// </summary>
    public List<string> GetAssigned(int workerId)
    {
        lock (_lock)
        {
            if (_assigned.TryGetValue(workerId, out var assigned))
                return assigned.ToList();
            return [];
        }
    }
}
