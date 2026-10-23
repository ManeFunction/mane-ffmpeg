namespace ManeFfmpeg.Core.Editing;

/// <summary>Snapshot-based undo history. <see cref="Current"/> is always the live state.</summary>
public sealed class UndoStack<T>(T initial, int capacity = 300) where T : class
{
    private readonly LinkedList<T> _undo = new();
    private readonly Stack<T> _redo = new();

    public T Current { get; private set; } = initial;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public event EventHandler? Changed;

    /// <summary>Makes <paramref name="next"/> current and records the previous state. No-op if equal.</summary>
    public bool Commit(T next)
    {
        EndGesture();
        if (Equals(next, Current))
            return false;
        _undo.AddLast(Current);
        if (_undo.Count > capacity)
            _undo.RemoveFirst();
        _redo.Clear();
        Current = next;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private T? _gestureBase;

    public bool InGesture => _gestureBase is not null;

    /// <summary>Starts a drag: later <see cref="ReplaceCurrent"/> calls collapse into one undo step.</summary>
    public void BeginGesture() => _gestureBase ??= Current;

    /// <summary>Replaces the current state without recording history (live drag previews).</summary>
    public void ReplaceCurrent(T state)
    {
        if (Equals(state, Current))
            return;
        Current = state;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Finishes a drag, recording one undo step if anything changed.</summary>
    public void EndGesture()
    {
        if (_gestureBase is not { } start)
            return;
        _gestureBase = null;
        if (Equals(start, Current))
            return;
        _undo.AddLast(start);
        if (_undo.Count > capacity)
            _undo.RemoveFirst();
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Abandons a drag and restores the state it started from.</summary>
    public void CancelGesture()
    {
        if (_gestureBase is not { } start)
            return;
        _gestureBase = null;
        Current = start;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Undo()
    {
        EndGesture();
        if (_undo.Last is not { } last)
            return false;
        _undo.RemoveLast();
        _redo.Push(Current);
        Current = last.Value;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Redo()
    {
        EndGesture();
        if (!_redo.TryPop(out var next))
            return false;
        _undo.AddLast(Current);
        Current = next;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Reset(T state)
    {
        _gestureBase = null;
        _undo.Clear();
        _redo.Clear();
        Current = state;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
