using System;
using System.Diagnostics;
using System.Reflection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace LeXtudio.UI.Controls;

/// <summary>
/// A TextBox with a CoreTextEditContext IME bridge for correct IME input on all Uno platforms.
/// </summary>
public sealed class TextBox : UserControl, IDisposable
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(
            nameof(Text),
            typeof(string),
            typeof(TextBox),
            new PropertyMetadata(string.Empty, OnTextPropertyChanged));

    public static readonly DependencyProperty PlaceholderTextProperty =
        DependencyProperty.Register(
            nameof(PlaceholderText),
            typeof(string),
            typeof(TextBox),
            new PropertyMetadata("Type here...", OnPlaceholderTextPropertyChanged));

    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(
            nameof(Header),
            typeof(object),
            typeof(TextBox),
            new PropertyMetadata(null, OnHeaderPropertyChanged));

    public static readonly DependencyProperty AcceptsReturnProperty =
        DependencyProperty.Register(
            nameof(AcceptsReturn),
            typeof(bool),
            typeof(TextBox),
            new PropertyMetadata(false, OnAcceptsReturnPropertyChanged));

    public static readonly DependencyProperty TextWrappingProperty =
        DependencyProperty.Register(
            nameof(TextWrapping),
            typeof(TextWrapping),
            typeof(TextBox),
            new PropertyMetadata(TextWrapping.NoWrap, OnTextWrappingPropertyChanged));

    private readonly Microsoft.UI.Xaml.Controls.TextBox _textBox;
    private LeXtudio.UI.Text.Core.CoreTextEditContext? _context;
    private bool _isApplyingImeText;
    private bool _isComposing;
    private int _compositionStart;
    private int _compositionLength;

    public event TextChangedEventHandler? TextChanged;

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value ?? string.Empty);
    }

    public string PlaceholderText
    {
        get => (string)GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value ?? string.Empty);
    }

    public object? Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public bool AcceptsReturn
    {
        get => (bool)GetValue(AcceptsReturnProperty);
        set => SetValue(AcceptsReturnProperty, value);
    }

    public TextWrapping TextWrapping
    {
        get => (TextWrapping)GetValue(TextWrappingProperty);
        set => SetValue(TextWrappingProperty, value);
    }

    public TextBox()
    {
        _textBox = new Microsoft.UI.Xaml.Controls.TextBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            PlaceholderText = PlaceholderText,
            Header = Header,
            AcceptsReturn = AcceptsReturn,
            TextWrapping = TextWrapping,
        };

        _textBox.TextChanged += OnTextBoxTextChanged;
        _textBox.GotFocus += OnTextBoxGotFocus;
        _textBox.LostFocus += OnTextBoxLostFocus;
        _textBox.SelectionChanged += OnTextBoxSelectionChanged;
        _textBox.KeyDown += OnTextBoxKeyDown;
        _textBox.SizeChanged += (_, _) => SyncPlatformState();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Content = new Grid { Children = { _textBox } };
    }

    public new void Dispose()
    {
        DisposeContext();
        GC.SuppressFinalize(this);
    }

    private static void OnTextPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (TextBox)d;
        string text = e.NewValue as string ?? string.Empty;

        if (control._textBox.Text != text)
        {
            control._textBox.Text = text;
        }
    }

    private static void OnPlaceholderTextPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (TextBox)d;
        control._textBox.PlaceholderText = e.NewValue as string ?? string.Empty;
    }

    private static void OnHeaderPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (TextBox)d;
        control._textBox.Header = e.NewValue;
    }

    private static void OnAcceptsReturnPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (TextBox)d;
        control._textBox.AcceptsReturn = (bool)e.NewValue;
    }

    private static void OnTextWrappingPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (TextBox)d;
        control._textBox.TextWrapping = (TextWrapping)e.NewValue;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        EnsureContext();
        SyncPlatformState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DisposeContext();
    }

    private void OnTextBoxGotFocus(object sender, RoutedEventArgs e)
    {
        if (!EnsureContext())
        {
            return;
        }

        try
        {
            _context?.NotifyFocusEnter();
            SyncPlatformState();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"TextBox focus enter failed: {ex}");
        }
    }

    private void OnTextBoxLostFocus(object sender, RoutedEventArgs e)
    {
        EndComposition();

        try
        {
            _context?.NotifyFocusLeave();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"TextBox focus leave failed: {ex}");
        }
    }

    private void OnTextBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        if (Text != _textBox.Text)
        {
            SetValue(TextProperty, _textBox.Text);
        }

        if (!_isApplyingImeText && _isComposing)
        {
            EndComposition();
        }

        TextChanged?.Invoke(this, e);
        SyncPlatformState();
    }

    private void OnTextBoxSelectionChanged(object sender, RoutedEventArgs e)
    {
        SyncPlatformState();
    }

    private void OnTextBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_context is null)
        {
            return;
        }

        bool ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        bool shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        try
        {
            if (_context.ProcessKeyEvent((int)e.Key, shift, ctrl))
            {
                e.Handled = true;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"TextBox ProcessKeyEvent failed: {ex}");
        }
    }

    private bool EnsureContext()
    {
        if (_context is not null)
        {
            return true;
        }

        try
        {
            _context = LeXtudio.UI.Text.Core.CoreTextServicesManager.GetForCurrentView().CreateEditContext();
            _context.InputScope = LeXtudio.UI.Text.Core.CoreTextInputScope.Text;
            _context.TextRequested += OnTextRequested;
            _context.TextUpdating += OnTextUpdating;
            _context.SelectionRequested += OnSelectionRequested;
            _context.SelectionUpdating += OnSelectionUpdating;
            _context.LayoutRequested += OnLayoutRequested;
            _context.CompositionStarted += OnCompositionStarted;
            _context.CompositionCompleted += OnCompositionCompleted;
            _context.FocusRemoved += OnFocusRemoved;
            _context.CommandReceived += OnCommandReceived;

            bool attached = _context.AttachToCurrentWindow(Window.Current);
            Debug.WriteLine($"TextBox attached CoreTextEditContext: {attached}");
            return attached;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"TextBox context initialization failed: {ex}");
            DisposeContext();
            return false;
        }
    }

    private void DisposeContext()
    {
        if (_context is null)
        {
            return;
        }

        _context.TextRequested -= OnTextRequested;
        _context.TextUpdating -= OnTextUpdating;
        _context.SelectionRequested -= OnSelectionRequested;
        _context.SelectionUpdating -= OnSelectionUpdating;
        _context.LayoutRequested -= OnLayoutRequested;
        _context.CompositionStarted -= OnCompositionStarted;
        _context.CompositionCompleted -= OnCompositionCompleted;
        _context.FocusRemoved -= OnFocusRemoved;
        _context.CommandReceived -= OnCommandReceived;
        _context.Dispose();
        _context = null;
    }

    private void OnTextRequested(LeXtudio.UI.Text.Core.CoreTextEditContext sender, LeXtudio.UI.Text.Core.CoreTextTextRequestedEventArgs args)
    {
        string text = _textBox.Text ?? string.Empty;
        int start = Math.Clamp(args.Request.Range.StartCaretPosition, 0, text.Length);
        int end = Math.Clamp(args.Request.Range.EndCaretPosition, start, text.Length);
        args.Request.Text = text.Substring(start, end - start);
    }

    private void OnTextUpdating(LeXtudio.UI.Text.Core.CoreTextEditContext sender, LeXtudio.UI.Text.Core.CoreTextTextUpdatingEventArgs args)
    {
        if (_textBox.IsReadOnly)
        {
            return;
        }

        string text = _textBox.Text ?? string.Empty;
        int start = Math.Clamp(args.Range.StartCaretPosition, 0, text.Length);
        int end = Math.Clamp(args.Range.EndCaretPosition, start, text.Length);
        string replacement = args.Text ?? string.Empty;

        ApplyTextReplacement(start, end - start, replacement);

        int newLength = _textBox.Text?.Length ?? 0;
        int newStart = Math.Clamp(args.NewSelection.StartCaretPosition, 0, newLength);
        int newEnd = Math.Clamp(args.NewSelection.EndCaretPosition, 0, newLength);
        SelectRange(Math.Min(newStart, newEnd), Math.Abs(newEnd - newStart));

        if (_isComposing)
        {
            _compositionStart = start;
            _compositionLength = replacement.Length;
        }

        SyncPlatformState();
    }

    private void OnSelectionRequested(LeXtudio.UI.Text.Core.CoreTextEditContext sender, LeXtudio.UI.Text.Core.CoreTextSelectionRequestedEventArgs args)
    {
        args.Request.Selection = new LeXtudio.UI.Text.Core.CoreTextRange
        {
            StartCaretPosition = _textBox.SelectionStart,
            EndCaretPosition = _textBox.SelectionStart + _textBox.SelectionLength,
        };
    }

    private void OnSelectionUpdating(LeXtudio.UI.Text.Core.CoreTextEditContext sender, LeXtudio.UI.Text.Core.CoreTextSelectionUpdatingEventArgs args)
    {
        int length = _textBox.Text?.Length ?? 0;
        int start = Math.Clamp(args.Selection.StartCaretPosition, 0, length);
        int end = Math.Clamp(args.Selection.EndCaretPosition, 0, length);
        SelectRange(Math.Min(start, end), Math.Abs(end - start));
        SyncPlatformState();
    }

    private void OnLayoutRequested(LeXtudio.UI.Text.Core.CoreTextEditContext sender, LeXtudio.UI.Text.Core.CoreTextLayoutRequestedEventArgs args)
    {
        Rect caret = CalculateCaretRectInWindow();
        Rect control = CalculateElementRectInWindow(_textBox);

        args.Request.LayoutBounds.TextBounds = new LeXtudio.UI.Text.Core.CoreTextRect
        {
            X = caret.X,
            Y = caret.Y,
            Width = caret.Width,
            Height = caret.Height,
        };
        args.Request.LayoutBounds.ControlBounds = new LeXtudio.UI.Text.Core.CoreTextRect
        {
            X = control.X,
            Y = control.Y,
            Width = control.Width,
            Height = control.Height,
        };
    }

    private void OnCompositionStarted(LeXtudio.UI.Text.Core.CoreTextEditContext sender, LeXtudio.UI.Text.Core.CoreTextCompositionStartedEventArgs args)
    {
        _isComposing = true;
        _compositionStart = _textBox.SelectionStart;
        _compositionLength = _textBox.SelectionLength;
    }

    private void OnCompositionCompleted(LeXtudio.UI.Text.Core.CoreTextEditContext sender, LeXtudio.UI.Text.Core.CoreTextCompositionCompletedEventArgs args)
    {
        EndComposition();
    }

    private void OnFocusRemoved(LeXtudio.UI.Text.Core.CoreTextEditContext sender, object args)
    {
        _textBox.Focus(FocusState.Unfocused);
    }

    private void OnCommandReceived(object? sender, LeXtudio.UI.Text.Core.CoreTextCommandReceivedEventArgs args)
    {
        bool handled = args.Command switch
        {
            "deleteBackward:" => Backspace(),
            "deleteForward:" => Delete(),
            "insertNewline:" => InsertText(Environment.NewLine),
            "insertTab:" => InsertText("\t"),
            "selectAll:" => SelectAllText(),
            _ => false,
        };

        if (handled)
        {
            args.Handled = true;
            SyncPlatformState();
        }
    }

    private void ApplyTextReplacement(int start, int length, string replacement)
    {
        string text = _textBox.Text ?? string.Empty;
        start = Math.Clamp(start, 0, text.Length);
        length = Math.Clamp(length, 0, text.Length - start);

        _isApplyingImeText = true;
        try
        {
            _textBox.Text = text.Remove(start, length).Insert(start, replacement);
            SelectRange(start + replacement.Length, 0);
            Text = _textBox.Text;
        }
        finally
        {
            _isApplyingImeText = false;
        }
    }

    private bool InsertText(string text)
    {
        if (_textBox.IsReadOnly)
        {
            return false;
        }

        ApplyTextReplacement(_textBox.SelectionStart, _textBox.SelectionLength, text);
        return true;
    }

    private bool Backspace()
    {
        if (_textBox.IsReadOnly)
        {
            return false;
        }

        if (_textBox.SelectionLength > 0)
        {
            ApplyTextReplacement(_textBox.SelectionStart, _textBox.SelectionLength, string.Empty);
            return true;
        }

        if (_textBox.SelectionStart <= 0)
        {
            return false;
        }

        ApplyTextReplacement(_textBox.SelectionStart - 1, 1, string.Empty);
        return true;
    }

    private bool Delete()
    {
        if (_textBox.IsReadOnly)
        {
            return false;
        }

        string text = _textBox.Text ?? string.Empty;
        if (_textBox.SelectionLength > 0)
        {
            ApplyTextReplacement(_textBox.SelectionStart, _textBox.SelectionLength, string.Empty);
            return true;
        }

        if (_textBox.SelectionStart >= text.Length)
        {
            return false;
        }

        ApplyTextReplacement(_textBox.SelectionStart, 1, string.Empty);
        return true;
    }

    private bool SelectAllText()
    {
        SelectRange(0, _textBox.Text?.Length ?? 0);
        return true;
    }

    private void EndComposition()
    {
        _isComposing = false;
        _compositionStart = 0;
        _compositionLength = 0;
    }

    private void SelectRange(int start, int length)
    {
        string text = _textBox.Text ?? string.Empty;
        start = Math.Clamp(start, 0, text.Length);
        length = Math.Clamp(length, 0, text.Length - start);
        _textBox.SelectionStart = start;
        _textBox.SelectionLength = length;
    }

    private void SyncPlatformState()
    {
        if (_context is null)
        {
            return;
        }

        try
        {
            double scale = XamlRoot?.RasterizationScale ?? 1.0;
            Rect caret = CalculateCaretRectInWindow();
            _context.RasterizationScale = scale;
            _context.NotifyLayoutChanged();
            _context.NotifySelectionChanged(new LeXtudio.UI.Text.Core.CoreTextRange
            {
                StartCaretPosition = _textBox.SelectionStart,
                EndCaretPosition = _textBox.SelectionStart + _textBox.SelectionLength,
            });
            _context.NotifyCaretRectChanged(caret.X, caret.Y, caret.Width, caret.Height, scale);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"TextBox state sync failed: {ex}");
        }
    }

    private Rect CalculateCaretRectInWindow()
    {
        if (TryGetParsedTextCaretRect(out Rect reflectedRect))
        {
            return reflectedRect;
        }

        Rect control = CalculateElementRectInWindow(_textBox);
        return new Rect(control.X + 8, control.Y + 8, 2, Math.Max(16, control.Height - 16));
    }

    private bool TryGetParsedTextCaretRect(out Rect result)
    {
        result = default;

        try
        {
            object? textBoxView = GetInstanceProperty(_textBox, "TextBoxView");
            object? displayBlock = GetInstanceProperty(textBoxView, "DisplayBlock");
            object? parsedText = GetInstanceProperty(displayBlock, "ParsedText");
            MethodInfo? getRectForIndex = parsedText?.GetType().GetMethod("GetRectForIndex", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (displayBlock is not UIElement displayElement || getRectForIndex is null)
            {
                return false;
            }

            int caretIndex = Math.Clamp(_textBox.SelectionStart + _textBox.SelectionLength, 0, _textBox.Text?.Length ?? 0);
            object? rectObj = getRectForIndex.Invoke(parsedText, new object[] { caretIndex });
            if (rectObj is not Rect rect)
            {
                return false;
            }

            GeneralTransform transform = displayElement.TransformToVisual(null);
            Point topLeft = transform.TransformPoint(new Point(rect.X, rect.Y));
            Point bottomLeft = transform.TransformPoint(new Point(rect.X, rect.Y + rect.Height));
            result = new Rect(topLeft.X, topLeft.Y, 2, Math.Max(16, bottomLeft.Y - topLeft.Y));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static object? GetInstanceProperty(object? instance, string name)
    {
        return instance?.GetType()
            .GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(instance);
    }

    private static Rect CalculateElementRectInWindow(FrameworkElement element)
    {
        try
        {
            GeneralTransform transform = element.TransformToVisual(null);
            Point point = transform.TransformPoint(new Point(0, 0));
            return new Rect(point.X, point.Y, element.ActualWidth, element.ActualHeight);
        }
        catch
        {
            return Rect.Empty;
        }
    }
}
