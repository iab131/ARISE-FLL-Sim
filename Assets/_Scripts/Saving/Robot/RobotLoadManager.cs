#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

using SFB;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

public class RobotLoadManager : MonoBehaviour
{
    public GameObject robotRoot;
    public static RobotLoadManager Instance { get; private set; }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
    }
#if UNITY_WEBGL && !UNITY_EDITOR
    //[DllImport("__Internal")]
    //private static extern void UploadJsonFile(string gameObjectName, string methodName);

#endif
    public void LoadRobot(string json)
    {
        if (string.IsNullOrEmpty(json))
        {
            Debug.LogError("LoadRobot: JSON input is empty or null.");
            return;
        }
        // Clear old build
        foreach (Transform child in robotRoot.transform)
            Destroy(child.gameObject);

        // Create new Robot root under robotRoot
        GameObject robot = new GameObject("Robot");
        robot.transform.SetParent(robotRoot.transform, false);
        CameraController.Instance.parentModel = robot;
        ControlManager.Instance.spawnRoot = robot.transform;


        // Deserialize save data
        RobotSaveData robotData = JsonUtility.FromJson<RobotSaveData>(json);
        if (robotData == null)
        {
            Debug.LogError("Failed to parse robot JSON data.");
            return;
        }
        foreach (GroupSaveData groupData in robotData.groups)
        {
            GameObject group = new GameObject(groupData.groupName);
            group.name = group.name.Replace("(Clone)", "").Trim();
            group.transform.localPosition = groupData.position;
            group.transform.localRotation = groupData.rotation;
            group.transform.SetParent(robot.transform, false);

            foreach (PartSaveData partData in groupData.parts)
            {
                LoadPart(partData, group.transform);
            }
        }
    }

    private void LoadPart(PartSaveData data, Transform parent)
    {
        if (data == null || string.IsNullOrEmpty(data.prefabName))
        {
            Debug.LogWarning("Invalid part data.");
            return;
        }
        // Try loading prefab from Resources/Robot/
        GameObject prefab = Resources.Load<GameObject>("Robot/" + data.prefabName);
        if (prefab == null)
        {
            Debug.LogWarning("Missing prefab: " + data.prefabName + " in Resources/Robot/");
            return;
        }

        GameObject part = Instantiate(prefab, parent);
        part.name = part.name.Replace("(Clone)", "").Trim();
        part.transform.localPosition = data.position;
        part.transform.localRotation = data.rotation;

        if (data.isMotorHub)
        {
            Transform motorHub = part.transform.Find("MotorHub");
            if (motorHub != null)
            {
                foreach (PartSaveData childData in data.childParts)
                {
                    LoadPart(childData, motorHub);
                }
            }
            else
            {
                Debug.LogWarning("Motor prefab missing MotorHub child: " + data.prefabName);
            }
        }
    }
    public void LoadRobotFromFile()
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        //UploadJsonFile(gameObject.name, "OnRobotJsonLoaded");
#else
        var paths = StandaloneFileBrowser.OpenFilePanel("Open Robot File", "", "fllrobot", false);
        if (paths.Length > 0 && File.Exists(paths[0]))
        {
            string path = paths[0];
            if (Path.GetExtension(path) != ".fllrobot")
            {
                Debug.LogWarning("Selected file is not a valid robot save file.");
                return;
            }

            string encrypted = File.ReadAllText(path);

            // If using encryption:
            string json = JsonEncryptor.Decrypt(encrypted);
            LoadRobot(json);

            // No encryption:
            //LoadRobot(encrypted);
        }
#endif
    }

}
