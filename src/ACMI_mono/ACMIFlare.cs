using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using UnityEngine;
using static BulletSim;

namespace NOBlackBox
{
    // One flare. Tomo's Nukes performance build (2026-10-06): a plain object ticked by Recorder_mono instead of a new
    // GameObject plus component per flare. Same lines, same frames: Tick runs where Unity called Update and LateTick where
    // it called LateUpdate, with Unity's rules for a component added part way through a frame and for Destroy.
    internal class ACMIFlare : IACMIObject
    {
        private static int FLAREID = 0;

        public long unitId;
        public long tacviewId;
        internal float timer = 0f;
        internal bool enabled = true;
        public PropBag props = new PropBag(2);   // 2026-10-12: one small store per flare, reused

        // Unity gave a component added during the physics step its first Update in that same frame, and one added
        // during the Update pass its first Update in the next frame. A destroyed component got no calls from the next
        // frame on.
        internal readonly int createdFrame = Time.frameCount;
        internal readonly bool createdInPhysics = Time.inFixedTimeStep;
        internal int destroyedFrame = int.MaxValue;

        private Vector3 lastPos = new(float.NaN, float.NaN, float.NaN);

        public IRFlare flare;

        long IACMIObject.TacviewId => tacviewId;
        PropBag IACMIObject.Props => props;
        bool IACMIObject.DestroyedEvent => false;

        internal bool Gone => destroyedFrame < Time.frameCount;

        internal bool TicksThisFrame => createdFrame < Time.frameCount || createdInPhysics;

        public virtual void Init(IRSource source)
        {
            this.flare = source.transform.gameObject.GetComponent<IRFlare>();
            unitId = (long)(Interlocked.Increment(ref FLAREID) - 1) | (1L << 32);
            tacviewId = unitId;
            props.Clear();
            props.Add("Type", "Misc+Decoy+Flare");
            try
            {
                Recorder_mono.Current!.invokeWriterUpdate(this);
            } catch
            {
                Destroy();
            }

            props.Clear();
        }

        public virtual void Tick()
        {
            timer += Time.deltaTime;
            if (timer < Configuration.flareUpdateDelta.Value)
            {
                return;
            }
            UpdatePose();
            Recorder_mono.Current!.invokeWriterUpdate(this);
            props.Clear();
            timer = 0;
        }

        public virtual void LateTick()
        {
            // 2026-10-12: the position read that was here compared a Vector3 with null, which is never true
            if  (flare == null || !flare.enabled)
            {
                DisableFlare();
            }
        }

        public void UpdatePose()
        {
            try
            {
                float fx = MathF.Round(flare.transform.position.GlobalX(), 2);
                float fy = MathF.Round(flare.transform.position.GlobalY(), 2);
                float fz = MathF.Round(flare.transform.position.GlobalZ(), 2);

                Vector3 newPos = new(fx, fy, fz);

                if (newPos != lastPos)
                {
                    // the text is built on the writer thread (PropBag / PoseText.Point)
                props.AddPose5("T", newPos, lastPos);
#if SHADOW
                Shadow.Expect(props, UpdatePosition(newPos).ToString(CultureInfo.InvariantCulture));
#endif

                    lastPos = newPos;
                }
            } catch
            {
                DisableFlare();
            }

        }

#if SHADOW
        // the old main-thread text, kept in the check build to prove the writer thread writes the same
        private string UpdatePosition(Vector3 newPos)
        {
            string x = Mathf.Approximately(newPos.x, lastPos.x) ? "" : newPos.x.ToString(CultureInfo.InvariantCulture);
            string y = Mathf.Approximately(newPos.y, lastPos.y) ? "" : newPos.y.ToString(CultureInfo.InvariantCulture);
            string z = Mathf.Approximately(newPos.z, lastPos.z) ? "" : newPos.z.ToString(CultureInfo.InvariantCulture);

            (float latitude, float longitude) = Helpers.CartesianToGeodetic(newPos.x, newPos.z);

            return $"{(newPos.x != lastPos.x ? longitude.ToString(CultureInfo.InvariantCulture) : string.Empty)}|{(newPos.z != lastPos.z ? latitude.ToString(CultureInfo.InvariantCulture) : string.Empty)}|{y}|{x}|{z}";
        }
#endif

        private void DisableFlare()
        {
            enabled = false;
            Recorder_mono.Current!.invokeWriterRemove(this);
            Plugin.Logger?.LogDebug($"DISABLING FLARE {unitId.ToString(CultureInfo.InvariantCulture)}");
            props.Clear();
            Destroy();
        }

        private void Destroy()
        {
            if (destroyedFrame == int.MaxValue)
            {
                destroyedFrame = Time.frameCount;
            }
        }
    }
}
