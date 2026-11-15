using System;
using UnityEngine;

namespace TushonkaHorse
{
    /// <summary>One bone's place in its parent: position, rotation, scale.</summary>
    internal struct Pose
    {
        public Vector3 P, S;
        public Quaternion Q;

        public static Pose Blend(in Pose a, in Pose b, float t)
            => new Pose { P = Vector3.LerpUnclamped(a.P, b.P, t), Q = Quaternion.SlerpUnclamped(a.Q, b.Q, t), S = Vector3.LerpUnclamped(a.S, b.S, t) };
    }

    /// <summary>Rotations as vectors, for adding and fading them. Only plain arithmetic (safe off the main thread).</summary>
    internal static class PoseMath
    {
        public static Quaternion Mul(Quaternion a, Quaternion b)
            => new Quaternion(a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y, a.w * b.y + a.y * b.w + a.z * b.x - a.x * b.z,
                              a.w * b.z + a.z * b.w + a.x * b.y - a.y * b.x, a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z);

        public static Quaternion Conjugate(Quaternion q) => new Quaternion(-q.x, -q.y, -q.z, q.w);

        // The rotation as axis times angle (radians), the short way round.
        public static Vector3 Log(Quaternion q)
        {
            if (q.w < 0) { q.x = -q.x; q.y = -q.y; q.z = -q.z; q.w = -q.w; }
            float s = (float)Math.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z);
            if (s < 1e-5f) return new Vector3(q.x * 2, q.y * 2, q.z * 2);
            float k = 2 * (float)Math.Atan2(s, q.w) / s;
            return new Vector3(q.x * k, q.y * k, q.z * k);
        }

        public static Quaternion Exp(Vector3 r)
        {
            float a = (float)Math.Sqrt(r.x * r.x + r.y * r.y + r.z * r.z);
            if (a < 1e-5f) return new Quaternion(r.x * .5f, r.y * .5f, r.z * .5f, 1);
            float s = (float)Math.Sin(a * .5f) / a;
            return new Quaternion(r.x * s, r.y * s, r.z * s, (float)Math.Cos(a * .5f));
        }

        public static float Smooth(float t) { t = t < 0 ? 0 : t > 1 ? 1 : t; return t * t * (3 - 2 * t); }
    }
}
