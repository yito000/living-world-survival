using UnityEngine.SceneManagement;

namespace FishNet.Managing.Scened
{
    public struct UnloadedScene
    {
        public readonly string Name;
        public readonly int Handle;

        public UnloadedScene(Scene s)
        {
            Name = s.name;
            Handle = GetSceneHandle(s);
        }

        public UnloadedScene(string name, int handle)
        {
            Name = name;
            Handle = handle;
        }

        /// <summary>
        /// Returns a scene based on handle.
        /// Result may not be valid as some Unity versions discard of the scene information after unloading.
        /// </summary>
        /// <returns></returns>
        private static int GetSceneHandle(Scene scene)
        {
#if UNITY_6000_5_OR_NEWER
            return unchecked((int)scene.handle.GetRawData());
#else
            return scene.handle;
#endif
        }

        public Scene GetScene()
        {
            int loadedScenes = UnityEngine.SceneManagement.SceneManager.sceneCount;
            for (int i = 0; i < loadedScenes; i++)
            {
                Scene s = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (s.IsValid() && GetSceneHandle(s) == Handle)
                    return s;
            }

            return default;
        }
    }
}


