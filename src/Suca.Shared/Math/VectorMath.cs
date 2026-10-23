using System;

namespace SUCA_Compressor.Shared.Math
{
    public static class VectorMath
    {
        public static double CosineSimilarity(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
        {
            if (left.Length == 0 || left.Length != right.Length) return 0;

            double dot = 0;
            double leftNorm = 0;
            double rightNorm = 0;
            for (int i = 0; i < left.Length; i++)
            {
                dot += left[i] * right[i];
                leftNorm += left[i] * left[i];
                rightNorm += right[i] * right[i];
            }

            return leftNorm <= 0 || rightNorm <= 0
                ? 0
                : dot / (System.Math.Sqrt(leftNorm) * System.Math.Sqrt(rightNorm));
        }
    }
}
