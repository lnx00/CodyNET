namespace CodyNET.Core.Cody;

/// <summary>
/// An ordered list of actions that run on the emulator thread, each one a given number of
/// CPU cycles after the previous one. Timing in emulated cycles (not wall-clock time) keeps
/// scripted input working at any clock speed, including --fast.
/// </summary>
internal sealed class InputSequence
{
    private readonly List<(long DelayCycles, Action Action)> _steps = new();
    private int _index;
    private long _nextAt = -1;

    /// <summary>Adds <paramref name="action"/> to run <paramref name="delayCycles"/> after the previous step.</summary>
    public InputSequence Then(long delayCycles, Action action)
    {
        _steps.Add((delayCycles, action));
        return this;
    }

    /// <summary>
    /// Runs every step that is due. Called once per CPU step.
    /// </summary>
    /// <returns>True once all steps have run.</returns>
    public bool Update(Cpu cpu)
    {
        if (_index >= _steps.Count)
            return true;

        if (_nextAt < 0)
            _nextAt = cpu.TotalCyclesExecuted + _steps[0].DelayCycles;

        while (_index < _steps.Count && cpu.TotalCyclesExecuted >= _nextAt)
        {
            _steps[_index].Action();
            _index++;
            // Measured from *after* the action, so a CPU reset (which zeroes the cycle counter) works.
            if (_index < _steps.Count)
                _nextAt = cpu.TotalCyclesExecuted + _steps[_index].DelayCycles;
        }

        return _index >= _steps.Count;
    }
}
