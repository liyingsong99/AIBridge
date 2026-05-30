using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;

namespace AIBridge.Editor
{
    public sealed class AIBridgeWorkflowsWindow : EditorWindow
    {
        private static readonly string[] ExportTargets =
        {
            "codex-task-pack",
            "generic-cli",
            "claude-workflow"
        };

        private Vector2 _scrollPosition;
        private int _tabIndex;
        private int _selectedRecipeIndex;
        private int _selectedRunIndex;
        private int _selectedExportTargetIndex;
        private readonly List<WorkflowRecipeView> _recipes = new List<WorkflowRecipeView>();
        private readonly List<WorkflowRunView> _runs = new List<WorkflowRunView>();
        private string _lastCliOutput;

        [MenuItem("Tools/AIBridge/Workflows")]
        public static void OpenWindow()
        {
            var window = GetWindow<AIBridgeWorkflowsWindow>();
            window.titleContent = new GUIContent(AIBridgeEditorText.T("AIBridge Workflows", "AIBridge Workflows"));
            window.minSize = new Vector2(760, 460);
            window.Show();
        }

        [MenuItem("AIBridge/Workflows")]
        private static void OpenLegacyMenuWindow()
        {
            OpenWindow();
        }

        private void OnEnable()
        {
            RefreshAll();
        }

        private void OnGUI()
        {
            DrawToolbar();
            _tabIndex = GUILayout.Toolbar(_tabIndex, GetTabNames());
            EditorGUILayout.Space(6);

            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);
            switch (_tabIndex)
            {
                case 0:
                    DrawOverview();
                    break;
                case 1:
                    DrawRecipes();
                    break;
                case 2:
                    DrawRuns();
                    break;
                case 3:
                    DrawArtifacts();
                    break;
                case 4:
                    DrawExports();
                    break;
                case 5:
                    DrawSkills();
                    break;
                case 6:
                    DrawSkillLibrary();
                    break;
                case 7:
                    DrawCleanup();
                    break;
            }

            DrawCliOutput();
            EditorGUILayout.EndScrollView();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            if (GUILayout.Button(AIBridgeEditorText.T("Refresh", "刷新"), EditorStyles.toolbarButton, GUILayout.Width(76)))
            {
                RefreshAll();
            }

            if (GUILayout.Button(AIBridgeEditorText.T("Open Workflow Dir", "打开 Workflow 目录"), EditorStyles.toolbarButton, GUILayout.Width(138)))
            {
                Directory.CreateDirectory(GetWorkflowRootDirectory());
                EditorUtility.RevealInFinder(GetWorkflowRootDirectory());
            }

            if (GUILayout.Button(AIBridgeEditorText.T("Copy CLI Root", "复制 CLI 根命令"), EditorStyles.toolbarButton, GUILayout.Width(122)))
            {
                CopyCli("workflow list");
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField(
                AIBridgeEditorText.T("Recipes: ", "Recipe：") + _recipes.Count + "  " + AIBridgeEditorText.T("Runs: ", "Run：") + _runs.Count,
                EditorStyles.miniLabel,
                GUILayout.Width(150));
            EditorGUILayout.EndHorizontal();
        }

        private static string[] GetTabNames()
        {
            return new[]
            {
                AIBridgeEditorText.T("Overview", "概览"),
                AIBridgeEditorText.T("Recipes", "Recipes"),
                AIBridgeEditorText.T("Runs", "Runs"),
                AIBridgeEditorText.T("Artifacts", "Artifacts"),
                AIBridgeEditorText.T("Exports", "导出"),
                AIBridgeEditorText.T("Skills", "Skills"),
                AIBridgeEditorText.T("Library", "推荐库"),
                AIBridgeEditorText.T("Cleanup", "清理")
            };
        }

        private void DrawOverview()
        {
            EditorGUILayout.LabelField(AIBridgeEditorText.T("Workflow Overview", "Workflow 概览"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                AIBridgeEditorText.T(
                    "Use this panel to inspect workflow recipes, active runs, artifacts, exports, and AI Skill installation entry points.",
                    "使用此面板查看 Workflow recipes、active runs、artifacts、导出以及 AI Skill 安装入口。"),
                MessageType.Info);

            DrawInfoLine(AIBridgeEditorText.T("Built-in Recipes", "内置 Recipes"), _recipes.Count.ToString());
            DrawInfoLine(AIBridgeEditorText.T("Run Directory", "Run 目录"), GetRunsDirectory());
            DrawInfoLine(AIBridgeEditorText.T("Active Run", "Active Run"), ReadActiveRunSummary());

            EditorGUILayout.Space(8);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(AIBridgeEditorText.T("Begin Selected Recipe", "开始选中 Recipe"), GUILayout.Height(28)))
            {
                var recipe = GetSelectedRecipe();
                if (recipe != null)
                {
                    RunCli("workflow begin --file " + Quote(recipe.Path));
                    RefreshAll();
                }
            }

            if (GUILayout.Button(AIBridgeEditorText.T("Finish Active Run", "结束 Active Run"), GUILayout.Height(28)))
            {
                RunCli("workflow finish --status passed");
                RefreshAll();
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawRecipes()
        {
            EditorGUILayout.LabelField(AIBridgeEditorText.T("Workflow Recipes", "Workflow Recipes"), EditorStyles.boldLabel);
            if (_recipes.Count == 0)
            {
                EditorGUILayout.HelpBox(AIBridgeEditorText.T("No workflow recipes found.", "未找到 workflow recipes。"), MessageType.Warning);
                return;
            }

            var names = _recipes.ConvertAll(recipe => recipe.Name).ToArray();
            _selectedRecipeIndex = Mathf.Clamp(_selectedRecipeIndex, 0, names.Length - 1);
            _selectedRecipeIndex = EditorGUILayout.Popup(AIBridgeEditorText.T("Recipe", "Recipe"), _selectedRecipeIndex, names);
            var selected = GetSelectedRecipe();
            if (selected == null)
            {
                return;
            }

            EditorGUILayout.LabelField(selected.Title, EditorStyles.boldLabel);
            EditorGUILayout.LabelField(selected.Description, EditorStyles.wordWrappedMiniLabel);
            DrawInfoLine(AIBridgeEditorText.T("Path", "路径"), selected.Path);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(AIBridgeEditorText.T("Validate", "校验"), GUILayout.Height(26)))
            {
                RunCli("workflow validate --file " + Quote(selected.Path));
            }

            if (GUILayout.Button(AIBridgeEditorText.T("Plan", "生成 Plan"), GUILayout.Height(26)))
            {
                RunCli("workflow plan --file " + Quote(selected.Path) + " --format markdown");
            }

            if (GUILayout.Button(AIBridgeEditorText.T("Copy Begin CLI", "复制 Begin 命令"), GUILayout.Height(26)))
            {
                CopyCli("workflow begin --file " + Quote(selected.Path));
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawRuns()
        {
            EditorGUILayout.LabelField(AIBridgeEditorText.T("Workflow Runs", "Workflow Runs"), EditorStyles.boldLabel);
            RefreshRunsIfNeeded();
            if (_runs.Count == 0)
            {
                EditorGUILayout.HelpBox(AIBridgeEditorText.T("No workflow runs found.", "未找到 workflow runs。"), MessageType.Info);
                return;
            }

            var names = _runs.ConvertAll(run => run.RunId + "  [" + run.Status + "]").ToArray();
            _selectedRunIndex = Mathf.Clamp(_selectedRunIndex, 0, names.Length - 1);
            _selectedRunIndex = EditorGUILayout.Popup(AIBridgeEditorText.T("Run", "Run"), _selectedRunIndex, names);
            var selected = GetSelectedRun();
            if (selected == null)
            {
                return;
            }

            DrawInfoLine("RunId", selected.RunId);
            DrawInfoLine(AIBridgeEditorText.T("Recipe", "Recipe"), selected.RecipeName);
            DrawInfoLine(AIBridgeEditorText.T("Status", "状态"), selected.Status);
            DrawInfoLine(AIBridgeEditorText.T("Artifacts", "Artifacts"), selected.ArtifactCount.ToString());
            DrawInfoLine(AIBridgeEditorText.T("Report", "Report"), selected.ReportPath);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(AIBridgeEditorText.T("Open Report", "打开 Report"), GUILayout.Height(26)))
            {
                OpenPath(selected.ReportPath);
            }

            if (GUILayout.Button(AIBridgeEditorText.T("Attach Active", "设为 Active"), GUILayout.Height(26)))
            {
                RunCli("workflow attach --run " + selected.RunId);
                RefreshAll();
            }

            if (GUILayout.Button(AIBridgeEditorText.T("Copy Status CLI", "复制状态命令"), GUILayout.Height(26)))
            {
                CopyCli("workflow status --run " + selected.RunId);
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawArtifacts()
        {
            EditorGUILayout.LabelField(AIBridgeEditorText.T("Run Artifacts", "Run Artifacts"), EditorStyles.boldLabel);
            var run = GetSelectedRun();
            if (run == null)
            {
                EditorGUILayout.HelpBox(AIBridgeEditorText.T("Select a run on the Runs tab first.", "请先在 Runs 页签选择一个 run。"), MessageType.Info);
                return;
            }

            var artifactsDirectory = Path.Combine(run.Directory, "artifacts");
            if (!Directory.Exists(artifactsDirectory))
            {
                EditorGUILayout.HelpBox(AIBridgeEditorText.T("No artifacts directory found.", "未找到 artifacts 目录。"), MessageType.Info);
                return;
            }

            foreach (var artifactDirectory in Directory.GetDirectories(artifactsDirectory))
            {
                EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
                EditorGUILayout.LabelField(Path.GetFileName(artifactDirectory), EditorStyles.miniLabel);
                if (GUILayout.Button(AIBridgeEditorText.T("Open", "打开"), GUILayout.Width(64)))
                {
                    EditorUtility.RevealInFinder(artifactDirectory);
                }
                EditorGUILayout.EndHorizontal();
            }
        }

        private void DrawExports()
        {
            EditorGUILayout.LabelField(AIBridgeEditorText.T("Workflow Export", "Workflow 导出"), EditorStyles.boldLabel);
            var recipe = GetSelectedRecipe();
            if (recipe == null)
            {
                EditorGUILayout.HelpBox(AIBridgeEditorText.T("No recipe selected.", "未选择 recipe。"), MessageType.Info);
                return;
            }

            _selectedExportTargetIndex = EditorGUILayout.Popup(
                AIBridgeEditorText.T("Target", "目标"),
                Mathf.Clamp(_selectedExportTargetIndex, 0, ExportTargets.Length - 1),
                ExportTargets);
            var target = ExportTargets[_selectedExportTargetIndex];
            DrawInfoLine(AIBridgeEditorText.T("Recipe", "Recipe"), recipe.Name);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(AIBridgeEditorText.T("Export", "导出"), GUILayout.Height(28)))
            {
                RunCli("workflow export --file " + Quote(recipe.Path) + " --target " + target + " --output " + Quote(Path.Combine(GetWorkflowRootDirectory(), "exports")));
            }

            if (GUILayout.Button(AIBridgeEditorText.T("Copy Export CLI", "复制导出命令"), GUILayout.Height(28)))
            {
                CopyCli("workflow export --file " + Quote(recipe.Path) + " --target " + target + " --output " + Quote(Path.Combine(GetWorkflowRootDirectory(), "exports")));
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawSkills()
        {
            EditorGUILayout.LabelField(AIBridgeEditorText.T("AIBridge Skills", "AIBridge Skills"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                AIBridgeEditorText.T(
                    "Workflow-related Skill installation lives here. Existing selected assistant integrations are reused.",
                    "Workflow 相关 Skill 安装入口集中在这里，并复用现有已选择的 AI 工具集成。"),
                MessageType.Info);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(AIBridgeEditorText.T("Install Selected Integrations", "安装选中集成"), GUILayout.Height(28)))
            {
                SkillInstaller.ManualInstall();
            }

            if (GUILayout.Button(AIBridgeEditorText.T("Open Settings Skills", "打开 Settings Skills"), GUILayout.Height(28)))
            {
                EditorApplication.ExecuteMenuItem("AIBridge/Settings");
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawSkillLibrary()
        {
            EditorGUILayout.LabelField(AIBridgeEditorText.T("Recommended Skill Library", "推荐 Skill 库"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                AIBridgeEditorText.T(
                    "Recommended Skill repository operations are available from this workflow surface and reuse the existing installer.",
                    "推荐 Skill 仓库操作从 Workflow 面板提供入口，并复用现有安装器。"),
                MessageType.Info);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(AIBridgeEditorText.T("Open Install Root", "打开安装目录"), GUILayout.Height(28)))
            {
                var root = Path.Combine(GetProjectRoot(), RecommendedSkillInstaller.GetPrimaryInstallRootDirectory(GetProjectRoot()));
                Directory.CreateDirectory(root);
                EditorUtility.RevealInFinder(root);
            }

            if (GUILayout.Button(AIBridgeEditorText.T("Open Settings Library", "打开 Settings 推荐库"), GUILayout.Height(28)))
            {
                EditorApplication.ExecuteMenuItem("AIBridge/Settings");
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawCleanup()
        {
            EditorGUILayout.LabelField(AIBridgeEditorText.T("Workflow Cleanup", "Workflow 清理"), EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(AIBridgeEditorText.T("Dry Run Clean", "清理预览"), GUILayout.Height(28)))
            {
                RunCli("workflow clean --older-than 30d --dry-run true --keep-failed true --keep-latest 20");
            }

            if (GUILayout.Button(AIBridgeEditorText.T("Copy Clean CLI", "复制清理命令"), GUILayout.Height(28)))
            {
                CopyCli("workflow clean --older-than 30d --dry-run false --keep-failed true --keep-latest 20");
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawCliOutput()
        {
            if (string.IsNullOrWhiteSpace(_lastCliOutput))
            {
                return;
            }

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField(AIBridgeEditorText.T("Last CLI Output", "最近 CLI 输出"), EditorStyles.boldLabel);
            EditorGUILayout.TextArea(_lastCliOutput, GUILayout.MinHeight(90));
        }

        private void RefreshAll()
        {
            RefreshRecipes();
            RefreshRuns();
            Repaint();
        }

        private void RefreshRecipes()
        {
            _recipes.Clear();
            AddRecipesFromDirectory(GetBuiltInRecipesDirectory(), "builtin");
            AddRecipesFromDirectory(Path.Combine(GetWorkflowRootDirectory(), "recipes"), "project");
        }

        private void RefreshRunsIfNeeded()
        {
            if (_runs.Count == 0)
            {
                RefreshRuns();
            }
        }

        private void RefreshRuns()
        {
            _runs.Clear();
            var runsDirectory = GetRunsDirectory();
            if (!Directory.Exists(runsDirectory))
            {
                return;
            }

            foreach (var directory in Directory.GetDirectories(runsDirectory))
            {
                var manifestPath = Path.Combine(directory, "manifest.json");
                if (!File.Exists(manifestPath))
                {
                    continue;
                }

                var manifest = ReadJson<WorkflowManifestView>(manifestPath);
                if (manifest == null)
                {
                    continue;
                }

                _runs.Add(new WorkflowRunView
                {
                    RunId = manifest.runId,
                    RecipeName = manifest.recipeName,
                    Status = manifest.status,
                    ArtifactCount = manifest.artifactRefs == null ? 0 : manifest.artifactRefs.Length,
                    Directory = directory,
                    ReportPath = Path.Combine(directory, "report.md")
                });
            }

            _runs.Sort((left, right) => string.Compare(right.RunId, left.RunId, StringComparison.OrdinalIgnoreCase));
        }

        private void AddRecipesFromDirectory(string directory, string source)
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            foreach (var file in Directory.GetFiles(directory, "*.aibridge-workflow.json"))
            {
                var recipe = ReadJson<WorkflowRecipeView>(file);
                if (recipe == null || string.IsNullOrEmpty(recipe.Name))
                {
                    continue;
                }

                recipe.Path = file;
                recipe.Source = source;
                _recipes.Add(recipe);
            }
        }

        private void RunCli(string commandBody)
        {
            var cliPath = ResolveCliPath();
            if (string.IsNullOrEmpty(cliPath))
            {
                _lastCliOutput = AIBridgeEditorText.T("AIBridgeCLI was not found.", "未找到 AIBridgeCLI。");
                return;
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = cliPath,
                    Arguments = commandBody,
                    WorkingDirectory = GetProjectRoot(),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (var process = new Process())
                using (var stdoutDone = new ManualResetEvent(false))
                using (var stderrDone = new ManualResetEvent(false))
                {
                    var stdout = new StringBuilder();
                    var stderr = new StringBuilder();
                    process.StartInfo = startInfo;
                    process.OutputDataReceived += (sender, args) =>
                    {
                        if (args.Data == null)
                        {
                            stdoutDone.Set();
                            return;
                        }

                        stdout.AppendLine(args.Data);
                    };
                    process.ErrorDataReceived += (sender, args) =>
                    {
                        if (args.Data == null)
                        {
                            stderrDone.Set();
                            return;
                        }

                        stderr.AppendLine(args.Data);
                    };

                    if (!process.Start())
                    {
                        _lastCliOutput = AIBridgeEditorText.T("Failed to start AIBridgeCLI.", "启动 AIBridgeCLI 失败。");
                        return;
                    }

                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    if (!process.WaitForExit(30000))
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch
                        {
                            // 忽略清理失败，保留超时信息给面板显示。
                        }

                        _lastCliOutput = AIBridgeEditorText.T(
                            "AIBridgeCLI timed out after 30000ms.",
                            "AIBridgeCLI 执行超过 30000ms，已超时。");
                        return;
                    }

                    stdoutDone.WaitOne(1000);
                    stderrDone.WaitOne(1000);
                    var stdoutText = stdout.ToString();
                    var stderrText = stderr.ToString();
                    _lastCliOutput = stdoutText + (string.IsNullOrWhiteSpace(stderrText) ? string.Empty : "\n" + stderrText);
                }
            }
            catch (Exception ex)
            {
                _lastCliOutput = ex.Message;
            }
        }

        private void CopyCli(string commandBody)
        {
            EditorGUIUtility.systemCopyBuffer = "$CLI " + commandBody;
            UnityEngine.Debug.Log(AIBridgeEditorText.T("[AIBridge] Workflow CLI command copied.", "[AIBridge] Workflow CLI 命令已复制。"));
        }

        private WorkflowRecipeView GetSelectedRecipe()
        {
            return _recipes.Count == 0 ? null : _recipes[Mathf.Clamp(_selectedRecipeIndex, 0, _recipes.Count - 1)];
        }

        private WorkflowRunView GetSelectedRun()
        {
            return _runs.Count == 0 ? null : _runs[Mathf.Clamp(_selectedRunIndex, 0, _runs.Count - 1)];
        }

        private static void DrawInfoLine(string label, string value)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, EditorStyles.miniBoldLabel, GUILayout.Width(110));
            EditorGUILayout.SelectableLabel(string.IsNullOrEmpty(value) ? "-" : value, EditorStyles.wordWrappedMiniLabel, GUILayout.Height(18));
            EditorGUILayout.EndHorizontal();
        }

        private static void OpenPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            if (File.Exists(path))
            {
                EditorUtility.OpenWithDefaultApp(path);
            }
            else if (Directory.Exists(path))
            {
                EditorUtility.RevealInFinder(path);
            }
        }

        private static string ReadActiveRunSummary()
        {
            var path = Path.Combine(GetWorkflowRootDirectory(), "active-run.json");
            if (!File.Exists(path))
            {
                return AIBridgeEditorText.T("None", "无");
            }

            var pointer = ReadJson<ActiveRunView>(path);
            return pointer == null || string.IsNullOrEmpty(pointer.runId)
                ? AIBridgeEditorText.T("Invalid active-run.json", "active-run.json 无效")
                : pointer.runId + " / " + pointer.recipeName;
        }

        private static T ReadJson<T>(string path) where T : class
        {
            try
            {
                return JsonUtility.FromJson<T>(File.ReadAllText(path));
            }
            catch
            {
                return null;
            }
        }

        private static string GetProjectRoot()
        {
            return Path.GetDirectoryName(Application.dataPath);
        }

        private static string GetWorkflowRootDirectory()
        {
            return Path.Combine(GetProjectRoot(), ".aibridge", "workflows");
        }

        private static string GetRunsDirectory()
        {
            return Path.Combine(GetWorkflowRootDirectory(), "runs");
        }

        private static string GetBuiltInRecipesDirectory()
        {
            const string packageName = "cn.lys.aibridge";
            var embedded = Path.Combine(GetProjectRoot(), "Packages", packageName, "Templates~", "Workflows");
            if (Directory.Exists(embedded))
            {
                return embedded;
            }

            var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/" + packageName);
            return packageInfo == null
                ? embedded
                : Path.Combine(packageInfo.resolvedPath, "Templates~", "Workflows");
        }

        private static string ResolveCliPath()
        {
            var cli = AIBridgeCodeIndexEditorUtility.ResolveCliPath();
            return string.IsNullOrEmpty(cli) ? null : cli;
        }

        private static string Quote(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "\"\"";
            }

            return value.IndexOfAny(new[] { ' ', '\t', '"' }) < 0
                ? value
                : "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        [Serializable]
        private sealed class WorkflowRecipeView
        {
            public string name;
            public string title;
            public string description;
            public string Path { get; set; }
            public string Source { get; set; }

            public string Name
            {
                get { return string.IsNullOrEmpty(name) ? Path : name; }
            }

            public string Title
            {
                get { return string.IsNullOrEmpty(title) ? Name : title; }
            }

            public string Description
            {
                get { return description ?? string.Empty; }
            }
        }

        [Serializable]
        private sealed class WorkflowManifestView
        {
            public string runId;
            public string recipeName;
            public string status;
            public ArtifactView[] artifactRefs;
        }

        [Serializable]
        private sealed class ArtifactView
        {
            public string artifactId;
        }

        [Serializable]
        private sealed class ActiveRunView
        {
            public string runId;
            public string recipeName;
        }

        private sealed class WorkflowRunView
        {
            public string RunId { get; set; }
            public string RecipeName { get; set; }
            public string Status { get; set; }
            public int ArtifactCount { get; set; }
            public string Directory { get; set; }
            public string ReportPath { get; set; }
        }
    }
}
