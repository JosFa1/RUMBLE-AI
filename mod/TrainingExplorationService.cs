using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace AI_Train;

internal sealed class TrainingExplorationService
{
    private static readonly string[] ProbeTypeNames =
    {
        "MoveSystemTester",
        "PlayerLIV",
        "PlayerCamera",
        "LCKCameraController",
        "LCKCameraModeHelper",
        "SpawnStructureModifier",
        "SpawnStructureTargetModifier",
        "SpawnStructureGroundedModifier",
        "SpawnStructureNonGroundedModifier",
        "SpawnPoolableModifier"
    };

    private static readonly string[] ProbeMethodTokens =
    {
        "Spawn",
        "Summon",
        "Kick",
        "Gesture",
        "Execute",
        "Test",
        "Camera",
        "Mode",
        "Attach",
        "Connect",
        "Load"
    };

    private static readonly string[] PreferredExactMethods =
    {
        "ToggleTabletSummon",
        "SpawnLCKTablet",
        "ExecuteTestLoop",
        "ExecuteTestSequence",
        "KickPlayer",
        "SwitchCameraModes",
        "SetCameraMode",
        "ProcessThirdCameraPosition",
        "ProcessFirstCameraPosition"
    };

    private readonly TrainingRuntimeHost _host;
    private readonly TrainingMonitorCamera _camera;
    private readonly Action<string> _logInfo;
    private readonly Action<string> _logWarn;
    private readonly Action<string> _logError;

    public TrainingExplorationService(
        TrainingRuntimeHost host,
        TrainingMonitorCamera camera,
        Action<string> logInfo,
        Action<string> logWarn,
        Action<string> logError)
    {
        _host = host;
        _camera = camera;
        _logInfo = logInfo ?? (_ => { });
        _logWarn = logWarn ?? (_ => { });
        _logError = logError ?? (_ => { });
    }

    public TrainingBridgeDebugResponse BuildDebugProbe(TrainingEnvironmentManager manager, string reason = null)
    {
        var response = new TrainingBridgeDebugResponse
        {
            type = "debug_probe_result",
            protocolVersion = TrainingProtocol.Version,
            requestType = "debug_probe",
            sceneReady = manager?.IsReady ?? false,
            playerRootFound = manager?.CurrentPlayerRoot != null,
            trainingSceneName = manager != null ? manager.CurrentTrainingScene.name : null,
            playerRootPath = GetPath(manager?.CurrentPlayerRoot != null ? manager.CurrentPlayerRoot.transform : null),
            probeHostReady = _host != null,
            camera = _camera != null ? _camera.GetState() : null,
            types = new List<TrainingDebugTypeReport>(),
            warnings = new List<string>(),
            error = null
        };

        if (manager == null)
        {
            _logWarn("Training debug probe skipped: manager unavailable.");
            response.error = new TrainingBridgeErrorInfo
            {
                code = "manager_unavailable",
                message = "Training environment manager is unavailable.",
                details = null
            };
            return response;
        }

        if (!manager.IsReady)
        {
            _logWarn("Training debug probe skipped: scene not ready.");
            response.error = new TrainingBridgeErrorInfo
            {
                code = "scene_not_ready",
                message = "Training scene is not ready.",
                details = null
            };
            return response;
        }

        _camera?.UpdateTarget(manager.CurrentPlayerRoot);

        _logInfo(string.IsNullOrWhiteSpace(reason)
            ? "Training debug probe started."
            : $"Training debug probe started ({reason}).");

        foreach (var typeName in ProbeTypeNames)
        {
            var candidates = GetProbeTypeCandidates(typeName).ToList();
            if (candidates.Count == 0)
            {
                response.warnings.Add($"type_not_found:{typeName}");
                continue;
            }

            foreach (var type in candidates)
            {
                var instance = FindFirstRuntimeInstance(type);
                var report = new TrainingDebugTypeReport
                {
                    typeName = type.FullName ?? type.Name,
                    assemblyName = type.Assembly.GetName().Name,
                    instancePath = GetRuntimeObjectPath(instance),
                    methodCandidates = GetRelevantMethods(type).Select(method => method.Name).ToList(),
                    invocations = new List<TrainingDebugInvocationReport>(),
                    notes = new List<string>()
                };

                if (instance == null)
                {
                    report.notes.Add("instance_not_found");
                }

                TryInvokeProbeMethods(type, instance, report);
                response.types.Add(report);
            }
        }

        if (response.types.Count == 0)
        {
            _logWarn("Training debug probe resolved no runtime targets.");
            response.warnings.Add("no_probe_targets_resolved");
        }

        return response;
    }

    private void TryInvokeProbeMethods(Type type, object instance, TrainingDebugTypeReport report)
    {
        if (type == null || report == null)
        {
            return;
        }

        var bindingFlags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var methodCandidates = new List<MethodInfo>();

        foreach (var exactName in PreferredExactMethods)
        {
            var exactMatches = type.GetMethods(bindingFlags)
                .Where(method => string.Equals(method.Name, exactName, StringComparison.Ordinal))
                .ToList();
            methodCandidates.AddRange(exactMatches);
        }

        var tokenMatches = GetRelevantMethods(type)
            .Where(method => !methodCandidates.Any(candidate => candidate.Name == method.Name))
            .Take(8)
            .ToList();
        methodCandidates.AddRange(tokenMatches);

        foreach (var method in methodCandidates.Distinct())
        {
            if (TryInvokeMethod(type, instance, method, out var result))
            {
                report.invocations.Add(result);
            }
        }
    }

    private bool TryInvokeMethod(Type type, object instance, MethodInfo method, out TrainingDebugInvocationReport result)
    {
        result = new TrainingDebugInvocationReport
        {
            memberName = method?.Name,
            result = "skipped",
            details = null
        };

        if (type == null || method == null)
        {
            result.result = "invalid";
            result.details = "missing_type_or_method";
            return false;
        }

        try
        {
            var parameters = method.GetParameters();
            var invokeTarget = method.IsStatic ? null : instance;
            if (!method.IsStatic && invokeTarget == null)
            {
                result.result = "skipped";
                result.details = "instance_missing";
                return false;
            }

            object returnValue = null;
            if (parameters.Length == 0)
            {
                returnValue = method.Invoke(invokeTarget, Array.Empty<object>());
            }
            else if (parameters.Length == 1)
            {
                if (!TryBuildSingleArgument(parameters[0].ParameterType, method.Name, out var argument))
                {
                    result.result = "skipped";
                    result.details = $"unsupported_parameter:{parameters[0].ParameterType?.FullName}";
                    return false;
                }

                returnValue = method.Invoke(invokeTarget, new[] { argument });
            }
            else
            {
                result.result = "skipped";
                result.details = $"too_many_parameters:{parameters.Length}";
                return false;
            }

            if (returnValue is IEnumerator enumerator)
            {
                if (_host != null)
                {
                    _host.RunCoroutine(enumerator);
                    result.result = "coroutine_started";
                    result.details = "enumerator_started";
                }
                else
                {
                    result.result = "coroutine_unavailable";
                    result.details = "host_missing";
                }
                return true;
            }

            result.result = "invoked";
            result.details = returnValue != null ? $"returned:{returnValue.GetType().FullName}" : "completed";
            return true;
        }
        catch (Exception ex)
        {
            result.result = "failed";
            result.details = ex.InnerException?.Message ?? ex.Message;
            _logError($"Training debug invocation failed for {type.FullName}.{method.Name}: {result.details}");
            return false;
        }
    }

    private static bool TryBuildSingleArgument(Type parameterType, string methodName, out object argument)
    {
        argument = null;

        if (parameterType == typeof(int))
        {
            argument = 1;
            return true;
        }

        if (parameterType == typeof(short))
        {
            argument = (short)1;
            return true;
        }

        if (parameterType == typeof(float))
        {
            argument = 1f;
            return true;
        }

        if (parameterType == typeof(double))
        {
            argument = 1.0d;
            return true;
        }

        if (parameterType == typeof(bool))
        {
            argument = true;
            return true;
        }

        if (parameterType == typeof(string))
        {
            argument = string.Empty;
            return true;
        }

        if (parameterType.IsEnum)
        {
            var values = Enum.GetValues(parameterType);
            if (values.Length > 0)
            {
                argument = values.GetValue(0);
                return true;
            }
        }

        if (parameterType == typeof(Vector2))
        {
            argument = Vector2.zero;
            return true;
        }

        if (parameterType == typeof(Vector3))
        {
            argument = Vector3.zero;
            return true;
        }

        if (parameterType == typeof(Quaternion))
        {
            argument = Quaternion.identity;
            return true;
        }

        if (parameterType == typeof(GameObject) || parameterType == typeof(Transform) || parameterType == typeof(Component))
        {
            return false;
        }

        return false;
    }

    private static IEnumerable<MethodInfo> GetRelevantMethods(Type type)
    {
        if (type == null)
        {
            yield break;
        }

        var bindingFlags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var methods = type.GetMethods(bindingFlags)
            .Where(method =>
                ProbeMethodTokens.Any(token => method.Name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0))
            .OrderBy(method => method.Name)
            .Take(40);

        foreach (var method in methods)
        {
            yield return method;
        }
    }

    private static IEnumerable<Type> GetProbeTypeCandidates(string typeName)
    {
        var candidates = new List<Type>();

        var exactType = FindLoadedType(typeName) ?? FindLoadedType($"Il2Cpp{typeName}");
        if (exactType != null)
        {
            candidates.Add(exactType);
        }

        foreach (var match in FindTypeMatches(typeName)
                     .OrderByDescending(ScoreTypeCandidate)
                     .ThenBy(type => type.FullName)
                     .Take(10))
        {
            if (!candidates.Contains(match))
            {
                candidates.Add(match);
            }
        }

        return candidates
            .Where(IsGameRuntimeType)
            .OrderByDescending(ScoreTypeCandidate)
            .ThenBy(type => type.FullName);
    }

    private static Type FindLoadedType(string fullName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type type;
            try
            {
                type = assembly.GetType(fullName, false, false);
            }
            catch
            {
                continue;
            }

            if (type != null)
            {
                return type;
            }
        }

        return null;
    }

    private static IEnumerable<Type> FindTypeMatches(string token)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            foreach (var type in GetAssemblyTypesSafe(assembly))
            {
                var fullName = type.FullName ?? string.Empty;
                if (fullName.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    yield return type;
                }
            }
        }
    }

    private static IEnumerable<Type> GetAssemblyTypesSafe(Assembly assembly)
    {
        if (assembly == null)
        {
            yield break;
        }

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types;
        }
        catch
        {
            yield break;
        }

        if (types == null)
        {
            yield break;
        }

        foreach (var type in types)
        {
            if (type != null)
            {
                yield return type;
            }
        }
    }

    private static bool IsGameRuntimeType(Type type)
    {
        if (type == null)
        {
            return false;
        }

        var fullName = type.FullName ?? string.Empty;
        var assemblyName = type.Assembly.GetName().Name ?? string.Empty;

        if (fullName.StartsWith("UnityEngine.", StringComparison.OrdinalIgnoreCase) ||
            assemblyName.StartsWith("UnityEngine", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return fullName.IndexOf("RUMBLE", StringComparison.OrdinalIgnoreCase) >= 0 ||
               assemblyName.IndexOf("RUMBLE", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static int ScoreTypeCandidate(Type type)
    {
        if (type == null)
        {
            return int.MinValue;
        }

        var fullName = type.FullName ?? string.Empty;
        var assemblyName = type.Assembly.GetName().Name ?? string.Empty;
        var score = 0;

        if (fullName.StartsWith("Il2CppRUMBLE.", StringComparison.OrdinalIgnoreCase) ||
            fullName.StartsWith("RUMBLE.", StringComparison.OrdinalIgnoreCase))
        {
            score += 1000;
        }

        if (assemblyName.IndexOf("RUMBLE", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            score += 400;
        }

        if (fullName.IndexOf("RUMBLE", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            score += 250;
        }

        if (fullName.IndexOf("SceneManager", StringComparison.OrdinalIgnoreCase) >= 0 ||
            fullName.IndexOf("BootLoader", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            score += 150;
        }

        if (fullName.StartsWith("UnityEngine.", StringComparison.OrdinalIgnoreCase))
        {
            score -= 1000;
        }

        if (assemblyName.StartsWith("UnityEngine", StringComparison.OrdinalIgnoreCase))
        {
            score -= 800;
        }

        if (assemblyName.IndexOf("Photon", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            score -= 150;
        }

        return score;
    }

    private static object FindFirstRuntimeInstance(Type type)
    {
        try
        {
            if (type == null)
            {
                return null;
            }

            var method = typeof(Resources).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(candidate =>
                    candidate.Name == "FindObjectsOfTypeAll" &&
                    candidate.IsGenericMethodDefinition &&
                    candidate.GetGenericArguments().Length == 1 &&
                    candidate.GetParameters().Length == 0);

            if (method != null)
            {
                var results = method.MakeGenericMethod(type)
                    .Invoke(null, Array.Empty<object>()) as IEnumerable;
                if (results != null)
                {
                    foreach (var result in results)
                    {
                        if (result != null)
                        {
                            return result;
                        }
                    }
                }
            }
        }
        catch
        {
            // Best effort only.
        }

        return null;
    }

    private static string GetRuntimeObjectPath(object obj)
    {
        if (obj is Component component)
        {
            return GetPath(component.transform);
        }

        if (obj is UnityEngine.Object unityObject)
        {
            return unityObject.name;
        }

        return obj != null ? obj.GetType().FullName ?? obj.ToString() : string.Empty;
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
}
