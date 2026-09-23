namespace ACS.Geometry;

/// <summary>
/// Pure-coordinate Bézier sampling kernel shared by the server and the simulator.
/// Owns the only curve-sampling loops in the repository; each host keeps a thin
/// type-conversion adapter in front of it.
/// </summary>
public static class BezierSampler
{
    /// <summary>
    /// Returns the historical ordered samples: <c>[start, end]</c> for null/empty controls or
    /// non-positive <paramref name="segments"/>; <c>segments + 1</c> samples for positive segments
    /// with controls. One control point uses a quadratic Bézier; two or more use only the first
    /// two control points with a cubic Bézier; extra controls do not alter the output.
    /// </summary>
    public static List<(double X, double Y)> Sample(
        double startX, double startY,
        double endX, double endY,
        IReadOnlyList<(double X, double Y)>? controlPoints,
        int segments)
    {
        var points = new List<(double X, double Y)>();

        if (controlPoints == null || controlPoints.Count == 0 || segments <= 0)
        {
            points.Add((startX, startY));
            points.Add((endX, endY));
            return points;
        }

        if (controlPoints.Count == 1)
        {
            // Quadratic Bezier (1 control point)
            double cpX = controlPoints[0].X;
            double cpY = controlPoints[0].Y;

            for (int i = 0; i <= segments; i++)
            {
                double t = i / (double)segments;
                double mt = 1 - t;

                double x = (mt * mt * startX) + (2 * mt * t * cpX) + (t * t * endX);
                double y = (mt * mt * startY) + (2 * mt * t * cpY) + (t * t * endY);

                points.Add((x, y));
            }
        }
        else
        {
            // Cubic Bezier (2+ control points, using first 2)
            double cp1X = controlPoints[0].X;
            double cp1Y = controlPoints[0].Y;
            double cp2X = controlPoints.Count > 1 ? controlPoints[1].X : cp1X;
            double cp2Y = controlPoints.Count > 1 ? controlPoints[1].Y : cp1Y;

            for (int i = 0; i <= segments; i++)
            {
                double t = i / (double)segments;
                double mt = 1 - t;

                double a = mt * mt * mt;
                double b = 3 * mt * mt * t;
                double c = 3 * mt * t * t;
                double d = t * t * t;

                double x = a * startX + b * cp1X + c * cp2X + d * endX;
                double y = a * startY + b * cp1Y + c * cp2Y + d * endY;

                points.Add((x, y));
            }
        }

        return points;
    }
}
