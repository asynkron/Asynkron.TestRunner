namespace Asynkron.TestRunner;

/// <summary>
/// Thread-safe work queue for distributing tests to workers.
/// Three queues: pending → suspicious → confirmed (for true timeout culprits).
/// Confirmed tests only run when batch size = 1 (isolation mode).
/// </summary>
public class WorkQueue
{
    private readonly object _lock = new();
    private readonly Queue<string> _pending = new();
    private readonly Queue<string> _suspicious = new();
    private readonly Queue<string> _confirmed = new(); // Tests that actually triggered timeouts
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
    /// Tests in pending queue
    /// </summary>
    public int PendingCount
    {
        get
        {
            lock (_lock) return _pending.Count;
        }
    }

    /// <summary>
    /// Tests in suspicious queue (waiting for promotion)
    /// </summary>
    public int SuspiciousCount
    {
        get
        {
            lock (_lock) return _suspicious.Count;
        }
    }

    /// <summary>
    /// Tests in confirmed queue (known timeout culprits, wait for isolation)
    /// </summary>
    public int ConfirmedCount
    {
        get
        {
            lock (_lock) return _confirmed.Count;
        }
    }

    /// <summary>
    /// Check if there's pending work available
    /// </summary>
    public bool HasPendingWork
    {
        get
        {
            lock (_lock) return _pending.Count > 0;
        }
    }

    /// <summary>
    /// Check if all work is complete (nothing pending, suspicious, confirmed, or assigned)
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
    /// Check if all workers are idle (nothing assigned)
    /// </summary>
    public bool AllWorkersIdle
    {
        get
        {
            lock (_lock) return _assigned.Values.All(h => h.Count == 0);
        }
    }

    /// <summary>
    /// Take a batch of tests from pending queue.
    /// When in isolation mode (maxSize=1) and pending is empty, pulls from confirmed.
    /// </summary>
    public List<string> TakeBatch(int workerId, int maxSize)
    {
        lock (_lock)
        {
            if (!_assigned.ContainsKey(workerId))
                _assigned[workerId] = new HashSet<string>();

            var batch = new List<string>();

            // First, pull from pending
            while (batch.Count < maxSize && _pending.Count > 0)
            {
                var test = _pending.Dequeue();
                batch.Add(test);
                _assigned[workerId].Add(test);
            }

            // In isolation mode (batch=1), also pull from confirmed if pending is empty
            if (maxSize == 1 && batch.Count == 0 && _confirmed.Count > 0)
            {
                var test = _confirmed.Dequeue();
                batch.Add(test);
                _assigned[workerId].Add(test);
            }

            return batch;
        }
    }

    /// <summary>
    /// Mark a test as completed (passed, failed, skipped).
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
    /// Mark tests as suspicious - they'll be retried after promotion.
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
    /// Mark tests as confirmed bad - they triggered a timeout/crash.
    /// These skip tier progression and wait for isolation mode (batch=1).
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
    /// Promote all suspicious tests to pending.
    /// Call this when pending is empty but suspicious has tests.
    /// Returns the number of tests promoted.
    /// </summary>
    public int PromoteSuspicious()
    {
        lock (_lock)
        {
            var count = _suspicious.Count;
            while (_suspicious.Count > 0)
            {
                _pending.Enqueue(_suspicious.Dequeue());
            }
            return count;
        }
    }

    /// <summary>
    /// Worker crashed - reclaim all assigned tests to suspicious (not pending).
    /// </summary>
    public List<string> WorkerCrashed(int workerId)
    {
        lock (_lock)
        {
            if (!_assigned.TryGetValue(workerId, out var assigned))
                return [];

            var reclaimed = assigned.ToList();
            foreach (var test in reclaimed)
                _suspicious.Enqueue(test);

            assigned.Clear();
            return reclaimed;
        }
    }

    /// <summary>
    /// Get tests currently assigned to a worker
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
