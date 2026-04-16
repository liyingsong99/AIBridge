using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AIBridge.Editor
{
    /// <summary>
    /// GameObject operations: create, destroy, find, set_active, rename, duplicate
    /// Supports multiple sub-commands via "action" parameter
    /// </summary>
    public class GameObjectCommand : ICommand
    {
        public string Type => "gameobject";
        public bool RequiresRefresh => true;

        public string SkillDescription => @"### `gameobject` - GameObject Operations

```bash
$CLI gameobject create --name ""Cube"" --primitiveType Cube [--parentPath ""Parent""]
$CLI gameobject destroy --path ""Cube"" [--instanceId 12345]
$CLI gameobject find --name ""Player"" [--tag ""Enemy""] [--withComponent ""BoxCollider""] [--maxResults 10]
$CLI gameobject set_active --path ""Player"" --active false [--toggle true]
$CLI gameobject rename --path ""OldName"" --newName ""NewName""
$CLI gameobject duplicate --path ""Original""
$CLI gameobject get_info --path ""Player""
```";

        public CommandResult Execute(CommandRequest request)
        {
            var action = request.GetParam("action", "find");

            try
            {
                switch (action.ToLower())
                {
                    case "create":
                        return Create(request);
                    case "destroy":
                        return Destroy(request);
                    case "find":
                        return Find(request);
                    case "set_active":
                        return SetActive(request);
                    case "rename":
                        return Rename(request);
                    case "duplicate":
                        return Duplicate(request);
                    case "get_info":
                        return GetInfo(request);
                    default:
                        return CommandResult.Failure(request.id, $"Unknown action: {action}. Supported: create, destroy, find, set_active, rename, duplicate, get_info");
                }
            }
            catch (Exception ex)
            {
                return CommandResult.FromException(request.id, ex);
            }
        }

        private CommandResult Create(CommandRequest request)
        {
            var name = request.GetParam("name", "New GameObject");
            var primitiveType = request.GetParam<string>("primitiveType", null);
            var parentPath = request.GetParam<string>("parentPath", null);

            GameObject go;

            if (!string.IsNullOrEmpty(primitiveType))
            {
                if (Enum.TryParse<PrimitiveType>(primitiveType, true, out var primitive))
                {
                    go = GameObject.CreatePrimitive(primitive);
                    go.name = name;
                }
                else
                {
                    return CommandResult.Failure(request.id, $"Unknown primitive type: {primitiveType}. Supported: Cube, Sphere, Capsule, Cylinder, Plane, Quad");
                }
            }
            else
            {
                go = new GameObject(name);
            }

            if (!string.IsNullOrEmpty(parentPath))
            {
                var parent = GameObject.Find(parentPath);
                if (parent != null)
                {
                    go.transform.SetParent(parent.transform, false);
                }
            }

            Undo.RegisterCreatedObjectUndo(go, $"Create {name}");
            Selection.activeGameObject = go;

            return CommandResult.Success(request.id, new
            {
                name = go.name,
                path = GetGameObjectPath(go),
                instanceId = go.GetInstanceID()
            });
        }

        private CommandResult Destroy(CommandRequest request)
        {
            var go = GetTargetGameObject(request);
            if (go == null)
            {
                return CommandResult.Failure(request.id, "GameObject not found. Provide 'path' or 'instanceId', or select a GameObject");
            }

            var name = go.name;
            var path = GetGameObjectPath(go);

            Undo.DestroyObjectImmediate(go);

            return CommandResult.Success(request.id, new
            {
                action = "destroy",
                destroyedName = name,
                destroyedPath = path
            });
        }

        private CommandResult Find(CommandRequest request)
        {
            var name = request.GetParam<string>("name", null);
            var tag = request.GetParam<string>("tag", null);
            var withComponent = request.GetParam<string>("withComponent", null);
            var maxResults = request.GetParam("maxResults", 50);

            var results = new List<GameObjectInfo>();

            if (!string.IsNullOrEmpty(tag))
            {
                var objects = GameObject.FindGameObjectsWithTag(tag);
                foreach (var obj in objects)
                {
                    if (results.Count >= maxResults) break;
                    if (string.IsNullOrEmpty(name) || obj.name.Contains(name))
                    {
                        results.Add(CreateGameObjectInfo(obj));
                    }
                }
            }
            else
            {
                GameObject[] allObjects;
#if UNITY_2021_3_OR_NEWER
                allObjects = UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None);
#else
                allObjects = UnityEngine.Object.FindObjectsOfType<GameObject>();
#endif

                foreach (GameObject obj in allObjects)
                {
                    if (results.Count >= maxResults) break;

                    if (!string.IsNullOrEmpty(name) && !obj.name.Contains(name))
                    {
                        continue;
                    }

                    if (!string.IsNullOrEmpty(withComponent))
                    {
                        var hasComponent = false;
                        foreach (var comp in obj.GetComponents<Component>())
                        {
                            if (comp != null && comp.GetType().Name == withComponent)
                            {
                                hasComponent = true;
                                break;
                            }
                        }
                        if (!hasComponent) continue;
                    }

                    results.Add(CreateGameObjectInfo(obj));
                }
            }

            return CommandResult.Success(request.id, new
            {
                results = results,
                count = results.Count
            });
        }

        private CommandResult SetActive(CommandRequest request)
        {
            var go = GetTargetGameObject(request);
            if (go == null)
            {
                return CommandResult.Failure(request.id, "GameObject not found");
            }

            var active = request.GetParam("active", true);
            var toggle = request.GetParam("toggle", false);

            if (toggle)
            {
                active = !go.activeSelf;
            }

            Undo.RecordObject(go, $"Set Active {go.name}");
            go.SetActive(active);

            return CommandResult.Success(request.id, new
            {
                name = go.name,
                activeSelf = go.activeSelf,
                activeInHierarchy = go.activeInHierarchy
            });
        }

        private CommandResult Rename(CommandRequest request)
        {
            var go = GetTargetGameObject(request);
            if (go == null)
            {
                return CommandResult.Failure(request.id, "GameObject not found");
            }

            var newName = request.GetParam<string>("newName");
            if (string.IsNullOrEmpty(newName))
            {
                return CommandResult.Failure(request.id, "Missing 'newName' parameter");
            }

            var oldName = go.name;

            Undo.RecordObject(go, $"Rename {oldName}");
            go.name = newName;

            return CommandResult.Success(request.id, new
            {
                oldName = oldName,
                newName = go.name,
                path = GetGameObjectPath(go)
            });
        }

        private CommandResult Duplicate(CommandRequest request)
        {
            var go = GetTargetGameObject(request);
            if (go == null)
            {
                return CommandResult.Failure(request.id, "GameObject not found");
            }

            var duplicate = UnityEngine.Object.Instantiate(go, go.transform.parent);
            duplicate.name = go.name;

            Undo.RegisterCreatedObjectUndo(duplicate, $"Duplicate {go.name}");
            Selection.activeGameObject = duplicate;

            return CommandResult.Success(request.id, new
            {
                originalName = go.name,
                duplicateName = duplicate.name,
                duplicatePath = GetGameObjectPath(duplicate),
                duplicateInstanceId = duplicate.GetInstanceID()
            });
        }

        private CommandResult GetInfo(CommandRequest request)
        {
            var go = GetTargetGameObject(request);
            if (go == null)
            {
                return CommandResult.Failure(request.id, "GameObject not found");
            }

            var components = new List<string>();
            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp != null)
                {
                    components.Add(comp.GetType().FullName);
                }
            }

            var childCount = go.transform.childCount;
            var children = new List<string>();
            for (var i = 0; i < Math.Min(childCount, 20); i++)
            {
                children.Add(go.transform.GetChild(i).name);
            }

            return CommandResult.Success(request.id, new
            {
                name = go.name,
                path = GetGameObjectPath(go),
                instanceId = go.GetInstanceID(),
                tag = go.tag,
                layer = LayerMask.LayerToName(go.layer),
                layerIndex = go.layer,
                activeSelf = go.activeSelf,
                activeInHierarchy = go.activeInHierarchy,
                isStatic = go.isStatic,
                components = components,
                childCount = childCount,
                children = children,
                parentName = go.transform.parent?.name
            });
        }

        private GameObject GetTargetGameObject(CommandRequest request)
        {
            var path = request.GetParam<string>("path", null);
            var instanceId = request.GetParam("instanceId", 0);

            if (instanceId != 0)
            {
#if UNITY_6000_3_OR_NEWER
                return EditorUtility.EntityIdToObject(instanceId) as GameObject;
#else
                return EditorUtility.InstanceIDToObject(instanceId) as GameObject;
#endif
            }

            if (!string.IsNullOrEmpty(path))
            {
                return GameObject.Find(path);
            }

            return Selection.activeGameObject;
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

        private GameObjectInfo CreateGameObjectInfo(GameObject go)
        {
            return new GameObjectInfo
            {
                name = go.name,
                path = GetGameObjectPath(go),
                instanceId = go.GetInstanceID(),
                activeSelf = go.activeSelf
            };
        }

        [Serializable]
        private class GameObjectInfo
        {
            public string name;
            public string path;
            public int instanceId;
            public bool activeSelf;
        }
    }
}
