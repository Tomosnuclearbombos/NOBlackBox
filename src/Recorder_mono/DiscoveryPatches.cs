using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace NOBlackBox
{
    // Tomo's Nukes performance build (2026-10-12): the game tells the recorder about new units, units whose disabled
    // flag changes and new shockwaves, so it no longer searches the whole scene for them.
    //   UnitRegistry.RegisterUnit: every unit registers here when it starts. With the game's own list of units
    //     (UnitRegistry.allUnits) the discovery finds the units the scene search found, without the search.
    //   Unit.Networkdisabled (setter): every change to a unit's disabled flag goes through it on the server. Only a
    //     disabled unit had anything to do in LateTick, so the recorder visits just those.
    //   Shockwave.Start: an explosion's shockwave sets its blast radius here, the frame after it is created.
    // A hook that cannot be applied, or throws once, puts the recorder back on the scene searches (the old way).
    internal static class DiscoveryPatches
    {
        internal static bool unitsApplied = false;
        internal static bool disabledApplied = false;
        internal static bool wavesApplied = false;
        internal static bool detonationsApplied = false;   // 2026-10-14: every warhead going off (ACMIDetonation_mono)
        private static bool detonationFailed = false;
        internal static bool failed = false;
        private static ManualLogSource? log;

        internal static void Apply(Harmony harmony, ManualLogSource logger)
        {
            log = logger;
            unitsApplied = TryPatch(harmony, AccessTools.Method(typeof(UnitRegistry), nameof(UnitRegistry.RegisterUnit)),
                nameof(Registered), "unit registration");
            // a finaliser, not a postfix: it runs even when a game handler inside the setter (UnitDisabled ->
            // onDisableUnit) throws, which would skip a postfix and leave the death to the 1 s sweep
            disabledApplied = TryPatch(harmony, AccessTools.PropertySetter(typeof(Unit), nameof(Unit.Networkdisabled)),
                nameof(DisabledSet), "unit disabled flag", finalizer: true);
            wavesApplied = TryPatch(harmony, AccessTools.Method(typeof(Shockwave), "Start"), nameof(WaveStarted), "shockwave start");
            // 2026-10-14: Missile.Detonate sends RpcDetonate, whose body (UserCode_RpcDetonate_<hash>) runs on the server
            // too and moves the missile to the blast point first. Found by its name prefix, so a game update that changes
            // the hash still finds it. Missile is not a generic type.
            MethodInfo? detonate = null;
            foreach (MethodInfo m in AccessTools.GetDeclaredMethods(typeof(Missile)))
            {
                if (m.Name.StartsWith("UserCode_RpcDetonate", StringComparison.Ordinal))
                {
                    detonate = m;
                    break;
                }
            }
            detonationsApplied = TryPatch(harmony, detonate, nameof(Detonated), "missile detonation");
        }

        private static bool TryPatch(Harmony harmony, MethodBase? target, string method, string what, bool finalizer = false)
        {
            try
            {
                if (target == null)
                {
                    throw new MissingMethodException(what);
                }
                HarmonyMethod hm = new HarmonyMethod(typeof(DiscoveryPatches), method);
                if (finalizer)
                {
                    harmony.Patch(target, finalizer: hm);
                }
                else
                {
                    harmony.Patch(target, postfix: hm);
                }
                return true;
            }
            catch (Exception e)
            {
                log?.LogWarning($"Recorder hook for the {what} not applied, it searches the scene for them as before: {e.Message}");
                return false;
            }
        }

        // these run inside the game's own code: a recorder problem must never break it
        private static void Registered(Unit unit)
        {
            try
            {
                Recorder_mono.Current?.NoteRegistered(unit);
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        // a finaliser: hands back the game's own exception (if any) untouched
        private static Exception? DisabledSet(Unit __instance, Exception? __exception)
        {
            try
            {
                Recorder_mono.Current?.NoteDisabledChanged(__instance);
            }
            catch (Exception e)
            {
                Fail(e);
            }
            return __exception;
        }

        private static void WaveStarted(Shockwave __instance)
        {
            try
            {
                Recorder_mono.Current?.NoteWave(__instance);
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        // its own failure flag: a problem here only stops the detonation records, never the discovery hooks
        private static void Detonated(Missile __instance, bool armed)
        {
            if (detonationFailed)
            {
                return;
            }
            try
            {
                Recorder_mono.Current?.NoteDetonation(__instance, armed);
            }
            catch (Exception e)
            {
                detonationFailed = true;
                log?.LogWarning($"Recorder detonation hook failed (logged once), detonations are no longer recorded: {e}");
            }
        }

        private static void Fail(Exception e)
        {
            if (!failed)
            {
                failed = true;
                log?.LogWarning($"Recorder hook failed (logged once), back to the scene searches: {e}");
            }
        }
    }
}
