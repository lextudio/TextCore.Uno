// Stub for Uno Platform XAML source generator compatibility.
// TextCore is a plain net10.0 library with no XAML files, but projects that use Uno.Sdk
// (like UnoEdit) reference it, and Uno's XAML source generator emits calls to this type
// in the consuming project's GlobalStaticResources.Initialize(). These stubs satisfy those
// bindings - none of them do anything because TextCore has no XAML resources.
namespace LeXtudio.UI.Text.Core;

public static class GlobalStaticResources
{
    public static void Initialize() { }
    public static void RegisterDefaultStyles() { }
    public static void RegisterResourceDictionariesBySource() { }
}
