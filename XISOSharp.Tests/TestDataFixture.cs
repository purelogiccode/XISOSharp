using System.Runtime.CompilerServices;
using XISOSharp.TestDataGenerator;

namespace XISOSharp.Tests;

/// <summary>
/// Restores the TestData fixture when the test assembly loads, so a fresh clone
/// (or a deleted TestData folder) never breaks the suite. The generation logic
/// lives in <see cref="TestDataWriter"/> (XISOSharp.TestDataGenerator project,
/// referenced — not linked — by this test project).
/// </summary>
internal static class TestDataFixture
{
    // Centralized via TestDataLocator (BUG-TEST-006): no fragile 4x ".." literal here.
    private static readonly string TestDataRoot = TestDataLocator.GetTestDataRoot(AppContext.BaseDirectory);

    [ModuleInitializer]
    internal static void EnsureTestData()
    {
        TestDataWriter.EnsureTestData(TestDataRoot);
    }
}
