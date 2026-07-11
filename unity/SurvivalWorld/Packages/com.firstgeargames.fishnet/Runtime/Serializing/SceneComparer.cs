using System.Collections.Generic;
using UnityEngine.SceneManagement;

namespace FishNet.Serializing.Helping
{
    internal sealed class SceneHandleEqualityComparer : EqualityComparer<Scene>
    {
        public override bool Equals(Scene a, Scene b)
        {
            return GetSceneHandle(a) == GetSceneHandle(b);
        }

        public override int GetHashCode(Scene obj)
        {
            return GetSceneHandle(obj).GetHashCode();
        }

        private static ulong GetSceneHandle(Scene scene)
        {
#if UNITY_6000_5_OR_NEWER
            return scene.handle.GetRawData();
#else
            return unchecked((ulong)scene.handle);
#endif
        }
    }
}
