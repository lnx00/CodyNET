using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace CodyNET.Frontend.Controls;

public class BreakpointMargin : AbstractMargin
{
    private const double MarginWidth = 18.0;
    private static readonly IBrush BreakpointBrush = new SolidColorBrush(Color.Parse("#E84D4D"));
    private static readonly IPen PendingPen = new Pen(BreakpointBrush, 1.5);
    private static readonly IPen DisabledPen = new Pen(new SolidColorBrush(Color.Parse("#8191B0")), 1.5);

    private readonly Dictionary<int, bool> _breakpoints;
    private readonly Func<int, bool> _isArmed;
    private readonly Action<int> _onToggle;

    /// <param name="breakpoints">Editor line -> enabled.</param>
    /// <param name="isArmed">Whether the breakpoint on a line is active in the debugger. Enabled breakpoints
    /// that are not armed yet (program not assembled since) are drawn as a ring instead of a dot.</param>
    /// <param name="onToggle">Called with the line that was clicked.</param>
    public BreakpointMargin(Dictionary<int, bool> breakpoints, Func<int, bool> isArmed, Action<int> onToggle)
    {
        _breakpoints = breakpoints;
        _isArmed = isArmed;
        _onToggle = onToggle;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    protected override void OnTextViewChanged(TextView? oldValue, TextView? newValue)
    {
        if (oldValue != null)
        {
            oldValue.VisualLinesChanged -= OnViewChanged;
            oldValue.ScrollOffsetChanged -= OnViewChanged;
        }
        base.OnTextViewChanged(oldValue, newValue);
        if (newValue != null)
        {
            newValue.VisualLinesChanged += OnViewChanged;
            newValue.ScrollOffsetChanged += OnViewChanged;
        }
        InvalidateVisual();
    }

    private void OnViewChanged(object? sender, EventArgs e) => InvalidateVisual();

    protected override Size MeasureOverride(Size availableSize) => new(MarginWidth, 0);

    public override void Render(DrawingContext context)
    {
        // Transparent fill gives Avalonia a hit-testable geometry for the whole margin area.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        var textView = TextView;
        if (textView is null || !textView.VisualLinesValid) return;

        var scrollOffsetY = textView.ScrollOffset.Y;

        foreach (var line in textView.VisualLines)
        {
            var lineNumber = line.FirstDocumentLine.LineNumber;
            if (!_breakpoints.TryGetValue(lineNumber, out var enabled))
                continue;

            var y = line.VisualTop - scrollOffsetY;
            var diameter = Math.Min(line.Height - 4, 11.0);
            var x = (MarginWidth - diameter) / 2;
            var cy = y + (line.Height - diameter) / 2;

            if (!enabled)
            {
                DrawRing(context, DisabledPen, x, cy, diameter);
            }
            else if (_isArmed(lineNumber))
            {
                context.FillRectangle(BreakpointBrush,
                    new Rect(x, cy, diameter, diameter),
                    (float)(diameter / 2));
            }
            else
            {
                DrawRing(context, PendingPen, x, cy, diameter);
            }
        }
    }

    private static void DrawRing(DrawingContext context, IPen pen, double x, double y, double diameter)
    {
        var radius = (diameter - pen.Thickness) / 2;
        context.DrawEllipse(null, pen, new Point(x + diameter / 2, y + diameter / 2), radius, radius);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var textView = TextView;
        if (textView is null || !textView.VisualLinesValid) return;

        var clickY = e.GetPosition(this).Y + textView.ScrollOffset.Y;

        foreach (var line in textView.VisualLines)
        {
            if (clickY >= line.VisualTop && clickY < line.VisualTop + line.Height)
            {
                _onToggle(line.FirstDocumentLine.LineNumber);
                break;
            }
        }

        e.Handled = true;
    }
}
