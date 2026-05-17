# LeXtudio.TextBox

A drop-in `TextBox` control for Uno Platform and WinUI that surfaces correct
IME / input-method support on every desktop platform.

## When to use this instead of the built-in TextBox

Uno Platform's built-in `TextBox` uses the platform's native text widget on
Windows but a custom Skia renderer on Linux and macOS desktop targets.
Composition input (Japanese, Chinese, Korean, etc.) can be unreliable on those
Skia targets.  `LeXtudio.TextBox` wraps the built-in control and connects it to
`LeXtudio.UI.Text.Core` — a managed CoreText/IME bridge — so IME composition
works correctly on all Skia desktop platforms.

On Windows (WinUI / Windows App SDK) the class extends
`Microsoft.UI.Xaml.Controls.TextBox` directly; the CoreText bridge is not
involved and there is no behavioural difference from the inbox control.

## Supported targets

| Target framework              | Behaviour |
|-------------------------------|-----------|
| `net9.0-desktop` (Uno Skia)   | Full IME bridge via `LeXtudio.UI.Text.Core` |
| `net9.0-windows10.0.19041.0`  | Thin subclass of the native WinUI `TextBox` |

## Installation

```bash
dotnet add package LeXtudio.TextBox
```

Or add a `PackageReference` to your project file:

```xml
<PackageReference Include="LeXtudio.TextBox" Version="0.2.10" />
```

## Usage

```xml
xmlns:lx="using:LeXtudio.UI.Controls"

<lx:TextBox PlaceholderText="Type here…"
            AcceptsReturn="True"
            TextWrapping="Wrap" />
```

```csharp
using LeXtudio.UI.Controls;

var box = new TextBox
{
    PlaceholderText = "Type here…",
    AcceptsReturn = true,
};
box.TextChanged += (s, e) => Console.WriteLine(box.Text);
```

The control implements `IDisposable`; call `Dispose()` if you remove it from the
visual tree manually (it is called automatically via `Unloaded`).

## API surface

All properties delegate to the inner `Microsoft.UI.Xaml.Controls.TextBox`.

| Member | Type | Notes |
|---|---|---|
| `Text` | `string` | Two-way bindable |
| `PlaceholderText` | `string` | |
| `PlaceholderForeground` | `Brush?` | Added on Windows target via `DependencyProperty` |
| `Header` | `object?` | |
| `AcceptsReturn` | `bool` | |
| `TextWrapping` | `TextWrapping` | |
| `TextChanged` | event | Forwarded from the inner control |
| `SelectAll()` | method | |
| `Dispose()` | method | Tears down the CoreText context |

## License

This project is licensed under the MIT License. See the
[LICENSE](../../LICENSE) file for details.

## Copyright

(c) 2026 LeXtudio Inc. All rights reserved.
