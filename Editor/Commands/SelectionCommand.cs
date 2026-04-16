using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AIBridge.Editor
{
    /// <summary>
    /// Selection operations: get, set, clear
    /// Supports multiple sub-commands via "action" parameter
    /// </summary>
    public class SelectionCommand : ICommand
    {
        public string Type => "selection";
        public bool RequiresRefresh => false;

        public string SkillDescription => @"### `selection` - Selection Operations

```bash
$CLI selection get [--includeComponents true]
$CLI selection set --path ""Player"" [--assetPath ""Assets/Prefabs/Player.prefab""]
$CLI selection clear
$CLI selection add --path ""Enemy1""
$CLI selection remove --path ""Enemy1""
```";

        public CommandResult Execute(CommandRequest request)
        {
            var action = request.GetParam("action", "get");

            try
            {
                switch (action.ToLower())
                {
                    case "get":
                        return GetSelection(request);
                    case "set":
                        return SetSelection(request);
                    case "clear":
                        return ClearSelection(request);
                    case "add":
                        return AddToSelection(request);
                    case "remove":
                        return RemoveFromSelection(request);
                    default:
                        return CommandResult.Failure(request.id, $"Unknown action: {action}. Supported: get, set, clear, add, remove");
                }
            }
            catch (Exception ex)
            {
                return CommandResult.FromException(request.id, ex);
            }
        }

        private CommandResult GetSelection(CommandRequest request)
        {
            var includeComponents = request.GetParam("includeComponents", false);

            var gameObjects = new List<GameObjectInfo>();
            var assets = new List<AssetInfo>();

            // Get selected game objects
            foreach (var go in Selection.gameObjects)
            {
                var info = new GameObjectInfo
                {
                    name = go.name,
                    path = GetGameObjectPath(go),
                    tag = go.tag,
                    layer = LayerMask.LayerToName(go.layer),
                    activeSelf = go.activeSelf,
                    activeInHierarchy = go.activeInHierarchy,
                    instanceId = go.GetInstanceID()
                };

                if (includeComponents)
                {
                    info.components = new List<string>();
                    foreach (var component in go.GetComponents<Component>())
                    {
                        if (component != null)
                        {
                            info.components.Add(component.GetType().Name);
                        }
                    }
                }

                gameObjects.Add(info);
            }

            // Get selected assets
            foreach (var obj in Selection.objects)
            {
                if (obj is GameObject)
                {
                    continue;  // Already handled above
                }

                var path = AssetDatabase.GetAssetPath(obj);
                if (!string.IsNullOrEmpty(path))
                {
                    assets.Add(new AssetInfo
                    {
                        name = obj.name,
                        path = path,
                        type = obj.GetType().Name,
                        instanceId = obj.GetInstanceID()
                    });
                }
            }

            return CommandResult.Success(request.id, new
            {
                gameObjects = gameObjects,
                assets = assets,
                activeObject = Selection.activeObject?.name,
                activeObjectInstanceId = Selection.activeObject?.GetInstanceID(),
                count = gameObjects.Count + assets.Count
            });
        }

        private CommandResult SetSelection(CommandRequest request)
        {
            var path = request.GetParam<string>("path", null);
            var assetPath = request.GetParam<string>("assetPath", null);
            var instanceId = request.GetParam("instanceId", 0);
            var instanceIds = request.GetParam<string>("instanceIds", null);

            UnityEngine.Object selectedObject = null;
            var selectedObjects = new List<UnityEngine.Object>();

            // By instance ID
            if (instanceId != 0)
            {
#if UNITY_6000_3_OR_NEWER
                selectedObject = EditorUtility.EntityIdToObject(instanceId);
#else
                selectedObject = EditorUtility.InstanceIDToObject(instanceId);
#endif
                if (selectedObject != null)
                {
                    selectedObjects.Add(selectedObject);
                }
            }
            // By multiple instance IDs
            else if (!string.IsNullOrEmpty(instanceIds))
            {
                var ids = instanceIds.Split(',');
                foreach (var idStr in ids)
                {
                    if (int.TryParse(idStr.Trim(), out var id))
                    {
#if UNITY_6000_3_OR_NEWER
                        var obj = EditorUtility.EntityIdToObject(id);
#else
                        var obj = EditorUtility.InstanceIDToObject(id);
#endif
                        if (obj != null)
                        {
                            selectedObjects.Add(obj);
                        }
                    }
                }
            }
            // By hierarchy path
            else if (!string.IsNullOrEmpty(path))
            {
                var go = GameObject.Find(path);
                if (go != null)
                {
                    selectedObject = go;
                    selectedObjects.Add(go);
                }
            }
            // By asset path
            else if (!string.IsNullOrEmpty(assetPath))
            {
                selectedObject = AssetDatabase.LoadMainAssetAtPath(assetPath);
                if (selectedObject != null)
                {
                    selectedObjects.Add(selectedObject);
                }
            }

            if (selectedObjects.Count == 0)
            {
                return CommandResult.Failure(request.id, "No objects found to select. Provide 'path', 'assetPath', 'instanceId', or 'instanceIds'");
            }

            Selection.objects = selectedObjects.ToArray();
            Selection.activeObject = selectedObject ?? selectedObjects[0];

            return CommandResult.Success(request.id, new
            {
                action = "set",
                selectedCount = selectedObjects.Count,
                activeObject = Selection.activeObject?.name
            });
        }

        private CommandResult ClearSelection(CommandRequest request)
        {
            Selection.objects = new UnityEngine.Object[0];
            Selection.activeObject = null;

            return CommandResult.Success(request.id, new
            {
                action = "clear",
                cleared = true
            });
        }

        private CommandResult AddToSelection(CommandRequest request)
        {
            var path = request.GetParam<string>("path", null);
            var assetPath = request.GetParam<string>("assetPath", null);
            var instanceId = request.GetParam("instanceId", 0);

            UnityEngine.Object objectToAdd = null;

            if (instanceId != 0)
            {
#if UNITY_6000_3_OR_NEWER
                objectToAdd = EditorUtility.EntityIdToObject(instanceId);
#else
                objectToAdd = EditorUtility.InstanceIDToObject(instanceId);
#endif
            }
            else if (!string.IsNullOrEmpty(path))
            {
                objectToAdd = GameObject.Find(path);
            }
            else if (!string.IsNullOrEmpty(assetPath))
            {
                objectToAdd = AssetDatabase.LoadMainAssetAtPath(assetPath);
            }

            if (objectToAdd == null)
            {
                return CommandResult.Failure(request.id, "Object not found to add to selection");
            }

            var currentSelection = new List<UnityEngine.Object>(Selection.objects);
            if (!currentSelection.Contains(objectToAdd))
            {
                currentSelection.Add(objectToAdd);
                Selection.objects = currentSelection.ToArray();
            }

            return CommandResult.Success(request.id, new
            {
                action = "add",
                addedObject = objectToAdd.name,
                newCount = Selection.objects.Length
            });
        }

        private CommandResult RemoveFromSelection(CommandRequest request)
        {
            var path = request.GetParam<string>("path", null);
            var assetPath = request.GetParam<string>("assetPath", null);
            var instanceId = request.GetParam("instanceId", 0);

            UnityEngine.Object objectToRemove = null;

            if (instanceId != 0)
            {
#if UNITY_6000_3_OR_NEWER
                objectToRemove = EditorUtility.EntityIdToObject(instanceId);
#else
                objectToRemove = EditorUtility.InstanceIDToObject(instanceId);
#endif
            }
            else if (!string.IsNullOrEmpty(path))
            {
                objectToRemove = GameObject.Find(path);
            }
            else if (!string.IsNullOrEmpty(assetPath))
            {
                objectToRemove = AssetDatabase.LoadMainAssetAtPath(assetPath);
            }

            if (objectToRemove == null)
            {
                return CommandResult.Failure(request.id, "Object not found to remove from selection");
            }

            var currentSelection = new List<UnityEngine.Object>(Selection.objects);
            if (currentSelection.Contains(objectToRemove))
            {
                currentSelection.Remove(objectToRemove);
                Selection.objects = currentSelection.ToArray();
            }

            return CommandResult.Success(request.id, new
            {
                action = "remove",
                removedObject = objectToRemove.name,
                newCount = Selection.objects.Length
            });
        }

        private string GetGameObjectPath(GameObject go)
        {
            var path = go.name;
            var parent = go.transform.parent;

            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }

            return path;
        }

        [Serializable]
        private class GameObjectInfo
        {
            public string name;
            public string path;
            public string tag;
            public string layer;
            public bool activeSelf;
            public bool activeInHierarchy;
            public int instanceId;
            public List<string> components;
        }

        [Serializable]
        private class AssetInfo
        {
            public string name;
            public string path;
            public string type;
            public int instanceId;
        }
    }
}
