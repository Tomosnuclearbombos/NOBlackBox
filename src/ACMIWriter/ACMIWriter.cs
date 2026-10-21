using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using NuclearOption.SavedMission;
using NuclearOption.SceneLoading;
using UnityEngine;

namespace NOBlackBox
{
    internal class ACMIWriter
    {
        private MultiThreadedStreamWriter output;
        private readonly DateTime reference;
        public static TimeSpan lastUpdate;
        public static DateTime lastFlushTime;
        // Tomo's Nukes performance build (2026-10-06): the autosave check ran DateTime.Now (a time zone conversion) for
        // every line written. It now reads a monotonic clock; lastFlushTime is still kept for the countdown widget.
        private static readonly Stopwatch flushClock = Stopwatch.StartNew();
        private double lastFlushSeconds;
        internal string filename;
        internal MapKey currentMapKey;
        internal ACMIWriter(DateTime reference)
        {
            string dir = Configuration.OutputPath;
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string basename = Path.Combine(dir, DateTime.Now.ToString("s").Replace(":", "-"));
            basename += "_" + MissionManager.CurrentMission.Name + "_" + MapSettingsManager.i.MapLoader.CurrentMap.Path;
            filename = basename + ".acmi";
            int postfix = 0;
            while (File.Exists(filename))
                filename = basename + $" ({++postfix}).acmi";

            output = new MultiThreadedStreamWriter(File.CreateText(filename));
            //sb = new StringBuilder();
            this.reference = reference;
            currentMapKey = MapSettingsManager.i.MapLoader.CurrentMap;

            Plugin.Logger?.LogDebug("MAP NAME IS " + currentMapKey.Path);
            output.WriteLine("FileType=text/acmi/tacview");
            output.WriteLine("FileVersion=2.2");

            Dictionary<string, string> initProps = new()
            {
                { "ReferenceTime", reference.ToString("s") + "Z" },
                { "DataSource", $"Nuclear Option {Application.version}" },
                { "DataRecorder", $"NOBlackBox 0.3.8.5" },
                { "Author", Plugin.localPlayer?.name.Replace(",", "\\,") ?? "Server" },
                { "RecordingTime", DateTime.Now.ToString("s") + "Z" },
                { "MapId", $"NuclearOption.{currentMapKey.Path}"},
            };

            Mission mission = MissionManager.CurrentMission;
            initProps.Add("Title", mission.Name.Replace(",", "\\,"));

            /*
            if (mission.missionSettings.description != null)
            {
                string briefing = mission.missionSettings.description.Replace(",", "\\,");
                if (briefing != "")
                    initProps.Add("Briefing", briefing);
            }
            */
            output.WriteLine($"0,{StringifyProps(initProps)}");
            output.Flush();

            lastFlushTime = DateTime.Now;
            lastFlushSeconds = flushClock.Elapsed.TotalSeconds;
        }

        ~ACMIWriter()
        {
            Close();
        }

        public void UpdateObject(IACMIObject aObject, DateTime updateTime)
        {
            if (aObject.Props.Count == 0)
                return;

            TimeSpan diff = updateTime - reference;
            if (diff != lastUpdate)
            {
                lastUpdate = diff;
                //output.WriteLine("#" + diff.TotalSeconds);
                WriteTime(diff.TotalSeconds);
            }

            //output.WriteLine($"{aObject.id:X},{StringifyProps(props)}");
            WriteProps(aObject.TacviewId, aObject.Props);
            //Plugin.Logger.LogDebug($"Time Elapsed = {diff.TotalSeconds}");
        }

        // 2026-10-12: an object line and its time line are handed to the writer thread as values (LineRec), which builds
        // the same text the main thread built here: "<id>,<key>=<value>,..." and "#" + seconds
        private void WriteProps(long tacviewId, PropBag props)
        {
            LineRec rec = output.Rent();
            rec.SetProps(tacviewId, props);
#if SHADOW
            rec.expectLine = Shadow.OldLine(tacviewId, props);
#endif
            Enqueue(rec);
        }

        private void WriteTime(double seconds)
        {
            LineRec rec = output.Rent();
            rec.SetTime(seconds, CultureInfo.CurrentCulture);
#if SHADOW
            rec.expectLine = "#" + seconds;
#endif
            Enqueue(rec);
        }

        internal void RemoveObject(IACMIObject aObject, DateTime updateTime)
        {

            TimeSpan diff = updateTime - reference;
            if (diff != lastUpdate)
            {
                lastUpdate = diff;
                //output.WriteLine("#" + diff.TotalSeconds);
                WriteLine("#" + diff.TotalSeconds);
            }

            WriteLine($"-{aObject.TacviewId:X}");
        }

        internal void WriteDestroyedEvent(IACMIObject aObject, DateTime updateTime)
        {
            TimeSpan diff = updateTime - reference;
            if (diff != lastUpdate)
            {
                lastUpdate = diff;
                //output.WriteLine("#" + diff.TotalSeconds);
                WriteLine("#" + diff.TotalSeconds);
            }

            if (Configuration.DestructionEvents.Value == true)
            {
                WriteLine("#" + diff.TotalSeconds);
                if (aObject.DestroyedEvent)
                {
                    WriteLine($"0,Event=Message|{aObject.TacviewId:X}|Has been destroyed.");
                }
            }
        }

        internal void WriteRepairedEvent(IACMIObject aObject, DateTime updateTime)
        {
            TimeSpan diff = updateTime - reference;
            if (diff != lastUpdate)
            {
                lastUpdate = diff;
                //output.WriteLine("#" + diff.TotalSeconds);
                WriteLine("#" + diff.TotalSeconds);
            }

            if (Configuration.DestructionEvents.Value == true)
            {
                WriteLine("#" + diff.TotalSeconds);
                if (aObject.DestroyedEvent)
                {
                    WriteLine($"0,Event=Message|{aObject.TacviewId:X}|Has been repaired.");
                }
            }
        }

        private string StringifyProps(Dictionary<string, string> props)
        {
            string[] propStrings = props.Select(x => x.Key + "=" + x.Value/*.Replace(",", "\\,")*/).ToArray();
            return string.Join(",", propStrings);
        }

        internal void WriteLine(string line)
        {
            CheckAutoSave();
            output.WriteLine(line);
#if RECPROF
            RecProf.Line(line.Length);
#endif
        }

        private void Enqueue(LineRec rec)
        {
            CheckAutoSave();
            output.Write(rec);
#if RECPROF
            RecProf.Line(0);
#endif
        }

        // wakes the writer thread for the lines queued this frame (Recorder_mono, end of LateUpdate)
        internal void Signal()
        {
            output.Signal();
        }

        private void CheckAutoSave()
        {
            double now = flushClock.Elapsed.TotalSeconds;
            if (now - lastFlushSeconds > Configuration.AutoSaveInterval)
            {
                output.Flush();
                lastFlushTime = DateTime.Now;
                lastFlushSeconds = now;
                //output.Close();
                //output = new StreamWriter(filename, append: true);
            }
        }

        internal void Flush()
        {
            output.Flush();
        }

        // 2026-10-12 (bug sweep): closed once. The recorder's OnDisable and OnDestroy both call this, and so does the
        // finaliser, so a second zip ran on the same file at the same time: "Sharing violation" or "Could not find file"
        // after every recording on the live servers (10-08 to 10-12), and in the worst timing the zip written twice.
        private int closed = 0;

        // Tomo's Nukes 2026-10-21 (Tomo: "make sure that the zips don't break"): the zip ran in the background
        // ("async void") and nothing waited for it, so a game stopped mid-mission exited about 1.5 s later with the zip
        // cut short (the 10-20 05:40 PvE restart left a 495 MB recording's zip at 21.7 MB, its raw file complete).
        // While the game is quitting (Quitting, set from Application.quitting) the recording is now zipped on the spot,
        // before the game exits. Otherwise it is zipped on a worker thread as before, and a quitting game first waits
        // for any of those still running (WaitForZips).
        internal static volatile bool Quitting = false;
        private static readonly List<System.Threading.Tasks.Task> zips = new();

        internal void Close()
        {
            if (System.Threading.Interlocked.Exchange(ref closed, 1) != 0)
            {
                return;
            }
            try
            {
                output.Close();
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"[NOBlackBox] closing the recording {Path.GetFileName(filename)}: {e.Message}");
                return;
            }
            if (Quitting)
            {
                ZipAndDelete(true);
                return;
            }
            System.Threading.Tasks.Task zip = System.Threading.Tasks.Task.Run(() => ZipAndDelete(false));
            lock (zips)
            {
                zips.RemoveAll(t => t.IsCompleted);
                zips.Add(zip);
            }
        }

        private void ZipAndDelete(bool atQuit)
        {
            try
            {
                Stopwatch took = Stopwatch.StartNew();
                long size = new FileInfo(filename).Length;
                string zipPath = ZipHelper.ZipFile(filename);
                Plugin.Logger?.LogDebug($"saving compressed acmi {zipPath}");
                ZipHelper.DeleteFile(filename);
                Plugin.Logger?.LogDebug($"deleted uncompressed acmi {filename}");
                if (atQuit)
                {
                    UnityEngine.Debug.Log($"[NOBlackBox] game quitting: {Path.GetFileName(zipPath)} zipped ({size / 1048576.0:0.0} MB) in {took.Elapsed.TotalSeconds:0.0} s");
                }
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"[NOBlackBox] zipping the recording {Path.GetFileName(filename)}: {e.Message}");
            }
        }

        // the game is quitting: let any zip still running on a worker thread finish before it exits
        internal static void WaitForZips(int milliseconds)
        {
            System.Threading.Tasks.Task[] running;
            lock (zips)
            {
                running = zips.Where(t => !t.IsCompleted).ToArray();
            }
            if (running.Length == 0)
            {
                return;
            }
            Stopwatch took = Stopwatch.StartNew();
            bool done = System.Threading.Tasks.Task.WaitAll(running, milliseconds);
            UnityEngine.Debug.Log($"[NOBlackBox] game quitting: waited {took.Elapsed.TotalSeconds:0.0} s for {running.Length} zip(s) still running{(done ? "" : ", not finished")}");
        }
    }
}
