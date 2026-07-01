using System;

namespace AI_Train;

internal sealed class RewardCalculator
{
    private const float NearTargetThreshold = 0.08f;
    private const float ClampPenaltyPerHand = 0.08f;
    private const float NoProgressPenalty = 0.05f;

    public TrainingRewardBreakdown Calculate(RewardCalculationInput input)
    {
        input ??= new RewardCalculationInput();

        var leftProgress = SafeDelta(input.leftDistanceBefore, input.leftDistanceAfter);
        var rightProgress = SafeDelta(input.rightDistanceBefore, input.rightDistanceAfter);
        var leftReward = leftProgress;
        var rightReward = rightProgress;

        var bothHandsNearTarget =
            input.leftHandFound &&
            input.rightHandFound &&
            input.leftDistanceAfter <= NearTargetThreshold &&
            input.rightDistanceAfter <= NearTargetThreshold;

        var clampPenalty = 0f;
        if (input.leftTargetClamped)
        {
            clampPenalty += ClampPenaltyPerHand;
        }

        if (input.rightTargetClamped)
        {
            clampPenalty += ClampPenaltyPerHand;
        }

        var noProgress = Math.Abs(leftProgress) + Math.Abs(rightProgress) <= 0.001f;
        if (input.leftMovementApplied || input.rightMovementApplied)
        {
            noProgress = false;
        }

        var noProgressPenalty = noProgress ? NoProgressPenalty : 0f;
        var bothHandsNearBonus = bothHandsNearTarget ? 0.15f : 0f;
        var totalReward = leftReward + rightReward + bothHandsNearBonus - clampPenalty - noProgressPenalty;

        return new TrainingRewardBreakdown
        {
            leftDistanceBefore = input.leftDistanceBefore,
            leftDistanceAfter = input.leftDistanceAfter,
            rightDistanceBefore = input.rightDistanceBefore,
            rightDistanceAfter = input.rightDistanceAfter,
            leftProgress = leftProgress,
            rightProgress = rightProgress,
            leftReward = leftReward,
            rightReward = rightReward,
            bothHandsNearBonus = bothHandsNearBonus,
            clampPenalty = clampPenalty,
            noProgressPenalty = noProgressPenalty,
            totalReward = totalReward,
            bothHandsNearTarget = bothHandsNearTarget,
            noProgress = noProgress
        };
    }

    private static float SafeDelta(float before, float after)
    {
        if (float.IsNaN(before) || float.IsNaN(after))
        {
            return 0f;
        }

        return before - after;
    }
}

internal sealed class RewardCalculationInput
{
    public float leftDistanceBefore { get; set; } = float.NaN;
    public float leftDistanceAfter { get; set; } = float.NaN;
    public float rightDistanceBefore { get; set; } = float.NaN;
    public float rightDistanceAfter { get; set; } = float.NaN;
    public bool leftHandFound { get; set; }
    public bool rightHandFound { get; set; }
    public bool leftMovementApplied { get; set; }
    public bool rightMovementApplied { get; set; }
    public bool leftTargetClamped { get; set; }
    public bool rightTargetClamped { get; set; }
}
