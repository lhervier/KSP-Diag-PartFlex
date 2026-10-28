using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace com.github.lhervier.ksp.diag.partflex
{
    /// <summary>
    /// Part flex viewer. In flight, it shows how far the physics stretches and bends the attachment of each
    /// part of the active vessel to its parent, against the pose the joint of that attachment rests in: the
    /// pose the two parts had when the joint was made, on loading the pose of the save. A stretch is drawn as
    /// an arrow from where the attachment point should be to where it is, as long as the stretch times a scale
    /// chosen in the window, its colour going from yellow to red with the stretch; a bend as the axes of the
    /// part at that point, grey where it should point, coloured where it points, the angle between them times
    /// another scale. Below a stretch and a bend chosen in the window, nothing is drawn. The values are written
    /// next to each drawing, and the window lists the attachments drawn, the largest stretch first; it can
    /// freeze them. By default what stands in front of the drawing hides it; the window switches between that,
    /// drawn through everything, and nothing. Nothing is written to the log.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class KSPDiagPartFlex : MonoBehaviour
    {
        private const string LOG_PREFIX = "[KSPDiagPartFlex] ";

        // Unity's own shader for its debug lines: unlike the unlit shaders of the game, it lets a script
        // choose the depth test.
        private const string SHADER_NAME = "Hidden/Internal-Colored";

        // How often the attachments are measured again, in seconds of real time, always right after a physics
        // step. The drawing itself follows the vessel every frame.
        private const float REFRESH_PERIOD = 0.1f;

        // Layer the drawing is queued on: Local Scenery, the layer of the terrain and of the statics, which
        // the flight cameras draw.
        private const int DRAW_LAYER = 15;

        // Drawn after everything else.
        private const int LINE_QUEUE = 4002;

        // Stretch, in metres, at which an arrow is red; yellow at none.
        private const float RED_STRETCH = 0.05f;

        // Gap between the anchors of a joint when its rest pose was taken, in metres, beyond which the window
        // says it was taken late, the physics having already moved the parts.
        private const float LATE_GAP = 0.0005f;

        private static readonly Color SHALLOW = new Color(1f, 1f, 0f, 1f);
        private static readonly Color DEEP = new Color(1f, 0f, 0f, 1f);
        private static readonly Color BEND_ONLY = new Color(0.85f, 0.85f, 0.85f, 1f);

        // The window: its id, distinct from those of the other Diags, its width, and where it first opens, near
        // the top left corner, under the clock.
        private const int WINDOW_ID = 0x47485009;
        private const float WINDOW_WIDTH = 420f;
        private const float WINDOW_X = 20f;
        private const float WINDOW_Y = 40f;

        // Height of the list of attachments in the window, in pixels, beyond which it scrolls: the window fits
        // a screen of 720 pixels.
        private const float LIST_HEIGHT = 150f;

        private static readonly string[] DISPLAY_LABELS =
        {
            "Hidden by what stands in front",
            "Through everything",
            "Off",
        };

        /// <summary>What is drawn, and how.</summary>
        private enum Display
        {
            DepthTested,
            ThroughEverything,
            Nothing,
        }

        // The arrow lengths offered, as times the stretch, and the bends drawn, as times the bend.
        private static readonly int[] STRETCH_SCALES = { 1, 10, 100 };
        private static readonly int[] BEND_SCALES = { 10, 100, 300 };

        // The thresholds offered in the window, in millimetres and degrees; the MCP tools take any.
        private static readonly float[] STRETCH_THRESHOLDS = { 0.1f, 1f, 5f, 10f };
        private static readonly float[] BEND_THRESHOLDS = { 0.05f, 0.1f, 0.5f, 1f };

        // Static, so that the choices survive a scene change or the loading of a save.
        private static Display display = Display.DepthTested;
        private static int stretchScale = 10;
        private static int bendScale = 100;
        private static float minStretchMm = 1f;
        private static float minBendDeg = 0.1f;

        private Material lines;

        // The rest pose of each joint seen in a loaded vessel, by its first internal joint, taken when this
        // mod first saw it.
        private readonly Dictionary<ConfigurableJoint, RestPose> rests = new Dictionary<ConfigurableJoint, RestPose>();

        private Vessel vessel;
        private readonly Dictionary<PartJoint, Flex> flexes = new Dictionary<PartJoint, Flex>();

        // The attachments drawn, in the order of the window's list.
        private readonly List<Flex> listed = new List<Flex>();

        private bool frozen;
        private float nextRefresh;

        // Scratch buffer of a refresh.
        private readonly HashSet<PartJoint> seen = new HashSet<PartJoint>();

        // Static, so that the window stays where it was put from one flight scene to the next.
        private static Rect windowRect = new Rect(WINDOW_X, WINDOW_Y, WINDOW_WIDTH, 0f);
        private Vector2 listScroll;
        private GUIStyle labelStyle;

        // Scratch buffer of the labels: where each one written this frame is, on the screen.
        private readonly List<Rect> labelRects = new List<Rect>();

        private void Awake()
        {
            Shader shader = Shader.Find(SHADER_NAME);
            if (shader == null)
            {
                Debug.LogError(LOG_PREFIX + "shader " + SHADER_NAME + " not found: nothing will be drawn");
                enabled = false;
                return;
            }
            lines = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            // White: the lines carry their colour in their vertices.
            lines.SetColor("_Color", Color.white);
            lines.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            lines.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            lines.SetInt("_Cull", (int)CullMode.Off);
            lines.SetInt("_ZWrite", 0);
            lines.renderQueue = LINE_QUEUE;
            ApplyDepthTest();
        }

        private void OnDestroy()
        {
            Clear();
            if (lines != null)
            {
                Destroy(lines);
            }
        }

        /// <summary>Gives the material the depth test of the current display mode.</summary>
        private void ApplyDepthTest()
        {
            CompareFunction test = display == Display.ThroughEverything
                ? CompareFunction.Always
                : CompareFunction.LessEqual;
            lines.SetInt("_ZTest", (int)test);
        }

        // Mod+F6 shows or hides the window, the same key for every KSP Diag. Static: the choice holds from one
        // flight scene to the next.
        private static readonly KeyBinding WINDOW_KEY = new KeyBinding(KeyCode.F6);
        private static bool windowVisible = true;

        /// <summary>Whether the window shows, as Mod+F6 toggles it; the measures go on either way.</summary>
        internal static bool WindowVisible
        {
            get { return windowVisible; }
            set { windowVisible = value; }
        }

        /// <summary>Where the window is on the screen, and how big.</summary>
        internal Rect WindowRect
        {
            get { return windowRect; }
            set { windowRect = value; }
        }

        private void Start()
        {
            StartCoroutine(MeasureAfterPhysics());
        }

        private void FixedUpdate()
        {
            TakeNewRestPoses();
        }

        private void Update()
        {
            TakeNewRestPoses();
            if (GameSettings.MODIFIER_KEY.GetKey() && WINDOW_KEY.GetKeyDown())
            {
                windowVisible = !windowVisible;
            }
        }

        /// <summary>
        /// Takes the rest pose of every attachment of a loaded vessel not seen yet: the pose of its two bodies,
        /// one in the frame of the other.
        /// </summary>
        private void TakeNewRestPoses()
        {
            // A joint rests in the pose its two bodies had when it was made, which the game keeps nowhere:
            // it is read here as early as possible, before any physics step when this runs before the step
            // that follows the joint's making. On loading, the joints are made on rails, where the physics
            // moves nothing. The gap between the anchors at that moment, zero at rest, tells how late it was.
            List<Vessel> loaded = FlightGlobals.VesselsLoaded;
            for (int v = 0; v < loaded.Count; v++)
            {
                List<Part> parts = loaded[v].parts;
                for (int p = 0; p < parts.Count; p++)
                {
                    PartJoint joint = parts[p].attachJoint;
                    if (!Flex.Bodies(joint, out Transform host, out Transform connected)
                        || rests.ContainsKey(joint.joints[0]))
                    {
                        continue;
                    }
                    Flex.AttachPoints(joint, out Vector3 onHost, out Vector3 onConnected);
                    rests.Add(joint.joints[0], new RestPose
                    {
                        Relative = Quaternion.Inverse(connected.rotation) * host.rotation,
                        Gap = (onHost - onConnected).magnitude,
                        OnRails = loaded[v].packed,
                    });
                }
            }
        }

        /// <summary>Measures the attachments again every <see cref="REFRESH_PERIOD"/>, each time right after a
        /// physics step, where the physics has the parts.</summary>
        private IEnumerator MeasureAfterPhysics()
        {
            WaitForFixedUpdate afterPhysics = new WaitForFixedUpdate();
            while (true)
            {
                yield return afterPhysics;
                if (frozen || Time.unscaledTime < nextRefresh)
                {
                    continue;
                }
                nextRefresh = Time.unscaledTime + REFRESH_PERIOD;
                Refresh();
            }
        }

        private void LateUpdate()
        {
            if (display == Display.Nothing)
            {
                return;
            }
            foreach (Flex flex in listed)
            {
                flex.Draw(lines, DRAW_LAYER);
            }
        }

        /// <summary>
        /// Measures again the attachment of every part of the active vessel whose joint has a rest pose, and
        /// brings the drawing and the window in line with them.
        /// </summary>
        private void Refresh()
        {
            ForgetDestroyedJoints();
            Vessel active = FlightGlobals.ActiveVessel;
            if (active != vessel)
            {
                Clear();
                vessel = active;
            }
            if (vessel == null || !vessel.loaded)
            {
                Clear();
                return;
            }

            seen.Clear();
            foreach (Part part in vessel.parts)
            {
                PartJoint joint = part.attachJoint;
                if (!Flex.Bodies(joint, out _, out _) || !rests.TryGetValue(joint.joints[0], out RestPose rest))
                {
                    continue;
                }
                if (!flexes.TryGetValue(joint, out Flex flex))
                {
                    flex = new Flex(joint, rest, vessel);
                    flexes.Add(joint, flex);
                }
                if (flex.Measure(rest))
                {
                    seen.Add(joint);
                }
            }
            DropUnseen();
            Rebuild();
        }

        /// <summary>Forgets the rest poses of the joints the game has destroyed.</summary>
        private void ForgetDestroyedJoints()
        {
            List<ConfigurableJoint> gone = null;
            foreach (ConfigurableJoint joint in rests.Keys)
            {
                if (joint == null)
                {
                    (gone ?? (gone = new List<ConfigurableJoint>())).Add(joint);
                }
            }
            if (gone != null)
            {
                foreach (ConfigurableJoint joint in gone)
                {
                    rests.Remove(joint);
                }
            }
        }

        /// <summary>Drops the attachments not measured in this refresh.</summary>
        private void DropUnseen()
        {
            List<PartJoint> gone = null;
            foreach (PartJoint joint in flexes.Keys)
            {
                if (!seen.Contains(joint))
                {
                    (gone ?? (gone = new List<PartJoint>())).Add(joint);
                }
            }
            if (gone == null)
            {
                return;
            }
            foreach (PartJoint joint in gone)
            {
                flexes[joint].Destroy();
                flexes.Remove(joint);
            }
        }

        /// <summary>Builds the drawing of every attachment from what was last measured, with the scales and
        /// thresholds chosen now, and makes the window's list hold those drawn, the largest stretch
        /// first.</summary>
        private void Rebuild()
        {
            listed.Clear();
            foreach (Flex flex in flexes.Values)
            {
                flex.Build(minStretchMm / 1000f, minBendDeg, stretchScale, bendScale, ColorOf);
                if (flex.Drawn)
                {
                    listed.Add(flex);
                }
            }
            listed.Sort((a, b) => a.Stretch != b.Stretch ? b.Stretch.CompareTo(a.Stretch) : b.Bend.CompareTo(a.Bend));
        }

        /// <summary>The colour of an arrow, and of its label, for a stretch of <paramref name="stretch"/>
        /// metres.</summary>
        private static Color ColorOf(float stretch)
        {
            return Color.Lerp(SHALLOW, DEEP, stretch / RED_STRETCH);
        }

        /// <summary>The colour of the label of <paramref name="flex"/>: that of its arrow, light grey when only
        /// its bend is drawn.</summary>
        private static Color LabelColorOf(Flex flex)
        {
            return flex.Stretch >= minStretchMm / 1000f ? ColorOf(flex.Stretch) : BEND_ONLY;
        }

        /// <summary>Drops every attachment measured.</summary>
        private void Clear()
        {
            foreach (Flex flex in flexes.Values)
            {
                flex.Destroy();
            }
            flexes.Clear();
            listed.Clear();
        }

        // ---- The window ----

        private void OnGUI()
        {
            GUI.skin = HighLogic.Skin;
            DrawLabels();
            if (!windowVisible)
            {
                return;
            }
            windowRect = GUILayout.Window(WINDOW_ID, windowRect, DrawWindow, "KSP Diag - Part Flex");
        }

        /// <summary>Writes the stretch and the bend of each attachment drawn next to where its attachment point
        /// should be, unless nothing is drawn; a label that would cover another one goes under it.</summary>
        private void DrawLabels()
        {
            if (display == Display.Nothing || listed.Count == 0)
            {
                return;
            }
            Camera camera = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            if (camera == null)
            {
                return;
            }
            if (labelStyle == null)
            {
                labelStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
            }
            labelRects.Clear();
            foreach (Flex flex in listed)
            {
                Vector3 screen = camera.WorldToScreenPoint(flex.LabelPoint);
                if (screen.z <= 0f)
                {
                    continue;
                }
                string text = Millimetres(flex.Stretch) + ", " + Degrees(flex.Bend);
                Vector2 size = labelStyle.CalcSize(new GUIContent(text));
                Rect rect = new Rect(screen.x + 6f, Screen.height - screen.y - 0.5f * size.y, size.x, size.y);
                // Attachments a few centimetres apart, such as two docking ports, would write on each other:
                // move this label down until it covers none written before it.
                bool moved = true;
                while (moved)
                {
                    moved = false;
                    foreach (Rect other in labelRects)
                    {
                        if (rect.Overlaps(other))
                        {
                            rect.y = other.yMax;
                            moved = true;
                        }
                    }
                }
                labelRects.Add(rect);
                // A dark copy under the text, so that it reads on bright ground as on dark.
                labelStyle.normal.textColor = Color.black;
                GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), text, labelStyle);
                labelStyle.normal.textColor = LabelColorOf(flex);
                GUI.Label(rect, text, labelStyle);
            }
        }

        private void DrawWindow(int id)
        {
            GUILayout.BeginVertical();
            GUILayout.Label(VesselLine());
            ChooseDisplay(GUILayout.SelectionGrid((int)display, DISPLAY_LABELS, 1));

            int chosen = Row("Arrow length:", System.Array.IndexOf(STRETCH_SCALES, stretchScale), ScaleLabels(STRETCH_SCALES));
            if (chosen >= 0 && STRETCH_SCALES[chosen] != stretchScale)
            {
                ChooseStretchScale(STRETCH_SCALES[chosen]);
            }
            chosen = Row("Bend drawn:", System.Array.IndexOf(BEND_SCALES, bendScale), ScaleLabels(BEND_SCALES));
            if (chosen >= 0 && BEND_SCALES[chosen] != bendScale)
            {
                ChooseBendScale(BEND_SCALES[chosen]);
            }
            chosen = Row("Stretch from:", System.Array.IndexOf(STRETCH_THRESHOLDS, minStretchMm), ThresholdLabels(STRETCH_THRESHOLDS, " mm"));
            if (chosen >= 0 && STRETCH_THRESHOLDS[chosen] != minStretchMm)
            {
                ChooseThresholds(STRETCH_THRESHOLDS[chosen], minBendDeg);
            }
            chosen = Row("Bend from:", System.Array.IndexOf(BEND_THRESHOLDS, minBendDeg), ThresholdLabels(BEND_THRESHOLDS, "°"));
            if (chosen >= 0 && BEND_THRESHOLDS[chosen] != minBendDeg)
            {
                ChooseThresholds(minStretchMm, BEND_THRESHOLDS[chosen]);
            }

            bool freeze = GUILayout.Toggle(frozen, "Freeze the values");
            if (freeze != frozen)
            {
                Freeze(freeze);
            }
            GUILayout.Label(SummaryLine());

            listScroll = GUILayout.BeginScrollView(listScroll, GUILayout.Height(LIST_HEIGHT));
            foreach (Flex flex in listed)
            {
                GUILayout.Label(Title(flex.Child) + " ← " + Title(flex.Parent) + ": " + Millimetres(flex.Stretch)
                    + ", " + Degrees(flex.Bend) + LateRemark(flex));
            }
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
            GUI.DragWindow();
        }

        /// <summary>A labelled row of choices; returns the index chosen, <paramref name="selected"/> when
        /// nothing was clicked (-1 when the current value is none of them).</summary>
        private static int Row(string label, int selected, string[] labels)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(100f));
            int chosen = GUILayout.SelectionGrid(selected, labels, labels.Length);
            GUILayout.EndHorizontal();
            return chosen;
        }

        private static string[] ScaleLabels(int[] scales)
        {
            string[] labels = new string[scales.Length];
            for (int i = 0; i < scales.Length; i++)
            {
                labels[i] = "x" + scales[i];
            }
            return labels;
        }

        private static string[] ThresholdLabels(float[] thresholds, string unit)
        {
            string[] labels = new string[thresholds.Length];
            for (int i = 0; i < thresholds.Length; i++)
            {
                labels[i] = thresholds[i].ToString("0.##") + unit;
            }
            return labels;
        }

        /// <summary>The line of the window that says which vessel is looked at, and whether the physics moves
        /// it.</summary>
        private string VesselLine()
        {
            if (vessel == null)
            {
                return "No active vessel";
            }
            return vessel.vesselName + ": " + (vessel.packed ? "on rails" : "off rails, physics running");
        }

        /// <summary>The line of the window that sums up what is measured.</summary>
        private string SummaryLine()
        {
            float stretch = 0f;
            float bend = 0f;
            foreach (Flex flex in flexes.Values)
            {
                stretch = Mathf.Max(stretch, flex.Stretch);
                bend = Mathf.Max(bend, flex.Bend);
            }
            return flexes.Count + " attachments measured, " + listed.Count + " drawn; largest stretch "
                + Millimetres(stretch) + ", largest bend " + Degrees(bend) + (frozen ? " (frozen)" : "");
        }

        /// <summary>What the list of the window says after an attachment whose rest pose was taken after the
        /// physics had already moved its parts.</summary>
        private static string LateRemark(Flex flex)
        {
            return flex.CaptureGap > LATE_GAP ? " (rest taken late, " + Millimetres(flex.CaptureGap) + " off)" : "";
        }

        private static string Title(Part part)
        {
            return part != null ? part.partInfo.title : "?";
        }

        private static string Millimetres(float metres)
        {
            return (metres * 1000f).ToString("F1") + " mm";
        }

        private static string Degrees(float degrees)
        {
            return degrees.ToString("F2") + "°";
        }

        // ---- What the MCP tools do ----

        /// <summary>
        /// What the window shows, ready to be written as JSON: the active vessel and whether it is on rails, the
        /// display mode, the scales, the thresholds, whether the values are frozen, how many attachments are
        /// measured, and the attachments drawn (every one measured when <paramref name="all"/>), largest
        /// stretch first, each with its part and parent, the stretch and the bend, the same against the poses
        /// of the save, and how and when the rest pose of its joint was taken.
        /// </summary>
        internal Dictionary<string, object> Read(bool all)
        {
            List<Flex> chosen = listed;
            if (all)
            {
                chosen = new List<Flex>(flexes.Values);
                chosen.Sort((a, b) => a.Stretch != b.Stretch ? b.Stretch.CompareTo(a.Stretch) : b.Bend.CompareTo(a.Bend));
            }
            List<object> attachments = new List<object>();
            foreach (Flex flex in chosen)
            {
                if (flex.Child == null || flex.Parent == null)
                {
                    continue;
                }
                attachments.Add(new Dictionary<string, object>
                {
                    { "part", flex.Child.partInfo.title },
                    { "partName", flex.Child.partInfo.name },
                    { "partId", (double)flex.Child.flightID },
                    { "parent", flex.Parent.partInfo.title },
                    { "parentId", (double)flex.Parent.flightID },
                    { "drawn", flex.Drawn },
                    { "stretchMm", System.Math.Round(flex.Stretch * 1000.0, 2) },
                    { "bendDeg", System.Math.Round((double)flex.Bend, 3) },
                    { "saveStretchMm", System.Math.Round(flex.SaveStretch * 1000.0, 2) },
                    { "saveBendDeg", System.Math.Round((double)flex.SaveBend, 3) },
                    { "restGapMm", System.Math.Round(flex.CaptureGap * 1000.0, 2) },
                    { "anchorFromNodeMm", System.Math.Round(flex.AnchorFromNode * 1000.0, 1) },
                    { "restOnRails", flex.CapturedOnRails }
                });
            }
            return new Dictionary<string, object>
            {
                { "vessel", vessel != null ? vessel.vesselName : "" },
                { "onRails", vessel != null && vessel.packed },
                { "display", DISPLAY_LABELS[(int)display] },
                { "arrowScale", stretchScale },
                { "bendScale", bendScale },
                { "minStretchMm", (double)minStretchMm },
                { "minBendDeg", (double)minBendDeg },
                { "frozen", frozen },
                { "measured", flexes.Count },
                { "attachments", attachments }
            };
        }

        /// <summary>
        /// Chooses a display mode, as a click on its label in the window does: its index in the window, from
        /// the top. Returns the label of the mode now chosen.
        /// </summary>
        internal string ChooseDisplay(int index)
        {
            if (index < 0 || index >= DISPLAY_LABELS.Length)
            {
                throw new System.ArgumentOutOfRangeException(nameof(index), "0 to " + (DISPLAY_LABELS.Length - 1));
            }
            Display chosen = (Display)index;
            if (chosen != display)
            {
                display = chosen;
                ApplyDepthTest();
            }
            return DISPLAY_LABELS[index];
        }

        /// <summary>
        /// Chooses how long the arrows are, as times the stretch, as a click on its label in the window does:
        /// one of 1, 10 and 100. Returns the scale now chosen.
        /// </summary>
        internal int ChooseStretchScale(int scale)
        {
            if (System.Array.IndexOf(STRETCH_SCALES, scale) < 0)
            {
                throw new System.ArgumentOutOfRangeException(nameof(scale), "1, 10 or 100");
            }
            stretchScale = scale;
            Rebuild();
            return stretchScale;
        }

        /// <summary>
        /// Chooses how far the coloured axes are drawn turned, as times the bend, as a click on its label in the
        /// window does: one of 10, 100 and 300. Returns the scale now chosen.
        /// </summary>
        internal int ChooseBendScale(int scale)
        {
            if (System.Array.IndexOf(BEND_SCALES, scale) < 0)
            {
                throw new System.ArgumentOutOfRangeException(nameof(scale), "10, 100 or 300");
            }
            bendScale = scale;
            Rebuild();
            return bendScale;
        }

        /// <summary>
        /// Chooses the stretch, in millimetres, and the bend, in degrees, from which an attachment is drawn and
        /// listed: any value from zero, those of the window included.
        /// </summary>
        internal void ChooseThresholds(float stretchMm, float bendDeg)
        {
            if (stretchMm < 0f || float.IsNaN(stretchMm))
            {
                throw new System.ArgumentOutOfRangeException(nameof(stretchMm), "0 or more");
            }
            if (bendDeg < 0f || float.IsNaN(bendDeg))
            {
                throw new System.ArgumentOutOfRangeException(nameof(bendDeg), "0 or more");
            }
            minStretchMm = stretchMm;
            minBendDeg = bendDeg;
            Rebuild();
        }

        /// <summary>The stretch, in millimetres, and the bend, in degrees, from which an attachment is drawn and
        /// listed.</summary>
        internal static float MinStretchMm
        {
            get { return minStretchMm; }
        }

        internal static float MinBendDeg
        {
            get { return minBendDeg; }
        }

        /// <summary>
        /// Freezes the values, as ticking the box of the window does, or lets them follow the physics again.
        /// Frozen, nothing is measured, and the drawing stays as last measured, following the vessel.
        /// </summary>
        internal void Freeze(bool freeze)
        {
            frozen = freeze;
            if (!frozen)
            {
                nextRefresh = 0f;
            }
        }

        /// <summary>Whether the values are frozen.</summary>
        internal bool Frozen
        {
            get { return frozen; }
        }
    }
}
