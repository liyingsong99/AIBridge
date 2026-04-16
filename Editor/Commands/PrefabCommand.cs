using System;
using UnityEditor;
using UnityEngine;

namespace AIBridge.Editor
{
    /// <summary>
    /// Prefab operations: instantiate, save, unpack, inspect, apply
    /// Supports multiple sub-commands via "action" parameter
    /// </summary>
    public class PrefabCommand : ICommand
    {
        public string Type => "prefab";
        public bool RequiresRefresh => true;

        public string SkillDescription => @"### `prefab` - Prefab Operations

```bash
$CLI prefab instantiate --prefabPath ""Assets/Prefabs/Player.prefab"" [--posX 5 --posY 0 --posZ 0]
$CLI prefab save --gameObjectPath ""Player"" --savePath ""Assets/Prefabs/Player.prefab""
$CLI prefab unpack --gameObjectPath ""Player(Clone)"" [--completely true]
$CLI prefab get_info --prefabPath ""Assets/Prefabs/Player.prefab""
$CLI prefab get_hierarchy --prefabPath ""Assets/Prefabs/Player.prefab"" [--depth 4] [--includeInactive false]
$CLI prefab apply --gameObjectPath ""Player(Clone)""
```";

        public CommandResult Execute(CommandRequest request)
        {
            var action = request.GetParam("action", "instantiate");

            try
            {
                switch (action.ToLower())
                {
                    case "instantiate":
                        return InstantiatePrefab(request);
                    case "save":
                        return SaveAsPrefab(request);
                    case "unpack":
                        return UnpackPrefab(request);
                    case "get_info":
                        return GetPrefabInfo(request);
                    case "get_hierarchy":
                        return GetPrefabHierarchy(request);
                    case "apply":
                        return ApplyPrefab(request);
                    default:
                        return CommandResult.Failure(request.id, $"Unknown action: {action}. Supported: instantiate, save, unpack, get_info, get_hierarchy, apply");
                }
            }
            catch (Exception ex)
            {
                return CommandResult.FromException(request.id, ex);
            }
        }

        private CommandResult InstantiatePrefab(CommandRequest request)
        {
            var prefabPath = request.GetParam<string>("prefabPath");
            if (string.IsNullOrEmpty(prefabPath))
            {
                return CommandResult.Failure(request.id, "Missing 'prefabPath' parameter");
            }

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
            {
                return CommandResult.Failure(request.id, $"Prefab not found at path: {prefabPath}");
            }

            // Get optional position
            var posX = request.GetParam("posX", 0f);
            var posY = request.GetParam("posY", 0f);
            var posZ = request.GetParam("posZ", 0f);

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            instance.transform.position = new Vector3(posX, posY, posZ);

            // Select the new instance
            Selection.activeGameObject = instance;

            return CommandResult.Success(request.id, new
            {
                prefabPath = prefabPath,
                instanceName = instance.name,
                position = new { x = posX, y = posY, z = posZ }
            });
        }

        private CommandResult SaveAsPrefab(CommandRequest request)
        {
            var gameObjectPath = request.GetParam<string>("gameObjectPath");
            var savePath = request.GetParam<string>("savePath");

            if (string.IsNullOrEmpty(savePath))
            {
                return CommandResult.Failure(request.id, "Missing 'savePath' parameter");
            }

            GameObject go;

            if (!string.IsNullOrEmpty(gameObjectPath))
            {
                go = GameObject.Find(gameObjectPath);
                if (go == null)
                {
                    return CommandResult.Failure(request.id, $"GameObject not found: {gameObjectPath}");
                }
            }
            else
            {
                go = Selection.activeGameObject;
                if (go == null)
                {
                    return CommandResult.Failure(request.id, "No GameObject selected and no 'gameObjectPath' provided");
                }
            }

            // Ensure path ends with .prefab
            if (!savePath.EndsWith(".prefab"))
            {
                savePath += ".prefab";
            }

            var savedPrefab = PrefabUtility.SaveAsPrefabAsset(go, savePath);

            // Refresh AssetDatabase to ensure changes are visible
            AssetDatabase.Refresh();

            return CommandResult.Success(request.id, new
            {
                gameObjectName = go.name,
                prefabPath = savePath,
                saved = savedPrefab != null
            });
        }

        private CommandResult UnpackPrefab(CommandRequest request)
        {
            var gameObjectPath = request.GetParam<string>("gameObjectPath", null);
            var completely = request.GetParam("completely", false);

            GameObject go;

            if (!string.IsNullOrEmpty(gameObjectPath))
            {
                go = GameObject.Find(gameObjectPath);
                if (go == null)
                {
                    return CommandResult.Failure(request.id, $"GameObject not found: {gameObjectPath}");
                }
            }
            else
            {
                go = Selection.activeGameObject;
                if (go == null)
                {
                    return CommandResult.Failure(request.id, "No GameObject selected and no 'gameObjectPath' provided");
                }
            }

            if (!PrefabUtility.IsPartOfAnyPrefab(go))
            {
                return CommandResult.Failure(request.id, $"GameObject '{go.name}' is not part of a prefab");
            }

            var mode = completely
                ? PrefabUnpackMode.Completely
                : PrefabUnpackMode.OutermostRoot;

            PrefabUtility.UnpackPrefabInstance(go, mode, InteractionMode.AutomatedAction);

            return CommandResult.Success(request.id, new
            {
                gameObjectName = go.name,
                unpacked = true,
                completely = completely
            });
        }

        private CommandResult GetPrefabInfo(CommandRequest request)
        {
            var prefabPath = request.GetParam<string>("prefabPath", null);
            var gameObjectPath = request.GetParam<string>("gameObjectPath", null);

            GameObject target = null;

            if (!string.IsNullOrEmpty(prefabPath))
            {
                target = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (target == null)
                {
                    return CommandResult.Failure(request.id, $"Prefab not found at path: {prefabPath}");
                }
            }
            else if (!string.IsNullOrEmpty(gameObjectPath))
            {
                target = GameObject.Find(gameObjectPath);
                if (target == null)
                {
                    return CommandResult.Failure(request.id, $"GameObject not found: {gameObjectPath}");
                }
            }
            else
            {
                target = Selection.activeGameObject;
                if (target == null)
                {
                    return CommandResult.Failure(request.id, "No target specified. Provide 'prefabPath', 'gameObjectPath', or select a GameObject");
                }
            }

            var isPrefabAsset = PrefabUtility.IsPartOfPrefabAsset(target);
            var isPrefabInstance = PrefabUtility.IsPartOfPrefabInstance(target);
            var prefabAssetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(target);
            var prefabType = PrefabUtility.GetPrefabAssetType(target).ToString();
            var prefabStatus = PrefabUtility.GetPrefabInstanceStatus(target).ToString();

            return CommandResult.Success(request.id, new
            {
                name = target.name,
                isPrefabAsset = isPrefabAsset,
                isPrefabInstance = isPrefabInstance,
                prefabAssetPath = prefabAssetPath,
                prefabType = prefabType,
                prefabStatus = prefabStatus
            });
        }

        private CommandResult GetPrefabHierarchy(CommandRequest request)
        {
            var prefabPath = request.GetParam<string>("prefabPath");
            if (string.IsNullOrEmpty(prefabPath))
            {
                return CommandResult.Failure(request.id, "Missing 'prefabPath' parameter");
            }

            var depth = Math.Max(0, request.GetParam("depth", 5));
            var includeInactive = request.GetParam("includeInactive", true);
            var includeComponents = request.GetParam("includeComponents", true);

            var prefabRoot = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefabRoot == null)
            {
                return CommandResult.Failure(request.id, $"Prefab not found at path: {prefabPath}");
            }

            var hierarchy = new System.Collections.Generic.List<PrefabHierarchyNode>();
            var truncated = false;

            if (includeInactive || prefabRoot.activeSelf)
            {
                hierarchy.Add(BuildHierarchyNode(prefabRoot, prefabRoot.name, depth, includeInactive, includeComponents, ref truncated));
            }

            return CommandResult.Success(request.id, new
            {
                prefabPath = prefabPath,
                prefabName = prefabRoot.name,
                depth = depth,
                includeInactive = includeInactive,
                includeComponents = includeComponents,
                rootCount = hierarchy.Count,
                truncated = truncated,
                hierarchy = hierarchy
            });
        }

        private PrefabHierarchyNode BuildHierarchyNode(GameObject go, string path, int remainingDepth, bool includeInactive, bool includeComponents, ref bool truncated)
        {
            var node = new PrefabHierarchyNode
            {
                name = go.name,
                path = path,
                active = go.activeSelf,
                childCount = go.transform.childCount,
                components = new System.Collections.Generic.List<string>(),
                children = new System.Collections.Generic.List<PrefabHierarchyNode>()
            };

            if (includeComponents)
            {
                foreach (var component in go.GetComponents<Component>())
                {
                    if (component != null)
                    {
                        node.components.Add(component.GetType().Name);
                    }
                }
            }

            if (remainingDepth <= 0)
            {
                if (go.transform.childCount > 0)
                {
                    truncated = true;
                }

                return node;
            }

            foreach (Transform child in go.transform)
            {
                if (!includeInactive && !child.gameObject.activeSelf)
                {
                    continue;
                }

                node.children.Add(BuildHierarchyNode(
                    child.gameObject,
                    path + "/" + child.gameObject.name,
                    remainingDepth - 1,
                    includeInactive,
                    includeComponents,
                    ref truncated));
            }

            return node;
        }

        private CommandResult ApplyPrefab(CommandRequest request)
        {
            var gameObjectPath = request.GetParam<string>("gameObjectPath", null);

            GameObject go;

            if (!string.IsNullOrEmpty(gameObjectPath))
            {
                go = GameObject.Find(gameObjectPath);
                if (go == null)
                {
                    return CommandResult.Failure(request.id, $"GameObject not found: {gameObjectPath}");
                }
            }
            else
            {
                go = Selection.activeGameObject;
                if (go == null)
                {
                    return CommandResult.Failure(request.id, "No GameObject selected and no 'gameObjectPath' provided");
                }
            }

            if (!PrefabUtility.IsPartOfPrefabInstance(go))
            {
                return CommandResult.Failure(request.id, $"GameObject '{go.name}' is not a prefab instance");
            }

            var prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
            PrefabUtility.ApplyPrefabInstance(go, InteractionMode.AutomatedAction);

            // Refresh AssetDatabase to ensure changes are visible
            AssetDatabase.Refresh();

            return CommandResult.Success(request.id, new
            {
                gameObjectName = go.name,
                prefabPath = prefabPath,
                applied = true
            });
        }

        [Serializable]
        private class PrefabHierarchyNode
        {
            public string name;
            public string path;
            public bool active;
            public System.Collections.Generic.List<string> components;
            public int childCount;
            public System.Collections.Generic.List<PrefabHierarchyNode> children;
        }
    }
}
