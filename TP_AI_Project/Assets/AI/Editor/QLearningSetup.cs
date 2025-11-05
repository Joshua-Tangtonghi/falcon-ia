#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace AI.Editor
{
    // Editor utility to create an example QLearningController prefab and a trainer GameObject in the scene.
    public static class QLearningSetup
    {
        [MenuItem("Tools/QLearning/Create Example Prefab & Trainer")]
        public static void CreatePrefabAndTrainer()
        {
            // Create temp game object for prefab
            GameObject tmp = new GameObject("QLearningController_PrefabTemp");
            var controller = tmp.AddComponent<AI.QLearningController>();

            // Configure some readable defaults
            controller.TrainingMode = true;
            controller.SaveFileName = "qtable.json";

            // Ensure AI folder exists
            string prefPath = "Assets/AI/QLearningController.prefab";

            // Save as prefab asset
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(tmp, prefPath);
            if (prefab != null)
            {
                Debug.Log("QLearning prefab created at: " + prefPath);
            }
            else
            {
                Debug.LogError("Failed to create QLearning prefab.");
            }

            // Destroy temp
            Object.DestroyImmediate(tmp);

            // Instantiate prefab into the scene
            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            instance.name = "QLearningController_Instance";

            // Create trainer object and assign reference
            GameObject trainerGo = new GameObject("QLearningTrainer");
            var trainer = trainerGo.AddComponent<AI.QLearningTrainer>();
            var controllerComp = instance.GetComponent<AI.QLearningController>();
            trainer.controller = controllerComp;
            trainer.SaveFileName = "qtable.json";
            trainer.AutoLoad = false;
            trainer.AutoSaveOnEnd = true;
            trainer.AutoRestartOnEnd = true;
            trainer.TrainingTimeScale = 1.0f;

            // Select created objects
            Selection.activeGameObject = trainerGo;
            Debug.Log("QLearning Trainer created in scene and linked to controller instance. Select the trainer to adjust settings.");
        }
    }
}
#endif
