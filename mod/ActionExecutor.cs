using System;
using System.Collections.Generic;
using UnityEngine;

namespace AI_Train;

internal sealed class ActionExecutor
{
    private const float MaxSpeedMetersPerSecond = 3.5f;
    private const float MinimumDurationSeconds = 0.016f;
    private const float MoveEpsilon = 0.0005f;
    private const float TargetReachedThreshold = 0.02f;
    private const int DefaultDurationMilliseconds = 100;
    private const int MaxDurationMilliseconds = 1000;
    private static readonly Vector3 NeutralLeftHandLocal = new(-0.2f, 1.1f, 0.35f);
    private static readonly Vector3 NeutralRightHandLocal = new(0.2f, 1.1f, 0.35f);

    private readonly TrainingEnvironmentManager _manager;
    private readonly RewardCalculator _rewardCalculator = new();
    private readonly Action<string> _logInfo;
    private readonly Action<string> _logWarn;
    private readonly object _gate = new();

    private ActiveStep _activeStep;
    private ResolvedStep _resolvedStep;
    private TrainingRewardBreakdown _lastRewardBreakdown;
    private float _lastReward;
    private bool _handRigControlApplied;

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

        var playerRoot = _manager.CurrentPlayerRoot;
        if (playerRoot == null || playerRoot.transform == null)
        {
            errorCode = "player_root_missing";
            return null;
        }

        var durationMs = request.durationMs > 0
            ? Math.Min(request.durationMs, MaxDurationMilliseconds)
            : DefaultDurationMilliseconds;
        var durationSeconds = Math.Max(durationMs / 1000f, MinimumDurationSeconds);

        var leftClamped = false;
        var rightClamped = false;
        leftTargetLocal = TrainingActorLocator.ClampLocalTarget(leftTargetLocal, out leftClamped);
        rightTargetLocal = TrainingActorLocator.ClampLocalTarget(rightTargetLocal, out rightClamped);

        var playerTransform = playerRoot.transform;
        var leftTargetWorld = TrainingActorLocator.ToWorldTarget(playerTransform, leftTargetLocal);
        var rightTargetWorld = TrainingActorLocator.ToWorldTarget(playerTransform, rightTargetLocal);
        var leftHand = TrainingActorLocator.Resolve(playerTransform, TrainingActorLocator.LeftHandCandidates);
        var rightHand = TrainingActorLocator.Resolve(playerTransform, TrainingActorLocator.RightHandCandidates);
        if (leftHand.Transform != null && leftHand.Transform == rightHand.Transform)
        {
            errorCode = "hand_transform_alias";
            _logWarn($"ActionExecutor rejected aliased hand transforms at {leftHand.Path}.");
            return null;
        }

        EnsureHandRigControl(playerTransform, leftHand, rightHand);
        var leftDistanceBefore = leftHand.Transform != null
            ? Vector3.Distance(leftHand.Transform.position, leftTargetWorld)
            : float.NaN;
        var rightDistanceBefore = rightHand.Transform != null
            ? Vector3.Distance(rightHand.Transform.position, rightTargetWorld)
            : float.NaN;

        var replacedActiveStep = false;
        lock (_gate)
        {
            if (_activeStep != null)
            {
                replacedActiveStep = true;
                CancelActiveStepLocked("step_replaced", captureResolution: true);
            }

            _resolvedStep = null;
            _lastReward = 0f;
            _lastRewardBreakdown = null;
        }

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
            elapsedMs = 0f,
            leftDistanceBefore = leftHand.Transform != null ? SanitizeDistance(leftDistanceBefore) : null,
            leftDistanceAfter = null,
            rightDistanceBefore = rightHand.Transform != null ? SanitizeDistance(rightDistanceBefore) : null,
            rightDistanceAfter = null,
            leftTargetWorld = ToObservationVector(leftTargetWorld),
            rightTargetWorld = ToObservationVector(rightTargetWorld),
            leftTargetLocalClamped = ToObservationVector(leftTargetLocal),
            rightTargetLocalClamped = ToObservationVector(rightTargetLocal),
            reachedLeftTarget = false,
            reachedRightTarget = false,
            actionWindowCompleted = false,
            activeStepReplaced = replacedActiveStep,
            reward = 0f,
            rewardBreakdown = null,
            notes = new List<string>()
        };

        if (leftHand.Transform == null)
        {
            info.notes.Add("left hand missing");
            info.blockedReason = "left_hand_transform_missing";
            _logWarn("ActionExecutor left hand missing; movement blocked for that hand.");
        }

        if (rightHand.Transform == null)
        {
            info.notes.Add("right hand missing");
            info.blockedReason = info.blockedReason == null
                ? "right_hand_transform_missing"
                : "both_hand_transforms_missing";
            _logWarn("ActionExecutor right hand missing; movement blocked for that hand.");
        }

        if (leftClamped)
        {
            info.notes.Add("left target clamped");
        }

        if (rightClamped)
        {
            info.notes.Add("right target clamped");
        }

        if (leftHand.Transform == null && rightHand.Transform == null)
        {
            info.blockedReason = "both_hand_transforms_missing";
        }

        var activeStep = new ActiveStep
        {
            StartedAtSeconds = Time.unscaledTime,
            EndsAtSeconds = Time.unscaledTime + durationSeconds,
            DurationMs = durationMs,
            LeftHand = leftHand,
            RightHand = rightHand,
            LeftTargetWorld = leftTargetWorld,
            RightTargetWorld = rightTargetWorld,
            LeftDistanceBefore = leftDistanceBefore,
            RightDistanceBefore = rightDistanceBefore,
            Info = info
        };

        lock (_gate)
        {
            _activeStep = activeStep;
        }

        _logInfo(
            $"ActionExecutor step started: durationMs={durationMs} leftFound={info.leftHandFound} rightFound={info.rightHandFound} leftClamped={leftClamped} rightClamped={rightClamped}.");

        return info;
    }

    public bool TryConsumeResolvedStep(out TrainingBridgeStepInfo info, out string errorCode)
    {
        lock (_gate)
        {
            if (_resolvedStep == null)
            {
                info = null;
                errorCode = null;
                return false;
            }

            info = _resolvedStep.Info;
            errorCode = _resolvedStep.ErrorCode;
            _resolvedStep = null;
            return true;
        }
    }

    public void CancelActiveStep(string errorCode, bool captureResolution)
    {
        lock (_gate)
        {
            CancelActiveStepLocked(errorCode, captureResolution);
        }
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

        lock (_gate)
        {
            if (_activeStep != null)
            {
                CancelActiveStepLocked("step_canceled_by_reset", captureResolution: true);
                warnings.Add("active step canceled");
            }

            _lastReward = 0f;
            _lastRewardBreakdown = null;
        }

        var leftHand = TrainingActorLocator.Resolve(playerRoot.transform, TrainingActorLocator.LeftHandCandidates);
        var rightHand = TrainingActorLocator.Resolve(playerRoot.transform, TrainingActorLocator.RightHandCandidates);
        if (leftHand.Transform != null && leftHand.Transform == rightHand.Transform)
        {
            errorCode = "hand_transform_alias";
            warnings.Add("left and right hand resolved to the same transform");
            _logWarn($"ActionExecutor reset rejected aliased hand transforms at {leftHand.Path}.");
            return resetInfo;
        }

        EnsureHandRigControl(playerRoot.transform, leftHand, rightHand);
        if (leftHand.Transform != null)
        {
            resetInfo.leftHandFound = true;
            resetInfo.leftHandPath = leftHand.Path;
            SnapHandToNeutral(leftHand.Transform, playerRoot.transform, NeutralLeftHandLocal);
        }
        else
        {
            warnings.Add("left hand missing");
            _logWarn("ActionExecutor reset could not locate the left hand transform.");
        }

        if (rightHand.Transform != null)
        {
            resetInfo.rightHandFound = true;
            resetInfo.rightHandPath = rightHand.Path;
            SnapHandToNeutral(rightHand.Transform, playerRoot.transform, NeutralRightHandLocal);
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

    private void EnsureHandRigControl(
        Transform playerRoot,
        TrainingActorLocator.ResolvedTransform leftHand,
        TrainingActorLocator.ResolvedTransform rightHand)
    {
        if (_handRigControlApplied || playerRoot == null)
        {
            return;
        }

        var disabledCount = 0;
        disabledCount += DisableTrackingBehaviours(leftHand.Transform, playerRoot);
        disabledCount += DisableTrackingBehaviours(rightHand.Transform, playerRoot);
        _handRigControlApplied = true;
        _logInfo($"ActionExecutor acquired PC hand-rig control; disabledBehaviours={disabledCount}.");
    }

    private static int DisableTrackingBehaviours(Transform handTransform, Transform playerRoot)
    {
        if (handTransform == null)
        {
            return 0;
        }

        var disabledCount = 0;
        var current = handTransform;
        while (current != null && current != playerRoot)
        {
            var behaviours = current.GetComponents<Behaviour>();
            foreach (var behaviour in behaviours)
            {
                if (behaviour != null && behaviour.enabled)
                {
                    behaviour.enabled = false;
                    disabledCount++;
                }
            }

            if (current.name.IndexOf("Controller", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                break;
            }

            current = current.parent;
        }

        return disabledCount;
    }

    public void Pump(float deltaTime)
    {
        ActiveStep step;
        lock (_gate)
        {
            step = _activeStep;
        }

        if (step == null)
        {
            return;
        }

        var frameDelta = deltaTime > 0f ? deltaTime : MinimumDurationSeconds;
        var speed = MaxSpeedMetersPerSecond * frameDelta;
        var elapsedMs = Math.Max(0f, (Time.unscaledTime - step.StartedAtSeconds) * 1000f);
        var leftMoved = MoveHand(step.LeftHand.Transform, step.LeftTargetWorld, speed);
        var rightMoved = MoveHand(step.RightHand.Transform, step.RightTargetWorld, speed);

        step.LeftMoved |= leftMoved;
        step.RightMoved |= rightMoved;
        step.Info.elapsedMs = elapsedMs;

        if (leftMoved && !step.LeftMoveLogged)
        {
            step.LeftMoveLogged = true;
            step.Info.notes.Add("left hand moved");
        }

        if (rightMoved && !step.RightMoveLogged)
        {
            step.RightMoveLogged = true;
            step.Info.notes.Add("right hand moved");
        }

        if (Time.unscaledTime < step.EndsAtSeconds)
        {
            return;
        }

        FinalizeActiveStep(step, elapsedMs);
    }

    private void FinalizeActiveStep(ActiveStep step, float elapsedMs)
    {
        if (step == null)
        {
            return;
        }

        var info = step.Info;
        var leftDistanceAfter = step.LeftHand.Transform != null
            ? Vector3.Distance(step.LeftHand.Transform.position, step.LeftTargetWorld)
            : float.NaN;
        var rightDistanceAfter = step.RightHand.Transform != null
            ? Vector3.Distance(step.RightHand.Transform.position, step.RightTargetWorld)
            : float.NaN;

        var rewardBreakdown = _rewardCalculator.Calculate(new RewardCalculationInput
        {
            leftDistanceBefore = step.LeftDistanceBefore,
            leftDistanceAfter = leftDistanceAfter,
            rightDistanceBefore = step.RightDistanceBefore,
            rightDistanceAfter = rightDistanceAfter,
            leftHandFound = step.LeftHand.Transform != null,
            rightHandFound = step.RightHand.Transform != null,
            leftMovementApplied = step.LeftMoved,
            rightMovementApplied = step.RightMoved,
            leftTargetClamped = info.leftTargetClamped,
            rightTargetClamped = info.rightTargetClamped
        });

        info.elapsedMs = elapsedMs;
        info.leftDistanceAfter = step.LeftHand.Transform != null ? SanitizeDistance(leftDistanceAfter) : null;
        info.rightDistanceAfter = step.RightHand.Transform != null ? SanitizeDistance(rightDistanceAfter) : null;
        info.reachedLeftTarget = step.LeftHand.Transform != null && leftDistanceAfter <= TargetReachedThreshold;
        info.reachedRightTarget = step.RightHand.Transform != null && rightDistanceAfter <= TargetReachedThreshold;
        info.actionWindowCompleted = true;
        info.actionApplied = step.LeftMoved || step.RightMoved;
        info.rewardBreakdown = rewardBreakdown;
        info.reward = rewardBreakdown.totalReward;

        lock (_gate)
        {
            _activeStep = null;
            _resolvedStep = new ResolvedStep
            {
                Info = info,
                ErrorCode = null
            };
            _lastReward = rewardBreakdown.totalReward;
            _lastRewardBreakdown = rewardBreakdown;
        }

        _logInfo(
            $"ActionExecutor step completed: elapsedMs={elapsedMs:0.###} reward={rewardBreakdown.totalReward:0.###} leftReached={info.reachedLeftTarget} rightReached={info.reachedRightTarget}.");
    }

    private void CancelActiveStepLocked(string errorCode, bool captureResolution)
    {
        if (_activeStep == null)
        {
            if (!captureResolution)
            {
                _resolvedStep = null;
            }

            return;
        }

        var info = _activeStep.Info;
        info.blockedReason = errorCode;
        info.actionWindowCompleted = false;
        _activeStep = null;

        if (captureResolution)
        {
            _resolvedStep = new ResolvedStep
            {
                Info = info,
                ErrorCode = errorCode
            };
        }
        else
        {
            _resolvedStep = null;
        }

        _lastReward = 0f;
        _lastRewardBreakdown = null;
    }

    private static ObservationVector3 ToObservationVector(Vector3 value)
    {
        return new ObservationVector3
        {
            x = value.x,
            y = value.y,
            z = value.z
        };
    }

    private static float SanitizeDistance(float value)
    {
        return float.IsFinite(value) ? value : 0f;
    }

    private void SnapHandToNeutral(Transform handTransform, Transform playerRoot, Vector3 localTarget)
    {
        if (handTransform == null)
        {
            return;
        }

        handTransform.position = TrainingActorLocator.ToWorldTarget(playerRoot, localTarget);
    }

    private static bool MoveHand(Transform handTransform, Vector3 targetWorld, float maxDistance)
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
        return true;
    }

    private sealed class ActiveStep
    {
        public float StartedAtSeconds { get; set; }
        public float EndsAtSeconds { get; set; }
        public int DurationMs { get; set; }
        public TrainingActorLocator.ResolvedTransform LeftHand { get; set; }
        public TrainingActorLocator.ResolvedTransform RightHand { get; set; }
        public Vector3 LeftTargetWorld { get; set; }
        public Vector3 RightTargetWorld { get; set; }
        public float LeftDistanceBefore { get; set; }
        public float RightDistanceBefore { get; set; }
        public bool LeftMoved { get; set; }
        public bool RightMoved { get; set; }
        public bool LeftMoveLogged { get; set; }
        public bool RightMoveLogged { get; set; }
        public TrainingBridgeStepInfo Info { get; set; }
    }

    private sealed class ResolvedStep
    {
        public TrainingBridgeStepInfo Info { get; set; }
        public string ErrorCode { get; set; }
    }
}
