using System.Runtime.CompilerServices;
using XISOSharp.TestDataGenerator;

namespace XISOSharp.Tests;

/// <summary>
/// Restores the TestData fixture when the test assembly loads, so a fresh clone
/// (or a deleted TestData folder) never breaks the suite. The generation logic
/// lives in <see cref="TestDataWriter"/> (shared with the XISOSharp.TestDataGenerator tool).
/// </summary>
internal static class TestDataFixture
{
    private static readonly string TestDataRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TestData"));

    [ModuleInitializer]
    internal static void EnsureTestData() => TestDataWriter.EnsureTestData(TestDataRoot);
}
