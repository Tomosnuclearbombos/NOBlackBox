using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEngine;

namespace NOBlackBox
{
    internal class ACMIShockwave_mono : ACMIObject_mono
    {
        private static int SHOCKWAVEID = 0;
        private static readonly FieldInfo propagation = typeof(Shockwave).GetField("blastPropagation", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo blastRadiusField = typeof(Shockwave).GetField("blastRadius", BindingFlags.NonPublic | BindingFlags.Instance);

        private float blastRadius = 0f;

        public Shockwave shockwave;
        private float lastRadius = 0f;
        private float waveSpeed = 0f;

        private float shockWaveTimer = 0f;

        public virtual void Init(Shockwave shockwave)
        {
            Vector3 pos = shockwave.transform.position;
            this.shockwave = shockwave;
            blastRadius = (float)blastRadiusField.GetValue(shockwave);
            base.unitId = (long)(Interlocked.Increment(ref SHOCKWAVEID) - 1) | (1L << 34);
            base.tacviewId = unitId;
            (float lat, float lon) = Helpers.CartesianToGeodetic(pos.GlobalX(), pos.GlobalZ());
            props = new Dictionary<string, string>()
            {
                { "T", $"{
                    lon.ToString(CultureInfo.InvariantCulture)
                }|{
                    lat.ToString(CultureInfo.InvariantCulture)
                }|{
                    pos.GlobalY().ToString("0.##", CultureInfo.InvariantCulture)
                }|{
                    pos.GlobalX().ToString("0.##", CultureInfo.InvariantCulture)
                }|{
                    pos.GlobalZ().ToString("0.##", CultureInfo.InvariantCulture)
                }" },
                { "Type", "Misc+Explosion" }
            };
            Recorder_mono.Current!.invokeWriterUpdate(this);
            props.Clear();
        }

        public override void Tick()
        {
            if (done)
            {
                return;
            }
            try
            {
                shockWaveTimer += Time.deltaTime;
                timer += Time.deltaTime;
                if (timer < Configuration.shockwaveUpdateDelta.Value)
                {
                    return;
                }
                if (!shockwave || !shockwave.enabled)
                {
                    DisableShockWave();
                    return;
                }
                // 2026-10-11: the game sets blastRadius in Shockwave.Start, so a blast found in the frame it was made
                // reads 0 until then. It waits for the real value instead of ending straight away.
                if (blastRadius <= 0f)
                {
                    blastRadius = (float)blastRadiusField.GetValue(shockwave);
                    if (blastRadius <= 0f)
                    {
                        return;
                    }
                }
                // 2026-10-11: the game's wave keeps growing at 340 m/s for about 10 s after the blast while its dust
                // fades (out to about 3.5 km for a bomb), so the explosion is recorded up to its real blast radius and
                // ends there. The radius is written before the removal, never after it: a Radius line after the
                // removal brought the removed explosion back as a new object.
                float propagated = (float)propagation.GetValue(shockwave);
                float radius = MathF.Round(Math.Min(propagated, blastRadius), 2);
                waveSpeed = (radius - lastRadius) / timer;
                bool stalled = radius <= lastRadius;
                props.Add("Radius", radius.ToString("0.##", CultureInfo.InvariantCulture));
                Recorder_mono.Current!.invokeWriterUpdate(this);
                props.Clear();
                timer = 0f;
                lastRadius = radius;
                Plugin.Logger?.LogDebug($"Shockwave {SHOCKWAVEID.ToString(CultureInfo.InvariantCulture)}Blast Radius: {blastRadius.ToString(CultureInfo.InvariantCulture)}, Current Radius: {radius.ToString(CultureInfo.InvariantCulture)}, Speed: {waveSpeed.ToString(CultureInfo.InvariantCulture)}");
                if (propagated >= blastRadius || stalled || (blastRadius <= 1000 && shockWaveTimer > 3f))
                {
                    DisableShockWave();
                }
            } catch
            {
                DisableShockWave();
            }

        }

        private bool done = false;

        private void DisableShockWave()
        {
            if (done)
            {
                return;
            }
            done = true;
            Recorder_mono.Current!.invokeWriterRemove(this);
            Plugin.Logger?.LogDebug($"DISABLING SHOCKWAVE {unitId.ToString(CultureInfo.InvariantCulture)}");
            this.enabled = false;
            base.enabled = false;
            props.Clear();
            GameObject.Destroy(gameObject);   // 2026-10-09: the whole object, so unitObjects/waves can drop it
        }
    }
}
