using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Rendering;

namespace CodyNET.Frontend.Controls;

/// <summary>
/// Draws a red highlight behind source lines that the assembler reported errors for.
/// Register it via TextArea.TextView.BackgroundRenderers.Add(renderer).
/// </summary>
public class ErrorLineRenderer : IBackgroundRenderer
{
    private static readonly IBrush HighlightBrush =
        new SolidColorBrush(Color.FromArgb(70, 255, 40, 40));   // semi-transparent red

    private static readonly IPen MarkerPen =
        new Pen(new SolidColorBrush(Color.FromArgb(220, 230, 40, 40)), 3);

    /// <summary>1-based editor line number -> assembler messages reported for that line.</summary>
    public Dictionary<int, List<string>> Messages { get; } = new();

    public KnownLayer Layer => KnownLayer.Background;

    public void Add(int line, string message)
    {
        if (!Messages.TryGetValue(line, out var list))
            Messages[line] = list = new List<string>();
        list.Add(message);
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (Messages.Count == 0 || !textView.VisualLinesValid) return;

        foreach (var vl in textView.VisualLines)
        {
            if (!Messages.ContainsKey(vl.FirstDocumentLine.LineNumber)) continue;

            var y    = vl.VisualTop - textView.ScrollOffset.Y;
            var rect = new Rect(0, y, textView.Bounds.Width, vl.Height);
            drawingContext.FillRectangle(HighlightBrush, rect);
            // Solid bar on the left edge so the line stays visible under the yellow PC highlight.
            drawingContext.DrawLine(MarkerPen, new Point(1.5, y), new Point(1.5, y + vl.Height));
        }
    }
}
