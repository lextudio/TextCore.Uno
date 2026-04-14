# TextCore.Uno — Core Text APIs for Uno Platform

TextCore.Uno aims to offer a small, stable set of CoreText/IME primitives editor
hosts can use to implement platform text input and IME support on Skia-based
desktop platforms. It exposes a managed `CoreText*` API surface and uses
different approaches for platform integration:

- On Windows, it uses the Win32 IME APIs (user32/imm32) via P/Invoke.
- On macOS, it includes a tiny native macOS helper (`libUnoEditMacInput.dylib`) used to surface AppKit text input callbacks to managed code.
- On Linux, it uses `libX11` via P/Invoke for X11 calls and communicates with
  IBus over D-Bus using a built-in managed DBus-over-socket implementation
  (no libibus/libdbus P/Invoke).

## Quick start

- Install the NuGet package:

```bash
dotnet add package LeXtudio.UI.Text.Core
```

- Or add a `PackageReference` to your project file:

```xml
<PackageReference Include="LeXtudio.UI.Text.Core" Version="1.*" />
```

For advanced workflows (consuming from source, building the native macOS helper, or producing packages) see `CONTRIBUTING.md`.

## API overview

- `CoreTextServicesManager.GetForCurrentView()` — factory; call `CreateEditContext()` to get a `CoreTextEditContext`.
- `CoreTextEditContext` — central event hub. Important events:
  - `TextRequested`
  - `TextUpdating`
  - `SelectionRequested`
  - `SelectionUpdating`
  - `LayoutRequested`
  - `CompositionStarted` / `CompositionCompleted` / `FocusRemoved`
  - `CommandReceived` — receives platform command selectors (e.g. AppKit `doCommandBySelector:` strings).

Lifecycle methods:

- `bool Attach(nint windowHandle)` — attach to a native window.
- `void NotifyCaretRectChanged(double x, double y, double width, double height)`.
- `void NotifyFocusEnter()` / `void NotifyFocusLeave()`.
- `void Dispose()`.

### Minimal usage example

```csharp
var manager = CoreTextServicesManager.GetForCurrentView();
var ctx = manager.CreateEditContext();

ctx.TextRequested += (s, e) => e.Request.Text = GetDocumentText();
ctx.TextUpdating += (s, e) => ApplyEdit(e.NewText);
ctx.CommandReceived += (s, e) => {
  if (e.Command == "deleteBackward:") { Backspace(); e.Handled = true; }
};

ctx.Attach(hwnd);
ctx.NotifyCaretRectChanged(x, y, w, h);
// ...
ctx.Dispose();
```

## License

This project is licensed under the MIT License. See the [LICENSE](LICENSE) file for details.

## Copyright

(c) 2026 LeXtudio Inc. All rights reserved.
