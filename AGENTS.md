# Repository Guidelines

## Project Structure & Module Organization

OpenUtau is a cross-platform .NET/Avalonia application. The solution is defined in `OpenUtau.slnx`:

- `OpenUtau/` contains the desktop UI, views, controls, view models, assets, localization, and integrations.
- `OpenUtau.Core/` contains the editor engine, audio/rendering, file formats, commands, phonemizers, and plugin APIs.
- `OpenUtau.Plugin.Builtin/` contains bundled phonemizers and related data.
- `OpenUtau.Test/` contains xUnit tests and reusable fixtures under `Files/` and `Usts/`.
- `OpenUtau.UiTest/` contains Avalonia headless UI tests and generated screenshots.
- `cpp/` contains the native Worldline code; `runtimes/` stores platform-native binaries.
- `Misc/` and `Logo/` hold scripts and project artwork.

Keep `.axaml` views paired with their `.axaml.cs` code-behind and update localized strings in the resource tables rather than hard-coding UI text.

## Build, Test, and Development Commands

Use a current .NET SDK (CI tests SDKs 6, 8, and 10; projects target .NET 10).

```text
dotnet restore OpenUtau/OpenUtau.csproj -r win-x64
dotnet build OpenUtau.slnx
dotnet run --project OpenUtau/OpenUtau.csproj
dotnet run --project OpenUtau.Test/OpenUtau.Test.csproj
dotnet run --project OpenUtau.UiTest/OpenUtau.UiTest.csproj
```

Replace `win-x64` with `linux-x64` or `osx-arm64` when needed. Release packaging uses `dotnet publish OpenUtau -c Release -r <rid> --self-contained true` (macOS uses the `BundleApp` MSBuild target).

## Coding Style & Naming Conventions

Follow `.editorconfig`: four spaces, UTF-8 with BOM, final newlines, and a 100-character guideline for C# files. Use PascalCase for types and constants, clear descriptive members, braces, sorted system usings, and the repository’s existing `var` preferences. Nullable reference types are enabled and the UI project treats warnings as errors.

## Testing Guidelines

Tests use xUnit v3. Name files and classes with a `Test` suffix (for example, `PhonemizerTest.cs`) and keep fixtures in the appropriate `OpenUtau.Test/Files` or `Usts` directory. Run unit and UI tests as separate commands; UI tests produce screenshots under `OpenUtau.UiTest/bin/**/ui-screenshots/`. No coverage threshold is currently documented.

## Commit & Pull Request Guidelines

Use short, imperative commit subjects such as `Fix waveform peak rendering inversion`; scoped subjects like `fix(pianoroll): ...` are also used. Add an issue or PR reference when applicable. Keep PRs focused, describe the behavior and testing performed (including OS), link the issue they resolve, and avoid debug leftovers, personal paths, binaries, or unrelated files. For non-trivial changes, smoke-test opening a project, loading a singer, editing notes or pitch, and playing back.
