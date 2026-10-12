using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace NOBlackBox
{
    internal class ACMIBuilding_mono : ACMIUnit_mono
    {
        public Building building;
        private string coalition;
        public virtual void Init(Building building)
        {
            base.unit = building;
            this.building = (Building)base.unit;
            base.unitId = unit.persistentID.Id;
            base.tacviewId = unit.persistentID.Id + 1;
            base.destroyedEvent = true;
            lastState = unit.unitState;
            Faction? faction = this.unit.NetworkHQ?.faction;
            props = new PropBag()
            {
                { "Name", this.unit.definition.unitName },
                { "CallSign", $"{building.definition.code} {tacviewId:X}"},
                { "Coalition", faction?.factionName ?? "Neutral" },
                { "Color", faction == null ? "Green" : (faction.factionName == "Boscali" ? "Blue" : "Red") },
                { "Type","Ground+Static+Building"},
                { "Debug", lastState.ToString()}
            };
            UpdatePose();
            UpdateState();
            Recorder_mono.Current!.invokeWriterUpdate(this);
            props.Clear();
            this.enabled = true;
            base.enabled = true;
        }

        internal override int TickRate => RateBuilding;

        // a building handles its own destruction and repair here, so it is not in the recorder's disabled queue
        internal override bool WatchesDisabled => false;

        // 2026-10-12: the old Tick after its timer check (the recorder keeps the timer and calls this when it is due)
        internal override void DueTick()
        {
            if (unit.NetworkHQ?.faction.factionName != coalition && !building.disabled && !base.disabled)
            {
                coalition = unit.NetworkHQ?.faction.factionName ?? "Neutral";
                props.Add("Coalition", unit.NetworkHQ?.faction.factionName ?? "Neutral");

                string color = "Green";
                switch (unit.NetworkHQ?.faction.factionName)
                {
                    case "Boscali":
                        color = "Blue";
                        break;
                    case "Primeva":
                        color = "Red";
                        break;
                    default:
                        color = "Green";
                        break;
                }

                props.Add("Color", color);
                props.Add("Debug", $"{building.unitState.ToString()}");
                Recorder_mono.Current!.invokeWriterUpdate(this);

            }
            if (unit.disabled && !base.disabled)
            {
                props.Clear();
                props.Add("Visible", "0.0");
                props.Add("Type", null);
                Recorder_mono.Current!.invokeWriterUpdate(this);
                Recorder_mono.Current!.invokeWriterDestroy(this);
                props.Clear();
                base.disabled = true;
            }
            if (!unit.disabled && base.disabled)
            {
                this.Init(this.building);
                props.Add("Visible", "1.0");
                Recorder_mono.Current!.invokeWriterUpdate(this);
                props.Clear();
                Recorder_mono.Current!.invokeWriterRepair(this);
                base.disabled = false;
            }
            props.Clear();
        }

        public override void LateTick()
        {
            
        }

    }
}
