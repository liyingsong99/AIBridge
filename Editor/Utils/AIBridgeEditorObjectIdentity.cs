using System;
using AIBridge.Runtime;
using UnityEditor;
using UnityEngine;

namespace AIBridge.Editor
{
    internal static class AIBridgeEditorObjectIdentity
    {
        public static object GetRequestObjectId(CommandRequest request, string legacyKey)
        {
            if (request == null || request.@params == null)
            {
                return null;
            }

            var entityKey = GetEntityIdAlias(legacyKey);
            object value;
            if (!string.IsNullOrEmpty(entityKey) && request.@params.TryGetValue(entityKey, out value))
            {
                return value;
            }

            return request.@params.TryGetValue(legacyKey, out value) ? value : null;
        }

        public static string GetRequestObjectIds(CommandRequest request, string legacyKey)
        {
            var value = GetRequestObjectId(request, legacyKey);
            return value != null ? value.ToString() : null;
        }

        public static bool HasRequestObjectId(CommandRequest request, string legacyKey)
        {
            return AIBridgeObjectIdentity.HasSerializedId(GetRequestObjectId(request, legacyKey));
        }

        public static UnityEngine.Object ResolveObject(object serializedId)
        {
#if UNITY_6000_4_OR_NEWER
            ulong entityId;
            if (!AIBridgeObjectIdentity.TryParseSerializedEntityId(serializedId, out entityId))
            {
                return null;
            }

            return EditorUtility.EntityIdToObject(EntityId.FromULong(entityId));
#else
            int instanceId;
            if (!AIBridgeObjectIdentity.TryParseLegacyInstanceId(serializedId, out instanceId))
            {
                return null;
            }

            return EditorUtility.InstanceIDToObject(instanceId);
#endif
        }

        public static UnityEngine.Object ResolveObject(CommandRequest request, string legacyKey)
        {
            return ResolveObject(GetRequestObjectId(request, legacyKey));
        }

        public static GameObject ResolveGameObject(object serializedId)
        {
#if UNITY_6000_4_OR_NEWER
            return ResolveObject(serializedId) as GameObject;
#else
            return ResolveObject(serializedId) as GameObject;
#endif
        }

        public static GameObject ResolveGameObject(CommandRequest request, string legacyKey)
        {
            return ResolveGameObject(GetRequestObjectId(request, legacyKey));
        }

        public static object GetSerializedId(UnityEngine.Object obj)
        {
            return AIBridgeObjectIdentity.GetSerializedId(obj);
        }

        public static string GetSerializedEntityId(UnityEngine.Object obj)
        {
            return AIBridgeObjectIdentity.GetSerializedEntityId(obj);
        }

        private static string GetEntityIdAlias(string legacyKey)
        {
            if (string.IsNullOrEmpty(legacyKey))
            {
                return null;
            }

            if (string.Equals(legacyKey, AIBridgeObjectIdentity.InstanceIdFieldName, StringComparison.Ordinal))
            {
                return AIBridgeObjectIdentity.EntityIdFieldName;
            }

            const string suffix = "InstanceId";
            if (legacyKey.EndsWith(suffix, StringComparison.Ordinal))
            {
                return legacyKey.Substring(0, legacyKey.Length - suffix.Length) + "EntityId";
            }

            const string pluralSuffix = "InstanceIds";
            if (legacyKey.EndsWith(pluralSuffix, StringComparison.Ordinal))
            {
                return legacyKey.Substring(0, legacyKey.Length - pluralSuffix.Length) + "EntityIds";
            }

            return null;
        }
    }
}
