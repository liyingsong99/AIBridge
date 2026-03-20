using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace AIBridge.Editor
{
    /// <summary>
    /// Asset database operations: search, find, get_path, load, import, refresh, read_text (fallback)
    /// Supports multiple sub-commands via "action" parameter
    /// </summary>
    public class AssetDatabaseCommand : ICommand
    {
        public string Type => "asset";
        public bool RequiresRefresh => true;

        /// <summary>
        /// Predefined search mode filters
        /// </summary>
        private static readonly Dictionary<string, string> SearchModeFilters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "all", "" },
            { "prefab", "t:Prefab" },
            { "scene", "t:Scene" },
            { "script", "t:Script" },
            { "texture", "t:Texture" },
            { "material", "t:Material" },
            { "audio", "t:AudioClip" },
            { "animation", "t:AnimationClip" },
            { "shader", "t:Shader" },
            { "font", "t:Font" },
            { "model", "t:Model" },
            { "so", "t:ScriptableObject" }
        };

        public CommandResult Execute(CommandRequest request)
        {
            var action = request.GetParam("action", "find");

            try
            {
                switch (action.ToLower())
                {
                    case "find":
                        return FindAssets(request);
                    case "search":
                        return SearchAssets(request);
                    case "import":
                        return ImportAsset(request);
                    case "refresh":
                        return RefreshAssets(request);
                    case "get_path":
                        return GetAssetPath(request);
                    case "load":
                        return LoadAsset(request);
                    case "read_text":
                        return ReadTextAsset(request);
                    default:
                        return CommandResult.Failure(request.id, $"Unknown action: {action}. Supported: find, search, import, refresh, get_path, load, read_text");
                }
            }
            catch (Exception ex)
            {
                return CommandResult.FromException(request.id, ex);
            }
        }

        private CommandResult FindAssets(CommandRequest request)
        {
            var filter = request.GetParam("filter", "");
            var searchInFolders = request.GetParam<string>("searchInFolders", null);
            var maxResults = request.GetParam("maxResults", 100);
            var format = GetAssetResponseFormat(request);
            if (format == null)
            {
                return CommandResult.Failure(request.id, "Invalid 'format' parameter. Supported values: full, paths");
            }

            string[] guids;
            if (!string.IsNullOrEmpty(searchInFolders))
            {
                var folders = searchInFolders.Split(',');
                guids = AssetDatabase.FindAssets(filter, folders);
            }
            else
            {
                guids = AssetDatabase.FindAssets(filter);
            }

            var count = Math.Min(guids.Length, maxResults);
            if (format == "paths")
            {
                var paths = new List<string>(count);
                for (var i = 0; i < count; i++)
                {
                    paths.Add(AssetDatabase.GUIDToAssetPath(guids[i]));
                }

                return CommandResult.Success(request.id, new
                {
                    assets = paths,
                    totalFound = guids.Length,
                    returned = count
                });
            }

            var results = new List<AssetInfo>();

            for (var i = 0; i < count; i++)
            {
                var path = AssetDatabase.GUIDToAssetPath(guids[i]);
                var assetType = AssetDatabase.GetMainAssetTypeAtPath(path);

                results.Add(new AssetInfo
                {
                    guid = guids[i],
                    path = path,
                    type = assetType?.Name ?? "Unknown"
                });
            }

            return CommandResult.Success(request.id, new
            {
                assets = results,
                totalFound = guids.Length,
                returned = count
            });
        }

        /// <summary>
        /// Search assets with predefined modes (simplified wrapper for FindAssets)
        /// </summary>
        private CommandResult SearchAssets(CommandRequest request)
        {
            var mode = request.GetParam("mode", "all");
            var customFilter = request.GetParam<string>("filter", null);
            var keyword = request.GetParam<string>("keyword", null);
            var searchInFolders = request.GetParam<string>("searchInFolders", null);
            var maxResults = request.GetParam("maxResults", 100);
            var format = GetAssetResponseFormat(request);
            if (format == null)
            {
                return CommandResult.Failure(request.id, "Invalid 'format' parameter. Supported values: full, paths");
            }

            // Determine the filter to use
            string filter;
            if (!string.IsNullOrEmpty(customFilter))
            {
                // Custom filter overrides mode
                filter = customFilter;
            }
            else if (SearchModeFilters.TryGetValue(mode, out var modeFilter))
            {
                filter = modeFilter;
            }
            else
            {
                // Return available modes if invalid mode specified
                var availableModes = string.Join(", ", SearchModeFilters.Keys);
                return CommandResult.Failure(request.id, $"Unknown mode: {mode}. Available modes: {availableModes}");
            }

            // Append keyword to filter if provided
            if (!string.IsNullOrEmpty(keyword))
            {
                filter = string.IsNullOrEmpty(filter) ? keyword : $"{filter} {keyword}";
            }

            // Execute search
            string[] guids;
            if (!string.IsNullOrEmpty(searchInFolders))
            {
                var folders = searchInFolders.Split(',');
                for (var i = 0; i < folders.Length; i++)
                {
                    folders[i] = folders[i].Trim();
                }
                guids = AssetDatabase.FindAssets(filter, folders);
            }
            else
            {
                guids = AssetDatabase.FindAssets(filter);
            }

            var count = Math.Min(guids.Length, maxResults);
            if (format == "paths")
            {
                var paths = new List<string>(count);
                for (var i = 0; i < count; i++)
                {
                    paths.Add(AssetDatabase.GUIDToAssetPath(guids[i]));
                }

                return CommandResult.Success(request.id, new
                {
                    assets = paths,
                    mode = mode,
                    filter = filter,
                    totalFound = guids.Length,
                    returned = count
                });
            }

            var results = new List<SearchAssetInfo>();

            for (var i = 0; i < count; i++)
            {
                var path = AssetDatabase.GUIDToAssetPath(guids[i]);
                var assetType = AssetDatabase.GetMainAssetTypeAtPath(path);
                var assetName = System.IO.Path.GetFileNameWithoutExtension(path);

                results.Add(new SearchAssetInfo
                {
                    guid = guids[i],
                    path = path,
                    name = assetName,
                    type = assetType?.Name ?? "Unknown"
                });
            }

            return CommandResult.Success(request.id, new
            {
                assets = results,
                mode = mode,
                filter = filter,
                totalFound = guids.Length,
                returned = count
            });
        }

        private CommandResult ImportAsset(CommandRequest request)
        {
            var assetPath = request.GetParam<string>("assetPath");
            if (string.IsNullOrEmpty(assetPath))
            {
                return CommandResult.Failure(request.id, "Missing 'assetPath' parameter");
            }

            var options = request.GetParam("forceUpdate", false)
                ? ImportAssetOptions.ForceUpdate
                : ImportAssetOptions.Default;

            AssetDatabase.ImportAsset(assetPath, options);

            return CommandResult.Success(request.id, new
            {
                assetPath = assetPath,
                imported = true
            });
        }

        private CommandResult RefreshAssets(CommandRequest request)
        {
            var options = request.GetParam("forceUpdate", false)
                ? ImportAssetOptions.ForceUpdate
                : ImportAssetOptions.Default;

            AssetDatabase.Refresh(options);

            return CommandResult.Success(request.id, new
            {
                refreshed = true
            });
        }

        private CommandResult GetAssetPath(CommandRequest request)
        {
            var guid = request.GetParam<string>("guid");
            if (string.IsNullOrEmpty(guid))
            {
                return CommandResult.Failure(request.id, "Missing 'guid' parameter");
            }

            var path = AssetDatabase.GUIDToAssetPath(guid);

            return CommandResult.Success(request.id, new
            {
                guid = guid,
                path = path,
                exists = !string.IsNullOrEmpty(path)
            });
        }

        private CommandResult LoadAsset(CommandRequest request)
        {
            var assetPath = request.GetParam<string>("assetPath");
            if (string.IsNullOrEmpty(assetPath))
            {
                return CommandResult.Failure(request.id, "Missing 'assetPath' parameter");
            }

            var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (asset == null)
            {
                return CommandResult.Failure(request.id, $"Asset not found at path: {assetPath}");
            }

            return CommandResult.Success(request.id, new
            {
                name = asset.name,
                path = assetPath,
                type = asset.GetType().Name,
                instanceId = asset.GetInstanceID()
            });
        }

        private CommandResult ReadTextAsset(CommandRequest request)
        {
            var assetPath = request.GetParam<string>("assetPath");
            if (string.IsNullOrEmpty(assetPath))
            {
                return CommandResult.Failure(request.id, "Missing 'assetPath' parameter");
            }

            var startLine = Math.Max(1, request.GetParam("startLine", 1));
            var maxLines = Math.Max(1, request.GetParam("maxLines", 200));
            var maxChars = Math.Max(200, request.GetParam("maxChars", 12000));

            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            var normalizedAssetPath = assetPath.Replace('\\', '/');
            var fullPath = Path.GetFullPath(Path.Combine(projectRoot, normalizedAssetPath));
            var fullProjectRoot = Path.GetFullPath(projectRoot + Path.DirectorySeparatorChar);

            if (!fullPath.StartsWith(fullProjectRoot, StringComparison.OrdinalIgnoreCase))
            {
                return CommandResult.Failure(request.id, $"Asset path escapes project root: {assetPath}");
            }

            if (!File.Exists(fullPath))
            {
                return CommandResult.Failure(request.id, $"Text asset not found at path: {assetPath}");
            }

            var text = File.ReadAllText(fullPath, Encoding.UTF8);
            var allLines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var startIndex = startLine - 1;

            if (startIndex >= allLines.Length)
            {
                return CommandResult.Success(request.id, new
                {
                    path = normalizedAssetPath,
                    totalLines = allLines.Length,
                    returnedLineStart = startLine,
                    returnedLineEnd = startLine - 1,
                    returnedLineCount = 0,
                    truncated = false,
                    content = string.Empty
                });
            }

            var builder = new StringBuilder();
            var currentLine = startLine;
            var returnedLines = 0;
            var truncated = false;

            for (var i = startIndex; i < allLines.Length; i++)
            {
                var lineWithNumber = $"{currentLine}: {allLines[i]}";
                var separatorLength = returnedLines > 0 ? 1 : 0;
                if (builder.Length + separatorLength + lineWithNumber.Length > maxChars)
                {
                    truncated = true;
                    break;
                }

                if (returnedLines > 0)
                {
                    builder.Append('\n');
                }

                builder.Append(lineWithNumber);
                returnedLines++;

                if (returnedLines >= maxLines)
                {
                    truncated = i < allLines.Length - 1;
                    break;
                }

                currentLine++;
            }

            var returnedLineEnd = returnedLines == 0 ? startLine - 1 : startLine + returnedLines - 1;

            return CommandResult.Success(request.id, new
            {
                path = normalizedAssetPath,
                totalLines = allLines.Length,
                returnedLineStart = startLine,
                returnedLineEnd = returnedLineEnd,
                returnedLineCount = returnedLines,
                truncated = truncated,
                content = builder.ToString()
            });
        }

        private static string GetAssetResponseFormat(CommandRequest request)
        {
            var format = request.GetParam("format", "full");

            if (string.IsNullOrWhiteSpace(format))
            {
                return "full";
            }

            format = format.Trim();
            if (string.Equals(format, "full", StringComparison.OrdinalIgnoreCase))
            {
                return "full";
            }

            if (string.Equals(format, "paths", StringComparison.OrdinalIgnoreCase))
            {
                return "paths";
            }

            return null;
        }

        [Serializable]
        private class AssetInfo
        {
            public string guid;
            public string path;
            public string type;
        }

        [Serializable]
        private class SearchAssetInfo
        {
            public string guid;
            public string path;
            public string name;
            public string type;
        }
    }
}
