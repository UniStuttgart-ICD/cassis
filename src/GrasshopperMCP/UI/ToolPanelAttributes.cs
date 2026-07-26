using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Attributes;

namespace GrasshopperMCP.UI;

/// <summary>
/// Expandable tool panel below the MCP server component card.
/// Extends ServerAttributes so the start/stop button UI is preserved.
/// </summary>
public class ToolPanelAttributes : ServerAttributes
{
    private bool _isPanelExpanded;
    private readonly HashSet<string> _expandedCategories = new();
    private int _hoveredRowIndex = -1;
    private bool _headerHovered;

    private const float HeaderHeight = 20f;
    private const float CategoryRowHeight = 18f;
    private const float ToolRowHeight = 16f;
    private const float CheckboxSize = 11f;
    private const float Padding = 4f;
    private const float IndentSize = 16f;
    private const float MinPanelWidth = 200f;

    private RectangleF _componentBounds;
    private RectangleF _headerBounds;
    private RectangleF _panelBounds;
    private readonly List<RowInfo> _visibleRows = new();

    private readonly string[] _categoryNames;
    private readonly string[][] _categoryTools;

    private sealed class RowInfo
    {
        public RectangleF Bounds;
        public RectangleF CheckboxBounds;
        public bool IsCategory;
        public string CategoryName = string.Empty;
        public string? ToolName;
        public int CategoryIndex;
    }

    public ToolPanelAttributes(McpListenerComponent owner)
        : base(
            owner,
            owner.GetButtonText,
            owner.HandleButtonClick,
            owner.GetStatusLabel,
            owner.GetUptimeShort,
            owner.GetMessageCountString,
            owner.GetLastMsgShort,
            owner.GetLastToolShort)
    {
        _categoryNames = ToolCategories.Categories.Keys.ToArray();
        _categoryTools = ToolCategories.Categories.Values.ToArray();
    }

    private McpListenerComponent McpOwner => (McpListenerComponent)Owner;

    private (int enabled, int total) GetToolCounts()
    {
        int total = ToolCategories.Categories.Values.Sum(c => c.Length);
        return (McpOwner.EnabledToolCount, total);
    }

    private CheckState GetCategoryCheckState(string[] tools)
    {
        int enabledCount = tools.Count(t => McpOwner.IsToolEnabled(t));
        if (enabledCount == 0) return CheckState.Unchecked;
        if (enabledCount == tools.Length) return CheckState.Checked;
        return CheckState.Indeterminate;
    }

    protected override void Layout()
    {
        base.Layout();
        _componentBounds = Bounds;

        float panelWidth = Math.Max(_componentBounds.Width, MinPanelWidth);
        float headerX = _componentBounds.Left + (_componentBounds.Width - panelWidth) / 2;
        float headerY = _componentBounds.Bottom + 2;

        _headerBounds = new RectangleF(headerX, headerY, panelWidth, HeaderHeight);
        _visibleRows.Clear();

        if (_isPanelExpanded)
        {
            float rowY = headerY + HeaderHeight + Padding;

            for (int catIdx = 0; catIdx < _categoryNames.Length; catIdx++)
            {
                var categoryName = _categoryNames[catIdx];
                var tools = _categoryTools[catIdx];
                bool categoryExpanded = _expandedCategories.Contains(categoryName);

                var catRowBounds = new RectangleF(headerX + Padding, rowY, panelWidth - (Padding * 2), CategoryRowHeight);
                var catCheckBounds = new RectangleF(
                    catRowBounds.Left + 14,
                    rowY + ((CategoryRowHeight - CheckboxSize) / 2),
                    CheckboxSize, CheckboxSize);

                _visibleRows.Add(new RowInfo
                {
                    Bounds = catRowBounds,
                    CheckboxBounds = catCheckBounds,
                    IsCategory = true,
                    CategoryName = categoryName,
                    ToolName = null,
                    CategoryIndex = catIdx,
                });
                rowY += CategoryRowHeight;

                if (categoryExpanded)
                {
                    foreach (var tool in tools)
                    {
                        var toolRowBounds = new RectangleF(
                            headerX + Padding + IndentSize,
                            rowY,
                            panelWidth - (Padding * 2) - IndentSize,
                            ToolRowHeight);
                        var toolCheckBounds = new RectangleF(
                            toolRowBounds.Left + 2,
                            rowY + ((ToolRowHeight - CheckboxSize) / 2),
                            CheckboxSize, CheckboxSize);

                        _visibleRows.Add(new RowInfo
                        {
                            Bounds = toolRowBounds,
                            CheckboxBounds = toolCheckBounds,
                            IsCategory = false,
                            CategoryName = categoryName,
                            ToolName = tool,
                            CategoryIndex = catIdx,
                        });
                        rowY += ToolRowHeight;
                    }
                }
            }

            float panelHeight = rowY - (headerY + HeaderHeight) + Padding;
            _panelBounds = new RectangleF(headerX, headerY + HeaderHeight, panelWidth, panelHeight);
        }
        else
        {
            _panelBounds = RectangleF.Empty;
        }
    }

    public override bool IsPickRegion(PointF point)
    {
        if (_componentBounds.Contains(point)) return true;
        if (_headerBounds.Contains(point)) return true;
        if (_isPanelExpanded && _panelBounds.Contains(point)) return true;
        return false;
    }

    protected override void Render(GH_Canvas canvas, Graphics graphics, GH_CanvasChannel channel)
    {
        base.Render(canvas, graphics, channel);

        if (channel != GH_CanvasChannel.Objects) return;

        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        RenderHeader(graphics);

        if (_isPanelExpanded)
            RenderPanel(graphics);
    }

    private void RenderHeader(Graphics graphics)
    {
        var (enabled, total) = GetToolCounts();
        string headerText = $"Tools: {enabled}/{total} enabled";

        var headerColor = _headerHovered ? ToolPanelColours.HeaderBackgroundHover : ToolPanelColours.HeaderBackground;
        using (var brush = new SolidBrush(headerColor))
        using (var path = RoundedRect(_headerBounds, 3))
            graphics.FillPath(brush, path);

        using (var pen = new Pen(ToolPanelColours.HeaderBorder, 1))
        using (var path = RoundedRect(_headerBounds, 3))
            graphics.DrawPath(pen, path);

        float arrowSize = 6;
        float arrowX = _headerBounds.Left + Padding + 2;
        float arrowY = _headerBounds.Top + ((_headerBounds.Height - arrowSize) / 2);

        using (var brush = new SolidBrush(ToolPanelColours.ArrowColor))
        {
            PointF[] arrow = _isPanelExpanded
                ? new[] { new PointF(arrowX, arrowY), new PointF(arrowX + arrowSize, arrowY), new PointF(arrowX + (arrowSize / 2), arrowY + arrowSize) }
                : new[] { new PointF(arrowX, arrowY), new PointF(arrowX + arrowSize, arrowY + (arrowSize / 2)), new PointF(arrowX, arrowY + arrowSize) };
            graphics.FillPolygon(brush, arrow);
        }

        float textX = arrowX + arrowSize + 6;
        using (var brush = new SolidBrush(ToolPanelColours.HeaderText))
        using (var font = GH_FontServer.NewFont(GH_FontServer.Standard, 8f / GH_GraphicsUtil.UiScale))
        {
            var textBounds = new RectangleF(textX, _headerBounds.Top, _headerBounds.Right - textX - Padding, _headerBounds.Height);
            var format = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
            graphics.DrawString(headerText, font, brush, textBounds, format);
        }
    }

    private void RenderPanel(Graphics graphics)
    {
        using (var brush = new SolidBrush(ToolPanelColours.PanelBackground))
        using (var path = RoundedRect(_panelBounds, 3))
            graphics.FillPath(brush, path);

        using (var pen = new Pen(ToolPanelColours.PanelBorder, 1))
        using (var path = RoundedRect(_panelBounds, 3))
            graphics.DrawPath(pen, path);

        for (int i = 0; i < _visibleRows.Count; i++)
            RenderRow(graphics, i);
    }

    private void RenderRow(Graphics graphics, int index)
    {
        var row = _visibleRows[index];
        bool isHovered = _hoveredRowIndex == index;

        if (isHovered)
        {
            using var brush = new SolidBrush(ToolPanelColours.RowBackgroundHover);
            graphics.FillRectangle(brush, row.Bounds);
        }

        if (row.IsCategory)
            RenderCategoryRow(graphics, row, isHovered);
        else
            RenderToolRow(graphics, row, isHovered);
    }

    private void RenderCategoryRow(Graphics graphics, RowInfo row, bool isHovered)
    {
        var tools = _categoryTools[row.CategoryIndex];
        var checkState = GetCategoryCheckState(tools);
        bool isExpanded = _expandedCategories.Contains(row.CategoryName);
        int enabledCount = tools.Count(t => McpOwner.IsToolEnabled(t));

        float arrowSize = 5;
        float arrowX = row.Bounds.Left + 2;
        float arrowY = row.Bounds.Top + ((row.Bounds.Height - arrowSize) / 2);

        using (var brush = new SolidBrush(ToolPanelColours.ArrowColor))
        {
            PointF[] arrow = isExpanded
                ? new[] { new PointF(arrowX, arrowY), new PointF(arrowX + arrowSize, arrowY), new PointF(arrowX + (arrowSize / 2), arrowY + arrowSize) }
                : new[] { new PointF(arrowX, arrowY), new PointF(arrowX + arrowSize, arrowY + (arrowSize / 2)), new PointF(arrowX, arrowY + arrowSize) };
            graphics.FillPolygon(brush, arrow);
        }

        DrawCheckbox(graphics, row.CheckboxBounds, checkState, isHovered);

        float textX = row.CheckboxBounds.Right + 6;
        using (var brush = new SolidBrush(ToolPanelColours.RowText))
        using (var font = GH_FontServer.NewFont(GH_FontServer.StandardBold, 7.5f / GH_GraphicsUtil.UiScale))
        {
            var textBounds = new RectangleF(textX, row.Bounds.Top, row.Bounds.Right - textX - 40, row.Bounds.Height);
            var format = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
            graphics.DrawString(row.CategoryName, font, brush, textBounds, format);
        }

        string countText = $"{enabledCount}/{tools.Length}";
        using (var brush = new SolidBrush(ToolPanelColours.CountText))
        using (var font = GH_FontServer.NewFont(GH_FontServer.Standard, 7f / GH_GraphicsUtil.UiScale))
        {
            var countBounds = new RectangleF(row.Bounds.Right - 38, row.Bounds.Top, 34, row.Bounds.Height);
            var format = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
            graphics.DrawString(countText, font, brush, countBounds, format);
        }
    }

    private void RenderToolRow(Graphics graphics, RowInfo row, bool isHovered)
    {
        bool isEnabled = McpOwner.IsToolEnabled(row.ToolName!);
        var checkState = isEnabled ? CheckState.Checked : CheckState.Unchecked;

        DrawCheckbox(graphics, row.CheckboxBounds, checkState, isHovered);

        float textX = row.CheckboxBounds.Right + 6;
        var textColor = isEnabled ? ToolPanelColours.RowText : ToolPanelColours.RowTextDisabled;
        using (var brush = new SolidBrush(textColor))
        using (var font = GH_FontServer.NewFont(GH_FontServer.Standard, 7f / GH_GraphicsUtil.UiScale))
        {
            var textBounds = new RectangleF(textX, row.Bounds.Top, row.Bounds.Right - textX - Padding, row.Bounds.Height);
            var format = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
            graphics.DrawString(row.ToolName, font, brush, textBounds, format);
        }
    }

    private static void DrawCheckbox(Graphics graphics, RectangleF bounds, CheckState state, bool isHovered)
    {
        var borderColor = isHovered ? ToolPanelColours.CheckboxBorderHover : ToolPanelColours.CheckboxBorder;

        using (var bgBrush = new SolidBrush(ToolPanelColours.CheckboxBackground))
            graphics.FillRectangle(bgBrush, bounds);

        using (var pen = new Pen(borderColor, 1))
            graphics.DrawRectangle(pen, bounds.X, bounds.Y, bounds.Width, bounds.Height);

        if (state == CheckState.Checked)
        {
            using var brush = new SolidBrush(ToolPanelColours.CheckboxFill);
            var inner = new RectangleF(bounds.X + 2, bounds.Y + 2, bounds.Width - 4, bounds.Height - 4);
            graphics.FillRectangle(brush, inner);
        }
        else if (state == CheckState.Indeterminate)
        {
            using var brush = new SolidBrush(ToolPanelColours.CheckboxFillIndeterminate);
            float midY = bounds.Y + (bounds.Height / 2);
            graphics.FillRectangle(brush, bounds.X + 2, midY - 1.5f, bounds.Width - 4, 3);
        }
    }

    private static GraphicsPath RoundedRect(RectangleF bounds, float radius)
    {
        float diameter = radius * 2;
        var path = new GraphicsPath();

        if (radius <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    public override GH_ObjectResponse RespondToMouseDown(GH_Canvas sender, GH_CanvasMouseEvent e)
    {
        if (e.Button == MouseButtons.Left)
        {
            if (_headerBounds.Contains(e.CanvasLocation))
            {
                _isPanelExpanded = !_isPanelExpanded;
                ExpireLayout();
                sender.Refresh();
                return GH_ObjectResponse.Handled;
            }

            if (_isPanelExpanded)
            {
                for (int i = 0; i < _visibleRows.Count; i++)
                {
                    var row = _visibleRows[i];
                    if (!row.Bounds.Contains(e.CanvasLocation)) continue;

                    if (row.IsCategory)
                    {
                        if (row.CheckboxBounds.Contains(e.CanvasLocation) ||
                            (e.CanvasLocation.X >= row.CheckboxBounds.Left && e.CanvasLocation.X <= row.CheckboxBounds.Right + 50))
                        {
                            McpOwner.ToggleCategory(row.CategoryName, _categoryTools[row.CategoryIndex]);
                        }
                        else
                        {
                            if (!_expandedCategories.Add(row.CategoryName))
                                _expandedCategories.Remove(row.CategoryName);
                            ExpireLayout();
                        }
                    }
                    else
                    {
                        McpOwner.ToggleTool(row.ToolName!);
                    }

                    sender.Refresh();
                    return GH_ObjectResponse.Handled;
                }
            }
        }

        return base.RespondToMouseDown(sender, e);
    }

    public override GH_ObjectResponse RespondToMouseMove(GH_Canvas sender, GH_CanvasMouseEvent e)
    {
        bool needsRefresh = false;
        bool headerHovered = _headerBounds.Contains(e.CanvasLocation);
        if (headerHovered != _headerHovered)
        {
            _headerHovered = headerHovered;
            needsRefresh = true;
        }

        if (_isPanelExpanded)
        {
            int newHoveredIndex = -1;
            for (int i = 0; i < _visibleRows.Count; i++)
            {
                if (_visibleRows[i].Bounds.Contains(e.CanvasLocation))
                {
                    newHoveredIndex = i;
                    break;
                }
            }

            if (newHoveredIndex != _hoveredRowIndex)
            {
                _hoveredRowIndex = newHoveredIndex;
                needsRefresh = true;
            }
        }

        if (needsRefresh)
            sender.Refresh();

        if (_headerHovered || _hoveredRowIndex >= 0)
        {
            Grasshopper.Instances.CursorServer.AttachCursor(sender, "GH_HandCursor");
            return GH_ObjectResponse.Handled;
        }

        return base.RespondToMouseMove(sender, e);
    }
}
