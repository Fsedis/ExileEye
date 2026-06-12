# ExileEye

**On-screen price checker overlay for Path of Exile 2.**
Point it at a reward / exchange panel — it OCRs the item names and shows live
[poe.ninja](https://poe.ninja/poe2) prices right next to each row, click-through,
without alt-tabbing. English and Russian game clients are both first-class.

> Прозрачный оверлей цен для Path of Exile 2: распознаёт названия предметов на экране
> и показывает живые цены с poe.ninja рядом с каждой строкой. Русский клиент
> поддерживается наравне с английским.

## Status

🚧 Early development — the app shell builds, the core engine is being written.

## Planned

- First-run wizard: language → league (auto-detected) → one-drag region calibration
- Live prices next to each panel row, stack-aware (`2.4 (0.4 each)`)
- English & Russian client support, localized UI
- Tray icon, human-readable status, global hotkeys
- One-click installer with auto-updates (Velopack)

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
