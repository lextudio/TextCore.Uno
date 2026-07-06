#if !WINDOWS_APP_SDK
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Uno.UI.Xaml;

namespace LeXtudio.UI.Controls;

internal static class CoreTextNativeWindowResolver
{
    public static bool TryResolve(Window? window, out nint windowHandle, out nint displayHandle)
    {
        windowHandle = nint.Zero;
        displayHandle = nint.Zero;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            TryGetX11Handles(window, out displayHandle, out windowHandle);
        }

        if (windowHandle == nint.Zero)
        {
            windowHandle = TryGetNativeWindowHandle(window);
        }

        return windowHandle != nint.Zero;
    }

    private static nint TryGetNativeWindowHandle(Window? window)
    {
        if (window is null)
        {
            return System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
        }

        object? nativeWindow = WindowHelper.GetNativeWindow(window);
        if (nativeWindow is null)
        {
            return nint.Zero;
        }

        foreach (string name in new[] { "Hwnd", "HWnd", "Handle", "WindowHandle", "NativeHandle", "Pointer", "hwnd", "_hwnd" })
        {
            PropertyInfo? property = nativeWindow.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property is not null)
            {
                nint handle = ToNativeHandle(property.GetValue(nativeWindow));
                if (handle != nint.Zero)
                {
                    return handle;
                }
            }

            FieldInfo? field = nativeWindow.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field is not null)
            {
                nint handle = ToNativeHandle(field.GetValue(nativeWindow));
                if (handle != nint.Zero)
                {
                    return handle;
                }
            }
        }

        return nint.Zero;
    }

    private static void TryGetX11Handles(Window? window, out nint display, out nint nativeWindow)
    {
        display = nint.Zero;
        nativeWindow = nint.Zero;

        try
        {
            if (window is null)
            {
                return;
            }

            Type? hostType = Type.GetType("Uno.WinUI.Runtime.Skia.X11.X11XamlRootHost, Uno.UI.Runtime.Skia.X11");
            MethodInfo? getHost = hostType?.GetMethod("GetHostFromWindow", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            object? host = getHost?.Invoke(null, new object[] { window });
            object? x11Window = hostType?.GetProperty("RootX11Window", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(host);
            Type? windowType = x11Window?.GetType();

            display = ToNativeHandle(windowType?.GetProperty("Display", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(x11Window));
            nativeWindow = ToNativeHandle(windowType?.GetProperty("Window", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(x11Window));
        }
        catch
        {
        }
    }

    private static nint ToNativeHandle(object? value)
    {
        return value switch
        {
            IntPtr handle => handle,
            long handle => new nint(handle),
            int handle => new nint(handle),
            _ => nint.Zero,
        };
    }
}
#endif
