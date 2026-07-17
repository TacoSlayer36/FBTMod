using MelonLoader;
using ReplayMod;
using ReplayMod.Replay;
using ReplayMod.Replay.Serialization;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using static FBTMod.Main;

namespace FBTMod;

public class ReplayModExtension
{
    // This example demonstrates how to extend replays by recording
    // and replaying a scene object (in this case, the Park bell [RIP]).

    public static ReplayModExtension instance;
    public ReplayModExtension() => instance = this;

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

            // Capture transform state for this frame
            frame.SetExtensionData(this, new FBTState
            {
                trackerTransforms = Main.instance.runtimeTrackerTransforms
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

            foreach (var transform in state.trackerTransforms)
            {
                //if (transform.Key is TrackerRole.RightFoot)
                writer.WriteChunk((int)transform.Key, w =>
                {
                    w.Write(FBTField.Position, transform.Value.position);
                    w.Write(FBTField.Rotation, transform.Value.rotation);
                });
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
            //subIndex = (int)TrackerRole.RightFoot;
            var state = ReplaySerializer.ReadChunk<FBTState, FBTField>(
                br,
                () => lastState?.Clone() ?? new FBTState(),
                (s, field, size, reader) =>
                {
                    if (!s.trackerTransforms.ContainsKey((TrackerRole)subIndex))
                    {
                        s.trackerTransforms[(TrackerRole)subIndex] = new Main.Pose { position = Vector3.zero, rotation = Quaternion.identity };
                    }

                    switch (field)
                    {
                        case FBTField.Position:
                            s.trackerTransforms[(TrackerRole)subIndex].position = reader.ReadVector3();
                            break;

                        case FBTField.Rotation:
                            s.trackerTransforms[(TrackerRole)subIndex].rotation = reader.ReadQuaternion();
                            break;
                    }
                });

            frame.SetExtensionData(this, state);
            lastState = state;
        }

        // NextFrame should be used for interpolation, though that isn't implemented here.
        public override void OnPlaybackFrame(Frame frame, Frame nextFrame)
        {
            if (!frame.TryGetExtensionData(this, out FBTState state))
                return;

            // Apply reconstructed transform state to the live object.
            MelonLogger.Msg(state.trackerTransforms[TrackerRole.RightFoot].position.y);
        }
    }

    // Simple container for bell transform state
    internal class FBTState
    {
        public Dictionary<TrackerRole, Main.Pose> trackerTransforms = new();

        // Used to preserve previous state during reconstruction.
        public FBTState Clone()
        {
            Dictionary<TrackerRole, Main.Pose> newTransforms = new();
            foreach (var transform in trackerTransforms)
            {
                newTransforms[transform.Key] = new Main.Pose()
                {
                    position = transform.Value.position,
                    rotation = transform.Value.rotation
                };
            }

            return new FBTState
            {
                trackerTransforms = newTransforms
            };
        }
    }
}