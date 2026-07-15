using System;
using System.Collections.Generic;
using Survival.V1;
using SurvivalWorld.Inventory;
using UnityEngine;

namespace SurvivalWorld.Server.AI
{
    public sealed class ActionTemplateDefinition
    {
        public ActionTemplateDefinition(
            string templateId,
            int version,
            IEnumerable<string> tags,
            IEnumerable<string> preconditions,
            IEnumerable<string> interrupts,
            IEnumerable<ActionStepSpec> steps,
            int minDurationSeconds,
            int maxDurationSeconds,
            int maxRetries)
        {
            TemplateId = templateId ?? string.Empty;
            Version = version <= 0 ? 1 : version;
            Tags = Copy(tags);
            Preconditions = Copy(preconditions);
            Interrupts = Copy(interrupts);
            Steps = steps == null ? new List<ActionStepSpec>() : new List<ActionStepSpec>(steps);
            MinDurationSeconds = Math.Max(0, minDurationSeconds);
            MaxDurationSeconds = Math.Max(Math.Max(1, MinDurationSeconds), maxDurationSeconds <= 0 ? 600 : maxDurationSeconds);
            MaxRetries = Math.Max(0, maxRetries);
        }

        public string TemplateId { get; }
        public int Version { get; }
        public IReadOnlyList<string> Tags { get; }
        public IReadOnlyList<string> Preconditions { get; }
        public IReadOnlyList<string> Interrupts { get; }
        public IReadOnlyList<ActionStepSpec> Steps { get; }
        public int MinDurationSeconds { get; }
        public int MaxDurationSeconds { get; }
        public int MaxRetries { get; }

        public bool HasTag(string tag)
        {
            for (int i = 0; i < Tags.Count; i++)
            {
                if (string.Equals(Tags[i], tag ?? string.Empty, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        public bool PreconditionsMet(AIPreconditionContext context, out string failedExpression)
        {
            failedExpression = string.Empty;
            for (int i = 0; i < Preconditions.Count; i++)
            {
                string expression = Preconditions[i];
                if (!PreconditionEvaluator.Evaluate(expression, context))
                {
                    failedExpression = expression;
                    return false;
                }
            }

            return true;
        }

        public bool ValidatePrimitives(PrimitiveActionRegistry registry, out string missingPrimitive)
        {
            missingPrimitive = string.Empty;
            if (registry == null)
            {
                missingPrimitive = "registry";
                return false;
            }

            for (int i = 0; i < Steps.Count; i++)
            {
                if (!registry.Contains(Steps[i].Name))
                {
                    missingPrimitive = Steps[i].Name;
                    return false;
                }
            }

            return true;
        }

        public ActionTemplateDefinition WithDecisionSteps(IEnumerable<ActionStep> decisionSteps)
        {
            if (decisionSteps == null)
            {
                return this;
            }

            var steps = new List<ActionStepSpec>();
            foreach (ActionStep decisionStep in decisionSteps)
            {
                if (decisionStep == null || string.IsNullOrWhiteSpace(decisionStep.ActionTemplateId))
                {
                    continue;
                }

                var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, string> pair in decisionStep.Params)
                {
                    parameters[pair.Key] = pair.Value;
                }

                steps.Add(new ActionStepSpec(decisionStep.ActionTemplateId, parameters));
            }

            return steps.Count == 0
                ? this
                : new ActionTemplateDefinition(TemplateId, Version, Tags, Preconditions, Interrupts, steps, MinDurationSeconds, MaxDurationSeconds, MaxRetries);
        }

        public static bool TryParseJson(string json, out ActionTemplateDefinition definition, out string error)
        {
            definition = null;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(json))
            {
                error = "Template JSON is empty.";
                return false;
            }

            try
            {
                ActionTemplateJson dto = JsonUtility.FromJson<ActionTemplateJson>(json);
                if (dto == null || string.IsNullOrWhiteSpace(dto.template_id))
                {
                    error = "template_id is required.";
                    return false;
                }

                var steps = new List<ActionStepSpec>();
                if (dto.steps != null)
                {
                    for (int i = 0; i < dto.steps.Length; i++)
                    {
                        if (!string.IsNullOrWhiteSpace(dto.steps[i]))
                        {
                            steps.Add(new ActionStepSpec(dto.steps[i], null));
                        }
                    }
                }

                definition = new ActionTemplateDefinition(
                    dto.template_id,
                    dto.version,
                    dto.tags,
                    dto.preconditions,
                    dto.interrupts,
                    steps,
                    dto.min_duration_sec,
                    dto.max_duration_sec,
                    dto.max_retries <= 0 ? 1 : dto.max_retries);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static IReadOnlyList<ActionTemplateDefinition> CreateM4Defaults()
        {
            return new[]
            {
                Template("survival.eat_owned_food", 1, TagArray("hunger_high", "food_owned"), NoConditions(), NoInterrupts(), StepArray("SelectOwnedFood", "UseItem"), 1, 120),
                Template("survival.acquire_food_hunt", 1, TagArray("hunger_high", "weapon_owned", "animal_available"), NoConditions(), NoInterrupts(), StepArray("FindAnimal", "HuntAnimal", "ButcherCarcass"), 5, 240),
                Template("survival.cook_meat", 1, TagArray("raw_meat_owned", "cooking_station"), Conditions("inventory.free_slots > 0"), NoInterrupts(), StepArray("MoveTo", "CookRawMeat"), 5, 180),
                Template("mining.acquire_iron", 1, TagArray("iron_needed", "pickaxe_owned", "earn"), Conditions("inventory.free_slots > 0"), NoInterrupts(), StepArray("FindResourceNode", "MoveTo", "MineIron"), 5, 180),
                Template("smithing.craft_stone_spear", 1, TagArray("no_weapon", "stone_owned", "wood_owned"), Conditions("inventory.free_slots > 0"), NoInterrupts(), StepArray("MoveTo", "CraftStoneSpear"), 5, 180),
                Template("development.unlock_spear", 1, TagArray("blueprint_locked", "materials_available"), NoConditions(), NoInterrupts(), StepArray("MoveTo", "ResearchIronSpear"), 20, 300),
                Template("smithing.craft_spear", 1, TagArray("weapon_needed", "blueprint_unlocked"), Conditions("inventory.free_slots > 0"), NoInterrupts(), StepArray("MoveTo", "CraftIronSpear"), 20, 300),
                Template("economy.visit_buyer", 1, TagArray("wanted_item", "buyer_available", "cash_available"), NoConditions(), NoInterrupts(), StepArray("PurchaseStub"), 1, 60),
                Template("economy.sell_surplus", 1, TagArray("inventory_overflow", "sellable_item", "sell"), Conditions("inventory.sellable_count > 0"), NoInterrupts(), StepArray("SellStub"), 1, 60),
                Template("inventory.discard_low_value", 1, TagArray("inventory_overflow", "no_buyer", "sell"), Conditions("inventory.used_slots >= inventory.capacity"), NoInterrupts(), StepArray("DiscardLowValue"), 1, 60),
                Template("cleaning.clean_nearby", 1, TagArray("cleanliness_high", "waste_nearby", "cleanup"), NoConditions(), NoInterrupts(), StepArray("FindWaste", "MoveTo", "CleanNearby"), 1, 120),
                Template("worldevent.join", 1, TagArray("event_available", "risk_acceptable"), NoConditions(), NoInterrupts(), StepArray("WorldEventStub"), 1, 60),
                Template("safety.idle_at_camp", 1, TagArray("fallback"), NoConditions(), NoInterrupts(), StepArray("MoveTo", "Wait"), 1, 60)
            };
        }

        private static ActionTemplateDefinition Template(string id, int version, string[] tags, string[] preconditions, string[] interrupts, string[] stepNames, int minDuration, int maxDuration)
        {
            var steps = new List<ActionStepSpec>();
            for (int i = 0; i < stepNames.Length; i++)
            {
                steps.Add(new ActionStepSpec(stepNames[i], null));
            }

            return new ActionTemplateDefinition(id, version, tags, preconditions, interrupts, steps, minDuration, maxDuration, 1);
        }

        private static IReadOnlyList<string> Copy(IEnumerable<string> values)
        {
            if (values == null)
            {
                return Array.Empty<string>();
            }

            var copy = new List<string>();
            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    copy.Add(value);
                }
            }

            return copy;
        }

        private static string[] TagArray(params string[] tags) => tags;
        private static string[] StepArray(params string[] steps) => steps;
        private static string[] Conditions(params string[] conditions) => conditions;
        private static string[] NoConditions() => Array.Empty<string>();
        private static string[] NoInterrupts() => Array.Empty<string>();
    }

    public readonly struct ActionStepSpec
    {
        public ActionStepSpec(string name, IDictionary<string, string> parameters)
        {
            Name = name ?? string.Empty;
            Parameters = parameters == null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(parameters, StringComparer.Ordinal);
        }

        public string Name { get; }
        public IReadOnlyDictionary<string, string> Parameters { get; }
    }

    public readonly struct AIPreconditionContext
    {
        public AIPreconditionContext(AIPersonalState personalState, AIInventorySummary inventorySummary)
        {
            PersonalState = personalState;
            InventorySummary = inventorySummary;
        }

        public AIPersonalState PersonalState { get; }
        public AIInventorySummary InventorySummary { get; }
    }

    public sealed class ActionTemplateCatalog
    {
        private readonly Dictionary<string, ActionTemplateDefinition> templates = new Dictionary<string, ActionTemplateDefinition>(StringComparer.Ordinal);

        public ActionTemplateCatalog(IEnumerable<ActionTemplateDefinition> definitions)
        {
            if (definitions == null)
            {
                return;
            }

            foreach (ActionTemplateDefinition definition in definitions)
            {
                if (definition != null && !string.IsNullOrWhiteSpace(definition.TemplateId))
                {
                    templates[definition.TemplateId] = definition;
                }
            }
        }

        public IReadOnlyCollection<ActionTemplateDefinition> Templates => templates.Values;

        public bool TryGet(string templateId, out ActionTemplateDefinition definition)
        {
            return templates.TryGetValue(templateId ?? string.Empty, out definition);
        }
    }

    internal static class PreconditionEvaluator
    {
        private static readonly string[] Operators = { ">=", "<=", "==", "!=", ">", "<" };

        public static bool Evaluate(string expression, AIPreconditionContext context)
        {
            if (string.IsNullOrWhiteSpace(expression))
            {
                return true;
            }

            string normalized = expression.Trim();
            for (int i = 0; i < Operators.Length; i++)
            {
                string op = Operators[i];
                int index = normalized.IndexOf(op, StringComparison.Ordinal);
                if (index <= 0)
                {
                    continue;
                }

                string leftToken = normalized.Substring(0, index).Trim();
                string rightToken = normalized.Substring(index + op.Length).Trim();
                if (!TryResolve(leftToken, context, out float left) || !TryResolve(rightToken, context, out float right))
                {
                    return false;
                }

                switch (op)
                {
                    case ">=": return left >= right;
                    case "<=": return left <= right;
                    case "==": return Math.Abs(left - right) < 0.0001f;
                    case "!=": return Math.Abs(left - right) >= 0.0001f;
                    case ">": return left > right;
                    case "<": return left < right;
                }
            }

            return string.Equals(normalized, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryResolve(string token, AIPreconditionContext context, out float value)
        {
            value = 0f;
            if (float.TryParse(token, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value))
            {
                return true;
            }

            switch (token)
            {
                case "inventory.free_slots": value = context.InventorySummary.FreeSlots; return true;
                case "inventory.used_slots": value = context.InventorySummary.UsedSlots; return true;
                case "inventory.capacity": value = context.InventorySummary.CapacitySlots; return true;
                case "inventory.sellable_count": value = context.InventorySummary.SellableCount; return true;
                case "personal.food_urgency": value = context.PersonalState == null ? 0f : context.PersonalState.Urgency.Food; return true;
                case "personal.cleanup_urgency": value = context.PersonalState == null ? 0f : context.PersonalState.Urgency.Cleanup; return true;
                case "personal.earn_urgency": value = context.PersonalState == null ? 0f : context.PersonalState.Urgency.Earn; return true;
            }

            return false;
        }
    }

    [Serializable]
    internal sealed class ActionTemplateJson
    {
        public string template_id;
        public int version;
        public string[] tags;
        public string[] preconditions;
        public string[] interrupts;
        public string[] steps;
        public int min_duration_sec;
        public int max_duration_sec;
        public int max_retries;
    }
}

