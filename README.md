# ExileEye

**On-screen price checker overlay for Path of Exile 2.**
Point it at a reward / exchange panel — it OCRs the item names and shows live
[poe.ninja](https://poe.ninja/poe2) prices right next to each row, click-through,
without alt-tabbing. English and Russian game clients are both first-class.

> Прозрачный оверлей цен для Path of Exile 2: распознаёт названия предметов на экране
> и показывает живые цены с poe.ninja рядом с каждой строкой. Русский клиент
> поддерживается наравне с английским.

## Status

🚧 Early development — the core engine works end to end: screen capture → OCR →
live poe.ninja prices → click-through overlay. Calibrate with one drag, `F5`
starts/stops, `Esc` or `Ctrl+Click` hides the overlay.

## Working today

- Live prices next to each panel row, stack-aware (`2.4 (0.4 ea)`) — both the
  exchange panel's `14x` prefix and the combinations panel's `(6)` suffix
- English & Russian clients (the Russian OCR model downloads on first use)
- Exact → prefix → fuzzy name matching that shrugs off OCR misreads
- One-drag region calibration, Fluent settings window

## Planned

- First-run wizard, auto-detected league list
- Tray icon, localized UI (ru/en)
- One-click installer with auto-updates (Velopack)
- Currency icons in the overlay instead of `div`/`ex` text

## Build from source

Requires the .NET 8 SDK.

```sh
dotnet test src/ExileEye.Tests/
dotnet build src/ExileEye/
```

## Tech

.NET 8, WPF + [WPF-UI](https://github.com/lepoco/wpfui) (Fluent), Tesseract OCR,
Win32 layered overlay, SharpHook global hotkeys.

## License

MIT
