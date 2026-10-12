using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace NOBlackBox
{
    // What the writer needs from anything it records: units and shockwaves (ACMIObject_mono) and the plain gun round and
    // flare objects (ACMITracer, ACMIFlare).
    internal interface IACMIObject
    {
        long TacviewId { get; }
        PropBag Props { get; }
        bool DestroyedEvent { get; }
    }

    // Tomo's Nukes performance build (2026-10-06): Unity no longer calls anything on these components. Recorder_mono ticks
    // them itself (Tick in its Update, LateTick in its LateUpdate) in the same frames Unity used to call Update and
    // LateUpdate. That was three Unity calls per recorded object per frame (Update, LateUpdate and an empty FixedUpdate),
    // for 2,000 to 3,800 objects. "enabled" is a plain field now: it is only read by the recorder's tick.
    internal class ACMIObject_mono : MonoBehaviour, IACMIObject
    {
        public long unitId;
        public long tacviewId;
        internal float timer = 0f;
        internal float fx, fy, fz, fax, fay, faz;
        internal bool disabled = false;
        internal bool destroyedEvent = false;
        internal new bool enabled = true;
#if RECPROF
        internal int profKind = -1;   // RecProf (timing-only build)
#endif
        // 2026-10-12: the recorder's order of creation (lines in one frame keep the old object order), the frame it was
        // created in (it gets its first tick the next frame, as before), and whether it waits in the disabled queue
        internal int seq;
        internal int createdFrame;
        internal bool lateQueued;
#if SHADOW
        internal float shadowTimer;   // check build: the old per-object timer
#endif

        public event Action<string, long[], string>? OnEvent;

        public PropBag props = new PropBag();

        long IACMIObject.TacviewId => tacviewId;
        PropBag IACMIObject.Props => props;
        bool IACMIObject.DestroyedEvent => destroyedEvent;

        public virtual void Init()
        {

        }

        public virtual void Tick()
        {

        }

        // 2026-10-12: the recorder calls this only when the object's update is due (it keeps the timer), so most objects
        // are no longer touched every frame. What used to run before the timer check runs here first, then the work.
        internal virtual void DueTick()
        {

        }

        // 2026-10-12: a number the writer thread turns into text, value.ToString("0.##" or "0.###", culture) as before
        internal void Num(string key, float value, byte kind, CultureInfo culture)
        {
            props.AddNum(key, value, kind, culture);
#if SHADOW
            Shadow.Expect(props, value.ToString(kind == PropBag.Num3 ? "0.###" : "0.##", culture));
#endif
        }

        // which update rate the recorder schedules it on: -1 none (nothing to tick), 0 every frame, else a Rate* value
        internal virtual int TickRate => -1;
        internal const int RateAircraft = 1, RateVehicle = 2, RateMunition = 3, RateBuilding = 4;

        public virtual void LateTick()
        {

        }
    }
}
