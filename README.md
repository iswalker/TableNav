# TableNav

A fast, lightweight desktop app for exploring and editing CSV and XLSX files — built for analysts who want raw data speed without the clutter of a full spreadsheet suite.

## What it does

- **Instant load** — opens CSV/XLSX files faster than Excel
- **Virtual rendering** — handles large files smoothly with a virtualized grid
- **Smart filters** — hover a column header and press Space to open a rich filter pane with search, value tree, date grouping (year/month/day), and numeric "greater than" thresholds
- **Inspect / Pivot** — drag fields into the Pivot zone for instant grouped summaries; supports date nesting and sort-by-count or sort-by-value
- **Tabs** — open multiple files in one window; switch with keyboard shortcuts
- **Keyboard-first** — Ctrl+Arrow navigation, Esc to clear filters, and more
- **Hex color preview** — detected hex codes show a color swatch inline
- **Auto column sizing** — columns fit content automatically on every edit or paste
- **Median in status bar** — selected range shows sum, count, average, **and median**

## Requirements

- Windows 10/11
- [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/) (usually pre-installed on Windows 11)

## Running from source

```bat
build.bat
TableNav.exe
```

`build.bat` compiles `TableNav.cs` using the .NET Framework C# compiler (`csc.exe`). The resulting `TableNav.exe` embeds an HTTP server that serves `TableNav.html` to the WebView2 control.

## File registration (optional)

Run `register.bat` (as Administrator) to associate `.csv` and `.xlsx` files with TableNav so they open on double-click. Run `unregister.bat` to remove the association.

## License

MIT
