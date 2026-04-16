using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AIBridge.Editor
{
    /// <summary>
    /// Scene operations: load, save, get hierarchy
    /// Supports multiple sub-commands via "action" parameter
    /// </summary>
    public class SceneCommand : ICommand
    {
        public string Type => "scene";
        public bool RequiresRefresh => true;

        public string SkillDescription => @"### `scene` - Scene Operations

```bash
$CLI scene load --scenePath ""Assets/Scenes/Main.unity"" [--mode additive]
$CLI scene save [--saveAs ""Assets/Scenes/NewScene.unity""]
$CLI scene get_hierarchy [--depth 3] [--includeInactive false]
$CLI scene get_active
$CLI scene new [--setup empty]
```";

        public CommandResult Execute(CommandRequest request)
        {
            var action = request.GetParam("action", "get_hierarchy");

            try
            {
                switch (action.ToLower())
                {
                    case "load":
                        return LoadScene(request);
                    case "save":
                        return SaveScene(request);
                    case "get_hierarchy":
                        return GetHierarchy(request);
                    case "get_active":
                        return GetActiveScene(request);
                    case "new":
                        return NewScene(request);
                    default:
                        return CommandResult.Failure(request.id, $"Unknown action: {action}. Supported: load, save, get_hierarchy, get_active, new");
                }
            }
            catch (Exception ex)
            {
                return CommandResult.FromException(request.id, ex);
            }
        }

        private CommandResult LoadScene(CommandRequest request)
        {
            var scenePath = request.GetParam<string>("scenePath");
            if (string.IsNullOrEmpty(scenePath))
            {
                return CommandResult.Failure(request.id, "Missing 'scenePath' parameter");
            }

            var modeStr = request.GetParam("mode", "single");
            var mode = modeStr.ToLower() == "additive"
                ? OpenSceneMode.Additive
                : OpenSceneMode.Single;

            var scene = EditorSceneManager.OpenScene(scenePath, mode);

            return CommandResult.Success(request.id, new
            {
                scenePath = scenePath,
                sceneName = scene.name,
                loaded = scene.isLoaded
            });
        }

        private CommandResult SaveScene(CommandRequest request)
        {
            var saveAs = request.GetParam<string>("saveAs", null);

            Scene scene;
            bool saved;

            if (!string.IsNullOrEmpty(saveAs))
            {
                scene = SceneManager.GetActiveScene();
                saved = EditorSceneManager.SaveScene(scene, saveAs);
            }
            else
            {
                saved = EditorSceneManager.SaveOpenScenes();
                scene = SceneManager.GetActiveScene();
            }

            return CommandResult.Success(request.id, new
            {
                sceneName = scene.name,
                scenePath = scene.path,
                saved = saved
            });
        }

        private CommandResult GetHierarchy(CommandRequest request)
        {
            var depth = request.GetParam("depth", 3);
            var includeInactive = request.GetParam("includeInactive", true);

            var scene = SceneManager.GetActiveScene();
            var rootObjects = scene.GetRootGameObjects();
            var hierarchy = new List<HierarchyNode>();

            foreach (var root in rootObjects)
            {
                if (!includeInactive && !root.activeInHierarchy)
                {
                    continue;
                }

                hierarchy.Add(BuildHierarchyNode(root, depth, includeInactive));
            }

            return CommandResult.Success(request.id, new
            {
                sceneName = scene.name,
                scenePath = scene.path,
                rootCount = hierarchy.Count,
                hierarchy = hierarchy
            });
        }

        private CommandResult GetActiveScene(CommandRequest request)
        {
            var scene = SceneManager.GetActiveScene();

            return CommandResult.Success(request.id, new
            {
                name = scene.name,
                path = scene.path,
                isLoaded = scene.isLoaded,
                isDirty = scene.isDirty,
                rootCount = scene.rootCount
            });
        }

        private CommandResult NewScene(CommandRequest request)
        {
            var setup = request.GetParam("setup", "default");
            var newSceneSetup = setup.ToLower() == "empty"
                ? NewSceneSetup.EmptyScene
                : NewSceneSetup.DefaultGameObjects;

            var scene = EditorSceneManager.NewScene(newSceneSetup, NewSceneMode.Single);

            return CommandResult.Success(request.id, new
            {
                sceneName = scene.name,
                created = true
            });
        }

        private HierarchyNode BuildHierarchyNode(GameObject go, int remainingDepth, bool includeInactive)
        {
            var node = new HierarchyNode
            {
                name = go.name,
                active = go.activeSelf,
                components = new List<string>()
            };

            foreach (var component in go.GetComponents<Component>())
            {
                if (component != null)
                {
                    node.components.Add(component.GetType().Name);
                }
            }

            if (remainingDepth > 0 && go.transform.childCount > 0)
            {
                node.children = new List<HierarchyNode>();

                foreach (Transform child in go.transform)
                {
                    if (!includeInactive && !child.gameObject.activeInHierarchy)
                    {
                        continue;
                    }

                    node.children.Add(BuildHierarchyNode(child.gameObject, remainingDepth - 1, includeInactive));
                }
            }

            return node;
        }

        [Serializable]
        private class HierarchyNode
        {
            public string name;
            public bool active;
            public List<string> components;
            public List<HierarchyNode> children;
        }
    }
}
