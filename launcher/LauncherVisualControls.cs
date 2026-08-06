using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace KinojoMeterLauncher
{
    internal static class LauncherPalette
    {
        public static readonly Color Window = Color.FromArgb(10, 13, 20);
        public static readonly Color Sidebar = Color.FromArgb(12, 16, 24);
        public static readonly Color Topbar = Color.FromArgb(15, 19, 28);
        public static readonly Color Surface = Color.FromArgb(20, 25, 36);
        public static readonly Color SurfaceRaised = Color.FromArgb(25, 31, 44);
        public static readonly Color Border = Color.FromArgb(47, 57, 75);
        public static readonly Color Text = Color.FromArgb(242, 245, 250);
        public static readonly Color Muted = Color.FromArgb(153, 163, 181);
        public static readonly Color Accent = Color.FromArgb(124, 92, 255);
        public static readonly Color AccentBright = Color.FromArgb(67, 218, 255);
        public static readonly Color Success = Color.FromArgb(65, 211, 153);
        public static readonly Color Error = Color.FromArgb(255, 103, 125);
    }

    internal static class LauncherDrawing
    {
        public static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            var diameter = Math.Max(2, Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height)));
            var arc = new Rectangle(bounds.X, bounds.Y, diameter, diameter);
            path.AddArc(arc, 180, 90);
            arc.X = bounds.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = bounds.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = bounds.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class LauncherBackdrop : Panel
    {
        public LauncherBackdrop()
        {
            BackColor = LauncherPalette.Window;
            DoubleBuffered = true;
            ResizeRedraw = true;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (ClientRectangle.Width <= 0 || ClientRectangle.Height <= 0) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var background = new LinearGradientBrush(
                ClientRectangle,
                Color.FromArgb(24, 29, 45),
                Color.FromArgb(10, 14, 23),
                24F))
            {
                e.Graphics.FillRectangle(background, ClientRectangle);
            }

            var glowSize = Math.Max(280, Math.Min(620, Width / 2));
            using (var glow = new PathGradientBrush(new[]
            {
                new Point(Width - glowSize, 0),
                new Point(Width, 0),
                new Point(Width, glowSize),
                new Point(Width - glowSize, glowSize)
            }))
            {
                glow.CenterPoint = new PointF(Width - 90, 110);
                glow.CenterColor = Color.FromArgb(72, LauncherPalette.Accent);
                glow.SurroundColors = new[]
                {
                    Color.Transparent,
                    Color.Transparent,
                    Color.Transparent,
                    Color.Transparent
                };
                e.Graphics.FillRectangle(glow, new Rectangle(Width - glowSize, 0, glowSize, glowSize));
            }

            using (var linePen = new Pen(Color.FromArgb(28, LauncherPalette.AccentBright), 1F))
            {
                for (var offset = -Height; offset < Width; offset += 92)
                    e.Graphics.DrawLine(linePen, offset, Height, offset + Height, 0);
            }
        }
    }

    internal sealed class LauncherCard : Panel
    {
        private int _cornerRadius = 16;
        private Color _borderColor = LauncherPalette.Border;

        public LauncherCard()
        {
            BackColor = LauncherPalette.Surface;
            DoubleBuffered = true;
            ResizeRedraw = true;
        }

        public int CornerRadius
        {
            get { return _cornerRadius; }
            set { _cornerRadius = Math.Max(2, value); UpdateRegion(); Invalidate(); }
        }

        public Color BorderColor
        {
            get { return _borderColor; }
            set { _borderColor = value; Invalidate(); }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(BackColor);
            var bounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            using (var path = LauncherDrawing.RoundedRectangle(bounds, _cornerRadius))
            using (var fill = new SolidBrush(BackColor))
            using (var border = new Pen(_borderColor, 1F))
            {
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(border, path);
            }
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            UpdateRegion();
        }

        private void UpdateRegion()
        {
            if (Width <= 0 || Height <= 0) return;
            using (var path = LauncherDrawing.RoundedRectangle(new Rectangle(0, 0, Width, Height), _cornerRadius))
            {
                var previous = Region;
                Region = new Region(path);
                if (previous != null) previous.Dispose();
            }
        }
    }

    internal sealed class LauncherProgressBar : Control
    {
        private int _value;
        private bool _error;

        public LauncherProgressBar()
        {
            DoubleBuffered = true;
            Height = 8;
            SetStyle(ControlStyles.ResizeRedraw, true);
        }

        public int Value
        {
            get { return _value; }
            set { _value = Math.Max(0, Math.Min(100, value)); Invalidate(); }
        }

        public bool Error
        {
            get { return _error; }
            set { _error = value; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var trackBounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            using (var trackPath = LauncherDrawing.RoundedRectangle(trackBounds, Math.Max(2, Height / 2)))
            using (var track = new SolidBrush(Color.FromArgb(52, 61, 79)))
                e.Graphics.FillPath(track, trackPath);

            var fillWidth = (int)Math.Round(trackBounds.Width * (_value / 100D));
            if (fillWidth <= 0) return;
            var fillBounds = new Rectangle(0, 0, Math.Max(2, fillWidth), trackBounds.Height);
            using (var fillPath = LauncherDrawing.RoundedRectangle(fillBounds, Math.Max(2, Height / 2)))
            using (var fill = _error
                ? (Brush)new SolidBrush(LauncherPalette.Error)
                : new LinearGradientBrush(fillBounds, LauncherPalette.Accent, LauncherPalette.AccentBright, 0F))
                e.Graphics.FillPath(fill, fillPath);
        }
    }

    internal sealed class LauncherActionButton : Button
    {
        private bool _hovered;

        public LauncherActionButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Font = new Font("Segoe UI", 10F, FontStyle.Bold);
            ForeColor = Color.White;
            Cursor = Cursors.Hand;
            DoubleBuffered = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hovered = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hovered = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            using (var path = LauncherDrawing.RoundedRectangle(bounds, 12))
            {
                if (!Enabled)
                {
                    using (var disabled = new SolidBrush(Color.FromArgb(61, 68, 85))) e.Graphics.FillPath(disabled, path);
                }
                else
                {
                    var left = _hovered ? Color.FromArgb(141, 109, 255) : LauncherPalette.Accent;
                    var right = _hovered ? Color.FromArgb(78, 228, 255) : LauncherPalette.AccentBright;
                    using (var fill = new LinearGradientBrush(bounds, left, right, 0F)) e.Graphics.FillPath(fill, path);
                }
            }

            TextRenderer.DrawText(
                e.Graphics,
                Text,
                Font,
                ClientRectangle,
                Enabled ? ForeColor : LauncherPalette.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        protected override void OnPaintBackground(PaintEventArgs pevent)
        {
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (Width <= 0 || Height <= 0) return;
            using (var path = LauncherDrawing.RoundedRectangle(new Rectangle(0, 0, Width, Height), 12))
            {
                var previous = Region;
                Region = new Region(path);
                if (previous != null) previous.Dispose();
            }
        }
    }
}
