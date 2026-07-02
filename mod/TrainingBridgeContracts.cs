using System.Collections.Generic;

namespace AI_Train;

internal sealed class TrainingBridgeErrorResponse
{
    public string type { get; set; }
    public string protocolVersion { get; set; }
    public string requestType { get; set; }
    public TrainingBridgeErrorInfo error { get; set; }
}

internal sealed class TrainingBridgeErrorInfo
{
    public string code { get; set; }
    public string message { get; set; }
    public Dictionary<string, object> details { get; set; }
}

internal sealed class TrainingBridgeObservationResponse
{
    public string type { get; set; }
    public string protocolVersion { get; set; }
    public string requestType { get; set; }
    public TrainingObservation observation { get; set; }
    public TrainingBridgeErrorInfo error { get; set; }
}

internal sealed class TrainingBridgeStepResponse
{
    public string type { get; set; }
    public string protocolVersion { get; set; }
    public string requestType { get; set; }
    public TrainingObservation observation { get; set; }
    public float reward { get; set; }
    public bool terminated { get; set; }
    public bool truncated { get; set; }
    public TrainingBridgeStepInfo info { get; set; }
    public TrainingBridgeErrorInfo error { get; set; }
}

internal sealed class TrainingBridgeResetResponse
{
    public string type { get; set; }
    public string protocolVersion { get; set; }
    public string requestType { get; set; }
    public int episodeId { get; set; }
    public TrainingObservation observation { get; set; }
    public bool sceneReady { get; set; }
    public string resetMode { get; set; }
    public List<string> warnings { get; set; }
    public TrainingBridgeErrorInfo error { get; set; }
}

internal sealed class TrainingBridgeStepInfo
{
    public bool actionApplied { get; set; }
    public bool leftHandFound { get; set; }
    public bool rightHandFound { get; set; }
    public bool leftTargetClamped { get; set; }
    public bool rightTargetClamped { get; set; }
    public bool leftMovementBlocked { get; set; }
    public bool rightMovementBlocked { get; set; }
    public string leftHandPath { get; set; }
    public string rightHandPath { get; set; }
    public string blockedReason { get; set; }
    public int durationMs { get; set; }
    public float elapsedMs { get; set; }
    public float? leftDistanceBefore { get; set; }
    public float? leftDistanceAfter { get; set; }
    public float? rightDistanceBefore { get; set; }
    public float? rightDistanceAfter { get; set; }
    public ObservationVector3 leftTargetWorld { get; set; }
    public ObservationVector3 rightTargetWorld { get; set; }
    public ObservationVector3 leftTargetLocalClamped { get; set; }
    public ObservationVector3 rightTargetLocalClamped { get; set; }
    public bool reachedLeftTarget { get; set; }
    public bool reachedRightTarget { get; set; }
    public bool actionWindowCompleted { get; set; }
    public bool activeStepReplaced { get; set; }
    public float reward { get; set; }
    public TrainingRewardBreakdown rewardBreakdown { get; set; }
    public List<string> notes { get; set; }
}

internal sealed class TrainingRewardBreakdown
{
    public float leftDistanceBefore { get; set; }
    public float leftDistanceAfter { get; set; }
    public float rightDistanceBefore { get; set; }
    public float rightDistanceAfter { get; set; }
    public float leftProgress { get; set; }
    public float rightProgress { get; set; }
    public float leftReward { get; set; }
    public float rightReward { get; set; }
    public float bothHandsNearBonus { get; set; }
    public float clampPenalty { get; set; }
    public float noProgressPenalty { get; set; }
    public float totalReward { get; set; }
    public bool bothHandsNearTarget { get; set; }
    public bool noProgress { get; set; }
}

internal sealed class TrainingBridgeStepActionRequest
{
    public float[] leftHandTargetLocal { get; set; }
    public float[] rightHandTargetLocal { get; set; }
    public int durationMs { get; set; }
}

internal sealed class TrainingBridgeResetInfo
{
    public string resetMode { get; set; }
    public bool leftHandFound { get; set; }
    public bool rightHandFound { get; set; }
    public string leftHandPath { get; set; }
    public string rightHandPath { get; set; }
    public List<string> warnings { get; set; }
}

internal sealed class TrainingBridgeDebugResponse
{
    public string type { get; set; }
    public string protocolVersion { get; set; }
    public string requestType { get; set; }
    public bool sceneReady { get; set; }
    public bool playerRootFound { get; set; }
    public string trainingSceneName { get; set; }
    public string playerRootPath { get; set; }
    public bool probeHostReady { get; set; }
    public TrainingMonitorCameraState camera { get; set; }
    public List<TrainingDebugTypeReport> types { get; set; }
    public List<string> warnings { get; set; }
    public TrainingBridgeErrorInfo error { get; set; }
}

internal sealed class TrainingMonitorCameraState
{
    public bool freeFlyEnabled { get; set; }
    public bool targetFound { get; set; }
    public string targetPath { get; set; }
    public string cameraName { get; set; }
    public ObservationVector3 cameraPosition { get; set; }
    public ObservationQuaternion cameraRotation { get; set; }
}

internal sealed class TrainingDebugTypeReport
{
    public string typeName { get; set; }
    public string assemblyName { get; set; }
    public string instancePath { get; set; }
    public List<string> methodCandidates { get; set; }
    public List<TrainingDebugInvocationReport> invocations { get; set; }
    public List<string> notes { get; set; }
}

internal sealed class TrainingDebugInvocationReport
{
    public string memberName { get; set; }
    public string result { get; set; }
    public string details { get; set; }
}
