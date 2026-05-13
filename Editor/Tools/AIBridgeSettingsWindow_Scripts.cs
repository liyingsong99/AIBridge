using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using AIBridge.Editor.ScriptExecution;

namespace AIBridge.Editor
{
    public partial class AIBridgeSettingsWindow
    {
        // ==================== 脚本执行页签 ====================
        
        private string _scriptDirectory = AIBridgeProjectSettings.DefaultScriptDirectory;
        private List<string> _scriptFiles = new List<string>();
        private int _selectedScriptIndex = -1;
        private Vector2 _scriptLogScrollPosition;

        private void DrawScriptsTab()
        {
            EditorGUILayout.LabelField("脚本执行管理", EditorStyles.boldLabel);
            EditorGUILayout.Space(5);

            // 脚本目录设置
            DrawScriptDirectorySettings();
            EditorGUILayout.Space(10);

            // 脚本列表
            DrawScriptList();
            EditorGUILayout.Space(10);

            // 执行控制
            DrawExecutionControls();
            EditorGUILayout.Space(10);

            // 执行状态和日志
            DrawExecutionStatus();
        }

        private void DrawScriptDirectorySettings()
        {
            EditorGUILayout.LabelField("脚本目录", EditorStyles.boldLabel);
            
            EditorGUILayout.BeginHorizontal();
            _scriptDirectory = EditorGUILayout.TextField("目录路径", _scriptDirectory);
            
            if (GUILayout.Button("浏览", GUILayout.Width(60)))
            {
                var path = EditorUtility.OpenFolderPanel("选择脚本目录", "Assets", "");
                if (!string.IsNullOrEmpty(path))
                {
                    // 转换为相对路径
                    if (path.StartsWith(Application.dataPath))
                    {
                        _scriptDirectory = "Assets" + path.Substring(Application.dataPath.Length);
                    }
                    else
                    {
                        _scriptDirectory = path;
                    }
                    _scriptDirectoryOption.Value = _scriptDirectory;
                }
            }
            
            if (GUILayout.Button("刷新", GUILayout.Width(60)))
            {
                RefreshScriptList();
            }
            EditorGUILayout.EndHorizontal();

            if (GUILayout.Button("创建默认目录和示例脚本"))
            {
                CreateDefaultScriptDirectory();
            }
        }

        private void DrawScriptList()
        {
            EditorGUILayout.LabelField("可用脚本", EditorStyles.boldLabel);

            if (_scriptFiles.Count == 0)
            {
                EditorGUILayout.HelpBox("未找到脚本文件。请刷新或创建示例脚本。", MessageType.Info);
                return;
            }

            EditorGUILayout.BeginVertical(GUI.skin.box);
            for (int i = 0; i < _scriptFiles.Count; i++)
            {
                var scriptPath = _scriptFiles[i];
                var scriptName = Path.GetFileName(scriptPath);
                
                EditorGUILayout.BeginHorizontal();
                
                // 选择按钮
                var isSelected = _selectedScriptIndex == i;
                if (GUILayout.Toggle(isSelected, "", GUILayout.Width(20)) && !isSelected)
                {
                    _selectedScriptIndex = i;
                }
                
                // 脚本名称
                EditorGUILayout.LabelField(scriptName);
                
                // 执行按钮
                if (GUILayout.Button("执行", GUILayout.Width(60)))
                {
                    ExecuteScript(scriptPath);
                }
                
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawExecutionControls()
        {
            EditorGUILayout.LabelField("执行控制", EditorStyles.boldLabel);
            
            EditorGUILayout.BeginHorizontal();
            
            GUI.enabled = !ScriptExecution.ScriptExecutor.IsExecuting;
            if (GUILayout.Button("执行选中脚本"))
            {
                if (_selectedScriptIndex >= 0 && _selectedScriptIndex < _scriptFiles.Count)
                {
                    ExecuteScript(_scriptFiles[_selectedScriptIndex]);
                }
                else
                {
                    EditorUtility.DisplayDialog("提示", "请先选择一个脚本", "确定");
                }
            }
            GUI.enabled = true;

            GUI.enabled = ScriptExecution.ScriptExecutor.IsExecuting;
            if (GUILayout.Button("暂停"))
            {
                ScriptExecution.ScriptExecutor.Pause();
            }
            
            if (GUILayout.Button("恢复"))
            {
                ScriptExecution.ScriptExecutor.Resume();
            }
            
            if (GUILayout.Button("停止"))
            {
                ScriptExecution.ScriptExecutor.Stop();
            }
            GUI.enabled = true;
            
            EditorGUILayout.EndHorizontal();
        }

        private void DrawExecutionStatus()
        {
            EditorGUILayout.LabelField("执行状态", EditorStyles.boldLabel);
            
            var state = ScriptExecution.ScriptExecutor.CurrentState;
            
            EditorGUILayout.BeginVertical(GUI.skin.box);
            
            EditorGUILayout.LabelField("状态", state.Status.ToString());
            
            if (!string.IsNullOrEmpty(state.ScriptPath))
            {
                EditorGUILayout.LabelField("当前脚本", Path.GetFileName(state.ScriptPath));
                EditorGUILayout.LabelField("执行进度", $"{state.CurrentLine + 1} 行");
            }
            
            if (!string.IsNullOrEmpty(state.StartTime))
            {
                EditorGUILayout.LabelField("开始时间", state.StartTime);
            }
            
            if (!string.IsNullOrEmpty(state.ErrorMessage))
            {
                EditorGUILayout.HelpBox($"错误: {state.ErrorMessage}", MessageType.Error);
            }
            
            EditorGUILayout.EndVertical();
            
            // 日志输出
            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField("执行日志", EditorStyles.boldLabel);
            
            _scriptLogScrollPosition = EditorGUILayout.BeginScrollView(_scriptLogScrollPosition, GUI.skin.box, GUILayout.Height(150));
            
            if (state.Logs != null && state.Logs.Count > 0)
            {
                foreach (var log in state.Logs)
                {
                    EditorGUILayout.LabelField(log, EditorStyles.wordWrappedLabel);
                }
            }
            else
            {
                EditorGUILayout.LabelField("暂无日志", EditorStyles.centeredGreyMiniLabel);
            }
            
            EditorGUILayout.EndScrollView();
        }

        private void RefreshScriptList()
        {
            _scriptFiles.Clear();
            
            // 加载保存的目录
            if (_scriptDirectoryOption == null)
            {
                _scriptDirectoryOption = new EditorOption<string>("AIBridge_ScriptDirectory", AIBridgeProjectSettings.DefaultScriptDirectory, ReadScriptDirectory, WriteScriptDirectory);
            }
            _scriptDirectory = _scriptDirectoryOption.Value;
            
            if (!Directory.Exists(_scriptDirectory))
            {
                return;
            }
            
            var files = Directory.GetFiles(_scriptDirectory, "*.txt", SearchOption.AllDirectories);
            _scriptFiles.AddRange(files);
            
            Debug.Log($"[AIBridge] 找到 {_scriptFiles.Count} 个脚本文件");
        }

        private void ExecuteScript(string scriptPath)
        {
            // 验证脚本
            var error = ScriptExecution.ScriptParser.Validate(scriptPath);
            if (!string.IsNullOrEmpty(error))
            {
                EditorUtility.DisplayDialog("脚本验证失败", error, "确定");
                return;
            }
            
            // 执行脚本
            ScriptExecution.ScriptExecutor.Execute(scriptPath);
            Debug.Log($"[AIBridge] 开始执行脚本: {scriptPath}");
        }

        private void CreateDefaultScriptDirectory()
        {
            var directory = "Assets/AIBridgeScripts";
            
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                Debug.Log($"[AIBridge] 创建脚本目录: {directory}");
            }
            
            // 创建示例脚本
            var exampleScriptPath = Path.Combine(directory, "example.txt");
            if (!File.Exists(exampleScriptPath))
            {
                var exampleContent = @"# AIBridge 脚本示例
# 这是一个简单的示例脚本，演示基本命令用法

# 输出日志
log ""开始执行示例脚本""

# 延迟 1 秒
delay 1000

# 调用 AIBridge CLI 命令（示例：获取场景层级）
# call scene get_hierarchy --depth 2

# 调用 AIBridge CLI 命令（示例：编译 Unity）
# call compile unity

# 执行编辑器菜单项（示例：刷新资源）
# menu Assets/Refresh

log ""示例脚本执行完成""
";
                File.WriteAllText(exampleScriptPath, exampleContent);
                Debug.Log($"[AIBridge] 创建示例脚本: {exampleScriptPath}");
            }
            
            _scriptDirectory = directory;
            _scriptDirectoryOption.Value = _scriptDirectory;
            
            RefreshScriptList();
            AssetDatabase.Refresh();
            
            EditorUtility.DisplayDialog("成功", $"已创建脚本目录和示例脚本:\n{directory}", "确定");
        }

        private static string ReadScriptDirectory(string key, string defaultValue)
        {
            EnsureScriptDirectoryMigrated(key, defaultValue);
            return AIBridgeProjectSettings.Instance.ScriptDirectory;
        }

        private static void EnsureScriptDirectoryMigrated(string key, string defaultValue)
        {
            var settings = AIBridgeProjectSettings.Instance;
            if (settings.LegacyScriptDirectoryMigrated)
            {
                return;
            }

            // 脚本目录此前存于 EditorPrefs，这里在首次读取时迁移到项目级配置。
            if (EditorPrefs.HasKey(key))
            {
                settings.ScriptDirectory = EditorPrefs.GetString(key, defaultValue);
                EditorPrefs.DeleteKey(key);
            }

            settings.LegacyScriptDirectoryMigrated = true;
            settings.SaveSettings();
        }

        private static void WriteScriptDirectory(string key, string value)
        {
            var settings = AIBridgeProjectSettings.Instance;
            var newValue = string.IsNullOrEmpty(value) ? AIBridgeProjectSettings.DefaultScriptDirectory : value;
            if (settings.ScriptDirectory == newValue)
            {
                return;
            }

            settings.ScriptDirectory = newValue;
            settings.SaveSettings();
        }
    }
}
