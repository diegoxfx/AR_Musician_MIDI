using System;
using UnityEngine;

internal enum PianoDetectionIssue
{
    None,
    MissingCorners,
    InvalidCorner,
    ClippedCorner,
    LowConfidence,
    InvalidGeometry
}

internal static class PianoDetectionValidator
{
    public const float DefaultMinimumConfidence = 0.1f;
    private const int CornerCount = 4;
    private const float MinimumArea = 4f;

    public static PianoDetectionIssue Validate(Vector2[] corners, float[] confidences, int width, int height,
        float minimumConfidence)
    {
        if (corners == null || corners.Length != CornerCount)
            return PianoDetectionIssue.MissingCorners;

        foreach (Vector2 corner in corners)
        {
            if (!(corner.x >= 0f && corner.x < width && corner.y >= 0f && corner.y < height))
                return PianoDetectionIssue.InvalidCorner;

            if (corner.x < 1f || corner.x >= width - 1 || corner.y < 1f || corner.y >= height - 1)
                return PianoDetectionIssue.ClippedCorner;
        }

        if (confidences == null || confidences.Length != CornerCount)
            return PianoDetectionIssue.MissingCorners;

        foreach (float confidence in confidences)
        {
            if (!(confidence >= minimumConfidence && confidence <= 1f))
                return PianoDetectionIssue.LowConfidence;
        }

        float twiceArea = 0f;
        float firstTurn = 0f;
        for (int i = 0; i < CornerCount; i++)
        {
            Vector2 a = corners[i];
            Vector2 b = corners[(i + 1) % CornerCount];
            Vector2 c = corners[(i + 2) % CornerCount];
            float dx = b.x - a.x;
            float dy = b.y - a.y;
            float turn = dx * (c.y - b.y) - dy * (c.x - b.x);
            if (dx * dx + dy * dy < 1f || turn == 0f || (i > 0 && turn * firstTurn <= 0f))
                return PianoDetectionIssue.InvalidGeometry;

            if (i == 0) firstTurn = turn;
            twiceArea += a.x * b.y - a.y * b.x;
        }

        return Math.Abs(twiceArea) < 2f * MinimumArea ? PianoDetectionIssue.InvalidGeometry : PianoDetectionIssue.None;
    }
}
