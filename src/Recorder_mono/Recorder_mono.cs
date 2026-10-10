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

        // Tomo's Nukes performance build (2026-10-06): everything recorded is ticked from here, in creation order, instead
        // of Unity calling Update and LateUpdate on each object. Units and shockwaves are still components (Destroy and the
        // unitObjects lookups behave as before); gun rounds and flares are plain objects.
        private List<ACMIObject_mono> objects = [];
        private int updatesDue = 0;
        private List<ACMIFlare> flares = [];
        private List<ACMITracer> liveTracers = [];

        // every round seen this recording (as the old tracers dictionary), so the fallback discovery never adds one twice
        private HashSet<BulletSim.Bullet> tracers = [];

        private Unit[] units = [];
        private BulletSim[] bulletSims = [];
        private bool processUnits = false;
        private bool processBulletSims = false;
        private bool processShockWaves = false;

        public void invokeWriterUpdate(IACMIObject obj)
        {
            writer.UpdateObject(obj, curTime);
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
            objects.Add(obj);
            return obj;
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

        }
        void Update()
        {
            curTime += TimeSpan.FromSeconds(Time.deltaTime);

            // A component Unity destroyed at the end of last frame is null now. One added from here on (the units below)
            // got its first Update next frame, so only the ones that exist now get this frame's Tick (in LateUpdate).
            objects.RemoveAll(o => o == null);
            flares.RemoveAll(f => f.Gone);
            updatesDue = objects.Count;
            ProcessDiscovered();
        }

        private void ProcessDiscovered()
        {
            if (units.Length == 0)
            {
                return;
            }
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
                                isNew = true;
                                break;
                            default:
                                break;
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
                        AddTracer(bulletSim, bullet, null);
                    }
                }
                processBulletSims = false;
            }

            if(processShockWaves)
            {
                foreach (Shockwave wave in shockwaves)
                {
                    if (waves.ContainsKey(wave))
                    {
                        continue;
                    }
                    GameObject shockwave = new GameObject();
                    ACMIShockwave_mono shockwaveObj = Track(shockwave.AddComponent<ACMIShockwave_mono>());
                    shockwaveObj.Init(wave);
                    shockwaveObj.enabled = true;
                    waves.Add(wave, shockwave);
                }
            }
        }
        void LateUpdate()
        {
            // The Update pass. Each object's Update used to run after its own unit's Update (it is always created after
            // its unit), so running it here, after every game Update of the frame, reads the same values.
            for (int i = 0; i < updatesDue; i++)
            {
                ACMIObject_mono obj = objects[i];
                if (obj != null && obj.enabled)
                {
                    try { obj.Tick(); } catch (Exception e) { Debug.LogException(e); }
                }
            }
            updatesDue = 0;
            for (int i = 0; i < flares.Count; i++)
            {
                ACMIFlare flare = flares[i];
                if (flare.enabled && !flare.Gone && flare.TicksThisFrame)
                {
                    try { flare.Tick(); } catch (Exception e) { Debug.LogException(e); }
                }
            }

            unitDiscoveryTimer += Time.deltaTime;
            bulletSimDiscoveryTimer += Time.deltaTime;
            shockwaveDiscoveryTimer += Time.deltaTime;
            if (shockwaveDiscoveryTimer >= Configuration.shockwaveDiscoveryDelta.Value)
            {
                processShockWaves = ShockWaveDiscovery();
            }
            if (unitDiscoveryTimer >= Configuration.unitDiscoveryDelta.Value)
            {
                processUnits = UnitDiscovery();
            }
            float bulletSimDelta = BulletFiredPatch.applied && !BulletFiredPatch.failed ? bulletSimFallbackDelta : Configuration.bulletSimDiscoveryDelta.Value;
            if (bulletSimDiscoveryTimer >= bulletSimDelta)
            {
                processBulletSims = BulletSimDiscovery();
            }

            for (int i = 0; i < objects.Count; i++)
            {
                ACMIObject_mono obj = objects[i];
                if (obj != null && obj.enabled)
                {
                    try { obj.LateTick(); } catch (Exception e) { Debug.LogException(e); }
                }
            }
            for (int i = 0; i < flares.Count; i++)
            {
                ACMIFlare flare = flares[i];
                if (flare.enabled && !flare.Gone)
                {
                    try { flare.LateTick(); } catch (Exception e) { Debug.LogException(e); }
                }
            }
            for (int i = 0; i < liveTracers.Count; i++)
            {
                ACMITracer tracer = liveTracers[i];
                if (tracer.enabled)
                {
                    try { tracer.LateTick(); } catch (Exception e) { Debug.LogException(e); }
                }
            }
            liveTracers.RemoveAll(t => !t.enabled);
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
            waves = [];
            tracers = [];
            objects = [];
            flares = [];
            liveTracers = [];
            writer?.Close();
            Plugin.Logger?.LogDebug("DESTROYED MONO RECORDER");
        }

        private bool UnitDiscovery()
        {
            foreach (int key in unitObjects.Keys)
            {
                if (null == unitObjects[key])
                {
                    // Plugin.Logger?.LogDebug($"REMOVING OBJECT {key.ToString(CultureInfo.InvariantCulture)}");
                    unitObjects.Remove(key);
                }
            }
            //Plugin.Logger?.LogDebug($"Frame TICK at {curTime.ToString(CultureInfo.InvariantCulture)}");
            units = UnityEngine.Object.FindObjectsByType<Unit>(FindObjectsSortMode.None);
            //Plugin.Logger?.LogDebug($"DISCOVERED {units.Length.ToString(CultureInfo.InvariantCulture)} UNITS!");
            unitDiscoveryTimer = 0f;
            return true;

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
            foreach (Shockwave key in waves.Keys)
            {
                if (null == waves[key])
                {
                    //Plugin.Logger?.LogDebug($"REMOVING TRACER");
                    waves.Remove(key);
                }
            }
            shockwaves = UnityEngine.Object.FindObjectsByType<Shockwave>(FindObjectsSortMode.None);
            //Plugin.Logger?.LogDebug($"DISCOVERED {shockwaves.Length.ToString(CultureInfo.InvariantCulture)} SHOCKWAVES!");
            shockwaveDiscoveryTimer = 0f;
            return true;
        }
    }
}
