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
// The system backdrop can't be tinted, so the form and its GlassControls paint Palette.BackdropTint (a
// translucent color) over it instead; the title bar, drawn by Windows, stays untinted. Blend modes other
// than Normal are approximated: the window can only alpha-blend over the backdrop, so the tint is blended
// with Palette.BackdropBase (the backdrop's typical color) and the result painted at the tint's opacity.
//
// Controls with text input (TextField, NumberBox, SliderField, ColorPicker) contain TextBoxes, so they
// belong on opaque forms such as a Flyout.
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

    // How Palette.BackdropTint mixes with the backdrop (as in image editors)
    public enum TintBlend { Normal, Overlay }

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
        public Color BackdropTint; // painted over the backdrop; its alpha is the tint's opacity (0 = none)
        public TintBlend TintBlend;
        public Color BackdropBase; // roughly what the system backdrop looks like, for blend modes other than Normal

        // BackdropTint's opacity as a percentage
        public int TintOpacity
        {
            get { return (int)Math.Round(BackdropTint.A * 100 / 255.0); }
            set { BackdropTint = Color.FromArgb((int)Math.Round(Math.Max(0, Math.Min(100, value)) * 255 / 100.0), BackdropTint); }
        }

        // The color actually painted over the backdrop: BackdropTint blended with BackdropBase, at
        // BackdropTint's opacity
        public Color GlassTint
        {
            get
            {
                if (TintBlend == TintBlend.Normal) return BackdropTint;
                return Color.FromArgb(BackdropTint.A,
                    Overlay(BackdropBase.R, BackdropTint.R),
                    Overlay(BackdropBase.G, BackdropTint.G),
                    Overlay(BackdropBase.B, BackdropTint.B));
            }
        }

        // Darkens dark backgrounds and lightens light ones by the blend color, keeping their contrast
        static int Overlay(int background, int blend)
        {
            double b = background / 255.0, t = blend / 255.0;
            double result = b <= 0.5 ? 2 * b * t : 1 - 2 * (1 - b) * (1 - t);
            return (int)Math.Round(result * 255);
        }

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
            BackdropTint = Color.FromArgb(0, 243, 243, 243),
            BackdropBase = Color.FromArgb(220, 220, 220),
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
            BackdropTint = Color.FromArgb(0, 32, 32, 32),
            BackdropBase = Color.FromArgb(44, 44, 44),
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

    // Controls that restyle themselves when their FluentForm applies a theme
    public interface IThemeAware
    {
        void ApplyTheme(FluentForm form);
    }

    // Sizes for form layouts, derived from the font so they follow display scaling
    public static class Metrics
    {
        public static int Row(Font font) { return font.Height * 2; }
        public static int FieldHeight(Font font) { return font.Height * 7 / 4; }
        public static int LabelColumn(Font font) { return font.Height * 3 / 2; }
        public static int NumberColumn(Font font) { return font.Height * 9 / 2; }
        public static int Gap(Font font) { return font.Height / 2; }
    }

    // Paints with GDI+ into a 32-bit bitmap and copies it to the window with its alpha channel, which
    // ordinary WinForms drawing drops
    public static class AlphaPaint
    {
        public static void Paint(Graphics target, Size size, Color background, Action<Graphics> draw)
        {
            if (size.Width <= 0 || size.Height <= 0) return;
            using (var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb))
            {
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.Clear(background);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    // ClearType needs an opaque background to blend with
                    g.TextRenderingHint = background.A == 255 ? TextRenderingHint.ClearTypeGridFit : TextRenderingHint.AntiAliasGridFit;
                    if (draw != null) draw(g);
                }
                // GetHbitmap with a transparent background color keeps the (premultiplied) alpha
                IntPtr hdc = target.GetHdc();
                IntPtr hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
                IntPtr memDC = NativeMethods.CreateCompatibleDC(hdc);
                IntPtr old = NativeMethods.SelectObject(memDC, hBitmap);
                NativeMethods.BitBlt(hdc, 0, 0, size.Width, size.Height, memDC, 0, 0, NativeMethods.SRCCOPY);
                NativeMethods.SelectObject(memDC, old);
                NativeMethods.DeleteDC(memDC);
                NativeMethods.DeleteObject(hBitmap);
                target.ReleaseHdc(hdc);
            }
        }
    }

    // Form with a system backdrop, a dark/light title bar and a theme that can follow Windows.
    // ThemeChanged fires whenever the theme is (re)applied, so ordinary controls can be recolored.
    // After changing Palette.BackdropTint, call Invalidate(true) to repaint.
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

        // What the form and its GlassControls fill their background with
        public Color GlassFill
        {
            get { return BackdropActive ? Palette.GlassTint : Palette.Background; }
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

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (BackdropActive) AlphaPaint.Paint(e.Graphics, ClientSize, GlassFill, null);
            else base.OnPaintBackground(e);
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
            ApplyThemeTo(Controls);
            Invalidate(true);
            if (ThemeChanged != null) ThemeChanged(this, EventArgs.Empty);
        }

        void ApplyThemeTo(Control.ControlCollection controls)
        {
            foreach (Control control in controls)
            {
                var aware = control as IThemeAware;
                if (aware != null) aware.ApplyTheme(this);
                ApplyThemeTo(control.Controls);
            }
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
    // window with its alpha channel intact. Subclasses draw in PaintContent and lay out children in OnLayout.
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

        protected abstract void PaintContent(Graphics g);

        // Display scaling moves and resizes children after the constructor laid them out, so lay out again
        // once the window exists
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            PerformLayout();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            FluentForm form = FluentForm;
            AlphaPaint.Paint(e.Graphics, Size, form != null ? form.GlassFill : BackColor, PaintContent);
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

        // Artist's palette with dabs of paint
        public static void ArtistPalette(Graphics g, Palette palette, Color tint)
        {
            // The thumb hole is cut out by the alternate fill mode
            using (var board = new GraphicsPath(FillMode.Alternate))
            using (var brush = new LinearGradientBrush(new RectangleF(2, 3.5f, 20, 17), Color.FromArgb(244, 218, 172), Color.FromArgb(206, 160, 100), 45f))
            using (var pen = new Pen(Color.FromArgb(170, 120, 65), 0.8f))
            {
                board.AddEllipse(2, 3.5f, 20, 17);
                board.AddEllipse(13.5f, 13, 4.5f, 4.5f);
                g.FillPath(brush, board);
                g.DrawPath(pen, board);
            }
            Dab(g, Color.FromArgb(232, 40, 50), 6.5f, 8.5f);
            Dab(g, Color.FromArgb(255, 190, 0), 11.5f, 6.8f);
            Dab(g, Color.FromArgb(40, 180, 90), 16.5f, 8.5f);
            Dab(g, Color.FromArgb(0, 120, 215), 6.8f, 14);
        }

        static void Dab(Graphics g, Color color, float x, float y)
        {
            using (var brush = new SolidBrush(color))
            {
                g.FillEllipse(brush, x - 2.1f, y - 2.1f, 4.2f, 4.2f);
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

    // Single-line text input: a borderless TextBox in a SurfacePanel, restyled with the theme.
    // Use Box for the text and its events. Needs an opaque area (see the top of this file).
    public class TextField : SurfacePanel, IThemeAware
    {
        readonly TextBox box = new TextBox { BorderStyle = BorderStyle.None };

        public TextField()
        {
            Controls.Add(box);
        }

        public TextBox Box
        {
            get { return box; }
        }

        public void ApplyTheme(FluentForm form)
        {
            box.BackColor = form.Palette.Surface;
            box.ForeColor = form.Palette.Text;
            Invalidate();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int padding = Font.Height / 3;
            box.SetBounds(padding, (Height - box.Height) / 2, Math.Max(0, Width - 2 * padding), box.Height);
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            PerformLayout();
        }

        // Clicking the padding around the text focuses it too
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            box.Focus();
        }
    }

    // Whole-number input limited to Minimum..Maximum. Typing a valid number applies it right away; anything
    // else is corrected on Enter or when leaving. Up/Down, Page Up/Down and the mouse wheel step the value.
    // Setting Value doesn't raise ValueChanged; only user edits do.
    public class NumberBox : TextField
    {
        int minimum, maximum = 100, current;
        bool showing;

        public event EventHandler ValueChanged;

        public NumberBox()
        {
            Box.TextAlign = HorizontalAlignment.Center;
            Box.Text = "0";
            Box.KeyPress += OnBoxKeyPress;
            Box.KeyDown += OnBoxKeyDown;
            Box.TextChanged += OnBoxTextChanged;
            Box.Leave += delegate { Commit(); };
            Box.MouseWheel += delegate(object sender, MouseEventArgs e) { SetValue(current + Math.Sign(e.Delta), true); };
        }

        public int Minimum
        {
            get { return minimum; }
            set { minimum = value; maximum = Math.Max(maximum, value); SetValue(current, false); }
        }

        public int Maximum
        {
            get { return maximum; }
            set { maximum = value; minimum = Math.Min(minimum, value); SetValue(current, false); }
        }

        public int Value
        {
            get { return current; }
            set { SetValue(value, false); }
        }

        void SetValue(int value, bool raise)
        {
            value = Math.Max(minimum, Math.Min(maximum, value));
            bool changed = value != current;
            current = value;
            ShowValue();
            if (changed && raise && ValueChanged != null) ValueChanged(this, EventArgs.Empty);
        }

        void ShowValue()
        {
            string text = current.ToString();
            if (Box.Text == text) return;
            showing = true;
            Box.Text = text;
            Box.SelectionStart = text.Length;
            showing = false;
        }

        void Commit()
        {
            int parsed;
            if (int.TryParse(Box.Text, out parsed)) SetValue(parsed, true);
            else ShowValue();
        }

        void OnBoxTextChanged(object sender, EventArgs e)
        {
            int parsed;
            if (showing || !int.TryParse(Box.Text, out parsed) || parsed < minimum || parsed > maximum || parsed == current) return;
            current = parsed;
            if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
        }

        void OnBoxKeyPress(object sender, KeyPressEventArgs e)
        {
            bool allowed = char.IsDigit(e.KeyChar) || char.IsControl(e.KeyChar) || (e.KeyChar == '-' && minimum < 0);
            if (!allowed) e.Handled = true;
        }

        void OnBoxKeyDown(object sender, KeyEventArgs e)
        {
            int step = 0;
            if (e.KeyCode == Keys.Up) step = 1;
            else if (e.KeyCode == Keys.Down) step = -1;
            else if (e.KeyCode == Keys.PageUp) step = 10;
            else if (e.KeyCode == Keys.PageDown) step = -10;
            else if (e.KeyCode == Keys.Enter)
            {
                // Also stops the error beep
                e.SuppressKeyPress = true;
                Commit();
                return;
            }
            if (step == 0) return;
            e.Handled = true;
            SetValue(current + step, true);
        }
    }

    // Horizontal slider in the Windows 11 style: accent fill up to a round thumb. With TrackColor set, the
    // track instead shows a color gradient (e.g. a hue rainbow) and the thumb's center shows the picked color.
    // Setting Value doesn't raise ValueChanged; only user edits do.
    public class GlassSlider : GlassControl
    {
        int minimum, maximum = 100, current;
        bool hovered, dragging;
        Func<float, Color> trackColor;

        public event EventHandler ValueChanged;

        public GlassSlider()
        {
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
            Size = new Size(160, 32);
        }

        public int Minimum
        {
            get { return minimum; }
            set { minimum = value; maximum = Math.Max(maximum, value); SetValue(current, false); Invalidate(); }
        }

        public int Maximum
        {
            get { return maximum; }
            set { maximum = value; minimum = Math.Min(minimum, value); SetValue(current, false); Invalidate(); }
        }

        public int Value
        {
            get { return current; }
            set { SetValue(value, false); }
        }

        // Color at a position along the track, from 0 (left) to 1 (right); null for the accent style
        public Func<float, Color> TrackColor
        {
            get { return trackColor; }
            set { trackColor = value; Invalidate(); }
        }

        float ThumbRadius
        {
            get { return Height * 0.3f; }
        }

        float TrackLeft
        {
            get { return ThumbRadius + 1; }
        }

        float TrackRight
        {
            get { return Width - ThumbRadius - 1; }
        }

        void SetValue(int value, bool raise)
        {
            value = Math.Max(minimum, Math.Min(maximum, value));
            if (value == current) return;
            current = value;
            Invalidate();
            if (raise && ValueChanged != null) ValueChanged(this, EventArgs.Empty);
        }

        int ValueAt(int x)
        {
            float length = TrackRight - TrackLeft;
            float t = length > 0 ? Math.Max(0, Math.Min(1, (x - TrackLeft) / length)) : 0;
            return minimum + (int)Math.Round(t * (maximum - minimum));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            dragging = true;
            SetValue(ValueAt(e.X), true);
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging) SetValue(ValueAt(e.X), true);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            dragging = false;
            Invalidate();
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
            hovered = false;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            SetValue(current + Math.Sign(e.Delta), true);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            return key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            int page = Math.Max(1, (maximum - minimum) / 10);
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Down) SetValue(current - 1, true);
            else if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Up) SetValue(current + 1, true);
            else if (e.KeyCode == Keys.PageDown) SetValue(current - page, true);
            else if (e.KeyCode == Keys.PageUp) SetValue(current + page, true);
            else if (e.KeyCode == Keys.Home) SetValue(minimum, true);
            else if (e.KeyCode == Keys.End) SetValue(maximum, true);
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
            float radius = ThumbRadius, left = TrackLeft, right = TrackRight, middle = Height / 2f;
            float t = maximum > minimum ? (float)(current - minimum) / (maximum - minimum) : 0;
            float x = left + t * (right - left);

            if (trackColor != null)
            {
                float thickness = radius * 1.1f;
                var track = new RectangleF(left - thickness / 2, middle - thickness / 2, right - left + thickness, thickness);
                using (GraphicsPath path = Icons.RoundedRect(track, thickness / 2))
                using (var brush = new LinearGradientBrush(new PointF(left, 0), new PointF(right, 0), Color.Black, Color.White))
                using (var pen = new Pen(palette.Separator))
                {
                    var blend = new ColorBlend(17);
                    for (int i = 0; i < 17; i++)
                    {
                        blend.Positions[i] = i / 16f;
                        blend.Colors[i] = trackColor(i / 16f);
                    }
                    brush.InterpolationColors = blend;
                    // The rounded ends reach past the gradient; mirror it there instead of repeating it
                    brush.WrapMode = WrapMode.TileFlipX;
                    g.FillPath(brush, path);
                    g.DrawPath(pen, path);
                }
            }
            else
            {
                float thickness = Math.Max(2, radius * 0.45f);
                using (GraphicsPath rest = Icons.RoundedRect(new RectangleF(left, middle - thickness / 2, right - left, thickness), thickness / 2))
                using (GraphicsPath filled = Icons.RoundedRect(new RectangleF(left, middle - thickness / 2, x - left, thickness), thickness / 2))
                using (var restBrush = new SolidBrush(Color.FromArgb(115, palette.Text)))
                using (var fillBrush = new SolidBrush(palette.Accent))
                {
                    g.FillPath(restBrush, rest);
                    g.FillPath(fillBrush, filled);
                }
            }

            // Thumb: a surface-colored disc whose center dot grows on hover and shrinks while dragging
            float dot = radius * (dragging ? 0.45f : hovered ? 0.62f : 0.52f);
            using (var surface = new SolidBrush(palette.Surface))
            using (var border = new Pen(palette.Border))
            using (var center = new SolidBrush(trackColor != null ? trackColor(t) : palette.Accent))
            {
                g.FillEllipse(surface, x - radius, middle - radius, 2 * radius, 2 * radius);
                g.DrawEllipse(border, x - radius, middle - radius, 2 * radius, 2 * radius);
                g.FillEllipse(center, x - dot, middle - dot, 2 * dot, 2 * dot);
            }
            if (Focused && ShowFocusCues)
            {
                float ring = radius + 2;
                using (var pen = new Pen(palette.Text, 1.5f))
                {
                    g.DrawEllipse(pen, x - ring, middle - ring, 2 * ring, 2 * ring);
                }
            }
        }
    }

    // A row with a label (Text), a slider and a number box kept in sync, laid out in the Metrics columns.
    // Setting Value doesn't raise ValueChanged; only user edits do.
    public class SliderField : GlassControl
    {
        readonly GlassSlider slider = new GlassSlider();
        readonly NumberBox numberBox = new NumberBox();
        bool syncing;

        public event EventHandler ValueChanged;

        public SliderField()
        {
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            Controls.Add(slider);
            Controls.Add(numberBox);
            slider.ValueChanged += delegate { Sync(slider.Value); };
            numberBox.ValueChanged += delegate { Sync(numberBox.Value); };
        }

        public int Minimum
        {
            get { return slider.Minimum; }
            set { slider.Minimum = value; numberBox.Minimum = value; }
        }

        public int Maximum
        {
            get { return slider.Maximum; }
            set { slider.Maximum = value; numberBox.Maximum = value; }
        }

        public int Value
        {
            get { return slider.Value; }
            set { slider.Value = value; numberBox.Value = value; }
        }

        public Func<float, Color> TrackColor
        {
            get { return slider.TrackColor; }
            set { slider.TrackColor = value; }
        }

        // Repaints the track after the colors TrackColor returns have changed
        public void RefreshTrack()
        {
            slider.Invalidate();
        }

        void Sync(int value)
        {
            if (syncing) return;
            syncing = true;
            slider.Value = value;
            numberBox.Value = value;
            syncing = false;
            if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            Invalidate();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int label = Metrics.LabelColumn(Font), number = Metrics.NumberColumn(Font), gap = Metrics.Gap(Font);
            int field = Math.Min(Height, Metrics.FieldHeight(Font));
            numberBox.SetBounds(Width - number, (Height - field) / 2, number, field);
            slider.SetBounds(label, 0, Math.Max(0, Width - label - number - gap), Height);
        }

        protected override void PaintContent(Graphics g)
        {
            using (var brush = new SolidBrush(Palette.Text))
            using (var format = new StringFormat { LineAlignment = StringAlignment.Center })
            {
                g.DrawString(Text, Font, brush, new RectangleF(0, 0, Metrics.LabelColumn(Font), Height), format);
            }
        }
    }

    // Equal-width text tabs; the selected one gets a subtle fill and an accent underline
    public class TabStrip : GlassControl
    {
        string[] items = new string[0];
        int selectedIndex, hoverIndex = -1;

        public event EventHandler SelectedIndexChanged;

        public TabStrip()
        {
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
        }

        public void SetItems(params string[] names)
        {
            items = names;
            selectedIndex = 0;
            Invalidate();
        }

        public int SelectedIndex
        {
            get { return selectedIndex; }
            set
            {
                value = Math.Max(0, Math.Min(items.Length - 1, value));
                if (value == selectedIndex) return;
                selectedIndex = value;
                Invalidate();
                if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
        }

        RectangleF TabBounds(int index)
        {
            float width = (float)Width / items.Length;
            return new RectangleF(index * width, 0, width, Height);
        }

        int IndexAt(Point point)
        {
            if (items.Length == 0 || !ClientRectangle.Contains(point)) return -1;
            return Math.Min(items.Length - 1, point.X * items.Length / Width);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int index = IndexAt(e.Location);
            if (index != hoverIndex) { hoverIndex = index; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hoverIndex = -1;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            int index = IndexAt(e.Location);
            if (e.Button == MouseButtons.Left && index >= 0) SelectedIndex = index;
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            return key == Keys.Left || key == Keys.Right || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Left) SelectedIndex--;
            else if (e.KeyCode == Keys.Right) SelectedIndex++;
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
            float radius = Height / 8f;
            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                for (int i = 0; i < items.Length; i++)
                {
                    RectangleF tab = TabBounds(i);
                    tab.Inflate(-2, -2);
                    if (i == selectedIndex || i == hoverIndex)
                    {
                        using (GraphicsPath path = Icons.RoundedRect(tab, radius))
                        using (var brush = new SolidBrush(palette.Hover))
                        {
                            g.FillPath(brush, path);
                        }
                    }
                    using (var brush = new SolidBrush(i == selectedIndex ? palette.Text : Color.FromArgb(170, palette.Text)))
                    {
                        g.DrawString(items[i], Font, brush, tab, format);
                    }
                    if (i == selectedIndex)
                    {
                        float width = Math.Min(tab.Width / 3, Height * 0.6f), thickness = Math.Max(2, Height / 10f);
                        var pill = new RectangleF(tab.X + (tab.Width - width) / 2, tab.Bottom - thickness - 1, width, thickness);
                        using (GraphicsPath path = Icons.RoundedRect(pill, thickness / 2))
                        using (var brush = new SolidBrush(palette.Accent))
                        {
                            g.FillPath(brush, path);
                        }
                        if (Focused && ShowFocusCues)
                        {
                            using (GraphicsPath path = Icons.RoundedRect(tab, radius))
                            using (var pen = new Pen(palette.Text, 1.5f))
                            {
                                g.DrawPath(pen, path);
                            }
                        }
                    }
                }
            }
        }
    }

    // Color editor: RGB and HSV tabs with a SliderField per channel (each track previews its channel's
    // colors), a swatch and an editable hex value. Hue and saturation are kept separately from the RGB
    // color, so they survive passing through grays and black. Size it with PreferredHeight.
    public class ColorPicker : GlassControl
    {
        readonly TabStrip tabs = new TabStrip();
        readonly SliderField[] channels = { new SliderField(), new SliderField(), new SliderField() };
        readonly TextField hex = new TextField();
        Rectangle swatch;
        Color color = Color.White;
        double hue, saturation, brightness = 1;
        bool loading;

        public event EventHandler ColorChanged;

        public ColorPicker()
        {
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            tabs.SetItems("RGB", "HSV");
            tabs.SelectedIndexChanged += delegate { LoadChannels(); };
            Controls.Add(tabs);
            foreach (SliderField channel in channels)
            {
                channel.ValueChanged += OnChannelChanged;
                Controls.Add(channel);
            }
            hex.Box.MaxLength = 7;
            hex.Box.TextAlign = HorizontalAlignment.Center;
            hex.Box.TextChanged += OnHexTextChanged;
            hex.Box.Leave += delegate { ShowHex(); };
            hex.Box.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;
                ShowHex();
            };
            Controls.Add(hex);
            LoadChannels();
            ShowHex();
        }

        // Always opaque; the alpha channel is ignored
        public Color Color
        {
            get { return color; }
            set { Apply(value, true, true); }
        }

        public int PreferredHeight
        {
            get { return Metrics.Row(Font) * 5 + Metrics.Gap(Font) * 2; }
        }

        bool IsHsv
        {
            get { return tabs.SelectedIndex == 1; }
        }

        public static string ToHex(Color color)
        {
            return "#" + color.R.ToString("X2") + color.G.ToString("X2") + color.B.ToString("X2");
        }

        // Accepts #RRGGBB or #RGB, with or without the #
        public static bool TryParseHex(string text, out Color color)
        {
            color = Color.Empty;
            string digits = (text ?? "").Trim().TrimStart('#');
            if (digits.Length == 3) digits = new string(new[] { digits[0], digits[0], digits[1], digits[1], digits[2], digits[2] });
            int value;
            if (digits.Length != 6 || !int.TryParse(digits, System.Globalization.NumberStyles.HexNumber, null, out value)) return false;
            color = Color.FromArgb(255, Color.FromArgb(value));
            return true;
        }

        public static Color FromHsv(double hue, double saturation, double brightness)
        {
            double h = (hue % 360 + 360) % 360 / 60;
            int sector = (int)Math.Floor(h);
            double f = h - sector, v = brightness;
            double p = v * (1 - saturation), q = v * (1 - saturation * f), t = v * (1 - saturation * (1 - f));
            double r, g, b;
            switch (sector)
            {
                case 0: r = v; g = t; b = p; break;
                case 1: r = q; g = v; b = p; break;
                case 2: r = p; g = v; b = t; break;
                case 3: r = p; g = q; b = v; break;
                case 4: r = t; g = p; b = v; break;
                default: r = v; g = p; b = q; break;
            }
            return Color.FromArgb(To255(r), To255(g), To255(b));
        }

        static int To255(double fraction)
        {
            return (int)Math.Round(Math.Max(0, Math.Min(1, fraction)) * 255);
        }

        // Grays keep the current hue, and black keeps the current hue and saturation
        void UpdateHsv()
        {
            double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), delta = max - min;
            brightness = max;
            if (max > 0) saturation = delta / max;
            if (delta > 0)
            {
                double h = max == r ? (g - b) / delta : max == g ? 2 + (b - r) / delta : 4 + (r - g) / delta;
                hue = (h * 60 + 360) % 360;
            }
        }

        // updateHsv is false when hue/saturation/brightness were just edited and produced this color
        void Apply(Color value, bool updateHsv, bool showHex)
        {
            value = Color.FromArgb(255, value);
            bool changed = value.ToArgb() != color.ToArgb();
            color = value;
            if (updateHsv) UpdateHsv();
            LoadChannels();
            if (showHex) ShowHex();
            Invalidate();
            if (changed && ColorChanged != null) ColorChanged(this, EventArgs.Empty);
        }

        void LoadChannels()
        {
            loading = true;
            string[] names = IsHsv ? new[] { "H", "S", "V" } : new[] { "R", "G", "B" };
            int[] maximums = IsHsv ? new[] { 360, 100, 100 } : new[] { 255, 255, 255 };
            int[] values = IsHsv
                ? new[] { (int)Math.Round(hue), (int)Math.Round(saturation * 100), (int)Math.Round(brightness * 100) }
                : new int[] { color.R, color.G, color.B };
            for (int i = 0; i < 3; i++)
            {
                channels[i].Text = names[i];
                channels[i].Maximum = maximums[i];
                channels[i].Value = values[i];
                channels[i].TrackColor = TrackFor(i);
                channels[i].RefreshTrack();
            }
            loading = false;
        }

        // The colors each channel's track shows: the current color with that channel swept from 0 to max
        Func<float, Color> TrackFor(int channel)
        {
            if (IsHsv)
            {
                if (channel == 0) return t => FromHsv(t * 360, 1, 1);
                if (channel == 1) return t => FromHsv(hue, t, brightness);
                return t => FromHsv(hue, saturation, t);
            }
            if (channel == 0) return t => Color.FromArgb(To255(t), color.G, color.B);
            if (channel == 1) return t => Color.FromArgb(color.R, To255(t), color.B);
            return t => Color.FromArgb(color.R, color.G, To255(t));
        }

        void OnChannelChanged(object sender, EventArgs e)
        {
            if (loading) return;
            if (IsHsv)
            {
                hue = channels[0].Value;
                saturation = channels[1].Value / 100.0;
                brightness = channels[2].Value / 100.0;
                Apply(FromHsv(hue, saturation, brightness), false, true);
            }
            else
            {
                Apply(Color.FromArgb(channels[0].Value, channels[1].Value, channels[2].Value), true, true);
            }
        }

        void OnHexTextChanged(object sender, EventArgs e)
        {
            Color parsed;
            // Applies as soon as the text is a complete color; the text itself is tidied up on Enter or leaving
            if (!loading && TryParseHex(hex.Box.Text, out parsed)) Apply(parsed, true, false);
        }

        void ShowHex()
        {
            loading = true;
            hex.Box.Text = ToHex(color);
            loading = false;
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int row = Metrics.Row(Font), gap = Metrics.Gap(Font), number = Metrics.NumberColumn(Font), field = Metrics.FieldHeight(Font);
            tabs.SetBounds(0, 0, Width, row);
            int y = row + gap;
            foreach (SliderField channel in channels)
            {
                channel.SetBounds(0, y, Width, row);
                y += row;
            }
            y += gap + (row - field) / 2;
            hex.SetBounds(Width - number, y, number, field);
            swatch = new Rectangle(0, y, Math.Max(0, Width - number - gap), field);
        }

        protected override void PaintContent(Graphics g)
        {
            using (GraphicsPath path = Icons.RoundedRect(swatch, Height / 60f + 3))
            using (var brush = new SolidBrush(color))
            using (var pen = new Pen(Palette.Separator))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }
        }
    }

    // Borderless popup with rounded corners that closes when it loses focus or on Esc, like Windows 11
    // flyouts. Its background is solid, so ordinary controls (TextField, NumberBox...) work anywhere on it.
    // Lay out its content from the font (see Metrics); it isn't display-scaled automatically.
    public class Flyout : FluentForm
    {
        public Flyout()
        {
            Backdrop = Backdrop.None;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int round = NativeMethods.DWMWCP_ROUND;
            NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, 4);
        }

        // Keeps the owner looking active (title bar and backdrop) while the flyout has focus, as menus do,
        // so changes to the owner can be previewed as it normally looks
        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            if (Owner != null) NativeMethods.SendMessage(Owner.Handle, NativeMethods.WM_NCACTIVATE, (IntPtr)1, IntPtr.Zero);
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            Form owner = Owner;
            Close();
            if (owner == null || owner.IsDisposed) return;
            // Once activation settles: if focus went to another app rather than the owner, the owner
            // should look inactive again
            owner.BeginInvoke((MethodInvoker)delegate
            {
                if (!owner.IsDisposed && Form.ActiveForm != owner)
                    NativeMethods.SendMessage(owner.Handle, NativeMethods.WM_NCACTIVATE, IntPtr.Zero, IntPtr.Zero);
            });
        }

        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                Close();
                return true;
            }
            return base.ProcessDialogKey(keyData);
        }

        // Shows the flyout below anchor (above it if there's no room), lined up with its left edge
        public void ShowNear(Control anchor)
        {
            Rectangle bounds = anchor.RectangleToScreen(anchor.ClientRectangle);
            Rectangle area = Screen.FromRectangle(bounds).WorkingArea;
            int gap = Math.Max(2, bounds.Height / 8);
            int x = Math.Max(area.Left, Math.Min(bounds.Left, area.Right - Width));
            int y = bounds.Bottom + gap;
            if (y + Height > area.Bottom) y = Math.Max(area.Top, bounds.Top - gap - Height);
            Location = new Point(x, y);
            Show(anchor.FindForm());
        }
    }

    // Flyout that edits a FluentForm's backdrop tint (opacity and color) for its current theme, previewing
    // live. Changed fires on every edit.
    public class BackdropFlyout : Flyout
    {
        readonly FluentForm target;
        readonly SliderField opacity = new SliderField { Minimum = 0, Maximum = 100 };
        readonly TabStrip blend = new TabStrip();
        readonly ColorPicker picker = new ColorPicker();

        public event EventHandler Changed;

        public BackdropFlyout(FluentForm target)
        {
            this.target = target;
            Font = target.Font;
            ThemeMode = target.ThemeMode;
            Palette palette = target.Palette;
            opacity.Value = palette.TintOpacity;
            picker.Color = palette.BackdropTint;
            blend.SetItems(Enum.GetNames(typeof(TintBlend)));
            blend.SelectedIndex = (int)palette.TintBlend;

            SuspendLayout();
            var headingFont = new Font("Segoe UI Semibold", Font.SizeInPoints);
            Disposed += delegate { headingFont.Dispose(); };
            var opacityLabel = new GlassLabel { Text = "Opacity", Font = headingFont };
            var blendLabel = new GlassLabel { Text = "Blend", Font = headingFont };
            var tintLabel = new GlassLabel { Text = "Tint", Font = headingFont };
            // Added first so they inherit the font that the layout below measures with
            Controls.AddRange(new Control[] { opacityLabel, opacity, blendLabel, blend, tintLabel, picker });
            int pad = Font.Height, width = Font.Height * 18, y = pad;
            opacityLabel.Location = new Point(pad, y);
            y += opacityLabel.Height + pad / 4;
            opacity.SetBounds(pad, y, width, Metrics.Row(Font));
            y += opacity.Height + pad / 2;
            blendLabel.Location = new Point(pad, y);
            y += blendLabel.Height + pad / 4;
            blend.SetBounds(pad, y, width, Metrics.Row(Font));
            y += blend.Height + pad / 2;
            tintLabel.Location = new Point(pad, y);
            y += tintLabel.Height + pad / 4;
            picker.SetBounds(pad, y, width, picker.PreferredHeight);
            y += picker.Height + pad;
            ClientSize = new Size(width + 2 * pad, y);
            ResumeLayout();

            opacity.ValueChanged += OnEdited;
            blend.SelectedIndexChanged += OnEdited;
            picker.ColorChanged += OnEdited;
        }

        void OnEdited(object sender, EventArgs e)
        {
            Palette palette = target.Palette;
            palette.BackdropTint = Color.FromArgb(palette.BackdropTint.A, picker.Color);
            palette.TintOpacity = opacity.Value;
            palette.TintBlend = (TintBlend)blend.SelectedIndex;
            target.Invalidate(true);
            if (Changed != null) Changed(this, EventArgs.Empty);
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

        public int GetInt(string key, int fallback)
        {
            object value;
            if (!values.TryGetValue(key, out value)) return fallback;
            if (value is int) return (int)value;
            if (value is decimal) return (int)Math.Round((decimal)value);
            return fallback;
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
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
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
        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        public const int DWMWCP_ROUND = 2;
        public const int WM_NCACTIVATE = 0x0086;
        public const int SRCCOPY = 0x00CC0020;
    }
}
