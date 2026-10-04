using VRCFaceTracking.Core.Params.Expressions;

namespace SteamLinkVRCFTModule {
	public class Overrider {
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
		
		public interface IOverider {
			void Initialize();
			void Teardown();
			float Apply(EyeExpression expression, float nativeValue);
			float Apply(UnifiedExpressions expression, float nativeValue);
		}

		public class Nop : IOverider {
			public void Initialize() {}
			public void Teardown() {}
			public float Apply(EyeExpression expression, float nativeValue) => nativeValue;
			public float Apply(UnifiedExpressions expression, float nativeValue) => nativeValue;
		}
	}
}