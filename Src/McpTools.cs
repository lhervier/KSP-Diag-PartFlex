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
            "Reads what the window of KSP Diag - Part Flex shows: vessel, onRails, display, scales, thresholds, " +
            "frozen, and the attachments drawn (all: every one measured), largest stretch first: part and parent, " +
            "stretchMm and bendDeg from the joint's rest, the same against the save, restGapMm, anchorFromNodeMm.")]
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
            "Sets the stretch (mm) and bend (degrees) from which KSP Diag - Part Flex draws an attachment, as its " +
            "Stretch from and Bend from rows do, any value from 0. Returns minStretchMm and minBendDeg.")]
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
            "Freezes the values of KSP Diag - Part Flex, as its Freeze the values box does, or lets them follow " +
            "the physics again; frozen, the drawing stays as last measured. Returns frozen.")]
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
