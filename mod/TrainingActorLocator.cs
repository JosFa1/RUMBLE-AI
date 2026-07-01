using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace AI_Train;

internal static class TrainingActorLocator
{
    internal static readonly string[] HeadCandidates =
    {
        "Headset Offset/Headset",
        "Headset",
        "Head",
        "HeadPivot",
        "Camera",
        "Visuals/Head",
        "Visuals/Headset"
    };

    internal static readonly string[] LeftHandCandidates =
    {
        "Left Controller/IkTarget/InteractionHand",
        "Left Controller/IkTarget",
        "Left Controller/InteractionHand",
        "Left Controller/LeftHand",
        "Left Controller/Left Hand",
        "Visuals/Left",
        "LeftHand",
        "Left Hand"
    };

    internal static readonly string[] RightHandCandidates =
    {
        "Right Controller/IkTarget/InteractionHand",
        "Right Controller/IkTarget",
        "Right Controller/InteractionHand",
        "Right Controller/RightHand",
        "Right Controller/Right Hand",
        "Visuals/Right",
        "RightHand",
        "Right Hand"
    };

    private const float ReachClampX = 0.85f;
    private const float ReachClampY = 1.6f;
    private const float ReachClampZ = 0.85f;
    private const float ReachMagnitude = 1.15f;

    public static bool TryResolveHandTransforms(Transform playerRoot, out ResolvedTransform leftHand, out ResolvedTransform rightHand)
    {
        leftHand = Resolve(playerRoot, LeftHandCandidates);
        rightHand = Resolve(playerRoot, RightHandCandidates);
        return leftHand.Transform != null || rightHand.Transform != null;
    }

    public static ResolvedTransform Resolve(Transform root, IEnumerable<string> candidates)
    {
        if (root == null || candidates == null)
        {
            return default;
        }

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var direct = root.Find(candidate);
            if (direct != null)
            {
                return new ResolvedTransform(direct);
            }

            var fallback = FindDescendantByName(root, candidate.Split('/').Last());
            if (fallback != null)
            {
                return new ResolvedTransform(fallback);
            }
        }

        return default;
    }

    public static bool TryReadLocalVector3(float[] values, out Vector3 vector, out string error)
    {
        vector = default;
        error = null;

        if (values == null)
        {
            error = "missing_vector";
            return false;
        }

        if (values.Length != 3)
        {
            error = "vector_length";
            return false;
        }

        vector = new Vector3(values[0], values[1], values[2]);
        if (float.IsNaN(vector.x) || float.IsNaN(vector.y) || float.IsNaN(vector.z) ||
            float.IsInfinity(vector.x) || float.IsInfinity(vector.y) || float.IsInfinity(vector.z))
        {
            error = "vector_nan";
            return false;
        }

        return true;
    }

    public static Vector3 ClampLocalTarget(Vector3 target, out bool clamped)
    {
        var clampedTarget = new Vector3(
            Mathf.Clamp(target.x, -ReachClampX, ReachClampX),
            Mathf.Clamp(target.y, -0.35f, ReachClampY),
            Mathf.Clamp(target.z, -ReachClampZ, ReachClampZ));

        if ((clampedTarget - target).sqrMagnitude > 0.0001f)
        {
            clamped = true;
            return ClampMagnitude(clampedTarget, ReachMagnitude);
        }

        var magnitudeClamped = ClampMagnitude(clampedTarget, ReachMagnitude);
        clamped = (magnitudeClamped - target).sqrMagnitude > 0.0001f;
        return magnitudeClamped;
    }

    public static Vector3 ToWorldTarget(Transform playerRoot, Vector3 localTarget)
    {
        if (playerRoot == null)
        {
            return localTarget;
        }

        return playerRoot.TransformPoint(localTarget);
    }

    public static string GetPath(Transform transform)
    {
        if (transform == null)
        {
            return string.Empty;
        }

        var parts = new Stack<string>();
        while (transform != null)
        {
            parts.Push(transform.name);
            transform = transform.parent;
        }

        return string.Join("/", parts);
    }

    private static Transform FindDescendantByName(Transform root, string name)
    {
        if (root == null || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var queue = new Queue<Transform>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current != null && string.Equals(current.name, name, StringComparison.OrdinalIgnoreCase))
            {
                return current;
            }

            var childCount = current != null ? current.childCount : 0;
            for (var i = 0; i < childCount; i++)
            {
                queue.Enqueue(current.GetChild(i));
            }
        }

        return null;
    }

    private static Vector3 ClampMagnitude(Vector3 value, float maxMagnitude)
    {
        if (value.sqrMagnitude <= maxMagnitude * maxMagnitude)
        {
            return value;
        }

        return value.normalized * maxMagnitude;
    }

    internal readonly struct ResolvedTransform
    {
        public ResolvedTransform(Transform transform)
        {
            Transform = transform;
            Path = GetPath(transform);
        }

        public Transform Transform { get; }
        public string Path { get; }
    }
}
