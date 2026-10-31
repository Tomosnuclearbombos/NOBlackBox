using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace NOBlackBox
{
    // Tomo's Nukes 2026-10-25 (Tomo: "I would want radar detection lines to be shown with a distinct colour line different
    // to the active lock colour. It should show in their focus menu whether they are detected on enemy data link too").
    // 2026-10-26 (Tomo: "Yes do site and radar thanks"): SightDetects, what each unit's eyes see.
    // 2026-10-27 (Tomo: "Maybe fix that in the recorder then so it works properly"): RadarLockedBy, the red warnings.
    // Five properties for the viewer, each written only when it changes (an empty value when it empties):
    //   RadarDetects=<id>;<id>   on a unit with a Radar: what its radar (not its sight) detected in its last complete scan.
    //     Every TargetDetector refills its own list each scan (TargetDetector.RepeatSearch). DetectionRequest.ProcessResult,
    //     the only caller of Radar.CanSeeRadarReturn, calls DetectTarget straight after a radar check passes, so a
    //     detection that follows a passing check on the same radar and target is a radar one. TargetSearch starts
    //     each scan (Radar overrides it without calling the base one, so both are hooked): the scan before it is
    //     complete then and becomes the published set.
    //   SightDetects=<id>;<id>   on a unit with any TargetDetector (a Radar is one too): what its eyes saw in their last
    //     complete scan. Every other detection is a sight one (DetectionRequest.ProcessResult's line of sight branch,
    //     after VisualCheck). A target its radar found is left out, so it is in only one of the two.
    //   PassiveTracks=<id>;<id>  on an aircraft with a RadarLocator: the emitters that pinged its receiver in the last 4 s.
    //     The locator keeps no list of its own; each ping (Aircraft.onRadarWarning) puts the emitter in its side's tracking.
    //     It takes the locator's own rules (server, a side, onlySurface) and the game's 4 s tracking window.
    //   TrackedBy=<Coalition>;<Coalition> on aircraft, ships and ground vehicles: the other sides whose data link has it,
    //     FactionHQ.IsTargetBeingTracked (spotted by that side in the last 4 s).
    //   RadarLockedBy=<id>;<id>  on aircraft: the emitters whose latest radar warning to it, in the last 4 s, was red.
    //     The game's own red (Aircraft.OnRadarWarning.isTarget, what the map and tac screen colour TargetPing) is
    //     "!(emitter is Aircraft) && detected && emitter.CheckIsTarget(this)": a jet's warning is never red there. Here
    //     a jet counts too when its radar detects the aircraft and one of its weapons is aimed at it (the same
    //     CheckIsTarget). Read from each aircraft's own onRadarWarning event, so it needs no hook.
    // None of the hooked methods is generic. The hooks run inside the game's own code: a recorder problem never breaks it.
    internal static class DetectionFeed
    {
        internal const float TrackWindow = 4f;   // FactionHQ.IsTargetBeingTracked's 4 s
        internal static bool applied = false;
        private static ManualLogSource? log;
        private static bool warned = false;

        private sealed class ScanSets
        {
            internal HashSet<long> building = new HashSet<long>();
            internal HashSet<long> published = new HashSet<long>();
            internal HashSet<long> sightBuilding = new HashSet<long>();
            internal HashSet<long> sightPublished = new HashSet<long>();
        }
        private static readonly ConditionalWeakTable<TargetDetector, ScanSets> scans = new ConditionalWeakTable<TargetDetector, ScanSets>();
        private static readonly Dictionary<long, Dictionary<long, float>> heard = new Dictionary<long, Dictionary<long, float>>();
        private static readonly Dictionary<long, Dictionary<long, float>> locked = new Dictionary<long, Dictionary<long, float>>();
        private static readonly ConditionalWeakTable<Aircraft, object> watched = new ConditionalWeakTable<Aircraft, object>();
        private static readonly object yes = new object();   // the value in watched: never the aircraft itself

        // the radar check that just passed (DetectionRequest.ProcessResult runs on the main thread)
        private static Radar? passedRadar;
        private static Unit? passedTarget;

        internal static void Apply(Harmony harmony, ManualLogSource logger)
        {
            log = logger;
            try
            {
                Patch(harmony, AccessTools.Method(typeof(Radar), nameof(Radar.CanSeeRadarReturn)), nameof(CheckPassed), postfix: true);
                Patch(harmony, AccessTools.Method(typeof(TargetDetector), nameof(TargetDetector.DetectTarget)), nameof(Detected), postfix: false);
                Patch(harmony, AccessTools.Method(typeof(Radar), "TargetSearch"), nameof(ScanStarts), postfix: false);
                Patch(harmony, AccessTools.Method(typeof(TargetDetector), "TargetSearch"), nameof(EyesScanStart), postfix: false);
                Patch(harmony, AccessTools.Method(typeof(RadarLocator), "RadarLocator_OnRadarWarning"), nameof(Pinged), postfix: true);
                applied = true;
            }
            catch (Exception e)
            {
                logger.LogWarning($"Radar detection hooks not applied, RadarDetects/SightDetects/PassiveTracks/TrackedBy/RadarLockedBy are not recorded: {e.Message}");
            }
        }

        private static void Patch(Harmony harmony, System.Reflection.MethodBase? target, string method, bool postfix)
        {
            if (target == null) throw new MissingMethodException(method);
            if (target.IsGenericMethod || target.DeclaringType!.IsGenericType) throw new NotSupportedException("generic: " + method);
            HarmonyMethod hm = new HarmonyMethod(typeof(DetectionFeed), method);
            if (postfix) harmony.Patch(target, postfix: hm); else harmony.Patch(target, prefix: hm);
        }

        private static void Fail(Exception e)
        {
            if (!warned)
            {
                warned = true;
                log?.LogWarning($"Radar detection hook failed once (no more warnings): {e.Message}");
            }
        }

        private static void CheckPassed(Radar __instance, IRadarReturn radarReturn, bool __result)
        {
            try
            {
                if (__result)
                {
                    passedRadar = __instance;
                    passedTarget = radarReturn as Unit;
                }
                else
                {
                    passedRadar = null;
                    passedTarget = null;
                }
            }
            catch (Exception e) { Fail(e); }
        }

        private static void Detected(TargetDetector __instance, Unit target)
        {
            try
            {
                if (target != null && __instance != null)
                {
                    ScanSets s = scans.GetOrCreateValue(__instance);
                    if (__instance is Radar radar && ReferenceEquals(radar, passedRadar) && ReferenceEquals(target, passedTarget))
                    {
                        s.building.Add(target.persistentID.Id);
                    }
                    else
                    {
                        s.sightBuilding.Add(target.persistentID.Id);
                    }
                }
                passedRadar = null;
                passedTarget = null;
            }
            catch (Exception e) { Fail(e); }
        }

        private static void ScanStarts(Radar __instance) => Publish(__instance);

        private static void EyesScanStart(TargetDetector __instance) => Publish(__instance);

        private static void Publish(TargetDetector detector)
        {
            try
            {
                ScanSets s = scans.GetOrCreateValue(detector);
                HashSet<long> done = s.building;
                s.building = s.published;
                s.building.Clear();
                s.published = done;
                done = s.sightBuilding;
                s.sightBuilding = s.sightPublished;
                s.sightBuilding.Clear();
                s.sightPublished = done;
            }
            catch (Exception e) { Fail(e); }
        }

        private static void Pinged(Aircraft ___aircraft, bool ___onlySurface, Aircraft.OnRadarWarning source)
        {
            try
            {
                if (___aircraft == null || !___aircraft.IsServer || ___aircraft.NetworkHQ == null || source.emitter == null) return;
                if (___onlySurface)
                {
                    TypeIdentity t = source.emitter.definition.typeIdentity;
                    if (t.air > 0f || t.missile > 0f) return;
                }
                long id = ___aircraft.persistentID.Id;
                if (!heard.TryGetValue(id, out Dictionary<long, float>? got))
                {
                    got = new Dictionary<long, float>();
                    heard[id] = got;
                }
                got[source.emitter.persistentID.Id] = Time.timeSinceLevelLoad;
            }
            catch (Exception e) { Fail(e); }
        }

        internal static void WatchWarnings(Aircraft aircraft)
        {
            try
            {
                if (aircraft == null || watched.TryGetValue(aircraft, out _)) return;
                watched.Add(aircraft, yes);
                aircraft.onRadarWarning += w => Warned(aircraft, w);
            }
            catch (Exception e) { Fail(e); }
        }

        private static void Warned(Aircraft target, Aircraft.OnRadarWarning w)
        {
            try
            {
                if (target == null || !target.IsServer || w.emitter == null) return;
                bool red = w.isTarget || (w.detected && w.emitter is Aircraft && w.emitter.CheckIsTarget(target));
                long id = target.persistentID.Id;
                if (red)
                {
                    if (!locked.TryGetValue(id, out Dictionary<long, float>? got))
                    {
                        got = new Dictionary<long, float>();
                        locked[id] = got;
                    }
                    got[w.emitter.persistentID.Id] = Time.timeSinceLevelLoad;
                }
                else if (locked.TryGetValue(id, out Dictionary<long, float>? got))
                {
                    got.Remove(w.emitter.persistentID.Id);   // its latest warning isn't red
                    if (got.Count == 0) locked.Remove(id);
                }
            }
            catch (Exception e) { Fail(e); }
        }

        // ------------------------------------------------------------- read by the recorded units (main thread)

        private static readonly List<long> idBuf = new List<long>();
        private static readonly List<long> pruneBuf = new List<long>();
        private static readonly List<string> nameBuf = new List<string>();
        private static readonly HashSet<long> radarSet = new HashSet<long>();
        private static readonly HashSet<long> sightSet = new HashSet<long>();
        private static readonly HashSet<long> heardSet = new HashSet<long>();
        private static readonly StringBuilder sb = new StringBuilder();

        internal static TargetDetector[] DetectorsOf(Unit unit)
        {
            try { return unit.GetComponentsInChildren<TargetDetector>(true); } catch { return Array.Empty<TargetDetector>(); }
        }

        internal static bool HasLocator(Unit unit)
        {
            try { return unit.GetComponentInChildren<RadarLocator>(true) != null; } catch { return false; }
        }

        // the recording's object ids (as LockedTarget) of the units, sorted, into last; false when they are what last held.
        // A unit the recorder doesn't have is left out. Nothing is allocated while the set stays the same.
        private static bool Changed(HashSet<long> units, List<long> last)
        {
            idBuf.Clear();
            Recorder_mono? rec = Recorder_mono.Current;
            if (rec != null)
            {
                foreach (long u in units)
                {
                    ACMIUnit_mono? recorded = rec.RecordedUnit(u);
                    if (recorded != null) idBuf.Add(recorded.tacviewId);
                }
            }
            idBuf.Sort();
            if (idBuf.Count == last.Count)
            {
                bool same = true;
                for (int i = 0; i < idBuf.Count && same; i++) same = idBuf[i] == last[i];
                if (same) return false;
            }
            last.Clear();
            last.AddRange(idBuf);
            return true;
        }

        // hex, ';' between them
        internal static string Join(List<long> ids)
        {
            sb.Clear();
            for (int i = 0; i < ids.Count; i++)
            {
                if (i > 0) sb.Append(';');
                sb.Append(ids[i].ToString("X"));
            }
            return sb.ToString();
        }

        // what the unit's detectors found in their last complete scans: radar into lastRadar, sight (without what its
        // radar found) into lastSight. A detector switched off, or on a unit with no side, isn't scanning: its old scan
        // doesn't count.
        internal static void Scans(Unit unit, TargetDetector[] detectors, List<long> lastRadar, List<long> lastSight,
                                   out bool radarChanged, out bool sightChanged)
        {
            radarSet.Clear();
            sightSet.Clear();
            if (unit.NetworkHQ != null)
            {
                foreach (TargetDetector d in detectors)
                {
                    if (d == null || !d.activated || !scans.TryGetValue(d, out ScanSets? s)) continue;
                    foreach (long id in s.published) radarSet.Add(id);
                    foreach (long id in s.sightPublished) sightSet.Add(id);
                }
                foreach (long id in radarSet) sightSet.Remove(id);
            }
            radarChanged = Changed(radarSet, lastRadar);
            sightChanged = Changed(sightSet, lastSight);
        }

        internal static bool PassiveTracks(Unit unit, List<long> last) => Recent(heard, unit, last);

        internal static bool LockedBy(Unit unit, List<long> last) => Recent(locked, unit, last);

        // the emitters seen for this unit in the last 4 s into last; older ones are dropped
        private static bool Recent(Dictionary<long, Dictionary<long, float>> seen, Unit unit, List<long> last)
        {
            heardSet.Clear();
            if (seen.TryGetValue(unit.persistentID.Id, out Dictionary<long, float>? got))
            {
                float now = Time.timeSinceLevelLoad;
                pruneBuf.Clear();
                foreach (KeyValuePair<long, float> kv in got)
                {
                    if (now - kv.Value <= TrackWindow && kv.Value <= now) heardSet.Add(kv.Key); else pruneBuf.Add(kv.Key);
                }
                foreach (long k in pruneBuf) got.Remove(k);
                if (got.Count == 0) seen.Remove(unit.persistentID.Id);
            }
            return Changed(heardSet, last);
        }

        internal static string TrackedBy(Unit unit)
        {
            nameBuf.Clear();
            foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
            {
                if (hq == null || hq == unit.NetworkHQ || hq.faction == null) continue;
                if (hq.IsTargetBeingTracked(unit)) nameBuf.Add(hq.faction.factionName);
            }
            nameBuf.Sort(StringComparer.Ordinal);
            return string.Join(";", nameBuf);
        }

        internal static void Forget(long unitId)
        {
            heard.Remove(unitId);
            locked.Remove(unitId);
        }
    }

    // one per recorded unit: what was last written, so a property is written only when it changes
    internal sealed class DetectionState
    {
        private readonly TargetDetector[] detectors;
        private readonly bool radar;
        private readonly bool locator;
        private readonly bool tracked;
        private readonly bool warned;
        private readonly List<long> lastRadar = new List<long>(), lastSight = new List<long>(), lastPassive = new List<long>(),
                                    lastLocked = new List<long>();
        private string? lastTracked;

        internal DetectionState(Unit unit, bool tracked)
        {
            detectors = DetectionFeed.DetectorsOf(unit);
            foreach (TargetDetector d in detectors) if (d is Radar) radar = true;
            locator = unit is Aircraft && DetectionFeed.HasLocator(unit);
            this.tracked = tracked;
            if (unit is Aircraft aircraft)
            {
                DetectionFeed.WatchWarnings(aircraft);
                warned = true;
            }
        }

        internal bool HasDetectors => detectors.Length > 0;

        internal void Write(ACMIUnit_mono rec, Unit unit, PropBag props)
        {
            if (!DetectionFeed.applied || !Configuration.RecordDetections.Value) return;
            try
            {
                if (detectors.Length > 0)
                {
                    DetectionFeed.Scans(unit, detectors, lastRadar, lastSight, out bool radarChanged, out bool sightChanged);
                    if (radar && radarChanged) props.Add("RadarDetects", DetectionFeed.Join(lastRadar));
                    if (sightChanged) props.Add("SightDetects", DetectionFeed.Join(lastSight));
                }
                if (locator && DetectionFeed.PassiveTracks(unit, lastPassive))
                {
                    props.Add("PassiveTracks", DetectionFeed.Join(lastPassive));
                }
                if (warned && DetectionFeed.LockedBy(unit, lastLocked))
                {
                    props.Add("RadarLockedBy", DetectionFeed.Join(lastLocked));
                }
                if (tracked && unit.NetworkHQ != null)
                {
                    string v = DetectionFeed.TrackedBy(unit);
                    if (v != (lastTracked ?? "")) { props.Add("TrackedBy", v); lastTracked = v; }
                }
            }
            catch (Exception) { }
        }
    }
}
