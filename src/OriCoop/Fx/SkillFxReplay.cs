using System;

namespace OriCoop.Fx
{
    internal sealed class SkillFxReplay
    {
        public bool Active { get; private set; }
        public bool CanSend(bool localCaster) { return !Active && localCaster; }

        public T Run<T>(Func<T> play)
        {
            bool previous = Active;
            Active = true;
            try { return play(); }
            finally { Active = previous; }
        }
    }
}
