using System;
using System.Collections.Generic;
using UnityEngine;

namespace AI_Train;

internal sealed class ActionExecutor
{
    private const float MaxSpeedMetersPerSecond = 3.5f;
    private const float DefaultDurationSeconds = 0.1f;
    private const float MoveEpsilon = 0.0005f;
    private static readonly Vector3 NeutralLeftHandLocal = new(-0.2f, 1.1f, 0.35f);
    private static readonly Vector3 NeutralRightHandLocal = new(0.2f, 1.1f, 0.35f);

    private readonly TrainingEnvironmentManager _manager;
    private readonly RewardCalculator _rewardCalculator = new();
    private readonly Action<string> _logInfo;
    private readonly Action<string> _logWarn;
    private readonly object _gate = new();

    private ActiveStep _activeStep;
    private TrainingRewardBreakdown _lastRewardBreakdown;
    private float _lastReward;

    public ActionExecutor(TrainingEnvironmentManager manager, Action<string> logInfo, Action<string> logWarn)
    {
        _manager = manager;
        _logInfo = logInfo ?? (_ => { });
        _logWarn = logWarn ?? (_ => { });
    }

    public bool HasActiveStep
    {
        get
        {
            lock (_gate)
            {
                return _activeStep != null;
            }
        }
    }

    public float LastReward
    {
        get
        {
            lock (_gate)
            {
                return _lastReward;
            }
        }
    }

    public TrainingRewardBreakdown LastRewardBreakdown
    {
        get
        {
            lock (_gate)
            {
                return _lastRewardBreakdown;
            }
        }
    }

    public TrainingBridgeStepInfo StartStep(TrainingBridgeStepActionRequest request, out string errorCode)
    {
        errorCode = null;

        if (_manager == null || !_manager.IsReady)
        {
            errorCode = "scene_not_ready";
            return null;
        }

        lock (_gate)
        {
            _activeStep = null;
            _lastReward = 0f;
            _lastRewardBreakdown = null;
        }

        var playerRoot = _manager.CurrentPlayerRoot;
        if (playerRoot == null || playerRoot.transform == null)
        {
            errorCode = "player_root_missing";
            return null;
        }

        if (request == null)
        {
            errorCode = "invalid_action";
            return null;
        }

        if (!TrainingActorLocator.TryReadLocalVector3(request.leftHandTargetLocal, out var leftTargetLocal, out var leftError))
        {
            errorCode = $"left_{leftError}";
            return null;
        }

        if (!TrainingActorLocator.TryReadLocalVector3(request.rightHandTargetLocal, out var rightTargetLocal, out var rightError))
        {
            errorCode = $"right_{rightError}";
            return null;
        }

        var durationMs = request.durationMs > 0 ? Math.Min(request.durationMs, 1000) : 100;
        var durationSeconds = Math.Max(durationMs / 1000f, DefaultDurationSeconds);

        var leftClamped = false;
        var rightClamped = false;
        leftTargetLocal = TrainingActorLocator.ClampLocalTarget(leftTargetLocal, out leftClamped);
        rightTargetLocal = TrainingActorLocator.ClampLocalTarget(rightTargetLocal, out rightClamped);

        var leftTargetWorld = TrainingActorLocator.ToWorldTarget(playerRoot.transform, leftTargetLocal);
        var rightTargetWorld = TrainingActorLocator.ToWorldTarget(playerRoot.transform, rightTargetLocal);

        var leftHand = TrainingActorLocator.Resolve(playerRoot.transform, TrainingActorLocator.LeftHandCandidates);
        var rightHand = TrainingActorLocator.Resolve(playerRoot.transform, TrainingActorLocator.RightHandCandidates);

        var leftDistanceBefore = leftHand.Transform != null
            ? Vector3.Distance(leftHand.Transform.position, leftTargetWorld)
            : float.NaN;
        var rightDistanceBefore = rightHand.Transform != null
            ? Vector3.Distance(rightHand.Transform.position, rightTargetWorld)
            : float.NaN;

        var info = new TrainingBridgeStepInfo
        {
            actionApplied = false,
            leftHandFound = leftHand.Transform != null,
            rightHandFound = rightHand.Transform != null,
            leftTargetClamped = leftClamped,
            rightTargetClamped = rightClamped,
            leftMovementBlocked = leftHand.Transform == null,
            rightMovementBlocked = rightHand.Transform == null,
            leftHandPath = leftHand.Path,
            rightHandPath = rightHand.Path,
            blockedReason = null,
            durationMs = durationMs,
            elapsedMs = 0,
            reward = 0,
            rewardBreakdown = null,
            notes = new List<string>()
        };

        if (leftHand.Transform != null)
        {
            _logInfo($"ActionExecutor left hand resolved: {leftHand.Path}");
        }
        else
        {
            info.notes.Add("left hand missing");
            _logWarn("ActionExecutor left hand missing; movement blocked for that hand.");
        }

        if (rightHand.Transform != null)
        {
            _logInfo($"ActionExecutor right hand resolved: {rightHand.Path}");
        }
        else
        {
            info.notes.Add("right hand missing");
            _logWarn("ActionExecutor right hand missing; movement blocked for that hand.");
        }

        if (leftClamped)
        {
            info.notes.Add("left target clamped");
            _logInfo("ActionExecutor left target clamped to safe envelope.");
        }

        if (rightClamped)
        {
            info.notes.Add("right target clamped");
            _logInfo("ActionExecutor right target clamped to safe envelope.");
        }

        if (leftHand.Transform == null && rightHand.Transform == null)
        {
            info.blockedReason = "both_hand_transforms_missing";
            _logWarn("ActionExecutor step blocked: both hand transforms are missing.");
        }

        lock (_gate)
        {
            _activeStep = new ActiveStep
            {
                StartedAtUtc = DateTime.UtcNow,
                EndsAtUtc = DateTime.UtcNow.AddSeconds(durationSeconds),
                LeftHand = leftHand,
                RightHand = rightHand,
                LeftTargetWorld = leftTargetWorld,
                RightTargetWorld = rightTargetWorld,
                DurationMs = durationMs
            };
        }

        var movementResult = ApplyFrame(Math.Max(Time.unscaledDeltaTime, 0.016f), info);
        var leftDistanceAfter = leftHand.Transform != null
            ? Vector3.Distance(leftHand.Transform.position, leftTargetWorld)
            : float.NaN;
        var rightDistanceAfter = rightHand.Transform != null
            ? Vector3.Distance(rightHand.Transform.position, rightTargetWorld)
            : float.NaN;

        var rewardBreakdown = _rewardCalculator.Calculate(new RewardCalculationInput
        {
            leftDistanceBefore = leftDistanceBefore,
            leftDistanceAfter = leftDistanceAfter,
            rightDistanceBefore = rightDistanceBefore,
            rightDistanceAfter = rightDistanceAfter,
            leftHandFound = leftHand.Transform != null,
            rightHandFound = rightHand.Transform != null,
            leftMovementApplied = movementResult.LeftMoved,
            rightMovementApplied = movementResult.RightMoved,
            leftTargetClamped = leftClamped,
            rightTargetClamped = rightClamped
        });

        info.rewardBreakdown = rewardBreakdown;
        info.reward = rewardBreakdown.totalReward;
        info.actionApplied = movementResult.LeftMoved || movementResult.RightMoved;
        info.elapsedMs = movementResult.ElapsedMs;

        lock (_gate)
        {
            _lastReward = rewardBreakdown.totalReward;
            _lastRewardBreakdown = rewardBreakdown;
        }

        _logInfo(
            $"ActionExecutor step started: durationMs={durationMs} leftApplied={movementResult.LeftMoved} rightApplied={movementResult.RightMoved} reward={rewardBreakdown.totalReward:0.###}.");

        return info;
    }

    public TrainingBridgeResetInfo ResetEpisodeState(out string errorCode)
    {
        errorCode = null;

        if (_manager == null || !_manager.IsReady)
        {
            errorCode = "scene_not_ready";
            return null;
        }

        var playerRoot = _manager.CurrentPlayerRoot;
        if (playerRoot == null || playerRoot.transform == null)
        {
            errorCode = "player_root_missing";
            return null;
        }

        var warnings = new List<string>();
        var resetInfo = new TrainingBridgeResetInfo
        {
            resetMode = "partial",
            warnings = warnings
        };

        var leftHand = TrainingActorLocator.Resolve(playerRoot.transform, TrainingActorLocator.LeftHandCandidates);
        if (leftHand.Transform != null)
        {
            resetInfo.leftHandFound = true;
            resetInfo.leftHandPath = leftHand.Path;
            SnapHandToNeutral(leftHand.Transform, playerRoot.transform, NeutralLeftHandLocal, "left");
        }
        else
        {
            warnings.Add("left hand missing");
            _logWarn("ActionExecutor reset could not locate the left hand transform.");
        }

        var rightHand = TrainingActorLocator.Resolve(playerRoot.transform, TrainingActorLocator.RightHandCandidates);
        if (rightHand.Transform != null)
        {
            resetInfo.rightHandFound = true;
            resetInfo.rightHandPath = rightHand.Path;
            SnapHandToNeutral(rightHand.Transform, playerRoot.transform, NeutralRightHandLocal, "right");
        }
        else
        {
            warnings.Add("right hand missing");
            _logWarn("ActionExecutor reset could not locate the right hand transform.");
        }

        warnings.Add("actor reset is partial; root transform preserved");
        _logInfo(
            $"ActionExecutor reset episode state: mode={resetInfo.resetMode} leftFound={resetInfo.leftHandFound} rightFound={resetInfo.rightHandFound}.");

        return resetInfo;
    }

    public void Pump(float deltaTime)
    {
        ActiveStep activeStep;
        lock (_gate)
        {
            activeStep = _activeStep;
        }

        if (activeStep == null)
        {
            return;
        }

        ApplyFrame(Math.Max(deltaTime, 0.016f), null);

        lock (_gate)
        {
            if (_activeStep != null && DateTime.UtcNow >= _activeStep.EndsAtUtc)
            {
                _logInfo("ActionExecutor step completed.");
                _activeStep = null;
            }
        }
    }

    private MovementResult ApplyFrame(float deltaTime, TrainingBridgeStepInfo info)
    {
        ActiveStep step;
        lock (_gate)
        {
            step = _activeStep;
        }

        if (step == null)
        {
            return default;
        }

        var speed = MaxSpeedMetersPerSecond * deltaTime;
        var leftMoved = MoveHand(step.LeftHand.Transform, step.LeftTargetWorld, speed, "left");
        var rightMoved = MoveHand(step.RightHand.Transform, step.RightTargetWorld, speed, "right");
        var elapsedMs = (float)(DateTime.UtcNow - step.StartedAtUtc).TotalMilliseconds;

        if (info != null)
        {
            info.elapsedMs = elapsedMs;
            if (leftMoved)
            {
                info.notes.Add("left hand moved");
            }

            if (rightMoved)
            {
                info.notes.Add("right hand moved");
            }
        }

        return new MovementResult
        {
            LeftMoved = leftMoved,
            RightMoved = rightMoved,
            ElapsedMs = elapsedMs
        };
    }

    private void SnapHandToNeutral(Transform handTransform, Transform playerRoot, Vector3 localTarget, string handName)
    {
        if (handTransform == null)
        {
            return;
        }

        var targetWorld = TrainingActorLocator.ToWorldTarget(playerRoot, localTarget);
        handTransform.position = targetWorld;
        _logInfo($"ActionExecutor reset {handName} hand to neutral pose: {TrainingActorLocator.GetPath(handTransform)} -> {targetWorld.x:0.###},{targetWorld.y:0.###},{targetWorld.z:0.###}");
    }

    private bool MoveHand(Transform handTransform, Vector3 targetWorld, float maxDistance, string handName)
    {
        if (handTransform == null)
        {
            return false;
        }

        var current = handTransform.position;
        var next = Vector3.MoveTowards(current, targetWorld, maxDistance);
        if ((next - current).sqrMagnitude <= MoveEpsilon * MoveEpsilon)
        {
            return false;
        }

        handTransform.position = next;
        _logInfo($"ActionExecutor applied {handName} hand movement: {TrainingActorLocator.GetPath(handTransform)} -> {next.x:0.###},{next.y:0.###},{next.z:0.###}");
        return true;
    }

    private sealed class ActiveStep
    {
        public DateTime StartedAtUtc { get; set; }
        public DateTime EndsAtUtc { get; set; }
        public TrainingActorLocator.ResolvedTransform LeftHand { get; set; }
        public TrainingActorLocator.ResolvedTransform RightHand { get; set; }
        public Vector3 LeftTargetWorld { get; set; }
        public Vector3 RightTargetWorld { get; set; }
        public int DurationMs { get; set; }
    }

    private struct MovementResult
    {
        public bool LeftMoved;
        public bool RightMoved;
        public float ElapsedMs;
    }
}
