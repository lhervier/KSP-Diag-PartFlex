using System.Collections.Generic;
using UnityEngine;

namespace com.github.lhervier.ksp.diag.partflex
{
    /// <summary>
    /// How far the physics has moved one part from its parent, at the attachment that holds it: the stretch of
    /// the attachment point and the bend of the part, both against the pose the joint rests in, and their
    /// drawing. The stretch is drawn as an arrow from where the attachment point should be, as long as the
    /// stretch times a scale; the bend as two sets of axes of the part at that point, grey where the part
    /// should point, coloured where it points, the angle between them times a scale.
    /// </summary>
    internal sealed class Flex
    {
        // Length of the drawn axes, in metres, and the most the bend is drawn turned, in degrees, whatever the
        // scale.
        private const float AXIS_LENGTH = 0.5f;
        private const float MAX_DRAWN_ANGLE = 90f;

        // Size of the cross that marks where the attachment point should be, and of the head of the arrow at
        // most, in metres, whatever the scale.
        private const float MARK_SIZE = 0.03f;
        private const float HEAD_SIZE = 0.15f;

        private static readonly Color REST_AXIS = new Color(0.6f, 0.6f, 0.6f, 1f);
        private static readonly Color[] AXIS_COLORS =
        {
            new Color(1f, 0.2f, 0.2f, 1f),    // x, red
            new Color(0.2f, 1f, 0.2f, 1f),    // y, green
            new Color(0.3f, 0.5f, 1f, 1f),    // z, blue
        };

        // Scratch buffers shared by every rebuild, which all happen on the main thread.
        private static readonly List<Vector3> VERTICES = new List<Vector3>();
        private static readonly List<Color32> COLORS = new List<Color32>();
        private static readonly List<int> INDICES = new List<int>();

        /// <summary>The part the attachment holds.</summary>
        public Part Child { get; }

        /// <summary>The part it holds it to.</summary>
        public Part Parent { get; }

        /// <summary>The attachment.</summary>
        public PartJoint Joint { get; }

        /// <summary>How far the attachment point is from where it should be, in metres, as last measured.</summary>
        public float Stretch { get; private set; }

        /// <summary>The angle the part is turned by from where it should point, in degrees, as last
        /// measured.</summary>
        public float Bend { get; private set; }

        /// <summary>The stretch and the bend measured against the poses the save gives the two parts, in metres
        /// and degrees, as last measured: they match <see cref="Stretch"/> and <see cref="Bend"/> as long as
        /// the game has not rewritten those poses since the joint was made.</summary>
        public float SaveStretch { get; private set; }
        public float SaveBend { get; private set; }

        /// <summary>How far apart the two anchors of the joint were when its rest pose was taken, in metres:
        /// zero when it was taken before the physics moved anything.</summary>
        public float CaptureGap { get; }

        /// <summary>Whether the vessel was on rails when the rest pose of the joint was taken.</summary>
        public bool CapturedOnRails { get; }

        // What was last measured, in the frame of the vessel, so that the drawing follows the vessel and can be
        // built again from it at another scale: where the attachment point should be, the stretch, where the
        // body on the part's side should point, and the axis of its bend.
        private readonly Transform frame;
        private Vector3 expected;
        private Vector3 stretch;
        private Quaternion restRotation = Quaternion.identity;
        private Vector3 bendAxis = Vector3.up;

        private readonly Mesh mesh;
        private bool drawn;

        /// <summary>Creates the measure of <paramref name="joint"/>, whose rest pose was taken as
        /// <paramref name="rest"/>, with nothing measured yet; its drawing follows <paramref name="vessel"/>.</summary>
        public Flex(PartJoint joint, RestPose rest, Vessel vessel)
        {
            Joint = joint;
            Child = joint.Child;
            Parent = joint.Parent;
            CaptureGap = rest.Gap;
            CapturedOnRails = rest.OnRails;
            frame = vessel.transform;
            mesh = new Mesh { name = "KSPDiagPartFlex " + Child.partInfo.name };
        }

        /// <summary>
        /// The two bodies <paramref name="joint"/> links, as the physics has them: the one that carries it
        /// (<paramref name="host"/>, on the side of the part it holds) and the one it is linked to
        /// (<paramref name="connected"/>, on the side of the parent). False when the joint has none.
        /// </summary>
        public static bool Bodies(PartJoint joint, out Transform host, out Transform connected)
        {
            host = null;
            connected = null;
            if (joint == null || joint.joints == null || joint.joints.Count == 0)
            {
                return false;
            }
            ConfigurableJoint first = joint.joints[0];
            if (first == null || first.connectedBody == null)
            {
                return false;
            }
            host = first.transform;
            connected = first.connectedBody.transform;
            return true;
        }

        /// <summary>
        /// Where the attachment point of <paramref name="joint"/> is, in world space, on the side of the part it
        /// holds (<paramref name="onHost"/>) and on the side of the parent (<paramref name="onConnected"/>):
        /// the same point while the joint rests.
        /// </summary>
        public static void AttachPoints(PartJoint joint, out Vector3 onHost, out Vector3 onConnected)
        {
            // Every internal joint of an attachment links the same two bodies; a large node has three of
            // them, spread around the node, whose anchors average to the node.
            Vector3 host = Vector3.zero;
            Vector3 connected = Vector3.zero;
            int count = 0;
            foreach (ConfigurableJoint j in joint.joints)
            {
                if (j == null || j.connectedBody == null)
                {
                    continue;
                }
                host += j.transform.TransformPoint(j.anchor);
                connected += j.connectedBody.transform.TransformPoint(j.connectedAnchor);
                count++;
            }
            onHost = count > 0 ? host / count : host;
            onConnected = count > 0 ? connected / count : connected;
        }

        /// <summary>How far the anchor of the joint is from the node that joins the two parts, in metres, as
        /// last measured: zero for most parts, not for a robotic part, whose joint the game anchors in the frame
        /// of its moving part.</summary>
        public float AnchorFromNode { get; private set; }

        /// <summary>
        /// Where the node that joins the part to its parent is, in world space: the node of the part that holds
        /// it to the parent. False when the part has none, as two docked ports may not.
        /// </summary>
        private bool Node(out Vector3 point)
        {
            AttachNode node = Child.FindAttachNodeByPart(Parent);
            if (node == null && Child.srfAttachNode != null && Child.srfAttachNode.attachedPart == Parent)
            {
                node = Child.srfAttachNode;
            }
            if (node == null)
            {
                point = Vector3.zero;
                return false;
            }
            point = Child.partTransform.TransformPoint(node.position);
            return true;
        }

        /// <summary>
        /// Measures the attachment as the physics has it now, against <paramref name="rest"/>, the pose its
        /// joint rests in, at the node that joins the two parts (at the anchors of the joint when there is
        /// none). Returns false when the joint can no longer be measured.
        /// </summary>
        public bool Measure(RestPose rest)
        {
            if (Child == null || Parent == null || !Bodies(Joint, out Transform host, out Transform connected))
            {
                return false;
            }
            AttachPoints(Joint, out Vector3 onHost, out Vector3 onConnected);

            // Where the body on the part's side should point, given where the parent's body is.
            Quaternion restWorld = connected.rotation * rest.Relative;
            Quaternion turn = host.rotation * Quaternion.Inverse(restWorld);
            Bend = AngleOf(turn, out Vector3 axisWorld);

            // The game does not always anchor the joint at the node (a robotic part's joint can be metres
            // away): measure at the node, a point of the body on the part's side. Where that point should
            // be follows from the pose that body should have: its anchor on the parent's anchor, turned
            // back by the bend.
            if (!Node(out Vector3 actual))
            {
                actual = onHost;
            }
            AnchorFromNode = (onHost - actual).magnitude;
            Vector3 expectedWorld = onConnected + Quaternion.Inverse(turn) * (actual - onHost);
            Vector3 stretchWorld = actual - expectedWorld;
            Stretch = stretchWorld.magnitude;
            MeasureAgainstSave(actual);

            Quaternion toFrame = Quaternion.Inverse(frame.rotation);
            expected = frame.InverseTransformPoint(expectedWorld);
            stretch = toFrame * stretchWorld;
            restRotation = toFrame * restWorld;
            bendAxis = toFrame * axisWorld;
            return true;
        }

        /// <summary>Sets <see cref="SaveStretch"/> and <see cref="SaveBend"/>, the attachment point being at
        /// <paramref name="actual"/>.</summary>
        private void MeasureAgainstSave(Vector3 actual)
        {
            // The save gives each part its pose in the frame of the root part: from it, where the child
            // should be in the frame of its parent.
            Quaternion parentSaved = Parent.orgRot;
            Quaternion relative = Quaternion.Inverse(parentSaved) * Child.orgRot;
            Vector3 offset = Quaternion.Inverse(parentSaved) * (Child.orgPos - Parent.orgPos);
            Transform child = Child.partTransform;
            Transform parent = Parent.partTransform;
            Quaternion expectedRotation = parent.rotation * relative;
            Vector3 point = Quaternion.Inverse(child.rotation) * (actual - child.position);
            Vector3 expectedPoint = parent.position + parent.rotation * (offset + relative * point);
            SaveStretch = (actual - expectedPoint).magnitude;
            SaveBend = AngleOf(child.rotation * Quaternion.Inverse(expectedRotation), out _);
        }

        /// <summary>The angle <paramref name="turn"/> turns by, in degrees, from 0 to 180, and the axis it turns
        /// about (any unit vector when the angle is zero).</summary>
        private static float AngleOf(Quaternion turn, out Vector3 axis)
        {
            // From the sine of the half angle rather than its cosine: the cosine of a small angle is too
            // close to 1 for a float to tell tenths of a degree apart (what Quaternion.Angle reads).
            double x = turn.x, y = turn.y, z = turn.z, w = turn.w;
            double sine = System.Math.Sqrt(x * x + y * y + z * z);
            if (sine < 1e-12)
            {
                axis = Vector3.up;
                return 0f;
            }
            double sign = w < 0.0 ? -1.0 : 1.0;
            axis = new Vector3((float)(sign * x / sine), (float)(sign * y / sine), (float)(sign * z / sine));
            return (float)(2.0 * System.Math.Atan2(sine, System.Math.Abs(w)) * 180.0 / System.Math.PI);
        }

        /// <summary>
        /// Builds the drawing of what was last measured: the arrow when the stretch is at least
        /// <paramref name="minStretch"/> metres, as long as the stretch times <paramref name="stretchScale"/> and
        /// of the colour <paramref name="colorOf"/> gives the stretch; the axes when the bend is at least
        /// <paramref name="minBend"/> degrees, the coloured ones turned by the bend times
        /// <paramref name="bendScale"/>, up to <see cref="MAX_DRAWN_ANGLE"/>.
        /// </summary>
        public void Build(float minStretch, float minBend, float stretchScale, float bendScale,
            System.Func<float, Color> colorOf)
        {
            VERTICES.Clear();
            COLORS.Clear();
            INDICES.Clear();
            bool showStretch = Stretch > 0f && Stretch >= minStretch;
            bool showBend = Bend > 0f && Bend >= minBend;
            if (showStretch)
            {
                AddArrow(colorOf(Stretch), stretchScale);
            }
            if (showBend)
            {
                Quaternion shown = Quaternion.AngleAxis(Mathf.Min(Bend * bendScale, MAX_DRAWN_ANGLE), bendAxis) * restRotation;
                AddAxes(restRotation, REST_AXIS, REST_AXIS, REST_AXIS);
                AddAxes(shown, AXIS_COLORS[0], AXIS_COLORS[1], AXIS_COLORS[2]);
            }
            drawn = showStretch || showBend;
            mesh.Clear();
            mesh.SetVertices(VERTICES);
            // The colour of each line is its vertices'; the material is white.
            mesh.SetColors(COLORS);
            mesh.SetIndices(INDICES.ToArray(), MeshTopology.Lines, 0);
            mesh.RecalculateBounds();
        }

        private void AddArrow(Color32 c, float scale)
        {
            Vector3 direction = stretch.normalized;
            Vector3 tip = expected + stretch * scale;
            Vector3 side1 = Vector3.Cross(direction, Mathf.Abs(direction.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            Vector3 side2 = Vector3.Cross(direction, side1);

            // A small cross where the attachment point should be, seen even when the arrow is too short to be.
            AddSegment(expected - MARK_SIZE * side1, expected + MARK_SIZE * side1, c);
            AddSegment(expected - MARK_SIZE * side2, expected + MARK_SIZE * side2, c);

            // The shaft, then a head of four strokes, no longer than a quarter of the arrow.
            AddSegment(expected, tip, c);
            float head = Mathf.Min(HEAD_SIZE, 0.25f * stretch.magnitude * scale);
            Vector3 back = tip - head * direction;
            AddSegment(tip, back + 0.5f * head * side1, c);
            AddSegment(tip, back - 0.5f * head * side1, c);
            AddSegment(tip, back + 0.5f * head * side2, c);
            AddSegment(tip, back - 0.5f * head * side2, c);
        }

        private void AddAxes(Quaternion rotation, Color32 x, Color32 y, Color32 z)
        {
            AddSegment(expected, expected + rotation * Vector3.right * AXIS_LENGTH, x);
            AddSegment(expected, expected + rotation * Vector3.up * AXIS_LENGTH, y);
            AddSegment(expected, expected + rotation * Vector3.forward * AXIS_LENGTH, z);
        }

        /// <summary>Adds a line from <paramref name="a"/> to <paramref name="b"/>, both in the frame of the
        /// vessel.</summary>
        private static void AddSegment(Vector3 a, Vector3 b, Color32 color)
        {
            INDICES.Add(VERTICES.Count);
            VERTICES.Add(a);
            COLORS.Add(color);
            INDICES.Add(VERTICES.Count);
            VERTICES.Add(b);
            COLORS.Add(color);
        }

        /// <summary>Whether the last build drew anything: a stretch or a bend at least the minimum.</summary>
        public bool Drawn
        {
            get { return drawn; }
        }

        /// <summary>Where the attachment point should be, as last measured, now in world space: where the
        /// label goes.</summary>
        public Vector3 LabelPoint
        {
            get { return frame.TransformPoint(expected); }
        }

        /// <summary>Queues the drawing for this frame, where the vessel is now.</summary>
        public void Draw(Material lines, int layer)
        {
            if (!drawn || frame == null)
            {
                return;
            }
            Graphics.DrawMesh(mesh, frame.localToWorldMatrix, lines, layer);
        }

        /// <summary>Releases the drawing. The measure cannot be drawn afterwards.</summary>
        public void Destroy()
        {
            Object.Destroy(mesh);
        }
    }

    /// <summary>
    /// The pose a joint rests in, as taken when it was first seen: the rotation of the body on the part's side
    /// in the frame of the body on the parent's side, how far apart its anchors were then, and whether the
    /// vessel was on rails.
    /// </summary>
    internal struct RestPose
    {
        public Quaternion Relative;
        public float Gap;
        public bool OnRails;
    }
}
