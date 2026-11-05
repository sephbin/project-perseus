using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Newtonsoft.Json;
using ProjectPerseus.logging;
using ProjectPerseus.models;

namespace ProjectPerseus.web
{
    /// <summary>
    /// The element part of an upload, serialised exactly once. ElementDelta reads parameters and geometry lazily
    /// while being serialised, so serialising is where the "collection" work actually happens. It used to happen up
    /// to three times per sync (debug dump, size measurement, then again per chunk); now each element is serialised
    /// once here, with progress reported per element, and the dump and the upload chunks are assembled from these
    /// JSON fragments. Must be built on Revit's main thread (the serialisation calls the Revit API); the result is
    /// plain strings.
    /// </summary>
    internal class ElementPayload
    {
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore };

        public readonly List<string> Elements = new List<string>();
        public long TotalChars;
        public int Failed;

        /// <summary>
        /// Serialises every delta, reporting progress as "<paramref name="label"/>" (per element, throttled). An element
        /// that fails to serialise is skipped and logged instead of the whole payload being lost.
        /// </summary>
        internal static ElementPayload Build(IList<ElementDelta> deltas, string label, Action<int, int> progress = null)
        {
            var payload = new ElementPayload();
            var clock = Stopwatch.StartNew();
            int total = deltas.Count;
            for (int i = 0; i < total; i++)
            {
                try
                {
                    string json = JsonConvert.SerializeObject(deltas[i], Formatting.None, Settings);
                    payload.Elements.Add(json);
                    payload.TotalChars += json.Length;
                }
                catch (Exception ex)
                {
                    if (++payload.Failed <= 5)
                        Log.Warn($"[ElementPayload] skipped element {SafeId(deltas[i])}: {ex.Message}");
                }
                progress?.Invoke(i + 1, total);
            }
            Log.Info($"[ElementPayload] {label}: {payload.Elements.Count} element(s), {payload.TotalChars / 1024} KB in " +
                     $"{clock.ElapsedMilliseconds} ms{(payload.Failed > 0 ? $", {payload.Failed} skipped (serialisation failed)" : "")}");
            return payload;
        }

        private static string SafeId(ElementDelta d)
        {
            try { return d?.Element?.Id.ToString() ?? "?"; } catch { return "?"; }
        }

        /// <summary>
        /// Full request/dump JSON: <paramref name="header"/>'s properties plus "elements" (fragments
        /// [<paramref name="from"/>, <paramref name="from"/>+<paramref name="count"/>)) and "deletedElements".
        /// </summary>
        internal string Envelope(object header, int from, int count, IList<long> deleted)
        {
            string head = JsonConvert.SerializeObject(header, Formatting.None, Settings);
            var sb = new StringBuilder(head.Length + 64 + (int)Math.Min(int.MaxValue / 2, TotalChars));
            // header is an object: drop its closing brace and append the two arrays.
            sb.Append(head, 0, head.Length - 1);
            if (head.Length > 2) sb.Append(',');
            sb.Append("\"elements\":[");
            for (int i = from; i < from + count; i++)
            {
                if (i > from) sb.Append(',');
                sb.Append(Elements[i]);
            }
            sb.Append("],\"deletedElements\":");
            sb.Append(JsonConvert.SerializeObject(deleted ?? new List<long>()));
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>Splits the fragments into consecutive runs whose JSON stays under <paramref name="maxChars"/>.</summary>
        internal List<(int From, int Count)> Chunks(long maxChars)
        {
            var chunks = new List<(int, int)>();
            int start = 0;
            long size = 0;
            for (int i = 0; i < Elements.Count; i++)
            {
                long len = Elements[i].Length + 1;
                if (i > start && size + len > maxChars)
                {
                    chunks.Add((start, i - start));
                    start = i;
                    size = 0;
                }
                size += len;
            }
            chunks.Add((start, Elements.Count - start)); // always at least one (possibly empty: deletions only)
            return chunks;
        }
    }
}
