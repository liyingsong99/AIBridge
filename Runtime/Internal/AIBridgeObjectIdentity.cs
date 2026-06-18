using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace AIBridge.Runtime
{
    public static class AIBridgeObjectIdentity
    {
        public const string EntityIdFieldName = "entityId";
        public const string InstanceIdFieldName = "instanceId";

        public static object GetSerializedId(UnityEngine.Object obj)
        {
#if UNITY_6000_4_OR_NEWER
            return GetSerializedEntityId(obj);
#else
            return GetLegacyInstanceId(obj);
#endif
        }

        public static string GetSerializedEntityId(UnityEngine.Object obj)
        {
#if UNITY_6000_4_OR_NEWER
            if (obj == null)
            {
                return null;
            }

            return EntityId.ToULong(obj.GetEntityId()).ToString(CultureInfo.InvariantCulture);
#else
            return null;
#endif
        }

        public static int GetLegacyInstanceId(UnityEngine.Object obj)
        {
#if UNITY_6000_4_OR_NEWER
            return 0;
#else
            return obj != null ? obj.GetInstanceID() : 0;
#endif
        }

        public static void AddSerializedId(Dictionary<string, object> data, UnityEngine.Object obj)
        {
            if (data == null)
            {
                return;
            }

#if UNITY_6000_4_OR_NEWER
            // Unity 6000.4+ 弃用 GetInstanceID；对外保留 instanceId 作为旧参数别名，新增 entityId 表达真实语义。
            var entityId = GetSerializedEntityId(obj);
            data[EntityIdFieldName] = entityId;
            data[InstanceIdFieldName] = entityId;
#else
            data[InstanceIdFieldName] = GetLegacyInstanceId(obj);
#endif
        }

        public static bool HasSerializedId(object value)
        {
#if UNITY_6000_4_OR_NEWER
            ulong entityId;
            return TryParseSerializedEntityId(value, out entityId) && entityId != 0UL;
#else
            int instanceId;
            return TryParseLegacyInstanceId(value, out instanceId) && instanceId != 0;
#endif
        }

        public static bool MatchesSerializedId(UnityEngine.Object obj, object value)
        {
            if (obj == null)
            {
                return false;
            }

#if UNITY_6000_4_OR_NEWER
            ulong entityId;
            return TryParseSerializedEntityId(value, out entityId)
                && EntityId.ToULong(obj.GetEntityId()) == entityId;
#else
            int instanceId;
            return TryParseLegacyInstanceId(value, out instanceId)
                && obj.GetInstanceID() == instanceId;
#endif
        }

        public static bool TryParseSerializedEntityId(object value, out ulong entityId)
        {
            entityId = 0UL;
            if (value == null)
            {
                return false;
            }

            if (value is ulong ulongValue)
            {
                entityId = ulongValue;
                return true;
            }

            if (value is long longValue)
            {
                if (longValue < 0L)
                {
                    return false;
                }

                entityId = (ulong)longValue;
                return true;
            }

            if (value is int intValue)
            {
                if (intValue < 0)
                {
                    return false;
                }

                entityId = (ulong)intValue;
                return true;
            }

            if (value is double doubleValue)
            {
                if (doubleValue < 0 || Math.Truncate(doubleValue) != doubleValue)
                {
                    return false;
                }

                entityId = (ulong)doubleValue;
                return true;
            }

            return ulong.TryParse(
                Convert.ToString(value, CultureInfo.InvariantCulture),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out entityId);
        }

        public static bool TryParseLegacyInstanceId(object value, out int instanceId)
        {
            instanceId = 0;
            if (value == null)
            {
                return false;
            }

            if (value is int intValue)
            {
                instanceId = intValue;
                return true;
            }

            if (value is long longValue)
            {
                if (longValue < int.MinValue || longValue > int.MaxValue)
                {
                    return false;
                }

                instanceId = (int)longValue;
                return true;
            }

            if (value is double doubleValue)
            {
                if (doubleValue < int.MinValue
                    || doubleValue > int.MaxValue
                    || Math.Truncate(doubleValue) != doubleValue)
                {
                    return false;
                }

                instanceId = (int)doubleValue;
                return true;
            }

            return int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out instanceId);
        }
    }
}
