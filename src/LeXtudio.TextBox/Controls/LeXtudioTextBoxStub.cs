#if WINDOWS_APP_SDK
using Microsoft.UI.Xaml.Controls;

namespace LeXtudio.UI.Controls
{
    /// <summary>
    /// Windows App SDK build: expose the same public type as a thin wrapper over WinUI TextBox.
    /// </summary>
    public sealed class TextBox : global::Microsoft.UI.Xaml.Controls.TextBox
    {
    }
}
#endif
