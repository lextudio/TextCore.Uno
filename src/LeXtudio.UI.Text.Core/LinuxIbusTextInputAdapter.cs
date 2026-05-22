using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace LeXtudio.UI.Text.Core
{
    /// <summary>
    /// Ubuntu/Linux adapter based on IBus over D-Bus.
    /// Attaches to an IBus input context, forwards focus/caret updates,
    /// and translates IBus signals into <see cref="CoreTextEditContext"/> events.
    /// </summary>
    internal sealed class LinuxIbusTextInputAdapter : IPlatformTextInputAdapter
    {
        private static readonly object s_sharedGate = new();
        private static LinuxIbusTextInputAdapter? s_activeSharedAdapter;

        private const uint IbusCapPreeditText = 1u << 0;
        private const uint IbusCapFocus = 1u << 3;

        private LinuxIBusConnection? _ibus;
        private UnoX11ImeBridge? _unoBridge;
        private bool _usingUnoSharedIme;
        private CoreTextEditContext? _context;
        private bool _disposed;
        private bool _isComposing;
        private bool _suppressNextDirectCommit;
        private int _selectionStart;
        private int _selectionEnd;
        private int _compositionStart;
        private int _compositionLength;
        private bool _compositionAwaitingCommit;
        private nint _x11Display;
        private nint _x11Window;
        private double _lastCaretX;
        private double _lastCaretY;
        private double _lastCaretW;
        private double _lastCaretH;
        private double _lastCaretScale;

        [DllImport("libX11.so.6")]
        private static extern bool XTranslateCoordinates(
            nint display, nint src_w, nint dest_w,
            int src_x, int src_y,
            out int dest_x_return, out int dest_y_return,
            out nint child_return);

        [DllImport("libX11.so.6")]
        private static extern nint XDefaultRootWindow(nint display);

        /// <inheritdoc />
        public bool Attach(nint windowHandle, nint displayHandle, CoreTextEditContext context)
        {
            _context = context;
            _x11Window = windowHandle;
            _x11Display = displayHandle;

            _unoBridge = UnoX11ImeBridge.TryCreate(this);
            if (_unoBridge is not null)
            {
                _usingUnoSharedIme = true;
                Log("Attach succeeded: using Uno X11 shared IME bridge.");
                return true;
            }

            _ibus = LinuxIBusConnection.TryConnect();
            if (_ibus == null || !_ibus.IsConnected)
            {
                Log("Attach failed: IBus is unavailable.");
                return false;
            }

            _ibus.SetCapabilities(IbusCapPreeditText | IbusCapFocus);

            Log("Attach succeeded: Linux IBus adapter active.");
            return true;
        }

        /// <inheritdoc />
        public void NotifyCaretRectChanged(double x, double y, double width, double height, double scale)
        {
            // Remember last caret rect/scale so NotifyLayoutChanged can reapply it.
            _lastCaretX = x;
            _lastCaretY = y;
            _lastCaretW = width;
            _lastCaretH = height;
            _lastCaretScale = scale;

            int screenX = (int)(x * scale);
            int screenY = (int)(y * scale);
            int w = Math.Max(1, (int)(width * scale));
            int h = Math.Max(1, (int)(height * scale));

            // Convert window-relative to screen-absolute for IME candidate window placement.
            if (_x11Display != nint.Zero && _x11Window != nint.Zero)
            {
                try
                {
                    nint root = XDefaultRootWindow(_x11Display);
                    if (XTranslateCoordinates(_x11Display, _x11Window, root,
                            screenX, screenY, out int destX, out int destY, out _))
                    {
                        screenX = destX;
                        screenY = destY;
                    }
                }
                catch
                {
                }
            }

            if (_usingUnoSharedIme)
            {
                if (IsActiveSharedOwner())
                {
                    _unoBridge?.TrySetCursorLocation(screenX, screenY, w, h);
                }
                return;
            }

            if (_ibus == null || !OwnsInput())
            {
                return;
            }

            _ibus.SetCursorLocation(screenX, screenY, w, h);
        }

        /// <summary>
        /// Re-apply the last known caret rect. Called by hosts when layout changes
        /// so IME candidate windows can be repositioned (WinUI parity).
        /// </summary>
        public void NotifyLayoutChanged()
        {
            if (_usingUnoSharedIme)
            {
                if (_lastCaretScale != 0)
                {
                    NotifyCaretRectChanged(_lastCaretX, _lastCaretY, _lastCaretW, _lastCaretH, _lastCaretScale);
                }
                return;
            }

            if (_ibus == null || _lastCaretScale == 0 || !OwnsInput())
            {
                return;
            }

            int screenX = (int)(_lastCaretX * _lastCaretScale);
            int screenY = (int)(_lastCaretY * _lastCaretScale);
            int w = Math.Max(1, (int)(_lastCaretW * _lastCaretScale));
            int h = Math.Max(1, (int)(_lastCaretH * _lastCaretScale));

            if (_x11Display != nint.Zero && _x11Window != nint.Zero)
            {
                try
                {
                    nint root = XDefaultRootWindow(_x11Display);
                    if (XTranslateCoordinates(_x11Display, _x11Window, root,
                            screenX, screenY, out int destX, out int destY, out _))
                    {
                        screenX = destX;
                        screenY = destY;
                    }
                }
                catch
                {
                }
            }

            _ibus.SetCursorLocation(screenX, screenY, w, h);
        }

        /// <summary>
        /// Track the current editor selection so preedit/commit updates can emit
        /// WinUI-like range and new-selection payloads.
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
            if (_usingUnoSharedIme)
            {
                lock (s_sharedGate)
                {
                    s_activeSharedAdapter = this;
                }
                return;
            }

            if (OwnsInput())
            {
                _ibus?.FocusIn();
            }
        }

        /// <inheritdoc />
        public void NotifyFocusLeave()
        {
            if (_usingUnoSharedIme)
            {
                lock (s_sharedGate)
                {
                    if (ReferenceEquals(s_activeSharedAdapter, this))
                    {
                        s_activeSharedAdapter = null;
                    }
                }
                return;
            }

            _ibus?.FocusOut();
        }

        /// <inheritdoc />
        public bool ProcessKeyEvent(int virtualKey, bool shiftPressed, bool controlPressed, char? unicodeKey = null)
        {
            if (_usingUnoSharedIme)
            {
                if (!OwnsInput() || !IsActiveSharedOwner())
                {
                    return false;
                }

                if (controlPressed)
                {
                    return false;
                }

                // In shared mode, textual keys should be consumed by the Uno X11 IME
                // channel to avoid duplicate editor insertion from fallback key paths.
                if ((unicodeKey.HasValue && !char.IsControl(unicodeKey.Value)) || _unoBridge?.IsComposing == true)
                {
                    return true;
                }

                return false;
            }

            if (_ibus == null || !_ibus.IsConnected || !OwnsInput())
            {
                return false;
            }

            // Control-key shortcuts are handled by the editor, not the IME.
            if (controlPressed)
            {
                return false;
            }

            uint keyval = X11KeyHelper.ConvertToX11Keysym(virtualKey, shiftPressed);

            // For OEM keys (VirtualKey.None on Skia/Linux), the keysym equals the
            // Unicode codepoint for printable ASCII characters.
            if (keyval == 0 && unicodeKey.HasValue && unicodeKey.Value >= 0x20 && unicodeKey.Value < 0x7F)
            {
                keyval = (uint)unicodeKey.Value;
            }

            if (keyval == 0)
            {
                return false; // unknown key — let the editor handle it
            }

            uint state = X11KeyHelper.GetX11ModifierState(shiftPressed, controlPressed);

            // Drain any pending signals before sending the key event.
            DrainSignals();

            var (handled, _) = _ibus.ProcessKeyEvent(keyval, 0, state);
            Log($"ProcessKeyEvent keyval=0x{keyval:X} state=0x{state:X} -> handled={handled}");

            // Drain signals that IBus may have produced in response.
            // Signals may arrive shortly after the method reply, so use a
            // blocking read with a short timeout for the first attempt.
            DrainSignals(blockTimeoutMs: 50);

            return handled;
        }

        private void DrainSignals(int blockTimeoutMs = 0)
        {
            if (_ibus?.Connection == null)
            {
                return;
            }

            for (int i = 0; i < 16; i++)
            {
                LinuxDBusMessage? msg;
                if (i == 0 && blockTimeoutMs > 0)
                {
                    // First iteration: use a blocking receive to wait for signals
                    // that may arrive shortly after the ProcessKeyEvent reply.
                    msg = _ibus.Connection.TryReceiveOne(blockTimeoutMs);
                    if (msg != null && msg.Type == LinuxDBusConstants.Signal)
                    {
                        Log($"DrainSignals: blocking receive got signal: {msg.Interface}.{msg.Member}");
                    }
                    else if (msg != null)
                    {
                        Log($"DrainSignals: blocking receive got non-signal type={msg.Type} interface={msg.Interface} member={msg.Member}");
                        continue;
                    }
                    else
                    {
                        // Check the signal queue in case SendAndWaitReply queued it.
                        msg = _ibus.Connection.Poll();
                        if (msg != null)
                        {
                            Log($"DrainSignals: poll got queued msg type={msg.Type} interface={msg.Interface} member={msg.Member}");
                        }
                    }
                }
                else
                {
                    msg = _ibus.Connection.Poll();
                    if (msg != null)
                    {
                        Log($"DrainSignals: poll got msg type={msg.Type} interface={msg.Interface} member={msg.Member}");
                    }
                }

                if (msg == null)
                {
                    break;
                }

                if (msg.Type != LinuxDBusConstants.Signal || msg.Interface != "org.freedesktop.IBus.InputContext")
                {
                    continue;
                }

                switch (msg.Member)
                {
                    case "CommitText":
                        HandleCommitText(msg);
                        break;
                    case "UpdatePreeditText":
                        HandleUpdatePreeditText(msg);
                        break;
                    case "HidePreeditText":
                        _isComposing = false;
                        _compositionLength = 0;
                        // HidePreeditText indicates composition is finished without commit.
                        // Finalize immediately.
                        _compositionAwaitingCommit = false;
                        _context?.RaiseCompositionCompleted();
                        break;
                }
            }

            // If an UpdatePreeditText cleared the preedit (empty text) and we
            // deferred finalization waiting for a CommitText, finalize now.
            if (_compositionAwaitingCommit)
            {
                _compositionAwaitingCommit = false;
                _compositionLength = 0;
                _context?.RaiseCompositionCompleted();
            }
        }

        private bool OwnsInput()
        {
            return _context?.IsInputActiveNow() == true;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            _unoBridge?.Dispose();
            _unoBridge = null;
            _usingUnoSharedIme = false;

            lock (s_sharedGate)
            {
                if (ReferenceEquals(s_activeSharedAdapter, this))
                {
                    s_activeSharedAdapter = null;
                }
            }

            _ibus?.Dispose();
            _ibus = null;
            _context = null;
        }

        private bool IsActiveSharedOwner()
        {
            lock (s_sharedGate)
            {
                return ReferenceEquals(s_activeSharedAdapter, this);
            }
        }

        private void OnUnoCompositionStarted()
        {
            if (!OwnsInput() || !IsActiveSharedOwner())
            {
                return;
            }

            if (_suppressNextDirectCommit && !_isComposing)
            {
                Log("OnUnoCompositionStarted: suppressed synthetic direct-commit start.");
                return;
            }

            if (!_isComposing)
            {
                _isComposing = true;
                _compositionStart = _selectionStart;
                _compositionLength = Math.Max(0, _selectionEnd - _selectionStart);
                _context?.RaiseCompositionStarted();
            }
        }

        private void OnUnoCompositionUpdated(string text, int cursorPos)
        {
            if (!OwnsInput() || !IsActiveSharedOwner())
            {
                return;
            }

            if (string.IsNullOrEmpty(text))
            {
                if (_isComposing)
                {
                    _isComposing = false;
                    _compositionAwaitingCommit = true;
                }

                return;
            }

            _suppressNextDirectCommit = false;

            if (!_isComposing)
            {
                _isComposing = true;
                _compositionStart = _selectionStart;
                _compositionLength = Math.Max(0, _selectionEnd - _selectionStart);
                _context?.RaiseCompositionStarted();
            }

            // X11 shared IME may report cursorPos=0 for whole-string preedit updates
            // (especially CJK candidate flow). Keep caret at preedit end for editor parity.
            int selectionInMarked = cursorPos <= 0 ? text.Length : Math.Clamp(cursorPos, 0, text.Length);

            var args = new CoreTextTextUpdatingEventArgs(text);
            args.Range.StartCaretPosition = _compositionStart;
            args.Range.EndCaretPosition = _compositionStart + _compositionLength;
            args.NewSelection.StartCaretPosition = _compositionStart + selectionInMarked;
            args.NewSelection.EndCaretPosition = _compositionStart + selectionInMarked;
            _compositionLength = text.Length;
            _selectionStart = args.NewSelection.StartCaretPosition;
            _selectionEnd = args.NewSelection.EndCaretPosition;
            _context?.RaiseTextUpdating(args);
        }

        private void OnUnoCompositionCompleted(string text)
        {
            if (!OwnsInput() || !IsActiveSharedOwner())
            {
                return;
            }

            if (string.IsNullOrEmpty(text))
            {
                if (_isComposing)
                {
                    _isComposing = false;
                    _compositionLength = 0;
                    _compositionAwaitingCommit = false;
                    // X11 shared IME frequently sends an empty completion first,
                    // then a synthetic direct-commit cycle with the same final text.
                    // Arm suppression so that follow-up cycle doesn't insert twice.
                    _suppressNextDirectCommit = true;
                    Log("OnUnoCompositionCompleted: empty completion; armed synthetic direct-commit suppression.");
                    _context?.RaiseCompositionCompleted();
                }

                return;
            }

            if (_suppressNextDirectCommit && !_isComposing)
            {
                Log("OnUnoCompositionCompleted: suppressed synthetic direct-commit completion.");
                _suppressNextDirectCommit = false;
                return;
            }

            int rangeStart;
            int rangeEnd;

            if (_isComposing || _compositionAwaitingCommit)
            {
                rangeStart = _compositionStart;
                rangeEnd = _compositionStart + _compositionLength;
            }
            else
            {
                rangeStart = _selectionStart;
                rangeEnd = _selectionEnd;
            }

            var args = new CoreTextTextUpdatingEventArgs(text);
            args.Range.StartCaretPosition = rangeStart;
            args.Range.EndCaretPosition = rangeEnd;
            args.NewSelection.StartCaretPosition = rangeStart + text.Length;
            args.NewSelection.EndCaretPosition = rangeStart + text.Length;
            _context?.RaiseTextUpdating(args);

            _selectionStart = args.NewSelection.StartCaretPosition;
            _selectionEnd = args.NewSelection.EndCaretPosition;
            _isComposing = false;
            _compositionAwaitingCommit = false;
            _compositionLength = 0;
            _suppressNextDirectCommit = true;
            _context?.RaiseCompositionCompleted();
        }

        private sealed class UnoX11ImeBridge : IDisposable
        {
            private readonly LinuxIbusTextInputAdapter _owner;
            private readonly object _instance;
            private readonly EventInfo _startedEvent;
            private readonly EventInfo _updatedEvent;
            private readonly EventInfo _completedEvent;
            private readonly FieldInfo? _dbusImeField;
            private Delegate? _startedHandler;
            private Delegate? _updatedHandler;
            private Delegate? _completedHandler;

            private UnoX11ImeBridge(
                LinuxIbusTextInputAdapter owner,
                object instance,
                EventInfo startedEvent,
                EventInfo updatedEvent,
                EventInfo completedEvent,
                FieldInfo? dbusImeField)
            {
                _owner = owner;
                _instance = instance;
                _startedEvent = startedEvent;
                _updatedEvent = updatedEvent;
                _completedEvent = completedEvent;
                _dbusImeField = dbusImeField;
            }

            public bool IsComposing
            {
                get
                {
                    try
                    {
                        PropertyInfo? isComposing = _instance.GetType().GetProperty("IsComposing", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        return (bool?)isComposing?.GetValue(_instance) == true;
                    }
                    catch
                    {
                        return false;
                    }
                }
            }

            public static UnoX11ImeBridge? TryCreate(LinuxIbusTextInputAdapter owner)
            {
                try
                {
                    Type? extType = Type.GetType("Uno.WinUI.Runtime.Skia.X11.X11ImeTextBoxExtension, Uno.UI.Runtime.Skia.X11", false);
                    if (extType is null)
                    {
                        return null;
                    }

                    PropertyInfo? instanceProp = extType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    object? instance = instanceProp?.GetValue(null);
                    if (instance is null)
                    {
                        return null;
                    }

                    EventInfo? startedEvent = extType.GetEvent("CompositionStarted", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    EventInfo? updatedEvent = extType.GetEvent("CompositionUpdated", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    EventInfo? completedEvent = extType.GetEvent("CompositionCompleted", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    FieldInfo? dbusImeField = extType.GetField("_dbusIme", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (startedEvent?.EventHandlerType is null || updatedEvent?.EventHandlerType is null || completedEvent?.EventHandlerType is null)
                    {
                        return null;
                    }

                    MethodInfo startedMethod = typeof(UnoX11ImeBridge).GetMethod(nameof(OnStarted), BindingFlags.Instance | BindingFlags.NonPublic)!;
                    MethodInfo updatedMethod = typeof(UnoX11ImeBridge).GetMethod(nameof(OnUpdated), BindingFlags.Instance | BindingFlags.NonPublic)!;
                    MethodInfo completedMethod = typeof(UnoX11ImeBridge).GetMethod(nameof(OnCompleted), BindingFlags.Instance | BindingFlags.NonPublic)!;

                    UnoX11ImeBridge bridge = new(
                        owner,
                        instance,
                        startedEvent,
                        updatedEvent,
                        completedEvent,
                        dbusImeField);

                    bridge._startedHandler = Delegate.CreateDelegate(startedEvent.EventHandlerType, bridge, startedMethod);
                    bridge._updatedHandler = Delegate.CreateDelegate(updatedEvent.EventHandlerType, bridge, updatedMethod);
                    bridge._completedHandler = Delegate.CreateDelegate(completedEvent.EventHandlerType, bridge, completedMethod);

                    startedEvent.AddEventHandler(instance, bridge._startedHandler);
                    updatedEvent.AddEventHandler(instance, bridge._updatedHandler);
                    completedEvent.AddEventHandler(instance, bridge._completedHandler);
                    return bridge;
                }
                catch (Exception ex)
                {
                    Log($"UnoX11ImeBridge.TryCreate failed: {ex.Message}");
                    return null;
                }
            }

            public void Dispose()
            {
                if (_startedHandler is not null)
                {
                    try { _startedEvent.RemoveEventHandler(_instance, _startedHandler); } catch { }
                    _startedHandler = null;
                }

                if (_updatedHandler is not null)
                {
                    try { _updatedEvent.RemoveEventHandler(_instance, _updatedHandler); } catch { }
                    _updatedHandler = null;
                }

                if (_completedHandler is not null)
                {
                    try { _completedEvent.RemoveEventHandler(_instance, _completedHandler); } catch { }
                    _completedHandler = null;
                }
            }

            public bool TrySetCursorLocation(int x, int y, int w, int h)
            {
                try
                {
                    object? dbusIme = _dbusImeField?.GetValue(_instance);
                    if (dbusIme is null)
                    {
                        Log("UnoX11ImeBridge.TrySetCursorLocation: _dbusIme is null.");
                        return false;
                    }

                    PropertyInfo? enabledProp = dbusIme.GetType().GetProperty("IsEnabled", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if ((bool?)enabledProp?.GetValue(dbusIme) != true)
                    {
                        Log("UnoX11ImeBridge.TrySetCursorLocation: _dbusIme is not enabled.");
                        return false;
                    }

                    MethodInfo? setCursorLocation = dbusIme.GetType().GetMethod("SetCursorLocation", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (setCursorLocation is null)
                    {
                        Log("UnoX11ImeBridge.TrySetCursorLocation: SetCursorLocation method not found.");
                        return false;
                    }

                    setCursorLocation.Invoke(dbusIme, new object[] { x, y, w, h });
                    Log($"UnoX11ImeBridge.TrySetCursorLocation: x={x} y={y} w={w} h={h}");
                    return true;
                }
                catch (Exception ex)
                {
                    Log($"UnoX11ImeBridge.TrySetCursorLocation failed: {ex.Message}");
                    return false;
                }
            }

            private void OnStarted(object? sender, EventArgs e)
            {
                _owner.OnUnoCompositionStarted();
            }

            private void OnUpdated(object? sender, object e)
            {
                try
                {
                    Type t = e.GetType();
                    string text = (string?)t.GetProperty("Text")?.GetValue(e) ?? string.Empty;
                    int cursorPos = (int?)t.GetProperty("CursorPosition")?.GetValue(e) ?? -1;
                    _owner.OnUnoCompositionUpdated(text, cursorPos);
                }
                catch (Exception ex)
                {
                    Log($"UnoX11ImeBridge.OnUpdated failed: {ex.Message}");
                }
            }

            private void OnCompleted(object? sender, object e)
            {
                try
                {
                    Type t = e.GetType();
                    string text = (string?)t.GetProperty("Text")?.GetValue(e) ?? string.Empty;
                    _owner.OnUnoCompositionCompleted(text);
                }
                catch (Exception ex)
                {
                    Log($"UnoX11ImeBridge.OnCompleted failed: {ex.Message}");
                }
            }
        }

        private void HandleCommitText(LinuxDBusMessage msg)
        {
            Log($"HandleCommitText: body.Length={msg.Body.Length}");
            if (msg.Body.Length == 0)
            {
                return;
            }

            try
            {
                var reader = new LinuxDBusReader(msg.Body, 0);
                string? text = reader.ReadIBusText();
                Log($"HandleCommitText: text='{text}'");
                if (!string.IsNullOrEmpty(text))
                {
                    int rangeStart;
                    int rangeEnd;

                    if (_isComposing || _compositionAwaitingCommit)
                    {
                        // Replace the last composition range (even if we deferred
                        // finalization due to an empty preedit signal arriving
                        // before the CommitText).
                        rangeStart = _compositionStart;
                        rangeEnd = _compositionStart + _compositionLength;
                    }
                    else
                    {
                        rangeStart = _selectionStart;
                        rangeEnd = _selectionEnd;
                    }

                    var args = new CoreTextTextUpdatingEventArgs(text);
                    args.Range.StartCaretPosition = rangeStart;
                    args.Range.EndCaretPosition = rangeEnd;
                    args.NewSelection.StartCaretPosition = rangeStart + text.Length;
                    args.NewSelection.EndCaretPosition = rangeStart + text.Length;
                    _context?.RaiseTextUpdating(args);

                    _selectionStart = args.NewSelection.StartCaretPosition;
                    _selectionEnd = args.NewSelection.EndCaretPosition;
                    _isComposing = false;
                    _compositionAwaitingCommit = false;
                    _compositionLength = 0;
                    _context?.RaiseCompositionCompleted();
                }
            }
            catch (Exception ex)
            {
                Log($"HandleCommitText parse error: {ex.Message}");
            }
        }

        private void HandleUpdatePreeditText(LinuxDBusMessage msg)
        {
            Log($"HandleUpdatePreeditText: body.Length={msg.Body.Length}");
            if (msg.Body.Length == 0)
            {
                return;
            }

            try
            {
                var reader = new LinuxDBusReader(msg.Body, 0);
                string? text = reader.ReadIBusText();
                uint _ = reader.ReadUInt32();
                bool visible = reader.ReadBool();

                Log($"HandleUpdatePreeditText: text='{text}' visible={visible}");

                if (!visible || string.IsNullOrEmpty(text))
                {
                    if (_isComposing)
                    {
                        // IME cleared the preedit text. Defer finalization briefly
                        // to allow a subsequent CommitText message to replace the
                        // preedit rather than inserting again (prevents duplicates).
                        _isComposing = false;
                        _compositionAwaitingCommit = true;
                        // Do not raise CompositionCompleted here; it will be raised
                        // after a CommitText or at the end of DrainSignals.
                    }

                    return;
                }

                if (!_isComposing)
                {
                    _isComposing = true;
                    _compositionStart = _selectionStart;
                    _compositionLength = Math.Max(0, _selectionEnd - _selectionStart);
                    _context?.RaiseCompositionStarted();
                }

                var args = new CoreTextTextUpdatingEventArgs(text);
                args.Range.StartCaretPosition = _compositionStart;
                args.Range.EndCaretPosition = _compositionStart + _compositionLength;
                args.NewSelection.StartCaretPosition = _compositionStart + text.Length;
                args.NewSelection.EndCaretPosition = _compositionStart + text.Length;
                _compositionLength = text.Length;
                _selectionStart = args.NewSelection.StartCaretPosition;
                _selectionEnd = args.NewSelection.EndCaretPosition;
                _context?.RaiseTextUpdating(args);
            }
            catch (Exception ex)
            {
                Log($"HandleUpdatePreeditText parse error: {ex.Message}");
            }
        }

        private static void Log(string message)
        {
            try
            {
                ImeLogging.AppendLine($"{DateTime.Now:HH:mm:ss.fff} [LinuxAdapter] {message}");
            }
            catch
            {
            }
        }
    }
}
