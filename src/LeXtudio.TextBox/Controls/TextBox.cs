#if !WINDOWS_APP_SDK
using System;
using System.Diagnostics;
using System.IO;
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
    private static readonly FontFamily s_defaultFontFamily = new("Open Sans");
    private static readonly string[] s_textControlResourceKeys =
    [
        "TextControlBackground",
        "TextControlBackgroundPointerOver",
        "TextControlBackgroundFocused",
        "TextControlBackgroundDisabled",
        "TextControlBackgroundReadOnly",
        "TextControlBackgroundReadOnlyPointerOver",
        "TextControlBackgroundReadOnlyFocused",
        "TextControlBackgroundFocusedPointerOver",
        "TextControlForeground",
        "TextControlForegroundPointerOver",
        "TextControlForegroundFocused",
        "TextControlForegroundDisabled",
        "TextControlForegroundReadOnly",
        "TextControlForegroundReadOnlyPointerOver",
        "TextControlForegroundReadOnlyFocused",
        "TextControlPlaceholderForeground",
        "TextControlPlaceholderForegroundPointerOver",
        "TextControlPlaceholderForegroundFocused",
        "TextControlBorderBrush",
        "TextControlBorderBrushPointerOver",
        "TextControlBorderBrushFocused",
        "TextControlBorderBrushDisabled",
        "TextControlBorderBrushReadOnly",
        "TextControlBorderBrushReadOnlyPointerOver",
        "TextControlBorderBrushReadOnlyFocused"
    ];

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

    public static readonly DependencyProperty PlaceholderForegroundProperty =
        DependencyProperty.Register(
            nameof(PlaceholderForeground),
            typeof(Brush),
            typeof(TextBox),
            new PropertyMetadata(null, OnPlaceholderForegroundPropertyChanged));

    public static readonly DependencyProperty IsReadOnlyProperty =
        DependencyProperty.Register(
            nameof(IsReadOnly),
            typeof(bool),
            typeof(TextBox),
            new PropertyMetadata(false, OnIsReadOnlyPropertyChanged));

    public static readonly DependencyProperty InputScopeProperty =
        DependencyProperty.Register(
            nameof(InputScope),
            typeof(InputScope),
            typeof(TextBox),
            new PropertyMetadata(null, OnInputScopePropertyChanged));

    public static readonly DependencyProperty TextAlignmentProperty =
        DependencyProperty.Register(
            nameof(TextAlignment),
            typeof(TextAlignment),
            typeof(TextBox),
            new PropertyMetadata(TextAlignment.Left, OnTextAlignmentPropertyChanged));

    private readonly Microsoft.UI.Xaml.Controls.TextBox _textBox;

    /// <summary>The inner platform TextBox. Exposed so hosts can configure
    /// surface-level state (e.g. <see cref="FrameworkElement.ContextFlyout"/>)
    /// that the platform consults directly instead of bubbling up to this
    /// shim.</summary>
    public Microsoft.UI.Xaml.Controls.TextBox InnerTextBox => _textBox;
    private LeXtudio.UI.Text.Core.CoreTextEditContext? _context;
    private bool _isApplyingImeText;
    private bool _isComposing;
    private int _compositionStart;
    private int _compositionLength;
    private bool _suppressNextFormattingControlEdit;
    private bool _isRestoringFormattingControlEdit;
    private string _textBeforeFormattingAccelerator = string.Empty;
    private int _selectionStartBeforeFormattingAccelerator;
    private int _selectionLengthBeforeFormattingAccelerator;
    private static readonly string s_diagnosticLogPath = Path.Combine(Path.GetTempPath(), "LeXtudio.RichText.TextBox.log");

    public static bool DiagnosticsEnabled { get; set; }

    public event TextChangedEventHandler? TextChanged;

    /// <summary>Raised when the selection changes inside the inner TextBox.</summary>
    public event RoutedEventHandler? SelectionChanged;

    /// <summary>Raised for Ctrl+B/I/U before the inner platform text box can mutate the selection.</summary>
    public event EventHandler<TextFormattingAcceleratorRequestedEventArgs>? FormattingAcceleratorRequested;

    /// <summary>Raised for editor commands that should be handled by a document owner.</summary>
    public event EventHandler<TextEditingCommandRequestedEventArgs>? EditingCommandRequested;

    /// <summary>Selection start (caret index). Mirrors Microsoft.UI.Xaml.Controls.TextBox.SelectionStart.</summary>
    public int SelectionStart
    {
        get => _textBox.SelectionStart;
        set => _textBox.SelectionStart = value;
    }

    /// <summary>Selection length. Mirrors Microsoft.UI.Xaml.Controls.TextBox.SelectionLength.</summary>
    public int SelectionLength
    {
        get => _textBox.SelectionLength;
        set => _textBox.SelectionLength = value;
    }

    /// <summary>Returns or replaces the currently selected text.</summary>
    public string SelectedText
    {
        get => _textBox.SelectedText ?? string.Empty;
        set => _textBox.SelectedText = value ?? string.Empty;
    }

    /// <summary>Select a contiguous range. Same shape as Microsoft.UI.Xaml.Controls.TextBox.Select.</summary>
    public void Select(int start, int length) => SelectRange(start, length);

    /// <summary>
    /// Replaces the current selection (or inserts at the caret when no selection is active)
    /// with <paramref name="text"/>. Routes through the same path that IME composition uses
    /// so the platform stays in sync.
    /// </summary>
    public void ReplaceSelection(string text)
    {
        if (text is null) text = string.Empty;
        int start = _textBox.SelectionStart;
        int length = _textBox.SelectionLength;
        ApplyTextReplacement(start, length, text);
        int newCaret = start + text.Length;
        SelectRange(newCaret, 0);
    }

    /// <summary>
    /// Wraps the current selection with <paramref name="prefix"/> and <paramref name="suffix"/>.
    /// When no selection exists, inserts <c>prefix + suffix</c> at the caret and places the
    /// caret between them — convenient for "**bold**" / "*italic*" / "__underline__" markdown-
    /// style toolbar actions.
    /// </summary>
    public void WrapSelection(string prefix, string suffix)
    {
        prefix ??= string.Empty;
        suffix ??= string.Empty;
        int start = _textBox.SelectionStart;
        int length = _textBox.SelectionLength;
        string current = length > 0 ? (_textBox.Text ?? string.Empty).Substring(start, length) : string.Empty;
        string replacement = prefix + current + suffix;
        ApplyTextReplacement(start, length, replacement);
        // Place the caret either after the wrapped selection or between the markers.
        int caret = length > 0
            ? start + replacement.Length
            : start + prefix.Length;
        SelectRange(caret, 0);
    }

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

    public Brush? PlaceholderForeground
    {
        get => (Brush?)GetValue(PlaceholderForegroundProperty);
        set => SetValue(PlaceholderForegroundProperty, value);
    }

    public bool IsReadOnly
    {
        get => (bool)GetValue(IsReadOnlyProperty);
        set => SetValue(IsReadOnlyProperty, value);
    }

    public InputScope? InputScope
    {
        get => (InputScope?)GetValue(InputScopeProperty);
        set => SetValue(InputScopeProperty, value);
    }

    public TextAlignment TextAlignment
    {
        get => (TextAlignment)GetValue(TextAlignmentProperty);
        set => SetValue(TextAlignmentProperty, value);
    }

    public TextBox()
    {
        FontFamily = s_defaultFontFamily;

        _textBox = new Microsoft.UI.Xaml.Controls.TextBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            FontFamily = FontFamily,
            PlaceholderText = PlaceholderText,
            Header = Header,
            AcceptsReturn = AcceptsReturn,
            TextWrapping = TextWrapping,
            IsReadOnly = IsReadOnly,
            InputScope = InputScope,
            TextAlignment = TextAlignment,
            Padding = Padding,
        };

        _textBox.TextChanged += OnTextBoxTextChanged;
        _textBox.GotFocus += OnTextBoxGotFocus;
        _textBox.LostFocus += OnTextBoxLostFocus;
        _textBox.SelectionChanged += OnTextBoxSelectionChanged;
        _textBox.KeyDown += OnTextBoxKeyDown;
        _textBox.Loaded += (_, _) => RefreshThemeResources();
        _textBox.PointerEntered += (_, _) => PatchTemplateBackgroundSoon();
        _textBox.PointerExited += (_, _) => PatchTemplateBackgroundSoon();
        _textBox.GotFocus += (_, _) => PatchTemplateBackgroundSoon();
        _textBox.LostFocus += (_, _) => PatchTemplateBackgroundSoon();
        AddFormattingKeyboardAccelerator(VirtualKey.B);
        AddFormattingKeyboardAccelerator(VirtualKey.I);
        AddFormattingKeyboardAccelerator(VirtualKey.U);
        AddEditingKeyboardAccelerator(VirtualKey.Z, TextEditingCommand.Undo);
        AddEditingKeyboardAccelerator(VirtualKey.Y, TextEditingCommand.Redo);
        _textBox.SizeChanged += (_, _) => SyncPlatformState();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        RegisterPropertyChangedCallback(BackgroundProperty, (d, _) =>
            _textBox.Background = ((TextBox)d).Background);
        RegisterPropertyChangedCallback(ForegroundProperty, (d, _) =>
            _textBox.Foreground = ((TextBox)d).Foreground);
        RegisterPropertyChangedCallback(PaddingProperty, (d, _) =>
            _textBox.Padding = ((TextBox)d).Padding);
        RegisterPropertyChangedCallback(FontFamilyProperty, (d, _) =>
            _textBox.FontFamily = ((TextBox)d).FontFamily);
        // Forward the box-frame properties so a host that explicitly sets them on the
        // wrapper (e.g. CornerRadius="0" for a flat, square look) actually reaches the
        // inner platform TextBox. Not seeded in the constructor so consumers that leave
        // them unset keep the inner control's default border/corner styling.
        RegisterPropertyChangedCallback(CornerRadiusProperty, (d, _) =>
            ((TextBox)d)._textBox.CornerRadius = ((TextBox)d).CornerRadius);
        RegisterPropertyChangedCallback(BorderThicknessProperty, (d, _) =>
            ((TextBox)d)._textBox.BorderThickness = ((TextBox)d).BorderThickness);
        RegisterPropertyChangedCallback(BorderBrushProperty, (d, _) =>
            ((TextBox)d)._textBox.BorderBrush = ((TextBox)d).BorderBrush);
        // Forward sizing so a host can make the box compact. The inner platform TextBox
        // otherwise imposes its own default MinHeight (~32) and would ignore a smaller
        // wrapper height.
        RegisterPropertyChangedCallback(MinHeightProperty, (d, _) =>
            ((TextBox)d)._textBox.MinHeight = ((TextBox)d).MinHeight);
        RegisterPropertyChangedCallback(HeightProperty, (d, _) =>
            ((TextBox)d)._textBox.Height = ((TextBox)d).Height);

        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Content = new Grid { Children = { _textBox } };
    }

    public new void Dispose()
    {
        DisposeContext();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Copies host-provided text control theme resources from the wrapper to the inner platform text box.
    /// </summary>
    public void RefreshThemeResources()
    {
        _textBox.Background = Background;
        _textBox.Foreground = Foreground;
        _textBox.BorderBrush = BorderBrush;
        _textBox.PlaceholderForeground = PlaceholderForeground;

        foreach (var key in s_textControlResourceKeys)
        {
            if (Resources.TryGetValue(key, out var value))
                _textBox.Resources[key] = value;
        }

        PatchTemplateBackgroundSoon();
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

    private static void OnIsReadOnlyPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (TextBox)d;
        control._textBox.IsReadOnly = (bool)e.NewValue;
    }

    private static void OnInputScopePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (TextBox)d;
        control._textBox.InputScope = (InputScope?)e.NewValue;
    }

    private static void OnTextAlignmentPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (TextBox)d;
        control._textBox.TextAlignment = (TextAlignment)e.NewValue;
    }

    private static void OnPlaceholderForegroundPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (TextBox)d;
        control._textBox.PlaceholderForeground = e.NewValue as Brush;
    }

    private void PatchTemplateBackgroundSoon()
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            PatchTemplateBackground(_textBox, Background);
        });
    }

    private static void PatchTemplateBackground(DependencyObject root, Brush brush)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Border border && (border.Name == "BackgroundElement" || border.Name == "BorderElement"))
                border.Background = brush;
            PatchTemplateBackground(child, brush);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SeedInnerBoxFrame();
        RefreshThemeResources();
        EnsureContext();
        SyncPlatformState();
    }

    // Forward the box-frame properties the host explicitly set on the wrapper. The change
    // callbacks above only fire when the value differs from the property default, so a host
    // value that equals the default (e.g. CornerRadius="0", BorderThickness="0") would never
    // reach the inner platform TextBox. Checking ReadLocalValue lets an explicit-but-default
    // value through while leaving the inner control's own defaults intact for hosts that set
    // nothing.
    private void SeedInnerBoxFrame()
    {
        if (ReadLocalValue(CornerRadiusProperty) != DependencyProperty.UnsetValue)
            _textBox.CornerRadius = CornerRadius;
        if (ReadLocalValue(BorderThicknessProperty) != DependencyProperty.UnsetValue)
            _textBox.BorderThickness = BorderThickness;
        if (ReadLocalValue(BorderBrushProperty) != DependencyProperty.UnsetValue)
            _textBox.BorderBrush = BorderBrush;
        if (ReadLocalValue(MinHeightProperty) != DependencyProperty.UnsetValue)
            _textBox.MinHeight = MinHeight;
        if (ReadLocalValue(HeightProperty) != DependencyProperty.UnsetValue)
            _textBox.Height = Height;
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
        LogDiagnostic($"TextChanged text={DescribeText(_textBox.Text)} selection={_textBox.SelectionStart}+{_textBox.SelectionLength} applyingIme={_isApplyingImeText} composing={_isComposing}");

        if (TrySuppressFormattingControlEdit())
        {
            return;
        }

        if (_isRestoringFormattingControlEdit)
        {
            if (Text != _textBox.Text)
            {
                SetValue(TextProperty, _textBox.Text);
            }

            SyncPlatformState();
            return;
        }

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
        LogDiagnostic($"SelectionChanged selection={_textBox.SelectionStart}+{_textBox.SelectionLength}");
        SyncPlatformState();
        if (_suppressNextFormattingControlEdit || _isRestoringFormattingControlEdit)
        {
            LogDiagnostic($"SelectionChanged suppressedForFormattingControl pending={_suppressNextFormattingControlEdit} restoring={_isRestoringFormattingControlEdit}");
            return;
        }

        SelectionChanged?.Invoke(this, e);
    }

    private void OnTextBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = IsKeyDown(VirtualKey.Control) || IsKeyDown(VirtualKey.LeftControl) || IsKeyDown(VirtualKey.RightControl);
        bool shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        LogDiagnostic($"KeyDown key={e.Key} ctrl={ctrl} shift={shift} handledIn={e.Handled} selection={_textBox.SelectionStart}+{_textBox.SelectionLength}");

        if (ctrl && TryHandleFormattingAccelerator(e.Key))
        {
            e.Handled = true;
            LogDiagnostic($"KeyDown handled formatting key={e.Key}");
            return;
        }

        if (_context is null)
        {
            return;
        }

        try
        {
            if (_context.ProcessKeyEvent((int)e.Key, shift, ctrl))
            {
                e.Handled = true;
                LogDiagnostic($"KeyDown handled by CoreText key={e.Key}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"TextBox ProcessKeyEvent failed: {ex}");
        }
    }

    private bool TryHandleFormattingAccelerator(VirtualKey key)
    {
        var accelerator = key switch
        {
            VirtualKey.B => TextFormattingAccelerator.Bold,
            VirtualKey.I => TextFormattingAccelerator.Italic,
            VirtualKey.U => TextFormattingAccelerator.Underline,
            _ => TextFormattingAccelerator.None,
        };

        if (accelerator == TextFormattingAccelerator.None)
        {
            return false;
        }

        CaptureFormattingAcceleratorState();
        var args = new TextFormattingAcceleratorRequestedEventArgs(
            accelerator,
            _selectionStartBeforeFormattingAccelerator,
            _selectionLengthBeforeFormattingAccelerator);
        LogDiagnostic($"FormattingAccelerator request={accelerator} subscribers={FormattingAcceleratorRequested is not null}");
        FormattingAcceleratorRequested?.Invoke(this, args);
        LogDiagnostic($"FormattingAccelerator handled={args.Handled}");
        if (!args.Handled)
        {
            _suppressNextFormattingControlEdit = false;
        }

        return args.Handled;
    }

    private void CaptureFormattingAcceleratorState()
    {
        _textBeforeFormattingAccelerator = _textBox.Text ?? string.Empty;
        _selectionStartBeforeFormattingAccelerator = _textBox.SelectionStart;
        _selectionLengthBeforeFormattingAccelerator = _textBox.SelectionLength;
        _suppressNextFormattingControlEdit = true;
    }

    private bool TrySuppressFormattingControlEdit()
    {
        if (!_suppressNextFormattingControlEdit)
        {
            return false;
        }

        string current = _textBox.Text ?? string.Empty;
        bool isFormattingControlEdit = IsFormattingControlEdit(
            current,
            _textBeforeFormattingAccelerator,
            _selectionStartBeforeFormattingAccelerator,
            _selectionLengthBeforeFormattingAccelerator);
        if (!isFormattingControlEdit)
        {
            _suppressNextFormattingControlEdit = false;
            return false;
        }

        LogDiagnostic($"SuppressFormattingControlEdit current={DescribeText(current)} restore={DescribeText(_textBeforeFormattingAccelerator)} selection={_selectionStartBeforeFormattingAccelerator}+{_selectionLengthBeforeFormattingAccelerator}");
        _suppressNextFormattingControlEdit = false;
        _isApplyingImeText = true;
        _isRestoringFormattingControlEdit = true;
        try
        {
            _textBox.Text = _textBeforeFormattingAccelerator;
            SelectRange(_selectionStartBeforeFormattingAccelerator, _selectionLengthBeforeFormattingAccelerator);
            Text = _textBeforeFormattingAccelerator;
        }
        finally
        {
            _isRestoringFormattingControlEdit = false;
            _isApplyingImeText = false;
        }

        SyncPlatformState();
        return true;
    }

    private static bool IsFormattingControlEdit(string current, string previous, int selectionStart, int selectionLength)
    {
        return IsSingleFormattingControlCharacterInsertion(current, previous)
            || IsSingleFormattingControlCharacterReplacement(current, previous, selectionStart, selectionLength);
    }

    private static bool IsSingleFormattingControlCharacterInsertion(string current, string previous)
    {
        if (current.Length != previous.Length + 1)
        {
            return false;
        }

        int currentIndex = 0;
        int previousIndex = 0;
        bool foundInsertedControlCharacter = false;

        while (currentIndex < current.Length)
        {
            if (previousIndex < previous.Length && current[currentIndex] == previous[previousIndex])
            {
                currentIndex++;
                previousIndex++;
                continue;
            }

            if (foundInsertedControlCharacter || Array.IndexOf(s_formattingControlCharacters, current[currentIndex]) < 0)
            {
                return false;
            }

            foundInsertedControlCharacter = true;
            currentIndex++;
        }

        return foundInsertedControlCharacter && previousIndex == previous.Length;
    }

    private static bool IsSingleFormattingControlCharacterReplacement(string current, string previous, int selectionStart, int selectionLength)
    {
        selectionStart = Math.Clamp(selectionStart, 0, previous.Length);
        selectionLength = Math.Clamp(selectionLength, 0, previous.Length - selectionStart);
        if (selectionLength <= 0 || current.Length != previous.Length - selectionLength + 1)
        {
            return false;
        }

        if (!current.AsSpan(0, selectionStart).SequenceEqual(previous.AsSpan(0, selectionStart)))
        {
            return false;
        }

        if (Array.IndexOf(s_formattingControlCharacters, current[selectionStart]) < 0)
        {
            return false;
        }

        var previousSuffixStart = selectionStart + selectionLength;
        var currentSuffixStart = selectionStart + 1;
        return current.AsSpan(currentSuffixStart).SequenceEqual(previous.AsSpan(previousSuffixStart));
    }

    private static readonly char[] s_formattingControlCharacters =
    {
        '\u0002', // Ctrl+B
        '\u0009', // Ctrl+I can surface as a tab character on some platforms.
        '\u0015', // Ctrl+U
    };

    private void AddFormattingKeyboardAccelerator(VirtualKey key)
    {
        AddFormattingKeyboardAccelerator(key, addToInnerTextBox: true);
        AddFormattingKeyboardAccelerator(key, addToInnerTextBox: false);
    }

    private void AddEditingKeyboardAccelerator(VirtualKey key, TextEditingCommand command)
    {
        AddEditingKeyboardAccelerator(key, command, addToInnerTextBox: true);
        AddEditingKeyboardAccelerator(key, command, addToInnerTextBox: false);
    }

    private void AddFormattingKeyboardAccelerator(VirtualKey key, bool addToInnerTextBox)
    {
        var accelerator = new KeyboardAccelerator
        {
            Key = key,
            Modifiers = VirtualKeyModifiers.Control,
        };
        accelerator.Invoked += OnFormattingKeyboardAcceleratorInvoked;

        if (addToInnerTextBox)
            _textBox.KeyboardAccelerators.Add(accelerator);
        else
            KeyboardAccelerators.Add(accelerator);
    }

    private void AddEditingKeyboardAccelerator(VirtualKey key, TextEditingCommand command, bool addToInnerTextBox)
    {
        var accelerator = new KeyboardAccelerator
        {
            Key = key,
            Modifiers = VirtualKeyModifiers.Control,
        };
        accelerator.Invoked += (_, args) => OnEditingKeyboardAcceleratorInvoked(command, args);

        if (addToInnerTextBox)
            _textBox.KeyboardAccelerators.Add(accelerator);
        else
            KeyboardAccelerators.Add(accelerator);
    }

    private void OnFormattingKeyboardAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        LogDiagnostic($"KeyboardAccelerator invoked key={sender.Key} handledIn={args.Handled} selection={_textBox.SelectionStart}+{_textBox.SelectionLength}");
        if (TryHandleFormattingAccelerator(sender.Key))
        {
            args.Handled = true;
            LogDiagnostic($"KeyboardAccelerator handled key={sender.Key}");
        }
    }

    private void OnEditingKeyboardAcceleratorInvoked(TextEditingCommand command, KeyboardAcceleratorInvokedEventArgs args)
    {
        LogDiagnostic($"EditingKeyboardAccelerator invoked command={command} handledIn={args.Handled} selection={_textBox.SelectionStart}+{_textBox.SelectionLength}");
        if (TryHandleEditingCommand(command))
        {
            args.Handled = true;
            LogDiagnostic($"EditingKeyboardAccelerator handled command={command}");
        }
    }

    private bool TryHandleEditingCommand(TextEditingCommand command)
    {
        var args = new TextEditingCommandRequestedEventArgs(command);
        LogDiagnostic($"EditingCommand request={command} subscribers={EditingCommandRequested is not null}");
        EditingCommandRequested?.Invoke(this, args);
        LogDiagnostic($"EditingCommand handled={args.Handled}");
        return args.Handled;
    }

    private static bool IsKeyDown(VirtualKey key)
        => InputKeyboardSource.GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private static void LogDiagnostic(string message)
    {
        if (!DiagnosticsEnabled)
        {
            return;
        }

        try
        {
            File.AppendAllText(s_diagnosticLogPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never affect text input behavior.
        }
    }

    private static string DescribeText(string? text)
    {
        if (text is null)
            return "<null>";

        return "\"" + text
            .Replace("\\", "\\\\")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n")
            .Replace("\t", "\\t")
            .Replace("\u0002", "\\u0002")
            .Replace("\"", "\\\"") + "\"";
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
        LogDiagnostic($"TextUpdating range={args.Range.StartCaretPosition}..{args.Range.EndCaretPosition} text={DescribeText(args.Text)} newSelection={args.NewSelection.StartCaretPosition}..{args.NewSelection.EndCaretPosition} readOnly={_textBox.IsReadOnly}");

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
        LogDiagnostic($"CommandReceived command={args.Command} selection={_textBox.SelectionStart}+{_textBox.SelectionLength}");

        bool handled = args.Command switch
        {
            "deleteBackward:" => Backspace(),
            "deleteForward:" => Delete(),
            "insertNewline:" => InsertText(Environment.NewLine),
            "insertTab:" => InsertText("\t"),
            "selectAll:" => SelectAllText(),
            "toggleBoldface:" => TryHandleFormattingAccelerator(VirtualKey.B),
            "toggleItalics:" => TryHandleFormattingAccelerator(VirtualKey.I),
            "toggleUnderline:" => TryHandleFormattingAccelerator(VirtualKey.U),
            "undo:" => TryHandleEditingCommand(TextEditingCommand.Undo),
            "redo:" => TryHandleEditingCommand(TextEditingCommand.Redo),
            _ => false,
        };

        if (handled)
        {
            args.Handled = true;
            LogDiagnostic($"CommandReceived handled command={args.Command}");
            SyncPlatformState();
        }
    }

    private void ApplyTextReplacement(int start, int length, string replacement)
    {
        string text = _textBox.Text ?? string.Empty;
        start = Math.Clamp(start, 0, text.Length);
        length = Math.Clamp(length, 0, text.Length - start);
        LogDiagnostic($"ApplyTextReplacement start={start} length={length} replacement={DescribeText(replacement)} oldText={DescribeText(text)}");

        _isApplyingImeText = true;
        try
        {
            _textBox.Text = text.Remove(start, length).Insert(start, replacement);
            SelectRange(start + replacement.Length, 0);
            Text = _textBox.Text;
            LogDiagnostic($"ApplyTextReplacement result={DescribeText(_textBox.Text)} selection={_textBox.SelectionStart}+{_textBox.SelectionLength}");
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

    public void SelectAll() => SelectRange(0, _textBox.Text?.Length ?? 0);

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

public enum TextFormattingAccelerator
{
    None,
    Bold,
    Italic,
    Underline,
}

/// <summary>Document-owner editing commands raised by <see cref="TextBox"/>.</summary>
public enum TextEditingCommand
{
    /// <summary>Undo the previous document edit.</summary>
    Undo,
    /// <summary>Redo the previous undone document edit.</summary>
    Redo,
}

public sealed class TextFormattingAcceleratorRequestedEventArgs(TextFormattingAccelerator accelerator, int selectionStart, int selectionLength) : EventArgs
{
    public TextFormattingAccelerator Accelerator { get; } = accelerator;
    public int SelectionStart { get; } = selectionStart;
    public int SelectionLength { get; } = selectionLength;
    public bool Handled { get; set; }
}

/// <summary>Provides data for document-owner editing command requests.</summary>
public sealed class TextEditingCommandRequestedEventArgs(TextEditingCommand command) : EventArgs
{
    /// <summary>The requested editing command.</summary>
    public TextEditingCommand Command { get; } = command;
    /// <summary>Gets or sets whether the command was handled by the document owner.</summary>
    public bool Handled { get; set; }
}
#endif
