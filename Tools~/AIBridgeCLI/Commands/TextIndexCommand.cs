using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AIBridge.Runtime.Internal;
using AIBridgeCLI.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace AIBridgeCLI.Commands
{
    public static class TextIndexCommand
    {
        private const string IndexDirectoryName = "text-index";
        private const string ConfigFileName = "config.json";
        private const string ManifestFileName = "manifest.json";
        private const string FileGramsDirectoryName = "file-grams";
        private const string PostingsDirectoryName = "postings";
        private const int SchemaVersion = 1;
        private const int DefaultMaxFileBytes = 1024 * 1024;
        private const int DefaultMaxResults = 100;
        private const int MaxResultsLimit = 10000;
        private const int PreviewLimit = 300;
        private const int BinaryProbeBytes = 4096;
        private const int ShardCount = 16;
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);
        private static readonly string[] DefaultIncludePaths = { "Assets", "Packages", "ProjectSettings", ".codex", ".aibridge/plan" };
        private static readonly string[] DefaultExcludePaths = { "Library", "Temp", "Logs", "Build", "obj", "bin", ".git", ".svn" };
        private static readonly string[] DefaultIncludeExtensions =
        {
            ".cs", ".asmdef", ".json", ".md", ".txt", ".xml", ".yaml", ".yml", ".prefab", ".unity",
            ".asset", ".mat", ".shader", ".uss", ".uxml"
        };
        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            ContractResolver = new CamelCasePropertyNamesContractResolver()
        };

        public static int Execute(string action, Dictionary<string, string> options, List<string> extraArgs, OutputMode outputMode)
        {
            var stopwatch = Stopwatch.StartNew();
            JObject result;
            TextIndexContext context = null;
            try
            {
                var normalizedAction = NormalizeAction(action);
                context = TextIndexContext.Resolve(options);
                TouchTextIndexLastUsed(context);
                switch (normalizedAction)
                {
                    case "status":
                        result = BuildStatus(context);
                        break;
                    case "build":
                        result = Build(context);
                        break;
                    case "search":
                        result = Search(context, options, extraArgs);
                        break;
                    case "reset":
                        result = Reset(context);
                        break;
                    default:
                        result = BuildFailure(context, "Unsupported text_index action: " + action, "unsupported_action");
                        break;
                }
            }
            catch (Exception ex)
            {
                result = BuildFailure(context, "text_index failed: " + ex.Message, "cli_error");
            }

            result["executionTime"] = stopwatch.ElapsedMilliseconds;
            Print(result, outputMode);
            return result.Value<bool>("success") ? 0 : 1;
        }

        private static string NormalizeAction(string action)
        {
            return string.IsNullOrWhiteSpace(action) ? "status" : action.Trim().ToLowerInvariant();
        }

        private static void TouchTextIndexLastUsed(TextIndexContext context)
        {
            try
            {
                if (context != null)
                {
                    AIBridgeCacheCleanup.TouchLastUsed(context.IndexDirectory);
                }
            }
            catch
            {
            }
        }

        private static JObject BuildStatus(TextIndexContext context)
        {
            var manifest = ReadManifest(context);
            var indexExists = File.Exists(context.ManifestPath);
            var integrityError = manifest == null ? null : GetIndexIntegrityError(context, manifest);
            var corrupt = (indexExists && manifest == null) || !string.IsNullOrWhiteSpace(integrityError);
            var stale = corrupt || manifest == null || ComputeStale(context, manifest);
            var indexSizeBytes = ResolveIndexSizeBytes(context, manifest, indexExists, corrupt);
            var suggestions = corrupt
                ? new JArray("Run text_index reset, then text_index build.")
                : null;
            return new JObject
            {
                ["success"] = true,
                ["source"] = "text-index",
                ["indexed"] = manifest != null,
                ["semantic"] = false,
                ["stale"] = stale,
                ["projectRoot"] = context.ProjectRoot,
                ["indexPath"] = context.IndexDirectory,
                ["configPath"] = context.ConfigPath,
                ["manifestPath"] = context.ManifestPath,
                ["fileCount"] = manifest == null ? 0 : manifest.Files.Count,
                ["indexSizeBytes"] = indexSizeBytes,
                ["excludedCount"] = manifest == null ? 0 : manifest.ExcludedCount,
                ["schemaVersion"] = manifest == null ? SchemaVersion : manifest.SchemaVersion,
                ["lastBuiltUtc"] = manifest == null ? null : manifest.BuiltAtUtc,
                ["message"] = corrupt
                    ? integrityError ?? "Text index manifest is unreadable or incompatible. Run text_index reset, then text_index build."
                    : manifest == null
                        ? "Text index has not been built. Run text_index build."
                        : null,
                ["errorCode"] = corrupt ? "index_corrupt" : null,
                ["suggestions"] = suggestions
            };
        }

        private static JObject Build(TextIndexContext context)
        {
            Directory.CreateDirectory(context.IndexDirectory);

            var previousManifest = ReadManifest(context);
            var previousByPath = previousManifest == null
                ? new Dictionary<string, TextIndexFileRecord>(StringComparer.OrdinalIgnoreCase)
                : previousManifest.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
            var fileIdsInUse = new HashSet<int>();
            var nextId = previousManifest == null || previousManifest.Files.Count == 0
                ? 1
                : previousManifest.Files.Max(file => file.Id) + 1;
            var files = new List<TextIndexFileRecord>();
            var postings = CreatePostingShards();
            var excluded = new TextIndexExcludedCounts();
            var changedFiles = 0;
            var reusedFiles = 0;
            var fileGramsSizeBytes = 0L;

            EnsureIndexSubdirectories(context);
            var configSizeBytes = WriteConfig(context);

            foreach (var fullPath in EnumerateCandidateFiles(context, excluded))
            {
                var relativePath = ToIndexPath(MakeRelativePath(context.ProjectRoot, fullPath));
                FileInfo info;
                try
                {
                    info = new FileInfo(fullPath);
                }
                catch
                {
                    excluded.Unreadable++;
                    continue;
                }

                if (info.Length > context.Config.MaxFileBytes)
                {
                    excluded.TooLarge++;
                    continue;
                }

                if (LooksBinary(fullPath))
                {
                    excluded.Binary++;
                    continue;
                }

                TextIndexFileRecord previous;
                var reused = previousByPath.TryGetValue(relativePath, out previous)
                             && previous.Size == info.Length
                             && previous.LastWriteTimeUtcTicks == info.LastWriteTimeUtc.Ticks
                             && File.Exists(GetFileGramsPath(context, previous.Id));

                string[] grams;
                string hash;
                string encodingName;
                if (reused && TryReadFileGrams(context, previous.Id, out grams))
                {
                    hash = previous.Hash;
                    encodingName = previous.Encoding;
                    reusedFiles++;
                }
                else
                {
                    string text;
                    if (!TryReadText(fullPath, out text, out encodingName))
                    {
                        excluded.Unreadable++;
                        continue;
                    }

                    hash = ComputeHash(Encoding.UTF8.GetBytes(text));
                    grams = ExtractTrigrams(text).ToArray();
                    changedFiles++;
                }

                var id = reused ? previous.Id : nextId++;
                fileIdsInUse.Add(id);
                fileGramsSizeBytes += WriteFileGrams(context, id, grams);
                AddPostings(postings, id, grams);
                files.Add(new TextIndexFileRecord
                {
                    Id = id,
                    Path = relativePath,
                    Size = info.Length,
                    LastWriteTimeUtcTicks = info.LastWriteTimeUtc.Ticks,
                    Hash = hash,
                    Encoding = encodingName
                });
            }

            RemoveStaleFileGrams(context, fileIdsInUse);
            var manifest = new TextIndexManifest
            {
                SchemaVersion = SchemaVersion,
                BuiltAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ProjectRoot = context.ProjectRoot,
                ConfigHash = ComputeConfigHash(context.Config),
                Files = files.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToList(),
                ExcludedCount = excluded.Total,
                Excluded = excluded
            };
            var postingsSizeBytes = WritePostings(context, postings);
            var stableIndexSizeBytes = GetFileLength(Path.Combine(context.IndexDirectory, AIBridgeCacheCleanup.LastUsedMarkerFileName))
                + configSizeBytes
                + fileGramsSizeBytes
                + postingsSizeBytes;
            var indexSizeBytes = WriteManifestWithIndexSize(context, manifest, stableIndexSizeBytes);

            return new JObject
            {
                ["success"] = true,
                ["source"] = "text-index",
                ["indexed"] = true,
                ["semantic"] = false,
                ["stale"] = false,
                ["projectRoot"] = context.ProjectRoot,
                ["indexPath"] = context.IndexDirectory,
                ["configPath"] = context.ConfigPath,
                ["fileCount"] = manifest.Files.Count,
                ["changedFileCount"] = changedFiles,
                ["reusedFileCount"] = reusedFiles,
                ["indexSizeBytes"] = indexSizeBytes,
                ["excludedCount"] = manifest.ExcludedCount,
                ["excluded"] = JObject.FromObject(excluded, JsonSerializer.Create(JsonSettings))
            };
        }

        private static JObject Search(TextIndexContext context, Dictionary<string, string> options, List<string> extraArgs)
        {
            if (options.ContainsKey("query"))
            {
                return BuildFailure(context, "Unsupported parameter: --query. Usage: text_index search \"literal text\"", "unsupported_query_option");
            }

            var query = extraArgs != null && extraArgs.Count > 0 ? extraArgs[0] : null;
            if (string.IsNullOrEmpty(query))
            {
                return BuildFailure(context, "Missing search text. Usage: text_index search \"literal text\"", "missing_query");
            }

            var regex = ResolveBool(options, "regex", false);
            if (regex && !context.Config.EnableRegex)
            {
                return BuildFailure(context, "Regex search is disabled in text-index config.", "regex_disabled");
            }

            var maxResults = Math.Min(MaxResultsLimit, Math.Max(1, ResolveInt(options, "max-results", DefaultMaxResults)));
            var manifest = ReadManifest(context);
            if (manifest == null)
            {
                return BuildFailure(context, "Text index has not been built. Run text_index build.", "index_missing");
            }

            var integrityError = GetIndexIntegrityError(context, manifest);
            var stale = !string.IsNullOrWhiteSpace(integrityError) || ComputeStale(context, manifest);
            if (stale && context.Config.AutoRefresh)
            {
                var build = Build(context);
                if (!build.Value<bool>("success"))
                {
                    return build;
                }

                manifest = ReadManifest(context);
                integrityError = manifest == null ? "Text index manifest is unreadable or incompatible." : GetIndexIntegrityError(context, manifest);
                stale = false;
            }

            if (!string.IsNullOrWhiteSpace(integrityError))
            {
                return BuildFailure(context, integrityError + " Run text_index reset, then text_index build.", "index_corrupt");
            }

            if (stale)
            {
                return BuildFailure(context, "Text index is stale. Run text_index build or enable autoRefresh before searching.", "index_stale");
            }

            var pathFilter = ToIndexPath(ResolveString(options, "path", null));
            var globs = ResolveGlobFilters(options);
            var candidates = GetCandidateIds(context, manifest, query, regex);
            var items = new JArray();
            var searchedFileCount = 0;
            try
            {
                foreach (var record in manifest.Files)
                {
                    if (!candidates.Contains(record.Id))
                    {
                        continue;
                    }

                    if (!MatchesPathFilters(record.Path, pathFilter, globs))
                    {
                        continue;
                    }

                    searchedFileCount++;
                    var fullPath = Path.Combine(context.ProjectRoot, FromIndexPath(record.Path));
                    if (!File.Exists(fullPath))
                    {
                        continue;
                    }

                    AddMatches(fullPath, record.Path, query, regex, maxResults, items);
                    if (items.Count >= maxResults)
                    {
                        break;
                    }
                }
            }
            catch (ArgumentException ex)
            {
                return BuildFailure(context, ex.Message, regex ? "regex_error" : "search_error");
            }


            return new JObject
            {
                ["success"] = true,
                ["source"] = "text-index",
                ["indexed"] = true,
                ["semantic"] = false,
                ["stale"] = stale,
                ["projectRoot"] = context.ProjectRoot,
                ["indexPath"] = context.IndexDirectory,
                ["fileCount"] = manifest.Files.Count,
                ["indexSizeBytes"] = GetDirectorySize(context.IndexDirectory),
                ["excludedCount"] = manifest.ExcludedCount,
                ["query"] = query,
                ["regex"] = regex,
                ["candidateFileCount"] = candidates.Count,
                ["searchedFileCount"] = searchedFileCount,
                ["maxResults"] = maxResults,
                ["items"] = items
            };
        }

        private static JObject Reset(TextIndexContext context)
        {
            if (Directory.Exists(context.IndexDirectory))
            {
                foreach (var path in Directory.GetFiles(context.IndexDirectory, "*", SearchOption.TopDirectoryOnly))
                {
                    if (!string.Equals(Path.GetFileName(path), ConfigFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(path);
                    }
                }

                DeleteDirectoryIfExists(Path.Combine(context.IndexDirectory, FileGramsDirectoryName));
                DeleteDirectoryIfExists(Path.Combine(context.IndexDirectory, PostingsDirectoryName));
            }

            return new JObject
            {
                ["success"] = true,
                ["source"] = "text-index",
                ["indexed"] = false,
                ["semantic"] = false,
                ["stale"] = true,
                ["projectRoot"] = context.ProjectRoot,
                ["indexPath"] = context.IndexDirectory,
                ["configPath"] = context.ConfigPath,
                ["fileCount"] = 0,
                ["indexSizeBytes"] = GetDirectorySize(context.IndexDirectory),
                ["excludedCount"] = 0,
                ["message"] = "text_index cache was reset; config.json was preserved when present."
            };
        }

        private static bool ComputeStale(TextIndexContext context, TextIndexManifest manifest)
        {
            if (context == null || manifest == null || manifest.SchemaVersion != SchemaVersion)
            {
                return true;
            }

            if (!string.Equals(manifest.ConfigHash, ComputeConfigHash(context.Config), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var current = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
            var excluded = new TextIndexExcludedCounts();
            foreach (var path in EnumerateCandidateFiles(context, excluded))
            {
                var relative = ToIndexPath(MakeRelativePath(context.ProjectRoot, path));
                FileInfo info;
                try
                {
                    info = new FileInfo(path);
                }
                catch
                {
                    continue;
                }

                if (info.Length > context.Config.MaxFileBytes || LooksBinary(path))
                {
                    continue;
                }

                current[relative] = info;
            }

            if (current.Count != manifest.Files.Count)
            {
                return true;
            }

            foreach (var record in manifest.Files)
            {
                FileInfo info;
                if (!current.TryGetValue(record.Path, out info))
                {
                    return true;
                }

                if (info.Length != record.Size || info.LastWriteTimeUtc.Ticks != record.LastWriteTimeUtcTicks)
                {
                    return true;
                }
            }

            return false;
        }

        private static IEnumerable<string> EnumerateCandidateFiles(TextIndexContext context, TextIndexExcludedCounts excluded)
        {
            foreach (var includePath in context.Config.IncludePaths)
            {
                var fullIncludePath = Path.Combine(context.ProjectRoot, FromIndexPath(includePath));
                if (File.Exists(fullIncludePath))
                {
                    if (ShouldIndexFile(context, fullIncludePath, excluded))
                    {
                        yield return fullIncludePath;
                    }

                    continue;
                }

                if (!Directory.Exists(fullIncludePath))
                {
                    continue;
                }

                foreach (var file in EnumerateFilesSafe(context, fullIncludePath, excluded))
                {
                    if (IsExcludedPath(context, file))
                    {
                        excluded.PathExcluded++;
                        continue;
                    }

                    if (ShouldIndexFile(context, file, excluded))
                    {
                        yield return file;
                    }
                }
            }
        }

        private static IEnumerable<string> EnumerateFilesSafe(TextIndexContext context, string root, TextIndexExcludedCounts excluded)
        {
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                string[] files;
                try
                {
                    files = Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly);
                }
                catch
                {
                    excluded.Unreadable++;
                    continue;
                }

                for (var i = 0; i < files.Length; i++)
                {
                    yield return files[i];
                }

                string[] children;
                try
                {
                    children = Directory.GetDirectories(directory, "*", SearchOption.TopDirectoryOnly);
                }
                catch
                {
                    excluded.Unreadable++;
                    continue;
                }

                for (var i = 0; i < children.Length; i++)
                {
                    if (IsExcludedPath(context, children[i]))
                    {
                        excluded.PathExcluded++;
                        continue;
                    }

                    pending.Push(children[i]);
                }
            }
        }

        private static bool ShouldIndexFile(TextIndexContext context, string fullPath, TextIndexExcludedCounts excluded)
        {
            var extension = Path.GetExtension(fullPath);
            if (!context.Config.IncludeExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                excluded.ExtensionExcluded++;
                return false;
            }

            if (IsExcludedPath(context, fullPath))
            {
                excluded.PathExcluded++;
                return false;
            }

            return true;
        }

        private static bool IsExcludedPath(TextIndexContext context, string fullPath)
        {
            var relative = ToIndexPath(MakeRelativePath(context.ProjectRoot, fullPath));
            foreach (var excludePath in context.Config.ExcludePaths)
            {
                var normalized = ToIndexPath(excludePath).TrimEnd('/');
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    continue;
                }

                if (string.Equals(relative, normalized, StringComparison.OrdinalIgnoreCase)
                    || relative.StartsWith(normalized + "/", StringComparison.OrdinalIgnoreCase)
                    || relative.IndexOf("/" + normalized + "/", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static string GetIndexIntegrityError(TextIndexContext context, TextIndexManifest manifest)
        {
            if (context == null || manifest == null)
            {
                return null;
            }

            var postingsDirectory = Path.Combine(context.IndexDirectory, PostingsDirectoryName);
            if (!Directory.Exists(postingsDirectory))
            {
                return "Text index postings are missing.";
            }

            var fileGramsDirectory = Path.Combine(context.IndexDirectory, FileGramsDirectoryName);
            if (!Directory.Exists(fileGramsDirectory))
            {
                return "Text index file gram cache is missing.";
            }

            var postingShardCache = new Dictionary<int, JObject>();
            for (var i = 0; i < manifest.Files.Count; i++)
            {
                var record = manifest.Files[i];
                string[] grams;
                if (!TryReadFileGrams(context, record.Id, out grams))
                {
                    return "Text index file gram cache is unreadable for " + record.Path + ".";
                }

                for (var gramIndex = 0; gramIndex < grams.Length; gramIndex++)
                {
                    var gram = grams[gramIndex];
                    var shard = GetShard(gram);
                    JObject shardObject;
                    if (!TryReadPostingShard(context, shard, postingShardCache, out shardObject))
                    {
                        return string.IsNullOrEmpty(gram) ? null : "Text index postings are incomplete.";
                    }

                    var ids = shardObject[gram] as JArray;
                    if (ids == null || !ids.OfType<JToken>().Any(token => token != null && token.Type == JTokenType.Integer && token.Value<int>() == record.Id))
                    {
                        return "Text index postings are missing entries for " + record.Path + ".";
                    }
                }
            }

            return null;
        }

        private static HashSet<int> GetCandidateIds(TextIndexContext context, TextIndexManifest manifest, string query, bool regex)
        {
            var queryGrams = ExtractQueryTrigrams(query, regex).ToList();
            if (queryGrams.Count == 0)
            {
                return new HashSet<int>(manifest.Files.Select(file => file.Id));
            }

            HashSet<int> candidates = null;
            foreach (var gram in queryGrams)
            {
                var ids = ReadPosting(context, gram);
                if (candidates == null)
                {
                    candidates = new HashSet<int>(ids);
                }
                else
                {
                    candidates.IntersectWith(ids);
                }

                if (candidates.Count == 0)
                {
                    break;
                }
            }

            return candidates ?? new HashSet<int>(manifest.Files.Select(file => file.Id));
        }

        private static IEnumerable<string> ExtractQueryTrigrams(string query, bool regex)
        {
            if (!regex)
            {
                return ExtractTrigrams(query);
            }

            // General regex can contain alternation or optional groups, so extracted literals
            // are not guaranteed to appear in every match. Scan all indexed files, then run
            // the real Regex matcher to avoid false negatives.
            return Array.Empty<string>();
        }

        private static IEnumerable<string> ExtractTrigrams(string text)
        {
            var normalized = (text ?? string.Empty).ToLowerInvariant();
            if (normalized.Length < 3)
            {
                yield break;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i <= normalized.Length - 3; i++)
            {
                var gram = normalized.Substring(i, 3);
                if (seen.Add(gram))
                {
                    yield return gram;
                }
            }
        }

        private static Dictionary<string, List<int>>[] CreatePostingShards()
        {
            var shards = new Dictionary<string, List<int>>[ShardCount];
            for (var i = 0; i < shards.Length; i++)
            {
                shards[i] = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            }

            return shards;
        }

        private static void AddPostings(Dictionary<string, List<int>>[] postings, int id, IEnumerable<string> grams)
        {
            foreach (var gram in grams)
            {
                var shardMap = postings[GetShard(gram)];
                List<int> ids;
                if (!shardMap.TryGetValue(gram, out ids))
                {
                    ids = new List<int>();
                    shardMap[gram] = ids;
                }

                ids.Add(id);
            }
        }

        private static long WritePostings(TextIndexContext context, Dictionary<string, List<int>>[] postings)
        {
            var postingsDirectory = Path.Combine(context.IndexDirectory, PostingsDirectoryName);
            DeleteDirectoryIfExists(postingsDirectory);
            Directory.CreateDirectory(postingsDirectory);

            long totalBytes = 0;
            for (var shardIndex = 0; shardIndex < postings.Length; shardIndex++)
            {
                var shardMap = postings[shardIndex];
                if (shardMap == null || shardMap.Count == 0)
                {
                    continue;
                }

                var path = GetPostingShardPath(context, shardIndex);
                WritePostingShard(path, shardMap);
                totalBytes += GetFileLength(path);
            }

            return totalBytes;
        }

        private static void WritePostingShard(string path, Dictionary<string, List<int>> shardMap)
        {
            // build 的热点在 postings 聚合尾声，这里直接按 shard 流式写盘，避免再构造一整套 JObject/JArray 中间对象。
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            using (var jsonWriter = new JsonTextWriter(writer))
            {
                jsonWriter.Formatting = Formatting.None;
                jsonWriter.WriteStartObject();
                foreach (var gram in shardMap.Keys.OrderBy(item => item, StringComparer.Ordinal))
                {
                    var ids = shardMap[gram];
                    ids.Sort();
                    jsonWriter.WritePropertyName(gram);
                    jsonWriter.WriteStartArray();
                    for (var i = 0; i < ids.Count; i++)
                    {
                        jsonWriter.WriteValue(ids[i]);
                    }

                    jsonWriter.WriteEndArray();
                }

                jsonWriter.WriteEndObject();
            }
        }

        private static HashSet<int> ReadPosting(TextIndexContext context, string gram)
        {
            JObject shard;
            if (!TryReadPostingShard(context, GetShard(gram), null, out shard))
            {
                return new HashSet<int>();
            }

            var array = shard[gram] as JArray;
            return array == null
                ? new HashSet<int>()
                : new HashSet<int>(array.Select(token => token.Value<int>()));
        }

        private static bool TryReadPostingShard(TextIndexContext context, int shard, Dictionary<int, JObject> cache, out JObject shardObject)
        {
            shardObject = null;
            if (cache != null && cache.TryGetValue(shard, out shardObject))
            {
                return true;
            }

            var path = GetPostingShardPath(context, shard);
            if (!File.Exists(path))
            {
                return false;
            }

            try
            {
                shardObject = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
                if (cache != null)
                {
                    cache[shard] = shardObject;
                }

                return true;
            }
            catch
            {
                shardObject = null;
                return false;
            }
        }

        private static int GetShard(string gram)
        {
            if (string.IsNullOrEmpty(gram))
            {
                return 0;
            }

            unchecked
            {
                var hash = 17;
                for (var i = 0; i < gram.Length; i++)
                {
                    hash = hash * 31 + gram[i];
                }

                return (hash & int.MaxValue) % ShardCount;
            }
        }

        private static string GetPostingShardPath(TextIndexContext context, int shard)
        {
            return Path.Combine(context.IndexDirectory, PostingsDirectoryName, shard.ToString("x2", CultureInfo.InvariantCulture) + ".json");
        }

        private static bool TryReadFileGrams(TextIndexContext context, int id, out string[] grams)
        {
            grams = Array.Empty<string>();
            var path = GetFileGramsPath(context, id);
            if (!File.Exists(path))
            {
                return false;
            }

            try
            {
                var array = JArray.Parse(File.ReadAllText(path, Encoding.UTF8));
                grams = array.Select(token => token.Value<string>()).Where(value => !string.IsNullOrEmpty(value)).ToArray();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static long WriteFileGrams(TextIndexContext context, int id, string[] grams)
        {
            var path = GetFileGramsPath(context, id);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            using (var jsonWriter = new JsonTextWriter(writer))
            {
                jsonWriter.Formatting = Formatting.None;
                jsonWriter.WriteStartArray();
                for (var i = 0; i < grams.Length; i++)
                {
                    jsonWriter.WriteValue(grams[i]);
                }

                jsonWriter.WriteEndArray();
            }

            return GetFileLength(path);
        }

        private static string GetFileGramsPath(TextIndexContext context, int id)
        {
            return Path.Combine(context.IndexDirectory, FileGramsDirectoryName, id.ToString(CultureInfo.InvariantCulture) + ".json");
        }

        private static void RemoveStaleFileGrams(TextIndexContext context, HashSet<int> fileIdsInUse)
        {
            var directory = Path.Combine(context.IndexDirectory, FileGramsDirectoryName);
            if (!Directory.Exists(directory))
            {
                return;
            }

            foreach (var file in Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
            {
                int id;
                if (!int.TryParse(Path.GetFileNameWithoutExtension(file), NumberStyles.Integer, CultureInfo.InvariantCulture, out id)
                    || !fileIdsInUse.Contains(id))
                {
                    File.Delete(file);
                }
            }
        }

        private static void AddMatches(string fullPath, string relativePath, string query, bool regex, int maxResults, JArray items)
        {
            string text;
            string encodingName;
            if (!TryReadText(fullPath, out text, out encodingName))
            {
                return;
            }

            Regex pattern = null;
            if (regex)
            {
                try
                {
                    pattern = new Regex(query, RegexOptions.Compiled, RegexTimeout);
                }
                catch (RegexMatchTimeoutException ex)
                {
                    throw new ArgumentException("Invalid regex query: " + ex.Message);
                }
                catch (Exception ex)
                {
                    throw new ArgumentException("Invalid regex query: " + ex.Message);
                }
            }

            using (var reader = new StringReader(text))
            {
                string line;
                var lineNumber = 0;
                while ((line = reader.ReadLine()) != null)
                {
                    lineNumber++;
                    if (regex)
                    {
                        MatchCollection matches;
                        try
                        {
                            matches = pattern.Matches(line);
                        }
                        catch (RegexMatchTimeoutException ex)
                        {
                            throw new ArgumentException("Regex query timed out: " + ex.Message);
                        }

                        foreach (Match match in matches)
                        {
                            if (!match.Success)
                            {
                                continue;
                            }

                            AddItem(items, relativePath, lineNumber, match.Index + 1, match.Value, line);
                            if (items.Count >= maxResults)
                            {
                                return;
                            }
                        }
                    }
                    else
                    {
                        var start = 0;
                        while (start <= line.Length)
                        {
                            var index = line.IndexOf(query, start, StringComparison.OrdinalIgnoreCase);
                            if (index < 0)
                            {
                                break;
                            }

                            AddItem(items, relativePath, lineNumber, index + 1, line.Substring(index, query.Length), line);
                            if (items.Count >= maxResults)
                            {
                                return;
                            }

                            start = index + Math.Max(1, query.Length);
                        }
                    }
                }
            }
        }

        private static void AddItem(JArray items, string path, int line, int column, string match, string preview)
        {
            items.Add(new JObject
            {
                ["path"] = path,
                ["line"] = line,
                ["column"] = column,
                ["match"] = match,
                ["preview"] = TrimPreview(preview),
                ["score"] = 1.0,
                ["semantic"] = false
            });
        }

        private static bool MatchesPathFilters(string path, string pathFilter, List<string> globs)
        {
            if (!string.IsNullOrWhiteSpace(pathFilter)
                && !path.StartsWith(pathFilter.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(path, pathFilter.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (globs == null || globs.Count == 0)
            {
                return true;
            }

            foreach (var glob in globs)
            {
                if (GlobMatches(path, glob))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool GlobMatches(string path, string glob)
        {
            if (string.IsNullOrWhiteSpace(glob))
            {
                return true;
            }

            var normalizedGlob = ToIndexPath(glob);
            var target = normalizedGlob.IndexOf('/') >= 0 ? path : Path.GetFileName(path);
            var regex = "^" + Regex.Escape(normalizedGlob)
                .Replace("\\*\\*", ".*")
                .Replace("\\*", "[^/]*")
                .Replace("\\?", ".") + "$";
            return Regex.IsMatch(target, regex, RegexOptions.IgnoreCase);
        }

        private static List<string> ResolveGlobFilters(Dictionary<string, string> options)
        {
            var value = ResolveString(options, "glob", null);
            if (string.IsNullOrWhiteSpace(value))
            {
                return new List<string>();
            }

            return value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim())
                .Where(item => item.Length > 0)
                .ToList();
        }

        private static long WriteConfig(TextIndexContext context)
        {
            Directory.CreateDirectory(context.IndexDirectory);
            File.WriteAllText(context.ConfigPath, JsonConvert.SerializeObject(context.Config, Formatting.Indented, JsonSettings), new UTF8Encoding(false));
            return GetFileLength(context.ConfigPath);
        }

        private static TextIndexManifest ReadManifest(TextIndexContext context)
        {
            if (context == null || !File.Exists(context.ManifestPath))
            {
                return null;
            }

            try
            {
                var manifest = JsonConvert.DeserializeObject<TextIndexManifest>(File.ReadAllText(context.ManifestPath, Encoding.UTF8), JsonSettings);
                if (manifest == null || manifest.SchemaVersion != SchemaVersion || manifest.Files == null)
                {
                    return null;
                }

                return manifest;
            }
            catch
            {
                return null;
            }
        }

        private static long WriteManifest(TextIndexContext context, TextIndexManifest manifest)
        {
            File.WriteAllText(context.ManifestPath, JsonConvert.SerializeObject(manifest, Formatting.Indented, JsonSettings), new UTF8Encoding(false));
            return GetFileLength(context.ManifestPath);
        }

        private static long WriteManifestWithIndexSize(TextIndexContext context, TextIndexManifest manifest, long stableIndexSizeBytes)
        {
            // manifest 自己也会占用索引体积，最多回写几次即可收敛，不再额外扫描整个索引目录。
            var totalIndexSizeBytes = stableIndexSizeBytes;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                manifest.IndexSizeBytes = totalIndexSizeBytes;
                var manifestSizeBytes = WriteManifest(context, manifest);
                var actualTotalBytes = stableIndexSizeBytes + manifestSizeBytes;
                if (actualTotalBytes == totalIndexSizeBytes)
                {
                    return actualTotalBytes;
                }

                totalIndexSizeBytes = actualTotalBytes;
            }

            manifest.IndexSizeBytes = totalIndexSizeBytes;
            var finalManifestSizeBytes = WriteManifest(context, manifest);
            var finalTotalBytes = stableIndexSizeBytes + finalManifestSizeBytes;
            if (finalTotalBytes != totalIndexSizeBytes)
            {
                manifest.IndexSizeBytes = finalTotalBytes;
                finalManifestSizeBytes = WriteManifest(context, manifest);
                finalTotalBytes = stableIndexSizeBytes + finalManifestSizeBytes;
            }

            return finalTotalBytes;
        }

        private static void EnsureIndexSubdirectories(TextIndexContext context)
        {
            Directory.CreateDirectory(Path.Combine(context.IndexDirectory, FileGramsDirectoryName));
            Directory.CreateDirectory(Path.Combine(context.IndexDirectory, PostingsDirectoryName));
        }

        private static bool TryReadText(string path, out string text, out string encodingName)
        {
            text = null;
            encodingName = "utf-8";
            try
            {
                var bytes = File.ReadAllBytes(path);
                if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                {
                    text = new UTF8Encoding(true, false).GetString(bytes);
                    encodingName = "utf-8-bom";
                    return true;
                }

                if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                {
                    text = Encoding.Unicode.GetString(bytes);
                    encodingName = "utf-16le";
                    return true;
                }

                if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                {
                    text = Encoding.BigEndianUnicode.GetString(bytes);
                    encodingName = "utf-16be";
                    return true;
                }

                text = new UTF8Encoding(false, false).GetString(bytes);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool LooksBinary(string path)
        {
            try
            {
                var buffer = new byte[BinaryProbeBytes];
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var read = stream.Read(buffer, 0, buffer.Length);
                    if (read >= 2 && ((buffer[0] == 0xFF && buffer[1] == 0xFE) || (buffer[0] == 0xFE && buffer[1] == 0xFF)))
                    {
                        return false;
                    }

                    for (var i = 0; i < read; i++)
                    {
                        if (buffer[i] == 0)
                        {
                            return true;
                        }
                    }
                }
            }
            catch
            {
                return true;
            }

            return false;
        }

        private static string ComputeHash(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(bytes);
                var builder = new StringBuilder(hash.Length * 2);
                for (var i = 0; i < hash.Length; i++)
                {
                    builder.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                }

                return builder.ToString();
            }
        }

        private static string ComputeConfigHash(TextIndexConfig config)
        {
            return ComputeHash(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(config, Formatting.None, JsonSettings)));
        }

        private static long GetDirectorySize(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return 0L;
            }

            long total = 0;
            try
            {
                foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        total += new FileInfo(file).Length;
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }

            return total;
        }

        private static long GetFileLength(string path)
        {
            try
            {
                return File.Exists(path) ? new FileInfo(path).Length : 0L;
            }
            catch
            {
                return 0L;
            }
        }

        private static long ResolveIndexSizeBytes(TextIndexContext context, TextIndexManifest manifest, bool indexExists, bool corrupt)
        {
            if (!indexExists || context == null)
            {
                return 0L;
            }

            if (!corrupt && manifest != null && manifest.IndexSizeBytes > 0)
            {
                return manifest.IndexSizeBytes;
            }

            return GetDirectorySize(context.IndexDirectory);
        }

        private static JObject BuildFailure(TextIndexContext context, string error, string errorCode)
        {
            return new JObject
            {
                ["success"] = false,
                ["source"] = "text-index",
                ["indexed"] = false,
                ["semantic"] = false,
                ["stale"] = true,
                ["projectRoot"] = context == null ? null : context.ProjectRoot,
                ["indexPath"] = context == null ? null : context.IndexDirectory,
                ["fileCount"] = 0,
                ["indexSizeBytes"] = context == null ? 0L : GetDirectorySize(context.IndexDirectory),
                ["excludedCount"] = 0,
                ["errorCode"] = errorCode,
                ["error"] = error
            };
        }

        private static void Print(JObject result, OutputMode outputMode)
        {
            if (outputMode == OutputMode.Quiet && !result.Value<bool>("success"))
            {
                Console.Error.WriteLine(result.Value<string>("error") ?? "text_index failed");
                return;
            }

            var formatting = outputMode == OutputMode.Pretty ? Formatting.Indented : Formatting.None;
            Console.WriteLine(JsonConvert.SerializeObject(result, formatting, JsonSettings));
        }

        private static void DeleteDirectoryIfExists(string path)
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }

        private static string MakeRelativePath(string root, string path)
        {
            var rootUri = new Uri(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar);
            var fileUri = new Uri(Path.GetFullPath(path));
            return Uri.UnescapeDataString(rootUri.MakeRelativeUri(fileUri).ToString()).Replace('/', Path.DirectorySeparatorChar);
        }

        private static string ToIndexPath(string path)
        {
            return (path ?? string.Empty).Replace('\\', '/').Trim('/');
        }

        private static string FromIndexPath(string path)
        {
            return (path ?? string.Empty).Replace('/', Path.DirectorySeparatorChar);
        }

        private static string TrimPreview(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return string.Empty;
            }

            var trimmed = line.Trim();
            return trimmed.Length <= PreviewLimit ? trimmed : trimmed.Substring(0, PreviewLimit);
        }

        private static string ResolveString(Dictionary<string, string> options, string key, string defaultValue)
        {
            if (options == null || !options.TryGetValue(key, out var value) || value == null)
            {
                return defaultValue;
            }

            return value;
        }

        private static bool ResolveBool(Dictionary<string, string> options, string key, bool defaultValue)
        {
            if (options == null || !options.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            {
                return defaultValue;
            }

            return !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
                   && !string.Equals(value, "0", StringComparison.OrdinalIgnoreCase);
        }

        private static int ResolveInt(Dictionary<string, string> options, string key, int defaultValue)
        {
            if (options == null || !options.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            {
                return defaultValue;
            }

            int result;
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? result : defaultValue;
        }

        private sealed class TextIndexContext
        {
            public string ProjectRoot { get; private set; }
            public string IndexDirectory { get; private set; }
            public string ConfigPath { get; private set; }
            public string ManifestPath { get; private set; }
            public TextIndexConfig Config { get; private set; }

            public static TextIndexContext Resolve(Dictionary<string, string> options)
            {
                var projectRoot = ResolveProjectRoot(options);
                var indexDirectory = Path.Combine(projectRoot, ".aibridge", IndexDirectoryName);
                var configPath = Path.Combine(indexDirectory, ConfigFileName);
                var config = LoadConfig(configPath);
                return new TextIndexContext
                {
                    ProjectRoot = projectRoot,
                    IndexDirectory = indexDirectory,
                    ConfigPath = configPath,
                    ManifestPath = Path.Combine(indexDirectory, ManifestFileName),
                    Config = config
                };
            }

            private static string ResolveProjectRoot(Dictionary<string, string> options)
            {
                if (options != null && options.TryGetValue("project-root", out var explicitRoot) && !string.IsNullOrWhiteSpace(explicitRoot))
                {
                    return Path.GetFullPath(explicitRoot);
                }

                var environmentRoot = Environment.GetEnvironmentVariable("UNITY_PROJECT_ROOT");
                if (!string.IsNullOrWhiteSpace(environmentRoot))
                {
                    return Path.GetFullPath(environmentRoot);
                }

                var unityRoot = PathHelper.TryGetUnityProjectRoot();
                return string.IsNullOrWhiteSpace(unityRoot)
                    ? Directory.GetCurrentDirectory()
                    : unityRoot;
            }

            private static TextIndexConfig LoadConfig(string configPath)
            {
                if (File.Exists(configPath))
                {
                    try
                    {
                        var text = File.ReadAllText(configPath, Encoding.UTF8);
                        var json = JObject.Parse(text);
                        var loaded = json.ToObject<TextIndexConfig>(JsonSerializer.Create(JsonSettings));
                        var defaults = TextIndexConfig.Default();
                        if (json.Property("enableRegex", StringComparison.OrdinalIgnoreCase) == null)
                        {
                            loaded.EnableRegex = defaults.EnableRegex;
                        }

                        if (json.Property("autoRefresh", StringComparison.OrdinalIgnoreCase) == null)
                        {
                            loaded.AutoRefresh = defaults.AutoRefresh;
                        }

                        return TextIndexConfig.Normalize(loaded);
                    }
                    catch
                    {
                    }
                }

                return TextIndexConfig.Default();
            }
        }

        private sealed class TextIndexConfig
        {
            public List<string> IncludePaths { get; set; }
            public List<string> ExcludePaths { get; set; }
            public List<string> IncludeExtensions { get; set; }
            public long MaxFileBytes { get; set; }
            public bool EnableRegex { get; set; }
            public bool AutoRefresh { get; set; }

            public static TextIndexConfig Default()
            {
                return new TextIndexConfig
                {
                    IncludePaths = DefaultIncludePaths.Select(ToIndexPath).ToList(),
                    ExcludePaths = DefaultExcludePaths.Select(ToIndexPath).ToList(),
                    IncludeExtensions = DefaultIncludeExtensions.ToList(),
                    MaxFileBytes = DefaultMaxFileBytes,
                    EnableRegex = true,
                    AutoRefresh = true
                };
            }

            public static TextIndexConfig Normalize(TextIndexConfig config)
            {
                config = config ?? Default();
                config.IncludePaths = NormalizeList(config.IncludePaths, DefaultIncludePaths.Select(ToIndexPath));
                config.ExcludePaths = NormalizeList(config.ExcludePaths, DefaultExcludePaths.Select(ToIndexPath));
                config.IncludeExtensions = NormalizeExtensions(config.IncludeExtensions);
                config.MaxFileBytes = config.MaxFileBytes <= 0 ? DefaultMaxFileBytes : config.MaxFileBytes;
                return config;
            }

            private static List<string> NormalizeList(List<string> value, IEnumerable<string> defaults)
            {
                return value == null || value.Count == 0
                    ? defaults.ToList()
                    : value.Select(ToIndexPath).Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }

            private static List<string> NormalizeExtensions(List<string> extensions)
            {
                var values = extensions == null || extensions.Count == 0
                    ? DefaultIncludeExtensions
                    : extensions.ToArray();
                return values
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => item.StartsWith(".", StringComparison.Ordinal) ? item : "." + item)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        private sealed class TextIndexManifest
        {
            public int SchemaVersion { get; set; }
            public string BuiltAtUtc { get; set; }
            public string ProjectRoot { get; set; }
            public string ConfigHash { get; set; }
            public long IndexSizeBytes { get; set; }
            public List<TextIndexFileRecord> Files { get; set; } = new List<TextIndexFileRecord>();
            public int ExcludedCount { get; set; }
            public TextIndexExcludedCounts Excluded { get; set; }
        }

        private sealed class TextIndexFileRecord
        {
            public int Id { get; set; }
            public string Path { get; set; }
            public long Size { get; set; }
            public long LastWriteTimeUtcTicks { get; set; }
            public string Hash { get; set; }
            public string Encoding { get; set; }
        }

        private sealed class TextIndexExcludedCounts
        {
            public int PathExcluded { get; set; }
            public int ExtensionExcluded { get; set; }
            public int TooLarge { get; set; }
            public int Binary { get; set; }
            public int Unreadable { get; set; }
            public int Total
            {
                get { return PathExcluded + ExtensionExcluded + TooLarge + Binary + Unreadable; }
            }
        }
    }
}
