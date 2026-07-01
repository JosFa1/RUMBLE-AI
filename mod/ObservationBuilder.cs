using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;
using UnityEngine;

namespace AI_Train;

internal sealed class ObservationBuilder
{
    private static readonly string[] HeadCandidates =
    {
        "Headset Offset/Headset",
        "Headset",
        "Head",
        "HeadPivot",
        "Camera",
        "Visuals/Head",
        "Visuals/Headset"
    };

    private static readonly string[] LeftHandCandidates =
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

    private static readonly string[] RightHandCandidates =
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

    private static readonly string[] HealthNameHints =
    {
        "health",
        "currenthealth",
        "maxhealth",
        "hitpoints",
        "hp"
    };

    private readonly Action<string> _logInfo;
    private readonly Action<string> _logWarn;
    private readonly HashSet<string> _missingLogged = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _resolvedLogged = new(StringComparer.OrdinalIgnoreCase);

    public ObservationBuilder(Action<string> logInfo, Action<string> logWarn)
    {
        _logInfo = logInfo ?? (_ => { });
        _logWarn = logWarn ?? (_ => { });
    }

    public TrainingObservation BuildObservation(TrainingEnvironmentManager manager, string reason = null)
    {
        var observation = new TrainingObservation
        {
            protocolVersion = TrainingProtocol.Version,
            tick = Time.frameCount,
            timeSeconds = Time.unscaledTime,
            warnings = new List<string>()
        };

        if (manager == null)
        {
            observation.error = "TrainingEnvironmentManager is unavailable.";
            LogMissingOnce("manager", observation.error);
            return observation;
        }

        var status = manager.GetStatus();
        observation.sceneReady = status.SceneReady;
        observation.episodeId = status.CurrentEpisodeId;
        observation.episodeStep = status.CurrentEpisodeStepCount;

        if (!status.SceneReady)
        {
            observation.error = "Training scene is not ready.";
            LogMissingOnce("sceneReady", observation.error);
            return observation;
        }

        var playerRoot = manager.CurrentPlayerRoot;
        if (playerRoot == null)
        {
            observation.error = "Current player root is missing.";
            observation.warnings.Add(observation.error);
            LogMissingOnce("playerRoot", observation.error);
            return observation;
        }

        var rootTransform = playerRoot.transform;
        if (rootTransform == null)
        {
            observation.error = "Player root transform is missing.";
            observation.warnings.Add(observation.error);
            LogMissingOnce("playerRootTransform", observation.error);
            return observation;
        }

        LogResolvedOnce("root", GetPath(rootTransform));
        observation.rootPosition = ToVector3(rootTransform.position);
        observation.rootRotation = ToQuaternion(rootTransform.rotation);

        var head = ResolveTransform(rootTransform, "head", HeadCandidates);
        if (head != null)
        {
            LogResolvedOnce("head", GetPath(head));
            observation.headPosition = ToVector3(head.position);
            observation.headRotation = ToQuaternion(head.rotation);
        }
        else
        {
            var warning = $"Head transform missing under {GetPath(rootTransform)}.";
            observation.warnings.Add(warning);
            LogMissingOnce("head", warning);
        }

        var leftHand = ResolveTransform(rootTransform, "leftHand", LeftHandCandidates);
        if (leftHand != null)
        {
            LogResolvedOnce("leftHand", GetPath(leftHand));
            observation.leftHandPosition = ToVector3(leftHand.position);
            observation.leftHandRotation = ToQuaternion(leftHand.rotation);
        }
        else
        {
            var warning = $"Left hand transform missing under {GetPath(rootTransform)}.";
            observation.warnings.Add(warning);
            LogMissingOnce("leftHand", warning);
        }

        var rightHand = ResolveTransform(rootTransform, "rightHand", RightHandCandidates);
        if (rightHand != null)
        {
            LogResolvedOnce("rightHand", GetPath(rightHand));
            observation.rightHandPosition = ToVector3(rightHand.position);
            observation.rightHandRotation = ToQuaternion(rightHand.rotation);
        }
        else
        {
            var warning = $"Right hand transform missing under {GetPath(rootTransform)}.";
            observation.warnings.Add(warning);
            LogMissingOnce("rightHand", warning);
        }

        if (TryReadHealth(rootTransform, out var health, out var healthSource))
        {
            observation.health = health;
            LogResolvedOnce("health", healthSource);
        }
        else
        {
            var warning = $"Health value not found under {GetPath(rootTransform)}.";
            observation.warnings.Add(warning);
            LogMissingOnce("health", warning);
        }

        if (!string.IsNullOrWhiteSpace(reason))
        {
            _logInfo($"ObservationBuilder built observation ({reason}). warnings={observation.warnings.Count} error={OrNone(observation.error)}");
        }
        else
        {
            _logInfo($"ObservationBuilder built observation. warnings={observation.warnings.Count} error={OrNone(observation.error)}");
        }

        return observation;
    }

    private void LogResolvedOnce(string key, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (_resolvedLogged.TryGetValue(key, out var previous) &&
            string.Equals(previous, path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _resolvedLogged[key] = path;
        _missingLogged.Remove(key);
        _logInfo($"ObservationBuilder resolved {key} path: {path}");
    }

    private void LogMissingOnce(string key, string message)
    {
        if (!_missingLogged.Add(key))
        {
            return;
        }

        _logWarn($"ObservationBuilder missing {key}: {message}");
    }

    private static Transform ResolveTransform(Transform root, string key, IEnumerable<string> candidates)
    {
        if (root == null)
        {
            return null;
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
                return direct;
            }

            var fallback = FindDescendantByName(root, candidate.Split('/').Last());
            if (fallback != null)
            {
                return fallback;
            }
        }

        return null;
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

    private static bool TryReadHealth(Transform root, out float health, out string source)
    {
        health = default;
        source = null;

        if (root == null)
        {
            return false;
        }

        var components = root.GetComponentsInChildren<Component>(true);
        foreach (var component in components)
        {
            if (component == null)
            {
                continue;
            }

            var type = component.GetType();
            var bindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var fields = type.GetFields(bindingFlags);
            foreach (var field in fields)
            {
                if (!IsHealthMember(field.Name))
                {
                    continue;
                }

                if (TryReadNumeric(field.GetValue(component), out health))
                {
                    source = $"{GetPath(component.transform)}.{field.Name}";
                    return true;
                }
            }

            var properties = type.GetProperties(bindingFlags);
            foreach (var property in properties)
            {
                if (!property.CanRead || property.GetIndexParameters().Length > 0 || !IsHealthMember(property.Name))
                {
                    continue;
                }

                try
                {
                    var value = property.GetValue(component);
                    if (TryReadNumeric(value, out health))
                    {
                        source = $"{GetPath(component.transform)}.{property.Name}";
                        return true;
                    }
                }
                catch
                {
                    // Reflection is best effort only.
                }
            }
        }

        return false;
    }

    private static bool IsHealthMember(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var lower = name.ToLowerInvariant();
        return HealthNameHints.Any(hint => lower.Contains(hint));
    }

    private static bool TryReadNumeric(object value, out float numeric)
    {
        numeric = default;

        if (value == null)
        {
            return false;
        }

        try
        {
            switch (value)
            {
                case float f:
                    numeric = f;
                    return true;
                case double d:
                    numeric = (float)d;
                    return true;
                case int i:
                    numeric = i;
                    return true;
                case long l:
                    numeric = l;
                    return true;
                case short s:
                    numeric = s;
                    return true;
                case byte b:
                    numeric = b;
                    return true;
                case decimal m:
                    numeric = (float)m;
                    return true;
                default:
                    if (value is IConvertible convertible)
                    {
                        numeric = convertible.ToSingle(CultureInfo.InvariantCulture);
                        return true;
                    }

                    return false;
            }
        }
        catch
        {
            return false;
        }
    }

    private static ObservationVector3 ToVector3(Vector3 value)
    {
        return new ObservationVector3
        {
            x = value.x,
            y = value.y,
            z = value.z
        };
    }

    private static ObservationQuaternion ToQuaternion(Quaternion value)
    {
        return new ObservationQuaternion
        {
            x = value.x,
            y = value.y,
            z = value.z,
            w = value.w
        };
    }

    private static string GetPath(Transform transform)
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

    private static string OrNone(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "none" : value;
    }
}

internal sealed class TrainingObservation
{
    [JsonPropertyName("protocolVersion")]
    public string protocolVersion { get; set; }

    [JsonPropertyName("tick")]
    public int tick { get; set; }

    [JsonPropertyName("timeSeconds")]
    public float timeSeconds { get; set; }

    [JsonPropertyName("sceneReady")]
    public bool sceneReady { get; set; }

    [JsonPropertyName("episodeId")]
    public int episodeId { get; set; }

    [JsonPropertyName("episodeStep")]
    public int episodeStep { get; set; }

    [JsonPropertyName("rootPosition")]
    public ObservationVector3 rootPosition { get; set; }

    [JsonPropertyName("rootRotation")]
    public ObservationQuaternion rootRotation { get; set; }

    [JsonPropertyName("headPosition")]
    public ObservationVector3 headPosition { get; set; }

    [JsonPropertyName("headRotation")]
    public ObservationQuaternion headRotation { get; set; }

    [JsonPropertyName("leftHandPosition")]
    public ObservationVector3 leftHandPosition { get; set; }

    [JsonPropertyName("leftHandRotation")]
    public ObservationQuaternion leftHandRotation { get; set; }

    [JsonPropertyName("rightHandPosition")]
    public ObservationVector3 rightHandPosition { get; set; }

    [JsonPropertyName("rightHandRotation")]
    public ObservationQuaternion rightHandRotation { get; set; }

    [JsonPropertyName("health")]
    public float? health { get; set; }

    [JsonPropertyName("error")]
    public string error { get; set; }

    [JsonPropertyName("warnings")]
    public List<string> warnings { get; set; }
}

internal sealed class ObservationVector3
{
    [JsonPropertyName("x")]
    public float x { get; set; }

    [JsonPropertyName("y")]
    public float y { get; set; }

    [JsonPropertyName("z")]
    public float z { get; set; }
}

internal sealed class ObservationQuaternion
{
    [JsonPropertyName("x")]
    public float x { get; set; }

    [JsonPropertyName("y")]
    public float y { get; set; }

    [JsonPropertyName("z")]
    public float z { get; set; }

    [JsonPropertyName("w")]
    public float w { get; set; }
}
