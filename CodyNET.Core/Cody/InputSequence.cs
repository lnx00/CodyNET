namespace CodyNET.Core.Cody;

/// <summary>
/// An ordered list of actions that run on the emulator thread, either a given number of CPU
/// cycles after the previous one, or as soon as a condition on the machine state becomes true.
/// Timing in emulated cycles (not wall-clock time) keeps scripted input working at any clock
/// speed, including --fast.
/// </summary>
internal sealed class InputSequence
{
    private sealed record Step(long Cycles, Func<bool>? Condition, Action Action, Action? OnTimeout);

    private readonly List<Step> _steps = new();
    private int _index;
    private long _nextAt = -1; // Delay step: when it runs. Condition step: when it times out.

    /// <summary>Adds <paramref name="action"/> to run <paramref name="delayCycles"/> after the previous step.</summary>
    public InputSequence Then(long delayCycles, Action action)
    {
        _steps.Add(new Step(delayCycles, null, action, null));
        return this;
    }

    /// <summary>
    /// Adds <paramref name="action"/> to run as soon as <paramref name="condition"/> is true (checked
    /// before every CPU instruction). If it is still false <paramref name="timeoutCycles"/> after the
    /// previous step, <paramref name="onTimeout"/> runs instead.
    /// </summary>
    public InputSequence ThenWhen(Func<bool> condition, long timeoutCycles, Action action, Action onTimeout)
    {
        _steps.Add(new Step(timeoutCycles, condition, action, onTimeout));
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
            _nextAt = cpu.TotalCyclesExecuted + _steps[0].Cycles;

        while (_index < _steps.Count)
        {
            var step = _steps[_index];
            if (step.Condition is not null && step.Condition())
                step.Action();
            else if (cpu.TotalCyclesExecuted >= _nextAt)
                (step.Condition is null ? step.Action : step.OnTimeout!)();
            else
                break; // Not due yet

            _index++;
            // Measured from *after* the action, so a CPU reset (which zeroes the cycle counter) works.
            if (_index < _steps.Count)
                _nextAt = cpu.TotalCyclesExecuted + _steps[_index].Cycles;
        }

        return _index >= _steps.Count;
    }
}
