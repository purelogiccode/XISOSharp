namespace XISOSharp.Tests;

/// <summary>
/// Defines a test collection that disables parallel execution,
/// ensuring sequential test execution for tests that share mutable state.
/// </summary>
[CollectionDefinition("Sequential", DisableParallelization = true)]
public class SequentialCollection;
