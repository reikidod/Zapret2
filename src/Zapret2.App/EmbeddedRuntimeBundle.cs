using System.Reflection;
using System.IO;
using Zapret2.Core;

namespace Zapret2.App;

internal static class EmbeddedRuntimeBundle
{
    public const string Version = "caf11b2d7993+ffce9179b2b1";
    private const string ResourceName = "Zapret2.App.Resources.zapret-runtime.zip";

    public static string Install()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var archive = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("В portable EXE не встроен runtime bundle. Пересоберите приложение через build-portable.ps1.");
        var target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Zapret2GUI", "components");
        return BundleInstaller.Install(archive, target, Version);
    }
}
