using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using BuildSoft.OscCore;
using Il2CppRUMBLE.Input;
using Il2CppRUMBLE.Managers;
using Il2CppRUMBLE.Players;
using Il2CppSmartLocalization.Editor;
using MelonLoader;
using ReplayMod.Replay;
using RumbleModdingAPI.RMAPI;
using UnityEngine;
using UnityEngine.UI;
using Valve.VR;
using Main = FBTMod.Main;

[assembly: MelonInfo(typeof(Main), "FBTMod", "1.0.0", "ERROR")]
[assembly: MelonGame("Buckethead Entertainment", "RUMBLE")]
[assembly: MelonAdditionalDependencies("UIFramework")]
[assembly: MelonColor(255, 255, 0, 0), MelonAuthorColor(255, 255, 0, 0)]

namespace FBTMod
{
    public class Main : MelonMod
    {
        public static float AA_L = 10f;
        public static float AA_R = -10f;

        public static Main instance;
        public Main() => instance = this;

        public static bool globalInit = false;

        public static Player LocalPlayer => PlayerManager.instance?.LocalPlayer;

        private static Transform modParent;
        
        public CVRSystem VRSystem;

        internal static bool IsCalibrated;
        internal static bool IsCalibrating;

        public static List<FullBodyTracking> AllFBTs = new();
        public static FullBodyTracking LocalFBT;
        private static Transform referenceSkeleton;

        // OpenVR device indexes are in a fixed range of 64
        public TrackedDevicePose_t[] poses = new TrackedDevicePose_t[64];

        public Dictionary<uint, TrackerState> Trackers = new();

        // Trackers
        public class TrackerState
        {
            public uint DeviceIndex;
            public Vector3 position;
            public Quaternion rotation;
            public bool IsConnected;
        }

        // Supported trackers
        public enum TrackerRole
        {
            Unassigned,
            Chest,
            Hips,
            LeftKnee,
            RightKnee,
            LeftFoot,
            RightFoot
        }
        
        // Offsets
        public class TrackerCalibration
        {
            public uint DeviceIndex;
            public Pose offset;
        }

        public class Pose
        {
            public Vector3 position;
            public Quaternion rotation;
        }

        // ReplayMod Support
        private ReplayExtension fbtExtension;
        private ReplayExtension etExtension;

        // Settings
        public const int LEG_SOLVE_ITERATIONS = 3;

        public const float HIP_POSITION_WEIGHT = 1f;
        public const float HIP_ROTATION_WEIGHT = 1f;

        public const float CHEST_POSITION_WEIGHT = 1f;
        public const float CHEST_ROTATION_WEIGHT = 1f;

        internal const string USER_DATA = "UserData/FullBodyTracking";
        
        // ----------------------------------------------------------------
        
        public override void OnLateInitializeMelon() =>
            Actions.onMapInitialized += _ => OnMapInitialized();

        public override void OnInitializeMelon()
        {
            // Doesn't create it if it already exists
            Directory.CreateDirectory(USER_DATA);

            modParent = new GameObject("FBTMod").transform;
            GameObject.DontDestroyOnLoad(modParent.gameObject);

            Config.SetUp();

            // Load gaze visualizer from asset bundle
            GazeVisualizerPanel = GameObject.Instantiate(AssetBundles.LoadAssetFromStream<GameObject>(this, "FBTMod.assets.fbt", "GazeVisualizer"));
            GazeVisualizerPanel.transform.SetParent(modParent);
            GazeVisualizerMat = GazeVisualizerPanel.GetComponentInChildren<Image>().material;
            GazeVisualizerPanel.SetActive(false);

            EVRInitError error = EVRInitError.None;
            VRSystem = OpenVR.Init(ref error, EVRApplicationType.VRApplication_Other);

            if (error != EVRInitError.None)
            {
                LoggerInstance.Error($"[FBT] OpenVR initialization failed: {error}");
                VRSystem = null;
                return;
            }

            LoggerInstance.Msg("[FBT] OpenVR initialized.");
        }

        public override void OnEarlyInitializeMelon()
        {
            // ReplayMod
            //fbtExtension = ReplayAPI.RegisterExtension(new FBTReplayExtension.FBTExtension());
            //etExtension = ReplayAPI.RegisterExtension(new ETReplayExtension.ETExtension());

            //ReplayAPI.onReplayEnded += _ => {
            //    FBTReplayExtension.lastState = null;
            //    ETReplayExtension.lastState = null;
            //};
        }

        public override void OnApplicationQuit()
        {
            if (VRSystem != null)
            {
                OpenVR.Shutdown();
                VRSystem = null;
            }
        }

        private void OnMapInitialized()
        {
            EnsureStaticObjects();

            if (!globalInit && UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "Gym")
            {
                RunGlobalInit();
            }
        }

        private void RunGlobalInit()
        {
            if (Config.EnableEyeTracking.Value) StartEyeTracking();

            globalInit = true;
        }

        public static void EnsureStaticObjects()
        {
            if (referenceSkeleton != null)
                return;

            PlayerController templateController = Resources
                .FindObjectsOfTypeAll<PlayerController>()
                .FirstOrDefault(p => !p.gameObject.scene.IsValid());

            Transform visuals = templateController.PlayerVisuals.transform;

            referenceSkeleton = GameObject.Instantiate(visuals.GetChild(1).gameObject).transform;
            referenceSkeleton.name = "ReferenceSkeleton";
            referenceSkeleton.SetParent(modParent);
        }

        // T-Pose
        public static void ToggleTPose(PlayerController target, bool toggle)
        {
            target.PlayerIK.enabled = !toggle;
            target.PlayerIK.VrIK.enabled = !toggle;

            var animator = target.PlayerAnimator.animator;
            animator.enabled = !toggle;

            if (toggle && referenceSkeleton != null)
            {
                Transform playerSkeleton = animator.transform.GetChild(1);
                CopySkeleton(playerSkeleton, referenceSkeleton);
            }
        }

        public static void CopySkeleton(Transform target, Transform source)
        {
            target.localRotation = source.localRotation;

            for (int i = 0; i < target.childCount; i++)
            {
                Transform targetChild = target.GetChild(i);
                Transform sourceChild = source.Find(targetChild.name);

                if (sourceChild != null)
                    CopySkeleton(targetChild, sourceChild);
            }
        }
        
        // ----------------------------------------------------------------

        public static Quaternion ToLocalRotation(Transform root, Quaternion worldRot)
        {
            return Quaternion.Inverse(root.rotation) * worldRot;
        }

        public static Quaternion ToWorldRotation(Transform root, Quaternion localRot)
        {
            return root.rotation * localRot;
        }
        
        private bool AssignNearestTrackers()
        {
            LocalFBT.TrackerOffsets.Clear();

            Transform root = LocalPlayer.Controller.PlayerVR.transform;
            List<uint> available = Trackers.Keys.ToList();
            
            if (available.Count < 6)
            {
                LoggerInstance.Error($"Not enough trackers for full calibration. Found {Trackers.Count}, expected 6.");
                return false;
            }

            foreach (var pair in LocalFBT.RuntimeTrackerTransforms)
            {
                TrackerRole role = pair.Key;
                Pose target = pair.Value;

                uint bestIndex = 0;
                float bestDistance = float.MaxValue;
                bool found = false;

                foreach (uint index in available)
                {
                    float dist = Vector3.Distance(Trackers[index].position, target.position);

                    if (dist < bestDistance)
                    {
                        bestDistance = dist;
                        bestIndex = index;
                        found = true;
                    }
                }

                if (!found)
                {
                    LoggerInstance.Warning($"Could not find tracker for {role}.");
                    continue;
                }

                available.Remove(bestIndex);

                Vector3 localTrackerPos = root.InverseTransformPoint(Trackers[bestIndex].position);
                Vector3 localTargetPos = root.InverseTransformPoint(target.position);

                Quaternion localTrackerRot = ToLocalRotation(root, Trackers[bestIndex].rotation);
                Quaternion localTargetRot = ToLocalRotation(root, target.rotation);
                
                LocalFBT.TrackerOffsets[role] = new TrackerCalibration
                {
                    DeviceIndex = bestIndex,
                    offset = new Pose
                    {
                        position = localTargetPos - localTrackerPos,
                        rotation = Quaternion.Inverse(localTrackerRot) * localTargetRot
                    }
                };
                
                LoggerInstance.Msg($"{role} assigned to tracker {bestIndex}, distance {bestDistance:F3}");
            }

            bool hasMinimum =
                LocalFBT.TrackerOffsets.ContainsKey(TrackerRole.Hips) &&
                LocalFBT.TrackerOffsets.ContainsKey(TrackerRole.Chest) &&
                LocalFBT.TrackerOffsets.ContainsKey(TrackerRole.LeftFoot) &&
                LocalFBT.TrackerOffsets.ContainsKey(TrackerRole.RightFoot) &&
                LocalFBT.TrackerOffsets.ContainsKey(TrackerRole.LeftKnee) &&
                LocalFBT.TrackerOffsets.ContainsKey(TrackerRole.RightKnee);

            if (!hasMinimum)
            {
                LoggerInstance.Error("Calibration failed: missing required trackers.");
                return false;
            }

            return true;
        }
        
        internal IEnumerator Calibration()
        {
            static bool AreBothTriggersPressed()
            {
                return Calls.ControllerMap.LeftController.GetTrigger() > 0.75f &&
                       Calls.ControllerMap.RightController.GetTrigger() > 0.75f;
            }
            
            IsCalibrating = true;
            IsCalibrated = false;

            EnsureStaticObjects();
            
            ToggleTPose(LocalPlayer.Controller, true);

            yield return null;

            while (AreBothTriggersPressed())
                yield return null;

            while (!AreBothTriggersPressed())
                yield return null;
            
            // Player is (hopefully) matching T-Pose, calibrate.

           LocalFBT.CreateCalibrationTargets();
            
            if (!AssignNearestTrackers())
            {
                ToggleTPose(LocalPlayer.Controller, false);
                IsCalibrating = false;
                yield break;
            }

            if (!LocalFBT.CreateLegSolvers())
            {
                ToggleTPose(LocalPlayer.Controller, false);
                IsCalibrating = false;
                yield break;
            }

            ToggleTPose(LocalPlayer.Controller, false);

            LocalFBT.ToggleVrikLegSolving(false);

            IsCalibrating = false;
            IsCalibrated = true;
            LocalFBT.Disabled = false;
        }

        public static void OnShowTrackersToggled(bool _, bool newValue)
        {
            ToggleTrackingMarkers(newValue);
        }
        public static void ToggleTrackingMarkers(bool enabled)
        {
            foreach (FullBodyTracking fbt in AllFBTs)
            {
                fbt.ToggleTrackingMarkers(enabled);
            }
        }

        // EYE TRACKING

        public static float LocalLeftPitch = 0f;
        public static float LocalLeftYaw = 0f;
        public static float LocalRightPitch = 0f;
        public static float LocalRightYaw = 0f;
        public static float LocalCloseAmount = 0f;

        public static List<EyeTracking> AllETs = new();
        public static EyeTracking LocalET;

        public static Material GazeVisualizerMat;
        public static GameObject GazeVisualizerPanel;

        public static bool ReceivedDataThisFrame = false;
        public static DateTime TimeLastDataReceived;
        public static bool IsReceivingETData => DateTime.Now < TimeLastDataReceived.AddSeconds(0.25f);

        internal static OscServer? server = null;

        public static void StartEyeTracking()
        {
            server = new OscServer(9000);

            server.TryAddMethod("/tracking/eye/LeftRightPitchYaw", OnPitchYaw);
            server.TryAddMethod("/tracking/eye/EyesClosedAmount", OnEyesClosed);

            Main.instance.LoggerInstance.Msg("Listening for eye tracking on port 9000...");
            server.Start();

            GazeVisualizerPanel.SetActive(Config.ShowGazeVisualizer.EditedValue);

            PlayerManager.Instance.LocalPlayer.Controller.PlayerEyeSystem.enabled = false;
        }

        public static void StopEyeTracking()
        {
            server.Dispose();
            server = null;
            GazeVisualizerPanel.SetActive(false);
            PlayerManager.Instance.LocalPlayer.Controller.PlayerEyeSystem.enabled = true;
        }

        static void OnPitchYaw(OscMessageValues values)
        {
            LocalLeftPitch = values.ReadFloatElement(0);
            LocalLeftYaw = values.ReadFloatElement(1) + 10f;
            LocalRightPitch = values.ReadFloatElement(2);
            LocalRightYaw = values.ReadFloatElement(3) - 10f;
            ReceivedDataThisFrame = true;
        }

        static void OnEyesClosed(OscMessageValues values)
        {
            LocalCloseAmount = values.ReadFloatElement(0);
            ReceivedDataThisFrame = true;
        }

        public static void OnEnableEyeTrackingToggled(bool _, bool newValue)
        {
            if (newValue) StartEyeTracking();
            else StopEyeTracking();
        }

        public static void OnShowGazeVisualizerToggled(bool _, bool newValue)
        {
            GazeVisualizerPanel?.SetActive(newValue);
        }
    }
    
    public class LegIKSolver
    {
        private Transform UpperLeg;
        private Transform LowerLeg;
        private Transform Foot;

        public Main.Pose FootTarget;
        private Main.Pose KneeHint;

        public float Weight = 1f;
        
        private readonly float upperLen;
        private readonly float lowerLen;

        private Vector3 lastGoodBendDir;

        public LegIKSolver(
            Transform upperLeg,
            Transform lowerLeg,
            Transform foot,
            Main.Pose footTarget,
            Main.Pose kneeHint,
            Vector3 defaultBendDir
        )
        {
            UpperLeg = upperLeg;
            LowerLeg = lowerLeg;
            Foot = foot;

            FootTarget = footTarget;
            KneeHint = kneeHint;

            upperLen = Vector3.Distance(UpperLeg.position, LowerLeg.position);
            lowerLen = Vector3.Distance(LowerLeg.position, foot.position);

            lastGoodBendDir = defaultBendDir.sqrMagnitude > 0.0001f
                ? defaultBendDir.normalized
                : Vector3.forward;
        }

        public void Solve()
        {
            if (UpperLeg == null || LowerLeg == null || Foot == null)
                return;

            Vector3 hipPos = UpperLeg.position;
            Vector3 targetFootPos = FootTarget.position;
            
            Vector3 hipToFoot = targetFootPos - hipPos;

            if (hipToFoot.sqrMagnitude < 0.0001f)
                return;

            float maxReach = upperLen + lowerLen - 0.001f;
            float minReach = Mathf.Abs(upperLen - lowerLen) + 0.001f;

            float rawDist = hipToFoot.magnitude;
            float dist = Mathf.Clamp(rawDist, minReach, maxReach);

            // Direction from the hip to the foot with distance removed.
            Vector3 legForward = hipToFoot.normalized;
            // Clamped foot tracker position based off avatar leg length
            Vector3 solvedFootPos = hipPos + legForward * dist;
            
            Vector3 hipToKneeHint = KneeHint.position - hipPos;
            // Flattens the vector to only tell which side of the hip-to-foot line it's on rather than the distance along it.
            Vector3 bendDir = Vector3.ProjectOnPlane(hipToKneeHint, legForward);

            // Corrections in case of odd positioning
            if (bendDir.sqrMagnitude < 0.0001f)
                bendDir = Vector3.ProjectOnPlane(lastGoodBendDir, legForward);
            
            if (bendDir.sqrMagnitude < 0.0001f)
                bendDir = Vector3.ProjectOnPlane(UpperLeg.forward, legForward);

            if (bendDir.sqrMagnitude < 0.0001f)
                return;

            bendDir.Normalize();

            // Sudden flip correction
            if (Vector3.Dot(bendDir, lastGoodBendDir) < -0.25f)
                bendDir = lastGoodBendDir;
            else
                lastGoodBendDir = bendDir;
            
            // Knee Position calculation
            float x = (dist * dist + upperLen * upperLen - lowerLen * lowerLen) / (2f * dist);
            float ySquared = upperLen * upperLen - x * x;
            float y = Mathf.Sqrt(Mathf.Max(0f, ySquared));

            Vector3 solvedKneePos = hipPos + legForward * x + bendDir * y;
            
            // The knee is a position with distance upper leg length from the hip, and lower leg length from the foot.
            // To solve for that, we find how far along the hip-to-foot line we have to go before the knee is perpendicular to this point.
            // Then, to solve for the y position, we have x^2 + y^2 = upperLen^2. We can solve for y to see how far the knee sticks out.
            
            // To find the position of the knee that fits within the length of the avatar's leg:
            // We start at the hip, and then move x along the hip-to-foot line. This puts us so the knee is perpendicular to this point.
            // Then, we move y in the knee bend direction, which is the distance perpendicular to the hip-to-foot line. This gives us the final
            // solved knee position.

            // Rotates the upper leg transform so the child joint, the knee, points toward the solved position
            RotateBoneToPoint(UpperLeg, LowerLeg.position, solvedKneePos, Weight);
            // Simply rotates the lower leg's end point to match the (clamped) foot tracker position.
            RotateBoneToPoint(LowerLeg, Foot.position, solvedFootPos, Weight);
        }

        private static void RotateBoneToPoint(Transform bone, Vector3 currentEndPos, Vector3 desiredEndPos, float weight)
        {
            Vector3 currentDir = currentEndPos - bone.position;
            Vector3 desiredDir = desiredEndPos - bone.position;

            if (currentDir.sqrMagnitude < 0.0001f || desiredDir.sqrMagnitude < 0.0001f)
                return;
            
            Quaternion delta = Quaternion.FromToRotation(currentDir, desiredDir);
            Quaternion targetRotation = delta * bone.rotation;

            bone.rotation = Quaternion.Slerp(bone.rotation, targetRotation, Mathf.Clamp01(weight));
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(PlayerController), "Initialize")]
    public static class PlayerInitPatch
    {
        private static void Postfix(ref Player player)
        {
            FullBodyTracking newFBT = player.Controller.gameObject.AddComponent<FullBodyTracking>();
            newFBT.Owner = player.Controller;

            EyeTracking newET = player.Controller.gameObject.AddComponent<EyeTracking>();
            newET.Owner = player.Controller;

            if (player.Controller.controllerType is Il2CppRUMBLE.Players.ControllerType.Local) // If the player is you, Tracked
            {
                newFBT.Type = FullBodyTracking.FBTType.Tracked;
                Main.LocalFBT = newFBT;

                newET.Type = FullBodyTracking.FBTType.Tracked;
                newET.Disabled = false;
                Main.LocalET = newET;
            }
            else
            {
                //TODO: if (ReplayMod.Replay.Utilities.IsReplayClone(player.Controller)) // If player is part of a replay, Animated
                if (true)
                {
                    newFBT.Type = FullBodyTracking.FBTType.Animated;
                    newET.Type = FullBodyTracking.FBTType.Animated;
                }
                else // If the player has their own tracking, Networked
                {
                    newFBT.Type = FullBodyTracking.FBTType.Networked;
                    newET.Type = FullBodyTracking.FBTType.Networked;
                }
            }

            if (newFBT.Type is FullBodyTracking.FBTType.Networked or FullBodyTracking.FBTType.Animated)
            {
                newFBT.CreateCalibrationTargets();
                newFBT.CreateLegSolvers();
            }
        }
    }
}