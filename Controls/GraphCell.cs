using System.Windows;
using System.Windows.Media;
using CompleteGit.Git;

namespace CompleteGit.Controls;

/// <summary>Draws one row of the commit graph (lanes, merges, node).</summary>
public sealed class GraphCell : FrameworkElement
{
    public const double LaneWidth = 14;

    private static readonly Color[] Palette =
    {
        Color.FromRgb(0x4F, 0xC1, 0xFF), Color.FromRgb(0x89, 0xD1, 0x85), Color.FromRgb(0xE2, 0xC0, 0x8D),
        Color.FromRgb(0xC5, 0x86, 0xC0), Color.FromRgb(0xF4, 0x87, 0x71), Color.FromRgb(0x4E, 0xC9, 0xB0),
        Color.FromRgb(0xDC, 0xDC, 0xAA), Color.FromRgb(0x9C, 0xDC, 0xFE),
    };
    private static readonly Pen[] Pens = Palette.Select(c => { var p = new Pen(new SolidColorBrush(c), 2); p.Freeze(); return p; }).ToArray();
    private static readonly Brush[] Brushes = Palette.Select(c => { var b = new SolidColorBrush(c); b.Freeze(); return (Brush)b; }).ToArray();
    private static readonly Brush NodeFill = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));

    public static readonly DependencyProperty RowProperty = DependencyProperty.Register(
        nameof(Row), typeof(GraphRow), typeof(GraphCell), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public GraphRow? Row
    {
        get => (GraphRow?)GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    static GraphCell() { NodeFill.Freeze(); }

    protected override void OnRender(DrawingContext dc)
    {
        var row = Row;
        if (row == null) return;
        double h = ActualHeight;
        if (h <= 0) h = 20;
        double mid = h / 2;
        static double X(int lane) => lane * LaneWidth + LaneWidth / 2;

        foreach (var e in row.Edges)
        {
            var pen = Pens[e.Color % Pens.Length];
            switch (e.Kind)
            {
                case EdgeKind.Through:
                    dc.DrawLine(pen, new Point(X(e.From), 0), new Point(X(e.From), h));
                    break;
                case EdgeKind.In:
                    if (e.From == e.To) dc.DrawLine(pen, new Point(X(e.From), 0), new Point(X(e.From), mid));
                    else DrawCurve(dc, pen, X(e.From), 0, X(e.To), mid);
                    break;
                case EdgeKind.Out:
                    if (e.From == e.To) dc.DrawLine(pen, new Point(X(e.From), mid), new Point(X(e.From), h));
                    else DrawCurve(dc, pen, X(e.From), mid, X(e.To), h);
                    break;
            }
        }

        var brush = Brushes[row.NodeColor % Brushes.Length];
        var center = new Point(X(row.NodeLane), mid);
        if (row.IsMerge) dc.DrawEllipse(NodeFill, new Pen(brush, 2), center, 3.5, 3.5);
        else dc.DrawEllipse(brush, null, center, 4, 4);
    }

    private static void DrawCurve(DrawingContext dc, Pen pen, double x1, double y1, double x2, double y2)
    {
        var fig = new PathFigure { StartPoint = new Point(x1, y1) };
        double cy = (y1 + y2) / 2;
        fig.Segments.Add(new BezierSegment(new Point(x1, cy), new Point(x2, cy), new Point(x2, y2), true));
        var geo = new PathGeometry(new[] { fig });
        dc.DrawGeometry(null, pen, geo);
    }
}
