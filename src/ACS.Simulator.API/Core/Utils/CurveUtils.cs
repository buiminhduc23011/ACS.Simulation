using ACS.Geometry;
using ACS.Simulator.API.Core.Models;
using System.Collections.Generic;

namespace ACS.Simulator.API.Core.Utils;

public static class CurveUtils
{
    public static List<(double X, double Y)> GenerateCurvePoints(
        double startX, double startY, 
        double endX, double endY, 
        List<ControlPoint> controlPoints, 
        int segments = 30)
    {
        // Read at most the first two controls, exactly like the sampler's own branches, so
        // non-positive segments and controls after index 1 are never dereferenced (historical behavior).
        List<(double X, double Y)>? coordinates = null;
        if (segments > 0 && controlPoints != null && controlPoints.Count > 0)
        {
            coordinates = new List<(double X, double Y)>
            {
                (controlPoints[0].X, controlPoints[0].Y)
            };
            if (controlPoints.Count > 1)
            {
                coordinates.Add((controlPoints[1].X, controlPoints[1].Y));
            }
        }

        return BezierSampler.Sample(startX, startY, endX, endY, coordinates, segments);
    }
}
