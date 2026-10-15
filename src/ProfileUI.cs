using System;
using System.Collections.Generic;
using System.Globalization;
using KSP.UI.Screens;
using ToolbarControl_NS;
using ClickThroughFix;
using UnityEngine;

namespace GridFinStepDeploy
{
    /// <summary>Global deploy profile override for all ModuleGridFinStepDeploy parts. Saved in PluginData; null = cfg defaults.</summary>
    public class Profile
    {
        public class StepDef { public float target, move, hold; }
        public List<StepDef> steps = new List<StepDef>();
        public string finalApproach = "smooth", lockMode = "impact", retractMode = "smooth";
        public float impactSpeedFactor = 1.5f, lockRebound = 0.12f, lockMaxRebound = 1f, lockDamping = 0.25f, lockFrequency = 5.5f,
                     lockSettle = 1.1f, shiverAmplitude = 0.12f, shiverFrequency = 15f, shiverDecay = 0.1f, retractDuration = 6f;

        public static Profile Saved;
        static string FilePath { get { return KSPUtil.ApplicationRootPath + "GameData/GridFinStepDeploy/PluginData/GridFinStepDeploy_Profile.cfg"; } }
        static string F(float v) { return v.ToString("R", CultureInfo.InvariantCulture); }

        public Profile Clone() { var n = new ConfigNode(); Save(n); return Load(n); }

        public void Save(ConfigNode n)
        {
            n.AddValue("finalApproach", finalApproach); n.AddValue("lockMode", lockMode); n.AddValue("retractMode", retractMode);
            n.AddValue("impactSpeedFactor", F(impactSpeedFactor)); n.AddValue("lockRebound", F(lockRebound)); n.AddValue("lockMaxRebound", F(lockMaxRebound));
            n.AddValue("lockDamping", F(lockDamping)); n.AddValue("lockFrequency", F(lockFrequency)); n.AddValue("lockSettle", F(lockSettle));
            n.AddValue("shiverAmplitude", F(shiverAmplitude)); n.AddValue("shiverFrequency", F(shiverFrequency)); n.AddValue("shiverDecay", F(shiverDecay));
            n.AddValue("retractDuration", F(retractDuration));
            foreach (var st in steps) { var sn = n.AddNode("STEP"); sn.AddValue("target", F(st.target)); sn.AddValue("move", F(st.move)); sn.AddValue("hold", F(st.hold)); }
        }
        public static Profile Load(ConfigNode n)
        {
            var p = new Profile();
            n.TryGetValue("finalApproach", ref p.finalApproach); n.TryGetValue("lockMode", ref p.lockMode); n.TryGetValue("retractMode", ref p.retractMode);
            n.TryGetValue("impactSpeedFactor", ref p.impactSpeedFactor); n.TryGetValue("lockRebound", ref p.lockRebound); n.TryGetValue("lockMaxRebound", ref p.lockMaxRebound);
            n.TryGetValue("lockDamping", ref p.lockDamping); n.TryGetValue("lockFrequency", ref p.lockFrequency); n.TryGetValue("lockSettle", ref p.lockSettle);
            n.TryGetValue("shiverAmplitude", ref p.shiverAmplitude); n.TryGetValue("shiverFrequency", ref p.shiverFrequency); n.TryGetValue("shiverDecay", ref p.shiverDecay);
            n.TryGetValue("retractDuration", ref p.retractDuration);
            foreach (var sn in n.GetNodes("STEP"))
            {
                var st = new StepDef(); sn.TryGetValue("target", ref st.target); sn.TryGetValue("move", ref st.move); sn.TryGetValue("hold", ref st.hold); p.steps.Add(st);
            }
            return p;
        }
        public static void LoadSaved()
        {
            try
            {
                if (!System.IO.File.Exists(FilePath)) { Saved = null; return; }
                var root = ConfigNode.Load(FilePath); var n = root != null ? root.GetNode("GRIDFIN_PROFILE") : null;
                Saved = n != null ? Load(n) : null;
                if (Saved != null && Saved.steps.Count == 0) Saved = null;
            }
            catch (Exception e) { Debug.LogWarning("[GridFinStepDeploy] profile load: " + e.Message); Saved = null; }
        }
        public static void WriteSaved()
        {
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath));
                var root = new ConfigNode(); if (Saved != null) Saved.Save(root.AddNode("GRIDFIN_PROFILE"));
                root.Save(FilePath);
            }
            catch (Exception e) { Debug.LogWarning("[GridFinStepDeploy] profile save: " + e.Message); }
        }
        public static void DeleteSaved() { Saved = null; try { if (System.IO.File.Exists(FilePath)) System.IO.File.Delete(FilePath); } catch { } }

        /// the cfg (CRS-35) defaults: read from the first part prefab that carries the module (prefab is never overridden)
        public static Profile CfgDefaults()
        {
            foreach (var ap in PartLoader.LoadedPartsList)
            {
                if (ap.partPrefab == null) continue;
                var m = ap.partPrefab.FindModuleImplementing<ModuleGridFinStepDeploy>();
                if (m == null) continue;
                foreach (var mn in ap.partConfig.GetNodes("MODULE"))
                    if (mn.GetValue("name") == "ModuleGridFinStepDeploy") { var p = Load(mn); if (p.steps.Count > 0) return p; }
            }
            var d = new Profile();
            d.steps.Add(new StepDef { target = 45, move = 3, hold = 3 }); d.steps.Add(new StepDef { target = 75, move = 2.5f, hold = 3.5f }); d.steps.Add(new StepDef { target = 90, move = 2.5f, hold = 0 });
            return d;
        }
    }

    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public class GFRegister : MonoBehaviour
    {
        void Start() { ToolbarControl.RegisterMod(ProfileWindow.MODID, ProfileWindow.MODNAME); Profile.LoadSaved(); }
    }

    [KSPAddon(KSPAddon.Startup.AllGameScenes, false)]
    public class ProfileWindow : MonoBehaviour
    {
        public const string MODID = "GridFinStepDeploy_NS", MODNAME = "Grid Fin Step Deploy";
        ToolbarControl toolbar; bool show; Rect win = new Rect(260, 90, 420, 10);
        Profile edit; Vector2 scroll; string msg = "";

        void Start()
        {
            if (!HighLogic.LoadedSceneIsFlight && !HighLogic.LoadedSceneIsEditor) { Destroy(this); return; }
            toolbar = gameObject.AddComponent<ToolbarControl>();
            toolbar.AddToAllToolbars(() => show = true, () => show = false,
                ApplicationLauncher.AppScenes.FLIGHT | ApplicationLauncher.AppScenes.VAB | ApplicationLauncher.AppScenes.SPH,
                MODID, "GridFinStepDeployButton", "GridFinStepDeploy/Icons/gridfin_38", "GridFinStepDeploy/Icons/gridfin_24", MODNAME);
        }
        void OnDestroy() { if (toolbar != null) { toolbar.OnDestroy(); Destroy(toolbar); } }

        IEnumerable<ModuleGridFinStepDeploy> Fins(bool activeOnly)
        {
            if (HighLogic.LoadedSceneIsEditor)
            {
                if (EditorLogic.fetch != null && EditorLogic.fetch.ship != null)
                    foreach (var p in EditorLogic.fetch.ship.parts) { var m = p.FindModuleImplementing<ModuleGridFinStepDeploy>(); if (m != null) yield return m; }
                yield break;
            }
            var vs = activeOnly ? new List<Vessel> { FlightGlobals.ActiveVessel } : FlightGlobals.VesselsLoaded;
            foreach (var v in vs)
                if (v != null) foreach (var p in v.parts) { var m = p.FindModuleImplementing<ModuleGridFinStepDeploy>(); if (m != null) yield return m; }
        }

        void OnGUI()
        {
            if (!show) return;
            if (edit == null) edit = (Profile.Saved ?? Profile.CfgDefaults()).Clone();
            win = ClickThruBlocker.GUILayoutWindow(0x6F1D5, win, Draw, "Grid fin deploy profile");
        }

        static float Num(string label, float v, float min, float max, string unit)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(120));
            v = GUILayout.HorizontalSlider(v, min, max, GUILayout.Width(170));
            GUILayout.Label(v.ToString("0.##") + unit, GUILayout.Width(70));
            GUILayout.EndHorizontal();
            return (float)Math.Round(v, 2);
        }
        static string Choice(string label, string v, params string[] opts)
        {
            GUILayout.BeginHorizontal(); GUILayout.Label(label, GUILayout.Width(120));
            foreach (var o in opts) if (GUILayout.Toggle(v == o, o, GUI.skin.button)) v = o;
            GUILayout.EndHorizontal(); return v;
        }

        void Draw(int id)
        {
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(470));
            GUILayout.Label(Profile.Saved != null ? "Using saved profile (PluginData)" : "Using cfg defaults (CRS-35)");
            for (int i = 0; i < edit.steps.Count; i++)
            {
                var st = edit.steps[i];
                GUILayout.BeginHorizontal(); GUILayout.Label("Step " + (i + 1), GUILayout.Width(60));
                if (edit.steps.Count > 1 && GUILayout.Button("Remove", GUILayout.Width(70))) { edit.steps.RemoveAt(i); GUILayout.EndHorizontal(); break; }
                GUILayout.EndHorizontal();
                st.target = Num("  target angle", st.target, 0, 90, "°");
                st.move = Num("  move time", st.move, 0.1f, 10, " s");
                st.hold = Num("  hold time", st.hold, 0, 10, " s");
            }
            if (GUILayout.Button("Add step")) { var last = edit.steps.Count > 0 ? edit.steps[edit.steps.Count - 1] : null; edit.steps.Add(new Profile.StepDef { target = 90, move = 1, hold = last != null ? 1 : 0 }); }
            GUILayout.Space(6);
            edit.finalApproach = Choice("Final approach", edit.finalApproach, "smooth", "accelerate");
            edit.lockMode = Choice("Lock mode", edit.lockMode, "impact", "bounce", "none");
            edit.impactSpeedFactor = Num("Impact speed ×", edit.impactSpeedFactor, 0, 3, "");
            edit.lockRebound = Num("Rebound", edit.lockRebound, 0, 0.4f, " °/(°/s)");
            edit.lockMaxRebound = Num("Max rebound", edit.lockMaxRebound, 0, 3, "°");
            edit.lockDamping = Num("Damping ratio", edit.lockDamping, 0.02f, 0.9f, "");
            edit.lockFrequency = Num("Frequency", edit.lockFrequency, 1, 15, " Hz");
            edit.lockSettle = Num("Settle window", edit.lockSettle, 0.2f, 3, " s");
            edit.shiverAmplitude = Num("Shiver", edit.shiverAmplitude, 0, 0.5f, "°");
            edit.shiverFrequency = Num("Shiver freq", edit.shiverFrequency, 5, 30, " Hz");
            edit.retractMode = Choice("Retract mode", edit.retractMode, "smooth", "reverseSteps");
            edit.retractDuration = Num("Retract time", edit.retractDuration, 0.5f, 20, " s");
            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Apply + save")) { edit.steps.Sort((a, b) => a.target.CompareTo(b.target)); Profile.Saved = edit.Clone(); Profile.WriteSaved(); int n = 0; foreach (var m in Fins(false)) { m.ApplyProfile(Profile.Saved, true); n++; } msg = "Applied to " + n + " fin(s), saved"; }
            if (GUILayout.Button("Reset to CRS-35 defaults")) { Profile.DeleteSaved(); edit = Profile.CfgDefaults().Clone(); int n = 0; foreach (var m in Fins(false)) { m.ApplyProfile(edit, true); n++; } msg = "Defaults restored (" + n + " fin(s))"; }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Preview deploy")) { int n = 0; foreach (var m in Fins(true)) { m.ApplyProfile(edit, false); m.Preview(true); n++; } msg = n + " fin(s) deploying (unsaved edits previewed)"; }
            if (GUILayout.Button("Preview retract")) { int n = 0; foreach (var m in Fins(true)) { m.ApplyProfile(edit, false); m.Preview(false); n++; } msg = n + " fin(s) retracting"; }
            GUILayout.EndHorizontal();
            if (msg.Length > 0) GUILayout.Label(msg);
            if (GUILayout.Button("Close")) { show = false; if (toolbar != null) toolbar.SetFalse(false); }
            GUI.DragWindow();
        }
    }
}
