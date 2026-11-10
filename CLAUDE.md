# Review links before they open in Brave (Windows)

> Project note: the notes below are the original design. The current implementation differs:
> - The handler is a C# WinForms app, `UrlIntercepter.cs`, not a PowerShell script. PowerShell flashed a console window and took ~1.2 s to start; the exe starts in ~0.15 s. It must compile with the C# 5 compiler that ships with .NET Framework 4 (no string interpolation, `?.`, `=>` members).
> - It opens the user's default browser (read from the `.html`/`.htm` UserChoice, since http/https point at this handler), not a hard-coded Brave path.
> - Dialog: resizable, follows display scaling, uses a monospaced font, colors letters/digits/symbols differently (with `?`, `&` and `#` in their own colors so query parameters and the anchor stand out). A bar of square icon buttons with tooltips, in groups: theme (System/Light/Dark), backdrop tint (a flyout with opacity and an RGB/HSV/hex color, per theme) | Remove parameters `?` (cuts from the `?` up to the `#`, keeping the anchor), Remove anchor `#` | Undo, Redo — and on the right Copy, Cancel, Open.
> - The reusable UI lives in `FluentUI.cs` (namespace `FluentUI`); the app in `UrlIntercepter.cs`. The acrylic backdrop shows only where pixels have alpha, and ordinary GDI controls write none: put them in a `SurfacePanel` set as `FluentForm.SolidContent` (excluded from the backdrop), and use `GlassControl` subclasses everywhere else. Colors, including the danger red and `BackdropTint`, are fields on `Palette.Light`/`Palette.Dark`. Windows' backdrop can't be tinted, so the form and GlassControls paint the translucent `BackdropTint` over it (`FluentForm.GlassFill`); the title bar stays untinted. Text-input controls (`TextField`, `NumberBox`, `SliderField`, `ColorPicker`) need an opaque form such as a `Flyout`. Settings (theme, tint) are in `UrlIntercepter.json` next to the exe. Icons are GDI+ vector drawings in a 24×24 box, because GDI/GDI+ can't render color emoji.
> - Display scaling runs after the constructor lays controls out, so self-sizing/layout controls redo their layout afterwards (`GlassLabel.ScaleControl`, `GlassControl.OnHandleCreated`). Flyouts aren't auto-scaled; they lay out from the font via `Metrics`, so add controls before measuring. While a flyout has focus it sends its owner WM_NCACTIVATE(TRUE), as menus do, so the owner keeps its active title bar and backdrop for previewing.
> - Some apps (new Outlook `olk.exe`, Claude Code's `/login`) launch the link handler with a "start hidden" flag. Windows applies it to the first window shown, which left the dialog running invisibly and the link lost. `OnShown` calls `ShowWindow(SW_SHOWNORMAL)` again and `Activate()`, because only the first call is affected. To check for this, look for `UrlIntercepter.exe` processes whose "Open link" window isn't visible.
> - `Install.cmd` compiles `UrlIntercepter.exe` from both `.cs` files and registers it from the folder it sits in (`%~dp0`); `Uninstall.cmd` removes the registration. There is no `.reg` file, because `.reg` files can't use relative paths.

## Problem
Clicking a web link in another app (Discord, email, PDF, etc.) opens Brave and loads the page instantly. Goal: see and edit the URL first, then open it only on confirmation.

## Key fact
Browsers have no setting or launch flag for "put the URL in the address bar but don't load it." The workaround is a small handler set as the default app for HTTP/HTTPS links. It shows the URL in an editable box and opens Brave only when you press OK.

## 1. Script — save as `C:\Tools\UrlPreview.ps1`

```powershell
param([string]$Url)
Add-Type -AssemblyName Microsoft.VisualBasic
$edited = [Microsoft.VisualBasic.Interaction]::InputBox("Review or edit the link:", "Open link", $Url)
if ($edited) {
    Start-Process "C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe" -ArgumentList "`"$edited`""
}
```

- Cancel or an empty box means nothing opens.
- If Brave was installed per-user, use `"$env:LOCALAPPDATA\BraveSoftware\Brave-Browser\Application\brave.exe"` instead. Check the real path via the Brave shortcut → Properties → Target.

## 2. Registry — save as `UrlPreview.reg` and double-click (current user only, no admin)

```reg
Windows Registry Editor Version 5.00

[HKEY_CURRENT_USER\Software\Classes\UrlPreview.Link]
@="URL Preview"

[HKEY_CURRENT_USER\Software\Classes\UrlPreview.Link\shell\open\command]
@="powershell.exe -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File \"C:\\Tools\\UrlPreview.ps1\" \"%1\""

[HKEY_CURRENT_USER\Software\UrlPreview\Capabilities]
"ApplicationName"="URL Preview"
"ApplicationDescription"="Review links before opening them"

[HKEY_CURRENT_USER\Software\UrlPreview\Capabilities\URLAssociations]
"http"="UrlPreview.Link"
"https"="UrlPreview.Link"

[HKEY_CURRENT_USER\Software\RegisteredApplications]
"URL Preview"="Software\\UrlPreview\\Capabilities"
```

## 3. Set as default
Settings → Apps → Default apps → search "URL Preview" (or "Choose defaults by link type") → set **HTTP** and **HTTPS** to URL Preview. This has to be done by hand; Windows blocks scripts from setting it.

## Caveats
- A PowerShell window may flash briefly.
- Links Windows forces into Edge (Widgets, Start search) use `microsoft-edge:` and bypass this.
- Undo: set HTTP/HTTPS back to Brave in Default apps.

## Alternatives
Browser-picker apps such as Browser Tamer or BrowserPicker (open source, GitHub) also intercept links and show them first. Check whether they let you *edit* the URL before relying on them.

## Possible next steps
- Add a "copy to clipboard" button.
- Show the real destination behind redirect or tracking links.
