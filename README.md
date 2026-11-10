# URL Intercepter

Review and edit web links before they open in your browser (Windows).

Clicking a link in another app (Discord, email, a PDF…) normally loads the page right away. URL Intercepter registers itself as the handler for `http`/`https` links and shows each link in a small dialog first. The link opens in your default browser only when you press Open.

## Features

- Editable link box with a monospaced font and colors for letters, digits and symbols, so look-alike characters are easy to tell apart
- `?`, `&` and `#` get their own colors, so query parameters and the anchor stand out
- Icon buttons (hover for their names), grouped left to right:
  - **Theme**: cycles System → Light → Dark. The choice is saved in `UrlIntercepter.json` next to the exe
  - **Backdrop** (palette): a flyout with the acrylic tint's opacity, blend mode (Normal or Overlay) and color (RGB or HSV sliders, or a hex value), previewed live and saved separately for the light and dark themes. Windows draws the title bar, so it stays untinted
  - **Remove parameters** (`?`) deletes the query string, from the `?` up to the `#` (the anchor is kept); **Remove anchor** (`#`) deletes everything from the `#`
  - **Undo** / **Redo** (Ctrl+Z, Ctrl+Y)
  - **Copy link**, **Cancel** (Esc), **Open** in the browser (Enter)
- Windows 11 look: acrylic backdrop (Windows 11 22H2 and later; a solid background elsewhere), colorful icons, follows display scaling, and the window is resizable
- Starts in about 0.15 s with no console flash

## Install

1. Put the folder where you want to keep it, then run `Install.cmd`. It compiles `UrlIntercepter.exe` with the C# compiler that ships with Windows (.NET Framework 4) and registers it for the current user. You don't need admin rights.
2. Open **Settings → Apps → Default apps → URL Intercepter** and set **HTTP** and **HTTPS** to it. Windows doesn't let scripts change this setting, so you have to do it by hand.

Run `Install.cmd` again after editing the `.cs` files or moving the folder. `FluentUI.cs` holds the reusable UI parts (backdrop, themes, icon buttons, icons, settings file); `UrlIntercepter.cs` holds the app.

## Uninstall

Set HTTP and HTTPS back to your browser in Default apps, then run `Uninstall.cmd`.

## Notes

- URL Intercepter opens links in the browser set as the default for `.html` files, because the `http`/`https` default now points to URL Intercepter.
- Links that Windows forces into Edge (Widgets, Start search) use the `microsoft-edge:` scheme and skip URL Intercepter.
