#if RECPROF
using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using NOBlackBox.src.ACMI_mono;
using UnityEngine;

namespace NOBlackBox
{
    // Timing-only build (2026-10-12, test server only, never deployed): where the recorder's main-thread time goes.
    // Every frame the whole Update and LateUpdate are timed, and so is the discovery. On one frame in Every the passes,
    // each object type and the parts of a unit's tick are timed as well (scaled up in the report). One line a minute in
    // the BepInEx log.
    internal static class RecProf
    {
        internal const int Every = 4;
        internal static bool On;

        internal const int UPD = 0, LATE = 1, U_REMOVE = 2, U_DISCOVER = 3;
        internal const int T_BASE = 4;          // + kind: Tick per object type
        internal const int F_TICK = 12, D_UNITS = 13, D_WAVES = 14, D_BULLETS = 15;
        internal const int L_BASE = 16;         // + kind: LateTick per object type
        internal const int F_LATE = 24, TR_LATE = 25, TR_REMOVE = 26;
        internal const int P_READ = 27, P_FMT = 28, A_PROPS = 29, A_TARGETS = 30, STATE = 31, WRITER = 32, M_PROPS = 33;
        internal const int N = 34;
        // the always-timed buckets; the rest are sampled
        private static readonly bool[] always = new bool[N];
        private static readonly string[] kinds = { "air", "gnd", "ship", "msl", "bldg", "pilot", "scen", "wave" };

        private static readonly long[] ticks = new long[N];
        private static readonly long[] counts = new long[N];
        private static long frames, sampled, lines, chars;
        private static float windowStart = -1f;
        private static int gcStart;
        private static BepInEx.Logging.ManualLogSource? log;

        static RecProf()
        {
            always[UPD] = always[LATE] = always[U_DISCOVER] = true;
            always[D_UNITS] = always[D_WAVES] = always[D_BULLETS] = true;
        }

        internal static long Now() => Stopwatch.GetTimestamp();

        internal static void Add(int b, long t0)
        {
            ticks[b] += Stopwatch.GetTimestamp() - t0;
            counts[b]++;
        }

        internal static void Line(int length)
        {
            lines++;
            chars += length;
        }

        internal static int KindOf(ACMIObject_mono o)
        {
            if (o.profKind < 0)
            {
                o.profKind = o is ACMIAircraft_mono ? 0 : o is ACMIGroundVehicle_mono ? 1 : o is ACMIShip_mono ? 2
                    : o is ACMIMissile_mono ? 3 : o is ACMIBuilding_mono ? 4 : o is ACMIPilotDismounted_mono ? 5
                    : o is ACMIScenery_mono ? 6 : 7;
            }
            return o.profKind;
        }

        internal static void BeginFrame()
        {
            if (windowStart < 0f)
            {
                windowStart = Time.realtimeSinceStartup;
                gcStart = GC.CollectionCount(0);
            }
            frames++;
            On = Time.frameCount % Every == 0;
            if (On)
            {
                sampled++;
            }
        }

        internal static void MaybeReport(System.Collections.Generic.List<ACMIObject_mono> objects, int flares, int tracers)
        {
            float now = Time.realtimeSinceStartup;
            float secs = now - windowStart;
            if (secs < 60f || sampled == 0)
            {
                return;
            }
            int[] alive = new int[kinds.Length];
            foreach (ACMIObject_mono o in objects)
            {
                if (o != null)
                {
                    alive[KindOf(o)]++;
                }
            }
            double scale = (double)frames / sampled;
            double MsPerSec(int b) => ticks[b] * 1000.0 / Stopwatch.Frequency * (always[b] ? 1.0 : scale) / secs;
            double PerSec(int b) => counts[b] * (always[b] ? 1.0 : scale) / secs;
            string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
            StringBuilder sb = new StringBuilder(1024);
            sb.Append("[RecProf] ").Append(F(secs)).Append(" s, ").Append(frames).Append(" frames (")
              .Append(sampled).Append(" timed in detail) | ms/s: total ").Append(F(MsPerSec(UPD) + MsPerSec(LATE)))
              .Append(" update ").Append(F(MsPerSec(UPD))).Append(" late ").Append(F(MsPerSec(LATE)))
              .Append(" | alive");
            for (int k = 0; k < kinds.Length; k++)
            {
                sb.Append(' ').Append(kinds[k]).Append(' ').Append(alive[k]);
            }
            sb.Append(" flare ").Append(flares).Append(" round ").Append(tracers);
            sb.Append(" | schedule ").Append(F(MsPerSec(U_REMOVE))).Append(" processDiscovered ").Append(F(MsPerSec(U_DISCOVER)));
            sb.Append(" | tick");
            for (int k = 0; k < kinds.Length; k++)
            {
                sb.Append(' ').Append(kinds[k]).Append(' ').Append(F(MsPerSec(T_BASE + k)));
            }
            sb.Append(" flare ").Append(F(MsPerSec(F_TICK)));
            sb.Append(" | lateTick");
            for (int k = 0; k < kinds.Length; k++)
            {
                sb.Append(' ').Append(kinds[k]).Append(' ').Append(F(MsPerSec(L_BASE + k)));
            }
            sb.Append(" flare ").Append(F(MsPerSec(F_LATE))).Append(" round ").Append(F(MsPerSec(TR_LATE)))
              .Append(" roundRemove ").Append(F(MsPerSec(TR_REMOVE)));
            sb.Append(" | find units ").Append(F(MsPerSec(D_UNITS))).Append(" waves ").Append(F(MsPerSec(D_WAVES)))
              .Append(" bullets ").Append(F(MsPerSec(D_BULLETS)));
            sb.Append(" | inside ticks: poseRead ").Append(F(MsPerSec(P_READ))).Append(" (").Append(F(PerSec(P_READ))).Append("/s)")
              .Append(" poseText ").Append(F(MsPerSec(P_FMT))).Append(" (").Append(F(PerSec(P_FMT))).Append("/s)")
              .Append(" airProps ").Append(F(MsPerSec(A_PROPS))).Append(" airTargets ").Append(F(MsPerSec(A_TARGETS)))
              .Append(" mslProps ").Append(F(MsPerSec(M_PROPS))).Append(" state ").Append(F(MsPerSec(STATE)))
              .Append(" writer ").Append(F(MsPerSec(WRITER))).Append(" (").Append(F(PerSec(WRITER))).Append("/s)");
            sb.Append(" | lines/s ").Append(F(lines / secs)).Append(" chars/s ").Append(F(chars / secs))
              .Append(" | gen0 GCs ").Append(GC.CollectionCount(0) - gcStart);
            // its own log source: Plugin.Logger is null unless the recorder's EnableLogging is on (off on the servers)
            (log ??= BepInEx.Logging.Logger.CreateLogSource("RecProf")).LogInfo(sb.ToString());

            Array.Clear(ticks, 0, N);
            Array.Clear(counts, 0, N);
            frames = sampled = lines = chars = 0;
            windowStart = now;
            gcStart = GC.CollectionCount(0);
        }
    }
}
#endif
