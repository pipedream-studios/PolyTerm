using Xunit;

namespace PolyTerm.App.Tests;

/// <summary>
/// Any test class that reads or writes the real <see cref="App.Settings"/> singleton (a shared,
/// process-wide mutable object — <c>McpApprovalPolicy</c>, <c>McpMaxCallsPerMinute</c>) belongs in
/// this collection. xUnit runs different test classes in parallel by default but keeps a whole
/// collection sequential — this exists specifically so, e.g., a rate-limit test setting
/// <c>McpMaxCallsPerMinute</c> to 1 can't transiently affect an unrelated pipe test running on
/// another thread at the same moment.
/// </summary>
[CollectionDefinition("AppSettings", DisableParallelization = true)]
public sealed class AppSettingsTestCollection;
