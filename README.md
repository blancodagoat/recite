<div align="center">

<img src="assets/logo.png" width="96" alt="">

# Recite

### Copy text from anything on your screen.

**Hotkey, drag, done. The words are on your clipboard,<br>even from videos, images, games, and dialogs that won't let you select.**

`one exe` · `works offline` · `no account` · `no uploads` · `no telemetry`

</div>

---

## The whole app in 10 seconds

1. **Press <kbd>Ctrl</kbd>+<kbd>PrintScreen</kbd>.** The screen freezes; windows highlight as you hover.
2. **Drag over the text**, or just click a window to take all of it.
3. **The text is on your clipboard.** A balloon shows the first line so you know it worked.

That's the entire app. Error dialogs that block selection, hardcoded subtitles, code in a YouTube tutorial, a screenshot someone posted in chat: if you can see the words, you can copy them.

## Why not the tools you already have?

| | The catch | Recite |
|---|---|---|
| Snipping Tool's text actions | Snip, wait for the editor, click Text actions, select, copy: five steps and a window | Hotkey, drag, done |
| PowerToys Text Extractor | Installing a fifteen-tool suite for one feature | One exe, under a megabyte |
| Capture2Text | Abandoned, bundles its own OCR models | Uses the OCR engine already inside Windows |

Recite reads with the engine that ships in Windows 10 and 11, in whatever languages you have installed, entirely offline. Nothing is bundled and nothing is sent anywhere.

<details>
<summary><b>Details</b></summary>

<br>

- The hotkey is rebindable from the tray menu; the click-to-record dialog applies instantly and reverts anything Windows refuses.
- The selection overlay freezes the desktop first, so the overlay itself never ends up in the grab, and hovering highlights whole windows and individual panes for one-click capture.
- Very large multi-monitor grabs are scaled down to the OCR engine's input limit rather than refused.
- If the hotkey stops working while an admin app or game has focus, use *Restart as administrator* in the tray once; that's a Windows rule about elevated windows, not ours.
- The app never phones home. The update check in the tray menu runs only when you click it.
- Config lives in `%APPDATA%\Recite\config.json`, and a small rolling log in the same folder records what the app did.
- Requires Windows 10 2004 or later with at least one language pack (OCR comes with them).
- Windows 11 ships a newer, sharper OCR model inside Snipping Tool and Photos. Recite can load it (set `experimentalOneOcr` to true in config), but it is off by default while its recognition path is still being finished; the shipping engine is the stable built-in one.

</details>

<details>
<summary><b>Building &amp; tests</b></summary>

<br>

```
dotnet build src/Recite/Recite.csproj
dotnet run --project tests/Recite.Tests
```

The test suite includes a real OCR round trip: a known sentence is rendered into a bitmap and the Windows engine has to read it back. It runs headless, no desktop required.

</details>

---

<div align="center">

**[MIT license](LICENSE)** · part of a family with [Memento](https://github.com/blancodagoat/memento) (screenshots) and [DejaVu](https://github.com/blancodagoat/DejaVu) (instant replay)

</div>
