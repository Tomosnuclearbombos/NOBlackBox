using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace NOBlackBox
{
    // Starts each gun round's track where and when it is fired. The bullet discovery in Recorder_mono only looks every
    // "BulletSim Discovery Rate" (0.2 s), by which time a round can be ~200 m out from the gun.
    [HarmonyPatch(typeof(BulletSim), nameof(BulletSim.AddBullet))]
    internal static class BulletFiredPatch
    {
        // applied: Plugin.Awake patched AddBullet. failed: the hook has thrown at least once, so a round may have been
        // missed and the bullet discovery goes back to its configured rate.
        internal static bool applied = false;
        internal static bool failed = false;

        private static void Postfix(BulletSim __instance, Transform muzzle, List<BulletSim.Bullet> ___bullets)
        {
            // this runs inside the game's gun code: a recorder problem must never stop a gun firing
            try
            {
                if (___bullets.Count == 0)
                {
                    return;
                }
                Recorder_mono? recorder = Recorder_mono.Current;
                if (recorder == null || !recorder.enabled)
                {
                    return;
                }
                // AddBullet appends the new round to the end of the list
                GlobalPosition? firedFrom = muzzle != null ? muzzle.position.ToGlobalPosition() : null;
                recorder.AddTracer(__instance, ___bullets[___bullets.Count - 1], firedFrom);
            }
            catch (Exception e)
            {
                if (!failed)
                {
                    failed = true;
                    Plugin.Logger?.LogWarning($"Gun round fire hook failed (logged once): {e}");
                }
            }
        }
    }
}
