using System.Runtime.CompilerServices;
using RalFsrHelper.Core;

namespace RalFsrHelper.Tests;

internal static class TestSetup
{
    /// <summary>
    /// Before any test runs: the helper's folder (its log, caches and settings) moves to a temporary folder, so test runs
    /// never add lines to the real helper.log that support reports collect.
    /// </summary>
    [ModuleInitializer]
    internal static void UseTemporaryHelperFolder()
    {
        AppPaths.Root = Path.Combine(Path.GetTempPath(), "ralfsr-tests", "helper-" + Environment.ProcessId);
        OptiPatcherSource.CacheDir = Path.Combine(AppPaths.Root, "optipatcher");
    }
}
