using BuildSoft.OscCore;
using Il2CppRUMBLE.Managers;
using Il2CppRUMBLE.Utilities;
using MelonLoader;
using System;
using UnityEngine;

namespace FBTMod
{
    internal class EyeTracking
    {
        public static OscServer server;

        public static float LeftPitch = 0;
        public static float RightPitch = 0;
        public static float LeftYaw = 0;
        public static float RightYaw = 0;
        public static float CloseAmount = 0;

        public static Material GazeVisualizerMat;
        public static GameObject GazeVisualizerPanel;

        private static bool receivedDataThisFrame = false;
        private static float timeLastDataReceived;
        public static bool IsReceivingData => Time.realtimeSinceStartup < timeLastDataReceived + 0.25f;

        public static void Start()
        {
            server = new OscServer(9000);

            server.TryAddMethod("/tracking/eye/LeftRightPitchYaw", OnPitchYaw);
            server.TryAddMethod("/tracking/eye/EyesClosedAmount", OnEyesClosed);

            Main.instance.LoggerInstance.Msg("Listening for eye tracking on port 9000...");
            server.Start();

            GazeVisualizerPanel.SetActive(Config.ShowGazeVisualizer.EditedValue);

            PlayerManager.Instance.LocalPlayer.Controller.PlayerEyeSystem.enabled = false;
        }

        public static void Stop()
        {
            server.Dispose();
            GazeVisualizerPanel.SetActive(false);
            PlayerManager.Instance.LocalPlayer.Controller.PlayerEyeSystem.enabled = true;
        }

        static void OnPitchYaw(OscMessageValues values)
        {
            LeftPitch = values.ReadFloatElement(0);
            LeftYaw = values.ReadFloatElement(1);
            RightPitch = values.ReadFloatElement(2);
            RightYaw = values.ReadFloatElement(3);

            receivedDataThisFrame = true;
        }

        static void OnEyesClosed(OscMessageValues values)
        {
            CloseAmount = Mathf.Clamp01(values.ReadFloatElement(0));
            //MelonLogger.Msg($"{values.ReadFloatElement(0)},     {values.ReadFloatElement(1)},     {values.ReadFloatElement(2)}");

            receivedDataThisFrame = true;
        }

        public static void Update()
        {
            if (receivedDataThisFrame)
                timeLastDataReceived = Time.realtimeSinceStartup;
            receivedDataThisFrame = false;

            if (!Main.globalInit) return;

            Transform head = PlayerManager.Instance.LocalPlayer.Controller.PlayerVR.headset.Transform;

            float avgPitch = (LeftPitch + RightPitch) / 2f;
            float avgYaw = (LeftYaw + RightYaw) / 2f;
            Vector3 lookDir = head.rotation * Quaternion.Euler(avgPitch, avgYaw * 1.2f, 0.0f) * Vector3.forward;

            Vector3 lookTarget = Vector3.zero;
            if (Physics.Raycast(head.position + lookDir * 0.4f, lookDir, out RaycastHit hit, 100f))
            {
                lookTarget = hit.point + lookDir * -0.3f;
            }
            else
            {
                lookTarget = head.transform.position + lookDir * 100f;
            }

            Vector2 screenPoint = RecordingCamera.Instance.LegacyCamera.WorldToScreenPoint(lookTarget);

            float dist = Vector3.Distance(head.transform.position, lookTarget);

            GazeVisualizerMat.SetFloat("_Darkness", 0.7f);

            GazeVisualizerMat.SetFloat("_Proportional", 1f - CloseAmount);
            GazeVisualizerMat.SetFloat("_Radius", 1f / Mathf.Clamp(dist, 2.25f, 60f) / 7.5f);
            GazeVisualizerMat.SetVector("_Center", new Vector4(screenPoint.x / Screen.width, screenPoint.y / Screen.height, 0f, 0f));
        }

        public static Quaternion GetLeftEyeRot()
        {
            return Quaternion.Euler((RightPitch + LeftPitch) / 2f, LeftYaw, 0.0f);
        }

        public static Quaternion GetRightEyeRot()
        {
            return Quaternion.Euler((RightPitch + LeftPitch) / 2f, RightYaw, 0.0f);
        }

        public static void OnEnableEyeTrackingToggled(bool _, bool newValue)
        {
            if (newValue) Start();
            else Stop();
        }

        public static void OnShowGazeVisualizerToggled(bool _, bool newValue)
        {
            GazeVisualizerPanel?.SetActive(newValue);
        }
    }
}
