using BepInEx.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NOBlackBox
{
    // Tomo's Nukes performance build (2026-10-12): one object line or time line, handed over as values for this thread to
    // turn into text. Reused (a pool) so handing a line over allocates nothing on the main thread.
    internal sealed class LineRec
    {
        internal bool isTime;
        internal double time;
        internal CultureInfo? culture;
        internal long id;
        internal readonly PropBag props = new PropBag();
#if SHADOW
        internal string? expectLine;   // check build: the text the old main-thread code built
#endif

        // "#" + seconds, as the main thread wrote it (string concatenation: the thread's culture)
        internal void SetTime(double seconds, CultureInfo threadCulture)
        {
            isTime = true;
            time = seconds;
            culture = threadCulture;
        }

        internal void SetProps(long tacviewId, PropBag source)
        {
            isTime = false;
            id = tacviewId;
            props.CopyFrom(source);
        }

        internal void Format(StringBuilder sb)
        {
            sb.Clear();
            if (isTime)
            {
                sb.Append('#').Append(time.ToString(culture));
            }
            else
            {
                props.AppendLine(sb, id);
            }
        }
    }

    internal class MultiThreadedStreamWriter
    {
        private StreamWriter _writer;
        private object _stateLock = new object();
        private volatile bool _shouldFlush = false;
        private volatile bool _shouldClose = false;
        // a line is a ready string or a LineRec this thread formats; one queue, so the order is kept
        private ConcurrentQueue<object> _lineQueue;
        private Task _writeTask;
        // Tomo's Nukes performance build (2026-10-06): the write loop used to go straight round again when there was
        // nothing to write, so this thread spun a whole CPU core for the entire recording. It now sleeps on this until a
        // line, a flush or the close arrives. Same lines, same order.
        private readonly ManualResetEventSlim _wake = new ManualResetEventSlim(false);

        private readonly ConcurrentQueue<LineRec> _pool = new ConcurrentQueue<LineRec>();
        private int _pooled = 0;
        private const int MaxPooled = 4096;
        private readonly StringBuilder _sb = new StringBuilder(512);
        private char[] _chars = new char[512];

        public MultiThreadedStreamWriter(StreamWriter writer)
        {
            _writer = writer;
            _lineQueue = new ConcurrentQueue<object>();
            _writeTask = Task.Run(() => _writeLoop());
        }

        private void _writeLoop()
        {
            Plugin.Logger?.LogDebug("Starting ACMI StreamWriter Thread.");
            bool flushThisLoop = false;
            bool closeThisLoop = false;
            while (true)
            {
                lock (_stateLock)
                {
                    flushThisLoop = _shouldFlush;
                    closeThisLoop = _shouldClose;
                }

                if (closeThisLoop)
                {
                    // 2026-10-12: the lines still queued at the close are written. This wrote the queue object's name
                    // (System.Collections.Concurrent.ConcurrentQueue...) once per line left instead.
                    while (_lineQueue.TryDequeue(out object item))
                    {
                        WriteItem(item);
                    }
                    _writer.Flush();
                    _writer.Close();
                    Plugin.Logger?.LogDebug("Exiting ACMI StreamWriter Thread.");
                    return;
                }

                if (flushThisLoop)
                {
                    Plugin.Logger?.LogDebug("ACMI Flush started.");
                    _writer.Flush();
                    Plugin.Logger?.LogDebug("ACMI Flush complete.");
                    lock (_stateLock)
                    {
                        _shouldFlush = false;
                        flushThisLoop = false;
                    }
                }

                var linesWrittenThisLoop = 0;
                while (linesWrittenThisLoop < 100 && _lineQueue.TryDequeue(out object item))
                {
                    WriteItem(item);
                    linesWrittenThisLoop++;
                }

                if (linesWrittenThisLoop == 0)
                {
                    // WriteLine, Signal, Flush and Close set _wake after queueing or flagging, so nothing can be missed here
                    _wake.Wait();
                    _wake.Reset();
                }
            }
        }

        private void WriteItem(object item)
        {
            if (item is string line)
            {
                _writer.WriteLine(line);
                return;
            }
            LineRec rec = (LineRec)item;
            rec.Format(_sb);
#if SHADOW
            Shadow.CheckLine(_sb.ToString(), rec.expectLine);
            rec.expectLine = null;
#endif
            int n = _sb.Length;
            if (_chars.Length < n)
            {
                _chars = new char[Math.Max(n, _chars.Length * 2)];
            }
            _sb.CopyTo(0, _chars, 0, n);
            _writer.Write(_chars, 0, n);
            _writer.WriteLine();
            if (Interlocked.Increment(ref _pooled) <= MaxPooled)
            {
                _pool.Enqueue(rec);
            }
            else
            {
                Interlocked.Decrement(ref _pooled);
            }
        }

        // a LineRec to fill (main thread)
        public LineRec Rent()
        {
            if (_pool.TryDequeue(out LineRec rec))
            {
                Interlocked.Decrement(ref _pooled);
                return rec;
            }
            return new LineRec();
        }

        public int PendingLineCount()
        {
            return _lineQueue.Count;
        }

        public void Flush()
        {
            Plugin.Logger?.LogDebug("ACMI Flush requested.");
            lock (_stateLock)
            {
                _shouldFlush = true;
            }
            _wake.Set();
        }

        public void Close()
        {
            //Set the close flag and wait for the task to finish.
            lock (_stateLock)
            {
                _shouldClose = true;
            }
            _wake.Set();
            _writeTask.Wait();
        }

        public void WriteLine(string line)
        {
            _lineQueue.Enqueue(line);
            _wake.Set();
        }

        // 2026-10-12: queued without waking this thread; the recorder wakes it once a frame (Signal) instead of once per
        // line (~800 a second at 40 AI, each a thread wake-up on the main thread's time). Same lines, same order.
        public void Write(LineRec rec)
        {
            _lineQueue.Enqueue(rec);
        }

        public void Signal()
        {
            if (!_lineQueue.IsEmpty)
            {
                _wake.Set();
            }
        }
    }
}
