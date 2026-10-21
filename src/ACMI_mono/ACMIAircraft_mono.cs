using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NuclearOption.Networking;
using UnityEngine;

namespace NOBlackBox
{
    internal class ACMIAircraft_mono : ACMIUnit_mono
    {
        /*
        private readonly static Dictionary<string, string> TYPES = new()
        {
            { "CI-22", "Air+FixedWing" },
            { "T/A-30", "Air+FixedWing" },
            { "SAH-46", "Air+Rotorcraft" },
            { "FS-12", "Air+FixedWing" },
            { "KR-67", "Air+FixedWing" },
            { "EW-25", "Air+FixedWing" },
            { "SFB-81", "Air+FixedWing" },
            { "VL-49", "Air+Rotorcraft" },
            { "FS-20", "Air+FixedWing" },
            { "UH-90", "Air+Rotorcraft" },
            { "A-19", "Air+FixedWing" }
        };
        */

        private bool lastGear = false;
        private int lastRadarMode = -1;   // -1 = not written yet
        private float lastAGL = float.NaN;
        private float lastTAS = float.NaN;
        private float lastAOA = float.NaN;
        private float lastThrottle = float.NaN;
        private float lastPitch = float.NaN;
        private float lastYaw = float.NaN;
        private float lastRoll = float.NaN;

        private Vector3 lastHead = Vector3.zero;

        Aircraft aircraft;
        Aircraft localAircraft = null;

        // Tomo's Nukes pilot-name fix (2026-10-04). Since game 0.34 GetPlayerName() returns a PlayerName object, so the
        // callsign was written as "(NuclearOption.Networking.PlayerName)". A dedicated server often cannot resolve a Steam
        // persona straight away either: the game then hands back "Player" or "ID: <steam64>". Recordings are published,
        // so a Steam ID is never written; until a real name resolves the callsign says "(Pilot)" and the name is checked
        // again every 5 s.
        private string pilotName = "";
        private float nextNameCheck = 0f;
        private static MethodInfo? nukeStatsRawName;
        private static bool nukeStatsLooked = false;
        private static readonly Regex steamIdRe = new Regex(@"7656119\d{10}");
        private static readonly Regex placeholderRe = new Regex(@"^Pilot( \d{4})?$");

        public virtual void Init(Aircraft aircraft)
        {
            GameManager.GetLocalAircraft(out localAircraft);
            if (localAircraft && localAircraft.persistentID == aircraft.persistentID) { base.unit = localAircraft; } else { base.unit = aircraft; }
            this.aircraft = (Aircraft)base.unit;
            base.unitId = aircraft.persistentID.Id;
            base.tacviewId = aircraft.persistentID.Id + 1;
            base.destroyedEvent = true;
            base.canTarget = true;
            lastState = aircraft.unitState;
            Faction? faction = this.unit.NetworkHQ?.faction;
            string[] info = { "Default", "Air" };
            if (Plugin.NOBlackBoxUnitInfo["aircraft"].ContainsKey(aircraft.definition.code))
            {
                info = Plugin.NOBlackBoxUnitInfo["aircraft"][unit.definition.code];
            }
            props = new PropBag()
            {
                { "Name", this.unit.definition.unitName },
                { "Coalition", faction?.factionName ?? "Neutral" },
                { "Color", faction == null ? "Green" : (faction.factionName == "Boscali" ? "Blue" : "Red") },
                { "Debug", lastState.ToString()},
                { "Type", info[1]},
                { "CallSign", $"{aircraft.definition.code} {tacviewId:X}" }
            };
            if (aircraft.Player != null)
            {
                pilotName = PilotNameOf(aircraft.Player);
                nextNameCheck = Time.time + 5f;
                props["CallSign"] = CallSignFor(pilotName);
                if (pilotName != "")
                {
                    props["Pilot"] = pilotName.Replace(",", "\\,");
                }
                if (Configuration.RecordSteamID.Value == true)
                {
                    props.Add("Registration", aircraft.Player.SteamID.ToString());
                }
            }
            Recorder_mono.Current!.invokeWriterUpdate(this);
            props.Clear();
            this.enabled = true;
            base.enabled = true;
        }

        internal override int TickRate => RateAircraft;

        // 2026-10-12: the old Tick set this every frame for the LateTick of the frame the aircraft is disabled in (the
        // only time it is read). It is worked out at that moment now, from the same frame's values.
        internal override void BeforeDisabledLateTick()
        {
            base.destroyedEvent = !aircraft.IsLanded();
        }

        // the old Tick after its timer check (the recorder keeps the timer and calls this when the update is due)
        internal override void DueTick()
        {
            if (!this.enabled || unit.disabled)
            {
                return;
            }
            if (aircraft.Player != null && pilotName == "" && Time.time >= nextNameCheck)
            {
                nextNameCheck = Time.time + 5f;
                string name = PilotNameOf(aircraft.Player);
                if (name != "")
                {
                    pilotName = name;
                    props["CallSign"] = CallSignFor(pilotName);
                    props["Pilot"] = pilotName.Replace(",", "\\,");
                }
            }
            UpdatePose();
#if RECPROF
            long t0 = RecProf.On ? RecProf.Now() : 0;
#endif
            UpdateAircraft();
#if RECPROF
            if (RecProf.On) RecProf.Add(RecProf.A_PROPS, t0);
#endif
            UpdateState();
#if RECPROF
            t0 = RecProf.On ? RecProf.Now() : 0;
#endif
            UpdateTargets();
#if RECPROF
            if (RecProf.On) RecProf.Add(RecProf.A_TARGETS, t0);
#endif
            Recorder_mono.Current!.invokeWriterUpdate(this);
            props.Clear();
        }

        // Tomo's Nukes 2026-10-20: the pilot's selected weapon targets are written whenever the list changes, a single
        // target included, and a slot no longer in use is written blank, so an emptied or shorter list is cleared. The
        // old code wrote the list only with 2 or more targets (AI lists hold 0 or 1, so AI targets never showed), never
        // cleared it, and wrote it again every update because it compared two new arrays.
        private const int TargetSlots = 10;
        private readonly long[] writtenTargets = new long[TargetSlots];
        private readonly long[] seenTargets = new long[TargetSlots];
        private int writtenTargetCount = 0;

        internal override void UpdateTargets()
        {
            List<Unit>? list = aircraft.weaponManager?.GetTargetList();
            int count = 0;
            if (list != null)
            {
                for (int i = 0; i < list.Count && count < TargetSlots; i++)
                {
                    Unit target = list[i];
                    if (!ReferenceEquals(target, null))
                    {
                        seenTargets[count++] = target.persistentID.Id;
                    }
                }
            }
            bool changed = count != writtenTargetCount;
            for (int i = 0; i < count && !changed; i++)
            {
                changed = seenTargets[i] != writtenTargets[i];
            }
            if (!changed)
            {
                return;
            }
            int slots = Math.Max(count, writtenTargetCount);
            for (int i = 0; i < slots; i++)
            {
                lockedTargetString = i == 0 ? "LockedTarget" : $"LockedTarget{i:X}";
                props.Add(lockedTargetString, i < count ? $"{GetTacviewIdOfUnit(seenTargets[i]):X}" : "");
            }
            Array.Copy(seenTargets, writtenTargets, count);
            writtenTargetCount = count;
        }

        void UpdateAircraft()
        {
            // 2026-10-12: numbers go to the writer thread as numbers, with the same formats (PropBag.AddNum)
            if (aircraft.speed != lastTAS && Configuration.RecordSpeed.Value == true)
            {
                Num("TAS", aircraft.speed, PropBag.Num2, CultureInfo.InvariantCulture);
                Num("Mach", (aircraft.speed / 340), PropBag.Num3, CultureInfo.InvariantCulture);
                lastTAS = aircraft.speed;
            }

            Vector3 vector3 = aircraft.cockpit.transform.InverseTransformDirection(aircraft.cockpit.rb.velocity);
            float num = MathF.Round(Mathf.Atan2(vector3.y, vector3.z) * -57.29578f, 2);

            if (num != lastAOA && Configuration.RecordAOA.Value == true)
            {
                Num("AOA", num, PropBag.Num2, CultureInfo.CurrentCulture);   // as before: the thread's culture
                lastAOA = num;
            }

            if (aircraft.radarAlt != lastAGL && Configuration.RecordAGL.Value == true)
            {
                Num("AGL", Mathf.Max(0, aircraft.radarAlt), PropBag.Num2, CultureInfo.InvariantCulture);
                lastAGL = aircraft.radarAlt;
            }

            if (aircraft.gearDeployed != lastGear && Configuration.RecordLandingGear.Value == true)
            {
                props.Add("LandingGear", aircraft.gearDeployed ? "1" : "0");
                lastGear = aircraft.gearDeployed;
            }

            // Tomo's Nukes 2026-10-20: RadarMode follows the radar being switched on and off (radar.activated, which the
            // player's toggle sets on the server too). The old check compared the radar object with a bool, so it was
            // written once when the radar first existed and never again.
            if (Configuration.RecordRadarMode.Value == true)
            {
                TargetDetector? radar = aircraft.radar;
                if (radar != null || lastRadarMode != -1)
                {
                    int mode = radar != null && radar.activated ? 1 : 0;
                    if (mode != lastRadarMode)
                    {
                        props.Add("RadarMode", mode == 1 ? "1" : "0");
                        lastRadarMode = mode;
                    }
                }
            }

            if (localAircraft && localAircraft.persistentID == aircraft.persistentID && CameraStateManager.cameraMode == CameraMode.cockpit && Configuration.RecordPilotHead.Value == true)
            {

                Camera camera = CameraStateManager.i.mainCamera;

                Vector3 rot = camera.transform.localEulerAngles;

                float fax = MathF.Round(rot.x, 2);
                float fay = MathF.Round(rot.y, 2);
                float faz = MathF.Round(rot.z, 2);

                Vector3 newRot = new(fax, fay, faz);

                if (newRot != lastHead)
                {
                    if (!Mathf.Approximately(newRot.x, lastHead.x))
                    {
                        float adjusted_pitch = newRot.x > 180.0f ? 360 - newRot.x : -newRot.x;
                        props.Add("PilotHeadPitch", adjusted_pitch.ToString("0.##", CultureInfo.InvariantCulture));
                    }

                    if (!Mathf.Approximately(newRot.y, lastHead.y))
                    {
                        float adjusted_yaw = newRot.y;
                        props.Add("PilotHeadYaw", adjusted_yaw.ToString("0.##", CultureInfo.InvariantCulture));
                    }

                    lastHead = newRot;
                }
            }

            if (Configuration.RecordExtraTelemetry.Value == true)
            {

                ControlInputs inputs = aircraft.GetInputs();   // GetInputs returns the same object every call
                if (lastThrottle != inputs.throttle)
                {
                    Num("Throttle", inputs.throttle, PropBag.Num2, CultureInfo.InvariantCulture);
                    lastThrottle = inputs.throttle;
                }
                if (lastRoll != inputs.roll)
                {
                    Num("RollControlInput", inputs.roll, PropBag.Num2, CultureInfo.InvariantCulture);
                    lastRoll = inputs.roll;
                }
                if (lastPitch != inputs.pitch)
                {
                    Num("PitchControlInput", inputs.pitch, PropBag.Num2, CultureInfo.InvariantCulture);
                    lastPitch = inputs.pitch;
                }
                if (lastYaw != inputs.yaw)
                {
                    Num("YawControlInput", inputs.yaw, PropBag.Num2, CultureInfo.InvariantCulture);
                    lastYaw = inputs.yaw;
                }
                // 2026-10-12 (owner: "drop the engine RPM"): EngineRPM is no longer written. Its average started from
                // NaN, so every aircraft line carried EngineRPM=NaN (31% of all lines in a test recording).
            }
        }

        private string CallSignFor(string name)
        {
            return $"{aircraft.definition.code} ({(name == "" ? "Pilot" : name.Replace(",", "\\,"))}) {tacviewId:X}";
        }

        // The name the rest of the server shows: NukeStats' resolver first (Steam, then its own last-known names), then the
        // game's resolved name, then the name the client reported when it joined. "" = nothing real yet.
        private static string PilotNameOf(Player player)
        {
            try
            {
                if (!nukeStatsLooked)
                {
                    nukeStatsLooked = true;
                    Type? t = AppDomain.CurrentDomain.GetAssemblies()
                        .Select(a => a.GetType("NukeStats.NukeStatsPlugin", false)).FirstOrDefault(x => x != null);
                    nukeStatsRawName = t?.GetMethod("RawNameOf", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                }
                if (nukeStatsRawName != null && IsRealName(nukeStatsRawName.Invoke(null, new object[] { player }) as string, out string fromStats))
                {
                    return fromStats;
                }
                PlayerName pn = player.GetPlayerName();
                if (pn != null && IsRealName(pn.SanitizedName, out string fromGame))
                {
                    return fromGame;
                }
                if (pn != null && IsRealName(pn.ClientReportedSteamName?.SanitizeRichText(32), out string reported))
                {
                    return reported;
                }
            }
            catch (Exception) { }
            return "";
        }

        private static bool IsRealName(string? n, out string name)
        {
            name = (n ?? "").Trim();
            return name != "" && name != "?" && name != "Player" && !name.StartsWith("ID: ", StringComparison.Ordinal)
                && !placeholderRe.IsMatch(name) && !steamIdRe.IsMatch(name) && !Regex.IsMatch(name, @"^\d+$");
        }
    }
}
