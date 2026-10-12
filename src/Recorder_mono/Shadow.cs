#if SHADOW
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using NOBlackBox.src.ACMI_mono;
using UnityEngine;

namespace NOBlackBox
{
    // Check build only (2026-10-12, test server, never deployed). It runs the old way next to the new one and counts
    // every difference, once a minute in the BepInEx log:
    //   lines: every line the writer thread builds against the text the old main-thread code built
    //   ticks: the old per-object timers against the shared group timers (which objects update in which frame)
    //   disabled: a disabled unit the hook did not report before its LateTick
    //   units: the game's unit list against the old scene search (the units each discovery would record)
    //   waves: an explosion the old scene search found that the hook did not record
    internal static class Shadow
    {
        private static long lineChecks, lineMismatches, tickFrames, tickMismatches, unitScans, unitOnlyOld, unitOnlyNew;
        private static long waveScans, waveMissed, disabledMissed, sweepCaught, objectTicks;
        private static readonly ConcurrentQueue<string> samples = new ConcurrentQueue<string>();
        private static int sampleCount;
        private static float windowStart = -1f;
        private static BepInEx.Logging.ManualLogSource? log;

        private static void Sample(string s)
        {
            if (Interlocked.Increment(ref sampleCount) <= 80)
            {
                samples.Enqueue(s);
            }
        }

        // ---- lines ----

        internal static void Expect(PropBag bag, string text)
        {
            bag.expect[bag.count - 1] = text;
        }

        // the old ACMIWriter.StringifyProps over the strings the old code put in the dictionary
        internal static string OldLine(long tacviewId, PropBag bag)
        {
            StringBuilder sb = new StringBuilder(256);
            sb.Append(tacviewId.ToString("X")).Append(',');
            for (int i = 0; i < bag.count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }
                sb.Append(bag.keys[i]).Append('=').Append(bag.kinds[i] == PropBag.Str ? bag.strs[i] : (bag.expect[i] ?? "<NO OLD TEXT>"));
            }
            return sb.ToString();
        }

        // writer thread
        internal static void CheckLine(string written, string? expected)
        {
            Interlocked.Increment(ref lineChecks);
            if (expected != null && !string.Equals(written, expected, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref lineMismatches);
                Sample("LINE wrote [" + written + "] old [" + expected + "]");
            }
        }

        // ---- ticks ----

        private static readonly List<int> expectedDue = new List<int>();
        private static readonly List<int> actualDue = new List<int>();

        // the old Update pass: each object counted its own timer (objects older than this frame, not destroyed, enabled;
        // a unit other than a building stopped counting while disabled)
        internal static void OldTimers(List<ACMIObject_mono> objects, int frame, float dt)
        {
            expectedDue.Clear();
            for (int i = 0; i < objects.Count; i++)
            {
                ACMIObject_mono o = objects[i];
                if (o == null || !o.enabled || o.createdFrame == frame)
                {
                    continue;
                }
                int rate = o.TickRate;
                if (rate <= 0)
                {
                    continue;
                }
                if (o is ACMIUnit_mono u && !(u is ACMIBuilding_mono) && (u.unit is null || u.unit.disabled))
                {
                    continue;
                }
                o.shadowTimer += dt;
                if (o.shadowTimer >= Recorder_mono.DeltaOf(rate))
                {
                    o.shadowTimer = 0f;
                    expectedDue.Add(o.seq);
                }
            }
        }

        // the new due list, the objects that do their work (a disabled unit returns straight away, as before)
        internal static void CompareTicks(List<ACMIObject_mono> due)
        {
            tickFrames++;
            actualDue.Clear();
            for (int i = 0; i < due.Count; i++)
            {
                ACMIObject_mono o = due[i];
                if (o.TickRate <= 0)
                {
                    continue;
                }
                if (o is ACMIUnit_mono u && !(u is ACMIBuilding_mono) && (u.unit is null || u.unit.disabled))
                {
                    continue;
                }
                actualDue.Add(o.seq);
            }
            objectTicks += actualDue.Count;
            bool same = expectedDue.Count == actualDue.Count;
            for (int i = 0; same && i < actualDue.Count; i++)
            {
                same = expectedDue[i] == actualDue[i];
            }
            if (!same)
            {
                tickMismatches++;
                Sample("TICKS frame " + Time.frameCount + " old " + expectedDue.Count + " [" + string.Join(",", expectedDue.GetRange(0, Math.Min(12, expectedDue.Count)))
                    + "] new " + actualDue.Count + " [" + string.Join(",", actualDue.GetRange(0, Math.Min(12, actualDue.Count))) + "]");
            }
        }

        // ---- disabled ----

        internal static void CheckDisabled(List<ACMIObject_mono> objects, Recorder_mono recorder)
        {
            for (int i = 0; i < objects.Count; i++)
            {
                if (objects[i] is ACMIUnit_mono u && u != null && u.enabled && u.WatchesDisabled && !u.lateQueued
                    && !(u.unit is null) && u.unit.disabled)
                {
                    disabledMissed++;
                    Sample("DISABLED not reported by the hook: " + Describe(u.unit));
                    recorder.NoteDisabled(u);
                }
            }
        }

        internal static void SweepCaught(ACMIUnit_mono u)
        {
            sweepCaught++;
            Sample("SWEEP caught a disabled unit: " + (u.unit is null ? "(no unit)" : Describe(u.unit)));
        }

        // ---- units ----

        internal static void CompareUnits(List<Unit> units, Dictionary<long, GameObject> unitObjects, bool hookMode)
        {
            if (!hookMode)
            {
                return;
            }
            unitScans++;
            HashSet<Unit> oldSet = new HashSet<Unit>();
            foreach (Unit u in UnityEngine.Object.FindObjectsByType<Unit>(FindObjectsSortMode.None))
            {
                if (Wanted(u, unitObjects))
                {
                    oldSet.Add(u);
                }
            }
            HashSet<Unit> newSet = new HashSet<Unit>();
            foreach (Unit u in units)
            {
                if (Wanted(u, unitObjects))
                {
                    newSet.Add(u);
                }
            }
            foreach (Unit u in oldSet)
            {
                if (!newSet.Contains(u))
                {
                    unitOnlyOld++;
                    Sample("UNIT only in the scene search: " + Describe(u));
                }
            }
            foreach (Unit u in newSet)
            {
                if (!oldSet.Contains(u))
                {
                    unitOnlyNew++;
                    Sample("UNIT only in the game's list: " + Describe(u));
                }
            }
        }

        // what ProcessDiscovered would record
        private static bool Wanted(Unit u, Dictionary<long, GameObject> unitObjects)
        {
            if (u == null || !u.networked || (u.disabled && u.GetType() != typeof(Missile)) || unitObjects.ContainsKey(u.persistentID.Id))
            {
                return false;
            }
            return u is Aircraft || u is Missile || u is GroundVehicle || u is Ship || u is Building || u is Scenery
                || (u is PilotDismounted && Configuration.RecordEjectedPilots.Value);
        }

        private static string Describe(Unit u)
        {
            try
            {
                return u.GetType().Name + " " + u.persistentID.Id + " '" + u.definition?.unitName + "' disabled=" + u.disabled
                    + " networked=" + u.networked + " active=" + u.gameObject.activeInHierarchy
                    + " inList=" + UnitRegistry.allUnits.Contains(u);
            }
            catch (Exception e)
            {
                return u.GetType().Name + " (" + e.GetType().Name + ")";
            }
        }

        // ---- waves ----

        private static readonly List<Shockwave> waveSuspects = new List<Shockwave>();

        internal static void CompareWaves(Dictionary<Shockwave, GameObject> waves, List<Shockwave> pending, bool hookMode)
        {
            if (!hookMode)
            {
                return;
            }
            waveScans++;
            // found by the last search and not recorded then (it may have been made that frame, before its Start):
            // half a second later the hook must have recorded it
            foreach (Shockwave w in waveSuspects)
            {
                if (w != null && !waves.ContainsKey(w) && w.gameObject.activeInHierarchy)
                {
                    waveMissed++;
                    Sample("WAVE found by the scene search, not recorded by the hook: " + w.name + " pending=" + pending.Contains(w));
                }
            }
            waveSuspects.Clear();
            foreach (Shockwave w in UnityEngine.Object.FindObjectsByType<Shockwave>(FindObjectsSortMode.None))
            {
                if (!waves.ContainsKey(w))
                {
                    waveSuspects.Add(w);
                }
            }
        }

        // ---- report ----

        internal static void MaybeReport()
        {
            float now = Time.realtimeSinceStartup;
            if (windowStart < 0f)
            {
                windowStart = now;
                return;
            }
            if (now - windowStart < 60f)
            {
                return;
            }
            windowStart = now;
            log ??= BepInEx.Logging.Logger.CreateLogSource("Shadow");
            log.LogInfo("[Shadow] totals since start | lines checked " + Interlocked.Read(ref lineChecks) + " different "
                + Interlocked.Read(ref lineMismatches) + " | tick frames " + tickFrames + " object updates " + objectTicks
                + " frames different " + tickMismatches + " | disabled missed by the hook " + disabledMissed
                + " caught by the 1 s sweep " + sweepCaught + " | unit scans " + unitScans + " only in scene search "
                + unitOnlyOld + " only in game list " + unitOnlyNew + " | wave scans " + waveScans + " missed " + waveMissed);
            while (samples.TryDequeue(out string? s))
            {
                log.LogInfo("[Shadow]   " + s);
            }
        }
    }
}
#endif
