using System;

namespace SurvivalWorld.Server.AI
{
    public sealed class UtilityFallback
    {
        public const float LowUrgencyThreshold = 0.01f;

        public ActionTemplateDefinition SelectTemplate(AIPersonalState state, ActionTemplateCatalog catalog)
        {
            if (state == null || catalog == null)
            {
                return null;
            }

            FallbackNeed need = SelectNeed(state);
            switch (need)
            {
                case FallbackNeed.Food:
                    return FirstAvailable(catalog, "survival.eat_owned_food", "survival.acquire_food_hunt", "survival.cook_meat", "safety.idle_at_camp");
                case FallbackNeed.Cleanup:
                    return FirstAvailable(catalog, "cleaning.clean_nearby", "inventory.discard_low_value", "safety.idle_at_camp");
                case FallbackNeed.Earn:
                    return FirstAvailable(catalog, "mining.acquire_iron", "smithing.craft_stone_spear", "safety.idle_at_camp");
                case FallbackNeed.Sell:
                    return FirstAvailable(catalog, "economy.sell_surplus", "inventory.discard_low_value", "safety.idle_at_camp");
                default:
                    return FirstAvailable(catalog, "safety.idle_at_camp");
            }
        }

        public FallbackNeed SelectNeed(AIPersonalState state)
        {
            if (state == null)
            {
                return FallbackNeed.Idle;
            }

            float food = state.Urgency.Food;
            float cleanup = Math.Max(state.Urgency.Cleanup, AIPersonalState.Clamp01(state.Urgency.CleanlinessPressure));
            float earn = state.Urgency.Earn;
            float sell = state.SellableCount > 0 ? state.Urgency.Cleanup : 0f;
            float max = Math.Max(Math.Max(food, cleanup), Math.Max(earn, sell));
            if (max <= LowUrgencyThreshold)
            {
                return FallbackNeed.Idle;
            }

            if (NearlyEqual(food, max))
            {
                return FallbackNeed.Food;
            }

            if (NearlyEqual(cleanup, max))
            {
                return FallbackNeed.Cleanup;
            }

            if (NearlyEqual(earn, max))
            {
                return FallbackNeed.Earn;
            }

            return FallbackNeed.Sell;
        }

        private static ActionTemplateDefinition FirstAvailable(ActionTemplateCatalog catalog, params string[] templateIds)
        {
            for (int i = 0; i < templateIds.Length; i++)
            {
                if (catalog.TryGet(templateIds[i], out ActionTemplateDefinition definition))
                {
                    return definition;
                }
            }

            return null;
        }

        private static bool NearlyEqual(float left, float right)
        {
            return Math.Abs(left - right) < 0.0001f;
        }
    }

    public enum FallbackNeed
    {
        Idle = 0,
        Food = 1,
        Cleanup = 2,
        Earn = 3,
        Sell = 4
    }
}
