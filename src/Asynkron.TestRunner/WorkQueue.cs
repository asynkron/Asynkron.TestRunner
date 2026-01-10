namespace Asynkron.TestRunner;

/// <summary>
/// Thread-safe work queue for distributing tests to workers.
/// Three-tier queue: pending → suspicious (small batches) → confirmed (one-by-one)
/// </summary>
public class WorkQueue
{
    private readonly object _lock = new();
    private readonly Queue<string> _pending = new();
    private readonly Queue<string> _suspicious = new(); // First timeout - try in smaller batches
    private readonly Queue<string> _confirmed = new();  // Second timeout - run isolated
    private readonly Dictionary<int, HashSet<string>> _assigned = new();

    public WorkQueue(IEnumerable<string> tests)
    {
        foreach (var test in tests)
            _pending.Enqueue(test);
    }

    /// <summary>
    /// Total tests remaining (pending + suspicious + confirmed + assigned)
    /// </summary>
    public int RemainingCount
    {
        get
        {
            lock (_lock)
            {
                return _pending.Count + _suspicious.Count + _confirmed.Count + _assigned.Values.Sum(h => h.Count);
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
            lock (_lock) return _pending.Count + _suspicious.Count + _confirmed.Count;
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
    /// Number of suspicious tests waiting for small-batch retry
    /// </summary>
    public int SuspiciousCount
    {
        get
        {
            lock (_lock) return _suspicious.Count;
        }
    }

    /// <summary>
    /// Number of confirmed suspicious tests waiting for isolated retry
    /// </summary>
    public int ConfirmedCount
    {
        get
        {
            lock (_lock) return _confirmed.Count;
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
                       _confirmed.Count == 0 &&
                       _assigned.Values.All(h => h.Count == 0);
            }
        }
    }

    /// <summary>
    /// Take a batch of tests for a main worker (from pending queue).
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
    /// Take a batch of suspicious tests for a suspect worker.
    /// These run in smaller batches to clear tests that were just CPU-starved.
    /// </summary>
    public List<string> TakeSuspiciousBatch(int workerId, int maxSize)
    {
        lock (_lock)
        {
            if (!_assigned.ContainsKey(workerId))
                _assigned[workerId] = new HashSet<string>();

            var batch = new List<string>();
            while (batch.Count < maxSize && _suspicious.Count > 0)
            {
                var test = _suspicious.Dequeue();
                batch.Add(test);
                _assigned[workerId].Add(test);
            }

            return batch;
        }
    }

    /// <summary>
    /// Take a single confirmed test for an isolation worker.
    /// These are tests that timed out even in small batches.
    /// Returns null if no confirmed tests available.
    /// </summary>
    public string? TakeConfirmed(int workerId)
    {
        lock (_lock)
        {
            if (_confirmed.Count == 0)
                return null;

            if (!_assigned.ContainsKey(workerId))
                _assigned[workerId] = new HashSet<string>();

            var test = _confirmed.Dequeue();
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
    /// Mark tests as suspicious - they'll be retried in smaller batches.
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
    /// Mark tests as confirmed suspicious - they'll be retried one-by-one.
    /// Used when tests timeout even in small batches.
    /// </summary>
    public void MarkConfirmed(int workerId, IEnumerable<string> tests)
    {
        lock (_lock)
        {
            if (_assigned.TryGetValue(workerId, out var assigned))
            {
                foreach (var test in tests)
                {
                    assigned.Remove(test);
                    _confirmed.Enqueue(test);
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
