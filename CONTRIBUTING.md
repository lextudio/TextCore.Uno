# Contributing — Building from source

This project is distributed as a NuGet package for normal consumption. Building from source is an advanced workflow intended for contributors or integrators who need to modify the library or produce packages with native assets.

## As a source dependency (advanced)

You can include the project directly in your workspace and reference it from your application using a `ProjectReference`:

```xml
<ProjectReference Include="../path/to/src/LeXtudio.UI.Text.Core.csproj" />
```

If you prefer to keep the repository separate, add it as a submodule in your consumer repository:

```bash
git submodule add https://github.com/lextudio/TextCore.Uno.git external/coretext
git submodule update --init --recursive
```

## Building the macOS native helper

When targeting macOS the project includes a tiny native helper (`libUnoEditMacInput.dylib`) that bridges AppKit text input callbacks into managed code. To build it when developing locally:

```bash
dotnet build src/LeXtudio.UI.Text.Core.csproj -c Debug -f net9.0-desktop /t:Build,BuildTextCoreMacInputBridge
```

The build produces `libUnoEditMacInput.dylib` under:

```
src/bin/<Configuration>/<TargetFramework>/libUnoEditMacInput.dylib
```

Consumer projects that build from source should ensure this dylib is copied into their app output directory. See the `LeXtudio.UI.Text.Core.csproj` targets for an example `CopyTextCoreMacInputBridge` target.

## Packaging & CI (developer notes)

- When producing NuGet packages, ensure `libUnoEditMacInput.dylib` exists in the Text.Core output before packing — the `Pack` target validates its presence for macOS packaging.
- In CI, build the Text.Core project and run the `BuildTextCoreMacInputBridge` target before building consumers that depend on the dylib.

## Reporting issues and contributions

- Open issues and PRs against https://github.com/lextudio/TextCore.Uno.
- For contributor guidelines, coding style, and release process, follow the parent project's CONTRIBUTING.md (if present) or start a discussion in an issue.

Thank you for contributing!
