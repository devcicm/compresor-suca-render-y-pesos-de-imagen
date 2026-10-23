using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SUCA_Compressor
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum LlmJsonParseStatus
    {
        Success,
        Partial,
        Failed
    }

    public sealed class ParsedLlmJsonResponse
    {
        public LlmJsonParseStatus Status { get; set; } = LlmJsonParseStatus.Failed;
        public string RawContent { get; set; } = "";
        public string SanitizedContent { get; set; } = "";
        public string? JsonPayload { get; set; }
        public string? Question { get; set; }
        public string? Answer { get; set; }
        public double? Confidence { get; set; }
        public string? Format { get; set; }
        public List<string> Warnings { get; set; } = new();
    }

    public static class LmStudioJsonResponseParser
    {
        private static readonly Regex ThinkBlockRegex = new(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex FencedBlockRegex = new(@"```(?:json)?\s*(.*?)\s*```", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex TrailingCommaRegex = new(@",(?=\s*[}\]])", RegexOptions.Compiled);

        public static ParsedLlmJsonResponse Parse(string rawContent, string? finishReason = null)
        {
            var parsed = new ParsedLlmJsonResponse
            {
                RawContent = rawContent ?? string.Empty
            };

            string cleaned = Sanitize(rawContent);
            parsed.SanitizedContent = cleaned;

            if (string.IsNullOrWhiteSpace(cleaned))
            {
                parsed.Status = string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase)
                    ? LlmJsonParseStatus.Partial
                    : LlmJsonParseStatus.Failed;
                parsed.Warnings.Add("Empty content.");
                return parsed;
            }

            foreach (string candidate in BuildCandidates(cleaned))
            {
                if (TryMapKnownSchema(candidate, parsed))
                {
                    parsed.Status = LlmJsonParseStatus.Success;
                    return parsed;
                }
            }

            if (LooksPartial(cleaned, finishReason))
            {
                parsed.Status = LlmJsonParseStatus.Partial;
                parsed.Warnings.Add("Detected truncated or incomplete JSON.");
            }
            else
            {
                parsed.Status = LlmJsonParseStatus.Failed;
                parsed.Warnings.Add("Could not extract a valid JSON object.");
            }

            return parsed;
        }

        private static IEnumerable<string> BuildCandidates(string cleaned)
        {
            yield return cleaned;

            string withoutThink = ThinkBlockRegex.Replace(cleaned, string.Empty).Trim();
            if (!string.Equals(withoutThink, cleaned, StringComparison.Ordinal))
                yield return withoutThink;

            Match fenced = FencedBlockRegex.Match(withoutThink);
            if (fenced.Success)
                yield return fenced.Groups[1].Value.Trim();

            if (TryExtractFirstJsonBlock(withoutThink, out string json))
                yield return json;

            string repaired = RepairJson(withoutThink);
            if (!string.Equals(repaired, withoutThink, StringComparison.Ordinal))
                yield return repaired;
        }

        private static bool TryMapKnownSchema(string candidate, ParsedLlmJsonResponse parsed)
        {
            if (!TryParseJson(candidate, out JsonElement root, out string normalized))
                return false;

            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
                root = root[0];

            if (root.ValueKind != JsonValueKind.Object)
                return false;

            parsed.JsonPayload = normalized;
            parsed.Question = GetFirstString(root, "question", "prompt", "query", "q");
            parsed.Answer = GetFirstString(root, "answer", "response", "result", "text", "content");
            parsed.Format = GetFirstString(root, "format", "type", "content_type");
            parsed.Confidence = GetFirstDouble(root, "confidence", "score", "probability");

            if (parsed.Answer is null && TryGetNestedObject(root, out JsonElement nested))
            {
                parsed.Question ??= GetFirstString(nested, "question", "prompt", "query", "q");
                parsed.Answer ??= GetFirstString(nested, "answer", "response", "result", "text", "content");
                parsed.Format ??= GetFirstString(nested, "format", "type", "content_type");
                parsed.Confidence ??= GetFirstDouble(nested, "confidence", "score", "probability");
            }

            if (string.IsNullOrWhiteSpace(parsed.Answer))
                return false;

            parsed.Format ??= "json";
            return true;
        }

        private static bool TryParseJson(string candidate, out JsonElement root, out string normalized)
        {
            normalized = string.Empty;
            root = default;

            try
            {
                using JsonDocument doc = JsonDocument.Parse(candidate);
                root = doc.RootElement.Clone();
                normalized = doc.RootElement.GetRawText();
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static bool TryExtractFirstJsonBlock(string text, out string json)
        {
            json = string.Empty;
            int start = -1;
            int depth = 0;
            bool inString = false;
            bool escaped = false;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];

                if (start < 0)
                {
                    if (c == '{' || c == '[')
                    {
                        start = i;
                        depth = 1;
                    }
                    continue;
                }

                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (c == '\\')
                    {
                        escaped = true;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }
                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                    continue;
                }

                if (c == '{' || c == '[') depth++;
                if (c == '}' || c == ']') depth--;

                if (depth == 0)
                {
                    json = text.Substring(start, i - start + 1).Trim();
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetNestedObject(JsonElement root, out JsonElement nested)
        {
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object)
                {
                    nested = property.Value;
                    return true;
                }
            }

            nested = default;
            return false;
        }

        private static string? GetFirstString(JsonElement root, params string[] names)
        {
            foreach (string name in names)
            {
                if (TryGetPropertyIgnoreCase(root, name, out JsonElement value))
                {
                    if (value.ValueKind == JsonValueKind.String)
                        return value.GetString();

                    if (value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                        return value.ToString();
                }
            }

            return null;
        }

        private static double? GetFirstDouble(JsonElement root, params string[] names)
        {
            foreach (string name in names)
            {
                if (!TryGetPropertyIgnoreCase(root, name, out JsonElement value))
                    continue;

                if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number))
                    return number;

                if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), out double parsed))
                    return parsed;
            }

            return null;
        }

        private static bool TryGetPropertyIgnoreCase(JsonElement root, string name, out JsonElement value)
        {
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }

            value = default;
            return false;
        }

        private static string Sanitize(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return string.Empty;

            return raw.Replace("\uFEFF", string.Empty).Trim();
        }

        private static string RepairJson(string text)
        {
            string repaired = text.Trim();
            repaired = TrailingCommaRegex.Replace(repaired, string.Empty);
            return repaired;
        }

        private static bool LooksPartial(string text, string? finishReason)
        {
            if (string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase))
                return true;

            int opens = 0;
            int closes = 0;
            foreach (char c in text)
            {
                if (c == '{' || c == '[') opens++;
                if (c == '}' || c == ']') closes++;
            }

            return opens > closes;
        }
    }
}
