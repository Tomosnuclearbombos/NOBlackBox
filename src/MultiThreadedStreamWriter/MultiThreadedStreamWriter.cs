using BepInEx.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NOBlackBox
{
    internal class MultiThreadedStreamWriter
    {
        private StreamWriter _writer;
        private object _stateLock = new object();
        private volatile bool _shouldFlush = false;
        private volatile bool _shouldClose = false;
        private ConcurrentQueue<string> _lineQueue;
        private Task _writeTask;
        // Tomo's Nukes performance build (2026-10-06): the write loop used to go straight round again when there was
        // nothing to write, so this thread spun a whole CPU core for the entire recording. It now sleeps on this until a
        // line, a flush or the close arrives. Same lines, same order.
        private readonly ManualResetEventSlim _wake = new ManualResetEventSlim(false);

        public MultiThreadedStreamWriter(StreamWriter writer)
        {
            _writer = writer;
            _lineQueue = new ConcurrentQueue<string>();
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
                    while (!_lineQueue.IsEmpty)
                    {
                        //Empty the line queue
                        string newLine;
                        _lineQueue.TryDequeue(out newLine);
                        _writer.WriteLine(_lineQueue);
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
                while (!_lineQueue.IsEmpty && linesWrittenThisLoop < 100)
                {
                    string newLine;
                    _lineQueue.TryDequeue(out newLine);
                    _writer.WriteLine(newLine);
                    linesWrittenThisLoop++;
                }

                if (linesWrittenThisLoop == 0)
                {
                    // WriteLine, Flush and Close set _wake after queueing or flagging, so nothing can be missed here
                    _wake.Wait();
                    _wake.Reset();
                }
            }
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
    }
}
