using Il2CppRUMBLE.Players;
using MelonLoader;
using ReplayMod.Replay;
using ReplayMod.Replay.Serialization;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using static FBTMod.Main;
using PoseDict = System.Collections.Generic.Dictionary<FBTMod.Main.TrackerRole, FBTMod.Main.Pose>;

namespace FBTMod;

public class FBTReplayExtension
{
    // This example demonstrates how to extend replays by recording
    // and replaying a scene object

    public static FBTReplayExtension instance;
    public FBTReplayExtension() => instance = this;

    // Used when reading frames to allow state to carry forward from delta-compression
    // Delta-compression is not used in this example, but is highly recommended.
    internal static FBTState lastState;

    internal static MelonPreferences_Entry<bool> RecordFBT;

    // Field identifiers used when writing frame data.
    // These values are serialized as byte tags and must remain in a stable order.
    private enum FBTField : byte
    {
        Position,
        Rotation
    }

    internal class FBTExtension : ReplayExtension
    {
        public override string Id => "FBTSupport";

        public override void OnRecordFrame(Frame frame, bool isBuffer)
        {
            if (!RecordFBT.Value)
                return;

            if (ReplayMod.Core.Main.Recording.RecordedPlayers.Count == 0) return;
            if (!ReplayAPI.IsRecording) return;

            Dictionary<byte, PoseDict> transforms = new();
            for (byte i = 0; i < ReplayMod.Core.Main.Recording.RecordedPlayers.Count; i++)
            {
                Player player = ReplayMod.Core.Main.Recording.RecordedPlayers[i];
                if (player == null) continue;

                FullBodyTracking fbt = player.Controller.GetComponent<FullBodyTracking>();
                if (fbt.Disabled || fbt.Type is FullBodyTracking.FBTType.None) continue;

                transforms[i] = fbt.RuntimeTrackerTransforms;
            }

            if (transforms.Values.Count == 0) return;

            frame.SetExtensionData(this, new FBTState
            {
                TrackerTransforms = transforms
            }.Clone());
        }

        public override void OnWriteFrame(ReplayAPI.FrameExtensionWriter writer, Frame frame)
        {
            // If this frame has no recorded data, write nothing.
            if (!frame.TryGetExtensionData(this, out FBTState state))
                return;

            /*
             * Each Write(field, value) call writes:
             *   - Field ID (1 byte)
             *   - Field payload length (1 byte)
             *   - The actual data (N bytes)
             *
             * The mod groups these field entries into a single chunk
             * for this extension automatically.
             *
             * IMPORTANT:
             *   - Only write fields that changed between frames (delta encoding recommended).
             *   - Do NOT manually write field IDs or lengths using raw bw.Write.
             *     Always use the provided BinaryWriter.Write(field, value) overloads.
            */

            foreach (var (ownerId, poseDict) in state.TrackerTransforms)
            {
                foreach (var transform in poseDict)
                {
                    var subIndex = (ownerId << 8) | (int)transform.Key;
                    writer.WriteChunk(subIndex, w =>
                    {
                        w.Write(FBTField.Position, transform.Value.position);
                        w.Write(FBTField.Rotation, transform.Value.rotation);
                    });
                }
            }
        }

        public override void OnReadFrame(BinaryReader br, Frame frame, int subIndex)
        {
            /*
             * ReadChunk builds a state object for this frame.
             *
             * The ctor function used to create the initial state for this frame.
             * Each field encountered in the chunk mutates the state via the callback.
             *
             * When finished, ReadChunk returns the fully reconstructed  state.
             *
             * Unknown fields are automatically skipped.
             *
             * Technically, the ctor function here is unnecessary due to our lack of delta-compression,
             * but it is highly recommended to do so.
             */

            var state = ReplaySerializer.ReadChunk<FBTState, FBTField>(
                br,
                () => lastState?.Clone() ?? new FBTState(),
                (s, field, size, reader) =>
                {
                    var ownerId = (subIndex >> 8) & 0xff;
                    var trackerRole = (subIndex) & 0xff;

                    if (!s.TrackerTransforms.ContainsKey((byte)ownerId))
                    {
                        s.TrackerTransforms[(byte)ownerId] = new PoseDict();
                    }

                    var poseDict = s.TrackerTransforms[(byte)ownerId];

                    if (!poseDict.ContainsKey((TrackerRole)trackerRole))
                    {
                        poseDict[(TrackerRole)trackerRole] = new Main.Pose
                        {
                            position = Vector3.zero,
                            rotation = Quaternion.identity
                        };
                    }

                    switch (field)
                    {
                        case FBTField.Position:
                            poseDict[(TrackerRole)trackerRole].position = reader.ReadVector3();
                            break;

                        case FBTField.Rotation:
                            poseDict[(TrackerRole)trackerRole].rotation = reader.ReadQuaternion();
                            break;
                    }
                });

            frame.SetExtensionData(this, state);
            lastState = state;
        }

        public override void OnPlaybackFrame(Frame frame, Frame nextFrame)
        {
            foreach (FullBodyTracking fbt in Main.AllFBTs)
            {
                if (fbt.Type is FullBodyTracking.FBTType.Animated)
                    fbt.Disabled = true;
            }

            if (!frame.TryGetExtensionData(this, out FBTState state))
                return;

            if (state == null) return;
            //FBTState lerpedState = LerpStates(state, nextFrame, (ReplayAPI.CurrentTime - frame.Time) / (nextFrame.Time - frame.Time));

            foreach (var (ownerId, poseDict) in state.TrackerTransforms)
            {
                PlayerController player = ReplayMod.Core.Main.Playback.PlaybackPlayers[ownerId].Controller;
                FullBodyTracking fbt = player.GetComponent<FullBodyTracking>();
                if (fbt == null) return;
                fbt.Disabled = false;

                if (fbt.LeftLegSolver == null || fbt.RightLegSolver == null) return;

                fbt.RuntimeTrackerTransforms = poseDict;
                fbt.LeftLegSolver.KneeHint = poseDict[TrackerRole.LeftKnee];
                fbt.LeftLegSolver.FootTarget = poseDict[TrackerRole.LeftFoot];
                fbt.RightLegSolver.KneeHint = poseDict[TrackerRole.RightKnee];
                fbt.RightLegSolver.FootTarget = poseDict[TrackerRole.RightFoot];

                foreach (var (role, tracker) in poseDict)
                {
                    GameObject debugSphere = fbt.debugSpheres[(int)role];
                    if (debugSphere?.active == true)
                        fbt.debugSpheres[(int)role].transform.position = tracker.position;
                        fbt.debugSpheres[(int)role].transform.rotation = tracker.rotation;
                }
            }
        }

        internal static FBTState LerpStates(FBTState a, FBTState b, float t)
        {
            FBTState lerpedState = a.Clone();
            foreach (var (prev, current) in a.TrackerTransforms.Zip(b.TrackerTransforms))
            {
                foreach (var (prevPose, currPose) in prev.Value.Zip(current.Value))
                {
                    prevPose.Value.position = Vector3.Lerp(prevPose.Value.position, currPose.Value.position, t);
                    prevPose.Value.rotation = Quaternion.Slerp(prevPose.Value.rotation, currPose.Value.rotation, t);
                }
            }
            return lerpedState;
        }
    }

    internal class FBTState
    {
        public Dictionary<byte, PoseDict> TrackerTransforms = new();

        // Used to preserve previous state during reconstruction.
        public FBTState Clone()
        {
            Dictionary<byte, PoseDict> newTransforms = new();
            foreach (var (ownerId, poseDict) in TrackerTransforms)
            {
                Dictionary<TrackerRole, Main.Pose> newPoseDict = new();
                foreach (var transform in poseDict)
                {
                    newPoseDict[transform.Key] = new Main.Pose()
                    {
                        position = transform.Value.position,
                        rotation = transform.Value.rotation
                    };
                }
                newTransforms[ownerId] = newPoseDict;
            }

            return new FBTState
            {
                TrackerTransforms = newTransforms
            };
        }
    }
}