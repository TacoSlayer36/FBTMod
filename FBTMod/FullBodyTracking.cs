using Il2CppRUMBLE.Players;
using Il2CppRUMBLE.Players.Scaling;
using MelonLoader;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem.LowLevel;
using Valve.VR;
using static FBTMod.Main;
using Pose = FBTMod.Main.Pose;

namespace FBTMod;

[RegisterTypeInIl2Cpp]
public class FullBodyTracking : MonoBehaviour
{
    public PlayerController Owner;

    public enum FBTType
    {
        Tracked,
        Networked,
        Animated,
        None
    }
    public FBTType Type = FBTType.None;

    public bool Disabled = true;

    private Transform trackersParent;
    public LegIKSolver LeftLegSolver;
    public LegIKSolver RightLegSolver;

    public GameObject[] debugSpheres = new GameObject[64];

    public Dictionary<TrackerRole, TrackerCalibration> TrackerOffsets = new();
    public Dictionary<TrackerRole, Pose> RuntimeTrackerTransforms = new();

    public void Start()
    {
        Main.AllFBTs.Add(this);

        // Create tracking spheres
        trackersParent = new GameObject("TrackerSpheres").transform;
        trackersParent.SetParent(Owner.transform);
        for (int i = 0; i < debugSpheres.Length; i++)
        {
            GameObject trackerSphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            trackerSphere.GetComponent<Renderer>().material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            GameObject.Destroy(trackerSphere.GetComponent<Collider>());
            trackerSphere.transform.localScale = Vector3.one * 0.1f;
            trackerSphere.transform.SetParent(trackersParent);
            trackerSphere.SetActive(Config.ShowTrackingMarkers.Value);
            debugSpheres[i] = trackerSphere;
        }
    }

    public void Destroy()
    {
        if (trackersParent != null)
            GameObject.Destroy(trackersParent);
        Main.AllFBTs.Remove(this);
    }

    public void ToggleTrackingMarkers(bool enabled)
    {
        trackersParent?.gameObject?.SetActive(enabled);
    }

    public bool CreateLegSolvers()
    {
        Animator animator = Owner.PlayerAnimator.animator;

        Transform leftUpperLeg = animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
        Transform leftLowerLeg = animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg);
        Transform leftFoot = animator.GetBoneTransform(HumanBodyBones.LeftFoot);

        Transform rightUpperLeg = animator.GetBoneTransform(HumanBodyBones.RightUpperLeg);
        Transform rightLowerLeg = animator.GetBoneTransform(HumanBodyBones.RightLowerLeg);
        Transform rightFoot = animator.GetBoneTransform(HumanBodyBones.RightFoot);

        if (!RuntimeTrackerTransforms.TryGetValue(TrackerRole.LeftFoot, out var leftFootTarget) ||
            !RuntimeTrackerTransforms.TryGetValue(TrackerRole.RightFoot, out var rightFootTarget) ||
            !RuntimeTrackerTransforms.TryGetValue(TrackerRole.LeftKnee, out var leftKneeHint) ||
            !RuntimeTrackerTransforms.TryGetValue(TrackerRole.RightKnee, out var rightKneeHint))
        {
            Main.instance.LoggerInstance.Error("Could not create custom leg solvers. Missing FBT targets.");
            return false;
        }

        Vector3 defaultBendDir = animator.transform.forward;

        LeftLegSolver = new LegIKSolver(
            leftUpperLeg,
            leftLowerLeg,
            leftFoot,
            leftFootTarget,
            leftKneeHint,
            defaultBendDir
        );

        RightLegSolver = new LegIKSolver(
            rightUpperLeg,
            rightLowerLeg,
            rightFoot,
            rightFootTarget,
            rightKneeHint,
            defaultBendDir
        );

        LeftLegSolver.Weight = 1f;
        RightLegSolver.Weight = 1f;

        Main.instance.LoggerInstance.Msg("Leg solvers created.");
        return true;
    }

    public void ToggleVrikLegSolving(bool toggle)
    {
        var ik = Owner.PlayerIK.VrIK;
        var value = toggle ? 1f : 0f;

        ik.solver.leftLeg.positionWeight = value;
        ik.solver.rightLeg.positionWeight = value;

        ik.solver.leftLeg.rotationWeight = value;
        ik.solver.rightLeg.rotationWeight = value;

        ik.solver.leftLeg.bendGoalWeight = value;
        ik.solver.rightLeg.bendGoalWeight = value;
    }

    // Builds target calibration points based on the current pose of the player (should be T-Pose when ran)
    public void CreateCalibrationTargets()
    {
        Animator animator = Owner.PlayerAnimator.animator;

        Transform hips = animator.GetBoneTransform(HumanBodyBones.Hips);
        Transform chest = animator.GetBoneTransform(HumanBodyBones.Chest);

        Transform leftLowerLeg = animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg);
        Transform rightLowerLeg = animator.GetBoneTransform(HumanBodyBones.RightLowerLeg);
        Transform leftFoot = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
        Transform rightFoot = animator.GetBoneTransform(HumanBodyBones.RightFoot);

        Vector3 bendForward = animator.transform.forward;

        Pose hipTarget = new Pose();
        hipTarget.position = hips.position;
        hipTarget.rotation = hips.rotation;

        Pose chestTarget = new Pose();
        chestTarget.position = chest.position;
        chestTarget.rotation = chest.rotation;

        Pose leftKneeTarget = new Pose();
        leftKneeTarget.position = leftLowerLeg.position + bendForward * 0.05f;
        leftKneeTarget.rotation = leftLowerLeg.rotation;

        Pose rightKneeTarget = new Pose();
        rightKneeTarget.position = rightLowerLeg.position + bendForward * 0.05f;
        rightKneeTarget.rotation = rightLowerLeg.rotation;

        Pose leftFootTarget = new Pose();
        leftFootTarget.position = leftFoot.position;
        leftFootTarget.rotation = leftFoot.rotation;

        Pose rightFootTarget = new Pose();
        rightFootTarget.position = rightFoot.position;
        rightFootTarget.rotation = rightFoot.rotation;

        RuntimeTrackerTransforms = new Dictionary<TrackerRole, Pose>()
            {
                { TrackerRole.Chest, chestTarget },
                { TrackerRole.Hips, hipTarget },
                { TrackerRole.LeftFoot, leftFootTarget },
                { TrackerRole.RightFoot, rightFootTarget },
                { TrackerRole.LeftKnee, leftKneeTarget },
                { TrackerRole.RightKnee, rightKneeTarget }
            };
    }

    public void Update()
    {
        if (Disabled) return;

        if (Type is not FBTType.Tracked) return;
        if (Type is FBTType.None) return;
        if (Main.instance.VRSystem == null) return;
        if (Owner == null) return;

        Main.instance.VRSystem.GetDeviceToAbsoluteTrackingPose(
            ETrackingUniverseOrigin.TrackingUniverseStanding,
            0,
            Main.instance.poses
        );

        for (uint i = 0; i < Main.instance.poses.Length; i++)
        {
            bool connected = Main.instance.VRSystem.IsTrackedDeviceConnected(i);

            // Avoids the HMD/Controllers
            if (Main.instance.VRSystem.GetTrackedDeviceClass(i) != ETrackedDeviceClass.GenericTracker)
                continue;

            TrackedDevicePose_t pose = Main.instance.poses[i];
            HmdMatrix34_t matrix = pose.mDeviceToAbsoluteTracking;

            // Flipped on the local Z-axis
            Vector3 position = new Vector3(
                matrix.m3,
                matrix.m7,
                -matrix.m11
            );

            Vector3 forward = new Vector3(
                -matrix.m2,
                -matrix.m6,
                matrix.m10
            );

            Vector3 up = new Vector3(
                matrix.m1,
                matrix.m5,
                -matrix.m9
            );

            Quaternion rotation = Quaternion.LookRotation(forward, up);

            Transform root = Owner.PlayerVR.transform;

            position = root.TransformPoint(position);
            rotation = root.rotation * rotation;

            Main.instance.Trackers[i] = new TrackerState
            {
                DeviceIndex = i,
                position = position,
                rotation = rotation,
                IsConnected = connected
            };
        }
    }

    public void LateUpdate()
    {
        if (Type is FBTType.None) return;
        if (Disabled) return;

        if (Type is FBTType.Tracked && IsCalibrated && !IsCalibrating)
        {
            UpdateRuntimeTrackers();
        }

        else if (Type is FBTType.Networked)
        {
            // INSERT NETWORKING
        }

        // Animated type is handled by FBTReplayExtension

        ApplyHipAndChestTracking();
        ApplyLegTracking();
    }

    private void UpdateRuntimeTrackers()
    {
        Transform root = Owner.PlayerVR.transform;

        for (int i = 0; i < TrackerOffsets.Count; i++)
        {
            var (role, calibration) = TrackerOffsets.ElementAt(i);

            if (!RuntimeTrackerTransforms.TryGetValue(role, out var transform))
                continue;

            if (!Main.instance.Trackers.TryGetValue(calibration.DeviceIndex, out var state))
                continue;

            Vector3 localTrackerPos = root.InverseTransformPoint(state.position);
            Vector3 correctedLocalPos = localTrackerPos + calibration.offset.position;

            transform.position = root.TransformPoint(correctedLocalPos);

            Quaternion localTrackerRot = ToLocalRotation(root, state.rotation);
            Quaternion correctedLocalRot = localTrackerRot * calibration.offset.rotation;

            transform.rotation = ToWorldRotation(root, correctedLocalRot);

            debugSpheres[i].transform.position = transform.position;
            debugSpheres[i].transform.rotation = transform.rotation;
        }
    }

    private void ApplyHipAndChestTracking()
    {
        if (Owner == null) return;
        var animator = Owner.PlayerAnimator.animator;

        if (RuntimeTrackerTransforms.TryGetValue(TrackerRole.Hips, out var transform))
        {
            var hipsBone = animator.GetBoneTransform(HumanBodyBones.Hips);
            hipsBone.position = Vector3.Lerp(hipsBone.position, transform.position, HIP_POSITION_WEIGHT);
            hipsBone.rotation = Quaternion.Slerp(hipsBone.rotation, transform.rotation, HIP_ROTATION_WEIGHT);
        }
        if (RuntimeTrackerTransforms.TryGetValue(TrackerRole.Chest, out transform))
        {
            var chestBone = animator.GetBoneTransform(HumanBodyBones.Chest);
            chestBone.rotation = Quaternion.Slerp(chestBone.rotation, transform.rotation, CHEST_ROTATION_WEIGHT);
        }
    }

    private void ApplyLegTracking()
    {
        for (int i = 0; i < LEG_SOLVE_ITERATIONS; i++)
        {
            LeftLegSolver?.Solve();
            RightLegSolver?.Solve();
        }
    }
}
