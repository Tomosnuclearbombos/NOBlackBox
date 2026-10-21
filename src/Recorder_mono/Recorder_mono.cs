using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using NOBlackBox.src.ACMI_mono;
using NuclearOption.Networking;
using UnityEngine;

namespace NOBlackBox
{
    internal class Recorder_mono : MonoBehaviour
    {
        private static readonly FieldInfo bulletSim = typeof(Gun).GetField("bulletSim", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo bullets = typeof(BulletSim).GetField("bullets", BindingFlags.NonPublic | BindingFlags.Instance);

        // While the fire hook catches every round, the bullet discovery only looks this often, as a fallback. It goes
        // back to the configured rate if the hook is not applied or has ever failed.
        private const float bulletSimFallbackDelta = 10f;

        // The recorder that is running. Replaces Plugin.recorderMono.GetComponent<Recorder_mono>() on every write.
        internal static Recorder_mono? Current;

        private Dictionary<Shockwave, GameObject> waves = [];
        Shockwave[] shockwaves = [];

        private DateTime startDate;
        private DateTime curTime;
        public static ACMIWriter writer;
        private float unitDiscoveryTimer = 0f;
        private float bulletSimDiscoveryTimer = 0f;
        private float shockwaveDiscoveryTimer = 0f;

        internal Dictionary<long, GameObject> unitObjects = [];
        // 2026-10-12: the recorded component for each unitObjects entry (same keys), for the target lookups and the
        // disabled hook, which used GetComponent before
        private Dictionary<long, ACMIUnit_mono> unitAcmi = [];

        // Tomo's Nukes performance build (2026-10-06): everything recorded is ticked from here, in creation order, instead
        // of Unity calling Update and LateUpdate on each object. Units and shockwaves are still components (Destroy and the
        // unitObjects lookups behave as before); gun rounds and flares are plain objects.
        private List<ACMIObject_mono> objects = [];
        private List<ACMIFlare> flares = [];
        private List<ACMITracer> liveTracers = [];

        // 2026-10-12: the update timers. Every recorded object counted Time.deltaTime up to its update rate itself, every
        // frame. Objects created in the same frame with the same rate count exactly the same, so they share one timer
        // (a group) and the recorder only calls the ones whose update is due. Same frames, same order.
        private sealed class TickGroup
        {
            internal int rate;
            internal int startFrame;
            internal float timer;
            internal readonly List<ACMIObject_mono> members = [];
        }
        private readonly List<TickGroup> groups = [];
        private readonly TickGroup?[] openGroups = new TickGroup?[5];
        private int openFrame = -1;
        private readonly List<ACMIObject_mono> everyFrame = [];   // shockwaves: their Tick runs every frame, as before
        private readonly List<ACMIObject_mono> due = [];
        private int nextSeq = 0;
        private static readonly Comparison<ACMIObject_mono> bySeq = (a, b) => a.seq.CompareTo(b.seq);
        private static readonly Comparison<ACMIUnit_mono> unitBySeq = (a, b) => a.seq.CompareTo(b.seq);

        // 2026-10-12: units whose disabled flag is set, waiting for their LateTick (the game's flag setter tells the
        // recorder, DiscoveryPatches). LateTick did nothing for any other unit, and it ran for every object every frame.
        private readonly List<ACMIUnit_mono> lateQueue = [];

        // every round seen this recording (as the old tracers dictionary), so the fallback discovery never adds one twice
        private HashSet<BulletSim.Bullet> tracers = [];

        // 2026-10-12: the units a discovery found, for the next Update to record. Was a scene search for every Unit
        // (FindObjectsByType, 2.5 to 4 ms each, once a second); now the game's own unit list plus the units registered or
        // re-enabled since the last discovery. The scene search stays as the fallback if a hook is missing.
        private List<Unit> units = [];
        private readonly HashSet<Unit> unitSeen = [];
        private float nextFullScan = 0f;   // 2026-10-12: the scene search for units the game's list never holds
        private readonly List<Unit> pendingUnits = [];
        // shockwaves the game started since the last Update (the scene search for them cost 4.5 to 7.5 ms/s)
        private readonly List<Shockwave> pendingWaves = [];
        // 2026-10-14: warheads that went off since the last Update (DiscoveryPatches.Detonated)
        private readonly List<ACMIDetonation_mono.Pending> pendingDetonations = [];
        private BulletSim[] bulletSims = [];
        private bool processUnits = false;
        private bool processBulletSims = false;
        private bool processShockWaves = false;

        private static bool UseUnitHooks => DiscoveryPatches.unitsApplied && DiscoveryPatches.disabledApplied && !DiscoveryPatches.failed;
        // 2026-10-12 (bug sweep): on a client the disabled flag arrives from the network without the setter, so a
        // client recording (the public build) keeps the every-frame check
        private static bool UseDisabledHook => DiscoveryPatches.disabledApplied && !DiscoveryPatches.failed && IsServer;
        private static bool IsServer
        {
            get
            {
                try
                {
                    return NetworkManagerNuclearOption.i != null && NetworkManagerNuclearOption.i.Server.Active;
                }
                catch
                {
                    return false;
                }
            }
        }
        private static bool UseWaveHook => DiscoveryPatches.wavesApplied && !DiscoveryPatches.failed;

        public void invokeWriterUpdate(IACMIObject obj)
        {
#if RECPROF
            long t0 = RecProf.On ? RecProf.Now() : 0;
#endif
            writer.UpdateObject(obj, curTime);
#if RECPROF
            if (RecProf.On) RecProf.Add(RecProf.WRITER, t0);
#endif
        }

        public void invokeWriterRemove(IACMIObject obj)
        {
            writer.RemoveObject(obj, curTime);
        }

        public void invokeWriterDestroy(IACMIObject obj)
        {
            writer.WriteDestroyedEvent(obj, curTime);
        }

        public void invokeWriterRepair(IACMIObject obj)
        {
            writer.WriteRepairedEvent(obj, curTime);
        }

        // one ACMITracer per gun round, from the bullet discovery or from BulletFiredPatch at the moment it is fired
        internal void AddTracer(BulletSim sim, BulletSim.Bullet bullet, GlobalPosition? firedFrom)
        {
            if (tracers.Contains(bullet))
            {
                return;
            }
            ACMITracer tracer = new ACMITracer();
            liveTracers.Add(tracer);
            tracer.Init(sim, bullet, firedFrom);
            tracer.enabled = true;
            tracers.Add(bullet);
        }

        private void AddFlare(IRSource source)
        {
            ACMIFlare flare = new ACMIFlare();
            flares.Add(flare);
            flare.Init(source);
            flare.enabled = true;
        }

        private T Track<T>(T obj) where T : ACMIObject_mono
        {
            obj.seq = ++nextSeq;
            obj.createdFrame = Time.frameCount;
            objects.Add(obj);
            int rate = obj.TickRate;
            if (rate == 0)
            {
                everyFrame.Add(obj);
            }
            else if (rate > 0)
            {
                int frame = Time.frameCount;
                if (openFrame != frame)
                {
                    Array.Clear(openGroups, 0, openGroups.Length);
                    openFrame = frame;
                }
                TickGroup? group = openGroups[rate];
                if (group == null)
                {
                    group = new TickGroup { rate = rate, startFrame = frame };
                    groups.Add(group);
                    openGroups[rate] = group;
                }
                group.members.Add(obj);
            }
            return obj;
        }

        internal static float DeltaOf(int rate)
        {
            switch (rate)
            {
                case ACMIObject_mono.RateAircraft: return Configuration.aircraftUpdateDelta.Value;
                case ACMIObject_mono.RateVehicle: return Configuration.vehicleUpdateDelta.Value;
                case ACMIObject_mono.RateMunition: return Configuration.munitionUpdateDelta.Value;
                default: return Configuration.buildingUpdateDelta.Value;
            }
        }

        // the recorded component for a unit id, or null (GetTacviewIdOfUnit)
        internal ACMIUnit_mono? RecordedUnit(long unitId)
        {
            if (!unitObjects.TryGetValue(unitId, out GameObject go) || go == null)
            {
                return null;
            }
            return unitAcmi.TryGetValue(unitId, out ACMIUnit_mono recorded) && recorded != null ? recorded : null;
        }

        // ---- the game's hooks (DiscoveryPatches) ----

        internal void NoteRegistered(Unit unit)
        {
            if (enabled && UseUnitHooks)   // 2026-10-12: nothing drains the list when the scene search is in use
            {
                pendingUnits.Add(unit);
            }
        }

        internal void NoteDisabledChanged(Unit unit)
        {
            if (!enabled || !UseUnitHooks)
            {
                return;
            }
            if (unit.disabled)
            {
                if (unitAcmi.TryGetValue(unit.persistentID.Id, out ACMIUnit_mono recorded) && !(recorded is null)
                    && ReferenceEquals(recorded.unit, unit))
                {
                    NoteDisabled(recorded);
                }
            }
            else
            {
                // a unit the discovery skipped while it was disabled is a candidate again, as the scene search found it
                pendingUnits.Add(unit);
            }
        }

        internal void NoteDetonation(Missile missile, bool armed)
        {
            if (!enabled || missile == null)
            {
                return;
            }
            Vector3 pos = missile.transform.position;
            string name = missile.definition != null ? missile.definition.unitName : "Warhead";
            pendingDetonations.Add(new ACMIDetonation_mono.Pending(pos.GlobalX(), pos.GlobalY(), pos.GlobalZ(), name,
                ACMIDetonation_mono.RadiusFor(missile.GetYield()), armed));
        }

        internal void NoteWave(Shockwave wave)
        {
            if (enabled && UseWaveHook)
            {
                pendingWaves.Add(wave);
            }
        }

        internal void NoteDisabled(ACMIUnit_mono recorded)
        {
            if (!recorded.lateQueued && recorded.WatchesDisabled)
            {
                recorded.lateQueued = true;
                lateQueue.Add(recorded);
            }
        }

        void Awake()
        {
            Current = this;
            Plugin.Logger?.LogDebug("USING MONO RECORDER");
            if (Configuration.UseMissionTime?.Value == true)
            {
                startDate = DateTime.Today + TimeSpan.FromHours(MissionManager.CurrentMission.environment.timeOfDay);
                Plugin.Logger?.LogDebug("USING MISSION CLOCK");
            }
            else
            {
                startDate = DateTime.Now;
                Plugin.Logger?.LogDebug("USING SERVER CLOCK");
            }

            curTime = startDate;
            writer = new ACMIWriter(startDate);
            Plugin.Logger?.LogDebug("STARTED MONO RECORDER");
            Plugin.recordingManually = false;

            // explosions already going when the recording starts (the hook only sees the ones that start from now on)
            if (UseWaveHook)
            {
                pendingWaves.AddRange(UnityEngine.Object.FindObjectsByType<Shockwave>(FindObjectsSortMode.None));
            }
        }
        void Update()
        {
#if RECPROF
            RecProf.BeginFrame();
            long tUpdate = RecProf.Now();
#endif
            curTime += TimeSpan.FromSeconds(Time.deltaTime);

            // 2026-10-12: the object list is compacted once a second (UnitDiscovery) instead of every frame; everything
            // that walks it skips a destroyed object, as before. Objects created from here on (the units below) get their
            // first tick next frame (Track notes the frame).
            flares.RemoveAll(f => f.Gone);
#if RECPROF
            long t0 = RecProf.Now();
#endif
            ProcessDiscovered();
#if RECPROF
            RecProf.Add(RecProf.U_DISCOVER, t0);
            RecProf.Add(RecProf.UPD, tUpdate);
#endif
        }

        private void ProcessDiscovered()
        {
            if (processUnits)
            {
                foreach (var unit in units)
                {
                    if (!unit.networked || (unit.disabled && unit.GetType() != typeof(Missile)))
                    {
                        continue;
                    }
                    bool isNew = false;
                    if (!unitObjects.TryGetValue(unit.persistentID.Id, out GameObject acmi))
                    {
                        ACMIUnit_mono? recorded = null;
                        switch (unit)
                        {
                            case Aircraft aircraft:

                                aircraft.onAddIRSource += (IRSource source) =>
                                {
                                    if (source.flare)
                                    {
                                        // the recorder running when the flare appears, as the old flare component found it
                                        Current?.AddFlare(source);
                                    }
                                };
                                acmi = new GameObject();
                                ACMIAircraft_mono aircraftObj = Track(acmi.AddComponent<ACMIAircraft_mono>());
                                aircraftObj.Init(aircraft);
                                aircraftObj.enabled = true;
                                Plugin.Logger?.LogDebug($"RECORDED UNIT,{unit.definition.name}," +
                                    $"{unit.definition.unitName}," +
                                    $"{unit.definition.code}");
                                unitObjects.Add(unit.persistentID.Id, acmi);
                                recorded = aircraftObj;
                                isNew = true;
                                break;
                            case Missile missile:
                                acmi = new GameObject();
                                ACMIMissile_mono missileObj = Track(acmi.AddComponent<ACMIMissile_mono>());
                                missileObj.Init(missile);
                                missileObj.enabled = true;
                                Plugin.Logger?.LogDebug($"RECORDED UNIT,{unit.definition.name}," +
                                    $"{unit.definition.unitName}," +
                                    $"{unit.definition.code}");
                                unitObjects.Add(unit.persistentID.Id, acmi);
                                recorded = missileObj;
                                isNew = true;
                                break;
                            case GroundVehicle vehicle:
                                acmi = new GameObject();
                                ACMIGroundVehicle_mono vehicleObj = Track(acmi.AddComponent<ACMIGroundVehicle_mono>());
                                vehicleObj.Init(vehicle);
                                vehicleObj.enabled = true;
                                Plugin.Logger?.LogDebug($"RECORDED UNIT,{unit.definition.name}," +
                                    $"{unit.definition.unitName}," +
                                    $"{unit.definition.code}");
                                unitObjects.Add(unit.persistentID.Id, acmi);
                                recorded = vehicleObj;
                                isNew = true;
                                break;
                            case Ship ship:
                                acmi = new GameObject();
                                ACMIShip_mono shipObj = Track(acmi.AddComponent<ACMIShip_mono>());
                                shipObj.Init(ship);
                                shipObj.enabled = true;
                                Plugin.Logger?.LogDebug($"RECORDED UNIT,{unit.definition.name}," +
                                    $"{unit.definition.unitName}," +
                                    $"{unit.definition.code}");
                                unitObjects.Add(unit.persistentID.Id, acmi);
                                recorded = shipObj;
                                isNew = true;
                                break;
                            case PilotDismounted pilot:
                                if (Configuration.RecordEjectedPilots.Value == true)
                                {
                                    acmi = new GameObject();
                                    ACMIPilotDismounted_mono pilotObj = Track(acmi.AddComponent<ACMIPilotDismounted_mono>());
                                    pilotObj.Init(pilot);
                                    pilotObj.enabled = true;
                                    Plugin.Logger?.LogDebug($"RECORDED UNIT,{unit.definition.name}," +
                                        $"{unit.definition.unitName}," +
                                        $"{unit.definition.code}");
                                    unitObjects.Add(unit.persistentID.Id, acmi);
                                    recorded = pilotObj;
                                    isNew = true;
                                }
                                break;
                            case Building building:
                                acmi = new GameObject();
                                ACMIBuilding_mono buildingObj = Track(acmi.AddComponent<ACMIBuilding_mono>());
                                buildingObj.Init(building);
                                buildingObj.enabled = true;
                                Plugin.Logger?.LogDebug($"RECORDED UNIT,{unit.definition.name}," +
                                    $"{unit.definition.unitName}," +
                                    $"{unit.definition.code}");
                                unitObjects.Add(unit.persistentID.Id, acmi);
                                recorded = buildingObj;
                                isNew = true;
                                break;
                            case Scenery scenery:
                                acmi = new GameObject();
                                ACMIScenery_mono sceneryObj = Track(acmi.AddComponent<ACMIScenery_mono>());
                                sceneryObj.Init(scenery);
                                sceneryObj.enabled = true;
                                Plugin.Logger?.LogDebug($"RECORDED UNIT,{unit.definition.name}," +
                                    $"{unit.definition.unitName}," +
                                    $"{unit.definition.code}");
                                unitObjects.Add(unit.persistentID.Id, acmi);
                                recorded = sceneryObj;
                                isNew = true;
                                break;
                            default:
                                break;
                        }
                        if (recorded != null)
                        {
                            unitAcmi[unit.persistentID.Id] = recorded;
                            // a unit already disabled when it is recorded (a missile that has gone off) gets this frame's
                            // LateTick, as every object did; the hook only reports changes from here on
                            if (unit.disabled)
                            {
                                NoteDisabled(recorded);
                            }
                        }
                        //acmi = new GameObject();
                    }
                }
                processUnits = false;
            }
            if (processBulletSims)
            {
                foreach (var bulletSim in bulletSims)
                {
                    List<BulletSim.Bullet> bullets = (List<BulletSim.Bullet>)Recorder_mono.bullets.GetValue(bulletSim);

                    foreach (var bullet in bullets)
                    {
                        if (bullet != null && bullet.active)     // 2026-10-09: a finished round is not recorded again
                            AddTracer(bulletSim, bullet, null);
                    }
                }
                processBulletSims = false;
            }

            for (int i = 0; i < pendingDetonations.Count; i++)
            {
                GameObject go = new GameObject();
                ACMIDetonation_mono det = Track(go.AddComponent<ACMIDetonation_mono>());
                det.Init(pendingDetonations[i]);
                det.enabled = true;
            }
            pendingDetonations.Clear();

            if (UseWaveHook)
            {
                // 2026-10-12: each shockwave the frame after the game creates it (its Start sets the blast radius),
                // instead of at the next half-second scene search
                int keep = 0;
                for (int i = 0; i < pendingWaves.Count; i++)
                {
                    Shockwave wave = pendingWaves[i];
                    if (wave == null || waves.ContainsKey(wave))
                    {
                        continue;
                    }
                    if (!wave.gameObject.activeInHierarchy)
                    {
                        pendingWaves[keep++] = wave;   // the scene search skipped inactive objects too: wait for it
                        continue;
                    }
                    AddWave(wave);
                }
                pendingWaves.RemoveRange(keep, pendingWaves.Count - keep);
            }
            else if (processShockWaves)
            {
                foreach (Shockwave wave in shockwaves)
                {
                    if (waves.ContainsKey(wave))
                    {
                        continue;
                    }
                    AddWave(wave);
                }
            }
        }

        private void AddWave(Shockwave wave)
        {
            GameObject shockwave = new GameObject();
            ACMIShockwave_mono shockwaveObj = Track(shockwave.AddComponent<ACMIShockwave_mono>());
            shockwaveObj.Init(wave);
            shockwaveObj.enabled = true;
            waves.Add(wave, shockwave);
        }

        void LateUpdate()
        {
#if RECPROF
            long tLate = RecProf.Now();
            bool on = RecProf.On;
            long ts = RecProf.Now();
#endif
            int frame = Time.frameCount;
            float dt = Time.deltaTime;
#if SHADOW
            Shadow.OldTimers(objects, frame, dt);
#endif
            // The Update pass, now only for the objects whose update is due. Each object's Update used to run after its
            // own unit's Update (it is always created after its unit), so running it here, after every game Update of
            // the frame, reads the same values.
            due.Clear();
            int keepGroups = 0;
            for (int gi = 0; gi < groups.Count; gi++)
            {
                TickGroup group = groups[gi];
                if (group.startFrame != frame)
                {
                    group.timer += dt;
                    if (group.timer >= DeltaOf(group.rate))
                    {
                        group.timer = 0f;
                        int keep = 0;
                        for (int mi = 0; mi < group.members.Count; mi++)
                        {
                            ACMIObject_mono member = group.members[mi];
                            if (member == null || !member.enabled)
                            {
                                continue;
                            }
                            group.members[keep++] = member;
                            due.Add(member);
                        }
                        group.members.RemoveRange(keep, group.members.Count - keep);
                    }
                }
                if (group.members.Count > 0)
                {
                    groups[keepGroups++] = group;
                }
            }
            groups.RemoveRange(keepGroups, groups.Count - keepGroups);
            int keepEvery = 0;
            for (int i = 0; i < everyFrame.Count; i++)
            {
                ACMIObject_mono obj = everyFrame[i];
                if (obj == null || !obj.enabled)
                {
                    continue;
                }
                everyFrame[keepEvery++] = obj;
                if (obj.createdFrame != frame)
                {
                    due.Add(obj);
                }
            }
            everyFrame.RemoveRange(keepEvery, everyFrame.Count - keepEvery);
            if (due.Count > 1)
            {
                due.Sort(bySeq);
            }
#if SHADOW
            Shadow.CompareTicks(due);
#endif
#if RECPROF
            if (on) RecProf.Add(RecProf.U_REMOVE, ts);
#endif
            for (int i = 0; i < due.Count; i++)
            {
                ACMIObject_mono obj = due[i];
#if RECPROF
                long t0 = on ? RecProf.Now() : 0;
#endif
                try
                {
                    if (obj.TickRate == 0)
                    {
                        obj.Tick();
                    }
                    else
                    {
                        obj.DueTick();
                    }
                }
                catch (Exception e) { Debug.LogException(e); }
#if RECPROF
                if (on) RecProf.Add(RecProf.T_BASE + RecProf.KindOf(obj), t0);
#endif
            }
            due.Clear();
#if RECPROF
            long tf = on ? RecProf.Now() : 0;
#endif
            for (int i = 0; i < flares.Count; i++)
            {
                ACMIFlare flare = flares[i];
                if (flare.enabled && !flare.Gone && flare.TicksThisFrame)
                {
                    try { flare.Tick(); } catch (Exception e) { Debug.LogException(e); }
                }
            }
#if RECPROF
            if (on) RecProf.Add(RecProf.F_TICK, tf);
#endif

            unitDiscoveryTimer += Time.deltaTime;
            bulletSimDiscoveryTimer += Time.deltaTime;
            shockwaveDiscoveryTimer += Time.deltaTime;
            if (shockwaveDiscoveryTimer >= Configuration.shockwaveDiscoveryDelta.Value)
            {
#if RECPROF
                long td = RecProf.Now();
#endif
                processShockWaves = ShockWaveDiscovery();
#if RECPROF
                RecProf.Add(RecProf.D_WAVES, td);
#endif
            }
            if (unitDiscoveryTimer >= Configuration.unitDiscoveryDelta.Value)
            {
#if RECPROF
                long td = RecProf.Now();
#endif
                processUnits = UnitDiscovery();
#if RECPROF
                RecProf.Add(RecProf.D_UNITS, td);
#endif
            }
            float bulletSimDelta = BulletFiredPatch.applied && !BulletFiredPatch.failed ? bulletSimFallbackDelta : Configuration.bulletSimDiscoveryDelta.Value;
            if (bulletSimDiscoveryTimer >= bulletSimDelta)
            {
#if RECPROF
                long td = RecProf.Now();
#endif
                processBulletSims = BulletSimDiscovery();
#if RECPROF
                RecProf.Add(RecProf.D_BULLETS, td);
#endif
            }

            // The LateTick pass. With the disabled hook only the disabled units are visited, in creation order, as the
            // full pass reached them; without it (hook missing or failed) every object, as before.
            if (UseDisabledHook)
            {
#if SHADOW
                Shadow.CheckDisabled(objects, this);
#endif
                if (lateQueue.Count > 1)
                {
                    lateQueue.Sort(unitBySeq);
                }
                int keep = 0;
                for (int i = 0; i < lateQueue.Count; i++)
                {
                    ACMIUnit_mono obj = lateQueue[i];
                    if (obj == null || !obj.enabled)
                    {
                        obj.lateQueued = false;
                        continue;
                    }
#if RECPROF
                    long t0 = on ? RecProf.Now() : 0;
#endif
                    if (obj.createdFrame != frame)
                    {
                        obj.BeforeDisabledLateTick();   // what the old Tick worked out this frame (it ran for the objects older than this frame)
                    }
                    try { obj.LateTick(); } catch (Exception e) { Debug.LogException(e); }
#if RECPROF
                    if (on) RecProf.Add(RecProf.L_BASE + RecProf.KindOf(obj), t0);
#endif
                    // the unit may be destroyed by now: its disabled flag is still readable, as the old pass read it
                    if (obj != null && obj.enabled && !(obj.unit is null) && obj.unit.disabled)
                    {
                        lateQueue[keep++] = obj;   // its second frame: the removal (Tacview needs them a step apart)
                    }
                    else
                    {
                        obj.lateQueued = false;
                    }
                }
                lateQueue.RemoveRange(keep, lateQueue.Count - keep);
            }
            else
            {
                for (int i = 0; i < lateQueue.Count; i++)
                {
                    lateQueue[i].lateQueued = false;
                }
                lateQueue.Clear();
                for (int i = 0; i < objects.Count; i++)
                {
                    ACMIObject_mono obj = objects[i];
                    if (obj != null && obj.enabled)
                    {
                        if (obj.createdFrame != frame && obj is ACMIUnit_mono recordedUnit)
                        {
                            recordedUnit.BeforeDisabledLateTick();
                        }
                        try { obj.LateTick(); } catch (Exception e) { Debug.LogException(e); }
                    }
                }
            }
#if RECPROF
            tf = on ? RecProf.Now() : 0;
#endif
            for (int i = 0; i < flares.Count; i++)
            {
                ACMIFlare flare = flares[i];
                if (flare.enabled && !flare.Gone)
                {
                    try { flare.LateTick(); } catch (Exception e) { Debug.LogException(e); }
                }
            }
#if RECPROF
            if (on) RecProf.Add(RecProf.F_LATE, tf);
            tf = on ? RecProf.Now() : 0;
#endif
            for (int i = 0; i < liveTracers.Count; i++)
            {
                ACMITracer tracer = liveTracers[i];
                if (tracer.enabled)
                {
                    try { tracer.LateTick(); } catch (Exception e) { Debug.LogException(e); }
                }
            }
#if RECPROF
            if (on) RecProf.Add(RecProf.TR_LATE, tf);
            tf = on ? RecProf.Now() : 0;
#endif
            // 2026-10-09: a finished round leaves the seen-rounds record too (it grew to 105k rounds in one mission)
            liveTracers.RemoveAll(t =>
            {
                if (t.enabled) return false;
                if (t.bullet != null) tracers.Remove(t.bullet);
                return true;
            });
            writer.Signal();   // the frame's lines to the writer thread in one wake-up
#if RECPROF
            if (on) RecProf.Add(RecProf.TR_REMOVE, tf);
            RecProf.Add(RecProf.LATE, tLate);
            RecProf.MaybeReport(objects, flares.Count, liveTracers.Count);
#endif
#if SHADOW
            Shadow.MaybeReport();
#endif
        }
        void OnDisable()
        {
            writer?.Close();
            Plugin.Logger?.LogDebug("DISABLED MONO RECORDER");
        }
        void OnDestroy()
        {
            if (Current == this)
            {
                Current = null;
            }
            unitObjects = [];
            unitAcmi = [];
            waves = [];
            tracers = [];
            objects = [];
            flares = [];
            liveTracers = [];
            groups.Clear();
            everyFrame.Clear();
            lateQueue.Clear();
            pendingUnits.Clear();
            pendingWaves.Clear();
            pendingDetonations.Clear();
            writer?.Close();
            Plugin.Logger?.LogDebug("DESTROYED MONO RECORDER");
        }

        private bool UnitDiscovery()
        {
            // 2026-10-09: collect first, remove after (removing inside the loop threw after the first removal on Mono)
            List<long> deadUnits = null;
            foreach (KeyValuePair<long, GameObject> kv in unitObjects)
            {
                if (null == kv.Value)
                    (deadUnits ??= new List<long>()).Add(kv.Key);
            }
            if (deadUnits != null)
                foreach (long key in deadUnits)
                {
                    unitObjects.Remove(key);
                    unitAcmi.Remove(key);
                }
            objects.RemoveAll(o => o == null);
            //Plugin.Logger?.LogDebug($"Frame TICK at {curTime.ToString(CultureInfo.InvariantCulture)}");
            units.Clear();
            if (UseUnitHooks)
            {
                // the same units the scene search returned (alive, on an active object), minus the ones already
                // recorded, which the recording step skipped anyway
                unitSeen.Clear();
                AddCandidates(UnitRegistry.allUnits);
                AddCandidates(pendingUnits);
                pendingUnits.Clear();
                // 2026-10-12 (bug sweep): a building with hideOnMap never registers (Building.OnStartClient), so the
                // game's list never holds it. One scene search when the recording starts and one a minute (about
                // 4 ms each) finds those; only live ones, so a dead missile left in the scene is not re-recorded.
                if (Time.realtimeSinceStartup >= nextFullScan)
                {
                    nextFullScan = Time.realtimeSinceStartup + 60f;
                    foreach (Unit unit in UnityEngine.Object.FindObjectsByType<Unit>(FindObjectsSortMode.None))
                    {
                        if (unit != null && !unit.disabled && unitSeen.Add(unit) && !unitObjects.ContainsKey(unit.persistentID.Id))
                        {
                            units.Add(unit);
                        }
                    }
                }
            }
            else
            {
                units.AddRange(UnityEngine.Object.FindObjectsByType<Unit>(FindObjectsSortMode.None));
            }
            if (UseDisabledHook)
            {
                // a once-a-second check that no disabled unit was missed (the hook should catch them all; a miss is
                // handled here at most a second late instead of never)
                for (int i = 0; i < objects.Count; i++)
                {
                    if (objects[i] is ACMIUnit_mono recordedUnit && recordedUnit.enabled && !recordedUnit.lateQueued
                        && recordedUnit.WatchesDisabled && (recordedUnit.unit is null || recordedUnit.unit.disabled))
                    {
                        NoteDisabled(recordedUnit);
#if SHADOW
                        Shadow.SweepCaught(recordedUnit);
#endif
                    }
                }
            }
#if SHADOW
            Shadow.CompareUnits(units, unitObjects, UseUnitHooks);
#endif
            //Plugin.Logger?.LogDebug($"DISCOVERED {units.Length.ToString(CultureInfo.InvariantCulture)} UNITS!");
            unitDiscoveryTimer = 0f;
            return true;

        }

        private void AddCandidates(List<Unit> source)
        {
            for (int i = 0; i < source.Count; i++)
            {
                Unit unit = source[i];
                if (unit == null || !unitSeen.Add(unit) || unitObjects.ContainsKey(unit.persistentID.Id))
                {
                    continue;
                }
                if (unit.gameObject.activeInHierarchy)
                {
                    units.Add(unit);
                }
            }
        }

        internal bool BulletSimDiscovery()
        {
            bulletSims = UnityEngine.Object.FindObjectsByType<BulletSim>(FindObjectsSortMode.None);
            //Plugin.Logger?.LogDebug($"DISCOVERED {bulletSims.Length.ToString(CultureInfo.InvariantCulture)} BULLETSIMS!");
            bulletSimDiscoveryTimer = 0f;
            return true;
        }

        public bool ShockWaveDiscovery()
        {
            List<Shockwave> deadWaves = null;
            foreach (KeyValuePair<Shockwave, GameObject> kv in waves)
            {
                // 2026-10-11: dropped only once the game's own shockwave is gone. It lives about 10 s past its blast while
                // the dust fades, and dropping it when our explosion ended had every scan record it again as a new one.
                if (null == kv.Key)
                    (deadWaves ??= new List<Shockwave>()).Add(kv.Key);
            }
            if (deadWaves != null)
                foreach (Shockwave key in deadWaves) waves.Remove(key);
            if (!UseWaveHook)
            {
                shockwaves = UnityEngine.Object.FindObjectsByType<Shockwave>(FindObjectsSortMode.None);
            }
#if SHADOW
            Shadow.CompareWaves(waves, pendingWaves, UseWaveHook);
#endif
            //Plugin.Logger?.LogDebug($"DISCOVERED {shockwaves.Length.ToString(CultureInfo.InvariantCulture)} SHOCKWAVES!");
            shockwaveDiscoveryTimer = 0f;
            return true;
        }
    }
}
