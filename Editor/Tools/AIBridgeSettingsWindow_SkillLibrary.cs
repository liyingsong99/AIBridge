using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace AIBridge.Editor
{
    public partial class AIBridgeSettingsWindow
    {
        private void DrawRecommendedSkillLibraryTab()
        {
            var repositories = RecommendedSkillRepositories.GetDefaultRepositories();
            EditorGUILayout.LabelField(AIBridgeEditorText.T("Recommended Skill Library", "推荐 Skill 库"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                AIBridgeEditorText.T(
                    "Install third-party skills into the project-root skills directory. Review third-party skill content before use.",
                    "将第三方 Skill 安装到项目根目录 skills。使用前请自行确认第三方 Skill 内容。"),
                MessageType.Info);

            EditorGUILayout.LabelField(
                AIBridgeEditorText.T("Install Root: ", "安装根目录：") + AIBridgeProjectSettings.Instance.SkillRootDirectory,
                EditorStyles.miniLabel);

            var repositoryNames = repositories.Select(repository => repository.DisplayName).ToArray();
            _selectedRecommendedRepositoryIndex = Mathf.Clamp(_selectedRecommendedRepositoryIndex, 0, repositoryNames.Length - 1);
            _selectedRecommendedRepositoryIndex = EditorGUILayout.Popup(
                AIBridgeEditorText.T("Repository", "仓库"),
                _selectedRecommendedRepositoryIndex,
                repositoryNames);

            var repository = repositories[_selectedRecommendedRepositoryIndex];
            EditorGUILayout.LabelField(repository.RepositoryUrl + "#" + repository.BranchOrTag, EditorStyles.miniLabel);
            EditorGUILayout.LabelField(repository.Description, EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.Space(5);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(AIBridgeEditorText.T("Refresh Skill List", "刷新 Skill 列表"), GUILayout.Height(28)))
            {
                RefreshRecommendedSkillList(repository);
            }

            if (GUILayout.Button(AIBridgeEditorText.T("Open Install Root", "打开安装目录"), GUILayout.Height(28)))
            {
                OpenSharedSkillRootDirectory();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(5);
            if (_recommendedSkills == null || _recommendedSkills.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    AIBridgeEditorText.T(
                        "Click Refresh Skill List to clone the repository and scan available skills.",
                        "点击“刷新 Skill 列表”拉取仓库并扫描可用 Skill。"),
                    MessageType.None);
                return;
            }

            foreach (var skill in _recommendedSkills)
            {
                DrawRecommendedSkillItem(repository, skill);
            }
        }

        private void DrawRecommendedSkillItem(RecommendedSkillRepository repository, RecommendedSkillInfo skill)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(skill.DisplayName, EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField(GetInstallStateText(skill.InstallState), EditorStyles.miniLabel, GUILayout.Width(92));
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(skill.Description))
            {
                EditorGUILayout.LabelField(skill.Description, EditorStyles.wordWrappedMiniLabel);
            }

            EditorGUILayout.LabelField(skill.SourceRelativePath, EditorStyles.miniLabel);

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            var buttonText = skill.InstallState == RecommendedSkillInstallState.NotInstalled
                ? AIBridgeEditorText.T("Install", "安装")
                : AIBridgeEditorText.T("Update", "更新");
            if (GUILayout.Button(buttonText, GUILayout.Width(96)))
            {
                InstallRecommendedSkill(repository, skill);
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        private void RefreshRecommendedSkillList(RecommendedSkillRepository repository)
        {
            try
            {
                _recommendedSkills = RecommendedSkillInstaller.RefreshRepository(GetProjectRoot(), repository);
                Repaint();
            }
            catch (System.Exception ex)
            {
                EditorUtility.DisplayDialog(
                    AIBridgeEditorText.T("Refresh Failed", "刷新失败"),
                    ex.Message,
                    AIBridgeEditorText.T("OK", "确定"));
            }
        }

        private void InstallRecommendedSkill(RecommendedSkillRepository repository, RecommendedSkillInfo skill)
        {
            var targetDirectory = Path.Combine(GetProjectRoot(), AIBridgeProjectSettings.Instance.SkillRootDirectory, skill.Name);
            var overwrite = true;
            if (Directory.Exists(targetDirectory))
            {
                overwrite = EditorUtility.DisplayDialog(
                    AIBridgeEditorText.T("Confirm Install", "确认安装"),
                    AIBridgeEditorText.T(
                        "Target skill already exists. Overwrite it?\n\n" + targetDirectory,
                        "目标 Skill 已存在，是否覆盖？\n\n" + targetDirectory),
                    AIBridgeEditorText.T("Overwrite", "覆盖"),
                    AIBridgeEditorText.T("Cancel", "取消"));
            }

            if (!overwrite)
            {
                return;
            }

            var result = RecommendedSkillInstaller.Install(GetProjectRoot(), repository, skill, true);
            if (result.Success)
            {
                RefreshRecommendedSkillList(repository);
            }

            EditorUtility.DisplayDialog(
                result.Success
                    ? AIBridgeEditorText.T("Install Complete", "安装成功")
                    : AIBridgeEditorText.T("Install Failed", "安装失败"),
                result.Success
                    ? result.Message + "\n" + result.InstalledDirectory
                    : result.Message,
                AIBridgeEditorText.T("OK", "确定"));
        }

        private void OpenSharedSkillRootDirectory()
        {
            var directory = Path.Combine(GetProjectRoot(), AIBridgeProjectSettings.Instance.SkillRootDirectory);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            EditorUtility.RevealInFinder(directory);
        }

        private static string GetInstallStateText(RecommendedSkillInstallState state)
        {
            switch (state)
            {
                case RecommendedSkillInstallState.Installed:
                    return AIBridgeEditorText.T("Installed", "已安装");
                case RecommendedSkillInstallState.UpdateAvailable:
                    return AIBridgeEditorText.T("Update", "可更新");
                default:
                    return AIBridgeEditorText.T("Not installed", "未安装");
            }
        }
    }
}
