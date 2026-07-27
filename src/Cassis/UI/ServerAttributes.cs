using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Attributes;

namespace Cassis.UI
{
    /// <summary>
    /// Dark card UI for the MCP server component
    /// Layout:
    ///  ┌──────────────────────────────────────────┐
    ///  │  🧠 Server                      ● Running│
    ///  │                               00:20      │
    ///  ├──────────────────────────────────────────┤
    ///  │  Messages           0                    │
    ///  │  Last msg           —                    │
    ///  │  Last tool call     —                    │
    ///  ├──────────────────────────────────────────┤
    ///  │            [  Stop Server  ]             │
    ///  └──────────────────────────────────────────┘
    /// </summary>
    public class ServerAttributes : GH_ComponentAttributes
    {
        // Delegates from the component (keeps this paint-only)
        private readonly Func<string> _getButtonText;
        private readonly Action _onButtonClick;
        private readonly Func<string> _getStatusLabel;   // Running / Starting / Stopped / Error
        private readonly Func<string> _getUptime;        // "00:20" or empty
        private readonly Func<string> _getMessages;      // "0"
        private readonly Func<string> _getLastMsg;       // "—" or short text/time
        private readonly Func<string> _getLastTool;      // "—" or short text/time
        public ServerAttributes(
            GH_Component owner,
            Func<string> getButtonText,
            Action onButtonClick,
            Func<string> getStatusLabel,
            Func<string> getUptime,
            Func<string> getMessages,
            Func<string> getLastMsg,
            Func<string> getLastTool) : base(owner)
        {
            _getButtonText = getButtonText;
            _onButtonClick = onButtonClick;
            _getStatusLabel = getStatusLabel;
            _getUptime = getUptime;
            _getMessages = getMessages;
            _getLastMsg = getLastMsg;
            _getLastTool = getLastTool;
        }

        // Layout rects
        private RectangleF _panel;
        private RectangleF _header;
        private RectangleF _body;
        private RectangleF _footer;
        private RectangleF _buttonRect;

        private bool _mouseOver;
        private bool _mouseDown;

        // Tokens (dark theme) - refined for better contrast
        static readonly int R = 6;
        static readonly int PAD = 14;
        const float HeaderGap = 6f;
        static readonly Color PanelFill = Color.FromArgb(0x1F, 0x21, 0x24);   // Refined dark background
        static readonly Color PanelBorder = Color.FromArgb(0x2D, 0x32, 0x38); // Subtle border
        static readonly Color Separator = Color.FromArgb(0x2A, 0x2F, 0x35);   // Refined separator
        static readonly Color Text = Color.FromArgb(0xF8, 0xF9, 0xFA);        // Crisp white text
        static readonly Color Muted = Color.FromArgb(0x9C, 0xA3, 0xAF);       // Consistent muted text
        static readonly Color Danger = Color.FromArgb(0xEF, 0x44, 0x44);      // Clean red
        static readonly Color Success = Color.FromArgb(0x10, 0xB9, 0x81);     // Refined green
        static readonly Color Warning = Color.FromArgb(0xF5, 0x9E, 0x0B);     // Amber
        static readonly Color Disabled = Color.FromArgb(0x6B, 0x72, 0x80);    // Gray

        protected override void Layout()
        {
            base.Layout();

            // Base GH capsule is already sized; we extend under it.
            float baseWidth = Bounds.Width;
            float baseHeight = Bounds.Bottom - Bounds.Top;
            float width = Math.Max(baseWidth, 240f);
            float headerH = 54f;
            float bodyH = 4 * 26f + 16f; // 3 rows + extra line for last tool call
            float footerH = 52f;
            float totalH = headerH + HeaderGap + bodyH + footerH;

            // Center the capsule content when widening the component.
            float dx = width - baseWidth;
            float newX = Bounds.X - (dx / 2f);

            _panel = new RectangleF(newX, Bounds.Bottom + 6, width, totalH);
            _header = new RectangleF(_panel.X, _panel.Y, _panel.Width, headerH);
            _body   = new RectangleF(_panel.X, _header.Bottom + HeaderGap, _panel.Width, bodyH);
            _footer = new RectangleF(_panel.X, _body.Bottom, _panel.Width, footerH);

            _buttonRect = new RectangleF(
                _footer.X + PAD,
                _footer.Y + 12,
                _footer.Width - PAD * 2,
                _footer.Height - 24
            );

            // Expand component bounds to include our custom card
            Bounds = new RectangleF(
                newX,
                Bounds.Y,
                width,
                baseHeight + 6 + totalH
            );

            // Slide IO anchors to keep them aligned with the centered capsule
            if (Math.Abs(dx) > 0.1f)
            {
                foreach (var p in Owner.Params.Output)
                {
                    var a = p.Attributes;
                    a.Pivot = new PointF(a.Pivot.X + dx / 2f, a.Pivot.Y);
                }
                foreach (var p in Owner.Params.Input)
                {
                    var a = p.Attributes;
                    a.Pivot = new PointF(a.Pivot.X - dx / 2f, a.Pivot.Y);
                }
            }
        }

        protected override void Render(GH_Canvas canvas, Graphics g, GH_CanvasChannel channel)
        {
            base.Render(canvas, g, channel);
            if (channel != GH_CanvasChannel.Objects) return;

            g.SmoothingMode = SmoothingMode.AntiAlias;
            var titleFont = GH_FontServer.NewFont(GH_FontServer.Small, FontStyle.Bold);
            var bodyFont = GH_FontServer.Small;
            var uptimeFont = GH_FontServer.Small;
            var buttonFont = GH_FontServer.Small;

            // Panel with subtle shadow
            using (var panelPath = RoundedRect(_panel, R))
            {
                // Subtle shadow
                var shadowRect = new RectangleF(_panel.X + 2, _panel.Y + 2, _panel.Width, _panel.Height);
                using (var shadowPath = RoundedRect(shadowRect, R))
                {
                    g.FillPath(new SolidBrush(Color.FromArgb(30, 0, 0, 0)), shadowPath);
                }
                
                // Main panel
                g.FillPath(new SolidBrush(PanelFill), panelPath);
                g.DrawPath(new Pen(PanelBorder, 1), panelPath);
            }

            // Header band
            DrawHeader(g, titleFont, uptimeFont);
            DrawSeparator(g, _header.Bottom + (HeaderGap / 2f));

            // Body metrics
            float y = _body.Top + 6;
            DrawMetricRow(g, bodyFont, "Messages", _getMessages(), ref y);
            DrawMetricRow(g, bodyFont, "Last msg", _getLastMsg(), ref y);
            DrawMetricRowValueBelow(g, bodyFont, "Last tool call", _getLastTool(), ref y);

            DrawSeparator(g, _footer.Top);

            // Primary button
            DrawPrimaryButton(g, buttonFont, _buttonRect, _getButtonText());
        }

        private void DrawHeader(Graphics g, Font titleFont, Font uptimeFont)
        {
            // Status indicator (left) - replaces the blue brain icon
            var status = _getStatusLabel()?.Trim() ?? "";
            var dot = StatusColor(status);
            var iconRect = new RectangleF(_header.X + PAD, _header.Y + 16, 18, 18);
            g.FillEllipse(new SolidBrush(dot), iconRect);

            // Combined title and status (left of center)
            var combinedTitle = $"Server {status}";
            var titleRect = new RectangleF(_header.X + PAD + 26, _header.Y + 10, _header.Width - (PAD + 26) * 2, 18);
            g.DrawString(combinedTitle, titleFont, new SolidBrush(Text), titleRect, GH_TextRenderingConstants.NearCenter);

            // Uptime (below title)
            var up = _getUptime();
            if (!string.IsNullOrWhiteSpace(up))
            {
                var upRect = new RectangleF(_header.X + PAD + 26, _header.Y + 34, _header.Width - (PAD + 26) * 2, 16);
                g.DrawString(up, uptimeFont, new SolidBrush(Muted), upRect, GH_TextRenderingConstants.NearCenter);
            }
        }

        private void DrawMetricRow(Graphics g, Font bodyFont, string label, string value, ref float y)
        {
            var lh = 26f;
            var leftRect  = new RectangleF(_body.X + PAD, y, _body.Width / 2f, lh);
            var rightRect = new RectangleF(_body.Right - PAD - _body.Width / 2f, y, _body.Width / 2f, lh);

            g.DrawString(label, bodyFont, new SolidBrush(Muted), leftRect, GH_TextRenderingConstants.NearCenter);
            g.DrawString(Trunc(value, 32), bodyFont, new SolidBrush(Text), rightRect, GH_TextRenderingConstants.FarCenter);
            y += lh;
        }

        private void DrawMetricRowValueBelow(Graphics g, Font bodyFont, string label, string value, ref float y)
        {
            var lh = 26f;
            var labelRect = new RectangleF(_body.X + PAD, y, _body.Width - PAD * 2, lh);
            g.DrawString(label, bodyFont, new SolidBrush(Muted), labelRect, GH_TextRenderingConstants.NearCenter);
            y += lh;

            var valueRect = new RectangleF(_body.X + PAD, y, _body.Width - PAD * 2, lh);
            g.DrawString(Trunc(value, 48), bodyFont, new SolidBrush(Text), valueRect, GH_TextRenderingConstants.NearCenter);
            y += lh;
        }

        private void DrawSeparator(Graphics g, float y)
        {
            g.DrawLine(new Pen(Separator, 1),
                new PointF(_panel.Left + PAD, y),
                new PointF(_panel.Right - PAD, y));
        }

        private void DrawPrimaryButton(Graphics g, Font buttonFont, RectangleF r, string text)
        {
            string status = _getStatusLabel()?.Trim() ?? "";
            bool running = status.Equals("Running", StringComparison.OrdinalIgnoreCase);

            var bg = running ? Danger : Success;
            var hover = Color.FromArgb(
                Math.Min(255, bg.R + 20),
                Math.Min(255, bg.G + 20),
                Math.Min(255, bg.B + 20));

            var fill = _mouseDown ? Darken(bg, 0.2) : _mouseOver ? hover : bg;

            using (var path = RoundedRect(r, 8))
            {
                using (var br = new SolidBrush(fill)) g.FillPath(br, path);
                g.DrawPath(new Pen(Darken(bg, 0.4f), 1.2f), path);
            }

            // Button text with better typography
            var sz = g.MeasureString(text, buttonFont);
            var tx = r.X + (r.Width - sz.Width) / 2f;
            var ty = r.Y + (r.Height - sz.Height) / 2f;
            g.DrawString(text, buttonFont, Brushes.White, new PointF(tx, ty));
        }

        // Interaction
        public override GH_ObjectResponse RespondToMouseMove(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            if (_buttonRect.Contains(e.CanvasLocation))
            {
                _mouseOver = true;
                Owner.OnDisplayExpired(false);
                sender.Cursor = System.Windows.Forms.Cursors.Hand;
                return GH_ObjectResponse.Capture;
            }
            if (_mouseOver)
            {
                _mouseOver = false;
                Owner.OnDisplayExpired(false);
                Grasshopper.Instances.CursorServer.ResetCursor(sender);
                return GH_ObjectResponse.Release;
            }
            return base.RespondToMouseMove(sender, e);
        }

        public override GH_ObjectResponse RespondToMouseDown(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left && _buttonRect.Contains(e.CanvasLocation))
            {
                _mouseDown = true;
                Owner.OnDisplayExpired(false);
                return GH_ObjectResponse.Capture;
            }
            return base.RespondToMouseDown(sender, e);
        }

        public override GH_ObjectResponse RespondToMouseUp(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left && _buttonRect.Contains(e.CanvasLocation))
            {
                bool wasDown = _mouseDown;
                _mouseDown = false;
                _mouseOver = false;
                Owner.OnDisplayExpired(false);

                if (wasDown)
                {
                    try { _onButtonClick(); }
                    catch (Exception ex) { Rhino.RhinoApp.WriteLine($"[MCP WARN] Button click error: {ex.Message}"); }
                    return GH_ObjectResponse.Release;
                }
            }
            else if (e.Button == System.Windows.Forms.MouseButtons.Right && _panel.Contains(e.CanvasLocation))
            {
                ShowContextMenu(sender, e);
                return GH_ObjectResponse.Handled;
            }
            return base.RespondToMouseUp(sender, e);
        }

        private void ShowContextMenu(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            try
            {
#if NET48 || NET8_0_WINDOWS
                var contextMenu = new ContextMenuStrip();
                var screenPos = sender.PointToScreen(new Point((int)e.CanvasLocation.X, (int)e.CanvasLocation.Y));
                contextMenu.Show(sender, screenPos);
#endif
            }
            catch (Exception ex)
            {
                Rhino.RhinoApp.WriteLine($"[MCP WARN] Context menu error: {ex.Message}");
            }
        }

        // Helpers
        private static GraphicsPath RoundedRect(RectangleF bounds, int radius)
        {
            int d = radius * 2;
            var gp = new GraphicsPath();
            gp.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            gp.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            gp.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            gp.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            gp.CloseFigure();
            return gp;
        }

        private static Color StatusColor(string s)
        {
            if (s.Equals("Running", StringComparison.OrdinalIgnoreCase)) return Success;
            if (s.StartsWith("Start", StringComparison.OrdinalIgnoreCase)) return Warning;
            if (s.StartsWith("Stop", StringComparison.OrdinalIgnoreCase)) return Warning;
            if (s.Equals("Error", StringComparison.OrdinalIgnoreCase)) return Danger;
            return Disabled;
        }

        private static string Trunc(string v, int max)
        {
            if (string.IsNullOrWhiteSpace(v)) return "—";
            v = v.Trim();
            return v.Length <= max ? v : v.Substring(0, max - 1) + "…";
        }

        private static Color Darken(Color c, double amount)
        {
            amount = Math.Max(0, Math.Min(1, amount));
            return Color.FromArgb(
                c.A,
                (int)(c.R * (1 - amount)),
                (int)(c.G * (1 - amount)),
                (int)(c.B * (1 - amount)));
        }
    }
}
