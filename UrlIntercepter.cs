// Shows a clicked link in an editable box and opens it in the default browser only when confirmed.
// Install.cmd compiles this and FluentUI.cs (the reusable UI parts) with the C# 5 compiler that ships
// with Windows (.NET Framework 4), so newer C# syntax (string interpolation, ?., => members, out var) isn't available.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using FluentUI;
using Microsoft.Win32;

static class Native
{
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmdShow);

    public const int WM_SETREDRAW = 0x000B;
    public const int SW_SHOWNORMAL = 1;
}

static class Program
{
    const string OwnProgId = "UrlIntercepter.Link";

    [STAThread]
    static void Main(string[] args)
    {
        // Opt in to DPI awareness before any window is created; otherwise Windows bitmap-stretches
        // (blurry) or, on some setups, doesn't scale the dialog at all
        Native.SetProcessDPIAware();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        string edited;
        using (var dialog = new LinkDialog(args.Length > 0 ? args[0] : ""))
        {
            if (dialog.ShowDialog() != DialogResult.OK) return;
            edited = dialog.Link;
        }
        if (edited.Length == 0) return;

        string command = GetDefaultBrowserCommand();
        if (command == null)
        {
            MessageBox.Show("Could not find your default browser.", "Open link", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        // Split "C:\path\browser.exe" --args %1 into executable and arguments
        Match m = Regex.Match(command, "^\\s*\"([^\"]+)\"\\s*(.*)$");
        if (!m.Success) m = Regex.Match(command, "^\\s*(\\S+)\\s*(.*)$");
        string exe = Environment.ExpandEnvironmentVariables(m.Groups[1].Value);
        string arguments = m.Groups[2].Value.Replace("%*", "");

        // Quotes and spaces aren't valid in a URL anyway; encoding them keeps the argument intact
        string safeUrl = edited.Replace("\"", "%22").Replace(" ", "%20");
        if (Regex.IsMatch(arguments, "%[1lL]"))
            arguments = Regex.Replace(arguments, "%[1lL]", delegate { return safeUrl; });
        else
            arguments = (arguments + " \"" + safeUrl + "\"").Trim();

        Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = false });
    }

    // Returns the open command of the user's default browser. Once this program is the
    // http/https handler, those associations point back here, so they're skipped and the
    // .html/.htm association (which Windows also sets when choosing a default browser) is used.
    static string GetDefaultBrowserCommand()
    {
        string[] userChoiceKeys =
        {
            @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice",
            @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice",
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.html\UserChoice",
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.htm\UserChoice",
        };
        foreach (string key in userChoiceKeys)
        {
            string progId;
            using (RegistryKey choice = Registry.CurrentUser.OpenSubKey(key))
            {
                progId = choice == null ? null : choice.GetValue("ProgId") as string;
            }
            if (string.IsNullOrEmpty(progId) || progId == OwnProgId) continue;

            using (RegistryKey open = Registry.ClassesRoot.OpenSubKey(progId + @"\shell\open\command"))
            {
                string command = open == null ? null : open.GetValue("") as string;
                if (!string.IsNullOrEmpty(command)) return command;
            }
        }
        return null;
    }
}


// Resizable dialog with a wrapping text box that fills the space between the label and the button bar
class LinkDialog : FluentForm
{
    readonly RichTextBox textBox;
    readonly SurfacePanel textPanel;
    readonly ButtonBar buttonBar;
    readonly IconButton theme, backdropButton, removeParams, removeAnchor, undo, redo, copy, cancel, open;
    readonly Timer copyTimer = new Timer { Interval = 1500 };
    readonly Font monoFont;
    readonly JsonSettings settings = JsonSettings.NextToExecutable();
    Color letterColor, digitColor, symbolColor, queryBack, valueBack, anchorBack;

    // Recoloring changes the selection, which must not be recorded as an edit
    bool busy;
    // The built-in undo also undoes each recoloring, so keep our own text history
    readonly List<string> history = new List<string>();
    readonly List<string> redoHistory = new List<string>();
    // When the backdrop flyout last closed; see OnBackdropClick
    int flyoutClosedAt;

    public string Link
    {
        get { return Regex.Replace(textBox.Text, "[\r\n]", "").Trim(); }
    }

    public LinkDialog(string url)
    {
        SuspendLayout();
        // Sizes below are in 96-DPI pixels; WinForms scales them to the display's scaling factor
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9);
        Text = "Open link";
        ClientSize = new Size(720, 200);
        // The width is raised to fit the button bar in OnLoad
        MinimumSize = new Size(200, 180);
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ShowIcon = false;
        MinimizeBox = false;

        var label = new GlassLabel
        {
            Text = "Review or edit the link:",
            Location = new Point(12, 12),
        };

        // Monospaced font where l/I/1 and O/0 look different; first one installed wins
        var installed = new HashSet<string>(new InstalledFontCollection().Families.Select(f => f.Name));
        string monoName = new[] { "Cascadia Mono", "Consolas", "Courier New" }.FirstOrDefault(installed.Contains) ?? "Courier New";
        monoFont = new Font(monoName, 10);

        // RichTextBox so letters, digits and symbols can be colored differently. It's an ordinary control,
        // so it sits in the opaque panel rather than on the backdrop.
        textBox = new RichTextBox
        {
            Multiline = true,
            WordWrap = true,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            DetectUrls = false,
            BorderStyle = BorderStyle.None,
            Font = monoFont,
            Text = url,
            Dock = DockStyle.Fill,
        };
        textPanel = new SurfacePanel
        {
            Location = new Point(12, 34),
            Size = new Size(696, 116),
            Padding = new Padding(8, 6, 2, 6),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
        };
        textPanel.Controls.Add(textBox);

        // Icon and tooltip follow the theme mode; see OnThemeChanged
        theme = new IconButton();
        backdropButton = new IconButton { Icon = Icons.ArtistPalette, ToolTipText = "Backdrop tint and opacity" };
        removeParams = new IconButton { Icon = Icons.Question, Tone = ButtonTone.Danger, ToolTipText = "Remove parameters (from ? up to #)" };
        removeAnchor = new IconButton { Icon = Icons.Hash, Tone = ButtonTone.Danger, ToolTipText = "Remove anchor (from #)" };
        undo = new IconButton { Icon = Icons.Undo, ToolTipText = "Undo (Ctrl+Z)" };
        redo = new IconButton { Icon = Icons.Redo, ToolTipText = "Redo (Ctrl+Y)" };
        copy = new IconButton { Icon = Icons.Clipboard, ToolTipText = "Copy link" };
        cancel = new IconButton { Icon = Icons.Cancel, ToolTipText = "Cancel (Esc)", DialogResult = DialogResult.Cancel };
        open = new IconButton { Icon = Icons.Globe, ToolTipText = "Open in browser (Enter)", DialogResult = DialogResult.OK };

        buttonBar = new ButtonBar
        {
            Location = new Point(12, 158),
            Size = new Size(696, 32),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
        };
        // App settings, link edits and edit history on the left; final actions on the right
        buttonBar.AddGroup(theme, backdropButton);
        buttonBar.AddGroup(removeParams, removeAnchor);
        buttonBar.AddGroup(undo, redo);
        buttonBar.AddTrailingGroup(copy, cancel, open);

        Controls.AddRange(new Control[] { label, textPanel, buttonBar });
        // Enter opens (see OnTextBoxKeyDown), Esc cancels
        AcceptButton = open;
        CancelButton = cancel;
        SolidContent = textPanel;
        ResumeLayout();

        LoadTint(Palette.Light, "light");
        LoadTint(Palette.Dark, "dark");
        ThemeChanged += OnThemeChanged;
        ThemeMode = settings.GetEnum("theme", ThemeMode.System);

        copyTimer.Tick += delegate { copyTimer.Stop(); copy.Icon = Icons.Clipboard; copy.ToolTipText = "Copy link"; };
        theme.Click += OnThemeClick;
        backdropButton.Click += OnBackdropClick;
        removeParams.Click += OnRemoveParamsClick;
        removeAnchor.Click += OnRemoveAnchorClick;
        undo.Click += delegate { Undo(); textBox.Focus(); };
        redo.Click += delegate { Redo(); textBox.Focus(); };
        copy.Click += OnCopyClick;
        textBox.TextChanged += OnTextBoxTextChanged;
        textBox.KeyDown += OnTextBoxKeyDown;
    }

    void OnThemeChanged(object sender, EventArgs e)
    {
        if (IsDark)
        {
            letterColor = Color.FromArgb(240, 240, 240);
            digitColor = Color.FromArgb(255, 184, 108);
            symbolColor = Color.FromArgb(110, 180, 255);
            queryBack = Mix(Palette.Surface, Color.FromArgb(190, 110, 255), 0.18);
            valueBack = Mix(Palette.Surface, Color.FromArgb(190, 110, 255), 0.40);
            anchorBack = Mix(Palette.Surface, Color.FromArgb(70, 210, 120), 0.28);
        }
        else
        {
            letterColor = Color.FromArgb(30, 30, 30);
            digitColor = Color.FromArgb(176, 82, 0);
            symbolColor = Color.FromArgb(0, 92, 197);
            queryBack = Mix(Palette.Surface, Color.FromArgb(150, 70, 230), 0.10);
            valueBack = Mix(Palette.Surface, Color.FromArgb(150, 70, 230), 0.25);
            anchorBack = Mix(Palette.Surface, Color.FromArgb(30, 170, 80), 0.18);
        }
        textBox.BackColor = Palette.Surface;
        textBox.ForeColor = Palette.Text;
        SystemTheme.ApplyToControl(textBox, IsDark);

        theme.Icon = ThemeMode == ThemeMode.Dark ? Icons.Moon : ThemeMode == ThemeMode.Light ? (IconPainter)Icons.Sun : Icons.Monitor;
        theme.ToolTipText = "Theme: " + ThemeMode;
        if (textBox.IsHandleCreated) Recolor();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // Sizes are display-scaled by now; keep every button visible
        MinimumSize = new Size(buttonBar.MinimumWidth + Width - buttonBar.Width, MinimumSize.Height);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Some apps (new Outlook, Claude Code) launch link handlers with a "start hidden" flag.
        // Windows applies it to the first window shown, which leaves this dialog invisible;
        // a second ShowWindow call isn't affected by it.
        Native.ShowWindow(Handle, Native.SW_SHOWNORMAL);
        Activate();
        history.Add(textBox.Text);
        Recolor();
        textBox.Focus();
        // Caret at the end instead of selecting everything, so the colors are visible right away
        textBox.Select(textBox.TextLength, 0);
    }

    // Recolor the whole text: letters, digits, everything else. Also resets the font, so
    // formatting pasted from web pages or documents doesn't stick.
    void Recolor()
    {
        busy = true;
        int start = textBox.SelectionStart;
        int length = textBox.SelectionLength;
        Native.SendMessage(textBox.Handle, Native.WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);

        textBox.SelectAll();
        textBox.SelectionFont = monoFont;
        textBox.SelectionColor = letterColor;
        textBox.SelectionBackColor = textBox.BackColor;
        foreach (Match m in Regex.Matches(textBox.Text, "[0-9]+"))
        {
            textBox.Select(m.Index, m.Length);
            textBox.SelectionColor = digitColor;
        }
        foreach (Match m in Regex.Matches(textBox.Text, "[^\\p{L}0-9]+"))
        {
            textBox.Select(m.Index, m.Length);
            textBox.SelectionColor = symbolColor;
        }
        // Background highlights so the parts can be skimmed: the query (from ? up to #) gets a
        // light tint, each parameter's value a stronger one, and the anchor (from #) its own tint
        string text = textBox.Text;
        int hash = text.IndexOf('#');
        int queryEnd = hash >= 0 ? hash : text.Length;
        int query = QueryStart(text);
        if (query >= 0)
        {
            textBox.Select(query, queryEnd - query);
            textBox.SelectionBackColor = queryBack;
            // Parameters are separated by & (or ;); a value runs from its first = to the separator
            foreach (Match m in Regex.Matches(text.Substring(query + 1, queryEnd - query - 1), "=([^&;]+)"))
            {
                textBox.Select(query + 1 + m.Groups[1].Index, m.Groups[1].Length);
                textBox.SelectionBackColor = valueBack;
            }
        }
        if (hash >= 0)
        {
            textBox.Select(hash, text.Length - hash);
            textBox.SelectionBackColor = anchorBack;
        }

        textBox.Select(start, length);
        Native.SendMessage(textBox.Handle, Native.WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
        textBox.Invalidate();
        busy = false;

        // Recolor runs after every text change, so keep the button states in sync here
        removeParams.Enabled = QueryStart(textBox.Text) >= 0;
        removeAnchor.Enabled = textBox.Text.Contains("#");
        undo.Enabled = history.Count > 1;
        redo.Enabled = redoHistory.Count > 0;
    }

    // Blend a tint into a background color; amount 0 is the background, 1 the tint
    static Color Mix(Color background, Color tint, double amount)
    {
        return Color.FromArgb(
            (int)Math.Round(background.R + (tint.R - background.R) * amount),
            (int)Math.Round(background.G + (tint.G - background.G) * amount),
            (int)Math.Round(background.B + (tint.B - background.B) * amount));
    }

    // A ? only starts the query when it comes before the anchor's #
    static int QueryStart(string text)
    {
        int index = text.IndexOf('?');
        int hash = text.IndexOf('#');
        return hash >= 0 && index > hash ? -1 : index;
    }

    void OnTextBoxTextChanged(object sender, EventArgs e)
    {
        // Color changes (e.g. switching theme) also raise TextChanged
        if (busy || (history.Count > 0 && history[history.Count - 1] == textBox.Text)) return;
        history.Add(textBox.Text);
        redoHistory.Clear();
        Recolor();
    }

    void OnTextBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            // A RichTextBox takes Enter as a newline; make it confirm like Open instead
            e.SuppressKeyPress = true;
            open.PerformClick();
        }
        else if (e.Control && e.KeyCode == Keys.Z && !e.Shift)
        {
            e.SuppressKeyPress = true;
            Undo();
        }
        else if (e.Control && (e.KeyCode == Keys.Y || (e.KeyCode == Keys.Z && e.Shift)))
        {
            e.SuppressKeyPress = true;
            Redo();
        }
    }

    void Undo()
    {
        if (history.Count < 2) return;
        redoHistory.Add(history[history.Count - 1]);
        history.RemoveAt(history.Count - 1);
        ShowHistoryText(history[history.Count - 1]);
    }

    void Redo()
    {
        if (redoHistory.Count == 0) return;
        string text = redoHistory[redoHistory.Count - 1];
        redoHistory.RemoveAt(redoHistory.Count - 1);
        history.Add(text);
        ShowHistoryText(text);
    }

    // Sets the text without recording it as a new edit
    void ShowHistoryText(string text)
    {
        busy = true;
        textBox.Text = text;
        busy = false;
        Recolor();
        textBox.Select(textBox.TextLength, 0);
    }

    // Cycles System -> Light -> Dark and remembers the choice
    void OnThemeClick(object sender, EventArgs e)
    {
        ThemeMode = (ThemeMode)(((int)ThemeMode + 1) % 3);
        settings.Set("theme", ThemeMode);
        textBox.Focus();
    }

    // Opens the tint and opacity settings for the current theme; they preview live and are saved on close
    void OnBackdropClick(object sender, EventArgs e)
    {
        // Clicking this button while the flyout is open first closes the flyout (it loses focus);
        // don't reopen it right away
        if (unchecked(Environment.TickCount - flyoutClosedAt) < 300) return;
        var flyout = new BackdropFlyout(this);
        flyout.FormClosed += delegate
        {
            flyoutClosedAt = Environment.TickCount;
            SaveTint(Palette.Light, "light");
            SaveTint(Palette.Dark, "dark");
        };
        flyout.ShowNear(backdropButton);
    }

    // Stored per theme as e.g. "darkTint": "#202020", "darkTintOpacity": 30 (percent) and
    // "darkTintBlend": "Overlay"
    void LoadTint(Palette palette, string theme)
    {
        Color color;
        if (ColorPicker.TryParseHex(settings.GetString(theme + "Tint", null), out color))
            palette.BackdropTint = Color.FromArgb(palette.BackdropTint.A, color);
        palette.TintOpacity = settings.GetInt(theme + "TintOpacity", palette.TintOpacity);
        palette.TintBlend = settings.GetEnum(theme + "TintBlend", TintBlend.Overlay);
    }

    void SaveTint(Palette palette, string theme)
    {
        settings.Set(theme + "Tint", ColorPicker.ToHex(palette.BackdropTint));
        settings.Set(theme + "TintOpacity", palette.TintOpacity);
        settings.Set(theme + "TintBlend", palette.TintBlend);
    }

    // Copies the link without closing the window; the icon briefly turns into a check mark
    void OnCopyClick(object sender, EventArgs e)
    {
        string link = Link;
        if (link.Length > 0)
        {
            Clipboard.SetText(link);
            copy.Icon = Icons.Check;
            copy.ToolTipText = "Copied!";
            copyTimer.Stop();
            copyTimer.Start();
        }
        textBox.Focus();
    }

    void OnRemoveParamsClick(object sender, EventArgs e)
    {
        string text = textBox.Text;
        int index = QueryStart(text);
        if (index >= 0)
        {
            // Stop at the #, keeping the anchor. Goes through TextChanged, so it's recolored
            // and Ctrl+Z restores the parameters
            int hash = text.IndexOf('#');
            textBox.Text = text.Substring(0, index) + (hash >= 0 ? text.Substring(hash) : "");
        }
        textBox.Focus();
        textBox.Select(textBox.TextLength, 0);
    }

    void OnRemoveAnchorClick(object sender, EventArgs e)
    {
        int index = textBox.Text.IndexOf('#');
        if (index >= 0)
        {
            textBox.Text = textBox.Text.Substring(0, index);
        }
        textBox.Focus();
        textBox.Select(textBox.TextLength, 0);
    }
}
