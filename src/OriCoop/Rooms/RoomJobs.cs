using System;
using System.Collections.Generic;
using System.Threading;

namespace OriCoop.Rooms
{
    // Jobs execute serially; only Pump invokes completions on the caller's thread.
    public sealed class RoomJobs : IDisposable
    {
        sealed class Job
        {
            public Func<RoomReply> Work;
            public Action<RoomReply> Done;
            public Func<bool> Wanted;
            public RoomReply Reply;
        }

        readonly Queue<Job> pending = new Queue<Job>();
        readonly Queue<Job> completed = new Queue<Job>();
        readonly AutoResetEvent ready = new AutoResetEvent(false);
        readonly Thread worker;
        bool stopping;

        public RoomJobs()
        {
            worker = new Thread(Run);
            worker.IsBackground = true;
            worker.Start();
        }

        public void Add(Func<RoomReply> work, Action<RoomReply> done)
        {
            Add(work, done, null);
        }

        // T-200: `wanted` tells, when the job's turn comes, whether anyone still waits for it (it runs on the worker
        // thread: read plain fields only). A job nobody waits for is not run; its reply is the error "cancelled".
        // Requests abandoned by leaving a page (each can wait for its whole HTTP timeout on an unreachable server) no
        // longer make the next request wait for all of them.
        public void Add(Func<RoomReply> work, Action<RoomReply> done, Func<bool> wanted)
        {
            lock (pending) pending.Enqueue(new Job
            {
                Work = work, Done = done, Wanted = wanted
            });
            ready.Set();
        }

        void Run()
        {
            while (true)
            {
                Job job = null;
                lock (pending)
                {
                    if (pending.Count > 0) job = pending.Dequeue();
                    else if (stopping) return;
                }
                if (job == null)
                {
                    ready.WaitOne();
                    continue;
                }
                try
                {
                    if (job.Wanted != null && !job.Wanted())
                        job.Reply = new RoomReply
                        {
                            Error = "cancelled", Fields = new RoomWire.Fields()
                        };
                    else job.Reply = job.Work();
                }
                catch (Exception)
                {
                    job.Reply = new RoomReply
                    {
                        Error = "network", Fields = new RoomWire.Fields()
                    };
                }
                lock (completed) completed.Enqueue(job);
            }
        }

        public void Pump()
        {
            while (true)
            {
                Job job;
                lock (completed)
                {
                    if (completed.Count == 0) return;
                    job = completed.Dequeue();
                }
                if (job.Done != null) job.Done(job.Reply);
            }
        }

        public void Dispose()
        {
            lock (pending) stopping = true;
            ready.Set();
        }
    }
}
