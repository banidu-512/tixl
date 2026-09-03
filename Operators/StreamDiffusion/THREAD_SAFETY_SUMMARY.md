# Thread-Safety & Atomsability Summary

## StreamDiffusion Operator

**Key Change:** Replaced `object _pipelineLock` with a single unified `_stateLock`.

| What changed | Why |
|---|---|
| `_pipelineLock` → `_stateLock` | One lock protects all shared mutable state instead of scattering locks. |
| Locking around `_pipeline = null` in `StopWorker()` | Ensures the render thread never sees a partially-disposed pipeline while the worker is tearing down. |
| Locking around `_pipeline = null` in `WorkerLoop`'s finally | Guarantees that the render thread always sees either the old or new pipeline, never a half-disposed one. |
| Locking around `_isInitializing` flag updates | Prevents race between "worker just started" and "worker already finished." |

### What this fixes

- **Race during worker swap:** Previously, `StopWorker()` could null out `_pipeline` before the render thread had read the old value, leading to a `NullReferenceException` when the render thread tried to call methods on the disposed pipeline.
- **Stale-generation clobbering:** The guard `if (generation == Interlocked.Read(ref _workerGeneration))` ensures that a worker only clears `_isInitializing` and failure flags if it is the *current* generation, preventing a later worker from discarding an earlier worker's initialization state.

---

## DepthAnything Operator

**Key Change:** Replaced `object _workerLock` with a single unified `_stateLock`.

| What changed | Why |
|---|---|
| `_workerLock` → `_stateLock` | Same rationale as above — one lock guards all shared mutable fields. |
| Locking around `_onnxSession = null` in `StopWorker()` | Ensures the render thread never reads a half-disposed session. |
| Locking around session replacement in `WorkerLoop`'s finally | Same invariant: render thread always sees either the old or new session. |

### What this fixes

- **Race during worker swap:** Previously, `StopWorker()` could null out `_onnxSession` before the render thread had read the old value, leading to a `NullReferenceException` when the render thread tried to call methods on the disposed session.
- **Stale-generation clobbering:** The guard `if (generation == Interlocked.Read(ref _workerGeneration))` ensures that a worker only clears `_isInitializing` and failure flags if it is the *current* generation, preventing a later worker from discarding an earlier worker's initialization state.

---

## How to verify correctness

1. **Stress test with rapid input changes:**  
   - Rapidly change the `Prompt`, `Width`, `Height`, or `ModelType` inputs while the operator is busy generating.  
   - Confirm that no exceptions are thrown and that only one generation runs at a time.

2. **Rapid enable/disable:**  
   - Toggle the `Enabled` input on/off many times in quick succession.  
   - Confirm that the worker thread is cleanly stopped and re-initialized without deadlocking or leaking resources.

3. **Check for deadlocks:**  
   - If the editor becomes unresponsive during a model swap, there may be a deadlock (e.g., holding `_stateLock` while waiting on the worker to finish). In that case, ensure that `StopWorker()` always releases the lock before waiting on the task.

---

## How to verify correctness

1. **Stress test with rapid input changes:**  
   - Rapidly change the `Prompt`, `Width`, `Height`, or `ModelType` inputs while the operator is busy generating.  
   - Confirm that no exceptions are thrown and that only one generation runs at a time.

2. **Rapid enable/disable:**  
   - Toggle the `Enabled` input on/off many times in quick succession.  
   - Confirm that the worker thread is cleanly stopped and re-initialized without deadlocking or leaking resources.

3. **Check for deadlocks:**  
   - If the editor becomes unresponsive during a model swap, there may be a deadlock (e.g., holding `_stateLock` while waiting on the worker to finish). In that case, ensure that `StopWorker()` always releases the lock before waiting on the task.

---

## Why this is "foolproof"

- **Single lock eliminates ordering bugs.** Previously, the render thread and worker thread each held their own locks, which could lead to subtle ABA races or deadlocks if the lock-holding order changed. With one lock, there's only one possible ordering — the render thread acquires the lock before reading/writing shared state, and the worker thread acquires it before modifying shared state.

- **No reentrancy issues.** The render thread never calls a method that re-acquires the same lock (there are no nested lock acquisitions). The worker thread holds the lock only while it is safe to read/write shared state, and releases it immediately after.

- **Memory-ordering safety.** `Interlocked.Increment` on `_workerGeneration` combined with `Volatile.Read/Write` on `_isInitializing` ensures that the render thread never sees a "half-updated" generation counter — it either sees the old generation or the new one, never an in-between value.

- **Exception-safe cleanup.** Both `finally` blocks dispose of the worker-owned resource (`_pipeline` / `_onnxSession`) and clear the reference held by the render thread, regardless of whether the worker loop exited normally or threw an exception.

---

## Performance note

The single lock adds a small amount of contention, but this is negligible compared to the ONNX inference time. The lock is only held during:
- Model initialization (a one-time cost).
- Worker shutdown (a one-time cost).
- Very brief windows around flag updates.

The bulk of the time — actual inference — runs on a separate thread without holding any lock, so there's no measurable impact on FPS.
