using UnityEngine;

public static class UmbilicTorus
{
    /// <summary>
    /// Returns a point along the umbilical path of a torus. For a triangle (S=3), the polygon sweeps 3 times around the Y axis,
    /// with a roll of π/6 radians per π/2 sweep angle (or equivalently, 2π/3 roll per 2π sweep).
    /// </summary>
    /// <param name="S">Number of sides in the polygon cross-section</param>
    /// <param name="edgeLength">Length of each polygon edge</param>
    /// <param name="R">Major radius of the torus (distance from center to polygon centers)</param>
    /// <param name="t">Parameter along umbilical path (0 to 1)</param>
    /// <param name="offsetAlpha">Starting rotation offset to determine center point</param>
    /// <param name="rotM">Rotation multiplier to induce extra turns</param>
    /// <returns>Point along the umbilical path</returns>
    public static Vector3 PointAlongUmbilical(int S, float edgeLength, float R, float t, float offsetAlpha,
        int rotM = 1)
    {
        // Calculate the radius of the circumscribed circle of the polygon
        float polyRad = edgeLength / (2f * Mathf.Sin(Mathf.PI / S));
        float r = R + polyRad; // radius to polygon centers

        // Convert t to angle (negative for clockwise sweep, multiplied by S for S sweeps)
        float angle = -2f * Mathf.PI * S * t;

        // Calculate center position at this t
        Vector3 center = new(
            r * Mathf.Cos(angle),
            0f,
            r * Mathf.Sin(angle)
        );

        // Calculate the vertex position that lies on the umbilical
        float vertexAngle = (2f * Mathf.PI * t + offsetAlpha) * rotM; // Complete one rotation over the entire path
        Vector3 localVertex = new(
            polyRad * Mathf.Cos(vertexAngle),
            polyRad * Mathf.Sin(vertexAngle),
            0f
        );

        // Rotate around Y axis (clockwise looking down)
        Quaternion yRotation = Quaternion.Euler(0f, -angle * Mathf.Rad2Deg, 0f);

        return center + yRotation * localVertex;
    }
}