using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace LeXtudio.UI.Text.Core
{
    /// <summary>
    /// macOS text input adapter that bridges AppKit IME callbacks through
    /// libUnoEditMacInput.dylib when available.
    /// </summary>
    internal sealed class MacOSTextInputAdapter : IPlatformTextInputAdapter
    {
        private static readonly bool s_debug =
            string.Equals(Environment.GetEnvironmentVariable("UNOEDIT_DEBUG_IME"), "1", StringComparison.Ordinal);

        private static long s_nextEventId;

        private readonly InsertTextDelegate _insertTextDelegate;
        private readonly SetMarkedTextDelegate _setMarkedTextDelegate;
        private readonly UnmarkTextDelegate _unmarkTextDelegate;
        private readonly CommandDelegate _commandDelegate;

        private CoreTextEditContext? _context;
        private GCHandle _selfHandle;
        private nint _bridgeHandle;
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

        public MacOSTextInputAdapter()
        {
            _insertTextDelegate = OnInsertText;
            _setMarkedTextDelegate = OnSetMarkedText;
            _unmarkTextDelegate = OnUnmarkText;
            _commandDelegate = OnCommand;
        }

        /// <inheritdoc />
        public bool Attach(nint windowHandle, nint displayHandle, CoreTextEditContext context)
        {
            if (windowHandle == nint.Zero)
            {
                Log("Attach failed: window handle is zero.");
                return false;
            }

            _context = context;
            _selfHandle = GCHandle.Alloc(this);

            try
            {
                nint managedContext = GCHandle.ToIntPtr(_selfHandle);
                _bridgeHandle = NativeMethods.unoedit_ime_create(
                    windowHandle,
                    managedContext,
                    Marshal.GetFunctionPointerForDelegate(_insertTextDelegate),
                    Marshal.GetFunctionPointerForDelegate(_setMarkedTextDelegate),
                    Marshal.GetFunctionPointerForDelegate(_unmarkTextDelegate),
                    Marshal.GetFunctionPointerForDelegate(_commandDelegate));

                if (_bridgeHandle == nint.Zero)
                {
                    Log("Attach failed: unoedit_ime_create returned null.");
                    _selfHandle.Free();
                    return false;
                }

                Log($"Attach succeeded: bridge=0x{_bridgeHandle:X}");
                return true;
            }
            catch (DllNotFoundException)
            {
                Log("Attach failed: libUnoEditMacInput.dylib was not found.");
            }
            catch (EntryPointNotFoundException ex)
            {
                Log($"Attach failed: missing native entry point: {ex.Message}");
            }
            catch (Exception ex)
            {
                Log($"Attach failed: {ex.Message}");
            }

            if (_selfHandle.IsAllocated)
            {
                _selfHandle.Free();
            }

            return false;
        }

        /// <inheritdoc />
        public void NotifyCaretRectChanged(double x, double y, double width, double height, double scale)
        {
            if (_bridgeHandle == nint.Zero)
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

                ulong eventId = (ulong)Interlocked.Increment(ref s_nextEventId);
                NativeMethods.unoedit_ime_update_caret_rect(_bridgeHandle, eventId, x, y, width, height);
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
            if (_bridgeHandle == nint.Zero)
            {
                return;
            }

            try
            {
                ulong eventId = (ulong)Interlocked.Increment(ref s_nextEventId);
                NativeMethods.unoedit_ime_update_caret_rect(_bridgeHandle, eventId, _lastCaretX, _lastCaretY, _lastCaretW, _lastCaretH);
            }
            catch (Exception ex)
            {
                Log($"NotifyLayoutChanged failed: {ex.Message}");
            }
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
            if (_bridgeHandle == nint.Zero)
            {
                return;
            }

            try
            {
                NativeMethods.unoedit_ime_focus(_bridgeHandle, true);
            }
            catch (Exception ex)
            {
                Log($"NotifyFocusEnter failed: {ex.Message}");
            }
        }

        /// <inheritdoc />
        public void NotifyFocusLeave()
        {
            if (_bridgeHandle == nint.Zero)
            {
                return;
            }

            try
            {
                NativeMethods.unoedit_ime_focus(_bridgeHandle, false);
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

            if (_bridgeHandle != nint.Zero)
            {
                try
                {
                    NativeMethods.unoedit_ime_destroy(_bridgeHandle);
                }
                catch (Exception ex)
                {
                    Log($"Dispose failed: {ex.Message}");
                }

                _bridgeHandle = nint.Zero;
            }

            if (_selfHandle.IsAllocated)
            {
                _selfHandle.Free();
            }

            _context = null;
        }

        private static void OnInsertText(nint context, nint utf8Text)
        {
            if (GCHandle.FromIntPtr(context).Target is not MacOSTextInputAdapter adapter)
            {
                return;
            }

            string? text = Marshal.PtrToStringUTF8(utf8Text);
            if (string.IsNullOrEmpty(text) || adapter._context == null)
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

        private static void OnSetMarkedText(
            nint context,
            nint utf8Text,
            int selectedStart,
            int selectedLength,
            int replacementStart,
            int replacementLength)
        {
            if (GCHandle.FromIntPtr(context).Target is not MacOSTextInputAdapter adapter || adapter._context == null)
            {
                return;
            }

            adapter.SyncSelectionFromContext();

            string text = Marshal.PtrToStringUTF8(utf8Text) ?? string.Empty;
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

            bool replacementSpecified = replacementStart >= 0 && replacementLength >= 0;
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
                if (replacementSpecified)
                {
                    start = replacementStart;
                    end = replacementStart + replacementLength;
                }

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

        private static void OnUnmarkText(nint context)
        {
            if (GCHandle.FromIntPtr(context).Target is not MacOSTextInputAdapter adapter || adapter._context == null)
            {
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

        private static void OnCommand(nint context, nint utf8Command)
        {
            if (GCHandle.FromIntPtr(context).Target is not MacOSTextInputAdapter adapter)
            {
                return;
            }

            string? command = Marshal.PtrToStringUTF8(utf8Command);
            if (string.IsNullOrEmpty(command) || adapter._context == null)
            {
                return;
            }

            // Forward the command to the consumer via CommandReceived.
            var args = new CoreTextCommandReceivedEventArgs(command);
            adapter._context.RaiseCommandReceived(args);

            // If the consumer didn't handle it, apply default composition logic.
            if (!args.Handled)
            {
                if (string.Equals(command, "insertNewline:", StringComparison.Ordinal)
                    || string.Equals(command, "cancelOperation:", StringComparison.Ordinal))
                {
                    adapter._context.RaiseCompositionCompleted();
                }
            }
        }

        private static void Log(string message)
        {
            if (!s_debug)
            {
                return;
            }

            try
            {
                string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "unoedit_ime.log");
                System.IO.File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} [MacOSAdapter] {message}{Environment.NewLine}");
            }
            catch
            {
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void InsertTextDelegate(nint context, nint utf8Text);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SetMarkedTextDelegate(
            nint context,
            nint utf8Text,
            int selectedStart,
            int selectedLength,
            int replacementStart,
            int replacementLength);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void UnmarkTextDelegate(nint context);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void CommandDelegate(nint context, nint utf8Command);

        private static class NativeMethods
        {
            [DllImport("libUnoEditMacInput.dylib", CallingConvention = CallingConvention.Cdecl)]
            internal static extern nint unoedit_ime_create(
                nint windowHandle,
                nint managedContext,
                nint insertTextCallback,
                nint setMarkedTextCallback,
                nint unmarkTextCallback,
                nint commandCallback);

            [DllImport("libUnoEditMacInput.dylib", CallingConvention = CallingConvention.Cdecl)]
            internal static extern void unoedit_ime_destroy(nint bridgeHandle);

            [DllImport("libUnoEditMacInput.dylib", CallingConvention = CallingConvention.Cdecl)]
            internal static extern void unoedit_ime_focus(nint bridgeHandle, [MarshalAs(UnmanagedType.I1)] bool focus);

            [DllImport("libUnoEditMacInput.dylib", CallingConvention = CallingConvention.Cdecl)]
            internal static extern void unoedit_ime_update_caret_rect(
                nint bridgeHandle,
                ulong eventId,
                double x,
                double y,
                double width,
                double height);
        }
    }
}
