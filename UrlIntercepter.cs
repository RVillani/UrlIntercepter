// Shows a clicked link in an editable box and opens it in the default browser only when confirmed.
// Install.cmd compiles this with the C# 5 compiler that ships with Windows (.NET Framework 4),
// so newer C# syntax (string interpolation, ?., => members, out var) isn't available.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

static class Native
{
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] public static extern int SetWindowTheme(IntPtr hwnd, string appName, string idList);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmdShow);

    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
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

// Resizable dialog with a wrapping text box that fills the space between the label and the buttons
class LinkDialog : Form
{
    readonly RichTextBox textBox;
    readonly Button copy, removeParams, removeAnchor, ok, cancel;
    readonly Timer copyTimer = new Timer { Interval = 1500 };
    readonly Font monoFont;
    readonly bool dark;
    readonly Color letterColor, digitColor, symbolColor, queryColor, ampersandColor, anchorColor;

    // Recoloring changes the selection, which must not be recorded as an edit
    bool busy;
    // The built-in undo also undoes each recoloring, so keep our own text history
    readonly List<string> history = new List<string>();

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
        MinimumSize = new Size(600, 180);
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ShowIcon = false;
        MinimizeBox = false;

        var label = new Label
        {
            Text = "Review or edit the link:",
            AutoSize = true,
            Location = new Point(12, 12),
        };

        // Monospaced font where l/I/1 and O/0 look different; first one installed wins
        var installed = new HashSet<string>(new InstalledFontCollection().Families.Select(f => f.Name));
        string monoName = new[] { "Cascadia Mono", "Consolas", "Courier New" }.FirstOrDefault(installed.Contains) ?? "Courier New";
        monoFont = new Font(monoName, 10);

        // RichTextBox so letters, digits and symbols can be colored differently
        textBox = new RichTextBox
        {
            Multiline = true,
            WordWrap = true,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            DetectUrls = false,
            Font = monoFont,
            Text = url,
            Location = new Point(12, 34),
            Size = new Size(696, 120),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
        };

        // Copies the link without closing the window; the label briefly confirms it
        copy = new Button
        {
            Text = "Copy link",
            Size = new Size(100, 28),
            Location = new Point(12, 162),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
        };
        // Deletes the query string (from the ? up to the #), keeping the anchor
        removeParams = new Button
        {
            Text = "Remove parameters",
            Size = new Size(130, 28),
            Location = new Point(118, 162),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
        };
        // Deletes the anchor (from the # to the end)
        removeAnchor = new Button
        {
            Text = "Remove anchor",
            Size = new Size(110, 28),
            Location = new Point(254, 162),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
        };
        ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Size = new Size(80, 28),
            Location = new Point(542, 162),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
        };
        cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Size = new Size(80, 28),
            Location = new Point(628, 162),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
        };

        Controls.AddRange(new Control[] { label, textBox, copy, removeParams, removeAnchor, ok, cancel });
        // Enter confirms (see OnTextBoxKeyDown), Esc cancels
        AcceptButton = ok;
        CancelButton = cancel;
        ResumeLayout();

        // Follow Windows' app theme (Settings > Personalization > Colors > app mode)
        using (RegistryKey personalize = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
        {
            dark = personalize != null && Equals(personalize.GetValue("AppsUseLightTheme"), 0);
        }
        if (dark)
        {
            letterColor = Color.FromArgb(240, 240, 240);
            digitColor = Color.FromArgb(255, 184, 108);
            symbolColor = Color.FromArgb(110, 180, 255);
            queryColor = Color.FromArgb(255, 110, 210);
            ampersandColor = Color.FromArgb(90, 230, 120);
            anchorColor = Color.FromArgb(255, 80, 80);
            ApplyDarkTheme();
        }
        else
        {
            letterColor = Color.FromArgb(30, 30, 30);
            digitColor = Color.FromArgb(176, 82, 0);
            symbolColor = Color.FromArgb(0, 92, 197);
            queryColor = Color.FromArgb(200, 0, 160);
            ampersandColor = Color.FromArgb(0, 135, 60);
            anchorColor = Color.FromArgb(210, 0, 0);
        }

        copyTimer.Tick += delegate { copyTimer.Stop(); copy.Text = "Copy link"; };
        copy.Click += OnCopyClick;
        removeParams.Click += OnRemoveParamsClick;
        removeAnchor.Click += OnRemoveAnchorClick;
        textBox.TextChanged += OnTextBoxTextChanged;
        textBox.KeyDown += OnTextBoxKeyDown;
    }

    void ApplyDarkTheme()
    {
        BackColor = Color.FromArgb(32, 32, 32);
        ForeColor = Color.FromArgb(240, 240, 240);
        textBox.BackColor = Color.FromArgb(43, 43, 43);
        textBox.ForeColor = Color.White;
        textBox.BorderStyle = BorderStyle.FixedSingle;
        foreach (Button button in new[] { copy, removeParams, removeAnchor, ok, cancel })
        {
            button.FlatStyle = FlatStyle.Flat;
            button.BackColor = Color.FromArgb(55, 55, 55);
            button.ForeColor = Color.White;
            button.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 80);
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(70, 70, 70);
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(85, 85, 85);
        }
        // Highlight the default (Enter) button with the accent-style border
        ok.FlatAppearance.BorderColor = Color.FromArgb(96, 205, 255);

        // Dark scrollbar; no-op on older Windows
        textBox.HandleCreated += delegate { Native.SetWindowTheme(textBox.Handle, "DarkMode_Explorer", null); };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Dark title bar; no-op on older Windows
        if (dark)
        {
            int on = 1;
            Native.DwmSetWindowAttribute(Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, 4);
        }
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
        // ? starts the query, & separates parameters and # starts the anchor; their own colors
        // make each part easy to spot
        foreach (Match m in Regex.Matches(textBox.Text, "[?&#]"))
        {
            textBox.Select(m.Index, m.Length);
            textBox.SelectionColor = m.Value == "?" ? queryColor : m.Value == "&" ? ampersandColor : anchorColor;
        }

        textBox.Select(start, length);
        Native.SendMessage(textBox.Handle, Native.WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
        textBox.Invalidate();
        busy = false;

        // Recolor runs after every text change, so keep the button state in sync here
        removeParams.Enabled = QueryStart(textBox.Text) >= 0;
        removeAnchor.Enabled = textBox.Text.Contains("#");
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
        if (busy) return;
        history.Add(textBox.Text);
        Recolor();
    }

    void OnTextBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            // A RichTextBox takes Enter as a newline; make it confirm like OK instead
            e.SuppressKeyPress = true;
            ok.PerformClick();
        }
        else if (e.Control && e.KeyCode == Keys.Z)
        {
            e.SuppressKeyPress = true;
            if (history.Count > 1)
            {
                history.RemoveAt(history.Count - 1);
                busy = true;
                textBox.Text = history[history.Count - 1];
                busy = false;
                Recolor();
                textBox.Select(textBox.TextLength, 0);
            }
        }
    }

    void OnCopyClick(object sender, EventArgs e)
    {
        string link = Link;
        if (link.Length > 0)
        {
            Clipboard.SetText(link);
            copy.Text = "Copied!";
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
