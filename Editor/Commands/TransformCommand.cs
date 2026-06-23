using System;
using AIBridge.Runtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AIBridge.Editor
{
    /// <summary>
    /// Transform operations: get, set_position, set_rotation, set_scale, set_parent
    /// </summary>
    public class TransformCommand : ICommand
    {
        public string Type => "transform";
        public bool RequiresRefresh => false;

        public string SkillDescription => @"### `transform` - Transform Operations

```bash
$CLI transform get --path ""Player""
$CLI transform set_position --path ""Player"" --x 0 --y 1 --z 0 [--local true]
$CLI transform set_rotation --path ""Player"" --x 0 --y 90 --z 0
$CLI transform set_scale --path ""Player"" --x 2 --y 2 --z 2 [--uniform 2]
$CLI transform set_parent --path ""Child"" --parentPath ""Parent""
$CLI transform look_at --path ""Player"" --targetPath ""Enemy""
$CLI transform look_at --path ""Player"" --targetInstanceId 12345
$CLI transform look_at --path ""Player"" --targetX 0 --targetY 0 --targetZ 10
# look_at 目标参数三选一：--targetPath / --targetInstanceId / --targetX --targetY --targetZ
$CLI transform reset --path ""Player""
$CLI transform set_sibling_index --path ""Child"" --index 0 [--first true] [--last true]
```";

        public CommandResult Execute(CommandRequest request)
        {
            var action = request.GetParam("action", "get");

            try
            {
                switch (action.ToLower())
                {
                    case "get":
                        return Get(request);
                    case "set_position":
                        return SetPosition(request);
                    case "set_rotation":
                        return SetRotation(request);
                    case "set_scale":
                        return SetScale(request);
                    case "set_parent":
                        return SetParent(request);
                    case "look_at":
                        return LookAt(request);
                    case "reset":
                        return Reset(request);
                    case "set_sibling_index":
                        return SetSiblingIndex(request);
                    default:
                        return CommandResult.Failure(request.id, $"Unknown action: {action}");
                }
            }
            catch (Exception ex)
            {
                return CommandResult.FromException(request.id, ex);
            }
        }

        private CommandResult Get(CommandRequest request)
        {
            var transform = GetTargetTransform(request);
            if (transform == null)
            {
                return CommandResult.Failure(request.id, "Transform not found");
            }

            return CommandResult.Success(request.id, new
            {
                name = transform.name,
                position = new { x = transform.position.x, y = transform.position.y, z = transform.position.z },
                localPosition = new { x = transform.localPosition.x, y = transform.localPosition.y, z = transform.localPosition.z },
                rotation = new { x = transform.eulerAngles.x, y = transform.eulerAngles.y, z = transform.eulerAngles.z },
                localRotation = new { x = transform.localEulerAngles.x, y = transform.localEulerAngles.y, z = transform.localEulerAngles.z },
                localScale = new { x = transform.localScale.x, y = transform.localScale.y, z = transform.localScale.z },
                parent = transform.parent != null ? transform.parent.name : null,
                childCount = transform.childCount
            });
        }

        private CommandResult SetPosition(CommandRequest request)
        {
            var transform = GetTargetTransform(request);
            if (transform == null)
            {
                return CommandResult.Failure(request.id, "Transform not found");
            }

            var x = request.GetParam("x", transform.position.x);
            var y = request.GetParam("y", transform.position.y);
            var z = request.GetParam("z", transform.position.z);
            var local = request.GetParam("local", false);

            Undo.RecordObject(transform, $"Set Position {transform.name}");

            if (local)
            {
                transform.localPosition = new Vector3(x, y, z);
            }
            else
            {
                transform.position = new Vector3(x, y, z);
            }

            return CommandResult.Success(request.id, new
            {
                name = transform.name,
                position = new { x = transform.position.x, y = transform.position.y, z = transform.position.z },
                localPosition = new { x = transform.localPosition.x, y = transform.localPosition.y, z = transform.localPosition.z }
            });
        }

        private CommandResult SetRotation(CommandRequest request)
        {
            var transform = GetTargetTransform(request);
            if (transform == null)
            {
                return CommandResult.Failure(request.id, "Transform not found");
            }

            var x = request.GetParam("x", transform.eulerAngles.x);
            var y = request.GetParam("y", transform.eulerAngles.y);
            var z = request.GetParam("z", transform.eulerAngles.z);
            var local = request.GetParam("local", false);

            Undo.RecordObject(transform, $"Set Rotation {transform.name}");

            if (local)
            {
                transform.localEulerAngles = new Vector3(x, y, z);
            }
            else
            {
                transform.eulerAngles = new Vector3(x, y, z);
            }

            return CommandResult.Success(request.id, new
            {
                name = transform.name,
                rotation = new { x = transform.eulerAngles.x, y = transform.eulerAngles.y, z = transform.eulerAngles.z }
            });
        }

        private CommandResult SetScale(CommandRequest request)
        {
            var transform = GetTargetTransform(request);
            if (transform == null)
            {
                return CommandResult.Failure(request.id, "Transform not found");
            }

            var x = request.GetParam("x", transform.localScale.x);
            var y = request.GetParam("y", transform.localScale.y);
            var z = request.GetParam("z", transform.localScale.z);
            var uniform = request.GetParam("uniform", float.NaN);

            Undo.RecordObject(transform, $"Set Scale {transform.name}");

            if (!float.IsNaN(uniform))
            {
                transform.localScale = new Vector3(uniform, uniform, uniform);
            }
            else
            {
                transform.localScale = new Vector3(x, y, z);
            }

            return CommandResult.Success(request.id, new
            {
                name = transform.name,
                localScale = new { x = transform.localScale.x, y = transform.localScale.y, z = transform.localScale.z }
            });
        }

        private CommandResult SetParent(CommandRequest request)
        {
            var transform = GetTargetTransform(request);
            if (transform == null)
            {
                return CommandResult.Failure(request.id, "Transform not found");
            }

            var parentPath = request.GetParam<string>("parentPath", null);
            var parentSerializedId = AIBridgeEditorObjectIdentity.GetRequestObjectId(request, "parentInstanceId");
            var worldPositionStays = request.GetParam("worldPositionStays", true);

            Transform newParent = null;

            if (AIBridgeObjectIdentity.HasSerializedId(parentSerializedId))
            {
                var parentGo = AIBridgeEditorObjectIdentity.ResolveGameObject(parentSerializedId);
                newParent = parentGo != null ? parentGo.transform : null;
            }
            else if (!string.IsNullOrEmpty(parentPath))
            {
                var parentGo = GameObject.Find(parentPath);
                newParent = parentGo != null ? parentGo.transform : null;
            }

            Undo.SetTransformParent(transform, newParent, $"Set Parent {transform.name}");
            transform.SetParent(newParent, worldPositionStays);

            return CommandResult.Success(request.id, new
            {
                name = transform.name,
                parent = transform.parent != null ? transform.parent.name : null
            });
        }

        private CommandResult LookAt(CommandRequest request)
        {
            var transform = GetTargetTransform(request);
            if (transform == null)
            {
                return CommandResult.Failure(request.id, "Transform not found");
            }

            Vector3 targetPosition;
            string targetMode;

            // 优先支持对象目标，避免传入 targetPath/targetInstanceId 时误判为缺少坐标。
            var targetPath = request.GetParam<string>("targetPath", null);
            var targetSerializedId = AIBridgeEditorObjectIdentity.GetRequestObjectId(request, "targetInstanceId");
            if (!string.IsNullOrEmpty(targetPath) || AIBridgeObjectIdentity.HasSerializedId(targetSerializedId))
            {
                var targetTransform = FindTransform(targetPath, targetSerializedId);
                if (targetTransform == null)
                {
                    return CommandResult.Failure(request.id, "Target Transform not found");
                }

                targetPosition = targetTransform.position;
                targetMode = AIBridgeObjectIdentity.HasSerializedId(targetSerializedId) ? "instanceId" : "path";
            }
            else
            {
                var targetX = request.GetParam("targetX", float.NaN);
                var targetY = request.GetParam("targetY", float.NaN);
                var targetZ = request.GetParam("targetZ", float.NaN);

                if (float.IsNaN(targetX) || float.IsNaN(targetY) || float.IsNaN(targetZ))
                {
                    return CommandResult.Failure(request.id, "Missing target. Provide --targetPath/--targetInstanceId or --targetX --targetY --targetZ");
                }

                targetPosition = new Vector3(targetX, targetY, targetZ);
                targetMode = "coordinates";
            }

            Undo.RecordObject(transform, $"LookAt {transform.name}");
            transform.LookAt(targetPosition);
            EditorUtility.SetDirty(transform);

            return CommandResult.Success(request.id, new
            {
                name = transform.name,
                targetMode = targetMode,
                targetPosition = new { x = targetPosition.x, y = targetPosition.y, z = targetPosition.z },
                rotation = new { x = transform.eulerAngles.x, y = transform.eulerAngles.y, z = transform.eulerAngles.z }
            });
        }

        private CommandResult SetSiblingIndex(CommandRequest request)
        {
            var transform = GetTargetTransform(request);
            if (transform == null)
            {
                return CommandResult.Failure(request.id, "Transform not found");
            }

            var first = request.GetParam("first", false);
            var last = request.GetParam("last", false);
            var siblingCount = GetSiblingCount(transform);

            Undo.RecordObject(transform, $"Set Sibling Index {transform.name}");

            if (first)
            {
                transform.SetAsFirstSibling();
            }
            else if (last)
            {
                transform.SetAsLastSibling();
            }
            else
            {
                var requestedIndex = request.GetParam("index", transform.GetSiblingIndex());
                var index = Mathf.Clamp(requestedIndex, 0, siblingCount - 1);
                transform.SetSiblingIndex(index);
            }

            EditorUtility.SetDirty(transform);
            if (transform.gameObject.scene.IsValid())
            {
                EditorSceneManager.MarkSceneDirty(transform.gameObject.scene);
            }

            return CommandResult.Success(request.id, new
            {
                name = transform.name,
                path = GetTransformPath(transform),
                index = transform.GetSiblingIndex(),
                siblingCount = siblingCount,
                parent = transform.parent != null ? transform.parent.name : null
            });
        }

        private CommandResult Reset(CommandRequest request)
        {
            var transform = GetTargetTransform(request);
            if (transform == null)
            {
                return CommandResult.Failure(request.id, "Transform not found");
            }

            var position = request.GetParam("position", true);
            var rotation = request.GetParam("rotation", true);
            var scale = request.GetParam("scale", true);

            Undo.RecordObject(transform, $"Reset Transform {transform.name}");

            if (position)
            {
                transform.localPosition = Vector3.zero;
            }
            if (rotation)
            {
                transform.localRotation = Quaternion.identity;
            }
            if (scale)
            {
                transform.localScale = Vector3.one;
            }

            return CommandResult.Success(request.id, new
            {
                name = transform.name,
                localPosition = new { x = transform.localPosition.x, y = transform.localPosition.y, z = transform.localPosition.z },
                localRotation = new { x = transform.localEulerAngles.x, y = transform.localEulerAngles.y, z = transform.localEulerAngles.z },
                localScale = new { x = transform.localScale.x, y = transform.localScale.y, z = transform.localScale.z }
            });
        }

        private Transform GetTargetTransform(CommandRequest request)
        {
            var path = request.GetParam<string>("path", null);
            var serializedId = AIBridgeEditorObjectIdentity.GetRequestObjectId(request, "instanceId");

            if (!string.IsNullOrEmpty(path) || AIBridgeObjectIdentity.HasSerializedId(serializedId))
            {
                return FindTransform(path, serializedId);
            }

            return Selection.activeTransform;
        }

        private Transform FindTransform(string path, object serializedId)
        {
            if (AIBridgeObjectIdentity.HasSerializedId(serializedId))
            {
                var go = AIBridgeEditorObjectIdentity.ResolveGameObject(serializedId);
                return go != null ? go.transform : null;
            }

            if (!string.IsNullOrEmpty(path))
            {
                var go = GameObject.Find(path);
                return go != null ? go.transform : null;
            }

            return null;
        }

        private int GetSiblingCount(Transform transform)
        {
            if (transform.parent != null)
            {
                return transform.parent.childCount;
            }

            return transform.gameObject.scene.rootCount;
        }

        private string GetTransformPath(Transform transform)
        {
            var path = transform.name;
            var parent = transform.parent;

            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }

            return path;
        }
    }
}
