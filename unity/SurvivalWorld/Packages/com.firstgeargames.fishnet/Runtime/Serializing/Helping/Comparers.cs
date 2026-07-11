using System;
using System.Collections.Generic;
using UnityEngine.SceneManagement;

namespace FishNet.Serializing.Helping
{
    public class PublicPropertyComparer<T>
    {
        /// <summary>
        /// Compare if T is default.
        /// </summary>
        public static Func<T, bool> IsDefault { get; set; }
        /// <summary>
        /// Compare if T is the same as T2.
        /// </summary>
        public static Func<T, T, bool> Compare { get; set; }
    }

    public class Comparers
    {
        /// <summary>
        /// Returns if A equals B using EqualityCompare.
        /// </summary>
        /// <typeparam name = "T"></typeparam>
        /// <param name = "a"></param>
        /// <param name = "b"></param>
        /// <returns></returns>
        public static bool EqualityCompare<T>(T a, T b)
        {
            return EqualityComparer<T>.Default.Equals(a, b);
        }

        public static bool IsDefault<T>(T t)
        {
            return t.Equals(default(T));
        }

        public static bool IsEqualityCompareDefault<T>(T a)
        {
            return EqualityComparer<T>.Default.Equals(a, default);
        }
    }

    internal class SceneComparer : IEqualityComparer<Scene>
    {
        public bool Equals(Scene a, Scene b)
        {
            if (!a.IsValid() || !b.IsValid())
                return false;

            ulong aHandle = GetSceneHandle(a);
            ulong bHandle = GetSceneHandle(b);
            if (aHandle != 0 || bHandle != 0)
                return aHandle == bHandle;

            return a.name == b.name;
        }

        private static ulong GetSceneHandle(Scene scene)
        {
#if UNITY_6000_5_OR_NEWER
            return scene.handle.GetRawData();
#else
            return unchecked((ulong)scene.handle);
#endif
        }

        public int GetHashCode(Scene obj)
        {
            return obj.GetHashCode();
        }
    }
}
