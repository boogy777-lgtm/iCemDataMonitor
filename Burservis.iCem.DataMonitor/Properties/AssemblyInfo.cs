using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("Burservis.iCem.DataMonitor")]
[assembly: AssemblyProduct("DataMonitor")]
[assembly: AssemblyCompany("Internal POC")]
[assembly: AssemblyDescription("iCem in-process realtime data tap (Prism AddIn).")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: ComVisible(false)]

// Self-test harness (BusSelfTest.exe, W2) exercises internal bus surface.
[assembly: InternalsVisibleTo("BusSelfTest")]
// xunit test project (Burservis.iCem.DataMonitor.Tests) — same internals access.
[assembly: InternalsVisibleTo("Burservis.iCem.DataMonitor.Tests")]
