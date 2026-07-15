using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SurvivalWorld.Shared.MasterData;

namespace SurvivalWorld.Shared.Events
{
    public static class JsonPayload
    {
        public static string Object(params JsonField[] fields)
        {
            var builder = new StringBuilder();
            builder.Append('{');
            bool wrote = false;
            for (int i = 0; i < fields.Length; i++)
            {
                if (fields[i].Skip)
                {
                    continue;
                }

                if (wrote)
                {
                    builder.Append(',');
                }

                builder.Append('"');
                builder.Append(Escape(fields[i].Name));
                builder.Append("\":");
                builder.Append(fields[i].RawValue);
                wrote = true;
            }

            builder.Append('}');
            return builder.ToString();
        }

        public static JsonField Field(string name, string value)
        {
            return new JsonField(name, Quote(value));
        }

        public static JsonField Field(string name, int value)
        {
            return new JsonField(name, value.ToString(CultureInfo.InvariantCulture));
        }

        public static JsonField Field(string name, long value)
        {
            return new JsonField(name, value.ToString(CultureInfo.InvariantCulture));
        }

        public static JsonField Field(string name, float value)
        {
            return new JsonField(name, value.ToString(CultureInfo.InvariantCulture));
        }

        public static JsonField Field(string name, bool value)
        {
            return new JsonField(name, value ? "true" : "false");
        }

        public static JsonField Raw(string name, string rawValue)
        {
            return new JsonField(name, string.IsNullOrWhiteSpace(rawValue) ? "null" : rawValue);
        }

        public static JsonField OptionalField(string name, string value)
        {
            return new JsonField(name, Quote(value), string.IsNullOrWhiteSpace(value));
        }

        public static string Quote(string value)
        {
            return "\"" + Escape(value ?? string.Empty) + "\"";
        }

        public static string ItemStackArray(IEnumerable<ItemStack> stacks, bool includeInstanceIds = true)
        {
            var builder = new StringBuilder();
            builder.Append('[');
            bool wrote = false;
            if (stacks != null)
            {
                foreach (ItemStack stack in stacks)
                {
                    if (wrote)
                    {
                        builder.Append(',');
                    }

                    builder.Append(ItemStackObject(stack, includeInstanceIds));
                    wrote = true;
                }
            }

            builder.Append(']');
            return builder.ToString();
        }

        public static string ItemStackObject(ItemStack stack, bool includeInstanceId)
        {
            return Object(
                Field("item_definition_id", stack.ItemDefinitionId),
                Field("quantity", stack.Quantity),
                OptionalField("item_instance_id", includeInstanceId ? stack.ItemInstanceId : string.Empty));
        }

        public static string Vector3(float x, float y, float z)
        {
            return Object(Field("x", x), Field("y", y), Field("z", z));
        }

        public static string StringArray(IEnumerable<string> values)
        {
            var builder = new StringBuilder();
            builder.Append('[');
            bool wrote = false;
            if (values != null)
            {
                foreach (string value in values)
                {
                    if (wrote)
                    {
                        builder.Append(',');
                    }

                    builder.Append(Quote(value));
                    wrote = true;
                }
            }

            builder.Append(']');
            return builder.ToString();
        }

        public static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
        }
    }

    public readonly struct JsonField
    {
        public JsonField(string name, string rawValue, bool skip = false)
        {
            Name = name ?? string.Empty;
            RawValue = rawValue ?? "null";
            Skip = skip;
        }

        public string Name { get; }
        public string RawValue { get; }
        public bool Skip { get; }
    }
}
