using Mirage.Serialization;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using UnityEngine;
using static Mirage.NetworkBehaviour;

namespace NOBlackBox
{
    internal class ACMIUnit_mono : ACMIObject_mono
    {
        private Vector3 lastPos = new(float.NaN, float.NaN, float.NaN);
        private Vector3 lastRot = new(float.NaN, float.NaN, float.NaN);
        internal Unit unit;
        internal Unit.UnitState lastState;
        internal bool purge = false;
        internal bool canTarget = false;
        // 2026-10-12: the unit's transform, looked up once (it was looked up six times per position)
        private Transform? unitTransform;

        internal Unit[] lastTargets = [];
        internal Unit[] targets = [];
        internal string lockedTargetString = "LockedTarget";

        public virtual void Init(Unit unit)
        {
            /*
            this.unit = unit;
            base.unitId = unit.persistentID.Id;
            base.tacviewId = unit.persistentID.Id + 1;
            lastState = unit.unitState;
            Faction? faction = this.unit.NetworkHQ?.faction;
            props = new PropBag()
            {
                { "Name", this.unit.definition.unitName },
                { "Coalition", faction?.factionName ?? "Neutral" },
                { "Color", faction == null ? "Green" : (faction.factionName == "Boscali" ? "Blue" : "Red") },
                { "Debug", lastState.ToString()}
            };
            Recorder_mono.Current!.invokeWriterUpdate(this);
            props.Clear();
            this.enabled = true;
            base.enabled = true;
            */
        }
        public override void Tick()
        {

        }

        // 2026-10-12: LateTick only acts on a unit that is disabled, so the recorder calls it only for those (the game
        // tells it when a unit's disabled flag changes) instead of every recorded object every frame. Buildings handle
        // their own destruction in their tick and say no here.
        internal virtual bool WatchesDisabled => true;

        // what the old per-frame Tick worked out before LateTick, for a unit LateTick is about to handle (aircraft)
        internal virtual void BeforeDisabledLateTick()
        {

        }

        public override void LateTick()
        {
            try
            {
                if (unit.disabled)
                {
                    base.disabled = true;
                    props.Add("Visible", "0.0");
                    props.Add("Type", null);
                    Recorder_mono.Current!.invokeWriterUpdate(this);
                    props.Clear();
                    if (base.destroyedEvent && !purge)
                    {
                        Recorder_mono.Current!.invokeWriterDestroy(this);
                    }
                    if (purge)
                    {
                        Recorder_mono.Current!.invokeWriterRemove(this);
                        this.enabled = false;
                        Plugin.Logger?.LogDebug($"DISABLING UNIT {unitId.ToString(CultureInfo.InvariantCulture)}");
                        GameObject.Destroy(gameObject);   // 2026-10-09: the whole object, so unitObjects/waves can drop it
                    }
                    //this is to stagger the Destroyed Event and the Remove call to 2 different time steps, as having them both on the same time step will confuse Tacview
                    purge = true;
                }
            } catch
            {
                GameObject.Destroy(gameObject);   // 2026-10-09: the whole object, so unitObjects/waves can drop it
            }
        }

#if SHADOW
        // the old main-thread text, kept in the check build to prove the writer thread writes the same
        private string UpdatePosition(Vector3 newPos, Vector3 newRot)
        {
            string x = Mathf.Approximately(newPos.x, lastPos.x) ? "" : newPos.x.ToString(CultureInfo.InvariantCulture);
            string y = Mathf.Approximately(newPos.y, lastPos.y) ? "" : newPos.y.ToString(CultureInfo.InvariantCulture);
            string z = Mathf.Approximately(newPos.z, lastPos.z) ? "" : newPos.z.ToString(CultureInfo.InvariantCulture);

            float adjusted_roll = newRot.z > 180.0f ? 360 - newRot.z : -newRot.z;
            float adjusted_pitch = newRot.x > 180.0f ? 360 - newRot.x : -newRot.x;
            float adjusted_yaw = newRot.y;

            string roll = Mathf.Approximately(newRot.z, lastRot.z) ? "" : adjusted_roll.ToString(CultureInfo.InvariantCulture);
            string pitch = Mathf.Approximately(newRot.x, lastRot.x) ? "" : adjusted_pitch.ToString(CultureInfo.InvariantCulture);
            string yaw = Mathf.Approximately(newRot.y, lastRot.y) ? "" : adjusted_yaw.ToString(CultureInfo.InvariantCulture);

            (float latitude, float longitude) = Helpers.CartesianToGeodetic(newPos.x, newPos.z);

            return $"{(newPos.x != lastPos.x ? longitude.ToString(CultureInfo.InvariantCulture) : string.Empty)}|{(newPos.z != lastPos.z ? latitude.ToString(CultureInfo.InvariantCulture) : string.Empty)}|{y}|{roll}|{pitch}|{yaw}|{x}|{z}|{yaw}";
        }
#endif

        internal void UpdatePose()
        {
#if RECPROF
            long tRead = RecProf.On ? RecProf.Now() : 0;
#endif
            // 2026-10-12: one position and one rotation read (the same values the six reads returned)
            unitTransform ??= unit.transform;
            Vector3 position = unitTransform.position;
            Vector3 angles = unitTransform.eulerAngles;
            float fx = MathF.Round(position.GlobalX(), 2);
            float fy = MathF.Round(position.GlobalY(), 2);
            float fz = MathF.Round(position.GlobalZ(), 2);

            float fax = MathF.Round(angles.x, 2);
            float fay = MathF.Round(angles.y, 2);
            float faz = MathF.Round(angles.z, 2);

            Vector3 newPos = new(fx, fy, fz);
            Vector3 newRot = new(fax, fay, faz);
#if RECPROF
            if (RecProf.On) RecProf.Add(RecProf.P_READ, tRead);
#endif

            if (newPos != lastPos || newRot != lastRot)
            {
#if RECPROF
                long tFmt = RecProf.On ? RecProf.Now() : 0;
#endif
                // the text is built on the writer thread (PropBag / PoseText.Unit)
                props.AddPose9("T", newPos, newRot, lastPos, lastRot);
#if SHADOW
                Shadow.Expect(props, UpdatePosition(newPos, newRot).ToString(CultureInfo.InvariantCulture));
#endif
#if RECPROF
                if (RecProf.On) RecProf.Add(RecProf.P_FMT, tFmt);
#endif

                lastPos = newPos;
                lastRot = newRot;
            }
        }

        internal void UpdateState()
        {
            if (unit.unitState != lastState)
            {
                lastState = unit.unitState;
                props.Add("Debug",lastState.ToString());
            }
        }

        internal virtual void UpdateTargets()
        {

        }


        // 2026-10-12: the recorder's own id map instead of a GetComponent call per target per update (same answer: the
        // recorded object's tacviewId, or -1 when the unit is not recorded or its object is gone)
        internal long GetTacviewIdOfUnit(long unitId)
        {
            long id = -1;
            try
            {
                ACMIUnit_mono? recorded = Recorder_mono.Current!.RecordedUnit(unitId);
                if (recorded == null)
                {
                    throw new KeyNotFoundException();
                }
                id = recorded.tacviewId;
            } catch
            {
                Plugin.Logger?.LogDebug($"GetTacviewIdofUnit failed to look up UNIT ID {unitId}");
                id = -1;
            }
            return id;
        }

        // Tomo's Nukes 2026-10-20: a ship's or ground vehicle's EngagementRange from its own detectors, read the way the
        // game's map range ring reads them (MapToolTip): every TargetDetector under the unit, the larger of the radars'
        // max range (Radar.RadarParameters.maxRange) and the sight range (TargetDetector.visualRange). These are prefab
        // values, there before the recorder sees the unit, and a headless server keeps them. The hand-typed table
        // (PluginUnitInfo: name, type, optical, radar) had gaps and old names, so it's used only for a unit with no
        // detector, or if the read fails.
        private static bool rangeReadFailed = false;

        internal static int EngagementRangeOf(Unit unit, string[] info)
        {
            try
            {
                TargetDetector[] detectors = unit.transform.GetComponentsInChildren<TargetDetector>();
                if (detectors.Length > 0)
                {
                    float radar = 0f;
                    float optical = 0f;
                    foreach (TargetDetector detector in detectors)
                    {
                        if (detector is Radar)
                        {
                            radar = Math.Max(radar, detector.GetRadarRange());
                        }
                        optical = Math.Max(optical, detector.GetVisualRange());
                    }
                    return Mathf.RoundToInt(Math.Max(radar, optical));
                }
            }
            catch (Exception e)
            {
                if (!rangeReadFailed)
                {
                    rangeReadFailed = true;
                    Plugin.Logger?.LogWarning($"EngagementRange: reading the detectors of {unit.definition.unitName} failed, using the table ({e.Message})");
                }
            }
            int optic = 0;
            int radarTable = 0;
            if (info.Length > 3)
            {
                int.TryParse(info[2], out optic);
                int.TryParse(info[3], out radarTable);
            }
            return Math.Max(optic, radarTable);
        }
    }
}
