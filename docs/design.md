# TextCore.Uno IME Design Review

## Problem Statement

`TextCore.Uno` exists to give third-party Uno input controls a small Core Text-style surface for IME integration without each control reimplementing platform-specific IME logic. The immediate repro was a side-by-side macOS sample with:

- Uno's built-in `TextBox`.
- `UnoEdit` using `TextCore.Uno`.

The desired behavior is:

- Uno's built-in controls continue to use Uno's own IME path.
- Third-party controls such as `UnoEdit` and future `UnoRichText` can opt into IME support.
- Platform-specific native glue is owned by Uno as much as possible.
- `TextCore.Uno` stays a thin control-facing layer, not a competing platform runtime.

## Findings

### macOS

Uno's macOS Skia runtime uses a window-level native callback table:

- Native `NSTextInputClient` implementation in `libUnoNativeMac.dylib`.
- Managed callback registration via `uno_set_ime_callbacks`.
- Managed dispatch through `MacOSWindowHost`.
- Built-in `TextBox` composition handling through `MacOSImeTextBoxExtension`.

The original `TextCore.Uno` macOS adapter replaced Uno's global callback table. That allowed `UnoEdit` to receive IME events, but it also broke Uno's default `TextBox` when both were loaded in one process.

The current macOS fix is:

- Preserve Uno's existing IME callbacks before installing the TextCore callback chain.
- Let TextCore consume IME callbacks only while a TextCore-backed control owns input.
- Forward all other callbacks to Uno's original handlers.
- Do not call `uno_set_ime_active(false)` from TextCore focus-leave/dispose paths. Uno's default `TextBox` also depends on the same native active flag, so TextCore must not disable the native route globally.

This removes the old native `libUnoEditMacInput.dylib` dependency and reuses Uno's native AppKit glue.

### Win32

Uno's Win32 Skia runtime already handles IME messages in `Win32WindowWrapper`:

- `WM_IME_STARTCOMPOSITION`
- `WM_IME_COMPOSITION`
- `WM_IME_ENDCOMPOSITION`

Those messages are dispatched to internal `Win32ImeTextBoxExtension.Instance`, which is TextBox-shaped and not exposed as a reusable input-client service.

`TextCore.Uno` currently subclasses the HWND to observe the same `WM_IME_*` messages. This duplicates part of Uno's Win32 IME work and can interfere with Uno's `TextBox` unless guarded carefully.

The current compatibility fix is:

- TextCore's Win32 adapter checks `CoreTextEditContext.IsInputActiveNow()` before consuming IME messages.
- If the TextCore control does not own input, messages are forwarded to the original WndProc so Uno's own runtime can handle them.
- Candidate/preedit positioning is also gated by ownership.

This is safe, but not ideal. The better design is for Uno to expose the window-level IME dispatch to arbitrary active input clients.

### Linux/X11

Uno's X11 Skia runtime has substantial IME support:

- D-Bus IBus backend.
- D-Bus Fcitx backend.
- XIM fallback.
- TextBox-specific bridge through `X11ImeTextBoxExtension`.

`TextCore.Uno` currently has its own IBus D-Bus path. That avoids native dependencies, but duplicates Uno's Linux runtime work and does not reuse Uno's Fcitx/XIM fallback logic.

The current compatibility fix is:

- TextCore's Linux adapter only sends focus, caret, and key events while the TextCore-backed control owns input.
- UnoEdit explicitly forwards key events to TextCore on Linux; TextCore does not globally intercept Uno's TextBox path.

This is less risky than Win32 because TextCore is not stealing global callbacks, but it still duplicates too much runtime logic.

## Design Assessment

The macOS result shows the right direction: Uno should own platform IME glue, and TextCore should provide a control-facing abstraction that can be plugged into that glue.

However, Win32 and Linux do not currently expose an equivalent reusable hook:

- Win32 routes `WM_IME_*` internally to Uno's TextBox extension.
- Linux routes IBus/Fcitx/XIM internally to Uno's TextBox extension.
- The existing Uno `IImeTextBoxExtension` is internal and TextBox-specific.

Reflection-based reuse of those internal types would be brittle and should be avoided. The correct long-term fix is an upstreamable Uno text-input-client abstraction that is not tied to `TextBox`.

## Proposed Core Text API

The proposed API has two layers:

- A public or semi-public Uno runtime-facing input client contract.
- A TextCore control-facing `CoreTextEditContext` that implements/adapts to that contract.

### Runtime Input Client

Uno should expose a platform-neutral text input client interface for Skia runtimes:

```csharp
public interface ISkiaTextInputClient
{
    bool IsTextInputActive { get; }

    CoreTextInputScope InputScope { get; }

    CoreTextRange Selection { get; }

    string GetText(CoreTextRange range);

    CoreTextLayoutBounds GetLayoutBounds();

    void BeginComposition();

    void UpdateComposition(CoreTextCompositionUpdate update);

    void CompleteComposition(string text);

    void EndComposition();

    bool TryHandleTextCommand(CoreTextCommand command);
}
```

Supporting data contracts:

```csharp
public sealed class CoreTextCompositionUpdate
{
    public string Text { get; init; } = string.Empty;
    public int CursorPosition { get; init; } = -1;
    public int ResolvedLength { get; init; }
    public CoreTextRange ReplacementRange { get; init; } = CoreTextRange.Empty;
    public CoreTextRange NewSelection { get; init; } = CoreTextRange.Empty;
}

public sealed class CoreTextLayoutBounds
{
    public CoreTextRect TextBounds { get; init; }
    public CoreTextRect ControlBounds { get; init; }
}

public readonly record struct CoreTextRange(int StartCaretPosition, int EndCaretPosition)
{
    public static CoreTextRange Empty { get; } = new(0, 0);
}

public readonly record struct CoreTextRect(double X, double Y, double Width, double Height);

public readonly record struct CoreTextCommand(string Name);
```

### Runtime Session Manager

Uno should also expose a small session manager:

```csharp
public interface ISkiaTextInputManager
{
    void Activate(ISkiaTextInputClient client);
    void Deactivate(ISkiaTextInputClient client);
    void NotifySelectionChanged(ISkiaTextInputClient client);
    void NotifyLayoutChanged(ISkiaTextInputClient client);
}
```

The platform runtime would keep exactly one active input client per native window/root. Platform-specific behavior would be owned by Uno:

- macOS: `NSTextInputClient` callbacks dispatch to the active client.
- Win32: `WM_IME_*` dispatches to the active client.
- Linux/X11: IBus/Fcitx/XIM dispatches to the active client.

Uno's built-in `TextBox` would become one client implementation. TextCore-backed controls would become another.

### TextCore API Shape

`TextCore.Uno` should keep exposing a Core Text-style event API for third-party controls:

```csharp
public sealed class CoreTextEditContext : IDisposable
{
    public CoreTextInputScope InputScope { get; set; }

    public Func<bool>? IsInputActive { get; set; }

    public event EventHandler<CoreTextEditContext, CoreTextTextRequestedEventArgs>? TextRequested;
    public event EventHandler<CoreTextEditContext, CoreTextTextUpdatingEventArgs>? TextUpdating;
    public event EventHandler<CoreTextEditContext, CoreTextSelectionRequestedEventArgs>? SelectionRequested;
    public event EventHandler<CoreTextEditContext, CoreTextSelectionUpdatingEventArgs>? SelectionUpdating;
    public event EventHandler<CoreTextEditContext, CoreTextLayoutRequestedEventArgs>? LayoutRequested;
    public event EventHandler<CoreTextEditContext, CoreTextCompositionStartedEventArgs>? CompositionStarted;
    public event EventHandler<CoreTextEditContext, CoreTextCompositionCompletedEventArgs>? CompositionCompleted;
    public event EventHandler<CoreTextCommandReceivedEventArgs>? CommandReceived;

    public bool AttachToCurrentWindow(Window? window);
    public void NotifyFocusEnter();
    public void NotifyFocusLeave();
    public void NotifySelectionChanged(CoreTextRange range);
    public void NotifyLayoutChanged();
    public void NotifyCaretRectChanged(double x, double y, double width, double height, double scale);
}
```

Internally, when Uno exposes `ISkiaTextInputManager`, `CoreTextEditContext` can register an adapter implementation of `ISkiaTextInputClient`. Existing consumers keep the Core Text-style event model, while platform runtimes stop being duplicated in TextCore.

## Migration Plan

1. Keep the current guarded adapters for compatibility.
2. Remove the old macOS native `libUnoEditMacInput.dylib` path permanently.
3. Propose an upstream Uno API for active text input clients.
4. Once available, replace TextCore's Win32 HWND subclass with the Uno input manager.
5. Replace TextCore's Linux IBus implementation with Uno's X11 input manager, including Fcitx and XIM fallback.
6. Keep `CoreTextEditContext` as the stable third-party control API.

## Open Questions

- Should the reusable Uno contract live in `Uno.UI.Xaml.Controls.Extensions`, next to `IImeTextBoxExtension`, or in a new Skia runtime namespace?
- Should the active client be registered per `XamlRoot`, per native window, or both?
- Should `CoreTextRange` use UTF-16 code-unit offsets to match .NET strings and current Uno TextBox behavior? The current answer should be yes.
- Should command routing be platform-neutral enums where possible, with raw platform command names as an escape hatch?
- Should composition updates carry attributes/clauses for richer IME styling, or should that remain out of scope for the first API?

## Current Recommendation

Treat the current platform adapters as a compatibility bridge, not the final architecture.

The final architecture should be:

- Uno owns native IME integration on every Skia platform.
- Uno exposes a small active text input client contract.
- `TextCore.Uno` implements that contract and keeps a Core Text-style event API for third-party controls.
- Third-party controls integrate with `CoreTextEditContext`, not with platform-specific IME code.
