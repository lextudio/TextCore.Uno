# LeXtudio.UI.Text.Core sample

This small Uno single-project demonstrates using the LeXtudio.UI.Text.Core
APIs to attach a platform text input adapter and handle IME composition events.
It includes macOS native adapter integration, IME debug logging, and improved
composition handling (caret positioning and delete/backspace commands).

## Build & run (desktop host)

```bash
dotnet build -c Debug -f net10.0-desktop
env UNOEDIT_DEBUG_IME=1 dotnet run -c Debug -f net10.0-desktop
```

## Notes

- On macOS the sample attempts to load `libUnoEditMacInput.dylib` (native
  bridge). Build the native bridge under
  `external/coretext/src/Native/MacOS` and copy the resulting library into the
  runtime output so the sample can load it. The sample project contains
  MSBuild hooks that will copy an existing dylib into the app output when
  available.
- Enable detailed native IME logs by setting `UNOEDIT_DEBUG_IME=1`. Logs are
  written to `$TMPDIR/unoedit_ime.log`. The sample also emits console
  diagnostics to correlate managed and native coordinates.

## What it demonstrates

- Create a `CoreTextEditContext` via `CoreTextServicesManager` and attach it to
  the current native window. See
  [external/coretext/sample/Controls/ImeTextBox.cs](external/coretext/sample/Controls/ImeTextBox.cs)
  for the recommended no-Uno-source-patch wrapper approach.
- Subscribe to `TextUpdating`, `TextRequested`, `CompositionStarted`,
  `CompositionCompleted`, and `CommandReceived` to receive AppKit selector
  commands such as `deleteBackward:`.
- Forward key events to the platform adapter via `ProcessKeyEvent`. The
  adapter may consume keys while the IME is active.
- Notify the platform about caret geometry via `NotifyCaretRectChanged`. The
  sample calls this even when the text is empty, so IME candidate windows are
  positioned correctly before composition starts.
- The adapter and native bridge use `XamlRoot.RasterizationScale` (managed) and
  device-pixel rounding (native) to align AppKit candidate windows with the
  rendered caret.

## Debugging & troubleshooting

- To reproduce layout or positioning issues, enable native logging and include
  the tail of `$TMPDIR/unoedit_ime.log` when filing issues.
- Backspace/Delete: the sample handles deletion commands routed from AppKit via
  `CommandReceived`, so Backspace and Delete work correctly during normal
  editing and IME composition.

This sample keeps Uno's built-in `TextBox` for the visual/editing surface and
uses CoreText only as the platform IME bridge. This is the recommended approach
when Uno itself cannot be source-patched.
