#if TOOLS
using Enaweg.Plugin.Internal;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

[TestSuite]
public class FrameThrottleTests
{
    [TestCase]
    public static void IsDueFirstThenEveryIntervalFrames()
    {
        var throttle = new FrameThrottle(30);

        Assertions.AssertBool(throttle.Due(5)).IsTrue();
        Assertions.AssertBool(throttle.Due(5)).IsFalse();
        Assertions.AssertBool(throttle.Due(34)).IsFalse();
        Assertions.AssertBool(throttle.Due(35)).IsTrue();
        // counted from the frame it ran in, not from when it was due
        Assertions.AssertBool(throttle.Due(100)).IsTrue();
        Assertions.AssertBool(throttle.Due(129)).IsFalse();
        Assertions.AssertBool(throttle.Due(130)).IsTrue();
    }

    [TestCase]
    public static void ResetMakesItDueAtOnce()
    {
        var throttle = new FrameThrottle(600);
        Assertions.AssertBool(throttle.Due(10)).IsTrue();
        Assertions.AssertBool(throttle.Due(11)).IsFalse();

        throttle.Reset();

        Assertions.AssertBool(throttle.Due(11)).IsTrue();
        Assertions.AssertBool(throttle.Due(12)).IsFalse();
    }
}
#endif
