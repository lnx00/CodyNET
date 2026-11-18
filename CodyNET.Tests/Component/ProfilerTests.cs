using CodyNET.Core.Devices;
using NUnit.Framework;

namespace CodyNET.Tests.Component;

public class ProfilerTests
{
    [Test]
    public void CpuResetDoesNotProduceNegativeFrequency()
    {
        // Long interval so the profiler never snapshots on its own during the test
        var profiler = new Profiler(TimeSpan.FromHours(1), null);

        profiler.SampleCpu(500_000, 1_000_000);
        profiler.CalculateSnapshot();            // window now starts at 500k cycles
        profiler.SampleCpu(700_000, 1_000_000);
        profiler.SampleCpu(0, 1_000_000);        // CPU reset zeroes the cycle counter
        profiler.SampleCpu(300_000, 1_000_000);
        Thread.Sleep(20);
        profiler.CalculateSnapshot();

        var snapshot = profiler.LastSnapshot;
        // Before the fix: 300k - 500k = -200k cycles -> negative frequency ("MAX (-300%)")
        Assert.That(snapshot.ActualFrequency, Is.GreaterThan(0));
        // Cycles before (200k) and after (300k) the reset both count
        Assert.That(snapshot.ActualFrequency * snapshot.SecondsElapsed, Is.EqualTo(500_000).Within(0.1).Percent);
    }
}
