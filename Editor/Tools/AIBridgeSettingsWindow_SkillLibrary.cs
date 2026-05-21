using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace AIBridge.Editor
{
    public partial class AIBridgeSettingsWindow
    {
        private const string GitRepositorySuffix = ".git";

        private void DrawRecommendedSkillLibraryTab()
        {
            var repositories = RecommendedSkillRepositories.GetDefaultRepositories();
            EditorGUILayout.LabelField(AIBridgeEditorText.T("Recommended Skill Library", "推荐 Skill 库"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                AIBridgeEditorText.T(
                    "Install third-party skills into the shared project Skill directory. Review third-party skill content before use.",
                    "将第三方 Skill 安装到项目共享 Skill 目录。使用前请自行确认第三方 Skill 内容。"),
                MessageType.Info);

            EditorGUILayout.LabelField(
                AIBridgeEditorText.T("Install Root: ", "安装根目录：") + AIBridgeProjectSettings.Instance.SkillRootDirectory,
                EditorStyles.miniLabel);

            var repositoryNames = repositories.Select(item => item.DisplayName).ToArray();
            _selectedRecommendedRepositoryIndex = Mathf.Clamp(_selectedRecommendedRepositoryIndex, 0, repositoryNames.Length - 1);
            _selectedRecommendedRepositoryIndex = EditorGUILayout.Popup(
                AIBridgeEditorText.T("Repository", "仓库"),
                _selectedRecommendedRepositoryIndex,
                repositoryNames);

            var selectedRepository = repositories[_selectedRecommendedRepositoryIndex];
            EditorGUILayout.LabelField(selectedRepository.RepositoryUrl + "#" + selectedRepository.BranchOrTag, EditorStyles.miniLabel);
            EditorGUILayout.LabelField(selectedRepository.Description, EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.Space(5);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(AIBridgeEditorText.T("Refresh Skill List", "刷新 Skill 列表"), GUILayout.Height(28)))
            {
                RefreshRecommendedSkillList(selectedRepository);
            }

            if (GUILayout.Button(AIBridgeEditorText.T("Open Repository", "前往仓库"), GUILayout.Height(28)))
            {
                OpenRepositoryWebPage(selectedRepository);
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
                DrawRecommendedSkillItem(selectedRepository, skill);
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

            if (skill.InstallState != RecommendedSkillInstallState.NotInstalled
                && GUILayout.Button(AIBridgeEditorText.T("Remove", "移除"), GUILayout.Width(96)))
            {
                RemoveRecommendedSkill(repository, skill);
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

        private void RemoveRecommendedSkill(RecommendedSkillRepository repository, RecommendedSkillInfo skill)
        {
            var targetDirectory = Path.Combine(GetProjectRoot(), AIBridgeProjectSettings.Instance.SkillRootDirectory, skill.Name);
            if (!EditorUtility.DisplayDialog(
                AIBridgeEditorText.T("Confirm Remove", "确认移除"),
                AIBridgeEditorText.T(
                    "Remove this installed Skill?\n\n" + targetDirectory,
                    "是否移除这个已安装 Skill？\n\n" + targetDirectory),
                AIBridgeEditorText.T("Remove", "移除"),
                AIBridgeEditorText.T("Cancel", "取消")))
            {
                return;
            }

            var result = RecommendedSkillInstaller.Remove(GetProjectRoot(), skill);
            if (result.Success)
            {
                RefreshRecommendedSkillList(repository);
            }

            EditorUtility.DisplayDialog(
                result.Success
                    ? AIBridgeEditorText.T("Remove Complete", "移除成功")
                    : AIBridgeEditorText.T("Remove Failed", "移除失败"),
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

        private static void OpenRepositoryWebPage(RecommendedSkillRepository repository)
        {
            if (repository == null || string.IsNullOrEmpty(repository.RepositoryUrl))
            {
                return;
            }

            Application.OpenURL(GetRepositoryWebUrl(repository.RepositoryUrl));
        }

        internal static string GetRepositoryWebUrl(string repositoryUrl)
        {
            return repositoryUrl.EndsWith(GitRepositorySuffix, System.StringComparison.OrdinalIgnoreCase)
                ? repositoryUrl.Substring(0, repositoryUrl.Length - GitRepositorySuffix.Length)
                : repositoryUrl;
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
