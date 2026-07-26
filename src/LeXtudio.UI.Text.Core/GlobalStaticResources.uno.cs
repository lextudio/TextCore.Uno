// Stub for Uno Platform XAML source generator compatibility.
// TextCore is a plain net10.0 library with no XAML files, but UnoEdit (which uses Uno.Sdk)
// references it, and Uno's XAML generator emits a call to this type in the consuming project's
// GlobalStaticResources.Initialize(). The stub satisfies that binding.
namespace LeXtudio.UI.Text.Core;

public static class GlobalStaticResources
{
    public static void Initialize() { }
}
