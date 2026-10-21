using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;

namespace NOBlackBox
{
    internal class ACMIShip_mono : ACMIUnit_mono
    {
        /*
        private static readonly Dictionary<string, string> TYPES = new()
        {
            { "Shard Class Corvette", "Sea+Watercraft+Medium+Warship" },
            { "Dynamo Class Destroyer", "Sea+Watercraft+Medium+Warship" },
            { "Hyperion Class Carrier", "Sea+Watercraft+Heavy+AircraftCarrier" },
            { "Annex Class Carrier", "Sea+Watercraft+Heavy+AircraftCarrier" },
            { "OTB-31 landing craft", "Sea+Watercraft" }
        };

        private static readonly Dictionary<string, int> RANGES = new()
        {
            { "Shard Class Corvette", 15000 },
            { "Dynamo Class Destroyer", 50000 },
            { "Hyperion Class Carrier", 15000 }
        };
        */
        private Unit?[] lastTarget;
        private Ship ship;

        public virtual void Init(Ship ship)
        {
            base.unit = ship;
            this.ship = (Ship)base.unit;
            base.unitId = ship.persistentID.Id;
            base.tacviewId = ship.persistentID.Id + 1;
            base.destroyedEvent = true;
            base.canTarget = true;
            lastTarget = new Unit?[Math.Min(10, ship.weaponStations.Count)];
            Faction? faction = base.unit.NetworkHQ?.faction;
            string[] info = { "Default", "Sea+Watercraft", "0", "0" };
            if (Plugin.NOBlackBoxUnitInfo["ships"].ContainsKey(ship.definition.unitName))
            {
                info = Plugin.NOBlackBoxUnitInfo["ships"][ship.definition.unitName];
            }
            // Tomo's Nukes 2026-10-20: the larger of its radar and sight ranges, read from the unit's own detectors; the
            // table is only the fallback (ACMIUnit_mono.EngagementRangeOf)
            int winningRange = EngagementRangeOf(base.unit, info);

            props = new PropBag()
            {
                { "Name", base.unit.definition.unitName },
                { "Coalition", faction?.factionName ?? "Neutral" },
                { "CallSign", $"{ship.definition.unitName} {tacviewId:X}"},
                { "Color", faction == null ? "Green" : (faction.factionName == "Boscali" ? "Blue" : "Red") },
                { "Type", info[1] },
                { "EngagementRange", winningRange.ToString(CultureInfo.InvariantCulture) },
                { "Debug", lastState.ToString()}
            };
            Recorder_mono.Current!.invokeWriterUpdate(this);
            props.Clear();
            this.enabled = true;
            base.enabled = true;
        }

        internal override int TickRate => RateVehicle;

        // 2026-10-12: the old Tick after its timer check (the recorder keeps the timer and calls this when it is due)
        internal override void DueTick()
        {
            if (!this.enabled || unit.disabled)
            {
                return;
            }
            UpdatePose();
            UpdateTargets();
            UpdateState();
            Recorder_mono.Current!.invokeWriterUpdate(this);
            props.Clear();
        }

        // Tomo's Nukes performance build (2026-10-06): no UpdateTargets override. The upstream one called
        // targets.AddItem(station.GetTurret().GetTarget()) for every weapon station and threw the result away (AddItem
        // returns a new sequence), so targets stayed empty and nothing was ever written. A station without a turret
        // also threw and caught a NullReferenceException every update. GetTurret and GetTarget only read fields, so
        // dropping the loop writes exactly the same recording.
    }
}
