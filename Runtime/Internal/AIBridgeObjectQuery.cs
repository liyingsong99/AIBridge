using UnityEngine;

namespace AIBridge.Runtime
{
    /// <summary>
    /// 跨 Unity 版本的场景对象查找封装。
    /// Unity 6000.5+ 弃用带 FindObjectsSortMode 的重载，需走无排序 API。
    /// </summary>
    public static class AIBridgeObjectQuery
    {
        public static T[] FindObjectsByTypeNoSort<T>() where T : Object
        {
#if UNITY_6000_5_OR_NEWER
            return Object.FindObjectsByType<T>();
#elif UNITY_2022_3_OR_NEWER
            return Object.FindObjectsByType<T>(FindObjectsSortMode.None);
#else
            return Object.FindObjectsOfType<T>();
#endif
        }
    }
}
