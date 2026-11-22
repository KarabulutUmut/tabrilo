using System.Globalization;
using System.Text.Json.Nodes;
using Tabrilo.Core;

namespace Tabrilo.Processing;

public sealed partial class Pdfs
{
    public async Task<string> AssembleAsync(IReadOnlyList<PdfPageChoice> pages, string folder, string stem,
        CancellationToken token, bool allowPageExtrasLoss = false)
    {
        using var session = new AssemblySession(this, allowPageExtrasLoss);
        return await session.Write(pages, folder, stem, token);
    }

    public async Task<string> SplitAsync(string path, int[][] groups, string folder, bool allowPageExtrasLoss, CancellationToken token)
    {
        using var session = new AssemblySession(this, allowPageExtrasLoss);
        async Task<string> Part(string to, int index, CancellationToken ct) =>
            await session.Write(groups[index].Select(number => new PdfPageChoice(path, number)).ToArray(), to,
                groups.Length == 1 ? Stem(path, "split") : $"Part {index + 1:D4}", ct);
        return groups.Length == 1 ? await Part(folder, 0, token) :
            await OutputGroup.WriteAsync(folder, Stem(path, "split"), groups.Length, Part, token);
    }

    private sealed class AssemblySession(Pdfs pdf, bool allowLoss) : IDisposable
    {
        private readonly Dictionary<string, Details> inputs = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<FileStream> leases = [];

        public async Task<string> Write(IReadOnlyList<PdfPageChoice> pages, string folder, string stem, CancellationToken token)
        {
            if (pages.Count is 0 or > 2000) throw new ArgumentException("Choose between 1 and 2000 pages.");
            var options = new List<string> { "--empty", "--pages" };
            foreach (var page in pages)
            {
                var path = Path.GetFullPath(page.Path);
                if (!inputs.TryGetValue(path, out var details))
                {
                    leases.Add(new(path, FileMode.Open, FileAccess.Read, FileShare.Read));
                    details = await pdf.ReadDetails(path, null, token);
                    CheckTransform(details.Info, true);
                    if (details.Info.PageExtras && !allowLoss)
                        throw new NotSupportedException("Confirm removal of document tags, name indexes and initial view before assembling pages.");
                    inputs.Add(path, details);
                }
                if (page.Number < 1 || page.Number > details.Info.Pages || page.Rotation is not (0 or 90 or 180 or 270))
                    throw new ArgumentException("Invalid page or rotation.");
                options.Add(path);
                options.Add(page.Number.ToString(CultureInfo.InvariantCulture));
            }
            options.Add("--");
            foreach (var item in pages.Select((p, i) => (p.Rotation, Index: i + 1)).Where(p => p.Rotation != 0).GroupBy(p => p.Rotation))
                options.Add($"--rotate=+{item.Key}:{string.Join(',', item.Select(p => p.Index))}");
            return await OutputFiles.WriteAsync(folder, stem, ".pdf", async (output, ct) =>
            {
                var draft = output + ".draft";
                var patch = output + ".json";
                try
                {
                    await pdf.worker.RunAsync(["@-"], string.Join('\n', options.Append(draft)), ct);
                    var result = await pdf.ReadDetails(draft, null, ct);
                    var changes = Navigation(pages, result, ct);
                    if (changes.Count == 0) { File.Move(draft, output, true); return; }
                    var json = new JsonObject { ["qpdf"] = new JsonArray(result.Header.DeepClone(), changes) };
                    using (var stream = new FileStream(patch, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        await System.Text.Json.JsonSerializer.SerializeAsync(stream, json, cancellationToken: ct);
                    await pdf.worker.RunAsync([draft, "--update-from-json=" + patch, output], null, ct);
                }
                finally { File.Delete(draft); File.Delete(patch); }
            }, (path, ct) => pdf.Validate(path, pages.Count, ct), token);
        }

        private JsonObject Navigation(IReadOnlyList<PdfPageChoice> selection, Details result, CancellationToken token)
        {
            var changes = new JsonObject();
            var mapping = new Dictionary<(Details, string), string>();
            for (var i = 0; i < selection.Count; i++)
            {
                var page = selection[i];
                var source = inputs[Path.GetFullPath(page.Path)];
                mapping.TryAdd((source, source.Pages[page.Number - 1]!["object"]!.ToString()), result.Pages[i]!["object"]!.ToString());
            }
            var next = result.Header["maxobjectid"]!.GetValue<int>();
            string NewRef() => $"{++next} 0 R";
            void Put(string reference, JsonObject value) => changes["obj:" + reference] = new JsonObject { ["value"] = value };
            var outlines = new List<(string Title, string Target)>();
            foreach (var source in inputs.Values)
                foreach (var outline in Flatten(source.Outlines))
                    if (outline["destpageposfrom1"] is JsonValue number && number.TryGetValue<int>(out var page) &&
                        page >= 1 && page <= source.Pages.Count &&
                        mapping.TryGetValue((source, source.Pages[page - 1]!["object"]!.ToString()), out var target))
                        outlines.Add((outline["title"]?.ToString() ?? "", target));

            if (outlines.Count > 0)
            {
                var root = NewRef();
                var refs = outlines.Select(_ => NewRef()).ToArray();
                for (var i = 0; i < refs.Length; i++)
                {
                    var entry = new JsonObject { ["/Title"] = "u:" + outlines[i].Title, ["/Parent"] = root,
                        ["/Dest"] = new JsonArray(outlines[i].Target, "/Fit") };
                    if (i > 0) entry["/Prev"] = refs[i - 1];
                    if (i + 1 < refs.Length) entry["/Next"] = refs[i + 1];
                    Put(refs[i], entry);
                }
                Put(root, new JsonObject { ["/Type"] = "/Outlines", ["/Count"] = refs.Length, ["/First"] = refs[0], ["/Last"] = refs[^1] });
                var catalogRef = result.Trailer["/Root"]!;
                var catalog = result.Resolve(catalogRef)!.DeepClone().AsObject();
                catalog["/Outlines"] = root;
                Put(catalogRef.ToString(), catalog);
            }

            for (var i = 0; i < selection.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var page = selection[i];
                var source = inputs[Path.GetFullPath(page.Path)];
                var original = source.Resolve(source.Pages[page.Number - 1]!["object"])!.AsObject();
                if (source.Resolve(original["/Annots"]) is not JsonArray annotations) continue;
                var outputRef = result.Pages[i]!["object"]!;
                var output = result.Resolve(outputRef)!.DeepClone().AsObject();
                if (result.Resolve(output["/Annots"])?.DeepClone() is not JsonArray links || links.Count != annotations.Count)
                    throw new InvalidDataException("Page annotations could not be matched safely.");
                for (var j = 0; j < annotations.Count; j++)
                {
                    if (source.Resolve(annotations[j]) is not JsonObject link) continue;
                    var action = source.Resolve(link["/A"]) as JsonObject;
                    var destination = link["/Dest"] ?? (IsName(action?["/S"], "/GoTo") ? action!["/D"] : null);
                    if (destination == null) continue;
                    var dest = ResolveDestination(source, destination);
                    var updated = result.Resolve(links[j])!.DeepClone().AsObject();
                    updated.Remove("/A");
                    updated.Remove("/Dest");
                    if (dest is { Count: > 0 } && mapping.TryGetValue((source, dest[0]?.ToString() ?? ""), out var mapped))
                    {
                        var array = dest.DeepClone().AsArray();
                        array[0] = mapped;
                        updated["/Dest"] = array;
                    }
                    updated["/P"] = outputRef.DeepClone();
                    var reference = NewRef();
                    Put(reference, updated);
                    links[j] = reference;
                }
                output["/Annots"] = links;
                Put(outputRef.ToString(), output);
            }
            return changes;
        }

        private static IEnumerable<JsonNode> Flatten(JsonArray nodes)
        {
            foreach (var node in nodes.OfType<JsonObject>())
            {
                yield return node;
                if (node["kids"] is JsonArray children)
                    foreach (var child in Flatten(children)) yield return child;
            }
        }

        private static JsonArray? ResolveDestination(Details source, JsonNode value)
        {
            var dest = source.Resolve(value);
            if (dest is JsonArray direct) return direct;
            var name = dest?.ToString() ?? "";
            var catalog = source.Resolve(source.Trailer["/Root"])!;
            var legacy = source.Resolve(catalog["/Dests"]);
            var target = legacy is JsonObject dictionary ? source.Resolve(dictionary["/" + name.TrimStart('/')]) : null;
            if (target is null && source.Resolve(catalog["/Names"]) is JsonObject indexes)
            {
                var pending = new Stack<JsonNode>();
                var seen = new HashSet<JsonNode>();
                if (source.Resolve(indexes["/Dests"]) is { } root) pending.Push(root);
                while (pending.TryPop(out var tree) && target is null)
                {
                    if (!seen.Add(tree)) continue;
                    if (source.Resolve(tree["/Names"]) is JsonArray names)
                        for (var i = 0; i + 1 < names.Count; i += 2)
                            if (names[i]?.ToString() == name) { target = source.Resolve(names[i + 1]); break; }
                    if (source.Resolve(tree["/Kids"]) is JsonArray kids)
                        foreach (var kid in kids) if (source.Resolve(kid) is JsonObject child) pending.Push(child);
                }
            }
            return target is JsonObject wrapper ? source.Resolve(wrapper["/D"]) as JsonArray : target as JsonArray;
        }

        public void Dispose() { foreach (var lease in leases) lease.Dispose(); }
    }
}
