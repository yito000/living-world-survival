using System;
using Survival.V1;

namespace SurvivalWorld.Server.WorldEvents
{
    public sealed class WorldEventInstanceRunner
    {
        private readonly IWorldEventEffect effect;
        private long activatedAtUnixMs;

        public WorldEventInstanceRunner(WorldEventInstanceConfig config, IWorldEventEffect effect)
        {
            Config = config ?? throw new ArgumentNullException(nameof(config));
            this.effect = effect ?? throw new ArgumentNullException(nameof(effect));
            State = WorldEventState.Proposed;
        }

        public WorldEventInstanceConfig Config { get; }
        public WorldEventState State { get; private set; }
        public WorldEventStats Stats => effect.Stats;
        public long ActivatedAtUnixMs => activatedAtUnixMs;
        public long DurationMs => effect.DurationMs;
        public bool IsActive => State == WorldEventState.Active;

        public bool Activate(long unixTimeMs)
        {
            if (State == WorldEventState.Active)
            {
                return false;
            }

            if (State == WorldEventState.Completed || State == WorldEventState.Rejected)
            {
                return false;
            }

            activatedAtUnixMs = unixTimeMs;
            State = WorldEventState.Active;
            return true;
        }

        public bool Tick(long unixTimeMs, IWorldEventSpawnSink spawnSink)
        {
            if (State != WorldEventState.Active)
            {
                return false;
            }

            var context = new WorldEventEffectContext(Config, spawnSink, unixTimeMs);
            if (unixTimeMs - activatedAtUnixMs >= effect.DurationMs)
            {
                return Complete(context);
            }

            effect.Tick(context);
            if (unixTimeMs - activatedAtUnixMs >= effect.DurationMs)
            {
                return Complete(context);
            }

            return false;
        }

        public bool Complete(long unixTimeMs, IWorldEventSpawnSink spawnSink)
        {
            if (State != WorldEventState.Active)
            {
                return false;
            }

            return Complete(new WorldEventEffectContext(Config, spawnSink, unixTimeMs));
        }

        public void Reject()
        {
            if (State == WorldEventState.Completed)
            {
                return;
            }

            State = WorldEventState.Rejected;
        }

        public void RecordHarvested(int count)
        {
            effect.RecordHarvested(count);
        }

        public void RecordPurchased(int count)
        {
            effect.RecordPurchased(count);
        }

        public void RecordParticipant()
        {
            effect.RecordParticipant();
        }

        private bool Complete(WorldEventEffectContext context)
        {
            effect.Complete(context);
            State = WorldEventState.Completed;
            return true;
        }
    }
}