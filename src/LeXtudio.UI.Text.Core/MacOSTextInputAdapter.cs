using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LeXtudio.UI.Text.Core
{
    /// <summary>
    /// macOS text input adapter that binds directly to Uno's IME callbacks
    /// in libUnoNativeMac.dylib.
    /// </summary>
    internal sealed class MacOSTextInputAdapter : IPlatformTextInputAdapter
    {
        private static readonly object s_gate = new();
        private static readonly Dictionary<nint, MacOSTextInputAdapter> s_adaptersByWindow = new();
        private static MacOSTextInputAdapter? s_activeAdapter;
        private static bool s_callbacksRegistered;

        // Keep delegates rooted so Marshal.GetFunctionPointerForDelegate remains valid.
        private static readonly ImeInsertTextDelegate s_insertTextDelegate = OnImeInsertText;
        private static readonly ImeSetMarkedTextDelegate s_setMarkedTextDelegate = OnImeSetMarkedText;
        private static readonly ImeUnmarkTextDelegate s_unmarkTextDelegate = OnImeUnmarkText;
        private static readonly ImeGetCaretRectDelegate s_getCaretRectDelegate = OnImeGetCaretRect;

        private static ImeInsertTextDelegate? s_prevInsertTextCallback;
        private static ImeSetMarkedTextDelegate? s_prevSetMarkedTextCallback;
        private static ImeUnmarkTextDelegate? s_prevUnmarkTextCallback;
        private static ImeGetCaretRectDelegate? s_prevGetCaretRectCallback;
        private static bool s_prevCallbacksLoaded;

        private CoreTextEditContext? _context;
        private nint _windowHandle;
        private bool _disposed;
        private double _lastCaretX;
        private double _lastCaretY;
        private double _lastCaretW;
        private double _lastCaretH;
        private int _selectionStart;
        private int _selectionEnd;
        private bool _isComposing;
        private int _compositionStart;
        private int _compositionLength;

        /// <inheritdoc />
        public bool Attach(nint windowHandle, nint displayHandle, CoreTextEditContext context)
        {
            if (windowHandle == nint.Zero)
            {
                Log("Attach failed: window handle is zero.");
                return false;
            }

            _context = context;
            _windowHandle = windowHandle;

            try
            {
                EnsureCallbacksRegistered();

                lock (s_gate)
                {
                    s_adaptersByWindow[_windowHandle] = this;
                }

                Log($"Attach succeeded: window=0x{_windowHandle:X}");
                return true;
            }
            catch (DllNotFoundException)
            {
                Log("Attach failed: libUnoNativeMac.dylib was not found.");
            }
            catch (EntryPointNotFoundException ex)
            {
                Log($"Attach failed: missing native entry point: {ex.Message}");
            }
            catch (Exception ex)
            {
                Log($"Attach failed: {ex.Message}");
            }

            return false;
        }

        /// <inheritdoc />
        public void NotifyCaretRectChanged(double x, double y, double width, double height, double scale)
        {
            if (_windowHandle == nint.Zero)
            {
                return;
            }

            try
            {
                // Remember the last caret rect so NotifyLayoutChanged can re-apply it.
                _lastCaretX = x;
                _lastCaretY = y;
                _lastCaretW = width;
                _lastCaretH = height;
            }
            catch (Exception ex)
            {
                Log($"NotifyCaretRectChanged failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Called when the editor signals layout changes (WinUI parity). Re-apply
        /// the last known caret rect to ensure the candidate window is positioned.
        /// </summary>
        public void NotifyLayoutChanged()
        {
            // Uno native IME will request the latest caret rect via callback.
            // Nothing to push here.
        }

        /// <summary>
        /// Track the current editor selection so committed text can be reported
        /// with WinUI-like range and new-selection payloads.
        /// </summary>
        public void NotifySelectionChanged(CoreTextRange range)
        {
            if (range is null)
            {
                _selectionStart = 0;
                _selectionEnd = 0;
                return;
            }

            _selectionStart = Math.Min(range.StartCaretPosition, range.EndCaretPosition);
            _selectionEnd = Math.Max(range.StartCaretPosition, range.EndCaretPosition);
        }

        /// <inheritdoc />
        public void NotifyFocusEnter()
        {
            if (_windowHandle == nint.Zero)
            {
                return;
            }

            try
            {
                lock (s_gate)
                {
                    s_activeAdapter = this;
                }

                Log($"NotifyFocusEnter: window=0x{_windowHandle:X}");
                NativeMethods.uno_set_ime_active(_windowHandle, true);
            }
            catch (Exception ex)
            {
                Log($"NotifyFocusEnter failed: {ex.Message}");
            }
        }

        /// <inheritdoc />
        public void NotifyFocusLeave()
        {
            if (_windowHandle == nint.Zero)
            {
                return;
            }

            try
            {
                Log($"NotifyFocusLeave: window=0x{_windowHandle:X}");

                lock (s_gate)
                {
                    if (ReferenceEquals(s_activeAdapter, this))
                    {
                        s_activeAdapter = null;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"NotifyFocusLeave failed: {ex.Message}");
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_windowHandle != nint.Zero)
            {
                lock (s_gate)
                {
                    s_adaptersByWindow.Remove(_windowHandle);
                    if (ReferenceEquals(s_activeAdapter, this))
                    {
                        s_activeAdapter = null;
                    }
                }

                _windowHandle = nint.Zero;
            }

            _context = null;
        }

        private static void EnsureCallbacksRegistered()
        {
            lock (s_gate)
            {
                if (s_callbacksRegistered)
                {
                    return;
                }

                LoadPreviousImeCallbacks();

                NativeMethods.uno_set_ime_callbacks(
                    Marshal.GetFunctionPointerForDelegate(s_insertTextDelegate),
                    Marshal.GetFunctionPointerForDelegate(s_setMarkedTextDelegate),
                    Marshal.GetFunctionPointerForDelegate(s_unmarkTextDelegate),
                    Marshal.GetFunctionPointerForDelegate(s_getCaretRectDelegate));

                s_callbacksRegistered = true;
                Log("Registered Uno native IME callbacks.");
            }
        }

        private static MacOSTextInputAdapter? ResolveActiveAdapter(nint windowHandle)
        {
            lock (s_gate)
            {
                if (s_activeAdapter is not null
                    && (windowHandle == nint.Zero || s_activeAdapter._windowHandle == windowHandle))
                {
                    if (s_activeAdapter._context?.IsInputActiveNow() == true)
                    {
                        return s_activeAdapter;
                    }

                    Log($"ResolveActiveAdapter: active adapter rejected ownership for window=0x{windowHandle:X}");
                    return null;
                }

                Log($"ResolveActiveAdapter: no active adapter for window=0x{windowHandle:X}");
                return null;
            }
        }

        private static void LoadPreviousImeCallbacks()
        {
            if (s_prevCallbacksLoaded)
            {
                return;
            }

            try
            {
                IntPtr insertPtr = NativeMethods.uno_get_ime_insert_text_callback();
                IntPtr setMarkedPtr = NativeMethods.uno_get_ime_set_marked_text_callback();
                IntPtr unmarkPtr = NativeMethods.uno_get_ime_unmark_text_callback();
                IntPtr getCaretPtr = NativeMethods.uno_get_ime_get_caret_rect_callback();

                if (insertPtr != IntPtr.Zero)
                {
                    s_prevInsertTextCallback = Marshal.GetDelegateForFunctionPointer<ImeInsertTextDelegate>(insertPtr);
                }

                if (setMarkedPtr != IntPtr.Zero)
                {
                    s_prevSetMarkedTextCallback = Marshal.GetDelegateForFunctionPointer<ImeSetMarkedTextDelegate>(setMarkedPtr);
                }

                if (unmarkPtr != IntPtr.Zero)
                {
                    s_prevUnmarkTextCallback = Marshal.GetDelegateForFunctionPointer<ImeUnmarkTextDelegate>(unmarkPtr);
                }

                if (getCaretPtr != IntPtr.Zero)
                {
                    s_prevGetCaretRectCallback = Marshal.GetDelegateForFunctionPointer<ImeGetCaretRectDelegate>(getCaretPtr);
                }
            }
            catch (EntryPointNotFoundException)
            {
                // If the runtime does not expose getters, no previous callbacks are preserved.
            }
            catch (Exception ex)
            {
                Log($"LoadPreviousImeCallbacks failed: {ex.Message}");
            }
            finally
            {
                s_prevCallbacksLoaded = true;
            }
        }

        private static void OnImeInsertText(nint windowHandle, nint textPtr, int length)
        {
            Log($"OnImeInsertText: window=0x{windowHandle:X}, length={length}");
            var adapter = ResolveActiveAdapter(windowHandle);
            if (adapter is null)
            {
                if (s_prevInsertTextCallback is not null)
                {
                    Log($"OnImeInsertText: forwarding to Uno callback for window=0x{windowHandle:X}");
                    s_prevInsertTextCallback(windowHandle, textPtr, length);
                }
                return;
            }

            string? text = length > 0 ? Marshal.PtrToStringUni(textPtr, length) : null;
            if (string.IsNullOrEmpty(text) || adapter._context is null)
            {
                return;
            }

            adapter.SyncSelectionFromContext();

            int rangeStart = adapter._selectionStart;
            int rangeEnd = adapter._selectionEnd;
            if (adapter._isComposing)
            {
                rangeStart = adapter._compositionStart;
                rangeEnd = adapter._compositionStart + adapter._compositionLength;
            }

            var args = new CoreTextTextUpdatingEventArgs(text);
            args.Range.StartCaretPosition = rangeStart;
            args.Range.EndCaretPosition = rangeEnd;
            args.NewSelection.StartCaretPosition = rangeStart + text.Length;
            args.NewSelection.EndCaretPosition = rangeStart + text.Length;
            adapter._selectionStart = args.NewSelection.StartCaretPosition;
            adapter._selectionEnd = args.NewSelection.EndCaretPosition;
            adapter._context.RaiseTextUpdating(args);

            adapter._isComposing = false;
            adapter._compositionLength = 0;
            adapter._context.RaiseCompositionCompleted();
        }

        private static void OnImeSetMarkedText(
            nint windowHandle,
            nint textPtr,
            int length,
            int selectedStart,
            int selectedLength)
        {
            Log($"OnImeSetMarkedText: window=0x{windowHandle:X}, length={length}, selectedStart={selectedStart}, selectedLength={selectedLength}");
            var adapter = ResolveActiveAdapter(windowHandle);
            if (adapter is null || adapter._context is null)
            {
                if (s_prevSetMarkedTextCallback is not null)
                {
                    Log($"OnImeSetMarkedText: forwarding to Uno callback for window=0x{windowHandle:X}");
                    s_prevSetMarkedTextCallback(windowHandle, textPtr, length, selectedStart, selectedLength);
                }
                return;
            }

            adapter.SyncSelectionFromContext();

            string text = length > 0 ? Marshal.PtrToStringUni(textPtr, length) ?? string.Empty : string.Empty;
            if (string.IsNullOrEmpty(text))
            {
                if (adapter._isComposing)
                {
                    var clearArgs = new CoreTextTextUpdatingEventArgs(string.Empty);
                    clearArgs.Range.StartCaretPosition = adapter._compositionStart;
                    clearArgs.Range.EndCaretPosition = adapter._compositionStart + adapter._compositionLength;
                    clearArgs.NewSelection.StartCaretPosition = adapter._compositionStart;
                    clearArgs.NewSelection.EndCaretPosition = adapter._compositionStart;
                    adapter._selectionStart = adapter._compositionStart;
                    adapter._selectionEnd = adapter._compositionStart;
                    adapter._compositionLength = 0;
                    adapter._context.RaiseTextUpdating(clearArgs);
                    adapter._isComposing = false;
                    adapter._context.RaiseCompositionCompleted();
                }

                return;
            }

            if (adapter._isComposing)
            {
                int compositionEnd = adapter._compositionStart + adapter._compositionLength;
                bool selectionOutsideComposition =
                    adapter._selectionStart < adapter._compositionStart
                    || adapter._selectionStart > compositionEnd
                    || adapter._selectionEnd < adapter._compositionStart
                    || adapter._selectionEnd > compositionEnd;

                if (selectionOutsideComposition)
                {
                    adapter._isComposing = false;
                    adapter._compositionLength = 0;
                    adapter._context.RaiseCompositionCompleted();
                }
            }

            if (!adapter._isComposing)
            {
                int start = Math.Min(adapter._selectionStart, adapter._selectionEnd);
                int end = Math.Max(adapter._selectionStart, adapter._selectionEnd);

                adapter._compositionStart = start;
                adapter._compositionLength = Math.Max(0, end - start);
                adapter._isComposing = true;
                adapter._context.RaiseCompositionStarted();
            }

            int selectionInMarkedStart = selectedStart < 0
                ? text.Length
                : Math.Clamp(selectedStart, 0, text.Length);
            int selectionInMarkedEnd = Math.Clamp(
                selectionInMarkedStart + Math.Max(0, selectedLength),
                selectionInMarkedStart,
                text.Length);

            var args = new CoreTextTextUpdatingEventArgs(text);
            args.Range.StartCaretPosition = adapter._compositionStart;
            args.Range.EndCaretPosition = adapter._compositionStart + adapter._compositionLength;
            args.NewSelection.StartCaretPosition = adapter._compositionStart + selectionInMarkedStart;
            args.NewSelection.EndCaretPosition = adapter._compositionStart + selectionInMarkedEnd;
            adapter._compositionLength = text.Length;
            adapter._selectionStart = args.NewSelection.StartCaretPosition;
            adapter._selectionEnd = args.NewSelection.EndCaretPosition;
            adapter._context.RaiseTextUpdating(args);
        }

        private void SyncSelectionFromContext()
        {
            if (_context is null)
            {
                return;
            }

            try
            {
                var request = new CoreTextSelectionRequest();
                _context.RaiseSelectionRequested(new CoreTextSelectionRequestedEventArgs(request));
                int start = Math.Min(request.StartCaretPosition, request.EndCaretPosition);
                int end = Math.Max(request.StartCaretPosition, request.EndCaretPosition);
                _selectionStart = start;
                _selectionEnd = end;
            }
            catch
            {
                // Ignore failures and keep last known adapter selection.
            }
        }

        private static void OnImeUnmarkText(nint windowHandle)
        {
            Log($"OnImeUnmarkText: window=0x{windowHandle:X}");
            var adapter = ResolveActiveAdapter(windowHandle);
            if (adapter is null || adapter._context is null)
            {
                if (s_prevUnmarkTextCallback is not null)
                {
                    Log($"OnImeUnmarkText: forwarding to Uno callback for window=0x{windowHandle:X}");
                    s_prevUnmarkTextCallback(windowHandle);
                }
                return;
            }

            if (!adapter._isComposing)
            {
                return;
            }

            adapter._isComposing = false;
            adapter._compositionLength = 0;
            adapter._context.RaiseCompositionCompleted();
        }

        private static void OnImeGetCaretRect(nint windowHandle, out double x, out double y, out double width, out double height)
        {
            var adapter = ResolveActiveAdapter(windowHandle);
            if (adapter is null)
            {
                if (s_prevGetCaretRectCallback is not null)
                {
                    Log($"OnImeGetCaretRect: forwarding to Uno callback for window=0x{windowHandle:X}");
                    s_prevGetCaretRectCallback(windowHandle, out x, out y, out width, out height);
                    return;
                }

                x = y = width = height = 0;
                Log($"OnImeGetCaretRect: no adapter for window=0x{windowHandle:X}");
                return;
            }

            x = adapter._lastCaretX;
            y = adapter._lastCaretY;
            width = adapter._lastCaretW;
            height = adapter._lastCaretH;
            Log($"OnImeGetCaretRect: window=0x{windowHandle:X}, rect=({x:F1},{y:F1},{width:F1},{height:F1})");
        }

        private static void Log(string message)
        {
            try
            {
                ImeLogging.AppendLine($"{DateTime.Now:HH:mm:ss.fff} [MacOSAdapter pid={Environment.ProcessId}] {message}");
            }
            catch
            {
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ImeInsertTextDelegate(nint windowHandle, nint textPtr, int length);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ImeSetMarkedTextDelegate(
            nint windowHandle,
            nint textPtr,
            int length,
            int selectedStart,
            int selectedLength);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ImeUnmarkTextDelegate(nint windowHandle);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ImeGetCaretRectDelegate(
            nint windowHandle,
            out double x,
            out double y,
            out double width,
            out double height);

        private static class NativeMethods
        {
            [DllImport("libUnoNativeMac.dylib", CallingConvention = CallingConvention.Cdecl)]
            internal static extern void uno_set_ime_callbacks(
                nint insertTextCallback,
                nint setMarkedTextCallback,
                nint unmarkTextCallback,
                nint getCaretRectCallback);

            [DllImport("libUnoNativeMac.dylib", CallingConvention = CallingConvention.Cdecl)]
            internal static extern void uno_set_ime_active(nint windowHandle, [MarshalAs(UnmanagedType.I1)] bool active);

            [DllImport("libUnoNativeMac.dylib", CallingConvention = CallingConvention.Cdecl)]
            internal static extern IntPtr uno_get_ime_insert_text_callback();

            [DllImport("libUnoNativeMac.dylib", CallingConvention = CallingConvention.Cdecl)]
            internal static extern IntPtr uno_get_ime_set_marked_text_callback();

            [DllImport("libUnoNativeMac.dylib", CallingConvention = CallingConvention.Cdecl)]
            internal static extern IntPtr uno_get_ime_unmark_text_callback();

            [DllImport("libUnoNativeMac.dylib", CallingConvention = CallingConvention.Cdecl)]
            internal static extern IntPtr uno_get_ime_get_caret_rect_callback();
        }
    }
}
