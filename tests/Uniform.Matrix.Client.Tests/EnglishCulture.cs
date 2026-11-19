namespace Uniform.Matrix.Client.Tests;

using System.Globalization;
using System.Runtime.CompilerServices;

/// <summary>Runs the tests in English, whatever the machine's language, as they compare texts the apps show.</summary>
internal static class EnglishCulture
{
    /// <summary>Use English on every thread before any test runs.</summary>
    [ModuleInitializer]
    internal static void Use()
    {
        // Texts and formats
        CultureInfo english = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = english;
        CultureInfo.DefaultThreadCurrentUICulture = english;
        CultureInfo.CurrentCulture = english;
        // Done
        CultureInfo.CurrentUICulture = english;
    }
}
