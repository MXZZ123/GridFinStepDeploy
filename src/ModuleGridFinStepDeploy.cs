using System;
using System.Collections.Generic;
using UnityEngine;

[assembly: KSPAssembly("GridFinStepDeploy", 0, 2)]

namespace GridFinStepDeploy
{
    /// <summary>
    /// Drives a part's deploy animation through a data-driven stepped profile (move, hold, move, hold, ... lock bounce),
    /// replacing ModuleAnimateGeneric in place (same module index, so ModuleDeployableAero's DeployModuleIndex still finds it
    /// through IScalarModule). Targets are hinge ANGLES in degrees when linearizeTransform is set: the baked clip is sampled at
    /// load and inverted, so "45 deg" means the fin really sits at 45 deg regardless of the clip's own easing.
    /// cfg:
    ///   MODULE { name = ModuleGridFinStepDeploy  animationName = NewFinsDeploy  linearizeTransform = Fin  defaultActionGroup = Brakes
    ///            STEP { target = 45  move = 1  hold = 3 }  ...  lockOvershoot = 2.5  lockSettle = 0.3  lockBounces = 2
    ///            retractMode = smooth  retractDuration = 6  moveEase = smooth }
    /// </summary>
    public class ModuleGridFinStepDeploy : PartModule, IScalarModule
    {
        [KSPField] public string animationName = "NewFinsDeploy";
        [KSPField] public string linearizeTransform = "";   // transform whose rotation defines the hinge angle; "" = targets are clip time fractions
        [KSPField] public bool targetsInDegrees = true;     // with linearizeTransform: STEP target in degrees (else fraction 0..1 of full angle)
        [KSPField] public string moveEase = "smooth";      // smooth | linear (per step, in angle space)
        [KSPField] public float lockOvershoot = 2f;         // deg past the final angle at the hard stop
        [KSPField] public float lockSettle = 1.1f;          // s for the damped bounce to settle
        [KSPField] public float lockBounces = 3.5f;   // decaying bounces during settle
        [KSPField] public string finalApproach = "smooth"; // smooth | accelerate (old: speed into the hard stop)
        [KSPField] public string lockMode = "impact";      // impact (hinge hits stop, rebounds below it) | bounce (old) | none
        [KSPField] public float impactSpeedFactor = 1.5f;  // impact mode: arrival speed at the stop as a multiple of the last move's mean speed
        [KSPField] public float lockRebound = 0.12f;       // deg of rebound per deg/s of impact speed
        [KSPField] public float lockMaxRebound = 1.0f;     // deg cap
        [KSPField] public float lockDamping = 0.25f;       // damping ratio
        [KSPField] public float lockFrequency = 5.5f;      // Hz natural frequency
        [KSPField] public float shiverAmplitude = 0.12f;   // deg, high-freq structural shiver
        [KSPField] public float shiverFrequency = 15f;     // Hz
        [KSPField] public float shiverDecay = 0.1f;        // s time constant (~gone in 0.4 s)
        [KSPField] public float lockDecay = 3.5f;      // exp decay rate of the bounce over the settle time
        [KSPField] public string retractMode = "smooth";   // smooth | reverseSteps
        [KSPField] public float retractDuration = 6f;
        [KSPField] public string defaultActionGroup = "None";
        [KSPField] public string moduleID = "gridFinDeploy";

        [KSPField(isPersistant = true)] public float position = 0f;   // current angle fraction 0..1 (0 stowed, 1 deployed)
        [KSPField(isPersistant = true)] public int direction = 0;     // +1 deploying, -1 retracting, 0 idle
        [KSPField(isPersistant = true)] public int stepIndex = 0;
        [KSPField(isPersistant = true)] public int phase = 0;         // 0 move, 1 hold, 2 lock bounce
        [KSPField(isPersistant = true)] public float phaseTime = 0f;
        [KSPField(isPersistant = true)] public float phaseStart = 0f; // position at start of the current move
        [KSPField(isPersistant = true)] public bool agInitialised = false;

        [KSPField(guiActive = true, guiActiveEditor = true, guiName = "Grid fins")] public string statusText = "Stowed";

        struct Step { public float target, move, hold; }
        bool previewing;
        readonly List<Step> steps = new List<Step>();
        Animation anim; AnimationState state; Transform hinge;
        float fullAngle = 90f; Quaternion hingeRest = Quaternion.identity; Vector3 hingeAxisParent = Vector3.right;
        float[] lutAngleFrac; const int LUT = 1024; // angle fraction at clip time i/(LUT-1)
        float overshootDeg = 0f;
        EventData<float, float> onMoving; EventData<float> onStop;

        // ---------- config ----------
        public override void OnLoad(ConfigNode node)
        {
            base.OnLoad(node);
            var stepNodes = node.GetNodes("STEP");
            if (stepNodes.Length > 0) { steps.Clear(); foreach (var n in stepNodes) steps.Add(ParseStep(n)); }
            if (steps.Count == 0) LoadStepsFromPrefab();
            if (Profile.Saved != null) ApplyProfile(Profile.Saved, false);
        }
        static Step ParseStep(ConfigNode n)
        {
            var s = new Step(); n.TryGetValue("target", ref s.target); n.TryGetValue("move", ref s.move); n.TryGetValue("hold", ref s.hold); return s;
        }
        void LoadStepsFromPrefab()
        {
            // craft/save MODULE nodes don't carry STEP nodes; read them from the part's config
            if (part == null || part.partInfo == null || part.partInfo.partConfig == null) return;
            foreach (var m in part.partInfo.partConfig.GetNodes("MODULE"))
            {
                if (m.GetValue("name") != moduleName) continue;
                foreach (var n in m.GetNodes("STEP")) steps.Add(ParseStep(n));
                break;
            }
        }

        public override void OnAwake()
        {
            base.OnAwake();
            onMoving = new EventData<float, float>("GridFinStepDeploy.OnMoving");
            onStop = new EventData<float>("GridFinStepDeploy.OnStop");
        }

        public override void OnStart(StartState st)
        {
            if (steps.Count == 0) LoadStepsFromPrefab();
            if (Profile.Saved != null) ApplyProfile(Profile.Saved, false);
            if (steps.Count == 0) steps.Add(new Step { target = targetsInDegrees && !string.IsNullOrEmpty(linearizeTransform) ? 90 : 1, move = 3, hold = 0 });
            var anims = part.FindModelAnimators(animationName);
            if (anims != null && anims.Length > 0) { anim = anims[0]; state = anim[animationName]; }
            if (state == null) { Debug.LogWarning("[GridFinStepDeploy] animation '" + animationName + "' not found on " + part.partInfo.name); return; }
            state.enabled = true; state.weight = 1f; state.speed = 0f; state.wrapMode = WrapMode.ClampForever;
            BuildAngleTable();
            if (!agInitialised && (HighLogic.LoadedSceneIsEditor || HighLogic.LoadedSceneIsFlight))
            {
                KSPActionGroup g;
                try { g = (KSPActionGroup)Enum.Parse(typeof(KSPActionGroup), defaultActionGroup, true); } catch { g = KSPActionGroup.None; }
                if (g != KSPActionGroup.None) { Actions["ToggleAction"].actionGroup |= g; Actions["ToggleAction"].defaultActionGroup = g; }
                agInitialised = true;
            }
            Apply(position);
            UpdateUI();
        }

        /// <summary>Sample the clip to map clip time -> hinge angle, so targets can be real angles.</summary>
        void BuildAngleTable()
        {
            lutAngleFrac = null;
            if (string.IsNullOrEmpty(linearizeTransform)) return;
            hinge = part.FindModelTransform(linearizeTransform);
            if (hinge == null) { Debug.LogWarning("[GridFinStepDeploy] transform '" + linearizeTransform + "' not found; using clip time"); return; }
            var ang = new float[LUT];
            SampleAt(0f); Quaternion q0 = hinge.localRotation; hingeRest = q0;
            SampleAt(1f); Quaternion q1 = hinge.localRotation;
            float a; Vector3 ax; (q1 * Quaternion.Inverse(q0)).ToAngleAxis(out a, out ax);
            if (a > 180f) { a = 360f - a; ax = -ax; }
            fullAngle = Mathf.Max(1f, a); hingeAxisParent = ax;
            for (int i = 0; i < LUT; i++) { SampleAt(i / (float)(LUT - 1)); ang[i] = Quaternion.Angle(q0, hinge.localRotation) / fullAngle; }
            for (int i = 1; i < LUT; i++) ang[i] = Mathf.Max(ang[i], ang[i - 1]); // monotone
            lutAngleFrac = ang;
            Debug.Log("[GridFinStepDeploy] " + part.partInfo.name + ": clip " + state.length.ToString("F2") + " s, hinge " + fullAngle.ToString("F1") + " deg, steps " + steps.Count);
        }
        void SampleAt(float t) { state.normalizedTime = t; state.enabled = true; anim.Sample(); }

        float ClipTimeForAngleFrac(float f)
        {
            f = Mathf.Clamp01(f);
            if (lutAngleFrac == null) return f;
            for (int i = 1; i < LUT; i++)
                if (lutAngleFrac[i] >= f)
                {
                    float a0 = lutAngleFrac[i - 1], a1 = lutAngleFrac[i];
                    float u = a1 > a0 ? (f - a0) / (a1 - a0) : 0f;
                    return (i - 1 + u) / (LUT - 1);
                }
            return 1f;
        }
        float StepTarget(int i)
        {
            float t = steps[i].target;
            if (lutAngleFrac != null && targetsInDegrees) t /= fullAngle;
            return Mathf.Clamp01(t);
        }

        void Apply(float angleFrac)
        {
            if (state == null) return;
            state.normalizedTime = ClipTimeForAngleFrac(angleFrac);
            state.enabled = true; state.weight = 1f; state.speed = 0f;
            anim.Sample();
            if (hinge != null && lutAngleFrac != null && angleFrac > 0f && angleFrac < 1f)
            {
                // closed-loop: measure the true hinge angle and correct clip time (eased-out top of the clip is very flat)
                for (int k = 0; k < 3; k++)
                {
                    float err = angleFrac - Quaternion.Angle(hingeRest, hinge.localRotation) / fullAngle;
                    if (Mathf.Abs(err) * fullAngle < 0.02f) break;
                    float t = state.normalizedTime, dt = 1f / (LUT - 1);
                    int i = Mathf.Clamp((int)(t * (LUT - 1)), 0, LUT - 2);
                    float slope = Mathf.Max(1e-4f, (lutAngleFrac[i + 1] - lutAngleFrac[i]) / dt);
                    state.normalizedTime = Mathf.Clamp01(t + err / slope); anim.Sample();
                }
            }
            appliedTime = state.normalizedTime;
        }

        // ---------- commands ----------
        [KSPEvent(guiActive = true, guiActiveEditor = true, guiName = "Deploy Fins")]
        public void DeployEvent() { Deploy(); }
        [KSPEvent(guiActive = true, guiActiveEditor = true, guiName = "Retract Fins")]
        public void RetractEvent() { Retract(); }
        [KSPAction("Toggle Fins")] public void ToggleAction(KSPActionParam p)
        {
            if (p.type == KSPActionType.Activate) Deploy(); else Retract();
        }
        [KSPAction("Deploy Fins")] public void DeployAction(KSPActionParam p) { Deploy(); }
        [KSPAction("Retract Fins")] public void RetractAction(KSPActionParam p) { Retract(); }

        public void Deploy()
        {
            if (direction == 1 || (direction == 0 && position >= 0.999f)) return;
            Debug.Log("[GridFinStepDeploy] " + part.partInfo.name + ": deploy from " + (position * fullAngle).ToString("F0") + " deg, " + steps.Count + " steps");
            if (HighLogic.LoadedSceneIsEditor && !previewing) { position = 1f; direction = 0; Apply(1f); UpdateUI(); return; }
            direction = 1; phase = 0; phaseTime = 0f; phaseStart = position;
            stepIndex = 0; while (stepIndex < steps.Count - 1 && StepTarget(stepIndex) <= position + 0.001f) stepIndex++;
            onMoving.Fire(position, 1f);
        }
        public void Retract()
        {
            if (direction == -1 || (direction == 0 && position <= 0.001f)) return;
            if (HighLogic.LoadedSceneIsEditor && !previewing) { position = 0f; direction = 0; Apply(0f); UpdateUI(); return; }
            direction = -1; phase = 0; phaseTime = 0f; phaseStart = position; overshootDeg = 0f;
            stepIndex = steps.Count - 1; while (stepIndex > 0 && RetractTarget(stepIndex) >= position - 0.001f) stepIndex--;
            onMoving.Fire(position, 0f);
        }
        float RetractTarget(int i) { return i <= 0 ? 0f : StepTarget(i - 1); }

        // ---------- motion ----------
        void Update()
        {
            if (state == null || direction == 0) { previewing = false; return; }
            if (!HighLogic.LoadedSceneIsFlight && !previewing) return;
            float dt = HighLogic.LoadedSceneIsFlight ? TimeWarp.deltaTime : Time.deltaTime; if (dt <= 0) return;
            phaseTime += dt;
            if (direction == 1) RunDeploy(); else RunRetract();
            UpdateUI();
        }

        float Ease(float u) { u = Mathf.Clamp01(u); return moveEase == "linear" ? u : u * u * (3f - 2f * u); }

        void RunDeploy()
        {
            var s = steps[stepIndex];
            float target = StepTarget(stepIndex);
            bool last = stepIndex == steps.Count - 1;
            if (phase == 0)
            {
                float u = s.move > 0 ? phaseTime / s.move : 1f;
                // last step: hard stop - accelerate into the stop (no ease-out), like a hydraulic ram hitting its end stop
                float uc = Mathf.Clamp01(u);
                float e = last && lockMode == "impact" ? ImpactEase(uc)
                        : last && finalApproach == "accelerate" ? uc * uc : Ease(u);
                position = Mathf.Lerp(phaseStart, target, e); Apply(position);
                if (u >= 1f)
                {
                    position = target; Apply(position);
                    bool lockPhase = last && (lockMode == "impact" || (lockMode == "bounce" && lockOvershoot > 0));
                    if (lockPhase && lockMode == "impact") impactSpeed = ImpactSpeedDeg(s);
                    phase = lockPhase ? 2 : 1;
                    phaseTime = lockPhase ? Mathf.Max(0f, phaseTime - s.move) : 0f; // carry the frame remainder: no time snap
                }
            }
            else if (phase == 2)
            {
                float u = lockSettle > 0 ? phaseTime / lockSettle : 1f;
                overshootDeg = u >= 1f ? 0f : lockMode == "impact" ? -ImpactRebound(phaseTime) : lockOvershoot * Mathf.Exp(-lockDecay * u) * (1f - u) * Mathf.Sin(u * 2f * Mathf.PI * lockBounces);
                if (u >= 1f) { overshootDeg = 0f; phase = 1; phaseTime = 0f; s.hold = 0f; }
            }
            if (phase == 1 && phaseTime >= (last ? 0f : s.hold))
            {
                if (last) { direction = 0; position = 1f; onStop.Fire(position); }
                else { stepIndex++; phase = 0; phaseTime = 0f; phaseStart = position; }
            }
        }

        void RunRetract()
        {
            if (retractMode != "reverseSteps")
            {
                float dur = Mathf.Max(0.1f, retractDuration * phaseStart);
                position = Mathf.Lerp(phaseStart, 0f, Ease(phaseTime / dur)); Apply(position);
                if (phaseTime >= dur) { position = 0f; Apply(0f); direction = 0; onStop.Fire(0f); }
                return;
            }
            var s = steps[stepIndex];
            float target = RetractTarget(stepIndex);
            if (phase == 0)
            {
                float u = s.move > 0 ? phaseTime / s.move : 1f;
                position = Mathf.Lerp(phaseStart, target, Ease(u)); Apply(position);
                if (u >= 1f) { position = target; phase = 1; phaseTime = 0f; }
            }
            if (phase == 1)
            {
                float hold = stepIndex > 0 ? steps[stepIndex - 1].hold : 0f;
                if (stepIndex == 0) { direction = 0; position = 0f; onStop.Fire(0f); }
                else if (phaseTime >= hold) { stepIndex--; phase = 0; phaseTime = 0f; phaseStart = position; }
            }
        }

        bool warnedExternal; float appliedTime;
        // ---- impact model (pure functions; mirrored in tools/profile.py) ----
        float impactSpeed;
        // Hermite: p(0)=0, p'(0)=0, p(1)=1, p'(1)=impactSpeedFactor -> arrives at the stop still moving
        float ImpactEase(float u) { float r = impactSpeedFactor; return (3f - 2f * u) * u * u + r * (u * u * u - u * u); }
        float ImpactSpeedDeg(Step s) { float span = (StepTarget(steps.Count - 1) - (steps.Count > 1 ? StepTarget(steps.Count - 2) : 0f)) * fullAngle; return Mathf.Abs(span) * impactSpeedFactor / Mathf.Max(0.01f, s.move); }
        /// rebound distance BELOW the stop (deg, always >= 0): restitution bounce of a damped hinge spring
        /// plus a short structural shiver; the stop is a hard limit so the result is clamped at 0.
        float ImpactRebound(float t)
        {
            float w = 2f * Mathf.PI * lockFrequency, z = Mathf.Clamp(lockDamping, 0.01f, 0.95f);
            float wd = w * Mathf.Sqrt(1f - z * z);
            float A = Mathf.Min(lockMaxRebound, lockRebound * impactSpeed);
            float x = A * Mathf.Exp(-z * w * t) * Mathf.Abs(Mathf.Sin(wd * t));
            x += shiverAmplitude * Mathf.Exp(-t / Mathf.Max(0.01f, shiverDecay)) * Mathf.Sin(2f * Mathf.PI * shiverFrequency * t);
            float fade = lockSettle > 0 ? Mathf.Clamp01(1f - t / lockSettle) : 0f; fade = fade * fade * (3f - 2f * fade);
            return Mathf.Max(0f, x * fade);
        }

        void LateUpdate()
        {
            // own the clip: if anything else played/sped up NewFinsDeploy, put it back to our pose this frame
            if (state != null && (HighLogic.LoadedSceneIsFlight || previewing))
            {
                bool foreign = state.speed != 0f || Mathf.Abs(state.normalizedTime - appliedTime) > 0.002f;
                if (foreign && !warnedExternal) { warnedExternal = true; Debug.LogWarning("[GridFinStepDeploy] " + part.partInfo.name + ": another module drove '" + animationName + "' (speed " + state.speed + "); overriding"); }
                if (foreign) Apply(position);
            }
            // the lock bounce rotates the fin a few degrees past the clip's end pose about the clip's own hinge axis
            if (hinge != null && Mathf.Abs(overshootDeg) > 0.001f)
                hinge.localRotation = Quaternion.AngleAxis(overshootDeg, hingeAxisParent) * hinge.localRotation;
        }

        void UpdateUI()
        {
            float deg = position * (lutAngleFrac != null ? fullAngle : 90f);
            statusText = direction == 1 ? "Deploying " + deg.ToString("F0") + "°" + (phase == 1 ? " (hold)" : "")
                       : direction == -1 ? "Retracting " + deg.ToString("F0") + "°"
                       : position >= 0.999f ? "Deployed" : position <= 0.001f ? "Stowed" : deg.ToString("F0") + "°";
            Events["DeployEvent"].active = position < 0.999f && direction != 1;
            Events["RetractEvent"].active = position > 0.001f && direction != -1;
        }

        // ---------- live profile (tweak window) ----------
        public void ApplyProfile(Profile p, bool reapplyPose)
        {
            steps.Clear();
            foreach (var st in p.steps) steps.Add(new Step { target = st.target, move = st.move, hold = st.hold });
            finalApproach = p.finalApproach; lockMode = p.lockMode; impactSpeedFactor = p.impactSpeedFactor;
            lockRebound = p.lockRebound; lockMaxRebound = p.lockMaxRebound; lockDamping = p.lockDamping; lockFrequency = p.lockFrequency;
            lockSettle = p.lockSettle; shiverAmplitude = p.shiverAmplitude; shiverFrequency = p.shiverFrequency; shiverDecay = p.shiverDecay;
            retractMode = p.retractMode; retractDuration = p.retractDuration;
            if (direction != 0) { stepIndex = Mathf.Clamp(stepIndex, 0, Mathf.Max(0, steps.Count - 1)); }
            if (reapplyPose && direction == 0) Apply(position);
        }
        public Profile ToProfile()
        {
            var p = new Profile();
            foreach (var st in steps) p.steps.Add(new Profile.StepDef { target = st.target, move = st.move, hold = st.hold });
            p.finalApproach = finalApproach; p.lockMode = lockMode; p.impactSpeedFactor = impactSpeedFactor; p.lockRebound = lockRebound;
            p.lockMaxRebound = lockMaxRebound; p.lockDamping = lockDamping; p.lockFrequency = lockFrequency; p.lockSettle = lockSettle;
            p.shiverAmplitude = shiverAmplitude; p.shiverFrequency = shiverFrequency; p.shiverDecay = shiverDecay;
            p.retractMode = retractMode; p.retractDuration = retractDuration;
            return p;
        }
        /// run the real timed deploy / retract, also in the editor
        public void Preview(bool deploy)
        {
            previewing = true;
            if (deploy) { if (position >= 0.999f) { position = 0f; direction = 0; Apply(0f); } Deploy(); }
            else { if (position <= 0.001f) { position = 1f; direction = 0; Apply(1f); } Retract(); }
        }

        // ---------- IScalarModule (used by KRE ModuleDeployableAero via DeployModuleIndex) ----------
        public string ScalarModuleID { get { return moduleID; } }
        public float GetScalar { get { return position; } }
        public bool CanMove { get { return state != null; } }
        public EventData<float, float> OnMoving { get { return onMoving; } }
        public EventData<float> OnStop { get { return onStop; } }
        public void SetScalar(float t) { if (t >= 0.5f) Deploy(); else Retract(); }
        public void SetUIRead(bool s) { }
        public void SetUIWrite(bool s) { }
        public bool IsMoving() { return direction != 0; }

        public override string GetInfo()
        {
            string s = "Stepped hydraulic deploy:\n";
            foreach (var st in steps) s += "  -> " + st.target + (targetsInDegrees ? "°" : "") + " in " + st.move + " s, hold " + st.hold + " s\n";
            return s;
        }
    }
}
