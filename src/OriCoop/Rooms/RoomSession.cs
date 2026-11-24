using System;
using System.Threading;

namespace OriCoop.Rooms
{
    public interface IRoomGame
    {
        string LiveCode
        {
            get;
        }
        bool InGameplay
        {
            get;
        }
        bool Together
        {
            get;
        }
        long PlayedSeconds
        {
            get;
        }
        string ConflictMessage
        {
            get;
        }
        int WriteRoomSlot(string room, string save, long version, byte[] data);
        void UpdateVersion(int slot, string room, string save, long version);
        void Host(int slot, string server);   // server: the C-server the room plays on (T-159; was the relay)
        void Join(string target);
        byte[] Snapshot(out string summary);
        void CopySingle(byte[] data);
        void Message(string text);
    }

    public sealed class RoomSession
    {
        readonly RoomClient client;
        readonly RoomJobs jobs;
        readonly RoomLocalStore store;
        readonly IRoomGame game;
        readonly RoomAuth auth;
        readonly string server;
        public Action<Action<string>> Password;
        public Action<string> Error;
        public Action Copied;
        public bool Online;
        public bool Active
        {
            get;
            private set;
        }
        public bool Holder
        {
            get;
            private set;
        }
        public bool UploadStopped
        {
            get;
            private set;
        }
        public long Version
        {
            get;
            private set;
        }
        public string Save
        {
            get;
            private set;
        }
        public double BeatSeconds = 30, BackupSeconds = 60, WaitSeconds = 2;
        int generation, slot = -1;
        bool liveSent, waiting, polling, uploading, leaving, leaveQueued;
        double now, nextBeat, nextBackup, waitUntil;
        long lastPlayed;
        byte[] queued;
        string queuedSummary;
        long queuedPlayed;
        bool queuedTogether;

        public RoomSession(RoomClient client, RoomJobs jobs, RoomLocalStore store, IRoomGame game,
            RoomAuth auth, string server)
        {
            this.client = client;
            this.jobs = jobs;
            this.store = store;
            this.game = game;
            this.auth = auth;
            this.server = server;
        }

        public void Call(Func<RoomReply> work, Action<RoomReply> done)
        {
            Call(work, done, null);
        }

        // wanted: see RoomJobs.Add (T-200); null = always run.
        public void Call(Func<RoomReply> work, Action<RoomReply> done, Func<bool> wanted)
        {
            jobs.Add(work, delegate(RoomReply reply)
            {
                if (reply.Error == "bad_token" && Password != null)
                {
                    Password(delegate(string password)
                    {
                        if (password == null)
                        {
                            done(new RoomReply
                            {
                                Error = "cancelled", Fields = new RoomWire.Fields()
                            });
                            return;
                        }
                        jobs.Add(delegate
                        {
                            return client.Rebind(auth.Room, password, auth.Steam);
                        },
                            delegate(RoomReply rebound)
                            {
                                if (!rebound.Ok)
                                {
                                    done(rebound);
                                    return;
                                }
                                auth.Token = rebound.Fields.GetString("token");
                                store.SaveToken(auth.Room, auth.Token);
                                Call(work, done, wanted);
                            });
                    });
                    return;
                }
                done(reply);
            }, wanted);
        }

        void Fail(string error)
        {
            if (Error != null) Error(error);
        }

        public void Hello(Action<bool> done)
        {
            jobs.Add(client.Hello, delegate(RoomReply r)
            {
                Online = r.Ok;
                done(Online);
            });
        }

        public void Enter(string save)
        {
            if (!Online)
            {
                Fail("network");
                return;
            }
            if (Active)
            {
                Fail("busy");
                return;
            }
            Save = save;
            Holder = false;
            waiting = false;
            polling = false;
            slot = -1;
            Active = true;
            leaving = false;
            leaveQueued = false;
            UploadStopped = false;
            // No backup download until enter says we join (Mode sets it then): left at 0 (or a past value), the next
            // Tick downloaded the whole save before the enter reply, and a host then downloaded it a second time.
            nextBackup = double.MaxValue;
            int entered = ++generation;
            Call(delegate
            {
                return client.Enter(auth, save);
            }, delegate(RoomReply r)
            {
                if (!Active || leaving || entered != generation) return;
                if (!r.Ok)
                {
                    Active = false;
                    Fail(r.Error);
                    return;
                }
                Mode(r);
            });
        }

        void Mode(RoomReply r)
        {
            string mode = r.Fields.GetString("mode");
            if (mode == "host")
            {
                Holder = true;
                // A waiting player's beat can answer host: no longer waiting, or Tick gave the lease up at the wait deadline.
                waiting = false;
                Download(delegate(byte[] data)
                {
                    if (!Active || leaving) return;
                    slot = game.WriteRoomSlot(auth.Room, Save, Version, data);
                    if (slot < 0)
                    {
                        Fail("no_slot");
                        Leave();
                        return;
                    }
                    liveSent = false;
                    nextBeat = now + BeatSeconds;
                    game.Host(slot, server);
                    lastPlayed = game.PlayedSeconds;
                });
            }
            else if (mode == "join" && r.Fields.GetString("live").Length > 0)
            {
                waiting = false;
                nextBackup = now + BackupSeconds;
                game.Join(server + "|" + r.Fields.GetString("live"));
            }
            else
            {
                waiting = true;
                waitUntil = now + 30;
                nextBeat = now + WaitSeconds;
            }
        }

        void Download(Action<byte[]> done)
        {
            long have;
            string summary;
            byte[] local = store.LoadLatest(auth.Room, Save, out have, out summary);
            int entered = generation;
            string save = Save;
            Call(delegate
            {
                return client.Download(auth, save, have);
            }, delegate(RoomReply r)
            {
                if (entered != generation) return;
                polling = false;
                if (!r.Ok)
                {
                    Fail(r.Error);
                    return;
                }
                Version = r.Fields.GetLong("version", have);
                byte[] bytes = r.Fields.GetString("same") == "1" ? local : r.Fields.GetBytes("save");
                if (r.Fields.GetString("same") != "1")
                    store.StoreVersion(auth.Room, save, Version, bytes, r.Fields.GetString("summary"));
                if (done != null) done(bytes);
            });
        }

        public void Tick(double seconds)
        {
            now = seconds;
            if (!Active || leaving) return;
            if (Holder && slot >= 0 && !liveSent && !string.IsNullOrEmpty(game.LiveCode))
            {
                liveSent = true;
                string code = game.LiveCode;
                Call(delegate
                {
                    return client.Live(auth, Save, code);
                }, delegate(RoomReply r)
                {
                    if (!r.Ok)
                    {
                        liveSent = false;
                        Fail(r.Error);
                    }
                });
            }
            if ((Holder || waiting) && now >= nextBeat && !polling)
            {
                if (waiting && now >= waitUntil)
                {
                    Fail("waiting");
                    Leave();
                    return;
                }
                nextBeat = now + (waiting ? WaitSeconds : BeatSeconds);
                polling = true;
                Call(delegate
                {
                    return client.Beat(auth, Save, waiting);
                }, delegate(RoomReply r)
                {
                    polling = false;
                    if (!Active || leaving) return;
                    if (!r.Ok)
                    {
                        Fail(r.Error);
                        return;
                    }
                    if (waiting)
                    {
                        double deadline = waitUntil;
                        Mode(r);
                        waitUntil = deadline;
                    }
                });
            }
            if (!Holder && !waiting && !polling && now >= nextBackup)
            {
                polling = true;
                nextBackup = now + BackupSeconds;
                Download(null);
            }
        }

        public void Saved(byte[] bytes, string summary)
        {
            if (!Active || !Holder || UploadStopped || leaving) return;
            queued = bytes;
            queuedSummary = summary;
            queuedPlayed = game.PlayedSeconds;
            queuedTogether = game.Together;
            SendUpload();
        }

        void SendUpload()
        {
            if (uploading || queued == null || UploadStopped) return;
            byte[] bytes = queued;
            string summary = queuedSummary;
            queued = null;
            long version = Version;
            long played = queuedPlayed;
            long secs = Math.Max(0, played - lastPlayed);
            bool together = queuedTogether;
            uploading = true;
            Call(delegate
            {
                return client.Upload(auth, Save, version, bytes, summary, secs, together);
            },
                delegate(RoomReply r)
                {
                    uploading = false;
                    if (r.Ok)
                    {
                        Version = r.Fields.GetLong("version", version);
                        lastPlayed = played;
                        store.StoreVersion(auth.Room, Save, Version, bytes, summary);
                        game.UpdateVersion(slot, auth.Room, Save, Version);
                    }
                    else if (r.Error == "version_conflict" || r.Error == "not_host")
                    {
                        UploadStopped = true;
                        queued = null;
                        game.Message(game.ConflictMessage);
                    }
                    else Fail(r.Error);
                    if (queued != null && !UploadStopped) SendUpload();
                    else if (leaving) FinishLeave();
                });
        }

        public void ReturnToTitle()
        {
            if (!Active || leaving) return;
            if (Holder && game.InGameplay && !UploadStopped)
            {
                string summary;
                byte[] bytes = game.Snapshot(out summary);
                Saved(bytes, summary);
            }
            Leave();
        }

        public void Cancel()
        {
            generation++;
            Leave();
        }

        public void Leave()
        {
            if (!Active || leaving) return;
            leaving = true;
            if (!uploading) FinishLeave();
        }

        public void Quit(int timeoutMs)
        {
            Password = null;
            ReturnToTitle();
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (Active && DateTime.UtcNow < deadline)
            {
                jobs.Pump();
                Thread.Sleep(10);
            }
            if (Active) FinishLeave();
        }

        void FinishLeave()
        {
            if (leaveQueued) return;
            leaveQueued = true;
            string save = Save;
            jobs.Add(delegate
            {
                return client.Leave(auth, save);
            }, delegate(RoomReply r)
            {
                Active = false;
                Holder = false;
                waiting = false;
                slot = -1;
                generation++;
            });
        }

        void CopyBytes(byte[] bytes)
        {
            game.CopySingle(bytes);
            if (Copied != null) Copied();
        }

        public void Copy(string save)
        {
            if (Active)
            {
                Fail("busy");
                return;
            }
            Save = save;
            if (Online) Download(CopyBytes);
            else
            {
                long version;
                string summary;
                byte[] bytes = store.LoadLatest(auth.Room, save, out version, out summary);
                if (bytes == null) Fail("no_backup");
                else CopyBytes(bytes);
            }
        }
    }
}
