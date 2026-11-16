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
            lock (pending) pending.Enqueue(new Job
            {
                Work = work, Done = done
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
                    job.Reply = job.Work();
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
