// Reusable Windows 11 (Fluent) style building blocks for WinForms: acrylic/Mica backdrop, light/dark/system
// themes, controls that draw correctly on the backdrop, colorful vector icons and a JSON settings file.
// Compiled with the C# 5 compiler that ships with .NET Framework 4 (see Install.cmd), so newer C# syntax
// (string interpolation, ?., => members, auto-property initializers) isn't available.
//
// How the backdrop works: the form extends its frame over the client area, so the system backdrop shows
// wherever the window's pixels are transparent. Ordinary GDI drawing (Label, Button, TextBox...) writes no
// alpha, which makes it look washed out or invisible there. So:
//   - Put ordinary controls in one SurfacePanel and set it as FluentForm.SolidContent; that rectangle is
//     excluded from the backdrop and drawn opaque.
//   - Everywhere else, use GlassControl subclasses (GlassLabel, IconButton, ButtonBar), which draw with alpha.
// On Windows versions without system backdrops, everything falls back to a solid themed background.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace FluentUI
{
    public enum ThemeMode { System, Light, Dark }

    // Values of DWMWA_SYSTEMBACKDROP_TYPE
    public enum Backdrop { None = 1, Mica = 2, Acrylic = 3, MicaAlt = 4 }

    public enum ButtonTone { Neutral, Danger }

    // Colors of one theme. Fields are writable, so an app can restyle Palette.Light/Palette.Dark before
    // creating its forms (e.g. Palette.Dark.Danger = Color.Orange).
    public class Palette
    {
        public Color Background;   // window background when no backdrop is available
        public Color Surface;      // SurfacePanel fill, e.g. behind a text box
        public Color Border;       // SurfacePanel border
        public Color Text;
        public Color Hover;        // translucent fill behind a hovered button
        public Color Pressed;
        public Color Accent;       // focused SurfacePanel underline, accent-colored icons
        public Color Danger;       // icon color of ButtonTone.Danger buttons; their hover fill is a faint version of it
        public Color Separator;    // lines between ButtonBar groups

        public static readonly Palette Light = new Palette
        {
            Background = Color.FromArgb(243, 243, 243),
            Surface = Color.FromArgb(253, 253, 253),
            Border = Color.FromArgb(222, 222, 222),
            Text = Color.FromArgb(26, 26, 26),
            Hover = Color.FromArgb(18, 0, 0, 0),
            Pressed = Color.FromArgb(10, 0, 0, 0),
            Accent = Color.FromArgb(0, 95, 184),
            Danger = Color.FromArgb(196, 43, 28),
            Separator = Color.FromArgb(40, 0, 0, 0),
        };

        public static readonly Palette Dark = new Palette
        {
            Background = Color.FromArgb(32, 32, 32),
            Surface = Color.FromArgb(40, 40, 40),
            Border = Color.FromArgb(60, 60, 60),
            Text = Color.FromArgb(255, 255, 255),
            Hover = Color.FromArgb(22, 255, 255, 255),
            Pressed = Color.FromArgb(12, 255, 255, 255),
            Accent = Color.FromArgb(96, 205, 255),
            Danger = Color.FromArgb(255, 120, 130),
            Separator = Color.FromArgb(45, 255, 255, 255),
        };
    }

    public static class SystemTheme
    {
        // Settings > Personalization > Colors > app mode
        public static bool AppsUseDarkTheme()
        {
            using (RegistryKey personalize = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                return personalize != null && Equals(personalize.GetValue("AppsUseLightTheme"), 0);
            }
        }

        public static bool IsDark(ThemeMode mode)
        {
            return mode == ThemeMode.Dark || (mode == ThemeMode.System && AppsUseDarkTheme());
        }

        // Dark or light scroll bars for an ordinary control (e.g. a RichTextBox); no-op on older Windows
        public static void ApplyToControl(Control control, bool dark)
        {
            string theme = dark ? "DarkMode_Explorer" : "Explorer";
            if (control.IsHandleCreated)
            {
                NativeMethods.SetWindowTheme(control.Handle, theme, null);
                return;
            }
            EventHandler handler = null;
            handler = delegate
            {
                control.HandleCreated -= handler;
                NativeMethods.SetWindowTheme(control.Handle, theme, null);
            };
            control.HandleCreated += handler;
        }
    }

    // Form with a system backdrop, a dark/light title bar and a theme that can follow Windows.
    // ThemeChanged fires whenever the theme is (re)applied, so ordinary controls can be recolored.
    public class FluentForm : Form
    {
        ThemeMode themeMode = ThemeMode.System;
        Backdrop backdrop = Backdrop.Acrylic;
        Control solidContent;
        readonly ToolTip toolTip = new ToolTip();

        public event EventHandler ThemeChanged;

        public FluentForm()
        {
            IsDark = SystemTheme.IsDark(themeMode);
            BackColor = Palette.Background;
        }

        public bool IsDark { get; private set; }

        // False until the window exists, and on Windows versions without system backdrops
        public bool BackdropActive { get; private set; }

        public Palette Palette
        {
            get { return IsDark ? Palette.Dark : Palette.Light; }
        }

        // Shared by the form's controls (IconButton.ToolTipText uses it)
        public ToolTip ToolTip
        {
            get { return toolTip; }
        }

        public ThemeMode ThemeMode
        {
            get { return themeMode; }
            set { themeMode = value; ApplyTheme(); }
        }

        public Backdrop Backdrop
        {
            get { return backdrop; }
            set { backdrop = value; ApplyTheme(); }
        }

        // The control (usually a SurfacePanel) whose rectangle is drawn opaque instead of showing the backdrop
        public Control SolidContent
        {
            get { return solidContent; }
            set
            {
                if (solidContent != null)
                {
                    solidContent.LocationChanged -= OnSolidContentMoved;
                    solidContent.SizeChanged -= OnSolidContentMoved;
                }
                solidContent = value;
                if (solidContent != null)
                {
                    solidContent.LocationChanged += OnSolidContentMoved;
                    solidContent.SizeChanged += OnSolidContentMoved;
                }
                UpdateFrame();
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyTheme();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateFrame();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) toolTip.Dispose();
            base.Dispose(disposing);
        }

        void OnSolidContentMoved(object sender, EventArgs e)
        {
            UpdateFrame();
        }

        void ApplyTheme()
        {
            IsDark = SystemTheme.IsDark(themeMode);
            if (IsHandleCreated)
            {
                // The title bar and the backdrop's tint follow this
                int dark = IsDark ? 1 : 0;
                NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, 4);
                // Fails on Windows versions before Windows 11 22H2
                int type = (int)backdrop;
                int result = NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref type, 4);
                BackdropActive = backdrop != Backdrop.None && result == 0;
                UpdateFrame();
            }
            // GDI's black has zero alpha, so it's fully transparent over the backdrop
            BackColor = BackdropActive ? Color.Black : Palette.Background;
            Invalidate(true);
            if (ThemeChanged != null) ThemeChanged(this, EventArgs.Empty);
        }

        void UpdateFrame()
        {
            if (!IsHandleCreated || WindowState == FormWindowState.Minimized) return;
            var margins = new NativeMethods.Margins();
            if (BackdropActive)
            {
                if (solidContent == null)
                {
                    // The whole client area
                    margins.Left = margins.Right = margins.Top = margins.Bottom = -1;
                }
                else
                {
                    Rectangle solid = RectangleToClient(solidContent.Parent.RectangleToScreen(solidContent.Bounds));
                    margins.Left = Math.Max(0, solid.Left);
                    margins.Top = Math.Max(0, solid.Top);
                    margins.Right = Math.Max(0, ClientSize.Width - solid.Right);
                    margins.Bottom = Math.Max(0, ClientSize.Height - solid.Bottom);
                }
            }
            NativeMethods.DwmExtendFrameIntoClientArea(Handle, ref margins);
        }
    }

    // Opaque panel for ordinary controls; set it as FluentForm.SolidContent. Draws the theme's surface color,
    // a 1-pixel border and, while a child has focus, an accent underline like Windows 11 text boxes.
    // Children with their own background (e.g. RichTextBox) should be set to Palette.Surface on ThemeChanged.
    public class SurfacePanel : Panel
    {
        public SurfacePanel()
        {
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var form = FindForm() as FluentForm;
            Palette palette = form != null ? form.Palette : Palette.Light;
            e.Graphics.Clear(palette.Surface);
            using (var pen = new Pen(palette.Border))
            {
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            }
            if (ContainsFocus)
            {
                int thickness = Math.Max(2, Padding.Bottom / 3);
                using (var brush = new SolidBrush(palette.Accent))
                {
                    e.Graphics.FillRectangle(brush, 0, Height - thickness, Width, thickness);
                }
            }
        }

        protected override void OnEnter(EventArgs e)
        {
            base.OnEnter(e);
            Invalidate();
        }

        protected override void OnLeave(EventArgs e)
        {
            base.OnLeave(e);
            Invalidate();
        }
    }

    // Base for controls drawn on the backdrop: paints into a 32-bit bitmap with GDI+ and copies it to the
    // window with its alpha channel intact. Subclasses draw in PaintContent.
    public abstract class GlassControl : Control
    {
        protected GlassControl()
        {
            // The bitmap is the double buffer; WinForms' own buffer would drop the alpha channel
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.DoubleBuffer, false);
        }

        protected FluentForm FluentForm
        {
            get { return FindForm() as FluentForm; }
        }

        protected Palette Palette
        {
            get { FluentForm form = FluentForm; return form != null ? form.Palette : Palette.Light; }
        }

        protected bool BackdropActive
        {
            get { FluentForm form = FluentForm; return form != null && form.BackdropActive; }
        }

        protected abstract void PaintContent(Graphics g);

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width <= 0 || Height <= 0) return;
            bool backdrop = BackdropActive;
            using (var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb))
            {
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.Clear(backdrop ? Color.Transparent : BackColor);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    // ClearType needs an opaque background to blend with
                    g.TextRenderingHint = backdrop ? TextRenderingHint.AntiAliasGridFit : TextRenderingHint.ClearTypeGridFit;
                    PaintContent(g);
                }
                // GetHbitmap with a transparent background color keeps the (premultiplied) alpha
                IntPtr hdc = e.Graphics.GetHdc();
                IntPtr hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
                IntPtr memDC = NativeMethods.CreateCompatibleDC(hdc);
                IntPtr old = NativeMethods.SelectObject(memDC, hBitmap);
                NativeMethods.BitBlt(hdc, 0, 0, Width, Height, memDC, 0, 0, NativeMethods.SRCCOPY);
                NativeMethods.SelectObject(memDC, old);
                NativeMethods.DeleteDC(memDC);
                NativeMethods.DeleteObject(hBitmap);
                e.Graphics.ReleaseHdc(hdc);
            }
        }
    }

    // Single-line text that sizes itself to fit
    public class GlassLabel : GlassControl
    {
        public GlassLabel()
        {
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            FitText();
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            FitText();
        }

        // Display scaling would stretch the fitted size; fit again instead
        protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
        {
            base.ScaleControl(factor, specified);
            FitText();
        }

        void FitText()
        {
            using (var bitmap = new Bitmap(1, 1))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                SizeF size = g.MeasureString(Text, Font);
                Size = new Size((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height));
            }
        }

        protected override void PaintContent(Graphics g)
        {
            using (var brush = new SolidBrush(Palette.Text))
            {
                g.DrawString(Text, Font, brush, 0, 0);
            }
        }
    }

    // Draws an icon in a 24×24 box (IconButton scales it). tint is the theme's text color, or its danger
    // color for ButtonTone.Danger buttons; colorful icons can ignore it.
    public delegate void IconPainter(Graphics g, Palette palette, Color tint);

    // Square toolbar button with an icon and a tooltip. It shows a background only while hovered or pressed.
    // Works as a form's AcceptButton/CancelButton, and sets the form's DialogResult when clicked.
    public class IconButton : GlassControl, IButtonControl
    {
        IconPainter icon;
        ButtonTone tone;
        string toolTipText;
        bool hovered, pressed;

        public IconButton()
        {
            SetStyle(ControlStyles.Selectable | ControlStyles.StandardClick, true);
            // Quick repeated clicks are separate clicks, not double clicks
            SetStyle(ControlStyles.StandardDoubleClick, false);
            Size = new Size(32, 32);
            TabStop = true;
            AccessibleRole = AccessibleRole.PushButton;
        }

        public IconPainter Icon
        {
            get { return icon; }
            set { icon = value; Invalidate(); }
        }

        public ButtonTone Tone
        {
            get { return tone; }
            set { tone = value; Invalidate(); }
        }

        // Shown on hover; also the button's accessible name
        public string ToolTipText
        {
            get { return toolTipText; }
            set { toolTipText = value; AccessibleName = value; UpdateToolTip(); }
        }

        public DialogResult DialogResult { get; set; }

        public void NotifyDefault(bool value)
        {
        }

        public void PerformClick()
        {
            if (Enabled) OnClick(EventArgs.Empty);
        }

        void UpdateToolTip()
        {
            FluentForm form = FluentForm;
            if (form != null) form.ToolTip.SetToolTip(this, toolTipText);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            UpdateToolTip();
        }

        protected override void OnClick(EventArgs e)
        {
            Form form = FindForm();
            if (form != null && DialogResult != DialogResult.None) form.DialogResult = DialogResult;
            base.OnClick(e);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            hovered = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hovered = pressed = false;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left) { pressed = false; Invalidate(); }
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (e.KeyCode == Keys.Space) PerformClick();
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            // A disabled control gets no MouseLeave
            hovered = pressed = false;
            Invalidate();
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            Invalidate();
        }

        protected override void PaintContent(Graphics g)
        {
            Palette palette = Palette;
            Color tint = tone == ButtonTone.Danger ? palette.Danger : palette.Text;
            float radius = Math.Min(Width, Height) / 8f;

            if (Enabled && (hovered || pressed))
            {
                Color fill = pressed ? palette.Pressed : palette.Hover;
                if (tone == ButtonTone.Danger) fill = Color.FromArgb(pressed ? 30 : 45, palette.Danger);
                using (GraphicsPath path = Icons.RoundedRect(new RectangleF(0, 0, Width, Height), radius))
                using (var brush = new SolidBrush(fill))
                {
                    g.FillPath(brush, path);
                }
            }
            if (Focused && ShowFocusCues)
            {
                float thickness = Math.Max(1, Height / 16f);
                using (GraphicsPath path = Icons.RoundedRect(new RectangleF(thickness / 2, thickness / 2, Width - thickness, Height - thickness), radius))
                using (var pen = new Pen(palette.Text, thickness))
                {
                    g.DrawPath(pen, path);
                }
            }
            if (icon == null) return;

            float size = Math.Min(Width, Height) * 5 / 8f;
            var box = new RectangleF((Width - size) / 2, (Height - size) / 2, size, size);
            if (Enabled)
            {
                Icons.Draw(g, icon, box, palette, tint);
                return;
            }
            // Disabled: faded and gray
            using (var layer = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb))
            {
                using (Graphics lg = Graphics.FromImage(layer))
                {
                    lg.SmoothingMode = SmoothingMode.AntiAlias;
                    lg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    Icons.Draw(lg, icon, box, palette, tint);
                }
                var matrix = new ColorMatrix(new float[][]
                {
                    new float[] { 0.3f, 0.3f, 0.3f, 0, 0 },
                    new float[] { 0.59f, 0.59f, 0.59f, 0, 0 },
                    new float[] { 0.11f, 0.11f, 0.11f, 0, 0 },
                    new float[] { 0, 0, 0, 0.4f, 0 },
                    new float[] { 0, 0, 0, 0, 1 },
                });
                using (var attributes = new ImageAttributes())
                {
                    attributes.SetColorMatrix(matrix);
                    g.DrawImage(layer, new Rectangle(0, 0, Width, Height), 0, 0, Width, Height, GraphicsUnit.Pixel, attributes);
                }
            }
        }
    }

    // Row of IconButtons in groups divided by separator lines. AddGroup groups line up from the left,
    // AddTrailingGroup groups from the right, each side in the order added. MinimumWidth is the width
    // needed to show every button without overlap, for the form's MinimumSize.
    public class ButtonBar : GlassControl
    {
        readonly List<IconButton[]> leading = new List<IconButton[]>();
        readonly List<IconButton[]> trailing = new List<IconButton[]>();
        readonly List<int> separators = new List<int>();

        public ButtonBar()
        {
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
        }

        public void AddGroup(params IconButton[] buttons)
        {
            leading.Add(buttons);
            Controls.AddRange(buttons);
        }

        public void AddTrailingGroup(params IconButton[] buttons)
        {
            trailing.Add(buttons);
            Controls.AddRange(buttons);
        }

        // Spacing scales with the buttons, so it follows display scaling
        int ButtonGap
        {
            get { return Height / 8; }
        }

        int GroupGap
        {
            get { return Height / 2; }
        }

        public int MinimumWidth
        {
            get { return SideWidth(leading) + SideWidth(trailing) + GroupGap; }
        }

        int SideWidth(List<IconButton[]> groups)
        {
            int width = 0;
            foreach (IconButton[] group in groups)
            {
                if (width > 0) width += GroupGap;
                for (int i = 0; i < group.Length; i++) width += group[i].Width + (i > 0 ? ButtonGap : 0);
            }
            return width;
        }

        // Display scaling moves the buttons after the bar laid them out, so lay out again once the window exists
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            PerformLayout();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            separators.Clear();
            Place(leading, 0);
            Place(trailing, Width - SideWidth(trailing));
            Invalidate();
        }

        void Place(List<IconButton[]> groups, int x)
        {
            for (int g = 0; g < groups.Count; g++)
            {
                if (g > 0)
                {
                    separators.Add(x + GroupGap / 2);
                    x += GroupGap;
                }
                for (int i = 0; i < groups[g].Length; i++)
                {
                    IconButton button = groups[g][i];
                    if (i > 0) x += ButtonGap;
                    button.Location = new Point(x, (Height - button.Height) / 2);
                    x += button.Width;
                }
            }
        }

        protected override void PaintContent(Graphics g)
        {
            using (var pen = new Pen(Palette.Separator))
            {
                foreach (int x in separators) g.DrawLine(pen, x, Height / 4f, x, Height * 3 / 4f);
            }
        }
    }

    // Colorful vector icons in the style of Windows 11's color icons, drawn in a 24×24 box.
    // Each one matches IconPainter, e.g. button.Icon = Icons.Globe.
    public static class Icons
    {
        // Draws an IconPainter scaled into box
        public static void Draw(Graphics g, IconPainter icon, RectangleF box, Palette palette, Color tint)
        {
            GraphicsState state = g.Save();
            g.TranslateTransform(box.X, box.Y);
            g.ScaleTransform(box.Width / 24, box.Height / 24);
            icon(g, palette, tint);
            g.Restore(state);
        }

        // Clipboard: brown board, white paper, metal clip
        public static void Clipboard(Graphics g, Palette palette, Color tint)
        {
            using (GraphicsPath board = RoundedRect(new RectangleF(4, 3.5f, 16, 19), 2.2f))
            using (var brush = new LinearGradientBrush(new PointF(0, 3), new PointF(0, 23), Color.FromArgb(222, 160, 100), Color.FromArgb(160, 98, 50)))
            {
                g.FillPath(brush, board);
            }
            using (GraphicsPath paper = RoundedRect(new RectangleF(6.5f, 6.5f, 11, 13.5f), 1))
            using (var brush = new SolidBrush(Color.FromArgb(252, 252, 252)))
            {
                g.FillPath(brush, paper);
            }
            using (var pen = new Pen(Color.FromArgb(150, 160, 175), 1.2f))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                g.DrawLine(pen, 9, 11, 15, 11);
                g.DrawLine(pen, 9, 14, 15, 14);
                g.DrawLine(pen, 9, 17, 13, 17);
            }
            using (GraphicsPath clip = RoundedRect(new RectangleF(8, 1.5f, 8, 5), 1.5f))
            using (var brush = new LinearGradientBrush(new PointF(0, 1.5f), new PointF(0, 6.5f), Color.FromArgb(236, 240, 244), Color.FromArgb(150, 158, 170)))
            using (var pen = new Pen(Color.FromArgb(105, 113, 125), 0.8f))
            {
                g.FillPath(brush, clip);
                g.DrawPath(pen, clip);
            }
        }

        // Green circle with a white check mark
        public static void Check(Graphics g, Palette palette, Color tint)
        {
            var circle = new RectangleF(2.5f, 2.5f, 19, 19);
            using (var brush = new LinearGradientBrush(circle, Color.FromArgb(80, 210, 110), Color.FromArgb(16, 140, 60), 45f))
            {
                g.FillEllipse(brush, circle);
            }
            using (var pen = new Pen(Color.White, 2.2f))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                g.DrawLines(pen, new[] { new PointF(7.5f, 12.5f), new PointF(10.5f, 15.5f), new PointF(16.5f, 9) });
            }
        }

        // Arrow curving back to the left
        public static void Undo(Graphics g, Palette palette, Color tint)
        {
            using (var pen = new Pen(palette.Accent, 2.4f))
            using (var path = new GraphicsPath())
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                path.AddLine(5, 9, 14, 9);
                path.AddArc(8.5f, 9, 11, 11, 270, 180);
                path.AddLine(14, 20, 9, 20);
                g.DrawPath(pen, path);
                g.DrawLines(pen, new[] { new PointF(9, 5), new PointF(5, 9), new PointF(9, 13) });
            }
        }

        // Arrow curving forward to the right
        public static void Redo(Graphics g, Palette palette, Color tint)
        {
            g.TranslateTransform(24, 0);
            g.ScaleTransform(-1, 1);
            Undo(g, palette, tint);
        }

        public static void Question(Graphics g, Palette palette, Color tint)
        {
            Glyph(g, "?", tint);
        }

        public static void Hash(Graphics g, Palette palette, Color tint)
        {
            Glyph(g, "#", tint);
        }

        // Text drawn as a bold vector shape, centered in the box
        public static void Glyph(Graphics g, string text, Color color)
        {
            using (var family = new FontFamily("Segoe UI"))
            using (var path = new GraphicsPath())
            using (var matrix = new Matrix())
            using (var brush = new SolidBrush(color))
            {
                path.AddString(text, family, (int)FontStyle.Bold, 22, PointF.Empty, StringFormat.GenericTypographic);
                RectangleF bounds = path.GetBounds();
                matrix.Translate(12 - (bounds.X + bounds.Width / 2), 12 - (bounds.Y + bounds.Height / 2));
                path.Transform(matrix);
                g.FillPath(brush, path);
            }
        }

        // Red circle with a slash (prohibited)
        public static void Cancel(Graphics g, Palette palette, Color tint)
        {
            using (var pen = new Pen(Color.FromArgb(232, 40, 50), 2.6f))
            {
                g.DrawEllipse(pen, 3.5f, 3.5f, 17, 17);
                g.DrawLine(pen, 6, 6, 18, 18);
            }
        }

        // Blue globe with meridians and latitude lines
        public static void Globe(Graphics g, Palette palette, Color tint)
        {
            var circle = new RectangleF(2.5f, 2.5f, 19, 19);
            using (var brush = new LinearGradientBrush(circle, Color.FromArgb(90, 200, 255), Color.FromArgb(0, 95, 200), 45f))
            {
                g.FillEllipse(brush, circle);
            }
            using (var pen = new Pen(Color.FromArgb(225, 255, 255, 255), 1.3f))
            {
                g.DrawEllipse(pen, 8, 2.5f, 8, 19);
                g.DrawLine(pen, 12, 2.5f, 12, 21.5f);
                g.DrawLine(pen, 2.5f, 12, 21.5f, 12);
                g.DrawLine(pen, 3.9f, 7.5f, 20.1f, 7.5f);
                g.DrawLine(pen, 3.9f, 16.5f, 20.1f, 16.5f);
            }
        }

        // Desktop monitor with a blue screen
        public static void Monitor(Graphics g, Palette palette, Color tint)
        {
            using (var brush = new SolidBrush(Color.FromArgb(150, 158, 170)))
            {
                g.FillRectangle(brush, 10.5f, 17, 3, 3);
                using (GraphicsPath stand = RoundedRect(new RectangleF(7, 19.5f, 10, 2.5f), 1.2f)) g.FillPath(brush, stand);
            }
            using (GraphicsPath frame = RoundedRect(new RectangleF(2, 3.5f, 20, 14), 2))
            using (var brush = new LinearGradientBrush(new PointF(0, 3.5f), new PointF(0, 17.5f), Color.FromArgb(120, 128, 140), Color.FromArgb(80, 88, 100)))
            {
                g.FillPath(brush, frame);
            }
            using (GraphicsPath screen = RoundedRect(new RectangleF(3.5f, 5, 17, 11), 1))
            using (var brush = new LinearGradientBrush(new RectangleF(3.5f, 5, 17, 11), Color.FromArgb(90, 200, 255), Color.FromArgb(0, 95, 200), 45f))
            {
                g.FillPath(brush, screen);
            }
        }

        // Yellow sun with rays
        public static void Sun(Graphics g, Palette palette, Color tint)
        {
            using (var pen = new Pen(Color.FromArgb(255, 170, 0), 2))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                for (int i = 0; i < 8; i++)
                {
                    double angle = i * Math.PI / 4;
                    float cos = (float)Math.Cos(angle), sin = (float)Math.Sin(angle);
                    g.DrawLine(pen, 12 + cos * 7.4f, 12 + sin * 7.4f, 12 + cos * 10, 12 + sin * 10);
                }
            }
            var disc = new RectangleF(7, 7, 10, 10);
            using (var brush = new LinearGradientBrush(disc, Color.FromArgb(255, 220, 70), Color.FromArgb(255, 150, 0), 45f))
            using (var pen = new Pen(Color.FromArgb(225, 135, 0), 0.8f))
            {
                g.FillEllipse(brush, disc);
                g.DrawEllipse(pen, disc);
            }
        }

        // Yellow crescent moon
        public static void Moon(Graphics g, Palette palette, Color tint)
        {
            using (GraphicsPath crescent = Crescent(new PointF(12, 12), 9, new PointF(16.5f, 8), 7.5f))
            using (var brush = new LinearGradientBrush(new RectangleF(3, 3, 18, 18), Color.FromArgb(255, 220, 70), Color.FromArgb(255, 150, 0), 45f))
            using (var pen = new Pen(Color.FromArgb(225, 135, 0), 0.8f))
            {
                g.FillPath(brush, crescent);
                g.DrawPath(pen, crescent);
            }
        }

        public static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0)
            {
                path.AddRectangle(r);
                return path;
            }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // The part of circle 1 not covered by circle 2; the circles must overlap partially.
        // Built from arcs because GDI+ regions (for subtracting shapes) aren't antialiased.
        public static GraphicsPath Crescent(PointF c1, float r1, PointF c2, float r2)
        {
            double dx = c2.X - c1.X, dy = c2.Y - c1.Y, d = Math.Sqrt(dx * dx + dy * dy);
            double a = (r1 * r1 - r2 * r2 + d * d) / (2 * d), h = Math.Sqrt(r1 * r1 - a * a);
            double mx = c1.X + a * dx / d, my = c1.Y + a * dy / d;
            var p1 = new PointF((float)(mx + h * dy / d), (float)(my - h * dx / d));
            var p2 = new PointF((float)(mx - h * dy / d), (float)(my + h * dx / d));
            float towardC2 = Angle(c1, c2);

            // Outer edge: circle 1 from p1 to p2 around the side away from circle 2
            float start1 = Angle(c1, p1);
            float sweep1 = Normalize(Angle(c1, p2) - start1);
            if (Normalize(towardC2 - start1) < sweep1) sweep1 -= 360;
            // Inner edge: circle 2 from p2 back to p1 along the side facing circle 1
            float start2 = Angle(c2, p2);
            float sweep2 = Normalize(Angle(c2, p1) - start2);
            if (Normalize(towardC2 + 180 - start2) >= sweep2) sweep2 -= 360;

            var path = new GraphicsPath();
            path.AddArc(c1.X - r1, c1.Y - r1, 2 * r1, 2 * r1, start1, sweep1);
            path.AddArc(c2.X - r2, c2.Y - r2, 2 * r2, 2 * r2, start2, sweep2);
            path.CloseFigure();
            return path;
        }

        static float Angle(PointF from, PointF to)
        {
            return (float)(Math.Atan2(to.Y - from.Y, to.X - from.X) * 180 / Math.PI);
        }

        static float Normalize(float degrees)
        {
            degrees %= 360;
            return degrees < 0 ? degrees + 360 : degrees;
        }
    }

    // Flat key/value settings in a JSON file. Never throws: a missing, unreadable or read-only file
    // just means defaults are used and changes aren't kept.
    public class JsonSettings
    {
        readonly string path;
        readonly Dictionary<string, object> values;

        public JsonSettings(string path)
        {
            this.path = path;
            try
            {
                values = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
            }
            catch (Exception)
            {
            }
            if (values == null) values = new Dictionary<string, object>();
        }

        // Named after the executable, in its folder: C:\Apps\Tool.exe -> C:\Apps\Tool.json
        public static JsonSettings NextToExecutable()
        {
            return new JsonSettings(Path.ChangeExtension(Application.ExecutablePath, ".json"));
        }

        public string GetString(string key, string fallback)
        {
            object value;
            return values.TryGetValue(key, out value) && value is string ? (string)value : fallback;
        }

        public T GetEnum<T>(string key, T fallback) where T : struct
        {
            T result;
            string text = GetString(key, null);
            return text != null && Enum.TryParse(text, true, out result) && Enum.IsDefined(typeof(T), result) ? result : fallback;
        }

        // Saves right away. Enums are stored by name.
        public void Set(string key, object value)
        {
            values[key] = value is Enum ? value.ToString() : value;
            try
            {
                File.WriteAllText(path, new JavaScriptSerializer().Serialize(values));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    static class NativeMethods
    {
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("dwmapi.dll")] public static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] public static extern int SetWindowTheme(IntPtr hwnd, string appName, string idList);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr hdc, int x, int y, int width, int height, IntPtr srcDC, int srcX, int srcY, int rop);

        [StructLayout(LayoutKind.Sequential)]
        public struct Margins
        {
            public int Left, Right, Top, Bottom;
        }

        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
        public const int SRCCOPY = 0x00CC0020;
    }
}
