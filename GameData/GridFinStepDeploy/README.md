# GridFinStepDeploy 0.2.0
Stepped, Falcon 9 style deploy for the KRE titanium grid fins (Grid Fin S/M/L Titanium). It replaces ModuleAnimateGeneric in place.
Default profile (CRS-35): 0->45 deg over 3 s, hold 3 s; ->75 deg over 2.5 s, hold 3.5 s; ->90 deg over 2.5 s; then the hinge-impact lock.

Requires ToolbarControl + ClickThroughBlocker.

## Tweak window (0.2.0)
The Grid Fin Step Deploy toolbar button works in flight, the VAB and the SPH. You can edit:
- the steps (target angle, move time, hold time; add and remove steps);
- final approach and lock mode;
- impact speed, rebound, damping, frequency, settle time and shiver;
- retract mode and time.

**Apply + save** writes `PluginData/GridFinStepDeploy_Profile.cfg` and updates every loaded titanium fin. New fins use the saved profile too.
**Preview deploy/retract** runs the real timed motion on the active vessel's fins, also in the editor, using the window's current (unsaved) values.
**Reset to CRS-35 defaults** deletes the saved file and goes back to the cfg values in `GridFinStepDeploy_KRE_Titanium.cfg`.

Remove: delete `GameData/GridFinStepDeploy` (fins go back to KRE's stock animation).
