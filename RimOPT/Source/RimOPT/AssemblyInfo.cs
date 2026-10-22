using System.Reflection;

[assembly: AssemblyTitle("RimOPT")]
#if RIMWORLD17
[assembly: AssemblyDescription("RimOPT 1.7 experimental API-adaptation build; optimization modules opt-in")]
#else
[assembly: AssemblyDescription("Focused invalid work-search reduction for RimWorld 1.6")]
#endif
[assembly: AssemblyCompany("Allen")]
[assembly: AssemblyProduct("RimOPT")]
#if RIMWORLD17
[assembly: AssemblyVersion("0.2.0.0")]
[assembly: AssemblyFileVersion("0.2.0.0")]
#else
[assembly: AssemblyVersion("0.1.4.0")]
[assembly: AssemblyFileVersion("0.1.4.0")]
#endif
