using System;
using System.Collections.Immutable;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

namespace AIBridgeCodeIndex
{
    internal sealed class CodeIndexWorkspace : IDisposable
    {
        private const int MaxSymbolResults = 100;
        private const int MaxReferenceResults = 500;
        private const int MaxDiagnosticResults = 500;
        private const int MaxTokenDocumentCacheEntries = 256;
        private const int SchemaVersion = 2;
        private const int ManifestFormatKind = 1;
        private const int AssemblyFormatKind = 2;
        private const int TextIndexFormatKind = 3;
        private const string Magic = "AIBCI";
        private const string SnapshotRelativeDirectory = ".aibridge/code-index/snapshot";
        private const string ManifestFileName = "manifest.bin";

        private readonly string _projectRoot;
        private readonly SemaphoreSlim _loadGate = new SemaphoreSlim(1, 1);
        private readonly object _stateLock = new object();
        private readonly object _tokenIndexLock = new object();
        private AdhocWorkspace _workspace;
        private Solution _solution;
        private SnapshotManifest _manifest;
        private List<CodeIndexItem> _symbols = new List<CodeIndexItem>();
        private List<string> _workspaceWarnings = new List<string>();
        private Dictionary<string, MetadataReference> _metadataReferenceCache = new Dictionary<string, MetadataReference>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, Document> _documentPathMap = new Dictionary<string, Document>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, SourceSnapshotInfo> _sourcePathMap = new Dictionary<string, SourceSnapshotInfo>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, List<CodeIndexItem>> _symbolNameMap = new Dictionary<string, List<CodeIndexItem>>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, AssemblySnapshot> _assemblyById = new Dictionary<string, AssemblySnapshot>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _loadedAssemblyIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, HashSet<string>> _tokenDocumentCache = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        private List<CodeIndexItem> _semanticSymbols;
        private string _semanticSymbolsSnapshotContentHash;
        private string _loadedSnapshotContentHash;
        private bool _loadedAllAssemblies;
        private bool _disposed;

        public CodeIndexWorkspace(string projectRoot)
        {
            _projectRoot = Path.GetFullPath(projectRoot);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_workspace != null)
            {
                _workspace.Dispose();
                _workspace = null;
            }

            _solution = null;
            _manifest = null;
            _symbols.Clear();
            _workspaceWarnings.Clear();
            _metadataReferenceCache.Clear();
            _documentPathMap.Clear();
            _sourcePathMap.Clear();
            _symbolNameMap.Clear();
            _assemblyById.Clear();
            _loadedAssemblyIds.Clear();
            _tokenDocumentCache.Clear();
            _semanticSymbols = null;
            _semanticSymbolsSnapshotContentHash = null;
            _loadedSnapshotContentHash = null;
            _loadGate.Dispose();
        }

        public string SolutionPath { get { return null; } }
        public string WorkspaceMode { get { return "unity-snapshot"; } }
        public bool SnapshotExists { get; private set; }
        public int SnapshotVersion { get; private set; }
        public string GenerationId { get; private set; }
        public string SnapshotContentHash { get; private set; }
        public int AssemblyCount { get; private set; }
        public int SourceFileCount { get; private set; }
        public int ExcludedAssemblyCount { get; private set; }
        public int ExcludedSourceFileCount { get; private set; }
        public bool IncludePackageCacheSourceAssemblies { get; private set; }
        public string BuildTarget { get; private set; }
        public string UnityVersion { get; private set; }
        public string StaleReason { get; private set; }
        public int LoadedProjects { get; private set; }
        public int LoadedDocuments { get; private set; }

        public bool IsStale()
        {
            lock (_stateLock)
            {
                var manifestPath = GetManifestPath();
                if (!File.Exists(manifestPath))
                {
                    StaleReason = "missingSnapshot";
                    SnapshotExists = false;
                    return true;
                }

                SnapshotManifest manifest;
                try
                {
                    manifest = ReadManifest(manifestPath);
                    ValidateSnapshotFiles(manifest);
                }
                catch
                {
                    if (string.IsNullOrWhiteSpace(StaleReason))
                    {
                        StaleReason = "snapshotContentChanged";
                    }

                    return true;
                }

                if (string.IsNullOrEmpty(_loadedSnapshotContentHash)
                    || !string.Equals(manifest.SnapshotContentHash, _loadedSnapshotContentHash, StringComparison.OrdinalIgnoreCase))
                {
                    StaleReason = "snapshotContentChanged";
                    return true;
                }

                StaleReason = null;
                return false;
            }
        }

        public async Task WarmupAsync()
        {
            await _loadGate.WaitAsync();
            try
            {
                LoadSnapshotIndex();
            }
            finally
            {
                _loadGate.Release();
            }
        }

        public async Task<CodeIndexResponse> QueryAsync(string action, Dictionary<string, object> parameters)
        {
            switch ((action ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "symbol":
                    return await QuerySymbolAsync(GetString(parameters, "query"));
                case "definition":
                    return await QueryDefinitionAsync(parameters);
                case "references":
                    return await QueryReferencesAsync(parameters);
                case "implementations":
                    return await QueryImplementationsAsync(parameters);
                case "derived":
                    return await QueryDerivedAsync(parameters);
                case "callers":
                    return await QueryCallersAsync(parameters);
                case "diagnostics":
                    return await QueryDiagnosticsAsync(parameters);
                default:
                    return BuildErrorResponse("Unsupported code_index action: " + action, "unsupported_action");
            }
        }

        private void LoadSnapshotIndex()
        {
            var manifestPath = GetManifestPath();
            var manifest = LoadSnapshotManifestWithAssemblies();
            _workspaceWarnings = new List<string>();
            var symbols = LoadNameIndexSymbols(manifest);

            if (_workspace != null)
            {
                _workspace.Dispose();
                _workspace = null;
            }

            _solution = null;
            _metadataReferenceCache.Clear();
            _documentPathMap.Clear();
            _tokenDocumentCache.Clear();
            _semanticSymbols = null;
            _semanticSymbolsSnapshotContentHash = null;
            _symbols = symbols;
            _loadedAssemblyIds.Clear();
            _loadedAllAssemblies = false;
            RebuildSnapshotMaps(manifest, symbols);
            ApplyManifestStatus(manifest, loadedProjects: 0, loadedDocuments: 0);
            _manifest = manifest;
            _loadedSnapshotContentHash = manifest.SnapshotContentHash;
            StaleReason = null;
            SnapshotExists = true;
        }

        private void LoadSnapshotWorkspace(HashSet<string> requiredAssemblyIds, bool loadAllAssemblies)
        {
            var manifestPath = GetManifestPath();
            var manifest = LoadSnapshotManifestWithAssemblies();
            _workspaceWarnings = new List<string>();
            var assembliesToLoad = SelectAssemblies(manifest, requiredAssemblyIds, loadAllAssemblies);

            var workspace = new AdhocWorkspace();
            var solution = workspace.CurrentSolution;
            var projectIds = new Dictionary<string, ProjectId>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < assembliesToLoad.Count; i++)
            {
                var record = assembliesToLoad[i];
                var projectId = ProjectId.CreateNewId(record.AssemblyName);
                projectIds[record.AssemblyId] = projectId;
                var parseOptions = BuildParseOptions(record);
                var compilationOptions = new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    allowUnsafe: record.AllowUnsafe);
                var info = ProjectInfo.Create(
                    projectId,
                    VersionStamp.Create(),
                    record.AssemblyName,
                    record.AssemblyName,
                    LanguageNames.CSharp,
                    filePath: ResolveAbsolutePath(record.AsmdefPath),
                    outputFilePath: ResolveAbsolutePath(record.OutputPath),
                    compilationOptions: compilationOptions,
                    parseOptions: parseOptions);
                solution = solution.AddProject(info);
            }

            for (var i = 0; i < assembliesToLoad.Count; i++)
            {
                var record = assembliesToLoad[i];
                var projectId = projectIds[record.AssemblyId];
                var metadataReferences = BuildMetadataReferences(record);
                solution = solution.AddMetadataReferences(projectId, metadataReferences);

                for (var j = 0; j < record.DependencyAssemblyIds.Count; j++)
                {
                    ProjectId dependencyId;
                    if (projectIds.TryGetValue(record.DependencyAssemblyIds[j], out dependencyId))
                    {
                        solution = solution.AddProjectReference(projectId, new ProjectReference(dependencyId));
                    }
                }

                for (var j = 0; j < record.Sources.Count; j++)
                {
                    var sourcePath = ResolveAbsolutePath(record.Sources[j].Path);
                    if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                    {
                        continue;
                    }

                    var documentId = DocumentId.CreateNewId(projectId, record.Sources[j].Path);
                    var text = SourceText.From(File.ReadAllText(sourcePath, Encoding.UTF8), Encoding.UTF8);
                    solution = solution.AddDocument(documentId, Path.GetFileName(sourcePath), text, filePath: sourcePath);
                }
            }

            var previousWorkspace = _workspace;
            _workspace = workspace;
            _solution = solution;
            _semanticSymbols = null;
            _semanticSymbolsSnapshotContentHash = null;
            workspace.TryApplyChanges(solution);
            RebuildDocumentPathMap();
            _loadedAssemblyIds = new HashSet<string>(projectIds.Keys, StringComparer.OrdinalIgnoreCase);
            _loadedAllAssemblies = loadAllAssemblies || _loadedAssemblyIds.Count >= manifest.Assemblies.Count;
            RebuildSnapshotMaps(manifest, _symbols);

            ApplyManifestStatus(
                manifest,
                loadedProjects: _solution.Projects.Count(),
                loadedDocuments: _solution.Projects.SelectMany(project => project.Documents).Count(IsCSharpDocument));
            _manifest = manifest;
            _loadedSnapshotContentHash = manifest.SnapshotContentHash;
            StaleReason = null;
            SnapshotExists = true;
            DisposeWorkspace(previousWorkspace);
        }

        private SnapshotManifest LoadSnapshotManifestWithAssemblies()
        {
            var manifestPath = GetManifestPath();
            SnapshotExists = File.Exists(manifestPath);
            if (!SnapshotExists)
            {
                StaleReason = "missingSnapshot";
                throw new FileNotFoundException("No Unity compilation snapshot found. Open the Unity project once or run Code Index prewarm from AIBridge settings.", manifestPath);
            }

            var manifest = ReadManifest(manifestPath);
            ValidateSnapshotFiles(manifest);

            for (var i = 0; i < manifest.Assemblies.Count; i++)
            {
                var assembly = ReadAssemblySnapshot(Path.Combine(GetSnapshotDirectory(), manifest.Assemblies[i].SnapshotFile));
                manifest.Assemblies[i].Sources = assembly.Sources;
                manifest.Assemblies[i].References = assembly.References;
                manifest.Assemblies[i].Defines = assembly.Defines;
                manifest.Assemblies[i].CompilerOptions = assembly.CompilerOptions;
                manifest.Assemblies[i].ProjectReferenceAssemblyNames = assembly.ProjectReferenceAssemblyNames;
            }

            return manifest;
        }

        private void ApplyManifestStatus(SnapshotManifest manifest, int loadedProjects, int loadedDocuments)
        {
            SnapshotVersion = manifest.SchemaVersion;
            GenerationId = manifest.GenerationId;
            SnapshotContentHash = manifest.SnapshotContentHash;
            AssemblyCount = manifest.Assemblies.Count;
            SourceFileCount = manifest.Assemblies.Sum(item => item.Sources.Count);
            ExcludedAssemblyCount = manifest.ExcludedAssemblyCount;
            ExcludedSourceFileCount = manifest.ExcludedSourceFileCount;
            IncludePackageCacheSourceAssemblies = manifest.IncludePackageCacheSourceAssemblies;
            BuildTarget = manifest.BuildTarget;
            UnityVersion = manifest.UnityVersion;
            LoadedProjects = loadedProjects;
            LoadedDocuments = loadedDocuments;
        }

        private void ValidateSnapshotFiles(SnapshotManifest manifest)
        {
            for (var i = 0; i < manifest.Assemblies.Count; i++)
            {
                var record = manifest.Assemblies[i];
                if (!File.Exists(Path.Combine(GetSnapshotDirectory(), record.SnapshotFile)))
                {
                    StaleReason = "snapshotFilesMissing";
                    throw new FileNotFoundException("Unity compilation snapshot assembly file is missing.", record.SnapshotFile);
                }

                if (!File.Exists(Path.Combine(GetSnapshotDirectory(), record.NameIndexFile)))
                {
                    StaleReason = "snapshotFilesMissing";
                    throw new FileNotFoundException("Unity compilation snapshot name index file is missing.", record.NameIndexFile);
                }

                if (!File.Exists(Path.Combine(GetSnapshotDirectory(), record.TokenIndexFile)))
                {
                    StaleReason = "snapshotFilesMissing";
                    throw new FileNotFoundException("Unity compilation snapshot token index file is missing.", record.TokenIndexFile);
                }
            }
        }

        private void EnsureSemanticWorkspaceLoaded()
        {
            EnsureSemanticWorkspaceLoaded(null, loadAllAssemblies: true);
        }

        private void EnsureSemanticWorkspaceLoaded(AssemblySnapshot targetAssembly, bool includeReverseDependencies)
        {
            if (targetAssembly == null)
            {
                EnsureSemanticWorkspaceLoaded(null, loadAllAssemblies: true);
                return;
            }

            var requiredAssemblyIds = BuildRequiredAssemblyIds(targetAssembly, includeReverseDependencies);
            EnsureSemanticWorkspaceLoaded(requiredAssemblyIds, loadAllAssemblies: false);
        }

        private void EnsureSemanticWorkspaceLoaded(HashSet<string> requiredAssemblyIds, bool loadAllAssemblies)
        {
            if (IsSemanticWorkspaceLoadedFor(requiredAssemblyIds, loadAllAssemblies))
            {
                return;
            }

            _loadGate.Wait();
            try
            {
                if (IsSemanticWorkspaceLoadedFor(requiredAssemblyIds, loadAllAssemblies))
                {
                    return;
                }

                var nextRequiredAssemblyIds = loadAllAssemblies
                    ? null
                    : MergeLoadedAssemblyIds(requiredAssemblyIds);

                // 默认 warmup 只装载轻量索引；语义 workspace 按目标程序集和依赖逐步扩展。
                LoadSnapshotWorkspace(nextRequiredAssemblyIds, loadAllAssemblies);
            }
            finally
            {
                _loadGate.Release();
            }
        }

        private void EnsureSnapshotIndexLoaded()
        {
            if (_manifest != null)
            {
                return;
            }

            _loadGate.Wait();
            try
            {
                if (_manifest == null)
                {
                    LoadSnapshotIndex();
                }
            }
            finally
            {
                _loadGate.Release();
            }
        }

        private static void DisposeWorkspace(AdhocWorkspace workspace)
        {
            if (workspace == null)
            {
                return;
            }

            try
            {
                workspace.Dispose();
            }
            catch
            {
                // Roslyn workspace disposal should not make the current query fail.
            }
        }

        private bool IsSemanticWorkspaceLoadedFor(HashSet<string> requiredAssemblyIds, bool loadAllAssemblies)
        {
            if (_workspace == null || _solution == null)
            {
                return false;
            }

            if (loadAllAssemblies)
            {
                return _loadedAllAssemblies;
            }

            if (_loadedAllAssemblies || requiredAssemblyIds == null || requiredAssemblyIds.Count == 0)
            {
                return true;
            }

            foreach (var assemblyId in requiredAssemblyIds)
            {
                if (!_loadedAssemblyIds.Contains(assemblyId))
                {
                    return false;
                }
            }

            return true;
        }

        private HashSet<string> MergeLoadedAssemblyIds(HashSet<string> requiredAssemblyIds)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_loadedAssemblyIds != null)
            {
                foreach (var assemblyId in _loadedAssemblyIds)
                {
                    result.Add(assemblyId);
                }
            }

            if (requiredAssemblyIds != null)
            {
                foreach (var assemblyId in requiredAssemblyIds)
                {
                    result.Add(assemblyId);
                }
            }

            return result;
        }

        private List<AssemblySnapshot> SelectAssemblies(SnapshotManifest manifest, HashSet<string> requiredAssemblyIds, bool loadAllAssemblies)
        {
            var result = new List<AssemblySnapshot>();
            if (manifest == null)
            {
                return result;
            }

            for (var i = 0; i < manifest.Assemblies.Count; i++)
            {
                var record = manifest.Assemblies[i];
                if (loadAllAssemblies || requiredAssemblyIds == null || requiredAssemblyIds.Contains(record.AssemblyId))
                {
                    result.Add(record);
                }
            }

            return result;
        }

        private void RebuildSnapshotMaps(SnapshotManifest manifest, List<CodeIndexItem> symbols)
        {
            var sourcePathMap = new Dictionary<string, SourceSnapshotInfo>(StringComparer.OrdinalIgnoreCase);
            var symbolNameMap = new Dictionary<string, List<CodeIndexItem>>(StringComparer.OrdinalIgnoreCase);
            var assemblyById = new Dictionary<string, AssemblySnapshot>(StringComparer.OrdinalIgnoreCase);

            if (manifest != null)
            {
                for (var i = 0; i < manifest.Assemblies.Count; i++)
                {
                    var assembly = manifest.Assemblies[i];
                    if (!string.IsNullOrEmpty(assembly.AssemblyId))
                    {
                        assemblyById[assembly.AssemblyId] = assembly;
                    }

                    for (var j = 0; j < assembly.Sources.Count; j++)
                    {
                        var relativePath = assembly.Sources[j].Path;
                        var fullPath = ResolveAbsolutePath(relativePath);
                        if (!string.IsNullOrWhiteSpace(fullPath) && !sourcePathMap.ContainsKey(fullPath))
                        {
                            sourcePathMap[fullPath] = new SourceSnapshotInfo
                            {
                                Assembly = assembly,
                                FullPath = fullPath,
                                RelativePath = relativePath
                            };
                        }
                    }
                }
            }

            if (symbols != null)
            {
                for (var i = 0; i < symbols.Count; i++)
                {
                    var item = symbols[i];
                    if (item == null || string.IsNullOrWhiteSpace(item.name))
                    {
                        continue;
                    }

                    List<CodeIndexItem> items;
                    if (!symbolNameMap.TryGetValue(item.name, out items))
                    {
                        items = new List<CodeIndexItem>();
                        symbolNameMap[item.name] = items;
                    }

                    items.Add(item);
                }
            }

            _sourcePathMap = sourcePathMap;
            _symbolNameMap = symbolNameMap;
            _assemblyById = assemblyById;
        }

        private HashSet<string> BuildRequiredAssemblyIds(AssemblySnapshot root, bool includeReverseDependencies)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (root == null)
            {
                return result;
            }

            AddAssemblyAndDependencies(root.AssemblyId, result);
            if (includeReverseDependencies)
            {
                AddReverseDependencies(root.AssemblyId, result);
            }

            return result;
        }

        private void AddAssemblyAndDependencies(string assemblyId, HashSet<string> result)
        {
            if (string.IsNullOrEmpty(assemblyId) || result == null || !result.Add(assemblyId))
            {
                return;
            }

            AssemblySnapshot assembly;
            if (!_assemblyById.TryGetValue(assemblyId, out assembly) || assembly == null)
            {
                return;
            }

            for (var i = 0; i < assembly.DependencyAssemblyIds.Count; i++)
            {
                AddAssemblyAndDependencies(assembly.DependencyAssemblyIds[i], result);
            }
        }

        private void AddReverseDependencies(string assemblyId, HashSet<string> result)
        {
            AssemblySnapshot assembly;
            if (string.IsNullOrEmpty(assemblyId)
                || result == null
                || !_assemblyById.TryGetValue(assemblyId, out assembly)
                || assembly == null)
            {
                return;
            }

            for (var i = 0; i < assembly.ReverseDependencyAssemblyIds.Count; i++)
            {
                var reverseId = assembly.ReverseDependencyAssemblyIds[i];
                if (!result.Contains(reverseId))
                {
                    AddAssemblyAndDependencies(reverseId, result);
                    AddReverseDependencies(reverseId, result);
                }
            }
        }

        private List<CodeIndexItem> LoadNameIndexSymbols(SnapshotManifest manifest)
        {
            var result = new List<CodeIndexItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (manifest == null)
            {
                return result;
            }

            for (var i = 0; i < manifest.Assemblies.Count; i++)
            {
                var record = manifest.Assemblies[i];
                var path = Path.Combine(GetSnapshotDirectory(), record.NameIndexFile);
                if (!File.Exists(path))
                {
                    continue;
                }

                try
                {
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var reader = new BinaryReader(stream, Encoding.UTF8))
                    {
                        ReadHeader(reader, TextIndexFormatKind);
                        ReadString(reader);
                        ReadString(reader);
                        reader.ReadBoolean();
                        var count = reader.ReadInt32();
                        for (var j = 0; j < count; j++)
                        {
                            TryAddNameIndexEntry(result, seen, record.AssemblyName, ReadString(reader));
                        }
                    }
                }
                catch (Exception ex)
                {
                    _workspaceWarnings.Add("Failed to load name index " + path + ": " + ex.Message);
                }
            }

            return result;
        }

        private HashSet<string> GetTokenDocuments(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            lock (_tokenIndexLock)
            {
                HashSet<string> cached;
                if (_tokenDocumentCache.TryGetValue(token, out cached))
                {
                    return cached;
                }

                var result = LoadTokenDocuments(token);
                if (_tokenDocumentCache.Count >= MaxTokenDocumentCacheEntries)
                {
                    TrimTokenDocumentCache();
                }

                _tokenDocumentCache[token] = result;
                return result;
            }
        }

        private HashSet<string> LoadTokenDocuments(string token)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_manifest == null)
            {
                return result;
            }

            for (var i = 0; i < _manifest.Assemblies.Count; i++)
            {
                var record = _manifest.Assemblies[i];
                var path = Path.Combine(GetSnapshotDirectory(), record.TokenIndexFile);
                if (!File.Exists(path))
                {
                    continue;
                }

                try
                {
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var reader = new BinaryReader(stream, Encoding.UTF8))
                    {
                        ReadHeader(reader, TextIndexFormatKind);
                        ReadString(reader);
                        ReadString(reader);
                        reader.ReadBoolean();
                        var count = reader.ReadInt32();
                        for (var j = 0; j < count; j++)
                        {
                            string name;
                            string file;
                            int line;
                            if (!TryParseTextIndexEntry(ReadString(reader), out name, out file, out line))
                            {
                                continue;
                            }

                            if (string.Equals(name, token, StringComparison.OrdinalIgnoreCase))
                            {
                                result.Add(file);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _workspaceWarnings.Add("Failed to load token index " + path + ": " + ex.Message);
                }
            }

            return result;
        }

        private void TrimTokenDocumentCache()
        {
            foreach (var key in _tokenDocumentCache.Keys.ToList())
            {
                _tokenDocumentCache.Remove(key);
                if (_tokenDocumentCache.Count < MaxTokenDocumentCacheEntries)
                {
                    return;
                }
            }
        }

        private static void TryAddNameIndexEntry(List<CodeIndexItem> result, HashSet<string> seen, string assemblyName, string entry)
        {
            string name;
            string file;
            int line;
            if (!TryParseTextIndexEntry(entry, out name, out file, out line))
            {
                return;
            }

            var key = name + "|" + file + "|" + line;
            if (!seen.Add(key))
            {
                return;
            }

            result.Add(new CodeIndexItem
            {
                kind = "symbol-index",
                name = name,
                container = assemblyName,
                file = file,
                line = line,
                column = 1,
                signature = assemblyName + ":" + name
            });
        }

        private static bool TryParseTextIndexEntry(string entry, out string name, out string file, out int line)
        {
            name = null;
            file = null;
            line = 0;
            if (string.IsNullOrWhiteSpace(entry))
            {
                return false;
            }

            var first = entry.IndexOf('|');
            var second = first < 0 ? -1 : entry.IndexOf('|', first + 1);
            if (first <= 0 || second <= first + 1 || second + 1 >= entry.Length)
            {
                return false;
            }

            name = entry.Substring(0, first);
            file = entry.Substring(first + 1, second - first - 1);
            if (!int.TryParse(entry.Substring(second + 1), out line))
            {
                line = 0;
            }

            return true;
        }

        private async Task<CodeIndexResponse> QuerySymbolAsync(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                throw new ArgumentException("Missing required parameter: --query");
            }

            var normalized = query.Trim();
            // symbol 查询需要准确的声明类型和列号；首次调用时才加载完整语义表，避免 warmup 阶段过重。
            EnsureSemanticWorkspaceLoaded();
            var semanticSymbols = await GetSemanticSymbolsAsync();
            var items = SelectTopSymbols(semanticSymbols, normalized);
            if (items.Count == 0)
            {
                items = SelectTopSymbols(_symbols, normalized);
            }

            return BuildResponse(null, items);
        }

        private async Task<List<CodeIndexItem>> GetSemanticSymbolsAsync()
        {
            if (_semanticSymbols != null
                && string.Equals(_semanticSymbolsSnapshotContentHash, _loadedSnapshotContentHash, StringComparison.OrdinalIgnoreCase))
            {
                return _semanticSymbols;
            }

            var symbols = await BuildSymbolTableAsync(_solution);
            _semanticSymbols = symbols;
            _semanticSymbolsSnapshotContentHash = _loadedSnapshotContentHash;
            return symbols;
        }

        private async Task<CodeIndexResponse> QueryDefinitionAsync(Dictionary<string, object> parameters)
        {
            CodeIndexResponse syntaxResponse;
            if (TryQueryDeclarationDefinition(parameters, out syntaxResponse))
            {
                return syntaxResponse;
            }

            if (TryQueryIndexedDefinition(parameters, out syntaxResponse))
            {
                return syntaxResponse;
            }

            AssemblySnapshot targetAssembly;
            TryGetSnapshotSourceAssembly(parameters, out targetAssembly);
            EnsureSemanticWorkspaceLoaded(targetAssembly, includeReverseDependencies: false);
            var document = ResolveDocument(parameters, out var sourceText, out var position);
            var semanticModel = await document.GetSemanticModelAsync();
            var symbol = await SymbolFinder.FindSymbolAtPositionAsync(semanticModel, position, _workspace);
            var items = new List<CodeIndexItem>();

            if (symbol != null)
            {
                foreach (var location in symbol.Locations)
                {
                    AddLocationItem(items, symbol, location);
                }
            }

            return BuildResponse(null, items);
        }

        private async Task<CodeIndexResponse> QueryReferencesAsync(Dictionary<string, object> parameters)
        {
            AssemblySnapshot targetAssembly;
            TryGetSnapshotSourceAssembly(parameters, out targetAssembly);
            EnsureSemanticWorkspaceLoaded(targetAssembly, includeReverseDependencies: true);
            var document = ResolveDocument(parameters, out var sourceText, out var position);
            var semanticModel = await document.GetSemanticModelAsync();
            var symbol = await SymbolFinder.FindSymbolAtPositionAsync(semanticModel, position, _workspace);
            var items = new List<CodeIndexItem>();

            if (symbol != null)
            {
                var candidateDocuments = GetReferenceCandidateDocuments(symbol);
                var references = candidateDocuments == null
                    ? await SymbolFinder.FindReferencesAsync(symbol, _solution)
                    : await SymbolFinder.FindReferencesAsync(symbol, _solution, candidateDocuments);
                foreach (var referencedSymbol in references)
                {
                    foreach (var reference in referencedSymbol.Locations)
                    {
                        AddLocationItem(items, referencedSymbol.Definition, reference.Location);
                    }
                }
            }

            return BuildResponse(null, items
                .OrderBy(item => item.file, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.line)
                .ThenBy(item => item.column)
                .Take(MaxReferenceResults)
                .ToList());
        }

        private bool TryQueryDeclarationDefinition(Dictionary<string, object> parameters, out CodeIndexResponse response)
        {
            response = null;

            AssemblySnapshot assembly;
            SourceText sourceText;
            int position;
            string fullPath;
            string relativePath;
            if (!TryResolveSnapshotSourcePosition(parameters, out assembly, out sourceText, out position, out fullPath, out relativePath))
            {
                return false;
            }

            var tree = CSharpSyntaxTree.ParseText(sourceText, BuildParseOptions(assembly), fullPath);
            var root = tree.GetRoot();
            var token = FindTokenAtPosition(root, sourceText, position);
            var declaration = GetDeclarationForIdentifier(token);
            if (declaration == null)
            {
                return false;
            }

            var identifier = GetDeclarationIdentifier(declaration);
            if (identifier.RawKind == 0 || position < identifier.SpanStart || position > identifier.Span.End)
            {
                return false;
            }

            var item = BuildSyntaxDeclarationItem(declaration, identifier, relativePath, sourceText);
            if (item == null)
            {
                return false;
            }

            response = BuildResponse(null, new List<CodeIndexItem> { item });
            return true;
        }

        private bool TryQueryIndexedDefinition(Dictionary<string, object> parameters, out CodeIndexResponse response)
        {
            response = null;

            AssemblySnapshot assembly;
            SourceText sourceText;
            int position;
            string fullPath;
            string relativePath;
            if (!TryResolveSnapshotSourcePosition(parameters, out assembly, out sourceText, out position, out fullPath, out relativePath))
            {
                return false;
            }

            var tree = CSharpSyntaxTree.ParseText(sourceText, BuildParseOptions(assembly), fullPath);
            var root = tree.GetRoot();
            var token = FindTokenAtPosition(root, sourceText, position);
            if (!token.IsKind(SyntaxKind.IdentifierToken) || !IsSafeIndexedDefinitionReference(token))
            {
                return false;
            }

            List<CodeIndexItem> nameMatches;
            var matches = _symbolNameMap.TryGetValue(token.ValueText, out nameMatches)
                ? nameMatches.Take(2).ToList()
                : new List<CodeIndexItem>();
            if (matches.Count != 1)
            {
                return false;
            }

            CodeIndexItem declarationItem;
            if (TryBuildSyntaxDeclarationItem(matches[0], out declarationItem))
            {
                response = BuildResponse(null, new List<CodeIndexItem> { declarationItem });
                return true;
            }

            response = BuildResponse(null, new List<CodeIndexItem> { matches[0] });
            return true;
        }

        private bool TryResolveSnapshotSourcePosition(
            Dictionary<string, object> parameters,
            out AssemblySnapshot assembly,
            out SourceText sourceText,
            out int position,
            out string fullPath,
            out string relativePath)
        {
            assembly = null;
            sourceText = null;
            position = 0;
            fullPath = null;
            relativePath = null;

            var file = GetString(parameters, "file");
            var line = GetInt(parameters, "line");
            var column = GetInt(parameters, "column");
            if (string.IsNullOrWhiteSpace(file))
            {
                throw new ArgumentException("Missing required parameter: --file");
            }

            if (line <= 0 || column <= 0)
            {
                throw new ArgumentException("--line and --column must be positive 1-based numbers.");
            }

            if (_manifest == null)
            {
                EnsureSnapshotIndexLoaded();
            }

            if (!TryFindSnapshotSource(file, out assembly, out fullPath, out relativePath) || !File.Exists(fullPath))
            {
                return false;
            }

            sourceText = SourceText.From(File.ReadAllText(fullPath, Encoding.UTF8), Encoding.UTF8);
            if (line > sourceText.Lines.Count)
            {
                throw new ArgumentOutOfRangeException("line", "Line is outside the document.");
            }

            var textLine = sourceText.Lines[line - 1];
            var zeroBasedColumn = Math.Max(0, column - 1);
            var offsetInLine = Math.Min(zeroBasedColumn, Math.Max(0, textLine.End - textLine.Start));
            position = textLine.Start + offsetInLine;
            return true;
        }

        private bool TryFindSnapshotSource(string file, out AssemblySnapshot assembly, out string fullPath, out string relativePath)
        {
            assembly = null;
            fullPath = null;
            relativePath = null;
            if (_manifest == null)
            {
                return false;
            }

            var requestedFullPath = Path.IsPathRooted(file)
                ? Path.GetFullPath(file)
                : Path.GetFullPath(Path.Combine(_projectRoot, file));

            SourceSnapshotInfo sourceInfo;
            if (_sourcePathMap != null && _sourcePathMap.TryGetValue(requestedFullPath, out sourceInfo))
            {
                assembly = sourceInfo.Assembly;
                fullPath = sourceInfo.FullPath;
                relativePath = sourceInfo.RelativePath;
                return true;
            }

            return false;
        }

        private bool TryGetSnapshotSourceAssembly(Dictionary<string, object> parameters, out AssemblySnapshot assembly)
        {
            return TryGetSnapshotSourceAssembly(GetString(parameters, "file"), out assembly);
        }

        private bool TryGetSnapshotSourceAssembly(string file, out AssemblySnapshot assembly)
        {
            assembly = null;
            if (string.IsNullOrWhiteSpace(file))
            {
                return false;
            }

            EnsureSnapshotIndexLoaded();
            string fullPath;
            string relativePath;
            return TryFindSnapshotSource(file, out assembly, out fullPath, out relativePath);
        }

        private void EnsureSemanticWorkspaceLoadedForTypeQuery(string typeName)
        {
            EnsureSnapshotIndexLoaded();
            var requiredAssemblyIds = BuildTypeQueryAssemblyIds(typeName);
            if (requiredAssemblyIds.Count == 0)
            {
                EnsureSemanticWorkspaceLoaded();
                return;
            }

            EnsureSemanticWorkspaceLoaded(requiredAssemblyIds, loadAllAssemblies: false);
        }

        private HashSet<string> BuildTypeQueryAssemblyIds(string typeName)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var shortName = GetShortTypeName(typeName);
            if (string.IsNullOrWhiteSpace(shortName) || _symbolNameMap == null)
            {
                return result;
            }

            List<CodeIndexItem> candidates;
            if (!_symbolNameMap.TryGetValue(shortName, out candidates))
            {
                return result;
            }

            for (var i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                AssemblySnapshot assembly;
                string fullPath;
                string relativePath;
                if (candidate != null && TryFindSnapshotSource(candidate.file, out assembly, out fullPath, out relativePath))
                {
                    var required = BuildRequiredAssemblyIds(assembly, includeReverseDependencies: true);
                    foreach (var assemblyId in required)
                    {
                        result.Add(assemblyId);
                    }
                }
            }

            return result;
        }

        private static string GetShortTypeName(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
            {
                return null;
            }

            var normalized = typeName.Trim();
            var lastDot = normalized.LastIndexOf('.');
            if (lastDot >= 0 && lastDot + 1 < normalized.Length)
            {
                normalized = normalized.Substring(lastDot + 1);
            }

            var nestedSeparator = normalized.LastIndexOf('+');
            if (nestedSeparator >= 0 && nestedSeparator + 1 < normalized.Length)
            {
                normalized = normalized.Substring(nestedSeparator + 1);
            }

            return normalized;
        }

        private static SyntaxToken FindTokenAtPosition(SyntaxNode root, SourceText sourceText, int position)
        {
            if (root == null || sourceText == null || sourceText.Length == 0)
            {
                return default(SyntaxToken);
            }

            var safePosition = Math.Max(0, Math.Min(position, sourceText.Length - 1));
            var token = root.FindToken(safePosition);
            if (!token.IsKind(SyntaxKind.IdentifierToken) && safePosition > 0)
            {
                token = root.FindToken(safePosition - 1);
            }

            return token;
        }

        private static bool IsSafeIndexedDefinitionReference(SyntaxToken token)
        {
            var identifierName = token.Parent as IdentifierNameSyntax;
            if (identifierName == null)
            {
                return false;
            }

            var memberAccess = identifierName.Parent as MemberAccessExpressionSyntax;
            if (memberAccess != null && memberAccess.Name == identifierName)
            {
                return true;
            }

            var qualifiedName = identifierName.Parent as QualifiedNameSyntax;
            return qualifiedName != null && qualifiedName.Right == identifierName;
        }

        private static SyntaxNode GetDeclarationForIdentifier(SyntaxToken token)
        {
            if (!token.IsKind(SyntaxKind.IdentifierToken))
            {
                return null;
            }

            var node = token.Parent;
            var typeDeclaration = node as BaseTypeDeclarationSyntax;
            if (typeDeclaration != null && typeDeclaration.Identifier == token)
            {
                return typeDeclaration;
            }

            var delegateDeclaration = node as DelegateDeclarationSyntax;
            if (delegateDeclaration != null && delegateDeclaration.Identifier == token)
            {
                return delegateDeclaration;
            }

            var methodDeclaration = node as MethodDeclarationSyntax;
            if (methodDeclaration != null && methodDeclaration.Identifier == token)
            {
                return methodDeclaration;
            }

            var constructorDeclaration = node as ConstructorDeclarationSyntax;
            if (constructorDeclaration != null && constructorDeclaration.Identifier == token)
            {
                return constructorDeclaration;
            }

            var destructorDeclaration = node as DestructorDeclarationSyntax;
            if (destructorDeclaration != null && destructorDeclaration.Identifier == token)
            {
                return destructorDeclaration;
            }

            var propertyDeclaration = node as PropertyDeclarationSyntax;
            if (propertyDeclaration != null && propertyDeclaration.Identifier == token)
            {
                return propertyDeclaration;
            }

            var eventDeclaration = node as EventDeclarationSyntax;
            if (eventDeclaration != null && eventDeclaration.Identifier == token)
            {
                return eventDeclaration;
            }

            var variableDeclaration = node as VariableDeclaratorSyntax;
            if (variableDeclaration != null && variableDeclaration.Identifier == token)
            {
                return variableDeclaration;
            }

            var enumMemberDeclaration = node as EnumMemberDeclarationSyntax;
            if (enumMemberDeclaration != null && enumMemberDeclaration.Identifier == token)
            {
                return enumMemberDeclaration;
            }

            var parameterDeclaration = node as ParameterSyntax;
            if (parameterDeclaration != null && parameterDeclaration.Identifier == token)
            {
                return parameterDeclaration;
            }

            var typeParameterDeclaration = node as TypeParameterSyntax;
            if (typeParameterDeclaration != null && typeParameterDeclaration.Identifier == token)
            {
                return typeParameterDeclaration;
            }

            return null;
        }

        private static SyntaxToken GetDeclarationIdentifier(SyntaxNode node)
        {
            var typeDeclaration = node as BaseTypeDeclarationSyntax;
            if (typeDeclaration != null)
            {
                return typeDeclaration.Identifier;
            }

            var delegateDeclaration = node as DelegateDeclarationSyntax;
            if (delegateDeclaration != null)
            {
                return delegateDeclaration.Identifier;
            }

            var methodDeclaration = node as MethodDeclarationSyntax;
            if (methodDeclaration != null)
            {
                return methodDeclaration.Identifier;
            }

            var constructorDeclaration = node as ConstructorDeclarationSyntax;
            if (constructorDeclaration != null)
            {
                return constructorDeclaration.Identifier;
            }

            var destructorDeclaration = node as DestructorDeclarationSyntax;
            if (destructorDeclaration != null)
            {
                return destructorDeclaration.Identifier;
            }

            var propertyDeclaration = node as PropertyDeclarationSyntax;
            if (propertyDeclaration != null)
            {
                return propertyDeclaration.Identifier;
            }

            var eventDeclaration = node as EventDeclarationSyntax;
            if (eventDeclaration != null)
            {
                return eventDeclaration.Identifier;
            }

            var variableDeclaration = node as VariableDeclaratorSyntax;
            if (variableDeclaration != null)
            {
                return variableDeclaration.Identifier;
            }

            var enumMemberDeclaration = node as EnumMemberDeclarationSyntax;
            if (enumMemberDeclaration != null)
            {
                return enumMemberDeclaration.Identifier;
            }

            var parameterDeclaration = node as ParameterSyntax;
            if (parameterDeclaration != null)
            {
                return parameterDeclaration.Identifier;
            }

            var typeParameterDeclaration = node as TypeParameterSyntax;
            if (typeParameterDeclaration != null)
            {
                return typeParameterDeclaration.Identifier;
            }

            return default(SyntaxToken);
        }

        private static CodeIndexItem BuildSyntaxDeclarationItem(SyntaxNode declaration, SyntaxToken identifier, string relativePath, SourceText sourceText)
        {
            if (declaration == null || identifier.RawKind == 0 || sourceText == null)
            {
                return null;
            }

            var linePosition = sourceText.Lines.GetLinePosition(identifier.SpanStart);
            var container = GetSyntaxDeclarationContainer(declaration);
            var name = identifier.ValueText;
            return new CodeIndexItem
            {
                kind = GetSyntaxDeclarationKind(declaration),
                name = name,
                container = container,
                file = relativePath == null ? null : relativePath.Replace('\\', '/'),
                line = linePosition.Line + 1,
                column = linePosition.Character + 1,
                signature = BuildSyntaxDeclarationSignature(declaration, name, container)
            };
        }

        private static string GetSyntaxDeclarationKind(SyntaxNode declaration)
        {
            if (declaration is BaseTypeDeclarationSyntax || declaration is DelegateDeclarationSyntax)
            {
                return "namedtype";
            }

            if (declaration is MethodDeclarationSyntax || declaration is ConstructorDeclarationSyntax || declaration is DestructorDeclarationSyntax)
            {
                return "method";
            }

            if (declaration is PropertyDeclarationSyntax)
            {
                return "property";
            }

            if (declaration is EventDeclarationSyntax)
            {
                return "event";
            }

            var variable = declaration as VariableDeclaratorSyntax;
            if (variable != null)
            {
                if (variable.Parent != null && variable.Parent.Parent is FieldDeclarationSyntax)
                {
                    return "field";
                }

                if (variable.Parent != null && variable.Parent.Parent is EventFieldDeclarationSyntax)
                {
                    return "event";
                }

                return "local";
            }

            if (declaration is EnumMemberDeclarationSyntax)
            {
                return "field";
            }

            if (declaration is ParameterSyntax)
            {
                return "parameter";
            }

            if (declaration is TypeParameterSyntax)
            {
                return "typeparameter";
            }

            return "symbol";
        }

        private static string GetSyntaxDeclarationContainer(SyntaxNode declaration)
        {
            var parts = new List<string>();
            var namespaces = declaration.Ancestors().OfType<NamespaceDeclarationSyntax>().Reverse();
            foreach (var namespaceDeclaration in namespaces)
            {
                parts.Add(namespaceDeclaration.Name.ToString());
            }

            var types = declaration.Ancestors().OfType<BaseTypeDeclarationSyntax>().Reverse();
            foreach (var type in types)
            {
                parts.Add(type.Identifier.ValueText);
            }

            return parts.Count == 0 ? null : string.Join(".", parts);
        }

        private static string BuildSyntaxDeclarationSignature(SyntaxNode declaration, string name, string container)
        {
            var qualifiedName = string.IsNullOrEmpty(container) ? name : container + "." + name;
            var methodDeclaration = declaration as MethodDeclarationSyntax;
            if (methodDeclaration != null)
            {
                return qualifiedName + BuildParameterListSignature(methodDeclaration.ParameterList);
            }

            var constructorDeclaration = declaration as ConstructorDeclarationSyntax;
            if (constructorDeclaration != null)
            {
                return qualifiedName + BuildParameterListSignature(constructorDeclaration.ParameterList);
            }

            var destructorDeclaration = declaration as DestructorDeclarationSyntax;
            if (destructorDeclaration != null)
            {
                return qualifiedName + BuildParameterListSignature(destructorDeclaration.ParameterList);
            }

            var delegateDeclaration = declaration as DelegateDeclarationSyntax;
            if (delegateDeclaration != null)
            {
                return qualifiedName + BuildParameterListSignature(delegateDeclaration.ParameterList);
            }

            return qualifiedName;
        }

        private static string BuildParameterListSignature(BaseParameterListSyntax parameterList)
        {
            if (parameterList == null || parameterList.Parameters.Count == 0)
            {
                return "()";
            }

            var parts = new List<string>();
            foreach (var parameter in parameterList.Parameters)
            {
                var builder = new StringBuilder();
                foreach (var modifier in parameter.Modifiers)
                {
                    if (builder.Length > 0)
                    {
                        builder.Append(' ');
                    }

                    builder.Append(modifier.Text);
                }

                if (parameter.Type != null)
                {
                    if (builder.Length > 0)
                    {
                        builder.Append(' ');
                    }

                    builder.Append(parameter.Type);
                }

                parts.Add(builder.Length == 0 ? parameter.Identifier.ValueText : builder.ToString());
            }

            return "(" + string.Join(", ", parts) + ")";
        }

        private bool TryBuildSyntaxDeclarationItem(CodeIndexItem indexItem, out CodeIndexItem declarationItem)
        {
            declarationItem = null;
            if (indexItem == null || string.IsNullOrWhiteSpace(indexItem.file) || indexItem.line <= 0)
            {
                return false;
            }

            AssemblySnapshot assembly;
            string fullPath;
            string relativePath;
            if (!TryFindSnapshotSource(indexItem.file, out assembly, out fullPath, out relativePath) || !File.Exists(fullPath))
            {
                return false;
            }

            var sourceText = SourceText.From(File.ReadAllText(fullPath, Encoding.UTF8), Encoding.UTF8);
            if (indexItem.line > sourceText.Lines.Count)
            {
                return false;
            }

            var lineSpan = sourceText.Lines[indexItem.line - 1].Span;
            var tree = CSharpSyntaxTree.ParseText(sourceText, BuildParseOptions(assembly), fullPath);
            var root = tree.GetRoot();
            foreach (var token in root.DescendantTokens())
            {
                if (token.SpanStart < lineSpan.Start)
                {
                    continue;
                }

                if (token.SpanStart >= lineSpan.End)
                {
                    break;
                }

                if (!token.IsKind(SyntaxKind.IdentifierToken) || !string.Equals(token.ValueText, indexItem.name, StringComparison.Ordinal))
                {
                    continue;
                }

                var declaration = GetDeclarationForIdentifier(token);
                if (declaration == null)
                {
                    continue;
                }

                declarationItem = BuildSyntaxDeclarationItem(declaration, token, relativePath, sourceText);
                return declarationItem != null;
            }

            return false;
        }

        private IImmutableSet<Document> GetReferenceCandidateDocuments(ISymbol symbol)
        {
            if (symbol == null || string.IsNullOrWhiteSpace(symbol.Name) || !IsAsciiIdentifier(symbol.Name))
            {
                return null;
            }

            var files = GetTokenDocuments(symbol.Name);
            if (files.Count == 0)
            {
                return null;
            }

            if (_documentPathMap == null || _documentPathMap.Count == 0)
            {
                RebuildDocumentPathMap();
            }

            var builder = ImmutableHashSet.CreateBuilder<Document>();
            foreach (var file in files)
            {
                var fullPath = ResolveAbsolutePath(file);
                Document candidate;
                if (!string.IsNullOrWhiteSpace(fullPath) && _documentPathMap.TryGetValue(fullPath, out candidate))
                {
                    builder.Add(candidate);
                }
            }

            // token 索引只做候选文件剪枝；声明文件强制纳入，找不到候选时回退全量 Roslyn，避免漏结果。
            foreach (var reference in symbol.DeclaringSyntaxReferences)
            {
                var path = reference.SyntaxTree == null ? null : reference.SyntaxTree.FilePath;
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                Document declarationDocument;
                if (_documentPathMap.TryGetValue(Path.GetFullPath(path), out declarationDocument))
                {
                    builder.Add(declarationDocument);
                }
            }

            if (builder.Count == 0 || (_documentPathMap != null && builder.Count >= _documentPathMap.Count))
            {
                return null;
            }

            return builder.ToImmutable();
        }

        private async Task<CodeIndexResponse> QueryImplementationsAsync(Dictionary<string, object> parameters)
        {
            var typeName = GetString(parameters, "type");
            EnsureSemanticWorkspaceLoadedForTypeQuery(typeName);
            var type = await ResolveTypeSymbolAsync(typeName);
            if (type == null && !_loadedAllAssemblies)
            {
                EnsureSemanticWorkspaceLoaded();
                type = await ResolveTypeSymbolAsync(typeName);
            }
            var items = new List<CodeIndexItem>();

            if (type != null)
            {
                var implementations = await SymbolFinder.FindImplementationsAsync(type, _solution);
                foreach (var implementation in implementations)
                {
                    AddSourceLocations(items, implementation);
                }
            }

            return BuildResponse(type == null ? "Type was not found in the loaded snapshot workspace." : null, DistinctItems(items)
                .OrderBy(item => item.file, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.line)
                .Take(MaxReferenceResults)
                .ToList());
        }

        private async Task<CodeIndexResponse> QueryDerivedAsync(Dictionary<string, object> parameters)
        {
            var typeName = GetString(parameters, "type");
            EnsureSemanticWorkspaceLoadedForTypeQuery(typeName);
            var type = await ResolveTypeSymbolAsync(typeName);
            if (type == null && !_loadedAllAssemblies)
            {
                EnsureSemanticWorkspaceLoaded();
                type = await ResolveTypeSymbolAsync(typeName);
            }
            var items = new List<CodeIndexItem>();

            if (type != null)
            {
                if (type.TypeKind == TypeKind.Class)
                {
                    var derivedClasses = await SymbolFinder.FindDerivedClassesAsync(type, _solution);
                    foreach (var derivedClass in derivedClasses)
                    {
                        AddSourceLocations(items, derivedClass);
                    }
                }

                if (type.TypeKind == TypeKind.Interface)
                {
                    var derivedInterfaces = await SymbolFinder.FindDerivedInterfacesAsync(type, _solution);
                    foreach (var derivedInterface in derivedInterfaces)
                    {
                        AddSourceLocations(items, derivedInterface);
                    }

                    var implementations = await SymbolFinder.FindImplementationsAsync(type, _solution);
                    foreach (var implementation in implementations)
                    {
                        AddSourceLocations(items, implementation);
                    }
                }
            }

            return BuildResponse(type == null ? "Type was not found in the loaded snapshot workspace." : null, DistinctItems(items)
                .OrderBy(item => item.file, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.line)
                .Take(MaxReferenceResults)
                .ToList());
        }

        private async Task<CodeIndexResponse> QueryCallersAsync(Dictionary<string, object> parameters)
        {
            AssemblySnapshot targetAssembly;
            TryGetSnapshotSourceAssembly(parameters, out targetAssembly);
            EnsureSemanticWorkspaceLoaded(targetAssembly, includeReverseDependencies: true);
            var document = ResolveDocument(parameters, out var sourceText, out var position);
            var semanticModel = await document.GetSemanticModelAsync();
            var symbol = await SymbolFinder.FindSymbolAtPositionAsync(semanticModel, position, _workspace);
            var items = new List<CodeIndexItem>();

            if (symbol != null)
            {
                var callers = await SymbolFinder.FindCallersAsync(symbol, _solution);
                foreach (var caller in callers)
                {
                    foreach (var location in caller.Locations)
                    {
                        AddLocationItem(items, caller.CallingSymbol, location);
                    }
                }
            }

            return BuildResponse(null, DistinctItems(items)
                .OrderBy(item => item.file, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.line)
                .ThenBy(item => item.column)
                .Take(MaxReferenceResults)
                .ToList());
        }

        private async Task<CodeIndexResponse> QueryDiagnosticsAsync(Dictionary<string, object> parameters)
        {
            var file = GetString(parameters, "file");
            var all = GetBool(parameters, "all");
            if (string.IsNullOrWhiteSpace(file) && !all)
            {
                throw new ArgumentException("Missing required parameter: --file. Use --all true to run full workspace diagnostics.");
            }

            if (!string.IsNullOrWhiteSpace(file))
            {
                AssemblySnapshot targetAssembly;
                TryGetSnapshotSourceAssembly(file, out targetAssembly);
                EnsureSemanticWorkspaceLoaded(targetAssembly, includeReverseDependencies: false);
            }
            else
            {
                EnsureSemanticWorkspaceLoaded();
            }

            var diagnostics = new List<Diagnostic>();

            if (!string.IsNullOrWhiteSpace(file))
            {
                var document = ResolveDocument(file);
                var semanticModel = await document.GetSemanticModelAsync();
                diagnostics.AddRange(semanticModel.GetDiagnostics());
            }
            else
            {
                foreach (var project in _solution.Projects)
                {
                    var compilation = await project.GetCompilationAsync();
                    if (compilation != null)
                    {
                        diagnostics.AddRange(compilation.GetDiagnostics());
                    }
                }
            }

            return BuildResponse(null, diagnostics
                .Where(diagnostic => diagnostic != null)
                .OrderByDescending(diagnostic => diagnostic.Severity)
                .ThenBy(diagnostic => diagnostic.Location == null ? string.Empty : diagnostic.Location.GetLineSpan().Path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(diagnostic => diagnostic.Location == null ? 0 : diagnostic.Location.GetLineSpan().StartLinePosition.Line)
                .Take(MaxDiagnosticResults)
                .Select(ToDiagnosticItem)
                .ToList());
        }

        private Document ResolveDocument(Dictionary<string, object> parameters, out SourceText sourceText, out int position)
        {
            var file = GetString(parameters, "file");
            var line = GetInt(parameters, "line");
            var column = GetInt(parameters, "column");

            if (string.IsNullOrWhiteSpace(file))
            {
                throw new ArgumentException("Missing required parameter: --file");
            }

            if (line <= 0 || column <= 0)
            {
                throw new ArgumentException("--line and --column must be positive 1-based numbers.");
            }

            var document = ResolveDocument(file);
            sourceText = document.GetTextAsync().GetAwaiter().GetResult();
            if (line > sourceText.Lines.Count)
            {
                throw new ArgumentOutOfRangeException("line", "Line is outside the document.");
            }

            var textLine = sourceText.Lines[line - 1];
            var zeroBasedColumn = Math.Max(0, column - 1);
            var offsetInLine = Math.Min(zeroBasedColumn, Math.Max(0, textLine.End - textLine.Start));
            position = textLine.Start + offsetInLine;
            return document;
        }

        private Document ResolveDocument(string file)
        {
            if (string.IsNullOrWhiteSpace(file))
            {
                throw new ArgumentException("Missing required parameter: --file");
            }

            var fullPath = Path.IsPathRooted(file)
                ? Path.GetFullPath(file)
                : Path.GetFullPath(Path.Combine(_projectRoot, file));
            if (_documentPathMap == null || _documentPathMap.Count == 0)
            {
                RebuildDocumentPathMap();
            }

            Document document;
            if (!_documentPathMap.TryGetValue(fullPath, out document))
            {
                document = _solution.Projects
                    .SelectMany(project => project.Documents)
                    .FirstOrDefault(item => string.Equals(Path.GetFullPath(item.FilePath ?? string.Empty), fullPath, StringComparison.OrdinalIgnoreCase));
            }

            if (document == null)
            {
                throw new FileNotFoundException("File is not part of the loaded Unity snapshot workspace.", file);
            }

            return document;
        }

        private void RebuildDocumentPathMap()
        {
            var map = new Dictionary<string, Document>(StringComparer.OrdinalIgnoreCase);
            if (_solution != null)
            {
                foreach (var document in _solution.Projects.SelectMany(project => project.Documents))
                {
                    if (!IsCSharpDocument(document))
                    {
                        continue;
                    }

                    var path = Path.GetFullPath(document.FilePath);
                    if (!map.ContainsKey(path))
                    {
                        map.Add(path, document);
                    }
                }
            }

            _documentPathMap = map;
        }

        private async Task<INamedTypeSymbol> ResolveTypeSymbolAsync(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
            {
                throw new ArgumentException("Missing required parameter: --type");
            }

            var normalized = typeName.Trim();
            foreach (var project in _solution.Projects)
            {
                var compilation = await project.GetCompilationAsync();
                if (compilation == null)
                {
                    continue;
                }

                var metadataType = compilation.GetTypeByMetadataName(normalized);
                if (metadataType != null)
                {
                    return metadataType;
                }

                var found = FindTypeInNamespace(compilation.GlobalNamespace, normalized);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private static INamedTypeSymbol FindTypeInNamespace(INamespaceSymbol namespaceSymbol, string query)
        {
            if (namespaceSymbol == null)
            {
                return null;
            }

            foreach (var type in namespaceSymbol.GetTypeMembers())
            {
                var found = FindTypeInType(type, query);
                if (found != null)
                {
                    return found;
                }
            }

            foreach (var childNamespace in namespaceSymbol.GetNamespaceMembers())
            {
                var found = FindTypeInNamespace(childNamespace, query);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private static INamedTypeSymbol FindTypeInType(INamedTypeSymbol type, string query)
        {
            if (type == null)
            {
                return null;
            }

            if (string.Equals(type.Name, query, StringComparison.OrdinalIgnoreCase)
                || string.Equals(type.ToDisplayString(), query, StringComparison.OrdinalIgnoreCase)
                || string.Equals(type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat), query, StringComparison.OrdinalIgnoreCase))
            {
                return type;
            }

            foreach (var nestedType in type.GetTypeMembers())
            {
                var found = FindTypeInType(nestedType, query);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private void AddSourceLocations(List<CodeIndexItem> items, ISymbol symbol)
        {
            if (symbol == null)
            {
                return;
            }

            foreach (var location in symbol.Locations)
            {
                AddLocationItem(items, symbol, location);
            }
        }

        private static IEnumerable<CodeIndexItem> DistinctItems(IEnumerable<CodeIndexItem> items)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                if (item == null)
                {
                    continue;
                }

                var key = (item.kind ?? string.Empty)
                          + "|" + (item.name ?? string.Empty)
                          + "|" + (item.file ?? string.Empty)
                          + "|" + item.line
                          + "|" + item.column;
                if (seen.Add(key))
                {
                    yield return item;
                }
            }
        }

        private CodeIndexItem ToDiagnosticItem(Diagnostic diagnostic)
        {
            var span = diagnostic.Location == null ? default(FileLinePositionSpan) : diagnostic.Location.GetLineSpan();
            var hasPath = diagnostic.Location != null
                          && diagnostic.Location.IsInSource
                          && !string.IsNullOrEmpty(span.Path);

            return new CodeIndexItem
            {
                kind = "diagnostic",
                name = diagnostic.Id,
                id = diagnostic.Id,
                severity = diagnostic.Severity.ToString(),
                message = diagnostic.GetMessage(),
                file = hasPath ? ToProjectRelativePath(span.Path) : null,
                line = hasPath ? span.StartLinePosition.Line + 1 : 0,
                column = hasPath ? span.StartLinePosition.Character + 1 : 0,
                preview = diagnostic.GetMessage()
            };
        }

        private async Task<List<CodeIndexItem>> BuildSymbolTableAsync(Solution solution)
        {
            var result = new List<CodeIndexItem>();
            foreach (var project in solution.Projects)
            {
                foreach (var document in project.Documents)
                {
                    if (!IsCSharpDocument(document))
                    {
                        continue;
                    }

                    var root = await document.GetSyntaxRootAsync();
                    var semanticModel = await document.GetSemanticModelAsync();
                    if (root == null || semanticModel == null)
                    {
                        continue;
                    }

                    foreach (var node in root.DescendantNodes())
                    {
                        ISymbol symbol = null;
                        if (node is BaseTypeDeclarationSyntax
                            || node is DelegateDeclarationSyntax
                            || node is MethodDeclarationSyntax
                            || node is ConstructorDeclarationSyntax
                            || node is PropertyDeclarationSyntax
                            || node is EventDeclarationSyntax)
                        {
                            symbol = semanticModel.GetDeclaredSymbol(node);
                        }
                        else if (node is EnumMemberDeclarationSyntax enumMember)
                        {
                            symbol = semanticModel.GetDeclaredSymbol(enumMember);
                        }
                        else if (node is VariableDeclaratorSyntax variable
                                 && variable.Parent != null
                                 && (variable.Parent.Parent is FieldDeclarationSyntax
                                     || variable.Parent.Parent is EventFieldDeclarationSyntax))
                        {
                            symbol = semanticModel.GetDeclaredSymbol(variable);
                        }

                        if (symbol == null)
                        {
                            continue;
                        }

                        var location = symbol.Locations.FirstOrDefault(item => item.IsInSource);
                        if (location != null)
                        {
                            AddLocationItem(result, symbol, location);
                        }
                    }
                }
            }

            return result;
        }

        private void AddLocationItem(List<CodeIndexItem> items, ISymbol symbol, Location location)
        {
            if (symbol == null || location == null || !location.IsInSource)
            {
                return;
            }

            var span = location.GetLineSpan();
            var filePath = string.IsNullOrEmpty(span.Path) ? null : ToProjectRelativePath(span.Path);
            items.Add(new CodeIndexItem
            {
                kind = symbol.Kind.ToString().ToLowerInvariant(),
                name = symbol.Name,
                container = GetContainer(symbol),
                file = filePath,
                line = span.StartLinePosition.Line + 1,
                column = span.StartLinePosition.Character + 1,
                signature = symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)
            });
        }

        private CodeIndexResponse BuildResponse(string warning, List<CodeIndexItem> items = null)
        {
            return new CodeIndexResponse
            {
                items = items,
                warning = string.IsNullOrWhiteSpace(warning) ? BuildWorkspaceWarning() : warning
            };
        }

        private static CodeIndexResponse BuildErrorResponse(string error, string errorCode)
        {
            return new CodeIndexResponse
            {
                error = error,
                errorCode = errorCode
            };
        }

        private string ToProjectRelativePath(string path)
        {
            var fullPath = Path.GetFullPath(path);
            var root = _projectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return fullPath.Substring(root.Length).Replace('\\', '/');
            }

            return fullPath.Replace('\\', '/');
        }

        private static string GetContainer(ISymbol symbol)
        {
            if (symbol == null)
            {
                return null;
            }

            if (symbol.ContainingType != null)
            {
                return symbol.ContainingType.ToDisplayString();
            }

            if (symbol.ContainingNamespace != null && !symbol.ContainingNamespace.IsGlobalNamespace)
            {
                return symbol.ContainingNamespace.ToDisplayString();
            }

            return null;
        }

        private static bool IsCSharpDocument(Document document)
        {
            return document != null
                && !string.IsNullOrEmpty(document.FilePath)
                && document.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
        }

        private IEnumerable<MetadataReference> BuildMetadataReferences(AssemblySnapshot record)
        {
            var result = new List<MetadataReference>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < record.References.Count; i++)
            {
                var path = ResolveAbsolutePath(record.References[i].Path);
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || !seen.Add(path))
                {
                    continue;
                }

                try
                {
                    result.Add(GetOrCreateMetadataReference(path));
                }
                catch (Exception ex)
                {
                    _workspaceWarnings.Add("Failed to load metadata reference " + path + ": " + ex.Message);
                }
            }

            return result;
        }

        private MetadataReference GetOrCreateMetadataReference(string path)
        {
            MetadataReference reference;
            if (_metadataReferenceCache.TryGetValue(path, out reference))
            {
                return reference;
            }

            reference = MetadataReference.CreateFromFile(path);
            _metadataReferenceCache[path] = reference;
            return reference;
        }

        private CSharpParseOptions BuildParseOptions(AssemblySnapshot record)
        {
            var languageVersion = ParseLanguageVersion(record.LanguageVersion);
            return new CSharpParseOptions(
                languageVersion: languageVersion,
                preprocessorSymbols: record.Defines ?? new List<string>(),
                documentationMode: DocumentationMode.Parse);
        }

        private static LanguageVersion ParseLanguageVersion(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return LanguageVersion.CSharp9;
            }

            var normalized = value.Trim();
            if (normalized == "7.3")
            {
                return LanguageVersion.CSharp7_3;
            }

            if (normalized == "8" || normalized == "8.0")
            {
                return LanguageVersion.CSharp8;
            }

            return LanguageVersion.CSharp9;
        }

        private SnapshotManifest ReadManifest(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    ReadHeader(reader, ManifestFormatKind);
                    var manifest = new SnapshotManifest
                    {
                        SchemaVersion = reader.ReadInt32(),
                        ProjectRootHash = ReadString(reader),
                        UnityVersion = ReadString(reader),
                        BuildTarget = ReadString(reader),
                        GenerationId = ReadString(reader),
                        IncludePackageCacheSourceAssemblies = reader.ReadBoolean(),
                        IgnoredAssemblyPatterns = ReadStringList(reader),
                        IgnoredSourcePathPatterns = ReadStringList(reader),
                        FilterHash = ReadString(reader),
                        ExcludedAssemblyCount = reader.ReadInt32(),
                        ExcludedSourceFileCount = reader.ReadInt32()
                    };

                    if (manifest.SchemaVersion != SchemaVersion)
                    {
                        StaleReason = "schemaMismatch";
                        throw new InvalidDataException("Unsupported Code Index snapshot schema version: " + manifest.SchemaVersion);
                    }

                    var count = reader.ReadInt32();
                    for (var i = 0; i < count; i++)
                    {
                        manifest.Assemblies.Add(ReadAssemblyRecord(reader));
                    }

                    manifest.SnapshotContentHash = ComputeSnapshotContentHash(manifest);
                    return manifest;
                }
            }
            catch (InvalidDataException)
            {
                StaleReason = "schemaMismatch";
                throw;
            }
        }

        private AssemblySnapshot ReadAssemblySnapshot(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                ReadHeader(reader, AssemblyFormatKind);
                var record = ReadAssemblyRecord(reader);
                record.Defines = ReadStringList(reader);
                record.Sources = ReadFileStates(reader);
                record.References = ReadFileStates(reader);
                record.ProjectReferenceAssemblyNames = ReadStringList(reader);
                record.CompilerOptions = ReadStringList(reader);
                return record;
            }
        }

        private static void ReadHeader(BinaryReader reader, int expectedFormatKind)
        {
            var magic = Encoding.ASCII.GetString(reader.ReadBytes(Magic.Length));
            if (!string.Equals(magic, Magic, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Invalid Code Index snapshot header.");
            }

            var schema = reader.ReadInt32();
            var formatKind = reader.ReadInt32();
            reader.ReadInt64();
            if (schema != SchemaVersion || formatKind != expectedFormatKind)
            {
                throw new InvalidDataException("Unsupported Code Index snapshot format.");
            }
        }

        private static AssemblySnapshot ReadAssemblyRecord(BinaryReader reader)
        {
            return new AssemblySnapshot
            {
                AssemblyName = ReadString(reader),
                AssemblyId = ReadString(reader),
                SnapshotFile = ReadString(reader),
                NameIndexFile = ReadString(reader),
                TokenIndexFile = ReadString(reader),
                OutputPath = ReadString(reader),
                AsmdefPath = ReadString(reader),
                LanguageVersion = ReadString(reader),
                AllowUnsafe = reader.ReadBoolean(),
                SourceFileCount = reader.ReadInt32(),
                ReferenceCount = reader.ReadInt32(),
                DefinesHash = ReadString(reader),
                SourcesHash = ReadString(reader),
                ReferencesHash = ReadString(reader),
                CompilerOptionsHash = ReadString(reader),
                AssemblyHash = ReadString(reader),
                LastWriteTimeTicks = reader.ReadInt64(),
                DependencyAssemblyIds = ReadStringList(reader),
                ReverseDependencyAssemblyIds = ReadStringList(reader)
            };
        }

        private static List<string> ReadStringList(BinaryReader reader)
        {
            var count = reader.ReadInt32();
            var result = new List<string>(Math.Max(0, count));
            for (var i = 0; i < count; i++)
            {
                result.Add(ReadString(reader));
            }

            return result;
        }

        private static List<FileState> ReadFileStates(BinaryReader reader)
        {
            var count = reader.ReadInt32();
            var result = new List<FileState>(Math.Max(0, count));
            for (var i = 0; i < count; i++)
            {
                result.Add(new FileState
                {
                    Path = ReadString(reader),
                    Length = reader.ReadInt64(),
                    LastWriteTimeTicks = reader.ReadInt64(),
                    Hash = ReadString(reader)
                });
            }

            return result;
        }

        private static string ReadString(BinaryReader reader)
        {
            var length = reader.ReadInt32();
            if (length < 0)
            {
                return null;
            }

            return Encoding.UTF8.GetString(reader.ReadBytes(length));
        }

        private string GetSnapshotDirectory()
        {
            return Path.Combine(_projectRoot, SnapshotRelativeDirectory);
        }

        private string GetManifestPath()
        {
            return Path.Combine(GetSnapshotDirectory(), ManifestFileName);
        }

        private string ResolveAbsolutePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            return Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(_projectRoot, path));
        }

        private static string ComputeSnapshotContentHash(SnapshotManifest manifest)
        {
            var parts = new List<string>();
            if (manifest == null)
            {
                return ComputeHash(parts);
            }

            AddContentPart(parts, "schemaVersion", manifest.SchemaVersion.ToString(CultureInfo.InvariantCulture));
            AddContentPart(parts, "projectRootHash", manifest.ProjectRootHash);
            AddContentPart(parts, "unityVersion", manifest.UnityVersion);
            AddContentPart(parts, "buildTarget", manifest.BuildTarget);
            AddContentPart(parts, "includePackageCacheSourceAssemblies", manifest.IncludePackageCacheSourceAssemblies ? "true" : "false");
            AddStringListContentParts(parts, "ignoredAssemblyPatterns", manifest.IgnoredAssemblyPatterns);
            AddStringListContentParts(parts, "ignoredSourcePathPatterns", manifest.IgnoredSourcePathPatterns);
            AddContentPart(parts, "filterHash", manifest.FilterHash);
            AddContentPart(parts, "excludedAssemblyCount", manifest.ExcludedAssemblyCount.ToString(CultureInfo.InvariantCulture));
            AddContentPart(parts, "excludedSourceFileCount", manifest.ExcludedSourceFileCount.ToString(CultureInfo.InvariantCulture));
            AddContentPart(parts, "assemblyCount", manifest.Assemblies.Count.ToString(CultureInfo.InvariantCulture));
            for (var i = 0; i < manifest.Assemblies.Count; i++)
            {
                AddAssemblyContentParts(parts, manifest.Assemblies[i], i);
            }

            return ComputeHash(parts);
        }

        private static void AddAssemblyContentParts(List<string> parts, AssemblySnapshot record, int index)
        {
            AddContentPart(parts, "assembly[" + index + "].assemblyName", record.AssemblyName);
            AddContentPart(parts, "assembly[" + index + "].assemblyId", record.AssemblyId);
            AddContentPart(parts, "assembly[" + index + "].snapshotFile", record.SnapshotFile);
            AddContentPart(parts, "assembly[" + index + "].nameIndexFile", record.NameIndexFile);
            AddContentPart(parts, "assembly[" + index + "].tokenIndexFile", record.TokenIndexFile);
            AddContentPart(parts, "assembly[" + index + "].outputPath", record.OutputPath);
            AddContentPart(parts, "assembly[" + index + "].asmdefPath", record.AsmdefPath);
            AddContentPart(parts, "assembly[" + index + "].languageVersion", record.LanguageVersion);
            AddContentPart(parts, "assembly[" + index + "].allowUnsafe", record.AllowUnsafe ? "true" : "false");
            AddContentPart(parts, "assembly[" + index + "].sourceFileCount", record.SourceFileCount.ToString(CultureInfo.InvariantCulture));
            AddContentPart(parts, "assembly[" + index + "].referenceCount", record.ReferenceCount.ToString(CultureInfo.InvariantCulture));
            AddContentPart(parts, "assembly[" + index + "].definesHash", record.DefinesHash);
            AddContentPart(parts, "assembly[" + index + "].sourcesHash", record.SourcesHash);
            AddContentPart(parts, "assembly[" + index + "].referencesHash", record.ReferencesHash);
            AddContentPart(parts, "assembly[" + index + "].compilerOptionsHash", record.CompilerOptionsHash);
            AddContentPart(parts, "assembly[" + index + "].assemblyHash", record.AssemblyHash);
            AddContentPart(parts, "assembly[" + index + "].lastWriteTimeTicks", record.LastWriteTimeTicks.ToString(CultureInfo.InvariantCulture));
            AddStringListContentParts(parts, "assembly[" + index + "].dependencyAssemblyIds", record.DependencyAssemblyIds);
            AddStringListContentParts(parts, "assembly[" + index + "].reverseDependencyAssemblyIds", record.ReverseDependencyAssemblyIds);
        }

        private static void AddStringListContentParts(List<string> parts, string name, List<string> values)
        {
            var count = values == null ? 0 : values.Count;
            AddContentPart(parts, name + ".count", count.ToString(CultureInfo.InvariantCulture));
            for (var i = 0; values != null && i < values.Count; i++)
            {
                AddContentPart(parts, name + "[" + i + "]", values[i]);
            }
        }

        private static void AddContentPart(List<string> parts, string name, string value)
        {
            parts.Add(name + "=" + (value ?? string.Empty));
        }

        private static string ComputeHash(IEnumerable<string> values)
        {
            var builder = new StringBuilder();
            if (values != null)
            {
                foreach (var value in values)
                {
                    builder.Append(value ?? string.Empty).Append('\n');
                }
            }

            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
                var hash = new StringBuilder(bytes.Length * 2);
                for (var i = 0; i < bytes.Length; i++)
                {
                    hash.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
                }

                return hash.ToString();
            }
        }

        private static string ComputeFileHash(string path)
        {
            try
            {
                using (var sha = System.Security.Cryptography.SHA256.Create())
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var bytes = sha.ComputeHash(stream);
                    var builder = new StringBuilder(bytes.Length * 2);
                    for (var i = 0; i < bytes.Length; i++)
                    {
                        builder.Append(bytes[i].ToString("x2"));
                    }

                    return builder.ToString();
                }
            }
            catch
            {
                return null;
            }
        }

        private string BuildWorkspaceWarning()
        {
            if (_workspaceWarnings == null || _workspaceWarnings.Count == 0)
            {
                return null;
            }

            return string.Join(" | ", _workspaceWarnings.Take(5));
        }

        private static List<CodeIndexItem> SelectTopSymbols(IEnumerable<CodeIndexItem> candidates, string query)
        {
            var result = new List<CodeIndexItem>();
            if (candidates == null)
            {
                return result;
            }

            foreach (var item in candidates)
            {
                if (item == null
                    || (!Contains(item.name, query) && !Contains(item.container, query) && !Contains(item.signature, query)))
                {
                    continue;
                }

                InsertTopSymbol(result, item, query);
            }

            return result;
        }

        private static void InsertTopSymbol(List<CodeIndexItem> result, CodeIndexItem item, string query)
        {
            var insertIndex = result.Count;
            for (var i = 0; i < result.Count; i++)
            {
                if (CompareSymbolResult(item, result[i], query) < 0)
                {
                    insertIndex = i;
                    break;
                }
            }

            if (insertIndex < MaxSymbolResults)
            {
                result.Insert(insertIndex, item);
                if (result.Count > MaxSymbolResults)
                {
                    result.RemoveAt(result.Count - 1);
                }
            }
        }

        private static int CompareSymbolResult(CodeIndexItem left, CodeIndexItem right, string query)
        {
            var score = ScoreSymbol(left, query).CompareTo(ScoreSymbol(right, query));
            if (score != 0)
            {
                return score;
            }

            var file = string.Compare(left.file, right.file, StringComparison.OrdinalIgnoreCase);
            if (file != 0)
            {
                return file;
            }

            return left.line.CompareTo(right.line);
        }

        private static int ScoreSymbol(CodeIndexItem item, string query)
        {
            if (string.Equals(item.name, query, StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            if (!string.IsNullOrEmpty(item.name) && item.name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            if (Contains(item.container, query))
            {
                return 2;
            }

            return 3;
        }

        private static bool Contains(string value, string query)
        {
            return !string.IsNullOrEmpty(value)
                && value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsAsciiIdentifier(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            var first = value[0];
            if (!(first == '_' || first >= 'A' && first <= 'Z' || first >= 'a' && first <= 'z'))
            {
                return false;
            }

            for (var i = 1; i < value.Length; i++)
            {
                var c = value[i];
                if (!(c == '_' || c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' || c >= '0' && c <= '9'))
                {
                    return false;
                }
            }

            return true;
        }

        private static string GetString(Dictionary<string, object> parameters, string key)
        {
            if (parameters == null || !parameters.TryGetValue(key, out var value) || value == null)
            {
                return null;
            }

            return Convert.ToString(value);
        }

        private static int GetInt(Dictionary<string, object> parameters, string key)
        {
            if (parameters == null || !parameters.TryGetValue(key, out var value) || value == null)
            {
                return 0;
            }

            if (value is long longValue)
            {
                return (int)longValue;
            }

            if (value is int intValue)
            {
                return intValue;
            }

            int.TryParse(Convert.ToString(value), out var result);
            return result;
        }

        private static bool GetBool(Dictionary<string, object> parameters, string key)
        {
            if (parameters == null || !parameters.TryGetValue(key, out var value) || value == null)
            {
                return false;
            }

            if (value is bool boolValue)
            {
                return boolValue;
            }

            var text = Convert.ToString(value);
            return string.Equals(text, "true", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(text, "1", StringComparison.OrdinalIgnoreCase);
        }

        private sealed class SnapshotManifest
        {
            public int SchemaVersion;
            public string ProjectRootHash;
            public string UnityVersion;
            public string BuildTarget;
            public string GenerationId;
            public string SnapshotContentHash;
            public bool IncludePackageCacheSourceAssemblies;
            public List<string> IgnoredAssemblyPatterns = new List<string>();
            public List<string> IgnoredSourcePathPatterns = new List<string>();
            public string FilterHash;
            public int ExcludedAssemblyCount;
            public int ExcludedSourceFileCount;
            public List<AssemblySnapshot> Assemblies = new List<AssemblySnapshot>();
        }

        private sealed class AssemblySnapshot
        {
            public string AssemblyName;
            public string AssemblyId;
            public string SnapshotFile;
            public string NameIndexFile;
            public string TokenIndexFile;
            public string OutputPath;
            public string AsmdefPath;
            public string LanguageVersion;
            public bool AllowUnsafe;
            public int SourceFileCount;
            public int ReferenceCount;
            public string DefinesHash;
            public string SourcesHash;
            public string ReferencesHash;
            public string CompilerOptionsHash;
            public string AssemblyHash;
            public long LastWriteTimeTicks;
            public List<string> DependencyAssemblyIds = new List<string>();
            public List<string> ReverseDependencyAssemblyIds = new List<string>();
            public List<string> Defines = new List<string>();
            public List<FileState> Sources = new List<FileState>();
            public List<FileState> References = new List<FileState>();
            public List<string> ProjectReferenceAssemblyNames = new List<string>();
            public List<string> CompilerOptions = new List<string>();
        }

        private sealed class SourceSnapshotInfo
        {
            public AssemblySnapshot Assembly;
            public string FullPath;
            public string RelativePath;
        }

        private sealed class FileState
        {
            public string Path;
            public long Length;
            public long LastWriteTimeTicks;
            public string Hash;
        }
    }
}
