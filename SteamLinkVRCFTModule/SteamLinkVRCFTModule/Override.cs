using VRCFaceTracking.Core.Params.Expressions;

namespace SteamLinkVRCFTModule.Overrides;

public enum EyeExpression {
	EyeLeftGazeX,
	EyeLeftGazeY,
	EyeRightGazeX,
	EyeRightGazeY,
	EyeLeftOpenness,
	EyeRightOpenness,
	EyeLeftPupilDiameter,
	EyeRightPupilDiameter,
	EyeMaxDilation,
	EyeMinDilation,
}

public interface IOverrider {
	void Initialize() {}
	void Teardown() {}
	float Apply(EyeExpression expression, float nativeValue);
	float Apply(UnifiedExpressions expression, float nativeValue);
	void ProcessOscMessages(IReadOnlyList<OSCM> messages) {}
}

public class NopOverrider : IOverrider {
	public float Apply(EyeExpression expression, float nativeValue) => nativeValue;
	public float Apply(UnifiedExpressions expression, float nativeValue) => nativeValue;
}
