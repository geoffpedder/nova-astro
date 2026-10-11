using System.Runtime.CompilerServices;
using Nova.Server.Services;

namespace Nova.Tests;

internal static class TestSetup
{
    /// <summary>
    /// Sun protection off unless a test turns it on: whether a test's target happens to be near the Sun depends
    /// on the time of day the tests run. The Sun protection tests set it themselves.
    /// </summary>
    [ModuleInitializer]
    internal static void Init()
    {
        SiteService.InitialSunAvoidance = 0;
        SiteService.InitialSettleSeconds = 0; // slews in tests don't wait; the settle tests set it themselves
    }
}
