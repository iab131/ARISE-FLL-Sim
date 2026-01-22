using System.Collections;
using UnityEngine;

public class UseSampleRobot : MonoBehaviour
{
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    [SerializeField] private GameObject sampleRobot;
    [SerializeField] private GameObject root;
    public void LoadRobot()
    {
        StartCoroutine(LoadRobotRoutine());
    }
    private IEnumerator LoadRobotRoutine()
    {
        // destroy existing robots
        foreach (Transform t in root.transform)
        {
            if (t.gameObject.name.Contains("Robot"))
                Destroy(t.gameObject);
        }

        // wait one frame so OnDestroy() runs and objects are really gone
        yield return null;


        MotorLabel._nextIndex = 1;
        // optional: clear sim map & exit assign mode
        MotorSimulationManager.Instance.ClearAllMotors();
        if (MotorLabelManager.Instance) MotorLabelManager.Instance.SetAssignMode(false);

        // instantiate – use the (original, parent) overload
        GameObject go = Instantiate(sampleRobot, root.transform);
        go.name = "Robot";

        CameraController.Instance.parentModel = go;
        ControlManager.Instance.spawnRoot = go.transform;
    }

}
