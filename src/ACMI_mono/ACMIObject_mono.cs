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
        Dictionary<string, string> Props { get; }
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

        public event Action<string, long[], string>? OnEvent;

        public Dictionary<string, string> props = [];

        long IACMIObject.TacviewId => tacviewId;
        Dictionary<string, string> IACMIObject.Props => props;
        bool IACMIObject.DestroyedEvent => destroyedEvent;

        public virtual void Init()
        {

        }

        public virtual void Tick()
        {

        }

        public virtual void LateTick()
        {

        }
    }
}
