using System;
using System.Collections.Generic;
using UnityEngine;
using com.github.lhervier.ksp.util.mcpserver;

namespace com.github.lhervier.ksp.diag.partflex
{
    /// <summary>
    /// What KSP-Util-MCPServer, when it is installed, offers of this mod as tools: what its window shows, each
    /// of its choices, and the moving of the window. Each tool works on the mod loaded in the flight scene, but
    /// the showing of the window. Nothing here is needed to play by hand, and this mod runs the same without
    /// KSP-Util-MCPServer: only that server reads the attribute.
    /// </summary>
    internal static class McpTools
    {
        [McpTool("partflex_read",
            "Reads what the window of KSP Diag - Part Flex shows, without writing anything: the active vessel " +
                "(vessel, empty when there is none) and whether it is on rails (onRails), the display mode, the " +
                "arrow scale (arrowScale, the arrows are that many times the stretch), the bend scale (bendScale, " +
                "the coloured axes are turned by that many times the bend), the stretch in mm and the bend in " +
                "degrees from which an attachment is drawn (minStretchMm, minBendDeg), whether the values are " +
                "frozen, how many attachments are measured (measured), and the attachments drawn, or every one " +
                "measured when all is true, largest stretch first (attachments): the title, part name and flight " +
                "id of the part (part, partName, partId) and the title and flight id of its parent (parent, " +
                "parentId), whether it is drawn, how far the attachment point is from where the joint holds it " +
                "at rest in mm (stretchMm) and the angle the part is turned by from its rest pose in degrees " +
                "(bendDeg), the same against the poses of the save (saveStretchMm, saveBendDeg), how far apart " +
                "the anchors of the joint were when its rest pose was taken in mm (restGapMm, 0 when taken " +
                "before the physics moved anything) and whether the vessel was on rails then (restOnRails), and " +
                "how far the anchor of the joint is from the node that joins the two parts in mm " +
                "(anchorFromNodeMm: the stretch is measured at the node, at the anchor when there is none).")]
        internal static object Read(bool all = false)
        {
            return Mod().Read(all);
        }

        [McpTool("partflex_set_display",
            "Chooses how KSP Diag - Part Flex draws the arrows, the axes and their labels, as clicking one of the " +
            "three modes of its window does: 0 Hidden by what stands in front (the default), 1 Through " +
            "everything, 2 Off. What it measures goes on in every mode. Returns the mode now chosen (display).")]
        internal static object SetDisplay(double mode)
        {
            return new Dictionary<string, object> { { "display", Mod().ChooseDisplay((int)mode) } };
        }

        [McpTool("partflex_set_arrow_scale",
            "Chooses how long the arrows of KSP Diag - Part Flex are, as clicking x1, x10 or x100 in its window " +
            "does: scale 1, 10 or 100, the arrows being that many times the stretch. Returns the scale now " +
            "chosen (arrowScale).")]
        internal static object SetArrowScale(double scale)
        {
            return new Dictionary<string, object> { { "arrowScale", Mod().ChooseStretchScale((int)Math.Round(scale)) } };
        }

        [McpTool("partflex_set_bend_scale",
            "Chooses how far KSP Diag - Part Flex draws the coloured axes of a bent part turned from the grey ones, " +
            "as clicking x10, x100 or x300 in its window does: scale 10, 100 or 300, the angle drawn being that many " +
            "times the bend, 90 degrees at most. Returns the scale now chosen (bendScale).")]
        internal static object SetBendScale(double scale)
        {
            return new Dictionary<string, object> { { "bendScale", Mod().ChooseBendScale((int)Math.Round(scale)) } };
        }

        [McpTool("partflex_set_thresholds",
            "Chooses from which stretch, in mm, and which bend, in degrees, KSP Diag - Part Flex draws and lists an " +
            "attachment, as the rows Stretch from and Bend from of its window do, but taking any value from 0. " +
            "An attachment is drawn when either reaches its threshold; its arrow only when the stretch does, its " +
            "axes only when the bend does. Returns the thresholds now chosen (minStretchMm, minBendDeg).")]
        internal static object SetThresholds(double stretchMm, double bendDeg)
        {
            Mod().ChooseThresholds((float)stretchMm, (float)bendDeg);
            return new Dictionary<string, object>
            {
                { "minStretchMm", (double)KSPDiagPartFlex.MinStretchMm },
                { "minBendDeg", (double)KSPDiagPartFlex.MinBendDeg }
            };
        }

        [McpTool("partflex_freeze",
            "Freezes the values of KSP Diag - Part Flex, as ticking the box Freeze the values of its window does, " +
            "or lets them follow the physics again. Frozen, nothing is measured and the drawing stays as last " +
            "measured, following the vessel; the scales and thresholds still apply to it. Returns whether the " +
            "values are frozen (frozen).")]
        internal static object Freeze(bool frozen)
        {
            KSPDiagPartFlex mod = Mod();
            mod.Freeze(frozen);
            return new Dictionary<string, object> { { "frozen", mod.Frozen } };
        }

        [McpTool("partflex_move_window",
            "Moves the window of KSP Diag - Part Flex, as dragging it does: x and y in pixels from the top left " +
            "corner of the screen. Returns its position and size (x, y, width, height).")]
        internal static object MoveWindow(double x, double y)
        {
            KSPDiagPartFlex mod = Mod();
            Rect rect = mod.WindowRect;
            rect.x = (float)x;
            rect.y = (float)y;
            mod.WindowRect = rect;
            return new Dictionary<string, object>
            {
                { "x", (double)rect.x },
                { "y", (double)rect.y },
                { "width", (double)rect.width },
                { "height", (double)rect.height }
            };
        }

        [McpTool("partflex_show_window",
            "Shows or hides the window of KSP Diag - Part Flex, as Mod+F6 does; what it measures goes on either " +
            "way. Returns whether it shows (visible).")]
        internal static object ShowWindow(bool visible)
        {
            KSPDiagPartFlex.WindowVisible = visible;
            return new Dictionary<string, object> { { "visible", KSPDiagPartFlex.WindowVisible } };
        }

        private static KSPDiagPartFlex Mod()
        {
            KSPDiagPartFlex mod = UnityEngine.Object.FindObjectOfType<KSPDiagPartFlex>();
            if (mod == null)
            {
                throw new InvalidOperationException("KSP Diag - Part Flex only runs in flight");
            }
            return mod;
        }
    }
}
