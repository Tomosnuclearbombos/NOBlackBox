using System;
using System.Globalization;
using System.Threading;
using UnityEngine;

namespace NOBlackBox
{
    // Tomo's Nukes build (2026-10-14): every warhead going off, written as a short explosion. The game only makes a
    // Shockwave (ACMIShockwave_mono) for warheads of about 45 kg and up, so before this most missiles, bombs and guided
    // shells (air-to-air and SAM kills, AGM-48s, 76/127/155 mm guided rounds) left no explosion in the recording.
    // DiscoveryPatches hooks the missile's detonation, Recorder_mono makes one of these the next Update, and it is removed
    // about 0.1 s later. The big ones get both this and their shockwave; a viewer can match the two.
    internal class ACMIDetonation_mono : ACMIObject_mono
    {
        private static int DETONATIONID = 0;
        private const float Lifetime = 0.1f;
        private float age = 0f;
        private bool done = false;

        // what the hook saw, in global coordinates (the floating origin can move before the next Update)
        internal readonly struct Pending
        {
            internal readonly float x, y, z, radius;
            internal readonly string name;
            internal readonly bool armed;

            internal Pending(float x, float y, float z, string name, float radius, bool armed)
            {
                this.x = x; this.y = y; this.z = z; this.name = name; this.radius = radius; this.armed = armed;
            }
        }

        // 13 x cube root of the yield in kg: the radius the game's own shockwaves record
        internal static float RadiusFor(float yieldKg)
        {
            return yieldKg > 0f ? MathF.Round(13f * MathF.Pow(yieldKg, 1f / 3f), 2) : 0f;
        }

        public void Init(Pending d)
        {
            base.unitId = (long)(Interlocked.Increment(ref DETONATIONID) - 1) | (1L << 35);   // shockwaves use 1L << 34
            base.tacviewId = unitId;
            (float lat, float lon) = Helpers.CartesianToGeodetic(d.x, d.z);
            props = new PropBag()
            {
                { "T", $"{
                    lon.ToString(CultureInfo.InvariantCulture)
                }|{
                    lat.ToString(CultureInfo.InvariantCulture)
                }|{
                    d.y.ToString("0.##", CultureInfo.InvariantCulture)
                }|{
                    d.x.ToString("0.##", CultureInfo.InvariantCulture)
                }|{
                    d.z.ToString("0.##", CultureInfo.InvariantCulture)
                }" },
                { "Type", "Misc+Explosion" },
                { "Name", d.name },
                { "Armed", d.armed ? "1" : "0" }   // 0 = it fizzled: shot down or a dud
            };
            // the radius goes out with the first line, never after the removal (a Radius line after the removal
            // brings the removed object back, see ACMIShockwave_mono)
            Num("Radius", d.radius, PropBag.Num2, CultureInfo.InvariantCulture);
            Recorder_mono.Current!.invokeWriterUpdate(this);
            props.Clear();
        }

        internal override int TickRate => 0;

        public override void Tick()
        {
            if (done)
            {
                return;
            }
            age += Time.deltaTime;
            if (age < Lifetime)
            {
                return;
            }
            done = true;
            try
            {
                Recorder_mono.Current?.invokeWriterRemove(this);
            }
            finally
            {
                this.enabled = false;
                props.Clear();
                GameObject.Destroy(gameObject);
            }
        }
    }
}
