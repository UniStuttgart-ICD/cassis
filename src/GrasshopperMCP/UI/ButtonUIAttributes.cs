using Grasshopper.Kernel.Attributes;
using Grasshopper.GUI.Canvas;
using Grasshopper.GUI;
using Grasshopper.Kernel;
using System;
using System.Drawing;
using System.Collections.Generic;
using Rhino.Display;
using System.Drawing.Drawing2D;
using GrasshopperMCP.Properties;

namespace GrasshopperMCP.UI
{
    /// <summary>
    /// Class to create custom component UI with a button
    /// 
    /// To use this method override CreateAttributes() in component class and set m_attributes = new ButtonUIAttributes(...
    /// </summary>
    public class ButtonUIAttributes : GH_ComponentAttributes
    {
        public ButtonUIAttributes(GH_Component owner, Func<string> getButtonText, Action clickHandle, string spacerText = "", Func<string>? getStatusText = null, Func<string>? getStatusColor = null, Func<string>? getLastToolCall = null) : base(owner)
        {
            this.getButtonText = getButtonText;
            SpacerTxt = spacerText;
            action = clickHandle;
            this.getStatusText = getStatusText ?? (() => "");
            this.getStatusColor = getStatusColor ?? (() => "Connected");
            this.getLastToolCall = getLastToolCall ?? (() => "Last Tool Call: None");
        }

        private RectangleF ButtonBounds; // area for button to be displayed
        private RectangleF StatusBounds; // area for status display
        private RectangleF ToolCallBounds; // area for tool call cell
        private Func<string> getButtonText; // Changed to function for dynamic updates
        private Action action;
        private Func<string> getStatusText; // function to get status text
        private Func<string> getStatusColor; // function to get status color
        private Func<string> getLastToolCall; // function to get last tool call text

        private string SpacerTxt; // text to be displayed on spacer

        bool mouseDown;
        float MinWidth
        {
            get
            {
                List<string> buttons = new List<string>();
                buttons.Add(getButtonText());
                float bt = MaxTextWidth(buttons, GH_FontServer.Standard);

                // Minimum width for button and status panel - increased for better text display
                float num = Math.Max(bt, 140);
                return num;
            }
            set { MinWidth = value; }
        }
        protected override void Layout()
        {
            base.Layout();

            // first change the width to suit; using max to determine component visualisation style
            FixLayout();

            int s = 3; // Increased spacing to edges for better breathing room

            // Calculate heights for our custom UI elements with refined proportions
            int buttonHeight = 34; // Slightly increased for better proportions
            int statusHeight = 92; // Increased for better text spacing and visual hierarchy
            int toolCallHeight = 38; // Increased for better text spacing
            int spacing = 14; // Increased spacing between elements for better visual separation

            // Button at top (below the Grasshopper icon) with better margins
            ButtonBounds = new RectangleF(Bounds.X + 2 * s, Bounds.Bottom + s, Bounds.Width - 4 * s, buttonHeight);
            
            // Status panel below button with improved spacing
            StatusBounds = new RectangleF(Bounds.X + 2 * s, ButtonBounds.Bottom + spacing, Bounds.Width - 4 * s, statusHeight);
            
            // Tool call cell below status panel
            ToolCallBounds = new RectangleF(Bounds.X + 2 * s, StatusBounds.Bottom + spacing, Bounds.Width - 4 * s, toolCallHeight);

            // Update component bounds to accommodate all our custom UI elements
            Bounds = new RectangleF(Bounds.X, Bounds.Y, Bounds.Width, Bounds.Height + buttonHeight + statusHeight + toolCallHeight + 4 * spacing + 2 * s);
        }

        protected override void Render(GH_Canvas canvas, System.Drawing.Graphics graphics, GH_CanvasChannel channel)
        {
            base.Render(canvas, graphics, channel);

            if (channel == GH_CanvasChannel.Objects)
            {
                // Enable high quality rendering
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                Font font = GH_FontServer.Standard;
                // adjust fontsize to high resolution displays
                font = new Font(font.FontFamily, font.Size / GH_GraphicsUtil.UiScale, FontStyle.Regular);

                Font sml = GH_FontServer.Small;
                // adjust fontsize to high resolution displays
                sml = new Font(sml.FontFamily, sml.Size / GH_GraphicsUtil.UiScale, FontStyle.Regular);

                // Draw button first (main interactive element)
                System.Drawing.Drawing2D.GraphicsPath button = RoundedRect(ButtonBounds, 10);

                // Create gradient background for button appearance
                LinearGradientBrush buttonGradient;
                
                if (mouseDown)
                {
                    buttonGradient = new LinearGradientBrush(
                        new PointF(ButtonBounds.X, ButtonBounds.Y), 
                        new PointF(ButtonBounds.X, ButtonBounds.Bottom), 
                        Color.FromArgb(255, 50, 50, 50), 
                        Color.FromArgb(255, 40, 40, 40));
                }
                else if (mouseOver)
                {
                    buttonGradient = new LinearGradientBrush(
                        new PointF(ButtonBounds.X, ButtonBounds.Y), 
                        new PointF(ButtonBounds.X, ButtonBounds.Bottom), 
                        Color.FromArgb(255, 90, 90, 90), 
                        Color.FromArgb(255, 80, 80, 80));
                }
                else
                {
                    buttonGradient = new LinearGradientBrush(
                        new PointF(ButtonBounds.X, ButtonBounds.Y), 
                        new PointF(ButtonBounds.X, ButtonBounds.Bottom), 
                        Color.FromArgb(255, 75, 75, 75), 
                        Color.FromArgb(255, 65, 65, 65));
                }
                
                graphics.FillPath(buttonGradient, button);

                // Draw button edge with enhanced shadow effect
                Color edgeColor = mouseDown ? Color.FromArgb(255, 25, 25, 25) : 
                                  mouseOver ? Color.FromArgb(255, 95, 95, 95) : 
                                  Color.FromArgb(255, 50, 50, 50);
                
                Pen pen = new Pen(edgeColor, mouseDown ? 2.0f : 1.0f);
                graphics.DrawPath(pen, button);

                // Add subtle inner highlight for depth
                System.Drawing.Drawing2D.GraphicsPath innerHighlight = RoundedRect(
                    new RectangleF(ButtonBounds.X + 1, ButtonBounds.Y + 1, ButtonBounds.Width - 2, ButtonBounds.Height / 2), 
                    7, true);
                graphics.FillPath(new SolidBrush(Color.FromArgb(mouseDown ? 20 : mouseOver ? 40 : 30, 255, 255, 255)), innerHighlight);

                // Draw button text with improved typography and positioning
                var buttonTextBounds = new RectangleF(
                    ButtonBounds.X + 18,
                    ButtonBounds.Y + 8,
                    ButtonBounds.Width - 36,
                    ButtonBounds.Height - 16);
                
                // Use slightly bolder font for button text
                var buttonFont = new Font(font.FontFamily, font.Size * 1.05f / GH_GraphicsUtil.UiScale, FontStyle.Bold);
                graphics.DrawString(getButtonText(), buttonFont, ButtonColours.AnnotationTextBright, buttonTextBounds, GH_TextRenderingConstants.CenterCenter);

                // Draw information display below button with enhanced design
                if (getStatusText() != "")
                {
                    var statusText = getStatusText();
                    var statusColor = getStatusColor();
                    
                    var infoPanel = RoundedRect(StatusBounds, 8);
                    
                    // Create gradient background for status panel
                    var statusBrush = GetStatusPanelBrush(statusColor);
                    var statusColorValue = ((SolidBrush)statusBrush).Color;
                    var statusGradient = new LinearGradientBrush(
                        new PointF(StatusBounds.X, StatusBounds.Y), 
                        new PointF(StatusBounds.X, StatusBounds.Bottom), 
                        statusColorValue, 
                        statusColorValue);
                    graphics.FillPath(statusGradient, infoPanel);
                    
                    // Add subtle border with enhanced styling
                    graphics.DrawPath(new Pen(GetStatusPanelBorderColor(statusColor), 1.5f), infoPanel);
                    
                    // Add inner shadow for depth
                    var innerShadow = RoundedRect(new RectangleF(StatusBounds.X + 1, StatusBounds.Y + 1, StatusBounds.Width - 2, StatusBounds.Height - 2), 5);
                    graphics.DrawPath(new Pen(Color.FromArgb(50, 0, 0, 0), 1.0f), innerShadow);
                    
                    // Draw status indicator icon
                    DrawStatusIndicator(graphics, statusColor, StatusBounds);
                    
                    var infoFont = new Font(font.FontFamily, font.Size * 0.9f / GH_GraphicsUtil.UiScale, FontStyle.Regular);
                    var infoTextBrush = new SolidBrush(Color.FromArgb(255, 45, 45, 45));
                    
                    // Split status text into separate lines with better formatting
                    var statusParts = statusText.Split(new[] { " • " }, StringSplitOptions.RemoveEmptyEntries);
                    var lineHeight = StatusBounds.Height / 3; // Divide height by 3 for 3 lines
                    
                    for (int i = 0; i < statusParts.Length && i < 3; i++)
                    {
                        // Enhanced text positioning with better padding, accounting for status indicator
                        var lineBounds = new RectangleF(
                            StatusBounds.X + 18, 
                            StatusBounds.Y + 14 + (i * lineHeight), 
                            StatusBounds.Width - 42, // Reduced width to account for status indicator
                            lineHeight - 10);
                        
                        var format = new StringFormat
                        {
                            Alignment = StringAlignment.Near, // Left-align text for better readability
                            LineAlignment = StringAlignment.Center,
                            Trimming = StringTrimming.EllipsisCharacter
                        };
                        
                        // Add subtle text shadow for better readability
                        var shadowBounds = new RectangleF(lineBounds.X + 1, lineBounds.Y + 1, lineBounds.Width, lineBounds.Height);
                        graphics.DrawString(statusParts[i].Trim(), infoFont, new SolidBrush(Color.FromArgb(30, 255, 255, 255)), shadowBounds, format);
                        graphics.DrawString(statusParts[i].Trim(), infoFont, infoTextBrush, lineBounds, format);
                    }
                }
                
                // Draw tool call cell below status panel with enhanced design
                var toolCallPanel = RoundedRect(ToolCallBounds, 8);
                
                // Create gradient background for tool call panel
                var toolCallGradient = new LinearGradientBrush(
                    new PointF(ToolCallBounds.X, ToolCallBounds.Y), 
                    new PointF(ToolCallBounds.X, ToolCallBounds.Bottom), 
                    Color.FromArgb(255, 252, 252, 252), 
                    Color.FromArgb(255, 245, 245, 245));
                graphics.FillPath(toolCallGradient, toolCallPanel);
                
                // Enhanced border styling
                graphics.DrawPath(new Pen(Color.FromArgb(255, 210, 210, 210), 1.5f), toolCallPanel);
                
                // Add subtle inner highlight
                var toolCallHighlight = RoundedRect(new RectangleF(ToolCallBounds.X + 1, ToolCallBounds.Y + 1, ToolCallBounds.Width - 2, ToolCallBounds.Height / 2), 5, true);
                graphics.FillPath(new SolidBrush(Color.FromArgb(40, 255, 255, 255)), toolCallHighlight);
                
                var toolCallFont = new Font(font.FontFamily, font.Size * 0.85f / GH_GraphicsUtil.UiScale, FontStyle.Regular);
                var toolCallTextBrush = new SolidBrush(Color.FromArgb(255, 55, 55, 55));
                
                var toolCallTextBounds = new RectangleF(
                    ToolCallBounds.X + 18, 
                    ToolCallBounds.Y + 12, 
                    ToolCallBounds.Width - 36, 
                    ToolCallBounds.Height - 24);
                
                var toolCallFormat = new StringFormat
                {
                    Alignment = StringAlignment.Near, // Left-align text for better readability
                    LineAlignment = StringAlignment.Center,
                    Trimming = StringTrimming.EllipsisCharacter
                };
                
                // Display last tool call information with text shadow
                var lastToolCallText = getLastToolCall();
                var toolCallShadowBounds = new RectangleF(toolCallTextBounds.X + 1, toolCallTextBounds.Y + 1, toolCallTextBounds.Width, toolCallTextBounds.Height);
                graphics.DrawString(lastToolCallText, toolCallFont, new SolidBrush(Color.FromArgb(20, 255, 255, 255)), toolCallShadowBounds, toolCallFormat);
                graphics.DrawString(lastToolCallText, toolCallFont, toolCallTextBrush, toolCallTextBounds, toolCallFormat);
            }
        }
        public override GH_ObjectResponse RespondToMouseDown(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left)
            {
                System.Drawing.RectangleF rec = ButtonBounds;
                if (rec.Contains(e.CanvasLocation))
                {
                    mouseDown = true;
                    Owner.OnDisplayExpired(false);
                    return GH_ObjectResponse.Capture;
                }
            }
            return base.RespondToMouseDown(sender, e);
        }

        public override GH_ObjectResponse RespondToMouseUp(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left)
            {
                System.Drawing.RectangleF rec = ButtonBounds;
                if (rec.Contains(e.CanvasLocation))
                {
                    if (mouseDown)
                    {
                        mouseDown = false;
                        mouseOver = false;
                        Owner.OnDisplayExpired(false);
                        try
                        {
                            action();
                        }
                        catch (Exception ex)
                        {
                            Rhino.RhinoApp.WriteLine($"[MCP UI] Button action error: {ex.Message}");
                        }
                        //                        Owner.ExpireSolution(true);
                        return GH_ObjectResponse.Release;
                    }
                }
            }
            return base.RespondToMouseUp(sender, e);
        }
        bool mouseOver;
        public override GH_ObjectResponse RespondToMouseMove(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            if (ButtonBounds.Contains(e.CanvasLocation))
            {
                mouseOver = true;
                Owner.OnDisplayExpired(false);
                sender.Cursor = System.Windows.Forms.Cursors.Hand;
                return GH_ObjectResponse.Capture;
            }

            if (mouseOver)
            {
                mouseOver = false;
                Owner.OnDisplayExpired(false);
                Grasshopper.Instances.CursorServer.ResetCursor(sender);
                return GH_ObjectResponse.Release;
            }

            return base.RespondToMouseMove(sender, e);
        }

        /// <summary>
        /// Refreshes the UI display
        /// </summary>
        public void RefreshUI()
        {
            Owner.OnDisplayExpired(false);
        }


        protected void FixLayout()
        {
            float width = this.Bounds.Width; // initial component width before UI overrides
            float num = Math.Max(width, MinWidth); // number for new width
            float num2 = 0f; // value for increased width (if any)

            // first check if original component must be widened
            if (num > width)
            {
                num2 = num - width; // change in width
                // update component bounds to new width
                this.Bounds = new RectangleF(
                    this.Bounds.X - num2 / 2f,
                    this.Bounds.Y,
                    num,
                    this.Bounds.Height);
            }

            // secondly update position of input and output parameter text
            // first find the maximum text width of parameters

            foreach (IGH_Param item in base.Owner.Params.Output)
            {
                PointF pivot = item.Attributes.Pivot; // original anchor location of output
                RectangleF bounds = item.Attributes.Bounds; // text box itself
                item.Attributes.Pivot = new PointF(
                    pivot.X + num2 / 2f, // move anchor to the right
                    pivot.Y);
                item.Attributes.Bounds = new RectangleF(
                    bounds.Location.X + num2 / 2f,  // move text box to the right
                    bounds.Location.Y,
                    bounds.Width,
                    bounds.Height);
            }
            // for input params first find the widest input text box as these are right-aligned
            float inputwidth = 0f;
            foreach (IGH_Param item in base.Owner.Params.Input)
            {
                if (inputwidth < item.Attributes.Bounds.Width)
                    inputwidth = item.Attributes.Bounds.Width;
            }
            foreach (IGH_Param item2 in base.Owner.Params.Input)
            {
                PointF pivot2 = item2.Attributes.Pivot; // original anchor location of input
                RectangleF bounds2 = item2.Attributes.Bounds;
                item2.Attributes.Pivot = new PointF(
                    pivot2.X - num2 / 2f + inputwidth, // move to the left, move back by max input width
                    pivot2.Y);
                item2.Attributes.Bounds = new RectangleF(
                     bounds2.Location.X - num2 / 2f,
                     bounds2.Location.Y,
                     bounds2.Width,
                     bounds2.Height);
            }
        }
        public static float MaxTextWidth(List<string> spacerTxts, Font font)
        {
            float sp = new float(); //width of spacer text

            // adjust fontsize to high resolution displays
            font = new Font(font.FontFamily, font.Size / GH_GraphicsUtil.UiScale, FontStyle.Regular);

            for (int i = 0; i < spacerTxts.Count; i++)
            {
                if (GH_FontServer.StringWidth(spacerTxts[i], font) + 24 > sp)
                    sp = GH_FontServer.StringWidth(spacerTxts[i], font) + 24;
            }
            return sp;
        }



        public static GraphicsPath RoundedRect(RectangleF bounds, int radius, bool overlay = false)
        {
            RectangleF b = new RectangleF(bounds.X, bounds.Y, bounds.Width, bounds.Height);
            int diameter = radius * 2;
            Size size = new Size(diameter, diameter);
            RectangleF arc = new RectangleF(b.Location, size);
            GraphicsPath path = new GraphicsPath();
            
            if (overlay)
                b.Height = diameter;

            if (radius == 0)
            {
                path.AddRectangle(b);
                return path;
            }

            // top left arc  
            path.AddArc(arc, 180, 90);

            // top right arc  
            arc.X = b.Right - diameter;
            path.AddArc(arc, 270, 90);

            if (!overlay)
            {
                // bottom right arc  
                arc.Y = b.Bottom - diameter;
                path.AddArc(arc, 0, 90);

                // bottom left arc 
                arc.X = b.Left;
                path.AddArc(arc, 90, 90);
            }
            else
            {
                path.AddLine(new PointF(b.X + b.Width, b.Y + b.Height), new PointF(b.X, b.Y + b.Height));
            }

            path.CloseFigure();
            return path;
        }

        private Brush GetStatusPanelBrush(string status)
        {
            return status switch
            {
                "Connected" => new SolidBrush(Color.FromArgb(255, 240, 255, 240)), // Softer, more elegant green
                "Starting" => new SolidBrush(Color.FromArgb(255, 255, 250, 235)), // Warmer, more sophisticated amber
                "Stopping" => new SolidBrush(Color.FromArgb(255, 255, 250, 235)), // Warmer, more sophisticated amber
                "Disconnected" => new SolidBrush(Color.FromArgb(255, 250, 250, 250)), // Cleaner, more neutral gray
                "Error" => new SolidBrush(Color.FromArgb(255, 255, 245, 245)), // Softer, less harsh red
                _ => new SolidBrush(Color.FromArgb(255, 252, 252, 252)) // Default clean light gray
            };
        }

        private Color GetStatusPanelBorderColor(string status)
        {
            return status switch
            {
                "Connected" => Color.FromArgb(255, 200, 230, 200), // Softer green border
                "Starting" => Color.FromArgb(255, 230, 215, 180), // Warmer amber border
                "Stopping" => Color.FromArgb(255, 230, 215, 180), // Warmer amber border
                "Disconnected" => Color.FromArgb(255, 220, 220, 220), // Softer gray border
                "Error" => Color.FromArgb(255, 230, 200, 200), // Softer red border
                _ => Color.FromArgb(255, 235, 235, 235) // Default softer gray border
            };
        }

        private void DrawStatusIndicator(Graphics graphics, string status, RectangleF bounds)
        {
            // Draw a more elegant status indicator circle in the top-right corner
            float indicatorSize = 10f;
            float indicatorX = bounds.Right - indicatorSize - 10f;
            float indicatorY = bounds.Y + 10f;
            
            var indicatorRect = new RectangleF(indicatorX, indicatorY, indicatorSize, indicatorSize);
            
            // Choose indicator color based on status with more sophisticated colors
            Color indicatorColor = status switch
            {
                "Connected" => Color.FromArgb(255, 34, 197, 94), // Vibrant but elegant green
                "Starting" => Color.FromArgb(255, 245, 158, 11), // Rich amber
                "Stopping" => Color.FromArgb(255, 245, 158, 11), // Rich amber
                "Disconnected" => Color.FromArgb(255, 156, 163, 175), // Sophisticated gray
                "Error" => Color.FromArgb(255, 239, 68, 68), // Clean red
                _ => Color.FromArgb(255, 156, 163, 175) // Default sophisticated gray
            };
            
            // Draw indicator circle with subtle gradient
            var indicatorGradient = new LinearGradientBrush(
                new PointF(indicatorRect.X, indicatorRect.Y), 
                new PointF(indicatorRect.Right, indicatorRect.Bottom), 
                indicatorColor, 
                Color.FromArgb(255, Math.Max(0, indicatorColor.R - 25), Math.Max(0, indicatorColor.G - 25), Math.Max(0, indicatorColor.B - 25)));
            
            graphics.FillEllipse(indicatorGradient, indicatorRect);
            
            // Add subtle border with better contrast
            graphics.DrawEllipse(new Pen(Color.FromArgb(255, Math.Max(0, indicatorColor.R - 40), Math.Max(0, indicatorColor.G - 40), Math.Max(0, indicatorColor.B - 40)), 0.8f), indicatorRect);
            
            // Add inner highlight for depth with better positioning
            var highlightRect = new RectangleF(indicatorX + 1, indicatorY + 1, indicatorSize - 2, (indicatorSize - 2) / 2);
            graphics.FillEllipse(new SolidBrush(Color.FromArgb(80, 255, 255, 255)), highlightRect);
        }
    }


    /// <summary>
    /// Colour class holding the main colours used in colour scheme. 
    /// Make calls to this class to be able to easy update colours.
    /// 
    /// </summary>
    public class ButtonColours
    {
        //Set colours for Component UI - Updated design with better accessibility
        static readonly Color Primary = Color.FromArgb(255, 65, 65, 65); // Neutral dark gray
        static readonly Color Primary_light = Color.FromArgb(255, 90, 90, 90); // Medium gray
        static readonly Color Primary_dark = Color.FromArgb(255, 35, 35, 35); // Dark gray
        static readonly Color Accent = Color.FromArgb(255, 120, 120, 120); // Light accent gray
        static readonly Color Background = Color.FromArgb(255, 250, 250, 250); // Clean light background
        
        public static Brush ButtonColor
        {
            get { return new SolidBrush(Primary); }
        }
        public static Brush ClickedButtonColor
        {
            get { return new SolidBrush(Primary_light); }
        }
        public static Color BorderColour
        {
            get { return Primary_dark; }
        }
        public static Color ClickedBorderColour
        {
            get { return Primary; }
        }
        public static Color SpacerColour
        {
            get { return Color.FromArgb(255, 200, 200, 200); } // Light gray
        }
        public static Brush AnnotationTextDark
        {
            get { return new SolidBrush(Color.FromArgb(255, 45, 45, 45)); } // Dark text
        }
        public static Brush AnnotationTextBright
        {
            get { return new SolidBrush(Color.FromArgb(255, 252, 252, 252)); } // Very light text for better contrast
        }
        public static Brush ClickedButtonColour
        {
            get { return new SolidBrush(Primary_light); }
        }
        public static Brush HoverButtonColour
        {
            get { return new SolidBrush(Color.FromArgb(255, 85, 85, 85)); } // Slightly lighter on hover
        }
        public static Color HoverBorderColour
        {
            get { return Color.FromArgb(255, 100, 100, 100); } // Medium gray on hover
        }

        // Enhanced colors for different button states
        public static Brush DisabledButtonColor
        {
            get { return new SolidBrush(Color.FromArgb(255, 200, 200, 200)); } // Light gray
        }
        public static Color DisabledBorderColour
        {
            get { return Color.FromArgb(255, 180, 180, 180); } // Light gray border
        }
        public static Brush LoadingButtonColor
        {
            get { return new SolidBrush(Color.FromArgb(255, 120, 120, 120)); } // Medium gray
        }
        public static Color LoadingBorderColour
        {
            get { return Color.FromArgb(255, 100, 100, 100); } // Medium gray border
        }

        public static Color WhiteOverlay(Color original, double ratio)
        {
            Color white = Color.White;
            return Color.FromArgb(255,
                (int)(ratio * white.R + (1 - ratio) * original.R),
                (int)(ratio * white.G + (1 - ratio) * original.G),
                (int)(ratio * white.B + (1 - ratio) * original.B));
        }
        public static Color Overlay(Color original, Color overlay, double ratio)
        {
            return Color.FromArgb(255,
                (int)(ratio * overlay.R + (1 - ratio) * original.R),
                (int)(ratio * overlay.G + (1 - ratio) * original.G),
                (int)(ratio * overlay.B + (1 - ratio) * original.B));
        }

    }
}
