using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Google.Protobuf;
using Survival.V1;

namespace SurvivalWorld.Server.WorldEvents
{
    public static class WorldEventTemplateIds
    {
        public const string GreatHunt = "world_event.great_hunt";
        public const string RareResource = "world_event.rare_resource";
        public const string RareBuyerRush = "world_event.rare_buyer_rush";
    }

    public static class WorldEventRules
    {
        public const int Version = 1;
    }

    public interface IWorldEventSpawnSink
    {
        int AliveRareDeer { get; }
        int ActiveRareOreNodes { get; }
        int ActiveRareBuyers { get; }
        bool SpawnRareDeer(string eventInstanceId, string regionId);
        bool SpawnRareOreNode(string eventInstanceId, string regionId, int yieldBudget);
        bool SpawnRareBuyer(string eventInstanceId, string regionId, int inventorySeed);
    }

    public sealed class NullWorldEventSpawnSink : IWorldEventSpawnSink
    {
        public static readonly NullWorldEventSpawnSink Instance = new NullWorldEventSpawnSink();

        private NullWorldEventSpawnSink()
        {
        }

        public int AliveRareDeer => 0;
        public int ActiveRareOreNodes => 0;
        public int ActiveRareBuyers => 0;
        public bool SpawnRareDeer(string eventInstanceId, string regionId) => false;
        public bool SpawnRareOreNode(string eventInstanceId, string regionId, int yieldBudget) => false;
        public bool SpawnRareBuyer(string eventInstanceId, string regionId, int inventorySeed) => false;
    }

    public interface IWorldEventServiceGateway
    {
        RegisterResponse Register(RegisterRequest request);
        UpdateStateResponse UpdateState(UpdateStateRequest request);
    }

    public sealed class NullWorldEventServiceGateway : IWorldEventServiceGateway
    {
        public static readonly NullWorldEventServiceGateway Instance = new NullWorldEventServiceGateway();

        private NullWorldEventServiceGateway()
        {
        }

        public RegisterResponse Register(RegisterRequest request)
        {
            string id = request == null || string.IsNullOrWhiteSpace(request.ProposalId)
                ? "local-event"
                : "local-" + request.ProposalId;
            return new RegisterResponse { EventInstanceId = id };
        }

        public UpdateStateResponse UpdateState(UpdateStateRequest request)
        {
            return new UpdateStateResponse { Status = ResultStatus.Ok };
        }
    }

    public sealed class WorldEventInstanceConfig
    {
        public WorldEventInstanceConfig(string eventInstanceId, string proposalId, string templateId, string worldId, string regionId, WorldEventParameterBag parameters)
        {
            EventInstanceId = eventInstanceId ?? string.Empty;
            ProposalId = proposalId ?? string.Empty;
            TemplateId = templateId ?? string.Empty;
            WorldId = worldId ?? string.Empty;
            RegionId = regionId ?? string.Empty;
            Parameters = parameters ?? WorldEventParameterBag.Empty;
        }

        public string EventInstanceId { get; }
        public string ProposalId { get; }
        public string TemplateId { get; }
        public string WorldId { get; }
        public string RegionId { get; }
        public WorldEventParameterBag Parameters { get; }
    }

    public sealed class WorldEventParameterBag
    {
        public static readonly WorldEventParameterBag Empty = new WorldEventParameterBag(new Dictionary<string, string>(StringComparer.Ordinal));

        private readonly Dictionary<string, string> values;

        private WorldEventParameterBag(Dictionary<string, string> values)
        {
            this.values = values ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }

        public IReadOnlyDictionary<string, string> Values => values;

        public static WorldEventParameterBag From(ByteString bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return Empty;
            }

            string text = bytes.ToStringUtf8();
            if (string.IsNullOrWhiteSpace(text))
            {
                return Empty;
            }

            return new WorldEventParameterBag(ParseFlat(text));
        }

        public bool TryGetInt(string key, out int value)
        {
            value = 0;
            return values.TryGetValue(key ?? string.Empty, out string raw)
                && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        public bool TryGetString(string key, out string value)
        {
            return values.TryGetValue(key ?? string.Empty, out value);
        }

        private static Dictionary<string, string> ParseFlat(string text)
        {
            var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
            string normalized = text.Trim();
            if (normalized.Length == 0)
            {
                return parsed;
            }

            if (normalized[0] == '{' && normalized[normalized.Length - 1] == '}')
            {
                string body = normalized.Substring(1, normalized.Length - 2);
                string[] entries = SplitRespectingQuotes(body, ',');
                for (int i = 0; i < entries.Length; i++)
                {
                    int colon = IndexOfUnquoted(entries[i], ':');
                    if (colon <= 0)
                    {
                        continue;
                    }

                    string key = Unquote(entries[i].Substring(0, colon).Trim());
                    string value = Unquote(entries[i].Substring(colon + 1).Trim());
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        parsed[key] = value;
                    }
                }

                return parsed;
            }

            char delimiter = normalized.IndexOf(';') >= 0 ? ';' : '&';
            string[] pairs = normalized.Split(delimiter);
            for (int i = 0; i < pairs.Length; i++)
            {
                int equals = pairs[i].IndexOf('=');
                if (equals <= 0)
                {
                    continue;
                }

                string key = pairs[i].Substring(0, equals).Trim();
                string value = pairs[i].Substring(equals + 1).Trim();
                if (!string.IsNullOrWhiteSpace(key))
                {
                    parsed[key] = value;
                }
            }

            return parsed;
        }

        private static string[] SplitRespectingQuotes(string text, char separator)
        {
            var parts = new List<string>();
            int start = 0;
            bool inQuotes = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '"' && (i == 0 || text[i - 1] != '\\'))
                {
                    inQuotes = !inQuotes;
                }
                else if (c == separator && !inQuotes)
                {
                    parts.Add(text.Substring(start, i - start));
                    start = i + 1;
                }
            }

            parts.Add(text.Substring(start));
            return parts.ToArray();
        }

        private static int IndexOfUnquoted(string text, char target)
        {
            bool inQuotes = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '"' && (i == 0 || text[i - 1] != '\\'))
                {
                    inQuotes = !inQuotes;
                }
                else if (c == target && !inQuotes)
                {
                    return i;
                }
            }

            return -1;
        }

        private static string Unquote(string value)
        {
            string trimmed = value.Trim();
            if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[trimmed.Length - 1] == '"')
            {
                trimmed = trimmed.Substring(1, trimmed.Length - 2).Replace("\\\"", "\"").Replace("\\\\", "\\");
            }

            return trimmed;
        }
    }

    public sealed class WorldEventStats
    {
        public int Spawned { get; private set; }
        public int Harvested { get; private set; }
        public int Purchased { get; private set; }
        public int Remaining { get; private set; }
        public int ParticipantCount { get; private set; }

        public void AddSpawned(int count)
        {
            Spawned += Math.Max(0, count);
        }

        public void AddHarvested(int count)
        {
            Harvested += Math.Max(0, count);
        }

        public void AddPurchased(int count)
        {
            Purchased += Math.Max(0, count);
        }

        public void AddParticipant()
        {
            ParticipantCount++;
        }

        public void SetRemaining(int count)
        {
            Remaining = Math.Max(0, count);
        }

        public ByteString ToByteString()
        {
            return ByteString.CopyFromUtf8(ToJson());
        }

        public string ToJson()
        {
            var builder = new StringBuilder(128);
            builder.Append('{');
            Append(builder, "spawned", Spawned).Append(',');
            Append(builder, "harvested", Harvested).Append(',');
            Append(builder, "purchased", Purchased).Append(',');
            Append(builder, "remaining", Remaining).Append(',');
            Append(builder, "participant_count", ParticipantCount);
            builder.Append('}');
            return builder.ToString();
        }

        private static StringBuilder Append(StringBuilder builder, string name, int value)
        {
            builder.Append('"').Append(name).Append("\":").Append(value.ToString(CultureInfo.InvariantCulture));
            return builder;
        }
    }

    public sealed class WorldEventEffectContext
    {
        public WorldEventEffectContext(WorldEventInstanceConfig config, IWorldEventSpawnSink spawnSink, long unixTimeMs)
        {
            Config = config ?? throw new ArgumentNullException(nameof(config));
            SpawnSink = spawnSink ?? NullWorldEventSpawnSink.Instance;
            UnixTimeMs = unixTimeMs;
        }

        public WorldEventInstanceConfig Config { get; }
        public IWorldEventSpawnSink SpawnSink { get; }
        public long UnixTimeMs { get; }
    }

    public interface IWorldEventEffect
    {
        string TemplateId { get; }
        long DurationMs { get; }
        WorldEventStats Stats { get; }
        void Tick(WorldEventEffectContext context);
        void Complete(WorldEventEffectContext context);
        void RecordHarvested(int count);
        void RecordPurchased(int count);
        void RecordParticipant();
    }

    public abstract class WorldEventEffectBase : IWorldEventEffect
    {
        protected WorldEventEffectBase(string templateId, long durationMs)
        {
            TemplateId = templateId ?? string.Empty;
            DurationMs = Math.Max(1L, durationMs);
            Stats = new WorldEventStats();
        }

        public string TemplateId { get; }
        public long DurationMs { get; }
        public WorldEventStats Stats { get; }

        public abstract void Tick(WorldEventEffectContext context);

        public virtual void Complete(WorldEventEffectContext context)
        {
        }

        public void RecordHarvested(int count)
        {
            Stats.AddHarvested(count);
        }

        public void RecordPurchased(int count)
        {
            Stats.AddPurchased(count);
        }

        public void RecordParticipant()
        {
            Stats.AddParticipant();
        }
    }

    public static class WorldEventEffectFactory
    {
        public static bool IsSupported(string templateId)
        {
            return string.Equals(templateId, WorldEventTemplateIds.GreatHunt, StringComparison.Ordinal)
                || string.Equals(templateId, WorldEventTemplateIds.RareResource, StringComparison.Ordinal)
                || string.Equals(templateId, WorldEventTemplateIds.RareBuyerRush, StringComparison.Ordinal);
        }

        public static IWorldEventEffect Create(string templateId, WorldEventParameterBag parameters)
        {
            switch (templateId)
            {
                case WorldEventTemplateIds.GreatHunt:
                    return new GreatHuntEffect();
                case WorldEventTemplateIds.RareResource:
                    return new RareResourceEffect(parameters ?? WorldEventParameterBag.Empty);
                case WorldEventTemplateIds.RareBuyerRush:
                    return new RareBuyerRushEffect();
                default:
                    throw new ArgumentException("Unsupported world event template: " + templateId, nameof(templateId));
            }
        }
    }

    public readonly struct WorldEventProposalResult
    {
        private WorldEventProposalResult(WorldEventProposalStatus status, string proposalId, string eventInstanceId, string reasonCode)
        {
            Status = status;
            ProposalId = proposalId ?? string.Empty;
            EventInstanceId = eventInstanceId ?? string.Empty;
            ReasonCode = reasonCode ?? string.Empty;
        }

        public WorldEventProposalStatus Status { get; }
        public string ProposalId { get; }
        public string EventInstanceId { get; }
        public string ReasonCode { get; }
        public bool Approved => Status == WorldEventProposalStatus.Approved || Status == WorldEventProposalStatus.Duplicate;

        public static WorldEventProposalResult ApprovedResult(string proposalId, string eventInstanceId)
        {
            return new WorldEventProposalResult(WorldEventProposalStatus.Approved, proposalId, eventInstanceId, string.Empty);
        }

        public static WorldEventProposalResult Duplicate(string proposalId, string eventInstanceId)
        {
            return new WorldEventProposalResult(WorldEventProposalStatus.Duplicate, proposalId, eventInstanceId, string.Empty);
        }

        public static WorldEventProposalResult Rejected(string proposalId, string reasonCode)
        {
            return new WorldEventProposalResult(WorldEventProposalStatus.Rejected, proposalId, string.Empty, reasonCode);
        }
    }

    public enum WorldEventProposalStatus
    {
        Approved = 0,
        Duplicate = 1,
        Rejected = 2
    }
}