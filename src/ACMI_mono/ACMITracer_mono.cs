using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using UnityEngine;

namespace NOBlackBox
{
    internal class ACMITracer_mono : ACMIObject_mono
    {
        private static int BULLETID = 0;

        public BulletSim sim;
        public BulletSim.Bullet bullet;

        private Vector3 lastPos = new(float.NaN, float.NaN, float.NaN);

        // A round gets two positions: where it was fired from and where it ended. Tacview draws the line between them,
        // so there are no updates in flight. firedFrom comes from BulletFiredPatch; a round it missed starts where the
        // bullet discovery found it.
        public virtual void Init(BulletSim sim, BulletSim.Bullet bullet, GlobalPosition? firedFrom = null)
        {
            this.sim = sim;
            this.bullet = bullet;
            base.unitId = (long)(Interlocked.Increment(ref BULLETID) - 1) | (1L << 33);
            base.tacviewId = base.unitId;
            props = new Dictionary<string, string>()
            {
                { "Type", "Misc+Projectile+Shell" }
            };
            UpdatePose(firedFrom ?? bullet.position);

            Plugin.recorderMono.GetComponent<Recorder_mono>().invokeWriterUpdate(this);
            props = [];
        }

        public virtual void LateUpdate()
        {
            // Bullet.tracer is only the visual and is always null on a dedicated server (AddBullet skips it when headless).
            // The game sets active = false when the round hits something or self destructs, with position where it ended.
            if (sim == null || !bullet.active)
            {
                UpdatePose(bullet.position);
                Plugin.recorderMono.GetComponent<Recorder_mono>().invokeWriterUpdate(this);
                props = [];
                this.enabled = false;
                base.enabled = false;
                Plugin.recorderMono.GetComponent<Recorder_mono>().invokeWriterRemove(this);
                Plugin.Logger?.LogDebug($"DISABLING TRACER {unitId.ToString(CultureInfo.InvariantCulture)}");
                props = [];
                GameObject.Destroy(this);
            }
        }

        public void UpdatePose(GlobalPosition position)
        {

            float fx = MathF.Round(position.x, 2);
            float fy = MathF.Round(position.y, 2);
            float fz = MathF.Round(position.z, 2);

            Vector3 newPos = new(fx, fy, fz);

            if (newPos != lastPos)
            {
                props.Add("T", UpdatePosition(newPos).ToString(CultureInfo.InvariantCulture));

                lastPos = newPos;
            }

        }

        private string UpdatePosition(Vector3 newPos)
        {
            string x = Mathf.Approximately(newPos.x, lastPos.x) ? "" : newPos.x.ToString(CultureInfo.InvariantCulture);
            string y = Mathf.Approximately(newPos.y, lastPos.y) ? "" : newPos.y.ToString(CultureInfo.InvariantCulture);
            string z = Mathf.Approximately(newPos.z, lastPos.z) ? "" : newPos.z.ToString(CultureInfo.InvariantCulture);

            (float latitude, float longitude) = Helpers.CartesianToGeodetic(newPos.x, newPos.z);

            return $"{(newPos.x != lastPos.x ? longitude.ToString(CultureInfo.InvariantCulture) : string.Empty)}|{(newPos.z != lastPos.z ? latitude.ToString(CultureInfo.InvariantCulture) : string.Empty)}|{y}|{x}|{z}";
        }
    }
}
