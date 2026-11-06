#if TOOLS
using System;
using Enaweg.Plugin.Internal;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

[TestSuite]
public class TransitionCoordinatorTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [TestCase]
    public static void WaitingStackEnumeratesFromTheTopAndKeepsTheOrderOfWhatItKeeps()
    {
        var stack = new WaitingStack<string>();
        foreach (var item in new[] { "a", "b", "a", "c", "d" }) stack.Push(item);

        stack.RemoveWhere(item => item is "a" or "c");

        Assertions.AssertThat(stack).ContainsExactly("d", "b");
        Assertions.AssertBool(stack.Contains("a")).IsFalse();
        Assertions.AssertString(stack.Pop()).IsEqual("d");
        Assertions.AssertInt(stack.Count).IsEqual(1);
    }

    [TestCase]
    public static void TakeAllEmptiesTheStackTopFirst()
    {
        var stack = new WaitingStack<string>();
        stack.Push("first");
        stack.Push("second");

        Assertions.AssertThat(stack.TakeAll()).ContainsExactly("second", "first");
        Assertions.AssertBool(stack.IsEmpty).IsTrue();
        Assertions.AssertThat(stack.TakeAll()).IsEmpty();
    }

    [TestCase]
    public static void HoldsNestAndAReleaseCountsOnce()
    {
        var coordinator = new TransitionCoordinator(TimeSpan.FromSeconds(30), 30);
        Assertions.AssertBool(coordinator.IsPending).IsFalse();

        var outer = coordinator.Hold();
        var inner = coordinator.Hold();
        inner.Dispose();
        inner.Dispose();
        Assertions.AssertBool(coordinator.IsHeld).IsTrue();
        Assertions.AssertBool(coordinator.IsPending).IsTrue();
        Assertions.AssertBool(coordinator.HasWaiting).IsFalse();

        outer.Dispose();
        Assertions.AssertBool(coordinator.IsHeld).IsFalse();
        Assertions.AssertBool(coordinator.IsPending).IsFalse();
    }

    [TestCase]
    public static void AStallNeedsBothTheTimeoutAndTheFrames()
    {
        var watchdog = new StallWatchdog(TimeSpan.FromSeconds(30), 30);

        Assertions.AssertBool(watchdog.IsStalled("op", Start, 100)).IsFalse();
        // one long frame, e.g. a build, is work
        Assertions.AssertBool(watchdog.IsStalled("op", Start + TimeSpan.FromMinutes(5), 101)).IsFalse();
        // many quick frames are not enough either
        Assertions.AssertBool(watchdog.IsStalled("op", Start + TimeSpan.FromSeconds(29), 1000)).IsFalse();
        Assertions.AssertBool(watchdog.IsStalled("op", Start + TimeSpan.FromSeconds(30), 130)).IsTrue();
    }

    [TestCase]
    public static void ProgressAndResetStartCountingAnew()
    {
        var watchdog = new StallWatchdog(TimeSpan.FromSeconds(30), 30);
        watchdog.IsStalled("op", Start, 0);

        // another signature is progress
        Assertions.AssertBool(watchdog.IsStalled("op, other", Start + TimeSpan.FromSeconds(20), 20)).IsFalse();
        Assertions.AssertBool(watchdog.IsStalled("op, other", Start + TimeSpan.FromSeconds(40), 40)).IsFalse();
        Assertions.AssertBool(watchdog.IsStalled("op, other", Start + TimeSpan.FromSeconds(50), 50)).IsTrue();

        watchdog.Reset();
        Assertions.AssertBool(watchdog.IsStalled("op, other", Start + TimeSpan.FromSeconds(60), 60)).IsFalse();
        Assertions.AssertBool(watchdog.IsStalled("op, other", Start + TimeSpan.FromSeconds(90), 90)).IsTrue();
    }
}
#endif
