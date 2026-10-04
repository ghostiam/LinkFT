using Microsoft.Extensions.Logging;
using SteamLinkVRCFTModule;
using SteamLinkVRCFTModule.Overriders;
using System.Net.Sockets;
using VRCFaceTracking;
using VRCFaceTracking.Core.Library;
using VRCFaceTracking.Core.Params.Expressions;
using static VRCFaceTracking.Core.Params.Expressions.UnifiedExpressions;

namespace SteamLinkVRCFTModule
{
    public class SteamLinkVRCFTModule : ExtTrackingModule
    {
        private OSCHandler OSCHandler;
        private const int DEFAULT_PORT = 9015;

        private bool _eyeTrackingEnabled;
        private bool _faceTrackingEnabled;
        private Overrider.IOverider _overrider = new Overrider.Nop();

        public override (bool SupportsEye, bool SupportsExpression) Supported => (true, true);

        public override (bool eyeSuccess, bool expressionSuccess) Initialize(bool eyeAvailable, bool expressionAvailable)
        {
            ModuleInformation.Name = "SteamLink Module";

            var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("SteamLinkVRCFTModule.Assets.steamlink.png");
            ModuleInformation.StaticImages = stream != null ? new List<Stream> { stream } : ModuleInformation.StaticImages;

            var config = TrackingConfig.Load(GetModuleDirectory(), Logger);

            _eyeTrackingEnabled = config.EyeTracking switch
            {
                TrackingMode.On => true,
                TrackingMode.Off => false,
                _ => eyeAvailable
            };
            _faceTrackingEnabled = config.FaceTracking switch
            {
                TrackingMode.On => true,
                TrackingMode.Off => false,
                _ => expressionAvailable
            };

            Logger.LogInformation("Tracking claim - Eye: {0} (config: {1}), Face: {2} (config: {3})",
                _eyeTrackingEnabled, config.EyeTracking, _faceTrackingEnabled, config.FaceTracking);

            if (!_eyeTrackingEnabled && !_faceTrackingEnabled)
            {
                Logger.LogInformation("Both eye and expression tracking disabled, staying idle.");
                return (false, false);
            }

            OSCHandler = new OSCHandler(Logger, DEFAULT_PORT);
            
            _overrider = new FwooffyQproEnhancedFT(Logger);
            _overrider.Initialize();
            OSCHandler.OnMessagesReceived += _overrider.ProcessOscMessages;

            return (_eyeTrackingEnabled, _faceTrackingEnabled);
        }

        private static string GetModuleDirectory()
        {
            var location = System.Reflection.Assembly.GetExecutingAssembly().Location;
            return string.IsNullOrEmpty(location) ? "." : Path.GetDirectoryName(location) ?? ".";
        }

        private static float CalculateEyeOpenness(float fEyeClosedWeight, float fEyeTightener)
        {
            return 1.0f - Math.Clamp(fEyeClosedWeight + fEyeClosedWeight * fEyeTightener, 0.0f, 1.0f);
        }

        private void UpdateEyeTracking()
        {
            {
                float fAngleX = MathF.Atan2(OSCHandler.eyeTrackData[0], -OSCHandler.eyeTrackData[2]);
                float fAngleY = MathF.Atan2(OSCHandler.eyeTrackData[1], -OSCHandler.eyeTrackData[2]);

                float fNmAngleX = fAngleX / (MathF.PI / 2.0f) * 2.0f;
                float fNmAngleY = fAngleY / (MathF.PI / 2.0f) * 2.0f;

                if (float.IsNaN(fNmAngleX))
                {
                    fNmAngleX = 0.0f;
                }
                if (float.IsNaN(fNmAngleY))
                {
                    fNmAngleY = 0.0f;
                }

                UnifiedTracking.Data.Eye.Left.Gaze.x = _overrider.Apply(Overrider.EyeExpression.EyeLeftGazeX, fAngleX);
                UnifiedTracking.Data.Eye.Left.Gaze.y = _overrider.Apply(Overrider.EyeExpression.EyeLeftGazeY, fAngleY);

                UnifiedTracking.Data.Eye.Right.Gaze.x = _overrider.Apply(Overrider.EyeExpression.EyeRightGazeX, fAngleX);
                UnifiedTracking.Data.Eye.Right.Gaze.y = _overrider.Apply(Overrider.EyeExpression.EyeRightGazeY, fAngleY);

                //Pupil Dilation, This is not supported, but if we don't set it can cause issues
                const float defaultPupilDiameterMm = 5f;
                UnifiedTracking.Data.Eye.Left.PupilDiameter_MM = _overrider.Apply(Overrider.EyeExpression.EyeLeftPupilDiameter, defaultPupilDiameterMm);
                UnifiedTracking.Data.Eye.Right.PupilDiameter_MM = _overrider.Apply(Overrider.EyeExpression.EyeRightPupilDiameter, defaultPupilDiameterMm);
                UnifiedTracking.Data.Eye._maxDilation = _overrider.Apply(Overrider.EyeExpression.EyeMaxDilation, 10);
                UnifiedTracking.Data.Eye._minDilation = _overrider.Apply(Overrider.EyeExpression.EyeMinDilation, 0);
            }

            {
                float fLeftOpenness = CalculateEyeOpenness(OSCHandler.eyelids[0], OSCHandler.ueData[UnifiedExpressions.EyeSquintLeft]);
                float fRightOpenness = CalculateEyeOpenness(OSCHandler.eyelids[1], OSCHandler.ueData[UnifiedExpressions.EyeSquintRight]);

                UnifiedTracking.Data.Eye.Left.Openness = _overrider.Apply(Overrider.EyeExpression.EyeLeftOpenness, fLeftOpenness);
                UnifiedTracking.Data.Eye.Right.Openness = _overrider.Apply(Overrider.EyeExpression.EyeRightOpenness, fRightOpenness);
            }
        }

        private void UpdateFaceTracking()
        {
            foreach (KeyValuePair<UnifiedExpressions, float> entry in OSCHandler.ueData)
            {
                UnifiedTracking.Data.Shapes[(int)entry.Key].Weight = _overrider.Apply(entry.Key, entry.Value);
            }
            //?? fix some weird ft things need to check (Its not needed anymore, this in fact causes upper lip to not move properly, its more notorious when you try to set an "eww" face)
            // UnifiedTracking.Data.Shapes[(int)MouthUpperUpLeft].Weight = Math.Max(0, UnifiedTracking.Data.Shapes[(int)MouthUpperUpLeft].Weight - UnifiedTracking.Data.Shapes[(int)NoseSneerLeft].Weight);
            // UnifiedTracking.Data.Shapes[(int)MouthUpperUpRight].Weight = Math.Max(0, UnifiedTracking.Data.Shapes[(int)MouthUpperUpRight].Weight - UnifiedTracking.Data.Shapes[(int)NoseSneerRight].Weight);
            // UnifiedTracking.Data.Shapes[(int)MouthUpperDeepenLeft].Weight = Math.Max(0, UnifiedTracking.Data.Shapes[(int)MouthUpperUpLeft].Weight - UnifiedTracking.Data.Shapes[(int)NoseSneerLeft].Weight);
            // UnifiedTracking.Data.Shapes[(int)MouthUpperDeepenRight].Weight = Math.Max(0, UnifiedTracking.Data.Shapes[(int)MouthUpperUpRight].Weight - UnifiedTracking.Data.Shapes[(int)NoseSneerRight].Weight);

            //lip Suck
            float lipSuckUpperLeftFixed = Math.Min(1.0f - (float)Math.Pow(UnifiedTracking.Data.Shapes[(int)MouthUpperLeft].Weight, 1f / 6f), UnifiedTracking.Data.Shapes[(int)LipSuckUpperLeft].Weight);
            float lipSuckUpperRightFixed = Math.Min(1.0f - (float)Math.Pow(UnifiedTracking.Data.Shapes[(int)MouthUpperRight].Weight, 1f / 6f), UnifiedTracking.Data.Shapes[(int)LipSuckUpperRight].Weight);
            UnifiedTracking.Data.Shapes[(int)LipSuckUpperLeft].Weight = _overrider.Apply(LipSuckUpperLeft, lipSuckUpperLeftFixed);
            UnifiedTracking.Data.Shapes[(int)LipSuckUpperRight].Weight = _overrider.Apply(LipSuckUpperRight, lipSuckUpperRightFixed);
        }

        public override void Update()
        {
            Thread.Sleep(10);
            if (Status != ModuleState.Active)
            {
                return;
            }
            if (_eyeTrackingEnabled)
            {
                UpdateEyeTracking();
            }
            if (_faceTrackingEnabled)
            {
                UpdateFaceTracking();
            }
        }
        public override void Teardown()
        {
            OSCHandler?.Teardown();
            _overrider?.Teardown();
        }
    }
}