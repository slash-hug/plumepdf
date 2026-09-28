using System.Runtime.CompilerServices;

namespace PlumePdf.CookbookTests;

public static class ModuleInit
{
    [ModuleInitializer]
    public static void Init()
    {
        // Recipes use readable relative paths ("samples/…", "output/…"); anchor them to the
        // test output directory so they resolve identically under every runner.
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        Directory.CreateDirectory("output");
    }
}
