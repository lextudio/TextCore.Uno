using System;
using System.Diagnostics;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
// single-line text box: no multiline helpers required
using Windows.Foundation;
using Windows.System;
using LeXtudio.UI.Text.Core;

namespace LeXtudio.UI.Text.Core.Sample.Controls;

/// <summary>
/// Very small demonstration control that wires a CoreTextEditContext to a simple visual.
/// It shows committed text and composition text and forwards key events to the platform adapter.
/// This is intentionally minimal — it demonstrates the event flow rather than a full editor.
/// </summary>
public sealed class CoreTextBox : UserControl, IDisposable
{
    private readonly TextBlock _prefix;
    private readonly TextBlock _rest;
    private readonly Rectangle _caret;
    private readonly Border _border;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _caretTimer;
    private bool _caretVisible;
    private readonly TextBlock _measureBlock;
    private CoreTextEditContext? _context;
    private bool _isComposing;
    private bool _hasFocus;
    // Set when focus is requested by a pointer press so we only show the caret
    // when the user clicked the control.
    private bool _pendingPointerFocus;
    // Visible start index for horizontal scrolling when focused.
    private int _visibleStart = 0;
    private int _prevVisibleStart = -1;
    private int _prevCaretIndex = -1;
    private string _composition = string.Empty;
    private string _text = string.Empty;
    private int _caretIndex = 0;

    // Public API: expose Text property and TextChanged event.
    public event EventHandler? TextChanged;

    public string Text
    {
        get => _text;
        set
        {
            var newValue = value ?? string.Empty;
            if (!string.Equals(_text, newValue, StringComparison.Ordinal))
            {
                _text = newValue;
                _caretIndex = Math.Clamp(_caretIndex, 0, _text.Length);
                UpdateDisplay();
                try { TextChanged?.Invoke(this, EventArgs.Empty); } catch { }
            }
        }
    }

    public CoreTextBox()
    {
        // Make the control focusable so it can receive keyboard events.
        this.IsTabStop = true;
        this.TabIndex = 0;

        _prefix = new TextBlock { Text = "Click to focus and type...", FontSize = 14, Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray), TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
        _rest = new TextBlock { Text = string.Empty, FontSize = 14, Foreground = new SolidColorBrush(Microsoft.UI.Colors.Black), TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
        _caret = new Rectangle { Width = 2, Height = 18, Fill = new SolidColorBrush(Microsoft.UI.Colors.Black), Visibility = Visibility.Visible, Opacity = 0.0, VerticalAlignment = VerticalAlignment.Center, Margin = new Microsoft.UI.Xaml.Thickness(2,0,2,0) };

        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(_prefix);
        panel.Children.Add(_caret);
        panel.Children.Add(_rest);

        _border = new Border
        {
            BorderThickness = new Microsoft.UI.Xaml.Thickness(1),
            Padding = new Microsoft.UI.Xaml.Thickness(2),
            Child = panel,
            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Gray),
            MinHeight = 18,
            MinWidth = 160,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
        };

        // Reusable text measurer (not added to visual tree)
        _measureBlock = new TextBlock { FontSize = _prefix.FontSize, FontFamily = _prefix.FontFamily, FontWeight = _prefix.FontWeight, TextWrapping = TextWrapping.NoWrap };

        // Adjust caret height when layout changes so it visually matches the text line.
        _border.SizeChanged += (_, __) =>
        {
            try
            {
                double textHeight = MeasureTextLineHeight();
                double h = Math.Max(12.0, textHeight);
                _caret.Height = h;
            }
            catch { }
            try
            {
                double w = _border.ActualWidth;
                double hh = _border.ActualHeight;
                double avail = Math.Max(0.0, w - _border.Padding.Left - _border.Padding.Right - 4.0);
                Console.WriteLine($"CoreTextBox: SizeChanged ActualWidth={w:F1} ActualHeight={hh:F1} availableWidth={avail:F1}");
            }
            catch { }

            // Recompute visible window when size changes
            try { UpdateDisplay(); } catch { }
        };

        this.Content = _border;

        this.Loaded += OnLoaded;
        this.Unloaded += OnUnloaded;
        Console.WriteLine("CoreTextBox: ctor");

        // Ensure clicks on the visible border focus the control and position the caret.
        _border.PointerPressed += (_, e) =>
        {
            var pt = e.GetCurrentPoint(_border).Position;
            Console.WriteLine($"CoreTextBox: PointerPressed at {pt}");

            int idx = ComputeCaretIndexFromPointer(pt);
            // Map display index back to committed-text index (do not place caret past committed text)
            idx = Math.Clamp(idx, 0, _text.Length);
            _caretIndex = idx;
            UpdateDisplay();

            // If already focused, show/start the caret immediately. Otherwise mark
            // that focus was requested by a pointer and request focus; GotFocus
            // will then start the caret blinking.
            if (_hasFocus)
            {
                _caretVisible = true;
                _caret.Opacity = 1.0;
                _caretTimer?.Start();
                Console.WriteLine($"CoreTextBox: PointerPressed (already focused) caretIndex={_caretIndex}");
            }
            else
            {
                _pendingPointerFocus = true;
                bool focused = this.Focus(FocusState.Pointer);
                Console.WriteLine($"CoreTextBox: Focus called, result={focused}, caretIndex={_caretIndex}");
            }
            e.Handled = true;
        };

        // Route focus and keyboard events to this UserControl so handlers fire.
        this.GotFocus += OnGotFocus;
        this.LostFocus += OnLostFocus;
        this.KeyDown += OnKeyDown;

        // Create caret blink timer using DispatcherQueue if available.
        try
        {
            _caretTimer = this.DispatcherQueue?.CreateTimer();
            if (_caretTimer != null)
            {
                _caretTimer.Interval = TimeSpan.FromMilliseconds(530);
                _caretTimer.IsRepeating = true;
                _caretTimer.Tick += (s, a) =>
                {
                    _caretVisible = !_caretVisible;
                    // Toggle opacity instead of Visibility so layout does not change when caret blinks.
                    _caret.Opacity = (_caretVisible && _hasFocus) ? 1.0 : 0.0;
                };
            }
        }
        catch { /* best-effort */ }
        this.UpdateDisplay();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Create edit context but delay Attach until focused so window handle is stable.
        if (_context is null)
        {
            _context = CoreTextServicesManager.GetForCurrentView().CreateEditContext();
            _context.TextRequested += OnTextRequested;
            _context.TextUpdating += OnTextUpdating;
            _context.CompositionStarted += OnCompositionStarted;
            _context.CompositionCompleted += OnCompositionCompleted;
            _context.CommandReceived += OnCommandReceived;
        }
        Console.WriteLine("CoreTextBox: Loaded");
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Dispose();
    }

    private void OnGotFocus(object sender, RoutedEventArgs e)
    {
        if (_context is null) return;
        Console.WriteLine("CoreTextBox: GotFocus");
        _hasFocus = true;
        _caretIndex = _text.Length;
        // Update focus visual
        try
        {
            _border.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue);
            _border.Background = new SolidColorBrush(Microsoft.UI.Colors.LightBlue);

            // Only show/blink the caret when focus was requested by a pointer click.
            if (_pendingPointerFocus)
            {
                _caretVisible = true;
                // Show immediately
                _caret.Opacity = 1.0;
                _caretTimer?.Start();
            }
            else
            {
                // Keep caret hidden (but present in layout) when focus was not from a pointer.
                _caretVisible = false;
                _caret.Opacity = 0.0;
            }
        }
        catch { }

        // Clear the pointer-focus marker after handling GotFocus.
        _pendingPointerFocus = false;

        // Attach to native window handle (best-effort). Try to resolve native window handle via reflection first,
        // then fall back to process main window handle.
        nint hwnd = TryGetNativeWindowHandle();
        try
        {
            bool attached = _context.Attach(hwnd);
            Console.WriteLine($"CoreTextBox: Attach(hwnd=0x{hwnd:X}) -> {attached}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"CoreTextBox: Attach threw: {ex}");
        }
        _context.NotifyFocusEnter();

        // Update visuals (remove placeholder when focused)
        UpdateDisplay();
    }

    private void OnLostFocus(object sender, RoutedEventArgs e)
    {
        if (_context is null) return;
        Console.WriteLine("CoreTextBox: LostFocus");
        _hasFocus = false;
        try
        {
            _border.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Gray);
            _border.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

            // Stop caret blinking and hide caret when unfocused.
            _caretTimer?.Stop();
            _caretVisible = false;
            // Keep caret in layout but hide visually so layout doesn't shift.
            _caret.Opacity = 0.0;
        }
        catch { }

        // Clear any pending pointer focus marker.
        _pendingPointerFocus = false;

        _context.NotifyFocusLeave();

        // Update visuals (restore placeholder when unfocused)
        UpdateDisplay();
    }

    private void OnKeyDown(object? sender, KeyRoutedEventArgs e)
    {
        if (_context is null)
            return;

        bool ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        bool shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        Console.WriteLine($"CoreTextBox: KeyDown {e.Key} (shift={shift}, ctrl={ctrl})");

        // Forward key to platform IME adapter; if it handles the key, suppress normal handling.
        try
        {
            bool consumed = _context.ProcessKeyEvent((int)e.Key, shift, ctrl, null);
            if (consumed)
            {
                e.Handled = true;
                return;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"CoreTextBox: ProcessKeyEvent threw: {ex}");
        }

        // Minimal editing support for the demo: Backspace + printable keys when not composing.
        if (e.Key == VirtualKey.Back)
        {
            if (_caretIndex > 0)
            {
                string newText = _text.Remove(_caretIndex - 1, 1);
                _caretIndex--;
                Console.WriteLine($"CoreTextBox: Backspace -> textLen={newText.Length} caretIndex={_caretIndex}");
                SetTextInternal(newText);
            }
            e.Handled = true;
            return;
        }

        if (!_isComposing)
        {
            // Simple letter handling for the demo (A-Z)
            if (e.Key >= VirtualKey.A && e.Key <= VirtualKey.Z)
            {
                int offset = (int)e.Key - (int)VirtualKey.A;
                char ch = (char)('a' + offset);
                if (shift) ch = char.ToUpperInvariant(ch);
                string newText = _text.Insert(_caretIndex, ch.ToString());
                _caretIndex++;
                Console.WriteLine($"CoreTextBox: Typed '{ch}' -> textLen={newText.Length} caretIndex={_caretIndex}");
                SetTextInternal(newText);
                e.Handled = true;
                return;
            }

            if (e.Key == VirtualKey.Space)
            {
                string newText = _text.Insert(_caretIndex, " ");
                _caretIndex++;
                Console.WriteLine($"CoreTextBox: Typed ' ' (space) -> textLen={newText.Length} caretIndex={_caretIndex}");
                SetTextInternal(newText);
                e.Handled = true;
                return;
            }
        }
    }

    private void OnCommandReceived(object? sender, CoreTextCommandReceivedEventArgs e)
    {
        Console.WriteLine($"CoreTextBox: CommandReceived '{e.Command}'");
        switch (e.Command)
        {
            case "deleteBackward:":
                if (_caretIndex > 0)
                {
                    string newText = _text.Remove(_caretIndex - 1, 1);
                    _caretIndex--;
                    SetTextInternal(newText);
                }
                e.Handled = true;
                break;
            case "deleteForward:":
                if (_caretIndex < _text.Length)
                {
                    string newText = _text.Remove(_caretIndex, 1);
                    SetTextInternal(newText);
                }
                e.Handled = true;
                break;
            case "selectAll:":
                SelectAll();
                e.Handled = true;
                break;
        }
    }

    private void OnCompositionStarted(object? sender, EventArgs e)
    {
        _isComposing = true;
        _composition = string.Empty;
        Console.WriteLine("CoreTextBox: CompositionStarted");
        UpdateDisplay();
    }

    private void OnCompositionCompleted(object? sender, EventArgs e)
    {
        _isComposing = false;
        _composition = string.Empty;
        Console.WriteLine("CoreTextBox: CompositionCompleted");
        UpdateDisplay();
    }

    private void OnTextUpdating(object? sender, CoreTextTextUpdatingEventArgs e)
    {
        _composition = e.NewText ?? string.Empty;
        Console.WriteLine($"CoreTextBox: TextUpdating new='{_composition}'");
        UpdateDisplay();
    }

    private void OnTextRequested(object? sender, CoreTextTextRequestedEventArgs e)
    {
        // Platform provided commit text (e.Request.Text) — insert it at the caret.
        try
        {
            var request = e.Request;
            if (request is null) return;
            string incoming = request.Text ?? string.Empty;
            Console.WriteLine($"CoreTextBox: TextRequested commit='{incoming}' -> inserting at {_caretIndex}");
            string newText = _text.Insert(_caretIndex, incoming);
            _caretIndex += incoming.Length;
            _isComposing = false;
            _composition = string.Empty;
            SetTextInternal(newText);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"CoreTextBox: TextRequested handler threw: {ex}");
        }
    }

    private void UpdateDisplay()
    {
        if (string.IsNullOrEmpty(_text) && !_isComposing)
        {
            // When focused, hide the placeholder so the box appears empty and ready to type.
            if (_hasFocus)
            {
                _prefix.Text = string.Empty;
                _rest.Text = string.Empty;
                _prefix.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray);
                // Keep caret in layout but show visually only if blinking is active.
                _caret.Opacity = (_hasFocus && _caretVisible) ? 1.0 : 0.0;

                // Notify platform of the caret position even when text is empty, so the IME candidate
                // window is positioned correctly before the user starts composing.
                try
                {
                    double scale = this.XamlRoot?.RasterizationScale ?? 1.0;
                    try
                    {
                        var transform = _border.TransformToVisual(null);
                        var pt = transform.TransformPoint(new Point(_border.Padding.Left, 0));
                        _context?.NotifyCaretRectChanged(pt.X, pt.Y + 3.0, _caret.Width, _caret.Height, scale);
                        Console.WriteLine($"CoreTextBox: NotifyCaretRectChanged (empty) x={pt.X:F1} y={pt.Y:F1} w={_caret.Width:F1} h={_caret.Height:F1} scale={scale:F2}");
                    }
                    catch (Exception ex)
                    {
                        _context?.NotifyCaretRectChanged(_border.Padding.Left, 0, _caret.Width, _caret.Height, scale);
                        Console.WriteLine($"CoreTextBox: NotifyCaretRectChanged (empty/approx) err={ex.Message}");
                    }
                }
                catch { }
            }
            else
            {
                _prefix.Text = "Click to focus and type...";
                _prefix.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray);
                _rest.Text = string.Empty;
                // Keep caret in layout but hide visually when empty/unfocused.
                _caret.Opacity = 0.0;
            }

            return;
        }

        _prefix.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Black);

        int safeIndex = Math.Clamp(_caretIndex, 0, _text.Length);

        // Determine available width inside border for the text content.
        double availableWidth = 0.0;
        try
        {
            availableWidth = Math.Max(0.0, _border.ActualWidth - _border.Padding.Left - _border.Padding.Right - 4.0);
        }
        catch { availableWidth = 160.0; }

        // When unfocused, always show the left side of the text.
        if (!_hasFocus)
        {
            _visibleStart = 0;
            string prefix = _text.Substring(0, safeIndex);
            string remainder = _text.Substring(safeIndex);
            if (_isComposing)
            {
                remainder = "[" + _composition + "]" + remainder;
            }
            _prefix.Text = prefix;
            _rest.Text = remainder;
            _caret.Opacity = 0.0;
            try
            {
                string prefixTrail = prefix.Length > 20 ? "..." + prefix.Substring(prefix.Length - 20) : prefix;
                string remainderHead = remainder.Length > 20 ? remainder.Substring(0, 20) + "..." : remainder;
                Console.WriteLine($"CoreTextBox: UpdateDisplay (unfocused) size={_border.ActualWidth:F1} available={availableWidth:F1} caretIndex={safeIndex} visibleStart={_visibleStart} prefixLen={prefix.Length} prefixTrail='{prefixTrail}' remainderHead='{remainderHead}'");
                if (_prevVisibleStart != _visibleStart) Console.WriteLine($"CoreTextBox: Scrolled visibleStart {_prevVisibleStart} -> {_visibleStart}");
                if (_prevCaretIndex != safeIndex) Console.WriteLine($"CoreTextBox: Caret moved {_prevCaretIndex} -> {safeIndex}");
                _prevVisibleStart = _visibleStart;
                _prevCaretIndex = safeIndex;
            }
            catch { }
            return;
        }

        // Focused: if full text fits within a target area (leaving a right gap), show from left;
        // otherwise compute a visible start so the caret appears near the right edge with some padding.
            try
            {
                // Tighten reserved right gap so caret stays very near the control's right edge.
                const double rightGap = 2.0; // minimal gap for tighter caret placement
                double caretTotal = _caret.Width + _caret.Margin.Left + _caret.Margin.Right;
                double targetWidth = Math.Max(0.0, availableWidth - rightGap - caretTotal);

            // Quick check: does the entire prefix up to caret fit within the target width?
            double fullPrefixWidth = MeasureTextWidth(_text.Substring(0, safeIndex));
            if (fullPrefixWidth <= targetWidth)
            {
                _visibleStart = 0;
                // ensure prefix uses natural width when it fits
                _prefix.Width = double.NaN;
                _prefix.TextAlignment = TextAlignment.Left;
            }
            else
            {
                // Find the largest start index so that substring(start, safeIndex-start)
                // fits within targetWidth. This shows the rightmost text up to the caret,
                // leaving a gap on the right for new input.
                // Find the leftmost start index such that the substring from start..caret
                // fits within targetWidth. This maximizes the number of visible chars
                // to the left of the caret (so new chars appear to the right of caret).
                int low = 0;
                int high = Math.Max(0, safeIndex);
                while (low < high)
                {
                    int mid = (low + high) / 2;
                    double w = MeasureTextWidth(_text.Substring(mid, safeIndex - mid));
                    if (w <= targetWidth)
                    {
                        // mid works, try to include more chars (move left)
                        high = mid;
                    }
                    else
                    {
                        // too wide, move right
                        low = mid + 1;
                    }
                }

                if (low >= safeIndex)
                {
                    // nothing fits; at least show one char before caret
                    _visibleStart = Math.Max(0, safeIndex - 1);
                }
                else
                {
                    _visibleStart = low;
                }

                // Force prefix to occupy the target width so the caret appears near the right edge.
                _prefix.Width = targetWidth;
                _prefix.TextAlignment = TextAlignment.Right;
            }
        }
        catch { _visibleStart = 0; }

        // Compose the displayed pieces from the visible window.
        int prefixStart = Math.Clamp(_visibleStart, 0, safeIndex);
        string displayPrefix = _text.Substring(prefixStart, safeIndex - prefixStart);
        string displayRemainder = _text.Substring(safeIndex);
        if (_isComposing)
        {
            displayRemainder = "[" + _composition + "]" + displayRemainder;
        }

        _prefix.Text = displayPrefix;
        _rest.Text = displayRemainder;

        _caret.Opacity = (_hasFocus && _caretVisible) ? 1.0 : 0.0;

        // Notify platform about the caret rect so IME candidate windows can be positioned.
        try
        {
            double prefixWidth = MeasureTextWidth(displayPrefix);
            double scale = this.XamlRoot?.RasterizationScale ?? 1.0;

            try
            {
                var transform = _border.TransformToVisual(null);
                var pt = transform.TransformPoint(new Point(prefixWidth + _border.Padding.Left, 0));
                _context?.NotifyCaretRectChanged(pt.X, pt.Y + 3.0, _caret.Width, _caret.Height, scale);
                Console.WriteLine($"CoreTextBox: NotifyCaretRectChanged x={pt.X:F1} y={pt.Y:F1} w={_caret.Width:F1} h={_caret.Height:F1} scale={scale:F2}");
            }
            catch (Exception ex)
            {
                double approxX = _border.Padding.Left + prefixWidth;
                _context?.NotifyCaretRectChanged(approxX, 0, _caret.Width, _caret.Height, scale);
                Console.WriteLine($"CoreTextBox: NotifyCaretRectChanged(approx) x={approxX:F1} err={ex.Message}");
            }
        }
        catch { }

        try
        {
            string prefixTrail = displayPrefix.Length > 20 ? "..." + displayPrefix.Substring(displayPrefix.Length - 20) : displayPrefix;
            string remainderHead = displayRemainder.Length > 20 ? displayRemainder.Substring(0, 20) + "..." : displayRemainder;
            Console.WriteLine($"CoreTextBox: UpdateDisplay size={_border.ActualWidth:F1} available={availableWidth:F1} caretIndex={safeIndex} visibleStart={_visibleStart} prefixLen={displayPrefix.Length} prefixTrail='{prefixTrail}' remainderHead='{remainderHead}'");
            if (_prevVisibleStart != _visibleStart) Console.WriteLine($"CoreTextBox: Scrolled visibleStart {_prevVisibleStart} -> {_visibleStart}");
            if (_prevCaretIndex != safeIndex) Console.WriteLine($"CoreTextBox: Caret moved {_prevCaretIndex} -> {safeIndex}");
            _prevVisibleStart = _visibleStart;
            _prevCaretIndex = safeIndex;
        }
        catch { }
    }

    private int ComputeCaretIndexFromPointer(Point pt)
    {
        try
        {
            // Single-line mode: compute caret index from X position only.
            double xInLine = pt.X - _border.Padding.Left;
            if (xInLine <= 0) return 0;

            int n = _text.Length;
            if (n == 0) return 0;

            // Search between the visible start and the end of the text so pointer mapping
            // respects the current horizontal scroll offset.
            int low = Math.Clamp(_visibleStart, 0, n);
            int high = n;
            while (low < high)
            {
                int mid = (low + high + 1) / 2;
                double w = MeasureTextWidth(_text.Substring(_visibleStart, mid - _visibleStart));
                if (w <= xInLine) low = mid;
                else high = mid - 1;
            }

            return Math.Clamp(low, 0, n);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ComputeCaretIndexFromPointer failed: {ex}");
            return 0;
        }
    }

    // Multiline wrapping helpers removed — single-line text box only.

    private static nint TryGetNativeWindowHandle()
    {
        // Strategy taken from UnoEdit: try WinRT interop, then call WindowHelper.GetNativeWindow via
        // reflection over loaded assemblies, and finally inspect common properties/fields for a native handle.
        object? windowObj = null;

        // 1) Try typical WinRT type if available: Microsoft.UI.Xaml.Window.Current
        try
        {
            var windowType = Type.GetType("Microsoft.UI.Xaml.Window, Microsoft.UI.Xaml");
            if (windowType != null)
            {
                var currentProp = windowType.GetProperty("Current", BindingFlags.Static | BindingFlags.Public);
                if (currentProp != null)
                {
                    windowObj = currentProp.GetValue(null);
                    if (windowObj != null)
                    {
                        var interopType = Type.GetType("WinRT.Interop.WindowNative, WinRT");
                        var getHandle = interopType?.GetMethod("GetWindowHandle", BindingFlags.Static | BindingFlags.Public);
                        if (getHandle != null)
                        {
                            var ret = getHandle.Invoke(null, new[] { windowObj });
                            if (ret is nint ni && ni != nint.Zero) return ni;
                            if (ret is IntPtr ip && ip != IntPtr.Zero) return (nint)ip;
                            if (ret is long lv && lv != 0) return new nint(lv);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"TryGetNativeWindowHandle: WinRT lookup failed: {ex.Message}");
        }

        // 2) Try to locate a Window instance by scanning loaded assemblies for a type named 'Window'
        if (windowObj == null)
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try { types = asm.GetTypes(); } catch { continue; }
                    foreach (var t in types)
                    {
                        if (string.Equals(t.Name, "Window", StringComparison.Ordinal))
                        {
                            var current = t.GetProperty("Current", BindingFlags.Static | BindingFlags.Public);
                            if (current != null)
                            {
                                var val = current.GetValue(null);
                                if (val != null)
                                {
                                    windowObj = val;
                                    break;
                                }
                            }
                        }
                    }

                    if (windowObj != null) break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"TryGetNativeWindowHandle: scanning assemblies for Window failed: {ex.Message}");
            }
        }

        // 3) If we found a Window object, attempt to call WindowHelper.GetNativeWindow(window)
        object? nativeWindow = null;
        if (windowObj != null)
        {
                try
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        Type? wh = null;
                        try
                        {
                            var types = asm.GetTypes();
                            foreach (var t in types)
                            {
                                try
                                {
                                    if (t.Name == "WindowHelper" || (t.FullName != null && t.FullName.EndsWith(".WindowHelper")))
                                    {
                                        wh = t;
                                        break;
                                    }
                                }
                                catch { }
                            }
                        }
                        catch { }

                        if (wh == null) continue;

                        var getNative = wh.GetMethod("GetNativeWindow", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        if (getNative == null) continue;

                        try
                        {
                            nativeWindow = getNative.Invoke(null, new[] { windowObj });
                            if (nativeWindow != null) break;
                        }
                        catch { }
                    }
                }
            catch (Exception ex)
            {
                Console.WriteLine($"TryGetNativeWindowHandle: WindowHelper lookup failed: {ex.Message}");
            }
        }

        // 4) If nativeWindow is available inspect well-known properties/fields for a pointer/handle
        if (nativeWindow != null)
        {
            string nativeTypeName = nativeWindow.GetType().FullName ?? string.Empty;

            // Handle common Uno/Win32 host types by probing known names
            if (nativeTypeName == "System.Windows.Window")
            {
                try
                {
                    Type? helperType = Type.GetType(
                        "System.Windows.Interop.WindowInteropHelper, PresentationFramework, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35");
                    helperType ??= Type.GetType("System.Windows.Interop.WindowInteropHelper, PresentationFramework");
                    if (helperType != null)
                    {
                        object? helper = Activator.CreateInstance(helperType, nativeWindow);
                        PropertyInfo? handleProp = helperType.GetProperty("Handle", BindingFlags.Instance | BindingFlags.Public);
                        if (handleProp?.GetValue(helper) is IntPtr hwndIntPtr)
                        {
                            return (nint)hwndIntPtr;
                        }
                    }
                }
                catch { }

                return nint.Zero;
            }

            // Uno Skia / Win32 native window wrappers
            if (nativeTypeName.Contains("Win32NativeWindow", StringComparison.Ordinal))
            {
                foreach (string name in new[] { "Hwnd", "HWnd", "Handle", "WindowHandle", "NativeHandle", "Pointer", "hwnd", "_hwnd" })
                {
                    try
                    {
                        var p = nativeWindow.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (p != null)
                        {
                            var v = p.GetValue(nativeWindow);
                            if (v is nint np && np != nint.Zero) return np;
                            if (v is IntPtr ip && ip != IntPtr.Zero) return (nint)ip;
                            if (v is long lv && lv != 0) return new nint(lv);
                        }

                        var f = nativeWindow.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (f != null)
                        {
                            var v = f.GetValue(nativeWindow);
                            if (v is nint np && np != nint.Zero) return np;
                            if (v is IntPtr ip && ip != IntPtr.Zero) return (nint)ip;
                            if (v is long lv && lv != 0) return new nint(lv);
                        }
                    }
                    catch { }
                }

                return nint.Zero;
            }

            // Generic 'Handle' property fallback
            try
            {
                var handleProperty = nativeWindow.GetType().GetProperty("Handle", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (handleProperty?.GetValue(nativeWindow) is nint ni && ni != nint.Zero) return ni;
                if (handleProperty?.GetValue(nativeWindow) is IntPtr ip2 && ip2 != IntPtr.Zero) return (nint)ip2;
                if (handleProperty?.GetValue(nativeWindow) is long lv2 && lv2 != 0) return new nint(lv2);
            }
            catch { }
        }

        // 5) Fallback to process main window handle
        return System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
    }

    private double MeasureTextWidth(string s)
    {
        try
        {
            _measureBlock.Text = s;
            _measureBlock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return _measureBlock.DesiredSize.Width;
        }
        catch { return 0.0; }
    }

    private double MeasureTextLineHeight()
    {
        try
        {
            // Measure a short sample string using the control's font properties.
            var tb = new TextBlock
            {
                Text = "Mg",
                FontSize = _prefix.FontSize,
                FontFamily = _prefix.FontFamily,
                FontWeight = _prefix.FontWeight,
                FontStyle = _prefix.FontStyle,
                FontStretch = _prefix.FontStretch
            };

            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double h = tb.DesiredSize.Height;
            if (h > 0)
            {
                return h;
            }
        }
        catch { }

        // Fallback: approximate from font size
        return Math.Max(12.0, _prefix.FontSize * 1.2);
    }

    private void SetTextInternal(string newText)
    {
        if (newText == null) newText = string.Empty;
        bool changed = !string.Equals(_text, newText, StringComparison.Ordinal);
        _text = newText;
        _caretIndex = Math.Clamp(_caretIndex, 0, _text.Length);
        UpdateDisplay();
        if (changed)
        {
            try { TextChanged?.Invoke(this, EventArgs.Empty); } catch { }
        }
    }

    /// <summary>
    /// Select all text in the control. Minimal implementation for the sample:
    /// ensures the control is focused, scrolls to show the start, and places
    /// the caret at the end of the text.
    /// </summary>
    public void SelectAll()
    {
        try
        {
            // If the control isn't focused, request programmatic focus so caret will show.
            if (!_hasFocus)
            {
                _pendingPointerFocus = false;
                _ = this.Focus(FocusState.Programmatic);
            }
        }
        catch { }

        // Show from the start of the text and place caret at the end.
        _visibleStart = 0;
        _caretIndex = Math.Clamp(_text?.Length ?? 0, 0, _text?.Length ?? 0);
        UpdateDisplay();
    }

    public void Dispose()
    {
        if (_context is not null)
        {
            _context.TextRequested -= OnTextRequested;
            _context.TextUpdating -= OnTextUpdating;
            _context.CompositionStarted -= OnCompositionStarted;
            _context.CompositionCompleted -= OnCompositionCompleted;
            _context.Dispose();
            _context = null;
        }
        try
        {
            _caretTimer?.Stop();
            _caretTimer = null;
        }
        catch { }
    }
}
