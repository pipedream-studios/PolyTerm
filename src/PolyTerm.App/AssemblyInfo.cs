using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("PolyTerm.App.Tests")]
// Moq generates mocks for internal interfaces (e.g. IApprovalGate) via a dynamic proxy assembly
// named "DynamicProxyGenAssembly2" — it needs its own InternalsVisibleTo grant to see them.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
