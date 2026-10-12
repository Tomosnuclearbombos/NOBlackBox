using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace NOBlackBox
{
    internal class ACMIMissile_mono : ACMIUnit_mono
    {
        private readonly Dictionary<string, string> TYPES = new()
        {
            {"MSL", "Weapon+Missile"},
            {"BOMB", "Weapon+Bomb"},
            {"SHL", "Projectile+Shell" }
        };

        FieldInfo warheadField;
        FieldInfo detonatedField;
        FieldInfo armedField;

        private float lastAGL = float.NaN;
        private float lastTAS = float.NaN;
        private float lastAOA = float.NaN;
        private uint lastTarget = 0;

        internal bool Detonated
        {
            get; private set;
        }
        Missile missile;
        GameObject shockwave;

        public event Action<ACMIMissile_mono>? OnDetonate;

        public virtual void Init(Missile missile)
        {
            base.unit = missile;
            this.missile = (Missile)base.unit;
            base.unitId = missile.persistentID.Id;
            base.tacviewId = missile.persistentID.Id + 1;
            base.canTarget = true;
            string[] info = { "Default", "Weapon" };
            if (Plugin.NOBlackBoxUnitInfo["missiles"].ContainsKey(missile.definition.code))
            {
                info = Plugin.NOBlackBoxUnitInfo["missiles"][missile.definition.code];
            }
            if (unit.unitName.EndsWith("kt)"))
            {
                base.destroyedEvent = true;
            }

            lastState = missile.unitState;
            Faction? faction = base.unit.NetworkHQ?.faction;
            props = new PropBag()
            {
                { "Name", base.unit.definition.unitName },
                { "Coalition", faction?.factionName ?? "Neutral" },
                { "Color", faction == null ? "Green" : (faction.factionName == "Boscali" ? "Blue" : "Red") },
                { "Type", info[1] },
                { "CallSign", $"{missile.definition.unitName} {tacviewId:X}" },
                { "Debug", lastState.ToString()}
            };
            Recorder_mono.Current!.invokeWriterUpdate(this);
            props.Clear();
            this.enabled = true;
            base.enabled = true;
        }
        internal override int TickRate => RateMunition;

        // 2026-10-12: the old Tick after its timer check (the recorder keeps the timer and calls this when it is due)
        internal override void DueTick()
        {
            if (!this.enabled || unit.disabled)
            {
                return;
            }
            UpdatePose();
            UpdateState();
#if RECPROF
            long t0 = RecProf.On ? RecProf.Now() : 0;
#endif
            UpdateMissile();
#if RECPROF
            if (RecProf.On) RecProf.Add(RecProf.M_PROPS, t0);
#endif
            Recorder_mono.Current!.invokeWriterUpdate(this);
            props.Clear();
        }

        void UpdateMissile()
        {
            //warheadField = typeof(Missile).GetField("warhead", BindingFlags.NonPublic | BindingFlags.Instance);
            //object warheadInstance = warheadField.GetValue(unit);
            //Type warheadType = warheadInstance.GetType();
            //detonatedField = warheadType.GetField("detonated", BindingFlags.NonPublic | BindingFlags.Instance);
            //armedField = warheadType.GetField("Armed", BindingFlags.Public | BindingFlags.Instance);
            //bool isDetonated = (bool)detonatedField.GetValue(warheadInstance);
            //bool isArmed = (bool)armedField.GetValue(warheadInstance);

            if (unit.speed != lastTAS && Configuration.RecordSpeed.Value == true)
            {
                Num("TAS", unit.speed, PropBag.Num2, CultureInfo.InvariantCulture);
                Num("Mach", (unit.speed / 340), PropBag.Num2, CultureInfo.InvariantCulture);
                lastTAS = unit.speed;
            }

            Vector3 vector3 = unit.transform.InverseTransformDirection(unit.rb.velocity);
            float num = Mathf.Atan2(vector3.y, vector3.z) * -57.29578f;

            if (num != lastAOA && Configuration.RecordAOA.Value == true)
            {
                Num("AOA", num, PropBag.Num2, CultureInfo.InvariantCulture);
                lastAOA = num;
            }

            if (unit.radarAlt != lastAGL && Configuration.RecordAGL.Value == true)
            {
                Num("AGL", unit.radarAlt, PropBag.Num2, CultureInfo.InvariantCulture);
                lastAGL = unit.radarAlt;
            }
            
            if (missile.targetID.Id != lastTarget)
            {
                if (missile.targetID.Id != -1)
                {
                    props.Add("LockedTarget", $"{GetTacviewIdOfUnit(missile.targetID.Id):X}");

                    if (lastTarget == -1)
                        props.Add("LockedTargetMode", "1");
                }
                else
                    props.Add("LockedTargetMode", "0");


                lastTarget = missile.targetID.Id;
            }
        }
    }
}
