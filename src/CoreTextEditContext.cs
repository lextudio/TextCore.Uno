using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Uno.UI.Xaml;

namespace LeXtudio.UI.Text.Core
{
    /// <summary>
    /// Central event hub that raises text, selection, layout and composition events
    /// for adapters and editor hosts.
    /// Consumers obtain instances via <see cref="CoreTextServicesManager.CreateEditContext"/>.
    /// The underlying platform adapter is transparent — callers never see it.
    /// </summary>
    public sealed class CoreTextEditContext : IDisposable
    {
        private readonly IPlatformTextInputAdapter _adapter;
        /// <summary>
        /// Input scope hint for the platform IME. Mirrors WinUI's CoreTextInputScope.
        /// Consumers may set this to inform platform keyboards/IMEs of the expected input.
        /// </summary>
        public CoreTextInputScope InputScope { get; set; } = CoreTextInputScope.Default;

        /// <summary>
        /// Rasterization scale used when converting logical caret coordinates to
        /// pixel coordinates in platform adapters that require it.
        /// Defaults to 1.0.
        /// </summary>
        public double RasterizationScale { get; set; } = 1.0;

        /// <summary>Initializes a context with no platform adapter (useful for testing).</summary>
        public CoreTextEditContext()
        {
            _adapter = new NullTextInputAdapter();
        }

        /// <summary>Initializes a context backed by the specified platform adapter.</summary>
        internal CoreTextEditContext(IPlatformTextInputAdapter adapter)
        {
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        }

        // ----- Events (public surface for consumers) -----

        /// <summary>Occurs when the platform requests the current text.</summary>
        public event EventHandler<CoreTextEditContext, CoreTextTextRequestedEventArgs>? TextRequested;

        /// <summary>Occurs when text is being updated by the platform.</summary>
        public event EventHandler<CoreTextEditContext, CoreTextTextUpdatingEventArgs>? TextUpdating;

        /// <summary>Occurs when the platform requests the current selection.</summary>
        public event EventHandler<CoreTextEditContext, CoreTextSelectionRequestedEventArgs>? SelectionRequested;

        /// <summary>Occurs when the selection is being updated by the platform.</summary>
        public event EventHandler<CoreTextEditContext, CoreTextSelectionUpdatingEventArgs>? SelectionUpdating;

        /// <summary>Occurs when a layout measurement is requested by the platform.</summary>
        public event EventHandler<CoreTextEditContext, CoreTextLayoutRequestedEventArgs>? LayoutRequested;

        /// <summary>Occurs when IME composition starts.</summary>
        public event EventHandler<CoreTextEditContext, CoreTextCompositionStartedEventArgs>? CompositionStarted;

        /// <summary>Occurs when IME composition completes.</summary>
        public event EventHandler<CoreTextEditContext, CoreTextCompositionCompletedEventArgs>? CompositionCompleted;

        /// <summary>Occurs when focus is removed from the text context.</summary>
        public event TypedEventHandler<CoreTextEditContext, object>? FocusRemoved;

        /// <summary>Occurs when a platform command is received (e.g. AppKit selectors like "deleteBackward:").</summary>
        public event EventHandler<CoreTextCommandReceivedEventArgs>? CommandReceived;

        // ----- Lifecycle (called by the host application) -----

        private bool Attach(nint windowHandle, nint displayHandle = 0) => _adapter.Attach(windowHandle, displayHandle, this);

        /// <summary>
        /// Attach this context to the current native window so the platform
        /// adapter can start listening for IME events.
        /// </summary>
        /// <param name="window">The current window instance used to resolve native handles.</param>
        /// <returns><c>true</c> if the adapter attached successfully.</returns>
        public bool AttachToCurrentWindow(Window? window)
        {
            nint windowHandle = nint.Zero;
            nint displayHandle = nint.Zero;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                TryGetX11Handles(window, out displayHandle, out windowHandle);
            }

            if (windowHandle == nint.Zero)
            {
                windowHandle = TryGetNativeWindowHandle(window);
            }

            return Attach(windowHandle, displayHandle);
        }

        /// <summary>
        /// Notify the platform that the caret rectangle has changed so the IME
        /// candidate window can be repositioned.
        /// Uses <see cref="RasterizationScale"/> for adapters that require scale.
        /// </summary>
        public void NotifyCaretRectChanged(double x, double y, double width, double height)
            => _adapter.NotifyCaretRectChanged(x, y, width, height, RasterizationScale);

        /// <summary>
        /// Notify the platform that the caret rectangle has changed and update
        /// <see cref="RasterizationScale"/> in one call.
        /// </summary>
        public void NotifyCaretRectChanged(double x, double y, double width, double height, double scale)
        {
            RasterizationScale = scale;
            _adapter.NotifyCaretRectChanged(x, y, width, height, scale);
        }

        /// <summary>Notify the platform that this context has received keyboard focus.</summary>
        public void NotifyFocusEnter() => _adapter.NotifyFocusEnter();

        /// <summary>Notify the platform that this context has lost keyboard focus.</summary>
        public void NotifyFocusLeave() => _adapter.NotifyFocusLeave();

        /// <summary>
        /// Notify the platform adapter that layout bounds or control geometry changed.
        /// Mirrors WinUI's `NotifyLayoutChanged` semantics.
        /// </summary>
        public void NotifyLayoutChanged() => _adapter.NotifyLayoutChanged();

        /// <summary>
        /// Notify the platform adapter that the selection changed using explicit caret positions.
        /// Mirrors WinUI's `NotifySelectionChanged` semantics.
        /// </summary>
        public void NotifySelectionChanged(CoreTextRange range) => _adapter.NotifySelectionChanged(range);

        /// <summary>
        /// Forward a key event to the platform IME for processing.
        /// Returns <c>true</c> if the IME consumed the key (caller should suppress normal handling).
        /// </summary>
        /// <param name="virtualKey">The virtual key code (cast from <c>Windows.System.VirtualKey</c>).</param>
        /// <param name="shiftPressed">Whether the Shift modifier is active.</param>
        /// <param name="controlPressed">Whether the Control modifier is active.</param>
        /// <param name="unicodeKey">Optional Unicode character for keys that map to VirtualKey.None.</param>
        public bool ProcessKeyEvent(int virtualKey, bool shiftPressed, bool controlPressed, char? unicodeKey = null)
            => _adapter.ProcessKeyEvent(virtualKey, shiftPressed, controlPressed, unicodeKey);

        /// <inheritdoc />
        public void Dispose() => _adapter.Dispose();

        // ----- Internal raise helpers (called by adapters) -----

        /// <summary>Raise the <see cref="TextRequested"/> event.</summary>
        public void RaiseTextRequested(CoreTextTextRequestedEventArgs e) => TextRequested?.Invoke(this, e);

        /// <summary>Raise the <see cref="TextUpdating"/> event.</summary>
        public void RaiseTextUpdating(CoreTextTextUpdatingEventArgs e) => TextUpdating?.Invoke(this, e);

        /// <summary>Raise the <see cref="SelectionRequested"/> event.</summary>
        public void RaiseSelectionRequested(CoreTextSelectionRequestedEventArgs e) => SelectionRequested?.Invoke(this, e);

        /// <summary>Raise the <see cref="SelectionUpdating"/> event.</summary>
        public void RaiseSelectionUpdating(CoreTextSelectionUpdatingEventArgs e) => SelectionUpdating?.Invoke(this, e);

        /// <summary>Raise the <see cref="LayoutRequested"/> event.</summary>
        public void RaiseLayoutRequested(CoreTextLayoutRequestedEventArgs e) => LayoutRequested?.Invoke(this, e);

        /// <summary>Raise the <see cref="CompositionStarted"/> event.</summary>
        public void RaiseCompositionStarted() => CompositionStarted?.Invoke(this, new CoreTextCompositionStartedEventArgs());

        /// <summary>Raise the <see cref="CompositionCompleted"/> event.</summary>
        public void RaiseCompositionCompleted() => CompositionCompleted?.Invoke(this, new CoreTextCompositionCompletedEventArgs());

        /// <summary>Raise the <see cref="FocusRemoved"/> event.</summary>
        public void RaiseFocusRemoved() => FocusRemoved?.Invoke(this, EventArgs.Empty);

        /// <summary>Raise the <see cref="CommandReceived"/> event.</summary>
        public void RaiseCommandReceived(CoreTextCommandReceivedEventArgs e) => CommandReceived?.Invoke(this, e);

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

            string nativeTypeName = nativeWindow.GetType().FullName ?? string.Empty;
            if (nativeTypeName == "System.Windows.Window")
            {
                try
                {
                    Type? helperType = Type.GetType(
                        "System.Windows.Interop.WindowInteropHelper, PresentationFramework, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35");
                    helperType ??= Type.GetType("System.Windows.Interop.WindowInteropHelper, PresentationFramework");
                    if (helperType is null)
                    {
                        return nint.Zero;
                    }

                    object? helper = Activator.CreateInstance(helperType, nativeWindow);
                    PropertyInfo? handleProp = helperType.GetProperty("Handle", BindingFlags.Instance | BindingFlags.Public);
                    return ToNativeHandle(handleProp?.GetValue(helper));
                }
                catch
                {
                    return nint.Zero;
                }
            }

            foreach (string name in new[] { "Hwnd", "HWnd", "Handle", "WindowHandle", "NativeHandle", "Pointer", "hwnd", "_hwnd" })
            {
                PropertyInfo? p = nativeWindow.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p is not null)
                {
                    nint handle = ToNativeHandle(p.GetValue(nativeWindow));
                    if (handle != nint.Zero)
                    {
                        return handle;
                    }
                }

                FieldInfo? f = nativeWindow.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f is not null)
                {
                    nint handle = ToNativeHandle(f.GetValue(nativeWindow));
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

                var hostType = Type.GetType("Uno.WinUI.Runtime.Skia.X11.X11XamlRootHost, Uno.UI.Runtime.Skia.X11");
                if (hostType is null)
                {
                    return;
                }

                var getHost = hostType.GetMethod("GetHostFromWindow", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var host = getHost?.Invoke(null, new object[] { window });
                if (host is null)
                {
                    return;
                }

                var rootX11WindowProp = hostType.GetProperty("RootX11Window", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var x11Window = rootX11WindowProp?.GetValue(host);
                if (x11Window is null)
                {
                    return;
                }

                var windowType = x11Window.GetType();
                display = ToNativeHandle(windowType.GetProperty("Display", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(x11Window));
                nativeWindow = ToNativeHandle(windowType.GetProperty("Window", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(x11Window));
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
}
