#if WINDOWS_APP_SDK
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Controls;

[assembly: TypeForwardedTo(typeof(global::Microsoft.UI.Xaml.Controls.TextBox))]

namespace LeXtudio.UI.Controls
{
    // Forward the LeXtudio.UI.Controls.TextBox type to the WinUI implementation.
}
#endif
